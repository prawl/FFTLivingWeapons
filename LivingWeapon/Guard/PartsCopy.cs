namespace LivingWeapon;

/// <summary>
/// The one voice of every paused part message. Order is what is paused and why, hedge, build sentence, what to do, drill, detail last.
/// </summary>
internal static class PartsCopy
{
    internal const string Hedge =
        "This usually means the game just updated, or another mod changed the same data.";
    internal const string WhatToDo =
        "What to do: check the Living Weapons mod page for a newer version; if you installed another mod recently, try a run without it; still stuck, email ptyrawl@gmail.com and attach livingweapon.log.";
    internal const string WhatToDoBox =
        "What to do: check the Living Weapons mod page for a newer version. If you installed another mod recently, try a run without it.\n\n"
        + "Still stuck? Email ptyrawl@gmail.com and attach livingweapon.log (Open Folder on the mod's page in Reloaded-II).";
    internal const string HooksWhatToDo =
        "What to do: enable the reloaded.sharedlib.hooks mod in Reloaded-II and launch the game again.";

    internal static string Detail(string diag) => $" (detail: {diag})";

    internal static string Disabled(string offBecause, string buildHedge, string drill, string diag)
        => $"{offBecause} {Hedge}{buildHedge} {WhatToDo}{drill}{Detail(diag)}";

    // For causes a game update cannot explain, such as bad shipped data or a loader error.
    internal static string DisabledPlain(string offBecause, string diag)
        => $"{offBecause} {WhatToDo}{Detail(diag)}";
}
