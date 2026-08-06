using System.Collections.Generic;
using System.Reflection;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// ActedPeriodLatch unit suite (LW-150 S3): the acted-period latch state machine extracted out
/// of KillTracker.cs's Poll (the acted-block) and FirstKillFallback, driven directly against a
/// REAL ActorResolver + ActorRegister over FakeSparseMemory (mirroring KillerStampTests.cs's and
/// ActorRegisterTests.cs's idioms) -- no KillTracker instance needed, since THE MIRROR-OUTPUTS
/// DESIGN means the latch only ever reads/writes the <see cref="ActedPeriodOutputs"/> carrier
/// this suite constructs and inspects directly. Companion to KillTrackerTests.cs (the end-to-end
/// KillTracker.Poll integration) and KillTrackerStampTests.cs; every value asserted here was
/// first measured off the pre-extraction KillTracker (same Poll-driven scenarios, read via
/// reflection on KillTracker's own private fields) before this class existed.
/// </summary>
public class ActedPeriodLatchTests
{
    private static void SetActive(FakeSparseMemory m, int hp, int maxHp, int level, int acted = 1)
    {
        m.U16s[Offsets.TurnQueue + Offsets.TqHp] = (ushort)hp;
        m.U16s[Offsets.TurnQueue + Offsets.TqMaxHp] = (ushort)maxHp;
        m.U16s[Offsets.TurnQueue + Offsets.TqLevel] = (ushort)level;
        m.U8s[Offsets.Acted] = (byte)acted;
    }

    private static void SetUnit(FakeSparseMemory m, int slot, int hp, int maxHp = 400, int gx = 5, int gy = 5,
                                int level = 10, int brave = 50, int faith = 50, int weapon = 0)
        => MemSeats.SeatBand(m, slot, weapon: weapon, lvl: level, br: brave, fa: faith,
                             gx: gx, gy: gy, hp: hp, maxHp: maxHp);

    private static void SetRoster(FakeSparseMemory m, int slot, int level, int brave, int faith, int weapon,
                                  int lhand = 0xFFFF, int offhand = 0xFFFF)
        => MemSeats.SeatRoster(m, slot, level, brave, faith, weapon, lhand, offhand);

    private const int P1 = Offsets.SlotsBack;       // band slot 20 (player-side actor)
    private const int P2 = Offsets.SlotsBack + 1;   // band slot 21

    // 30 (Arcanum) added for the same-weapon-different-wielder (Larceny) regression test.
    private static readonly HashSet<int> Weapons = new() { 22, 30, 52, 63, 73, 90 };

    private static int PeriodStartTick(ActorResolver resolver) => (int)typeof(ActorResolver)
        .GetField("_periodStartTick", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(resolver)!;

    private static int ActedLow(ActedPeriodLatch latch) => (int)typeof(ActedPeriodLatch)
        .GetField("_actedLow", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(latch)!;

    private static int FallbackStreak(ActedPeriodLatch latch) => (int)typeof(ActedPeriodLatch)
        .GetField("_fallbackStreak", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(latch)!;

    /// <summary>Bundles a real ActorResolver + ActorRegister (no KillTracker) over a fresh
    /// FakeSparseMemory, and drives them exactly the way KillTracker.Poll does: one
    /// <see cref="ActorRegister.Update"/> per tick, immediately followed by one
    /// <see cref="ActedPeriodLatch.Step"/>. <see cref="O"/> is the persistent
    /// <see cref="ActedPeriodOutputs"/> carrier -- callers are free to read AND mutate it directly
    /// between steps (a test seam this design gives for free: no KillTracker field to reach
    /// through reflection for).</summary>
    private sealed class Harness
    {
        internal readonly FakeSparseMemory Mem = new();
        internal readonly ActorRegister Register;
        internal readonly ActorResolver Resolver;
        internal readonly ActedPeriodLatch Latch;
        internal readonly ActedPeriodOutputs O = new();

        internal Harness()
        {
            Register = new ActorRegister(Mem);
            Resolver = new ActorResolver(Mem, Weapons, Register);
            Latch = new ActedPeriodLatch(Mem, Resolver, Register, recorder: null);
        }

        internal void Step(bool anyPending = false)
        {
            Register.Update();
            Latch.Step(O, anyPending);
        }
    }

    // --- edge-guarded open: PeriodOpen/BeginActedPeriod fires once per period, never re-fires ---

    [Fact]
    public void PeriodOpen_edge_guard_never_refires_BeginActedPeriod_mid_period()
    {
        var h = new Harness();
        // No band/roster data seeded anywhere: TryResolveActingPlayer always resolves false
        // (Band.IsValid rejects every unseeded slot), so o.Latched stays false for the whole
        // test -- every acted==1 tick re-enters the `if (!o.Latched)` resolve attempt, isolating
        // the PeriodOpen edge guard itself from the (separately tested) latch-freeze behavior.
        SetActive(h.Mem, hp: 999, maxHp: 999, level: 50, acted: 1);

        h.Step();   // tick 1: acted==1 for the first time -> period opens, periodStartTick stamped
        Assert.True(h.O.PeriodOpen);
        Assert.False(h.O.Latched);
        int openedAt = PeriodStartTick(h.Resolver);
        Assert.Equal(1, openedAt);   // register.Tick == 1 on this harness's very first Update()

        h.Step();   // tick 2: acted==1 still, period already open -> must NOT re-stamp
        Assert.Equal(openedAt, PeriodStartTick(h.Resolver));

        // A sub-UnfreezeTicks dip: acted==0 for 2 ticks (< UnfreezeTicks==3), never reaching the
        // debounced fall below.
        SetActive(h.Mem, hp: 999, maxHp: 999, level: 50, acted: 0);
        h.Step();
        h.Step();
        Assert.True(h.O.PeriodOpen);   // the fall never fired

        // Recover: acted==1 again, still within the SAME period by the edge guard's own contract.
        SetActive(h.Mem, hp: 999, maxHp: 999, level: 50, acted: 1);
        h.Step();
        Assert.Equal(openedAt, PeriodStartTick(h.Resolver));   // NOT refreshed to the recovery tick
    }

    // --- 3-tick debounced fall: ActedFalls increments, Latched/PeriodOpen flip, EndActedPeriod fires ---

    [Fact]
    public void DebouncedFall_after_three_acted_zero_ticks_flips_state_and_increments_ActedFalls()
    {
        var h = new Harness();
        SetRoster(h.Mem, slot: 3, level: 99, brave: 89, faith: 76, weapon: 52);
        SetUnit(h.Mem, P1, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetActive(h.Mem, hp: 352, maxHp: 352, level: 99, acted: 1);

        h.Step(); h.Step(); h.Step();   // settle: latches on tick 1, stays latched (acted stays 1)
        Assert.True(h.O.Latched);
        Assert.True(h.O.PeriodOpen);
        Assert.Equal(0, h.O.ActedFalls);

        SetActive(h.Mem, hp: 352, maxHp: 352, level: 99, acted: 0);
        h.Step();   // actedLow=1: sub-threshold
        Assert.Equal(0, h.O.ActedFalls);
        Assert.True(h.O.Latched);
        h.Step();   // actedLow=2: still sub-threshold
        Assert.Equal(0, h.O.ActedFalls);
        Assert.True(h.O.Latched);
        h.Step();   // actedLow=3: the debounced fall fires
        Assert.Equal(1, h.O.ActedFalls);
        Assert.False(h.O.Latched);
        Assert.False(h.O.PeriodOpen);
        Assert.Equal(-1, PeriodStartTick(h.Resolver));   // proves EndActedPeriod() was actually called
    }

    // --- empty-set replacement + sticky LatchResolvedEmpty ---

    [Fact]
    public void EmptySet_resolve_replaces_a_stale_nonempty_latch_and_LatchResolvedEmpty_stays_sticky()
    {
        var h = new Harness();
        // Player A: armed.
        SetRoster(h.Mem, slot: 3, level: 99, brave: 89, faith: 76, weapon: 52);
        SetUnit(h.Mem, P1, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetActive(h.Mem, hp: 352, maxHp: 352, level: 99, acted: 1);
        h.Step(); h.Step(); h.Step();
        Assert.Equal(new List<int> { 52 }, h.O.LastPlayerWeapons);
        Assert.False(h.O.LatchResolvedEmpty);
        Assert.True(h.O.AnyTrackedWeaponThisBattle);

        // End A's period (debounced fall) -- LastPlayerWeapons is untouched by the fall itself.
        SetActive(h.Mem, hp: 352, maxHp: 352, level: 99, acted: 0);
        h.Step(); h.Step(); h.Step();
        Assert.False(h.O.Latched);
        Assert.Equal(new List<int> { 52 }, h.O.LastPlayerWeapons);   // still A's, stale

        // Player B: a real roster player, band-confirmed UNARMED with its own empty-hands slot.
        SetRoster(h.Mem, slot: 4, level: 50, brave: 60, faith: 55, weapon: 0xFFFF, lhand: 0xFFFF, offhand: 0xFFFF);
        SetUnit(h.Mem, P2, hp: 400, maxHp: 400, level: 50, brave: 60, faith: 55, weapon: 0xFFFF);
        SetActive(h.Mem, hp: 400, maxHp: 400, level: 50, acted: 1);
        h.Step();   // B latches EMPTY -- must REPLACE A's stale [52], not leave it standing

        Assert.True(h.O.Latched);
        Assert.Empty(h.O.LastPlayerWeapons);        // REPLACED
        Assert.True(h.O.LatchResolvedEmpty);
        Assert.True(h.O.AnyTrackedWeaponThisBattle);   // sticky-true from A; B resolving empty must not clear it

        // B's period falls too; LatchResolvedEmpty must stay STICKY (not reset by the fall itself
        // -- only the NEXT successful resolve can change it).
        SetActive(h.Mem, hp: 400, maxHp: 400, level: 50, acted: 0);
        h.Step(); h.Step(); h.Step();
        Assert.False(h.O.Latched);
        Assert.True(h.O.LatchResolvedEmpty);   // sticky
    }

    // --- fingerprint refresh OUTSIDE the SameSet guard (the Larceny bug regression) ---

    [Fact]
    public void LastActorFp_refreshes_for_a_second_wielder_sharing_the_same_weapon_set()
    {
        var h = new Harness();
        // Two Arcanum (30) holders: SameSet is true between them despite different fingerprints.
        SetRoster(h.Mem, slot: 3, level: 99, brave: 89, faith: 76, weapon: 30);
        SetRoster(h.Mem, slot: 4, level: 50, brave: 60, faith: 55, weapon: 30);
        SetUnit(h.Mem, P1, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetUnit(h.Mem, P2, hp: 400, maxHp: 400, level: 50, brave: 60, faith: 55);

        SetActive(h.Mem, hp: 352, maxHp: 352, level: 99, acted: 1);
        h.Step(); h.Step(); h.Step();
        Assert.Equal((99, 89, 76), h.O.LastActorFp);
        Assert.Equal(new List<int> { 30 }, h.O.LastPlayerWeapons);
        Assert.Equal("30", h.O.ActorTag);

        SetActive(h.Mem, hp: 352, maxHp: 352, level: 99, acted: 0);
        h.Step(); h.Step(); h.Step();   // debounced fall

        SetActive(h.Mem, hp: 400, maxHp: 400, level: 50, acted: 1);
        h.Step();   // B latches; SameSet([30],[30]) is true

        Assert.Equal((50, 60, 55), h.O.LastActorFp);        // refreshed DESPITE SameSet
        Assert.Equal(new List<int> { 30 }, h.O.LastPlayerWeapons);   // unchanged (SameSet guard)
        Assert.Equal("30", h.O.ActorTag);                    // unchanged too (same guard)
    }

    // --- fallback 3-streak stability ---

    [Fact]
    public void FallbackStreak_of_three_matching_resolves_latches_and_stamps_lastResolveTick()
    {
        var h = new Harness();
        SetRoster(h.Mem, slot: 3, level: 99, brave: 89, faith: 76, weapon: 52);
        SetUnit(h.Mem, P1, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetActive(h.Mem, hp: 352, maxHp: 352, level: 99, acted: 0);   // the acted-path never fires

        h.Step(anyPending: true);
        Assert.Equal(1, FallbackStreak(h.Latch));
        Assert.Empty(h.O.LastPlayerWeapons);
        h.Step(anyPending: true);
        Assert.Equal(2, FallbackStreak(h.Latch));
        Assert.Empty(h.O.LastPlayerWeapons);
        h.Step(anyPending: true);   // 3rd consecutive identical non-empty resolve -> latches

        Assert.Equal(new List<int> { 52 }, h.O.LastPlayerWeapons);
        Assert.Equal(0, FallbackStreak(h.Latch));   // consumed on acceptance
        Assert.True(h.O.LatchViaFallback);
        Assert.True(h.O.AnyTrackedWeaponThisBattle);
        Assert.Equal(h.Register.Tick, h.O.LastResolveTick);
    }

    // --- the fallback streak's four reset conditions ---

    [Theory]
    [InlineData("nonEmptyLatch")]
    [InlineData("latchResolvedEmpty")]
    [InlineData("notPending")]
    [InlineData("paused")]
    public void FallbackStreak_resets_not_merely_pauses_on_each_bail_condition(string condition)
    {
        var h = new Harness();
        SetRoster(h.Mem, slot: 3, level: 99, brave: 89, faith: 76, weapon: 52);
        SetUnit(h.Mem, P1, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetActive(h.Mem, hp: 352, maxHp: 352, level: 99, acted: 0);

        h.Step(anyPending: true);   // streak=1
        h.Step(anyPending: true);   // streak=2
        Assert.Empty(h.O.LastPlayerWeapons);

        // Trigger exactly one of the four bail conditions on the would-be 3rd tick. Each is
        // undone immediately after so the NEXT ticks are otherwise identical to the streak-of-3
        // test above -- the only variable under test is whether this one interruption reset the
        // streak counter or merely skipped a beat.
        switch (condition)
        {
            case "nonEmptyLatch":
                h.O.LastPlayerWeapons = new List<int> { 999 };
                h.Step(anyPending: true);
                h.O.LastPlayerWeapons = new List<int>();
                break;
            case "latchResolvedEmpty":
                h.O.LatchResolvedEmpty = true;
                h.Step(anyPending: true);
                h.O.LatchResolvedEmpty = false;
                break;
            case "notPending":
                h.Step(anyPending: false);
                break;
            case "paused":
                h.Mem.U8s[Offsets.PauseFlag] = 1;
                h.Step(anyPending: true);
                h.Mem.U8s[Offsets.PauseFlag] = 0;
                break;
        }
        Assert.Equal(0, FallbackStreak(h.Latch));
        Assert.Empty(h.O.LastPlayerWeapons);

        // Exactly two more consistent ticks: if the interruption had only PAUSED the streak at 2
        // (bug), one more tick would suffice to reach 3 and latch. A true reset needs three fresh
        // ticks, so two must still fall short.
        h.Step(anyPending: true);
        h.Step(anyPending: true);
        Assert.Empty(h.O.LastPlayerWeapons);

        // The third fresh tick completes the (reset) streak.
        h.Step(anyPending: true);
        Assert.Equal(new List<int> { 52 }, h.O.LastPlayerWeapons);
    }

    // --- LastResolveTick stamped at BOTH latch-confirm sites ---

    [Fact]
    public void LastResolveTick_is_stamped_at_the_actedPath_site_and_the_fallback_site()
    {
        // Site 1: the acted-period latch (Step's acted==1 branch).
        var h1 = new Harness();
        SetRoster(h1.Mem, slot: 3, level: 99, brave: 89, faith: 76, weapon: 52);
        SetUnit(h1.Mem, P1, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetActive(h1.Mem, hp: 352, maxHp: 352, level: 99, acted: 1);
        h1.Step();
        Assert.True(h1.O.Latched);
        Assert.Equal(h1.Register.Tick, h1.O.LastResolveTick);

        // Site 2: the first-kill fallback (acted never fires this whole scenario).
        var h2 = new Harness();
        SetRoster(h2.Mem, slot: 3, level: 99, brave: 89, faith: 76, weapon: 52);
        SetUnit(h2.Mem, P1, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetActive(h2.Mem, hp: 352, maxHp: 352, level: 99, acted: 0);
        h2.Step(anyPending: true); h2.Step(anyPending: true); h2.Step(anyPending: true);
        Assert.Equal(new List<int> { 52 }, h2.O.LastPlayerWeapons);
        Assert.Equal(h2.Register.Tick, h2.O.LastResolveTick);
    }

    // --- Reset reproduces every battle-start default, including the list REPLACE ---

    [Fact]
    public void Reset_reproduces_every_battle_start_default_including_the_list_replace()
    {
        var h = new Harness();
        SetRoster(h.Mem, slot: 3, level: 99, brave: 89, faith: 76, weapon: 52);
        SetUnit(h.Mem, P1, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetActive(h.Mem, hp: 352, maxHp: 352, level: 99, acted: 1);
        h.Step(); h.Step(); h.Step();
        var staleList = h.O.LastPlayerWeapons;
        Assert.NotEmpty(staleList);

        // Also dirty the latch's own clean-move fields (a partial dip, a partial fallback streak)
        // so Reset has something real to clear on those too.
        SetActive(h.Mem, hp: 352, maxHp: 352, level: 99, acted: 0);
        h.Step(); h.Step();   // actedLow=2 (sub-threshold, still latched)
        Assert.Equal(2, ActedLow(h.Latch));

        h.Latch.Reset(h.O);

        Assert.False(h.O.PeriodOpen);
        Assert.False(h.O.Latched);
        Assert.Empty(h.O.LastPlayerWeapons);
        Assert.False(ReferenceEquals(staleList, h.O.LastPlayerWeapons));   // REPLACED, never mutated in place
        Assert.Equal(0, h.O.LastPlayerMainHand);
        Assert.Equal(default, h.O.LastActorFp);
        Assert.Equal(0, h.O.LastResolveTick);
        Assert.False(h.O.LatchResolvedEmpty);
        Assert.Equal("", h.O.ActorTag);
        Assert.Equal(0, h.O.ActedFalls);
        Assert.False(h.O.AnyTrackedWeaponThisBattle);
        Assert.False(h.O.LatchViaFallback);
        Assert.Equal(0, ActedLow(h.Latch));
        Assert.Equal(0, FallbackStreak(h.Latch));

        // A fresh acted-period opens cleanly after Reset (periodStartTick re-stamped from the
        // register's CURRENT tick, not suppressed by the pre-Reset dip).
        SetActive(h.Mem, hp: 352, maxHp: 352, level: 99, acted: 1);
        h.Step();
        Assert.True(h.O.Latched);
    }
}
