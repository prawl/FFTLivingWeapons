namespace LivingWeapon;

/// <summary>
/// LW-261: PoolLocator's file-only observability, split out as its own seam -- the same reasoning
/// Display.PoolPaintLog.cs draws for MaybePoolPaint's own rate-limited lines: "how do we watch
/// this system without flooding the log" is a different concern from either PoolLocator.cs's own
/// cache-and-publish surface or PoolLocator.Restart.cs's restart/cadence state machine, worth
/// keeping separate on its own merits rather than only when a line count forces it. Every line
/// here is ModLogger.Debug (file-only, no console noise): the flight-recorder tap for the SAME
/// completion event lives in Display.Flight.cs's RecordLocateCompleteIfTapped instead, since only
/// Display holds a recorder.
/// </summary>
internal sealed partial class PoolLocator
{
    // LW-257 (item 7): Signatures.StuckEdge state for LogRegionsFound below -- a persistent
    // "found nothing" state (or a pool that never resolves) would otherwise log every single
    // completed scan forever once promoted out of #if LWDEV. Mirrors Display.PoolPaint.cs's own
    // _noPoolRegionLatched, kept separate since the two fire from different call sites.
    private bool _noneFoundLatched;

    /// <summary>The cheap periodic reverify's own timing line (Step's revalidate-cadence branch):
    /// how long AllCachedStillPool actually took, and whether the cache is still good.</summary>
    private void LogRevalidate(long elapsedMs, bool stillPool) =>
        ModLogger.Debug(LogVerb.Display, $"LW37 revalidate: {elapsedMs}ms, {_cached.Count} cached region(s), stillPool={stillPool}");

    /// <summary>Round 3 verify (C1): fires every ProgressLogEveryTicks Steps of a still-running
    /// scan -- pure progress visibility, not an anomaly claim (see that constant's own doc for
    /// why this file no longer tries to guess a "this is stuck" threshold).</summary>
    private void LogProgress(int ticks, long bytes) =>
        ModLogger.Debug(LogVerb.Display, $"LW37 locate: still scanning after {ticks} ticks ({bytes} bytes so far)");

    /// <summary>Fires on every real scan completion: the ticks/bytes/ms evidence the budget
    /// constant (LocateBudgetBytes) needs retuning from once real numbers exist. Never rate-
    /// limited, and NOT rare on every path (round 3 verify, C3(c), corrects an earlier version of
    /// this doc that said "at most one per Invalidate() window, or one per failed periodic
    /// revalidate" -- true for those two triggers, but silent on the third: a cold boot before
    /// the pool exists yet retries on the SAME RevalidateMs cadence forever, PoolLocator.Restart.
    /// cs's own "empty-retry" trigger, so this line legitimately fires about once a second for as
    /// long as that drought lasts. Still nothing like the old locateMs line's every-Tick spam --
    /// once a second, not once per 33ms tick -- but a reader should expect a steady trickle during
    /// a cold-boot drought, not silence.</summary>
    private void LogLocateComplete(PoolScan.StepResult result) =>
        ModLogger.Debug(LogVerb.Display,
            $"LW37 locate-complete: {result.Ticks} ticks, {result.Bytes} bytes, {result.ElapsedMs}ms -> {_cached.Count} named-pool region(s)");

    /// <summary>Publish's own summary line: how many named-pool regions this scan found, and
    /// where. Promoted from #if LWDEV to unconditional Debug (file-only, no console noise --
    /// ModLogger.Debug always writes livingweapon.log, LogLevel only gates the console mirror) so
    /// a RELEASE tape can answer "how many pool regions, and how big" without a dev rebuild.
    /// Rate-limited when nothing is found: StuckEdge logs that transition once instead of every
    /// completed scan a drought persists; a found-something result always logs.</summary>
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
