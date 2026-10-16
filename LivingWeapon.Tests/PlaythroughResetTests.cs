using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// PlaythroughReset's stateful Observe: a sustained hold of the opening condition (FIX 1's
/// debounce) archives the previous kill tally and resets the shared instance, non-destructively.
/// LOAD-BEARING: a single qualifying tick must NEVER archive, only HoldTicks CONSECUTIVE
/// qualifying ticks do; a hold that breaks before the threshold resets the counter and never
/// fires. Mirrors KillTallyTests/SaveLocationTests' own-temp-dir idiom. LW-147: ModDir()'s
/// backing temp root is tracked and deleted in Dispose, not leaked.
/// </summary>
public class PlaythroughResetTests : IDisposable
{
    private readonly List<TempDirs> _tempDirs = new();

    public void Dispose()
    {
        foreach (var t in _tempDirs) t.Dispose();
    }

    // SaveLocation resolves SaveDir from modDir's GRANDPARENT (Directory.GetParent(modDir)?.Parent),
    // keyed on the real Mod.ModId, not on the temp folder's own random name. A bare random temp dir
    // used directly as modDir shares that grandparent (the OS temp root) across every test, so every
    // test would resolve to the SAME real SaveDir and collide. Mirrors SaveLocationTests' ModDirIn:
    // build root/Mods/<ModId> so each test's random ROOT keeps its resolved SaveDir isolated.
    private string ModDir()
    {
        var t = TempDirs.Create("lw_pr_");
        _tempDirs.Add(t);
        var dir = Path.Combine(t.Dir, "Mods", Mod.ModId);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private (SaveLocation save, KillTally tally) SeededTally(int weaponId = 1, int kills = 5)
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

        reset.Observe(eventId: 2, battleMode: 0, inLive: false, sensingArmed: true);   // exactly one qualifying tick

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
            reset.Observe(2, 0, false, sensingArmed: true);
        reset.Observe(5, 0, false, sensingArmed: true);   // breaks the hold: not the opening event
        for (int i = 0; i < PlaythroughResetPolicy.HoldTicks - 1; i++)
            reset.Observe(2, 0, false, sensingArmed: true);   // re-accumulates, one short of the threshold again

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
            reset.Observe(2, 0, false, sensingArmed: true);

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
            reset.Observe(2, 0, false, sensingArmed: true);   // fires once, archives to kills.1.json

        tally.Kills[2] = 1;               // simulate fresh data while the held signal is still true
        reset.Observe(2, 0, false, sensingArmed: true);       // heldTicks is now HoldTicks + 1

        Assert.False(File.Exists(Path.Combine(ArchiveDir(save), "kills.2.json")));
        Assert.Equal(1, tally.Kills[2]);
    }

    [Fact]
    public void Continuing_to_hold_past_the_threshold_never_refires_because_the_tally_is_now_empty()
    {
        var (save, tally) = SeededTally();
        var reset = new PlaythroughReset(save, tally);

        for (int i = 0; i < PlaythroughResetPolicy.HoldTicks + 20; i++)
            reset.Observe(2, 0, false, sensingArmed: true);

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
            reset.Observe(2, 0, false, sensingArmed: true);

        Assert.False(Directory.Exists(ArchiveDir(save)));
    }

    [Fact]
    public void Sustained_hold_while_inLive_never_fires()
    {
        var (save, tally) = SeededTally();
        var reset = new PlaythroughReset(save, tally);

        for (int i = 0; i < PlaythroughResetPolicy.HoldTicks + 5; i++)
            reset.Observe(eventId: 2, battleMode: 0, inLive: true, sensingArmed: true);

        Assert.False(Directory.Exists(ArchiveDir(save)));
        Assert.Equal(5, tally.Kills[1]);
    }

    [Fact]
    public void Sustained_hold_at_a_non_opening_event_never_fires()
    {
        var (save, tally) = SeededTally();
        var reset = new PlaythroughReset(save, tally);

        for (int i = 0; i < PlaythroughResetPolicy.HoldTicks + 5; i++)
            reset.Observe(eventId: 5, battleMode: 0, inLive: false, sensingArmed: true);

        Assert.False(Directory.Exists(ArchiveDir(save)));
        Assert.Equal(5, tally.Kills[1]);
    }

    [Fact]
    public void Sustained_hold_in_battle_never_fires()
    {
        var (save, tally) = SeededTally();
        var reset = new PlaythroughReset(save, tally);

        for (int i = 0; i < PlaythroughResetPolicy.HoldTicks + 5; i++)
            reset.Observe(eventId: 2, battleMode: 3, inLive: true, sensingArmed: true);

        Assert.False(Directory.Exists(ArchiveDir(save)));
        Assert.Equal(5, tally.Kills[1]);
    }

    // --- LW-56: Observe's return value is the "a new game just qualified" detection edge,
    // consumed by Engine to force BattleState's exit. Deliberately decoupled from the archive
    // action's own non-empty-tally gate below: a zero-credit battle followed by a new game still
    // needs the forced exit (state hygiene), even though there is nothing to archive. ---

    [Fact]
    public void Observe_returns_true_only_on_the_exact_HoldTicks_th_qualifying_tick()
    {
        var (save, tally) = SeededTally();
        var reset = new PlaythroughReset(save, tally);

        for (int i = 0; i < PlaythroughResetPolicy.HoldTicks; i++)
        {
            bool fired = reset.Observe(2, 0, false, sensingArmed: true) == ResetVerdict.Fired;
            bool expectedFinalTick = i == PlaythroughResetPolicy.HoldTicks - 1;
            Assert.Equal(expectedFinalTick, fired);
        }
    }

    [Fact]
    public void Observe_returns_false_the_tick_after_the_threshold_even_while_still_held()
    {
        var (save, tally) = SeededTally();
        var reset = new PlaythroughReset(save, tally);

        for (int i = 0; i < PlaythroughResetPolicy.HoldTicks; i++) reset.Observe(2, 0, false, sensingArmed: true);
        bool firedAgain = reset.Observe(2, 0, false, sensingArmed: true) == ResetVerdict.Fired;   // held past the threshold

        Assert.False(firedAgain);
    }

    [Fact]
    public void Observe_returns_true_again_only_after_the_hold_breaks_and_reclimbs()
    {
        var (save, tally) = SeededTally();
        var reset = new PlaythroughReset(save, tally);

        for (int i = 0; i < PlaythroughResetPolicy.HoldTicks; i++) reset.Observe(2, 0, false, sensingArmed: true);
        Assert.NotEqual(ResetVerdict.Fired, reset.Observe(5, 0, false, sensingArmed: true));   // breaks the hold: not the opening event

        bool fired = false;
        for (int i = 0; i < PlaythroughResetPolicy.HoldTicks; i++) fired = reset.Observe(2, 0, false, sensingArmed: true) == ResetVerdict.Fired;
        Assert.True(fired);
    }

    [Fact]
    public void Empty_tally_at_the_threshold_still_returns_true_although_the_archive_action_does_not_run()
    {
        // The decoupling's load-bearing test: the detection edge fires (Engine needs it to force
        // BattleState's exit) even though the archive/clear action stays gated on Kills.Count > 0.
        var save = new SaveLocation(ModDir());
        var tally = KillTally.Load(save.PathFor("kills.json"));   // nothing seeded/saved
        var reset = new PlaythroughReset(save, tally);

        bool fired = false;
        for (int i = 0; i < PlaythroughResetPolicy.HoldTicks; i++)
            fired = reset.Observe(2, 0, false, sensingArmed: true) == ResetVerdict.Fired;

        Assert.True(fired);
        Assert.False(Directory.Exists(ArchiveDir(save)));         // no archive call
        Assert.False(File.Exists(save.PathFor("kills.json")));    // no save call either
    }

    // --- LW-395: the sentinel-verification gate. The reset may only clear kills.json once the
    // sensing lane (LaunchGuard.Sensing.cs) has verified the battle sentinels this launch; a
    // sustained "new game" signal without that verification is REFUSED (data untouched). ---

    [Fact]
    public void Sensing_not_armed_refuses_the_reset_at_the_threshold()
    {
        // LOAD-BEARING (LW-395). NON-VACUITY LEVER: in PlaythroughReset.Observe, change the
        // threshold branch's `if (sensingArmed)` to `if (sensingArmed || true)` (a constant `true`
        // alone does not compile: TreatWarningsAsErrors turns the dead else path into CS0162) and
        // this test goes red (the HoldTicks-th call returns Fired and archives/clears). The reverse
        // lever, turning that term into `!sensingArmed`, turns the sensingArmed: true tests in this
        // file red.
        var (save, tally) = SeededTally();
        string before = File.ReadAllText(save.PathFor("kills.json"));
        var reset = new PlaythroughReset(save, tally);

        int refused = 0, fired = 0;
        for (int i = 0; i < PlaythroughResetPolicy.HoldTicks + 5; i++)
        {
            var v = reset.Observe(2, 0, false, sensingArmed: false);
            if (v == ResetVerdict.Refused) refused++;
            if (v == ResetVerdict.Fired) fired++;
        }

        Assert.False(Directory.Exists(ArchiveDir(save)));
        Assert.Equal(before, File.ReadAllText(save.PathFor("kills.json")));
        Assert.Equal(5, tally.Kills[1]);
        Assert.Equal(1, refused);
        Assert.Equal(0, fired);
    }

    [Fact]
    public void Sensing_not_armed_with_an_empty_tally_still_returns_Refused_and_archives_nothing()
    {
        // The refusal is about the SIGNAL, not the data, so it reports (and Engine runs its
        // hygiene) even with nothing to protect, exactly like the pre-LW-395 empty-tally edge.
        var save = new SaveLocation(ModDir());
        var tally = KillTally.Load(save.PathFor("kills.json"));   // nothing seeded/saved
        var reset = new PlaythroughReset(save, tally);

        var last = ResetVerdict.None;
        for (int i = 0; i < PlaythroughResetPolicy.HoldTicks; i++)
            last = reset.Observe(2, 0, false, sensingArmed: false);

        Assert.Equal(ResetVerdict.Refused, last);
        Assert.False(Directory.Exists(ArchiveDir(save)));
        Assert.False(File.Exists(save.PathFor("kills.json")));
    }

    [Fact]
    public void Sensing_arming_before_the_threshold_fires_on_the_threshold_tick()
    {
        var (save, tally) = SeededTally();
        var reset = new PlaythroughReset(save, tally);

        for (int i = 0; i < 10; i++)
            Assert.Equal(ResetVerdict.None, reset.Observe(2, 0, false, sensingArmed: false));
        var last = ResetVerdict.None;
        for (int i = 0; i < 20; i++)
            last = reset.Observe(2, 0, false, sensingArmed: true);   // the 30th held tick overall

        Assert.Equal(PlaythroughResetPolicy.HoldTicks, 30);   // the arithmetic above assumes 30
        Assert.Equal(ResetVerdict.Fired, last);
        Assert.True(File.Exists(Path.Combine(ArchiveDir(save), "kills.1.json")));
        Assert.Empty(tally.Kills);
    }

    [Fact]
    public void Sensing_arming_after_the_threshold_fires_once_on_the_first_armed_held_tick()
    {
        // The late-arm latch (LW-395 review A2): the lane can arm AFTER the hold already crossed
        // the threshold and was refused. The still-held signal then fires exactly once.
        var (save, tally) = SeededTally();
        var reset = new PlaythroughReset(save, tally);

        var last = ResetVerdict.None;
        for (int i = 0; i < PlaythroughResetPolicy.HoldTicks; i++)
            last = reset.Observe(2, 0, false, sensingArmed: false);
        Assert.Equal(ResetVerdict.Refused, last);
        Assert.False(Directory.Exists(ArchiveDir(save)));

        Assert.Equal(ResetVerdict.Fired, reset.Observe(2, 0, false, sensingArmed: true));
        Assert.True(File.Exists(Path.Combine(ArchiveDir(save), "kills.1.json")));
        Assert.Empty(tally.Kills);

        for (int i = 0; i < 5; i++)
            Assert.Equal(ResetVerdict.None, reset.Observe(2, 0, false, sensingArmed: true));
        Assert.Single(Directory.GetFiles(ArchiveDir(save), "kills.*.json"));
    }

    [Fact]
    public void Late_arm_latch_is_cleared_by_a_hold_break()
    {
        // A refused hold that BREAKS must not leave a latch behind: a later armed tick of a
        // fresh, still-short climb is not a new game.
        var (save, tally) = SeededTally();
        var reset = new PlaythroughReset(save, tally);

        for (int i = 0; i < PlaythroughResetPolicy.HoldTicks; i++)
            reset.Observe(2, 0, false, sensingArmed: false);   // Refused on the last, latch set
        reset.Observe(5, 0, false, sensingArmed: false);       // the hold breaks

        for (int i = 0; i < 3; i++)
            Assert.Equal(ResetVerdict.None, reset.Observe(2, 0, false, sensingArmed: true));
        Assert.False(Directory.Exists(ArchiveDir(save)));
        Assert.Equal(5, tally.Kills[1]);
    }

    [Fact]
    public void Late_arm_after_a_fire_and_a_break_fires_again_with_an_empty_tally_and_archives_nothing()
    {
        // Fired on an EMPTY tally is correct: the verdict is about the SIGNAL (a new game just
        // qualified), not the data. RunDataAction no-ops when the tally is empty, so nothing is
        // archived or rewritten, but Engine still needs the edge to run its battle-state hygiene.
        var (save, tally) = SeededTally();
        var reset = new PlaythroughReset(save, tally);

        var last = ResetVerdict.None;
        for (int i = 0; i < PlaythroughResetPolicy.HoldTicks; i++)
            last = reset.Observe(2, 0, false, sensingArmed: true);
        Assert.Equal(ResetVerdict.Fired, last);
        Assert.True(File.Exists(Path.Combine(ArchiveDir(save), "kills.1.json")));
        Assert.Equal("{}", File.ReadAllText(save.PathFor("kills.json")).Trim());

        for (int i = 0; i < 5; i++)
            Assert.Equal(ResetVerdict.None, reset.Observe(2, 0, false, sensingArmed: true));
        Assert.Single(Directory.GetFiles(ArchiveDir(save), "kills.*.json"));

        reset.Observe(5, 0, false, sensingArmed: true);   // break the hold

        for (int i = 0; i < PlaythroughResetPolicy.HoldTicks; i++)
            last = reset.Observe(2, 0, false, sensingArmed: false);
        Assert.Equal(ResetVerdict.Refused, last);
        Assert.Single(Directory.GetFiles(ArchiveDir(save), "kills.*.json"));

        Assert.Equal(ResetVerdict.Fired, reset.Observe(2, 0, false, sensingArmed: true));
        Assert.False(File.Exists(Path.Combine(ArchiveDir(save), "kills.2.json")));
        Assert.Equal("{}", File.ReadAllText(save.PathFor("kills.json")).Trim());
    }

    [Fact]
    public void A_second_refused_hold_after_a_break_refuses_again_and_a_later_arm_fires_it()
    {
        var (save, tally) = SeededTally();
        var reset = new PlaythroughReset(save, tally);

        for (int i = 0; i < PlaythroughResetPolicy.HoldTicks; i++)
            reset.Observe(2, 0, false, sensingArmed: false);
        reset.Observe(5, 0, false, sensingArmed: false);   // break
        var last = ResetVerdict.None;
        for (int i = 0; i < PlaythroughResetPolicy.HoldTicks; i++)
            last = reset.Observe(2, 0, false, sensingArmed: false);
        Assert.Equal(ResetVerdict.Refused, last);
        Assert.False(Directory.Exists(ArchiveDir(save)));

        Assert.Equal(ResetVerdict.Fired, reset.Observe(2, 0, false, sensingArmed: true));
        Assert.True(File.Exists(Path.Combine(ArchiveDir(save), "kills.1.json")));
        Assert.False(File.Exists(Path.Combine(ArchiveDir(save), "kills.2.json")));
        Assert.Equal("{}", File.ReadAllText(save.PathFor("kills.json")).Trim());
    }

    [Fact]
    public void Refusal_logs_exactly_once_per_hold()
    {
        using var cap = LogCapture.Start();
        var (save, tally) = SeededTally();
        var reset = new PlaythroughReset(save, tally);

        for (int i = 0; i < PlaythroughResetPolicy.HoldTicks + 20; i++)
            reset.Observe(2, 0, false, sensingArmed: false);
        Assert.Equal(1, cap.File.Count(l => l.Contains("battle sentinel check has not passed")));

        reset.Observe(5, 0, false, sensingArmed: false);   // break and reclimb
        for (int i = 0; i < PlaythroughResetPolicy.HoldTicks + 20; i++)
            reset.Observe(2, 0, false, sensingArmed: false);
        Assert.Equal(2, cap.File.Count(l => l.Contains("battle sentinel check has not passed")));
        Assert.DoesNotContain(cap.File, l => l.Contains("A new game was detected"));
        Assert.DoesNotContain(cap.File, l => l.Contains("standing down to protect your save"));
    }
}
