using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// Cross-turn charged-summon kill leak -- an UNTRACKED caster (summoner, no living weapon)
/// commits a charged action that matures AFTER its acted-period ends. Without the fix the
/// matured kill falls through to the normal stamp path (_lethalActor / live-latch) and leaks
/// to the next armed unit to latch. The fix snapshots the untracked committer at Charging-bit
/// 0->1 (while it is the latched empty actor) and arms _untrackedArmedTicks on 1->0 (landing),
/// so every corpse maturing inside the window is buried via _lethalUntracked regardless of the
/// current live latch.
///
/// Charging 0x08: SET proven live 2026-06-26 (charging_probe.py: summoner charge observed);
/// clear-at-landing inferred -- the untracked arm's efficacy is pending in-game verify
/// (see docs/LIVE_LEDGER.md).
/// </summary>
public class CrossTurnSummonTests
{
    // Weapons known to the tracker. V and W are tracked so kills against them show up.
    private static readonly HashSet<int> Weapons = new() { 52, 63, 73, 90 };

    private const int V = 63;   // live-latch weapon (the stale absorber without the fix)
    private const int W = 52;   // tracked-Jump weapon (U3)

    // Player band slots: summoner P at SlotsBack+1, armed Q at SlotsBack.
    private const int PSlot = Offsets.SlotsBack + 1;   // band slot 21 -- untracked summoner P
    private const int QSlot = Offsets.SlotsBack;        // band slot 20 -- armed actor Q

    // Enemy band slot 0 (enemy-side: <= EnemySlotMax=19).
    private const int EnemySlot = 0;

    // --- helpers (mirror DelayedActorTests / SummonerAttributionTests) ---

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

    private static void SetChargingBit(FakeSparseMemory m, int bandSlot, bool set = true)
    {
        long addr = Band.Entry(bandSlot);
        byte cur = m.U8s.TryGetValue(addr + Offsets.ADeadStatus, out var v) ? v : (byte)0;
        m.U8s[addr + Offsets.ADeadStatus] = set
            ? (byte)(cur | Offsets.AChargingBit)
            : (byte)(cur & ~Offsets.AChargingBit);
    }

    private static void Settle(KillTracker t, int n = 3)
    {
        for (int i = 0; i < n; i++) t.Poll(true);
    }

    // --- U1: LOAD-BEARING, NON-VACUOUS ---
    // Non-vacuity: without the fix, kills[V=63]=1 (armed Q absorbs the cross-turn kill because
    // _untrackedArmedTicks does not exist and the stamp falls to _lastPlayerWeapons.Count>0 at
    // deadStreak==1). The TDD red run MUST show kills[V]=1 before any code edit. If U1 passes
    // (Empty) before the edit, the setup is wrong -- Q must be the live latch at the death edge.

    [Fact]
    public void Cross_turn_untracked_charge_not_credited_to_live_latch()
    {
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();

        // P: untracked summoner (roster weapon 999, band weapon 0, fp 99/89/76).
        SetRoster(m, slot: 4, level: 99, brave: 89, faith: 76, weapon: 999);
        SetUnit(m, PSlot, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        // Q: armed actor (weapon V=63, fp 50/60/55).
        SetRoster(m, slot: 3, level: 50, brave: 60, faith: 55, weapon: V);
        SetUnit(m, QSlot, hp: 400, maxHp: 400, level: 50, brave: 60, faith: 55);

        // (1) P acts: latch empty, _latchResolvedEmpty=true, _lastActorFp=(99,89,76).
        //     Charging bit sets -> after edit: _chargeUntracked[PSlot]=true on the first tick.
        SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 1);
        SetChargingBit(m, PSlot, set: true);
        var t = new KillTracker(kills, m, Weapons);
        Settle(t, 3);

        // (2) Enemy seen alive (seenAlive must precede the kill edge).
        SetEnemy(m, EnemySlot, hp: 300);
        Settle(t, 3);

        // (3) End P's acted period: _latched=false.
        SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 0);
        Settle(t, KillTracker.UnfreezeTicks);

        // (4) Charging bit clears (1->0 transition): _performing[PSlot]=true, _chargeUntracked set ->
        //     after edit: arm _untrackedArmedTicks=UntrackedDelayedWindow.
        //     before edit: nothing arms (no untracked arm code).
        SetChargingBit(m, PSlot, set: false);
        t.Poll(true);

        // (5) Q acts: latches V -- the LIVE latch when the enemy dies.
        SetActive(m, hp: 400, maxHp: 400, level: 50, acted: 1);
        t.Poll(true);

        // (6) Enemy dies inside the untracked arm window.
        //     deadStreak==1: after edit: _untrackedArmedTicks>0 (FIRST branch) -> _lethalUntracked.
        //                    before edit: Count>0 (Q latched) -> _lethalActor=[V].
        //     deadStreak==3: after edit: delayed=null, _lethalUntracked -> no credit (kills empty).
        //                    before edit: culprit=[V] -> kills[V]=1 (the TDD RED).
        SetUnit(m, EnemySlot, hp: 0);
        t.Poll(true);   // deadStreak=1 -- stamp fires here
        t.Poll(true);   // deadStreak=2
        t.Poll(true);   // deadStreak=3 -> credit path

        Assert.Empty(kills);   // summoner's cross-turn kill must NOT reach Q's weapon
    }

    // --- U2: AoE not-consume -- two enemies die in the window, both uncredited ---
    // Pins: the arm is not consumed at the first _lethalUntracked stamp, so a multi-victim AoE
    // (the witnessed shape) buries every corpse that matures inside the window. The window-timer
    // expiry (not per-corpse consumption) governs the arm lifetime.

    [Fact]
    public void Cross_turn_untracked_AoE_buries_both_victims()
    {
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();

        // P: untracked summoner.
        SetRoster(m, slot: 4, level: 99, brave: 89, faith: 76, weapon: 999);
        SetUnit(m, PSlot, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        // Q: armed (weapon V=63).
        SetRoster(m, slot: 3, level: 50, brave: 60, faith: 55, weapon: V);
        SetUnit(m, QSlot, hp: 400, maxHp: 400, level: 50, brave: 60, faith: 55);

        // P acts, Charging bit sets -> snapshot _chargeUntracked[PSlot] (after edit).
        SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 1);
        SetChargingBit(m, PSlot, set: true);
        var t = new KillTracker(kills, m, Weapons);
        Settle(t, 3);

        // Two DISTINCT-identity enemies seen alive (distinct lvl/br/fa/mhp so the alive-edge
        // belt credits them independently rather than frozen-twin-dedup'ing the second).
        SetEnemy(m, EnemySlot,     hp: 300, maxHp: 400, level: 10, brave: 50, faith: 50);
        SetEnemy(m, EnemySlot + 1, hp: 280, maxHp: 380, level: 12, brave: 55, faith: 45);
        Settle(t, 3);   // both seenAlive

        // P's period ends; Charging bit clears -> arm _untrackedArmedTicks (after edit).
        SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 0);
        Settle(t, KillTracker.UnfreezeTicks);
        SetChargingBit(m, PSlot, set: false);
        t.Poll(true);   // arm fires (after edit)

        // Q latches V (live latch when both enemies die).
        SetActive(m, hp: 400, maxHp: 400, level: 50, acted: 1);
        t.Poll(true);

        // Both enemies die simultaneously inside the window.
        SetUnit(m, EnemySlot,     hp: 0);
        SetUnit(m, EnemySlot + 1, hp: 0, maxHp: 380, level: 12, brave: 55, faith: 45);
        t.Poll(true);   // deadStreak=1 for both; both _lethalUntracked stamped (after edit)
        t.Poll(true);   // deadStreak=2
        t.Poll(true);   // deadStreak=3 -> both no-credit (arm NOT consumed at first stamp)

        Assert.Empty(kills);   // neither AoE victim credited to Q's weapon
    }

    // --- U3: tracked-delayed actor WINS over the active untracked arm ---
    // Non-vacuity vs "untracked always buries": removing the `delayed == null` guard in ScanCorpses
    // would suppress the delayed actor and leave kills empty instead of kills[W]=1. This pins that
    // the ordering (ConsumeDelayedCulprit FIRST, then the _lethalUntracked branch) is load-bearing.

    [Fact]
    public void Tracked_delayed_actor_wins_over_active_untracked_arm()
    {
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        int jSlot = Offsets.SlotsBack + 2;   // band slot 22 -- tracked jumper J

        // P: untracked summoner (fp 99/89/76).
        SetRoster(m, slot: 4, level: 99, brave: 89, faith: 76, weapon: 999);
        SetUnit(m, PSlot, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        // J: tracked jumper (weapon W=52, fp 40/45/48).
        SetRoster(m, slot: 5, level: 40, brave: 45, faith: 48, weapon: W);
        SetUnit(m, jSlot, hp: 380, maxHp: 380, level: 40, brave: 45, faith: 48);

        // (1) P acts (untracked): snapshot _chargeUntracked[PSlot] (after edit). P's period ends.
        SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 1);
        SetChargingBit(m, PSlot, set: true);
        var t = new KillTracker(kills, m, Weapons);
        Settle(t, 3);

        // (2) Enemy seen alive.
        SetEnemy(m, EnemySlot, hp: 300);
        Settle(t, 3);

        // (3) End P's period.
        SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 0);
        Settle(t, KillTracker.UnfreezeTicks);

        // (4) J acts (tracked): latch W=52. Jump bit sets ->
        //     snapshot _chargeWeapons[jSlot]=[W] (Count>0 + fp match).
        SetActive(m, hp: 380, maxHp: 380, level: 40, acted: 1);
        SetJumpBit(m, jSlot, set: true);
        t.Poll(true);   // J latches W; tracked snapshot fires

        // (5) J's period ends.
        SetActive(m, hp: 380, maxHp: 380, level: 40, acted: 0);
        Settle(t, KillTracker.UnfreezeTicks);

        // (6) Charging bit clears -> after edit: arm _untrackedArmedTicks=UntrackedDelayedWindow.
        SetChargingBit(m, PSlot, set: false);
        t.Poll(true);

        // (7) Jump bit clears -> arm _delayedActor=[W], _delayedArmedTicks=DelayedActorWindow.
        SetJumpBit(m, jSlot, set: false);
        t.Poll(true);

        // (8) Enemy dies within BOTH windows.
        //     After edit: deadStreak==1: _untrackedArmedTicks>0 -> _lethalUntracked stamped.
        //     deadStreak==3: delayed=[W], delayed!=null -> skip _lethalUntracked -> kills[W]=1.
        //     Without the delayed==null guard (broken "untracked always buries"): no-credit fires
        //     first and kills empty -- that's the non-vacuity proof.
        SetUnit(m, EnemySlot, hp: 0);
        t.Poll(true);   // deadStreak=1
        t.Poll(true);   // deadStreak=2
        t.Poll(true);   // deadStreak=3 -> delayed=[W] wins over _lethalUntracked

        Assert.Equal(1, kills.GetValueOrDefault(W));   // tracked Jump wins over untracked arm
        Assert.Equal(1, kills.Count);
    }

    // --- U4: window decay regression (arm decays; later kill credits the live latch normally) ---
    // Mirrors DelayedActorTests T4. Pins that _untrackedArmedTicks does not bury forever -- only
    // within the UntrackedDelayedWindow tick window from the landing edge.

    [Fact]
    public void Untracked_arm_decays_and_later_kill_credits_live_latch()
    {
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();

        // P: untracked summoner.
        SetRoster(m, slot: 4, level: 99, brave: 89, faith: 76, weapon: 999);
        SetUnit(m, PSlot, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        // Q: armed (weapon V=63).
        SetRoster(m, slot: 3, level: 50, brave: 60, faith: 55, weapon: V);
        SetUnit(m, QSlot, hp: 400, maxHp: 400, level: 50, brave: 60, faith: 55);

        // P acts; arm fires on bit-clear.
        SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 1);
        SetChargingBit(m, PSlot, set: true);
        var t = new KillTracker(kills, m, Weapons);
        Settle(t, 3);   // latch empty; _chargeUntracked[PSlot]=true (after edit)

        // P's period ends; Charging bit clears -> _untrackedArmedTicks=UntrackedDelayedWindow (after edit).
        SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 0);
        Settle(t, KillTracker.UnfreezeTicks);
        SetChargingBit(m, PSlot, set: false);
        t.Poll(true);   // arm _untrackedArmedTicks=UntrackedDelayedWindow (after edit)

        // Q latches V.
        SetActive(m, hp: 400, maxHp: 400, level: 50, acted: 1);
        t.Poll(true);

        // Poll UntrackedDelayedWindow+1 ticks with no corpse -> arm decays to 0.
        for (int i = 0; i < Tuning.UntrackedDelayedWindow + 1; i++) t.Poll(true);

        // A kill happens after the window expires: must credit the live latch (V), NOT bury.
        SetEnemy(m, EnemySlot, hp: 300);
        Settle(t, 3);   // seenAlive
        SetUnit(m, EnemySlot, hp: 0);
        Settle(t, 3);   // deadStreak -> credit

        Assert.Equal(1, kills.GetValueOrDefault(V));   // arm expired; live latch V credited normally
        Assert.False(kills.ContainsKey(W));
    }

    // --- U5: bystander fp-mismatch and !Band.IsValid guard do not phantom-arm ---
    // (a) A DIFFERENT slot's Charging bit sets while P is latched, but that slot's fp !=
    //     _lastActorFp -> no untracked snapshot -> no arm on bit-clear. Mirrors T8.
    // (b) P's slot goes !Band.IsValid after the snapshot -> _chargeUntracked cleared; bit clears
    //     after restore -> no phantom arm. Mirrors T7.
    // Both sub-cases assert that a later normal kill credits the live armed latch correctly.

    [Fact]
    public void Bystander_fp_mismatch_and_invalid_slot_do_not_phantom_arm()
    {
        // --- Part (a): bystander slot, fp mismatch ---
        {
            var kills = new Dictionary<int, int>();
            var m = new FakeSparseMemory();
            int bSlot = Offsets.SlotsBack + 2;   // band slot 22 -- bystander B

            SetRoster(m, slot: 4, level: 99, brave: 89, faith: 76, weapon: 999);
            SetUnit(m, PSlot, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
            SetRoster(m, slot: 3, level: 50, brave: 60, faith: 55, weapon: V);
            SetUnit(m, QSlot, hp: 400, maxHp: 400, level: 50, brave: 60, faith: 55);
            // B: bystander, fp 40/45/48 != _lastActorFp (99/89/76).
            SetUnit(m, bSlot, hp: 300, maxHp: 300, level: 40, brave: 45, faith: 48);

            // P acts -> _lastActorFp=(99,89,76).
            SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 1);
            var t = new KillTracker(kills, m, Weapons);
            Settle(t, 3);   // latch empty; _lastActorFp=(99,89,76)

            SetEnemy(m, EnemySlot, hp: 300);
            Settle(t, 3);

            // B's Charging bit sets while P is latched: fp mismatch -> no untracked snapshot for B.
            SetChargingBit(m, bSlot, set: true);
            t.Poll(true);

            // B's bit clears: _chargeUntracked[bSlot]=false -> no arm. Normal kill follows.
            SetChargingBit(m, bSlot, set: false);
            t.Poll(true);

            // P's period ends; Q latches V.
            SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 0);
            Settle(t, KillTracker.UnfreezeTicks);
            SetActive(m, hp: 400, maxHp: 400, level: 50, acted: 1);
            t.Poll(true);   // Q latches V

            SetUnit(m, EnemySlot, hp: 0);
            Settle(t, 3);   // deadStreak -> credit

            // No phantom arm from bystander B -> Q's kill credited normally.
            Assert.Equal(1, kills.GetValueOrDefault(V));
            Assert.False(kills.ContainsKey(W));
        }

        // --- Part (b): P's slot goes !Band.IsValid after snapshot ---
        {
            var kills = new Dictionary<int, int>();
            var m = new FakeSparseMemory();

            SetRoster(m, slot: 4, level: 99, brave: 89, faith: 76, weapon: 999);
            SetUnit(m, PSlot, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
            SetRoster(m, slot: 3, level: 50, brave: 60, faith: 55, weapon: V);
            SetUnit(m, QSlot, hp: 400, maxHp: 400, level: 50, brave: 60, faith: 55);

            // P acts, Charging bit sets -> after edit: _chargeUntracked[PSlot]=true.
            SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 1);
            SetChargingBit(m, PSlot, set: true);
            var t = new KillTracker(kills, m, Weapons);
            Settle(t, 3);   // latch empty; _performing[PSlot]=true, _chargeUntracked[PSlot]=true

            SetEnemy(m, EnemySlot, hp: 300);
            Settle(t, 3);

            // P's band slot goes invalid (level=0): !IsValid guard clears _performing + _chargeUntracked.
            long pAddr = Band.Entry(PSlot);
            m.U8s[pAddr + Offsets.ALevel] = 0;
            t.Poll(true);   // !IsValid: _performing[PSlot]=false, _chargeUntracked[PSlot]=false (after edit)

            // Restore P and clear the bit: _performing was cleared, _chargeUntracked cleared -> no arm.
            // Without the guard: the retained _performing=true + snapshot would arm here (phantom).
            SetUnit(m, PSlot, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
            SetChargingBit(m, PSlot, set: false);
            t.Poll(true);   // _performing=false, _chargeUntracked=false -> no arm

            // P's period ends; Q latches V.
            SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 0);
            Settle(t, KillTracker.UnfreezeTicks);
            SetActive(m, hp: 400, maxHp: 400, level: 50, acted: 1);
            t.Poll(true);   // Q latches V

            SetUnit(m, EnemySlot, hp: 0);
            Settle(t, 3);   // deadStreak -> credit

            // !IsValid guard suppressed phantom arm -> Q's kill credited normally.
            Assert.Equal(1, kills.GetValueOrDefault(V));
        }
    }

    // --- U-negative: Charging bit held SET throughout, never 1->0 -> arm never fires ---
    // The arm requires a _performing[s] && !delayed transition (bit going 0 from 1). If the bit is
    // held set from the start and never clears, _performing stays true but !delayed is never true
    // -> _untrackedArmedTicks stays 0 -> Q's normal kill is credited, not buried.
    // Non-vacuous: a naive implementation that armed while the bit is HELD (removing the 1->0 gate)
    // would set _untrackedArmedTicks and bury Q's kill -> kills empty instead of kills[V]=1.

    [Fact]
    public void Charging_bit_held_set_never_arms_untracked_window()
    {
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();

        SetRoster(m, slot: 4, level: 99, brave: 89, faith: 76, weapon: 999);
        SetUnit(m, PSlot, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetRoster(m, slot: 3, level: 50, brave: 60, faith: 55, weapon: V);
        SetUnit(m, QSlot, hp: 400, maxHp: 400, level: 50, brave: 60, faith: 55);

        // P acts; Charging bit SET from the very first poll and STAYS set (never 1->0 transition).
        // _performing[PSlot] goes true on first tick; never gets the !delayed=true arm check.
        SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 1);
        SetChargingBit(m, PSlot, set: true);
        var t = new KillTracker(kills, m, Weapons);
        Settle(t, 3);   // latch empty; _chargeUntracked[PSlot]=true; bit held -> no arm

        SetEnemy(m, EnemySlot, hp: 300);
        Settle(t, 3);

        // Bit stays SET throughout. P's period ends; Q latches V normally.
        SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 0);
        Settle(t, KillTracker.UnfreezeTicks);
        SetActive(m, hp: 400, maxHp: 400, level: 50, acted: 1);
        t.Poll(true);   // Q latches V; _untrackedArmedTicks still 0 (no arm fired)

        SetUnit(m, EnemySlot, hp: 0);
        Settle(t, 3);   // deadStreak -> credit via normal lethal-stamp path

        // Arm never fired -> Q's kill credited normally via _lethalActor stamp.
        Assert.Equal(1, kills.GetValueOrDefault(V));
    }
}
