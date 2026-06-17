using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// The pure decisions behind Stormarc's "Chain Lightning" signature -- no memory access.
/// The stateful two-pass scanner and guarded band write live in Ricochet.cs.
/// </summary>
internal sealed partial class Ricochet
{
    /// <summary>A candidate bounce target: band slot, grid position, current HP, and whether it
    /// is enemy-side (static-array fingerprint match). Only enemies are valid bounce targets.</summary>
    internal readonly record struct SlotInfo(int Slot, int Gx, int Gy, int Hp, bool Enemy);

    /// <summary>True when the signature is configured (RicochetRadius set) and the kill tier is earned.</summary>
    public static bool IsActive(WeaponSignature? sig, int tier)
        => Signatures.Earned(sig, tier) && sig!.RicochetRadius > 0;

    /// <summary>Chip damage: pct% of the original damage, integer floor. Returns 0 when original is 0.
    /// Returns at least 1 when the original was >= 1 (a real hit always produces at least 1 chip).</summary>
    public static int ChipDamage(int dmg, int pct)
    {
        if (dmg <= 0) return 0;
        int chip = dmg * pct / 100;
        return chip < 1 ? 1 : chip;
    }

    /// <summary>Nearest live enemy within <paramref name="radius"/> Manhattan tiles of
    /// (<paramref name="cx"/>, <paramref name="cy"/>) not in <paramref name="excluded"/>.
    /// Tie-break: lowest band slot. Returns null if no candidate.</summary>
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

    /// <summary>Pick the best bounce target: nearest live OTHER enemy within <paramref name="radius"/>
    /// Manhattan tiles of the victim. Allies are never targets. Tie-break: lower band slot.
    /// Returns -1 if no candidate.</summary>
    public static int PickTarget(int victimSlot, int victimGx, int victimGy, int radius,
                                  IReadOnlyList<SlotInfo> slots)
        => NearestEnemyExcluding(victimGx, victimGy, radius, slots,
                                  new HashSet<int> { victimSlot })?.Slot ?? -1;

    /// <summary>Greedy chain selector: re-centers on each struck unit, never revisits a slot in
    /// <paramref name="excluded"/> (the caller passes the victim and any already-struck slots).
    /// Returns ordered target slots, at most <paramref name="maxHops"/>. Pure -- never mutates
    /// the caller's excluded set.</summary>
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

    /// <summary>Chip for a given hop index in the chain. Hop 0 = basePct% of origDmg; each
    /// subsequent hop = decayPct% of the previous hop's chip. Floored at 1 for positive damage,
    /// 0 when origDmg is 0.</summary>
    public static int ChipForHop(int origDmg, int basePct, int decayPct, int hopIndex)
    {
        int chip = ChipDamage(origDmg, basePct);
        for (int i = 0; i < hopIndex; i++) chip = ChipDamage(chip, decayPct);
        return chip;
    }

    /// <summary>Manhattan (taxicab) distance between two grid cells.</summary>
    public static int Manhattan(int x1, int y1, int x2, int y2)
        => Math.Abs(x2 - x1) + Math.Abs(y2 - y1);

    /// <summary>New HP after applying chip: max(currentHp - chip, 1). Chip never kills.</summary>
    public static int ClampHp(int currentHp, int chip) => Math.Max(currentHp - chip, 1);

    /// <summary>Write the clamped HP to the authoritative band entry. Both bytes written
    /// little-endian. No-ops if not writable (fail-safe) or if HP is already at the floor.</summary>
    public static void ApplyChip(IGameMemory mem, long addr, int currentHp, int chip)
    {
        int newHp = ClampHp(currentHp, chip);
        if (newHp == currentHp) return;   // already at floor (hp was 1) or chip is 0
        long hpAddr = addr + Offsets.AHp;
        if (!mem.Writable(hpAddr, 2)) return;
        mem.W8(hpAddr,     (byte)(newHp & 0xFF));
        mem.W8(hpAddr + 1, (byte)((newHp >> 8) & 0xFF));
    }
}

/// <summary>Per-slot HP tracking for the chain-lightning damage-event detector. Baselines the
/// first sighting silently; after that a drop in HP is one event (heals are ignored).
/// <see cref="Consume"/> records our OWN chip write so the bounce never feeds another bounce.</summary>
internal sealed class RicochetState
{
    private readonly bool[] _seen;
    private readonly int[] _prevHp;

    public RicochetState(int slots) { _seen = new bool[slots]; _prevHp = new int[slots]; }

    /// <summary>Reset on battle enter/exit.</summary>
    public void ResetBattle() { Array.Clear(_seen, 0, _seen.Length); Array.Clear(_prevHp, 0, _prevHp.Length); }

    /// <summary>Observe a slot's current HP. Returns the positive damage delta if this is a
    /// drop on a previously-seen slot, else 0 (first sighting baselines silently).</summary>
    public int Observe(int slot, int currentHp)
    {
        if (!_seen[slot])
        {
            _seen[slot] = true;
            _prevHp[slot] = currentHp;
            return 0;
        }
        int delta = _prevHp[slot] - currentHp;
        _prevHp[slot] = currentHp;
        return delta > 0 ? delta : 0;
    }

    /// <summary>Record OUR chip write as the slot's known HP, so the next Observe doesn't
    /// read it back as a fresh damage event (the no-chain guarantee).</summary>
    public void Consume(int slot, int newHp)
    {
        if (_seen[slot]) _prevHp[slot] = newHp;
    }
}
