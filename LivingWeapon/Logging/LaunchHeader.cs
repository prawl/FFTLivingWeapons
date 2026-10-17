namespace LivingWeapon;

/// <summary>Composers for the L3/L4 launch header save lines, split from Engine so
/// pluralization is unit-tested (the inline versions printed "1 Marks"). See BattleSummary.Plural.</summary>
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
