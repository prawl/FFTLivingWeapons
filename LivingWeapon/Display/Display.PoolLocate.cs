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
    /// <summary>Searches a slice of memory for the card text each tick, smaller in battle so play never stutters. Each
    /// finished search feeds the flight tap and the card text watch; does nothing when poolPaint is off.</summary>
    internal void StepPoolLocate(bool inBattle)
    {
        if (!_poolPaint) return;
        long budget = inBattle ? PoolLocator.LocateBudgetInBattle : PoolLocator.LocateBudgetOutOfBattle;
        var completion = _poolLocator.Step(budget, _nowMs());
        if (completion != null)
        {
            RecordLocateCompleteIfTapped(completion.Value);
            WatchCardText(completion.Value);
        }
    }

    private readonly CardTextWatch _cardText = new();

    /// <summary>The kill counter's door for the parts board, Open until the watch finds no place to draw.</summary>
    internal DoorReading CardTextDoor => _cardText.Door;

    /// <summary>Tells the player when no card text gave the counter a place to live. A walk that found
    /// no region is the only one that can need the painted-site check, since a found region latches on its own.</summary>
    private void WatchCardText(PoolLocator.LocateCompletion c)
    {
        int regions = System.Math.Max(c.Regions, _poolLocator.CachedRegions.Count);
        bool anySite = regions == 0 && AnyKillsSite();
        double seconds = _firstTickMs < 0 ? 0.0 : (_nowMs() - _firstTickMs) / 1000.0;
        if (_cardText.Observe(regions, anySite, seconds))
            _recorder?.Invoke("card", $"card-text hidden empty-walks={_cardText.EmptyWalks} seconds={(int)seconds}");
    }

    private bool AnyKillsSite()
    {
        foreach (var s in _sites.Snapshot()) if (s.IsKills) return true;
        return false;
    }
}
