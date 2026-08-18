using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// Display's pool-anchored in-place Kills paint (LW-37): once a writable pool region is
/// located and fully covers every tracked weapon id, the per-paint whole-heap DisplaySweep
/// is skipped. poolPaint is INJECTED (Display's ctor) so these tests reach the ON branch
/// directly regardless of the compiled Tuning.PoolPaintEnabled default (true in both build
/// flavors today, Tuning.cs) -- the injection is what makes the pool path deterministic to
/// test, not a PROD/DEV compile difference.
/// Every test proves non-vacuity the same way: display._sweep.IsComplete stays FALSE when
/// the pool path is exercised (the sweep is literally never Tick()'d), so a correct paint
/// result can only have come from the pool path, never a fallback sweep pass.
/// </summary>
public class DisplayPoolPaintTests
{
    private readonly record struct PoolFixture(FakeHeap Heap, long StaticsBase, long PoolBase,
        Dictionary<int, WeaponMeta> Meta, Dictionary<int, int> Kills,
        int SlotA, int SuffixA, int SlotB, int SuffixB);

    /// <summary>Two weapons (ids 10, 11) packed name -> suffix slot -> Kills -> flavor, the
    /// realistic pool geometry, contiguous in one small (single-chunk) region. Mirror target
    /// is id 10.</summary>
    private static PoolFixture BuildTwoWeaponPoolFixture()
    {
        var meta = new Dictionary<int, WeaponMeta>
        {
            { 10, new WeaponMeta { Name = "BowX", Flavor = "Fletched with regret" } },
            { 11, new WeaponMeta { Name = "BowY", Flavor = "Arrow never sleeps" } },
        };
        var kills = new Dictionary<int, int> { { 10, 0 }, { 11, 0 } };

        var poolBuf = new byte[2000];
        var (suffixA, slotA, flavorA) = CardFixtures.WriteCardForwardWithName(poolBuf, 0, "BowX", "Fletched with regret");
        int nextStart = flavorA + ByteScan.Ascii("Fletched with regret").Length + 20;
        var (suffixB, slotB, _) = CardFixtures.WriteCardForwardWithName(poolBuf, nextStart, "BowY", "Arrow never sleeps");

        long poolBase = 0x50_0000_0000L;
        long staticsBase = 0x51_0000_0000L;
        var statics = new byte[64];
        statics[0] = 10; statics[1] = 0;   // MirrorWeapon = 10

        var heap = new FakeHeap((poolBase, poolBuf, true), (staticsBase, statics, true));
        return new PoolFixture(heap, staticsBase, poolBase, meta, kills, slotA, suffixA, slotB, suffixB);
    }

    private static string ReadAscii(FakeHeap heap, long addr, int len)
    {
        heap.TryReadBytes(addr, len, out var buf);
        return System.Text.Encoding.ASCII.GetString(buf);
    }

    private readonly record struct TenWeaponFixture(FakeHeap Heap, long StaticsBase, long RegionBase,
        Dictionary<int, WeaponMeta> Meta, Dictionary<int, int> Kills,
        Dictionary<int, int> SuffixPos, Dictionary<int, int> SlotPos);

    /// <summary>Ten weapons (ids 10..19), names MUTUALLY PREFIX-FREE (BowA..BowJ, so FindAll's
    /// raw substring search cannot cross-hit one weapon's name against another's), unique
    /// flavors, packed name -> suffix slot -> Kills -> flavor (WriteCardForwardWithName, the
    /// realistic pool geometry) contiguous in one small (single-chunk) region. Mirror target
    /// is id 10 (LW-59 regression fixtures).</summary>
    private static TenWeaponFixture BuildTenWeaponFixture(int killsEach = 60)
    {
        var meta = new Dictionary<int, WeaponMeta>();
        var kills = new Dictionary<int, int>();
        var suffixPos = new Dictionary<int, int>();
        var slotPos = new Dictionary<int, int>();

        var buf = new byte[4096];
        int pos = 0;
        for (int i = 0; i < 10; i++)
        {
            int id = 10 + i;
            char letter = (char)('A' + i);
            string name = "Bow" + letter;
            string flavor = "Unique flavor line " + letter + " for the rotation fixture";
            meta[id] = new WeaponMeta { Name = name, Flavor = flavor };
            kills[id] = killsEach;

            var (suffix, killsSlot, flavorPos) = CardFixtures.WriteCardForwardWithName(buf, pos, name, flavor);
            suffixPos[id] = suffix;
            slotPos[id] = killsSlot;
            pos = flavorPos + ByteScan.Ascii(flavor).Length + 16;   // gap before the next card
        }

        long regionBase = 0x62_0000_0000L;
        long staticsBase = 0x63_0000_0000L;
        var statics = new byte[64];
        statics[0] = 10; statics[1] = 0;   // MirrorWeapon = id 10 (the target)

        var heap = new FakeHeap((regionBase, buf, true), (staticsBase, statics, true));
        return new TenWeaponFixture(heap, staticsBase, regionBase, meta, kills, suffixPos, slotPos);
    }

    // ─── forward multi-weapon attribution round trip (novel geometry) ─────────────

    [Fact]
    public void PoolPaint_registers_and_paints_the_correct_owner_for_each_weapon()
    {
        var f = BuildTwoWeaponPoolFixture();
        f.Kills[10] = 7; f.Kills[11] = 3;
        var clock = new TestClock();
        var display = CardFixtures.MakeDisplay(f.Meta, f.Kills, f.Heap, f.StaticsBase, clock, poolPaint: true);

        clock.Ms += DisplaySweep.HotRescanMs + 1;
        CardFixtures.TickWithPoolLocate(display, false);

        Assert.False(display._sweep.IsComplete);   // proves attribution came from the pool path

        Assert.Equal(Signatures.KillsMeterSlot(7), ReadAscii(f.Heap, f.PoolBase + f.SlotA, Signatures.KillsMeterSlotChars));
        Assert.Equal(Signatures.KillsMeterSlot(3), ReadAscii(f.Heap, f.PoolBase + f.SlotB, Signatures.KillsMeterSlotChars));
    }

    [Fact]
    public void PoolPaint_registers_and_paints_the_mirrored_weapons_suffix_too()
    {
        var f = BuildTwoWeaponPoolFixture();
        f.Kills[10] = 12;   // prod thresholds {5,10,15}: tier 2 -> "+2"
        var clock = new TestClock();
        var display = CardFixtures.MakeDisplay(f.Meta, f.Kills, f.Heap, f.StaticsBase, clock, poolPaint: true);

        clock.Ms += DisplaySweep.HotRescanMs + 1;
        CardFixtures.TickWithPoolLocate(display, false);

        Assert.False(display._sweep.IsComplete);

        Assert.Equal(Signatures.KillsMeterSlot(12), ReadAscii(f.Heap, f.PoolBase + f.SlotA, Signatures.KillsMeterSlotChars));
        Assert.Equal("+2", ReadAscii(f.Heap, f.PoolBase + f.SuffixA, 2));
    }

    // ─── LW-165 stage 1: the once-per-launch coverage stopwatch line ──────────────

    /// <summary>Born-red (LW-165): production has no line marking when the equip-card kill
    /// counters actually go live, so the owner's "slow to paint after cold boot" complaint
    /// cannot be measured. The first false-to-true edge of the pool coverage latch must emit
    /// exactly one Info-level line naming how many paint spots it maintains.</summary>
    [Fact]
    public void PoolPaint_first_coverage_emits_one_info_line_with_paint_spots()
    {
        var f = BuildTwoWeaponPoolFixture();
        f.Kills[10] = 7; f.Kills[11] = 3;
        var clock = new TestClock();
        var display = CardFixtures.MakeDisplay(f.Meta, f.Kills, f.Heap, f.StaticsBase, clock, poolPaint: true);

        using var cap = LogCapture.Start();
        clock.Ms += DisplaySweep.HotRescanMs + 1;
        CardFixtures.TickWithPoolLocate(display, false);

        Assert.False(display._sweep.IsComplete);   // proves attribution came from the pool path

        var infoHits = cap.File.FindAll(l => l.Contains("[INFO]") && l.Contains("paint spots"));
        Assert.Single(infoHits);
    }

    /// <summary>The once-per-launch pin: a post-Invalidate re-coverage (the normal battle-exit/
    /// battle-enter shape) must NOT repeat the Info-level announce line. It re-latches at Debug
    /// instead (file-only), so the Info-level "paint spots" count stays at exactly 1 across both
    /// coverage windows.</summary>
    [Fact]
    public void PoolPaint_re_coverage_after_invalidate_does_not_repeat_the_info_line()
    {
        var f = BuildTwoWeaponPoolFixture();
        f.Kills[10] = 7; f.Kills[11] = 3;
        var clock = new TestClock();
        var display = CardFixtures.MakeDisplay(f.Meta, f.Kills, f.Heap, f.StaticsBase, clock, poolPaint: true);

        using var cap = LogCapture.Start();
        clock.Ms += DisplaySweep.HotRescanMs + 1;
        CardFixtures.TickWithPoolLocate(display, false);   // first coverage: the announce line fires

        display.Invalidate();
        clock.Ms += DisplaySweep.HotRescanMs + 1;
        CardFixtures.TickWithPoolLocate(display, false);   // re-coverage: must not repeat the Info line

        Assert.False(display._sweep.IsComplete);

        var infoHits = cap.File.FindAll(l => l.Contains("[INFO]") && l.Contains("paint spots"));
        Assert.Single(infoHits);
    }

    // ─── LW-257 round-3 review, F1: the coverage summary line is rate-limited ─────

    /// <summary>F1: a region IS located (regions.Count > 0, so the "no named-pool region"
    /// StuckEdge gate never engages), but coverage can never complete (one tracked id is simply
    /// never present anywhere in the pool). Pre-fix, MaybePoolPaint's "LW37 paint: ..." summary
    /// line -- and the full _sites.Snapshot() copy behind it -- re-ran on EVERY Tick for as long
    /// as that persisted. NOT the CardEvictStrikes-tuned incident (Tuning.cs's own doc: that one's
    /// site count fell while CoversAllMeta() stayed TRUE throughout, so _poolCovered never
    /// dropped and this fall-through branch was never reached during it at all) -- this fixture
    /// instead models ScanPoolRegion's own documented "coverage never latches" residual risk (a
    /// tracked weapon absent from every pool region) and the repeated status-card-open unlatch
    /// window (Engine.cs's Display.Invalidate() call), both real triggers for this same
    /// fall-through repeating. Ten ticks of that permanent non-coverage must produce exactly one
    /// line, not ten.</summary>
    [Fact]
    public void Persistent_partial_coverage_logs_the_summary_line_once_not_every_tick()
    {
        var meta = new Dictionary<int, WeaponMeta>
        {
            { 10, new WeaponMeta { Name = "BowX", Flavor = "Fletched with regret" } },
            { 11, new WeaponMeta { Name = "BowY", Flavor = "Arrow never sleeps" } },   // never written below
        };
        var kills = new Dictionary<int, int> { { 10, 0 }, { 11, 0 } };
        var clock = new TestClock();

        var poolBuf = new byte[2000];
        CardFixtures.WriteCardForwardWithName(poolBuf, 0, "BowX", "Fletched with regret");
        // id 11 ("BowY") is deliberately never written anywhere: CoversAllMeta() can never
        // return true, so _poolCovered stays false and MaybePoolPaint falls through every Tick.

        long poolBase = 0x56_0000_0000L;
        long staticsBase = 0x57_0000_0000L;
        var statics = new byte[64];
        statics[0] = 10; statics[1] = 0;

        var heap = new FakeHeap((poolBase, poolBuf, true), (staticsBase, statics, true));
        var display = CardFixtures.MakeDisplay(meta, kills, heap, staticsBase, clock, poolPaint: true);

        using var cap = LogCapture.Start();
        for (int i = 0; i < 10; i++)
        {
            clock.Ms += DisplaySweep.HotRescanMs + 1;
            CardFixtures.TickWithPoolLocate(display, false);
        }

        // Coverage never completing is itself the documented existing fallback shape
        // (ScanPoolRegion's own doc, NOT MaybePoolPaint's: "it already re-scans all regions AND
        // runs the sweep every tick today" when coverage never latches), so the whole-heap sweep
        // legitimately also runs in this scenario -- unrelated to what this test checks. Non-vacuity instead: the
        // captured line must report the ONE region this fixture actually located ("1 region(s)"),
        // proving MaybePoolPaint's region-found branch (locate -> scan -> CoversAllMeta -> the
        // rate-limited log) is what ran, not the empty-regions StuckEdge branch (item 7).
        var summaryLines = cap.File.FindAll(l => l.Contains("LW37 paint:") && l.Contains("1 region(s)"));
        Assert.Single(summaryLines);

        // Round-5 fix (F1): the "LW37 locate-timing:" line escaped this same arc's own
        // rate-limit discipline -- it was unconditional, so this same ten-tick fixture printed
        // ten of them (a live verifier measured this exact fixture and got 10-to-1 against the
        // gated summary line above). TestClock cannot simulate elapsed wall-time within one
        // synchronous call (both _nowMs() reads in MaybePoolPaint see the same fixed Ms value),
        // so locateMs is deterministically 0 here regardless of real vs. short-circuited work --
        // this asserts the flood is gone, not that the line still fires when locateMs is
        // genuinely positive (a live-only fact, per spec 1.5's own 143-569ms measurement).
        var locateTimingLines = cap.File.FindAll(l => l.Contains("LW37 locate-timing:"));
        Assert.True(locateTimingLines.Count < 10, "must not print once per tick regardless of value");
    }

    // ─── commit 1B: bound the full-region ScanPoolRegion pass ─────────────────────

    /// <summary>Commit 1B, test 1: an unlatched steady state (this fixture's own doc: id 11 is
    /// never written anywhere, so CoversAllMeta() can never return true and MaybePoolPaint's
    /// region-found branch is reached every Tick) must NOT re-run the full-region scan on two
    /// CONSECUTIVE ticks at the same clock time -- only the maintenance cadence (MaintenanceMs) or
    /// a genuinely fresh PoolLocator publish may trigger it again. Uses the identical fixture
    /// shape as Persistent_partial_coverage_logs_the_summary_line_once_not_every_tick above.</summary>
    [Fact]
    public void Unlatched_steady_state_does_not_rescan_regions_on_consecutive_ticks()
    {
        var meta = new Dictionary<int, WeaponMeta>
        {
            { 10, new WeaponMeta { Name = "BowX", Flavor = "Fletched with regret" } },
            { 11, new WeaponMeta { Name = "BowY", Flavor = "Arrow never sleeps" } },   // never written
        };
        var kills = new Dictionary<int, int> { { 10, 0 }, { 11, 0 } };
        // Realistic nonzero clock origin (verifier F1): TestClock starting at 0 made
        // ShouldRunFullPoolScan's cadence check `now - _lastPoolScanMs < MaintenanceMs` read
        // `now - (-1)` as coincidentally small (a few hundred ms) whenever the generation branch
        // forgot to stamp _lastPoolScanMs, masking exactly that bug. A real process clock
        // (Environment.TickCount64) is never anywhere near zero, so this pins the realistic case.
        var clock = new TestClock { Ms = 5_000_000 };

        var poolBuf = new byte[2000];
        CardFixtures.WriteCardForwardWithName(poolBuf, 0, "BowX", "Fletched with regret");

        long poolBase = 0x58_0000_0000L;
        long staticsBase = 0x58_1000_0000L;
        var statics = new byte[64];
        statics[0] = 10; statics[1] = 0;

        var heap = new FakeHeap((poolBase, poolBuf, true), (staticsBase, statics, true));
        var display = CardFixtures.MakeDisplay(meta, kills, heap, staticsBase, clock, poolPaint: true);

        // Drive until the locate completes and the first scan pass has run at least once.
        int ticks = 0;
        while (display.PoolScanPassesForTest == 0 && ticks < 20)
        {
            clock.Ms += DisplaySweep.HotRescanMs + 1;
            CardFixtures.TickWithPoolLocate(display, false);
            ticks++;
        }
        Assert.True(display.PoolScanPassesForTest > 0, "the fixture must reach at least one real scan pass to be a meaningful test");
        int passesAfterFirstScan = display.PoolScanPassesForTest;

        // A SECOND tick at the SAME clock time: no cadence elapsed, no fresh publish queued.
        CardFixtures.TickWithPoolLocate(display, false);

        Assert.Equal(passesAfterFirstScan, display.PoolScanPassesForTest);
    }

    /// <summary>Commit 1B, test 2: a genuinely fresh PoolLocator publish (Invalidate + a
    /// completed re-locate) must trigger a scan pass immediately, even with NO time elapsed on the
    /// maintenance-cadence clock -- proving PublishGeneration, not just MaintenanceMs, is a real
    /// trigger.</summary>
    [Fact]
    public void Fresh_publish_triggers_a_scan_pass_even_with_no_cadence_elapsed()
    {
        var meta = new Dictionary<int, WeaponMeta>
        {
            { 10, new WeaponMeta { Name = "BowX", Flavor = "Fletched with regret" } },
        };
        var kills = new Dictionary<int, int> { { 10, 7 } };
        var clock = new TestClock();

        var poolBuf = new byte[500];
        CardFixtures.WriteCardForwardWithName(poolBuf, 0, "BowX", "Fletched with regret");

        long poolBase = 0x58_2000_0000L;
        long staticsBase = 0x58_3000_0000L;
        var statics = new byte[64];
        statics[0] = 10;

        var heap = new FakeHeap((poolBase, poolBuf, true), (staticsBase, statics, true));
        var display = CardFixtures.MakeDisplay(meta, kills, heap, staticsBase, clock, poolPaint: true);

        CardFixtures.TickWithPoolLocate(display, false);   // completes: coverage latches, one scan pass
        Assert.True(display.PoolScanPassesForTest > 0);
        Assert.Equal(1, display.PoolScanPassesForTest);
        int passesBeforeInvalidate = display.PoolScanPassesForTest;

        // Same clock instant: no cadence has elapsed at all. Invalidate() queues a restart that
        // bypasses PoolLocator's own revalidate cadence too (its Step's _restartPending branch),
        // so the re-locate completes within the SAME tick and republishes (a new PublishGeneration).
        display.Invalidate();
        CardFixtures.TickWithPoolLocate(display, false);

        Assert.True(display.PoolScanPassesForTest > passesBeforeInvalidate,
            "a fresh publish must trigger a scan pass even though no maintenance cadence has elapsed");
    }

    // ─── sweep-gate via the injected flag (B2) ─────────────────────────────────────

    [Fact]
    public void PoolPaint_true_with_full_coverage_skips_the_sweep_forever()
    {
        var f = BuildTwoWeaponPoolFixture();
        var clock = new TestClock();
        var display = CardFixtures.MakeDisplay(f.Meta, f.Kills, f.Heap, f.StaticsBase, clock, poolPaint: true);

        for (int i = 0; i < 10; i++)
        {
            clock.Ms += DisplaySweep.HotRescanMs + 1;
            CardFixtures.TickWithPoolLocate(display, false);
        }

        Assert.False(display._sweep.IsComplete);
        Assert.Equal(1, display._sweep.Generation);   // never advanced, Tick(budget, OnChunk) never ran
    }

    [Fact]
    public void PoolPaint_false_sweep_runs_and_generation_advances_like_before()
    {
        var f = BuildTwoWeaponPoolFixture();
        var clock = new TestClock();
        var display = CardFixtures.MakeDisplay(f.Meta, f.Kills, f.Heap, f.StaticsBase, clock, poolPaint: false);

        for (int i = 0; i < 10; i++)
        {
            clock.Ms += DisplaySweep.HotRescanMs + 1;
            CardFixtures.TickWithPoolLocate(display, false);
        }
        Assert.True(display._sweep.IsComplete);   // the sweep ran a full pass, unlike the skip case above

        clock.Ms += DisplaySweep.GenerationRestMs + DisplaySweep.HotRescanMs + 10;
        CardFixtures.TickWithPoolLocate(display, false);

        Assert.Equal(2, display._sweep.Generation);   // a fresh generation started, regression guard
    }

    // ─── read-only pool (B1/premise-9): zero writes, sweep NOT skipped ─────────────

    [Fact]
    public void PoolPaint_readonly_pool_issues_zero_writes_and_never_skips_the_sweep()
    {
        var meta = new Dictionary<int, WeaponMeta>
        {
            { 10, new WeaponMeta { Name = "BowX", Flavor = "Fletched with regret" } },
        };
        var kills = new Dictionary<int, int> { { 10, 7 } };
        var clock = new TestClock();

        var poolBuf = new byte[500];
        CardFixtures.WriteCardForwardWithName(poolBuf, 0, "BowX", "Fletched with regret");
        var poolBufSnapshot = (byte[])poolBuf.Clone();

        long poolBase = 0x54_0000_0000L;
        long staticsBase = 0x55_0000_0000L;
        var statics = new byte[64];
        statics[0] = 10; statics[1] = 0;

        var heap = new FakeHeap((poolBase, poolBuf, false), (staticsBase, statics, true));   // read-only pool
        var display = CardFixtures.MakeDisplay(meta, kills, heap, staticsBase, clock, poolPaint: true);

        for (int i = 0; i < 30; i++)
        {
            clock.Ms += DisplaySweep.HotRescanMs + 1;
            CardFixtures.TickWithPoolLocate(display, false);
        }

        // Read-only means the pool is absent from Regions() (matches production Mem.Regions()),
        // so the pool path never covers it: the sweep must have run instead of being skipped.
        Assert.True(display._sweep.IsComplete, "the sweep must run when the pool cannot be located");

        var current = heap.RegionBytes(poolBase)!;
        Assert.Equal(poolBufSnapshot, current);   // zero writes to the read-only region
    }

    // ─── LW-59: stale +N suffix survives a tally reset (pool path) ────────────────

    [Fact]
    public void PoolPaint_downgrades_every_suffix_after_a_tally_reset()
    {
        var f = BuildTenWeaponFixture();
        var clock = new TestClock();
        var display = CardFixtures.MakeDisplay(f.Meta, f.Kills, f.Heap, f.StaticsBase, clock, poolPaint: true);

        CardFixtures.TickWithPoolLocate(display, false);

        // Sanity (passes today): the target weapon's suffix is painted at tier 3.
        Assert.Equal("+3", ReadAscii(f.Heap, f.RegionBase + f.SuffixPos[10], 2));

        display.Invalidate();   // the battle-exit / forced-exit edge (LW-56)
        f.Kills.Clear();        // the PlaythroughReset action (LW-51)
        CardFixtures.TickWithPoolLocate(display, false);

        foreach (int id in f.Meta.Keys)
        {
            Assert.Equal("  ", ReadAscii(f.Heap, f.RegionBase + f.SuffixPos[id], 2));
            Assert.Equal(Signatures.KillsMeterSlot(0),
                ReadAscii(f.Heap, f.RegionBase + f.SlotPos[id], Signatures.KillsMeterSlotChars));
        }
        Assert.False(display._sweep.IsComplete);   // proves the pool path (not the sweep) did the work
    }

    [Fact]
    public void PoolPaint_registers_suffix_sites_for_every_pool_id_independent_of_targets()
    {
        var f = BuildTenWeaponFixture();
        var clock = new TestClock();
        var display = CardFixtures.MakeDisplay(f.Meta, f.Kills, f.Heap, f.StaticsBase, clock, poolPaint: true);

        CardFixtures.TickWithPoolLocate(display, false);

        var suffixIds = new HashSet<int>();
        foreach (var s in display._sites.Snapshot())
            if (!s.IsKills) suffixIds.Add(s.Id);

        foreach (int id in f.Meta.Keys)
            Assert.Contains(id, suffixIds);
    }

    [Fact]
    public void Reset_without_invalidate_still_downgrades_standing_suffix_sites()
    {
        var f = BuildTenWeaponFixture();
        var clock = new TestClock();
        var display = CardFixtures.MakeDisplay(f.Meta, f.Kills, f.Heap, f.StaticsBase, clock, poolPaint: true);

        CardFixtures.TickWithPoolLocate(display, false);

        // MANDATORY Act-1 mid-state assert: the main-menu New Game path never calls
        // Invalidate, so standing coverage from this very first Tick is what must heal the
        // reset below. Also what makes THIS test red today: rotation covers only 8 of the 9
        // non-target ids on the first chunk, so one id is never suffix-painted here.
        foreach (int id in f.Meta.Keys)
            Assert.Equal("+3", ReadAscii(f.Heap, f.RegionBase + f.SuffixPos[id], 2));

        f.Kills.Clear();   // the main-menu New Game path: no forced exit, no Invalidate
        CardFixtures.TickWithPoolLocate(display, false);

        foreach (int id in f.Meta.Keys)
        {
            Assert.Equal("  ", ReadAscii(f.Heap, f.RegionBase + f.SuffixPos[id], 2));
            Assert.Equal(Signatures.KillsMeterSlot(0),
                ReadAscii(f.Heap, f.RegionBase + f.SlotPos[id], Signatures.KillsMeterSlotChars));
        }
    }

    // ─── regression pin: the whole-heap sweep path is unaffected by D1 ────────────

    [Fact]
    public void Sweep_path_still_limits_suffix_search_to_targets_plus_rotation()
    {
        var f = BuildTenWeaponFixture();
        var clock = new TestClock();
        var display = CardFixtures.MakeDisplay(f.Meta, f.Kills, f.Heap, f.StaticsBase, clock, poolPaint: false);

        // Exactly ONE Tick: the single-chunk region completes generation 1 here. Do NOT
        // advance the clock past HotRescanMs between steps (a hot re-offer would legitimately
        // march the rotation further and break this bound for a reason unrelated to D1).
        CardFixtures.TickWithPoolLocate(display, false);

        Assert.True(display._sweep.IsComplete);

        int suffixSites = 0;
        foreach (var s in display._sites.Snapshot())
            if (!s.IsKills) suffixSites++;

        Assert.True(suffixSites <= 1 + SuffixRotation.RotationSlice,
            $"suffix sites ({suffixSites}) must not exceed target + rotation slice on the sweep path");
    }

    // ─── LW-163: a drained pool cache must re-locate on its own, no Invalidate involved ────

    /// <summary>LW-163 (load-bearing, born red): a save-load can free the pool region the cached
    /// sites point into WITHOUT any Invalidate() call reaching Display at all (the player's
    /// habitual check surface is the out-of-battle status page, which triggers none). Once
    /// coverage has latched, the pre-fix MaybePoolPaint short-circuits forever, so a drained
    /// cache is never re-discovered and the card freezes blind. Recipe: reach coverage, then
    /// (mirroring DisplayMutationGapTests' C4 region-move) remove the pool region and add a
    /// fresh one at a different base with unpainted card bytes for the same two weapons, advance
    /// the clock past Display.MaintenanceMs so the maintenance PaintAll evicts the now-dead sites
    /// (their anchors live in memory that no longer exists), and Tick with NO Invalidate()
    /// anywhere in the test. A correct fix re-locates the same Tick eviction actually happens on
    /// (the eviction and the pool-paint re-check both run inside Display.Tick, eviction first).
    ///
    /// LW-257: eviction itself now takes Tuning.CardEvictStrikes consecutive maintenance beats,
    /// not one (CardSites.Verify.cs's ApplyStrike leniency -- a single transient unreadable read
    /// must survive). So this recipe drives that many beats before the re-locate can be expected;
    /// MaybePoolPaint's count-compare short-circuit stays latched (Regions() untouched) for every
    /// beat before the last, exactly like the steady state it is, and only falls through once
    /// Count actually drops on the beat that reaches the strike cap.</summary>
    [Fact]
    public void PoolPaint_drained_pool_relocates_without_any_invalidate()
    {
        var f = BuildTwoWeaponPoolFixture();
        f.Kills[10] = 7; f.Kills[11] = 3;
        var clock = new TestClock();
        var display = CardFixtures.MakeDisplay(f.Meta, f.Kills, f.Heap, f.StaticsBase, clock, poolPaint: true);

        CardFixtures.TickWithPoolLocate(display, false);

        Assert.False(display._sweep.IsComplete);   // proves attribution came from the pool path
        Assert.Equal(Signatures.KillsMeterSlot(7), ReadAscii(f.Heap, f.PoolBase + f.SlotA, Signatures.KillsMeterSlotChars));

        // Drain: remove the original pool region and add a fresh replacement at a DIFFERENT
        // base, with fresh (unpainted) card bytes for both weapons. No Invalidate() call.
        f.Heap.RemoveRegion(f.PoolBase);
        long newBase = 0x58_0000_0000L;
        var newBuf = new byte[2000];
        var (_, slotA2, flavorA2) = CardFixtures.WriteCardForwardWithName(newBuf, 0, "BowX", "Fletched with regret");
        int nextStart2 = flavorA2 + ByteScan.Ascii("Fletched with regret").Length + 20;
        var (_, slotB2, _) = CardFixtures.WriteCardForwardWithName(newBuf, nextStart2, "BowY", "Arrow never sleeps");
        f.Heap.AddRegion(newBase, newBuf, writable: true);

        // Advance past MaintenanceMs Tuning.CardEvictStrikes times so the maintenance PaintAll
        // actually evicts the now-dead sites (LW-257: one strike per beat, eviction only on the
        // beat that reaches the cap) before the pool-paint re-check can see Count drop.
        for (int beat = 0; beat < Tuning.CardEvictStrikes; beat++)
        {
            clock.Ms += Display.MaintenanceMs + 1;
            CardFixtures.TickWithPoolLocate(display, false);
        }

        Assert.Equal(Signatures.KillsMeterSlot(7), ReadAscii(f.Heap, newBase + slotA2, Signatures.KillsMeterSlotChars));
        Assert.Equal(Signatures.KillsMeterSlot(3), ReadAscii(f.Heap, newBase + slotB2, Signatures.KillsMeterSlotChars));
        Assert.False(display._sweep.IsComplete);   // the whole-heap sweep still never ran
    }

    /// <summary>LW-163 regression pin, the non-vacuous negative for the fall-through above: the
    /// fix must not turn into a permanent every-tick rescan. RegionsSpyMem is copied (not
    /// referenced) from PoolLocatorTests.cs's private helper of the same name, per this file's
    /// own no-cross-file-reference convention for test-only fakes.
    ///
    /// LW-257: the drain-to-heal window now spans Tuning.CardEvictStrikes maintenance beats
    /// (CardSites.Verify.cs's ApplyStrike leniency), not one, so the "steady state calls
    /// Regions() zero times" pin has to hold across every beat but the last too, not just the
    /// five pre-drain ticks part (a) already covers -- that is what the loop below's own
    /// per-beat assertion proves, before the final beat is allowed to actually heal.</summary>
    [Fact]
    public void PoolPaint_steady_state_short_circuits_and_relatches_after_heal()
    {
        var f = BuildTwoWeaponPoolFixture();
        f.Kills[10] = 7; f.Kills[11] = 3;
        var clock = new TestClock();
        var spy = new RegionsSpyMem(f.Heap);
        var wrapped = new OffsetRemapMem(spy, f.StaticsBase, f.StaticsBase + 2, f.StaticsBase + 4);
        var display = new Display(f.Meta, f.Kills, wrapped, clock.Func, legends: null, poolPaint: true);

        CardFixtures.TickWithPoolLocate(display, false);   // establish coverage
        Assert.False(display._sweep.IsComplete);

        int callsAfterCoverage = spy.RegionsCalls;
        Assert.True(callsAfterCoverage > 0, "the cold locate must have scanned Regions() at least once");

        // (a) steady state: several ticks with sites alive must short-circuit on the cheap count
        // gate alone, never calling into the locator (so Regions() is never re-enumerated).
        for (int i = 0; i < 5; i++) CardFixtures.TickWithPoolLocate(display, false);
        Assert.Equal(callsAfterCoverage, spy.RegionsCalls);

        // Drain (same recipe as the born-red test above), then heal after the strike window.
        f.Heap.RemoveRegion(f.PoolBase);
        long newBase = 0x59_0000_0000L;
        var newBuf = new byte[2000];
        var (_, slotA2, flavorA2) = CardFixtures.WriteCardForwardWithName(newBuf, 0, "BowX", "Fletched with regret");
        int nextStart2 = flavorA2 + ByteScan.Ascii("Fletched with regret").Length + 20;
        CardFixtures.WriteCardForwardWithName(newBuf, nextStart2, "BowY", "Arrow never sleeps");
        f.Heap.AddRegion(newBase, newBuf, writable: true);

        // Retune round (R1): PoolLocator's own independent RevalidateMs reverify (driven every
        // beat now by TickWithPoolLocate, standing in for Engine's own "pool-locate" phase) is no
        // longer gated behind Display's OWN strike-cap eviction schedule -- it notices the removed
        // region and republishes CachedRegions on the FIRST beat that crosses RevalidateMs after
        // the drain. That relocate is ALSO this beat's own CardSites strike #1 against the now-
        // dead OLD sites (their anchor read fails, but CardSites.Verify.cs's leniency means one
        // strike alone does not evict) -- so MaybePoolPaint's cheap site-COUNT short-circuit still
        // holds (nothing evicted yet) and does not re-scan the freshly-republished region or
        // repaint the card until CardSites actually evicts, CardEvictStrikes beats in, exactly as
        // before this retune round. Only WHEN Regions() gets called moved earlier; WHEN the card
        // repaints did not -- F6 (round 5 verify): that second half was previously asserted only
        // in this comment, never in code (a verifier mutation of CardSites.Verify.cs's ApplyStrike
        // to evict on the FIRST strike left this test green), so the NotEqual checks below pin it
        // directly: the new site must NOT yet read the painted meter until the strike cap beat.
        clock.Ms += Display.MaintenanceMs + 1;
        CardFixtures.TickWithPoolLocate(display, false);   // strike 1: PoolLocator relocates and republishes THIS beat
        int callsAfterRelocate = spy.RegionsCalls;
        Assert.True(callsAfterRelocate > callsAfterCoverage, "PoolLocator's own revalidate must have re-scanned Regions() once the region vanished");
        Assert.NotEqual(Signatures.KillsMeterSlot(7), ReadAscii(f.Heap, newBase + slotA2, Signatures.KillsMeterSlotChars));

        for (int beat = 1; beat < Tuning.CardEvictStrikes - 1; beat++)
        {
            clock.Ms += Display.MaintenanceMs + 1;
            CardFixtures.TickWithPoolLocate(display, false);
            Assert.Equal(callsAfterRelocate, spy.RegionsCalls);   // PoolLocator's cache is already fresh: nothing further to revalidate
            Assert.NotEqual(Signatures.KillsMeterSlot(7), ReadAscii(f.Heap, newBase + slotA2, Signatures.KillsMeterSlotChars));
        }

        clock.Ms += Display.MaintenanceMs + 1;
        CardFixtures.TickWithPoolLocate(display, false);   // the strike-cap beat: CardSites finally evicts, MaybePoolPaint's count changes, the card repaints at its new address

        Assert.Equal(Signatures.KillsMeterSlot(7), ReadAscii(f.Heap, newBase + slotA2, Signatures.KillsMeterSlotChars));

        int callsAfterHeal = spy.RegionsCalls;
        Assert.Equal(callsAfterRelocate, callsAfterHeal);   // the repaint itself needs no NEW Regions() call -- the region was already relocated

        // (b) re-latch pin: further ticks with the healed region alive must NOT call
        // Regions() again -- the fix re-latches, it does not become a permanent rescan.
        for (int beat = 0; beat < Tuning.CardEvictStrikes; beat++)
        {
            clock.Ms += Display.MaintenanceMs + 1;
            CardFixtures.TickWithPoolLocate(display, false);
            Assert.Equal(callsAfterHeal, spy.RegionsCalls);
        }
    }

    /// <summary>IGameMemory wrapper that counts Regions() calls, forwarding everything else --
    /// copied from PoolLocatorTests.cs's private helper of the same name (this file's own
    /// no-cross-file-reference convention for test-only fakes).</summary>
    private sealed class RegionsSpyMem : IGameMemory
    {
        private readonly IGameMemory _inner;
        public int RegionsCalls { get; private set; }
        public RegionsSpyMem(IGameMemory inner) => _inner = inner;

        public byte U8(long addr) => _inner.U8(addr);
        public ushort U16(long addr) => _inner.U16(addr);
        public bool TryReadBytes(long addr, int len, out byte[] buf) => _inner.TryReadBytes(addr, len, out buf);
        public int ReadInto(long addr, byte[] buf, int len) => _inner.ReadInto(addr, buf, len);
        public void WriteBytes(long addr, byte[] data) => _inner.WriteBytes(addr, data);
        public void W8(long addr, byte value) => _inner.W8(addr, value);
        public bool Readable(long addr, int len) => _inner.Readable(addr, len);
        public bool Writable(long addr, int len) => _inner.Writable(addr, len);
        public IEnumerable<(long baseAddr, long size)> Regions()
        {
            RegionsCalls++;
            return _inner.Regions();
        }
    }
}
