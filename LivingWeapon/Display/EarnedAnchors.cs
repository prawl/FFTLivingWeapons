using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Decision 12 (docs/RELIQUARY_AC.md): per weapon id, holds [baked, current composed line,
/// previous distinct composed line] as the card anchors. The pair rotates only on a
/// compose-change edge via SetCurrent, never at paint time, so a site painted with the previous
/// line still verifies and is repainted instead of evicted (CardSites.cs). Encoded lines must
/// match the baked Flavor length in both encodings (ASCII, UTF-16LE); a mismatch refuses the
/// update, as FlavorSpike.cs:109-116 does. Engine-thread only, no locking.
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

    /// <summary>Rotate on a compose-change. Previous := old current, current := line. An identical
    /// line is a no-op, and a length mismatch is refused and logged. Returns the evicted line (for
    /// LegendStore.RotatePainted's "lastPainted"), or null if nothing rotated.</summary>
    public string? SetCurrent(int weaponId, string line)
    {
        if (_current.TryGetValue(weaponId, out var existing) && existing.Line == line)
            return null;   // dedup. Identical content, no rotation

        if (!TryEncode(weaponId, line, out var entry)) return null;

        _current.TryGetValue(weaponId, out var oldCurrent);
        string? evictedToPrevious = oldCurrent?.Line;
        if (oldCurrent != null) _previous[weaponId] = oldCurrent;
        _current[weaponId] = entry;
        return evictedToPrevious;
    }

    /// <summary>Startup seeding (StoryLines.SeedAtStartup): sets current with no rotation. A bad
    /// length is logged and ignored so a stale legends.json cannot crash.</summary>
    public void SeedCurrent(int weaponId, string? line)
    {
        if (line == null) return;
        if (TryEncode(weaponId, line, out var entry)) _current[weaponId] = entry;
    }

    /// <summary>Startup seeding of previous from LegendStore's persisted "lastPainted" line.
    /// Same rules as <see cref="SeedCurrent"/>.</summary>
    public void SeedPrevious(int weaponId, string? line)
    {
        if (line == null) return;
        if (TryEncode(weaponId, line, out var entry)) _previous[weaponId] = entry;
    }

    /// <summary>Anchor set for (weaponId, enc): baked, then current, then previous if distinct
    /// from current, then the baked Grows line. Empty if the weapon has no baked pattern.</summary>
    public List<byte[]> AnchorsFor(int weaponId, int enc)
    {
        var list = UniqueAnchorsFor(weaponId, enc);
        if (_pats.TryGet(weaponId, enc, out var baked) && baked.Grows.Length > 0)
            list.Add(baked.Grows);
        return list;
    }

    /// <summary><see cref="AnchorsFor"/> minus the baked Grows line. Same-lane weapons bake
    /// identical Grows lines, so CardScanner's two-key ownership rule (FindNearestFlavor) needs the
    /// unique candidates to break ties.
    /// Known gap (LW-332 round 3): analyze.py's check_flavor_prefix sees only baked flavor, not
    /// composed lines. Composed lines are length-locked to the weapon's own flavor, entries with no
    /// unique candidate in-window are ineligible (STEP 2), and CardSites' paint-time verify catches
    /// a mispaint.</summary>
    public List<byte[]> UniqueAnchorsFor(int weaponId, int enc)
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

    /// <summary>The current composed-line pattern for (weaponId, enc), or null. Used by CardSites'
    /// repaint-through (decision 12).</summary>
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
