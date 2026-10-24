namespace LivingWeapon;

/// <summary>
/// ActorRegister's roster-identity bridge, split out at the 200-line seam. Matches the engine's
/// actor-pointer entry (nameId, level, brave, faith) against the 20 roster slots and classifies it
/// Player, Enemy or Unknown. The enum and class doc stay in ActorRegister.cs.
/// </summary>
internal sealed partial class ActorRegister
{
    /// <summary>Decides whether a unit on the battlefield is one of your party, by matching it to the roster.
    /// One match is Player; several, or nameId 0 (the 0==0 trap), is Unknown; none gets one <see cref="RescueCanonical"/> try.</summary>
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

    /// <summary>Last chance to recognize a scripted guest as one of your party, otherwise it counts as an enemy.
    /// Runs only when <see cref="Bridge"/> found no nameId and stat match, and any doubt means Enemy.</summary>
    private RosterBridge RescueCanonical(long entry, ushort nameId, int lvl, int br, int fa, out long rosterBase)
    {
        // The Orbonne opener's scripted party units carry a nameId matching no roster row, so only a unit
        // whose nameId equals its own job byte is tried (a re-rolled generic hits that about 1 in 256).
        // A refusal returns Enemy, not Unknown, so nothing falls through to the TQ-fingerprint body.
        // Order is enemy bit, oracle veto, fingerprint key, weapon key, each needing exactly one roster row.
        // Known residual, a guest whose weapon uniquely matches a roster row can mis-bridge; the rescue
        // field on the tape shows it.
        rosterBase = 0;
        if (nameId != _mem.U8(entry + Offsets.AJob))
        {
            CurrentRescue = RescueOutcome.NotCanonical;
            return RosterBridge.Enemy;
        }

        // An enemy is never one of your party, however well its stats or weapon line up with a roster row.
        if ((_mem.U8(entry + Offsets.AFriendFoe) & Offsets.AFriendFoeEnemyBit) != 0)
        {
            CurrentRescue = RescueOutcome.EnemySide;
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

        // fpMatches == 0. The weapon key.
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

    /// <summary>Checks the unit's weapon against what the matched party member really holds.
    /// The row's hands (RRHand/RLHand/ROffHand) must contain <paramref name="bandWeapon"/>, or be empty for a sentinel.</summary>
    private bool WeaponsAgree(int bandWeapon, long rosterBase)
    {
        int rh = _mem.U16(rosterBase + Offsets.RRHand);
        int lh = _mem.U16(rosterBase + Offsets.RLHand);
        int oh = _mem.U16(rosterBase + Offsets.ROffHand);

        if (IsUnarmedWeapon(bandWeapon)) return !Held(rh) && !Held(lh) && !Held(oh);
        return HandsHold(rh, lh, oh, bandWeapon);
    }

    /// <summary>Last try at naming a scripted party unit, by the weapon in its hand. Reached when no roster row matched its stats.
    /// Needs exactly one row holding the weapon (counted before the level filter) that also passes the level drift window.</summary>
    private RosterBridge RescueWeaponKey(int bandWeapon, int lvl, out long rosterBase)
    {
        rosterBase = 0;
        if (IsUnarmedWeapon(bandWeapon)) { CurrentRescue = RescueOutcome.NoMatch; return RosterBridge.Enemy; }

        int holders = 0;
        int wpnMatches = 0;
        long wpnBase = 0;
        for (int s = 0; s < Offsets.RosterSlots; s++)
        {
            long b = Offsets.RosterBase + (long)s * Offsets.RosterStride;
            int rlvl = _mem.U8(b + Offsets.RLevel);
            if (rlvl < 1 || rlvl > 99) continue;                       // unoccupied slot
            int rh = _mem.U16(b + Offsets.RRHand);
            int lh = _mem.U16(b + Offsets.RLHand);
            int oh = _mem.U16(b + Offsets.ROffHand);
            if (!HandsHold(rh, lh, oh, bandWeapon)) continue;
            holders++;
            if (!Band.LevelMatchesRoster(rlvl, lvl)) continue;
            wpnMatches++;
            wpnBase = b;
        }

        if (holders > 1) { CurrentRescue = RescueOutcome.WeaponAmbiguous; return RosterBridge.Enemy; }
        if (wpnMatches == 1) { CurrentRescue = RescueOutcome.WeaponUnique; rosterBase = wpnBase; return RosterBridge.Player; }
        CurrentRescue = RescueOutcome.NoMatch;
        return RosterBridge.Enemy;
    }
}
