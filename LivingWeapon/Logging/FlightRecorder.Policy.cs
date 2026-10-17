using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

namespace LivingWeapon;

/// <summary>
/// Pure half of FlightRecorder (the 200-line seam). It holds filename sanitizing, JSONL serialization
/// and retention selection, with no lock, clock or IO. FlightRecorder.cs calls into these.
/// </summary>
internal sealed partial class FlightRecorder
{
    /// <summary>Linearizes the circular buffer oldest-first. Caller holds <c>_lock</c>.</summary>
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

    /// <summary>JSONL body. A header line (wall-clock, elapsedMs, mod version and game build at flush time), then one compact
    /// JSON object per record ({t, e, d}). Newtonsoft.Json only, so hostile payloads round-trip.</summary>
    private static string Serialize(FlightRecord[] records, DateTime wall, long flushElapsed, string version, string build)
    {
        var sb = new StringBuilder();
        sb.Append(JsonConvert.SerializeObject(new Dictionary<string, object>
        {
            ["hdr"] = true,
            ["wall"] = wall.ToString("yyyy-MM-dd HH:mm:ss.fff"),
            ["t"] = flushElapsed,
            ["version"] = version,
            ["build"] = build,
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

    /// <summary>Paths in <paramref name="existing"/> to delete, keeping the newest <paramref name="retentionCount"/>.
    /// Filenames sort chronologically (yyyyMMdd_HHmmss after flight_), so an ordinal sort works.</summary>
    private static List<string> SelectForDeletion(IEnumerable<string> existing, int retentionCount)
    {
        var files = existing.ToList();
        files.Sort(StringComparer.Ordinal);
        int excess = files.Count - retentionCount;
        return excess > 0 ? files.Take(excess).ToList() : new List<string>();
    }
}
