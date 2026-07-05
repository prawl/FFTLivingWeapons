using System.IO;
using Newtonsoft.Json.Linq;

namespace LivingWeapon;

/// <summary>
/// Deployment facts about the running mod, read fail-soft from the files deployed beside the
/// DLL. The launch header's L1 line reads <see cref="ReadVersion"/> ("ModVersion" from
/// modDir/ModConfig.json, the Reloaded manifest that ships with every install, dev deploy and
/// Publish zip alike). Any read/parse failure yields "unknown", never a throw: the header must
/// print even from a broken deploy folder.
/// </summary>
internal static class ModInfo
{
    /// <summary>The deployed mod's version string, or "unknown" when ModConfig.json is missing,
    /// unreadable, or carries no ModVersion.</summary>
    public static string ReadVersion(string modDir)
    {
        try
        {
            string path = Path.Combine(modDir, "ModConfig.json");
            if (!File.Exists(path)) return "unknown";
            var version = JObject.Parse(File.ReadAllText(path))["ModVersion"]?.ToString();
            return string.IsNullOrWhiteSpace(version) ? "unknown" : version;
        }
        catch { return "unknown"; }
    }
}
