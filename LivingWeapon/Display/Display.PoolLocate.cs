namespace LivingWeapon;

/// <summary>
/// Retune round (R1): Engine's own "pool-locate" tick lane (Engine.cs's BuildPhases row,
/// TickGates.Always, every tick) is now the SOLE production driver of the resumable scan.
/// Display.PoolPaint.cs's MaybePoolPaint used to also call this method on its own fall-through
/// (so a bare Display driven only by Tick() -- every pre-retune unit test, no Engine wired -- could
/// still make progress); the live tape showed that double call site paying the ~45ms region
/// snapshot roughly TWICE a tick (PoolScan.cs's own SnapshotRefreshMs doc has the arithmetic), so
/// MaybePoolPaint now only ever READS PoolLocator.CachedRegions and never steps the scan itself.
/// Tests that drive a bare Display therefore call this method directly in their own drive loops
/// (CardFixtures.TickWithPoolLocate is the shared helper) instead of relying on Tick() alone.
/// </summary>
internal sealed partial class Display
{
    /// <summary>Step the resumable pool-region scan by one tick's worth of budget --
    /// PoolLocator.LocateBudgetInBattle or LocateBudgetOutOfBattle depending on <paramref
    /// name="inBattle"/> (R3: mirrors Display's own BudgetInBattle/BudgetOutOfBattle split, and
    /// the gunslinger phase's own precedent for threading s.NowIn through a TickPhase, Engine.cs).
    /// A no-op most ticks -- nothing due, PoolLocator.Step's own doc -- except on the tick a scan
    /// actually finishes, when it fires the "locate-complete" flight tap (its own reserve,
    /// CoverageRecordBudget's own sibling; PoolLocator.Step already owns the matching file-only
    /// Debug evidence line). A pure no-op when poolPaint is off, so wiring the Engine phase costs
    /// nothing for a sweep-only build.</summary>
    internal void StepPoolLocate(bool inBattle)
    {
        if (!_poolPaint) return;
        long budget = inBattle ? PoolLocator.LocateBudgetInBattle : PoolLocator.LocateBudgetOutOfBattle;
        var completion = _poolLocator.Step(budget, _nowMs());
        if (completion != null) RecordLocateCompleteIfTapped(completion.Value);
    }
}
