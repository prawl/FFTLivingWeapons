using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Resolves the acting player's weapons (both hands): HP+MaxHP+level (not the condensed nameId, which is
/// unreliable) -> band entry (live; the static array freezes on battle restart) -> (level,brave,faith) ->
/// roster hands. Empty on no match, an enemy, or ambiguity; a miss beats a mis-credit. The twin filter prefers
/// the real-position entry over its (0,0) mirror, whose HP goes stale.
/// The register answers first (kill-attribution plan, 2026-07-01) when ownership predates the acted-period and the
/// corpse anchor allows it; otherwise the turn-queue walk runs, with its hover-trap and level+100-flake exposure.
/// Roster walks live in ActorResolver.TurnQueue.cs and veto rows whose Offsets.ANameId
/// disagrees ([frame-1fc-nameid-mirror], [party-nameid-unique-key]).
/// </summary>
internal sealed partial class ActorResolver
{
    private static readonly List<int> Empty = new();
    private readonly IGameMemory _mem;
    private readonly ISet<int> _weapons;   // ids that are real weapons (meta keys)
    private readonly ActorRegister? _register;   // null in ActorResolverUnarmedTests
    private int _periodStartTick = -1;     // -1 = not currently inside an acted-period
    private bool _corpseAnchorOk = true;   // KillTracker's per-tick corpse-anchor veto (V1)

    /// <summary>Which path answered the last credit-worthy resolve. Flags lane, register, or
    /// turn-queue fallback. Feeds the latch log tag and KillTracker's fallback counters.</summary>
    internal ResolveSource LastResolveSource { get; private set; }

    /// <summary>True when the last resolve came from the register path (the flags lane is neither).</summary>
    public bool LastResolveViaRegister => LastResolveSource == ResolveSource.Register;

    public ActorResolver(IGameMemory mem, ISet<int> weapons, ActorRegister? register = null)
    {
        _mem = mem;
        _weapons = weapons;
        _register = register;
    }

    /// <summary>Starts an acted-period at register tick <paramref name="tick"/>. KillTracker calls it once per
    /// period, so a sub-UnfreezeTicks Acted drift dip cannot refresh the gate.</summary>
    public void BeginActedPeriod(int tick) => _periodStartTick = tick;

    /// <summary>Ends the acted-period; the register path is inert outside one.</summary>
    public void EndActedPeriod() => _periodStartTick = -1;

    /// <summary>Corpse-anchor veto (kill-attribution plan, V1): false when a pending corpse is at least as old as the
    /// current ownership (pending-birth stamp &lt;= arrival tick). Pushed once per Poll tick from KillTracker.cs.</summary>
    public void SetCorpseAnchorOk(bool ok) => _corpseAnchorOk = ok;

    /// <summary>True when the register path may answer. A period is open, no pending corpse outdates the
    /// ownership, and ownership has been stable since before the period began.</summary>
    private bool RegisterPathOpen =>
        _register != null && _periodStartTick >= 0 && _corpseAnchorOk && _register.StableSince(_periodStartTick);

    /// <summary>The acting player's weapon ids (0, 1, or 2); empty if unresolved, ambiguous, or the player holds
    /// no tracked weapon (use <see cref="TryResolveActingPlayer"/> to tell those apart).</summary>
    public List<int> ResolveActingWeapons()
        => TryResolveActingPlayer(out var ws) ? ws : Empty;

    /// <summary>Resolves the acting unit to a roster player. True when a roster fingerprint matched;
    /// <paramref name="weapons"/> may then be empty (untracked gear such as DLC weapons, or unarmed). False for an
    /// enemy or an unresolved/ambiguous unit, which is where the caller's stale-latch mitigation applies. Conflating
    /// the two credited a Throw Stone kill by a DLC-armed Ramza to the previous actor's crossbow (2026-06-10).</summary>
    public bool TryResolveActingPlayer(out List<int> weapons)
    {
        weapons = Empty;
        LastResolveSource = ResolveSource.TqFallback;
        // The flags lane goes first; it names the acting unit structurally (better evidence than the
        // register's arrival-time snapshot). Any refusal falls through to the register/turn-queue chain.
        if (FlagPathOpen && TryResolveFlagOwner(out var flagRosterBase, out _))
        {
            weapons = Hands(flagRosterBase);
            LastResolveSource = ResolveSource.Flags;
            return true;
        }
        if (RegisterPathOpen)
        {
            var bridge = _register!.CurrentBridge;
            if (bridge == RosterBridge.Player)
            {
                weapons = Hands(_register.CurrentRosterBase);
                LastResolveSource = ResolveSource.Register;
                return true;
            }
            if (bridge == RosterBridge.Enemy) return false;
            // Unknown (duplicated roster identities) -> fall through to the turn-queue body.
        }

        if (!TryReadTqActor(out ushort maxHp, out ushort hp, out ushort level)) return false;

        // Collect all band entries matching (mhp, hp, lvl); the twin filter prefers real position.
        List<int>? found = null;
        bool foundSet = false;
        var twin = new TwinFilter();

        for (int s = 0; s < Offsets.BandSlots; s++)
        {
            long addr = Band.Entry(s);
            if (!Band.IsValid(_mem, addr)) continue;
            if (_mem.U16(addr + Offsets.AMaxHp) != maxHp) continue;
            if (_mem.U16(addr + Offsets.AHp) != hp) continue;
            if (_mem.U8(addr + Offsets.ALevel) != level) continue;

            bool realPos = _mem.U8(addr + Offsets.AGx) != 0 || _mem.U8(addr + Offsets.AGy) != 0;
            int actorWeapon = _mem.U16(addr + Offsets.AWeapon);
            int seatNameId = _mem.U16(addr + Offsets.ANameId);   // this seat's own identity, for the roster veto
            if (!FingerprintPlayer(level, _mem.U8(addr + Offsets.ABrave), _mem.U8(addr + Offsets.AFaith), actorWeapon, seatNameId, out var w))
                continue;   // not a roster unit -> enemy entry

            var verdict = twin.Step(realPos, haveAccumulation: foundSet);
            if (verdict == TwinFilter.Verdict.Skip) continue;
            if (verdict == TwinFilter.Verdict.Restart) { found = null; foundSet = false; }

            if (!foundSet) { found = w; foundSet = true; }
            else if (!SameSet(found!, w)) return false;   // two distinct weapon sets -> ambiguous
        }
        if (!foundSet) return false;
        weapons = found!;
        return true;
    }

    /// <summary>Both hands of a roster slot, keeping only ids that are real weapons (deduped).</summary>
    private List<int> Hands(long rosterBase)
    {
        var list = new List<int>(3);
        Add(list, _mem.U16(rosterBase + Offsets.RRHand));    // +0x14 right hand
        Add(list, _mem.U16(rosterBase + Offsets.RLHand));    // +0x16 (kept for safety; live it stays empty)
        Add(list, _mem.U16(rosterBase + Offsets.ROffHand));  // +0x18 the real dual-wield off-hand
        return list;
    }

    /// <summary>KillerStamp's roster-&gt;weapons resolve for the death-edge culprit stamp
    /// (KillTracker.Stamp.cs); weapon semantics stay in this class.</summary>
    internal List<int> HandsFromRoster(long rosterBase) => Hands(rosterBase);

    /// <summary>Raw right-hand id, no tracked-weapon filter. The 0/0xFF/0xFFFF sentinels tell
    /// unarmed from untracked gear (AttackRow.Policy.ComposeRow).</summary>
    public int RawMainHand(long rosterBase) => _mem.U16(rosterBase + Offsets.RRHand);

    /// <summary>The roster slot's SpriteSet byte (Offsets.RSprite), the human/monster gate for
    /// the "Fists" row decision (AttackRow.Policy.HumanSprite).</summary>
    public byte SpriteOf(long rosterBase) => _mem.U8(rosterBase + Offsets.RSprite);

    private void Add(List<int> list, ushort id)
    {
        if (id == 0x00FF || id == 0xFFFF) return;   // empty hand
        if (!_weapons.Contains(id)) return;          // shield / armor / non-weapon
        if (!list.Contains(id)) list.Add(id);        // dedup (same weapon somehow in both hands)
    }

    /// <summary>The acting player's main-hand (RRHand) weapon id, or 0 when not a roster player or ambiguous.
    /// A Living Weapon earns kills in any hand but commands its gift only from the main hand.</summary>
    public int ResolveActingMainHand()
    {
        // Flags-first, same as TryResolveActingPlayer (see ActorResolver.Flags.cs).
        if (FlagPathOpen && TryResolveFlagOwner(out var flagRosterBase, out _))
        {
            ushort flagRrHand = _mem.U16(flagRosterBase + Offsets.RRHand);
            return _weapons.Contains(flagRrHand) ? flagRrHand : 0;
        }
        if (RegisterPathOpen)
        {
            var bridge = _register!.CurrentBridge;
            if (bridge == RosterBridge.Player)
            {
                ushort rrHand = _mem.U16(_register.CurrentRosterBase + Offsets.RRHand);
                return _weapons.Contains(rrHand) ? rrHand : 0;
            }
            if (bridge == RosterBridge.Enemy) return 0;
            // Unknown -> fall through to the turn-queue body.
        }

        if (!TryReadTqActor(out ushort maxHp, out ushort hp, out ushort level)) return 0;

        int mainHand = 0; bool ambiguous = false;
        var twin = new TwinFilter();
        for (int s = 0; s < Offsets.BandSlots; s++)
        {
            long addr = Band.Entry(s);
            if (!Band.IsValid(_mem, addr)) continue;
            if (_mem.U16(addr + Offsets.AMaxHp) != maxHp) continue;
            if (_mem.U16(addr + Offsets.AHp) != hp) continue;
            if (_mem.U8(addr + Offsets.ALevel) != level) continue;

            bool realPos = _mem.U8(addr + Offsets.AGx) != 0 || _mem.U8(addr + Offsets.AGy) != 0;
            int actorWeapon = _mem.U16(addr + Offsets.AWeapon);
            int seatNameId = _mem.U16(addr + Offsets.ANameId);   // this seat's own identity, for the roster veto
            int rh = MainHandFromRoster(level, _mem.U8(addr + Offsets.ABrave), _mem.U8(addr + Offsets.AFaith), actorWeapon, seatNameId);
            if (rh == 0) continue;

            var verdict = twin.Step(realPos, haveAccumulation: mainHand != 0);
            if (verdict == TwinFilter.Verdict.Skip) continue;
            if (verdict == TwinFilter.Verdict.Restart) { mainHand = 0; ambiguous = false; }

            if (mainHand == 0) mainHand = rh;
            else if (mainHand != rh) ambiguous = true;
        }
        return ambiguous ? 0 : mainHand;
    }

    /// <summary>
    /// The acting unit's (level,brave,faith) fingerprint, from the same band walk as <see cref="ResolveActingMainHand"/>
    /// and <see cref="TurnTracker.TryActiveFingerprint"/> (maxHp>=2000 / level 1..99 rejected). False if surviving
    /// candidates disagree. KillTracker uses it to tell apart two wielders sharing a weapon set.
    /// </summary>
    public bool TryResolveActingFingerprint(out (int lvl, int br, int fa) fp)
        => TryResolveActingFingerprint(out fp, out _);

    /// <summary>Also outs the acting unit's frame nameId (Offsets.ANameId), so KillTracker's
    /// delayed-action committer compare (TrackDelayed, KillTracker.Delayed.cs) can check identity by nameId
    /// ([frame-1fc-nameid-mirror], [party-nameid-unique-key]). <paramref name="actorNameId"/> is 0 on any unresolved path.</summary>
    public bool TryResolveActingFingerprint(out (int lvl, int br, int fa) fp, out int actorNameId)
    {
        fp = default;
        actorNameId = 0;
        // Flags-first (see ActorResolver.Flags.cs). Returning the flags-named entry's own fp keeps
        // TrackDelayed's `_lastActorFp == (this slot's fp)` compare truthful when the flags lane named the committer.
        if (FlagPathOpen && TryResolveFlagOwner(out _, out long flagEntry))
        {
            fp = (_mem.U8(flagEntry + Offsets.ALevel), _mem.U8(flagEntry + Offsets.ABrave), _mem.U8(flagEntry + Offsets.AFaith));
            actorNameId = _mem.U16(flagEntry + Offsets.ANameId);
            return true;
        }
        if (RegisterPathOpen && _register!.CurrentEntry != 0)
        {
            // KillTracker only calls this after TryResolveActingPlayer confirmed a player, so any stable owner is
            // that player; return its fingerprint whatever the bridge says.
            fp = _register.CurrentFp;
            actorNameId = _register.CurrentNameId;
            return true;
        }

        if (!TryReadTqActor(out ushort maxHp, out ushort hp, out ushort level)) return false;

        (int, int, int) found = default;
        int foundNameId = 0;
        bool haveFp = false;
        var twin = new TwinFilter();

        for (int s = 0; s < Offsets.BandSlots; s++)
        {
            long addr = Band.Entry(s);
            if (!Band.IsValid(_mem, addr)) continue;
            if (_mem.U16(addr + Offsets.AMaxHp) != maxHp) continue;
            if (_mem.U16(addr + Offsets.AHp) != hp) continue;
            if (_mem.U8(addr + Offsets.ALevel) != level) continue;

            bool realPos = _mem.U8(addr + Offsets.AGx) != 0 || _mem.U8(addr + Offsets.AGy) != 0;
            var verdict = twin.Step(realPos, haveAccumulation: haveFp);
            if (verdict == TwinFilter.Verdict.Skip) continue;
            if (verdict == TwinFilter.Verdict.Restart) { found = default; foundNameId = 0; haveFp = false; }

            var candidate = ((int)level, (int)_mem.U8(addr + Offsets.ABrave), (int)_mem.U8(addr + Offsets.AFaith));
            if (!haveFp) { found = candidate; foundNameId = _mem.U16(addr + Offsets.ANameId); haveFp = true; }
            else if (found != candidate) return false;   // distinct fingerprints -> ambiguous
        }
        if (!haveFp) return false;
        fp = found;
        actorNameId = foundNameId;
        return true;
    }

    /// <summary>Order-independent equality for the tiny (&lt;=2) hand-weapon lists.</summary>
    internal static bool SameSet(List<int> a, List<int> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var x in a) if (!b.Contains(x)) return false;
        return true;
    }

    /// <summary>The roster fingerprint match both roster walks share. Does roster slot
    /// <paramref name="b"/> belong to a unit with this live (level,brave,faith)? Level uses the level-drift rule
    /// (exact equality once disarmed main-hand signatures after a mid-battle level-up). Extend here when the
    /// fingerprint grows; ActorRegister.Bridge.cs keeps its own copy, so touch both.</summary>
    private bool RosterFpMatches(long b, int level, int brave, int faith)
        => Band.LevelMatchesRoster(_mem.U8(b + Offsets.RLevel), level)
        && _mem.U8(b + Offsets.RBrave) == brave
        && _mem.U8(b + Offsets.RFaith) == faith;
}
