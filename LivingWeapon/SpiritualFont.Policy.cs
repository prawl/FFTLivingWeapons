using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// The pure decisions behind Umbral Rod's "Spiritual Font" signature -- no memory access.
/// The stateful turn-edge watcher, position snapshots, and guarded writes live in SpiritualFont.cs.
/// </summary>
internal sealed partial class SpiritualFont
{
    /// <summary>Rising edge on the actor latch: weapon id 56 just appeared in the latched set
    /// (it was absent last tick). This is "the wielder is starting an action" -- the correct
    /// trigger after CT reads on player units proved dead live (both +0x25 and +0x09 returned
    /// values that never reached the >=90 threshold across full player turns in the watcher).
    /// </summary>
    public static bool IsLatchEdge(bool wasIn, bool isIn) => !wasIn && isIn;

    /// <summary>True when the signature is configured and the kill tier is earned.</summary>
    public static bool IsActive(WeaponSignature? sig, int tier)
    {
        if (sig is null || !sig.FontOnMove) return false;
        return tier >= sig.AtTier;
    }

    /// <summary>The font fires only when a SNAPSHOTTED position changed: the first turn edge of
    /// a battle (or after a re-equip) has no snapshot yet, so it baselines silently -- standing
    /// still earns nothing.</summary>
    public static bool ShouldFire(bool posKnown, int lastGx, int lastGy, int gx, int gy)
        => posKnown && (gx != lastGx || gy != lastGy);

    /// <summary>New MP after the gain: clamped at maxMp. UNLIKE the HP half, mp 0 still gains --
    /// an empty pool is not a corpse (HP 0 -&gt; positive is the engine's revival signal; MP has
    /// no such semantics). No-op on junk maxMp or a non-positive gain.</summary>
    public static int NewMp(int mp, int maxMp, int gain)
    {
        if (maxMp <= 0 || gain <= 0) return mp;
        int n = mp + gain;
        return n > maxMp ? maxMp : n;
    }

    /// <summary>The MP half runs only for a LIVING wielder on a layout-proven battle: a wielder
    /// who moved and then died before their turn edge (trap tile, counter-kill) gains NOTHING --
    /// the HP half already no-ops at hp 0 (LifeSap.NewHp never revives), and MP must not be
    /// written into a corpse either, even though MP carries no revival semantics.</summary>
    public static bool MpHalfAllowed(int hp, bool mpOk) => mpOk && hp > 0;

    /// <summary>The PURE per-battle layout validation gating EVERY MP write: the band +0x18/+0x1A
    /// pair is PROVISIONAL (never live-verified), so before the first MP write of a battle the
    /// whole band must look like MP -- at least 2 sampled units, mp &lt;= maxMp AND maxMp &lt;= 999
    /// for ALL of them, and maxMp &gt;= 1 for at least one (an all-zero sweep proves nothing).
    /// A fail means HP-only for that battle; the HP half is never gated.</summary>
    public static bool MpLayoutOk(IReadOnlyList<(int mp, int maxMp)> units)
    {
        if (units.Count < 2) return false;
        bool anyPool = false;
        foreach (var (mp, maxMp) in units)
        {
            if (mp > maxMp || maxMp > 999) return false;
            if (maxMp >= 1) anyPool = true;
        }
        return anyPool;
    }

    /// <summary>Guarded little-endian u16 write of the wielder's MP on its band entry (the
    /// provisional +0x18). Fail-safe no-op when the page isn't writable -- LifeSap.WriteHp's
    /// shape. The caller re-reads afterwards and logs SET/MISS.</summary>
    public static void WriteMp(long entryAddr, int newMp)
    {
        long a = entryAddr + Offsets.AMp;
        if (!Mem.Writable(a, 2)) return;
        Mem.W8(a, (byte)(newMp & 0xFF));
        Mem.W8(a + 1, (byte)((newMp >> 8) & 0xFF));
    }
}
