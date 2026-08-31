using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// LW-351 round 8b (2026-08-31): the REPAIR half of <see cref="TemplateSeat"/>.
///
/// WHY: three copies of the game's owned-item template maintainer walked a template with a
/// bound of 261, so an extended id inside the table ended the walk early and every insert
/// doubled the id's neighbor and destroyed the word after it, and once the crawling id reached
/// the end marker the marker was lost too (owner-observed 2026-08-31: five doubled shields, an
/// inventory table with 129 ids and zeros where its marker had been, empty rows, a crash).
/// Widening the three bounds (ExtendedSites, fix rounds 8 and 8b) stops new damage but heals
/// nothing: the game's rebuild re-emits a list word for every doubled template entry and writes
/// the list back, and its maintainer deletes only ids the player owns none of. So every seat
/// scans the table first and rewrites it when it is damaged.
///
/// RULES, each pinned by a test: a doubled id keeps its first occurrence; a zero word goes (id 0
/// is never an entry, the menu shows it as an empty row); a table with no 0x00FF marker gets
/// one at its first zero word and everything past that word is dropped as stale; a table with
/// neither marker nor zero word is refused, not guessed at; a clean table is untouched.
/// </summary>
internal static partial class TemplateSeat
{
    /// <summary>What a scan found: the surviving words in order, where the marker is (or will
    /// be), what was healed (null when nothing), or why the table cannot be read as a list.</summary>
    internal readonly record struct Scan(List<ushort> Body, int Marker, string? Repaired, string? Refusal);

    /// <summary>Pure: read <paramref name="words"/> u16 words of <paramref name="region"/> and
    /// apply the repair rules above.</summary>
    internal static Scan ScanTable(byte[] region, int words)
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
        int zeros = 0;
        foreach (ushort w in raw)
        {
            if (w == 0) { zeros++; continue; }
            int id = w & 0x3FF;   // the game's own id mask; the templates carry no flag bits today
            if (!seen.Add(id)) { doubled.Add(id.ToString()); continue; }
            body.Add(w);
        }
        if (doubled.Count > 0 || zeros > 0)
        {
            string healed = $"removed {doubled.Count} doubled id(s) [{string.Join(",", doubled)}] and {zeros} zero word(s)";
            note = note == null ? healed : note + "; " + healed;
        }
        return new Scan(body, marker, note, null);
    }
}
