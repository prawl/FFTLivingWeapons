using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Pure decisions for Stormarc's "Chain Lightning" signature; no memory access.
/// The stateful scanner and band write live in Ricochet.cs.
/// </summary>
internal sealed partial class Ricochet
{
    /// <summary>A bounce candidate. Band slot, grid position, HP, and whether it is enemy-side.</summary>
    internal readonly record struct SlotInfo(int Slot, int Gx, int Gy, int Hp, bool Enemy);

    /// <summary>True when the signature is configured (RicochetRadius set) and the kill tier is earned.</summary>
    public static bool IsActive(WeaponSignature? sig, int tier)
        => Signatures.Earned(sig, tier) && sig!.RicochetRadius > 0;

    /// <summary>Chip damage. Pct% of the original, floored, minimum 1 for a real hit, 0 when dmg is 0.</summary>
    public static int ChipDamage(int dmg, int pct)
    {
        if (dmg <= 0) return 0;
        int chip = dmg * pct / 100;
        return chip < 1 ? 1 : chip;
    }

    /// <summary>Nearest live enemy within <paramref name="radius"/> Manhattan tiles of (<paramref name="cx"/>, <paramref name="cy"/>),
    /// skipping <paramref name="excluded"/>. Lowest band slot breaks ties; null if none.</summary>
    private static SlotInfo? NearestEnemyExcluding(int cx, int cy, int radius,
        IReadOnlyList<SlotInfo> slots, ISet<int> excluded)
    {
        SlotInfo? best = null; int bestDist = int.MaxValue;
        foreach (var s in slots)
        {
            if (!s.Enemy || s.Hp <= 0 || excluded.Contains(s.Slot)) continue;
            int d = Manhattan(cx, cy, s.Gx, s.Gy);
            if (d > radius) continue;
            if (d < bestDist || (d == bestDist && (best is null || s.Slot < best.Value.Slot)))
            { bestDist = d; best = s; }
        }
        return best;
    }

    /// <summary>Nearest live enemy other than the victim within <paramref name="radius"/>. Lower band slot breaks ties; -1 if none.</summary>
    public static int PickTarget(int victimSlot, int victimGx, int victimGy, int radius,
                                  IReadOnlyList<SlotInfo> slots)
        => NearestEnemyExcluding(victimGx, victimGy, radius, slots,
                                  new HashSet<int> { victimSlot })?.Slot ?? -1;

    /// <summary>Greedy chain that re-centers on each struck unit and skips <paramref name="excluded"/> (victim plus
    /// already struck). Returns at most <paramref name="maxHops"/> slots; does not mutate the caller's set.</summary>
    public static IReadOnlyList<int> PickChain(int startGx, int startGy, int radius, int maxHops,
        IReadOnlyList<SlotInfo> slots, ISet<int> excluded)
    {
        var chain = new List<int>();
        var seen = new HashSet<int>(excluded);
        int cx = startGx, cy = startGy;
        while (chain.Count < maxHops)
        {
            var next = NearestEnemyExcluding(cx, cy, radius, slots, seen);
            if (next is null) break;
            chain.Add(next.Value.Slot);
            seen.Add(next.Value.Slot);
            cx = next.Value.Gx; cy = next.Value.Gy;
        }
        return chain;
    }

    /// <summary>Chip for a hop. Hop 0 is basePct% of origDmg, each later hop decayPct% of the previous chip;
    /// minimum 1, or 0 when origDmg is 0.</summary>
    public static int ChipForHop(int origDmg, int basePct, int decayPct, int hopIndex)
    {
        int chip = ChipDamage(origDmg, basePct);
        for (int i = 0; i < hopIndex; i++) chip = ChipDamage(chip, decayPct);
        return chip;
    }

    /// <summary>Taxicab distance; forwards to <see cref="Grid.Manhattan"/>.</summary>
    public static int Manhattan(int x1, int y1, int x2, int y2) => Grid.Manhattan(x1, y1, x2, y2);

    /// <summary>New HP after applying chip. Max(currentHp, chip, 1). Chip never kills.</summary>
    public static int ClampHp(int currentHp, int chip) => Math.Max(currentHp - chip, 1);

    /// <summary>Write the clamped HP as one little-endian W16 (two W8 writes left a torn-value window).
    /// No-op if unwritable or already at the floor.</summary>
    public static void ApplyChip(IGameMemory mem, long addr, int currentHp, int chip)
    {
        int newHp = ClampHp(currentHp, chip);
        if (newHp == currentHp) return;   // already at floor (hp was 1) or chip is 0
        long hpAddr = addr + Offsets.AHp;
        if (!mem.Writable(hpAddr, 2)) return;
        mem.W16(hpAddr, (ushort)newHp);
    }
}

/// <summary>Per-slot HP tracking for chain lightning. An HP drop is one event, heals are ignored.
/// Bookkeeping is the shared <see cref="HpDeltaState"/>.</summary>
internal sealed class RicochetState : HpDeltaState
{
    public RicochetState(int slots) : base(slots) { }

    protected override int Delta(int prevHp, int currentHp) => prevHp - currentHp;
}
