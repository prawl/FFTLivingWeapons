using System;
using System.Linq;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-371: the pure policy half of <see cref="TemplateSync"/> (Projection, OldBlockImage,
/// RegionImage) and its two edge operations (Project, Adopt) over the fakes. T6b is the
/// non-vacuous negative the plan calls out by name: it encodes finding 10's helmet-chart clobber
/// (a page chart longer than the old block's own capacity must never spill a byte past that old
/// block's span).
/// </summary>
public class TemplateSyncTests
{
    private static byte[] Words(params ushort[] words)
    {
        var b = new byte[words.Length * 2];
        for (int i = 0; i < words.Length; i++) { b[i * 2] = (byte)(words[i] & 0xFF); b[i * 2 + 1] = (byte)(words[i] >> 8); }
        return b;
    }

    private static ushort WordAt(byte[] bytes, int wordIndex) => (ushort)(bytes[wordIndex * 2] | (bytes[wordIndex * 2 + 1] << 8));

    private static ushort[] ToWords(byte[] bytes) => Enumerable.Range(0, bytes.Length / 2).Select(i => WordAt(bytes, i)).ToArray();

    // --- T2: the marker rule (RegionImage) ---

    [Fact]
    public void Marker_rule_case_a_a_span_that_already_carries_a_marker_is_unchanged()
    {
        byte[] span = Words(1, 2, TemplateSeat.EndMarker, 0, 0);   // 5 words

        byte[] region = TemplateSync.RegionImage(span, 20);   // 10-word region

        Assert.Equal(span, region.Take(span.Length).ToArray());
        for (int i = 5; i < 10; i++) Assert.Equal((ushort)0xFFFF, WordAt(region, i));
    }

    [Fact]
    public void Marker_rule_case_b_the_first_zero_word_becomes_the_marker()
    {
        byte[] span = Words(1, 2, 0, 0, 0);

        byte[] region = TemplateSync.RegionImage(span, 20);

        Assert.Equal(Words(1, 2, TemplateSeat.EndMarker, 0, 0), region.Take(10).ToArray());
        for (int i = 5; i < 10; i++) Assert.Equal((ushort)0xFFFF, WordAt(region, i));
    }

    [Fact]
    public void Marker_rule_case_c_a_span_with_no_marker_and_no_zero_word_gets_the_marker_appended_after_it()
    {
        byte[] span = Words(1, 2, 3, 4, 5);   // every word a nonzero non-marker id: finding 10's overflowed shape

        byte[] region = TemplateSync.RegionImage(span, 20);

        Assert.Equal(span, region.Take(span.Length).ToArray());
        Assert.Equal(TemplateSeat.EndMarker, WordAt(region, 5));   // the marker, one word past the span
        for (int i = 6; i < 10; i++) Assert.Equal((ushort)0xFFFF, WordAt(region, i));   // wall from the marker + 1
    }

    // --- T6: Projection / OldBlockImage ---

    [Fact]
    public void Projection_returns_page_words_up_to_the_marker_any_id_kept_verbatim()
    {
        var page = new ushort[] { 1, 2, 0x4105, TemplateSeat.EndMarker, 0xFFFF, 0xFFFF };   // 0x4105 = an extended id with the E badge bit

        var proj = TemplateSync.Projection(page, 141);

        Assert.Equal(new ushort[] { 1, 2, 0x4105, TemplateSeat.EndMarker }, proj);
    }

    [Fact]
    public void Projection_truncates_to_capacity_minus_one_then_appends_the_marker()
    {
        var ids = Enumerable.Range(1, 150).Select(i => (ushort)i).ToArray();   // no marker inside the first 141
        var page = ids.Concat(new ushort[] { TemplateSeat.EndMarker }).ToArray();

        var proj = TemplateSync.Projection(page, 141);

        Assert.Equal(141, proj.Length);
        Assert.Equal(Enumerable.Range(1, 140).Select(i => (ushort)i), proj.Take(140));
        Assert.Equal(TemplateSeat.EndMarker, proj[140]);
    }

    [Fact]
    public void A_page_chart_of_133_kinds_round_trips_unchanged()
    {
        var ids = Enumerable.Range(1, 133).Select(i => (ushort)i).ToArray();
        var page = ids.Concat(new ushort[] { TemplateSeat.EndMarker }).Concat(Enumerable.Repeat((ushort)0xFFFF, 10)).ToArray();

        var proj = TemplateSync.Projection(page, 141);

        Assert.Equal(ids.Concat(new ushort[] { TemplateSeat.EndMarker }), proj);
    }

    [Fact]
    public void OldBlockImage_zero_fills_out_to_the_span()
    {
        var proj = new ushort[] { 1, 2, TemplateSeat.EndMarker };

        byte[] image = TemplateSync.OldBlockImage(proj, 10);   // 5-word span

        Assert.Equal(Words(1, 2, TemplateSeat.EndMarker, 0, 0), image);
    }

    // --- T6c ---

    [Fact]
    public void Project_and_Adopt_are_noops_when_not_installed()
    {
        var f = new FakeCodePatcher();
        var rel = new TemplateRelocation();   // never installed

        TemplateSync.Project(f, rel);
        TemplateSync.Adopt(f, rel);

        Assert.Empty(f.Writes);
    }

    // --- T6b (THE NON-VACUOUS NEGATIVE) ---

    /// <summary>Finding 10's helmet-chart clobber, encoded: a page chart of 150 (inventory/picker)
    /// or 270 (all-items) words -- longer than the old block's own capacity, extended ids included
    /// -- must never write past that old block's own span. The neighbor words a verbatim page copy
    /// WOULD clobber (the helmet chart's first word at 0x14187465A, the padding word at
    /// 0x1407B266A, the word after the all-items block at 0x141874932) must come back
    /// byte-identical. A projection that copies the page verbatim, forgets the truncation, or
    /// writes one word past the span fails this.</summary>
    [Fact]
    public void Project_never_writes_past_the_old_block_span_even_when_the_page_chart_is_longer()
    {
        var f = TemplateRelocationTests.SeedVanilla();
        var rel = new TemplateRelocation();
        Assert.Null(rel.Install(f, new FakeNearAllocator()));

        // Inventory and picker (capacity 141): 130 vanilla-shaped ids + 20 extended ids, no
        // marker or zero among the first 150 words -- forces truncation, not an early marker stop.
        var invWords = Enumerable.Range(1, 130).Select(i => (ushort)i)
            .Concat(Enumerable.Range(261, 20).Select(i => (ushort)i)).ToArray();
        // +500 (not +200): a +200 offset put 0x00FF (255) at i=55, which Projection reads as a
        // real marker and stops there -- a lesson banked by running this test red once.
        var pickerWords = Enumerable.Range(1, 130).Select(i => (ushort)(500 + i))
            .Concat(Enumerable.Range(261, 20).Select(i => (ushort)i)).ToArray();
        // All-items (capacity 262): 250 vanilla-shaped ids + 20 extended ids = 270 words.
        var allWords = Enumerable.Range(1, 250).Select(i => (ushort)i)
            .Concat(Enumerable.Range(261, 20).Select(i => (ushort)i)).ToArray();

        SeedPageChart(f, rel.PageAddr + 0x000, invWords);
        SeedPageChart(f, rel.PageAddr + 0x400, pickerWords);
        SeedPageChart(f, rel.PageAddr + 0x800, allWords);

        f.Seed(0x14187465AL, 0xAB, 0xCD);   // the picker's helmet-chart first word
        f.Seed(0x1407B266AL, 0x12, 0x34);   // the inventory chart's padding word (crash-1 residue)
        f.Seed(0x141874932L, 0x56, 0x78);   // the word right after the all-items block

        TemplateSync.Project(f, rel);

        AssertTruncatedOldBlock(f, Offsets.InventoryOrderTemplate, Offsets.InventoryOrderTemplateWords, invWords);
        AssertTruncatedOldBlock(f, Offsets.PickerOrderTemplate, Offsets.PickerOrderTemplateWords, pickerWords);
        AssertTruncatedOldBlock(f, Offsets.PickerAllItemsTemplate, Offsets.PickerAllItemsTemplateWords, allWords);

        Assert.Equal(new byte[] { 0xAB, 0xCD }, f.Read(0x14187465AL, 2));
        Assert.Equal(new byte[] { 0x12, 0x34 }, f.Read(0x1407B266AL, 2));
        Assert.Equal(new byte[] { 0x56, 0x78 }, f.Read(0x141874932L, 2));
    }

    private static void SeedPageChart(FakeCodePatcher f, long regionAddr, ushort[] idsNoMarker)
    {
        var region = new byte[TemplateRelocation.RegionBytes];
        for (int i = 0; i < idsNoMarker.Length; i++) { region[i * 2] = (byte)(idsNoMarker[i] & 0xFF); region[i * 2 + 1] = (byte)(idsNoMarker[i] >> 8); }
        // no marker anywhere in the region: Projection must truncate by capacity, not stop early.
        for (int i = idsNoMarker.Length; i * 2 + 1 < region.Length; i++) { region[i * 2] = 0xFE; region[i * 2 + 1] = 0xFE; }
        f.Seed(regionAddr, region);
    }

    private static void AssertTruncatedOldBlock(FakeCodePatcher f, long oldBase, int capacityWords, ushort[] pageIds)
    {
        var span = f.Read(oldBase, capacityWords * 2);
        var words = ToWords(span);
        var expectedIds = pageIds.Take(capacityWords - 1).ToArray();
        Assert.Equal(expectedIds, words.Take(capacityWords - 1).ToArray());
        Assert.Equal(TemplateSeat.EndMarker, words[capacityWords - 1]);
        // capacityWords * 2 == the chart's own SpanBytes (D4): nothing is left to zero-fill here.
        // This does NOT by itself prove the projection stayed inside the span -- the span was
        // fully seeded before Project ran, so the read above would succeed either way; the actual
        // proof that not one byte landed past it is the caller's neighbor-word assertions
        // (0x14187465A / 0x1407B266A / 0x141874932, byte-identical after Project).
        Assert.Equal(capacityWords, words.Length);
    }

    // --- T7 ---

    [Fact]
    public void Adopt_copies_the_old_block_and_walls_the_rest_one_write_per_chart()
    {
        var f = TemplateRelocationTests.SeedVanilla();
        var rel = new TemplateRelocation();
        Assert.Null(rel.Install(f, new FakeNearAllocator()));
        int writesBefore = f.Writes.Count;

        // The game just restored the old blocks from the save struct with fresh content.
        var freshSpans = new byte[TemplateRelocation.Charts.Length][];
        for (int i = 0; i < TemplateRelocation.Charts.Length; i++)
        {
            freshSpans[i] = TemplateRelocationTests.ChartSpan(TemplateRelocation.Charts[i], 0x5000, 2);
            f.Seed(TemplateRelocation.Charts[i].OldBase, freshSpans[i]);
        }

        TemplateSync.Adopt(f, rel);

        Assert.Equal(writesBefore + 3, f.Writes.Count);   // one write per chart, nothing else
        for (int i = 0; i < TemplateRelocation.Charts.Length; i++)
        {
            var chart = TemplateRelocation.Charts[i];
            var region = f.Read(rel.PageAddr + chart.PageOffset, chart.RegionBytes);
            Assert.Equal(freshSpans[i], region.Take(freshSpans[i].Length).ToArray());
            for (int b = freshSpans[i].Length; b < chart.RegionBytes; b += 2)
            {
                Assert.Equal(0xFF, region[b]);
                Assert.Equal(0xFF, region[b + 1]);
            }
        }
    }
}
