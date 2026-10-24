using System.Collections.Generic;
using System.Linq;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

public class CardTextWatchTests
{
    [Theory]
    [InlineData(2, false, false, 120.0, true)]
    [InlineData(5, false, false, 600.0, true)]
    [InlineData(1, false, false, 600.0, false)]
    [InlineData(2, false, false, 119.9, false)]
    [InlineData(2, true, false, 600.0, false)]
    [InlineData(2, false, true, 600.0, false)]
    public void Policy_table(int walks, bool region, bool site, double seconds, bool expected)
        => Assert.Equal(expected, CardTextWatch.ShouldReport(walks, region, site, seconds));

    [Fact]
    public void Two_empty_walks_past_two_minutes_fire_once_and_a_third_does_not_fire_again()
    {
        using var cap = LogCapture.Start();
        var watch = new CardTextWatch();

        Assert.False(watch.Observe(0, false, 10));
        Assert.True(watch.Observe(0, false, 130));
        Assert.False(watch.Observe(0, false, 200));
        Assert.Equal(3, watch.EmptyWalks);
        Assert.Single(cap.File, l => l.Contains("[WARN]"));
    }

    [Fact]
    public void A_region_found_before_blocks_it_for_the_session()
    {
        var watch = new CardTextWatch();
        watch.Observe(3, false, 5);
        Assert.False(watch.Observe(0, false, 130));
        Assert.False(watch.Observe(0, false, 300));
        Assert.Equal(DoorState.Open, watch.Door.State);
    }

    [Fact]
    public void A_region_found_after_blocks_it_for_the_session()
    {
        var watch = new CardTextWatch();
        watch.Observe(0, false, 10);
        watch.Observe(1, false, 20);
        Assert.False(watch.Observe(0, false, 130));
        Assert.False(watch.Observe(0, false, 300));
    }

    [Fact]
    public void A_painted_site_blocks_it_for_the_session()
    {
        var watch = new CardTextWatch();
        watch.Observe(0, true, 10);
        Assert.False(watch.Observe(0, false, 130));
        Assert.False(watch.Observe(0, false, 300));
    }

    [Fact]
    public void The_door_is_open_before_and_closed_with_the_reason_after()
    {
        using var cap = LogCapture.Start();
        var watch = new CardTextWatch();
        Assert.Equal(DoorState.Open, watch.Door.State);

        watch.Observe(0, false, 10);
        Assert.Equal(DoorState.Open, watch.Door.State);
        watch.Observe(0, false, 130);

        Assert.Equal(DoorState.Closed, watch.Door.State);
        Assert.False(watch.Door.FailedCheck);
        Assert.Equal("another mod's item text, or a game language other than English, left no place to draw the kill counter", watch.Door.Reason);
    }

    [Fact]
    public void The_warning_carries_the_player_text_word_for_word()
    {
        using var cap = LogCapture.Start();
        var watch = new CardTextWatch();
        watch.Observe(0, false, 10);
        watch.Observe(0, false, 130);

        string line = Assert.Single(cap.File, l => l.Contains("[WARN]"));
        Assert.Contains("The kill counter on weapon cards is hidden this session because another mod replaced the item descriptions, "
            + "or the game is not in English, so there is no place to draw it; kills, growth and every weapon power keep working, "
            + "and the counts come back once the descriptions are ours again. (detail: empty-walks=2 seconds=130)", line);
        Assert.DoesNotContain(PartsCopy.Hedge, line);
        Assert.DoesNotContain(PartsCopy.WhatToDo, line);
    }

    [Fact]
    public void The_board_names_the_card_counter_part_off_with_the_reason()
    {
        var watch = new CardTextWatch();
        var r = PartsReportTests.AllOpen();
        Assert.True(PartsReport.IsOn(Part.CardCounter, r));
        using var cap = LogCapture.Start();
        watch.Observe(0, false, 10);
        watch.Observe(0, false, 130);
        r[Door.CardText] = watch.Door;

        string line = PartsReport.ComposeLine(r);
        string label = PartsReport.LabelOf(Part.CardCounter);
        Assert.Contains($"{label} OFF ({watch.Door.Reason})", line);
        Assert.True(PartsReport.IsOn(Part.CardPaint, r));
    }

    [Fact]
    public void Roster_closed_with_the_card_text_door_open_does_not_list_the_counter_as_working()
    {
        var r = PartsReportTests.AllOpen();
        r[Door.Roster] = DoorReading.Failed("the party data moved");

        Assert.False(PartsReport.IsOn(Part.CardCounter, r));
        Assert.False(PartsReport.IsOn(Part.CardPaint, r));
        string box = PartsReport.ComposeBox(r, "", "", "");
        int working = box.IndexOf("Still working:", System.StringComparison.Ordinal);
        int paused = box.IndexOf("Paused this session:", System.StringComparison.Ordinal);
        Assert.True(paused > working);
        Assert.DoesNotContain(PartsReport.LabelOf(Part.CardCounter), box.Substring(working, paused - working));
    }

    private static DoorReading FiredDoor()
    {
        var watch = new CardTextWatch();
        watch.Observe(0, false, 10);
        watch.Observe(0, false, 130);
        return watch.Door;
    }

    [Fact]
    public void A_card_text_closure_alone_opens_no_box_and_the_report_line_shows_the_counter_paused()
    {
        using var cap = LogCapture.Start();
        var notices = new List<(string title, string text)>();
        var board = new PartsBoard((t, x) => notices.Add((t, x)), null);
        var r = PartsReportTests.AllOpen();
        r[Door.CardText] = FiredDoor();

        for (int i = 0; i < PartsBoard.SettleSteps + 5; i++) board.Observe(r, "", "", () => "footer");

        Assert.Equal(0, board.BoxCount);
        Assert.Empty(notices);
        Assert.False(board.WaveOpen);
        Assert.Contains($"{PartsReport.LabelOf(Part.CardCounter)} OFF", board.LastLine);
    }

    [Fact]
    public void A_card_text_closure_beside_a_real_failure_is_listed_under_paused_in_the_one_box()
    {
        using var cap = LogCapture.Start();
        var notices = new List<(string title, string text)>();
        var board = new PartsBoard((t, x) => notices.Add((t, x)), null);
        var r = PartsReportTests.AllOpen();
        r[Door.CardText] = FiredDoor();
        r[Door.Band] = DoorReading.Failed("the band moved");

        for (int i = 0; i < PartsBoard.SettleSteps + 5; i++) board.Observe(r, "", "", () => "footer");

        Assert.Equal(1, board.BoxCount);
        string box = Assert.Single(notices).text;
        int paused = box.IndexOf("Paused this session:", System.StringComparison.Ordinal);
        Assert.True(paused >= 0);
        Assert.Contains(PartsReport.LabelOf(Part.CardCounter), box.Substring(paused));
        Assert.Contains(CardTextWatch.Reason, board.LastLine);
    }

    [Fact]
    public void A_band_failure_alone_does_not_list_the_kill_counter_row_under_paused()
    {
        using var cap = LogCapture.Start();
        var notices = new List<(string title, string text)>();
        var board = new PartsBoard((t, x) => notices.Add((t, x)), null);
        var r = PartsReportTests.AllOpen();
        r[Door.CardText] = DoorReading.Open();
        r[Door.Band] = DoorReading.Failed("the band moved");

        for (int i = 0; i < PartsBoard.SettleSteps + 5; i++) board.Observe(r, "", "", () => "footer");

        string box = Assert.Single(notices).text;
        int paused = box.IndexOf("Paused this session:", System.StringComparison.Ordinal);
        Assert.True(paused >= 0);
        Assert.DoesNotContain(PartsReport.LabelOf(Part.CardCounter), box.Substring(paused));
    }

    private static (Display display, TestClock clock, List<(string type, string payload)> recorded) EmptyHeapDisplay()
    {
        var meta = new Dictionary<int, WeaponMeta> { { 10, new WeaponMeta { Name = "BowX", Flavor = "Fletched with regret" } } };
        var clock = new TestClock();
        var heap = new FakeHeap((0x77_0000_0000L, new byte[64], true));
        var recorded = new List<(string type, string payload)>();
        var display = CardFixtures.MakeDisplay(meta, new Dictionary<int, int>(), heap, 0x77_0000_0000L, clock, poolPaint: true,
            recorder: (t, p) => recorded.Add((t, p)));
        return (display, clock, recorded);
    }

    [Fact]
    public void A_display_that_never_finds_a_pool_closes_the_door_and_records_the_flight_line_once()
    {
        using var cap = LogCapture.Start();
        var (display, clock, recorded) = EmptyHeapDisplay();
        display.Tick(false);
        Assert.Equal(DoorState.Open, display.CardTextDoor.State);

        for (int i = 0; i < 400; i++)
        {
            clock.Ms += 1000;
            display.StepPoolLocate(false);
        }

        Assert.Equal(DoorState.Closed, display.CardTextDoor.State);
        var line = Assert.Single(recorded, r => r.payload.StartsWith("card-text hidden"));
        Assert.Matches(@"^card-text hidden empty-walks=\d+ seconds=\d+$", line.payload);
    }

    [Fact]
    public void A_display_with_a_real_pool_keeps_the_door_open()
    {
        var meta = new Dictionary<int, WeaponMeta> { { 10, new WeaponMeta { Name = "BowX", Flavor = "Fletched with regret" } } };
        var clock = new TestClock();
        var poolBuf = new byte[500];
        CardFixtures.WriteCardForwardWithName(poolBuf, 0, "BowX", "Fletched with regret");
        var heap = new FakeHeap((0x76_0000_0000L, poolBuf, true), (0x77_0000_0000L, new byte[64], true));
        var display = CardFixtures.MakeDisplay(meta, new Dictionary<int, int> { { 10, 7 } }, heap, 0x77_0000_0000L, clock, poolPaint: true);
        display.Tick(false);

        for (int i = 0; i < 400; i++)
        {
            clock.Ms += 1000;
            display.StepPoolLocate(false);
        }

        Assert.Equal(DoorState.Open, display.CardTextDoor.State);
    }
}
