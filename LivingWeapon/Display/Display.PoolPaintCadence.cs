namespace LivingWeapon;

/// <summary>
/// Commit 1B: when MaybePoolPaint's full-region ScanPoolRegion pass should run, split out of
/// Display.PoolPaint.cs. The pass used to run on every Tick that reached the fall-through while
/// coverage stayed unlatched, 130 to 480ms per pass on a live tape. LW-262's cache partition
/// should make that state rare; this bounds it anyway.
/// </summary>
internal sealed partial class Display
{
    // _lastScannedGeneration is the PoolLocator.PublishGeneration last scanned (-1 so the first call
    // counts as fresh). _lastPoolScanMs is this method's own clock, separate from Display.cs's
    // _lastMaintenanceMs because MaintenanceDue is consuming and already spent once per Tick; only
    // the MaintenanceMs interval is shared.
    private int _lastScannedGeneration = -1;
    private long _lastPoolScanMs = -1;

    /// <summary>Count of ScanPoolRegion loop runs (not skipped by <see cref="ShouldRunFullPoolScan"/>).
    /// Test accessor, like _flightBudget in Display.Flight.cs; read totals are polluted by the
    /// whole-heap sweep fallback in Display.cs's Tick.</summary>
    internal int PoolScanPassesForTest;

    /// <summary>True when the full-region scan in MaybePoolPaint should run: the located region list
    /// is new since the last scan (PoolLocator.Restart.cs's PublishGeneration), or MaintenanceMs has
    /// elapsed. ReOfferDrainedRegions' single-region scans (Display.PoolDrain.cs) are not gated by
    /// this.</summary>
    private bool ShouldRunFullPoolScan()
    {
        int gen = _poolLocator.PublishGeneration;
        long now = _nowMs();
        // F1 fix: the generation branch must stamp _lastPoolScanMs too. Left at -1, the next call's
        // cadence check `now - (-1)` always passed and ran a second full scan right after each publish.
        if (gen != _lastScannedGeneration) { _lastScannedGeneration = gen; _lastPoolScanMs = now; return true; }
        if (now - _lastPoolScanMs < MaintenanceMs) return false;
        _lastPoolScanMs = now;
        return true;
    }
}
