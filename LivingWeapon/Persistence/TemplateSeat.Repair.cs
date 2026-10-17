using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// The repair side of <see cref="TemplateSeat"/> (2026-08-31). Three copies of the game's
/// template maintainer walked with a bound of 261, so an extended id ended the walk early, doubled its
/// neighbor and could destroy the end marker (seen as doubled shields, a 129-id table and a crash).
/// The widened bounds in ExtendedSites stop new damage but heal nothing, so each seat scans first.
/// Rules, each pinned by a test. A doubled id keeps its first occurrence. A zero word goes (id 0 shows as an empty row).
/// A table with no 0x00FF marker gets one at its first zero word and the rest is dropped; with neither it is refused.
/// A word at or past the armed id range is dropped before the dedupe, since it ends the game's walk early too.
/// </summary>
internal static partial class TemplateSeat
{
    /// <summary>What a scan found. The surviving words, the marker position, what was healed (null if nothing), or why the table cannot be read.</summary>
    internal readonly record struct Scan(List<ushort> Body, int Marker, string? Repaired, string? Refusal);

    /// <summary>Pure. Reads <paramref name="words"/> u16 words of <paramref name="region"/> and applies the repair rules above.</summary>
    /// <param name="firstStaleId">Ids at or past this bound are dropped as stale. The default keeps legacy callers
    /// unchanged; production passes <see cref="ExtendedCatalog.FirstExtendedId"/> plus the armed extended count.</param>
    internal static Scan ScanTable(byte[] region, int words, int firstStaleId = int.MaxValue)
    {
        var raw = new List<ushort>(words);
        int marker = -1;
        for (int i = 0; i < words; i++)
        {
            ushort w = (ushort)(region[i * 2] | (region[i * 2 + 1] << 8));
            if (w == EndMarker) { marker = i; break; }
            raw.Add(w);
        }
        string? note = null;
        if (marker < 0)
        {
            int zero = raw.IndexOf(0);
            if (zero < 0)
                return new Scan(raw, -1, null, $"no {EndMarker:X4} end marker in its first {words} words, so where the list ends is unknown");
            raw = raw.GetRange(0, zero);
            marker = zero;
            note = $"no end marker, so the stale words from word {zero} (the first empty word) on were dropped and the marker now follows the surviving ids";
        }

        var seen = new HashSet<int>();
        var body = new List<ushort>(raw.Count);
        var doubled = new List<string>();
        var stale = new List<string>();
        int zeros = 0;
        foreach (ushort w in raw)
        {
            if (w == 0) { zeros++; continue; }
            int id = w & 0x3FF;   // the game's id mask; templates carry no flag bits today
            // Stale words go before the dedupe, so they never count as doubles.
            if (id >= firstStaleId) { stale.Add(id.ToString()); continue; }
            if (!seen.Add(id)) { doubled.Add(id.ToString()); continue; }
            body.Add(w);
        }
        if (doubled.Count > 0 || zeros > 0)
        {
            string healed = $"removed {doubled.Count} doubled id(s) [{string.Join(",", doubled)}] and {zeros} zero word(s)";
            note = note == null ? healed : note + "; " + healed;
        }
        if (stale.Count > 0)
        {
            string healed = $"dropped {stale.Count} stale id(s) [{string.Join(",", stale)}] past the armed range";
            note = note == null ? healed : note + "; " + healed;
        }
        return new Scan(body, marker, note, null);
    }
}
