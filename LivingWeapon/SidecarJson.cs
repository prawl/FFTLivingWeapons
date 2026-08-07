using System.IO;

namespace LivingWeapon;

/// <summary>
/// The atomic sidecar-file save chain KillTally.Save and LegendStore.SaveIfDirty share
/// (LW-153: the three lines were token-identical in both): write a .tmp beside the primary,
/// back the current primary up to .bak, move the .tmp over the primary. Throws on failure;
/// each caller owns its catch, its error message, and its dirty bookkeeping. One home on
/// purpose beyond the dedup: LW-28 (the one observed lost-tally-and-legends incident, cause
/// still unfound) suspects the save path of exactly these two files, so any diagnostic tap
/// for it lands HERE once and covers both.
///
/// NOT a consumer, by design: GunSlingerStore.Save, whose .bak deliberately holds the CURRENT
/// generation (a fresh post-move write, no prior-primary copy at all) so a single save cycle
/// leaves .bak valid; folding it in would change which generation its fallback Load sees.
/// </summary>
internal static class SidecarJson
{
    /// <summary>tmp -&gt; prior-copy-to-.bak -&gt; move. The .bak ends up holding the PREVIOUS
    /// generation (or is absent on the very first save).</summary>
    internal static void SaveAtomic(string path, string json)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        if (File.Exists(path)) File.Copy(path, path + ".bak", true);
        File.Move(tmp, path, true);
    }
}
