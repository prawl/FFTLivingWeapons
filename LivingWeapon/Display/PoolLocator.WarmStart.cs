using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Warm-starts the region cache from a persisted sidecar (PoolRegionSidecar, pool_regions.json)
/// across launches and keeps the sidecar in sync as the locator republishes.
/// SeedFromSidecar runs once, from the first <see cref="Step"/>, before the cold scan begins (PoolLocator.Restart.cs).
/// It never marks the locate complete, so the cold scan still runs and republishes; seeding only adds early paint.
/// MaybeSaveSidecar is called from Publish (PoolLocator.Restart.cs) and skips the write when the regions
/// match what is already on disk.
/// </summary>
internal sealed partial class PoolLocator
{
    private readonly string? _regionSidecarPath;
    private bool _warmStartAttempted;

    // Last known on-disk content, for MaybeSaveSidecar's dirty check; null until a load or save happens.
    private List<(long baseAddr, long size)>? _lastSavedRegions;

    /// <summary>Loads the sidecar (a null path means none is wired), re-verifies each persisted region against
    /// live memory via <see cref="ScanRegion"/> (the reverify <see cref="AllCachedStillPool"/> uses), and seeds the
    /// survivors into _cached like <see cref="SeedForTest"/>. Logs the adoption line whenever a sidecar is wired,
    /// N=0 included, because the ledger row [kills-pool-region-recurrence] needs that measurement.</summary>
    private void SeedFromSidecar()
    {
        if (_regionSidecarPath == null) return;

        var loaded = PoolRegionSidecar.Load(_regionSidecarPath);
        _lastSavedRegions = new List<(long baseAddr, long size)>(loaded.Regions);   // what's on disk right now

        var survivors = new List<(long baseAddr, long size)>();
        foreach (var r in loaded.Regions)
            if (ScanRegion(r.baseAddr, r.size).isPool) survivors.Add(r);

        if (survivors.Count > 0)
        {
            _cached.Clear();
            _cached.AddRange(survivors);
            PublishGeneration++;   // a fresh region list event, same as SeedForTest/Publish
        }

        // Force the cold scan to begin. Otherwise Step would see _cached non-empty and only revalidate,
        // skipping the full locate until RevalidateMs fires. Like Invalidate() but leaves _pendingTrigger
        // alone, so a first-ever scan still reports Trigger="first". Set even when nothing survived.
        _restartPending = true;

        ModLogger.Debug(LogVerb.Display,
            $"Warm start seeded {survivors.Count} of {loaded.Regions.Count} persisted pool regions; the full locate continues in the background.");
    }

    private void MaybeSaveSidecar()
    {
        if (_regionSidecarPath == null) return;
        if (_lastSavedRegions != null && RegionListsMatch(_lastSavedRegions, _cached)) return;

        PoolRegionSidecar.Save(_regionSidecarPath, _cached);
        _lastSavedRegions = new List<(long baseAddr, long size)>(_cached);
    }

    /// <summary>Set equality, since the full scan's discovery order can differ from the saved order.</summary>
    private static bool RegionListsMatch(List<(long baseAddr, long size)> a, IReadOnlyList<(long baseAddr, long size)> b)
    {
        if (a.Count != b.Count) return false;
        var setA = new HashSet<(long baseAddr, long size)>(a);
        foreach (var r in b) if (!setA.Contains(r)) return false;
        return true;
    }
}
