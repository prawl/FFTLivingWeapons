using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-368 round 2 / round 2b: <see cref="ListRelocation"/>'s 55-field install/restore transaction
/// and the hidden-writer tripwire, over the fakes. T1 is load-bearing and deliberately never
/// calls <see cref="ListRelocation.NewField"/>/<see cref="ListRelocation.Encode"/> for its
/// expected values -- it re-derives them from the raw D6 formulas so a bug shared between
/// production and the test cannot hide. Round 2b added ten interior sites (the new-game
/// starting-inventory seed the owner's live pass caught missing) and the tripwire's list-proper
/// window (it must not watch the live state dword at old-block +0x108).
/// </summary>
public class ListRelocationTests
{
    // A "recognizable pattern", not the real game bytes: distinguishable from zero-fill and from
    // each other so a copy landing at the wrong offset (or the wrong list) fails loudly.
    private static byte[] CountPattern() => Enumerable.Range(0, ListRelocation.BlockBytes).Select(i => (byte)(i + 1)).ToArray();
    private static byte[] SiblingPattern() => Enumerable.Range(0, ListRelocation.BlockBytes).Select(i => (byte)(0xA0 + i)).ToArray();

    /// <summary>Every one of the 55 fields at its vanilla value, plus the two old blocks --
    /// exactly what a vanilla 1.5.2 process image reads before this mod ever touches it.</summary>
    private static FakeCodePatcher SeedVanilla()
    {
        var f = new FakeCodePatcher();
        foreach (var site in ListRelocation.Sites) f.Seed(site.Addr, BitConverter.GetBytes(site.Vanilla));
        f.Seed(Offsets.BagCountArray, CountPattern());
        f.Seed(Offsets.SiblingListArray, SiblingPattern());
        return f;
    }

    private static bool Overlaps(long a1, int len1, long a2, int len2) => a1 < a2 + len2 && a2 < a1 + len1;

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "docs", "TODO.md")) && Directory.Exists(Path.Combine(dir.FullName, "LivingWeapon")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("repo root (docs/TODO.md + LivingWeapon/) not found above the test bin dir");
    }

    [Fact]
    public void Install_rewrites_every_field_to_reach_the_page()
    {
        var f = SeedVanilla();
        var alloc = new FakeNearAllocator();
        var rel = new ListRelocation();

        string? why = rel.Install(f, alloc);

        Assert.Null(why);
        Assert.True(rel.Installed);
        long page = alloc.Requests[0].Got;   // page = the allocator's first grant
        Assert.Equal(page, rel.PageAddr);
        foreach (var site in ListRelocation.Sites)
        {
            long off = site.List == ListRelocation.ListId.Count ? 0 : 0x400;
            int expected = site.Kind == ListRelocation.SiteKind.RipLea
                ? (int)(page + off + site.Off - (site.Addr + 4 + site.Trail))
                : (int)(page + off + site.Off - Offsets.ModuleBase);
            Assert.Equal(BitConverter.GetBytes(expected), f.Read(site.Addr, 4));
        }
        Assert.Equal(CountPattern(), f.Read(page, ListRelocation.BlockBytes));
        Assert.Equal(SiblingPattern(), f.Read(page + 0x400, ListRelocation.BlockBytes));
        Assert.Equal(page, rel.CountBase);
        Assert.Equal(page + 0x400, rel.SiblingBase);
    }

    [Fact]
    public void Site_table_is_self_consistent()
    {
        var sites = ListRelocation.Sites;
        Assert.Equal(55, sites.Length);
        Assert.Equal(sites.Length, sites.Select(s => s.Addr).Distinct().Count());
        var sorted = sites.Select(s => s.Addr).OrderBy(a => a).ToList();
        for (int i = 1; i < sorted.Count; i++)
            Assert.True(sorted[i] - sorted[i - 1] >= 4, $"0x{sorted[i - 1]:X} and 0x{sorted[i]:X} are within 4 bytes of each other");
        Assert.Equal(34, sites.Count(s => s.Kind == ListRelocation.SiteKind.RipLea));
        Assert.Equal(21, sites.Count(s => s.Kind == ListRelocation.SiteKind.ImageRelative));
        Assert.Equal(47, sites.Count(s => s.List == ListRelocation.ListId.Count));
        Assert.Equal(8, sites.Count(s => s.List == ListRelocation.ListId.Sibling));
        foreach (var site in sites)
        {
            long oldListBase = site.List == ListRelocation.ListId.Count ? Offsets.BagCountArray : Offsets.SiblingListArray;
            int expected = site.Kind == ListRelocation.SiteKind.RipLea
                ? (int)(oldListBase + site.Off - (site.Addr + 4 + site.Trail))
                : (int)(oldListBase + site.Off - Offsets.ModuleBase);
            Assert.Equal(expected, site.Vanilla);
        }
    }

    [Fact]
    public void Site_table_matches_the_probe_record()
    {
        string path = Path.Combine(RepoRoot(), "tools", "probes", "lw368_count_list_relocate_undo.json.done");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var rows = doc.RootElement.GetProperty("sites").EnumerateArray().ToList();
        var byAddr = ListRelocation.Sites.ToDictionary(s => s.Addr);
        var probeAddrs = new HashSet<long>();
        foreach (var row in rows)
        {
            long addr = row.GetProperty("addr").GetInt64();
            probeAddrs.Add(addr);
            Assert.True(byAddr.TryGetValue(addr, out var site), $"no transcribed site for probe address 0x{addr:X}");
            int expectedVanilla = BitConverter.ToInt32(Convert.FromHexString(row.GetProperty("old").GetString()!), 0);
            Assert.Equal(expectedVanilla, site.Vanilla);
            var expectedKind = row.GetProperty("kind").GetString() == "rip" ? ListRelocation.SiteKind.RipLea : ListRelocation.SiteKind.ImageRelative;
            Assert.Equal(expectedKind, site.Kind);
            var expectedList = row.GetProperty("list").GetString() == "count" ? ListRelocation.ListId.Count : ListRelocation.ListId.Sibling;
            Assert.Equal(expectedList, site.List);
            Assert.Equal(0, site.Trail);
            Assert.Equal(0, site.Off);
        }

        // Round 2b (LW-368): the ten interior new-game-seed sites are the only rows the probe
        // record does not carry -- they were found by the owner's live pass AFTER the probe ran,
        // never by the sweep the probe recorded. Each one targets a nonzero interior offset.
        var extra = ListRelocation.Sites.Where(s => !probeAddrs.Contains(s.Addr)).ToList();
        Assert.Equal(10, extra.Count);
        foreach (var site in extra)
            Assert.True(site.Off > 0, $"0x{site.Addr:X} is absent from the probe record but has Off == 0");
    }

    [Fact]
    public void The_new_game_seed_sites_target_the_documented_ids()
    {
        var expected = new HashSet<(int Off, int Trail)>
        {
            (0xF0, 4), (0xF4, 4), (0xF8, 4), (0xFC, 2),
            (0x4D, 1), (0x80, 1), (0x90, 1), (0xAC, 1), (0x33, 1), (0x3B, 1),
        };
        var actual = ListRelocation.Sites.Where(s => s.Off > 0).Select(s => (s.Off, s.Trail)).ToHashSet();
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void A_field_that_is_not_vanilla_refuses_with_nothing_written()
    {
        var f = SeedVanilla();
        var site = ListRelocation.Sites[20];
        f.Seed(site.Addr, BitConverter.GetBytes(site.Vanilla + 1));   // moved or redirected already

        var rel = new ListRelocation();
        string? why = rel.Install(f, new FakeNearAllocator());

        Assert.NotNull(why);
        Assert.Contains($"0x{site.Addr:X}", why);
        Assert.Empty(f.Writes);
        Assert.False(rel.Installed);
    }

    [Fact]
    public void A_refused_write_rolls_back_byte_identically()
    {
        var f = SeedVanilla();
        var alloc = new FakeNearAllocator();
        var before = new Dictionary<long, byte>(f.Bytes);
        var site20 = ListRelocation.Sites[19];   // the 20th field
        f.RefuseWritesAt.Add(site20.Addr);
        var rel = new ListRelocation();

        string? why = rel.Install(f, alloc);

        Assert.NotNull(why);
        Assert.False(rel.Installed);
        Assert.Equal(Offsets.BagCountArray, rel.CountBase);
        long page = alloc.Requests[0].Got;
        foreach (var kv in f.Bytes)
        {
            if (kv.Key >= page && kv.Key < page + ListRelocation.PageSize) continue;   // the leaked page, not a "field"
            Assert.True(before.TryGetValue(kv.Key, out var b) && b == kv.Value, $"0x{kv.Key:X} was not restored to its pre-Install value");
        }
    }

    [Fact]
    public void Restore_puts_every_field_back_byte_identically_and_is_idempotent()
    {
        var f = SeedVanilla();
        var rel = new ListRelocation();
        Assert.Null(rel.Install(f, new FakeNearAllocator()));

        Assert.True(rel.Restore(f));

        foreach (var site in ListRelocation.Sites)
            Assert.Equal(BitConverter.GetBytes(site.Vanilla), f.Read(site.Addr, 4));
        Assert.False(rel.Installed);
        Assert.Equal(Offsets.BagCountArray, rel.CountBase);
        Assert.Equal(Offsets.SiblingListArray, rel.SiblingBase);

        int writesBefore = f.Writes.Count;
        Assert.True(rel.Restore(f));   // idempotent: not installed, nothing more to do
        Assert.Equal(writesBefore, f.Writes.Count);
    }

    [Fact]
    public void Write_order_and_scope()
    {
        var f = SeedVanilla();
        var alloc = new FakeNearAllocator();
        var rel = new ListRelocation();

        Assert.Null(rel.Install(f, alloc));

        long page = alloc.Requests[0].Got;
        Assert.Equal(page, f.Writes[0].Addr);
        Assert.Equal(ListRelocation.PageSize, f.Writes[0].Data.Length);          // the zero-fill
        Assert.Equal(page, f.Writes[1].Addr);
        Assert.Equal(ListRelocation.BlockBytes, f.Writes[1].Data.Length);        // the count-list copy
        Assert.Equal(page + 0x400, f.Writes[2].Addr);
        Assert.Equal(ListRelocation.BlockBytes, f.Writes[2].Data.Length);        // the sibling-list copy

        var siteAddrs = ListRelocation.Sites.Select(s => s.Addr).ToHashSet();
        for (int i = 3; i < f.Writes.Count; i++)
            Assert.Contains(f.Writes[i].Addr, siteAddrs);   // every write after the copies is a field write
        foreach (var (addr, data) in f.Writes)
            Assert.True((addr >= page && addr + data.Length <= page + ListRelocation.PageSize) || siteAddrs.Contains(addr),
                $"write at 0x{addr:X} is outside the page and is not a known field");
    }

    [Fact]
    public void No_field_overlaps_another_patch()
    {
        var bootAddrs = ExtendedSites.BootPatches(1).Select(p => p.Addr).ToHashSet();
        var postAddrs = ExtendedSites.PostLoadPatches(1).Select(p => p.Addr).ToHashSet();
        var hookedEntries = new[]
        {
            Offsets.FnCategoryGetter, Offsets.FnOrderRebuild, Offsets.FnInventoryReset,
            Offsets.FnSaveSerialize, Offsets.FnSaveApply, Offsets.FnSaveApplyB,
        };
        foreach (var site in ListRelocation.Sites)
        {
            for (long b = site.Addr; b < site.Addr + 4; b++)
            {
                Assert.DoesNotContain(b, bootAddrs);
                Assert.DoesNotContain(b, postAddrs);
            }
            Assert.False(Overlaps(site.Addr, 4, Offsets.ExtCatalogDisp32, 4));
            Assert.False(Overlaps(site.Addr, 4, Offsets.ShopBuilderHighByteLeaRel32, 4));
            Assert.False(Overlaps(site.Addr, 4, Offsets.ShopBuilderLowByteDisp32, 4));
            foreach (var entry in hookedEntries)
                Assert.False(Overlaps(site.Addr, 4, entry, 16), $"0x{site.Addr:X} overlaps the hooked entry at 0x{entry:X}..+16");
        }
    }

    [Fact]
    public void No_page_refuses()
    {
        var f = SeedVanilla();
        var alloc = new FakeNearAllocator { RefuseAfter = 0 };
        var rel = new ListRelocation();

        string? why = rel.Install(f, alloc);

        Assert.NotNull(why);
        Assert.Contains("no page", why);
        Assert.Empty(f.Writes);
        Assert.False(rel.Installed);
    }

    [Fact]
    public void A_page_out_of_int32_reach_refuses_with_nothing_written()
    {
        // D6/REACH (v2.4, verifier finding b1): the allocator's window is 2 GB around the image
        // base, but the FIRST free region inside that window can sit anywhere -- including more
        // than 2 GB below every site and below Offsets.ModuleBase. A page granted there would
        // silently truncate every field's (int) distance into garbage without this check.
        var f = SeedVanilla();
        var alloc = new FakeNearAllocator { Base = 0x40000000L };
        var rel = new ListRelocation();

        string? why = rel.Install(f, alloc);

        Assert.NotNull(why);
        Assert.Contains("out of int32 reach", why);
        Assert.Empty(f.Writes);   // the reach check runs before anything is written, not even the zero-fill
        Assert.False(rel.Installed);
        Assert.Equal(Offsets.BagCountArray, rel.CountBase);
    }

    [Fact]
    public void Tripwire_warns_exactly_once_when_an_old_block_changes()
    {
        var f = SeedVanilla();
        var rel = new ListRelocation();
        Assert.Null(rel.Install(f, new FakeNearAllocator()));
        Assert.Null(rel.CheckOldBlocks(f));   // nothing has changed yet

        f.Seed(Offsets.BagCountArray + 5, 0xFF);   // some other code still writes the old block

        int fires = 0;
        for (int i = 0; i < 5; i++)
            if (rel.CheckOldBlocks(f) != null) fires++;
        Assert.Equal(1, fires);
    }

    /// <summary>Round 2b: bytes +0x105..+0x10F of each 0x110-byte old block are NOT list data --
    /// +0x108 is a live game state dword (read live 2026-08-31) the tripwire must never watch, or
    /// the dword's own ordinary churn would false-fire "something still writes the old block"
    /// forever. Covers both the count block and its sibling.</summary>
    [Fact]
    public void Tripwire_ignores_the_tail_bytes_that_hold_a_live_state_dword()
    {
        foreach (long oldBlock in new[] { Offsets.BagCountArray, Offsets.SiblingListArray })
        {
            var f = SeedVanilla();
            var rel = new ListRelocation();
            Assert.Null(rel.Install(f, new FakeNearAllocator()));

            for (long off = 0x105; off < ListRelocation.BlockBytes; off++)
                f.Seed(oldBlock + off, 0xFF);

            Assert.Null(rel.CheckOldBlocks(f));
        }
    }

    /// <summary>The other half of the same pin: byte 0 of each old block (squarely inside the
    /// list proper, <see cref="ListRelocation.ListProperBytes"/>) still fires the tripwire, so the
    /// narrowed window in the fix above is not a bug that turned the whole tripwire off.</summary>
    [Fact]
    public void Tripwire_still_fires_on_a_list_proper_byte()
    {
        foreach (long oldBlock in new[] { Offsets.BagCountArray, Offsets.SiblingListArray })
        {
            var f = SeedVanilla();
            var rel = new ListRelocation();
            Assert.Null(rel.Install(f, new FakeNearAllocator()));

            f.Seed(oldBlock + 0x00, 0xFF);

            Assert.NotNull(rel.CheckOldBlocks(f));
        }
    }
}
