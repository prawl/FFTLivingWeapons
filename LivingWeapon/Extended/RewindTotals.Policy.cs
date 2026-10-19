using System;
using System.Collections.Generic;
using System.Linq;

namespace LivingWeapon;

internal static class TotalsPolicy
{
    /// <summary>The bag byte that makes bag plus hands equal the total. A byte cannot go below 0 or above 255.</summary>
    public static int BagFor(int total, int hands) => Math.Clamp(total - hands, 0, 255);
}

/// <summary>Two recompute passes after a restore event, so the game's own roster revert is counted whether it lands before or after our write.</summary>
internal sealed class RecomputeScheduler
{
    // The roster revert landed 7 ms after the write, so about 100 ms clears it and 500 ms is the margin.
    public const int FirstAfterTicks = 3;
    public const int SecondAfterTicks = 15;

    private readonly List<long> _due = new();

    public int Pending => _due.Count;

    /// <summary>A new schedule replaces any pass that has not run yet.</summary>
    public void Schedule(long tick)
    {
        _due.Clear();
        _due.Add(tick + FirstAfterTicks);
        _due.Add(tick + SecondAfterTicks);
    }

    /// <summary>True once when a due tick has been reached, then those passes are spent.</summary>
    public bool Due(long tick)
    {
        if (!_due.Any(d => d <= tick)) return false;
        _due.RemoveAll(d => d <= tick);
        return true;
    }
}
