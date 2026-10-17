using System;
using System.IO;

namespace LivingWeapon;

/// <summary>
/// Stamps the build flavor ("dev" or "prod") into the update-safe save dir.
/// BuildLinked's deploy guard (Resolve-DeployedFlavor, tools/pipeline.ps1) reads it as a last-run record
/// when build_flavor.txt is missing or stale (a hand-extracted prod zip, or a dev build -Force-deployed over prod).
/// The value comes from the compiled-in <see cref="Tuning.BuildFlavor"/>; a torn or garbage file reads as absent.
/// </summary>
internal static class FlavorStamp
{
    /// <summary>File name in the save dir; the $stampPath Resolve-DeployedFlavor reads (as in SaveLocation.ResolveSaveDir).</summary>
    public const string FileName = "run_flavor.txt";

    /// <summary>Maps the compiled build flavor to the short token the deploy guard reads.</summary>
    internal static string ContentFor(string buildFlavor) => buildFlavor == "production" ? "prod" : "dev";

    /// <summary>
    /// Overwrites the stamp with the current build's flavor. Never throws. It runs in Engine's constructor
    /// and a ctor exception kills the mod (Mod.StartEngine's catch).
    /// </summary>
    internal static void Write(SaveLocation save)
    {
        try
        {
            string content = ContentFor(Tuning.BuildFlavor);
            File.WriteAllText(save.PathFor(FileName), content);
            ModLogger.Debug(LogVerb.Save, $"Stamped {FileName} ({content}) for the deploy guard.");
        }
        catch (Exception ex)
        {
            ModLogger.Error(LogVerb.Save, $"Failed to stamp {FileName}: {ex.Message}");
        }
    }
}
