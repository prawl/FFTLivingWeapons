using System.IO;
using Newtonsoft.Json.Linq;

namespace LivingWeapon;

/// <summary>
/// Deployment facts read from the files beside the DLL. The launch header's L1 line uses
/// <see cref="ReadVersion"/> (ModVersion from modDir/ModConfig.json). Read failures yield
/// "unknown" so the header still prints from a broken deploy folder.
/// </summary>
internal static class ModInfo
{
    /// <summary>The deployed mod version, or "unknown" if ModConfig.json is missing, unreadable or lacks ModVersion.</summary>
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
