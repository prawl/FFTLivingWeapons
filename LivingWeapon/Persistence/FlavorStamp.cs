using System;
using System.IO;

namespace LivingWeapon;

/// <summary>
/// LW-134: stamps the RUNNING build's flavor ("dev"/"prod") into the update-safe save dir, so
/// BuildLinked's deploy guard (Resolve-DeployedFlavor, tools/pipeline.ps1) has a last-RUN truth to
/// fall back on when build_flavor.txt (BuildLinked's own last-DEPLOY marker) is missing or stale --
/// e.g. a production zip that was hand-extracted and never touched by BuildLinked, or a dev build
/// -Force-deployed over a prod install. The written value is derived from
/// <see cref="Tuning.BuildFlavor"/>, a COMPILED-IN const, so the stamp can't lie about the build
/// that wrote it the way a value read off disk could. Resolve-DeployedFlavor treats a torn or
/// garbage file as absent, so it fails closed on a bad read rather than trusting it.
/// </summary>
internal static class FlavorStamp
{
    /// <summary>File name inside the save dir; matches the $stampPath Resolve-DeployedFlavor is
    /// pointed at (SaveDir/run_flavor.txt, mirroring SaveLocation.ResolveSaveDir).</summary>
    public const string FileName = "run_flavor.txt";

    /// <summary>Maps the compiled <see cref="Tuning.BuildFlavor"/> string to the short token the
    /// deploy guard reads. Pure.</summary>
    internal static string ContentFor(string buildFlavor) => buildFlavor == "production" ? "prod" : "dev";

    /// <summary>
    /// Always overwrites the stamp with the CURRENT build's flavor. Fail-soft (never throws): this
    /// runs during Engine's constructor (see the call site in Engine.cs), and a ctor exception
    /// kills the whole mod (Mod.StartEngine's catch). A failed write just means the deploy guard
    /// falls back further down Resolve-DeployedFlavor's precedence.
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
