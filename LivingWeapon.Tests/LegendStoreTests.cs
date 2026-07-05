using System.Collections.Generic;
using System.IO;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LegendStore is the Reliquary Phase 1 deed ledger (docs/RELIQUARY_AC.md): legends.json,
/// sibling of kills.json, holding per-weapon lastVictim + archetype counts + earned Marks.
/// Persistence mirrors KillTally's prior-copy-to-.bak ordering exactly (KillTally.cs:64-76)
/// PLUS a corrupt-load warning + flight record KillTally's own silent catch lacks. Each test
/// works in its own temp directory so parallel runs never collide (mirrors KillTallyTests).
/// </summary>
public class LegendStoreTests
{
    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "lw_legends_" + Path.GetRandomFileName());
        Directory.CreateDirectory(d);
        return d;
    }

    private static string PathIn(string dir) => Path.Combine(dir, "legends.json");

    private static VictimSnapshot Victim(ushort nameId, byte job, bool undead = false)
        => new(true, nameId, job, undead);

    [Fact]
    public void Load_missing_file_yields_an_empty_store()
    {
        var dir = TempDir();
        var store = LegendStore.Load(dir);
        Assert.False(store.Has(9));
    }

    [Fact]
    public void Save_then_Load_round_trips_deeds()
    {
        var dir = TempDir();
        var store = LegendStore.Load(dir);
        store.RecordDeed(9, Victim(918, 77));   // Archer -> Human
        store.RotatePainted(9, "some previous line");
        store.SaveIfDirty();

        var back = LegendStore.Load(dir);
        Assert.True(back.Has(9));
        var w = back.Get(9);
        Assert.Equal((ushort)918, w.LastVictimNameId);
        Assert.Equal((byte)77, w.LastVictimJob);
        Assert.Equal((int)VictimClass.Archetype.Human, w.LastVictimCls);
        Assert.Equal(1, w.Counts[(int)VictimClass.Archetype.Human]);
        Assert.Equal("some previous line", w.LastPainted);
    }

    [Fact]
    public void RecordDeed_updates_lastVictim_counts_and_marks()
    {
        var dir = TempDir();
        var store = LegendStore.Load(dir);

        store.RecordDeed(9, Victim(918, 77));   // Human, count 1
        var w = store.Get(9);
        Assert.Equal((ushort)918, w.LastVictimNameId);
        Assert.Equal((byte)77, w.LastVictimJob);
        Assert.Equal(1, w.Counts[(int)VictimClass.Archetype.Human]);
        Assert.Empty(w.Marks);

        store.RecordDeed(9, Victim(1003, 120));   // Monster, separate count lane
        w = store.Get(9);
        Assert.Equal((ushort)1003, w.LastVictimNameId);   // lastVictim follows the MOST RECENT deed
        Assert.Equal(1, w.Counts[(int)VictimClass.Archetype.Human]);
        Assert.Equal(1, w.Counts[(int)VictimClass.Archetype.Monster]);
    }

    [Fact]
    public void Mark_threshold_crossing_returns_newly_earned_once()
    {
        var dir = TempDir();
        var store = LegendStore.Load(dir);
        int threshold = Tuning.MarkThresholds[0];

        List<VictimClass.Archetype> earned = new();
        for (int i = 0; i < threshold; i++)
            earned = store.RecordDeed(9, Victim((ushort)(100 + i), 77));   // Human every time

        Assert.Single(earned);
        Assert.Equal(VictimClass.Archetype.Human, earned[0]);
        Assert.Contains((int)VictimClass.Archetype.Human, store.Get(9).Marks);

        // A further Human kill must NOT re-earn the same mark.
        var again = store.RecordDeed(9, Victim(999, 77));
        Assert.Empty(again);
    }

    [Fact]
    public void Unknown_archetype_is_counted_but_never_earns_a_mark()
    {
        var dir = TempDir();
        var store = LegendStore.Load(dir);
        int threshold = Tuning.MarkThresholds[0];

        List<VictimClass.Archetype> earned = new();
        for (int i = 0; i < threshold + 5; i++)
            earned = store.RecordDeed(9, Victim((ushort)(200 + i), 37));   // job 37: story id -> Unknown

        Assert.Empty(earned);
        Assert.Empty(store.Get(9).Marks);
        Assert.Equal(threshold + 5, store.Get(9).Counts[(int)VictimClass.Archetype.Unknown]);
    }

    [Fact]
    public void Save_leaves_no_tmp_behind_and_writes_a_bak_once_a_primary_exists()
    {
        var dir = TempDir();
        var p = PathIn(dir);
        var store = LegendStore.Load(dir);
        store.RecordDeed(1, Victim(1, 77));
        store.SaveIfDirty();                 // first save: no primary existed yet, so no .bak
        Assert.False(File.Exists(p + ".tmp"));
        Assert.False(File.Exists(p + ".bak"));

        store.RecordDeed(1, Victim(2, 77));
        store.SaveIfDirty();                 // second save: previous primary backed up first
        Assert.True(File.Exists(p + ".bak"));
        Assert.False(File.Exists(p + ".tmp"));
        // job 77 (Archer) classifies Human (archetype index 1): counts[1] == the generation's tally.
        Assert.Contains("\"counts\":[0,1,0,0,0]", File.ReadAllText(p + ".bak"));   // prior generation (1 kill)
        Assert.Contains("\"counts\":[0,2,0,0,0]", File.ReadAllText(p));             // current generation (2 kills)
    }

    [Fact]
    public void Prior_copy_bak_ordering_matches_KillTally()
    {
        // Three successive saves: .bak must always hold generation N-1, primary generation N.
        var dir = TempDir();
        var p = PathIn(dir);
        var store = LegendStore.Load(dir);

        store.RecordDeed(1, Victim(1, 77)); store.SaveIfDirty();   // gen1: count=1
        store.RecordDeed(1, Victim(2, 77)); store.SaveIfDirty();   // gen2: count=2 (.bak=gen1)
        store.RecordDeed(1, Victim(3, 77)); store.SaveIfDirty();   // gen3: count=3 (.bak=gen2)

        Assert.Contains("\"counts\":[0,2,0,0,0]", File.ReadAllText(p + ".bak"));
        Assert.Contains("\"counts\":[0,3,0,0,0]", File.ReadAllText(p));
    }

    [Fact]
    public void Load_falls_back_to_the_bak_when_the_primary_is_corrupt()
    {
        var dir = TempDir();
        var p = PathIn(dir);
        File.WriteAllText(p + ".bak", "{\"42\":{\"lastVictim\":{\"nameId\":7,\"job\":77,\"cls\":1},\"counts\":[0,1,0,0,0],\"marks\":[],\"legends\":[],\"pendingAnnounce\":[],\"lastPainted\":null}}");
        File.WriteAllText(p, "{ not json");

        var store = LegendStore.Load(dir);
        Assert.True(store.Has(42));
        Assert.Equal((ushort)7, store.Get(42).LastVictimNameId);
    }

    [Fact]
    public void CorruptLoad_falls_back_and_logs()
    {
        var dir = TempDir();
        var p = PathIn(dir);
        File.WriteAllText(p, "{ not json at all");

        var lines = new List<string>();
        var fake = new FileConsoleLogger(_ => { }, lines.Add);
        var prior = ModLogger.Instance;
        ModLogger.Instance = fake;
        try
        {
            var store = LegendStore.Load(dir);
            Assert.False(store.Has(42));   // no .bak either -- falls back to empty
            Assert.Contains(lines, l => l.Contains("legend-store") && l.Contains("WARNING"));
        }
        finally { ModLogger.Instance = prior; }
    }

    [Fact]
    public void Load_with_both_files_corrupt_yields_an_empty_store_not_a_crash()
    {
        var dir = TempDir();
        var p = PathIn(dir);
        File.WriteAllText(p, "][");
        File.WriteAllText(p + ".bak", "null");
        var store = LegendStore.Load(dir);
        Assert.False(store.Has(1));
    }

    [Fact]
    public void Save_failure_leaves_previous_file_intact()
    {
        var dir = TempDir();
        var p = PathIn(dir);
        var store = LegendStore.Load(dir);
        store.RecordDeed(9, Victim(1, 77));
        store.SaveIfDirty();
        string before = File.ReadAllText(p);

        store.RecordDeed(9, Victim(2, 77));
        // Force the next save to fail: collide the .tmp write target with a directory.
        Directory.CreateDirectory(p + ".tmp");
        var ex = Record.Exception(() => store.SaveIfDirty());
        Assert.Null(ex);   // never throws -- Engine tick thread

        // The previous primary is untouched (still the pre-failure content).
        Assert.Equal(before, File.ReadAllText(p));
        Directory.Delete(p + ".tmp");

        // Dirty stays set -- a later successful save retries and lands the new data.
        store.SaveIfDirty();
        Assert.NotEqual(before, File.ReadAllText(p));
    }
}
