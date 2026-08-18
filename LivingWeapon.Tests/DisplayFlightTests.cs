using System;
using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-257 commit 1: Display's flight-recorder injection (Display.Flight.cs), following the
/// established recorder-injection pattern (ActorRegisterTests.Injected_recorder_receives_pointer_
/// transitions, AttackCardTests, GunSlingerReconcileTests, KillCreditCoverageTests) -- inject a
/// recorder as `(type, payload) => recorded.Add((type, payload))` and assert on the captured list.
/// Before this file, Display.Flight.cs had zero tests: nothing in the suite ever constructed a
/// Display with a non-null recorder at all.
/// </summary>
public class DisplayFlightTests
{
    private const long SourceBase  = 0x72_0000_0000L;
    private const long StaticsBase = 0x73_0000_0000L;

    private static Dictionary<int, WeaponMeta> BuildMeta() => new()
    {
        { 10, new WeaponMeta { Name = "SwordA", Flavor = "Bright edge of dawn", Wp = 12, Cat = "Sword", Formula = 1 } },
    };

    private static FakeHeap BuildHeap(byte[] src, int mirrorId = 10)
    {
        var statics = new byte[16];
        statics[0] = (byte)(mirrorId & 0xFF);
        statics[1] = (byte)(mirrorId >> 8);
        return new FakeHeap((SourceBase, src), (StaticsBase, statics));
    }

    [Fact]
    public void Recorder_receives_a_card_record_when_a_site_is_painted()
    {
        var meta = BuildMeta();
        var kills = new Dictionary<int, int> { { 10, 5 } };
        var clock = new TestClock();
        var src = new byte[512];
        CardFixtures.WriteCard(src, 0, "SwordA", "Bright edge of dawn");
        var heap = BuildHeap(src);
        var recorded = new List<(string type, string payload)>();
        var display = CardFixtures.MakeDisplay(meta, kills, heap, StaticsBase, clock, recorder: (t, p) => recorded.Add((t, p)));

        // Initial discovery paints through OnChunk's untapped Paint() (Display.cs), not
        // PaintAllTapped -- so a genuine flight record needs a repaint AFTER the site is already
        // cached, exactly like a live kill landing.
        CardFixtures.DrainGeneration(display, clock, 500);

        kills[10] = 8;
        display.PaintCountsIfChanged();   // routes through PaintAllTapped -> CardSites.PaintAll(verdict)

        Assert.Contains(recorded, r => r.type == "card" && r.payload.StartsWith("paint id=10 to=8"));
    }

    [Fact]
    public void No_recorder_means_the_verdict_ledger_is_never_allocated()
    {
        var meta = BuildMeta();
        var kills = new Dictionary<int, int> { { 10, 5 } };
        var clock = new TestClock();
        var src = new byte[512];
        CardFixtures.WriteCard(src, 0, "SwordA", "Bright edge of dawn");
        var heap = BuildHeap(src);
        var display = CardFixtures.MakeDisplay(meta, kills, heap, StaticsBase, clock);   // no recorder

        Assert.Null(display._verdict);   // the allocation path (Display's ctor) is skipped entirely,
                                          // not merely "no taps fire" -- pins the existing behavior

        // Regression safety: real paint activity with no recorder at all must still behave
        // exactly as every pre-LW-257 test already proves, and must not allocate a ledger later.
        CardFixtures.DrainGeneration(display, clock, 500);
        kills[10] = 8;
        display.PaintCountsIfChanged();

        Assert.Null(display._verdict);
    }

    /// <summary>Item 1's regression pin (LW-257 review round 2): CardVerdict's notable lane must
    /// exclude AlreadyEqual so a late Wrote cannot be starved by a 256-entry cap a steady-state
    /// pass fills with AlreadyEqual first. Built directly against CardSites (bypassing the scan)
    /// so the insertion order -- 256 already-correct sites, THEN one stale site -- is exact and
    /// deterministic: List.Add/Remove preserve order, so under the OLD single-lane design (Note
    /// counts every outcome against the same cap) the stale site's Wrote would already be past
    /// the cap by the time PaintAll reaches it.</summary>
    [Fact]
    public void Recorder_still_receives_a_late_Wrote_record_past_256_AlreadyEqual_sites()
    {
        const int id = 10;
        const int blockStride = 64;
        int totalBlocks = CardVerdict.MaxEntries + 1;

        var meta = BuildMeta();
        var kills = new Dictionary<int, int> { { id, 8 } };
        var clock = new TestClock();

        var buf = new byte[totalBlocks * blockStride + 256];
        var slotPositions = new int[totalBlocks];
        for (int i = 0; i < totalBlocks; i++)
        {
            bool isLast = i == totalBlocks - 1;
            string slotValue = isLast ? Signatures.KillsMeterSlot(0) : Signatures.KillsMeterSlot(8);
            slotPositions[i] = CardFixtures.WriteKillsBlock(buf, i * blockStride, "Bright edge of dawn", gap: 20, slot: slotValue);
        }

        var heap = new FakeHeap((SourceBase, buf, true));
        heap.AddRegion(StaticsBase, new byte[16], writable: true);
        var recorded = new List<(string type, string payload)>();
        var display = CardFixtures.MakeDisplay(meta, kills, heap, StaticsBase, clock, recorder: (t, p) => recorded.Add((t, p)));

        // Register all totalBlocks sites directly, in the SAME order they were written, so the
        // one stale site really is last in PaintAll's List<Site> iteration.
        for (int i = 0; i < totalBlocks; i++)
        {
            long anchorAddr = SourceBase + i * blockStride;
            long slotAddr = SourceBase + slotPositions[i];
            display._sites.Add(new CardSites.Site(id, 1, slotAddr, anchorAddr, IsKills: true));
        }
        Assert.Equal(totalBlocks, display._sites.Count);

        display.PaintCountsIfChanged();   // _lastCounts starts empty, so kills[id]=8 reads as a change

        Assert.Contains(recorded, r => r.type == "card" && r.payload.StartsWith($"paint id={id} to=8"));
    }
    /// <summary>Item 2 (round-4 review): spec 1.4's core observability requirement -- "the strike
    /// policy this arc introduces can be seen working on a tape" -- had NO test at all before this
    /// one. Pins both halves: strikes short of the cap (Tuning.CardEvictStrikes == 3) emit
    /// NOTHING, and the capping strike emits exactly one site-evicted record with the right
    /// reason.</summary>
    [Fact]
    public void Site_evicted_record_fires_only_on_the_capping_strike_not_before()
    {
        var meta = BuildMeta();
        var kills = new Dictionary<int, int> { { 10, 5 } };
        var clock = new TestClock();
        var src = new byte[512];
        CardFixtures.WriteCard(src, 0, "SwordA", "Bright edge of dawn");
        var heap = BuildHeap(src);
        var recorded = new List<(string type, string payload)>();
        var display = CardFixtures.MakeDisplay(meta, kills, heap, StaticsBase, clock, recorder: (t, p) => recorded.Add((t, p)));

        CardFixtures.DrainGeneration(display, clock, 500);
        Assert.True(display._sites.Count > 0, "at least one site must be registered before the region disappears");

        heap.RemoveRegion(SourceBase);   // every cached site's anchor is now Unreadable

        // Strikes 1 and 2: no site-evicted record yet.
        for (int beat = 1; beat < Tuning.CardEvictStrikes; beat++)
        {
            clock.Ms += Display.MaintenanceMs + 1;
            CardFixtures.TickWithPoolLocate(display, false);
            Assert.DoesNotContain(recorded, r => r.type == "card" && r.payload.Contains("site-evicted"));
        }

        // The capping strike: the record fires now, carrying the right reason.
        clock.Ms += Display.MaintenanceMs + 1;
        CardFixtures.TickWithPoolLocate(display, false);
        Assert.Contains(recorded, r => r.type == "card" && r.payload.Contains("site-evicted")
                                        && r.payload.Contains("reason=anchor-unreadable"));
    }

    /// <summary>Item 1's priority-rule pin (round-4 review): an Evicted entry must survive a
    /// notable lane already saturated with routine Wrote entries. This is the exact failure mode
    /// round 3's F4 opened: a first-light discovery pass can register hundreds of Wrote entries
    /// in one Tick (Display.cs's OnChunk sharing this SAME _verdict), filling the 256-entry lane
    /// before PaintAll's own pass -- the one that finds every eviction -- ever gets a turn. 256
    /// filler sites (all Wrote, registered FIRST) plus one mismatched (evicted) site registered
    /// LAST reproduces that ordering deterministically: List&lt;Site&gt; iterates in insertion
    /// order, so the eviction is genuinely the (fillerCount+1)th Note() call this pass, arriving
    /// only after the lane is already full.</summary>
    [Fact]
    public void An_eviction_survives_a_notable_lane_saturated_with_routine_paints()
    {
        const int id = 10;
        const int blockStride = 64;
        int fillerCount = CardVerdict.MaxEntries;

        var meta = BuildMeta();
        var kills = new Dictionary<int, int> { { id, 8 } };
        var clock = new TestClock();

        var buf = new byte[(fillerCount + 1) * blockStride + 256];
        var slotPositions = new int[fillerCount];
        for (int i = 0; i < fillerCount; i++)
            // Stale (tier-0 placeholder) slot value against a target of 8 kills -> Wrote.
            slotPositions[i] = CardFixtures.WriteKillsBlock(buf, i * blockStride, "Bright edge of dawn", gap: 20);

        // The evicted site: a readable-but-WRONG flavor anchor (a genuine mismatch), registered LAST.
        int evictedAnchorPos = fillerCount * blockStride;
        var wrongFlavor = ByteScan.Ascii(new string('X', "Bright edge of dawn".Length));
        Array.Copy(wrongFlavor, 0, buf, evictedAnchorPos, wrongFlavor.Length);

        var heap = new FakeHeap((SourceBase, buf, true));
        heap.AddRegion(StaticsBase, new byte[16], writable: true);
        var recorded = new List<(string type, string payload)>();
        var display = CardFixtures.MakeDisplay(meta, kills, heap, StaticsBase, clock, recorder: (t, p) => recorded.Add((t, p)));

        for (int i = 0; i < fillerCount; i++)
            display._sites.Add(new CardSites.Site(id, 1, SourceBase + slotPositions[i], SourceBase + i * blockStride, IsKills: true));
        long evictedSlotAddr = SourceBase + evictedAnchorPos + 10_000;   // never read -- the anchor mismatch evicts first
        display._sites.Add(new CardSites.Site(id, 1, evictedSlotAddr, SourceBase + evictedAnchorPos, IsKills: true));
        Assert.Equal(fillerCount + 1, display._sites.Count);

        display.PaintCountsIfChanged();

        Assert.Contains(recorded, r => r.type == "card" && r.payload.Contains("site-evicted")
                                        && r.payload.Contains($"id={id}") && r.payload.Contains("reason=anchor-mismatch"));
    }

    /// <summary>Item 2 (round-4 review): RecordCoverageIfTapped was entirely unpinned. A normal
    /// first-coverage pass must emit exactly one "coverage" record, trigger=first.</summary>
    [Fact]
    public void Coverage_record_is_emitted_when_pool_coverage_latches()
    {
        var (display, clock, recorded) = BuildSingleWeaponPoolDisplay();

        clock.Ms += DisplaySweep.HotRescanMs + 1;
        CardFixtures.TickWithPoolLocate(display, false);

        var firstPass = recorded.FindAll(r => r.type == "card" && r.payload.StartsWith("coverage"));
        Assert.Single(firstPass);
        Assert.Contains("trigger=first", firstPass[0].payload);
    }

    /// <summary>Item 2 (round-4 review): CoverageRecordBudget and its Invalidate() reset were
    /// unpinned. Drives _coverageBudget straight to the cap (Display._coverageBudget, exposed for
    /// exactly this -- manufacturing CoverageRecordBudget real coverage-latch transitions just to
    /// reach the same state would be expensive for no extra fidelity) instead of asserting after
    /// only ONE real record, which would pass even with a completely broken reset (1 is nowhere
    /// near the cap of 8, so that alone proves nothing about the reset specifically).</summary>
    [Fact]
    public void Coverage_record_budget_caps_then_resets_on_Invalidate()
    {
        var (display, clock, recorded) = BuildSingleWeaponPoolDisplay();

        display._coverageBudget = Display.CoverageRecordBudget;   // simulate "already exhausted"

        clock.Ms += DisplaySweep.HotRescanMs + 1;
        CardFixtures.TickWithPoolLocate(display, false);
        Assert.DoesNotContain(recorded, r => r.type == "card" && r.payload.StartsWith("coverage"));

        display.Invalidate();
        clock.Ms += DisplaySweep.HotRescanMs + 1;
        CardFixtures.TickWithPoolLocate(display, false);
        Assert.Contains(recorded, r => r.type == "card" && r.payload.StartsWith("coverage"));
    }

    /// <summary>LW-261 test 8: PoolLocator.Step's own completion tap (Display.PoolLocate.cs's
    /// RecordLocateCompleteIfTapped), driven directly here since StepPoolLocate is normally called
    /// by Engine's own "pool-locate" tick lane, not by anything this unit-test level fixture wires
    /// up. The pool buffer is small enough to complete within the FIRST Step call, so exactly one
    /// "locate-complete" record must land, never more across the remaining (no-op) calls.</summary>
    [Fact]
    public void Locate_completion_emits_one_card_flight_record()
    {
        var meta = new Dictionary<int, WeaponMeta>
        {
            { 10, new WeaponMeta { Name = "BowX", Flavor = "Fletched with regret" } },
        };
        var kills = new Dictionary<int, int> { { 10, 7 } };
        var clock = new TestClock();

        var poolBuf = new byte[500];
        CardFixtures.WriteCardForwardWithName(poolBuf, 0, "BowX", "Fletched with regret");
        long poolBase = 0x7E_5000_0000L;
        long staticsBase = 0x7E_6000_0000L;
        var statics = new byte[64];
        statics[0] = 10;
        var heap = new FakeHeap((poolBase, poolBuf, true), (staticsBase, statics, true));
        var recorded = new List<(string type, string payload)>();
        var display = CardFixtures.MakeDisplay(meta, kills, heap, staticsBase, clock, poolPaint: true,
            recorder: (t, p) => recorded.Add((t, p)));

        for (int i = 0; i < 20; i++) { display.StepPoolLocate(false); clock.Ms += 33; }

        var hits = recorded.FindAll(r => r.type == "card" && r.payload.StartsWith("locate-complete"));
        Assert.Single(hits);
        Assert.Contains("regions=1", hits[0].payload);
        Assert.Contains("trigger=first", hits[0].payload);
    }

    /// <summary>Shared fixture for the two coverage-record tests above: one weapon, a single pool
    /// region, poolPaint:true, a recorder wired.</summary>
    private static (Display display, TestClock clock, List<(string type, string payload)> recorded) BuildSingleWeaponPoolDisplay()
    {
        var meta = new Dictionary<int, WeaponMeta>
        {
            { 10, new WeaponMeta { Name = "BowX", Flavor = "Fletched with regret" } },
        };
        var kills = new Dictionary<int, int> { { 10, 7 } };
        var clock = new TestClock();

        var poolBuf = new byte[500];
        CardFixtures.WriteCardForwardWithName(poolBuf, 0, "BowX", "Fletched with regret");

        long poolBase = 0x59_5000_0000L;
        long staticsBase = 0x59_6000_0000L;
        var statics = new byte[64];
        statics[0] = 10;

        var heap = new FakeHeap((poolBase, poolBuf, true), (staticsBase, statics, true));
        var recorded = new List<(string type, string payload)>();
        var display = CardFixtures.MakeDisplay(meta, kills, heap, staticsBase, clock, poolPaint: true, recorder: (t, p) => recorded.Add((t, p)));
        return (display, clock, recorded);
    }
}
