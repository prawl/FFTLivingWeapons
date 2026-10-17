namespace LivingWeapon;

/// <summary>
/// Retune round. Engine's "pool-locate" tick lane (BuildPhases, TickGates.Always) is the only
/// driver of the resumable scan. MaybePoolPaint (Display.PoolPaint.cs) used to step it too, which paid
/// the ~45ms region snapshot twice a tick (see PoolScan.SnapshotRefreshMs), so it now only reads
/// PoolLocator.CachedRegions. Tests driving a bare Display call this directly
/// (CardFixtures.TickWithPoolLocate).
/// </summary>
internal sealed partial class Display
{
    /// <summary>Steps the pool-region scan by one tick's budget (PoolLocator.LocateBudgetInBattle or
    /// LocateBudgetOutOfBattle). Fires the "locate-complete" flight tap on the tick a scan finishes.
    /// No-op when poolPaint is off.</summary>
    internal void StepPoolLocate(bool inBattle)
    {
        if (!_poolPaint) return;
        long budget = inBattle ? PoolLocator.LocateBudgetInBattle : PoolLocator.LocateBudgetOutOfBattle;
        var completion = _poolLocator.Step(budget, _nowMs());
        if (completion != null) RecordLocateCompleteIfTapped(completion.Value);
    }
}
