using System;
using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>LW-354: the shop-table mirror over the fakes (the builder's two sites carry their
/// vanilla displacements, read on the 1.5.2 exe on disk and live, 2026-08-27).</summary>
public class ShopFlagsMirrorTests
{
    internal static void SeedVanillaShopSites(FakeCodePatcher f)
    {
        f.Seed(Offsets.ShopBuilderHighByteLeaRel32, 0x89, 0x69, 0x3F, 0x00);   // lea r12,[rip+0x3F6989]
        f.Seed(Offsets.ShopBuilderLowByteDisp32, 0x90, 0xF8, 0x67, 0x00);      // [rcx+rbp+0x67F890]
        var tab = new byte[ShopFlagsMirror.VanillaBytes];
        for (int id = 1; id < 256; id++) { tab[id * 2] = 0xFE; tab[id * 2 + 1] = 0x01; }   // the loader's own shape
        f.Seed(Offsets.ShopFlagsTable, tab);
    }

    [Fact]
    public void Install_repoints_both_builder_sites_at_a_zeroed_page_carrying_our_rows()
    {
        var f = new FakeCodePatcher();
        SeedVanillaShopSites(f);
        var alloc = new FakeNearAllocator();
        var m = new ShopFlagsMirror();
        Assert.Null(m.Install(f, alloc, new List<(int, ushort)> { (261, 0x4002) }));   // Dorter | Gariland
        Assert.True(m.Installed);
        long buf = m.MirrorAddr;
        Assert.Equal(ShopFlagsMirror.PageSize, alloc.Requests[0].Size);
        Assert.Equal(0x02, f.Bytes[buf + 261 * 2]);
        Assert.Equal(0x40, f.Bytes[buf + 261 * 2 + 1]);
        Assert.Equal(0, f.Bytes[buf + 37 * 2]);   // vanilla half is empty until the first Sync
        int rel = BitConverter.ToInt32(f.Read(Offsets.ShopBuilderHighByteLeaRel32, 4), 0);
        Assert.Equal(buf + 1, Offsets.ShopBuilderHighByteLeaNextIp + rel);   // r12 walks the mirror's high bytes
        int disp = BitConverter.ToInt32(f.Read(Offsets.ShopBuilderLowByteDisp32, 4), 0);
        Assert.Equal(buf, Offsets.ModuleBase + disp);                        // low-byte read lands on the mirror
        Assert.Null(m.Install(f, alloc, new List<(int, ushort)>()));         // idempotent
        Assert.Single(alloc.Requests);
    }

    [Fact]
    public void Sync_copies_the_vanilla_table_only_when_it_changes()
    {
        var f = new FakeCodePatcher();
        SeedVanillaShopSites(f);
        var m = new ShopFlagsMirror();
        Assert.False(m.Sync(f));   // not installed: nothing
        Assert.Null(m.Install(f, new FakeNearAllocator(), new List<(int, ushort)> { (261, 1) }));
        Assert.True(m.Sync(f));
        Assert.Equal(0xFE, f.Bytes[m.MirrorAddr + 37 * 2]);
        Assert.Equal(0x01, f.Bytes[m.MirrorAddr + 37 * 2 + 1]);
        Assert.Equal(1, f.Bytes[m.MirrorAddr + 261 * 2]);   // our row untouched by the copy
        int writes = f.Writes.Count;
        Assert.False(m.Sync(f));   // unchanged: no write
        Assert.Equal(writes, f.Writes.Count);
        f.Seed(Offsets.ShopFlagsTable + 37 * 2, 0x00, 0x41);   // a partner mod sold the Chaos Blade in Dorter
        Assert.True(m.Sync(f));
        Assert.Equal(0x41, f.Bytes[m.MirrorAddr + 37 * 2 + 1]);
    }

    [Fact]
    public void Install_refuses_a_foreign_site_a_bad_id_or_a_failed_page_without_writing()
    {
        var f = new FakeCodePatcher();
        SeedVanillaShopSites(f);
        f.Seed(Offsets.ShopBuilderLowByteDisp32, 0x00, 0x00, 0x00, 0x10);   // already redirected
        var refusal = new ShopFlagsMirror().Install(f, new FakeNearAllocator(), new List<(int, ushort)> { (261, 1) });
        Assert.Contains("already redirected", refusal);
        Assert.Empty(f.Writes);

        var g = new FakeCodePatcher();
        SeedVanillaShopSites(g);
        Assert.Contains("outside 261..511", new ShopFlagsMirror().Install(g, new FakeNearAllocator(), new List<(int, ushort)> { (255, 1) }));
        Assert.Contains("no page", new ShopFlagsMirror().Install(g, new FakeNearAllocator { RefuseAfter = 0 }, new List<(int, ushort)> { (261, 1) }));
        Assert.Empty(g.Writes);
        Assert.Contains("unreadable", new ShopFlagsMirror().Install(new FakeCodePatcher(), new FakeNearAllocator(), new List<(int, ushort)>()));
    }

    [Fact]
    public void Restore_puts_both_vanilla_displacements_back()
    {
        var f = new FakeCodePatcher();
        SeedVanillaShopSites(f);
        var m = new ShopFlagsMirror();
        Assert.Null(m.Install(f, new FakeNearAllocator(), new List<(int, ushort)> { (261, 1) }));
        Assert.True(m.Restore(f));
        Assert.False(m.Installed);
        Assert.Equal(new byte[] { 0x89, 0x69, 0x3F, 0x00 }, f.Read(Offsets.ShopBuilderHighByteLeaRel32, 4));
        Assert.Equal(new byte[] { 0x90, 0xF8, 0x67, 0x00 }, f.Read(Offsets.ShopBuilderLowByteDisp32, 4));
    }

    [Theory]
    [InlineData("Dorter", 0x4000)]
    [InlineData("Dorter, Gariland", 0x4002)]
    [InlineData("lesalia", 0x0080)]
    [InlineData("None", 0)]
    [InlineData("", 0)]
    [InlineData(null, 0)]
    public void ParseShops_uses_the_loader_enum_bits(string? csv, int expected)
        => Assert.Equal(expected, ExtendedRecords.ParseShops(csv));

    [Fact]
    public void ParseShops_rejects_an_unknown_town()
        => Assert.Contains("Ivalice", Assert.Throws<FormatException>(() => ExtendedRecords.ParseShops("Dorter, Ivalice")).Message);
}
