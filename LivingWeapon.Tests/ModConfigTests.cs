using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using LivingWeapon.Configuration;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// Config loading, and the deliberate emptiness of the player-facing surface.
///
/// LW-52 removed the BannerToasts, DevSeedKills and VerboseLog toggles from the launcher so
/// players could not switch off designed behaviour, leaving TreasureAlwaysOn as the only
/// setting; LW-10 then removed Treasure Master itself (2026-08-14) and that setting went with
/// it. So the surface is now EMPTY, and that is a decision worth pinning rather than a gap:
/// every remaining behaviour keeps its compiled Tuning default on purpose.
///
/// Invariants:
///   (1) FromFile on a missing path returns a usable default Config and does not throw.
///   (2) FromFile on corrupt JSON returns a default Config and does not throw. Mod.cs depends
///       on exactly this: a config typo warns the player and startup continues.
///   (3) A saved config round-trips back through FromFile.
///   (4) Config declares NO read/write properties. Adding one is a product decision about what
///       players may switch off, so it should fail here and be argued for, not slip in.
///
/// LW-147: TempDir() directories are tracked and deleted in Dispose, not leaked.
/// </summary>
public class ModConfigTests : IDisposable
{
    private readonly List<TempDirs> _tempDirs = new();

    private string TempDir()
    {
        var t = TempDirs.Create("lw_cfg_");
        _tempDirs.Add(t);
        return t.Dir;
    }

    public void Dispose()
    {
        foreach (var t in _tempDirs) t.Dispose();
    }

    [Fact]
    public void FromFile_MissingPath_ReturnsDefaultAndDoesNotThrow()
    {
        var path = Path.Combine(TempDir(), "Config.json");
        var ex = Record.Exception(() => Assert.NotNull(Configurable<Config>.FromFile(path, "Test")));
        Assert.Null(ex);
    }

    [Fact]
    public void FromFile_CorruptJson_ReturnsDefaultNoThrow()
    {
        var path = Path.Combine(TempDir(), "Config.json");
        File.WriteAllText(path, "{ this is not valid json !!!");

        var ex = Record.Exception(() => Assert.NotNull(Configurable<Config>.FromFile(path, "Test")));
        Assert.Null(ex);
    }

    [Fact]
    public void SavedConfig_RoundTripsBackThroughFromFile()
    {
        var path = Path.Combine(TempDir(), "Config.json");

        Configurable<Config>.FromFile(path, "Test").Save();

        Assert.True(File.Exists(path));
        Assert.NotNull(Configurable<Config>.FromFile(path, "Test"));
    }

    // ---- The player-facing surface is empty ON PURPOSE (LW-52, then LW-10) ----
    // This reflection guard fails the moment any settable property appears on Config, including
    // the three LW-52 removed (BannerToasts, DevSeedKills, VerboseLog) coming back. It is not
    // saying "never add a setting"; it is saying a setting is a decision about what a player is
    // allowed to switch off, and that decision should be made in front of this test.
    [Fact]
    public void ConfigSurface_IsEmpty_LW52_LW10()
    {
        var declared = typeof(Config)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(p => p.CanRead && p.CanWrite)
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(Array.Empty<string>(), declared);
    }
}
