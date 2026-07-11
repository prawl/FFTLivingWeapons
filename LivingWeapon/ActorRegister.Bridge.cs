namespace LivingWeapon;

/// <summary>
/// ActorRegister's roster-identity bridge, split out at the 200-line refactor seam: matches the
/// engine's actor-pointer entry (nameId, level, brave, faith) against the 20 roster slots to
/// classify it Player, Enemy, or Unknown. ActorRegister.cs (the tick-driven ownership tracker)
/// calls into this; the enum and the class-level doc stay there.
/// </summary>
internal sealed partial class ActorRegister
{
    /// <summary>Roster bridge: a slot matches iff it is OCCUPIED (RLevel 1..99), its RNameId equals
    /// <paramref name="nameId"/> (already required &gt;0 by the 0==0 trap guard below), AND its
    /// (level,brave,faith) matches the entry's OWN band bytes (belt-and-suspenders against the
    /// unproven enemy/player nameId-pool overlap, K2 of the kill-attribution plan). Exactly one
    /// match -&gt; Player. More than one match, OR <paramref name="nameId"/> itself reading 0 (the
    /// 0==0 trap: a capture failure, not a confident "this is an enemy"), resolves Unknown (the
    /// gate is unsatisfied and callers fall through to the TQ-fingerprint body, so a genuine
    /// player whose nameId capture failed still resolves normally there). Zero matches, with a
    /// POSITIVELY-READ nonzero nameId, no longer concedes Enemy outright: LW-56's canonical-
    /// signature rescue (<see cref="RescueCanonical"/>) gets one strict shot at a positive
    /// identification first (fault 2: the new-game Orbonne opener's scripted player units carry a
    /// canonical nameId that matches no roster row).</summary>
    private RosterBridge Bridge(long entry, ushort nameId, out long rosterBase)
    {
        rosterBase = 0;
        if (nameId == 0) return RosterBridge.Unknown;   // the 0==0 trap: capture failure, not "confident enemy"
        int lvl = _mem.U8(entry + Offsets.ALevel);
        int br = _mem.U8(entry + Offsets.ABrave);
        int fa = _mem.U8(entry + Offsets.AFaith);
        int matches = 0;
        for (int s = 0; s < Offsets.RosterSlots; s++)
        {
            long b = Offsets.RosterBase + (long)s * Offsets.RosterStride;
            int rlvl = _mem.U8(b + Offsets.RLevel);
            if (rlvl < 1 || rlvl > 99) continue;                       // unoccupied slot
            if (_mem.U16(b + Offsets.RNameId) != nameId) continue;
            if (!Band.LevelMatchesRoster(rlvl, lvl)) continue;
            if (_mem.U8(b + Offsets.RBrave) != br || _mem.U8(b + Offsets.RFaith) != fa) continue;
            matches++;
            rosterBase = b;
        }
        if (matches == 1) return RosterBridge.Player;
        if (matches > 1) return RosterBridge.Unknown;
        return RescueCanonical(entry, nameId, lvl, br, fa, out rosterBase);
    }

    /// <summary>LW-56 fault 2 (round 2, D10/A1): called only when <see cref="Bridge"/>'s own
    /// nameId+stat loop found ZERO matches. Doctrine unchanged from round 1: a rescue turns a
    /// zero-evidence Enemy into a positively-identified Player, never touches a nameId-matched
    /// verdict (Bridge returns before this runs whenever matches != 0), and refuses on ANY doubt:
    /// the verdict on refusal is always Enemy, never Unknown (Unknown would fall through to the
    /// TQ-fingerprint body, a live behavior change this rescue must not cause).
    ///
    /// The ladder:
    ///   0. CANONICAL SIGNATURE, the scripted-opener tell: the frame nameId already handed to
    ///      <see cref="Bridge"/> equals this SAME entry's own job byte (<see cref="Puppeteer.JobOff"/>,
    ///      read UNGUARDED, mirroring this class's own ALevel/ABrave/AFaith reads). A strong
    ///      STATISTICAL filter only (a re-rolled generic nameId lands on its own job byte about
    ///      1 in 256); the rest of the ladder is what makes the individual keys structural.
    ///      Fails: <see cref="RescueOutcome.NotCanonical"/>.
    ///   1. ORACLE EXCLUSION (A1): if an enemy-identity predicate was wired (<see cref="_isOracleEnemy"/>,
    ///      null = off) and it claims this entry's (level,brave,faith,maxHp) tuple was captured
    ///      enemy-side at battle start, refuse before either key runs:
    ///      <see cref="RescueOutcome.OracleEnemy"/>. Closes the canonical-ENEMY vector for BOTH
    ///      keys below (a boss whose nameId happens to equal its own job byte). Does NOT close
    ///      the guest vector: guests are absent from the oracle by construction, so a guest's
    ///      weapon-key rescue is untouched by this clause.
    ///   2. FINGERPRINT KEY (unchanged): exactly one OCCUPIED roster row (RLevel 1..99) whose
    ///      level satisfies <see cref="Band.LevelMatchesRoster"/> and whose brave/faith equal the
    ///      entry's OWN (<paramref name="br"/>, <paramref name="fa"/>) exactly. nameId plays no
    ///      part in this compare, which is the whole point of a rescue.
    ///        - Exactly 1 with <see cref="WeaponsAgree"/> -> <see cref="RescueOutcome.Unique"/>.
    ///        - Exactly 1 WITHOUT weapon agreement -> <see cref="RescueOutcome.WpnMismatch"/>,
    ///          TERMINAL (conflicting evidence; never falls through to the weapon key).
    ///        - More than 1 -> <see cref="RescueOutcome.Ambiguous"/>, TERMINAL this round.
    ///        - Zero -> falls through to 3 (the live opener geometry: the scripted trio's brave/
    ///          faith never equal the roster's pre-battle stats, so this key always reads zero
    ///          for them).
    ///   3. WEAPON KEY (round 2, new): let bw be the same band-weapon read the fingerprint key's
    ///      agreement clause uses. bw unarmed/sentinel -> <see cref="RescueOutcome.NoMatch"/> (an
    ///      unarmed scripted unit is unrescuable; honest). Else count the OCCUPIED rows (drift
    ///      window per <see cref="Band.LevelMatchesRoster"/>) whose sentinel-filtered hand set
    ///      contains bw: exactly 1 -> <see cref="RescueOutcome.WeaponUnique"/> (a matched row
    ///      credits its FULL hand set, not just the hand that matched: a dual-wield row credits
    ///      both blades, same as every other resolve path in this codebase); more than 1 ->
    ///      <see cref="RescueOutcome.WeaponAmbiguous"/> (stale rosters can hold duplicate gear;
    ///      refuse); zero -> <see cref="RescueOutcome.NoMatch"/>.
    ///
    /// RESIDUAL RISKS (accepted, tape-visible via the rescue field): a canonical ENEMY whose band
    /// weapon uniquely matches an occupied row's hands within drift would mis-bridge if the oracle
    /// exclusion clause is off or missed it; a guest whose weapon id uniquely matches a roster row
    /// credits that weapon id for the guest's kills (the oracle clause does NOT close this vector,
    /// guests are absent from the oracle by construction). Both are narrow under the existing
    /// shields (canonical gate, drift, hand-set uniqueness) and always named on the tape.
    /// </summary>
    private RosterBridge RescueCanonical(long entry, ushort nameId, int lvl, int br, int fa, out long rosterBase)
    {
        rosterBase = 0;
        if (nameId != _mem.U8(entry + Puppeteer.JobOff))
        {
            CurrentRescue = RescueOutcome.NotCanonical;
            return RosterBridge.Enemy;
        }

        if (_isOracleEnemy != null)
        {
            int maxHp = _mem.U16(entry + Offsets.AMaxHp);
            if (_isOracleEnemy((lvl, br, fa, maxHp)))
            {
                CurrentRescue = RescueOutcome.OracleEnemy;
                return RosterBridge.Enemy;
            }
        }

        int bandWeapon = _mem.U16(entry + Offsets.AWeapon);   // shared by both keys below

        int fpMatches = 0;
        long fpBase = 0;
        for (int s = 0; s < Offsets.RosterSlots; s++)
        {
            long b = Offsets.RosterBase + (long)s * Offsets.RosterStride;
            int rlvl = _mem.U8(b + Offsets.RLevel);
            if (rlvl < 1 || rlvl > 99) continue;                       // unoccupied slot
            if (!Band.LevelMatchesRoster(rlvl, lvl)) continue;
            if (_mem.U8(b + Offsets.RBrave) != br || _mem.U8(b + Offsets.RFaith) != fa) continue;
            fpMatches++;
            fpBase = b;
        }

        if (fpMatches > 1) { CurrentRescue = RescueOutcome.Ambiguous; return RosterBridge.Enemy; }
        if (fpMatches == 1)
        {
            if (!WeaponsAgree(bandWeapon, fpBase)) { CurrentRescue = RescueOutcome.WpnMismatch; return RosterBridge.Enemy; }
            CurrentRescue = RescueOutcome.Unique;
            rosterBase = fpBase;
            return RosterBridge.Player;
        }

        // fpMatches == 0: the weapon key (D10, round 2).
        return RescueWeaponKey(bandWeapon, lvl, out rosterBase);
    }

    /// <summary>True when neither hand-side sentinel (0x00FF/0xFFFF) applies to <paramref name="id"/>.</summary>
    private static bool Held(int id) => id != 0x00FF && id != 0xFFFF;

    /// <summary>True when the band-weapon read is one of the unarmed sentinels (0, 0xFF, 0xFFFF;
    /// the LW-55 CursorGate precedent).</summary>
    private static bool IsUnarmedWeapon(int bandWeapon) => bandWeapon == 0 || bandWeapon == 0xFF || bandWeapon == 0xFFFF;

    /// <summary>True when the sentinel-filtered hand set {rh, lh, oh} contains <paramref name="weapon"/>.</summary>
    private static bool HandsHold(int rh, int lh, int oh, int weapon) =>
        (Held(rh) && rh == weapon) || (Held(lh) && lh == weapon) || (Held(oh) && oh == weapon);

    /// <summary>Clause (c) of the fingerprint key: the matched row's sentinel-filtered hand set
    /// (RRHand/RLHand/ROffHand) must CONTAIN <paramref name="bandWeapon"/>. If that weapon reads
    /// as a sentinel, agreement instead requires the hand set to be EMPTY (both sides unarmed).</summary>
    private bool WeaponsAgree(int bandWeapon, long rosterBase)
    {
        int rh = _mem.U16(rosterBase + Offsets.RRHand);
        int lh = _mem.U16(rosterBase + Offsets.RLHand);
        int oh = _mem.U16(rosterBase + Offsets.ROffHand);

        if (IsUnarmedWeapon(bandWeapon)) return !Held(rh) && !Held(lh) && !Held(oh);
        return HandsHold(rh, lh, oh, bandWeapon);
    }

    /// <summary>Step 3 of <see cref="RescueCanonical"/> (D10, round 2): reached only when the
    /// fingerprint key found zero matches. Scans the occupied roster rows for a unique
    /// drift-window match on the band weapon alone (brave/faith play no part; the fingerprint key
    /// already ruled them out for every row).</summary>
    private RosterBridge RescueWeaponKey(int bandWeapon, int lvl, out long rosterBase)
    {
        rosterBase = 0;
        if (IsUnarmedWeapon(bandWeapon)) { CurrentRescue = RescueOutcome.NoMatch; return RosterBridge.Enemy; }

        int wpnMatches = 0;
        long wpnBase = 0;
        for (int s = 0; s < Offsets.RosterSlots; s++)
        {
            long b = Offsets.RosterBase + (long)s * Offsets.RosterStride;
            int rlvl = _mem.U8(b + Offsets.RLevel);
            if (rlvl < 1 || rlvl > 99) continue;                       // unoccupied slot
            if (!Band.LevelMatchesRoster(rlvl, lvl)) continue;
            int rh = _mem.U16(b + Offsets.RRHand);
            int lh = _mem.U16(b + Offsets.RLHand);
            int oh = _mem.U16(b + Offsets.ROffHand);
            if (!HandsHold(rh, lh, oh, bandWeapon)) continue;
            wpnMatches++;
            wpnBase = b;
        }

        if (wpnMatches == 1) { CurrentRescue = RescueOutcome.WeaponUnique; rosterBase = wpnBase; return RosterBridge.Player; }
        if (wpnMatches > 1) { CurrentRescue = RescueOutcome.WeaponAmbiguous; return RosterBridge.Enemy; }
        CurrentRescue = RescueOutcome.NoMatch;
        return RosterBridge.Enemy;
    }
}
