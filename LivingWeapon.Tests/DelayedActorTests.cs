using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// Delayed-action kill attribution -- Dragoon Jump commits on the jumper's turn but lands
/// a full turn later. An intervening unit's turn overwrites the actor latch, causing the
/// stale latch to credit the wrong weapon. The fix snapshots the committer's weapon when
/// the Jump bit (+0x45/0x04) sets, arms it when the bit clears, and credits it over the
/// stale latch within Tuning.DelayedActorWindow ticks.
/// Jump 0x04: PROVEN LIVE 2026-06-26 (watchweapon trace). Charging 0x08: same mechanism,
/// live-unverified.
/// </summary>
public class DelayedActorTests
{
    // Weapons known to the tracker. W and V are tracked so kills against them show up.
    private static readonly HashSet<int> Weapons = new() { 52, 63, 73, 90 };

    private const int W = 52;   // jumper's weapon
    private const int V = 63;   // stale-latch weapon (the wrong one without the fix)

    // Player band slots (player-side: index >= SlotsBack=20).
    private const int PSlot = Offsets.SlotsBack;      // band slot 20 -- the jumper (P)
    private const int QSlot = Offsets.SlotsBack + 1;  // band slot 21 -- the intervenor (Q)

    // Enemy band slot 0 is safe (enemy-side: <= EnemySlotMax=19).
    private const int EnemySlot = 0;

    // --- helpers ---

    private static void SetActive(FakeSparseMemory m, int hp, int maxHp, int level, int acted = 1)
    {
        m.U16s[Offsets.TurnQueue + Offsets.TqTeam]  = 0;
        m.U16s[Offsets.TurnQueue + Offsets.TqHp]    = (ushort)hp;
        m.U16s[Offsets.TurnQueue + Offsets.TqMaxHp] = (ushort)maxHp;
        m.U16s[Offsets.TurnQueue + Offsets.TqLevel] = (ushort)level;
        m.U8s[Offsets.Acted] = (byte)acted;
    }

    private static void SetRoster(FakeSparseMemory m, int slot, int level, int brave, int faith, int weapon)
        => MemSeats.SeatRoster(m, slot, level, brave, faith, weapon);

    private static void SetUnit(FakeSparseMemory m, int bandSlot, int hp, int maxHp = 400,
                                int level = 10, int brave = 50, int faith = 50)
        => MemSeats.SeatBand(m, bandSlot, weapon: 0, lvl: level, br: brave, fa: faith,
                             gx: 5, gy: 5, hp: hp, maxHp: maxHp);

    private static void SetEnemy(FakeSparseMemory m, int bandSlot, int hp, int maxHp = 400,
                                 int level = 10, int brave = 50, int faith = 50)
    {
        MemSeats.SeatBand(m, bandSlot, weapon: 0, lvl: level, br: brave, fa: faith,
                          gx: 5, gy: 5, hp: hp, maxHp: maxHp);
        if (bandSlot <= Offsets.EnemySlotMax)
        {
            long s = Offsets.ArrayReadBase + (long)bandSlot * Offsets.ArrayStride;
            m.U16s[s + Offsets.AInBattle] = 1;
            m.U8s[s + Offsets.ALevel]     = (byte)level;
            m.U8s[s + Offsets.ABrave]     = (byte)brave;
            m.U8s[s + Offsets.AFaith]     = (byte)faith;
            m.U16s[s + Offsets.AMaxHp]    = (ushort)maxHp;
        }
    }

    private static void SetJumpBit(FakeSparseMemory m, int bandSlot, bool set = true)
    {
        long addr = Band.Entry(bandSlot);
        byte cur = m.U8s.TryGetValue(addr + Offsets.ADeadStatus, out var v) ? v : (byte)0;
        m.U8s[addr + Offsets.ADeadStatus] = set
            ? (byte)(cur | Offsets.AJumpBit)
            : (byte)(cur & ~Offsets.AJumpBit);
    }

    private static void Settle(KillTracker t, int n = 3)
    {
        for (int i = 0; i < n; i++) t.Poll(true);
    }

    // --- T1: LOAD-BEARING (both directions) ---

    /// <summary>
    /// P (weapon W) commits a Jump while latched: bit sets, W is snapshotted. Q then acts,
    /// moving the latch to V. P's bit clears (jump lands). Enemy corpse matures. The DELAYED
    /// actor (W) must be credited, not the stale latch/stamp (V).
    ///
    /// Negative proof: without ConsumeDelayedCulprit() the culprit chain falls through to
    /// _lethalActor (V) and the assertions flip: kills[V]=1, kills[W] absent.
    /// </summary>
    [Fact]
    public void Delayed_actor_overrides_stale_latch_on_jump()
    {
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();

        // P: jumper, weapon W.
        SetRoster(m, slot: 3, level: 99, brave: 89, faith: 76, weapon: W);
        SetUnit(m, PSlot, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        // Q: intervenor, weapon V.
        SetRoster(m, slot: 4, level: 50, brave: 60, faith: 55, weapon: V);
        SetUnit(m, QSlot, hp: 400, maxHp: 400, level: 50, brave: 60, faith: 55);

        // (1) P acts: latch W, _lastActorFp = (99,89,76).
        SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 1);
        SetJumpBit(m, PSlot, set: true);   // Jump bit set at commit
        var t = new KillTracker(kills, m, Weapons);
        Settle(t, 3);   // latch fires tick 1; TrackDelayed snapshots W on first poll

        // (2) Enemy seen alive (seenAlive).
        SetEnemy(m, EnemySlot, hp: 300, maxHp: 400, level: 10, brave: 50, faith: 50);
        Settle(t, 3);   // seenAlive

        // (3) End P's acted period, Q latches V.
        SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 0);
        Settle(t, KillTracker.UnfreezeTicks);   // debounce fall; enemy still alive -> seenAlive maintained
        SetActive(m, hp: 400, maxHp: 400, level: 50, acted: 1);
        t.Poll(true);   // Q latches V

        // (4) Jump lands: bit clears -> TrackDelayed arms W.
        SetJumpBit(m, PSlot, set: false);
        t.Poll(true);   // arm fires; enemy still alive

        // (5) Kill enemy -- 3 dead ticks to confirm.
        //     _lethalActor[EnemySlot] stamps at deadStreak=1 while Q is latched (stamps V).
        //     ConsumeDelayedCulprit() must override with W.
        SetUnit(m, EnemySlot, hp: 0, maxHp: 400, level: 10, brave: 50, faith: 50);
        t.Poll(true);   // deadStreak=1, stamp _lethalActor=V
        t.Poll(true);   // deadStreak=2
        bool changed = t.Poll(true);   // deadStreak=3 -> credit

        Assert.True(changed);
        Assert.Equal(1, kills.GetValueOrDefault(W));   // jumper's weapon earns the kill
        Assert.False(kills.ContainsKey(V));             // NOT the stale intervenor (negative: removing
                                                        // ConsumeDelayedCulprit flips this to V=1, W absent)
    }

    // --- T2: regression pin -- normal credit path unchanged when nobody jumps ---

    [Fact]
    public void Normal_kill_credits_the_latch_when_no_delayed_action()
    {
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        SetRoster(m, slot: 3, level: 99, brave: 89, faith: 76, weapon: W);
        SetUnit(m, PSlot, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 1);
        // No Jump bit set anywhere.
        var t = new KillTracker(kills, m, Weapons);
        Settle(t, 3);   // latch W

        SetEnemy(m, EnemySlot, hp: 300);
        Settle(t, 3);   // seenAlive
        SetUnit(m, EnemySlot, hp: 0);
        Settle(t, 3);   // deadStreak -> credit

        Assert.Equal(1, kills.GetValueOrDefault(W));
        Assert.False(kills.ContainsKey(V));
    }

    // --- T3: jumper's weapon untracked at commit -- no snapshot, no arm, no crash ---

    [Fact]
    public void No_snapshot_when_player_weapons_empty_at_jump_commit()
    {
        // P has no roster entry -> resolver returns empty -> _lastPlayerWeapons={}. Jump bit
        // sets but snapshot guard (_lastPlayerWeapons.Count > 0) blocks it. Bit clears ->
        // _chargeWeapons null -> no arm. Enemy dies -> pending (no latch). No crash.
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        // No SetRoster for P -> resolver returns empty set.
        SetUnit(m, PSlot, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 1);
        SetJumpBit(m, PSlot, set: true);
        var t = new KillTracker(kills, m, Weapons);
        Settle(t, 3);   // no latch (no roster match); TrackDelayed: weapons empty, no snapshot

        SetEnemy(m, EnemySlot, hp: 300);
        Settle(t, 3);   // seenAlive

        SetJumpBit(m, PSlot, set: false);
        t.Poll(true);   // bit clears; _chargeWeapons null -> no arm

        SetUnit(m, EnemySlot, hp: 0);
        Settle(t, 3);   // dead streak -> pending (no latch)

        Assert.Empty(kills);   // no credit (no actor), no crash
    }

    // --- T4: arm decays before any corpse matures; later kill credits the live latch ---

    [Fact]
    public void Delayed_actor_arm_decays_and_later_kill_credits_live_latch()
    {
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        // P: jumper (W=52).
        SetRoster(m, slot: 3, level: 99, brave: 89, faith: 76, weapon: W);
        SetUnit(m, PSlot, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        // Q: later actor (V=63).
        SetRoster(m, slot: 4, level: 50, brave: 60, faith: 55, weapon: V);
        SetUnit(m, QSlot, hp: 400, maxHp: 400, level: 50, brave: 60, faith: 55);

        SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 1);
        SetJumpBit(m, PSlot, set: true);
        var t = new KillTracker(kills, m, Weapons);
        Settle(t, 3);   // latch W, snapshot W

        // Q latches V.
        SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 0);
        Settle(t, KillTracker.UnfreezeTicks);
        SetActive(m, hp: 400, maxHp: 400, level: 50, acted: 1);
        t.Poll(true);   // Q latches V

        // Jump clears -> arm W (DelayedActorWindow=12).
        SetJumpBit(m, PSlot, set: false);
        t.Poll(true);   // arm fires (_delayedArmedTicks=12)

        // Poll DelayedActorWindow+1 times with no corpse -> arm decays to 0.
        for (int i = 0; i < Tuning.DelayedActorWindow + 1; i++) t.Poll(true);

        // Now a kill happens -- credit must go to the live latch (V), not the expired W.
        SetEnemy(m, EnemySlot, hp: 300);
        Settle(t, 3);   // seenAlive
        SetUnit(m, EnemySlot, hp: 0);
        Settle(t, 3);   // deadStreak -> credit

        Assert.Equal(1, kills.GetValueOrDefault(V));   // live latch V credited
        Assert.False(kills.ContainsKey(W));             // expired delayed actor W NOT credited
    }

    // --- T6: bit set at battle start, never transitions 1->0 -- never arms ---

    [Fact]
    public void Jump_bit_set_from_battle_start_never_arms_delayed_actor()
    {
        // The bit starts SET (phantom / stuck at start). _performing[PSlot] begins false, so
        // a true->false transition never fires -- the arm never triggers even if the bit clears
        // from a start-state that was never observed 0 first.
        // Here the bit stays set throughout; assert normal kill credits the latch.
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        SetRoster(m, slot: 3, level: 99, brave: 89, faith: 76, weapon: W);
        SetUnit(m, PSlot, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 1);
        SetJumpBit(m, PSlot, set: true);   // bit set from the very first poll -- never was 0
        var t = new KillTracker(kills, m, Weapons);
        Settle(t, 3);   // latch W; TrackDelayed: _performing starts false, sets true, no transition

        // Jump bit stays set (never 1->0 transition). Normal kill.
        SetEnemy(m, EnemySlot, hp: 300);
        Settle(t, 3);
        SetUnit(m, EnemySlot, hp: 0);
        Settle(t, 3);

        // Credit via normal stamp/latch (W), not a spurious delayed arm.
        Assert.Equal(1, kills.GetValueOrDefault(W));
    }

    // --- T7: slot goes !Band.IsValid after snapshot but before bit clears -- no phantom arm ---

    [Fact]
    public void Slot_going_invalid_after_snapshot_clears_charge_and_suppresses_arm()
    {
        // Non-vacuous setup: Q (weapon V) is the live latch at kill time, so the WRONG path (a
        // phantom arm of W, retained when the !IsValid guard is removed) credits a DIFFERENT weapon
        // (W) than the RIGHT path (live latch V). The enemy is seen alive BEFORE the arm so the
        // kill lands well inside DelayedActorWindow in the broken case.
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        SetRoster(m, slot: 3, level: 99, brave: 89, faith: 76, weapon: W);
        SetUnit(m, PSlot, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetRoster(m, slot: 4, level: 50, brave: 60, faith: 55, weapon: V);
        SetUnit(m, QSlot, hp: 400, maxHp: 400, level: 50, brave: 60, faith: 55);

        SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 1);
        SetJumpBit(m, PSlot, set: true);
        var t = new KillTracker(kills, m, Weapons);
        Settle(t, 3);   // latch W; snapshot W into _chargeWeapons[PSlot], _performing[PSlot]=true

        SetEnemy(m, EnemySlot, hp: 300);
        Settle(t, 3);   // enemy seenAlive (established before any arm)

        // P's band slot goes invalid (level 0) BEFORE the bit clears -> the guard must drop the
        // snapshot AND _performing. Without the guard the slot is skipped and both are retained.
        long addr = Band.Entry(PSlot);
        m.U8s[addr + Offsets.ALevel] = 0;
        t.Poll(true);   // !IsValid -> _performing[PSlot]=false, _chargeWeapons[PSlot]=null (the guard)

        // Restore P and clear the bit (the jump "lands" after the invalid gap). Without the guard,
        // the retained _performing[PSlot]=true + _chargeWeapons[PSlot]=W arms W here (phantom).
        SetUnit(m, PSlot, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetJumpBit(m, PSlot, set: false);
        t.Poll(true);

        // End P's period; Q latches V -- the live latch (and lethal stamp) at kill time.
        SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 0);
        Settle(t, KillTracker.UnfreezeTicks);
        SetActive(m, hp: 400, maxHp: 400, level: 50, acted: 1);
        t.Poll(true);   // Q latches V

        SetUnit(m, EnemySlot, hp: 0);
        Settle(t, 3);   // deadStreak -> credit

        Assert.Equal(1, kills.GetValueOrDefault(V));   // live latch V -- the guard suppressed the phantom W arm
        Assert.False(kills.ContainsKey(W));             // removing the !IsValid guard flips this to W=1
    }

    // --- T8: Jump bit set on a slot whose fp != _lastActorFp -- no snapshot, no arm ---

    [Fact]
    public void No_snapshot_when_jumping_slot_fingerprint_does_not_match_last_actor()
    {
        // Q (V) is the latched actor when P's Jump bit sets, but P's fp (99,89,76) != _lastActorFp
        // (50,60,55). With the fp-match guard: no snapshot, no arm. WITHOUT the guard: P snapshots
        // the CURRENT latch (V) and arms it. A THIRD actor R (weapon X) then becomes the live latch
        // before the kill, so the wrong path (armed V) credits a DIFFERENT weapon than the right
        // path (live latch X) -- making the guard observable.
        const int X = 73;                          // R's weapon (distinct from W and V; in Weapons)
        int rSlot = Offsets.SlotsBack + 2;         // band slot 22 -- the third actor R
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        SetRoster(m, slot: 3, level: 99, brave: 89, faith: 76, weapon: W);
        SetUnit(m, PSlot, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetRoster(m, slot: 4, level: 50, brave: 60, faith: 55, weapon: V);
        SetUnit(m, QSlot, hp: 400, maxHp: 400, level: 50, brave: 60, faith: 55);
        SetRoster(m, slot: 5, level: 40, brave: 45, faith: 48, weapon: X);
        SetUnit(m, rSlot, hp: 380, maxHp: 380, level: 40, brave: 45, faith: 48);

        // Q acts -- latch V, _lastActorFp = (50,60,55).
        SetActive(m, hp: 400, maxHp: 400, level: 50, acted: 1);
        var t = new KillTracker(kills, m, Weapons);
        Settle(t, 3);   // latch V

        SetEnemy(m, EnemySlot, hp: 300);
        Settle(t, 3);   // enemy seenAlive (before any arm)

        // P's Jump bit sets while Q is latched: fp mismatch -> no snapshot (guard). Without the
        // guard, P snapshots the current latch V.
        SetJumpBit(m, PSlot, set: true);
        t.Poll(true);

        // P's bit clears: with the guard _chargeWeapons[PSlot] is null -> no arm; without it, arm V.
        SetJumpBit(m, PSlot, set: false);
        t.Poll(true);

        // End Q's period; R latches X -- the live latch (and lethal stamp) at kill time.
        SetActive(m, hp: 400, maxHp: 400, level: 50, acted: 0);
        Settle(t, KillTracker.UnfreezeTicks);
        SetActive(m, hp: 380, maxHp: 380, level: 40, acted: 1);
        t.Poll(true);   // R latches X

        SetUnit(m, EnemySlot, hp: 0);
        Settle(t, 3);   // deadStreak -> credit

        Assert.Equal(1, kills.GetValueOrDefault(X));   // live latch X -- the fp-match guard blocked the wrong V arm
        Assert.False(kills.ContainsKey(V));             // removing the fp-match guard flips this to V=1
    }
}
