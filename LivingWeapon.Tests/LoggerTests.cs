using System;
using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// The logging overhaul's core: FileConsoleLogger's TWO-SINK semantics (file gets Debug-and-above
/// UNCONDITIONALLY; console only at/above the configured LogLevel) and the ModLogger static facade
/// that routes every call site to whichever ILogger is current. Ported shape from
/// FFTColorCustomizer's Tests/Core/LoggerTests.cs, adapted for the two-sink divergence. All
/// FileConsoleLogger cases use the injected-sink internal ctor so no test touches a real console
/// or file.
/// </summary>
public class LoggerTests
{
    private static (FileConsoleLogger log, List<string> console, List<string> file) Make(LogLevel level = LogLevel.Info)
    {
        var console = new List<string>();
        var file = new List<string>();
        var log = new FileConsoleLogger(console.Add, file.Add) { LogLevel = level };
        return (log, console, file);
    }

    // --- FileConsoleLogger: the two-sink core ---

    [Fact]
    public void Log_reaches_both_sinks_at_the_default_Info_level()
    {
        var (log, console, file) = Make();
        log.Log("hello");
        Assert.Single(console);
        Assert.Single(file);
        Assert.Contains("hello", console[0]);
        Assert.Contains("hello", file[0]);
    }

    [Fact]
    public void LogDebug_ALWAYS_reaches_the_file_regardless_of_LogLevel()
    {
        // The divergence from ColorCustomizer's single-threshold ConsoleLogger: file is Debug-and-
        // above UNCONDITIONALLY. Even at the noisiest possible console threshold (None), the file
        // still gets every Debug line -- the evidence chain must never thin out.
        var (log, _, file) = Make(LogLevel.None);
        log.LogDebug("diagnostic");
        Assert.Single(file);
        Assert.Contains("diagnostic", file[0]);
    }

    [Fact]
    public void LogDebug_reaches_the_console_ONLY_when_LogLevel_is_Debug()
    {
        var (quiet, quietConsole, _) = Make(LogLevel.Info);
        quiet.LogDebug("diagnostic");
        Assert.Empty(quietConsole);

        var (verbose, verboseConsole, _) = Make(LogLevel.Debug);
        verbose.LogDebug("diagnostic");
        Assert.Single(verboseConsole);
    }

    [Fact]
    public void Debug_tier_file_lines_carry_the_DBG_tag()
    {
        var (log, _, file) = Make();
        log.LogDebug("diagnostic");
        Assert.Contains("DBG ", file[0]);
    }

    [Fact]
    public void Info_tier_lines_carry_no_tier_tag()
    {
        var (log, console, file) = Make();
        log.Log("plain");
        Assert.DoesNotContain("DBG ", console[0]);
        Assert.DoesNotContain("WARNING: ", console[0]);
        Assert.DoesNotContain("ERROR: ", console[0]);
        Assert.DoesNotContain("DBG ", file[0]);
    }

    // InlineData can't carry the internal LogLevel enum on a public test method signature
    // (CS0051) -- pass its int value and cast inside the test body instead. Log() itself is
    // Info-tier, so only a Debug or Info threshold lets it reach the console.
    [Theory]
    [InlineData(0, true)]    // Debug
    [InlineData(1, true)]    // Info
    [InlineData(2, false)]   // Warning -- Info is now below the threshold
    [InlineData(3, false)]   // Error
    [InlineData(4, false)]   // None
    public void Log_console_visibility_follows_the_threshold(int thresholdValue, bool expectConsole)
    {
        var (log, console, file) = Make((LogLevel)thresholdValue);
        log.Log("info line");
        Assert.Equal(expectConsole, console.Count == 1);
        Assert.Single(file);   // file always gets it regardless of threshold
    }

    [Fact]
    public void LogWarning_tags_and_respects_threshold()
    {
        var (log, console, file) = Make(LogLevel.Warning);
        log.LogWarning("careful");
        Assert.Contains("WARNING: ", console[0]);
        Assert.Contains("WARNING: ", file[0]);

        var (quiet, quietConsole, quietFile) = Make(LogLevel.Error);
        quiet.LogWarning("careful");
        Assert.Empty(quietConsole);
        Assert.Single(quietFile);   // file still gets it
    }

    [Fact]
    public void LogError_reaches_the_console_at_every_normal_threshold()
    {
        var (log, console, file) = Make(LogLevel.None);   // even the quietest console threshold...
        log.LogError("boom");
        Assert.Empty(console);   // ...None truly silences the console (by design: total opt-out)
        Assert.Single(file);     // ...but the file still gets it

        var (log2, console2, _) = Make(LogLevel.Error);
        log2.LogError("boom");
        Assert.Single(console2);
        Assert.Contains("ERROR: ", console2[0]);
    }

    [Fact]
    public void LogError_with_exception_appends_a_second_line()
    {
        var (log, console, file) = Make();
        log.LogError("operation failed", new InvalidOperationException("bad state"));
        Assert.Equal(2, console.Count);
        Assert.Contains("operation failed", console[0]);
        Assert.Contains("InvalidOperationException", console[1]);
        Assert.Contains("bad state", console[1]);
        Assert.Equal(2, file.Count);
    }

    [Fact]
    public void LogError_with_null_exception_does_not_append_a_second_line()
    {
        var (log, console, _) = Make();
        log.LogError("operation failed", null!);
        Assert.Single(console);
    }

    [Fact]
    public void A_throwing_sink_does_not_escape_or_crash_the_call()
    {
        var log = new FileConsoleLogger(
            consoleSink: _ => throw new InvalidOperationException("console is down"),
            fileSink: _ => throw new InvalidOperationException("disk is full"));
        var ex = Record.Exception(() => log.Log("anything"));
        Assert.Null(ex);
    }

    // --- NullLogger: swallows everything, never throws ---

    [Fact]
    public void NullLogger_swallows_everything_without_throwing()
    {
        var logger = NullLogger.Instance;
        var ex = Record.Exception(() =>
        {
            logger.Log("x");
            logger.LogWarning("x");
            logger.LogError("x");
            logger.LogError("x", new Exception("x"));
            logger.LogDebug("x");
        });
        Assert.Null(ex);
    }

    // --- ModLogger: the static facade's routing + test-seam swap ---

    [Fact]
    public void ModLogger_routes_every_call_through_the_current_Instance()
    {
        var console = new List<string>();
        var file = new List<string>();
        ModLogger.Instance = new FileConsoleLogger(console.Add, file.Add);
        try
        {
            ModLogger.Log("a");
            ModLogger.LogWarning("b");
            ModLogger.LogError("c");
            ModLogger.LogDebug("d");
            ModLogger.LogException("e", new Exception("boom"));

            Assert.Contains(file, l => l.Contains("a"));
            Assert.Contains(file, l => l.Contains("b") && l.Contains("WARNING"));
            Assert.Contains(file, l => l.Contains("c") && l.Contains("ERROR"));
            Assert.Contains(file, l => l.Contains("d") && l.Contains("DBG"));
            Assert.Contains(file, l => l.Contains("e"));
        }
        finally { ModLogger.UseNullLogger(); }   // restore the assembly-wide safe default (TestLoggingSetup)
    }

    [Fact]
    public void ModLogger_LogLevel_passthrough_reads_and_writes_the_Instance()
    {
        var fake = new FileConsoleLogger(_ => { }, _ => { });
        ModLogger.Instance = fake;
        try
        {
            ModLogger.LogLevel = LogLevel.Debug;
            Assert.Equal(LogLevel.Debug, fake.LogLevel);
            Assert.Equal(LogLevel.Debug, ModLogger.LogLevel);
        }
        finally { ModLogger.UseNullLogger(); }   // restore the assembly-wide safe default (TestLoggingSetup)
    }

    [Fact]
    public void UseNullLogger_swaps_in_the_swallow_everything_logger()
    {
        ModLogger.UseNullLogger();
        try
        {
            var ex = Record.Exception(() => ModLogger.Log("anything, anywhere"));
            Assert.Null(ex);
            Assert.Same(NullLogger.Instance, ModLogger.Instance);
        }
        finally { ModLogger.UseNullLogger(); }   // restore the assembly-wide safe default (TestLoggingSetup)
    }

    [Fact]
    public void Reset_lets_a_fresh_Instance_be_installed_after_a_prior_swap()
    {
        // Never let the lazy default (a REAL FileConsoleLogger rooted at a real path) construct in
        // a test -- always install a fake before reading Instance, keeping this fully file/console-free.
        ModLogger.Instance = NullLogger.Instance;
        ModLogger.Reset();
        var replacement = new FileConsoleLogger(_ => { }, _ => { });
        ModLogger.Instance = replacement;
        Assert.Same(replacement, ModLogger.Instance);
        ModLogger.UseNullLogger();   // restore the assembly-wide safe default (TestLoggingSetup)
    }
}
