using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ModGates.Logging;
using Xunit;

namespace ModGates.Tests.Logging;

/// <summary>
/// The static facade and the ScopedLogger it builds. Every case tags its messages with a fresh
/// id and asserts on lines carrying that id, so another test class logging through the shared
/// static at the same time cannot make a case flake. Each case restores the logger it found.
/// </summary>
public class ModLoggerTests
{
    /// <summary>Thread-safe capture of (level, verb, message) with no rendering, for routing cases.</summary>
    private sealed class CaptureLogger : ILogger
    {
        private readonly object _gate = new();
        private readonly List<(LogLevel Level, LogVerb Verb, string Message)> _lines = new();
        public LogLevel LogLevel { get; set; } = LogLevel.Debug;
        public (LogLevel Level, LogVerb Verb, string Message)[] Snapshot()
        {
            lock (_gate) { return _lines.ToArray(); }
        }
        private void Add(LogLevel level, LogVerb verb, string message)
        {
            lock (_gate) { _lines.Add((level, verb, message)); }
        }
        public void Log(LogVerb verb, string message) => Add(LogLevel.Info, verb, message);
        public void LogWarning(LogVerb verb, string message) => Add(LogLevel.Warning, verb, message);
        public void LogError(LogVerb verb, string message) => Add(LogLevel.Error, verb, message);
        public void LogError(LogVerb verb, string message, Exception exception) => Add(LogLevel.Error, verb, message + " " + exception.Message);
        public void LogDebug(LogVerb verb, string message) => Add(LogLevel.Debug, verb, message);
        public void NoteBattleEdge() { }
    }

    private sealed class Swap : IDisposable
    {
        private readonly ILogger _prior = ModLogger.Instance;
        public Swap(ILogger logger) => ModLogger.Instance = logger;
        public void Dispose() => ModLogger.Instance = _prior;
    }

    private static string Tag() => Guid.NewGuid().ToString("N");

    [Fact]
    public void Every_facade_member_routes_through_the_current_Instance()
    {
        var capture = new CaptureLogger();
        string tag = Tag();
        using (new Swap(capture))
        {
            ModLogger.Event(TestVerbs.A, tag + " a");
            ModLogger.Warn(TestVerbs.A, tag + " b");
            ModLogger.Error(TestVerbs.B, tag + " c");
            ModLogger.Debug(LogVerb.Trace, tag + " d");
            ModLogger.Error(TestVerbs.B, tag + " e", new Exception("boom"));
        }
        var mine = capture.Snapshot().Where(l => l.Message.StartsWith(tag, StringComparison.Ordinal)).ToArray();
        Assert.Equal(new[] { LogLevel.Info, LogLevel.Warning, LogLevel.Error, LogLevel.Debug, LogLevel.Error }, mine.Select(l => l.Level));
        Assert.Equal(new[] { TestVerbs.A, TestVerbs.A, TestVerbs.B, LogVerb.Trace, TestVerbs.B }, mine.Select(l => l.Verb));
        Assert.EndsWith("e boom", mine[4].Message);
    }

    [Fact]
    public void LogLevel_passes_through_to_the_Instance()
    {
        var fake = new FileConsoleLogger(_ => { }, _ => { });
        using (new Swap(fake))
        {
            ModLogger.LogLevel = LogLevel.Debug;
            Assert.Equal(LogLevel.Debug, fake.LogLevel);
            Assert.Equal(LogLevel.Debug, ModLogger.LogLevel);
        }
    }

    [Fact]
    public void UseNullLogger_swaps_in_the_swallow_everything_logger()
    {
        var prior = ModLogger.Instance;
        try
        {
            ModLogger.UseNullLogger();
            Assert.Same(NullLogger.Instance, ModLogger.Instance);
            Assert.Null(Record.Exception(() => ModLogger.Event(TestVerbs.A, "anything, anywhere")));
        }
        finally { ModLogger.Instance = prior; }
    }

    [Fact]
    public void Reset_lets_a_fresh_Instance_be_installed_after_a_prior_swap()
    {
        var prior = ModLogger.Instance;
        try
        {
            ModLogger.Instance = NullLogger.Instance;
            ModLogger.Reset();
            var replacement = new FileConsoleLogger(_ => { }, _ => { });
            ModLogger.Instance = replacement;
            Assert.Same(replacement, ModLogger.Instance);
        }
        finally { ModLogger.Instance = prior; }
    }

    [Fact]
    public void Init_names_the_log_file_and_the_prefix_and_rotates_the_previous_launch_aside()
    {
        string dir = Path.Combine(Path.GetTempPath(), "modgates_" + Tag());
        Directory.CreateDirectory(dir);
        var prior = ModLogger.Instance;
        try
        {
            ModLogger.Init(dir, "[Test Mod]", "testmod");
            ModLogger.LogLevel = LogLevel.None;
            ModLogger.Event(TestVerbs.A, "first launch");
            ModLogger.Init(dir, "[Test Mod]", "testmod");
            ModLogger.LogLevel = LogLevel.None;
            ModLogger.Event(TestVerbs.A, "second launch");

            string current = File.ReadAllText(Path.Combine(dir, "testmod.log"));
            string previous = File.ReadAllText(Path.Combine(dir, "testmod.prev.log"));
            Assert.StartsWith("[Test Mod] [", current);
            Assert.Contains("second launch", current);
            Assert.Contains("first launch", previous);
            Assert.DoesNotContain("first launch", current);
        }
        finally
        {
            ModLogger.Reset();
            ModLogger.Instance = prior;
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Init_passes_the_fault_and_flush_hooks_to_the_logger()
    {
        string dir = Path.Combine(Path.GetTempPath(), "modgates_" + Tag());
        Directory.CreateDirectory(dir);
        var prior = ModLogger.Instance;
        int flushes = 0;
        try
        {
            ModLogger.Init(dir, "[Test Mod]", "testmod", errorFlush: () => flushes++);
            ModLogger.LogLevel = LogLevel.None;
            ModLogger.Error(TestVerbs.B, "boom");
            Assert.Equal(1, flushes);
        }
        finally
        {
            ModLogger.Reset();
            ModLogger.Instance = prior;
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Event_renders_Info_with_the_verb_on_the_file_only_and_Warn_keeps_it_on_both()
    {
        string tag = Tag();
        using var cap = LogCapture.Start();
        ModLogger.Event(TestVerbs.A, tag + " claims a kill");
        ModLogger.Warn(TestVerbs.B, tag + " backup copy is corrupt");

        string infoConsole = cap.Console.Single(l => l.Contains(tag + " claims"));
        string infoFile = cap.File.Single(l => l.Contains(tag + " claims"));
        Assert.Contains("[INFO]", infoConsole);
        Assert.DoesNotContain(TestVerbs.Bracket(TestVerbs.A), infoConsole);
        Assert.Contains(TestVerbs.Bracket(TestVerbs.A) + " " + tag, infoFile);

        string warnConsole = cap.Console.Single(l => l.Contains(tag + " backup"));
        Assert.Contains("[WARN]", warnConsole);
        Assert.Contains(TestVerbs.Bracket(TestVerbs.B), warnConsole);
        Assert.Contains(TestVerbs.Bracket(TestVerbs.B), cap.File.Single(l => l.Contains(tag + " backup")));
    }

    [Fact]
    public void Error_with_exception_appends_the_detail_line_and_Debug_stays_file_only()
    {
        string tag = Tag();
        using var cap = LogCapture.Start();
        ModLogger.Error(TestVerbs.B, tag + " failed to save", new InvalidOperationException("disk full " + tag));
        ModLogger.Debug(LogVerb.Trace, tag + " sentinels");

        Assert.Equal(2, cap.Console.Count(l => l.Contains(tag)));
        Assert.Contains(cap.Console, l => l.Contains("disk full " + tag));
        Assert.DoesNotContain(cap.Console, l => l.Contains(tag + " sentinels"));
        Assert.Contains(cap.File, l => l.Contains("[DEBUG] [trace] " + tag + " sentinels"));
    }

    [Fact]
    public void EventWithTrace_emits_a_clean_console_line_and_a_trace_companion_in_the_file()
    {
        string tag = Tag();
        using var cap = LogCapture.Start();
        ModLogger.EventWithTrace(TestVerbs.A, tag + " battle started", tag + " slot0=0x1 slot9=0x2");

        string console = cap.Console.Single(l => l.Contains(tag));
        Assert.Contains("battle started", console);
        Assert.DoesNotContain("slot0", console);
        Assert.Contains(cap.File, l => l.Contains(TestVerbs.Bracket(TestVerbs.A) + " " + tag + " battle started"));
        Assert.Contains(cap.File, l => l.Contains("[trace] " + tag + " slot0=0x1"));
    }

    [Fact]
    public void WarnWithTrace_emits_a_Warning_console_line_with_the_verb_and_a_trace_companion()
    {
        string tag = Tag();
        using var cap = LogCapture.Start();
        ModLogger.WarnWithTrace(TestVerbs.A, tag + " goes uncredited", tag + " waited 60 ticks");

        string console = cap.Console.Single(l => l.Contains(tag));
        Assert.Contains("[WARN] " + TestVerbs.Bracket(TestVerbs.A) + " " + tag + " goes uncredited", console);
        Assert.Contains(cap.File, l => l.Contains("[trace] " + tag + " waited 60 ticks"));
    }

    [Fact]
    public void The_file_gets_every_tier_and_the_console_dedups_per_battle()
    {
        string tag = Tag();
        using var cap = LogCapture.Start();
        ModLogger.Event(TestVerbs.A, tag + " refused");
        ModLogger.Event(TestVerbs.A, tag + " refused");
        ModLogger.Debug(LogVerb.Trace, tag + " detail");
        ModLogger.NoteBattleEdge();
        ModLogger.Event(TestVerbs.A, tag + " refused");

        Assert.Equal(4, cap.File.Count(l => l.Contains(tag)));
        Assert.Equal(2, cap.Console.Count(l => l.Contains(tag)));
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public void ScopedLogger_Info_logs_at_Info_when_armed_and_demotes_to_Debug_when_not(bool armed, int expectedLevel)
    {
        var capture = new CaptureLogger();
        string tag = Tag();
        using (new Swap(capture))
            ModLogger.For(TestVerbs.A, () => armed).Info(tag);
        Assert.Contains(((LogLevel)expectedLevel, TestVerbs.A, tag), capture.Snapshot());
    }

    [Theory]
    [InlineData(true, 2)]
    [InlineData(false, 0)]
    public void ScopedLogger_Warn_logs_at_Warning_when_armed_and_demotes_to_Debug_when_not(bool armed, int expectedLevel)
    {
        var capture = new CaptureLogger();
        string tag = Tag();
        using (new Swap(capture))
            ModLogger.For(TestVerbs.A, () => armed).Warn(tag);
        Assert.Contains(((LogLevel)expectedLevel, TestVerbs.A, tag), capture.Snapshot());
    }

    [Fact]
    public void ScopedLogger_Debug_always_reaches_the_file_regardless_of_armed()
    {
        var capture = new CaptureLogger();
        string tag = Tag();
        using (new Swap(capture))
            ModLogger.For(TestVerbs.A, () => false).Debug(tag);
        Assert.Contains((LogLevel.Debug, TestVerbs.A, tag), capture.Snapshot());
    }

    [Fact]
    public void ScopedLogger_treats_a_throwing_armed_predicate_as_not_armed_and_never_throws()
    {
        var capture = new CaptureLogger();
        string tag = Tag();
        Exception? ex;
        using (new Swap(capture))
            ex = Record.Exception(() => ModLogger.For(TestVerbs.A, () => throw new InvalidOperationException("memory read failed")).Info(tag));
        Assert.Null(ex);
        Assert.Contains((LogLevel.Debug, TestVerbs.A, tag), capture.Snapshot());
    }
}
