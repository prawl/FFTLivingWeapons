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

    /// <summary>LW-259: EmitVerdict's three tiers only order spending WITHIN one pass -- across
    /// many passes in the SAME window (no Invalidate between them), routine tier-2 paint lines
    /// can exhaust the entire shared <see cref="Display.FlightRecordBudget"/> (64) on their own,
    /// long before a rare tier-1 eviction ever shows up. One live kills site is repainted across
    /// exactly Display.FlightRecordBudget separate PaintCountsIfChanged passes -- a changing kills
    /// value each time so every pass genuinely writes and Note()s exactly one Wrote entry, spending
    /// the shared budget one-for-one (64 passes, 64 "paint" records, honestly earned, not a single
    /// oversized pass). Only THEN does a second site with a mismatched anchor get registered and
    /// painted -- tier 1's own Evicted loop -- reproducing the live failure: a late strike-cap or
    /// buffer-reuse eviction silently dropped because the shared budget a storm of routine paints
    /// already emptied.</summary>
    [Fact]
    public void A_late_eviction_still_records_after_paint_lines_exhaust_the_shared_budget()
    {
        const int id = 10;
        var meta = BuildMeta();   // id 10, Flavor = "Bright edge of dawn"
        var kills = new Dictionary<int, int> { { id, 0 } };
        var clock = new TestClock();

        var buf = new byte[1024];
        int slotPos = CardFixtures.WriteKillsBlock(buf, 0, "Bright edge of dawn", gap: 20);
        int wrongAnchorPos = 200;
        var wrongFlavor = ByteScan.Ascii(new string('X', "Bright edge of dawn".Length));
        Array.Copy(wrongFlavor, 0, buf, wrongAnchorPos, wrongFlavor.Length);

        var heap = BuildHeap(buf);
        var recorded = new List<(string type, string payload)>();
        var display = CardFixtures.MakeDisplay(meta, kills, heap, StaticsBase, clock, recorder: (t, p) => recorded.Add((t, p)));

        // The one live kills site, registered directly (bypassing the scan), so each pass below
        // is a pure repaint like a live kill landing -- CardFixtures.WriteKillsBlock's own idiom.
        display._sites.Add(new CardSites.Site(id, 1, SourceBase + slotPos, SourceBase + 0, IsKills: true));

        // Spend the WHOLE shared budget across Display.FlightRecordBudget separate passes, one
        // Wrote record per pass (a changing kills value each time so ByteEq never skips the write).
        for (int i = 1; i <= Display.FlightRecordBudget; i++)
        {
            kills[id] = i;
            display.PaintCountsIfChanged();
        }
        int paintRecordsAfterExhaustion = recorded.FindAll(r => r.type == "card" && r.payload.StartsWith($"paint id={id}")).Count;
        Assert.Equal(Display.FlightRecordBudget, paintRecordsAfterExhaustion);   // honestly earned, one per pass

        // NOW register the mismatched-anchor site and paint one more pass: a genuine tier-1
        // eviction landing in the SAME window the loop above already emptied the shared budget in.
        long evictedSlotAddr = SourceBase + wrongAnchorPos + 10_000;   // never read -- the mismatch evicts first
        display._sites.Add(new CardSites.Site(id, 1, evictedSlotAddr, SourceBase + wrongAnchorPos, IsKills: true));
        kills[id] = 999;
        display.PaintCountsIfChanged();

        Assert.Contains(recorded, r => r.type == "card" && r.payload.Contains("site-evicted")
                                        && r.payload.Contains($"id={id}") && r.payload.Contains("reason=anchor-mismatch"));
    }

    /// <summary>LW-259: <see cref="Display.EvictedRecordBudget"/> pin, mirroring every other
    /// reserve's own clamp test in this file (Coverage_record_budget_caps_then_resets_on_Invalidate
    /// below). More than the reserve's worth of GENUINE tier-1 evictions (mismatched anchors, not
    /// OnSitePruned's cap-relief bursts -- so the count filters on `reason=anchor-mismatch`,
    /// deliberately excluding `reason=pruned-dead`) land in a single pass: exactly
    /// EvictedRecordBudget of them get recorded, the rest silently dropped past the reserve, same
    /// diagnostic-not-transcript contract every other budget in this file already accepts.</summary>
    [Fact]
    public void Evicted_record_budget_clamps_even_with_more_genuine_evictions_in_one_pass()
    {
        const int id = 10;
        const int blockStride = 64;
        int mismatchCount = Display.EvictedRecordBudget + 4;   // more tier-1 evictions than the reserve holds

        var meta = BuildMeta();   // id 10, Flavor = "Bright edge of dawn"
        var kills = new Dictionary<int, int> { { id, 8 } };
        var clock = new TestClock();

        var buf = new byte[mismatchCount * blockStride + 256];
        var wrongFlavor = ByteScan.Ascii(new string('X', "Bright edge of dawn".Length));
        for (int i = 0; i < mismatchCount; i++)
            Array.Copy(wrongFlavor, 0, buf, i * blockStride, wrongFlavor.Length);   // every anchor mismatches

        var heap = new FakeHeap((SourceBase, buf, true));
        heap.AddRegion(StaticsBase, new byte[16], writable: true);
        var recorded = new List<(string type, string payload)>();
        var display = CardFixtures.MakeDisplay(meta, kills, heap, StaticsBase, clock, recorder: (t, p) => recorded.Add((t, p)));

        for (int i = 0; i < mismatchCount; i++)
            display._sites.Add(new CardSites.Site(id, 1, SourceBase + i * blockStride + 10_000, SourceBase + i * blockStride, IsKills: true));
        Assert.Equal(mismatchCount, display._sites.Count);

        display.PaintCountsIfChanged();   // one pass: every one of the mismatchCount sites evicts

        int evictedRecords = recorded.FindAll(r => r.type == "card" && r.payload.Contains("site-evicted")
                                                     && r.payload.Contains("reason=anchor-mismatch")).Count;
        Assert.Equal(Display.EvictedRecordBudget, evictedRecords);
    }

    /// <summary>LW-259: EvictedRecordBudget's own Invalidate() reset, mirroring
    /// Coverage_record_budget_caps_then_resets_on_Invalidate's shape exactly -- drives
    /// <see cref="Display._evictedBudget"/> straight to the cap (the reserve's own test-accessor
    /// convention, this file) instead of manufacturing EvictedRecordBudget real evictions just to
    /// reach the same state.</summary>
    [Fact]
    public void Evicted_record_budget_resets_on_Invalidate()
    {
        const int id = 10;
        var meta = BuildMeta();
        var kills = new Dictionary<int, int> { { id, 8 } };
        var clock = new TestClock();

        var buf = new byte[512];
        var wrongFlavor = ByteScan.Ascii(new string('X', "Bright edge of dawn".Length));
        Array.Copy(wrongFlavor, 0, buf, 0, wrongFlavor.Length);   // mismatches on read

        var heap = new FakeHeap((SourceBase, buf, true));
        heap.AddRegion(StaticsBase, new byte[16], writable: true);
        var recorded = new List<(string type, string payload)>();
        var display = CardFixtures.MakeDisplay(meta, kills, heap, StaticsBase, clock, recorder: (t, p) => recorded.Add((t, p)));

        display._evictedBudget = Display.EvictedRecordBudget;   // simulate "already exhausted"

        display._sites.Add(new CardSites.Site(id, 1, SourceBase + 10_000, SourceBase + 0, IsKills: true));
        display.PaintCountsIfChanged();   // kills[id]=8 vs empty _lastCounts reads as a change
        Assert.DoesNotContain(recorded, r => r.type == "card" && r.payload.Contains("site-evicted")
                                              && r.payload.Contains("reason=anchor-mismatch"));

        display.Invalidate();
        display._sites.Add(new CardSites.Site(id, 1, SourceBase + 10_000, SourceBase + 0, IsKills: true));   // Invalidate wiped the cache
        kills[id] = 9;   // force a genuine count change so this pass actually paints
        display.PaintCountsIfChanged();

        Assert.Contains(recorded, r => r.type == "card" && r.payload.Contains("site-evicted")
                                        && r.payload.Contains("reason=anchor-mismatch"));
    }

    /// <summary>LW-259 fix pin: a starvation bug the split itself introduced. EmitVerdict's tier-1
    /// loop guard used to be `if (_evictedBudget >= EvictedRecordBudget) return;` -- fine
    /// pre-split when every tier shared one budget, but once tier 1 got its OWN <see
    /// cref="Display._evictedBudget"/> reserve that `return` aborts the WHOLE method the moment
    /// the reserve caps, silencing tiers 2/3's genuinely separate <see cref="Display._flightBudget"/>
    /// lane for the rest of the window even though that budget has 63/64 of headroom left
    /// (DrainGeneration's settle pass spends one paint line here before the pre-seed). Mirrors
    /// Recorder_receives_a_card_record_when_a_site_is_painted's own paint recipe exactly, but
    /// drives _evictedBudget straight to the cap first (the reserve's own test-accessor
    /// convention, this file) -- no eviction traffic at all, so if tier 1's guard were still
    /// `return` this test fails with the to=8 repaint record missing.</summary>
    [Fact]
    public void Paint_records_still_flow_after_the_eviction_reserve_caps()
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

        display._evictedBudget = Display.EvictedRecordBudget;   // simulate "already exhausted";
                                                                  // _flightBudget keeps its headroom

        kills[10] = 8;
        display.PaintCountsIfChanged();   // routes through PaintAllTapped -> CardSites.PaintAll(verdict)

        Assert.Contains(recorded, r => r.type == "card" && r.payload.StartsWith("paint id=10 to=8"));
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

    /// <summary>LW-262 test 5: CardSites.Admission.cs's cap-relief prune now reaches Display's
    /// flight tape via the onPruneEvict ctor param (Display.cs) and Display.Flight.cs's
    /// OnSitePruned -- the round-3 review's "UNTAPPED" gap this arc closes. Drives the real
    /// Display-owned CardSites (not a bare CardSites instance) so the wiring itself is proven, not
    /// just the underlying prune logic CardSitesSuffixPartitionTests already pins.</summary>
    [Fact]
    public void Pruned_dead_site_reaches_the_flight_tape_as_site_evicted()
    {
        var meta = BuildMeta();
        var kills = new Dictionary<int, int> { { 10, 5 } };
        var clock = new TestClock();
        var heap = new FakeHeap((StaticsBase, new byte[16], writable: true));
        var recorded = new List<(string type, string payload)>();
        var display = CardFixtures.MakeDisplay(meta, kills, heap, StaticsBase, clock, recorder: (t, p) => recorded.Add((t, p)));

        // Fill the KILLS cap with dead sites (anchors point at unmapped memory, never written to
        // any FakeHeap region), then one more Add hits the cap and triggers the cap-relief prune
        // (_pruneImmediately starts true on a fresh CardSites, so this is the FIRST cap-hit).
        for (int i = 0; i < CardSites.MaxSites; i++)
            display._sites.Add(new CardSites.Site(10, 1, 0xDEAD_5000_0000L + i, 0xDEAD_0000_0000L + i, IsKills: true));
        Assert.Equal(CardSites.MaxSites, display._sites.Count);

        var trigger = new CardSites.Site(10, 1, 0xDEAD_6000_0000L, 0xDEAD_0000_0001L, IsKills: true);
        Assert.True(display._sites.Add(trigger), "the cap-relief prune should have freed room");

        Assert.Contains(recorded, r => r.type == "card" && r.payload.Contains("site-evicted")
                                        && r.payload.Contains("reason=pruned-dead"));
    }

    /// <summary>LW-269: OnSitePruned (Display.Flight.cs) spends the SAME shared _flightBudget/
    /// FlightRecordBudget tiers EmitVerdict's paint and site-refused lanes already spend -- a
    /// dedicated reserve was explicitly rejected (that method's own class doc). LW-259
    /// CORRECTION: this used to say "EmitVerdict's site-evicted lane" here, back when that lane
    /// drew from this same _flightBudget too -- it no longer does (its own EvictedRecordBudget
    /// reserve, EmitVerdict's own doc), so today's shared-budget spenders are EmitVerdict's tier 2
    /// (paint) and tier 3 (site-refused) lines plus this method, not tier 1. A cap-relief prune
    /// that evicts thousands of dead sites in one burst must still stop recording at exactly the
    /// budget, and once that budget is spent, a genuinely separate paint that happens later in
    /// the SAME window must have its OWN record suppressed too -- proving the two taps really do
    /// share one counter, not two counters that merely happen to be sized the same.</summary>
    [Fact]
    public void Cap_relief_prune_records_stop_at_the_shared_flight_budget()
    {
        var meta = BuildMeta();   // id 10
        var kills = new Dictionary<int, int> { { 10, 5 } };
        var clock = new TestClock();
        clock.Ms = 100_000;   // repo test hygiene (LW-261 lesson): nonzero clock origin; not
                              // load-bearing here -- this test's vacuity guard is the
                              // heap.Writes assert below, not the clock

        var src = new byte[512];
        int slotPos = CardFixtures.WriteKillsBlock(src, 0, "Bright edge of dawn", gap: 20);
        var heap = BuildHeap(src);
        var recorded = new List<(string type, string payload)>();
        var display = CardFixtures.MakeDisplay(meta, kills, heap, StaticsBase, clock, recorder: (t, p) => recorded.Add((t, p)));

        // Fill the KILLS cache to cap with dead sites (unmapped anchors), exactly like this
        // file's Pruned_dead_site_reaches_the_flight_tape_as_site_evicted above.
        for (int i = 0; i < CardSites.MaxSites; i++)
            display._sites.Add(new CardSites.Site(10, 1, 0xDEAD_1000_0000L + i, 0xDEAD_0000_0000L + i, IsKills: true));
        Assert.Equal(CardSites.MaxSites, display._sites.Count);

        // One more Add hits the cap: the cap-relief prune evicts the WHOLE dead fill (2048
        // sites) in a single burst, then admits this one live site.
        var live = new CardSites.Site(10, 1, SourceBase + slotPos, SourceBase, IsKills: true);
        Assert.True(display._sites.Add(live), "the cap-relief prune should have freed room for the live site");
        Assert.Equal(1, display._sites.Count);

        int pruned = 0;
        foreach (var r in recorded)
            if (r.type == "card" && r.payload.Contains("reason=pruned-dead"))
            {
                pruned++;
                Assert.StartsWith("site-evicted id=10 addr=0x", r.payload);
            }
        // Thousands of evictions happened above; the records clamp at EXACTLY the shared budget.
        Assert.Equal(Display.FlightRecordBudget, pruned);

        // Sharedness half: the budget the prune just exhausted is the SAME one EmitVerdict's
        // paint lane spends, not a private reserve.
        int writesBefore = heap.Writes;
        display.PaintCountsIfChanged();   // kills[10]=5 vs empty _lastCounts reads as a change,
                                           // so it paints through PaintAllTapped
        Assert.Equal(writesBefore + 1, heap.Writes);   // the paint genuinely wrote
        Assert.DoesNotContain(recorded, r => r.type == "card" && r.payload.StartsWith("paint id=10"));
        // ^ suppressed: the prune above already exhausted the shared budget for this window.
    }

    /// <summary>LW-262 test 6: the coverage record's new `suffix=` field (Display.Flight.cs's
    /// RecordCoverageIfTapped) must carry the REAL CardSites.SuffixCount, not just be present --
    /// asserted against an independently hardcoded expected count that DIFFERS from the kills
    /// count (see the F5(b) note in the body: asserting against the live accessor was
    /// tautological, since the record reads that same property).</summary>
    [Fact]
    public void Coverage_record_carries_the_real_suffix_site_count()
    {
        // Verifier F5(b) correction: the old version of this test asserted against
        // `display._sites.SuffixCount` itself (`$"suffix={display._sites.SuffixCount}"`), which
        // is tautological -- RecordCoverageIfTapped reads that SAME property, so the assertion
        // could never distinguish "the payload carries the real count" from "the payload carries
        // whatever this property happens to return", correct or not. Seeds extra suffix sites
        // directly (bypassing the real scan) so kills and suffix are two INDEPENDENTLY KNOWN,
        // hardcoded numbers that must differ, proven against literal expected text.
        var (display, clock, recorded) = BuildSingleWeaponPoolDisplay();   // one tracked id (10), one real card -> 1 kills site, 1 real suffix site once scanned

        // id 10 (not a fake untracked id): VerifyAnchor's first step is _pats.TryGet(s.Id, ...),
        // which returns Mismatch (evicts on its FIRST occurrence, no leniency) for any id absent
        // from meta -- an untracked fake id was tried first and got wiped by the very first
        // PaintAllTapped() inside Tick(), before the scan ever ran. Id 10 IS tracked, so
        // VerifyAnchor gets past that check; the unmapped AnchorAddr then reads Unreadable (not
        // Mismatch), which is LENIENT (Tuning.CardEvictStrikes=3 misses before eviction) and
        // survives the single PaintAllTapped call this test drives.
        const int seededExtraSuffixSites = 4;
        for (int i = 0; i < seededExtraSuffixSites; i++)
            display._sites.Add(new CardSites.Site(10, 1, 0x6F00_0000L + i, 0x6F01_0000L + i, IsKills: false));

        clock.Ms += DisplaySweep.HotRescanMs + 1;
        CardFixtures.TickWithPoolLocate(display, false);   // discovers the 1 real kills site and 1 real suffix site for id 10

        const int expectedKills = 1;
        const int expectedSuffix = 1 + seededExtraSuffixSites;   // 1 real + 4 seeded = 5
        Assert.NotEqual(expectedKills, expectedSuffix);   // the two numbers must genuinely differ for this test to discriminate anything

        var firstPass = recorded.FindAll(r => r.type == "card" && r.payload.StartsWith("coverage"));
        Assert.Single(firstPass);
        Assert.Contains($"kills={expectedKills}", firstPass[0].payload);
        Assert.Contains($"suffix={expectedSuffix}", firstPass[0].payload);
    }

    /// <summary>LW-262 test 7: the end-to-end feedback-loop pin. A pool region holding far more
    /// live suffix-pattern hits than MaxSuffixSites (found through the REAL ScanPoolRegion/OnChunk
    /// path, not seeded directly into CardSites like test 5 above) must still let kills coverage
    /// latch on the very FIRST locate/scan pass -- exactly one "coverage" record, trigger=first,
    /// no repeat locate fall-through. This is the actual bug the 2026-08-18 live tape showed: a
    /// suffix flood filling the (then-shared) cache before kills coverage could ever be reported
    /// complete, forcing an endless re-search loop.</summary>
    [Fact]
    public void Suffix_flood_in_the_real_pool_scan_still_lets_coverage_latch()
    {
        // Verifier F4 correction: a single-id flood only ever exercises the PER-ID cap (12), so
        // the latch assertion below used to pass even with BOTH caps disabled -- the per-id gate
        // alone already kept one id's suffix count small enough to never threaten coverage.
        // MANY ids instead, each contributing well under SuffixCopiesPerId copies, so the total
        // across ids exceeds MaxSuffixSites and only the GLOBAL cap can be what binds.
        const int idCount = 300;
        const int copiesPerId = 4;   // 300 * 4 = 1200 > MaxSuffixSites (1024), each id well under SuffixCopiesPerId (12)
        var meta = new Dictionary<int, WeaponMeta>();
        var kills = new Dictionary<int, int>();
        for (int i = 0; i < idCount; i++)
        {
            meta[i] = new WeaponMeta { Name = $"Bow{i:D3}", Flavor = $"Flv{i:D3}" };
            kills[i] = 0;
        }

        const int stride = 48;
        var poolBuf = new byte[idCount * copiesPerId * stride + 1024];
        int pos = 0;
        for (int i = 0; i < idCount; i++)
            for (int c = 0; c < copiesPerId; c++)
            {
                CardFixtures.WriteCardForwardWithName(poolBuf, pos, $"Bow{i:D3}", $"Flv{i:D3}");
                pos += stride;
            }

        long poolBase = 0x5A_5000_0000L;
        long staticsBase = 0x5A_6000_0000L;
        var statics = new byte[64];
        statics[0] = 0;

        var heap = new FakeHeap((poolBase, poolBuf, true), (staticsBase, statics, true));
        var recorded = new List<(string type, string payload)>();
        var display = CardFixtures.MakeDisplay(meta, kills, heap, staticsBase, clock: new TestClock(), poolPaint: true,
            recorder: (t, p) => recorded.Add((t, p)));

        CardFixtures.TickWithPoolLocate(display, false);

        var firstPass = recorded.FindAll(r => r.type == "card" && r.payload.StartsWith("coverage"));
        Assert.Single(firstPass);   // latches on the FIRST pass -- no repeat locate fall-through
        Assert.Contains("trigger=first", firstPass[0].payload);
        // No single id offers more than copiesPerId (4), well under SuffixCopiesPerId (12), so
        // ONLY the global cap can be responsible for this exact number.
        Assert.Equal(CardSites.MaxSuffixSites, display._sites.SuffixCount);
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
