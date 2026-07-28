using System.IO;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-134: FlavorStamp writes run_flavor.txt into the save dir so BuildLinked's deploy guard
/// (Resolve-DeployedFlavor, tools/pipeline.ps1) has a last-RUN flavour truth even when
/// build_flavor.txt (BuildLinked's own last-DEPLOY marker) is missing or stale -- e.g. a
/// hand-extracted production zip, which never went through BuildLinked at all. Mirrors
/// SaveLocationTests' unique-temp-ModDir idiom so parallel runs never collide.
/// </summary>
public class FlavorStampTests
{
    private static string TempRoot()
    {
        var d = Path.Combine(Path.GetTempPath(), "lw_flavorstamp_" + Path.GetRandomFileName());
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>Builds root/Mods/&lt;ModId&gt;, the same two-levels-under-the-Reloaded-root shape
    /// SaveLocationTests uses so ResolveSaveDir resolves inside the temp tree, not the real
    /// Reloaded dir.</summary>
    private static string ModDirIn(string root)
    {
        var dir = Path.Combine(root, "Mods", Mod.ModId);
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void ContentFor_production_maps_to_prod()
    {
        Assert.Equal("prod", FlavorStamp.ContentFor("production"));
    }

    [Fact]
    public void ContentFor_development_maps_to_dev()
    {
        Assert.Equal("dev", FlavorStamp.ContentFor("development"));
    }

    [Fact]
    public void Write_stamps_the_run_flavor_file_into_the_save_dir()
    {
        var root = TempRoot();
        var modDir = ModDirIn(root);
        var save = new SaveLocation(modDir);

        FlavorStamp.Write(save);

        string path = save.PathFor(FlavorStamp.FileName);
        Assert.True(File.Exists(path));
        Assert.Equal(FlavorStamp.ContentFor(Tuning.BuildFlavor), File.ReadAllText(path));
    }

    [Fact]
    public void Write_overwrites_a_stale_stamp()
    {
        var root = TempRoot();
        var modDir = ModDirIn(root);
        var save = new SaveLocation(modDir);
        string path = save.PathFor(FlavorStamp.FileName);
        string stale = FlavorStamp.ContentFor(Tuning.BuildFlavor) == "prod" ? "dev" : "prod";
        File.WriteAllText(path, stale);

        FlavorStamp.Write(save);

        Assert.Equal(FlavorStamp.ContentFor(Tuning.BuildFlavor), File.ReadAllText(path));
    }

    [Fact]
    public void Write_never_throws_when_the_save_dir_is_unusable()
    {
        var root = TempRoot();
        var modDir = ModDirIn(root);
        var save = new SaveLocation(modDir);
        // Sabotage: delete the resolved SaveDir, then create a FILE at that exact path, so
        // File.WriteAllText into SaveDir/run_flavor.txt must fail -- its "directory" is a file.
        Directory.Delete(save.SaveDir, recursive: true);
        File.WriteAllText(save.SaveDir, "not a directory");

        var ex = Record.Exception(() => FlavorStamp.Write(save));

        Assert.Null(ex);
    }
}
