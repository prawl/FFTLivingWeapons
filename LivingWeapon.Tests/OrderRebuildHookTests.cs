using System;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>LW-346 S2: the order-rebuild hook's pure list policy and its whole detour body
/// (Process with a lambda standing in for the game's own rebuild). Ported from the rig's
/// OrderRebuildHookTests (FFTHandsFree b1abd77).</summary>
public class OrderRebuildHookTests
{
    private static readonly nint List = (nint)0x141811470;   // the live inventory buffer address, any value works
    private const long Bag = Offsets.BagCountArray;
    private static readonly nint Picker = (nint)Offsets.PickerOrderTemplate;

    private static byte[] Words(params ushort[] words)
    {
        var b = new byte[words.Length * 2];
        for (int i = 0; i < words.Length; i++) { b[i * 2] = (byte)(words[i] & 0xFF); b[i * 2 + 1] = (byte)(words[i] >> 8); }
        return b;
    }

    private static FakeCodePatcher WithList(params ushort[] words)
    {
        var f = new FakeCodePatcher { ZeroFillUnseeded = true };
        f.Seed(List, Words(words));
        return f;
    }

    /// <summary>A stand-in for the game's rebuild: overwrite the list with <paramref name="after"/>
    /// (terminated) and return its count, exactly as the routine does.</summary>
    private static OrderRebuildHook.RebuildFn Rebuild(FakeCodePatcher f, params ushort[] after)
        => (table, list) =>
        {
            var w = new ushort[after.Length + 1];
            Array.Copy(after, w, after.Length);
            w[^1] = OrderRebuildHook.Terminator;
            f.TryWrite((long)list, Words(w));
            return after.Length;
        };

    [Fact]
    public void ParseList_stops_at_the_terminator_and_refuses_a_buffer_without_one()
    {
        Assert.Equal(new ushort[] { 1, 0x105, 3 }, OrderRebuildHook.ParseList(Words(1, 0x105, 3, 0xFFFF, 9)));
        Assert.Empty(OrderRebuildHook.ParseList(Words(0xFFFF, 5))!);
        Assert.Null(OrderRebuildHook.ParseList(Words(1, 2, 3)));
    }

    [Fact]
    public void DroppedWords_keeps_input_order_flags_and_reports_each_id_once()
    {
        var dropped = OrderRebuildHook.DroppedWords(new ushort[] { 1, 0x4105, 2, 0x106, 0x105 }, new ushort[] { 2, 1 });
        Assert.Equal(new ushort[] { 0x4105, 0x106 }, dropped);   // 0x105 appears once, first occurrence's flags kept
        Assert.Empty(OrderRebuildHook.DroppedWords(new ushort[] { 1, 2 }, new ushort[] { 0x4002, 1 }));   // masked compare
    }

    [Fact]
    public void TailBytes_is_the_dropped_words_then_the_terminator()
        => Assert.Equal(new byte[] { 0x05, 0x01, 0x06, 0x41, 0xFF, 0xFF }, OrderRebuildHook.TailBytes(new ushort[] { 0x105, 0x4106 }));

    [Fact]
    public void Process_reappends_what_the_rebuild_dropped_and_corrects_the_count()
    {
        var f = WithList(1, 2, 0x4105, 3, 0xFFFF);
        f.Seed(Bag + 0x105, 1);   // owned: the fix-round-7 filter re-appends only ids the bag holds
        var hook = new OrderRebuildHook(f);
        int count = hook.Process((nint)0x1407B2550, List, Rebuild(f, 1, 2, 3));
        Assert.Equal(4, count);
        Assert.Equal(new ushort[] { 1, 2, 3, 0x4105 }, OrderRebuildHook.ParseList(f.Read(List, 12)));
        Assert.Equal(1, hook.Reappended);
    }

    [Fact]
    public void Process_passes_through_when_nothing_was_dropped_or_the_list_is_unreadable()
    {
        var f = WithList(1, 2, 0xFFFF);
        var hook = new OrderRebuildHook(f);
        Assert.Equal(2, hook.Process(0, List, Rebuild(f, 2, 1)));
        Assert.Equal(0, hook.Reappended);

        var blind = new FakeCodePatcher();   // nothing readable at all
        int calls = 0;
        Assert.Equal(7, new OrderRebuildHook(blind).Process(0, List, (_, __) => { calls++; return 7; }));
        Assert.Equal(1, calls);   // the original runs exactly once even on passthrough
    }

    [Fact]
    public void Process_refuses_to_grow_the_list_past_its_own_input_footprint()
    {
        var f = WithList(0x105, 0xFFFF);
        f.Seed(Bag + 0x105, 1);
        var hook = new OrderRebuildHook(f);
        // A pathological rebuild that returns MORE words than it was given: re-appending would
        // write past the input footprint, so the hook declines and returns the game's answer.
        Assert.Equal(2, hook.Process(0, List, Rebuild(f, 1, 2)));
        Assert.Equal(new ushort[] { 1, 2 }, OrderRebuildHook.ParseList(f.Read(List, 8)));
        Assert.Equal(0, hook.Reappended);
    }

    [Fact]
    public void Process_returns_the_games_count_when_the_reappend_write_is_refused()
    {
        var f = WithList(0x105, 1, 0xFFFF);
        f.Seed(Bag + 0x105, 1);
        f.RefuseWritesAt.Add(List + 2);   // the tail write lands right after the one kept word
        Assert.Equal(1, new OrderRebuildHook(f).Process(0, List, Rebuild(f, 1)));
    }

    [Fact]
    public void ShouldArm_requires_the_live_prologue()
    {
        Assert.True(OrderRebuildHook.ShouldArm(true, new byte[] { 0x48, 0x89, 0x5C, 0x24, 0x08, 0x48, 0x89, 0x6C, 0x24, 0x18, 0x48, 0x89 }));
        Assert.False(OrderRebuildHook.ShouldArm(true, new byte[] { 0x48, 0x89, 0x5C, 0x24, 0x08, 0x48, 0x89, 0x6C, 0x24, 0x19 }));
        Assert.False(OrderRebuildHook.ShouldArm(false, OrderRebuildHook.ExpectedPrologue));
        Assert.Equal(Offsets.FnOrderRebuild, new OrderRebuildHook(new FakeCodePatcher()).TargetAddr);
    }

    // --- LW-351 fix round 7 (R7-3): seat owned ids BEFORE the rebuild; never re-append an unowned id ---

    private static ushort[] TemplateWords(FakeCodePatcher f, long addr)
    {
        var words = new System.Collections.Generic.List<ushort>();
        var b = f.Read(addr, Offsets.PickerOrderTemplateWords * 2);
        for (int i = 0; i < Offsets.PickerOrderTemplateWords; i++)
        {
            ushort w = (ushort)(b[i * 2] | (b[i * 2 + 1] << 8));
            if (w == TemplateSeat.EndMarker) break;
            words.Add(w);
        }
        return words.ToArray();
    }

    [Fact]
    public void Process_seats_owned_extended_ids_into_a_known_template_before_the_rebuild_runs()
    {
        var f = WithList(1, 0xFFFF);
        f.Seed((long)Picker, Words(5, TemplateSeat.EndMarker));
        f.Seed(Bag + 261, 2, 0, 1);   // 261 owned, 262 not, 263 owned
        var hook = new OrderRebuildHook(f, extendedCount: 3);
        ushort[]? seen = null;
        hook.Process(Picker, List, (table, list) => { seen = TemplateWords(f, (long)table); return Rebuild(f, 1)(table, list); });
        Assert.Equal(new ushort[] { 5, 261, 263 }, seen);
        Assert.Equal(2, hook.Seated);
        // idempotent: the next rebuild finds them seated and writes nothing more to the template
        int writes = f.Writes.Count;
        hook.Process(Picker, List, Rebuild(f, 1));
        Assert.Equal(new ushort[] { 5, 261, 263 }, TemplateWords(f, (long)Picker));
        Assert.DoesNotContain(f.Writes.GetRange(writes, f.Writes.Count - writes), w => w.Addr >= (long)Picker && w.Addr < (long)Picker + Offsets.PickerOrderTemplateWords * 2);
    }

    [Fact]
    public void Process_repairs_a_doubled_template_before_the_rebuild_runs()
    {
        // Round 8b: a template the un-widened maintainer already doubled (5 twice) is healed in
        // place before the game's rebuild reads it, owned ids appended after the repair.
        var f = WithList(1, 0xFFFF);
        f.Seed((long)Picker, Words(5, 5, 9, TemplateSeat.EndMarker));
        f.Seed(Bag + 261, 1);
        var hook = new OrderRebuildHook(f, extendedCount: 1);
        ushort[]? seen = null;
        hook.Process(Picker, List, (table, list) => { seen = TemplateWords(f, (long)table); return Rebuild(f, 1)(table, list); });
        Assert.Equal(new ushort[] { 5, 9, 261 }, seen);
        Assert.Equal(1, hook.Repaired);
    }

    [Fact]
    public void Process_repairs_a_doubled_template_even_when_no_extended_id_is_owned()
    {
        // Round 8c (verifier V8b-1): the repair is not gated on ownership; only the seat is.
        var f = WithList(1, 0xFFFF);
        f.Seed((long)Picker, Words(5, 5, 9, TemplateSeat.EndMarker));
        f.Seed(Bag + 261, 0);
        var hook = new OrderRebuildHook(f, extendedCount: 1);
        ushort[]? seen = null;
        hook.Process(Picker, List, (table, list) => { seen = TemplateWords(f, (long)table); return Rebuild(f, 1)(table, list); });
        Assert.Equal(new ushort[] { 5, 9 }, seen);
        Assert.Equal(1, hook.Repaired);
    }

    [Fact]
    public void Process_writes_nothing_to_a_clean_template_when_no_extended_id_is_owned()
    {
        var f = WithList(1, 0xFFFF);
        f.Seed((long)Picker, Words(5, 9, TemplateSeat.EndMarker));
        f.Seed(Bag + 261, 0);
        var hook = new OrderRebuildHook(f, extendedCount: 1);
        int writes = f.Writes.Count;
        hook.Process(Picker, List, Rebuild(f, 1));
        Assert.Equal(new ushort[] { 5, 9 }, TemplateWords(f, (long)Picker));
        Assert.DoesNotContain(f.Writes.GetRange(writes, f.Writes.Count - writes), w => w.Addr >= (long)Picker && w.Addr < (long)Picker + Offsets.PickerOrderTemplateWords * 2);
        Assert.Equal(0, hook.Repaired);
    }

    [Fact]
    public void Process_never_reappends_an_id_the_player_owns_none_of()
    {
        // "Owns none" = bag 0 AND no E badge on the word (a badged word is worn, hence owned;
        // see Process_reappends_an_equipped_extended_id_even_when_the_bag_holds_none).
        var f = WithList(1, 0x105, 0x106, 0xFFFF);
        f.Seed(Bag + 261, 0, 3);   // 261 gone from the bag and not worn, 262 owned
        var hook = new OrderRebuildHook(f);
        int count = hook.Process(0, List, Rebuild(f, 1));
        Assert.Equal(2, count);
        Assert.Equal(new ushort[] { 1, 0x106 }, OrderRebuildHook.ParseList(f.Read(List, 8)));
        Assert.Equal(1, hook.Reappended);
    }

    [Fact]
    public void Process_reappends_an_equipped_extended_id_even_when_the_bag_holds_none()
    {
        // LW-351 round 7 verify (F15): a design whose every copy is worn (bag 0) carries the E
        // badge (bit 14) on its list word; the game's rebuild can still drop it when the id is
        // not in the saved template yet, and the owned-only filter must not orphan it.
        var f = WithList(1, 0x4105, 0xFFFF);
        f.Seed(Bag + 261, 0);   // no copy in the bag, the word itself says "equipped"
        var hook = new OrderRebuildHook(f);
        int count = hook.Process(0, List, Rebuild(f, 1));
        Assert.Equal(2, count);
        Assert.Equal(new ushort[] { 1, 0x4105 }, OrderRebuildHook.ParseList(f.Read(List, 8)));
        Assert.Equal(1, hook.Reappended);
    }

    [Fact]
    public void Process_does_not_seat_into_a_table_that_is_not_a_known_template()
    {
        var f = WithList(1, 0xFFFF);
        var stranger = (nint)0x1418A0000;
        f.Seed((long)stranger, Words(5, TemplateSeat.EndMarker));
        f.Seed((long)Picker, Words(5, TemplateSeat.EndMarker));
        f.Seed(Bag + 261, 2);
        var hook = new OrderRebuildHook(f, extendedCount: 1);
        hook.Process(stranger, List, Rebuild(f, 1));
        Assert.Equal(new ushort[] { 5 }, TemplateWords(f, (long)stranger));
        Assert.Equal(new ushort[] { 5 }, TemplateWords(f, (long)Picker));
        Assert.Equal(0, hook.Seated);
    }

    [Fact]
    public void Process_leaves_a_full_template_untouched()
    {
        var f = WithList(1, 0xFFFF);
        var full = new ushort[Offsets.PickerOrderTemplateWords];
        for (int i = 0; i < full.Length - 1; i++) full[i] = (ushort)(i + 1);
        full[^1] = TemplateSeat.EndMarker;   // marker on the last word: no room for anything
        f.Seed((long)Picker, Words(full));
        f.Seed(Bag + 261, 2);
        var hook = new OrderRebuildHook(f, extendedCount: 1);
        hook.Process(Picker, List, Rebuild(f, 1));
        Assert.Equal(Words(full), f.Read((long)Picker, full.Length * 2));
        Assert.Equal(0, hook.Seated);
        Assert.Equal(1, hook.SeatRefusals);
    }

    /// <summary>LW-368 round 2 (T11): ownership reads through an injected base, not the vanilla
    /// constant -- an id owned at the vanilla block but not at the injected base must NOT seat.</summary>
    [Fact]
    public void Owned_reads_through_an_injected_bag_base()
    {
        const long altBase = 0x150000000L;
        var f = WithList(1, 0xFFFF);
        f.Seed((long)Picker, Words(5, TemplateSeat.EndMarker));
        f.Seed(Bag + 261, 9);            // owned at the VANILLA block: must be ignored
        f.Seed(altBase + 261, 2);        // owned at the INJECTED base: must be the one that seats
        var hook = new OrderRebuildHook(f, extendedCount: 1, bagBase: altBase);

        ushort[]? seen = null;
        hook.Process(Picker, List, (table, list) => { seen = TemplateWords(f, (long)table); return Rebuild(f, 1)(table, list); });

        Assert.Equal(new ushort[] { 5, 261 }, seen);
        Assert.Equal(1, hook.Seated);
    }

    /// <summary>LW-371 (T8, half b): the seat step looks up its regions through the injected
    /// <c>regions</c> getter, not <see cref="TemplateSeat.WeaponRegions"/> -- once a page address
    /// is injected, that page is "known" and seats, and the vanilla address is not (it is left
    /// untouched even when passed as the table), matching what the game itself does once the
    /// pointer slot has been re-pointed at the page.</summary>
    [Fact]
    public void Process_seats_into_a_page_table_address_and_ignores_the_vanilla_address_once_relocated()
    {
        const long pageAddr = 0x150000000L;
        var page = (nint)pageAddr;
        var f = WithList(1, 0xFFFF);
        f.Seed(pageAddr, Words(5, TemplateSeat.EndMarker));
        f.Seed((long)Picker, Words(9, TemplateSeat.EndMarker));   // the vanilla address: left alone once relocated
        f.Seed(Bag + 261, 2);
        TemplateSeat.Region[] Page() => new[] { new TemplateSeat.Region(pageAddr, 511, "the relocated picker order template") };
        var hook = new OrderRebuildHook(f, extendedCount: 1, regions: Page);

        ushort[]? seen = null;
        hook.Process(page, List, (table, list) => { seen = TemplateWords(f, (long)table); return Rebuild(f, 1)(table, list); });

        Assert.Equal(new ushort[] { 5, 261 }, seen);
        Assert.Equal(1, hook.Seated);
        Assert.Equal(new ushort[] { 9 }, TemplateWords(f, (long)Picker));   // the vanilla address never took the write

        // The vanilla address is no longer "known" once regions is injected: passing it as the
        // table seats nothing at all.
        var hook2 = new OrderRebuildHook(f, extendedCount: 1, regions: Page);
        hook2.Process(Picker, List, Rebuild(f, 1));
        Assert.Equal(0, hook2.Seated);
        Assert.Equal(new ushort[] { 9 }, TemplateWords(f, (long)Picker));
    }
}
