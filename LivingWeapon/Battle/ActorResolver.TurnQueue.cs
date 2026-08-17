using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// ActorResolver's TURN-QUEUE lane (LW-252 stage 2 split, 200-line rule: ActorResolver.cs had
/// grown to ~452 lines). Holds the private members the band-entry loops in ActorResolver.cs
/// drive: TryReadTqActor (the condensed-actor read + sanity gate), the two roster walks
/// (FingerprintPlayer, MainHandFromRoster) those loops call once per matched band seat, and the
/// TwinFilter struct they share. Moved verbatim off ActorResolver.cs -- see that file's class doc
/// for where this lane sits among the register/flags preambles.
///
/// LW-252 VETO (both roster walks below): stage 1 (Wielder.cs) closed the same legacy-borrow bug
/// for the Living Weapon signature Locate path; this closes its ActorResolver twin. The bug: a
/// fingerprint match alone (level,brave,faith) can collide between two DIFFERENT roster units
/// (a Dragoon and an fp-twin Archer, say), and the pre-LW-252 walks below had no way to tell
/// which one the ACTING seat actually was -- the lone armed match won by default ("legacy unique
/// armed"/`found == 1`), even when it named the wrong unit. A live probe (2026-08-17) proved
/// party roster nameIds are unique per occupied row and the combat frame mirrors them at
/// Offsets.ANameId (ledger [frame-1fc-nameid-mirror] PROVEN; [party-nameid-unique-key] pending
/// owner flip) -- so the CALLER (ActorResolver.cs) now reads the matched band seat's own frame
/// nameId and passes it in as <c>seatNameId</c>; each roster row a walk considers is required to
/// carry that SAME nameId once seatNameId is readable ( &gt; 0), else the row is skipped outright
/// -- a foreign fp-twin row can no longer be adopted for a seat whose own identity is known.
/// seatNameId == 0 (unseeded/unreadable) disarms the veto entirely, degrading byte-for-byte to
/// today's pre-LW-252 behavior (the same fail-open convention Wielder.cs's tier-2 veto uses).
/// </summary>
internal sealed partial class ActorResolver
{
    /// <summary>Roster slot whose (level,brave,faith) matches, using the band actor's equipped
    /// weapon to break same-fingerprint collisions. Two priority tracks run in parallel:
    /// (1) weapon-matched: slots whose hand set contains <paramref name="actorWeapon"/>; takes
    /// priority over the legacy path when unambiguous. (2) legacy armed: any armed match; used
    /// when the weapon path produces no candidate or the weapon is untracked. Returns false only
    /// when the armed path is ambiguous (two armed slots disagree on set). <paramref name="hands"/>
    /// is possibly empty (untracked / unarmed player -- still a real player turn).
    /// NOTE: the walk BODY here and in <see cref="MainHandFromRoster"/> stay separate on purpose
    /// (their ambiguity semantics differ: set equality + dual-track accumulation here vs RRHand
    /// identity + a band-confirmed mid-loop return there); the shared truth is the fingerprint
    /// MATCH RULE, <see cref="RosterFpMatches"/> (LW-154).
    /// LW-252: <paramref name="seatNameId"/> is the acting band seat's own frame nameId
    /// (Offsets.ANameId), read by the caller once per matched seat. See this file's class doc for
    /// the veto it arms below.</summary>
    private bool FingerprintPlayer(int level, int brave, int faith, int actorWeapon, int seatNameId, out List<int> hands)
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
            if (!RosterFpMatches(b, level, brave, faith)) continue;
            // LW-252: the acting seat's own nameId is known (> 0) -- a fingerprint-matching row
            // whose OWN roster nameId disagrees is a DIFFERENT unit, not this one. Skip it
            // outright rather than letting it feed the legacy armed/weapon-matched tracks below.
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

    /// <summary>RRHand weapon id of the acting unit's roster slot, or 0 when not a roster
    /// unit / ambiguous / unarmed. If <paramref name="actorWeapon"/> is tracked and any
    /// matching roster slot has RRHand equal to it, returns that weapon (band-confirmed main
    /// hand). Otherwise falls back to the legacy unique-match: exactly one armed slot -> its
    /// RRHand; else 0 (ambiguous or unarmed).
    /// A band-confirmed unarmed actor (sentinel weapon) that has its own empty-hands roster
    /// slot returns 0 (no main hand to command from) rather than borrowing a
    /// fingerprint-colliding armed slot's RRHand.
    /// NOTE: the walk BODY here and in <see cref="FingerprintPlayer"/> stay separate on purpose
    /// (their ambiguity semantics differ: RRHand identity + a band-confirmed mid-loop return
    /// here vs set equality + dual-track accumulation there); the shared truth is the
    /// fingerprint MATCH RULE, <see cref="RosterFpMatches"/> (LW-154).
    /// LW-252: <paramref name="seatNameId"/> mirrors <see cref="FingerprintPlayer"/>'s own
    /// parameter -- same veto, same class-doc rationale.</summary>
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
            // LW-252: same veto as FingerprintPlayer -- see this file's class doc.
            if (seatNameId > 0 && _mem.U16(b + Offsets.RNameId) != seatNameId) continue;
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

    /// <summary>The three condensed turn-queue reads plus the sanity gate every tq-fallback
    /// resolve opens with (LW-154: was spelled verbatim at all three sites). False = the
    /// condensed struct does not hold a plausible unit; each caller returns its own failure
    /// value. The gate is unchanged from the copies: maxHp 0 or &gt;= 2000, or level outside
    /// 1..99, refuses.</summary>
    private bool TryReadTqActor(out ushort maxHp, out ushort hp, out ushort level)
    {
        maxHp = _mem.U16(Offsets.TurnQueue + Offsets.TqMaxHp);
        hp    = _mem.U16(Offsets.TurnQueue + Offsets.TqHp);
        level = _mem.U16(Offsets.TurnQueue + Offsets.TqLevel);
        return maxHp != 0 && maxHp < 2000 && level >= 1 && level <= 99;
    }

    /// <summary>The twin filter's one home for THIS RESOLVER's three tq-fallback walks (LW-154:
    /// was spelled out at each, differing only in accumulation variables). Band.ActiveOwner and
    /// Wielder.Locate carry their own prefer-real variants with different reset payloads; those
    /// stayed put on purpose (different walks, different accumulations), so a repo-wide fold is
    /// NOT implied here. Prefers real-position (gx/gy != 0,0) band seats over a roster unit's
    /// frozen (0,0) mirror, whose stale fields would otherwise spoil the resolve as ambiguous.
    /// Per-candidate protocol: Skip = a (0,0) seat after a real match latched, ignore it;
    /// Restart = the FIRST real seat arriving after (0,0)-only accumulation, so the caller
    /// discards what it accumulated and re-accumulates from this candidate; Keep = accumulate
    /// normally. The caller supplies its own have-accumulation predicate and owns the discard
    /// action (the three callers' accumulations and ambiguity policies differ on purpose);
    /// this struct owns only the real-position bookkeeping. Construct fresh per resolve.</summary>
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
