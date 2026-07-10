using System.Collections.Generic;
using System.IO;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// PlaythroughReset's stateful Observe: a sustained hold of the opening condition (FIX 1's
/// debounce) archives the previous kill tally and resets the shared instance, non-destructively.
/// LOAD-BEARING: a single qualifying tick must NEVER archive, only HoldTicks CONSECUTIVE
/// qualifying ticks do; a hold that breaks before the threshold resets the counter and never
/// fires. Mirrors KillTallyTests/SaveLocationTests' own-temp-dir idiom.
/// </summary>
public class PlaythroughResetTests
{
    // SaveLocation resolves SaveDir from modDir's GRANDPARENT (Directory.GetParent(modDir)?.Parent),
    // keyed on the real Mod.ModId, not on the temp folder's own random name. A bare random temp dir
    // used directly as modDir shares that grandparent (the OS temp root) across every test, so every
    // test would resolve to the SAME real SaveDir and collide. Mirrors SaveLocationTests' ModDirIn:
    // build root/Mods/<ModId> so each test's random ROOT keeps its resolved SaveDir isolated.
    private static string ModDir()
    {
        var root = Path.Combine(Path.GetTempPath(), "lw_pr_" + Path.GetRandomFileName());
        var dir = Path.Combine(root, "Mods", Mod.ModId);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static (SaveLocation save, KillTally tally) SeededTally(int weaponId = 1, int kills = 5)
    {
        var save = new SaveLocation(ModDir());
        var tally = KillTally.Load(save.PathFor("kills.json"));
        tally.Kills[weaponId] = kills;
        tally.Save();
        return (save, tally);
    }

    private static string ArchiveDir(SaveLocation save) => Path.Combine(save.SaveDir, "archive");

    // --- LOAD-BEARING: the debounce ---

    [Fact]
    public void A_single_transient_opening_tick_does_not_archive()
    {
        var (save, tally) = SeededTally();
        var reset = new PlaythroughReset(save, tally);

        reset.Observe(eventId: 2, battleMode: 0, inLive: false);   // exactly one qualifying tick

        Assert.True(File.Exists(save.PathFor("kills.json")));
        Assert.False(Directory.Exists(ArchiveDir(save)));
        Assert.Equal(5, tally.Kills[1]);   // untouched
    }

    [Fact]
    public void A_hold_that_breaks_before_the_threshold_resets_the_counter_and_never_fires()
    {
        var (save, tally) = SeededTally();
        var reset = new PlaythroughReset(save, tally);

        for (int i = 0; i < PlaythroughResetPolicy.HoldTicks - 1; i++)
            reset.Observe(2, 0, false);
        reset.Observe(5, 0, false);   // breaks the hold: not the opening event
        for (int i = 0; i < PlaythroughResetPolicy.HoldTicks - 1; i++)
            reset.Observe(2, 0, false);   // re-accumulates, one short of the threshold again

        Assert.False(Directory.Exists(ArchiveDir(save)));
        Assert.Equal(5, tally.Kills[1]);
    }

    [Fact]
    public void Sustained_hold_reaching_HoldTicks_archives_and_resets_the_tally()
    {
        var (save, tally) = SeededTally();
        Dictionary<int, int> shared = tally.Kills;   // capture the shared instance up front
        var reset = new PlaythroughReset(save, tally);

        for (int i = 0; i < PlaythroughResetPolicy.HoldTicks; i++)
            reset.Observe(2, 0, false);

        Assert.Same(shared, tally.Kills);   // by-ref preserved (mirrors KillTallyTests.cs:97)
        Assert.Empty(tally.Kills);

        string archived = Path.Combine(ArchiveDir(save), "kills.1.json");
        Assert.True(File.Exists(archived));
        Assert.Contains("\"1\":5", File.ReadAllText(archived));

        string live = save.PathFor("kills.json");
        Assert.True(File.Exists(live));
        Assert.Equal("{}", File.ReadAllText(live));
    }

    // NON-VACUITY NOTE (Phase 4 lever): removing the hold gate entirely (fire on the FIRST
    // qualifying tick, e.g. changing `_heldTicks == HoldTicks` to `_heldTicks >= 1`) makes
    // A_single_transient_opening_tick_does_not_archive red above.

    [Fact]
    public void Firing_only_at_the_exact_threshold_tick_not_on_every_tick_at_or_past_it()
    {
        // Distinguishes `== HoldTicks` from `>= HoldTicks`: after the first fire the tally is
        // empty, so ordinarily neither variant refires. Feeding fresh, non-empty data back in
        // WHILE the held counter is still sitting past the threshold exposes the difference --
        // `>=` would refire on the very next qualifying tick; `== ` must not, because the counter
        // only ever equals HoldTicks once per climb (it must drop and climb back from scratch).
        var (save, tally) = SeededTally();
        var reset = new PlaythroughReset(save, tally);

        for (int i = 0; i < PlaythroughResetPolicy.HoldTicks; i++)
            reset.Observe(2, 0, false);   // fires once, archives to kills.1.json

        tally.Kills[2] = 1;               // simulate fresh data while the held signal is still true
        reset.Observe(2, 0, false);       // heldTicks is now HoldTicks + 1

        Assert.False(File.Exists(Path.Combine(ArchiveDir(save), "kills.2.json")));
        Assert.Equal(1, tally.Kills[2]);
    }

    [Fact]
    public void Continuing_to_hold_past_the_threshold_never_refires_because_the_tally_is_now_empty()
    {
        var (save, tally) = SeededTally();
        var reset = new PlaythroughReset(save, tally);

        for (int i = 0; i < PlaythroughResetPolicy.HoldTicks + 20; i++)
            reset.Observe(2, 0, false);

        Assert.Single(Directory.GetFiles(ArchiveDir(save), "kills.*.json"));
    }

    // --- non-fire gates ---

    [Fact]
    public void Reaching_the_threshold_with_an_empty_tally_does_not_archive()
    {
        var save = new SaveLocation(ModDir());
        var tally = KillTally.Load(save.PathFor("kills.json"));   // nothing seeded/saved
        var reset = new PlaythroughReset(save, tally);

        for (int i = 0; i < PlaythroughResetPolicy.HoldTicks; i++)
            reset.Observe(2, 0, false);

        Assert.False(Directory.Exists(ArchiveDir(save)));
    }

    [Fact]
    public void Sustained_hold_while_inLive_never_fires()
    {
        var (save, tally) = SeededTally();
        var reset = new PlaythroughReset(save, tally);

        for (int i = 0; i < PlaythroughResetPolicy.HoldTicks + 5; i++)
            reset.Observe(eventId: 2, battleMode: 0, inLive: true);

        Assert.False(Directory.Exists(ArchiveDir(save)));
        Assert.Equal(5, tally.Kills[1]);
    }

    [Fact]
    public void Sustained_hold_at_a_non_opening_event_never_fires()
    {
        var (save, tally) = SeededTally();
        var reset = new PlaythroughReset(save, tally);

        for (int i = 0; i < PlaythroughResetPolicy.HoldTicks + 5; i++)
            reset.Observe(eventId: 5, battleMode: 0, inLive: false);

        Assert.False(Directory.Exists(ArchiveDir(save)));
        Assert.Equal(5, tally.Kills[1]);
    }

    [Fact]
    public void Sustained_hold_in_battle_never_fires()
    {
        var (save, tally) = SeededTally();
        var reset = new PlaythroughReset(save, tally);

        for (int i = 0; i < PlaythroughResetPolicy.HoldTicks + 5; i++)
            reset.Observe(eventId: 2, battleMode: 3, inLive: true);

        Assert.False(Directory.Exists(ArchiveDir(save)));
        Assert.Equal(5, tally.Kills[1]);
    }
}
