using System.IO;

namespace LivingWeapon;

/// <summary>
/// Atomic sidecar save shared by KillTally.Save and LegendStore.SaveIfDirty. Writes a .tmp,
/// copies the current primary to .bak, then moves the .tmp over the primary. Throws on failure; callers handle errors.
/// Kept in one place so a diagnostic tap for the lost tally and legends (cause unfound) covers both files.
///
/// GunSlingerStore.Save does not use it, because its .bak holds the current generation, not the previous one.
/// </summary>
internal static class SidecarJson
{
    /// <summary>Leaves .bak holding the previous generation (absent on the first save).</summary>
    internal static void SaveAtomic(string path, string json)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        if (File.Exists(path)) File.Copy(path, path + ".bak", true);
        File.Move(tmp, path, true);
    }
}
