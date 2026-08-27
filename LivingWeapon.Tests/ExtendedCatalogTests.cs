using System;
using System.Collections.Generic;
using System.Linq;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>LW-346 S2: the extended catalog relocation over the fakes.</summary>
public class ExtendedCatalogTests
{
    private static readonly byte[] VanillaDisp32 = { 0x10, 0xF9, 0x67, 0x00 };   // 0x0067F910, read on disk 2026-08-27

    private static FakeCodePatcher Vanilla()
    {
        var f = new FakeCodePatcher();
        f.Seed(Offsets.ExtCatalogDisp32, VanillaDisp32);
        for (int id = ExtendedCatalog.DlcLo; id <= ExtendedCatalog.DlcHi; id++)
        {
            var rec = new byte[12];
            rec[0] = (byte)id; rec[5] = 3;   // a marker so the copy can be traced per id
            f.Seed(Offsets.ExtCatalogBase + (long)id * 12, rec);
        }
        return f;
    }

    private static byte[] Record(byte marker)
    {
        var r = new byte[12];
        r[0] = marker;
        return r;
    }

    [Fact]
    public void Disp32_encodes_the_buffer_relative_to_the_image_base()
    {
        Assert.Equal(VanillaDisp32, ExtendedCatalog.EncodeDisp32(Offsets.ExtCatalogBase, Offsets.ModuleBase));
        Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x10 }, ExtendedCatalog.EncodeDisp32(0x150000000L, Offsets.ModuleBase));
        Assert.Null(ExtendedCatalog.EncodeDisp32(0x240000000L, Offsets.ModuleBase));
    }

    [Fact]
    public void Copy_plan_names_the_five_plus_item_records_at_their_own_id_offsets()
    {
        var plan = ExtendedCatalog.CopyPlan().ToList();
        Assert.Equal(5, plan.Count);
        Assert.Equal((Offsets.ExtCatalogBase + 256L * 12, 256L * 12), plan[0]);
        Assert.Equal((Offsets.ExtCatalogBase + 260L * 12, 260L * 12), plan[4]);
    }

    [Fact]
    public void Install_zero_fills_copies_the_plus_items_writes_ours_and_flips_the_disp32_last()
    {
        var f = Vanilla();
        var alloc = new FakeNearAllocator();
        var cat = new ExtendedCatalog();
        Assert.Null(cat.Install(f, alloc, new List<(int, byte[])> { (261, Record(0xA1)), (262, Record(0xA2)) }));
        Assert.True(cat.Installed);
        long buf = cat.BufferAddr;
        Assert.Equal(ExtendedCatalog.BufferSize, alloc.Requests[0].Size);
        Assert.Equal(Offsets.ModuleBase, alloc.Requests[0].Near);
        Assert.Equal(buf, f.Writes[0].Addr);
        Assert.Equal(ExtendedCatalog.BufferSize, f.Writes[0].Data.Length);   // zero fill first
        for (int id = 256; id <= 260; id++) Assert.Equal((byte)id, f.Bytes[buf + id * 12]);
        Assert.Equal(0xA1, f.Bytes[buf + 261 * 12]);
        Assert.Equal(0xA2, f.Bytes[buf + 262 * 12]);
        Assert.Equal(Offsets.ExtCatalogDisp32, f.Writes[^1].Addr);           // disp32 flipped LAST
        Assert.Equal(ExtendedCatalog.EncodeDisp32(buf, Offsets.ModuleBase), f.Writes[^1].Data);
        Assert.Null(cat.Install(f, alloc, new List<(int, byte[])>()));       // idempotent
        Assert.Single(alloc.Requests);
        Assert.True(cat.Restore(f));
        Assert.Equal(VanillaDisp32, f.Read(Offsets.ExtCatalogDisp32, 4));
        Assert.False(cat.Installed);
    }

    [Fact]
    public void Install_refuses_when_the_accessor_is_already_redirected_or_the_site_is_unreadable()
    {
        var redirected = Vanilla();
        redirected.Seed(Offsets.ExtCatalogDisp32, 0x00, 0x00, 0x00, 0x10);
        var refusal = new ExtendedCatalog().Install(redirected, new FakeNearAllocator(), new List<(int, byte[])> { (261, Record(1)) });
        Assert.Contains("already redirected", refusal);
        Assert.Empty(redirected.Writes);

        var unreadable = new FakeCodePatcher();
        Assert.Contains("unreadable", new ExtendedCatalog().Install(unreadable, new FakeNearAllocator(), new List<(int, byte[])>()));
    }

    [Fact]
    public void Install_refuses_bad_ids_bad_records_and_a_failed_allocation()
    {
        var f = Vanilla();
        var cat = new ExtendedCatalog();
        Assert.Contains("outside 261..511", cat.Install(f, new FakeNearAllocator(), new List<(int, byte[])> { (260, Record(1)) }));
        Assert.Contains("outside 261..511", cat.Install(f, new FakeNearAllocator(), new List<(int, byte[])> { (512, Record(1)) }));
        Assert.Contains("must be 12 bytes", cat.Install(f, new FakeNearAllocator(), new List<(int, byte[])> { (261, new byte[8]) }));
        Assert.Contains("no page", cat.Install(f, new FakeNearAllocator { RefuseAfter = 0 }, new List<(int, byte[])> { (261, Record(1)) }));
        Assert.Empty(f.Writes);
        Assert.False(cat.Installed);
    }

    [Fact]
    public void Install_refuses_when_a_plus_item_record_cannot_be_read_and_leaves_the_disp32_alone()
    {
        var f = Vanilla();
        f.Bytes.Remove(Offsets.ExtCatalogBase + 258L * 12 + 3);   // one byte of record 258 missing
        var refusal = new ExtendedCatalog().Install(f, new FakeNearAllocator(), new List<(int, byte[])> { (261, Record(1)) });
        Assert.Contains("vanilla record", refusal);
        Assert.Equal(VanillaDisp32, f.Read(Offsets.ExtCatalogDisp32, 4));
        Assert.DoesNotContain(f.Writes, w => w.Addr == Offsets.ExtCatalogDisp32);
    }
}
