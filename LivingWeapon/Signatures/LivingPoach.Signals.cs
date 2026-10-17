namespace LivingWeapon;

/// <summary>
/// LW-167 Living Poach killer-signal reads: two stateless per-weapon reads off the deployed
/// main-hand wielder (Wielder.ResolveDeployedMainHand). Split from LivingPoach.cs, which holds the
/// stateful executor; the executor receives these as injected delegates.
/// Both inherit the resolve lane's limit. A credit naming an off-hand copy resolves to nobody, or
/// to a different unit main-handing the same id, so the miss direction is an under-poach.
/// </summary>
internal sealed partial class LivingPoach
{
    /// <summary>Reads the Poach support bit (id 215, combat +0x98 byte 2, mask 0x40) off the
    /// weapon's deployed main-hand wielder. Zero or ambiguous wielders return false so a poach
    /// never fires on a guess.</summary>
    internal static bool ReadKillerHasPoach(IGameMemory mem, int weaponId)
    {
        long entry = Wielder.ResolveDeployedMainHand(mem, weaponId, out _);
        if (entry == 0) return false;
        if (!Signatures.SupportBit(Tuning.PoachSupportAbilityId, out int off, out byte mask)) return false;
        long addr = entry + Offsets.ASupport + off;
        if (!mem.Readable(addr, 1)) return false;
        return (mem.U8(addr) & mask) != 0;
    }

    /// <summary>LW-167 stage 4 basic-Attack discriminator (LIVE_LEDGER "The basic-Attack
    /// discriminator (LW-167 stage 4)", 2026-08-12, probe tools/probes/arec_watch.py). Reads the
    /// killer's action record (Offsets.AArec) and is true only when kind (+0xA) is
    /// <see cref="Offsets.ArecKindPerforming"/> and abil (+0x2) is
    /// <see cref="Tuning.BasicAttackAbilityId"/> (0). Every failure returns false and logs why, so a
    /// miss can only under-poach, never double-poach with vanilla's Poach support.</summary>
    internal static bool ReadWasBasicAttack(IGameMemory mem, int weaponId)
    {
        long entry = Wielder.ResolveDeployedMainHand(mem, weaponId, out _);
        if (entry == 0)
        {
            ModLogger.Debug(LogVerb.Signature, $"Living Poach's basic-Attack discriminator could not resolve weapon {weaponId}'s killer (zero or ambiguous deployed wielders); treating the kill as not-an-Attack.");
            return false;
        }
        long recAddr = entry + Offsets.AArec;
        if (!mem.Readable(recAddr + Offsets.ArecKind, 1) || !mem.Readable(recAddr + Offsets.ArecAbil, 2))
        {
            ModLogger.Debug(LogVerb.Signature, $"Living Poach's basic-Attack discriminator could not read weapon {weaponId}'s killer's action record; treating the kill as not-an-Attack.");
            return false;
        }
        byte kind = mem.U8(recAddr + Offsets.ArecKind);
        ushort abil = mem.U16(recAddr + Offsets.ArecAbil);
        if (kind != Offsets.ArecKindPerforming || abil != Tuning.BasicAttackAbilityId)
        {
            ModLogger.Debug(LogVerb.Signature, $"Living Poach's basic-Attack discriminator read a non-Attack action record for weapon {weaponId}'s killer (kind={kind}, abil={abil}); the kill will not poach.");
            return false;
        }
        return true;
    }
}
