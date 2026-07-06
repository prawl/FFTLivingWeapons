using System;
using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// F2: maintenance repaint.  Display.Tick calls PaintAll on a MaintenanceMs cadence
/// (injected clock), regardless of count changes, so stale on-screen copies are
/// refreshed and dead sites are drained without waiting for a kill bump.
/// </summary>
public class DisplayMaintenanceTests
{
    // ─── Fixture (mirrors DisplayTests, but narrower scope) ───────────────────

    private const long StaticsBase = 0x50_0000_0000L;
    private const long SourceBase  = 0x60_0000_0000L;

    private static Dictionary<int, WeaponMeta> BuildMeta() => new()
    {
        { 10, new WeaponMeta { Name = "SwordA", Flavor = "Bright edge of dawn", Wp = 12, Cat = "Sword", Formula = 1 } },
    };

    private static (FakeHeap heap,
                    (int suffixPos, int flavorPos, int killsSlotPos) card,
                    Display display,
                    TestClock clock)
        BuildFixture(Dictionary<int, int> kills, int mirrorId = 10)
    {
        var meta = BuildMeta();
        var src  = new byte[512];
        var card = CardFixtures.WriteCard(src, 0, "SwordA", "Bright edge of dawn");

        var statics = new byte[16];
        statics[0] = (byte)(mirrorId & 0xFF);
        statics[1] = (byte)(mirrorId >> 8);
        statics[4] = 12; // WpScratch natural

        var heap = new FakeHeap((SourceBase, src), (StaticsBase, statics));
        var clock   = new TestClock();
        var display = CardFixtures.MakeDisplay(meta, kills, heap, StaticsBase, clock);
        return (heap, card, display, clock);
    }

    private static string ReadSlot(FakeHeap heap, long pos, int len)
    {
        heap.TryReadBytes(SourceBase + pos, len, out var buf);
        return System.Text.Encoding.ASCII.GetString(buf);
    }

    // ─── T3: maintenance repaint within MaintenanceMs, cadence respected ──────

    /// <summary>
    /// A painted slot whose bytes the game "reset" to "0   " (without touching the anchor)
    /// is repainted within MaintenanceMs without any count change.
    /// Before MaintenanceMs elapses it is NOT repainted (cadence respected).
    /// </summary>
    [Fact]
    public void Stale_slot_repainted_within_maintenance_cadence()
    {
        var kills  = new Dictionary<int, int> { { 10, 9 } };
        var (heap, card, display, clock) = BuildFixture(kills);

        int meterWidth = Signatures.KillsMeterSlotChars;
        string nine = Signatures.KillsMeterSlot(9);

        // Drain until the card is discovered and painted (count=9).
        CardFixtures.DrainGeneration(display, clock, 500);
        Assert.Equal(nine, ReadSlot(heap, card.killsSlotPos, meterWidth));
        Assert.True(display._sites.Count > 0, "sweep must register sites");

        // Force the maintenance clock to a precise epoch: run a tick far enough past
        // the last maintenance to guarantee _lastMaintenanceMs is set at exactly t0.
        // Because DrainGeneration advances the clock in 251ms increments, maintenance
        // fires roughly every 4 ticks; we jump far enough ahead that the NEXT tick
        // definitely fires maintenance and sets _lmm = clock.Ms.
        clock.Ms += Display.MaintenanceMs * 2;
        display.Tick(false);
        long t0 = clock.Ms; // _lastMaintenanceMs is now exactly t0

        // Slot is still 9's meter body (maintenance fired -> skip-if-equal).
        Assert.Equal(nine, ReadSlot(heap, card.killsSlotPos, meterWidth));

        // Simulate game buffer reset: write the unpainted placeholder without any count change.
        heap.WriteBytes(SourceBase + card.killsSlotPos, ByteScan.Ascii(Signatures.KillsMeterSlot(0)));

        // Half-interval tick: maintenance cadence not yet reached -> no repaint.
        // Use a time strictly less than MaintenanceMs from t0.
        clock.Ms = t0 + Display.MaintenanceMs - 1;
        display.Tick(false);
        Assert.Equal(Signatures.KillsMeterSlot(0), ReadSlot(heap, card.killsSlotPos, meterWidth));

        // Full-interval tick: past MaintenanceMs from t0 -> maintenance PaintAll fires.
        clock.Ms = t0 + Display.MaintenanceMs + 1;
        display.Tick(false);
        Assert.Equal(nine, ReadSlot(heap, card.killsSlotPos, meterWidth));
    }

    // ─── T4: dead sites evicted by maintenance pass ───────────────────────────

    /// <summary>
    /// Dead sites (anchor removed from heap) are evicted by the maintenance PaintAll,
    /// causing Count to decrease on the maintenance cadence, NOT only on count changes.
    /// </summary>
    [Fact]
    public void Dead_sites_evicted_by_maintenance_pass()
    {
        var kills   = new Dictionary<int, int> { { 10, 5 } };
        var (heap, card, display, clock) = BuildFixture(kills);

        // Drain until the card is discovered (may register enc=1 + enc=2 sites).
        CardFixtures.DrainGeneration(display, clock, 500);
        int countBeforeRemoval = display._sites.Count;
        Assert.True(countBeforeRemoval > 0, "at least one site must be registered");

        // Simulate buffer freed: remove the heap region so anchor reads fail.
        heap.RemoveRegion(SourceBase);

        // Anchor the maintenance clock: advance past MaintenanceMs so the next tick fires.
        clock.Ms += Display.MaintenanceMs + 1;
        display.Tick(false);

        // PaintAll triggered by maintenance should have evicted ALL now-dead sites.
        Assert.Equal(0, display._sites.Count);
    }
}
