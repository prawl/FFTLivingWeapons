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
/// </summary>
internal sealed class ActorResolver
{
    private static readonly List<int> Empty = new();
    private readonly IGameMemory _mem;
    private readonly ISet<int> _weapons;   // ids that are real weapons (meta keys)

    public ActorResolver(IGameMemory mem, ISet<int> weapons)
    {
        _mem = mem;
        _weapons = weapons;
    }

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
            if (!FingerprintPlayer(level, _mem.U8(addr + Offsets.ABrave), _mem.U8(addr + Offsets.AFaith), out var w))
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

    /// <summary>Roster slot whose (level,brave,faith) matches. True when a PLAYER matched;
    /// <paramref name="hands"/> = their tracked weapons, possibly EMPTY (untracked/unarmed --
    /// still a player). Armed matches outrank empty-handed same-fingerprint matches (the old
    /// behavior for player-vs-monster fingerprint collisions); two armed slots with different
    /// sets are ambiguous and resolve false.</summary>
    private bool FingerprintPlayer(int level, int brave, int faith, out List<int> hands)
    {
        hands = Empty;
        List<int>? armed = null;
        bool emptyMatch = false;
        for (int s = 0; s < Offsets.RosterSlots; s++)
        {
            long b = Offsets.RosterBase + (long)s * Offsets.RosterStride;
            // level is the LIVE value; the roster keeps the pre-battle level (level-drift rule).
            if (!Band.LevelMatchesRoster(_mem.U8(b + Offsets.RLevel), level)) continue;
            if (_mem.U8(b + Offsets.RBrave) != brave) continue;
            if (_mem.U8(b + Offsets.RFaith) != faith) continue;
            var h = Hands(b);
            if (h.Count == 0) { emptyMatch = true; continue; }   // a player, just no tracked weapon
            if (armed == null) armed = h;
            else if (!SameSet(armed, h)) return false;           // two armed slots disagree -> ambiguous
        }
        if (armed != null) { hands = armed; return true; }
        return emptyMatch;
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
            int rh = MainHandFromRoster(level, _mem.U8(addr + Offsets.ABrave), _mem.U8(addr + Offsets.AFaith));
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

    /// <summary>RRHand weapon id of the unique roster slot matching (level, brave, faith), or 0
    /// when not a roster unit / ambiguous / unarmed.</summary>
    private int MainHandFromRoster(int level, int brave, int faith)
    {
        int found = 0; int rh = 0;
        for (int s = 0; s < Offsets.RosterSlots; s++)
        {
            long b = Offsets.RosterBase + (long)s * Offsets.RosterStride;
            if (_mem.U8(b + Offsets.RLevel) != level) continue;
            if (_mem.U8(b + Offsets.RBrave) != brave) continue;
            if (_mem.U8(b + Offsets.RFaith) != faith) continue;
            int candidate = _mem.U16(b + Offsets.RRHand);
            if (!_weapons.Contains(candidate)) continue;   // not a real weapon (shield / empty)
            if (++found > 1) return 0;
            rh = candidate;
        }
        return found == 1 ? rh : 0;
    }

    /// <summary>Order-independent equality for the tiny (&lt;=2) hand-weapon lists.</summary>
    internal static bool SameSet(List<int> a, List<int> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var x in a) if (!b.Contains(x)) return false;
        return true;
    }
}
