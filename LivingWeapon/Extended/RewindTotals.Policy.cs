using System;
using System.Collections.Generic;
using System.Linq;

namespace LivingWeapon;

internal static class TotalsPolicy
{
    /// <summary>The bag is one byte per item, so it stops at 0 and 255.</summary>
    public static int BagFor(int total, int hands) => Math.Clamp(total - hands, 0, 255);
}

/// <summary>Two passes after a restore, because the game puts the roster back on its own schedule, sometimes just after our write.</summary>
internal sealed class RecomputeScheduler
{
    // The game's roster revert was seen 7 ms after our write, so about 100 ms clears it and 500 ms is the safety margin.
    public const int FirstAfterTicks = 3;
    public const int SecondAfterTicks = 15;

    private readonly List<long> _due = new();

    public int Pending => _due.Count;

    public void Schedule(long tick)
    {
        _due.Clear();
        _due.Add(tick + FirstAfterTicks);
        _due.Add(tick + SecondAfterTicks);
    }

    public bool Due(long tick)
    {
        if (!_due.Any(d => d <= tick)) return false;
        _due.RemoveAll(d => d <= tick);
        return true;
    }
}
