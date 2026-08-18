using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-257 commit 2: Display.Heartbeat.cs's shared maintenance beat (MaintenanceDue/RunMaintenance)
/// and the pending-id / per-region drain mechanisms built on top of it. Fixture idioms mirror the
/// established per-file convention: DisplayMaintenanceTests' stale-slot recipe for the beat itself,
/// DisplayPoolPaintTests' RegionsSpyMem (copied, not referenced -- that file's own no-cross-file-
/// reference convention for test-only fakes) for proving no full relocate ever ran.
/// </summary>
public class DisplayHeartbeatTests
{
    // ─── T7/T8: the on-field path (PaintCountsIfChanged) gets the shared beat ─────

    private const long StaticsBase = 0x78_0000_0000L;
    private const long SourceBase  = 0x79_0000_0000L;

    private static Dictionary<int, WeaponMeta> BuildMeta() => new()
    {
        { 10, new WeaponMeta { Name = "SwordA", Flavor = "Bright edge of dawn", Wp = 12, Cat = "Sword", Formula = 1 } },
    };

    private static string ReadSlot(FakeHeap heap, long pos, int len)
    {
        heap.TryReadBytes(SourceBase + pos, len, out var buf);
        return System.Text.Encoding.ASCII.GetString(buf);
    }

    /// <summary>Mirrors DisplayMaintenanceTests.Stale_slot_repainted_within_maintenance_cadence
    /// exactly, but drives ONLY PaintCountsIfChanged() (Engine.cs's ShouldPaintCard-false branch)
    /// instead of Tick -- before this commit, PaintCountsIfChanged never touched the maintenance
    /// clock at all, so a stale cached slot with no fresh count change would never self-heal on
    /// the on-field path. RED today: the old method returns immediately when CheckAndSnapshotCounts
    /// reports no change, never reaching any maintenance check.</summary>
    [Fact]
    public void On_field_path_repaints_every_cached_site_once_per_MaintenanceMs_with_no_count_change()
    {
        var meta = BuildMeta();
        var kills = new Dictionary<int, int> { { 10, 9 } };
        var clock = new TestClock();
        var src = new byte[512];
        var card = CardFixtures.WriteCard(src, 0, "SwordA", "Bright edge of dawn");
        var heap = new FakeHeap((SourceBase, src), (StaticsBase, new byte[16]));
        var display = CardFixtures.MakeDisplay(meta, kills, heap, StaticsBase, clock);

        CardFixtures.DrainGeneration(display, clock, 500);
        Assert.Equal(Signatures.KillsMeterSlot(9), ReadSlot(heap, card.killsSlotPos, Signatures.KillsMeterSlotChars));

        // Simulate a stale on-screen copy with NO count change at all (kills[10] stays 9).
        heap.WriteBytes(SourceBase + card.killsSlotPos, ByteScan.Ascii(Signatures.KillsMeterSlot(0)));

        clock.Ms += Display.MaintenanceMs + 1;
        display.PaintCountsIfChanged();   // ONLY this -- Tick is never called again below

        Assert.Equal(Signatures.KillsMeterSlot(9), ReadSlot(heap, card.killsSlotPos, Signatures.KillsMeterSlotChars));
    }

    /// <summary>The old bug's exact shape: a kill lands while the cache is empty (nothing to
    /// paint), then a site for that id appears; before this commit the count-change edge was
    /// already consumed into _lastCounts on the first call, so nothing would ever notice the
    /// id needed painting once a site showed up. RED today (Display.cs's old PaintCountsIfChanged:
    /// the CheckAndSnapshotCounts edge is consumed regardless, and the empty-cache branch just
    /// drops the paint with no record that anything is still owed).</summary>
    [Fact]
    public void Kill_while_the_cache_is_empty_still_lands_once_a_site_appears()
    {
        var meta = BuildMeta();
        var kills = new Dictionary<int, int> { { 10, 5 } };
        var clock = new TestClock();
        var src = new byte[512];
        var card = CardFixtures.WriteCard(src, 0, "SwordA", "Bright edge of dawn", slot: Signatures.KillsMeterSlot(5));
        var heap = new FakeHeap((StaticsBase, new byte[16]));   // SourceBase not added yet: the site cache is empty
        var display = CardFixtures.MakeDisplay(meta, kills, heap, StaticsBase, clock);

        kills[10] = 8;
        display.PaintCountsIfChanged();   // sites empty: nothing to paint, but the id is now pending

        heap.AddRegion(SourceBase, src, writable: true);
        display._sites.Add(new CardSites.Site(10, 1, SourceBase + card.killsSlotPos, SourceBase + card.flavorPos, IsKills: true));

        clock.Ms += Display.MaintenanceMs + 1;
        display.PaintCountsIfChanged();   // one maintenance beat: the newly-registered site gets painted

        Assert.Equal(Signatures.KillsMeterSlot(8), ReadSlot(heap, card.killsSlotPos, Signatures.KillsMeterSlotChars));
    }

    // ─── T9/T10: the pending set itself ────────────────────────────────────────

    /// <summary>The anti-wrong-copy pin. id 20's ONLY known site sits outside the one pool
    /// region this fixture locates (for an unrelated id 10); even though that site reads back
    /// correctly (settled under a NAIVE "any live site anywhere" predicate), the CORRECT clear
    /// predicate must not accept it once at least one region is actually located. RED today
    /// (_pendingIds/CachedRegions do not exist yet); would also RED against a naive
    /// implementation that calls verdict.Settled(id, _ => true) unconditionally.</summary>
    [Fact]
    public void A_pending_id_is_not_cleared_by_a_site_outside_every_located_region()
    {
        var meta = new Dictionary<int, WeaponMeta>
        {
            { 10, new WeaponMeta { Name = "BowX", Flavor = "Fletched with regret" } },
            { 20, new WeaponMeta { Name = "SwordZ", Flavor = "A quiet menace" } },
        };
        var kills = new Dictionary<int, int> { { 10, 7 }, { 20, 5 } };
        var clock = new TestClock();

        var poolBuf = new byte[500];
        CardFixtures.WriteCardForwardWithName(poolBuf, 0, "BowX", "Fletched with regret");

        var outsideBuf = new byte[300];
        var (_, outsideSlotPos, outsideFlavorPos) =
            CardFixtures.WriteCardForwardWithName(outsideBuf, 0, "SwordZ", "A quiet menace");

        long poolBase = 0x7A_0000_0000L;
        long outsideBase = 0x7B_0000_0000L;
        long staticsBase = 0x7C_0000_0000L;
        var statics = new byte[64];
        statics[0] = 10;

        var heap = new FakeHeap((poolBase, poolBuf, true), (staticsBase, statics, true));
        var display = CardFixtures.MakeDisplay(meta, kills, heap, staticsBase, clock, poolPaint: true);

        CardFixtures.TickWithPoolLocate(display, false);   // locates the pool region (id 10's card)

        bool poolSiteFound = false;
        foreach (var s in display._sites.Snapshot())
            if (s.Id == 10 && s.SlotAddr >= poolBase && s.SlotAddr < poolBase + poolBuf.Length) poolSiteFound = true;
        Assert.True(poolSiteFound, "setup must actually locate the pool region");

        // id 20's card exists ONLY here -- outside the located region, and nothing has scanned it.
        heap.AddRegion(outsideBase, outsideBuf, writable: true);
        display._sites.Add(new CardSites.Site(20, 1, outsideBase + outsideSlotPos, outsideBase + outsideFlavorPos, IsKills: true));

        kills[20] = 9;
        display.PaintCountsIfChanged();   // stages id 20 pending (never touches the pool/sweep)
        Assert.True(display._pendingIds.ContainsKey(20));

        clock.Ms += Display.MaintenanceMs + 1;
        display.PaintCountsIfChanged();   // one maintenance beat: the outside site settles (Wrote)

        Assert.Equal(Signatures.KillsMeterSlot(9), ReadSlot2(heap, outsideBase + outsideSlotPos, Signatures.KillsMeterSlotChars));
        Assert.True(display._pendingIds.ContainsKey(20),
            "a settled site outside every located region must not clear the pending id");
    }

    private static string ReadSlot2(FakeHeap heap, long addr, int len)
    {
        heap.TryReadBytes(addr, len, out var buf);
        return System.Text.Encoding.ASCII.GetString(buf);
    }

    /// <summary>Anti-storm: an id that can never settle (no card anywhere in the heap) must stop
    /// being WATCHED after exactly Tuning.CardPendingMaxBeats beats, not sit in the pending set
    /// forever -- a watchdog cap, not a retry (Display.Heartbeat.cs's own class doc: no extra
    /// paint attempt is ever gated on pending status either way). RED under a mutation removing
    /// the cap check (next &gt;= Tuning.CardPendingMaxBeats never true).</summary>
    [Fact]
    public void A_pending_id_stops_being_watched_after_CardPendingMaxBeats()
    {
        var meta = new Dictionary<int, WeaponMeta>
        {
            { 10, new WeaponMeta { Name = "SwordA", Flavor = "Bright edge of dawn" } },
        };
        var kills = new Dictionary<int, int> { { 10, 5 } };
        var clock = new TestClock();
        var heap = new FakeHeap((StaticsBase, new byte[16]));   // no card anywhere: id 10 can never settle
        var display = CardFixtures.MakeDisplay(meta, kills, heap, StaticsBase, clock);

        kills[10] = 8;
        display.PaintCountsIfChanged();
        Assert.True(display._pendingIds.ContainsKey(10));

        for (int beat = 1; beat < Tuning.CardPendingMaxBeats; beat++)
        {
            clock.Ms += Display.MaintenanceMs + 1;
            display.PaintCountsIfChanged();
            Assert.True(display._pendingIds.ContainsKey(10), $"must still be pending after beat {beat}");
        }

        clock.Ms += Display.MaintenanceMs + 1;
        display.PaintCountsIfChanged();
        Assert.False(display._pendingIds.ContainsKey(10), "must be dropped after CardPendingMaxBeats beats");
    }

    // ─── LW-261: RegionsStale keeps ProcessPending permissive across a queued restart ──

    /// <summary>The regression this arc's own PoolLocator rewrite could have introduced: since
    /// Invalidate() no longer clears CachedRegions (PoolLocator.cs's own class doc -- it queues a
    /// restart and marks the cache STALE instead), a naive ProcessPending that keys its permissive
    /// branch off "regions.Count == 0" alone would now see a non-empty (but stale) cache right
    /// after a battle edge and wrongly take the RESTRICTIVE branch -- judging a freshly-painted
    /// site at a brand-new (post-relocation) address against the OLD region list, which never
    /// contains it, so the id never settles and the watchdog eventually logs a false "gave up".
    /// RED under that naive predicate: regions.Count stays 1 (the stale poolBase entry) the whole
    /// test, so InAnyRegion(newBase, regions) is false and the pending id never clears.</summary>
    [Fact]
    public void ProcessPending_after_invalidate_still_settles_ids_painted_at_new_addresses()
    {
        var meta = new Dictionary<int, WeaponMeta>
        {
            { 10, new WeaponMeta { Name = "BowX", Flavor = "Fletched with regret" } },
        };
        var kills = new Dictionary<int, int> { { 10, 7 } };
        var clock = new TestClock();

        var poolBuf = new byte[500];
        CardFixtures.WriteCardForwardWithName(poolBuf, 0, "BowX", "Fletched with regret");
        long poolBase = 0x7E_1000_0000L;
        long staticsBase = 0x7E_2000_0000L;
        var statics = new byte[64];
        statics[0] = 10;
        var heap = new FakeHeap((poolBase, poolBuf, true), (staticsBase, statics, true));
        var display = CardFixtures.MakeDisplay(meta, kills, heap, staticsBase, clock, poolPaint: true);

        CardFixtures.TickWithPoolLocate(display, false);   // locates+covers the original pool region

        display.Invalidate();   // battle-edge style: RegionsStale flips true; CachedRegions keeps
                                 // serving the OLD (poolBase) list until a fresh scan republishes

        // A relocated card at a brand-new address, OUTSIDE the (now stale) cached region -- the
        // battle-exit shape (menu buffers reallocated). Registered directly, mirroring
        // A_pending_id_is_not_cleared_by_a_site_outside_every_located_region's own recipe.
        var newBuf = new byte[500];
        var (_, newSlot, newFlavor) = CardFixtures.WriteCardForwardWithName(newBuf, 0, "BowX", "Fletched with regret");
        long newBase = 0x7E_3000_0000L;
        heap.AddRegion(newBase, newBuf, writable: true);
        display._sites.Add(new CardSites.Site(10, 1, newBase + newSlot, newBase + newFlavor, IsKills: true));

        kills[10] = 9;
        display.PaintCountsIfChanged();   // stages id 10 pending -- the on-field path never steps the pool locator
        Assert.True(display._pendingIds.ContainsKey(10));

        clock.Ms += Display.MaintenanceMs + 1;
        display.PaintCountsIfChanged();   // one beat: the relocated site settles under the PERMISSIVE branch

        Assert.False(display._pendingIds.ContainsKey(10),
            "RegionsStale after Invalidate must route ProcessPending onto the permissive branch, exactly like an empty cache did before this arc");
    }

    // ─── T11/T12: the per-region drain check ───────────────────────────────────

    /// <summary>Losing ONE region's copy of a weapon that still has a copy elsewhere (CoversAllMeta
    /// stays true) must re-offer ONLY that region -- via the existing ScanPoolRegion -- not a full
    /// relocate. RED today: MaybePoolPaint's cheap aggregate re-latch just accepts the smaller
    /// count and never looks again, so region B's copy never comes back.</summary>
    [Fact]
    public void Losing_one_copy_of_a_still_covered_weapon_re_offers_only_that_region()
    {
        var meta = new Dictionary<int, WeaponMeta>
        {
            { 10, new WeaponMeta { Name = "BowX", Flavor = "Fletched with regret" } },
        };
        var kills = new Dictionary<int, int> { { 10, 7 } };
        var clock = new TestClock();

        var bufA = new byte[600];
        var (_, slotA, _) = CardFixtures.WriteCardForwardWithName(bufA, 0, "BowX", "Fletched with regret");

        var bufB = new byte[900];
        var (_, slotB1, flavorB1) = CardFixtures.WriteCardForwardWithName(bufB, 0, "BowX", "Fletched with regret");

        long regionABase = 0x7D_0000_0000L;
        long regionBBase = 0x7E_0000_0000L;
        long staticsBase = 0x7F_0000_0000L;
        var statics = new byte[64];
        statics[0] = 10;

        var heap = new FakeHeap((regionABase, bufA, true), (regionBBase, bufB, true), (staticsBase, statics, true));
        var spy = new RegionsSpyMem(heap);
        var wrapped = new OffsetRemapMem(spy, staticsBase, staticsBase + 2, staticsBase + 4);
        var display = new Display(meta, kills, wrapped, clock.Func, legends: null, poolPaint: true);

        CardFixtures.TickWithPoolLocate(display, false);   // both regions located+scanned; id 10 gets a kills site in each

        int killsSitesFor10 = 0;
        foreach (var s in display._sites.Snapshot()) if (s.Id == 10 && s.IsKills) killsSitesFor10++;
        Assert.Equal(2, killsSitesFor10);
        int regionsCallsAtCoverage = spy.RegionsCalls;
        Assert.True(regionsCallsAtCoverage > 0);
        int readMark = spy.ReadMark();   // everything before this is setup noise, not this beat's work

        // Corrupt region B's ORIGINAL flavor anchor (-> Mismatch -> evicts on the next verify) and
        // write a fresh, still-discoverable copy of the SAME card further into the SAME buffer --
        // region B's own base/size (hence its Regions() identity) never changes.
        var wrongFlavor = ByteScan.Ascii(new string('Z', "Fletched with regret".Length));
        heap.WriteBytes(regionBBase + flavorB1, wrongFlavor);
        var freshCard = new byte[400];
        var (_, freshSlot, _) = CardFixtures.WriteCardForwardWithName(freshCard, 0, "BowX", "Fletched with regret");
        heap.WriteBytes(regionBBase + 500, freshCard);

        clock.Ms += Display.MaintenanceMs + 1;
        // Plain Tick, not TickWithPoolLocate: coverage is already established (above), and this
        // beat means to isolate ReOfferDrainedRegions' OWN targeted re-offer -- also stepping
        // StepPoolLocate here would additionally trigger PoolLocator's own independent RevalidateMs
        // reverify (same 1000ms cadence as Display.MaintenanceMs, by design -- PoolLocator.Restart.
        // cs's own doc), which reads BOTH cached regions and would confound this test's
        // region-A-untouched assertion below with a second, unrelated read source.
        display.Tick(false);   // one maintenance beat: evict, then re-offer region B alone

        Assert.Equal(Signatures.KillsMeterSlot(7), ReadSlot2(heap, regionABase + slotA, Signatures.KillsMeterSlotChars));
        Assert.Equal(Signatures.KillsMeterSlot(7), ReadSlot2(heap, regionBBase + 500 + freshSlot, Signatures.KillsMeterSlotChars));
        Assert.Equal(regionsCallsAtCoverage, spy.RegionsCalls);   // no full relocate, no Regions() walk

        // THE central cost guarantee this whole arc exists for (round 2 review: RegionsCalls alone
        // cannot detect its removal -- a scan-every-latched-region mutation and a prepended full
        // LocateAll() both leave RegionsCalls untouched, the first because it never calls
        // Regions() at all and the second because AllCachedStillPool() short-circuits through
        // ScanRegion once the cache is warm, Display.PoolPaint.cs's own documented behavior).
        // Reading BY ADDRESS RANGE catches both: region B (drained) must have been re-read this
        // beat; region A (untouched) must NOT have been -- a targeted single-region re-offer reads
        // only the region that actually looks short.
        Assert.True(spy.AnyReadSince(readMark, regionBBase, bufB.Length),
            "the drained region must actually be re-read");
        Assert.False(spy.AnyReadSince(readMark, regionABase, bufA.Length),
            "an untouched region must not be re-read by a TARGETED re-offer");
    }

    /// <summary>Retargeted, round 2 review: the old body (RegionsCalls flat across steady beats)
    /// survived all 18 verifier mutations, including six aimed at it, because it pins something
    /// MaybePoolPaint's own top-of-method aggregate-count short-circuit already guarantees
    /// structurally, and a pre-existing test (DisplayPoolPaintTests.
    /// PoolPaint_steady_state_short_circuits_and_relatches_after_heal) already covers that ground.
    /// It also could not have caught the gap item 1 fixed, because RegionsCalls cannot see a
    /// ScanPoolRegion call at all. Retargeted at the drain check's OWN gate instead, using the
    /// same address-range read tracking test 11 added: a region that never drains must receive
    /// ZERO ReadInto calls from ReOfferDrainedRegions across several maintenance beats, even one
    /// carrying a real count change (non-vacuity: a write really does happen). This is the mutation
    /// RegionsCalls-only coverage could never catch -- an "always re-offer every latched region,
    /// drained or not" bug -- because ScanPoolRegion never touches _mem.Regions() either.</summary>
    [Fact]
    public void Steady_state_covered_beats_never_re_read_the_region_that_never_drained()
    {
        var meta = new Dictionary<int, WeaponMeta>
        {
            { 10, new WeaponMeta { Name = "BowX", Flavor = "Fletched with regret" } },
        };
        var kills = new Dictionary<int, int> { { 10, 7 } };
        var clock = new TestClock();

        var poolBuf = new byte[500];
        CardFixtures.WriteCardForwardWithName(poolBuf, 0, "BowX", "Fletched with regret");

        long poolBase = 0x80_0000_0000L;
        long staticsBase = 0x81_0000_0000L;
        var statics = new byte[64];
        statics[0] = 10;

        var heap = new FakeHeap((poolBase, poolBuf, true), (staticsBase, statics, true));
        var spy = new RegionsSpyMem(heap);
        var wrapped = new OffsetRemapMem(spy, staticsBase, staticsBase + 2, staticsBase + 4);
        var display = new Display(meta, kills, wrapped, clock.Func, legends: null, poolPaint: true);

        CardFixtures.TickWithPoolLocate(display, false);   // establish coverage
        int regionsCallsAtCoverage = spy.RegionsCalls;
        Assert.True(regionsCallsAtCoverage > 0);
        int readMark = spy.ReadMark();   // everything before this is setup noise, not steady state

        int writesBefore = heap.Writes;
        for (int i = 0; i < 6; i++)
        {
            if (i == 3) kills[10] = 12;   // a real change mid-steady-state: proves non-vacuity
            clock.Ms += Display.MaintenanceMs + 1;
            // Plain Tick, not TickWithPoolLocate: same reasoning as
            // Losing_one_copy_of_a_still_covered_weapon_re_offers_only_that_region above --
            // coverage is already established, and stepping StepPoolLocate on this same
            // MaintenanceMs-aligned clock would also trigger PoolLocator's own independent
            // RevalidateMs reverify of the cached region, confounding this test's own
            // never-re-read assertion with an unrelated read source.
            display.Tick(false);
        }

        Assert.True(heap.Writes > writesBefore, "a real count change must still produce a write");
        Assert.Equal(regionsCallsAtCoverage, spy.RegionsCalls);
        Assert.False(spy.AnyReadSince(readMark, poolBase, poolBuf.Length),
            "a region that never drained must never be re-read by the drain check");
    }

    // ─── LW-260: six mutation-survivor pins on the card heartbeat ──────────────

    /// <summary>LW-260 pin (E2): RunMaintenance's own drain/pending work (Display.Heartbeat.cs) is
    /// reached from exactly ONE gated call site per Tick -- MaintenanceDue(now) at Display.cs's
    /// Tick ordering (shared with PaintCountsIfChanged's identical gate). A verifier mutation that
    /// survived the LW-257 suite added a SECOND, unconditional call to that same work at the end
    /// of Tick's body: at ~30 ticks/second that pays a full paint-spot pass (every cached site
    /// re-verified) on EVERY tick instead of once a second. Pinned via a TryReadBytes-counting spy
    /// -- CardSites' own read funnel for anchor/slot verify, distinct from the sweep's ReadInto --
    /// so an idle tick (nothing changed, beat not due) must cost ZERO reads, and only a due beat
    /// may cost anything. RED under the described mutation: every idle tick in the loop below would
    /// ALSO pay the full pass, so the "zero growth" assertion fails on the very first idle tick.</summary>
    [Fact]
    public void Maintenance_beat_work_runs_only_on_the_beat_never_on_idle_ticks()
    {
        var meta = BuildMeta();
        var kills = new Dictionary<int, int> { { 10, 9 } };
        var clock = new TestClock();
        var src = new byte[512];
        CardFixtures.WriteCard(src, 0, "SwordA", "Bright edge of dawn");
        var heap = new FakeHeap((SourceBase, src), (StaticsBase, new byte[16]));
        var spy = new ReadCountSpyMem(heap);
        var wrapped = new OffsetRemapMem(spy, StaticsBase, StaticsBase + 2, StaticsBase + 4);
        var display = new Display(meta, kills, wrapped, clock.Func);

        CardFixtures.DrainGeneration(display, clock, 500);   // populate the site cache, settle counts/targets

        // A definite, controlled beat: jump well past cadence so this call is unambiguously due.
        clock.Ms += Display.MaintenanceMs * 2;
        display.Tick(false);

        // Idle ticks: the clock never moves again, so the shared beat is NOT due on any of these.
        int idleMark = spy.TryReadBytesCalls;
        for (int i = 0; i < 5; i++) display.Tick(false);
        Assert.Equal(idleMark, spy.TryReadBytesCalls);   // zero drain/pending-check work off the beat

        // A second real beat: reads must resume exactly once cadence elapses again.
        clock.Ms += Display.MaintenanceMs + 1;
        int beatMark = spy.TryReadBytesCalls;
        display.Tick(false);
        Assert.True(spy.TryReadBytesCalls > beatMark, "a genuinely due beat must still do real work");
    }

    /// <summary>LW-260 pin (M): the design forbids a separate in-battle maintenance clock --
    /// PaintCountsIfChanged (Engine's ShouldPaintCard-false, in-battle call site) and Tick
    /// (out-of-battle / paintable-card call site) must consult the SAME _lastMaintenanceMs
    /// (MaintenanceDue's own doc: "one shared clock ... two independently-updated timers would let
    /// the beat fire twice as often as intended whenever both call sites run close together"). Pin:
    /// once Tick fires a beat, an immediately following PaintCountsIfChanged call -- crossing from
    /// the out-of-battle to the in-battle call site, no time elapsed -- must NOT fire a second beat.
    /// Proven via a stale cached slot that only a REAL beat would repair: it must stay stale. RED
    /// under a mutation giving PaintCountsIfChanged its own separate last-beat field (never touched
    /// by Tick's own beat): that field would still read its initial sentinel, so the very next
    /// PaintCountsIfChanged call reads as overdue and repaints the slot right back to correct.</summary>
    [Fact]
    public void PaintCountsIfChanged_does_not_re_fire_the_beat_Tick_just_fired()
    {
        var meta = BuildMeta();
        var kills = new Dictionary<int, int> { { 10, 9 } };
        var clock = new TestClock();
        var src = new byte[512];
        var card = CardFixtures.WriteCard(src, 0, "SwordA", "Bright edge of dawn");
        var heap = new FakeHeap((SourceBase, src), (StaticsBase, new byte[16]));
        var display = CardFixtures.MakeDisplay(meta, kills, heap, StaticsBase, clock);

        CardFixtures.DrainGeneration(display, clock, 500);   // populate the site cache, settle counts/targets

        // A definite, controlled beat via Tick (the out-of-battle call site).
        clock.Ms += Display.MaintenanceMs * 2;
        display.Tick(false);

        // Simulate a stale on-screen copy with no count change -- only a genuine second beat would fix it.
        heap.WriteBytes(SourceBase + card.killsSlotPos, ByteScan.Ascii(Signatures.KillsMeterSlot(0)));

        // Cross onto the in-battle call site immediately, same clock: the shared cadence has not elapsed.
        display.PaintCountsIfChanged();

        Assert.Equal(Signatures.KillsMeterSlot(0), ReadSlot(heap, card.killsSlotPos, Signatures.KillsMeterSlotChars));
    }

    /// <summary>LW-260 pin (K): Display.cs's Invalidate() clears _pendingIds (its own doc: _sites is
    /// wiped on the same call, so every watched id's target site cache no longer exists -- without
    /// the clear each one burns all Tuning.CardPendingMaxBeats watching nothing and then logs a
    /// false "gave up" line). RED under a mutation removing that _pendingIds.Clear() call: the id
    /// staged below would still be present after Invalidate.</summary>
    [Fact]
    public void Invalidate_clears_the_pending_watch_list()
    {
        var meta = new Dictionary<int, WeaponMeta>
        {
            { 10, new WeaponMeta { Name = "SwordA", Flavor = "Bright edge of dawn" } },
        };
        var kills = new Dictionary<int, int> { { 10, 5 } };
        var clock = new TestClock();
        var heap = new FakeHeap((StaticsBase, new byte[16]));   // no card anywhere: id 10 stages pending, never settles
        var display = CardFixtures.MakeDisplay(meta, kills, heap, StaticsBase, clock);

        kills[10] = 8;
        display.PaintCountsIfChanged();
        Assert.True(display._pendingIds.ContainsKey(10), "setup must actually stage a pending id");

        display.Invalidate();

        Assert.Empty(display._pendingIds);
    }

    /// <summary>LW-260 pin (F): ReOfferDrainedRegions' targeted re-scan (Display.PoolDrain.cs) heals
    /// a drained region via the EXISTING ScanPoolRegion -- it must never touch PoolLocator's own
    /// cache. Throwing that cache away on every heal would force the NEXT locate to re-walk the
    /// whole process heap, reintroducing the stall this arc's own resumable rewrite (LW-261) exists
    /// to avoid, one step removed. RED under a mutation invalidating/clearing PoolLocator's cache at
    /// the end of a SUCCESSFUL re-offer (CoversAllMeta() true, the re-latch branch): RegionsStale
    /// would flip true and the next real scan driver would treat a restart as queued.</summary>
    [Fact]
    public void Successful_targeted_re_offer_leaves_the_located_region_cache_intact()
    {
        var meta = new Dictionary<int, WeaponMeta>
        {
            { 10, new WeaponMeta { Name = "BowX", Flavor = "Fletched with regret" } },
        };
        var kills = new Dictionary<int, int> { { 10, 7 } };
        var clock = new TestClock();

        var bufA = new byte[600];
        CardFixtures.WriteCardForwardWithName(bufA, 0, "BowX", "Fletched with regret");

        var bufB = new byte[900];
        var (_, killsSlotB1, flavorB1) = CardFixtures.WriteCardForwardWithName(bufB, 0, "BowX", "Fletched with regret");

        long regionABase = 0x8C_0000_0000L;
        long regionBBase = 0x8D_0000_0000L;
        long staticsBase = 0x8E_0000_0000L;
        var statics = new byte[64];
        statics[0] = 10;

        var heap = new FakeHeap((regionABase, bufA, true), (regionBBase, bufB, true), (staticsBase, statics, true));
        var display = CardFixtures.MakeDisplay(meta, kills, heap, staticsBase, clock, poolPaint: true);

        CardFixtures.TickWithPoolLocate(display, false);   // both regions located+scanned; id 10 gets a kills site in each

        int regionsBeforeBeat = display._poolLocator.CachedRegions.Count;
        int generationBeforeBeat = display._poolLocator.PublishGeneration;
        Assert.Equal(2, regionsBeforeBeat);
        Assert.False(display._poolLocator.RegionsStale, "coverage just latched -- must not already read stale");

        // Corrupt region B's ORIGINAL flavor anchor (-> Mismatch -> evicts on the next verify) and
        // write a fresh, still-discoverable copy of the SAME card further into the SAME buffer --
        // region B's own base/size (hence PoolLocator's identity for it) never changes. Mirrors
        // Losing_one_copy_of_a_still_covered_weapon_re_offers_only_that_region's own recipe. ALSO
        // destroy the original "Kills: " literal itself (not just the flavor): the fresh card's
        // flavor at offset 500 sits well within CardScanner.FlavorWindow (2048) of the stale
        // "Kills: " hit near offset 0 in this same 900-byte buffer, so leaving that literal intact
        // would let the re-scan bidirectionally re-attach the stale slot to the fresh flavor --
        // a phantom THIRD site that is a fixture hazard, not a production bug, and would make this
        // test's own site-count assertion below dishonest.
        var wrongFlavor = ByteScan.Ascii(new string('Z', "Fletched with regret".Length));
        heap.WriteBytes(regionBBase + flavorB1, wrongFlavor);
        heap.WriteBytes(regionBBase + killsSlotB1 - ByteScan.Ascii("Kills: ").Length,
            ByteScan.Ascii("XXXXXXX"));
        var freshCard = new byte[400];
        CardFixtures.WriteCardForwardWithName(freshCard, 0, "BowX", "Fletched with regret");
        heap.WriteBytes(regionBBase + 500, freshCard);

        clock.Ms += Display.MaintenanceMs + 1;
        display.Tick(false);   // one maintenance beat: evict, targeted re-offer heals region B, re-latch (success path)

        // Non-vacuity: the re-offer must genuinely have found the fresh copy and stayed covered --
        // the SUCCESS path (CoversAllMeta() true), not the "whole region genuinely gone" one.
        int killsSitesFor10 = 0;
        foreach (var s in display._sites.Snapshot()) if (s.Id == 10 && s.IsKills) killsSitesFor10++;
        Assert.Equal(2, killsSitesFor10);

        Assert.Equal(regionsBeforeBeat, display._poolLocator.CachedRegions.Count);
        Assert.Equal(generationBeforeBeat, display._poolLocator.PublishGeneration);
        Assert.False(display._poolLocator.RegionsStale,
            "a successful targeted re-offer must not mark the located region cache stale");
    }

    /// <summary>LW-260 pin (guard site 1 of 2, counts half): Display.cs's Tick() gates the shared
    /// beat on `MaintenanceDue(...) && !countsChanged && !targetsChanged` (~line 251) so a tick
    /// that ALREADY painted through the count/target-change branch never pays RunMaintenance's own
    /// full paint pass a second time -- doubling every guarded read a beat costs across a real
    /// cache (1400-2000 sites live). Pinned via a TryReadBytes-counting spy against a directly-
    /// measured single-pass reference, so the pin needs no hardcoded read count. RED under a
    /// mutation dropping `!countsChanged` from that condition: the maintenance branch then ALSO
    /// fires this same tick, doubling the reads. Its sibling below
    /// (Tick_pays_exactly_one_paint_pass_when_targets_changed_and_maintenance_is_due) pins the
    /// `!targetsChanged` half of this same guard separately: a counts-changed tick never exercises
    /// that half (targetsChanged stays false throughout this fixture), so a mutation dropping ONLY
    /// `!targetsChanged` leaves THIS test green -- confirmed by driving that exact mutation
    /// (Display.cs's ~line 251 guard reduced to `MaintenanceDue(...) && !countsChanged`) against
    /// this test alone, which still passes.</summary>
    [Fact]
    public void Tick_pays_exactly_one_paint_pass_when_counts_changed_and_maintenance_is_due()
    {
        var meta = BuildMeta();
        var kills = new Dictionary<int, int> { { 10, 9 } };
        var clock = new TestClock();
        var src = new byte[512];
        CardFixtures.WriteCard(src, 0, "SwordA", "Bright edge of dawn");
        var heap = new FakeHeap((SourceBase, src), (StaticsBase, new byte[16]));
        var spy = new ReadCountSpyMem(heap);
        var wrapped = new OffsetRemapMem(spy, StaticsBase, StaticsBase + 2, StaticsBase + 4);
        var display = new Display(meta, kills, wrapped, clock.Func);

        CardFixtures.DrainGeneration(display, clock, 500);   // populate the site cache, settle counts/targets

        // Cross the beat's due threshold AND change a tracked count on the SAME tick.
        kills[10] = 12;
        clock.Ms += Display.MaintenanceMs + 1;
        int mark = spy.TryReadBytesCalls;
        display.Tick(false);
        int actualReads = spy.TryReadBytesCalls - mark;

        // Reference: exactly what ONE full PaintAll pass over the (now-settled) cache costs,
        // measured directly against the same cache in its current (already-repainted) state.
        int refMark = spy.TryReadBytesCalls;
        display._sites.PaintAll(display.KillsFor);
        int onePassReads = spy.TryReadBytesCalls - refMark;

        Assert.True(onePassReads > 0, "premise: a real pass over a non-empty cache must read something");
        Assert.Equal(onePassReads, actualReads);
    }

    /// <summary>LW-260 pin (guard site 1 of 2, targets half): the same ~line 251 guard as the
    /// sibling above, but isolating the `!targetsChanged` term instead -- a tick where the MIRROR
    /// statics change (BuildTargets, driven by Offsets.MirrorWeapon/MirrorOffHand, not the kills
    /// dictionary) while no kill count changes must still pay RunMaintenance's paint pass exactly
    /// once, not twice. This is the mutation the counts sibling above cannot catch: dropping ONLY
    /// `!targetsChanged` from the guard leaves that sibling green (it never drives a targets
    /// change), so a targets-changed, maintenance-due tick would silently double-pay the paint
    /// pass with no test noticing. RED under that same mutation (guard reduced to
    /// `MaintenanceDue(...) && !countsChanged`): the maintenance branch fires a second time this
    /// tick, doubling the reads.</summary>
    [Fact]
    public void Tick_pays_exactly_one_paint_pass_when_targets_changed_and_maintenance_is_due()
    {
        var meta = BuildMeta();
        var kills = new Dictionary<int, int> { { 10, 9 } };
        var clock = new TestClock();
        var src = new byte[512];
        CardFixtures.WriteCard(src, 0, "SwordA", "Bright edge of dawn");
        var heap = new FakeHeap((SourceBase, src), (StaticsBase, new byte[16]));
        var spy = new ReadCountSpyMem(heap);
        var wrapped = new OffsetRemapMem(spy, StaticsBase, StaticsBase + 2, StaticsBase + 4);
        var display = new Display(meta, kills, wrapped, clock.Func);

        CardFixtures.DrainGeneration(display, clock, 500);   // populate the site cache, settle counts/targets

        // Cross the beat's due threshold AND flip the MIRROR-WEAPON static (targetsChanged) on the
        // SAME tick -- kills[10] is left untouched throughout so countsChanged stays false; only
        // the target set (BuildTargets, via the remapped MirrorWeapon address) changes.
        heap.WriteBytes(StaticsBase, new byte[] { 10, 0 });   // u16 mirror-weapon id 10, little-endian
        clock.Ms += Display.MaintenanceMs + 1;
        int mark = spy.TryReadBytesCalls;
        display.Tick(false);
        int actualReads = spy.TryReadBytesCalls - mark;

        // Reference: exactly what ONE full PaintAll pass over the (now-settled) cache costs,
        // measured directly against the same cache in its current (already-repainted) state.
        int refMark = spy.TryReadBytesCalls;
        display._sites.PaintAll(display.KillsFor);
        int onePassReads = spy.TryReadBytesCalls - refMark;

        Assert.True(onePassReads > 0, "premise: a real pass over a non-empty cache must read something");
        Assert.Equal(onePassReads, actualReads);
    }

    /// <summary>LW-260 pin (guard site 2 of 2): Display.cs's PaintCountsIfChanged() gates the shared
    /// beat on `MaintenanceDue(...) && !countsChanged` (~line 209) -- the same double-paint hazard
    /// as the Tick() guard above, on the in-battle call site. RED under a mutation dropping
    /// `!countsChanged` from that condition.</summary>
    [Fact]
    public void PaintCountsIfChanged_pays_exactly_one_paint_pass_when_counts_changed_and_maintenance_is_due()
    {
        var meta = BuildMeta();
        var kills = new Dictionary<int, int> { { 10, 9 } };
        var clock = new TestClock();
        var src = new byte[512];
        CardFixtures.WriteCard(src, 0, "SwordA", "Bright edge of dawn");
        var heap = new FakeHeap((SourceBase, src), (StaticsBase, new byte[16]));
        var spy = new ReadCountSpyMem(heap);
        var wrapped = new OffsetRemapMem(spy, StaticsBase, StaticsBase + 2, StaticsBase + 4);
        var display = new Display(meta, kills, wrapped, clock.Func);

        CardFixtures.DrainGeneration(display, clock, 500);   // populate the site cache, settle counts/targets

        kills[10] = 12;
        clock.Ms += Display.MaintenanceMs + 1;
        int mark = spy.TryReadBytesCalls;
        display.PaintCountsIfChanged();
        int actualReads = spy.TryReadBytesCalls - mark;

        int refMark = spy.TryReadBytesCalls;
        display._sites.PaintAll(display.KillsFor);
        int onePassReads = spy.TryReadBytesCalls - refMark;

        Assert.True(onePassReads > 0, "premise: a real pass over a non-empty cache must read something");
        Assert.Equal(onePassReads, actualReads);
    }

    /// <summary>LW-260 pin (end bound): Display.PoolDrain.cs's CountKillsSitesIn treats a region as
    /// [rbase, rbase+rsize) -- a site sitting exactly AT rbase+rsize belongs to whatever comes next
    /// in memory, not this region. RED under a mutation changing the end comparison from `&lt;` to
    /// `&lt;=`: the on-the-bound site below would then count, flipping the result from 1 to 2.</summary>
    [Fact]
    public void CountKillsSitesIn_excludes_a_site_sitting_exactly_at_the_region_end_bound()
    {
        const long rbase = 0x90_0000_0000L;
        const long rsize = 128L;
        var snapshot = new List<CardSites.Site>
        {
            new CardSites.Site(10, 1, rbase + rsize,     rbase, IsKills: true),   // exactly on the bound: OUTSIDE
            new CardSites.Site(10, 1, rbase + rsize - 1,  rbase, IsKills: true),  // last in-bound byte: INSIDE
        };

        int count = Display.CountKillsSitesIn(snapshot, rbase, rsize);

        Assert.Equal(1, count);
    }

    /// <summary>IGameMemory wrapper that counts Regions() calls AND records every ReadInto range,
    /// forwarding everything else -- extends DisplayPoolPaintTests.cs's private RegionsSpyMem of
    /// the same name (this file's own no-cross-file-reference convention for test-only fakes) with
    /// the read-range tracking round-2 review asked for: RegionsCalls alone cannot tell a targeted
    /// single-region ScanPoolRegion apart from a full LocateAll's AllCachedStillPool() re-verify,
    /// because ScanPoolRegion (like AllCachedStillPool's own ScanRegion) reads via ChunkReader's
    /// ReadInto, never via _mem.Regions() -- CardSites' own per-site verify/paint reads go through
    /// TryReadBytes instead, so they never pollute this list either; only chunk-scanning shows up
    /// here.</summary>
    private sealed class RegionsSpyMem : IGameMemory
    {
        private readonly IGameMemory _inner;
        private readonly List<(long addr, int len)> _reads = new();
        public int RegionsCalls { get; private set; }
        public RegionsSpyMem(IGameMemory inner) => _inner = inner;

        public byte U8(long addr) => _inner.U8(addr);
        public ushort U16(long addr) => _inner.U16(addr);
        public bool TryReadBytes(long addr, int len, out byte[] buf) => _inner.TryReadBytes(addr, len, out buf);
        public int ReadInto(long addr, byte[] buf, int len)
        {
            int got = _inner.ReadInto(addr, buf, len);
            if (got > 0) _reads.Add((addr, got));
            return got;
        }
        public void WriteBytes(long addr, byte[] data) => _inner.WriteBytes(addr, data);
        public void W8(long addr, byte value) => _inner.W8(addr, value);
        public bool Readable(long addr, int len) => _inner.Readable(addr, len);
        public bool Writable(long addr, int len) => _inner.Writable(addr, len);
        public IEnumerable<(long baseAddr, long size)> Regions()
        {
            RegionsCalls++;
            return _inner.Regions();
        }

        /// <summary>A marker for "reads from here forward" queries below.</summary>
        public int ReadMark() => _reads.Count;

        /// <summary>True if any ReadInto call recorded since <paramref name="mark"/> overlaps
        /// [rangeStart, rangeStart+rangeSize).</summary>
        public bool AnyReadSince(int mark, long rangeStart, long rangeSize)
        {
            long rangeEnd = rangeStart + rangeSize;
            for (int i = mark; i < _reads.Count; i++)
            {
                var (addr, len) = _reads[i];
                if (addr < rangeEnd && addr + len > rangeStart) return true;
            }
            return false;
        }
    }

    /// <summary>IGameMemory wrapper that counts TryReadBytes calls -- CardSites' own read funnel
    /// for anchor/slot verify (CardSites.cs's PaintSiteWithResult/VerifyAnchor), distinct from the
    /// sweep/pool-scan's ReadInto funnel (ChunkReader, RegionsSpyMem above), so this isolates a
    /// paint PASS's own cost from background scanning. Mirrors this file's own RegionsSpyMem (no
    /// cross-file reference for test-only fakes).</summary>
    private sealed class ReadCountSpyMem : IGameMemory
    {
        private readonly IGameMemory _inner;
        public int TryReadBytesCalls { get; private set; }
        public ReadCountSpyMem(IGameMemory inner) => _inner = inner;

        public byte U8(long addr) => _inner.U8(addr);
        public ushort U16(long addr) => _inner.U16(addr);
        public bool TryReadBytes(long addr, int len, out byte[] buf)
        {
            TryReadBytesCalls++;
            return _inner.TryReadBytes(addr, len, out buf);
        }
        public int ReadInto(long addr, byte[] buf, int len) => _inner.ReadInto(addr, buf, len);
        public void WriteBytes(long addr, byte[] data) => _inner.WriteBytes(addr, data);
        public void W8(long addr, byte value) => _inner.W8(addr, value);
        public bool Readable(long addr, int len) => _inner.Readable(addr, len);
        public bool Writable(long addr, int len) => _inner.Writable(addr, len);
        public IEnumerable<(long baseAddr, long size)> Regions() => _inner.Regions();
    }
}
