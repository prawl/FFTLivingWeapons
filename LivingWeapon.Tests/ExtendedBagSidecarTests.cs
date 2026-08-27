using System;
using System.Collections.Generic;
using System.IO;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>LW-348: the extended-inventory bag-count sidecar.</summary>
public class ExtendedBagSidecarTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lw_bag_" + Guid.NewGuid().ToString("N"));
    private string PathOf => Path.Combine(_dir, ExtendedBagSidecar.FileName);

    public ExtendedBagSidecarTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void A_missing_file_is_a_first_run_with_the_data_seed_as_the_boot_count()
    {
        var s = ExtendedBagSidecar.Load(PathOf);
        Assert.Empty(s.Counts);
        Assert.Equal("none (first run)", s.LoadedFrom);
        Assert.Equal(1, s.ResolveBootCount(261, seedCount: 1));
        Assert.Equal(0, s.ResolveBootCount(262, seedCount: 0));
    }

    [Fact]
    public void Update_persists_atomically_and_a_reload_replays_the_saved_counts_including_zero()
    {
        var s = ExtendedBagSidecar.Load(PathOf);
        s.Update(new Dictionary<int, int> { [261] = 3 });
        Assert.True(File.Exists(PathOf));
        s.Update(new Dictionary<int, int> { [261] = 0, [262] = 5 });
        Assert.True(File.Exists(PathOf + ".bak"));   // the SidecarJson chain: previous generation kept

        var back = ExtendedBagSidecar.Load(PathOf);
        Assert.Equal(PathOf, back.LoadedFrom);
        Assert.Equal(0, back.ResolveBootCount(261, seedCount: 1));   // an explicit 0 beats the seed
        Assert.Equal(5, back.ResolveBootCount(262, seedCount: 0));
        Assert.Equal(1, back.ResolveBootCount(263, seedCount: 1));   // no entry: the seed
    }

    [Fact]
    public void A_corrupt_or_foreign_file_loads_as_no_entries_never_throws()
    {
        File.WriteAllText(PathOf, "{ not json");
        var corrupt = ExtendedBagSidecar.Load(PathOf);
        Assert.Empty(corrupt.Counts);
        Assert.Equal("unreadable", corrupt.LoadedFrom);

        File.WriteAllText(PathOf, "{\"version\": 99, \"counts\": {\"261\": 4}}");
        var foreign = ExtendedBagSidecar.Load(PathOf);
        Assert.Empty(foreign.Counts);
        Assert.Equal(1, foreign.ResolveBootCount(261, 1));

        File.WriteAllText(PathOf, "{\"version\": 1, \"counts\": {\"261\": 4, \"abc\": 2, \"262\": 300, \"263\": -1}}");
        var partly = ExtendedBagSidecar.Load(PathOf);
        Assert.Equal(new Dictionary<int, int> { [261] = 4 }, partly.Counts);   // only well-formed byte-range entries survive
    }
}
