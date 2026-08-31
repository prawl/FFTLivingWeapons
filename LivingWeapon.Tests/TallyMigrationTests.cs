using System.Collections.Generic;
using System.IO;
using System.Linq;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-351: a weapon's DESIGN moved to a different item id (the Terrastaff off the Battle Axe's
/// slot and onto its own extended-inventory id), and the player's earned kills and deeds have to
/// move with it. These pin the whole rule, including the one case it must refuse.
///
/// Everything here drives the PURE policy (<see cref="TallyMigration"/>) over plain dictionaries,
/// plus one on-disk round trip for the .bak promise and one real <see cref="Engine"/> construction
/// for the wiring. No game, no memory fake needed: the migration reads meta and two maps.
/// </summary>
public class TallyMigrationTests
{
    private static WeaponMeta Meta(string name, int migratedFrom = 0) =>
        new() { Name = name, Cat = "Pole", Wp = 9, Formula = 1, Lane = "pa+ma", MigratedFrom = migratedFrom };

    /// <summary>The shipped shape: 262 says it came from 48, and 48 is no longer a living weapon
    /// (the restored Battle Axe is noGrowth, so the bake emits no entry for it).</summary>
    private static Dictionary<int, WeaponMeta> MovedTerrastaff() =>
        new() { [262] = Meta("Terrastaff", migratedFrom: 48), [108] = Meta("Ironreed Pole") };

    // ---- (a) the count moves, once ----

    [Fact]
    public void A_count_at_the_old_id_moves_to_the_new_id()
    {
        var plan = TallyMigration.Plan(MovedTerrastaff());
        Assert.Equal(new Dictionary<int, int> { [48] = 262 }, plan);

        var kills = new Dictionary<int, int> { [48] = 40, [108] = 3 };
        Assert.Equal(1, TallyMigration.MoveKills(plan, kills));

        Assert.False(kills.ContainsKey(48));
        Assert.Equal(40, kills[262]);
        Assert.Equal(3, kills[108]);   // an untouched weapon is untouched
    }

    // ---- (b) idempotent: the second load has nothing left to move ----

    [Fact]
    public void A_second_load_moves_nothing_and_changes_nothing()
    {
        var plan = TallyMigration.Plan(MovedTerrastaff());
        var kills = new Dictionary<int, int> { [48] = 40 };
        TallyMigration.MoveKills(plan, kills);

        Assert.Equal(0, TallyMigration.MoveKills(plan, kills));
        Assert.Equal(40, kills[262]);
        Assert.Single(kills);
    }

    // ---- (c) both ids carry a count: sum, never drop ----

    [Fact]
    public void Counts_on_both_ids_are_summed_onto_the_new_id()
    {
        var plan = TallyMigration.Plan(MovedTerrastaff());
        var kills = new Dictionary<int, int> { [48] = 40, [262] = 5 };

        Assert.Equal(1, TallyMigration.MoveKills(plan, kills));

        Assert.False(kills.ContainsKey(48));
        Assert.Equal(45, kills[262]);
    }

    // ---- (d) the persist keeps the previous generation as .bak ----

    [Fact]
    public void Persisting_the_migrated_tally_keeps_the_pre_migration_file_as_bak()
    {
        ModLogger.UseNullLogger();
        using var temp = TempDirs.Create("lw_migrate_");
        string path = Path.Combine(temp.Dir, "kills.json");
        File.WriteAllText(path, "{\"48\":40}");

        var tally = KillTally.Load(path);
        Assert.Equal(40, tally.Kills[48]);

        TallyMigration.MoveKills(TallyMigration.Plan(MovedTerrastaff()), tally.Kills);
        tally.Save();

        Assert.Equal("{\"262\":40}", File.ReadAllText(path));
        Assert.True(File.Exists(path + ".bak"), "the pre-migration tally must survive as .bak");
        Assert.Equal("{\"48\":40}", File.ReadAllText(path + ".bak"));
    }

    // ---- (e) THE NON-VACUOUS NEGATIVE. LW-361 redesigns the restored axes and flails, which
    // puts a living weapon back at id 48 while 262 still records that it came from there. The
    // old id then owns its own tally and the migration must keep its hands off it. A naive
    // "move everything any row names" loop passes every test above and fails this one. ----

    [Fact]
    public void An_old_id_that_is_a_living_weapon_again_keeps_its_own_count()
    {
        var meta = MovedTerrastaff();
        meta[48] = Meta("Battle Axe");   // LW-361: something living moved back into the slot

        var plan = TallyMigration.Plan(meta);
        Assert.Empty(plan);

        var kills = new Dictionary<int, int> { [48] = 40, [262] = 5 };
        Assert.Equal(0, TallyMigration.MoveKills(plan, kills));
        Assert.Equal(40, kills[48]);
        Assert.Equal(5, kills[262]);
    }

    // ---- (f) ORDER. The dev seed floors every known weapon; run BEFORE the migration it seeds
    // the dead old id and the move then sums that seed into the new one (a count the player
    // never earned). Run AFTER, the migrated total stands and the floor only ever raises. ----

    [Fact]
    public void The_dev_seed_must_run_after_the_migration_or_it_double_counts()
    {
        var plan = TallyMigration.Plan(MovedTerrastaff());
        var seedIds = new[] { 108, 262 };   // exactly meta.Keys: the restored 48 is not in the bake

        var after = new Dictionary<int, int> { [48] = 40 };
        TallyMigration.MoveKills(plan, after);
        Tuning.SeedKills(seedIds, after, 3);
        Assert.Equal(40, after[262]);   // the earned tally survives; the floor only raises

        var before = new Dictionary<int, int> { [48] = 40 };
        Tuning.SeedKills(new[] { 48, 108, 262 }, before, 3);   // the wrong order, with 48 still known
        TallyMigration.MoveKills(plan, before);
        Assert.Equal(43, before[262]);   // 3 the player never earned: this is what the order buys
    }

    /// <summary>The wiring: a real Engine construction must migrate what is on disk, before
    /// anything reads the tally. Proves the call sits between the tally load and the rest of the
    /// constructor, which no dictionary-level test can reach.</summary>
    [Fact]
    public void Engine_construction_migrates_the_tally_on_disk()
    {
        ModLogger.UseNullLogger();
        try
        {
            using var temp = TempDirs.Create("lw_migrate_engine_");
            string modDir = EngineTests.NestedModDir(temp);
            File.WriteAllText(Path.Combine(modDir, "kills.json"), "{\"48\":40}");
            File.WriteAllText(Path.Combine(modDir, "meta.json"),
                "{\"262\":{\"name\":\"Terrastaff\",\"wp\":9,\"cat\":\"Pole\",\"formula\":1,"
                + "\"lane\":\"pa+ma\",\"flavor\":\"f\",\"migratedFrom\":48}}");

            _ = new Engine(modDir, mem: EngineTests.HealthyMemory(), notice: (_, __) => { });

            string saved = Path.Combine(temp.Dir, "Reloaded-II", "User", "Mods", Mod.ModId, "kills.json");
            Assert.True(File.Exists(saved), $"no migrated tally at {saved}");
            Assert.Equal("{\"262\":40}", File.ReadAllText(saved));
        }
        finally { ModLogger.UseNullLogger(); }
    }

    // ---- deeds move on the same plan, and merge rather than overwrite ----

    [Fact]
    public void Deeds_move_to_the_new_id_and_merge_when_both_sides_have_a_record()
    {
        var plan = TallyMigration.Plan(MovedTerrastaff());

        var lone = new Dictionary<int, WeaponLegend> { [48] = Legend(counts: 4, mark: 1) };
        Assert.Equal(1, TallyMigration.MoveLegends(plan, lone));
        Assert.False(lone.ContainsKey(48));
        Assert.Equal(4, lone[262].Counts[1]);
        Assert.Equal(new List<int> { 1 }, lone[262].Marks);

        var both = new Dictionary<int, WeaponLegend>
        {
            [48] = Legend(counts: 4, mark: 1),
            [262] = Legend(counts: 2, mark: 2),
        };
        Assert.Equal(1, TallyMigration.MoveLegends(plan, both));
        Assert.Equal(6, both[262].Counts[1] + both[262].Counts[2]);
        Assert.Equal(new List<int> { 2, 1 }, both[262].Marks);   // destination's earn order first
        Assert.Equal(0, TallyMigration.MoveLegends(plan, both));   // idempotent, same as the tally
    }

    private static WeaponLegend Legend(int counts, int mark)
    {
        var w = new WeaponLegend();
        w.Counts[mark] = counts;
        w.Marks.Add(mark);
        return w;
    }

    // ---- (h) THE SHIPPED PAIRING. meta.json is baked from data/items.json; every design LW-351
    // moved must name its old id there, and no restored (noGrowth) id may still be a living weapon
    // in the bake, or the move for that pair silently becomes a no-op (rule (e) above). Reads the
    // TRACKED bake, so a regenerate that drops a migratedFrom goes red here, not in a save. ----

    [Fact]
    public void The_shipped_meta_names_all_seven_moves_and_leaves_no_old_id_living()
    {
        var meta = MetaLoader.Load(Path.Combine(RepoRoot(), "LivingWeapon"));
        var expected = new Dictionary<int, int>
        {
            [48] = 262, [49] = 263, [50] = 264, [67] = 265, [68] = 266, [69] = 267, [70] = 268,
        };
        Assert.Equal(expected, TallyMigration.Plan(meta));
        foreach (int old in expected.Keys) Assert.False(meta.ContainsKey(old), $"id {old} is still a living weapon in meta.json");
        Assert.Equal(new[] { "Terrastaff", "Ravager", "Sunderer", "Warbrand", "Bloodlash", "Climhazzard", "Sasori" },
            expected.Values.Select(id => meta[id].Name).ToArray());
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "docs", "TODO.md"))) dir = dir.Parent;
        return dir?.FullName ?? throw new System.InvalidOperationException("repo root not found above the test bin dir");
    }
}
