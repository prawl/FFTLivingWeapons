using System.Collections.Generic;
using System.IO;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// StoryLines is the compose driver (docs/RELIQUARY_P1_PLAN.md section G): owns EarnedAnchors +
/// the LegendStore view + meta budgets, and is the ONE seam Display.cs calls into (keeping
/// Display's own addition to ~12 lines). SeedAtStartup recomposes CURRENT from store state
/// (never persisted itself -- decision 12) and loads PREVIOUS from the store's "lastPainted".
/// RecomposeChanged is the live compose-change edge: it rotates the anchor and persists the
/// evicted line as the new "lastPainted" in the SAME step.
/// </summary>
public class StoryLinesTests
{
    private const int Id = 9;
    private const byte Archer = 77;   // Human

    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "lw_storylines_" + Path.GetRandomFileName());
        Directory.CreateDirectory(d);
        return d;
    }

    private static (Dictionary<int, WeaponMeta> meta, CardPatterns pats) Meta(string flavor)
    {
        var meta = new Dictionary<int, WeaponMeta>
        {
            [Id] = new WeaponMeta { Name = "Windrunner", Flavor = flavor, Wp = 10, Cat = "Bow", Formula = 1 },
        };
        return (meta, new CardPatterns(meta));
    }

    [Fact]
    public void Seed_from_store()
    {
        // Budget wide enough that both a mark-bearing line and a plain last-victim line fit.
        var (meta, pats) = Meta(new string('x', 200));
        var store = LegendStore.Load(TempDir());
        store.RecordDeed(Id, new VictimSnapshot(true, 42, Archer, false));
        string? expectedPrevious = "Windrunner: 3 felled; last, an Archer.".PadRight(200);
        store.RotatePainted(Id, expectedPrevious);

        var kills = new Dictionary<int, int> { [Id] = 5 };
        var stories = new StoryLines(store, meta, kills, pats);

        stories.SeedAtStartup();

        var anchors = stories.AnchorsFor(Id, 1);
        // baked + current(recomposed from the ONE recorded deed) + previous(the persisted lastPainted)
        Assert.Equal(3, anchors.Count);
        string current = System.Text.Encoding.ASCII.GetString(anchors[1]);
        Assert.Contains("an Archer", current);
        string previous = System.Text.Encoding.ASCII.GetString(anchors[2]);
        Assert.Equal(expectedPrevious, previous);
    }

    [Fact]
    public void Recompose_rotates_and_persists_previous()
    {
        var (meta, pats) = Meta(new string('x', 200));
        var store = LegendStore.Load(TempDir());
        var kills = new Dictionary<int, int> { [Id] = 1 };
        var stories = new StoryLines(store, meta, kills, pats);

        store.RecordDeed(Id, new VictimSnapshot(true, 1, Archer, false));
        stories.RecomposeChanged(new[] { Id });   // FIRST compose: no prior current -> no eviction

        Assert.Null(store.Get(Id).LastPainted);
        var afterFirst = stories.AnchorsFor(Id, 1);
        Assert.Equal(2, afterFirst.Count);   // baked + current only

        string firstLine = System.Text.Encoding.ASCII.GetString(afterFirst[1]);

        kills[Id] = 2;
        store.RecordDeed(Id, new VictimSnapshot(true, 2, Archer, false));   // changes the kill count -> different composed line
        stories.RecomposeChanged(new[] { Id });

        Assert.Equal(firstLine, store.Get(Id).LastPainted);   // the evicted current is now persisted
        var afterSecond = stories.AnchorsFor(Id, 1);
        Assert.Equal(3, afterSecond.Count);   // baked + current + previous
    }

    [Fact]
    public void Recompose_ignores_ids_with_no_baked_flavor()
    {
        var (meta, pats) = Meta(new string('x', 200));
        var store = LegendStore.Load(TempDir());
        var kills = new Dictionary<int, int>();
        var stories = new StoryLines(store, meta, kills, pats);

        var ex = Record.Exception(() => stories.RecomposeChanged(new[] { 12345 }));   // unknown id
        Assert.Null(ex);
        Assert.Empty(stories.AnchorsFor(12345, 1));
    }
}
