namespace LivingWeapon;

/// <summary>
/// The kill counter has no place on weapon cards when another mod or language replaced the item text.
/// The counter anchors on the flavor line this mod bakes, so walks that never find it mean that text is gone.
/// Reports once through the parts board door, in the log and the parts report with no pop up box, and disarms nothing.
/// </summary>
internal sealed partial class CardTextWatch
{
    internal const string Reason = "another mod's item text, or a game language other than English, left no place to draw the kill counter";

    private const string PlayerText =
        "The kill counter on weapon cards is hidden this session because another mod replaced the item descriptions, "
        + "or the game is not in English, so there is no place to draw it; kills, growth and every weapon power keep working, "
        + "and the counts come back once the descriptions are ours again.";

    private readonly DoorCell _door = new(DoorReading.Open());
    private bool _foundRegion;
    private bool _paintedSite;
    private bool _fired;

    internal DoorReading Door => _door.Value;
    internal int EmptyWalks { get; private set; }

    /// <summary>One finished walk. True on the single call that closes the door.</summary>
    internal bool Observe(int regionsFound, bool anyKillsSite, double secondsSinceArmed)
    {
        if (regionsFound > 0) _foundRegion = true; else EmptyWalks++;
        if (anyKillsSite) _paintedSite = true;
        if (_fired || !ShouldReport(EmptyWalks, _foundRegion, _paintedSite, secondsSinceArmed)) return false;
        _fired = true;
        _door.Set(DoorReading.Absent(Reason));
        ModLogger.Warn(LogVerb.Display, PlayerText + PartsCopy.Detail($"empty-walks={EmptyWalks} seconds={(int)secondsSinceArmed}"));
        return true;
    }
}
