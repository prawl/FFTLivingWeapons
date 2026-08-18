namespace LivingWeapon;

/// <summary>
/// Commit 1B: WHEN MaybePoolPaint's full-region ScanPoolRegion pass should actually run, split
/// out of Display.PoolPaint.cs by the same precedent PoolLocator.cs drew against PoolLocator.
/// Restart.cs (that file's own class doc: "a genuinely different concern... not a split of one
/// state machine across two files"). The pass used to run on EVERY Tick that reached the
/// fall-through while coverage stayed unlatched, measured at 130 to 480ms per pass on a live
/// tape -- LW-262's cache partition is expected to make an unlatched steady state rare, but this
/// bounds it anyway rather than trusting that alone.
/// </summary>
internal sealed partial class Display
{
    // _lastScannedGeneration tracks the PoolLocator.PublishGeneration this method last actually
    // scanned against (-1 before the first scan, so the very first opportunity always counts as
    // fresh); _lastPoolScanMs is this method's OWN maintenance-cadence clock, deliberately
    // separate from Display.cs's _lastMaintenanceMs (MaintenanceDue is a consuming, side-effecting
    // check already spent once per Tick by the maintenance beat; sharing it here would either
    // double-consume it or require restructuring Tick's call order, neither of which this bound
    // needs -- it only has to share the SAME cadence NUMBER, MaintenanceMs, not the same field).
    private int _lastScannedGeneration = -1;
    private long _lastPoolScanMs = -1;

    /// <summary>How many times ScanPoolRegion's own loop actually ran (as opposed to being
    /// skipped by <see cref="ShouldRunFullPoolScan"/>). Internal test accessor, mirroring
    /// _flightBudget/_coverageBudget's own convention (Display.Flight.cs) -- a direct,
    /// unambiguous signal instead of inferring the count from IGameMemory read totals, which the
    /// whole-heap sweep fallback (Display.cs's Tick, taken whenever MaybePoolPaint returns false)
    /// would otherwise pollute.</summary>
    internal int PoolScanPassesForTest;

    /// <summary>True when the full-region ScanPoolRegion loop (Display.PoolPaint.cs's
    /// MaybePoolPaint) should actually run this call: either the located region list is
    /// GENUINELY NEW since the last scan (a fresh PoolLocator.Step completion, a
    /// revalidate-triggered rescan, or a straddled-invalidate republish --
    /// PoolLocator.Restart.cs's PublishGeneration), or the maintenance cadence (MaintenanceMs)
    /// has elapsed since the last scan. ReOfferDrainedRegions' own single-region ScanPoolRegion
    /// calls (Display.PoolDrain.cs) are a separate call site entirely and are never gated by
    /// this -- that path already runs on its own cadence (RunMaintenance's own beat) and already
    /// bounds its blast radius to the specific drained region(s), which is a genuine trigger in
    /// its own right, not the "every tick" cost this file's class doc describes.</summary>
    private bool ShouldRunFullPoolScan()
    {
        int gen = _poolLocator.PublishGeneration;
        long now = _nowMs();
        // F1 fix (verifier round): the generation branch used to return WITHOUT stamping
        // _lastPoolScanMs, leaving it at its -1 sentinel. On a real process clock (never near
        // zero), the very next call's cadence check `now - (-1)` is always >= MaintenanceMs, so
        // the tick immediately after EVERY publish ran a SECOND full-region scan -- exactly when
        // the region set is largest. Both branches must stamp the same clock.
        if (gen != _lastScannedGeneration) { _lastScannedGeneration = gen; _lastPoolScanMs = now; return true; }
        if (now - _lastPoolScanMs < MaintenanceMs) return false;
        _lastPoolScanMs = now;
        return true;
    }
}
