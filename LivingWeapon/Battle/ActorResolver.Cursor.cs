namespace LivingWeapon;

/// <summary>Names the stage that refused a <see cref="ActorResolver.TryResolveCursorPlayer"/> call.
/// The resolve-miss tap in AttackCard.Resolve.cs logs it. Listed in stage order.</summary>
internal enum CursorMiss
{
    /// <summary>The resolve succeeded.</summary>
    None,

    /// <summary><see cref="Band.FlagOwner"/> refused. No band entry has ATurnFlag==1 at a real position,
    /// or several disagreeing ones do. FlagOwner's bool cannot tell the two apart, so they share a stage.
    /// Routine between turns.</summary>
    NoOwner,

    /// <summary>The flag owner's frame nameId (<see cref="Offsets.ANameId"/>) reads 0, so its identity was
    /// not captured yet. Fails closed.</summary>
    NameIdZero,

    /// <summary>The flag owner's (nameId,level,brave,faith) bridge matched zero, or more than one,
    /// <summary>The (nameId,level,brave,faith) bridge matched zero or several roster rows. Routine on every
    /// enemy turn; see the accepted residual in ActorResolver.Flags.cs.</summary>
    BridgeFail,
}

/// <summary>
/// The pre-action cursor resolve for AttackCard's dossier. It reads the PSX turn-flags
/// owner (<see cref="Band.FlagOwner"/>) instead of the condensed turn-queue struct
/// (<see cref="Offsets.TurnQueue"/>) used until 2026-07-14, which follows the cursor rather than the turn and
/// blanked the Attack row to vanilla when a T-status detour moved it. Evidence. The 2026-07-21 hover-follower
/// row in docs/LIVE_LEDGER.md (tools/probes/cursor_resolve_probe.py). Answers whose turn is open now, unlike
/// <see cref="ActorResolver.TryResolveActingPlayer"/>, which answers who committed an action. Same shape as
/// <see cref="ActorResolver.TryResolveFlagOwner"/> (ActorResolver.Flags.cs) but reports the refusing stage.
/// The roster bridge filters out enemies and unbridged guests (<see cref="CursorMiss.BridgeFail"/>); AI-controlled
/// roster units (Charmed/Confused/Berserk, the LWDEV Body Double clone) get their own dossier.
/// Raw facts only. Roster hand, band weapon (<see cref="Offsets.AWeapon"/>), turn flag and band slot.
/// The flag is re-read because it can fall between the FlagOwner walk and this read; <see cref="CursorGate.Decide"/>
/// gate B catches that and judges all the facts. No register fallback since 2026-07-06.
/// </summary>
internal sealed partial class ActorResolver
{
    /// <summary>True when <see cref="Band.FlagOwner"/> names one real-position entry with its turn flag set,
    /// its frame nameId is nonzero, and the nameId bridges to exactly one roster slot on
    /// (level,brave,faith). Otherwise false with <paramref name="miss"/> naming the refusing stage, and the
    /// caller composes vanilla.</summary>
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

        int rosterHand = RawMainHand(matchedRosterBase);
        int bandWeapon = _mem.U16(entry + Offsets.AWeapon);
        byte turnFlag = _mem.U8(entry + Offsets.ATurnFlag);
        answer = new CursorAnswer(matchedRosterBase, rosterHand, bandWeapon, turnFlag, bandSlot);
        return true;
    }

    /// <summary>ActorRegister.Bridge's pattern (nameId narrows, level+brave+faith confirms) applied to a band
    /// entry. True only for exactly one agreeing roster slot. Shared with <see cref="ActorResolver.TryResolveFlagOwner"/>
    /// and <see cref="ActorResolver.TryResolveFlagKiller"/> (ActorResolver.Flags.cs).</summary>
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
