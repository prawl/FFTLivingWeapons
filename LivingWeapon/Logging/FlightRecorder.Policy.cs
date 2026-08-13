using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

namespace LivingWeapon;

/// <summary>
/// FlightRecorder's PURE decision half (the 200-line refactor seam) -- filename sanitizing, JSONL
/// serialization, and the retention-selection logic, all plain functions of their inputs with no
/// lock, no clock, and no IO. FlightRecorder.cs (the stateful runtime half) calls into these.
/// </summary>
internal sealed partial class FlightRecorder
{
    /// <summary>Ring contents in insertion (oldest-first) order. Caller holds <c>_lock</c>; this
    /// just linearizes the circular buffer into a plain array (no IO, no lock of its own).</summary>
    private FlightRecord[] Linearize()
    {
        if (_count < Capacity)
        {
            var outArr = new FlightRecord[_count];
            Array.Copy(_ring, 0, outArr, 0, _count);
            return outArr;
        }
        var full = new FlightRecord[Capacity];
        int firstPart = Capacity - _head;
        Array.Copy(_ring, _head, full, 0, firstPart);
        Array.Copy(_ring, 0, full, firstPart, _head);
        return full;
    }

    private static string SafeTrigger(string trigger) => string.IsNullOrEmpty(trigger) ? "flush" : trigger;

    /// <summary>JSONL body: a header line (wall-clock + elapsedMs at flush time, S5) followed by
    /// one compact JSON object per record ({t, e, d}). Newtonsoft.Json only (S4) -- no hand-rolled
    /// escaping, so hostile payloads (quotes, backslashes, newlines, brackets) round-trip exactly.</summary>
    private static string Serialize(FlightRecord[] records, DateTime wall, long flushElapsed)
    {
        var sb = new StringBuilder();
        sb.Append(JsonConvert.SerializeObject(new Dictionary<string, object>
        {
            ["hdr"] = true,
            ["wall"] = wall.ToString("yyyy-MM-dd HH:mm:ss.fff"),
            ["t"] = flushElapsed,
        })).Append('\n');
        foreach (var r in records)
        {
            sb.Append(JsonConvert.SerializeObject(new Dictionary<string, object>
            {
                ["t"] = r.ElapsedMs,
                ["e"] = r.Type,
                ["d"] = r.Payload,
            })).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Which of <paramref name="existing"/> archive paths to delete so at most
    /// <paramref name="retentionCount"/> remain: everything beyond the newest N, oldest-first.
    /// Filenames sort chronologically (yyyyMMdd_HHmmss right after the flight_ prefix), so a
    /// plain ordinal sort is a valid oldest-first order -- pure given its inputs, no IO.</summary>
    private static List<string> SelectForDeletion(IEnumerable<string> existing, int retentionCount)
    {
        var files = existing.ToList();
        files.Sort(StringComparer.Ordinal);
        int excess = files.Count - retentionCount;
        return excess > 0 ? files.Take(excess).ToList() : new List<string>();
    }
}
