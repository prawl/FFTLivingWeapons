namespace LivingWeapon;

/// <summary>
/// LW-167 Living Poach -- the KILLER-SIGNAL reads: two stateless, static, per-weapon memory reads
/// that resolve "whoever is deployed as this weapon's main-hand wielder right now" via
/// Wielder.ResolveDeployedMainHand and read one live fact off that wielder's band entry. Split out
/// of LivingPoach.cs (the stateful per-corpse executor: dedupe latch, store write, toast) as a real
/// seam -- these two methods take no instance state, share only the killer-resolve lane, and are
/// consumed as constructor-injected delegates rather than called from RecordPoachDeed directly, so
/// they read as their own unit even though the executor is what wires them in.
///
/// SCOPE CAVEAT both reads inherit from the resolve lane: the lane finds the weapon's single
/// deployed MAIN-HAND wielder. A credit naming an OFF-hand copy of the weapon resolves either
/// nobody (fail closed) or, when a different deployed unit main-hands the same id, THAT unit --
/// whose signals are then read in the true killer's stead. Deep-tail conjunction (two copies of
/// one dormant weapon split across hands plus Poach on the resolved unit), inherited from the
/// attribution layer by design; the fail-closed default keeps every ordinary miss an under-poach.
/// </summary>
internal sealed partial class LivingPoach
{
    /// <summary>The killer's real Poach read: locate <paramref name="weaponId"/>'s single deployed
    /// main-hand wielder (Wielder.ResolveDeployedMainHand -- the same locate Choir/Kobu/Bulwark
    /// use) and read its live combat-struct support bitfield for the Poach bit (id 215, combat
    /// +0x98 byte 2 mask 0x40 via Signatures.SupportBit -- the band-relative address is
    /// Offsets.ASupport + that same (byteOffset, mask), the identity Choir already relies on).
    /// Locate unavailable (zero or ambiguous deployed wielders) -&gt; false, FAIL CLOSED: a poach
    /// must never fire on a guess about who's holding the weapon.</summary>
    internal static bool ReadKillerHasPoach(IGameMemory mem, int weaponId)
    {
        long entry = Wielder.ResolveDeployedMainHand(mem, weaponId, out _);
        if (entry == 0) return false;
        if (!Signatures.SupportBit(Tuning.PoachSupportAbilityId, out int off, out byte mask)) return false;
        long addr = entry + Offsets.ASupport + off;
        if (!mem.Readable(addr, 1)) return false;
        return (mem.U8(addr) & mask) != 0;
    }

    /// <summary>LW-167 stage 4 -- the real basic-Attack discriminator (LIVE_LEDGER's "The
    /// basic-Attack discriminator (LW-167 stage 4)" row, 2026-08-12, owner probe
    /// tools/probes/arec_watch.py). Resolves the KILLER via the SAME lane
    /// <see cref="ReadKillerHasPoach"/> uses (Wielder.ResolveDeployedMainHand -- <paramref
    /// name="weaponId"/>'s single deployed main-hand wielder), then reads that wielder's own
    /// per-unit action record (Offsets.AArec) at the credit moment: kind (+0xA) ==
    /// <see cref="Offsets.ArecKindPerforming"/> (this unit's own pending action, not a struck
    /// victim's stale stamp) AND abil (+0x2) == <see cref="Tuning.BasicAttackAbilityId"/> (0)
    /// together mean the pending action was the basic Attack command. Every failure path
    /// (unresolvable killer, an unreadable record, or a reading that names an ability instead)
    /// fails closed to false and logs the specific reason -- a false here can only ever
    /// under-poach, never double-poach alongside vanilla's own Poach support.</summary>
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
