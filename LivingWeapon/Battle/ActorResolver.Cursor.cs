namespace LivingWeapon;

/// <summary>Names the stage that refused a <see cref="ActorResolver.TryResolveCursorPlayer"/> call.
/// The resolve-miss tap in AttackCard.Resolve.cs logs it. Listed in stage order.</summary>
internal enum CursorMiss
{
    /// <summary>The resolve succeeded.</summary>
    None,

    /// <summary>Nobody's turn is open yet, which is routine between turns. <see cref="Band.FlagOwner"/> refused, as no band entry has
    /// ATurnFlag==1 at a real position or several disagreeing ones do, and its bool cannot tell those apart.</summary>
    NoOwner,

    /// <summary>The flag owner's frame nameId (<see cref="Offsets.ANameId"/>) reads 0, so its identity was
    /// not captured yet. Fails closed.</summary>
    NameIdZero,

    /// <summary>The turn owner could not be matched to exactly one party member, routine on every enemy turn. The bridge
    /// is (nameId,level,brave,faith) against roster rows; see the accepted residual in ActorResolver.Flags.cs.</summary>
    BridgeFail,
}

/// <summary>
/// Works out which party member's turn is open and what they hold, so the Attack card shows that unit's weapon and nobody else's.
/// It reads the PSX turn-flags owner (<see cref="Band.FlagOwner"/>), not the condensed turn-queue struct
/// (<see cref="Offsets.TurnQueue"/>), because that struct follows the cursor rather than the turn and blanked the
/// Attack row to vanilla when a T-status detour moved it [ledger attack-card-reverts-hover-follower]
/// (tools/probes/cursor_resolve_probe.py). There is no register fallback. Answers whose turn is open now, unlike
/// <see cref="ActorResolver.TryResolveActingPlayer"/>, which answers who committed an action. Same shape as
/// <see cref="ActorResolver.TryResolveFlagOwner"/> (ActorResolver.Flags.cs) but reports the refusing stage.
/// The roster bridge filters out enemies and unbridged guests (<see cref="CursorMiss.BridgeFail"/>); AI-controlled
/// roster units (Charmed/Confused/Berserk, the LWDEV Body Double clone) get their own dossier. Raw facts only.
/// Roster hand, band weapon (<see cref="Offsets.AWeapon"/>), turn flag and band slot. The flag is re-read because it can
/// fall between the FlagOwner walk and this read; <see cref="CursorGate.Decide"/> gate B catches that and judges all the facts.
/// </summary>
internal sealed partial class ActorResolver
{
    /// <summary>Finds whose turn is open, or says why not through <paramref name="miss"/> so the caller composes vanilla. True when
    /// FlagOwner names one flagged entry with a nonzero nameId that bridges to exactly one roster slot on (level,brave,faith).</summary>
    public bool TryResolveCursorPlayer(out CursorAnswer answer, out CursorMiss miss)
    {
        answer = default;
        miss = CursorMiss.None;

        if (!Band.FlagOwner(_mem, out long entry, out int bandSlot))
        {
            miss = CursorMiss.NoOwner;
            return false;
        }

        ushort frameNameId = _mem.U16(entry + Offsets.ANameId);
        if (frameNameId == 0)
        {
            miss = CursorMiss.NameIdZero;
            return false;
        }

        byte br = _mem.U8(entry + Offsets.ABrave);
        byte fa = _mem.U8(entry + Offsets.AFaith);
        byte lvl = _mem.U8(entry + Offsets.ALevel);
        if (!TryBridgeCursorToRoster(frameNameId, lvl, br, fa, out long matchedRosterBase))
        {
            miss = CursorMiss.BridgeFail;
            return false;
        }

        var (rosterHand, fromOffHand) = CursorGate.AttackHand(RawMainHand(matchedRosterBase),
            _mem.U16(matchedRosterBase + Offsets.ROffHand));
        int bandWeapon = _mem.U16(entry + Offsets.AWeapon);
        byte turnFlag = _mem.U8(entry + Offsets.ATurnFlag);
        answer = new CursorAnswer(matchedRosterBase, rosterHand, bandWeapon, turnFlag, bandSlot, fromOffHand);
        return true;
    }

    /// <summary>Matches a battle unit to its party roster row. nameId narrows, level+brave+faith confirms, and only exactly one row counts.
    /// Same pattern as ActorRegister.Bridge, shared with <see cref="ActorResolver.TryResolveFlagOwner"/> and <see cref="ActorResolver.TryResolveFlagKiller"/>.</summary>
    private bool TryBridgeCursorToRoster(ushort frameNameId, int level, int br, int fa, out long rosterBase)
    {
        rosterBase = 0;
        int matches = 0;
        for (int s = 0; s < Offsets.RosterSlots; s++)
        {
            long b = Offsets.RosterBase + (long)s * Offsets.RosterStride;
            int rlvl = _mem.U8(b + Offsets.RLevel);
            if (rlvl < 1 || rlvl > 99) continue;
            if (_mem.U16(b + Offsets.RNameId) != frameNameId) continue;
            if (!Band.LevelMatchesRoster(rlvl, level)) continue;
            if (_mem.U8(b + Offsets.RBrave) != br || _mem.U8(b + Offsets.RFaith) != fa) continue;
            matches++;
            rosterBase = b;
        }
        return matches == 1;
    }
}
