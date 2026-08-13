using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Decision 12 (the anchor-rotation rule, docs/RELIQUARY_AC.md/RELIQUARY_P1_PLAN.md) -- the
/// heart of the three-way card anchor: per weapon id, the runtime holds [baked, CURRENT composed
/// line, PREVIOUS distinct composed line]. The pair rotates ONLY on a compose-change edge (via
/// <see cref="SetCurrent"/>) -- NEVER at paint time (AnchorsFor is a pure read). A site painted
/// with a stale-but-known line (the previous one) verifies live and gets repainted to current,
/// rather than being evicted and freezing that buffer's Kills counter (CardSites.cs).
///
/// Both encodings (ASCII / UTF-16LE) are computed eagerly and REQUIRED to equal the baked Flavor
/// pattern's byte length for that encoding -- a mismatch refuses the update entirely (logged,
/// mirrors FlavorSpike.cs:109-116's defensive length check) rather than risk a mis-sized write
/// that would corrupt whatever bytes follow the flavor line in the card buffer.
///
/// Engine-thread only: no locking. Display/Engine's tick loop is single-threaded; this class is
/// not safe to call from any other thread.
/// </summary>
internal sealed class EarnedAnchors
{
    private sealed class Entry
    {
        public string Line = "";
        public byte[] Ascii = System.Array.Empty<byte>();
        public byte[] Utf16 = System.Array.Empty<byte>();
    }

    private readonly CardPatterns _pats;
    private readonly Dictionary<int, Entry> _current = new();
    private readonly Dictionary<int, Entry> _previous = new();

    public EarnedAnchors(CardPatterns pats) { _pats = pats; }

    /// <summary>Rotate on a genuine compose-change: previous := old current, current := line.
    /// Dedup: a line identical to the CURRENT one is a no-op (returns null, no rotation) -- a
    /// repeated compose of the same content must not manufacture a spurious rotation. Enforces
    /// the encoded-length invariant (see class doc); a mismatch refuses the whole update and
    /// logs, leaving the existing anchors untouched.
    /// Returns the line just evicted FROM current INTO previous (for LegendStore.RotatePainted
    /// to persist as "lastPainted"), or null when nothing rotated (dedup, first-ever compose, or
    /// a refused/mismatched update).</summary>
    public string? SetCurrent(int weaponId, string line)
    {
        if (_current.TryGetValue(weaponId, out var existing) && existing.Line == line)
            return null;   // dedup: identical content, no rotation

        if (!TryEncode(weaponId, line, out var entry)) return null;

        _current.TryGetValue(weaponId, out var oldCurrent);
        string? evictedToPrevious = oldCurrent?.Line;
        if (oldCurrent != null) _previous[weaponId] = oldCurrent;
        _current[weaponId] = entry;
        return evictedToPrevious;
    }

    /// <summary>Startup seeding (StoryLines.SeedAtStartup): set CURRENT directly, no rotation
    /// semantics, no return value. Same length enforcement as SetCurrent; a mismatch is a no-op
    /// (logged) rather than a crash on a stale/corrupt legends.json.</summary>
    public void SeedCurrent(int weaponId, string? line)
    {
        if (line == null) return;
        if (TryEncode(weaponId, line, out var entry)) _current[weaponId] = entry;
    }

    /// <summary>Startup seeding: set PREVIOUS directly from LegendStore's persisted "lastPainted"
    /// line. Same contract as <see cref="SeedCurrent"/>.</summary>
    public void SeedPrevious(int weaponId, string? line)
    {
        if (line == null) return;
        if (TryEncode(weaponId, line, out var entry)) _previous[weaponId] = entry;
    }

    /// <summary>The live anchor set for (weaponId, enc): baked FIRST (always present, if this
    /// weapon has a non-empty baked flavor), then current (if any), then previous (if any AND
    /// distinct from current -- dedup keeps the scanner from registering the same byte pattern
    /// twice). Empty for a weapon id with no baked pattern at all.</summary>
    public List<byte[]> AnchorsFor(int weaponId, int enc)
    {
        var list = new List<byte[]>(3);
        if (_pats.TryGet(weaponId, enc, out var baked) && baked.Flavor.Length > 0)
            list.Add(baked.Flavor);

        _current.TryGetValue(weaponId, out var cur);
        if (cur != null) list.Add(enc == 1 ? cur.Ascii : cur.Utf16);

        if (_previous.TryGetValue(weaponId, out var prev) && (cur == null || cur.Line != prev.Line))
            list.Add(enc == 1 ? prev.Ascii : prev.Utf16);

        return list;
    }

    /// <summary>The CURRENT composed-line pattern for (weaponId, enc), or null if none is
    /// registered. Used by CardSites' repaint-through (decision 12) -- distinct from
    /// AnchorsFor's whole verify set (baked + current + previous).</summary>
    public byte[]? CurrentFor(int weaponId, int enc)
    {
        if (!_current.TryGetValue(weaponId, out var cur)) return null;
        return enc == 1 ? cur.Ascii : cur.Utf16;
    }

    private bool TryEncode(int weaponId, string line, out Entry entry)
    {
        entry = new Entry();
        if (!_pats.TryGet(weaponId, 1, out var e1) || !_pats.TryGet(weaponId, 2, out var e2))
        {
            ModLogger.Error(LogVerb.Display, $"Refused to register an earned line: {LogNames.Weapon(weaponId)} has no baked pattern.");
            ModLogger.Debug(LogVerb.Trace, $"earned-line refusal detail (weapon id {weaponId}, no baked pattern)");
            return false;
        }
        byte[] ascii = ByteScan.Ascii(line);
        byte[] utf16 = ByteScan.Utf16(line);
        if (ascii.Length != e1.Flavor.Length || utf16.Length != e2.Flavor.Length)
        {
            ModLogger.Error(LogVerb.Display, $"Refused to register an earned line: {LogNames.Weapon(weaponId)}'s composed line length mismatches its baked flavor pattern (it would desync the painter).");
            ModLogger.Debug(LogVerb.Trace, "earned-line refusal detail (weapon id " + weaponId
                + ", ascii=" + ascii.Length + " vs " + e1.Flavor.Length
                + ", utf16=" + utf16.Length + " vs " + e2.Flavor.Length + ")");
            return false;
        }
        entry.Line = line;
        entry.Ascii = ascii;
        entry.Utf16 = utf16;
        return true;
    }
}
