namespace LivingWeapon;

/// <summary>
/// ActorRegister's roster-identity bridge, split out at the 200-line seam. Matches the engine's
/// actor-pointer entry (nameId, level, brave, faith) against the 20 roster slots and classifies it
/// Player, Enemy or Unknown. The enum and class doc stay in ActorRegister.cs.
/// </summary>
internal sealed partial class ActorRegister
{
    /// <summary>A slot matches when it is occupied (RLevel 1..99), its RNameId equals
    /// <paramref name="nameId"/>, and its (level,brave,faith) matches the entry's band bytes
    /// (guards a possible enemy/player nameId overlap, K2 of the kill-attribution plan). One match is
    /// Player. Several matches, or a nameId of 0 (a capture failure, the 0==0 trap), is Unknown so
    /// callers fall through to the TQ-fingerprint body. Zero matches with a nonzero nameId gets one
    /// <see cref="RescueCanonical"/> attempt (LW-56 fault 2) before conceding Enemy, because the
    /// new-game Orbonne opener's scripted player units carry a canonical nameId matching no roster row.</summary>
    private RosterBridge Bridge(long entry, ushort nameId, out long rosterBase)
    {
        rosterBase = 0;
        if (nameId == 0) return RosterBridge.Unknown;   // 0==0 trap, a capture failure
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

    /// <summary>LW-56 fault 2 (D10, A1). Called only when <see cref="Bridge"/> found zero nameId+stat
    /// matches. Turns a zero-evidence Enemy into a positively identified Player and refuses on any
    /// doubt. Refusal returns Enemy, not Unknown, because Unknown would fall through to the
    /// TQ-fingerprint body and change live behavior.
    ///
    /// Steps
    ///   0. Canonical signature (scripted-opener tell). The nameId equals this entry's own job byte
    ///      (<see cref="Offsets.AJob"/>, read unguarded like ALevel/ABrave/AFaith). Only a statistical
    ///      filter, since a re-rolled generic nameId hits its job byte about 1 in 256.
    ///   1. Oracle exclusion (A1). If <see cref="_isOracleEnemy"/> is wired (null = off) and claims
    ///      the (level,brave,faith,maxHp) tuple was captured enemy-side at battle start, refuse.
    ///      Covers a canonical enemy such as a boss, but not guests, which are absent from the oracle.
    ///   2. Fingerprint key. Exactly one occupied roster row matching level (<see cref="Band.LevelMatchesRoster"/>),
    ///      <paramref name="br"/> and <paramref name="fa"/>, ignoring nameId. One row with <see cref="WeaponsAgree"/>
    ///      is Unique. One row without agreement is WpnMismatch and more than one is Ambiguous, both terminal.
    ///      Zero falls through, which is the opener case since its scripted trio never matches the roster's brave/faith.
    ///   3. Weapon key (round 2). Count occupied rows (drift window) whose sentinel-filtered hand set
    ///      holds the band weapon. Unarmed or zero is NoMatch, one is WeaponUnique and credits the row's
    ///      full hand set (dual-wield credits both blades), more than one is WeaponAmbiguous.
    ///
    /// Accepted residual risks, visible on the tape via the rescue field. A canonical enemy or a guest
    /// whose weapon id uniquely matches a roster row can mis-bridge, and the oracle cannot close the
    /// guest case. Both are narrow given the canonical gate, drift and hand-set uniqueness.
    /// </summary>
    private RosterBridge RescueCanonical(long entry, ushort nameId, int lvl, int br, int fa, out long rosterBase)
    {
        rosterBase = 0;
        if (nameId != _mem.U8(entry + Offsets.AJob))
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

    /// <summary>True when the band-weapon read is an unarmed sentinel (0, 0xFF, 0xFFFF; see the LW-55
    /// CursorGate).</summary>
    private static bool IsUnarmedWeapon(int bandWeapon) => bandWeapon == 0 || bandWeapon == 0xFF || bandWeapon == 0xFFFF;

    /// <summary>True when the sentinel-filtered hand set {rh, lh, oh} contains <paramref name="weapon"/>.</summary>
    private static bool HandsHold(int rh, int lh, int oh, int weapon) =>
        (Held(rh) && rh == weapon) || (Held(lh) && lh == weapon) || (Held(oh) && oh == weapon);

    /// <summary>Clause (c) of the fingerprint key. The matched row's sentinel-filtered hand set
    /// (RRHand/RLHand/ROffHand) must contain <paramref name="bandWeapon"/>, or be empty when the
    /// band weapon is a sentinel.</summary>
    private bool WeaponsAgree(int bandWeapon, long rosterBase)
    {
        int rh = _mem.U16(rosterBase + Offsets.RRHand);
        int lh = _mem.U16(rosterBase + Offsets.RLHand);
        int oh = _mem.U16(rosterBase + Offsets.ROffHand);

        if (IsUnarmedWeapon(bandWeapon)) return !Held(rh) && !Held(lh) && !Held(oh);
        return HandsHold(rh, lh, oh, bandWeapon);
    }

    /// <summary>Step 3 of <see cref="RescueCanonical"/> (D10, round 2), reached when the fingerprint
    /// key found zero matches. Looks for a unique drift-window match on the band weapon alone.</summary>
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
