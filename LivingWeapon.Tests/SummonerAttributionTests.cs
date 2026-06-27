using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// Untracked-weapon AoE kill attribution -- when a summoner/dancer holding no living weapon
/// casts an AoE that kills an enemy, the kill must go UNCREDITED rather than falling through
/// to FirstKillFallback and crediting a stale armed unit that acted turns ago.
///
/// Root cause: _lastPlayerWeapons is empty for BOTH "RESOLVED-but-untracked" and
/// "never-resolved" (genuine first-kill) states. The two flags that distinguish them:
///   _latchResolvedEmpty -- true when the most recent successful TryResolveActingPlayer
///                          returned an empty weapon set (untracked actor). Drives the
///                          FirstKillFallback bail so the fallback stays inert after an
///                          untracked turn. Sticky until the next successful resolve.
///   _lethalUntracked[s] -- stamped at the alive->dead edge (deadStreak 0->1) when the
///                          latch is live (_latched=true) AND the latch is empty
///                          (_latchResolvedEmpty=true). Freezes the no-credit verdict at the
///                          stamp edge so a later armed re-latch cannot steal the credit.
///                          The && _latched guard prevents false stamps during the sticky
///                          interval when _latchResolvedEmpty is true but the untracked
///                          actor's period has already ended (T2 blocker).
/// </summary>
public class SummonerAttributionTests
{
    private static readonly HashSet<int> Weapons = new() { 35, 73, 90 };

    // Tracked weapon ids
    private const int ArmedWeapon  = 35;   // pre-summoner actor A
    private const int BWeapon      = 73;   // later armed unit B (T2/T3)
    private const int JumperWeapon = 90;   // delayed-action jumper P (T4)

    // Untracked weapon (not in Weapons -> empty hands on resolve)
    private const int UntrackedWeapon = 999;

    // Band slots -- player-side (index >= SlotsBack=20)
    private const int ASlot = Offsets.SlotsBack;       // slot 20 -- armed unit A / jumper P
    private const int SSlot = Offsets.SlotsBack + 1;   // slot 21 -- untracked summoner S / unarmed U
    private const int BSlot = Offsets.SlotsBack + 2;   // slot 22 -- later armed unit B (T3)

    // Enemy band slot (<=EnemySlotMax=19): the AoE victim
    private const int EnemySlot = 0;

    // --- helpers (mirror DelayedActorTests.cs / KillTrackerTests.cs style) ---

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

    // --- T1: HEADLINE -- the witnessed bug ---

    [Fact]
    public void Untracked_summoner_AoE_kill_is_not_credited_to_stale_armed_actor()
    {
        // T1 HEADLINE: A (weapon 35) acts and latches. A's period ends. Untracked summoner S
        // (weapon=999) acts (empty latch: _latchResolvedEmpty=true). Enemy dies while S is
        // latched -> _lethalUntracked stamped. S's period ends; A is stable at acted=0.
        // Pre-fix: FirstKillFallback resolves A (3 consecutive non-empty resolves) -> latch ->
        //   credit kills[ArmedWeapon]=1.
        // Fix: _lethalUntracked fires no-credit at deadStreak==3 BEFORE the corpse ever goes
        //   pending, so for THIS exact timing the stamp alone keeps kills empty and the
        //   FirstKillFallback bail is masked (exercised but not solely necessary here). The
        //   _latchResolvedEmpty bail is pinned independently by T5 (the slow case where the
        //   corpse goes pending and only the bail blocks the fallback).
        // Non-vacuous: removing both fix parts makes kills[ArmedWeapon]=1.
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();

        // A: tracked (weapon 35).
        SetRoster(m, slot: 3, level: 90, brave: 80, faith: 70, weapon: ArmedWeapon);
        SetUnit(m, ASlot, hp: 352, maxHp: 352, level: 90, brave: 80, faith: 70);

        // S: untracked summoner (weapon 999 not in Weapons -- empty hands on resolve).
        SetRoster(m, slot: 0, level: 50, brave: 55, faith: 60, weapon: UntrackedWeapon);
        SetUnit(m, SSlot, hp: 400, maxHp: 400, level: 50, brave: 55, faith: 60);

        // Phase 1: A acts, latches ArmedWeapon. _latchResolvedEmpty=false.
        SetActive(m, hp: 352, maxHp: 352, level: 90, acted: 1);
        var t = new KillTracker(kills, m, Weapons);
        Settle(t, 3);

        // Phase 2: A's period ends. _latched=false.
        SetActive(m, hp: 352, maxHp: 352, level: 90, acted: 0);
        Settle(t, KillTracker.UnfreezeTicks);

        // Phase 3: enemy seen alive (seenAlive must precede the kill edge).
        SetEnemy(m, EnemySlot, hp: 300);
        Settle(t, 3);

        // Phase 4: S acts AND enemy dies on the first poll (S is the latch when deadStreak==1).
        // _latched=true, _latchResolvedEmpty=true (S resolves empty) -> _lethalUntracked stamped.
        SetActive(m, hp: 400, maxHp: 400, level: 50, acted: 1);
        SetUnit(m, EnemySlot, hp: 0);
        t.Poll(true);   // S resolves empty, deadStreak=1, _lethalUntracked[EnemySlot]=true
        t.Poll(true);   // deadStreak=2

        // Phase 5: S's period ends; A is the stable active struct at acted=0.
        // Run 4 polls: deadStreak=3 (no-credit fires with fix); without fix, fallback builds
        // a streak of 3 resolving A and credits ArmedWeapon at poll 4.
        SetActive(m, hp: 352, maxHp: 352, level: 90, acted: 0);
        for (int i = 0; i < 4; i++) t.Poll(true);

        Assert.Empty(kills);   // summoner's kill -- A must NOT be credited
    }

    // --- T2: BLOCKER regression pin -- && _latched guard is load-bearing ---

    [Fact]
    public void Armed_archer_credits_pending_corpse_even_when_empty_latch_is_sticky()
    {
        // T2 BLOCKER: unarmed U acts first (_latchResolvedEmpty=true). U's period ends
        // (_latched=false; _latchResolvedEmpty stays STICKY true). An armed archer's enemy
        // corpse dies BEFORE the archer's acted edge (_latched=false at deadStreak==1).
        // Without && _latched: _lethalUntracked stamps at deadStreak==1 -> no-credit -> archer's
        //   kill silently dropped.
        // With && _latched: stamp guard fails (_latched=false) -> corpse goes pending ->
        //   archer acts -> pending corpse credited to BWeapon.
        // Non-vacuous: removing && _latched flips kills[BWeapon]=1 to kills.Empty().
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();

        // U: unarmed (weapon 999, untracked).
        SetRoster(m, slot: 1, level: 40, brave: 50, faith: 60, weapon: UntrackedWeapon);
        SetUnit(m, SSlot, hp: 300, maxHp: 300, level: 40, brave: 50, faith: 60);

        // Archer (B): tracked (weapon 73=BWeapon).
        SetRoster(m, slot: 0, level: 90, brave: 80, faith: 70, weapon: BWeapon);
        SetUnit(m, ASlot, hp: 400, maxHp: 400, level: 90, brave: 80, faith: 70);

        var t = new KillTracker(kills, m, Weapons);

        // Turn 1: U acts (untracked) -> empty latch. _latchResolvedEmpty=true, _latched=true.
        SetActive(m, hp: 300, maxHp: 300, level: 40, acted: 1);
        Settle(t, 3);

        // U's period ends: _latched=false, _latchResolvedEmpty stays sticky.
        SetActive(m, hp: 300, maxHp: 300, level: 40, acted: 0);
        Settle(t, KillTracker.UnfreezeTicks);

        // Enemy alive (seenAlive).
        SetEnemy(m, EnemySlot, hp: 300);
        Settle(t, 3);

        // Enemy dies while _latched=false: deadStreak==1 fires with _latchResolvedEmpty=true
        // but _latched=false. The && _latched guard prevents the _lethalUntracked stamp.
        // Corpse goes pending at deadStreak==3 (no culprit).
        SetUnit(m, EnemySlot, hp: 0);
        Settle(t, 3);   // deadStreak 1->3; corpse enters pending

        // Turn 2: archer acts -> latches BWeapon. Pending corpse credited via live latch.
        SetActive(m, hp: 400, maxHp: 400, level: 90, acted: 1);
        t.Poll(true);

        Assert.Equal(1, kills.GetValueOrDefault(BWeapon));   // archer's kill -- NOT suppressed
    }

    // --- T3: LEAK pin -- _lethalUntracked freezes verdict; later armed latch can't steal ---

    [Fact]
    public void Lethal_untracked_stamp_blocks_credit_even_after_armed_unit_re_latches()
    {
        // T3 LEAK pin: _lethalUntracked[s] is stamped at deadStreak==1 while S is latched.
        // Off-field polls end S's period (dead streak frozen at 1). Armed unit B then acts and
        // latches BWeapon before the dead streak matures. At deadStreak==3, the no-credit path
        // fires FIRST (before the culprit chain) and blocks any credit to B.
        // Non-vacuous: removing the _lethalUntracked check makes culprit=[BWeapon] -> kills[BWeapon]=1.
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();

        // S: untracked summoner.
        SetRoster(m, slot: 1, level: 50, brave: 55, faith: 60, weapon: UntrackedWeapon);
        SetUnit(m, SSlot, hp: 400, maxHp: 400, level: 50, brave: 55, faith: 60);

        // B: armed unit (weapon 73=BWeapon).
        SetRoster(m, slot: 0, level: 99, brave: 89, faith: 76, weapon: BWeapon);
        SetUnit(m, BSlot, hp: 350, maxHp: 350, level: 99, brave: 89, faith: 76);

        var t = new KillTracker(kills, m, Weapons);

        // S acts: empty latch. _latchResolvedEmpty=true, _latched=true.
        SetActive(m, hp: 400, maxHp: 400, level: 50, acted: 1);
        Settle(t, 3);

        // Enemy alive (seenAlive) while S is latched.
        SetEnemy(m, EnemySlot, hp: 300);
        Settle(t, 3);

        // Enemy dies while S is latched: deadStreak==1, _latched=true -> _lethalUntracked stamped.
        SetUnit(m, EnemySlot, hp: 0);
        t.Poll(true);   // deadStreak=1; _lethalUntracked[EnemySlot]=true

        // End S's period off-field (dead streak frozen at 1 -- off-field polls don't advance it).
        SetActive(m, hp: 400, maxHp: 400, level: 50, acted: 0);
        for (int i = 0; i < KillTracker.UnfreezeTicks; i++) t.Poll(false);   // _latched=false; streak stays 1

        // B acts: latches BWeapon. Dead streak advances (on-field); corpse matures.
        // _lethalUntracked[EnemySlot] is still true from S's period -- no-credit fires at deadStreak==3.
        SetActive(m, hp: 350, maxHp: 350, level: 99, acted: 1);
        t.Poll(true);   // B latches; deadStreak=2
        t.Poll(true);   // deadStreak=3 -> _lethalUntracked -> no-credit

        Assert.Empty(kills);   // B must NOT be credited -- the stamp from S's period wins
    }

    // --- T4: DELAYED PRIORITY -- delayed actor wins over _lethalUntracked ---

    [Fact]
    public void Delayed_actor_is_credited_over_untracked_latch_at_kill_maturation()
    {
        // T4 DELAYED PRIORITY: jumper P (weapon JumperWeapon=90) commits while latched,
        // snapshots its weapon, and arms on landing. An untracked S latches between the arm and
        // the kill maturation: _lethalUntracked is stamped at deadStreak==1. ConsumeDelayedCulprit()
        // is checked FIRST in the credit section; delayed != null -> the _lethalUntracked no-credit
        // branch is skipped -> JumperWeapon is credited.
        // Non-vacuous: checking _lethalUntracked before delayed (removing the delayed==null guard)
        // would suppress the delayed actor -> kills empty.
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();

        // P: tracked jumper (weapon JumperWeapon=90).
        SetRoster(m, slot: 0, level: 99, brave: 89, faith: 76, weapon: JumperWeapon);
        SetUnit(m, ASlot, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);

        // S: untracked summoner.
        SetRoster(m, slot: 1, level: 50, brave: 55, faith: 60, weapon: UntrackedWeapon);
        SetUnit(m, SSlot, hp: 400, maxHp: 400, level: 50, brave: 55, faith: 60);

        var t = new KillTracker(kills, m, Weapons);

        // (1) P acts + Jump bit sets -> latch JumperWeapon; TrackDelayed snapshots it.
        SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 1);
        SetJumpBit(m, ASlot, set: true);
        Settle(t, 3);   // latch 90; snapshot fires on tick 1 (fp match + weapons non-empty)

        // (2) Enemy seen alive (seenAlive).
        SetEnemy(m, EnemySlot, hp: 300);
        Settle(t, 3);

        // (3) End P's period.
        SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 0);
        Settle(t, KillTracker.UnfreezeTicks);   // _latched=false

        // (4) Jump lands: bit clears -> TrackDelayed arms JumperWeapon (_delayedArmedTicks=Window).
        SetJumpBit(m, ASlot, set: false);
        t.Poll(true);

        // (5) S acts (untracked): empty latch, _latchResolvedEmpty=true.
        SetActive(m, hp: 400, maxHp: 400, level: 50, acted: 1);
        t.Poll(true);   // S latches; _latchResolvedEmpty=true

        // (6) Enemy dies while S is latched: deadStreak==1 -> _lethalUntracked stamped.
        SetUnit(m, EnemySlot, hp: 0);
        t.Poll(true);   // deadStreak=1; _latched=true -> _lethalUntracked[EnemySlot]=true

        // (7) Dead streak matures. ConsumeDelayedCulprit() fires FIRST -> delayed=[JumperWeapon];
        //     delayed != null -> _lethalUntracked branch skipped -> JumperWeapon credited.
        t.Poll(true);   // deadStreak=2
        t.Poll(true);   // deadStreak=3 -> credit -> delayed=[90] wins

        Assert.Equal(1, kills.GetValueOrDefault(JumperWeapon));
        Assert.Equal(1, kills.Count);   // no spurious credits
    }

    // --- T5: bail SOLO-pin -- the slow case where only _latchResolvedEmpty blocks the fallback ---

    [Fact]
    public void Sticky_empty_latch_bails_fallback_from_crediting_a_stale_armed_actor()
    {
        // T5 isolates the `|| _latchResolvedEmpty` FirstKillFallback bail (KillTracker.cs). Unlike
        // T1 -- where the _lethalUntracked stamp buries the corpse before it can go pending, masking
        // the bail -- here the kill matures AFTER the untracked actor's period ends (_latched=false
        // at deadStreak==1), so NO stamp fires and the corpse goes PENDING. A stale armed actor A
        // then sits in the active struct at acted=0 (it never actually acts/latches). The ONLY thing
        // standing between FirstKillFallback and crediting A is the sticky _latchResolvedEmpty bail.
        // Non-vacuous: removing `|| _latchResolvedEmpty` lets the fallback resolve+latch A across 3
        // polls and credit kills[ArmedWeapon]=1.
        // (This is the slow-summon residual's BENIGN sub-case -- no real re-latch, so the bail saves
        //  it. The residual LEAK is when a real armed unit actually acts; see the _lethalUntracked
        //  field's V1-limitation note in KillTracker.Corpses.cs.)
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();

        // A: stale armed actor (weapon 35).
        SetRoster(m, slot: 3, level: 90, brave: 80, faith: 70, weapon: ArmedWeapon);
        SetUnit(m, ASlot, hp: 352, maxHp: 352, level: 90, brave: 80, faith: 70);

        // S: untracked summoner (weapon 999 -> empty hands on resolve).
        SetRoster(m, slot: 0, level: 50, brave: 55, faith: 60, weapon: UntrackedWeapon);
        SetUnit(m, SSlot, hp: 400, maxHp: 400, level: 50, brave: 55, faith: 60);

        var t = new KillTracker(kills, m, Weapons);

        // S acts -> empty latch. _latchResolvedEmpty=true, _latched=true.
        SetActive(m, hp: 400, maxHp: 400, level: 50, acted: 1);
        Settle(t, 3);

        // Enemy seen alive (seenAlive) while S is latched.
        SetEnemy(m, EnemySlot, hp: 300);
        Settle(t, 3);

        // S's period ends: _latched=false, _latchResolvedEmpty stays STICKY true. Active = S, acted 0.
        SetActive(m, hp: 400, maxHp: 400, level: 50, acted: 0);
        Settle(t, KillTracker.UnfreezeTicks);

        // Enemy dies AFTER S's period (slow case): deadStreak==1 fires with _latched=false -> NO stamp
        // -> corpse goes pending. Active is still S (untracked), so the fallback can't resolve anyone yet.
        SetUnit(m, EnemySlot, hp: 0);
        Settle(t, 4);   // deadStreak 1->3 -> pending; no stamp (slow case)

        // A stale ARMED actor now occupies the active struct at acted=0 (never actually acts). With the
        // bail, FirstKillFallback stays inert; without it, A is resolved+latched and credited.
        SetActive(m, hp: 352, maxHp: 352, level: 90, acted: 0);
        for (int i = 0; i < 5; i++) t.Poll(true);

        Assert.Empty(kills);   // stale armed A must NOT be credited the summoner's kill
    }

    // --- R4: RESOLVER COLLISION end-to-end -- the REAL witnessed bug (composes with _lethalUntracked) ---

    [Fact]
    public void Unarmed_summoner_colliding_with_armed_mage_does_not_credit_the_mage_weapon()
    {
        // R4 WITNESSED REPRO (the resolver-collision bug, distinct from T1's explicit empty latch):
        // summoner S holds no tracked weapon (band weapon 0; own roster slot untracked) but SHARES
        // (level,brave,faith) with an adjacent armed mage M whose roster slot holds a TRACKED weapon
        // (BWeapon -- stands in for the live Warlock's Staff id 60). S casts and kills during its own
        // acted-period.
        // Pre-resolver-fix: FingerprintPlayer's legacy armed branch resolves S to M's weapon (NON-empty)
        //   -> _latchResolvedEmpty=false -> _lethalActor stamped {BWeapon} -> mage weapon credited (the bug).
        // Post-fix: the band-confirmed-unarmed guard resolves S to EMPTY -> _latchResolvedEmpty=true ->
        //   _lethalUntracked -> no credit. The resolver fix and _lethalUntracked COMPOSE.
        // Non-vacuous: deleting the resolver guard credits kills[BWeapon]=1.
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();

        // S: untracked summoner (band weapon 0; roster rh = 999 untracked -> empty hands).
        SetRoster(m, slot: 1, level: 50, brave: 55, faith: 60, weapon: UntrackedWeapon);
        SetUnit(m, SSlot, hp: 400, maxHp: 400, level: 50, brave: 55, faith: 60);

        // M: armed mage SHARING S's (level,brave,faith) -- the collision. Tracked weapon (BWeapon).
        // No band entry for M (M is not the active actor); only its roster slot drives the collision.
        SetRoster(m, slot: 0, level: 50, brave: 55, faith: 60, weapon: BWeapon);

        var t = new KillTracker(kills, m, Weapons);

        // S acts: with the fix it resolves EMPTY (_latchResolvedEmpty=true, _latched=true).
        SetActive(m, hp: 400, maxHp: 400, level: 50, acted: 1);
        Settle(t, 3);

        // Enemy seen alive while S is latched.
        SetEnemy(m, EnemySlot, hp: 300);
        Settle(t, 3);

        // Enemy dies while S is latched: deadStreak==1, _latched=true, _latchResolvedEmpty=true ->
        // _lethalUntracked stamped (with the fix). Mature the streak.
        SetUnit(m, EnemySlot, hp: 0);
        Settle(t, 3);

        Assert.Empty(kills);   // mage's weapon must NOT be credited the summoner's kill
    }

    // --- T6: AoE multi-corpse -- a single untracked summon kills TWO enemies, both uncredited ---

    [Fact]
    public void Untracked_summon_killing_two_enemies_credits_neither()
    {
        // T6 locks in that _lethalUntracked is PER-SLOT: an AoE summon (the actual witnessed shape)
        // killing >1 enemy in one action must leave EVERY victim uncredited, not just the first.
        // Structural guard against a future refactor collapsing the per-slot bool[] to a scalar.
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();

        // S: untracked summoner.
        SetRoster(m, slot: 1, level: 50, brave: 55, faith: 60, weapon: UntrackedWeapon);
        SetUnit(m, SSlot, hp: 400, maxHp: 400, level: 50, brave: 55, faith: 60);

        var t = new KillTracker(kills, m, Weapons);

        // S acts -> empty latch (_latchResolvedEmpty=true, _latched=true).
        SetActive(m, hp: 400, maxHp: 400, level: 50, acted: 1);
        Settle(t, 3);

        // Two DISTINCT-identity enemies seen alive (distinct lvl/br/fa/mhp so the alive-edge belt
        // credits each independently rather than frozen-twin-dedup'ing the second).
        SetEnemy(m, EnemySlot,     hp: 300, maxHp: 400, level: 10, brave: 50, faith: 50);
        SetEnemy(m, EnemySlot + 1, hp: 280, maxHp: 380, level: 12, brave: 55, faith: 45);
        Settle(t, 3);   // both seenAlive

        // Both die on the same poll while S is still latched (acted=1) -> both stamped _lethalUntracked.
        SetEnemy(m, EnemySlot,     hp: 0, maxHp: 400, level: 10, brave: 50, faith: 50);
        SetEnemy(m, EnemySlot + 1, hp: 0, maxHp: 380, level: 12, brave: 55, faith: 45);
        Settle(t, 3);   // both deadStreak 1->3 -> both no-credit

        Assert.Empty(kills);   // neither AoE victim is credited
    }
}
