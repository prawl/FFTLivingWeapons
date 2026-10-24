using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace LivingWeapon;

/// <summary>Counts the errors the mod ignores on purpose, by place, so the end of a battle can report them.
/// The logger and the flight recorder call this from their own catches, so it never logs and never throws.</summary>
internal static class QuietFaults
{
    private static readonly object _lock = new();
    private static readonly Dictionary<string, int> _counts = new();
    private static int _noteFailures;

    internal static void Note(string site)
    {
        try
        {
            lock (_lock)
            {
                string key = string.IsNullOrEmpty(site) ? "unknown" : site;
                _counts[key] = _counts.TryGetValue(key, out int n) ? n + 1 : 1;
            }
        }
        catch { Interlocked.Increment(ref _noteFailures); }
    }

    internal static IReadOnlyList<(string Site, int Count)> Drain()
    {
        try
        {
            lock (_lock)
            {
                var list = _counts.Where(kv => kv.Value > 0).OrderBy(kv => kv.Key, StringComparer.Ordinal)
                    .Select(kv => (kv.Key, kv.Value)).ToList();
                _counts.Clear();
                return list;
            }
        }
        catch { Interlocked.Increment(ref _noteFailures); return Array.Empty<(string, int)>(); }
    }

    internal static string Describe(IEnumerable<(string Site, int Count)> faults) =>
        "Errors the mod chose to ignore since the last battle (none of them stopped play): "
        + string.Join(", ", faults.Select(f => $"{f.Site} x{f.Count}"));

    internal static void ReportBattleExit()
    {
        var faults = Drain();
        if (faults.Count == 0) return;
        ModLogger.Debug(LogVerb.Startup, Describe(faults));
        Flight.Record("quiet-faults", string.Join(" ", faults.Select(f => $"{f.Site}={f.Count}")));
    }

    internal static void ResetForTest()
    {
        lock (_lock) { _counts.Clear(); }
        Interlocked.Exchange(ref _noteFailures, 0);
    }
}
