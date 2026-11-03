using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ModGates.Logging;
using Xunit;

namespace ModGates.Tests.Logging;

/// <summary>
/// The two-sink contract. The file sink records every message of every tier unconditionally;
/// the console sink is curated by LogLevel, deduped per battle on the (level, verb, message)
/// identity, and drops the verb bracket at Info tier only. Every case builds its own logger
/// with injected sinks, so none touches the real console, the disk or the static facade.
/// </summary>
public class FileConsoleLoggerTests
{
    private const string Prefix = "[Test Mod]";

    private static readonly Regex LineShape = new(
        @"^\[Test Mod\] \[\d{2}:\d{2}:\d{2}\.\d{3}\] \[(DEBUG|INFO|WARN|ERROR)\] ", RegexOptions.Compiled);

    private static (FileConsoleLogger log, List<string> console, List<string> file) Make(LogLevel level = LogLevel.Info)
    {
        var console = new List<string>();
        var file = new List<string>();
        var log = new FileConsoleLogger(console.Add, file.Add, Prefix) { LogLevel = level };
        return (log, console, file);
    }

    private static string StripTimestamp(string line) => Regex.Replace(line, @"\[\d{2}:\d{2}:\d{2}\.\d{3}\]", "[TS]");

    [Fact]
    public void Log_reaches_both_sinks_at_the_default_Info_level()
    {
        var (log, console, file) = Make();
        log.Log(TestVerbs.A, "hello");
        Assert.Single(console);
        Assert.Single(file);
        Assert.Contains("hello", console[0]);
        Assert.Contains("hello", file[0]);
    }

    [Fact]
    public void File_records_every_tier_even_when_the_console_threshold_hides_them()
    {
        var (log, console, file) = Make(LogLevel.Error);
        log.LogDebug(LogVerb.Trace, "debug evidence");
        log.Log(TestVerbs.A, "Info line.");
        log.LogWarning(TestVerbs.A, "Warning line.");
        log.LogError(TestVerbs.B, "Error line.");

        Assert.Equal(4, file.Count);
        Assert.Single(console);
        Assert.Contains("Error line.", console[0]);
    }

    [Fact]
    public void LogDebug_always_reaches_the_file_even_when_the_console_is_silenced()
    {
        var (log, console, file) = Make(LogLevel.None);
        log.LogDebug(LogVerb.Trace, "diagnostic");
        Assert.Empty(console);
        Assert.Single(file);
        Assert.Contains("[DEBUG]", file[0]);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    public void Log_console_visibility_follows_the_threshold(int threshold, bool expectConsole)
    {
        var (log, console, file) = Make((LogLevel)threshold);
        log.Log(TestVerbs.A, "info line");
        Assert.Equal(expectConsole, console.Count == 1);
        Assert.Single(file);
    }

    [Fact]
    public void LogDebug_reaches_the_console_only_when_the_threshold_is_Debug()
    {
        var (quiet, quietConsole, _) = Make(LogLevel.Info);
        quiet.LogDebug(LogVerb.Trace, "diagnostic");
        Assert.Empty(quietConsole);

        var (verbose, verboseConsole, _) = Make(LogLevel.Debug);
        verbose.LogDebug(LogVerb.Trace, "diagnostic");
        Assert.Single(verboseConsole);
        Assert.Contains("[trace]", verboseConsole[0]);
    }

    [Fact]
    public void LogWarning_respects_the_threshold_on_the_console_and_never_on_the_file()
    {
        var (log, console, file) = Make(LogLevel.Warning);
        log.LogWarning(TestVerbs.A, "careful");
        Assert.Contains("[WARN]", console[0]);
        Assert.Contains("[WARN]", file[0]);

        var (quiet, quietConsole, quietFile) = Make(LogLevel.Error);
        quiet.LogWarning(TestVerbs.A, "careful");
        Assert.Empty(quietConsole);
        Assert.Single(quietFile);
    }

    [Fact]
    public void None_silences_the_console_for_errors_too_while_Error_still_shows_them()
    {
        var (log, console, file) = Make(LogLevel.None);
        log.LogError(TestVerbs.B, "boom");
        Assert.Empty(console);
        Assert.Single(file);

        var (log2, console2, _) = Make(LogLevel.Error);
        log2.LogError(TestVerbs.B, "boom");
        Assert.Single(console2);
        Assert.Contains("[ERROR]", console2[0]);
    }

    [Fact]
    public void Info_tier_lines_carry_the_INFO_token_and_no_other()
    {
        var (log, console, file) = Make();
        log.Log(TestVerbs.A, "plain");
        foreach (string line in new[] { console[0], file[0] })
        {
            Assert.Contains("[INFO]", line);
            Assert.DoesNotContain("[DEBUG]", line);
            Assert.DoesNotContain("[WARN]", line);
            Assert.DoesNotContain("[ERROR]", line);
        }
    }

    [Fact]
    public void Every_line_on_both_sinks_opens_with_the_prefix_a_millisecond_timestamp_and_the_level()
    {
        var (log, console, file) = Make(LogLevel.Debug);
        log.Log(TestVerbs.A, "Info line.");
        log.LogDebug(LogVerb.Trace, "debug line");
        log.LogWarning(TestVerbs.A, "Warning line.");
        log.LogError(TestVerbs.B, "Error line.");

        Assert.Equal(4, console.Count);
        Assert.Equal(4, file.Count);
        foreach (string line in console.Concat(file))
            Assert.Matches(LineShape, line);
    }

    [Fact]
    public void Info_console_line_drops_the_verb_bracket_but_the_file_line_keeps_it()
    {
        var (log, console, file) = Make();
        log.Log(TestVerbs.A, "The map is armed.");

        Assert.Contains(TestVerbs.Bracket(TestVerbs.A), file[0]);
        Assert.DoesNotContain(TestVerbs.Bracket(TestVerbs.A), console[0]);
        Assert.Contains("The map is armed.", console[0]);
        Assert.Matches(@"^\[Test Mod\] \[\d{2}:\d{2}:\d{2}\.\d{3}\] \[INFO\] The map is armed\.$", console[0]);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(0)]
    public void Console_lines_above_or_below_Info_keep_the_verb_bracket(int tierValue)
    {
        var (log, console, file) = Make(LogLevel.Debug);
        switch ((LogLevel)tierValue)
        {
            case LogLevel.Warning: log.LogWarning(TestVerbs.A, "Something degraded."); break;
            case LogLevel.Error: log.LogError(TestVerbs.A, "Something broke."); break;
            default: log.LogDebug(TestVerbs.A, "diagnostic line"); break;
        }
        Assert.Single(console);
        Assert.Contains(TestVerbs.Bracket(TestVerbs.A), console[0]);
        Assert.Contains(TestVerbs.Bracket(TestVerbs.A), file[0]);
    }

    [Fact]
    public void Error_with_exception_appends_a_detail_line_to_both_sinks()
    {
        var (log, console, file) = Make();
        log.LogError(TestVerbs.B, "The tick failed.", new InvalidOperationException("boom"));

        Assert.Equal(2, file.Count);
        Assert.Equal(2, console.Count);
        Assert.Contains("The tick failed.", console[0]);
        Assert.Contains("InvalidOperationException", file[1]);
        Assert.Contains("boom", file[1]);
    }

    [Fact]
    public void Error_with_a_null_exception_does_not_append_a_second_line()
    {
        var (log, console, _) = Make();
        log.LogError(TestVerbs.B, "operation failed", null!);
        Assert.Single(console);
    }

    [Fact]
    public void Console_dedups_a_repeated_line_but_the_file_never_does()
    {
        var (log, console, file) = Make();
        for (int i = 0; i < 3; i++) log.Log(TestVerbs.A, "battle started");
        Assert.Single(console);
        Assert.Equal(3, file.Count);
    }

    [Fact]
    public void Console_dedup_keys_on_the_semantic_identity_not_the_rendered_string()
    {
        var (log, console, _) = Make();
        log.Log(TestVerbs.A, "The same sentence.");
        log.Log(TestVerbs.B, "The same sentence.");
        Assert.Equal(2, console.Count);
        Assert.Equal(StripTimestamp(console[0]), StripTimestamp(console[1]));
    }

    [Fact]
    public void Dedup_distinguishes_by_level_and_by_content()
    {
        var (log, console, file) = Make(LogLevel.Debug);
        log.Log(TestVerbs.A, "same text");
        log.LogWarning(TestVerbs.A, "same text");
        log.Log(TestVerbs.A, "different text");
        Assert.Equal(3, console.Count);
        Assert.Equal(3, file.Count);
    }

    [Fact]
    public void NoteBattleEdge_resets_the_console_dedup()
    {
        var (log, console, file) = Make();
        log.Log(TestVerbs.A, "credited");
        log.NoteBattleEdge();
        log.Log(TestVerbs.A, "credited");
        Assert.Equal(2, console.Count);
        Assert.Equal(2, file.Count);
    }

    [Theory]
    [InlineData("[Test Mod] [12:00:00.000] [INFO] all accounted for", "[Test Mod] [12:00:00.001] [INFO] all accounted for", true)]
    [InlineData("[Test Mod] [12:00:00.999] [INFO] all accounted for", "[Test Mod] [12:00:01.000] [INFO] all accounted for", true)]
    [InlineData("[Test Mod] [12:00:00.000] [INFO] all accounted for", "[Test Mod] [12:00:00.000] [WARN] all accounted for", false)]
    [InlineData("[Test Mod] [12:00:00.000] [INFO] all accounted for", "[Test Mod] [12:00:00.000] [INFO] a different sentence", false)]
    public void StripTimestamp_erases_only_the_millisecond_stamp(string a, string b, bool expectEqual)
        => Assert.Equal(expectEqual, StripTimestamp(a) == StripTimestamp(b));

    [Fact]
    public void A_throwing_console_sink_never_loses_the_file_line()
    {
        var file = new List<string>();
        var log = new FileConsoleLogger(_ => throw new InvalidOperationException("console died"), file.Add, Prefix);
        log.Log(TestVerbs.A, "Still recorded.");
        Assert.Single(file);
    }

    [Fact]
    public void A_throwing_sink_does_not_escape_and_names_its_site_to_the_fault_note()
    {
        var faults = new List<string>();
        var log = new FileConsoleLogger(
            _ => throw new InvalidOperationException("console is down"),
            _ => throw new InvalidOperationException("disk is full"),
            Prefix, faults.Add);
        var ex = Record.Exception(() => log.Log(TestVerbs.A, "anything"));
        Assert.Null(ex);
        Assert.Equal(new[] { "FileConsoleLogger.File", "FileConsoleLogger.Console" }, faults);
    }

    [Fact]
    public void Every_error_asks_the_mod_to_flush_its_recorder_after_the_lines_are_written()
    {
        var (_, _, file) = Make();
        int flushes = 0, linesAtFlush = -1;
        var log = new FileConsoleLogger(_ => { }, file.Add, Prefix, errorFlush: () => { flushes++; linesAtFlush = file.Count; });
        log.LogError(TestVerbs.B, "one");
        log.LogError(TestVerbs.B, "two", new InvalidOperationException("why"));
        log.LogWarning(TestVerbs.B, "not an error");
        Assert.Equal(2, flushes);
        Assert.Equal(3, linesAtFlush);
    }

    [Fact]
    public void A_throwing_error_flush_is_noted_and_never_thrown()
    {
        var faults = new List<string>();
        var log = new FileConsoleLogger(_ => { }, _ => { }, Prefix, faults.Add, () => throw new InvalidOperationException("recorder gone"));
        var ex = Record.Exception(() => log.LogError(TestVerbs.B, "boom"));
        Assert.Null(ex);
        Assert.Equal(new[] { "FileConsoleLogger.ErrorFlush" }, faults);
    }

    [Fact]
    public void NullLogger_swallows_everything_without_throwing()
    {
        var logger = NullLogger.Instance;
        var ex = Record.Exception(() =>
        {
            logger.Log(TestVerbs.A, "x");
            logger.LogWarning(TestVerbs.A, "x");
            logger.LogError(TestVerbs.B, "x");
            logger.LogError(TestVerbs.B, "x", new Exception("x"));
            logger.LogDebug(LogVerb.Trace, "x");
            logger.NoteBattleEdge();
        });
        Assert.Null(ex);
    }

    [Fact]
    public async Task Concurrent_writes_and_battle_edges_never_throw_and_never_drop_a_file_line()
    {
        var fileLines = new ConcurrentQueue<string>();
        var consoleLines = new ConcurrentQueue<string>();
        var log = new FileConsoleLogger(consoleLines.Enqueue, fileLines.Enqueue, Prefix);
        const int writerThreads = 6, messagesPerThread = 300;
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
                    log.Log(TestVerbs.A, message);
                }
            }));
        }
        using var edgeCts = new CancellationTokenSource();
        var edgeTask = Task.Run(() => { while (!edgeCts.IsCancellationRequested) log.NoteBattleEdge(); });

        Exception? failure = null;
        try { await Task.WhenAll(writers).WaitAsync(TimeSpan.FromSeconds(30)); }
        catch (Exception ex) { failure = ex; }
        edgeCts.Cancel();
        await edgeTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(failure is null, $"a writer thread threw or timed out: {failure}");
        Assert.Equal(writerThreads * messagesPerThread, expected.Count);
        var fileList = fileLines.ToList();
        Assert.Equal(expected.Count, fileList.Count);
        Assert.Equal(expected.Count, fileList.Distinct().Count());
    }
}
