using System;
using System.Collections.Generic;
using System.Text;

namespace LivingWeapon;

/// <summary>
/// Pure decisions for <see cref="IconCacheHeal"/>, so the hot path rules are plain unit tests.
/// </summary>
internal static class IconCacheHealPolicy
{
    internal const int MaxNameBytes = 120;
    internal const int MaxAttempts = 3;
    // The longest measured screen build was 1770 ms, with margin.
    internal const int BuilderWindowMs = 2500;
    // 121 weapons, two surfaces each.
    internal const int TrackCap = 242;
    private const int PageSize = 4096;

    internal static bool IsStackKey(long keyAddr, (long Low, long High) stack)
        => keyAddr >= stack.Low && keyAddr < stack.High;

    /// <summary>First read stops at the page edge so a name beside unmapped memory still reads.</summary>
    internal static int FirstReadLength(long namePtr)
        => (int)Math.Min(MaxNameBytes, PageSize - (namePtr & (PageSize - 1)));

    /// <summary>The name from one or two reads, or null when no NUL ends it inside the budget.</summary>
    internal static string? NameFrom(byte[] first, byte[]? second)
    {
        int end = Array.IndexOf(first, (byte)0);
        if (end == 0) return null;
        if (end > 0) return Encoding.ASCII.GetString(first, 0, end);
        if (second == null) return null;
        int end2 = Array.IndexOf(second, (byte)0);
        if (end2 < 0) return null;
        var all = new byte[first.Length + end2];
        first.CopyTo(all, 0);
        Array.Copy(second, 0, all, first.Length, end2);
        return Encoding.ASCII.GetString(all);
    }

    internal static bool InBuilderWindow(long nowMs, long seenMs) => nowMs - seenMs < BuilderWindowMs;

    internal static bool AttemptsExhausted(int attempts) => attempts >= MaxAttempts;

    /// <summary>Adds names until the cap; returns how many are tracked afterward.</summary>
    internal static int AddTracked(HashSet<string> tracked, IEnumerable<string> names)
    {
        foreach (var n in names)
            if (tracked.Count < TrackCap) tracked.Add(n);
        return tracked.Count;
    }

    internal static string Tail(string name)
    {
        int slash = name.LastIndexOf('/');
        return slash < 0 ? name : name[(slash + 1)..];
    }
}
