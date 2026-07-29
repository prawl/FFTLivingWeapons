using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using LivingWeapon.Configuration;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// Config round-trip: write a Config.json to a temp dir, load it via
/// Configurable&lt;Config&gt;.FromFile, and assert TreasureAlwaysOn survives the round-trip.
///
/// Invariants:
///   (1) Default Config has TreasureAlwaysOn == false (opt-in via the config toggle).
///   (2) FromFile on a missing path creates a new Config with the default value (false).
///   (3) A Config.json written with TreasureAlwaysOn=false round-trips back as false.
///   (4) A Config.json written with TreasureAlwaysOn=true  round-trips back as true.
///   (5) FromFile on a corrupt JSON silently returns a default Config (no throw).
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
    public void DefaultConfig_TreasureAlwaysOnIsFalse()
    {
        var c = new Config();
        Assert.False(c.TreasureAlwaysOn);
    }

    [Fact]
    public void FromFile_MissingPath_ReturnsDefaultFalse()
    {
        var dir  = TempDir();
        var path = Path.Combine(dir, "Config.json");
        var c    = Configurable<Config>.FromFile(path, "Test");
        Assert.False(c.TreasureAlwaysOn);
    }

    [Fact]
    public void RoundTrip_FalseValue()
    {
        var dir  = TempDir();
        var path = Path.Combine(dir, "Config.json");

        // Write a false config
        var written = Configurable<Config>.FromFile(path, "Test");
        written.TreasureAlwaysOn = false;
        written.Save();

        // Load it back fresh
        var loaded = Configurable<Config>.FromFile(path, "Test");
        Assert.False(loaded.TreasureAlwaysOn);
    }

    [Fact]
    public void RoundTrip_TrueValue()
    {
        var dir  = TempDir();
        var path = Path.Combine(dir, "Config.json");

        var written = Configurable<Config>.FromFile(path, "Test");
        written.TreasureAlwaysOn = true;
        written.Save();

        var loaded = Configurable<Config>.FromFile(path, "Test");
        Assert.True(loaded.TreasureAlwaysOn);
    }

    [Fact]
    public void FromFile_CorruptJson_ReturnsDefaultNoThrow()
    {
        var dir  = TempDir();
        var path = Path.Combine(dir, "Config.json");
        File.WriteAllText(path, "{ this is not valid json !!!");

        var ex = Record.Exception(() =>
        {
            var c = Configurable<Config>.FromFile(path, "Test");
            // corrupt load falls back to default (false)
            Assert.False(c.TreasureAlwaysOn);
        });
        Assert.Null(ex);
    }

    // ---- LW-52: the player-facing config surface is exactly TreasureAlwaysOn ----
    // The BannerToasts, DevSeedKills, and VerboseLog toggles were removed from the launcher: toasts
    // are always on, dev-seeding is governed by the LWDEV compile flag, and console verbosity is
    // fixed at Info (the log FILE still records every line unconditionally). This reflection guard
    // fails if any of those properties reappears on Config, so the removal cannot silently regress.
    [Fact]
    public void ConfigSurface_IsExactlyTreasureAlwaysOn_LW52()
    {
        var declared = typeof(Config)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(p => p.CanRead && p.CanWrite)
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(new[] { "TreasureAlwaysOn" }, declared);
    }
}
