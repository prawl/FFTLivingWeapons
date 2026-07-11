namespace LivingWeapon;

/// <summary>Which resolve path answered the caller's request THIS call: the turn-queue fallback
/// body (no register, no flags), the engine actor-pointer register (the pre-existing register-
/// first preamble), or the per-unit turn-flags lane (LW-63, this file). Replaces the old bare
/// <c>LastResolveViaRegister</c> bool so the flags source is distinguishable from the register
/// source at KillTracker's fallback-attribution counters (KillTracker.cs) -- a flags-sourced
/// latch is pointer-quality evidence, not a fallback resolve, exactly like a register-sourced one.</summary>
internal enum ResolveSource { TqFallback, Register, Flags }

/// <summary>
/// LW-63 (docs/TODO.md): the per-unit PSX turn-flags resolve lane. Every credit source
/// (the live latch, the death-edge culprit stamp, the delayed-culprit arm) ultimately trusted
/// the engine actor pointer (<see cref="ActorRegister"/>), which PARKS on struck victims and
/// mirror-seat units -- live-reproduced twice 2026-07-10 (Ramza killed with the Chaos Blade id
/// 37 while Wilham's Warbrand id 67 claimed it). This lane keys the resolve on
/// <see cref="Offsets.ATurnFlag"/> instead (band +0x19C, PROVEN LIVE 2026-07-09): it names the
/// acting unit STRUCTURALLY, not by where the pointer happens to be parked at read time.
///
/// THE LATCH KEY (t==1 only, <see cref="TryResolveFlagOwner"/>): the same exactly-one-owner
/// exclusivity test the shipped <see cref="CursorGate.Decide"/> already relies on (its
/// turnFlag exactly-1 refusal). Tape provenance: across 58 flags records over 4 real tapes,
/// never two t=1 units in one record (58/58), and exactly one in 56/57 (the sole exception is the
/// battle-opening edge, a real zero-t record -- <see cref="Band.FlagOwner"/> falls through on
/// it, never guesses). AMoved/AActed are NOT exclusive the same way: many units read a1
/// simultaneously between their own turns (tape-verified), so requiring a==1 here would blind
/// this lane on ordinary hover -- LANE ASYMMETRY, deliberate.
///
/// THE STAMP KEY (t==1 AND a==1, <see cref="TryResolveFlagKiller"/>): the death-edge culprit
/// stamp (KillerStamp.cs) needs a STRICTER key than the live latch. A freshly-opened next turn
/// reads t1 m0 a0 (m/a are engine-reset at turn open, PROVEN ledger row), so requiring a==1
/// structurally excludes the innocent next owner during the 3-tick period-close debounce tail:
/// if the true killer's a byte has not yet risen at the dead edge, this stamp key refuses and
/// KillerStamp's existing register-snapshot lane governs (today's behavior -- miss beats
/// mis-credit). This is literally the ticket's own Verify language ("reads that unit's slot
/// with a1 at the killing edge").
///
/// FALL-THROUGH DOCTRINE (D2): both keys answer ONLY when the t=1 (or t=1+a=1) entry bridges to
/// EXACTLY ONE roster row via the SAME nameId+stats bridge the cursor resolve already uses
/// (<see cref="TryBridgeCursorToRoster"/>, generalized here rather than duplicated -- LW-62
/// already flags roster-walk proliferation). Every other outcome (no candidate, an ambiguous
/// candidate, a nameId==0 capture failure, zero or multiple roster matches) falls straight
/// through to the caller's existing register/turn-queue chain, UNCHANGED: this preserves the
/// LW-56 canonical-signature rescue (which lives in the register lane) and today's enemy-turn
/// behavior byte-for-byte (ACCEPTED RESIDUAL: during an enemy's turn the t=1 owner bridges to
/// zero roster rows, the lane falls through, and a parked Player-bridged register can still
/// latch exactly as today -- protected downstream by the TqTeam death-edge bury and CreditGate).
///
/// PERIOD GATING (D3): both keys are gated on <see cref="FlagPathOpen"/> only (an acted-period
/// is open) -- NOT the corpse-anchor veto or register stability. Those gates cure the register's
/// own arrival-time staleness; the flags are instantaneous structural truth, not a snapshot that
/// can go stale the way a pointer arrival can. Outside any period the lane is inert by
/// construction, the same invariant every other resolve path in this class already honors.
/// </summary>
internal sealed partial class ActorResolver
{
    /// <summary>True once an acted-period is open. Mirrors <see cref="RegisterPathOpen"/>'s own
    /// period gate (D3) -- the flags lane needs no corpse-anchor veto or register-stability check
    /// of its own; see this file's class doc comment for the parity argument.</summary>
    private bool FlagPathOpen => _periodStartTick >= 0;

    /// <summary>THE LATCH KEY (D1: t==1 only). Resolves <see cref="Band.FlagOwner"/>'s winning
    /// entry to exactly one roster row. False (rosterBase/entry left 0) on ANY refusal --
    /// no t=1 candidate, an ambiguous t=1 read, a nameId==0 capture failure, or a roster bridge
    /// that fails/is ambiguous -- so the caller's existing fall-through chain runs unchanged.</summary>
    internal bool TryResolveFlagOwner(out long rosterBase, out long entry)
    {
        rosterBase = 0;
        entry = 0;
        if (!Band.FlagOwner(_mem, out long e, out _)) return false;

        ushort nameId = _mem.U16(e + Offsets.ANameId);
        if (nameId == 0) return false;   // capture failure: fail closed, never a guess

        byte lvl = _mem.U8(e + Offsets.ALevel);
        byte br = _mem.U8(e + Offsets.ABrave);
        byte fa = _mem.U8(e + Offsets.AFaith);
        if (!TryBridgeCursorToRoster(nameId, lvl, br, fa, out long rb)) return false;

        rosterBase = rb;
        entry = e;
        return true;
    }

    /// <summary>THE STAMP KEY (D1/D4: t==1 AND a==1 on the same winning entry). Same resolve as
    /// <see cref="TryResolveFlagOwner"/>, plus <see cref="Offsets.AActed"/> must read exactly 1
    /// on the winning entry -- see this file's class doc comment for why that is the death-edge
    /// stamp's own ordering gate, needing no arrival-ordering check of its own. The outs beyond
    /// rosterBase/nameId (<paramref name="bandSlot"/>, <paramref name="moved"/>) are diagnostic
    /// only, carried through so the death-edge stamp can tape them onto its flight payload
    /// (review finding 5: the owner's live Verify -- "a1 at the killing edge" -- becomes
    /// satisfiable from the exit tape).</summary>
    internal bool TryResolveFlagKiller(out long rosterBase, out ushort nameId, out int bandSlot, out byte moved)
    {
        rosterBase = 0;
        nameId = 0;
        bandSlot = -1;
        moved = 0;
        if (!Band.FlagOwner(_mem, out long e, out int slot)) return false;
        if (_mem.U8(e + Offsets.AActed) != 1) return false;   // D4: the stamp lane's own ordering gate

        ushort nid = _mem.U16(e + Offsets.ANameId);
        if (nid == 0) return false;

        byte lvl = _mem.U8(e + Offsets.ALevel);
        byte br = _mem.U8(e + Offsets.ABrave);
        byte fa = _mem.U8(e + Offsets.AFaith);
        if (!TryBridgeCursorToRoster(nid, lvl, br, fa, out long rb)) return false;

        rosterBase = rb;
        nameId = nid;
        bandSlot = slot;
        moved = _mem.U8(e + Offsets.AMoved);
        return true;
    }
}
