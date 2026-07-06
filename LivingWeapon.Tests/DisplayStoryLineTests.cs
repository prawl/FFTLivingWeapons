using System.Collections.Generic;
using System.IO;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// Full-pipeline Reliquary Phase 1 integration (docs/RELIQUARY_AC.md Display section, decision
/// 12): Display -> StoryLines -> EarnedAnchors -> CardScanner/CardSites, driven against a real
/// FakeHeap the way DisplayTests.cs/DisplayRotationTests.cs already do. THE load-bearing test is
/// Kill_that_changes_line_migrates_both_cached_sites_and_kills_slots_keep_updating: two cached
/// sites verified via the OLD (now "previous") line must NOT be evicted when a kill rotates the
/// composed line -- reverting CardSites.AnchorIsLive to baked-only fails this by eviction
/// (frozen Kills counters), which is exactly the bug the three-way anchor exists to prevent.
/// </summary>
public class DisplayStoryLineTests
{
    private const int Id = 30;
    private const long SourceBase = 0x50_0000_0000L;
    private const long StaticsBase = 0x51_0000_0000L;

    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "lw_displaystory_" + Path.GetRandomFileName());
        Directory.CreateDirectory(d);
        return d;
    }

    private static Dictionary<int, WeaponMeta> Meta(string flavor)
        => new() { [Id] = new WeaponMeta { Name = "Windrunner", Flavor = flavor, Wp = 10, Cat = "Bow", Formula = 1 } };

    private static FakeHeap StaticsHeap(int mirrorId)
    {
        var statics = new byte[64];
        statics[0] = (byte)(mirrorId & 0xFF);
        statics[1] = (byte)(mirrorId >> 8);
        return new FakeHeap((StaticsBase, statics));
    }

    private static string ReadRegion(FakeHeap heap, long addr, int len)
    {
        heap.TryReadBytes(addr, len, out var buf);
        return System.Text.Encoding.ASCII.GetString(buf);
    }

    [Fact]
    public void Kill_that_changes_line_migrates_both_cached_sites_and_kills_slots_keep_updating()
    {
        // Budget wide enough for the last-victim form (below Tuning.MarkThresholds under prod).
        string flavor = new string('x', 60);
        int budget = flavor.Length;
        var meta = Meta(flavor);
        var pats = new CardPatterns(meta);

        var legends = LegendStore.Load(TempDir());
        legends.RecordDeed(Id, new VictimSnapshot(true, 1, 77, false));   // Human, below threshold
        var kills = new Dictionary<int, int> { [Id] = 5 };

        string l1 = CardLine.Compose("Windrunner", 5, legends.Get(Id), budget)!;
        Assert.NotNull(l1);

        // Two independent on-screen "cards" for the SAME weapon, both currently showing L1, in
        // two DIFFERENT source regions (mirrors two menu-scroll buffer copies).
        var srcA = new byte[400];
        int slotA = CardFixtures.WriteKillsBlock(srcA, 20, l1, gap: 10, slot: Signatures.KillsMeterSlot(5));
        var srcB = new byte[400];
        int slotB = CardFixtures.WriteKillsBlock(srcB, 20, l1, gap: 10, slot: Signatures.KillsMeterSlot(5));

        var heap = StaticsHeap(mirrorId: Id);
        heap.AddRegion(SourceBase, srcA);
        heap.AddRegion(SourceBase + 0x1_0000, srcB);

        var clock = new TestClock();
        var display = CardFixtures.MakeDisplay(meta, kills, heap, StaticsBase, clock, legends);
        CardFixtures.DrainGeneration(display, clock, 500);

        Assert.Equal(2, display._sites.Count);   // both cards discovered

        // A kill lands: the tally changes and a new deed rotates the composed line to L2.
        kills[Id] = 6;
        legends.RecordDeed(Id, new VictimSnapshot(true, 2, 77, false));
        string l2 = CardLine.Compose("Windrunner", 6, legends.Get(Id), budget)!;
        Assert.NotNull(l2);
        Assert.NotEqual(l1, l2);

        CardFixtures.DrainGeneration(display, clock, 500);

        // NON-VACUITY (manual verification during implementation): reverting CardSites.AnchorIsLive
        // to baked-only fails this assertion via eviction -- both sites drop to 0/frozen counters.
        Assert.Equal(2, display._sites.Count);   // NEITHER site was evicted

        Assert.Equal(l2, ReadRegion(heap, SourceBase + 20, l2.Length));
        Assert.Equal(l2, ReadRegion(heap, SourceBase + 0x1_0000 + 20, l2.Length));
        Assert.Equal(Signatures.KillsMeterSlot(6), ReadRegion(heap, SourceBase + slotA, Signatures.KillsMeterSlotChars));
        Assert.Equal(Signatures.KillsMeterSlot(6), ReadRegion(heap, SourceBase + 0x1_0000 + slotB, Signatures.KillsMeterSlotChars));
    }

    [Fact]
    public void Two_buffer_staggered_discovery_after_invalidate()
    {
        string flavor = new string('x', 60);
        int budget = flavor.Length;
        var meta = Meta(flavor);

        var legends = LegendStore.Load(TempDir());
        legends.RecordDeed(Id, new VictimSnapshot(true, 1, 77, false));
        var kills = new Dictionary<int, int> { [Id] = 5 };
        string l1 = CardLine.Compose("Windrunner", 5, legends.Get(Id), budget)!;

        var srcA = new byte[400];
        int slotA = CardFixtures.WriteKillsBlock(srcA, 20, l1, gap: 10, slot: Signatures.KillsMeterSlot(5));
        var srcB = new byte[400];
        int slotB = CardFixtures.WriteKillsBlock(srcB, 20, l1, gap: 10, slot: Signatures.KillsMeterSlot(5));

        var heap = StaticsHeap(mirrorId: Id);
        heap.AddRegion(SourceBase, srcA);
        heap.AddRegion(SourceBase + 0x1_0000, srcB);

        var clock = new TestClock();
        var display = CardFixtures.MakeDisplay(meta, kills, heap, StaticsBase, clock, legends);
        CardFixtures.DrainGeneration(display, clock, 500);
        Assert.Equal(2, display._sites.Count);

        // Invalidate wipes the site CACHE (menu buffers "reallocated") -- EarnedAnchors' state
        // (current/previous) is untouched, since it lives in StoryLines, not CardSites.
        display.Invalidate();
        Assert.Equal(0, display._sites.Count);

        // The kill lands AFTER invalidation, while both buffers still physically show L1.
        kills[Id] = 6;
        legends.RecordDeed(Id, new VictimSnapshot(true, 2, 77, false));
        string l2 = CardLine.Compose("Windrunner", 6, legends.Get(Id), budget)!;

        clock.Ms += DisplaySweep.GenerationMinGapMs + 1;
        CardFixtures.DrainGeneration(display, clock, 500);

        // Rediscovery must succeed via the PREVIOUS anchor (L1, now persisted as lastPainted)
        // even though the cache started completely empty -- both sites re-found and repainted.
        Assert.Equal(2, display._sites.Count);
        Assert.Equal(l2, ReadRegion(heap, SourceBase + 20, l2.Length));
        Assert.Equal(l2, ReadRegion(heap, SourceBase + 0x1_0000 + 20, l2.Length));
    }

    [Fact]
    public void Site_rediscovered_after_earned_line_changes()
    {
        // AC-named (docs/RELIQUARY_AC.md): the single-site case -- a cached site verified via
        // the current line at cache time must survive a later kill (which rotates the line) and
        // get repainted, not evicted.
        string flavor = new string('x', 60);
        int budget = flavor.Length;
        var meta = Meta(flavor);

        var legends = LegendStore.Load(TempDir());
        legends.RecordDeed(Id, new VictimSnapshot(true, 1, 77, false));
        var kills = new Dictionary<int, int> { [Id] = 5 };
        string l1 = CardLine.Compose("Windrunner", 5, legends.Get(Id), budget)!;

        var src = new byte[400];
        int slot = CardFixtures.WriteKillsBlock(src, 20, l1, gap: 10, slot: Signatures.KillsMeterSlot(5));
        var heap = StaticsHeap(mirrorId: Id);
        heap.AddRegion(SourceBase, src);

        var clock = new TestClock();
        var display = CardFixtures.MakeDisplay(meta, kills, heap, StaticsBase, clock, legends);
        CardFixtures.DrainGeneration(display, clock, 500);
        Assert.Equal(1, display._sites.Count);

        kills[Id] = 6;
        legends.RecordDeed(Id, new VictimSnapshot(true, 2, 77, false));
        string l2 = CardLine.Compose("Windrunner", 6, legends.Get(Id), budget)!;

        CardFixtures.DrainGeneration(display, clock, 500);

        Assert.Equal(1, display._sites.Count);
        Assert.Equal(l2, ReadRegion(heap, SourceBase + 20, l2.Length));
        Assert.Equal(Signatures.KillsMeterSlot(6), ReadRegion(heap, SourceBase + slot, Signatures.KillsMeterSlotChars));
    }

    [Fact]
    public void Fresh_site_from_onchunk_gets_earned_line()
    {
        // A brand-new site (never before cached) discovered while its buffer still shows the
        // BAKED flavor must be repainted to the CURRENT earned line on its very first paint.
        string flavor = new string('x', 60);
        int budget = flavor.Length;
        var meta = Meta(flavor);

        var legends = LegendStore.Load(TempDir());
        legends.RecordDeed(Id, new VictimSnapshot(true, 1, 77, false));   // deed exists BEFORE Display is even constructed
        var kills = new Dictionary<int, int> { [Id] = 5 };
        string expected = CardLine.Compose("Windrunner", 5, legends.Get(Id), budget)!;

        var src = new byte[400];
        int slot = CardFixtures.WriteKillsBlock(src, 20, flavor, gap: 10);   // buffer still shows the BAKED flavor
        var heap = StaticsHeap(mirrorId: Id);
        heap.AddRegion(SourceBase, src);

        var clock = new TestClock();
        var display = CardFixtures.MakeDisplay(meta, kills, heap, StaticsBase, clock, legends);   // SeedAtStartup composes `expected` as CURRENT
        CardFixtures.DrainGeneration(display, clock, 500);

        Assert.Equal(1, display._sites.Count);
        Assert.Equal(expected, ReadRegion(heap, SourceBase + 20, expected.Length));
        Assert.Equal(Signatures.KillsMeterSlot(5), ReadRegion(heap, SourceBase + slot, Signatures.KillsMeterSlotChars));
    }

    [Fact]
    public void No_deeds_means_no_flavor_write_at_the_display_level()
    {
        string flavor = new string('x', 60);
        var meta = Meta(flavor);
        var legends = LegendStore.Load(TempDir());   // never recorded a single deed for Id
        var kills = new Dictionary<int, int> { [Id] = 5 };

        var src = new byte[400];
        CardFixtures.WriteKillsBlock(src, 20, flavor, gap: 10);
        var heap = StaticsHeap(mirrorId: Id);
        heap.AddRegion(SourceBase, src);

        var clock = new TestClock();
        var display = CardFixtures.MakeDisplay(meta, kills, heap, StaticsBase, clock, legends);
        CardFixtures.DrainGeneration(display, clock, 500);

        Assert.Equal(1, display._sites.Count);
        Assert.Equal(flavor, ReadRegion(heap, SourceBase + 20, flavor.Length));   // baked flavor untouched
    }

    [Fact]
    public void FitsLookback_with_earned_patterns_registered()
    {
        // AC-named: CardPatterns.MaxAnchorLen/FitsLookback are computed from BAKED Name/Flavor
        // only and stay valid once earned patterns are registered -- EarnedAnchors enforces every
        // earned pattern's encoded length EQUAL to its weapon's baked Flavor pattern, so an
        // earned anchor can never exceed MaxAnchorLen.
        string flavor = new string('x', 60);
        var meta = Meta(flavor);
        var pats = new CardPatterns(meta);
        var anchors = new EarnedAnchors(pats);

        Assert.True(pats.FitsLookback(DisplaySweep.Lookback));

        int maxAnchorLenBefore = pats.MaxAnchorLen;
        anchors.SetCurrent(Id, new string('y', 60));   // same length as baked -- must not change MaxAnchorLen
        Assert.True(pats.FitsLookback(DisplaySweep.Lookback));
        Assert.Equal(maxAnchorLenBefore, pats.MaxAnchorLen);   // CardPatterns is immutable -- never consults EarnedAnchors
    }
}
