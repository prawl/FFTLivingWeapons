namespace LivingWeapon;

/// <summary>
/// ActorRegister's roster-identity bridge, split out at the 200-line seam. Matches the engine's
/// actor-pointer entry (nameId, level, brave, faith) against the 20 roster slots and classifies it
/// Player, Enemy or Unknown. The enum and class doc stay in ActorRegister.cs.
/// </summary>
internal sealed partial class ActorRegister
{
    /// <summary>One occupied slot matching nameId and (level,brave,faith) is Player. Several, or a
    /// nameId of 0 (a capture failure, the 0==0 trap), is Unknown so callers fall through to the
    /// TQ-fingerprint body. Zero gets one <see cref="RescueCanonical"/> attempt before Enemy,
    /// because the Orbonne opener's scripted player units carry a nameId matching no roster row.</summary>
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

    /// <summary>Runs only when <see cref="Bridge"/> found no nameId+stat match. It can turn that
    /// zero-evidence Enemy into a Player, and refuses on any doubt. A refusal returns Enemy, not
    /// Unknown, so nothing falls through to the TQ-fingerprint body. The gate is the opener's tell,
    /// a nameId equal to the entry's own job byte, which a re-rolled generic hits about 1 in 256.
    /// Then the oracle may refuse a tuple captured enemy-side at battle start (guests are not in
    /// it). Then the fingerprint key, then the weapon key, each needing exactly one roster row.
    /// Known residual, a canonical enemy or guest whose weapon uniquely matches a roster row
    /// can mis-bridge; the rescue field on the tape shows it.</summary>
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

        // fpMatches == 0. The weapon key (D10, round 2).
        return RescueWeaponKey(bandWeapon, lvl, out rosterBase);
    }

    /// <summary>True when neither hand-side sentinel (0x00FF/0xFFFF) applies to <paramref name="id"/>.</summary>
    private static bool Held(int id) => id != 0x00FF && id != 0xFFFF;

    /// <summary>True when the band-weapon read is an unarmed sentinel (0, 0xFF, 0xFFFF; see
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
