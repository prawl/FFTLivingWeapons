using System;
using System.Diagnostics;
using System.IO;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-148: LivingWeapon/Persistence/SaveLocation.cs's ResolveSaveDir (C#) and tools/pipeline.ps1's
/// Resolve-SaveDir (PowerShell, extracted off BuildLinked.ps1's own inline copy in this same
/// change) are one derivation deliberately kept in two languages -- BuildLinked's dev-vs-prod
/// deploy guard needs to know where the player's real save lives WITHOUT loading the DLL. Both
/// sides' comments already claimed they agree; nothing actually crossed languages to prove it.
/// This does: build a synthetic &lt;root&gt;\Mods\&lt;anything&gt; temp tree, run the real
/// SaveLocation constructor (its ctor resolves the dir) and the real PowerShell Resolve-SaveDir
/// over the SAME tree, and assert identical results. Shells powershell.exe the same way
/// DeployGuardTests does, dot-sourcing the real tools/pipeline.ps1 rather than re-implementing its
/// logic in C#. Repo-root resolution mirrors DeployGuardTests/PipelineManifestContractTests.
/// </summary>
public class SaveDirMirrorTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "docs", "TODO.md")) &&
                Directory.Exists(Path.Combine(dir.FullName, "LivingWeapon")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("repo root (docs/TODO.md + LivingWeapon/) not found above the test bin dir");
    }

    /// <summary>Single-quotes a path for embedding into the PowerShell command literal, doubling
    /// any embedded single quote, so a path can never break out of the quoting.</summary>
    private static string PsQuote(string s) => "'" + s.Replace("'", "''") + "'";

    /// <summary>Dot-sources the repo's real tools/pipeline.ps1 and calls the real Resolve-SaveDir
    /// with the scenario's paths, returning its printed result.</summary>
    private static string RunResolveSaveDir(string modsDir, string modId)
    {
        string pipelinePath = Path.Combine(RepoRoot(), "tools", "pipeline.ps1");
        string command =
            $". {PsQuote(pipelinePath)}; " +
            $"Write-Output (Resolve-SaveDir {PsQuote(modsDir)} {PsQuote(modId)})";

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(command);

        using var proc = Process.Start(psi)!;
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        bool exited = proc.WaitForExit(60000);
        Assert.True(exited, "powershell.exe did not exit within the timeout");
        Assert.True(proc.ExitCode == 0, $"powershell.exe exited {proc.ExitCode}. stdout: [{stdout}] stderr: [{stderr}]");

        return stdout.Trim();
    }

    [Fact]
    public void Resolve_SaveDir_matches_SaveLocations_own_ctor_over_the_same_tree()
    {
        using var temp = TempDirs.Create("lw_savedirmirror_");
        string modsDir = Path.Combine(temp.Dir, "Mods");
        // Deliberately a DIFFERENT folder name than Mod.ModId: SaveLocation.ResolveSaveDir's own
        // doc comment says it keys on the manifest ModId constant, never modDir's own folder name.
        // Using a mismatched name here proves that claim on BOTH sides, rather than merely not
        // contradicting it (a folder that happened to already match ModId would pass even if
        // either side quietly fell back to the folder name).
        string destDir = Path.Combine(modsDir, "some_other_folder_name_entirely");
        Directory.CreateDirectory(destDir);

        var saveLocation = new SaveLocation(destDir);
        string csharpResult = saveLocation.SaveDir;

        string psResult = RunResolveSaveDir(modsDir, Mod.ModId);

        Assert.Equal(csharpResult, psResult);
    }
}
