namespace LivingWeapon;

/// <summary>Decides when the kill counter has had its chance and still has nowhere to draw.</summary>
internal sealed partial class CardTextWatch
{
    internal const int EmptyWalksNeeded = 2;

    /// <summary>Twelve times the 10 seconds a healthy launch takes to draw its first counters.</summary>
    internal const double SecondsNeeded = 120.0;

    internal static bool ShouldReport(int emptyWalks, bool everFoundRegion, bool everPaintedSite, double secondsSinceArmed)
        => emptyWalks >= EmptyWalksNeeded && !everFoundRegion && !everPaintedSite && secondsSinceArmed >= SecondsNeeded;
}
