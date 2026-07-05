using System;
using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// The logging facelift's typed facade: LogVerb-tagged Event/Warn/Error/Debug, the
/// EventWithTrace/WarnWithTrace two-line id pattern (conflict C2), and ScopedLogger (the
/// GATE-ON-ARMED structural fix built by ModLogger.For). All cases install a fake
/// FileConsoleLogger via ModLogger.Instance so no test touches the real console/filesystem, and
/// restore NullLogger in a finally (mirrors LoggerTests' convention).
/// </summary>
public class ModLoggerFacadeTests
{
    private static (List<string> console, List<string> file) Install(LogLevel level = LogLevel.Debug)
    {
        var console = new List<string>();
        var file = new List<string>();
        ModLogger.Instance = new FileConsoleLogger(console.Add, file.Add) { LogLevel = level };
        return (console, file);
    }

    // --- Event/Warn/Error/Debug rendering split: FILE always carries "[verb] "; CONSOLE carries
    // it at Warning/Error tier only; an Info-tier console line is the subject-first sentence
    // with no leading bracket. ---

    [Fact]
    public void Event_is_Info_tier_and_the_console_omits_the_verb_while_the_file_keeps_it()
    {
        var (console, file) = Install();
        try { ModLogger.Event(LogVerb.Kill, "Windrunner claims kill number 8"); }
        finally { ModLogger.UseNullLogger(); }
        Assert.Contains("[INFO]", console[0]);
        Assert.DoesNotContain("[kill]", console[0]);
        Assert.Contains("Windrunner claims kill number 8", console[0]);
        Assert.Contains("[kill] Windrunner claims kill number 8", file[0]);
    }

    [Fact]
    public void Warn_is_Warning_tier_and_both_sinks_keep_the_verb()
    {
        var (console, file) = Install();
        try { ModLogger.Warn(LogVerb.Save, "the legends file's backup copy is corrupt"); }
        finally { ModLogger.UseNullLogger(); }
        Assert.Contains("[WARN]", console[0]);
        Assert.Contains("[save]", console[0]);
        Assert.Contains("[save]", file[0]);
    }

    [Fact]
    public void Error_is_Error_tier_and_both_sinks_keep_the_verb()
    {
        var (console, file) = Install();
        try { ModLogger.Error(LogVerb.Engine, "an internal error occurred and this update was skipped"); }
        finally { ModLogger.UseNullLogger(); }
        Assert.Contains("[ERROR]", console[0]);
        Assert.Contains("[engine]", console[0]);
        Assert.Contains("[engine]", file[0]);
    }

    [Fact]
    public void Error_with_exception_appends_the_exception_detail_line()
    {
        var (console, _) = Install();
        try { ModLogger.Error(LogVerb.Save, "failed to save the kill tally", new InvalidOperationException("disk full")); }
        finally { ModLogger.UseNullLogger(); }
        Assert.Equal(2, console.Count);
        Assert.Contains("[save]", console[0]);
        Assert.Contains("disk full", console[1]);
    }

    [Fact]
    public void Debug_folds_the_verb_and_stays_file_only_at_the_default_console_level()
    {
        var (console, file) = Install(LogLevel.Info);
        try { ModLogger.Debug(LogVerb.Trace, "battle-start sentinels (slot0=0x1 slot9=0x2 mode=1)"); }
        finally { ModLogger.UseNullLogger(); }
        Assert.Empty(console);
        Assert.Single(file);
        Assert.Contains("[trace]", file[0]);
    }

    // --- EventWithTrace / WarnWithTrace: the two-line id pattern (C2) ---

    [Fact]
    public void EventWithTrace_emits_a_clean_console_line_and_a_trace_companion()
    {
        var (console, file) = Install(LogLevel.Info);
        try { ModLogger.EventWithTrace(LogVerb.BattleStart, "battle started", "battle-start sentinels (slot0=0x1 slot9=0x2 mode=1)"); }
        finally { ModLogger.UseNullLogger(); }
        Assert.Single(console);
        // Event is Info tier: the console line drops the verb bracket entirely.
        Assert.DoesNotContain("[battle-start]", console[0]);
        Assert.Contains("battle started", console[0]);
        Assert.DoesNotContain("slot0", console[0]);
        // The file keeps the verb on the Info line, plus the trace companion with the ids.
        Assert.Contains(file, l => l.Contains("[battle-start] battle started"));
        Assert.Contains(file, l => l.Contains("[trace]") && l.Contains("slot0=0x1"));
        Assert.DoesNotContain(console, l => l.Contains("slot0"));
    }

    [Fact]
    public void WarnWithTrace_emits_a_clean_Warning_console_line_and_a_trace_companion()
    {
        var (console, file) = Install(LogLevel.Info);
        try { ModLogger.WarnWithTrace(LogVerb.Kill, "the kill goes uncredited", "detail (waited 60 ticks, battle slot 4)"); }
        finally { ModLogger.UseNullLogger(); }
        Assert.Single(console);
        Assert.Contains("[WARN]", console[0]);
        Assert.Contains("[kill] the kill goes uncredited", console[0]);
        Assert.Contains(file, l => l.Contains("[trace]") && l.Contains("battle slot 4"));
    }

    // --- ScopedLogger (ModLogger.For): the GATE-ON-ARMED structural fix ---

    [Fact]
    public void ScopedLogger_Info_reaches_console_when_armed_without_the_verb_bracket()
    {
        var (console, file) = Install();
        var scoped = ModLogger.For(LogVerb.Signature, () => true);
        try { scoped.Info("Galewind is armed for this battle"); }
        finally { ModLogger.UseNullLogger(); }
        Assert.Single(console);
        Assert.Contains("[INFO]", console[0]);
        Assert.DoesNotContain("[signature]", console[0]);
        Assert.Contains("Galewind is armed for this battle", console[0]);
        Assert.Contains("[signature]", file[0]);
    }

    [Fact]
    public void ScopedLogger_Info_demotes_to_Debug_when_not_armed()
    {
        var (console, file) = Install(LogLevel.Info);
        var scoped = ModLogger.For(LogVerb.Signature, () => false);
        try { scoped.Info("Galewind at tier three is wielded on the field"); }
        finally { ModLogger.UseNullLogger(); }
        Assert.Empty(console);
        Assert.Single(file);
        Assert.Contains("[DEBUG]", file[0]);
        Assert.Contains("[signature]", file[0]);
    }

    [Fact]
    public void ScopedLogger_Warn_demotes_to_Debug_when_not_armed()
    {
        var (console, file) = Install(LogLevel.Info);
        var scoped = ModLogger.For(LogVerb.Treasure, () => false);
        try { scoped.Warn("disarmed treasure marks: the dataset was built for a different game build"); }
        finally { ModLogger.UseNullLogger(); }
        Assert.Empty(console);
        Assert.Single(file);
        Assert.Contains("[DEBUG]", file[0]);
    }

    [Fact]
    public void ScopedLogger_Warn_reaches_console_at_Warning_tier_when_armed()
    {
        var (console, _) = Install();
        var scoped = ModLogger.For(LogVerb.Treasure, () => true);
        try { scoped.Warn("disarmed treasure marks: the dataset was built for a different game build"); }
        finally { ModLogger.UseNullLogger(); }
        Assert.Single(console);
        Assert.Contains("[WARN]", console[0]);
        Assert.Contains("[treasure]", console[0]);   // Warning tier keeps the verb on console
    }

    [Fact]
    public void ScopedLogger_Debug_always_reaches_the_file_regardless_of_armed()
    {
        var (console, file) = Install(LogLevel.Info);
        var scoped = ModLogger.For(LogVerb.Signature, () => false);
        try { scoped.Debug("evaluated: slot 3 damage 12 verdict=inactive"); }
        finally { ModLogger.UseNullLogger(); }
        Assert.Empty(console);
        Assert.Single(file);
    }

    [Fact]
    public void ScopedLogger_swallows_a_throwing_armed_predicate_and_treats_it_as_not_armed()
    {
        var (console, file) = Install(LogLevel.Info);
        var scoped = ModLogger.For(LogVerb.Signature, () => throw new InvalidOperationException("memory read failed"));
        Exception? ex;
        try { ex = Record.Exception(() => scoped.Info("should not throw")); }
        finally { ModLogger.UseNullLogger(); }
        Assert.Null(ex);
        Assert.Empty(console);
        Assert.Single(file);   // demoted to Debug, same as a clean "not armed" result
    }
}
