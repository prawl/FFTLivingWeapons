using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// Direct tests for the hot-chunk lifetime policy: LRU cap eviction, TTL expiry, the
/// pass cadence (clock + rescan request, including the consume-while-empty path), the
/// empty-set countdown reset on Mark, and the rotation cursor. The same behaviors are
/// pinned end-to-end (through DisplaySweep.Tick) by DisplaySweepHotTests and
/// DisplaySweepHotBudgetTests.
/// </summary>
public class HotChunkSetTests
{
    // ─── pass cadence ─────────────────────────────────────────────────────────

    [Fact]
    public void Pass_overdue_at_time_zero_with_marked_chunk()
    {
        // _lastPassMs starts at a far-back sentinel, but Mark on an empty set resets the
        // countdown -- so the first pass comes HotRescanMs after the first Mark.
        var hot = new HotChunkSet();
        hot.Mark(0x1000, now: 0);

        Assert.False(hot.TryBeginPass(HotChunkSet.HotRescanMs - 1));
        Assert.True(hot.TryBeginPass(HotChunkSet.HotRescanMs + 1));
    }

    [Fact]
    public void Pass_not_due_again_until_rescan_interval_elapses()
    {
        var hot = new HotChunkSet();
        hot.Mark(0x1000, now: 0);

        long t0 = HotChunkSet.HotRescanMs + 1;
        Assert.True(hot.TryBeginPass(t0));
        Assert.False(hot.TryBeginPass(t0 + HotChunkSet.HotRescanMs - 1));
        Assert.True(hot.TryBeginPass(t0 + HotChunkSet.HotRescanMs + 1));
    }

    [Fact]
    public void Rescan_request_forces_pass_before_clock()
    {
        var hot = new HotChunkSet();
        hot.Mark(0x1000, now: 0);

        hot.RequestRescan();
        Assert.True(hot.TryBeginPass(1));
        // Request was consumed: the next attempt falls back to the clock.
        Assert.False(hot.TryBeginPass(2));
    }

    [Fact]
    public void Rescan_request_consumed_even_when_set_is_empty()
    {
        // A queued rescan against an empty set must not fire spuriously after a later Mark.
        var hot = new HotChunkSet();
        hot.RequestRescan();

        Assert.False(hot.TryBeginPass(0));   // empty: no pass, request consumed

        hot.Mark(0x1000, now: 1);
        Assert.False(hot.TryBeginPass(2),
            "consumed request must not force a pass; the countdown restarts from the Mark");
    }

    [Fact]
    public void Empty_set_never_begins_a_pass()
    {
        var hot = new HotChunkSet();
        Assert.False(hot.TryBeginPass(HotChunkSet.HotRescanMs * 10));
    }

    [Fact]
    public void Mark_on_empty_set_resets_countdown_but_remark_does_not()
    {
        var hot = new HotChunkSet();
        hot.Mark(0x1000, now: 0);
        long t0 = HotChunkSet.HotRescanMs + 1;
        Assert.True(hot.TryBeginPass(t0));

        // Re-mark on a NON-empty set must not reset the countdown.
        hot.Mark(0x1000, now: t0 + HotChunkSet.HotRescanMs - 10);
        Assert.True(hot.TryBeginPass(t0 + HotChunkSet.HotRescanMs + 1));
    }

    // ─── LRU cap eviction ─────────────────────────────────────────────────────

    [Fact]
    public void Cap_overflow_evicts_oldest_marked_chunk()
    {
        var hot = new HotChunkSet();
        for (int i = 0; i <= HotChunkSet.MaxHotChunks; i++)
            hot.Mark(0x1000 + i * 0x1000, now: i);

        Assert.Equal(HotChunkSet.MaxHotChunks, hot.Count);
        Assert.DoesNotContain(0x1000L, hot.SortedSnapshot());                  // oldest evicted
        Assert.Contains(0x1000L + HotChunkSet.MaxHotChunks * 0x1000, hot.SortedSnapshot());
    }

    [Fact]
    public void Remark_refreshes_timestamp_so_chunk_survives_overflow()
    {
        var hot = new HotChunkSet();
        for (int i = 0; i < HotChunkSet.MaxHotChunks; i++)
            hot.Mark(0x1000 + i * 0x1000, now: i);

        hot.Mark(0x1000, now: 1000);                       // refresh the oldest
        hot.Mark(0x9999_0000, now: 1001);                  // overflow: evicts the NOW-oldest

        Assert.Contains(0x1000L, hot.SortedSnapshot());    // refreshed chunk survives
        Assert.DoesNotContain(0x2000L, hot.SortedSnapshot());
    }

    // ─── TTL expiry ───────────────────────────────────────────────────────────

    [Fact]
    public void EvictExpired_drops_chunks_older_than_ttl_only()
    {
        var hot = new HotChunkSet();
        hot.Mark(0x1000, now: 0);
        hot.Mark(0x2000, now: 100);

        // Strict ">": a chunk exactly HotTtlMs old survives.
        hot.EvictExpired(HotChunkSet.HotTtlMs);
        Assert.Equal(2, hot.Count);

        hot.EvictExpired(HotChunkSet.HotTtlMs + 1);
        Assert.Equal(1, hot.Count);
        Assert.Equal(new List<long> { 0x2000 }, hot.SortedSnapshot());
    }

    // ─── rotation cursor ──────────────────────────────────────────────────────

    [Fact]
    public void Cursor_resumes_after_last_processed_chunk_and_wraps()
    {
        var hot = new HotChunkSet();
        hot.Mark(0x3000, now: 0);
        hot.Mark(0x1000, now: 0);
        hot.Mark(0x2000, now: 0);

        var list = hot.SortedSnapshot();
        Assert.Equal(new List<long> { 0x1000, 0x2000, 0x3000 }, list);  // ascending order
        Assert.Equal(0, hot.Cursor);

        hot.NoteProcessed(1, list.Count);   // processed index 1 -> resume at 2
        Assert.Equal(2, hot.Cursor);

        hot.NoteProcessed(2, list.Count);   // processed the last -> wrap to 0
        Assert.Equal(0, hot.Cursor);
    }

    [Fact]
    public void Cursor_beyond_count_is_wrapped_by_snapshot()
    {
        var hot = new HotChunkSet();
        hot.Mark(0x1000, now: 0);
        hot.Mark(0x2000, now: 0);
        hot.NoteProcessed(1, 2);            // cursor = 0... advance to make it 1
        hot.NoteProcessed(0, 2);            // cursor = 1
        hot.Remove(0x2000);                 // shrink below the cursor

        hot.SortedSnapshot();
        Assert.Equal(0, hot.Cursor);        // wrapped back into range
    }
}
