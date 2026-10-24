using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// The tally of errors the mod swallows on purpose. Runs in a serial collection because the
/// counter is process wide and the engine drains it on every battle exit.
/// </summary>
[CollectionDefinition("QuietFaults", DisableParallelization = true)]
public class QuietFaultsCollection { }

[Collection("QuietFaults")]
public class QuietFaultsTests
{
    public QuietFaultsTests() => QuietFaults.ResetForTest();

    [Fact]
    public void Note_then_Drain_returns_the_site_and_its_count()
    {
        QuietFaults.Note("A.One");
        QuietFaults.Note("A.One");
        QuietFaults.Note("B.Two");
        var drained = QuietFaults.Drain();
        Assert.Equal(new[] { ("A.One", 2), ("B.Two", 1) }, drained.ToArray());
    }

    [Fact]
    public void Drain_clears_so_a_second_drain_is_empty()
    {
        QuietFaults.Note("A.One");
        QuietFaults.Drain();
        Assert.Empty(QuietFaults.Drain());
    }

    [Fact]
    public void Note_never_throws_on_a_null_or_empty_site()
    {
        QuietFaults.Note(null!);
        QuietFaults.Note("");
        QuietFaults.Drain();
    }

    [Fact]
    public void Concurrent_notes_from_several_threads_add_up()
    {
        Parallel.For(0, 8, _ => { for (int i = 0; i < 500; i++) QuietFaults.Note("Busy.Site"); });
        Assert.Equal(new[] { ("Busy.Site", 4000) }, QuietFaults.Drain().ToArray());
    }

    [Fact]
    public void Describe_joins_sites_with_their_counts_in_plain_words()
    {
        var line = QuietFaults.Describe(new List<(string, int)> { ("A.One", 2), ("B.Two", 1) });
        Assert.Equal("Errors the mod chose to ignore since the last battle (none of them stopped play): A.One x2, B.Two x1", line);
    }

    [Fact]
    public void ReportBattleExit_logs_one_debug_line_and_clears_the_counts()
    {
        var file = new List<string>();
        ModLogger.Instance = new FileConsoleLogger(_ => { }, file.Add) { LogLevel = LogLevel.Debug };
        try
        {
            QuietFaults.Note("A.One");
            QuietFaults.ReportBattleExit();
            QuietFaults.ReportBattleExit();
        }
        finally { ModLogger.UseNullLogger(); }
        var lines = file.Where(l => l.Contains("Errors the mod chose to ignore")).ToArray();
        Assert.Single(lines);
        Assert.Contains("[DEBUG]", lines[0]);
        Assert.Contains("[startup]", lines[0]);
        Assert.EndsWith("A.One x1", lines[0]);
    }
}
