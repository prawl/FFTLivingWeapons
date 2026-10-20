namespace LivingWeapon;

/// <summary>Pays the corpses a fast victory screen cut short, except on a forced new game exit.</summary>
internal static class BattleExitSettle
{
    // The exit reset drops the toast queue, so the banner tick has to run here.
    internal static bool Run(KillTracker tracker, BannerToast toast, bool forced)
    {
        if (forced || !tracker.SettleCorpsesAtBattleEnd()) return false;
        toast.Tick(tallyChanged: true);
        return true;
    }
}
