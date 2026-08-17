using System.Collections.Generic;
using System.Reflection;
using LivingWeapon;
using Xunit;
using static LivingWeapon.Tests.KillTrackerFixtures;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-233: KillTracker.Corpses.cs's RestartSentinel wiring, driven end-to-end through
/// KillTracker.Poll against a FakeSparseMemory -- the tape-shape tests from the plan's TDD list
/// (items 2-9). RestartSentinelTests.cs/RestartSentinelPolicyTests.cs cover the sentinel in
/// isolation; ActorRegisterTests.cs covers RawNullThisTick. Reuses KillTrackerTests' fixture
/// conventions (KillTrackerFixtures, AliveThenDead, Settle) so a slot's credit/revive shape reads
/// the same way here as everywhere else in the suite.
/// </summary>
public class KillTrackerRestartTests
{
    private const int Wilham = Offsets.SlotsBack;       // band slot 20 (player-side actor)

    /// <summary>Poll onField n times with no memory changes -- used to clear the sentinel's own
    /// battle-age grace (150 ticks) without perturbing any corpse belt.</summary>
    private static void AdvanceTicks(KillTracker t, int n) { for (int i = 0; i < n; i++) t.Poll(true); }

    /// <summary>Fold the engine actor pointer to a literal raw zero, readable (the RawNullThisTick
    /// contract -- an unreadable page must never count, so MarkReadable is required here).</summary>
    private static void NullPointer(FakeSparseMemory m)
    {
        m.MarkReadable(Offsets.ActorPtr, 8);
        m.SeedU64(Offsets.ActorPtr, 0);
    }

    private static KillTracker MakeLatchedTracker(FakeSparseMemory m, Dictionary<int, int> kills,
        HashSet<int> weapons, int weapon, System.Func<int, bool>? hasLiveWielder = null,
        int lhand = 0xFFFF)
    {
        SetRoster(m, slot: 3, level: 99, brave: 89, faith: 76, weapon: weapon, lhand: lhand);
        SetUnit(m, Wilham, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetActive(m, hp: 352, maxHp: 352, level: 99);
        var t = new KillTracker(kills, m, weapons, hasLiveWielder: hasLiveWielder);
        Settle(t);   // latch the weapon(s)
        return t;
    }

    // ---- test 2: LOAD-BEARING retry-A tape shape (the REAL 0ms null-revive join) ----

    /// <summary>
    /// LW-233 fix (verifier-caught): retry A's REAL tape is a 0ms join -- the raw null and the
    /// credited corpses' alive-again read land on the SAME tick. THE MECHANISM DETAIL that makes
    /// this dangerous: a REVIVE (a slot that was previously seenAlive, then died) is recognized
    /// IMMEDIATELY, on the very first tick hp reads positive again -- _seenAlive/_aliveStreak are
    /// never reset by death, so the AliveNeeded debounce only ever gates a slot's FIRST-ever alive
    /// sighting, not a revive. So when the null and the revive both land on the same Poll, the
    /// null streak the sentinel sees at that exact moment is only 1 -- one tick short of
    /// NullPersistTicks (2). Before the deferred-verdict fix, that event was simply refused and
    /// lost; a tick later, when the null qualified, the corpse's credited-weapon evidence was
    /// already gone (the ordinary re-arm had cleared it). This is THE double-payout battle the
    /// ledger names. RED-before-fix repro method used while building this test: temporarily force
    /// RestartSentinelPolicy.ShouldStash to return false (so the deferred verdict never fires) --
    /// the test then fails exactly where it does below (changedAtQualify never true, kills stay at
    /// the inflated baseline).
    /// </summary>
    [Fact]
    public void Retry_A_0ms_join_shape_uncredits_both_revived_credited_slots_then_recredits_on_a_real_rekill()
    {
        const int weapon = 19;
        var weapons = new HashSet<int> { weapon };
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        var t = MakeLatchedTracker(m, kills, weapons, weapon);

        const int slotA = 10, slotB = 15;
        AliveThenDead(m, slotA, t);
        AliveThenDead(m, slotB, t);
        Assert.Equal(2, kills.GetValueOrDefault(weapon));

        AdvanceTicks(t, 200);   // clear the sentinel's own battle-age grace

        // The REAL retry-A shape: the raw null and the credited corpses' alive-again read land on
        // the EXACT SAME tick -- revives are recognized immediately (no debounce), so this is the
        // razor-thin 0ms join, not a several-tick gap.
        SetUnit(m, slotA, hp: 100, maxHp: 400, level: 10, brave: 50, faith: 50);
        SetUnit(m, slotB, hp: 100, maxHp: 400, level: 10, brave: 50, faith: 50);
        NullPointer(m);   // the null begins on the SAME tick as the revive
        bool changedAtPresentation = t.Poll(true);   // revive presented THIS tick, null streak == 1

        // Not yet reversed -- the null hasn't qualified. The fix's whole point: this must not be
        // lost either, so it stays stashed until the null qualifies one tick later.
        Assert.False(changedAtPresentation);
        Assert.Equal(2, kills.GetValueOrDefault(weapon));

        bool changedAtQualify = t.Poll(true);   // null streak reaches 2 -> qualifies -> stash drains
        PointAt(m, Wilham);                     // re-acquire, matching the tape's own re-acquire-after-qualify shape

        Assert.True(changedAtQualify);
        Assert.Equal(0, kills.GetValueOrDefault(weapon));   // BOTH reversed, back to baseline
        Assert.True(t.Restart.LatchOpen);

        // Let the retry latch fully close (a fresh dead-edge while it is still open goes pending,
        // not credited -- test 6 covers that path directly) before the real re-kill.
        AdvanceTicks(t, RestartSentinelPolicy.LatchTicks + 10);
        Assert.False(t.Restart.LatchOpen);

        AliveThenDead(m, slotA, t);   // a real re-kill afterward
        Assert.Equal(1, kills.GetValueOrDefault(weapon));   // credits once, at the right count
    }

    /// <summary>The negative twin (verifier-requested): a 1-tick null FLICKER that coincides with
    /// a revive but breaks before ever qualifying must produce ZERO uncredits -- the deferred-
    /// verdict stash exists to WAIT for qualification, not to assume it. A real Raise never has an
    /// active null streak at all, so this also stands in for "the flicker never happened" byte-
    /// identity on the surrounding re-arm.</summary>
    [Fact]
    public void A_one_tick_null_flicker_that_breaks_before_qualifying_produces_zero_uncredits()
    {
        const int weapon = 19;
        var weapons = new HashSet<int> { weapon };
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        var t = MakeLatchedTracker(m, kills, weapons, weapon);

        const int slot = 10;
        AliveThenDead(m, slot, t);
        Assert.Equal(1, kills.GetValueOrDefault(weapon));

        AdvanceTicks(t, 200);

        SetUnit(m, slot, hp: 100, maxHp: 400, level: 10, brave: 50, faith: 50);
        NullPointer(m);   // same tick as the revive
        t.Poll(true);     // revive presented THIS tick, null streak == 1 -- deferred, stashed

        Assert.Equal(1, kills.GetValueOrDefault(weapon));   // not yet touched

        // The flicker breaks here -- the pointer re-acquires before the null ever persists 2 ticks.
        PointAt(m, Wilham);
        t.Poll(true);   // null streak resets to 0 -- the stash's TTL keeps ticking down, unqualified

        Assert.Equal(1, kills.GetValueOrDefault(weapon));   // ZERO uncredits
        Assert.False(t.Restart.LatchOpen);

        // Today's ordinary re-arm: a real re-kill afterward credits again, exactly like a normal Raise.
        AliveThenDead(m, slot, t);
        Assert.Equal(2, kills.GetValueOrDefault(weapon));
    }

    /// <summary>The "drill" tape's own shape (~656ms persist, ~62ms/2-tick LEAD before the revive
    /// even begins): a comfortably-qualified null takes the DIRECT open path, no stash needed.
    /// Kept as its own named fixture, distinct from retry-A's razor-thin 0ms join above.</summary>
    [Fact]
    public void Drill_timing_shape_a_two_tick_null_lead_uncredits_through_the_direct_path()
    {
        const int weapon = 19;
        var weapons = new HashSet<int> { weapon };
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        var t = MakeLatchedTracker(m, kills, weapons, weapon);

        const int slot = 10;
        AliveThenDead(m, slot, t);
        Assert.Equal(1, kills.GetValueOrDefault(weapon));

        AdvanceTicks(t, 200);
        NullPointer(m);
        AdvanceTicks(t, 2);   // ~62ms lead: the null is already fully qualified before the revive begins
        PointAt(m, Wilham);

        SetUnit(m, slot, hp: 100, maxHp: 400, level: 10, brave: 50, faith: 50);
        bool changed = t.Poll(true);   // revive presented THIS tick; qualified null already on record -> direct path

        Assert.True(changed);
        Assert.Equal(0, kills.GetValueOrDefault(weapon));
        Assert.True(t.Restart.LatchOpen);
    }

    // ---- test 3: RAISE NEGATIVE (the twin) ----

    [Fact]
    public void A_normal_raise_without_any_null_never_uncredits()
    {
        const int weapon = 19;
        var weapons = new HashSet<int> { weapon };
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        var t = MakeLatchedTracker(m, kills, weapons, weapon);

        const int slot = 10;
        AliveThenDead(m, slot, t);
        Assert.Equal(1, kills.GetValueOrDefault(weapon));

        AdvanceTicks(t, 200);   // past grace, but NO null is ever observed -- an ordinary Raise

        SetUnit(m, slot, hp: 100, maxHp: 400, level: 10, brave: 50, faith: 50);
        Settle(t);

        Assert.Equal(1, kills.GetValueOrDefault(weapon));   // untouched -- byte-identical to today
        Assert.False(t.Restart.LatchOpen);

        AliveThenDead(m, slot, t);   // re-kill after the Raise
        Assert.Equal(2, kills.GetValueOrDefault(weapon));   // credits again, exactly as today
    }

    // ---- test 4: DUAL-WIELD (blocker 1) ----

    [Fact]
    public void Dual_wield_credit_uncredits_both_weapons_on_a_latched_revive()
    {
        const int w1 = 52, w2 = 90;
        var weapons = new HashSet<int> { w1, w2 };
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        var t = MakeLatchedTracker(m, kills, weapons, w1, lhand: w2);

        const int slot = 10;
        AliveThenDead(m, slot, t);
        Assert.Equal(1, kills.GetValueOrDefault(w1));
        Assert.Equal(1, kills.GetValueOrDefault(w2));

        AdvanceTicks(t, 200);
        NullPointer(m);
        AdvanceTicks(t, 2);
        PointAt(m, Wilham);

        SetUnit(m, slot, hp: 100, maxHp: 400, level: 10, brave: 50, faith: 50);
        Settle(t);

        Assert.Equal(0, kills.GetValueOrDefault(w1));
        Assert.Equal(0, kills.GetValueOrDefault(w2));
    }

    [Fact]
    public void Dual_wield_partial_refusal_only_uncredits_the_surviving_credited_weapon()
    {
        const int liveWeapon = 52, ghostWeapon = 90;
        var weapons = new HashSet<int> { liveWeapon, ghostWeapon };
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        // ghostWeapon is refused at CREDIT time (no live wielder), so only liveWeapon is ever
        // actually stamped into _creditedWeapons.
        var t = MakeLatchedTracker(m, kills, weapons, liveWeapon, hasLiveWielder: id => id != ghostWeapon, lhand: ghostWeapon);

        const int slot = 10;
        AliveThenDead(m, slot, t);
        Assert.Equal(1, kills.GetValueOrDefault(liveWeapon));
        Assert.False(kills.ContainsKey(ghostWeapon));   // refused, never credited in the first place

        AdvanceTicks(t, 200);
        NullPointer(m);
        AdvanceTicks(t, 2);
        PointAt(m, Wilham);

        SetUnit(m, slot, hp: 100, maxHp: 400, level: 10, brave: 50, faith: 50);
        Settle(t);

        Assert.Equal(0, kills.GetValueOrDefault(liveWeapon));   // the survivor is uncredited
        Assert.False(kills.ContainsKey(ghostWeapon));           // never touched -- it was never credited
    }

    // ---- test 5: CHECKPOINT (retry-B shape) ----

    [Fact]
    public void Checkpoint_shape_only_uncredits_the_revived_credited_victim()
    {
        const int weapon = 19;
        var weapons = new HashSet<int> { weapon };
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        var t = MakeLatchedTracker(m, kills, weapons, weapon);

        const int revivedSlot = 11, staySlot = 12, playerCorpseSlot = 24;

        AliveThenDead(m, revivedSlot, t);
        AliveThenDead(m, staySlot, t);
        Assert.Equal(2, kills.GetValueOrDefault(weapon));

        // A player/guest corpse: band-alive then band-dead WITHOUT the array-oracle registration
        // SetEnemy performs -- exactly how a real ally/guest corpse is structurally excluded
        // (OracleGate's not-tracked-enemy no-credit path).
        SetUnit(m, playerCorpseSlot, hp: 300, maxHp: 400, level: 20, brave: 40, faith: 40);
        Settle(t);
        SetUnit(m, playerCorpseSlot, hp: 0, maxHp: 400, level: 20, brave: 40, faith: 40);
        Settle(t);

        AdvanceTicks(t, 200);
        NullPointer(m);
        AdvanceTicks(t, 2);
        PointAt(m, Wilham);
        AdvanceTicks(t, 5);   // "null 5 ticks earlier" -- comfortably inside the 30-tick join window

        SetUnit(m, revivedSlot, hp: 100, maxHp: 400, level: 10, brave: 50, faith: 50);
        SetUnit(m, playerCorpseSlot, hp: 100, maxHp: 400, level: 20, brave: 40, faith: 40);
        Settle(t);   // staySlot is left dead -- never revives

        Assert.Equal(1, kills.GetValueOrDefault(weapon));   // ONE uncredit; staySlot's credit survives
        Assert.True(t.Restart.LatchOpen);
    }

    // ---- test 6: LATCH-OPEN DEAD-EDGE ----

    [Fact]
    public void A_fresh_dead_edge_during_the_open_latch_goes_pending_then_resolves_after_it_closes()
    {
        const int weapon = 19;
        var weapons = new HashSet<int> { weapon };
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        var t = MakeLatchedTracker(m, kills, weapons, weapon);

        const int revivedSlot = 10, freshSlot = 16;
        AliveThenDead(m, revivedSlot, t);
        Assert.Equal(1, kills.GetValueOrDefault(weapon));

        AdvanceTicks(t, 200);
        NullPointer(m);
        AdvanceTicks(t, 2);
        PointAt(m, Wilham);

        SetUnit(m, revivedSlot, hp: 100, maxHp: 400, level: 10, brave: 50, faith: 50);
        Settle(t);
        Assert.Equal(0, kills.GetValueOrDefault(weapon));
        Assert.True(t.Restart.LatchOpen);

        // A fresh kill on a DIFFERENT slot while the latch is still open.
        SetEnemy(m, freshSlot, hp: 300, maxHp: 400, level: 30, brave: 20, faith: 20);
        Settle(t);
        SetUnit(m, freshSlot, hp: 0, maxHp: 400, level: 30, brave: 20, faith: 20);
        Settle(t);   // would normally credit at 3 dead ticks

        Assert.Equal(0, kills.GetValueOrDefault(weapon));   // suppressed -- pending, not credited
        Assert.True(t.Restart.LatchOpen);

        AdvanceTicks(t, RestartSentinelPolicy.LatchTicks + 10);   // let the latch fully close
        Assert.False(t.Restart.LatchOpen);
        t.Poll(true);   // re-evaluated the moment suppression lifts

        Assert.Equal(1, kills.GetValueOrDefault(weapon));   // resolves per today's ordinary pending rules
    }

    // ---- test 7: IDENTITY-SWAP ----

    [Fact]
    public void Identity_swap_revive_under_the_latch_uncredits_via_the_swap_branch()
    {
        const int weapon = 19;
        var weapons = new HashSet<int> { weapon };
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        var t = MakeLatchedTracker(m, kills, weapons, weapon);

        const int slot = 10;
        AliveThenDead(m, slot, t);   // identity captured as (10, 50, 50)
        Assert.Equal(1, kills.GetValueOrDefault(weapon));

        AdvanceTicks(t, 200);
        NullPointer(m);
        AdvanceTicks(t, 2);
        PointAt(m, Wilham);

        // The rewind restores a DIFFERENT brave than the one ScanAlivePath captured (70 vs 50) --
        // the swap branch's own trigger, not the ordinary re-arm block.
        SetUnit(m, slot, hp: 100, maxHp: 400, level: 10, brave: 70, faith: 50);
        t.Poll(true);   // the swap branch fires on the first read of the new identity, no debounce

        Assert.Equal(0, kills.GetValueOrDefault(weapon));
        Assert.True(t.Restart.LatchOpen);
    }

    [Fact]
    public void Identity_swap_without_a_qualifying_null_clears_the_credited_set_without_uncrediting()
    {
        const int weapon = 19;
        var weapons = new HashSet<int> { weapon };
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        var t = MakeLatchedTracker(m, kills, weapons, weapon);

        const int slot = 10;
        AliveThenDead(m, slot, t);
        Assert.Equal(1, kills.GetValueOrDefault(weapon));

        AdvanceTicks(t, 200);   // past grace, but no retry evidence at all

        SetUnit(m, slot, hp: 100, maxHp: 400, level: 10, brave: 70, faith: 50);   // identity swap only
        t.Poll(true);

        Assert.Equal(1, kills.GetValueOrDefault(weapon));   // untouched -- no latch, no uncredit
        Assert.False(t.Restart.LatchOpen);

        // The credited set must still have been cleared by the swap branch: kill the slot again
        // under its new (swapped) identity and confirm the tally takes the ordinary +1, with no
        // phantom extra decrement sneaking in from a stale stale-set reference.
        SetEnemy(m, slot, hp: 300, maxHp: 400, level: 12, brave: 70, faith: 55);
        Settle(t);
        SetUnit(m, slot, hp: 0, maxHp: 400, level: 12, brave: 70, faith: 55);
        Settle(t);

        Assert.Equal(2, kills.GetValueOrDefault(weapon));
    }

    // ---- test 8: FLOOR GUARD ----

    [Fact]
    public void UncreditKills_reverses_exactly_what_CreditKill_added()
    {
        const int w1 = 21, w2 = 22;
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        var t = new KillTracker(kills, m, new HashSet<int> { w1, w2 });

        t.CreditKill(0, 1, 1, new List<int> { w1 }, viaFallback: true);
        t.CreditKill(1, 1, 1, new List<int> { w2 }, viaFallback: false);
        Assert.Equal(1, kills[w1]);
        Assert.Equal(1, kills[w2]);
        Assert.Equal(1, t.FallbackCredits);

        t.UncreditKills(new List<int> { w1 }, viaFallback: true, slot: 0);
        t.UncreditKills(new List<int> { w2 }, viaFallback: false, slot: 1);

        Assert.Equal(0, kills[w1]);
        Assert.Equal(0, kills[w2]);
        Assert.Equal(0, t.BattleCredits[w1]);
        Assert.Equal(0, t.BattleCredits[w2]);
        Assert.Equal(0, t.FallbackCredits);   // only the viaFallback=true uncredit touched it
    }

    [Fact]
    public void UncreditKills_floors_at_zero_with_a_warning_instead_of_going_negative()
    {
        const int weapon = 77;
        var kills = new Dictionary<int, int> { [weapon] = 0 };   // never actually credited
        var m = new FakeSparseMemory();
        var t = new KillTracker(kills, m, new HashSet<int> { weapon });

        using var cap = LogCapture.Start(LogLevel.Debug);
        t.UncreditKills(new List<int> { weapon }, viaFallback: true, slot: 3);

        Assert.Equal(0, kills[weapon]);         // floored, never negative
        Assert.Equal(0, t.FallbackCredits);     // floored, never negative
        Assert.Contains(cap.File, line => line.Contains("WARN"));
    }

    // ---- test 9: COMPOSE (two retries in one battle) ----

    [Fact]
    public void Two_retries_in_one_battle_each_uncredit_only_their_own_revived_victims()
    {
        const int weapon = 19;
        var weapons = new HashSet<int> { weapon };
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        var t = MakeLatchedTracker(m, kills, weapons, weapon);

        const int slotA = 1, slotB = 2, slotC = 3, slotD = 4, slotE = 5;

        // Retry A's victims.
        AliveThenDead(m, slotA, t);
        AliveThenDead(m, slotB, t);
        Assert.Equal(2, kills.GetValueOrDefault(weapon));

        AdvanceTicks(t, 200);
        NullPointer(m);
        AdvanceTicks(t, 2);
        PointAt(m, Wilham);

        SetUnit(m, slotA, hp: 100, maxHp: 400, level: 10, brave: 50, faith: 50);
        SetUnit(m, slotB, hp: 100, maxHp: 400, level: 10, brave: 50, faith: 50);
        Settle(t);
        Assert.Equal(0, kills.GetValueOrDefault(weapon));

        AdvanceTicks(t, RestartSentinelPolicy.LatchTicks + 10);   // let retry A's latch fully close
        Assert.False(t.Restart.LatchOpen);

        // Kills earned in the replay -- C and D must survive retry B below untouched.
        AliveThenDead(m, slotC, t);
        AliveThenDead(m, slotD, t);
        AliveThenDead(m, slotE, t);
        Assert.Equal(3, kills.GetValueOrDefault(weapon));

        AdvanceTicks(t, 20);   // time passes; battle age never re-armed, so grace still holds
        NullPointer(m);
        AdvanceTicks(t, 2);
        PointAt(m, Wilham);

        // Retry B's revive: only E.
        SetUnit(m, slotE, hp: 100, maxHp: 400, level: 10, brave: 50, faith: 50);
        Settle(t);

        Assert.Equal(2, kills.GetValueOrDefault(weapon));   // C and D untouched, E reversed
    }

    // ---- small verifier-flagged item: a suppressed credit must not burn a live delayed-culprit arm ----

    /// <summary>
    /// LW-233 (verifier-flagged residual, now closed by a peek-before-consume reorder in
    /// ResolveCredit, KillTracker.Corpses.cs): when the restart latch suppresses a fresh dead-edge
    /// to pending, a delayed/charged culprit (Jump/spellcast) that is currently armed must survive
    /// the tick untouched. ConsumeDelayedCulprit's "first credit wins" semantics would otherwise
    /// burn the arm on a tick that never actually credited anyone, stranding the delayed action's
    /// real kill to a stale fallback (or nothing) once the corpse's death finally resolves.
    /// Verified via reflection on the private arm fields (mirrors DelayedActorTests.cs's own
    /// established pattern) since KillTracker exposes no public surface for them. Sequence mirrors
    /// DelayedActorTests' own load-bearing Jump scenario (P commits, Q intervenes with a different
    /// weapon, Jump lands), but happens WHILE a retry latch (opened via an unrelated slot) is open.
    /// </summary>
    [Fact]
    public void A_suppressed_credit_does_not_consume_the_delayed_culprit_arm()
    {
        const int w = 52, v = 63, wOpen = 90;
        var weapons = new HashSet<int> { w, v, wOpen };
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();

        const int pSlot = Offsets.SlotsBack, qSlot = Offsets.SlotsBack + 1;
        const int enemySlot = 0, openSlot = 1;

        // P starts out wielding wOpen, purely to latch something and open the retry latch via an
        // unrelated credited kill on openSlot.
        SetRoster(m, slot: 3, level: 99, brave: 89, faith: 76, weapon: wOpen);
        SetUnit(m, pSlot, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 1);
        var t = new KillTracker(kills, m, weapons);
        Settle(t);   // latch wOpen

        AliveThenDead(m, openSlot, t);
        Assert.Equal(1, kills.GetValueOrDefault(wOpen));

        AdvanceTicks(t, 200);
        NullPointer(m);
        AdvanceTicks(t, 2);
        PointAt(m, pSlot);

        SetUnit(m, openSlot, hp: 100, maxHp: 400, level: 10, brave: 50, faith: 50);
        Settle(t);
        Assert.True(t.Restart.LatchOpen);   // the retry latch is open now

        // P re-latches weapon w and commits a Jump WHILE the latch is open; Q intervenes with v
        // (the stale fallback if the delayed arm were lost); Jump lands.
        SetRoster(m, slot: 4, level: 99, brave: 89, faith: 76, weapon: w);
        SetUnit(m, pSlot, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 1);
        SetJumpBit(m, pSlot, set: true);
        Settle(t, 3);   // latch fires; TrackDelayed snapshots w

        SetRoster(m, slot: 5, level: 50, brave: 60, faith: 55, weapon: v);
        SetUnit(m, qSlot, hp: 400, maxHp: 400, level: 50, brave: 60, faith: 55);
        SetEnemy(m, enemySlot, hp: 300, maxHp: 400, level: 10, brave: 50, faith: 50);
        Settle(t, 3);   // enemy seenAlive

        SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 0);
        Settle(t, KillTracker.UnfreezeTicks);
        SetActive(m, hp: 400, maxHp: 400, level: 50, acted: 1);
        t.Poll(true);   // Q latches v -- the stale fallback if the delayed arm were lost

        SetJumpBit(m, pSlot, set: false);
        t.Poll(true);   // Jump lands -- arms w, _delayedArmedTicks = Tuning.DelayedActorWindow (12)
        Assert.True(t.Restart.LatchOpen);   // still well inside the 90-tick latch (only ~11 ticks spent)

        var armedTicksField = typeof(KillTracker).GetField("_delayedArmedTicks", BindingFlags.NonPublic | BindingFlags.Instance)!;
        int armedBefore = (int)armedTicksField.GetValue(t)!;
        Assert.True(armedBefore > 0);   // sanity: the arm really is live going into the suppressed tick

        // Kill the enemy while the latch is STILL open -- credit must be suppressed to pending.
        SetUnit(m, enemySlot, hp: 0, maxHp: 400, level: 10, brave: 50, faith: 50);
        t.Poll(true);   // deadStreak 1 -- stamps _lethalActor = v (Q's stale latch)
        t.Poll(true);   // deadStreak 2
        bool changed = t.Poll(true);   // deadStreak 3 -- would resolve, but the latch is open -> pending

        Assert.False(changed);
        Assert.False(kills.ContainsKey(w));
        Assert.False(kills.ContainsKey(v));   // neither weapon credited -- pending, not a stale-fallback mis-credit

        int armedAfter = (int)armedTicksField.GetValue(t)!;
        Assert.True(armedAfter > 0);   // THE FIX: the arm survived the suppressed tick, not zeroed by a premature consume
    }

    // ---- instruction 4: TOASTS (spec test #10's non-optional half) ----

    /// <summary>An uncredited retry tick must not enqueue a tier toast for the downward move, and
    /// a later LEGITIMATE re-cross of the same threshold must toast again -- BannerToast fed the
    /// real tally dictionary KillTracker/UncreditKills mutate (not a hand-edited copy), mirroring
    /// production's own wiring (Engine.cs shares one kills dict between the two). BannerToast's own
    /// tally-follows-both-ways design (DetectCrossings' _tiers snapshot, BannerToast.cs) needs no
    /// production change -- CrossedTier already returns 0 (no toast) for any non-upward move.</summary>
    [Fact]
    public void An_uncredited_retry_tick_enqueues_no_toast()
    {
        const int weapon = 19;
        var weapons = new HashSet<int> { weapon };
        var kills = new Dictionary<int, int> { [weapon] = Tuning.ProdThresholds[0] - 1 };   // one kill short of tier 1
        var m = new FakeSparseMemory();
        var t = MakeLatchedTracker(m, kills, weapons, weapon);
        var meta = new Dictionary<int, WeaponMeta> { [weapon] = new() { Name = "Testfire" } };
        var bt = new BannerToast(meta, kills, enabled: true);   // primes at the pre-seeded count -- no tier crossed yet

        const int slot = 10;
        AliveThenDead(m, slot, t);   // the kill that crosses tier 1
        Assert.Equal(Tuning.ProdThresholds[0], kills[weapon]);
        bt.Tick(tallyChanged: true);
        Assert.Single(bt._queue);
        Assert.True(bt.TryTake(out _));   // drain it so the assertions below start from an empty queue
        Assert.False(bt.HasPending);

        // Retry: null past grace, revive the corpse, uncredit.
        AdvanceTicks(t, 200);
        NullPointer(m);
        SetUnit(m, slot, hp: 100, maxHp: 400, level: 10, brave: 50, faith: 50);   // 0ms join -- exercises the deferred path too
        bool changed = t.Poll(true);
        Assert.False(changed);   // stashed, not yet drained
        changed = t.Poll(true);
        PointAt(m, Wilham);
        Assert.True(changed);
        Assert.Equal(Tuning.ProdThresholds[0] - 1, kills[weapon]);   // back below tier 1

        bt.Tick(tallyChanged: changed);   // the uncredit tick
        Assert.False(bt.HasPending);   // NO toast for the downward move
    }

    [Fact]
    public void A_later_legitimate_recross_of_the_same_threshold_toasts_again()
    {
        const int weapon = 19;
        var weapons = new HashSet<int> { weapon };
        var kills = new Dictionary<int, int> { [weapon] = Tuning.ProdThresholds[0] - 1 };
        var m = new FakeSparseMemory();
        var t = MakeLatchedTracker(m, kills, weapons, weapon);
        var meta = new Dictionary<int, WeaponMeta> { [weapon] = new() { Name = "Testfire" } };
        var bt = new BannerToast(meta, kills, enabled: true);

        const int slot = 10;
        AliveThenDead(m, slot, t);
        bt.Tick(tallyChanged: true);
        Assert.True(bt.TryTake(out _));   // drain the first crossing's toast

        AdvanceTicks(t, 200);
        NullPointer(m);
        SetUnit(m, slot, hp: 100, maxHp: 400, level: 10, brave: 50, faith: 50);
        t.Poll(true);
        bool changed = t.Poll(true);
        PointAt(m, Wilham);
        Assert.Equal(Tuning.ProdThresholds[0] - 1, kills[weapon]);
        bt.Tick(tallyChanged: changed);
        Assert.False(bt.HasPending);   // the uncredit itself toasts nothing (previous test pins this)

        AdvanceTicks(t, RestartSentinelPolicy.LatchTicks + 10);   // let the retry latch close

        // A later legitimate re-cross of the SAME threshold.
        AliveThenDead(m, slot, t);
        Assert.Equal(Tuning.ProdThresholds[0], kills[weapon]);
        bt.Tick(tallyChanged: true);
        Assert.Single(bt._queue);   // toasts again -- the snapshot followed the tally back down
    }
}
