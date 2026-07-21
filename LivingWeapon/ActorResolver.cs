using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Identifies the acting player's equipped WEAPONS -- both hands, so a dual-wielder is
/// fully credited -- without the unreliable condensed nameId. active HP+MaxHP+level ->
/// BAND entry (live source; the static array freezes on battle restart) ->
/// (level,brave,faith) fingerprint -> roster hands. A hand counts ONLY if it holds a
/// real weapon (id in the meta set): a shield in either hand is ignored, and a weapon is
/// credited whichever hand it sits in (trust the item, not the slot, so a weapon-in-off-hand /
/// shield-in-primary loadout still scores). Returns an empty list when no band entry matches,
/// the actor isn't a roster unit (an enemy), or the match is ambiguous (two entries share
/// HP/MaxHP/level AND resolve to different weapon sets) -- a miss beats a mis-credit.
///
/// Twin filter: when matches include both a real-position entry (gx/gy != 0,0) and a (0,0)
/// frozen twin (roster unit's mirror in the band), prefer the real-position entry to avoid
/// reading stale HP from the twin.
///
/// REGISTER-FIRST (kill-attribution plan, 2026-07-01): each of the three public resolve methods
/// now opens with a preamble that answers off <see cref="ActorRegister"/> (the engine's own actor
/// pointer) INSTEAD of the turn-queue fingerprint walk below, but ONLY when the register's current
/// owner has been stable since STRICTLY BEFORE the current acted-period began
/// (<see cref="ActorRegister.StableSince"/>) AND no pending corpse outdates that ownership (the
/// corpse-anchor veto KillTracker pushes via <see cref="SetCorpseAnchorOk"/>). Ownership churn
/// after the period started, an unsatisfied gate, or a null/absent register (the four
/// <c>ActorResolverUnarmedTests</c> call sites) all fall straight through to the UNCHANGED
/// turn-queue body below -- this is the single hinge the whole pre-existing suite pins byte-for-
/// byte. HONESTY (carried over from the TurnTracker rebuild): the turn-queue fallback still
/// retains today's hover-trap and level+100-flake exposure -- the register narrows, it does not
/// eliminate, the ambiguity window.
/// </summary>
internal sealed partial class ActorResolver
{
    private static readonly List<int> Empty = new();
    private readonly IGameMemory _mem;
    private readonly ISet<int> _weapons;   // ids that are real weapons (meta keys)
    private readonly ActorRegister? _register;
    private int _periodStartTick = -1;     // -1 = not currently inside an acted-period
    private bool _corpseAnchorOk = true;   // KillTracker's per-tick corpse-anchor veto (V1)

    /// <summary>Which path answered the most recent <see cref="TryResolveActingPlayer"/>
    /// credit-worthy resolve (a resolved player, tracked or not): the flags lane (LW-63), the
    /// register path, or the turn-queue fallback. Feeds the latch log line's source tag and
    /// KillTracker's fallback-attribution counters (see ActorResolver.Flags.cs's class doc).</summary>
    internal ResolveSource LastResolveSource { get; private set; }

    /// <summary>True when the most recent resolve came from the register path specifically
    /// (rather than the flags lane or the turn-queue fallback) -- kept for zero churn at the
    /// three pre-existing consumer sites (ActorResolver.cs, KillTracker.cs); no test asserts on
    /// it directly (inventory confirmed). Not equivalent to "not the turn-queue fallback" since
    /// LW-63: a flags-sourced resolve is also not a fallback, but also not this.</summary>
    public bool LastResolveViaRegister => LastResolveSource == ResolveSource.Register;

    public ActorResolver(IGameMemory mem, ISet<int> weapons, ActorRegister? register = null)
    {
        _mem = mem;
        _weapons = weapons;
        _register = register;
    }

    /// <summary>Marks the start of a fresh acted-period at register tick
    /// <paramref name="tick"/>. Called by KillTracker on the edge-guarded first acted==1 tick
    /// following a debounced fall (or battle start) -- never re-called mid-period, so a
    /// sub-UnfreezeTicks Acted drift dip cannot refresh the gate.</summary>
    public void BeginActedPeriod(int tick) => _periodStartTick = tick;

    /// <summary>Marks the end of the current acted-period (the debounced falling edge). The
    /// register path is inert (falls through to the turn-queue body) outside any period.</summary>
    public void EndActedPeriod() => _periodStartTick = -1;

    /// <summary>KillTracker's per-tick corpse-anchor veto (V1 of the kill-attribution plan):
    /// refuses the register path when any pending corpse is at least as old as the current
    /// ownership (pending-birth stamp &lt;= arrival tick, register-tick currency) -- a killer's
    /// turn must CONTAIN the death, so an ownership arriving at or after a corpse's first
    /// pending sight cannot be its killer. Computed in KillTracker.cs (it owns the per-slot
    /// pending-birth stamps); pushed here once per Poll tick.</summary>
    public void SetCorpseAnchorOk(bool ok) => _corpseAnchorOk = ok;

    /// <summary>True iff the register path is eligible to answer THIS resolve call: a period is
    /// open, no pending corpse outdates the current ownership, and that ownership has been stable
    /// since strictly before the period began.</summary>
    private bool RegisterPathOpen =>
        _register != null && _periodStartTick >= 0 && _corpseAnchorOk && _register.StableSince(_periodStartTick);

    /// <summary>The acting player's weapon ids (0, 1, or 2), or empty if unresolved/ambiguous
    /// OR the resolved player holds no tracked weapon (callers needing the distinction use
    /// <see cref="TryResolveActingPlayer"/>).</summary>
    public List<int> ResolveActingWeapons()
        => TryResolveActingPlayer(out var ws) ? ws : Empty;

    /// <summary>Resolve the acting unit to a ROSTER player. True when a roster fingerprint
    /// matched -- <paramref name="weapons"/> then holds their tracked weapon ids, POSSIBLY EMPTY
    /// (untracked gear like DLC weapons, or unarmed): a real player acted, they just hold no
    /// living weapon. False when no roster unit matches (an enemy's acted-period, or unresolved/
    /// ambiguous) -- the caller's stale-latch mitigation applies only then. The distinction is
    /// load-bearing: conflating the two credited a Throw Stone kill by a DLC-armed Ramza to the
    /// PREVIOUS actor's crossbow (live, 2026-06-10).</summary>
    public bool TryResolveActingPlayer(out List<int> weapons)
    {
        weapons = Empty;
        LastResolveSource = ResolveSource.TqFallback;
        // LW-63: the flags lane goes FIRST -- it names the acting unit structurally and is
        // strictly better evidence than the register's arrival-time snapshot (D3). Any refusal
        // (no candidate, ambiguity, capture failure, roster-bridge failure) falls straight
        // through to the register/turn-queue chain below, unchanged.
        if (FlagPathOpen && TryResolveFlagOwner(out var flagRosterBase, out _, out _))
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

        ushort maxHp = _mem.U16(Offsets.TurnQueue + Offsets.TqMaxHp);
        ushort hp = _mem.U16(Offsets.TurnQueue + Offsets.TqHp);
        ushort level = _mem.U16(Offsets.TurnQueue + Offsets.TqLevel);
        if (maxHp == 0 || maxHp >= 2000 || level < 1 || level > 99) return false;

        // Collect all band entries matching (mhp, hp, lvl); track whether any have real position.
        List<int>? found = null;
        bool foundSet = false;
        bool foundReal = false;   // any match at gx/gy != (0,0)

        for (int s = 0; s < Offsets.BandSlots; s++)
        {
            long addr = Band.Entry(s);
            if (!Band.IsValid(_mem, addr)) continue;
            if (_mem.U16(addr + Offsets.AMaxHp) != maxHp) continue;
            if (_mem.U16(addr + Offsets.AHp) != hp) continue;
            if (_mem.U8(addr + Offsets.ALevel) != level) continue;

            bool realPos = _mem.U8(addr + Offsets.AGx) != 0 || _mem.U8(addr + Offsets.AGy) != 0;
            int actorWeapon = _mem.U16(addr + Offsets.AWeapon);
            if (!FingerprintPlayer(level, _mem.U8(addr + Offsets.ABrave), _mem.U8(addr + Offsets.AFaith), actorWeapon, out var w))
                continue;   // not a roster unit -> enemy entry

            // Twin filter: if we already have a real-position match, skip (0,0) entries.
            if (foundReal && !realPos) continue;
            // If this is real and we only had (0,0) so far, discard the old match and restart.
            if (realPos && !foundReal && foundSet)
            {
                found = null;
                foundSet = false;
                foundReal = true;
            }
            if (realPos) foundReal = true;

            if (!foundSet) { found = w; foundSet = true; }
            else if (!SameSet(found!, w)) return false;   // two distinct weapon sets -> ambiguous
        }
        if (!foundSet) return false;
        weapons = found!;
        return true;
    }

    /// <summary>Roster slot whose (level,brave,faith) matches, using the band actor's equipped
    /// weapon to break same-fingerprint collisions. Two priority tracks run in parallel:
    /// (1) weapon-matched: slots whose hand set contains <paramref name="actorWeapon"/>; takes
    /// priority over the legacy path when unambiguous. (2) legacy armed: any armed match; used
    /// when the weapon path produces no candidate or the weapon is untracked. Returns false only
    /// when the armed path is ambiguous (two armed slots disagree on set). <paramref name="hands"/>
    /// is possibly empty (untracked / unarmed player -- still a real player turn).
    /// NOTE: the roster-walk here is duplicated in <see cref="MainHandFromRoster"/> (their
    /// ambiguity semantics differ: set equality here vs RRHand identity there); a shared helper
    /// is a follow-up seam.</summary>
    private bool FingerprintPlayer(int level, int brave, int faith, int actorWeapon, out List<int> hands)
    {
        hands = Empty;
        bool weaponTracked = actorWeapon != 0 && actorWeapon != 0xFFFF && _weapons.Contains(actorWeapon);
        bool actorUnarmed = actorWeapon == 0 || actorWeapon == 0x00FF || actorWeapon == 0xFFFF;
        List<int>? weaponSet = null;    // hand-set of the first slot that contains actorWeapon
        bool weaponAmbiguous = false;   // two weapon-matched slots disagree on their full set
        List<int>? armed = null;        // first armed match (legacy path)
        bool armedAmbiguous = false;    // legacy: two armed slots disagree on set
        bool emptyMatch = false;

        for (int s = 0; s < Offsets.RosterSlots; s++)
        {
            long b = Offsets.RosterBase + (long)s * Offsets.RosterStride;
            // level is the LIVE value; the roster keeps the pre-battle level (level-drift rule).
            if (!Band.LevelMatchesRoster(_mem.U8(b + Offsets.RLevel), level)) continue;
            if (_mem.U8(b + Offsets.RBrave) != brave) continue;
            if (_mem.U8(b + Offsets.RFaith) != faith) continue;
            var h = Hands(b);
            if (h.Count == 0) { emptyMatch = true; continue; }
            if (weaponTracked && h.Contains(actorWeapon))
            {
                if (weaponSet == null) weaponSet = h;
                else if (!SameSet(weaponSet, h)) weaponAmbiguous = true;
            }
            if (armed == null) armed = h;
            else if (!SameSet(armed, h)) armedAmbiguous = true;
        }
        // Resolution order matters: weapon-matched path wins when unambiguous.
        if (weaponSet != null && !weaponAmbiguous) { hands = weaponSet; return true; }  // weapon disambiguated
        // Band-confirmed UNARMED (mirror of the armed weapon track above, for the b42f77a-symmetric case):
        // a no-weapon actor (band +0x04 reads the unarmed sentinel) that ALSO has its own empty-hands roster
        // slot resolves to EMPTY rather than borrowing a (level,brave,faith)-colliding armed neighbor's weapon.
        // Resolving Empty (resolved-but-untracked) routes the kill to KillTracker's _lethalUntracked no-credit
        // path. NOTE: for an unarmed actor this also supersedes the legacy `armedAmbiguous -> return false`
        // sticky-latch fallthrough below -- crediting nobody beats falling through to a stale latch; still
        // "miss, never mis-credit". Strictly gated on emptyMatch so a genuinely-armed unit whose band field is
        // merely unpopulated (no unarmed lookalike) still uses the armed path.
        if (actorUnarmed && emptyMatch) { hands = Empty; return true; }
        if (armed != null && !armedAmbiguous)      { hands = armed;     return true; }  // legacy unique armed
        if (armed != null)                          return false;                       // exact legacy bail
        return emptyMatch;                                                               // player, no tracked weapon
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
    /// (KillTracker.Stamp.cs) -- weapon semantics stay in this class.</summary>
    internal List<int> HandsFromRoster(long rosterBase) => Hands(rosterBase);

    /// <summary>LW-31 stage 3: the RAW right-hand weapon id at a roster slot, with NO tracked-weapon
    /// filter (unlike <see cref="Hands"/>, which drops shields/untracked ids/empty-hand sentinels
    /// entirely). AttackCard's row-rename resolve needs to see the sentinel itself (0/0xFF/0xFFFF)
    /// to distinguish "unarmed" from "wielding something untracked": both look identical through
    /// the filtered Hands() lens, but only the former ever earns the "Fists" row
    /// (AttackRow.Policy.ComposeRow).</summary>
    public int RawMainHand(long rosterBase) => _mem.U16(rosterBase + Offsets.RRHand);

    /// <summary>LW-31 stage 3: the roster slot's SpriteSet byte (Offsets.RSprite), the human/monster
    /// gate an unarmed actor's "Fists" row decision needs (AttackRow.Policy.HumanSprite).</summary>
    public byte SpriteOf(long rosterBase) => _mem.U8(rosterBase + Offsets.RSprite);

    private void Add(List<int> list, ushort id)
    {
        if (id == 0x00FF || id == 0xFFFF) return;   // empty hand
        if (!_weapons.Contains(id)) return;          // shield / armor / non-weapon
        if (!list.Contains(id)) list.Add(id);        // dedup (same weapon somehow in both hands)
    }

    /// <summary>The acting player's main-hand (RRHand) weapon id, or 0 when the actor is not
    /// a roster player or the fingerprint is ambiguous. Mirrors <see cref="ResolveActingWeapons"/>
    /// but returns only the right-hand slot. A Living Weapon earns kills in any hand, but
    /// commands its gift only from the main hand.</summary>
    public int ResolveActingMainHand()
    {
        // LW-63: same flags-first preamble as TryResolveActingPlayer (see ActorResolver.Flags.cs).
        if (FlagPathOpen && TryResolveFlagOwner(out var flagRosterBase, out _, out _))
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

        ushort maxHp = _mem.U16(Offsets.TurnQueue + Offsets.TqMaxHp);
        ushort hp    = _mem.U16(Offsets.TurnQueue + Offsets.TqHp);
        ushort level = _mem.U16(Offsets.TurnQueue + Offsets.TqLevel);
        if (maxHp == 0 || maxHp >= 2000 || level < 1 || level > 99) return 0;

        bool foundReal = false;
        int mainHand = 0; bool ambiguous = false;
        for (int s = 0; s < Offsets.BandSlots; s++)
        {
            long addr = Band.Entry(s);
            if (!Band.IsValid(_mem, addr)) continue;
            if (_mem.U16(addr + Offsets.AMaxHp) != maxHp) continue;
            if (_mem.U16(addr + Offsets.AHp)    != hp)    continue;
            if (_mem.U8(addr  + Offsets.ALevel) != level) continue;

            bool realPos = _mem.U8(addr + Offsets.AGx) != 0 || _mem.U8(addr + Offsets.AGy) != 0;
            int actorWeapon = _mem.U16(addr + Offsets.AWeapon);
            int rh = MainHandFromRoster(level, _mem.U8(addr + Offsets.ABrave), _mem.U8(addr + Offsets.AFaith), actorWeapon);
            if (rh == 0) continue;

            if (foundReal && !realPos) continue;
            if (realPos && !foundReal && mainHand != 0)
            {
                mainHand = 0; ambiguous = false; foundReal = true;
            }
            if (realPos) foundReal = true;

            if (mainHand == 0) mainHand = rh;
            else if (mainHand != rh) ambiguous = true;
        }
        return ambiguous ? 0 : mainHand;
    }

    /// <summary>RRHand weapon id of the acting unit's roster slot, or 0 when not a roster
    /// unit / ambiguous / unarmed. If <paramref name="actorWeapon"/> is tracked and any
    /// matching roster slot has RRHand equal to it, returns that weapon (band-confirmed main
    /// hand). Otherwise falls back to the legacy unique-match: exactly one armed slot -> its
    /// RRHand; else 0 (ambiguous or unarmed).
    /// A band-confirmed unarmed actor (sentinel weapon) that has its own empty-hands roster
    /// slot returns 0 (no main hand to command from) rather than borrowing a
    /// fingerprint-colliding armed slot's RRHand.
    /// NOTE: the roster-walk here is duplicated from <see cref="FingerprintPlayer"/> (their
    /// ambiguity semantics differ: RRHand identity here vs set equality there); a shared helper
    /// is a follow-up seam.</summary>
    private int MainHandFromRoster(int level, int brave, int faith, int actorWeapon)
    {
        bool weaponTracked = actorWeapon != 0 && actorWeapon != 0xFFFF && _weapons.Contains(actorWeapon);
        bool actorUnarmed = actorWeapon == 0 || actorWeapon == 0x00FF || actorWeapon == 0xFFFF;
        bool emptyMatch = false;
        int found = 0; int rh = 0;
        for (int s = 0; s < Offsets.RosterSlots; s++)
        {
            long b = Offsets.RosterBase + (long)s * Offsets.RosterStride;
            // level is the LIVE value; the roster keeps the pre-battle level (level-drift rule --
            // exact equality here silently disarmed main-hand signatures after a mid-battle level-up).
            if (!Band.LevelMatchesRoster(_mem.U8(b + Offsets.RLevel), level)) continue;
            if (_mem.U8(b + Offsets.RBrave) != brave) continue;
            if (_mem.U8(b + Offsets.RFaith) != faith) continue;
            int candidate = _mem.U16(b + Offsets.RRHand);
            if (!_weapons.Contains(candidate)) { emptyMatch = true; continue; }   // empty / shield main hand
            if (weaponTracked && candidate == actorWeapon) return actorWeapon;   // band-confirmed main hand
            if (found == 0) rh = candidate;
            found++;
        }
        // Band-confirmed unarmed actor with its own empty-hands slot: no main-hand weapon to command a
        // signature from (mirror of the FingerprintPlayer unarmed guard). Worst case = a signature misses,
        // never a mis-credit. Gated on actorUnarmed so armed actors are unaffected.
        if (actorUnarmed && emptyMatch) return 0;
        return found == 1 ? rh : 0;
    }

    /// <summary>
    /// The acting unit's (level,brave,faith) fingerprint, resolved by the same band walk as
    /// <see cref="ResolveActingMainHand"/> and <see cref="TurnTracker.TryActiveFingerprint"/>.
    /// Used by <see cref="KillTracker"/> to attribute a per-wielder signature (e.g. Larceny) to
    /// the specific acting unit when two wielders share the same weapon set and SameSet alone
    /// cannot distinguish them.
    ///
    /// Reads turn-queue TqMaxHp/TqHp/TqLevel; rejects if maxHp==0 || maxHp>=2000 || level outside
    /// 1..99. Walks band slots 0..BandSlots-1, applies Band.IsValid, matches AMaxHp/AHp/ALevel;
    /// applies the twin filter (prefer real-position gx/gy != 0,0; restart on first real match).
    /// Returns false (default fp) if two surviving candidates have DIFFERENT fingerprints
    /// (ambiguous); true with the resolved fingerprint otherwise. Does NOT require the candidate to
    /// be a roster player -- KillTracker only calls this after confirming the actor is a player.
    /// </summary>
    public bool TryResolveActingFingerprint(out (int lvl, int br, int fa) fp)
    {
        fp = default;
        // LW-63: same flags-first preamble (see ActorResolver.Flags.cs). Pairs with the delayed
        // fp-gate (KillTracker.Delayed.cs) -- returning the flags-named entry's OWN fp (not the
        // register's) keeps TrackDelayed's `_lastActorFp == (this slot's fp)` compare truthful
        // when the flags lane, not the register, named the committer (D5).
        if (FlagPathOpen && TryResolveFlagOwner(out _, out long flagEntry, out _))
        {
            fp = (_mem.U8(flagEntry + Offsets.ALevel), _mem.U8(flagEntry + Offsets.ABrave), _mem.U8(flagEntry + Offsets.AFaith));
            return true;
        }
        if (RegisterPathOpen && _register!.CurrentEntry != 0)
        {
            // The method's contract does not require a roster player -- KillTracker only calls
            // this after TryResolveActingPlayer already confirmed one, so any stable owner here
            // IS that same player. Return its fingerprint regardless of bridge classification.
            fp = _register.CurrentFp;
            return true;
        }

        ushort maxHp = _mem.U16(Offsets.TurnQueue + Offsets.TqMaxHp);
        ushort hp    = _mem.U16(Offsets.TurnQueue + Offsets.TqHp);
        ushort level = _mem.U16(Offsets.TurnQueue + Offsets.TqLevel);
        if (maxHp == 0 || maxHp >= 2000 || level < 1 || level > 99) return false;

        (int, int, int) found = default;
        bool haveFp    = false;
        bool foundReal = false;

        for (int s = 0; s < Offsets.BandSlots; s++)
        {
            long addr = Band.Entry(s);
            if (!Band.IsValid(_mem, addr)) continue;
            if (_mem.U16(addr + Offsets.AMaxHp) != maxHp) continue;
            if (_mem.U16(addr + Offsets.AHp)    != hp)    continue;
            if (_mem.U8(addr  + Offsets.ALevel) != level) continue;

            bool realPos = _mem.U8(addr + Offsets.AGx) != 0 || _mem.U8(addr + Offsets.AGy) != 0;
            if (foundReal && !realPos) continue;
            if (realPos && !foundReal && haveFp) { found = default; haveFp = false; foundReal = true; }
            if (realPos) foundReal = true;

            var candidate = ((int)level, (int)_mem.U8(addr + Offsets.ABrave), (int)_mem.U8(addr + Offsets.AFaith));
            if (!haveFp) { found = candidate; haveFp = true; }
            else if (found != candidate) return false;   // distinct fingerprints -> ambiguous
        }
        if (!haveFp) return false;
        fp = found;
        return true;
    }

    /// <summary>Order-independent equality for the tiny (&lt;=2) hand-weapon lists.</summary>
    internal static bool SameSet(List<int> a, List<int> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var x in a) if (!b.Contains(x)) return false;
        return true;
    }
}
