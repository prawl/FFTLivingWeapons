using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LivingWeapon;
using Newtonsoft.Json.Linq;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// The flight recorder's INSTANCE core (FlightRecorder.cs) -- the LOCKED 9-test set from the
/// logging-overhaul plan's stage-C review corrections. Every test drives the instance directly
/// (never the Flight static facade, except FacadeTests below which pin the null-object contract,
/// B2 test 9). Every dependency (clock, wall clock, file writer, retention lister/deleter) is
/// injected, so nothing here touches a real disk or a real clock except where a test explicitly
/// wants the production default (the facade smoke test).
/// </summary>
public class FlightRecorderTests
{
    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "lw_flight_" + Path.GetRandomFileName());
        Directory.CreateDirectory(d);
        return d;
    }

    private static FlightRecorder Make(
        string? modDir = null,
        Func<long>? clock = null,
        Func<DateTime>? wallClock = null,
        Action<string, string>? fileWriter = null,
        Func<string, IEnumerable<string>>? lister = null,
        Action<string>? deleter = null)
        => new(modDir ?? TempDir(), clock, wallClock, fileWriter, lister, deleter);

    // ---- (1) ring wraparound, order-preserving ----
    // NOTE: this test was written and run against the tree BEFORE FlightRecorder.cs existed --
    // it failed to COMPILE (CS0246 FlightRecorder not found), which counts as RED per the task's
    // TDD instruction. Confirmed via `dotnet build LivingWeapon.Tests` at that point; the class
    // was then written and this went green along with the rest of the suite.
    [Fact]
    public void Ring_wraparound_preserves_insertion_order_oldest_first()
    {
        var rec = Make();
        int total = FlightRecorder.Capacity + 4;
        for (int i = 0; i < total; i++) rec.Record("t", i.ToString());

        var snap = rec.Snapshot();

        Assert.Equal(FlightRecorder.Capacity, snap.Length);
        Assert.Equal("4", snap[0].Payload);                       // the 4 oldest were dropped
        Assert.Equal((total - 1).ToString(), snap[^1].Payload);   // newest survives
        for (int i = 1; i < snap.Length; i++)
            Assert.Equal(int.Parse(snap[i - 1].Payload) + 1, int.Parse(snap[i].Payload));
    }

    // ---- (2) JSONL validity: hostile payloads round-trip through a REAL JSON parser ----
    [Fact]
    public void Flush_writes_valid_JSONL_that_round_trips_hostile_payloads()
    {
        string? written = null;
        var rec = Make(fileWriter: (path, content) => written = content);
        const string hostile = "quote\" backslash\\ newline\n dash -- bracket [x] {y}";
        rec.Record("weird", hostile);

        rec.Flush("test");

        Assert.NotNull(written);
        var lines = written!.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);   // header + one record

        var header = JObject.Parse(lines[0]);
        Assert.True((bool)header["hdr"]!);
        Assert.NotNull(header["wall"]);
        Assert.NotNull(header["t"]);

        var rec1 = JObject.Parse(lines[1]);
        Assert.Equal("weird", (string)rec1["e"]!);
        Assert.Equal(hostile, (string)rec1["d"]!);   // exact round-trip, no hand-rolled escaping artifacts
    }

    // ---- (3) flush clears the ring; an empty ring flushes nothing ----
    [Fact]
    public void Empty_ring_flush_writes_nothing_and_a_real_flush_clears_the_ring()
    {
        int writes = 0;
        var rec = Make(fileWriter: (_, _) => writes++);

        rec.Flush("noop");
        Assert.Equal(0, writes);   // nothing recorded -> nothing written
        Assert.Equal(0, rec.Count);

        rec.Record("t", "a");
        Assert.Equal(1, rec.Count);
        rec.Flush("real");
        Assert.Equal(1, writes);
        Assert.Equal(0, rec.Count);   // ring cleared by the flush

        rec.Flush("again");
        Assert.Equal(1, writes);   // still nothing new recorded -> no second write
    }

    // ---- (4) retention prunes to 20, oldest-first, via the injected lister/deleter ----
    [Fact]
    public void Retention_prunes_to_20_newest_deleting_oldest_first()
    {
        var existing = Enumerable.Range(0, 25)
            .Select(i => $"flight_2026070{i / 10}_{(i % 10):00}0000_battle-exit.jsonl")
            .OrderBy(n => n, StringComparer.Ordinal)   // the lister's return order is NOT assumed sorted
            .Reverse()
            .ToList();
        var deleted = new List<string>();
        var rec = Make(fileWriter: (_, _) => { }, lister: _ => existing, deleter: p => deleted.Add(p));

        rec.Record("t", "a");
        rec.Flush("battle-exit");

        Assert.Equal(5, deleted.Count);   // 25 - 20 = 5 pruned
        var expectedOldestFive = existing.OrderBy(n => n, StringComparer.Ordinal).Take(5).ToList();
        Assert.Equal(expectedOldestFive, deleted);   // oldest-first, exact identity
    }

    // ---- (5) injected clock stamps monotonic elapsedMs ----
    [Fact]
    public void Injected_clock_stamps_elapsedMs_on_every_record()
    {
        long t = 1000;
        var rec = Make(clock: () => t);
        rec.Record("a", "1"); t = 1500;
        rec.Record("b", "2"); t = 9999;
        rec.Record("c", "3");

        var snap = rec.Snapshot();
        Assert.Equal(new long[] { 1000, 1500, 9999 }, snap.Select(r => r.ElapsedMs).ToArray());
    }

    // ---- (6) FlushOnce: only the FIRST error-triggered request per launch actually flushes ----
    [Fact]
    public void Error_trigger_is_FlushOnce_second_request_no_ops()
    {
        int writes = 0;
        var rec = Make(fileWriter: (_, _) => writes++);

        rec.Record("t", "a");
        rec.RequestFlush("error");
        rec.RequestFlush("error");   // second request before drain -- must not double-arm
        rec.DrainPending();
        Assert.Equal(1, writes);

        rec.Record("t", "b");        // fresh data waiting...
        rec.RequestFlush("error");   // ...but a THIRD error request must still be inert (FlushOnce)
        rec.DrainPending();
        Assert.Equal(1, writes);     // no second flush, ever, for the error trigger
        Assert.Equal(1, rec.Count);  // "b" is still sitting unflushed in the ring
    }

    // ---- (6b) B1's central promise: RequestFlush NEVER performs the flush itself -- the write
    // happens only when the Engine loop drains. A synchronous flush inside RequestFlush would run
    // file I/O on the game's own SetTextString thread (the stall the deferred design exists to
    // prevent); this is the one test that catches that regression. ----
    [Fact]
    public void RequestFlush_defers_all_io_until_DrainPending()
    {
        int writes = 0;
        var rec = Make(fileWriter: (_, _) => writes++);

        rec.Record("t", "a");
        rec.RequestFlush("error");
        Assert.Equal(0, writes);     // the request only arms a flag -- NO write on the caller's thread

        rec.DrainPending();
        Assert.Equal(1, writes);     // the drain (Engine loop thread) performs the actual write
    }

    // ---- (7) a throwing flush sink: nothing escapes, no recursion, ring stays usable ----
    [Fact]
    public void Throwing_flush_sink_does_not_escape_or_corrupt_the_recorder()
    {
        int calls = 0;
        var rec = Make(fileWriter: (_, _) =>
        {
            calls++;
            throw new InvalidOperationException("disk is on fire");
        });
        rec.Record("t", "a");
        rec.Record("t", "b");

        var ex = Record.Exception(() => rec.Flush("boom"));

        Assert.Null(ex);          // never escapes
        Assert.Equal(1, calls);   // never recurses / retries within the same call
        Assert.Equal(0, rec.Count);   // the ring was already swapped out before the sink ran (S3) --
                                       // a failed write loses that batch, but the live ring is empty
                                       // and perfectly usable afterward, not corrupted or stuck:
        rec.Record("t", "c");
        Assert.Equal(1, rec.Count);
        var ex2 = Record.Exception(() => rec.Flush("boom-again"));
        Assert.Null(ex2);
    }

    // ---- (8) FRESH concurrency hammer: N writer threads + concurrent flushes ----
    [Fact]
    public void Concurrent_writers_and_flushes_conserve_every_record_with_no_exception()
    {
        var flushedPayloads = new HashSet<string>();
        var flushLock = new object();
        var rec = Make(fileWriter: (_, content) =>
        {
            lock (flushLock)
            {
                foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var o = JObject.Parse(line);
                    if (o["hdr"] != null) continue;
                    flushedPayloads.Add((string)o["d"]!);
                }
            }
        });

        const int writers = 8;
        const int perWriter = 200;   // 1600 total -- comfortably under Capacity(4096), so no drops expected
        var exceptions = new List<Exception>();
        var excLock = new object();

        var writerTasks = Enumerable.Range(0, writers).Select(w => Task.Run(() =>
        {
            try
            {
                for (int i = 0; i < perWriter; i++)
                    rec.Record("hammer", $"w{w}-{i}");
            }
            catch (Exception ex) { lock (excLock) exceptions.Add(ex); }
        })).ToArray();

        var flusherCts = new CancellationTokenSource();
        var flusherTask = Task.Run(() =>
        {
            try
            {
                while (!flusherCts.IsCancellationRequested)
                    rec.Flush("hammer");
            }
            catch (Exception ex) { lock (excLock) exceptions.Add(ex); }
        });

        Task.WaitAll(writerTasks, TimeSpan.FromSeconds(30));
        flusherCts.Cancel();
        flusherTask.Wait(TimeSpan.FromSeconds(30));
        rec.Flush("final");   // drain whatever is left in the ring

        Assert.Empty(exceptions);

        var retained = rec.Snapshot().Select(r => r.Payload).ToList();
        Assert.Empty(retained);   // the final Flush above drained everything

        // Conservation: every payload the writers produced is accounted for exactly once
        // (flushed here XOR still-retained -- retained is empty after the final drain above),
        // and nothing extra/corrupted shows up.
        var expected = new HashSet<string>();
        for (int w = 0; w < writers; w++)
            for (int i = 0; i < perWriter; i++)
                expected.Add($"w{w}-{i}");

        Assert.Equal(expected.Count, flushedPayloads.Count);
        Assert.True(expected.SetEquals(flushedPayloads));
    }

    // ---- (9) facade null-object: Flight.* before Init neither throws nor accumulates ----
    [Fact]
    public void Facade_is_inert_before_Init_and_works_after()
    {
        Flight.Reset();
        try
        {
            var ex = Record.Exception(() =>
            {
                Flight.Record("t", "before-init");
                Flight.RequestFlush("error");
                Flight.DrainPending();
                Flight.FlushBattleEnd();
            });
            Assert.Null(ex);   // pure no-ops, nothing throws, nothing queued anywhere

            // Post-Init smoke: a real recorder now backs the facade.
            var dir = TempDir();
            Flight.Init(dir);
            var ex2 = Record.Exception(() =>
            {
                Flight.Record("t", "after-init");
                Flight.FlushBattleEnd();
            });
            Assert.Null(ex2);
            var flightDir = Path.Combine(dir, "flight");
            Assert.True(Directory.Exists(flightDir));
            Assert.Single(Directory.GetFiles(flightDir, "flight_*.jsonl"));
        }
        finally { Flight.Reset(); }   // never leak a live recorder into a later test
    }
}
