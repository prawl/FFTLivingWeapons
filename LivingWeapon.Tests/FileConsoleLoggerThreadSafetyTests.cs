using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// BLOCKER regression (adversarial verification round): FileConsoleLogger's
/// _consoleSeenThisBattle HashSet is mutated by Write (Add, on any thread that logs) and
/// cleared by NoteBattleEdge (Clear, called by the Engine loop on both battle edges), with no
/// synchronization between the two call paths before the fix. In production the two threads
/// that race here are the game's own SetTextString thread (PromptSwapHook.Detour logs mid-toast
/// delivery) and the Engine background loop's own thread (every subsystem's routine logging,
/// plus NoteBattleEdge itself). A non-thread-safe HashSet mutated concurrently from multiple
/// threads with no external lock is undefined behavior, not just a race on which entry wins: it
/// can corrupt internal state, throw, or (worst case) spin inside Add's resize path. This test
/// hammers Write from several threads at once, interleaved with a dedicated thread hammering
/// NoteBattleEdge, and demands: no exception escapes, no hang, and the file sink (never deduped,
/// the one sink every live diagnosis depends on) receives every distinct message exactly once.
/// Uses the internal test-seam ctor with ConcurrentQueue-backed fake sinks so the test never
/// touches a real console or file.
/// </summary>
public class FileConsoleLoggerThreadSafetyTests
{
    [Fact]
    public void Concurrent_Write_and_NoteBattleEdge_never_throws_and_never_drops_a_file_line()
    {
        var fileLines = new ConcurrentQueue<string>();
        var consoleLines = new ConcurrentQueue<string>();
        var log = new FileConsoleLogger(consoleLines.Enqueue, fileLines.Enqueue);

        const int writerThreads = 6;
        const int messagesPerThread = 300;
        var expected = new ConcurrentBag<string>();

        var writers = new List<Task>();
        for (int t = 0; t < writerThreads; t++)
        {
            int threadIndex = t;
            writers.Add(Task.Run(() =>
            {
                for (int i = 0; i < messagesPerThread; i++)
                {
                    string message = $"thread {threadIndex} message {i}";
                    expected.Add(message);
                    log.Log(message);
                }
            }));
        }

        // A dedicated thread hammers NoteBattleEdge (the Clear side) the whole time the
        // writers are running: the same shape as Engine firing both battle edges while
        // PromptSwap logs from the game's own thread mid-toast.
        using var edgeCts = new CancellationTokenSource();
        var edgeTask = Task.Run(() =>
        {
            while (!edgeCts.IsCancellationRequested)
                log.NoteBattleEdge();
        });

        Exception? failure = null;
        bool completed;
        try
        {
            completed = Task.WaitAll(writers.ToArray(), TimeSpan.FromSeconds(30));
        }
        catch (Exception ex)
        {
            failure = ex;
            completed = false;
        }
        edgeCts.Cancel();
        edgeTask.Wait(TimeSpan.FromSeconds(5));

        Assert.True(failure is null, $"a writer thread threw: {failure}");
        Assert.True(completed, "writer threads did not complete within the timeout (possible HashSet corruption hang)");

        var expectedList = expected.ToList();
        Assert.Equal(writerThreads * messagesPerThread, expectedList.Count);

        var fileList = fileLines.ToList();
        Assert.Equal(expectedList.Count, fileList.Count);
        Assert.Equal(expectedList.Count, fileList.Distinct().Count());
    }
}
