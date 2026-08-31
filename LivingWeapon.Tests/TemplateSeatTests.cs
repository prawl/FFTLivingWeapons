using System.Collections.Generic;
using System.Linq;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-351 fix round 5. The owner's re-test 5 (2026-08-30) put three Terrastaffs in the bag before
/// the game's load path could look, and the item STILL did not appear in either equip menu. The
/// closing disassembly says why: the two weapon order templates are not rebuilt from the item data
/// at load time at all, they are restored byte-for-byte out of the save struct (and written back
/// by the serializer), so a save written before an extended id ever seated restores a table that
/// will never name it. <see cref="TemplateSeat"/> seats owned extended ids into those restored
/// tables, in the same load-detour moment that replays the bag counts.
///
/// These tests drive the pure policy over hand-built tables, and the applier over the dictionary
/// image every other extended-inventory test uses.
/// </summary>
public class TemplateSeatTests
{
    private const int Moon = 261, Terra = 262, Third = 263;

    /// <summary>A template region of <paramref name="capacityWords"/> u16 words holding
    /// <paramref name="words"/> (the caller writes its own 0x00FF marker) and zeroes after.</summary>
    private static byte[] Table(int capacityWords, params int[] words)
    {
        var b = new byte[capacityWords * 2];
        for (int i = 0; i < words.Length; i++)
        {
            b[i * 2] = (byte)(words[i] & 0xFF);
            b[i * 2 + 1] = (byte)((words[i] >> 8) & 0xFF);
        }
        return b;
    }

    private static ushort[] Words(byte[] region, int count)
        => Enumerable.Range(0, count).Select(i => (ushort)(region[i * 2] | (region[i * 2 + 1] << 8))).ToArray();

    private static ushort[] WordsAt(FakeCodePatcher f, long addr, int count)
        => Words(f.Read(addr, count * 2), count);

    /// <summary>The capacities are DERIVED, not guessed, so they are pinned here: the load-apply
    /// routine's fixed-size copy into the inventory template moves 2 iterations of 0x80 bytes plus
    /// a 0x1A-byte tail = 0x11A = 282 bytes = 141 words (loop 0x14021B560, counter r10 = 2 from
    /// `lea r10d,[r11+2]` at 0x14021B1C4, stride rsi = 0x80 from `lea esi,[rbx+0x7c]` at
    /// 0x14021B11C with ebx = 4), and the picker template's own pointer table names its next
    /// sub-table 0x11A bytes above it (0x14067FA98 -> 0x14187465A). Re-derive from the exe on disk
    /// with tools/probes/lw351_order_template_probe.py --disk.</summary>
    [Fact]
    public void Both_template_capacities_are_the_141_words_the_game_gives_them()
    {
        Assert.Equal(141, Offsets.InventoryOrderTemplateWords);
        Assert.Equal(141, Offsets.PickerOrderTemplateWords);
        Assert.Equal(0x1407B2550L, Offsets.InventoryOrderTemplate);
        Assert.Equal(0x141874540L, Offsets.PickerOrderTemplate);
        Assert.Equal((ushort)0x00FF, TemplateSeat.EndMarker);   // NOT the menu lists' 0xFFFF
    }

    // Round 8b: the filler tables below use DISTINCT ids (Range) because a table that lists
    // one id many times is now a damaged table the repair collapses, not a full one.

    /// <summary>The LW-346 hand poke, made policy: the id goes where the marker was and the marker
    /// moves one word on.</summary>
    [Fact]
    public void An_absent_id_is_written_over_the_end_marker_which_moves_one_word_on()
    {
        var seat = TemplateSeat.Plan(Table(141, 1, 2, 3, TemplateSeat.EndMarker), 141, new[] { Terra });

        Assert.Null(seat.Refusal);
        Assert.True(seat.Writes);
        Assert.Equal(3, seat.WordIndex);
        Assert.Equal(new byte[] { 0x06, 0x01, 0xFF, 0x00 }, seat.Bytes);
    }

    [Fact]
    public void A_template_that_already_lists_the_id_is_left_alone()
    {
        var seat = TemplateSeat.Plan(Table(141, 1, Terra, 3, TemplateSeat.EndMarker), 141, new[] { Terra });

        Assert.Null(seat.Refusal);
        Assert.False(seat.Writes);
        Assert.Null(seat.Bytes);
    }

    [Fact]
    public void Only_the_missing_ids_are_appended_and_they_go_in_id_order()
    {
        var seat = TemplateSeat.Plan(Table(141, 1, Moon, TemplateSeat.EndMarker), 141, new[] { Third, Moon, Terra });

        Assert.True(seat.Writes);
        Assert.Equal(2, seat.WordIndex);
        Assert.Equal(new ushort[] { Terra, Third, TemplateSeat.EndMarker }, Words(seat.Bytes!, 3));
    }

    /// <summary>The whole point of a derived capacity: the last word of the region may hold the
    /// marker and not one byte further is written.</summary>
    [Fact]
    public void An_id_that_exactly_fits_the_last_free_word_is_still_seated()
    {
        var region = Table(141, Enumerable.Range(1, 139).Append(TemplateSeat.EndMarker).ToArray());

        var seat = TemplateSeat.Plan(region, 141, new[] { Terra });

        Assert.Null(seat.Refusal);
        Assert.Equal(139, seat.WordIndex);
        Assert.Equal(new ushort[] { Terra, TemplateSeat.EndMarker }, Words(seat.Bytes!, 2));
    }

    [Fact]
    public void A_template_with_no_room_left_refuses_loudly_instead_of_writing_past_its_capacity()
    {
        var full = Table(141, Enumerable.Range(1, 140).Append(TemplateSeat.EndMarker).ToArray());

        var one = TemplateSeat.Plan(full, 141, new[] { Terra });
        Assert.False(one.Writes);
        Assert.NotNull(one.Refusal);
        Assert.Contains("141", one.Refusal);

        // Room for one id, two are owed: still a refusal, never a partial write past the end.
        var almost = Table(141, Enumerable.Range(1, 139).Append(TemplateSeat.EndMarker).ToArray());
        var two = TemplateSeat.Plan(almost, 141, new[] { Terra, Third });
        Assert.False(two.Writes);
        Assert.NotNull(two.Refusal);
    }

    [Fact]
    public void A_template_with_no_end_marker_is_refused_not_guessed_at()
    {
        var seat = TemplateSeat.Plan(Table(141, Enumerable.Range(1, 141).ToArray()), 141, new[] { Terra });

        Assert.False(seat.Writes);
        Assert.NotNull(seat.Refusal);
        Assert.Contains("00FF", seat.Refusal);
    }

    [Fact]
    public void Nothing_is_planned_when_no_ids_are_owned()
    {
        var seat = TemplateSeat.Plan(Table(141, 1, 2, TemplateSeat.EndMarker), 141, new int[0]);

        Assert.False(seat.Writes);
        Assert.Null(seat.Refusal);
    }

    /// <summary>The applier walks BOTH templates and writes through the guarded patcher only.</summary>
    // LW-351 round 8b (2026-08-31): REPAIR. The game's un-widened maintainer walks (fixed in
    // round 8) had already doubled entries and, on the owner's save, erased an end marker; the
    // widening stops new damage but heals nothing, because the game's rebuild re-emits every
    // doubled template entry and writes the list back. So Plan repairs first: doubled ids keep
    // their first occurrence, a zero word (id 0 is never an entry) goes, a missing marker lands at
    // the first zero word, and the whole table up to the new marker is rewritten in one write.

    [Fact]
    public void A_doubled_id_keeps_its_first_occurrence_and_the_whole_table_is_rewritten()
    {
        var region = Table(8, 1, 5, 2, 5, 3, TemplateSeat.EndMarker);

        var seat = TemplateSeat.Plan(region, 8, new[] { Terra });

        Assert.Null(seat.Refusal);
        Assert.Equal(0, seat.WordIndex);
        Assert.Equal(new ushort[] { 1, 5, 2, 3, Terra, TemplateSeat.EndMarker }, Words(seat.Bytes!, 6));
        Assert.NotNull(seat.Repaired);
        Assert.Contains("5", seat.Repaired);
    }

    [Fact]
    public void A_template_with_no_marker_gets_one_at_its_first_zero_word_and_stale_words_after_it_are_dropped()
    {
        var region = Table(8, 1, 2, 3, 0, 0, 9, 0, 0);   // no 0x00FF anywhere; a stale 9 past the zeros

        var seat = TemplateSeat.Plan(region, 8, new[] { Terra });

        Assert.Null(seat.Refusal);
        Assert.Equal(0, seat.WordIndex);
        Assert.Equal(new ushort[] { 1, 2, 3, Terra, TemplateSeat.EndMarker }, Words(seat.Bytes!, 5));
        Assert.Contains("marker", seat.Repaired);
    }

    [Fact]
    public void A_repair_runs_even_when_no_extended_id_is_owned()
    {
        var region = Table(8, 1, 2, 2, TemplateSeat.EndMarker);

        var seat = TemplateSeat.Plan(region, 8, new int[0]);

        Assert.True(seat.Writes);
        Assert.Equal(new ushort[] { 1, 2, TemplateSeat.EndMarker }, Words(seat.Bytes!, 3));
    }

    [Fact]
    public void A_zero_word_inside_a_marked_template_is_removed()
    {
        var region = Table(8, 1, 0, 2, TemplateSeat.EndMarker);

        var seat = TemplateSeat.Plan(region, 8, new int[0]);

        Assert.Equal(new ushort[] { 1, 2, TemplateSeat.EndMarker }, Words(seat.Bytes!, 3));
        Assert.Contains("zero", seat.Repaired);
    }

    [Fact]
    public void A_template_with_no_marker_and_no_zero_word_is_still_refused()
    {
        var region = Table(4, 1, 2, 3, 4);

        var seat = TemplateSeat.Plan(region, 4, new[] { Terra });

        Assert.NotNull(seat.Refusal);
        Assert.Contains("end marker", seat.Refusal);
        Assert.False(seat.Writes);
    }

    [Fact]
    public void A_clean_template_still_takes_the_append_only_path()
    {
        var region = Table(8, 1, 2, TemplateSeat.EndMarker);

        var seat = TemplateSeat.Plan(region, 8, new[] { Terra });

        Assert.Equal(2, seat.WordIndex);
        Assert.Equal(new ushort[] { Terra, TemplateSeat.EndMarker }, Words(seat.Bytes!, 2));
        Assert.Null(seat.Repaired);
    }

    [Fact]
    public void A_repair_that_would_not_fit_refuses_instead_of_writing_past_capacity()
    {
        var region = Table(4, 1, 1, 2, TemplateSeat.EndMarker);   // body 2 + two ids + marker = 5 > 4

        var seat = TemplateSeat.Plan(region, 4, new[] { Terra, Third });

        Assert.NotNull(seat.Refusal);
        Assert.False(seat.Writes);
    }

    [Fact]
    public void Apply_reports_a_repair_once_per_template_and_rewrites_it()
    {
        var f = new FakeCodePatcher();
        foreach (var r in TemplateSeat.WeaponRegions)
            f.Seed(r.Addr, Table(r.CapacityWords, 1, 7, 7, TemplateSeat.EndMarker));
        var repaired = new List<string>();

        TemplateSeat.Apply(f, new[] { Terra }, onRepaired: repaired.Add);

        Assert.Equal(2, repaired.Count);
        Assert.Contains("inventory order template", repaired[0]);
        Assert.Equal(new ushort[] { 1, 7, Terra, TemplateSeat.EndMarker }, WordsAt(f, Offsets.PickerOrderTemplate, 4));
    }

    /// <summary>Round 8c (verifier V8b-1): a damaged table must heal even on a save whose player
    /// owns no extended item at all; the crash shape (a marker-less inventory table yielding
    /// id-0 rows) does not care whether a design is owned.</summary>
    [Fact]
    public void Apply_repairs_a_doubled_template_even_when_no_id_is_owned()
    {
        var f = new FakeCodePatcher();
        foreach (var r in TemplateSeat.WeaponRegions)
            f.Seed(r.Addr, Table(r.CapacityWords, 1, 7, 7, TemplateSeat.EndMarker));
        var repaired = new List<string>();

        TemplateSeat.Apply(f, new int[0], onRepaired: repaired.Add);

        Assert.Equal(2, repaired.Count);
        Assert.Equal(2, f.Writes.Count);
        foreach (var r in TemplateSeat.WeaponRegions)
            Assert.Equal(new ushort[] { 1, 7, TemplateSeat.EndMarker }, WordsAt(f, r.Addr, 3));
    }

    [Fact]
    public void Apply_leaves_a_clean_template_alone_when_no_id_is_owned()
    {
        var f = new FakeCodePatcher();
        foreach (var r in TemplateSeat.WeaponRegions)
            f.Seed(r.Addr, Table(r.CapacityWords, 1, 2, TemplateSeat.EndMarker));
        var noise = new List<string>();

        TemplateSeat.Apply(f, new int[0], noise.Add, noise.Add, noise.Add);

        Assert.Empty(f.Writes);
        Assert.Empty(noise);
    }

    /// <summary>Round 8c (verifier V8b-4): the dedupe and the "already listed" check must agree
    /// on what an id is. A second copy carrying a flag bit (0x505 is 261 with bit 10 set) is a
    /// double of 261 by the game's own 0x3FF mask; it is removed once and 261 is NOT appended
    /// again, or every later pass would remove and re-add it forever.</summary>
    [Fact]
    public void A_flagged_double_is_collapsed_by_masked_id_and_the_id_is_not_re_added()
    {
        var region = Table(8, 0x505, 0x105, TemplateSeat.EndMarker);

        var seat = TemplateSeat.Plan(region, 8, new[] { Moon });

        Assert.Null(seat.Refusal);
        Assert.True(seat.Writes);
        Assert.Equal(new ushort[] { 0x505, TemplateSeat.EndMarker }, Words(seat.Bytes!, 2));
        Assert.NotNull(seat.Repaired);

        var again = TemplateSeat.Plan(Table(8, 0x505, TemplateSeat.EndMarker), 8, new[] { Moon });
        Assert.False(again.Writes);
        Assert.Null(again.Refusal);
    }

    [Fact]
    public void Apply_seats_the_ids_in_both_weapon_templates()
    {
        var f = new FakeCodePatcher();
        foreach (var r in TemplateSeat.WeaponRegions) f.Seed(r.Addr, Table(r.CapacityWords, 1, 2, 3, TemplateSeat.EndMarker));
        var seated = new List<string>();

        TemplateSeat.Apply(f, new[] { Moon, Terra }, why => Assert.Fail(why), seated.Add);

        foreach (var r in TemplateSeat.WeaponRegions)
            Assert.Equal(new ushort[] { 1, 2, 3, Moon, Terra, TemplateSeat.EndMarker }, WordsAt(f, r.Addr, 6));
        Assert.Equal(2, f.Writes.Count);
        Assert.Equal(Offsets.InventoryOrderTemplate + 6, f.Writes[0].Addr);
        Assert.Equal(Offsets.PickerOrderTemplate + 6, f.Writes[1].Addr);
        Assert.Equal(2, seated.Count);

        // Running it again is a no-op: the ids are all there, so not one further byte is written.
        TemplateSeat.Apply(f, new[] { Moon, Terra }, why => Assert.Fail(why), seated.Add);
        Assert.Equal(2, f.Writes.Count);
        Assert.Equal(2, seated.Count);
    }

    /// <summary>A template that cannot be read is left alone silently: the same fail-safe posture
    /// the order-rebuild hook takes when its read fails.</summary>
    [Fact]
    public void An_unreadable_template_is_left_alone_without_a_word()
    {
        var f = new FakeCodePatcher();
        var noise = new List<string>();

        TemplateSeat.Apply(f, new[] { Terra }, noise.Add, noise.Add);

        Assert.Empty(f.Writes);
        Assert.Empty(noise);
    }

    [Fact]
    public void A_refused_write_is_reported_and_the_other_template_is_still_seated()
    {
        var f = new FakeCodePatcher();
        foreach (var r in TemplateSeat.WeaponRegions) f.Seed(r.Addr, Table(r.CapacityWords, 1, TemplateSeat.EndMarker));
        f.RefuseWritesAt.Add(Offsets.InventoryOrderTemplate + 2);
        var refused = new List<string>();

        TemplateSeat.Apply(f, new[] { Terra }, refused.Add);

        Assert.Single(refused);
        Assert.Contains("inventory order template", refused[0]);
        Assert.Equal(new ushort[] { 1, Terra, TemplateSeat.EndMarker }, WordsAt(f, Offsets.PickerOrderTemplate, 3));
    }

    [Fact]
    public void A_template_without_room_reports_which_one_ran_out()
    {
        var f = new FakeCodePatcher();
        foreach (var r in TemplateSeat.WeaponRegions)
            f.Seed(r.Addr, Table(r.CapacityWords, Enumerable.Range(1, r.CapacityWords - 1).Append(TemplateSeat.EndMarker).ToArray()));
        var refused = new List<string>();

        TemplateSeat.Apply(f, new[] { Terra }, refused.Add);

        Assert.Equal(2, refused.Count);
        Assert.Contains("0x1407B2550", refused[0]);
        Assert.Contains("0x141874540", refused[1]);
        Assert.Empty(f.Writes);
    }

    /// <summary>LW-371 (T8, half a): a caller passing <paramref name="regions"/> is served THOSE
    /// regions, not <see cref="TemplateSeat.WeaponRegions"/> -- proven by seating into a page
    /// address that is nowhere near either vanilla template. Omitting the parameter (every
    /// pre-existing test above) keeps behaving exactly as it always has.</summary>
    [Fact]
    public void Apply_seats_into_the_regions_parameter_when_one_is_given()
    {
        var page = new TemplateSeat.Region[]
        {
            new(0x150000000L, 511, "the relocated inventory order template"),
            new(0x150000400L, 511, "the relocated equip-picker order template"),
        };
        var f = new FakeCodePatcher();
        foreach (var r in page) f.Seed(r.Addr, Table(r.CapacityWords, 1, 2, TemplateSeat.EndMarker));
        // The vanilla addresses are deliberately left UNSEEDED: if Apply ever fell back to
        // WeaponRegions despite the parameter, its guarded read would fail there and this test
        // would show zero writes instead of the wrong-address ones.
        var seated = new List<string>();

        TemplateSeat.Apply(f, new[] { Terra }, why => Assert.Fail(why), seated.Add, regions: page);

        Assert.Equal(2, seated.Count);
        foreach (var r in page)
            Assert.Equal(new ushort[] { 1, 2, Terra, TemplateSeat.EndMarker }, WordsAt(f, r.Addr, 4));
        Assert.DoesNotContain(f.Writes, w => w.Addr == Offsets.InventoryOrderTemplate || w.Addr == Offsets.PickerOrderTemplate);
    }
}
