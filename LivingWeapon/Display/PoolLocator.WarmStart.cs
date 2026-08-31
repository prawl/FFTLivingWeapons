using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// LW-324: warm-starting the region cache from a persisted sidecar (PoolRegionSidecar,
/// pool_regions.json) across process launches, and keeping that sidecar in sync as the real
/// locator republishes -- a genuinely different concern from PoolLocator.cs's own "what the
/// cache holds" and PoolLocator.Restart.cs's "when to (re)scan": this file is "how the cache is
/// seeded/persisted across launches", the same split PoolLocator.Restart.cs's own class doc
/// draws for timing vs. trust vs. plain data.
///
/// SeedFromSidecar is called exactly once, from the very first <see cref="Step"/> this
/// locator's lifetime ever sees, BEFORE that call begins the cold scan (PoolLocator.Restart.cs).
/// It never marks the locate complete: _hasScannedOnce/_restartPending/_stale are untouched
/// here, so the ordinary cold scan Step is about to begin runs to completion and republishes
/// exactly as it would with no sidecar at all -- a drifted or missing persisted region still
/// arrives on schedule (CoversAllMeta latches only on true coverage); this method only ever
/// ADDS early paint via a fresh PublishGeneration, never suppresses discovery.
///
/// MaybeSaveSidecar is called from Publish (PoolLocator.Restart.cs) -- the only place a fresh,
/// TRUSTED region list is ever written into _cached -- and is dirty-checked against the last
/// known on-disk content (whatever SeedFromSidecar loaded, or whatever this file itself last
/// wrote) so an Invalidate()-triggered rescan that finds the SAME regions again (the common
/// case: nothing moved) never re-writes the file.
/// </summary>
internal sealed partial class PoolLocator
{
    private readonly string? _regionSidecarPath;
    private bool _warmStartAttempted;

    // What is (or was just made to be) on disk right now, for MaybeSaveSidecar's dirty check.
    // Null until either a warm-start load or a save has actually happened this session.
    private List<(long baseAddr, long size)>? _lastSavedRegions;

    /// <summary>Loads the sidecar (if wired at all -- null path means every pre-existing caller
    /// and every test that does not opt in, byte-identical to before this arc), re-verifies each
    /// persisted region against LIVE memory via <see cref="ScanRegion"/> (the exact cheap
    /// per-region reverify <see cref="AllCachedStillPool"/> already uses), and seeds the
    /// survivors into _cached -- mirroring <see cref="SeedForTest"/>'s own shape. Logs the
    /// adoption line unconditionally whenever a sidecar is wired, N=0 included (the ledger row
    /// [kills-pool-region-recurrence] owes that measurement even on a drought or a first-ever
    /// launch with nothing persisted yet).</summary>
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

        // THE TRAP GUARD: without this, Step's own "not running" branch (immediately below, same
        // call) would see _cached newly non-empty and take the "already located -- just
        // revalidate" path (AllCachedStillPool, which trivially succeeds since ScanRegion just
        // verified the very same survivors), skipping the real Regions() walk until the
        // RevalidateMs cadence next fires -- exactly the "seed suppresses the ordinary full
        // locate" bug this whole design is built to avoid (this class's own doc). Mirrors
        // Invalidate()'s own _restartPending=true, minus its _pendingTrigger overwrite:
        // _pendingTrigger is left untouched here, so a genuinely first-ever scan still reports
        // Trigger="first", not "invalidate". Set unconditionally (even when nothing survived
        // reverify), so the empty/first-ever case takes the exact same forced-begin path instead
        // of relying on _cached happening to already read as empty.
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

    /// <summary>Set-equality, not list-equality: the full scan's own discovery order need not
    /// match whatever order the sidecar (or a prior save this session) happened to hold the same
    /// regions in.</summary>
    private static bool RegionListsMatch(List<(long baseAddr, long size)> a, IReadOnlyList<(long baseAddr, long size)> b)
    {
        if (a.Count != b.Count) return false;
        var setA = new HashSet<(long baseAddr, long size)>(a);
        foreach (var r in b) if (!setA.Contains(r)) return false;
        return true;
    }
}
