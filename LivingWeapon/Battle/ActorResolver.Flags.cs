namespace LivingWeapon;

/// <summary>Which resolve path answered this call: the turn-queue fallback, the engine
/// actor-pointer register, or the per-unit turn-flags lane (LW-63, this file). A flags-sourced
/// latch counts as pointer-quality evidence in KillTracker's fallback counters, like a register one.</summary>
internal enum ResolveSource { TqFallback, Register, Flags }

/// <summary>
/// Resolves the acting unit from the per-unit PSX turn flags. The engine
/// actor pointer (<see cref="ActorRegister"/>) parks on struck victims and mirror-seat units, which
/// credited a Chaos Blade (id 37) kill to Wilham's Warbrand (id 67) on 2026-07-10. This lane keys on
/// <see cref="Offsets.ATurnFlag"/> (band +0x19C, found 2026-07-09) instead.
/// Latch key (<see cref="TryResolveFlagOwner"/>) is t==1 alone. Across 58 flags records on 4 real
/// tapes there was never a second t=1 unit, and exactly one owner in 56/57 (the exception is the
/// zero-t battle-opening edge, where <see cref="Band.FlagOwner"/> falls through). AMoved/AActed are
/// not exclusive, since many units read a1 between their own turns, so the latch does not require a==1.
/// Stamp key (<see cref="TryResolveFlagKiller"/>) is t==1 and a==1. A freshly opened turn reads
/// t1 m0 a0, so a==1 excludes the next owner during the 3-tick period-close debounce. When the
/// killer's a byte has not risen yet the key refuses and KillerStamp's register lane governs.
/// Both keys answer only when the entry bridges to exactly one roster row via
/// <see cref="TryBridgeCursorToRoster"/> (generalized, since LW-62 flags roster-walk proliferation).
/// Any other outcome falls through to the register/turn-queue chain, which keeps the LW-56 rescue.
/// Accepted residual (D2) is that on an enemy turn a parked Player-bridged register can still latch,
/// guarded downstream by the TqTeam death-edge bury and CreditGate. Gating (D3) is only
/// <see cref="FlagPathOpen"/>, because flags are instantaneous and cannot go stale like a pointer.
/// </summary>
internal sealed partial class ActorResolver
{
    /// <summary>True once an acted-period is open (D3), the same gate as <see cref="RegisterPathOpen"/>.</summary>
    private bool FlagPathOpen => _periodStartTick >= 0;

    /// <summary>Latch key (D1, t==1 only). Resolves <see cref="Band.FlagOwner"/>'s winning entry to
    /// one roster row. On any refusal it returns false with outs left 0, so the caller's fall-through
    /// chain runs. LW-87 sibling <see cref="ActorResolver.TryResolveCursorPlayer"/> repeats this shape
    /// because it needs the band slot and the <see cref="CursorMiss"/> stage; keep the two in step and
    /// keep <see cref="TryBridgeCursorToRoster"/> shared.</summary>
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

    /// <summary>Stamp key (D1/D4, t==1 and a==1 on the winning entry). Same resolve as
    /// <see cref="TryResolveFlagOwner"/> plus <see cref="Offsets.AActed"/> must read 1. The
    /// <paramref name="bandSlot"/> and <paramref name="moved"/> outs are diagnostic, carried onto the
    /// death-edge flight payload so the "a1 at the killing edge" check can be read from the tape.</summary>
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
