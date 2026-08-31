using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-371: <see cref="TemplateRelocation"/>'s ten-field install/restore transaction over the
/// fakes. T1 is load-bearing and deliberately never calls <see cref="TemplateRelocation.NewField"/>
/// for its expected rip-field values -- it re-derives them from the raw formula so a bug shared
/// between production and the test cannot hide (the ListRelocationTests T1 idiom). T6b lives in
/// TemplateSyncTests.cs (it exercises <see cref="TemplateSync.Project"/>, not the lifecycle).
/// </summary>
public class TemplateRelocationTests
{
    // Distinct per-chart tags so a copy landing at the wrong page offset (or the wrong chart)
    // fails loudly, the same "recognizable pattern" idiom ListRelocationTests uses.
    internal static readonly ushort[] ChartTags = { 0x1000, 0x2000, 0x3000 };

    /// <summary>One chart span: <paramref name="idCount"/> distinct ids from <paramref
    /// name="tag"/>, the 0x00FF marker right after them, zero-filled out to the chart's own
    /// SpanBytes.</summary>
    internal static byte[] ChartSpan(TemplateRelocation.Chart chart, ushort tag, int idCount)
    {
        var bytes = new byte[chart.SpanBytes];
        for (int i = 0; i < idCount; i++) WriteWord(bytes, i, (ushort)(tag + i));
        WriteWord(bytes, idCount, TemplateSeat.EndMarker);
        return bytes;
    }

    private static void WriteWord(byte[] bytes, int wordIndex, ushort value)
    {
        bytes[wordIndex * 2] = (byte)(value & 0xFF);
        bytes[wordIndex * 2 + 1] = (byte)(value >> 8);
    }

    private static ushort ReadWord(byte[] bytes, int wordIndex) => (ushort)(bytes[wordIndex * 2] | (bytes[wordIndex * 2 + 1] << 8));

    /// <summary>Every one of the ten fields at its vanilla value, plus the three old chart spans
    /// (each a small recognizable pattern with a marker) -- exactly what a vanilla 1.5.2 process
    /// image reads before this mod ever touches it.</summary>
    internal static FakeCodePatcher SeedVanilla()
    {
        var f = new FakeCodePatcher();
        foreach (var s in TemplateRelocation.Slots) f.Seed(s.Addr, BitConverter.GetBytes(s.Vanilla));
        foreach (var rf in TemplateRelocation.RipFields) f.Seed(rf.Addr, BitConverter.GetBytes(rf.Vanilla));
        foreach (var c in TemplateRelocation.CapBytes) f.Seed(c.Addr, c.Vanilla);
        for (int i = 0; i < TemplateRelocation.Charts.Length; i++)
            f.Seed(TemplateRelocation.Charts[i].OldBase, ChartSpan(TemplateRelocation.Charts[i], ChartTags[i], 3));
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
        var invSpan = ChartSpan(TemplateRelocation.Charts[0], 0x1000, 5);
        var pickerSpan = ChartSpan(TemplateRelocation.Charts[1], 0x2000, 3);
        var allSpan = ChartSpan(TemplateRelocation.Charts[2], 0x3000, 4);
        var f = new FakeCodePatcher();
        foreach (var s in TemplateRelocation.Slots) f.Seed(s.Addr, BitConverter.GetBytes(s.Vanilla));
        foreach (var rf in TemplateRelocation.RipFields) f.Seed(rf.Addr, BitConverter.GetBytes(rf.Vanilla));
        foreach (var c in TemplateRelocation.CapBytes) f.Seed(c.Addr, c.Vanilla);
        f.Seed(TemplateRelocation.Charts[0].OldBase, invSpan);
        f.Seed(TemplateRelocation.Charts[1].OldBase, pickerSpan);
        f.Seed(TemplateRelocation.Charts[2].OldBase, allSpan);
        var alloc = new FakeNearAllocator();
        var rel = new TemplateRelocation();

        string? why = rel.Install(f, alloc);

        Assert.Null(why);
        Assert.True(rel.Installed);
        long page = alloc.Requests[0].Got;   // page = the allocator's first (and only) grant
        Assert.Equal(page, rel.PageAddr);

        // Slots: the new qword is page + the chart's OWN page offset, computed inline (D2:
        // inventory +0x000, picker +0x400, all-items +0x800 -- never referenced through the
        // component's PageOffsetOf helper).
        long[] slotOffsets = { 0x000, 0x000, 0x400 };
        for (int i = 0; i < TemplateRelocation.Slots.Length; i++)
            Assert.Equal(BitConverter.GetBytes(page + slotOffsets[i]), f.Read(TemplateRelocation.Slots[i].Addr, 8));

        // Rip fields: (int)(page + 0x800 + Off - (addr + 4)), computed inline. Named breaks this
        // must catch: dropping the "+ 4" (every field off by 4); swapping +0x400/+0x800 (this
        // and the slot loop above would both go wrong together). v1.2 T1 pin (verifier
        // SHOULD-FIX): field 0 (the shared rsi base) uses the LITERAL -0x1E6 here, never
        // `Off` read back from the table -- otherwise a table carrying Off=0 instead of -0x1E6
        // would compute the SAME (wrong) expected value production did and this loop would stay
        // green. The other four fields' Off is separately pinned to 0 in
        // Site_table_is_self_consistent, so a stray nonzero Off on any of them still goes red.
        for (int i = 0; i < TemplateRelocation.RipFields.Length; i++)
        {
            var rfField = TemplateRelocation.RipFields[i];
            int off = i == 0 ? -0x1E6 : rfField.Off;
            int expected = (int)(page + 0x800 + off - (rfField.Addr + 4));
            Assert.Equal(BitConverter.GetBytes(expected), f.Read(rfField.Addr, 4));
        }

        Assert.Equal(0x95, f.Bytes[Offsets.ListBuilderCapByte]);
        Assert.Equal(0x96, f.Bytes[Offsets.ListInsertBoundByte]);

        // The page holds each copy (unchanged: every seeded span already carries a marker) then
        // 0xFFFF walls to the region end -- verified without calling TemplateSync at all.
        AssertPageRegion(f, page + 0x000, invSpan, TemplateRelocation.RegionBytes);
        AssertPageRegion(f, page + 0x400, pickerSpan, TemplateRelocation.RegionBytes);
        AssertPageRegion(f, page + 0x800, allSpan, TemplateRelocation.RegionBytes);
    }

    private static void AssertPageRegion(FakeCodePatcher f, long regionAddr, byte[] span, int regionBytes)
    {
        var region = f.Read(regionAddr, regionBytes);
        Assert.Equal(span, region.Take(span.Length).ToArray());
        for (int i = span.Length; i < regionBytes; i += 2)
        {
            Assert.Equal(0xFF, region[i]);
            Assert.Equal(0xFF, region[i + 1]);
        }
    }

    [Fact]
    public void A_field_that_is_not_vanilla_refuses_with_nothing_written()
    {
        var slot = TemplateRelocation.Slots[0];
        var f1 = SeedVanilla();
        f1.Seed(slot.Addr, BitConverter.GetBytes(slot.Vanilla + 1));
        string? why1 = new TemplateRelocation().Install(f1, new FakeNearAllocator());
        Assert.NotNull(why1);
        Assert.Contains($"0x{slot.Addr:X}", why1);
        Assert.Empty(f1.Writes);

        var field = TemplateRelocation.RipFields[0];
        var f2 = SeedVanilla();
        f2.Seed(field.Addr, BitConverter.GetBytes(field.Vanilla + 1));
        string? why2 = new TemplateRelocation().Install(f2, new FakeNearAllocator());
        Assert.NotNull(why2);
        Assert.Contains($"0x{field.Addr:X}", why2);
        Assert.Empty(f2.Writes);

        var cap = TemplateRelocation.CapBytes[0];
        var f3 = SeedVanilla();
        f3.Seed(cap.Addr, (byte)(cap.Vanilla + 1));
        string? why3 = new TemplateRelocation().Install(f3, new FakeNearAllocator());
        Assert.NotNull(why3);
        Assert.Contains($"0x{cap.Addr:X}", why3);
        Assert.Empty(f3.Writes);
    }

    [Fact]
    public void A_refused_write_rolls_back_byte_identically()
    {
        var f = SeedVanilla();
        var alloc = new FakeNearAllocator();
        var before = new Dictionary<long, byte>(f.Bytes);
        // Field write order is slots(3) then rip fields(5) then caps(2); the 6th FIELD write (the
        // 9th write overall, after the three page images land first) is the third rip field
        // (0x1402861F5), the same "refuse mid-way" idiom ListRelocationTests uses.
        var sixthField = TemplateRelocation.RipFields[2];
        f.RefuseWritesAt.Add(sixthField.Addr);
        var rel = new TemplateRelocation();

        string? why = rel.Install(f, alloc);

        Assert.NotNull(why);
        Assert.False(rel.Installed);
        long page = alloc.Requests[0].Got;
        foreach (var kv in f.Bytes)
        {
            if (kv.Key >= page && kv.Key < page + TemplateRelocation.PageSize) continue;   // the leaked page, not a "field"
            Assert.True(before.TryGetValue(kv.Key, out var b) && b == kv.Value, $"0x{kv.Key:X} was not restored to its pre-Install value");
        }
    }

    [Fact]
    public void Restore_puts_every_field_back_byte_identically_and_is_idempotent()
    {
        var f = SeedVanilla();
        var alloc = new FakeNearAllocator();
        var rel = new TemplateRelocation();
        Assert.Null(rel.Install(f, alloc));
        long page = alloc.Requests[0].Got;
        const int written = 0xC00;   // D2: three 0x400-byte regions; +0xC00 unused this round, never written
        var pageSnapshot = f.Read(page, written);

        Assert.True(rel.Restore(f));

        foreach (var s in TemplateRelocation.Slots) Assert.Equal(BitConverter.GetBytes(s.Vanilla), f.Read(s.Addr, 8));
        foreach (var rf in TemplateRelocation.RipFields) Assert.Equal(BitConverter.GetBytes(rf.Vanilla), f.Read(rf.Addr, 4));
        foreach (var c in TemplateRelocation.CapBytes) Assert.Equal(c.Vanilla, f.Read(c.Addr, 1)[0]);
        Assert.False(rel.Installed);
        Assert.Equal(pageSnapshot, f.Read(page, written));   // D7: the page is leaked, Restore never touches it

        int writesBefore = f.Writes.Count;
        Assert.True(rel.Restore(f));   // idempotent: not installed, nothing more to do
        Assert.Equal(writesBefore, f.Writes.Count);
    }

    [Fact]
    public void Regions_follow_the_relocation()
    {
        var f = SeedVanilla();
        var rel = new TemplateRelocation();
        Assert.Equal(TemplateSeat.WeaponRegions, rel.Regions);   // before install: the vanilla pair

        Assert.Null(rel.Install(f, new FakeNearAllocator()));

        var regions = rel.Regions;
        Assert.Equal(2, regions.Length);
        Assert.Equal(rel.PageAddr + 0x000, regions[0].Addr);
        Assert.Equal(rel.PageAddr + 0x400, regions[1].Addr);
        Assert.Equal(511, regions[0].CapacityWords);
        Assert.Equal(511, regions[1].CapacityWords);
    }

    [Fact]
    public void Caps_fit_the_smallest_list_buffer()
    {
        int builderCapNew = TemplateRelocation.CapBytes[0].New;
        int insertBoundNew = TemplateRelocation.CapBytes[1].New;
        // v1.3 (P14): the builder alone only needs cap + 1 words, but fnA's weapons-only picker
        // list appends the unit's two hand items AFTER the capped builder list and only THEN
        // writes its own terminator, so the binding buffer requirement is cap + 3 words (the two
        // hand-item appends plus the terminator), not cap + 1.
        Assert.True((builderCapNew + 3) * 2 <= Offsets.ListBuilderStackRoomBytes);
        Assert.Equal(builderCapNew + 1, insertBoundNew);

        // The v1.2 candidate the second verifier's fnA-append find corrected: 0x97 (151) does NOT
        // fit once the two hand items and the terminator are counted -- (151 + 3) * 2 = 0x134.
        Assert.True((0x97 + 3) * 2 > Offsets.ListBuilderStackRoomBytes);
        // The v1.0 candidate the first verifier's cookie find corrected: 0x9F would NOT have fit
        // even under the (wrong) cap + 1 accounting.
        Assert.True((0x9F + 1) * 2 > Offsets.ListBuilderStackRoomBytes);
    }

    [Fact]
    public void Region_capacity_exceeds_max_owned_kinds()
    {
        int vanillaHandKinds = CountVanillaHandKinds();
        Assert.Equal(139, vanillaHandKinds);
        Assert.True(vanillaHandKinds + ExtendedSites.MaxExtendedCount + 1 <= 512);
        Assert.True(Offsets.PickerAllItemsTemplateWords + ExtendedSites.MaxExtendedCount <= 512);
    }

    /// <summary>139 = the vanilla items.json rows whose category is a weapon class (18 types,
    /// Throwing/Bomb excluded) or Shield, plus the two DLC blades 256/257 (not items.json rows;
    /// ExtendedCatalog.DlcLo/DlcLo+1) -- LW371_plan.md D11.</summary>
    private static int CountVanillaHandKinds()
    {
        string path = Path.Combine(RepoRoot(), "data", "items.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var weaponCats = new HashSet<string>
        {
            "Knife", "NinjaBlade", "Sword", "KnightSword", "Katana", "Axe", "Rod", "Staff",
            "Flail", "Gun", "Crossbow", "Bow", "Instrument", "Book", "Polearm", "Pole", "Bag", "Cloth",
        };
        int n = 0;
        foreach (var item in doc.RootElement.GetProperty("items").EnumerateArray())
        {
            int id = item.GetProperty("id").GetInt32();
            string cat = item.GetProperty("category").GetString()!;
            if (id < 261 && (weaponCats.Contains(cat) || cat == "Shield")) n++;
        }
        return n + 2;
    }

    /// <summary>LW-371 (T9d's arm-level twin lives in ExtendedInventoryTests.cs); here, the pure
    /// component check.</summary>
    [Fact]
    public void Site_table_is_self_consistent()
    {
        var allAddrs = new List<(long Addr, int Len)>();
        foreach (var s in TemplateRelocation.Slots) allAddrs.Add((s.Addr, 8));
        foreach (var rf in TemplateRelocation.RipFields) allAddrs.Add((rf.Addr, 4));
        foreach (var c in TemplateRelocation.CapBytes) allAddrs.Add((c.Addr, 1));
        Assert.Equal(10, allAddrs.Count);
        Assert.Equal(allAddrs.Count, allAddrs.Select(a => a.Addr).Distinct().Count());
        for (int i = 0; i < allAddrs.Count; i++)
            for (int j = i + 1; j < allAddrs.Count; j++)
                Assert.False(Overlaps(allAddrs[i].Addr, allAddrs[i].Len, allAddrs[j].Addr, allAddrs[j].Len),
                    $"0x{allAddrs[i].Addr:X} and 0x{allAddrs[j].Addr:X} overlap");

        // Reviewer (a): field 0 resolves to PickerOrderTemplate (the shared rsi base), the other
        // four to PickerAllItemsTemplate.
        long[] oldTargets =
        {
            Offsets.PickerOrderTemplate, Offsets.PickerAllItemsTemplate, Offsets.PickerAllItemsTemplate,
            Offsets.PickerAllItemsTemplate, Offsets.PickerAllItemsTemplate,
        };
        for (int i = 0; i < TemplateRelocation.RipFields.Length; i++)
        {
            var rf = TemplateRelocation.RipFields[i];
            int expected = (int)(oldTargets[i] - (rf.Addr + 4));
            Assert.Equal(expected, rf.Vanilla);
        }

        // v1.2 (verifier SHOULD-FIX): the rsi base's Off is the distance from the all-items
        // chart's own page target back to the picker chart's -- PickerOrderTemplate minus
        // PickerAllItemsTemplate, i.e. -0x1E6 -- and every other field's Off is 0 (they target
        // the all-items chart directly). T1 hardcodes -0x1E6 as a literal for field 0 precisely
        // so this pin (not T1) is what catches a table that carries the wrong Off.
        Assert.Equal((int)(Offsets.PickerOrderTemplate - Offsets.PickerAllItemsTemplate), TemplateRelocation.RipFields[0].Off);
        Assert.Equal(-0x1E6, TemplateRelocation.RipFields[0].Off);
        for (int i = 1; i < TemplateRelocation.RipFields.Length; i++)
            Assert.Equal(0, TemplateRelocation.RipFields[i].Off);

        Assert.Equal((byte)0x91, TemplateRelocation.CapBytes[0].Vanilla);
        Assert.Equal((byte)0x92, TemplateRelocation.CapBytes[1].Vanilla);

        var bootAddrs = ExtendedSites.BootPatches(1).Select(p => p.Addr).ToHashSet();
        var postAddrs = ExtendedSites.PostLoadPatches(1).Select(p => p.Addr).ToHashSet();
        var listAddrs = ListRelocation.Sites.Select(s => s.Addr).ToHashSet();
        var hookedEntries = new[]
        {
            Offsets.FnCategoryGetter, Offsets.FnOrderRebuild, Offsets.FnInventoryReset,
            Offsets.FnSaveSerialize, Offsets.FnSaveApply, Offsets.FnSaveApplyB,
        };
        foreach (var (addr, len) in allAddrs)
        {
            for (long b = addr; b < addr + len; b++)
            {
                Assert.DoesNotContain(b, bootAddrs);
                Assert.DoesNotContain(b, postAddrs);
                Assert.DoesNotContain(b, listAddrs);
            }
            Assert.False(Overlaps(addr, len, Offsets.ExtCatalogDisp32, 4));
            Assert.False(Overlaps(addr, len, Offsets.ShopBuilderHighByteLeaRel32, 4));
            Assert.False(Overlaps(addr, len, Offsets.ShopBuilderLowByteDisp32, 4));
            foreach (var entry in hookedEntries)
                Assert.False(Overlaps(addr, len, entry, 16), $"0x{addr:X} overlaps the hooked entry at 0x{entry:X}..+16");
        }
    }

    [Fact]
    public void Write_order_and_scope()
    {
        var f = SeedVanilla();
        var alloc = new FakeNearAllocator();
        var rel = new TemplateRelocation();

        Assert.Null(rel.Install(f, alloc));

        long page = alloc.Requests[0].Got;
        Assert.Equal(page + 0x000, f.Writes[0].Addr);
        Assert.Equal(TemplateRelocation.RegionBytes, f.Writes[0].Data.Length);
        Assert.Equal(page + 0x400, f.Writes[1].Addr);
        Assert.Equal(TemplateRelocation.RegionBytes, f.Writes[1].Data.Length);
        Assert.Equal(page + 0x800, f.Writes[2].Addr);
        Assert.Equal(TemplateRelocation.RegionBytes, f.Writes[2].Data.Length);

        var fieldAddrs = TemplateRelocation.Slots.Select(s => s.Addr)
            .Concat(TemplateRelocation.RipFields.Select(rf => rf.Addr))
            .Concat(TemplateRelocation.CapBytes.Select(c => c.Addr)).ToHashSet();
        for (int i = 3; i < f.Writes.Count; i++)
            Assert.Contains(f.Writes[i].Addr, fieldAddrs);
        foreach (var (addr, data) in f.Writes)
            Assert.True((addr >= page && addr + data.Length <= page + TemplateRelocation.PageSize) || fieldAddrs.Contains(addr),
                $"write at 0x{addr:X} is outside the page and is not a known field");
    }

    [Fact]
    public void No_page_refuses()
    {
        var f = SeedVanilla();
        var alloc = new FakeNearAllocator { RefuseAfter = 0 };
        var rel = new TemplateRelocation();

        string? why = rel.Install(f, alloc);

        Assert.NotNull(why);
        Assert.Contains("no page", why);
        Assert.Empty(f.Writes);
        Assert.False(rel.Installed);
    }

    [Fact]
    public void A_page_out_of_int32_reach_refuses_with_nothing_written()
    {
        var f = SeedVanilla();
        var alloc = new FakeNearAllocator { Base = 0x40000000L };
        var rel = new TemplateRelocation();

        string? why = rel.Install(f, alloc);

        Assert.NotNull(why);
        Assert.Contains("out of int32 reach", why);
        Assert.Empty(f.Writes);
        Assert.False(rel.Installed);
    }

    [Fact]
    public void An_unreadable_old_chart_span_refuses_with_nothing_written()
    {
        var f = SeedVanilla();
        f.Bytes.Remove(TemplateRelocation.Charts[1].OldBase);   // punch a hole in the picker span
        var rel = new TemplateRelocation();

        string? why = rel.Install(f, new FakeNearAllocator());

        Assert.NotNull(why);
        Assert.Contains("unreadable", why);
        Assert.Empty(f.Writes);
        Assert.False(rel.Installed);
    }

    /// <summary>T9f (v1.3, verifier 2 SHOULD-FIX): D3's "a page chart always has a marker inside
    /// its region" is only true if the region has room for the marker past the OLD block's own
    /// worst-case span -- pinned here against the REAL shipped <see cref="TemplateRelocation.Charts"/>
    /// (142, 142, 263 words), not toy sizes, so a future chart added with a span that does not
    /// leave room for its own marker inside its region goes red immediately.</summary>
    [Fact]
    public void Every_chart_span_plus_its_marker_fits_its_region()
    {
        foreach (var chart in TemplateRelocation.Charts)
            Assert.True(chart.SpanBytes / 2 + 1 <= chart.RegionBytes / 2,
                $"{chart.Label}: {chart.SpanBytes / 2} span words + 1 marker word exceeds its {chart.RegionBytes / 2}-word region");

        // Named against the literal numbers so a change to any chart's constants is visible here,
        // not just inferred from the loop above.
        Assert.Equal(142, TemplateRelocation.Charts[0].SpanBytes / 2 + 1);
        Assert.Equal(142, TemplateRelocation.Charts[1].SpanBytes / 2 + 1);
        Assert.Equal(263, TemplateRelocation.Charts[2].SpanBytes / 2 + 1);
    }
}
