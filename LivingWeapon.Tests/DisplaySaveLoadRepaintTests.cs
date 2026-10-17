using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-424. The equip card went blank after a quick Continue because the only full walk ran before
/// a region the card later reads from was listed, and the covered short circuit then ignored any
/// later publish. Edit A (the load drain invalidates the painter) is pinned by the Engine test;
/// edit B (a fresh list is scanned even while covered) is pinned here. The pair is the replay, not
/// this file alone. The fixture puts the post load copy in a region born after the first walk
/// because the root cause assumes it, so a green run proves the painter logic for that shape and
/// is not evidence for it.
/// </summary>
public class DisplaySaveLoadRepaintTests
{
    private const string FlavorA = "Fletched with regret";
    private const string FlavorB = "Arrow never sleeps";

    private sealed class Rig
    {
        public FakeHeap Heap = null!;
        public Display Display = null!;
        public TestClock Clock = null!;
        public long RegionA, RegionB;
        public int SlotA, SlotB;
        public byte[] CardBytes = null!;

        public string Read(long addr) { Heap.TryReadBytes(addr, Signatures.KillsMeterSlotChars, out var b); return System.Text.Encoding.ASCII.GetString(b); }

        public void Beat(long step = DisplaySweep.HotRescanMs + 1)
        {
            Clock.Ms += step;
            CardFixtures.TickWithPoolLocate(Display, false);
        }
    }

    private static Rig Build()
    {
        var meta = new Dictionary<int, WeaponMeta>
        {
            { 10, new WeaponMeta { Name = "BowX", Flavor = FlavorA } },
            { 11, new WeaponMeta { Name = "BowY", Flavor = FlavorB } },
        };
        var kills = new Dictionary<int, int> { { 10, 7 }, { 11, 3 } };
        var buf = new byte[2000];
        var (_, slotA, flavorA) = CardFixtures.WriteCardForwardWithName(buf, 0, "BowX", FlavorA);
        int next = flavorA + ByteScan.Ascii(FlavorA).Length + 20;
        var (_, slotB, _) = CardFixtures.WriteCardForwardWithName(buf, next, "BowY", FlavorB);

        long a = 0x50_0000_0000L, statics = 0x51_0000_0000L;
        var st = new byte[64];
        st[0] = 10;
        // Bigger than the out of battle locate budget, so a restart takes two steps and the old list
        // is rescanned in between, which is the live shape.
        var filler = new byte[24 * 1024 * 1024];
        var heap = new FakeHeap((a, buf, true), (statics, st, true), (0x53_0000_0000L, filler, true));
        var clock = new TestClock { Ms = 5_000_000 };
        var display = CardFixtures.MakeDisplay(meta, kills, heap, statics, clock, poolPaint: true);
        return new Rig { Heap = heap, Display = display, Clock = clock, RegionA = a, RegionB = 0x52_0000_0000L, SlotA = slotA, SlotB = slotB, CardBytes = buf };
    }

    private static void TickUntilCovered(Rig r)
    {
        for (int i = 0; i < 20 && !r.Display.PoolCoveredForTest; i++) r.Beat();
        Assert.True(r.Display.PoolCoveredForTest);
    }

    [Fact]
    public void Save_load_after_coverage_paints_a_card_copy_the_boot_walk_missed()
    {
        var r = Build();
        TickUntilCovered(r);
        Assert.Equal(Signatures.KillsMeterSlot(7), r.Read(r.RegionA + r.SlotA));
        Assert.Equal(Signatures.KillsMeterSlot(3), r.Read(r.RegionA + r.SlotB));

        // A second copy of both cards turns up in a region the first walk never listed.
        var fresh = new byte[r.CardBytes.Length];
        var (_, slotA, flavorA) = CardFixtures.WriteCardForwardWithName(fresh, 0, "BowX", FlavorA);
        var (_, slotB, _) = CardFixtures.WriteCardForwardWithName(fresh, flavorA + ByteScan.Ascii(FlavorA).Length + 20, "BowY", FlavorB);
        r.Heap.AddRegion(r.RegionB, fresh);
        string placeholder = Signatures.KillsMeterSlot(0);

        int passes = r.Display.PoolScanPassesForTest;
        for (int i = 0; i < 5; i++) r.Beat();
        Assert.Equal(passes, r.Display.PoolScanPassesForTest);
        Assert.Equal(placeholder, r.Read(r.RegionB + slotA));
        Assert.True(r.Display.PoolCoveredForTest);

        r.Display.Invalidate();
        r.Beat();
        Assert.True(r.Display.PoolCoveredForTest);
        Assert.True(r.Display._poolLocator.RegionsStale);
        int gen = r.Display._poolLocator.PublishGeneration;
        for (int i = 0; i < 40 && r.Display._poolLocator.PublishGeneration == gen; i++) r.Beat();
        Assert.NotEqual(gen, r.Display._poolLocator.PublishGeneration);
        r.Beat();

        Assert.Equal(Signatures.KillsMeterSlot(7), r.Read(r.RegionB + slotA));
        Assert.Equal(Signatures.KillsMeterSlot(3), r.Read(r.RegionB + slotB));
        Assert.Equal(Signatures.KillsMeterSlot(7), r.Read(r.RegionA + r.SlotA));
        Assert.True(r.Display.PoolScanPassesForTest > passes);
        Assert.False(r.Display._sweep.IsComplete);
    }

    [Fact]
    public void Publish_while_covered_scans_the_new_region_list_once()
    {
        var r = Build();
        TickUntilCovered(r);
        r.Beat();
        int before = r.Display.PoolScanPassesForTest;

        int gen = r.Display._poolLocator.PublishGeneration;
        r.Clock.Ms += Display.MaintenanceMs + 1;
        r.Display.Invalidate();
        bool coveredBeforeBeat = false;
        for (int i = 0; i < 40 && r.Display._poolLocator.PublishGeneration == gen; i++)
        {
            coveredBeforeBeat = r.Display.PoolCoveredForTest;
            r.Beat();
        }
        Assert.NotEqual(gen, r.Display._poolLocator.PublishGeneration);
        Assert.True(coveredBeforeBeat);
        int afterPublish = r.Display.PoolScanPassesForTest;
        Assert.Equal(before + 2, afterPublish);

        // Each beat here steps past MaintenanceMs, so a per beat scan would show.
        for (int i = 0; i < 5; i++) r.Beat(Display.MaintenanceMs + 1);
        Assert.Equal(afterPublish, r.Display.PoolScanPassesForTest);
    }

    // The locator keeps no public revalidate counter, so read its private clock.
    private static long RevalidateStamp(Rig r) =>
        (long)typeof(PoolLocator).GetField("_lastRevalidateMs", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(r.Display._poolLocator)!;

    [Fact]
    public void Covered_steady_state_with_passing_revalidate_never_rescans()
    {
        var r = Build();
        TickUntilCovered(r);
        r.Beat();
        int before = r.Display.PoolScanPassesForTest;
        long stampBefore = RevalidateStamp(r);
        for (int i = 0; i < 10; i++) r.Beat(Display.MaintenanceMs + 1);
        Assert.True(RevalidateStamp(r) > stampBefore);
        Assert.Equal(before, r.Display.PoolScanPassesForTest);
    }
}
