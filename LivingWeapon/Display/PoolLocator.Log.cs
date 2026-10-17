namespace LivingWeapon;

/// <summary>
/// PoolLocator's file-only logging, split out like Display.PoolPaintLog.cs. All lines are
/// ModLogger.Debug; the flight-recorder tap for locate completion is RecordLocateCompleteIfTapped
/// in Display.Flight.cs, since only Display holds a recorder.
/// </summary>
internal sealed partial class PoolLocator
{
    // StuckEdge state for LogRegionsFound, so a persistent "found nothing" scan
    // logs once. Separate from Display.PoolPaint.cs's _noPoolRegionLatched, which fires elsewhere.
    private bool _noneFoundLatched;

    /// <summary>Timing line for the periodic revalidate. How long AllCachedStillPool took and the result.</summary>
    private void LogRevalidate(long elapsedMs, bool stillPool) =>
        ModLogger.Debug(LogVerb.Display, $"LW37 revalidate: {elapsedMs}ms, {_cached.Count} cached region(s), stillPool={stillPool}");

    /// <summary>Progress line every ProgressLogEveryTicks Steps of a running scan.</summary>
    private void LogProgress(int ticks, long bytes) =>
        ModLogger.Debug(LogVerb.Display, $"LW37 locate: still scanning after {ticks} ticks ({bytes} bytes so far)");

    /// <summary>Logged on every scan completion, for retuning LocateBudgetInBattle and
    /// LocateBudgetOutOfBattle. A cold boot before the pool exists retries on the RevalidateMs
    /// cadence (PoolLocator.Restart.cs "empty-retry"), so expect about one line a second then.</summary>
    private void LogLocateComplete(PoolScan.StepResult result) =>
        ModLogger.Debug(LogVerb.Display,
            $"LW37 locate-complete: {result.Ticks} ticks, {result.Bytes} bytes, {result.ElapsedMs}ms -> {_cached.Count} named-pool region(s)");

    /// <summary>Summary of the named-pool regions a scan found. Unconditional Debug (file only),
    /// so a release log shows region counts and sizes. An empty result logs once per StuckEdge
    /// transition, a non-empty one always.</summary>
    private void LogRegionsFound()
    {
        bool noneFound = _cached.Count == 0;
        bool edge = Signatures.StuckEdge(ref _noneFoundLatched, noneFound);
        if (!noneFound || edge)
        {
            var parts = _cached.ConvertAll(r => "0x" + r.baseAddr.ToString("X") + ":" + r.size);
            ModLogger.Debug(LogVerb.Display, $"LW37 locate: {_cached.Count} named-pool region(s) at [{string.Join(", ", parts)}]");
        }
    }
}
