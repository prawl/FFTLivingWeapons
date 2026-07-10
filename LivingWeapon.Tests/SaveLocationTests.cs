using System.Collections.Generic;
using System.IO;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-51: SaveLocation resolves the update-safe save directory (Reloaded/User/Mods/ModId) and
/// runs a one-time, strictly non-destructive migration of a legacy file out of the deploy mod
/// dir. Every test builds its own random-rooted temp tree (mirrors KillTallyTests/LegendStoreTests)
/// so the resolved dir (which keys on the real Mod.ModId, not on any test-chosen folder name)
/// never collides across parallel runs.
/// </summary>
public class SaveLocationTests
{
    private static string TempRoot()
    {
        var d = Path.Combine(Path.GetTempPath(), "lw_saveloc_" + Path.GetRandomFileName());
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>Builds root/Mods/&lt;ModId&gt;, the same two-levels-under-the-Reloaded-root shape
    /// ResolveConfigPath (Mod.cs) already assumes for the deploy mod dir.</summary>
    private static string ModDirIn(string root)
    {
        var dir = Path.Combine(root, "Mods", Mod.ModId);
        Directory.CreateDirectory(dir);
        return dir;
    }

    // (e) ResolveSaveDir: the normal case, plus the fail-soft case.

    [Fact]
    public void SaveDir_resolves_to_the_update_safe_dir_and_creates_it()
    {
        var root = TempRoot();
        var modDir = ModDirIn(root);

        var save = new SaveLocation(modDir);

        string expected = Path.Combine(root, "User", "Mods", Mod.ModId);
        Assert.Equal(expected, save.SaveDir);
        Assert.True(Directory.Exists(save.SaveDir));
    }

    [Fact]
    public void SaveDir_fails_soft_to_modDir_when_there_is_no_grandparent()
    {
        // A drive root has no parent (Directory.GetParent returns null): the resolver must not
        // throw, it must simply hand back modDir unchanged.
        string driveRoot = Path.GetPathRoot(Path.GetTempPath())!;

        var save = new SaveLocation(driveRoot);

        Assert.Equal(driveRoot, save.SaveDir);
    }

    // (a) POSITIVE migration.

    [Fact]
    public void Migrate_copies_a_legacy_file_into_the_save_dir_when_the_dest_is_absent()
    {
        var root = TempRoot();
        var modDir = ModDirIn(root);
        string legacyPath = Path.Combine(modDir, "kills.json");
        File.WriteAllText(legacyPath, "{\"1\":5}");
        var save = new SaveLocation(modDir);

        string dest = save.Migrate("kills.json");

        Assert.Equal(Path.Combine(save.SaveDir, "kills.json"), dest);
        Assert.True(File.Exists(dest));
        Assert.Equal("{\"1\":5}", File.ReadAllText(dest));
        Assert.Equal("{\"1\":5}", File.ReadAllText(legacyPath));   // legacy untouched (copy, not move)

        var tally = KillTally.Load(dest);
        Assert.Equal(5, tally.Kills[1]);
    }

    // (b) LOAD-BEARING NEGATIVE: the non-destructive safety property.
    //
    // NON-VACUITY NOTE (for the Phase 4 adversarial review, not applied here): the correct
    // mutation lever for this test is rewriting AtomicCopyIfAbsent into an UNCONDITIONAL,
    // overwriting copy (File.Copy(src, dest, overwrite: true), the !File.Exists(dest) guard
    // removed entirely); that mutation makes dest read back the legacy content and this test
    // reds. Removing ONLY the !File.Exists(dest) guard (leaving the tmp-then-File.Move(...,
    // overwrite:false) dance intact) is NOT a valid lever: File.Move would throw on the
    // already-existing dest, Migrate's own try/catch (it must never throw out of Engine's ctor)
    // swallows that, and dest is left untouched, so that narrower mutation stays GREEN and
    // would make this test vacuous.
    [Fact]
    public void Migrate_never_overwrites_a_pre_existing_dest()
    {
        var root = TempRoot();
        var modDir = ModDirIn(root);
        var save = new SaveLocation(modDir);   // creates SaveDir as a side effect
        string dest = save.PathFor("kills.json");
        File.WriteAllText(dest, "{\"2\":9}");                          // pre-existing dest, different content
        string legacyPath = Path.Combine(modDir, "kills.json");
        File.WriteAllText(legacyPath, "{\"1\":5}");                    // legacy also present

        string result = save.Migrate("kills.json");

        Assert.Equal(dest, result);
        Assert.Equal("{\"2\":9}", File.ReadAllText(dest));             // byte-identical to its original content
        Assert.Equal("{\"1\":5}", File.ReadAllText(legacyPath));       // legacy also unchanged
    }

    // (c) The .bak guard is independent of the primary: an orphan .bak (primary legacy absent)
    // still migrates on its own.

    [Fact]
    public void Migrate_moves_an_orphan_bak_even_when_the_legacy_primary_is_absent()
    {
        var root = TempRoot();
        var modDir = ModDirIn(root);
        File.WriteAllText(Path.Combine(modDir, "kills.json.bak"), "{\"3\":2}");
        // No legacy primary at all.
        var save = new SaveLocation(modDir);

        string dest = save.Migrate("kills.json");

        Assert.False(File.Exists(dest));                 // nothing to migrate for the primary
        Assert.True(File.Exists(dest + ".bak"));
        Assert.Equal("{\"3\":2}", File.ReadAllText(dest + ".bak"));

        var tally = KillTally.Load(dest);                // KillTally.cs falls back to .bak alone
        Assert.Equal(2, tally.Kills[3]);
    }

    // (d) IDEMPOTENT: a second call is a no-op.

    [Fact]
    public void Migrate_a_second_time_is_a_no_op()
    {
        var root = TempRoot();
        var modDir = ModDirIn(root);
        string legacyPath = Path.Combine(modDir, "kills.json");
        File.WriteAllText(legacyPath, "{\"1\":5}");
        var save = new SaveLocation(modDir);

        string dest1 = save.Migrate("kills.json");
        File.WriteAllText(legacyPath, "{\"1\":999}");     // legacy changes after the first migration
        string dest2 = save.Migrate("kills.json");

        Assert.Equal(dest1, dest2);
        Assert.Equal("{\"1\":5}", File.ReadAllText(dest2));   // second call did not re-copy
    }

    // (f) ATOMIC self-heal: a stale .migrating.tmp from an interrupted prior copy must not block
    // or corrupt a fresh migration, and must not survive a successful one.

    [Fact]
    public void Migrate_self_heals_past_a_stale_migrating_tmp()
    {
        var root = TempRoot();
        var modDir = ModDirIn(root);
        string legacyPath = Path.Combine(modDir, "kills.json");
        File.WriteAllText(legacyPath, "{\"1\":5}");
        var save = new SaveLocation(modDir);
        string dest = save.PathFor("kills.json");
        File.WriteAllText(dest + ".migrating.tmp", "GARBAGE LEFT BY AN INTERRUPTED COPY");

        string result = save.Migrate("kills.json");

        Assert.Equal(dest, result);
        Assert.True(File.Exists(dest));
        Assert.Equal("{\"1\":5}", File.ReadAllText(dest));
        Assert.False(File.Exists(dest + ".migrating.tmp"));
    }

    // (g) BY-REFERENCE invariant: migrate-then-load still hands back the one shared mutable
    // dictionary every subsystem credits into (mirrors KillTallyTests.cs:97-105).

    [Fact]
    public void Kills_is_still_the_same_mutable_instance_after_migrate_then_load()
    {
        var root = TempRoot();
        var modDir = ModDirIn(root);
        File.WriteAllText(Path.Combine(modDir, "kills.json"), "{\"1\":5}");
        var save = new SaveLocation(modDir);

        var tally = KillTally.Load(save.Migrate("kills.json"));
        Dictionary<int, int> shared = tally.Kills;
        shared[9] = 42;                        // a subsystem credits a kill

        Assert.Equal(42, tally.Kills[9]);       // the tally sees it (same instance, by design)
        Assert.Equal(5, tally.Kills[1]);        // the migrated content is present too
    }

    // (h) REGRESSION: KillTally / LegendStore / GunSlinger keep their own APIs and persistence
    // contracts unchanged; only the directory Engine now hands them (save.SaveDir instead of
    // modDir) moved. This wires all three through SaveLocation exactly as Engine.cs does.
    // Whole-suite non-regression is additionally confirmed by dotnet test's before/after pass
    // counts (unchanged suite count plus these new tests, zero failures).

    [Fact]
    public void Stores_still_load_and_save_correctly_through_the_relocated_save_dir()
    {
        var root = TempRoot();
        var modDir = ModDirIn(root);
        File.WriteAllText(Path.Combine(modDir, "kills.json"), "{\"1\":3}");
        File.WriteAllText(Path.Combine(modDir, "gunslinger.json"),
            "{\"1\":{\"hasOff\":true,\"origOff\":100,\"hasSupp\":true,\"origSupp\":213}}");
        var save = new SaveLocation(modDir);

        var tally = KillTally.Load(save.Migrate("kills.json"));
        Assert.Equal(3, tally.Kills[1]);

        save.Migrate("legends.json");
        var legends = LegendStore.Load(save.SaveDir);
        Assert.False(legends.Has(1));   // fresh: no legacy legends.json existed

        save.Migrate("gunslinger.json");
        var gunSlinger = new GunSlinger(new Dictionary<int, WeaponMeta>(), tally.Kills, save.SaveDir, new FakeSparseMemory());
        var snap = gunSlinger.StoreForTest().Get(1);
        Assert.True(snap.HasOff);
        Assert.Equal(100, snap.OrigOff);
    }

    // --- LW-51 Tier-1: SaveLocation.Archive (strictly non-destructive) ---

    private static string ArchiveDirOf(SaveLocation save) => Path.Combine(save.SaveDir, "archive");

    [Fact]
    public void Archive_moves_the_primary_and_its_bak_to_archive_using_the_same_index()
    {
        var root = TempRoot();
        var modDir = ModDirIn(root);
        var save = new SaveLocation(modDir);
        string src = save.PathFor("kills.json");
        File.WriteAllText(src, "{\"9\":1}");
        File.WriteAllText(src + ".bak", "{\"9\":0}");

        save.Archive("kills.json");

        Assert.False(File.Exists(src));
        Assert.False(File.Exists(src + ".bak"));
        string archiveDir = ArchiveDirOf(save);
        Assert.Equal("{\"9\":1}", File.ReadAllText(Path.Combine(archiveDir, "kills.1.json")));
        Assert.Equal("{\"9\":0}", File.ReadAllText(Path.Combine(archiveDir, "kills.1.json.bak")));
    }

    [Fact]
    public void A_second_archive_lands_at_the_next_index_leaving_the_first_byte_unchanged()
    {
        var root = TempRoot();
        var modDir = ModDirIn(root);
        var save = new SaveLocation(modDir);
        string src = save.PathFor("kills.json");
        File.WriteAllText(src, "{\"1\":1}");
        save.Archive("kills.json");

        File.WriteAllText(src, "{\"1\":2}");   // a fresh kills.json after the reset
        save.Archive("kills.json");

        string archiveDir = ArchiveDirOf(save);
        Assert.Equal("{\"1\":1}", File.ReadAllText(Path.Combine(archiveDir, "kills.1.json")));   // untouched
        Assert.Equal("{\"1\":2}", File.ReadAllText(Path.Combine(archiveDir, "kills.2.json")));
    }

    // NON-VACUITY NOTE: the lever that exposes a File.Move(..., overwrite: true) regression on
    // the archive destination is this collision test, not the "second archive" test above (which
    // never collides under a correct N = max+1 scan).
    [Fact]
    public void Archive_never_overwrites_a_preexisting_archived_bak_the_index_scan_cannot_see()
    {
        // An orphan archived .bak with no matching primary (e.g. left over from some earlier,
        // partially-completed archive attempt): kills.1.json.bak exists but kills.1.json does
        // not, so the primary-only "kills.*.json" index scan is blind to it and computes N=1
        // again. The primary move then succeeds (no collision there), but the .bak move MUST
        // throw on the pre-existing destination rather than clobber it.
        var root = TempRoot();
        var modDir = ModDirIn(root);
        var save = new SaveLocation(modDir);
        string archiveDir = ArchiveDirOf(save);
        Directory.CreateDirectory(archiveDir);
        File.WriteAllText(Path.Combine(archiveDir, "kills.1.json.bak"), "PRE-EXISTING ARCHIVED BACKUP");
        string src = save.PathFor("kills.json");
        File.WriteAllText(src, "{\"9\":1}");
        File.WriteAllText(src + ".bak", "{\"9\":0}");

        save.Archive("kills.json");

        Assert.Equal("{\"9\":1}", File.ReadAllText(Path.Combine(archiveDir, "kills.1.json")));
        Assert.Equal("PRE-EXISTING ARCHIVED BACKUP", File.ReadAllText(Path.Combine(archiveDir, "kills.1.json.bak")));
        // The source .bak move threw (destination collision) before it could move: untouched.
        Assert.True(File.Exists(src + ".bak"));
        Assert.Equal("{\"9\":0}", File.ReadAllText(src + ".bak"));
    }

    [Fact]
    public void Archive_fails_closed_when_an_existing_archived_name_is_unparseable()
    {
        var root = TempRoot();
        var modDir = ModDirIn(root);
        var save = new SaveLocation(modDir);
        string archiveDir = ArchiveDirOf(save);
        Directory.CreateDirectory(archiveDir);
        File.WriteAllText(Path.Combine(archiveDir, "kills.garbage.json"), "UNPARSEABLE INDEX");
        string src = save.PathFor("kills.json");
        File.WriteAllText(src, "{\"9\":1}");

        save.Archive("kills.json");

        // Fail-closed: no fall back to N=0/1, nothing moves rather than risk a misnumbered
        // archive, so the source is left exactly as it was.
        Assert.True(File.Exists(src));
        Assert.Equal("{\"9\":1}", File.ReadAllText(src));
        Assert.False(File.Exists(Path.Combine(archiveDir, "kills.1.json")));
        Assert.False(File.Exists(Path.Combine(archiveDir, "kills.0.json")));
    }

    [Fact]
    public void Archive_is_a_no_op_when_the_source_file_is_absent()
    {
        var root = TempRoot();
        var modDir = ModDirIn(root);
        var save = new SaveLocation(modDir);

        save.Archive("kills.json");   // nothing on disk at all: must not throw or create anything

        Assert.False(Directory.Exists(ArchiveDirOf(save)));
    }

    [Fact]
    public void Archive_targets_the_exact_path_KillTally_Save_persists_to()
    {
        var root = TempRoot();
        var modDir = ModDirIn(root);
        var save = new SaveLocation(modDir);
        var tally = KillTally.Load(save.Migrate("kills.json"));
        tally.Kills[1] = 5;
        tally.Save();   // persists to save.PathFor("kills.json"), the coupling under test

        save.Archive("kills.json");

        Assert.True(File.Exists(Path.Combine(ArchiveDirOf(save), "kills.1.json")));
        Assert.False(File.Exists(save.PathFor("kills.json")));
    }
}
