using System.Collections.Generic;
using System.IO;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-91 Stage 2: Display.PaintCountsIfChanged, the narrow in-battle repaint Engine calls on the
/// ticks ShouldPaintCard skips (Engine.cs's new else branch on the bare card-paint if). Mirrors
/// the full Tick's count-change edge (CheckAndSnapshotCounts -> RecomposeChanged -> RequestRescan
/// -> PaintAll, Display.cs:145-157's own ordering) without any pool/sweep locate work or sweep
/// stepping, so a mid-battle kill repaints the equip card's Kills meter without waiting for the
/// post-battle settle window (ShouldPaintCard gates the full Tick to out-of-battle/paused frames).
/// </summary>
public class DisplayPaintCountsIfChangedTests
{
    private const long StaticsBase = 0x42_0000_0000L;
    private const long SourceBase  = 0x43_0000_0000L;

    private static Dictionary<int, WeaponMeta> BuildMeta() => new()
    {
        { 10, new WeaponMeta { Name = "SwordA", Flavor = "Bright edge of dawn", Wp = 12, Cat = "Sword", Formula = 1 } },
    };

    private static (FakeHeap heap, (int suffixPos, int flavorPos, int killsSlotPos) card)
        BuildFixture(int mirrorId)
    {
        var src = new byte[512];
        var card = CardFixtures.WriteCard(src, 0, "SwordA", "Bright edge of dawn");

        var statics = new byte[64];
        statics[0] = (byte)(mirrorId & 0xFF);
        statics[1] = (byte)(mirrorId >> 8);

        var heap = new FakeHeap((SourceBase, src), (StaticsBase, statics));
        return (heap, card);
    }

    private static string ReadSlot(FakeHeap heap, long pos, int len)
    {
        heap.TryReadBytes(SourceBase + pos, len, out var buf);
        return System.Text.Encoding.ASCII.GetString(buf);
    }

    // ─── T12: the narrow count-change edge itself ──────────────────────────────

    [Fact]
    public void Count_bump_with_a_populated_cache_repaints_the_site()
    {
        var meta = BuildMeta();
        var kills = new Dictionary<int, int> { { 10, 5 } };
        var clock = new TestClock();
        var (heap, card) = BuildFixture(mirrorId: 10);
        var display = CardFixtures.MakeDisplay(meta, kills, heap, StaticsBase, clock);

        CardFixtures.DrainGeneration(display, clock, 500);
        Assert.Equal(Signatures.KillsMeterSlot(5), ReadSlot(heap, card.killsSlotPos, Signatures.KillsMeterSlotChars));

        kills[10] = 8;
        display.PaintCountsIfChanged();

        Assert.Equal(Signatures.KillsMeterSlot(8), ReadSlot(heap, card.killsSlotPos, Signatures.KillsMeterSlotChars));
    }

    [Fact]
    public void No_change_tick_issues_zero_writes()
    {
        var meta = BuildMeta();
        var kills = new Dictionary<int, int> { { 10, 5 } };
        var clock = new TestClock();
        var (heap, _) = BuildFixture(mirrorId: 10);
        var display = CardFixtures.MakeDisplay(meta, kills, heap, StaticsBase, clock);

        CardFixtures.DrainGeneration(display, clock, 500);
        int writesBefore = heap.Writes;

        display.PaintCountsIfChanged();   // no kill count changed since the last snapshot

        Assert.Equal(writesBefore, heap.Writes);
    }

    [Fact]
    public void Empty_cache_issues_zero_writes_and_never_steps_the_sweep()
    {
        var meta = BuildMeta();
        var kills = new Dictionary<int, int> { { 10, 5 } };
        var clock = new TestClock();
        var (heap, _) = BuildFixture(mirrorId: 10);
        var display = CardFixtures.MakeDisplay(meta, kills, heap, StaticsBase, clock);

        // No Tick has ever run: the site cache is empty and the sweep has not started walking.
        long genBefore = display._sweep.Generation;
        bool completeBefore = display._sweep.IsComplete;
        int writesBefore = heap.Writes;

        kills[10] = 9;   // a real count change, but nothing is cached to paint yet
        display.PaintCountsIfChanged();

        Assert.Equal(writesBefore, heap.Writes);
        Assert.Equal(genBefore, display._sweep.Generation);      // no sweep step
        Assert.Equal(completeBefore, display._sweep.IsComplete); // no sweep step
    }

    // ─── T13: the review-blocker ordering + consumed-edge pins ────────────────

    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "lw_paintcounts_" + Path.GetRandomFileName());
        Directory.CreateDirectory(d);
        return d;
    }

    private static string ReadRegion(FakeHeap heap, long addr, int len)
    {
        heap.TryReadBytes(addr, len, out var buf);
        return System.Text.Encoding.ASCII.GetString(buf);
    }

    [Fact]
    public void Recompose_runs_before_paint_so_the_same_call_shows_the_current_story_line()
    {
        // Ordering pin (review blocker): RecomposeChanged must run BEFORE PaintAll inside
        // PaintCountsIfChanged (mirrors Display.cs's own Tick ordering, Display.cs:149-151), or a
        // kill that also rotates the composed story line would paint the STALE previous line for
        // one extra tick -- observable on a legends-wired Display (null in production today, the
        // Reliquary Phase-2 landmine the plan review defused).
        const int id = 30;
        string flavor = new string('x', 60);
        int budget = flavor.Length;
        var meta = new Dictionary<int, WeaponMeta>
        {
            [id] = new WeaponMeta { Name = "Windrunner", Flavor = flavor, Wp = 10, Cat = "Bow", Formula = 1 },
        };

        var legends = LegendStore.Load(TempDir());
        legends.RecordDeed(id, new VictimSnapshot(true, 1, 77, false));
        var kills = new Dictionary<int, int> { [id] = 5 };

        string l1 = CardLine.Compose("Windrunner", 5, legends.Get(id), budget)!;
        var src = new byte[400];
        int slot = CardFixtures.WriteKillsBlock(src, 20, l1, gap: 10, slot: Signatures.KillsMeterSlot(5));

        var statics = new byte[64];
        statics[0] = id; statics[1] = 0;
        var heap = new FakeHeap((StaticsBase, statics));
        heap.AddRegion(SourceBase, src);

        var clock = new TestClock();
        var display = CardFixtures.MakeDisplay(meta, kills, heap, StaticsBase, clock, legends);
        CardFixtures.DrainGeneration(display, clock, 500);
        Assert.Equal(1, display._sites.Count);

        // A kill lands AND a new deed rotates the composed line to L2, all in one narrow call.
        kills[id] = 6;
        legends.RecordDeed(id, new VictimSnapshot(true, 2, 77, false));
        string l2 = CardLine.Compose("Windrunner", 6, legends.Get(id), budget)!;
        Assert.NotEqual(l1, l2);

        display.PaintCountsIfChanged();

        Assert.Equal(l2, ReadRegion(heap, SourceBase + 20, l2.Length));
        Assert.Equal(Signatures.KillsMeterSlot(6), ReadRegion(heap, SourceBase + slot, Signatures.KillsMeterSlotChars));
    }

    [Fact]
    public void Narrow_repaint_latches_a_hot_rescan_the_next_full_tick_picks_up_before_its_own_clock_cadence()
    {
        // Consumed-edge pin (review blocker): the narrow path consumes the shared _lastCounts
        // edge, so it must also RequestRescan or a freshly-appeared card in an already-hot chunk
        // sits undiscovered until HotRescanMs (250ms) elapses on its own. A second card copy for
        // the same weapon appears in the SAME already-hot region after the background generation
        // already completed (that walk rests for GenerationRestMs, 90s); only a hot re-offer of
        // that chunk can ever find it in time. Non-vacuity: the clock only advances 1ms (far under
        // HotRescanMs) before the next full Tick, so the natural clock-due cadence cannot be what
        // triggers the hot pass -- only the RequestRescan latch can.
        var meta = BuildMeta();
        var kills = new Dictionary<int, int> { { 10, 5 } };
        var clock = new TestClock();
        var src = new byte[700];
        var cardA = CardFixtures.WriteCard(src, 0, "SwordA", "Bright edge of dawn");

        var statics = new byte[64];
        statics[0] = 10; statics[1] = 0;
        var heap = new FakeHeap((SourceBase, src), (StaticsBase, statics));
        var display = CardFixtures.MakeDisplay(meta, kills, heap, StaticsBase, clock);

        // A SHORT drain (not the usual 500-tick idiom): this tiny single-chunk region finishes its
        // generation within a couple of ticks, and the point of this test is that the chunk STAYS
        // in the hot set (HotChunkSet.HotTtlMs is 10s; 500 ticks * (HotRescanMs+1) would blow well
        // past that and let the chunk's hot marking expire before the latch below ever gets used).
        CardFixtures.DrainGeneration(display, clock, 10);
        Assert.Equal(Signatures.KillsMeterSlot(5), ReadSlot(heap, cardA.killsSlotPos, Signatures.KillsMeterSlotChars));

        // A second physical card copy for the same weapon "appears" (a menu redraw) at offset 300
        // in the same region/chunk, written directly into the heap after the generation completed.
        var buf2 = new byte[200];
        var cardB = CardFixtures.WriteCard(buf2, 0, "SwordA", "Bright edge of dawn");
        heap.WriteBytes(SourceBase + 300, buf2);

        kills[10] = 8;
        display.PaintCountsIfChanged();

        clock.Ms += 1;   // well under DisplaySweep.HotRescanMs (250)
        display.Tick(false);

        Assert.Equal(Signatures.KillsMeterSlot(8), ReadSlot(heap, 300 + cardB.killsSlotPos, Signatures.KillsMeterSlotChars));
    }
}
