using System;
using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-261: PoolScan, the resumable byte-budgeted walk PoolLocator drives (PoolLocator.cs's own
/// class doc explains why this replaced the old synchronous full-process scan -- a live
/// measurement clocked it at 7 to 10 seconds on the mod's single 33ms background loop). These two
/// tests pin the walk's own budget/termination contract in isolation, before PoolLocatorStepTests
/// exercises the publish/coalescing behavior built on top of it.
/// </summary>
public class PoolScanTests
{
    private static Dictionary<int, WeaponMeta> BuildMeta() => new()
    {
        { 1, new WeaponMeta { Name = "Sword", Flavor = "A sharp blade" } },
    };

    private static byte[] BuildPoolBuffer()
    {
        var buf = new byte[500];
        CardFixtures.WriteCardForwardWithName(buf, 0, "Sword", "A sharp blade");
        return buf;
    }

    /// <summary>THE load-bearing test. The fixture region (12MB) is deliberately LARGER than the
    /// budget (4MB): a per-region budgeter (one that reads a whole region once it starts, rather
    /// than one chunk at a time) would pass a same-size-as-budget fixture by accident. The total
    /// heap (12MB) is also larger than budget+one chunk (8MB), so a Step that read past its
    /// budget by more than the one-chunk-of-forward-progress allowance would show up in the byte
    /// total. Non-vacuity, confirmed by sabotage (neutralizing the `!firstChunk &amp;&amp;
    /// remaining &lt;= 0` early return so it can never fire, since deleting the check outright
    /// does not compile -- `firstChunk` and `InProgress()` would go unused): the region then
    /// finishes in one Step, so `Assert.False(result.Complete, ...)` is the one that actually
    /// fails first (xUnit stops at the first failing assertion; the byte-bound check below it
    /// never runs that pass) -- but the byte total climbs to ~12MB regardless, well past the 8MB
    /// bound, so either assertion alone would already catch this mutation.</summary>
    [Fact]
    public void Step_never_reads_more_than_budget_plus_one_chunk()
    {
        var heap = DisplaySweepFixtures.OneRegion(0x10_0000_0000L, 12 * 1024 * 1024);
        var spy = new BytesSpyMem(heap);
        var pats = new CardPatterns(BuildMeta());
        var scan = new PoolScan(spy, pats);
        scan.Begin(0);

        var result = scan.Step(PoolLocator.LocateBudgetInBattle, 0);

        Assert.False(result.Complete, "a 12MB region cannot finish in one 4MB-budget Step");
        long bound = PoolLocator.LocateBudgetInBattle + DisplaySweep.ChunkSize;
        Assert.True(spy.TotalBytesRead <= bound,
            $"Step read {spy.TotalBytesRead} bytes, more than budget+one chunk ({bound})");
        // LW-265: a double-spending Step (two chunks instead of one) trips the byte bound above
        // only via the ~12KB Lookback/TrailSlack read overhead paid twice -- a slack-margin
        // accident, not a designed catch. Only the chunk COUNT pins one slice versus two by
        // design, the same discriminator DisplayPoolLocateBudgetTests' battle-aware budget test
        // uses.
        Assert.Equal(1, spy.ChunkReads);
    }

    /// <summary>C2(a) (round 3 verify): a region that GROWS IN PLACE while the cursor is still
    /// resuming it (not a newly committed region) must have its grown extent covered -- the exact
    /// live shape this arc's own measurement showed (one region: 16.8MB, then 29.6MB, then
    /// 29.7MB, all mid-battle). The marker sits ONLY in the grown tail (past the region's
    /// original 8MB), reachable only on a THIRD Step call, so a scan that only ever remembers the
    /// region's size as of when it first touched it (rather than re-reading it fresh every
    /// SnapshotRefreshMs-due call, as Step's own cadence-gated Regions() snapshot does) would stop
    /// after the original extent and never stage this region at all. Retune round (R2): every
    /// `now` value here is spaced past PoolScan.SnapshotRefreshMs from the one before it, so each
    /// Step call is actually due for a fresh snapshot -- without that spacing this hazard is
    /// invisible by construction (the cadence gate would simply reuse the stale pre-growth
    /// snapshot the whole way through, passing for the wrong reason).</summary>
    [Fact]
    public void Step_covers_a_region_that_grows_in_place_while_being_resumed()
    {
        var pats = new CardPatterns(BuildMeta());
        long regionBase = 0x92_0000_0000L;
        var initial = new byte[8 * 1024 * 1024];   // > one 4MB chunk: still mid-region after Step 1
        var heap = new FakeHeap((regionBase, initial, true));
        var scan = new PoolScan(heap, pats);
        scan.Begin(0);

        long now = 0;
        var r1 = scan.Step(PoolLocator.LocateBudgetInBattle, now);
        Assert.False(r1.Complete);

        // Grow the SAME region (same base) to 12MB, with the only pool-shaped marker in the
        // newly-grown tail (past the original 8MB), only reachable via the region's 3rd chunk.
        var grown = new byte[12 * 1024 * 1024];
        Array.Copy(initial, grown, initial.Length);
        var marker = BuildPoolBuffer();
        Array.Copy(marker, 0, grown, 9 * 1024 * 1024, marker.Length);
        heap.RemoveRegion(regionBase);
        heap.AddRegion(regionBase, grown, writable: true);

        PoolScan.StepResult result = r1;
        for (int i = 0; i < 10 && !result.Complete; i++)
        {
            now += PoolScan.SnapshotRefreshMs + 1;   // past the cadence every call: each one is due for a fresh snapshot
            result = scan.Step(PoolLocator.LocateBudgetInBattle, now);
        }

        Assert.True(result.Complete, "scan did not complete within the bound");
        Assert.Contains(result.Regions, r => r.baseAddr == regionBase && r.size >= 12 * 1024 * 1024);
    }

    /// <summary>C2(b) (round 3 verify): PoolScan.Begin must discard any staging left over from an
    /// earlier, never-completed scan (the shape a restart -- PoolLocator's own Invalidate --
    /// produces: a scan aborted mid-flight in favor of a fresh one). Region A qualifies and gets
    /// internally staged during the FIRST (aborted) scan; the SAME PoolScan instance is then
    /// re-Begin()'d against a heap where A no longer exists and a different region B does. The
    /// completed second scan must report ONLY B.</summary>
    [Fact]
    public void Begin_discards_staging_left_over_from_an_earlier_incomplete_scan()
    {
        var pats = new CardPatterns(BuildMeta());
        long regionABase = 0x93_0000_0000L;
        long fillerBase = 0x94_0000_0000L;
        var filler = new byte[12 * 1024 * 1024];   // forces the first scan to pause after staging A
        var heap = new FakeHeap((regionABase, BuildPoolBuffer(), true), (fillerBase, filler, true));
        var scan = new PoolScan(heap, pats);
        scan.Begin(0);

        var r1 = scan.Step(PoolLocator.LocateBudgetInBattle, 0);   // stages region A, then starts on filler
        Assert.False(r1.Complete);

        // The restart: region A and the filler are gone; a different region B takes their place.
        heap.RemoveRegion(regionABase);
        heap.RemoveRegion(fillerBase);
        long regionBBase = 0x95_0000_0000L;
        heap.AddRegion(regionBBase, BuildPoolBuffer(), writable: true);

        scan.Begin(1);
        var result = scan.Step(PoolLocator.LocateBudgetInBattle, 1);   // tiny fixture: completes in one call

        Assert.True(result.Complete);
        Assert.Single(result.Regions);
        Assert.Equal(regionBBase, result.Regions[0].baseAddr);
    }

    /// <summary>An explicit termination BOUND, not just "eventually completes": catches a
    /// cursor-reset-every-tick bug that test 1 alone would still pass (each individual Step call
    /// would still respect its own byte budget even if it never advanced the walk).</summary>
    [Fact]
    public void Step_completes_within_a_bounded_tick_count()
    {
        int regionSize = 12 * 1024 * 1024;
        var heap = DisplaySweepFixtures.OneRegion(0x11_0000_0000L, regionSize);
        var pats = new CardPatterns(BuildMeta());
        var scan = new PoolScan(heap, pats);
        scan.Begin(0);

        int bound = regionSize / (int)PoolLocator.LocateBudgetInBattle + 5;
        bool complete = false;
        for (int i = 0; i < bound && !complete; i++)
        {
            var result = scan.Step(PoolLocator.LocateBudgetInBattle, i);
            complete = result.Complete;
        }

        Assert.True(complete, $"scan did not complete within {bound} Step calls");
    }

    /// <summary>R2 (retune round): a region's Regions() snapshot is retaken only once
    /// SnapshotRefreshMs has elapsed since the last one -- not on every Step call. The fixture has
    /// TWO regions so a single Step call cannot complete the whole scan (forcing several calls
    /// within the cadence window), and the spy counts Regions() calls directly, mirroring the
    /// Regions-counting spy pattern already used across PoolLocatorTests.cs/PoolLocatorStepTests.
    /// cs (copied here, not referenced, per this suite's own no-cross-file-reference convention
    /// for test-only fakes).</summary>
    [Fact]
    public void Snapshot_is_not_retaken_within_the_refresh_cadence()
    {
        var pats = new CardPatterns(BuildMeta());
        var fillerA = new byte[12 * 1024 * 1024];
        var fillerB = new byte[12 * 1024 * 1024];
        long baseA = 0xA0_0000_0000L;
        long baseB = 0xA1_0000_0000L;
        var heap = new FakeHeap((baseA, fillerA, true), (baseB, fillerB, true));
        var spy = new RegionsSpyMem(heap);
        var scan = new PoolScan(spy, pats);
        scan.Begin(0);   // Begin's own unconditional snapshot: RegionsCalls == 1 after this

        int callsAfterBegin = spy.RegionsCalls;
        Assert.Equal(1, callsAfterBegin);

        long now = 0;
        PoolScan.StepResult result = default;
        for (int i = 0; i < 5; i++)
        {
            now += 100;   // well within SnapshotRefreshMs of Begin's own snapshot
            result = scan.Step(PoolLocator.LocateBudgetInBattle, now);
        }

        Assert.False(result.Complete, "fixture sanity: two 12MB regions cannot finish in five 4MB-budget Steps");
        Assert.Equal(callsAfterBegin, spy.RegionsCalls);

        now += PoolScan.SnapshotRefreshMs + 1;
        scan.Step(PoolLocator.LocateBudgetInBattle, now);

        Assert.Equal(callsAfterBegin + 1, spy.RegionsCalls);
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
