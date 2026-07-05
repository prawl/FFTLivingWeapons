using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// The sweep-finished log line's per-launch liveness canary (adversarial verification round,
/// item 2): the approved design has generation 1 completing reach the console at Info (a player's
/// console paste can then confirm the display pipeline is alive), while every later generation
/// stays Debug (file-only), matching docs/LOGGING.md's per-launch-canary intent for the
/// `display` verb. Before this fix ALL generations logged at Debug only.
/// </summary>
public class DisplaySweepLogCanaryTests
{
    private static Dictionary<int, WeaponMeta> BuildMeta() => new()
    {
        { 10, new WeaponMeta { Name = "SwordA", Flavor = "Bright edge of dawn", Wp = 12, Cat = "Sword", Formula = 1 } },
    };

    private static (FakeHeap heap, Display display) BuildDisplay(long sourceBase, long staticsBase,
        Dictionary<int, int> kills, TestClock clock)
    {
        var meta = BuildMeta();
        var src = new byte[512];
        CardFixtures.WriteCard(src, 0, "SwordA", "Bright edge of dawn");
        var statics = new byte[64];
        var heap = new FakeHeap((sourceBase, src), (staticsBase, statics));
        var display = CardFixtures.MakeDisplay(meta, kills, heap, staticsBase, clock);
        return (heap, display);
    }

    [Fact]
    public void Generation_one_completing_logs_an_Info_line_reaching_the_console()
    {
        var clock = new TestClock();
        var kills = new Dictionary<int, int> { { 10, 0 } };
        var (_, display) = BuildDisplay(0x50_0000_0000L, 0x51_0000_0000L, kills, clock);

        var console = new List<string>();
        var file = new List<string>();
        var prior = ModLogger.Instance;
        ModLogger.Instance = new FileConsoleLogger(console.Add, file.Add);
        try
        {
            CardFixtures.DrainGeneration(display, clock, 500);

            Assert.Contains(console, l => l.Contains("card display sweep") && l.Contains("first pass"));
            Assert.Contains(file, l => l.Contains("[INFO]") && l.Contains("[display]") && l.Contains("first pass"));
        }
        finally { ModLogger.Instance = prior; }
    }

    [Fact]
    public void Generation_two_completing_stays_Debug_and_never_reaches_the_console()
    {
        var clock = new TestClock();
        var kills = new Dictionary<int, int> { { 10, 0 } };
        var (_, display) = BuildDisplay(0x52_0000_0000L, 0x53_0000_0000L, kills, clock);

        var console = new List<string>();
        var file = new List<string>();
        var prior = ModLogger.Instance;
        ModLogger.Instance = new FileConsoleLogger(console.Add, file.Add);
        try
        {
            CardFixtures.DrainGeneration(display, clock, 500);
            console.Clear();
            file.Clear();

            // A single Tick after the rest gap elapses is enough to complete the next
            // generation over this tiny region (ample per-Tick budget); draining many more
            // ticks here would let further rest gaps elapse and complete generation 3+ too,
            // which is not what this case is pinning.
            clock.Ms += DisplaySweep.GenerationRestMs + 1;
            display.Tick(false);

            Assert.DoesNotContain(console, l => l.Contains("card display sweep") || l.Contains("memory sweep"));
            Assert.Contains(file, l => l.Contains("[DEBUG]") && l.Contains("[display]") && l.Contains("memory sweep number"));
        }
        finally { ModLogger.Instance = prior; }
    }
}
