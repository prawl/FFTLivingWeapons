using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Reliquary Phase 1 compose driver. Owns EarnedAnchors, the LegendStore view and the meta
/// budgets, so Display.cs only needs a small seam to call it. CURRENT is recomposed from store
/// state on every launch (decision 12), so it is never persisted. PREVIOUS loads from LegendStore's
/// persisted "lastPainted", since a buffer painted before the last rotation may still be on screen.
/// </summary>
internal sealed class StoryLines
{
    private readonly LegendStore _store;
    private readonly Dictionary<int, WeaponMeta> _meta;
    private readonly Dictionary<int, int> _kills;
    private readonly EarnedAnchors _anchors;

    public StoryLines(LegendStore store, Dictionary<int, WeaponMeta> meta, Dictionary<int, int> kills, CardPatterns pats)
    {
        _store = store;
        _meta = meta;
        _kills = kills;
        _anchors = new EarnedAnchors(pats);
    }

    /// <summary>Prime every weapon's anchors from persisted store state. Call once at startup.</summary>
    public void SeedAtStartup()
    {
        foreach (var (id, m) in _meta)
        {
            if (string.IsNullOrEmpty(m.Flavor)) continue;
            var legend = _store.Get(id);
            string? current = CardLine.Compose(m.Name, KillsFor(id), legend, m.Flavor.Length);
            _anchors.SeedCurrent(id, current);
            _anchors.SeedPrevious(id, legend.LastPainted);
        }
    }

    /// <summary>Recompose each id in the changed set (kill tally changed this tick; KillTracker.CreditKill).
    /// A new line rotates the anchor and stores the evicted line as "lastPainted"; an identical one is a no-op.</summary>
    public void RecomposeChanged(IEnumerable<int> ids)
    {
        foreach (int id in ids)
        {
            if (!_meta.TryGetValue(id, out var m) || string.IsNullOrEmpty(m.Flavor)) continue;
            var legend = _store.Get(id);
            string? line = CardLine.Compose(m.Name, KillsFor(id), legend, m.Flavor.Length);
            if (line == null) continue;   // nothing composable (no deeds yet, or no form fits) -- leave anchors as-is
            string? evicted = _anchors.SetCurrent(id, line);
            if (evicted != null) _store.RotatePainted(id, evicted);
        }
    }

    /// <summary>Threaded into Display's OnChunk -> CardScanner/CardSites (Display.cs section O).</summary>
    public List<byte[]> AnchorsFor(int weaponId, int enc) => _anchors.AnchorsFor(weaponId, enc);

    /// <summary>The underlying anchor registry, passed to CardSites and CardScanner.FindKills (Display.cs).</summary>
    public EarnedAnchors Anchors => _anchors;

    private int KillsFor(int id) => _kills.TryGetValue(id, out int k) ? k : 0;
}
