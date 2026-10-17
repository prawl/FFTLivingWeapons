using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// ActorResolver's turn-queue lane, split off ActorResolver.cs for the 200-line rule. TryReadTqActor,
/// the roster walks FingerprintPlayer and MainHandFromRoster, and TwinFilter.
/// A (level,brave,faith) match can collide between two roster units (a Dragoon and an Archer twin) and the old walks
/// let the lone armed match win even when it was the wrong unit. A 2026-08-17 probe showed roster nameIds are unique per row
/// and mirrored in the combat frame at Offsets.ANameId ([frame-1fc-nameid-mirror] proven, [party-nameid-unique-key] pending
/// owner flip). The caller passes the seat's frame nameId as seatNameId and a row with another nameId is skipped;
/// seatNameId == 0 (unreadable) disarms the check, as in Wielder.cs.
/// </summary>
internal sealed partial class ActorResolver
{
    /// <summary>Roster slot matching (level,brave,faith), using the band actor's weapon to break fingerprint ties.
    /// A weapon-matched slot wins when unambiguous, else any armed match; false only when armed slots disagree on set.
    /// <paramref name="hands"/> may be empty (untracked or unarmed player). <paramref name="seatNameId"/> is the seat's frame nameId.
    /// Kept apart from <see cref="MainHandFromRoster"/> because ambiguity rules differ; both use <see cref="RosterFpMatches"/>.</summary>
    private bool FingerprintPlayer(int level, int brave, int faith, int actorWeapon, int seatNameId, out List<int> hands)
    {
        hands = Empty;
        bool weaponTracked = actorWeapon != 0 && actorWeapon != 0xFFFF && _weapons.Contains(actorWeapon);
        bool actorUnarmed = actorWeapon == 0 || actorWeapon == 0x00FF || actorWeapon == 0xFFFF;
        List<int>? weaponSet = null;    // hand-set of the first slot that contains actorWeapon
        bool weaponAmbiguous = false;   // two weapon-matched slots disagree on their full set
        List<int>? armed = null;        // first armed match (legacy path)
        bool armedAmbiguous = false;    // legacy. Two armed slots disagree on set
        bool emptyMatch = false;

        for (int s = 0; s < Offsets.RosterSlots; s++)
        {
            long b = Offsets.RosterBase + (long)s * Offsets.RosterStride;
            if (!RosterFpMatches(b, level, brave, faith)) continue;
            // A row whose own nameId differs from the known seat nameId is a different unit.
            if (seatNameId > 0 && _mem.U16(b + Offsets.RNameId) != seatNameId) continue;
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
        // Weapon-matched wins when unambiguous.
        if (weaponSet != null && !weaponAmbiguous) { hands = weaponSet; return true; }  // weapon disambiguated
        // Unarmed band actor with its own empty-hands slot resolves to Empty rather than borrowing an armed fingerprint twin's weapon (b42f77a).
        // Empty routes to KillTracker's _lethalUntracked no-credit path and also overrides the armedAmbiguous bail below. Miss, never mis-credit.
        // Gated on emptyMatch so an armed unit with an unpopulated band field still takes the armed path.
        if (actorUnarmed && emptyMatch) { hands = Empty; return true; }
        if (armed != null && !armedAmbiguous) { hands = armed; return true; }  // legacy unique armed
        if (armed != null) return false;                       // exact legacy bail
        return emptyMatch;                                                               // player, no tracked weapon
    }

    /// <summary>RRHand weapon id of the acting unit's roster slot, or 0 when not a roster unit, ambiguous or unarmed.
    /// A tracked <paramref name="actorWeapon"/> matching a slot's RRHand wins; otherwise exactly one armed slot gives its RRHand.
    /// An unarmed actor with its own empty-hands slot returns 0 instead of borrowing a twin's RRHand.
    /// <paramref name="seatNameId"/> is the check from <see cref="FingerprintPlayer"/>; the shared rule is <see cref="RosterFpMatches"/>.</summary>
    private int MainHandFromRoster(int level, int brave, int faith, int actorWeapon, int seatNameId)
    {
        bool weaponTracked = actorWeapon != 0 && actorWeapon != 0xFFFF && _weapons.Contains(actorWeapon);
        bool actorUnarmed = actorWeapon == 0 || actorWeapon == 0x00FF || actorWeapon == 0xFFFF;
        bool emptyMatch = false;
        int found = 0; int rh = 0;
        for (int s = 0; s < Offsets.RosterSlots; s++)
        {
            long b = Offsets.RosterBase + (long)s * Offsets.RosterStride;
            if (!RosterFpMatches(b, level, brave, faith)) continue;
            // Same check as FingerprintPlayer.
            if (seatNameId > 0 && _mem.U16(b + Offsets.RNameId) != seatNameId) continue;
            int candidate = _mem.U16(b + Offsets.RRHand);
            if (!_weapons.Contains(candidate)) { emptyMatch = true; continue; }   // empty / shield main hand
            if (weaponTracked && candidate == actorWeapon) return actorWeapon;   // band-confirmed main hand
            if (found == 0) rh = candidate;
            found++;
        }
        // Unarmed actor with its own empty-hands slot has no main hand to command from; a signature misses, never mis-credits.
        if (actorUnarmed && emptyMatch) return 0;
        return found == 1 ? rh : 0;
    }

    /// <summary>Reads the condensed turn-queue maxHp, hp and level and applies the sanity gate shared by the tq-fallback resolves.
    /// False means no plausible unit. MaxHp 0 or &gt;= 2000, or level outside 1..99. Each caller returns its own failure value.</summary>
    private bool TryReadTqActor(out ushort maxHp, out ushort hp, out ushort level)
    {
        maxHp = _mem.U16(Offsets.TurnQueue + Offsets.TqMaxHp);
        hp = _mem.U16(Offsets.TurnQueue + Offsets.TqHp);
        level = _mem.U16(Offsets.TurnQueue + Offsets.TqLevel);
        return maxHp != 0 && maxHp < 2000 && level >= 1 && level <= 99;
    }

    /// <summary>Twin filter for this resolver's three tq-fallback walks. Prefers real-position (gx/gy != 0,0) band seats over a
    /// roster unit's frozen (0,0) mirror, whose stale fields would make the resolve look ambiguous. Band.ActiveOwner and Wielder.Locate keep their own variants.
    /// Verdicts. Skip ignores a (0,0) seat after a real match; Restart is the first real seat after (0,0)-only accumulation, so the caller
    /// discards and restarts; Keep accumulates normally. The caller owns the accumulation. Construct fresh per resolve.</summary>
    private struct TwinFilter
    {
        internal enum Verdict { Keep, Restart, Skip }

        private bool _foundReal;   // any accepted match at gx/gy != (0,0) so far

        internal Verdict Step(bool realPos, bool haveAccumulation)
        {
            if (_foundReal && !realPos) return Verdict.Skip;
            bool restart = realPos && !_foundReal && haveAccumulation;
            if (realPos) _foundReal = true;
            return restart ? Verdict.Restart : Verdict.Keep;
        }
    }
}
