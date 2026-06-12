using System;
using System.IO;
using LivingWeapon.Configuration;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// Config round-trip: write a Config.json to a temp dir, load it via
/// Configurable&lt;Config&gt;.FromFile, and assert TreasureAlwaysOn survives the round-trip.
///
/// Invariants:
///   (1) Default Config has TreasureAlwaysOn == true.
///   (2) FromFile on a missing path creates a new Config with the default value.
///   (3) A Config.json written with TreasureAlwaysOn=false round-trips back as false.
///   (4) A Config.json written with TreasureAlwaysOn=true  round-trips back as true.
///   (5) FromFile on a corrupt JSON silently returns a default Config (no throw).
/// </summary>
public class ModConfigTests
{
    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "lw_cfg_" + Path.GetRandomFileName());
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void DefaultConfig_TreasureAlwaysOnIsTrue()
    {
        var c = new Config();
        Assert.True(c.TreasureAlwaysOn);
    }

    [Fact]
    public void FromFile_MissingPath_ReturnsDefaultTrue()
    {
        var dir  = TempDir();
        var path = Path.Combine(dir, "Config.json");
        var c    = Configurable<Config>.FromFile(path, "Test");
        Assert.True(c.TreasureAlwaysOn);
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
            // corrupt load falls back to default (true)
            Assert.True(c.TreasureAlwaysOn);
        });
        Assert.Null(ex);
    }
}
