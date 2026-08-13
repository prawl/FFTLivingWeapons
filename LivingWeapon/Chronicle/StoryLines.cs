using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// The Reliquary Phase 1 compose driver: owns EarnedAnchors + the LegendStore view + meta
/// budgets, so Display.cs's own addition stays a ~12-line seam (construct at startup, seed, call
/// RecomposeChanged on the existing change path, thread AnchorsFor into the scanner/sites).
///
/// SeedAtStartup recomposes CURRENT fresh from store state every launch (decision 12: CURRENT is
/// never itself persisted -- it is deterministic given the store, so recomputing it is cheap and
/// avoids a second persistence path to keep in sync) and loads PREVIOUS from LegendStore's
/// persisted "lastPainted" (the one thing that DOES need to survive a relaunch, since a stale
/// on-screen buffer painted before the last session's rotation may still be showing it).
///
/// RecomposeChanged is the live compose-change edge: recompose each given weapon id, and on an
/// actual content change, rotate the anchor (EarnedAnchors.SetCurrent) and persist the evicted
/// line as the new "lastPainted" (LegendStore.RotatePainted) in the SAME step -- the two always
/// move together, never separately.
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

    /// <summary>Prime every weapon's anchors from persisted store state. Call once at startup,
    /// before the first Tick.</summary>
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

    /// <summary>Recompose every id in the changed set (ids whose kill tally changed this tick --
    /// a deed can only change alongside a tally increment, KillTracker.CreditKill). A composed
    /// line identical to the current one is a no-op inside EarnedAnchors (dedup); a genuinely new
    /// line rotates the anchor and persists the evicted line as the store's new "lastPainted".</summary>
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

    /// <summary>The underlying anchor registry, handed to CardSites' ctor and CardScanner's
    /// FindKills calls (Display.cs) -- CardSites/CardScanner want the raw EarnedAnchors object,
    /// not a per-call passthrough.</summary>
    public EarnedAnchors Anchors => _anchors;

    private int KillsFor(int id) => _kills.TryGetValue(id, out int k) ? k : 0;
}
