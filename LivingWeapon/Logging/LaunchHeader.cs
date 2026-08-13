namespace LivingWeapon;

/// <summary>
/// The launch header's two save-line composers (LW-22): the pure string halves of Engine's
/// L3/L4 lines, split out so their pluralization is unit-tested. The inline originals printed
/// "1 Marks" / "1 weapons" on singular counts; BattleSummary.Plural is the shared rule.
/// </summary>
internal static class LaunchHeader
{
    /// <summary>L3: what the DISK held, before any dev seed can inflate the counts.</summary>
    public static string ComposeTally(int totalKills, int weaponCount, string loadedFrom)
        => $"The kill tally holds {BattleSummary.Plural(totalKills, "lifetime kill")} across " +
           $"{BattleSummary.Plural(weaponCount, "weapon")} (kills.json, {loadedFrom}).";

    /// <summary>L4: the legends load summary.</summary>
    public static string ComposeLegends(int weaponCount, int totalMarks, string loadedFrom)
        => $"The legends hold deeds for {BattleSummary.Plural(weaponCount, "weapon")} and " +
           $"{BattleSummary.Plural(totalMarks, "Mark")} (legends.json, {loadedFrom}).";
}
