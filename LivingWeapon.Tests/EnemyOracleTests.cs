using System.Collections.Generic;
using System.Reflection;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-34: the once-per-battle "All N enemies are accounted for" line must count enemies actually
/// FIELDED this battle, not every phantom identity the encounter's static array defines. These
/// tests lock in the fix: EnemyOracle gains two additive evidence sets (MarkFielded, MarkDead);
/// CheckCoverage counts only evidenced identities and latches only on two consecutive agreeing
/// passes; KillTracker.Corpses.cs feeds evidence from its existing band walk (CT slam or turn-flag
/// participation, 3-tick debounced, real-position filtered) plus the dead-edge stamp.
///
/// Mirrors KillTrackerTests' fixture idiom (FakeSparseMemory dictionary fakes, MemSeats seating
/// helpers). ModLogger defaults to NullLogger assembly-wide (TestLoggingSetup); CaptureLog swaps
/// in an injected-sink FileConsoleLogger for the duration of one call, mirroring LoggerTests.cs.
/// </summary>
public class EnemyOracleTests
{
    // Mirrors EnemyOracle's private CoverageInterval (150 ticks, about 5s at 33ms): not exposed
    // test-side, so tests that need a real check to fire drive this many TickField/Poll calls.
    private const int CoveragePollInterval = 150;

    /// <summary>Write identity fields into the STATIC ARRAY slot so EnemyOracle.Capture can
    /// classify this as a known enemy (mirrors KillTrackerTests.SetArrayEnemy).</summary>
    private static void SetArrayEnemy(FakeSparseMemory m, int slot, int level, int brave, int faith, int maxHp)
    {
        long s = Offsets.ArrayReadBase + (long)slot * Offsets.ArrayStride;
        m.U8s[s + Offsets.ALevel] = (byte)level;
        m.U8s[s + Offsets.ABrave] = (byte)brave;
        m.U8s[s + Offsets.AFaith] = (byte)faith;
        m.U16s[s + Offsets.AMaxHp] = (ushort)maxHp;
    }

    /// <summary>Seat a band entry (thin wrapper over MemSeats.SeatBand). Existing fixtures across
    /// the suite default ACtSlam/ATurnFlag to 0, which is why the 2409 pre-existing tests never
    /// generated fielded evidence; tests that need evidence seed those two bytes explicitly.</summary>
    private static void SetBandUnit(FakeSparseMemory m, int bandIdx, int level, int brave, int faith, int maxHp,
                                    int gx = 5, int gy = 5, int hp = 100)
        => MemSeats.SeatBand(m, bandIdx, weapon: 0, lvl: level, br: brave, fa: faith, gx: gx, gy: gy, hp: hp, maxHp: maxHp);

    /// <summary>Alive for 3 ticks (seenAlive), then dead for 1 tick, the dead-edge stamp point
    /// (deadStreak 0->1) where KillTracker.Corpses.cs calls EnemyOracle.MarkDead.</summary>
    private static void SettleAliveThenDead(FakeSparseMemory m, int bandIdx, KillTracker t,
                                            int level, int brave, int faith, int maxHp)
    {
        SetBandUnit(m, bandIdx, level, brave, faith, maxHp, hp: 100);
        for (int i = 0; i < 3; i++) t.Poll(true);
        SetBandUnit(m, bandIdx, level, brave, faith, maxHp, hp: 0);
        t.Poll(true);
    }

    /// <summary>Drive exactly one EnemyOracle coverage-check interval.</summary>
    private static void RunCoverageCheck(EnemyOracle o)
    {
        for (int i = 0; i < CoveragePollInterval; i++) o.TickField();
    }

    /// <summary>Swap ModLogger to an injected-sink FileConsoleLogger for the duration of
    /// <paramref name="body"/>, capturing both sinks, then restore the assembly-wide NullLogger
    /// default (TestLoggingSetup). Mirrors LoggerTests.cs's ModLogger_routes_every_call idiom.</summary>
    private static (List<string> console, List<string> file) CaptureLog(System.Action body)
    {
        var console = new List<string>();
        var file = new List<string>();
        ModLogger.Instance = new FileConsoleLogger(console.Add, file.Add);
        try { body(); }
        finally { ModLogger.UseNullLogger(); }
        return (console, file);
    }

    /// <summary>Reflects into KillTracker's private per-seat evidence-streak array (Corpses.cs),
    /// mirroring the existing reflection idiom in KillTrackerStampTests.cs.</summary>
    private static int[] FieldEvidenceStreak(KillTracker t) =>
        (int[])typeof(KillTracker).GetField("_fieldEvidenceStreak", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(t)!;

    // ---------------------------------------------------------------------------------------
    // (a) LOAD-BEARING: two captured identities, both band-visible, only one evidenced.
    // MUST FAIL against pre-fix code, which prints "All 2" (every captured identity, not just
    // the fielded one).
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Coverage_counts_only_evidenced_identities_not_every_captured_one()
    {
        var m = new FakeSparseMemory();
        var o = new EnemyOracle(m);
        SetArrayEnemy(m, slot: 0, level: 10, brave: 50, faith: 50, maxHp: 400);   // fielded
        SetArrayEnemy(m, slot: 1, level: 20, brave: 60, faith: 60, maxHp: 500);   // never-scheduled phantom
        SetBandUnit(m, bandIdx: 0, level: 10, brave: 50, faith: 50, maxHp: 400);
        SetBandUnit(m, bandIdx: 1, level: 20, brave: 60, faith: 60, maxHp: 500);  // band-visible too, but NOT evidenced
        o.MarkFielded((10, 50, 50, 400));

        var (_, file) = CaptureLog(() =>
        {
            RunCoverageCheck(o);   // check 1: total=1, unstable (fresh prevTotal) -> defers
            RunCoverageCheck(o);   // check 2: total=1, stable -> latches
        });

        Assert.True(o.CoverageDone);
        Assert.Contains(file, l => l.Contains("All 1 enemies are accounted for"));
        Assert.DoesNotContain(file, l => l.Contains("All 2 enemies"));
        Assert.Contains(file, l => l.Contains("1 captured identities excluded as never-scheduled"));
    }

    // ---------------------------------------------------------------------------------------
    // (b) Evidence admission, one class per test, driven through the REAL ScanCorpses walk.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Slam_evidence_admits_a_fielded_identity_via_the_real_band_walk()
    {
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        SetArrayEnemy(m, slot: 0, level: 10, brave: 50, faith: 50, maxHp: 400);
        SetBandUnit(m, bandIdx: 0, level: 10, brave: 50, faith: 50, maxHp: 400);
        m.U8s[Band.Entry(0) + Offsets.ACtSlam] = 70;   // fixtures default this to 0; seed explicitly
        var t = new KillTracker(kills, m, new HashSet<int>());

        for (int i = 0; i < 3; i++) t.Poll(true);   // 3-tick debounce -> MarkFielded fires

        var (_, file) = CaptureLog(() =>
        {
            for (int i = 0; i < CoveragePollInterval; i++) t.Poll(true);
            for (int i = 0; i < CoveragePollInterval; i++) t.Poll(true);
        });

        Assert.Contains(file, l => l.Contains("All 1 enemies are accounted for"));
    }

    [Fact]
    public void Turn_flag_evidence_admits_a_fielded_identity_via_the_real_band_walk()
    {
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        SetArrayEnemy(m, slot: 0, level: 10, brave: 50, faith: 50, maxHp: 400);
        SetBandUnit(m, bandIdx: 0, level: 10, brave: 50, faith: 50, maxHp: 400);
        m.U8s[Band.Entry(0) + Offsets.ATurnFlag] = 1;   // fixtures default this to 0; seed explicitly
        var t = new KillTracker(kills, m, new HashSet<int>());

        for (int i = 0; i < 3; i++) t.Poll(true);

        var (_, file) = CaptureLog(() =>
        {
            for (int i = 0; i < CoveragePollInterval; i++) t.Poll(true);
            for (int i = 0; i < CoveragePollInterval; i++) t.Poll(true);
        });

        Assert.Contains(file, l => l.Contains("All 1 enemies are accounted for"));
    }

    [Fact]
    public void Dead_edge_evidence_admits_a_fielded_identity_via_MarkDead()
    {
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        SetArrayEnemy(m, slot: 0, level: 10, brave: 50, faith: 50, maxHp: 400);
        var t = new KillTracker(kills, m, new HashSet<int>());

        SettleAliveThenDead(m, bandIdx: 0, t, level: 10, brave: 50, faith: 50, maxHp: 400);

        var (_, file) = CaptureLog(() =>
        {
            for (int i = 0; i < CoveragePollInterval; i++) t.Poll(true);
            for (int i = 0; i < CoveragePollInterval; i++) t.Poll(true);
        });

        Assert.Contains(file, l => l.Contains("All 1 enemies are accounted for"));
    }

    // ---------------------------------------------------------------------------------------
    // (c1)/(c2) Missing-from-band handling differs by evidence class.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Fielded_evidenced_identity_missing_from_band_warns_and_blocks_the_pass()
    {
        var m = new FakeSparseMemory();
        var o = new EnemyOracle(m);
        SetArrayEnemy(m, slot: 0, level: 10, brave: 50, faith: 50, maxHp: 400);
        // Deliberately NOT seated in the band: BandVisible is false.
        o.MarkFielded((10, 50, 50, 400));

        var (_, file) = CaptureLog(() =>
        {
            RunCoverageCheck(o);   // total=1, found=0 -> fail line + warn
            RunCoverageCheck(o);   // dedup: warn does not repeat
        });

        Assert.False(o.CoverageDone);
        Assert.Contains(file, l => l.Contains("A fielded enemy is no longer visible in the battle band; its kills may go uncredited."));
        Assert.Single(file.FindAll(l => l.Contains("A fielded enemy is no longer visible")));
    }

    [Fact]
    public void Dead_evidenced_identity_missing_from_band_still_counts_as_found_and_never_warns()
    {
        var m = new FakeSparseMemory();
        var o = new EnemyOracle(m);
        SetArrayEnemy(m, slot: 0, level: 10, brave: 50, faith: 50, maxHp: 400);
        o.MarkDead((10, 50, 50, 400));   // died; never seated in the band (the crystallize/chest case)

        var (_, file) = CaptureLog(() =>
        {
            RunCoverageCheck(o);
            RunCoverageCheck(o);
        });

        Assert.True(o.CoverageDone);
        Assert.Contains(file, l => l.Contains("All 1 enemies are accounted for"));
        Assert.DoesNotContain(file, l => l.Contains("no longer visible"));
    }

    // ---------------------------------------------------------------------------------------
    // (d) Grind-tape heal: a never-evidenced captured identity does not block coverage.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Never_evidenced_captured_identity_does_not_block_coverage()
    {
        var m = new FakeSparseMemory();
        var o = new EnemyOracle(m);
        SetArrayEnemy(m, slot: 0, level: 10, brave: 50, faith: 50, maxHp: 400);   // evidenced, band-visible
        SetArrayEnemy(m, slot: 1, level: 99, brave: 1, faith: 1, maxHp: 999);     // never evidenced (phantom)
        SetBandUnit(m, bandIdx: 0, level: 10, brave: 50, faith: 50, maxHp: 400);
        o.MarkFielded((10, 50, 50, 400));

        var (_, file) = CaptureLog(() =>
        {
            RunCoverageCheck(o);
            RunCoverageCheck(o);
        });

        Assert.True(o.CoverageDone);
        Assert.Contains(file, l => l.Contains("All 1 enemies are accounted for"));
    }

    // ---------------------------------------------------------------------------------------
    // (e) Empty fielded intersection: defer silently.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Empty_fielded_intersection_defers_silently()
    {
        var m = new FakeSparseMemory();
        var o = new EnemyOracle(m);
        SetArrayEnemy(m, slot: 0, level: 10, brave: 50, faith: 50, maxHp: 400);   // captured, never evidenced

        var (console, file) = CaptureLog(() =>
        {
            RunCoverageCheck(o);
            RunCoverageCheck(o);
        });

        Assert.False(o.CoverageDone);
        Assert.Empty(console);
        Assert.Empty(file);
    }

    // ---------------------------------------------------------------------------------------
    // (f) Raw-flag discipline: a turn-flag byte reading 3 is NOT evidence (== 1 exactly, LW-63).
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Turn_flag_byte_of_three_is_not_evidence()
    {
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        SetArrayEnemy(m, slot: 0, level: 10, brave: 50, faith: 50, maxHp: 400);
        SetBandUnit(m, bandIdx: 0, level: 10, brave: 50, faith: 50, maxHp: 400);
        m.U8s[Band.Entry(0) + Offsets.ATurnFlag] = 3;   // a raw flag value, never a plain boolean
        var t = new KillTracker(kills, m, new HashSet<int>());

        for (int i = 0; i < 5; i++) t.Poll(true);

        Assert.Equal(0, FieldEvidenceStreak(t)[0]);
    }

    // ---------------------------------------------------------------------------------------
    // (g) Regression: KillTrackerTests, ActorRegisterTests, BattleCensusTests,
    // KillTrackerDeedTests stay green, unmodified, as part of the full-suite gate. No test
    // lives here for that; it is a constraint on this session's edits, verified by the suite run.
    // ---------------------------------------------------------------------------------------

    // ---------------------------------------------------------------------------------------
    // (h) LATCH STABILITY: two consecutive agreeing passes required.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Latch_requires_two_consecutive_agreeing_passes()
    {
        var m = new FakeSparseMemory();
        var o = new EnemyOracle(m);
        SetArrayEnemy(m, slot: 0, level: 10, brave: 50, faith: 50, maxHp: 400);
        SetArrayEnemy(m, slot: 1, level: 20, brave: 60, faith: 60, maxHp: 500);
        SetBandUnit(m, bandIdx: 0, level: 10, brave: 50, faith: 50, maxHp: 400);
        SetBandUnit(m, bandIdx: 1, level: 20, brave: 60, faith: 60, maxHp: 500);
        o.MarkFielded((10, 50, 50, 400));   // only id1 evidenced for check 1

        var (_, file1) = CaptureLog(() => RunCoverageCheck(o));   // check 1: total=1, unstable -> defers
        Assert.False(o.CoverageDone);
        Assert.DoesNotContain(file1, l => l.Contains("enemies are accounted for"));

        o.MarkFielded((20, 60, 60, 500));   // id2 evidence arrives before check 2

        var (_, file2) = CaptureLog(() => RunCoverageCheck(o));   // check 2: total=2, unstable (prev=1) -> defers
        Assert.False(o.CoverageDone);
        Assert.DoesNotContain(file2, l => l.Contains("enemies are accounted for"));

        var (_, file3) = CaptureLog(() => RunCoverageCheck(o));   // check 3: total=2, stable -> latches
        Assert.True(o.CoverageDone);
        Assert.Contains(file3, l => l.Contains("All 2 enemies are accounted for"));
    }

    // ---------------------------------------------------------------------------------------
    // (i) onField=false ticks feed no evidence.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void OnField_false_ticks_never_advance_the_evidence_streak()
    {
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        SetArrayEnemy(m, slot: 0, level: 10, brave: 50, faith: 50, maxHp: 400);
        SetBandUnit(m, bandIdx: 0, level: 10, brave: 50, faith: 50, maxHp: 400);
        m.U8s[Band.Entry(0) + Offsets.ACtSlam] = 70;   // qualifying evidence, but ticks stay off-field
        var t = new KillTracker(kills, m, new HashSet<int>());

        for (int i = 0; i < 5; i++) t.Poll(false);   // off-field: must not advance the streak
        Assert.Equal(0, FieldEvidenceStreak(t)[0]);

        t.Poll(true); t.Poll(true);   // 2 on-field ticks: the streak restarts from scratch
        Assert.Equal(2, FieldEvidenceStreak(t)[0]);
    }

    // ---------------------------------------------------------------------------------------
    // (j) ResetBattle clears the fielded/died sets and the remembered previous total.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void ResetBattle_clears_the_died_evidence_set()
    {
        var m = new FakeSparseMemory();
        var o = new EnemyOracle(m);
        var id = ((byte)10, (byte)50, (byte)50, (ushort)400);
        SetArrayEnemy(m, slot: 0, level: 10, brave: 50, faith: 50, maxHp: 400);
        o.MarkDead(id);
        RunCoverageCheck(o);   // check 1: unstable -> defers
        RunCoverageCheck(o);   // check 2: stable -> latches
        Assert.True(o.CoverageDone);

        o.ResetBattle();

        var (_, file) = CaptureLog(() => RunCoverageCheck(o));   // fresh battle: nothing marked at all

        Assert.False(o.CoverageDone);   // the died-set entry did not survive the reset
        Assert.Empty(file);             // total==0 -> defers silently
    }

    [Fact]
    public void ResetBattle_clears_the_remembered_previous_total_so_latching_needs_two_fresh_passes()
    {
        var m = new FakeSparseMemory();
        var o = new EnemyOracle(m);
        SetArrayEnemy(m, slot: 0, level: 10, brave: 50, faith: 50, maxHp: 400);
        SetBandUnit(m, bandIdx: 0, level: 10, brave: 50, faith: 50, maxHp: 400);
        o.MarkFielded((10, 50, 50, 400));
        RunCoverageCheck(o);   // check 1: total=1, prevTotal 0->1, unstable -> defers (no line)
        Assert.False(o.CoverageDone);

        o.ResetBattle();
        o.MarkFielded((10, 50, 50, 400));   // same identity, fresh battle

        var (_, file) = CaptureLog(() => RunCoverageCheck(o));   // a single post-reset check

        // If _prevTotal had survived the reset (stale 1), this single check would wrongly
        // latch immediately (1 == stale 1). It must instead require a second agreeing pass.
        Assert.False(o.CoverageDone);
        Assert.DoesNotContain(file, l => l.Contains("enemies are accounted for"));
    }

    // ---------------------------------------------------------------------------------------
    // (k) MarkFielded of an uncaptured (e.g. player) tuple never affects the total.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void MarkFielded_of_an_uncaptured_identity_never_affects_the_total()
    {
        var m = new FakeSparseMemory();
        var o = new EnemyOracle(m);
        SetArrayEnemy(m, slot: 0, level: 10, brave: 50, faith: 50, maxHp: 400);   // the one real enemy
        SetBandUnit(m, bandIdx: 0, level: 10, brave: 50, faith: 50, maxHp: 400);
        o.MarkFielded((10, 50, 50, 400));    // real enemy, evidenced
        o.MarkFielded((99, 89, 76, 352));    // a player fingerprint, never captured

        var (_, file) = CaptureLog(() =>
        {
            RunCoverageCheck(o);
            RunCoverageCheck(o);
        });

        Assert.True(o.CoverageDone);
        Assert.Contains(file, l => l.Contains("All 1 enemies are accounted for"));
    }

    // ---------------------------------------------------------------------------------------
    // (l) Evidence debounce: 2 qualifying ticks then a gap never marks; 3 consecutive marks;
    // a (0,0)-position seat with flag==1 never qualifies (realPos filter).
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Two_qualifying_ticks_then_a_gap_resets_the_streak_without_marking()
    {
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        SetArrayEnemy(m, slot: 0, level: 10, brave: 50, faith: 50, maxHp: 400);
        SetBandUnit(m, bandIdx: 0, level: 10, brave: 50, faith: 50, maxHp: 400);
        m.U8s[Band.Entry(0) + Offsets.ACtSlam] = 70;
        var t = new KillTracker(kills, m, new HashSet<int>());

        t.Poll(true); t.Poll(true);
        Assert.Equal(2, FieldEvidenceStreak(t)[0]);

        m.U8s[Band.Entry(0) + Offsets.ACtSlam] = 0;   // the gap tick: no longer qualifying
        t.Poll(true);

        Assert.Equal(0, FieldEvidenceStreak(t)[0]);

        // Pin the debounce LOWER bound behaviorally, not just the counter: 2 qualifying ticks
        // must mean NEVER MARKED, so two full coverage intervals later there is still no
        // "accounted for" line (a threshold weakened to 1 would have marked on the first tick,
        // latched on the second agreeing check, and printed).
        var (_, file) = CaptureLog(() =>
        {
            for (int i = 0; i < CoveragePollInterval; i++) t.Poll(true);
            for (int i = 0; i < CoveragePollInterval; i++) t.Poll(true);
        });
        Assert.DoesNotContain(file, l => l.Contains("enemies are accounted for"));
    }

    [Fact]
    public void Three_consecutive_qualifying_ticks_marks_the_identity_fielded()
    {
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        SetArrayEnemy(m, slot: 0, level: 10, brave: 50, faith: 50, maxHp: 400);
        SetBandUnit(m, bandIdx: 0, level: 10, brave: 50, faith: 50, maxHp: 400);
        m.U8s[Band.Entry(0) + Offsets.ACtSlam] = 70;
        var t = new KillTracker(kills, m, new HashSet<int>());

        for (int i = 0; i < 3; i++) t.Poll(true);          // exactly the 3-tick debounce
        m.U8s[Band.Entry(0) + Offsets.ACtSlam] = 0;        // stop qualifying; already-marked evidence must persist

        var (_, file) = CaptureLog(() =>
        {
            for (int i = 0; i < CoveragePollInterval; i++) t.Poll(true);
            for (int i = 0; i < CoveragePollInterval; i++) t.Poll(true);
        });

        Assert.Contains(file, l => l.Contains("All 1 enemies are accounted for"));
    }

    // ---------------------------------------------------------------------------------------
    // (m) LW-75: the armed latch usually rises AFTER the success line latches (live-measured
    // 97s late, 2026-07-11); a demoted line must be remembered and pushed to the console once
    // when the armed latch later rises in the same battle.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Demoted_success_line_is_promoted_to_console_once_armed_latch_rises()
    {
        var m = new FakeSparseMemory();
        bool armed = false;
        var o = new EnemyOracle(m, () => armed);
        SetArrayEnemy(m, slot: 0, level: 10, brave: 50, faith: 50, maxHp: 400);
        SetBandUnit(m, bandIdx: 0, level: 10, brave: 50, faith: 50, maxHp: 400);
        o.MarkFielded((10, 50, 50, 400));

        var (console1, file1) = CaptureLog(() =>
        {
            RunCoverageCheck(o);   // check 1: unstable -> defers
            RunCoverageCheck(o);   // check 2: stable -> latches, unarmed -> demoted to file, stashed
        });

        Assert.True(o.CoverageDone);
        Assert.DoesNotContain(console1, l => l.Contains("enemies are accounted for"));
        Assert.Contains(file1, l => l.Contains("All 1 enemies are accounted for"));

        armed = true;

        var (console2, _) = CaptureLog(() => o.TickField());   // armed latch rises: promote once

        Assert.Single(console2.FindAll(l => l.Contains("All 1 enemies are accounted for")));

        var (console3, _) = CaptureLog(() =>
        {
            o.TickField();
            o.TickField();
        });

        Assert.Empty(console3.FindAll(l => l.Contains("All 1 enemies are accounted for")));
    }

    [Fact]
    public void Success_line_reaches_console_exactly_once_when_armed_at_latch_time()
    {
        var m = new FakeSparseMemory();
        var o = new EnemyOracle(m, () => true);
        SetArrayEnemy(m, slot: 0, level: 10, brave: 50, faith: 50, maxHp: 400);
        SetBandUnit(m, bandIdx: 0, level: 10, brave: 50, faith: 50, maxHp: 400);
        o.MarkFielded((10, 50, 50, 400));

        var (console, _) = CaptureLog(() =>
        {
            RunCoverageCheck(o);   // check 1: unstable -> defers
            RunCoverageCheck(o);   // check 2: stable -> latches, armed -> console immediately
            o.TickField();
            o.TickField();
        });

        Assert.Single(console.FindAll(l => l.Contains("All 1 enemies are accounted for")));
    }

    [Fact]
    public void ResetBattle_clears_the_pending_console_line()
    {
        var m = new FakeSparseMemory();
        bool armed = false;
        var o = new EnemyOracle(m, () => armed);
        SetArrayEnemy(m, slot: 0, level: 10, brave: 50, faith: 50, maxHp: 400);
        SetBandUnit(m, bandIdx: 0, level: 10, brave: 50, faith: 50, maxHp: 400);
        o.MarkFielded((10, 50, 50, 400));

        CaptureLog(() =>
        {
            RunCoverageCheck(o);
            RunCoverageCheck(o);
        });
        Assert.True(o.CoverageDone);

        o.ResetBattle();
        armed = true;

        var (console, _) = CaptureLog(() => o.TickField());

        Assert.Empty(console.FindAll(l => l.Contains("enemies are accounted for")));
    }

    [Fact]
    public void A_frozen_zero_zero_seat_with_turn_flag_one_never_qualifies()
    {
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        SetArrayEnemy(m, slot: 0, level: 10, brave: 50, faith: 50, maxHp: 400);
        SetBandUnit(m, bandIdx: 0, level: 10, brave: 50, faith: 50, maxHp: 400, gx: 0, gy: 0);   // frozen mirror
        m.U8s[Band.Entry(0) + Offsets.ATurnFlag] = 1;   // would otherwise qualify
        var t = new KillTracker(kills, m, new HashSet<int>());

        for (int i = 0; i < 5; i++) t.Poll(true);

        Assert.Equal(0, FieldEvidenceStreak(t)[0]);
    }
}
