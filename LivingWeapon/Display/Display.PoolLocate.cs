namespace LivingWeapon;

/// <summary>
/// LW-261: the call site Engine's own "pool-locate" tick lane (Engine.cs's BuildPhases row,
/// TickGates.Always, every tick) drives directly, independent of Display.Tick -- the fix for the
/// 7-to-10-second synchronous freeze PoolLocator.LocateAll used to cause on this same background
/// loop. Deliberately NOT reached only from Display.Tick or PaintCountsIfChanged (see PoolLocator.
/// cs's own class doc for why: the on-field path never even calls MaybePoolPaint, so a locate that
/// depended solely on Tick would stall for most of a battle) -- Display.PoolPaint.cs's
/// MaybePoolPaint also calls this same method on its own fall-through, so both call sites share
/// one completion-handling path and neither can miss the flight tap below.
/// </summary>
internal sealed partial class Display
{
    /// <summary>Step the resumable pool-region scan by one tick's worth of budget
    /// (PoolLocator.LocateBudgetBytes). A no-op most ticks -- nothing due, PoolLocator.Step's own
    /// doc -- except on the tick a scan actually finishes, when it fires the "locate-complete"
    /// flight tap (its own reserve, CoverageRecordBudget's own sibling; PoolLocator.Step already
    /// owns the matching file-only Debug evidence line). A pure no-op when poolPaint is off, so
    /// wiring the Engine phase costs nothing for a sweep-only build.</summary>
    internal void StepPoolLocate()
    {
        if (!_poolPaint) return;
        var completion = _poolLocator.Step(PoolLocator.LocateBudgetBytes, _nowMs());
        if (completion != null) RecordLocateCompleteIfTapped(completion.Value);
    }
}
