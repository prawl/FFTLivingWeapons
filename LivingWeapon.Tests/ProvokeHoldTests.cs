using System;
using System.Collections.Generic;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// Module-level coverage for the Provoke hold (LW-123 arc 2a) against FakeSparseMemory band seats:
/// the SLICE facade (the load-bearing behavior), the WINDOW fallback, the identical-enemy turn-count
/// guard, the transient-miss debounce, every release path, and the criteria the plan calls out by
/// number (3/3b/4/5/6/7/8/9/10/11/12/13/14/17). Pure-decision coverage lives in
/// ProvokeHoldPolicyTests.cs.
/// </summary>
public class ProvokeHoldTests
{
    private const int DefenderId = Provoke.DefenderId;   // 33
    private static Dictionary<int, int> Tier3Kills() => new() { [DefenderId] = 999 };

    private static void SeatBearer(FakeSparseMemory m, int rosterSlot, int bandIdx,
        int lvl = 30, int br = 50, int fa = 50, int gx = 1, int gy = 1)
    {
        MemSeats.SeatRoster(m, rosterSlot, lvl, br, fa, rh: DefenderId);
        MemSeats.SeatBand(m, bandIdx, DefenderId, lvl, br, fa, gx, gy);
        SetSide(m, bandIdx, enemy: false);
        // Stage the bearer's own Invisible byte writable (mirrors SeatAlly) so "the bearer is never
        // hidden" assertions actually bite: without this the fake silently refuses a wrongful write
        // and IsInvisible reads false for the wrong reason (a bearer-exclusion bug would go uncaught).
        StageInvisible(m, bandIdx);
    }

    private static void SeatAlly(FakeSparseMemory m, int bandIdx, int lvl, int br, int fa, int gx, int gy, int weapon = 5)
    {
        MemSeats.SeatBand(m, bandIdx, weapon, lvl, br, fa, gx, gy);
        SetSide(m, bandIdx, enemy: false);
        StageInvisible(m, bandIdx);
    }

    private static void SeatEnemy(FakeSparseMemory m, int bandIdx, int lvl, int br, int fa, int gx, int gy,
        bool marked = false, bool active = false, int nameId = 0)
    {
        MemSeats.SeatBand(m, bandIdx, weapon: 0, lvl, br, fa, gx, gy);
        SetSide(m, bandIdx, enemy: true);
        if (nameId != 0) MemSeats.SeatFrameNameId(m, bandIdx, nameId);
        // "active" now seats the engine's ACTOR POINTER on this seat during an enemy turn -- the
        // proven signal (LIVE_LEDGER "actor pointer names the acting unit" 2026-07-01) that replaces
        // the per-unit ATurnFlag byte, observed flaky live 2026-07-22 (hid 0 units one attempt,
        // missed the turn-done edge another -> 30s watchdog).
        if (active) PointActorAtEnemyTurn(m, bandIdx);
        if (marked) SetMark(m, bandIdx, true);
        // Stage the enemy's own Invisible byte writable (mirrors SeatAlly) so "the enemy side is
        // never touched" assertions actually bite: without this a broken player-side filter that
        // leaks an enemy into the hide list gets silently swallowed by the fake's write refusal.
        StageInvisible(m, bandIdx);
    }

    private static void SetSide(FakeSparseMemory m, int bandIdx, bool enemy) =>
        m.U8s[Band.Entry(bandIdx) + Offsets.AFriendFoe] = enemy ? Offsets.AFriendFoeEnemyBit : (byte)0;

    /// <summary>Point the engine's actor pointer (Offsets.ActorPtr) at bandIdx's combat frame and
    /// mark this as an ENEMY turn (TqTeam=1) -- the round-trip of Band.ActorEntry: frame =
    /// Band.Entry(bandIdx) - BandEntry, so ActorEntry resolves back to exactly Band.Entry(bandIdx).
    /// Mirrors IaiTests.PointActorAt. Only ONE unit can be the actor at a time (a single global
    /// pointer) -- last writer wins, which is fine since these tests seat at most one active enemy.</summary>
    private static void PointActorAtEnemyTurn(FakeSparseMemory m, int bandIdx)
    {
        long frame = Band.Entry(bandIdx) - Offsets.BandEntry;
        m.SeedU64(Offsets.ActorPtr, (ulong)frame);
        m.U16s[Offsets.TurnQueue + Offsets.TqTeam] = 1;
    }

    /// <summary>Clear the actor pointer so it resolves to nobody (Band.ActorEntry returns 0) --
    /// "this enemy's turn ends" for the actor-pointer signal.</summary>
    private static void ClearActor(FakeSparseMemory m) => m.SeedU64(Offsets.ActorPtr, 0);

    private static void SetMark(FakeSparseMemory m, int bandIdx, bool on)
    {
        long e = Band.Entry(bandIdx);
        long composedAddr = e + StatusApply.Composed + StatusApply.StatusByte(ProvokeHold.MarkId);
        long inflictedAddr = e + StatusApply.Inflicted + StatusApply.StatusByte(ProvokeHold.MarkId);
        byte mask = StatusApply.StatusMask(ProvokeHold.MarkId);
        m.U8s[composedAddr] = (byte)(m.U8(composedAddr) | (on ? mask : (byte)0));
        m.U8s[inflictedAddr] = (byte)(m.U8(inflictedAddr) | (on ? mask : (byte)0));
        m.WritableAddrs.Add(composedAddr);
        m.WritableAddrs.Add(inflictedAddr);
    }

    private static void StageInvisible(FakeSparseMemory m, int bandIdx) =>
        m.WritableAddrs.Add(Band.Entry(bandIdx) + Offsets.AInvisible);

    private static bool IsInvisible(FakeSparseMemory m, int bandIdx) =>
        (m.U8(Band.Entry(bandIdx) + Offsets.AInvisible) & Offsets.AInvisibleBit) != 0;

    private static bool HasMark(FakeSparseMemory m, int bandIdx) =>
        (m.U8(Band.Entry(bandIdx) + StatusApply.Composed + StatusApply.StatusByte(ProvokeHold.MarkId))
            & StatusApply.StatusMask(ProvokeHold.MarkId)) != 0;

    /// <summary>The INFLICTED-layer twin of <see cref="HasMark"/> (LW-130: a correct clear must
    /// scrub both layers, not just the composed one ClearMark's own doc calls out).</summary>
    private static bool HasInflictedMark(FakeSparseMemory m, int bandIdx) =>
        (m.U8(Band.Entry(bandIdx) + StatusApply.Inflicted + StatusApply.StatusByte(ProvokeHold.MarkId))
            & StatusApply.StatusMask(ProvokeHold.MarkId)) != 0;

    // ---- THE LOAD-BEARING TEST (SLICE mode) ----

    [Fact]
    public void SliceMode_hides_everyone_but_the_bearer_while_the_marked_enemy_is_active()
    {
        var m = new FakeSparseMemory();
        SeatBearer(m, rosterSlot: 0, bandIdx: 0);
        SeatAlly(m, bandIdx: 1, lvl: 25, br: 40, fa: 60, gx: 2, gy: 2);
        SeatEnemy(m, bandIdx: 10, lvl: 20, br: 30, fa: 30, gx: 5, gy: 5, marked: true, active: true);

        var hold = new ProvokeHold(Tier3Kills(), m);
        hold.Tick(DateTime.UtcNow, inLive: true);

        Assert.True(IsInvisible(m, 1));     // the other player unit is hidden
        Assert.False(IsInvisible(m, 0));    // the bearer itself is never hidden
        Assert.False(IsInvisible(m, 10));   // the enemy side is never touched
    }

    // ---- THE FACADE TEST (SLICE mode, the design pivot) ----

    [Fact]
    public void SliceMode_flags_nobody_when_a_different_enemy_is_active_not_the_marked_one()
    {
        var m = new FakeSparseMemory();
        SeatBearer(m, 0, 0);
        SeatAlly(m, 1, lvl: 25, br: 40, fa: 60, gx: 2, gy: 2);
        SeatEnemy(m, 10, lvl: 20, br: 30, fa: 30, gx: 5, gy: 5, marked: true, active: false, nameId: 500);
        SeatEnemy(m, 11, lvl: 22, br: 35, fa: 33, gx: 6, gy: 6, marked: false, active: true, nameId: 501);

        // Explicit sliceMode: true -- the shipped default is now WINDOW (Tuning.ProvokeSliceMode,
        // flipped 2026-07-22 after the slice turn-start race lost live), so this SLICE-specific
        // facade test opts in rather than relying on the default.
        var hold = new ProvokeHold(Tier3Kills(), m, sliceMode: true);
        hold.Tick(DateTime.UtcNow, true);

        Assert.False(IsInvisible(m, 1));   // nobody hidden: the ACTIVE unit is not the marked one
    }

    // ---- WINDOW-mode load-bearing test ----

    [Fact]
    public void WindowMode_hides_on_any_enemy_turn_via_TqTeam()
    {
        var m = new FakeSparseMemory();
        SeatBearer(m, 0, 0);
        SeatAlly(m, 1, lvl: 25, br: 40, fa: 60, gx: 2, gy: 2);
        SeatEnemy(m, 10, lvl: 20, br: 30, fa: 30, gx: 5, gy: 5, marked: true, active: false);
        m.U16s[Offsets.TurnQueue + Offsets.TqMaxHp] = 100;
        m.U16s[Offsets.TurnQueue + Offsets.TqLevel] = 10;
        m.U16s[Offsets.TurnQueue + Offsets.TqTeam] = 1;

        var hold = new ProvokeHold(Tier3Kills(), m, sliceMode: false);
        hold.Tick(DateTime.UtcNow, true);

        Assert.True(IsInvisible(m, 1));
        Assert.False(IsInvisible(m, 0));
        Assert.False(IsInvisible(m, 10));
    }

    // ---- Identical-enemy turn-count (guards the BLOCKER fix, decision 10) ----

    [Fact]
    public void Identical_enemy_twin_taking_its_turn_does_not_advance_the_marked_turn_count()
    {
        var m = new FakeSparseMemory();
        SeatBearer(m, 0, 0);
        SeatEnemy(m, 10, lvl: 20, br: 30, fa: 30, gx: 5, gy: 5, marked: true, active: false, nameId: 500);
        SeatEnemy(m, 11, lvl: 20, br: 30, fa: 30, gx: 6, gy: 6, marked: false, active: false, nameId: 501);   // fp-identical twin

        var hold = new ProvokeHold(Tier3Kills(), m);   // default ProvokeTurns = 1
        var t0 = DateTime.UtcNow;
        hold.Tick(t0, true);   // arms (marked not active)

        PointActorAtEnemyTurn(m, 11);   // the TWIN becomes the actor on an enemy turn -- its fp
                                         // collides with the marked enemy, but nameId (501 vs 500) must not
        hold.Tick(t0.AddMilliseconds(33), true);
        ClearActor(m);   // the TWIN's turn ends -- must not count as the marked enemy's
        hold.Tick(t0.AddMilliseconds(66), true);

        // An identity match keyed on the fp alone (ignoring nameId) would have collided on the
        // twin, released at ProvokeTurns=1, and scrubbed the mark. Assert it did NOT.
        Assert.True(HasMark(m, 10));
    }

    // ---- Struck-victim park does not miscount (the TqTeam==1 gate is load-bearing) ----
    //
    // Live testing 2026-07-22 proved the engine's actor pointer PARKS ON STRUCK VICTIMS: during a
    // PLAYER turn it can name the marked enemy the instant it gets hit, without that ever being the
    // enemy's own turn. Dropping the "&& TqTeam==1" gate makes THIS test fail.

    [Fact]
    public void Actor_pointer_parked_on_the_marked_enemy_during_a_player_turn_does_not_count_as_its_turn()
    {
        var m = new FakeSparseMemory();
        SeatBearer(m, 0, 0);
        SeatAlly(m, 1, lvl: 25, br: 40, fa: 60, gx: 2, gy: 2);
        SeatEnemy(m, 10, lvl: 20, br: 30, fa: 30, gx: 5, gy: 5, marked: true, active: false, nameId: 500);

        var hold = new ProvokeHold(Tier3Kills(), m);   // default ProvokeTurns = 1
        var t0 = DateTime.UtcNow;
        hold.Tick(t0, true);   // arms; TqTeam defaults to 0 (a player turn), nobody active yet

        // The actor pointer parks on the marked enemy -- it was just STRUCK mid-player-turn, not
        // taking its own turn. TqTeam is deliberately left at 0 (NOT set to 1).
        long frame = Band.Entry(10) - Offsets.BandEntry;
        m.SeedU64(Offsets.ActorPtr, (ulong)frame);
        hold.Tick(t0.AddMilliseconds(33), true);
        Assert.True(HasMark(m, 10));   // a player-turn park must not read as the enemy's own turn

        ClearActor(m);   // the pointer moves off it
        hold.Tick(t0.AddMilliseconds(66), true);

        Assert.True(HasMark(m, 10));   // no EnemyTurnDone release: _markedTurns never incremented
    }

    // ---- Transient-miss non-release (guards the debounce) ----

    [Fact]
    public void Transient_locate_miss_does_not_release_before_the_debounce_is_exhausted()
    {
        var m = new FakeSparseMemory();
        SeatBearer(m, 0, 0);
        SeatAlly(m, 1, lvl: 25, br: 40, fa: 60, gx: 2, gy: 2);
        SeatEnemy(m, 10, lvl: 20, br: 30, fa: 30, gx: 5, gy: 5, marked: true, active: true, nameId: 500);

        var hold = new ProvokeHold(Tier3Kills(), m);
        var t0 = DateTime.UtcNow;
        hold.Tick(t0, true);   // arms; marked active -> ally hidden
        Assert.True(IsInvisible(m, 1));

        long markedEntry = Band.Entry(10);
        // Band.IsValid now fails (gy>30) -> transiently unlocatable via the band-scan lookup used
        // for release/EnemyGone. The actor pointer is a stable global, not a scan, so it still
        // resolves this exact address and its identity fields (nameId/mhp/lvl/br/fa) are untouched.
        m.U8s[markedEntry + Offsets.AGy] = 99;

        for (int i = 1; i < Tuning.ProvokeMarkedMissTicks; i++)
            hold.Tick(t0.AddMilliseconds(33 * i), true);
        Assert.True(IsInvisible(m, 1));   // still under the debounce: armed, hide/reveal keeps running

        hold.Tick(t0.AddMilliseconds(33 * Tuning.ProvokeMarkedMissTicks), true);   // the Nth consecutive miss
        Assert.False(IsInvisible(m, 1));   // now released (EnemyGone)
    }

    // ---- SLICE reveal is automatic (criterion 5), not a release ----

    [Fact]
    public void SliceMode_reveals_the_instant_the_marked_enemy_stops_being_active_while_still_armed()
    {
        var m = new FakeSparseMemory();
        SeatBearer(m, 0, 0);
        SeatAlly(m, 1, lvl: 25, br: 40, fa: 60, gx: 2, gy: 2);
        SeatEnemy(m, 10, lvl: 20, br: 30, fa: 30, gx: 5, gy: 5, marked: true, active: true, nameId: 500);

        // provokeTurns=2 isolates SLICE's own reveal from the coincident EnemyTurnDone release the
        // shipped default of 1 would otherwise trigger on this exact edge. Explicit sliceMode: true
        // because the shipped default is now WINDOW (see the facade test above).
        var hold = new ProvokeHold(Tier3Kills(), m, sliceMode: true, provokeTurns: 2);
        var t0 = DateTime.UtcNow;
        hold.Tick(t0, true);
        Assert.True(IsInvisible(m, 1));

        ClearActor(m);   // the marked enemy's own turn ends -- the actor pointer moves off it
        hold.Tick(t0.AddMilliseconds(33), true);

        Assert.False(IsInvisible(m, 1));   // revealed automatically -- no per-turn toggle needed
        Assert.True(HasMark(m, 10));       // still armed (1 of 2 turns) -- this was SLICE's reveal, not a release
    }

    // ---- WINDOW ActionFor wiring ----

    [Fact]
    public void WindowMode_reveals_on_a_sane_player_turn()
    {
        var m = new FakeSparseMemory();
        SeatBearer(m, 0, 0);
        SeatAlly(m, 1, lvl: 25, br: 40, fa: 60, gx: 2, gy: 2);
        SeatEnemy(m, 10, lvl: 20, br: 30, fa: 30, gx: 5, gy: 5, marked: true, active: false, nameId: 500);
        m.U16s[Offsets.TurnQueue + Offsets.TqMaxHp] = 100;
        m.U16s[Offsets.TurnQueue + Offsets.TqLevel] = 10;
        m.U16s[Offsets.TurnQueue + Offsets.TqTeam] = 1;   // enemy turn at arm

        var hold = new ProvokeHold(Tier3Kills(), m, sliceMode: false);
        var t0 = DateTime.UtcNow;
        hold.Tick(t0, true);
        Assert.True(IsInvisible(m, 1));

        m.U16s[Offsets.TurnQueue + Offsets.TqTeam] = 0;   // now a clean player turn
        hold.Tick(t0.AddMilliseconds(33), true);
        Assert.False(IsInvisible(m, 1));
    }

    [Fact]
    public void WindowMode_keeps_units_hidden_on_an_insane_garbage_queue_read()
    {
        var m = new FakeSparseMemory();
        SeatBearer(m, 0, 0);
        SeatAlly(m, 1, lvl: 25, br: 40, fa: 60, gx: 2, gy: 2);
        SeatEnemy(m, 10, lvl: 20, br: 30, fa: 30, gx: 5, gy: 5, marked: true, active: false, nameId: 500);
        m.U16s[Offsets.TurnQueue + Offsets.TqMaxHp] = 100;
        m.U16s[Offsets.TurnQueue + Offsets.TqLevel] = 10;
        m.U16s[Offsets.TurnQueue + Offsets.TqTeam] = 1;

        var hold = new ProvokeHold(Tier3Kills(), m, sliceMode: false);
        var t0 = DateTime.UtcNow;
        hold.Tick(t0, true);
        Assert.True(IsInvisible(m, 1));

        m.U16s[Offsets.TurnQueue + Offsets.TqMaxHp] = 0;   // garbage read -> queueSane false
        hold.Tick(t0.AddMilliseconds(33), true);
        Assert.True(IsInvisible(m, 1));   // bias-to-hidden: stays hidden
    }

    // ---- FeignDeath's own Invisible bit is never set or cleared by us (criterion 11) ----

    [Fact]
    public void A_unit_already_invisible_before_arm_is_never_set_and_never_cleared()
    {
        var m = new FakeSparseMemory();
        SeatBearer(m, 0, 0);
        SeatAlly(m, 1, lvl: 25, br: 40, fa: 60, gx: 2, gy: 2);
        long invisAddr = Band.Entry(1) + Offsets.AInvisible;
        m.U8s[invisAddr] = Offsets.AInvisibleBit;   // already invisible (FeignDeath's, say)
        SeatEnemy(m, 10, lvl: 20, br: 30, fa: 30, gx: 5, gy: 5, marked: true, active: true, nameId: 500);

        var hold = new ProvokeHold(Tier3Kills(), m);   // default ProvokeTurns = 1
        var t0 = DateTime.UtcNow;
        hold.Tick(t0, true);   // arm + hide step: band1 already invisible -> skipped, never written
        Assert.False(m.Written.ContainsKey(invisAddr));

        ClearActor(m);   // marked enemy's turn ends (actor pointer moves off it) -> EnemyTurnDone release
        hold.Tick(t0.AddMilliseconds(33), true);

        Assert.False(m.Written.ContainsKey(invisAddr));   // release never touched it either
        Assert.True(IsInvisible(m, 1));                    // still invisible -- exactly as found
    }

    // ---- Reaching ProvokeTurns releases + clears our bits + scrubs the mark (criteria 3/3b) ----

    [Fact]
    public void Reaching_ProvokeTurns_releases_clears_our_bits_and_scrubs_the_mark_leaving_dead_bits_alone()
    {
        var m = new FakeSparseMemory();
        SeatBearer(m, 0, 0);
        SeatAlly(m, 1, lvl: 25, br: 40, fa: 60, gx: 2, gy: 2);
        SeatEnemy(m, 10, lvl: 20, br: 30, fa: 30, gx: 5, gy: 5, marked: true, active: true, nameId: 500);
        long composedByte = Band.Entry(10) + StatusApply.Composed;
        m.U8s[composedByte] = (byte)(m.U8(composedByte) | Offsets.AUndeadBit);   // pre-set on the SAME byte the mark shares

        var hold = new ProvokeHold(Tier3Kills(), m);   // default ProvokeTurns = 1
        var t0 = DateTime.UtcNow;
        hold.Tick(t0, true);
        Assert.True(IsInvisible(m, 1));

        ClearActor(m);   // one turn-end edge (the actor pointer moves off the marked enemy) -> EnemyTurnDone
        hold.Tick(t0.AddMilliseconds(33), true);

        Assert.False(IsInvisible(m, 1));
        Assert.False(HasMark(m, 10));
        long inflictedByte = Band.Entry(10) + StatusApply.Inflicted;
        Assert.Equal(0, m.U8(inflictedByte) & StatusApply.StatusMask(ProvokeHold.MarkId));
        Assert.NotEqual(0, m.U8(composedByte) & Offsets.AUndeadBit);   // Dead/Undead untouched
    }

    // ---- Bearer safety releases (criteria 3 / 10) ----

    [Fact]
    public void Bearer_unequipping_the_defender_mid_hold_releases()
    {
        var m = new FakeSparseMemory();
        SeatBearer(m, 0, 0);
        SeatAlly(m, 1, lvl: 25, br: 40, fa: 60, gx: 2, gy: 2);
        SeatEnemy(m, 10, lvl: 20, br: 30, fa: 30, gx: 5, gy: 5, marked: true, active: true, nameId: 500);

        var hold = new ProvokeHold(Tier3Kills(), m);
        var t0 = DateTime.UtcNow;
        hold.Tick(t0, true);
        Assert.True(IsInvisible(m, 1));

        m.U16s[Offsets.RosterBase + Offsets.RRHand] = 0xFFFF;   // roster slot 0 no longer wields the Defender
        hold.Tick(t0.AddMilliseconds(33), true);

        Assert.False(IsInvisible(m, 1));
        Assert.False(HasMark(m, 10));
    }

    [Fact]
    public void Bearer_dying_mid_hold_releases()
    {
        var m = new FakeSparseMemory();
        SeatBearer(m, 0, 0);
        SeatAlly(m, 1, lvl: 25, br: 40, fa: 60, gx: 2, gy: 2);
        SeatEnemy(m, 10, lvl: 20, br: 30, fa: 30, gx: 5, gy: 5, marked: true, active: true, nameId: 500);

        var hold = new ProvokeHold(Tier3Kills(), m);
        var t0 = DateTime.UtcNow;
        hold.Tick(t0, true);
        Assert.True(IsInvisible(m, 1));

        m.U16s[Band.Entry(0) + Offsets.AHp] = 0;   // bearer's own band entry HP -> 0
        hold.Tick(t0.AddMilliseconds(33), true);

        Assert.False(IsInvisible(m, 1));
        Assert.False(HasMark(m, 10));
    }

    // ---- Enemy dies mid-hold releases (mark-clear attempted) ----

    [Fact]
    public void Enemy_dying_mid_hold_releases_and_attempts_the_mark_clear()
    {
        var m = new FakeSparseMemory();
        SeatBearer(m, 0, 0);
        SeatAlly(m, 1, lvl: 25, br: 40, fa: 60, gx: 2, gy: 2);
        SeatEnemy(m, 10, lvl: 20, br: 30, fa: 30, gx: 5, gy: 5, marked: true, active: true, nameId: 500);

        var hold = new ProvokeHold(Tier3Kills(), m);
        var t0 = DateTime.UtcNow;
        hold.Tick(t0, true);
        Assert.True(IsInvisible(m, 1));

        m.U16s[Band.Entry(10) + Offsets.AHp] = 0;   // enemy HP hits 0, still otherwise locatable
        hold.Tick(t0.AddMilliseconds(33), true);

        Assert.False(IsInvisible(m, 1));
        Assert.False(HasMark(m, 10));
    }

    // ---- Ghost seat / guest side coverage (criteria 6/7) ----

    [Fact]
    public void Ghost_seat_with_a_real_position_is_never_flagged()
    {
        var m = new FakeSparseMemory();
        SeatBearer(m, 0, 0);
        SeatAlly(m, 1, lvl: 25, br: 40, fa: 60, gx: 3, gy: 3);
        SeatAlly(m, 2, lvl: 26, br: 41, fa: 61, gx: 4, gy: 4);
        m.U8s[Band.Entry(2) + Offsets.AGateByte] = Offsets.AGateHiddenValue;   // combat +0x01 == 0xFF
        SeatEnemy(m, 10, lvl: 20, br: 30, fa: 30, gx: 5, gy: 5, marked: true, active: true, nameId: 500);

        var hold = new ProvokeHold(Tier3Kills(), m);
        hold.Tick(DateTime.UtcNow, true);

        Assert.True(IsInvisible(m, 1));
        Assert.False(IsInvisible(m, 2));
    }

    [Fact]
    public void Guest_outside_the_classic_seat_range_is_hidden_alongside_the_party()
    {
        var m = new FakeSparseMemory();
        SeatBearer(m, 0, 0);
        SeatAlly(m, 40, lvl: 28, br: 44, fa: 66, gx: 7, gy: 7);   // a "guest" far from the usual party range
        SeatEnemy(m, 10, lvl: 20, br: 30, fa: 30, gx: 5, gy: 5, marked: true, active: true, nameId: 500);

        var hold = new ProvokeHold(Tier3Kills(), m);
        hold.Tick(DateTime.UtcNow, true);

        Assert.True(IsInvisible(m, 40));
    }

    // ---- Enter/exit sweep (criteria 12/13) ----

    [Fact]
    public void ResetBattle_reveals_every_ever_flagged_identity_and_scrubs_a_live_mark()
    {
        var m = new FakeSparseMemory();
        SeatBearer(m, 0, 0);
        SeatAlly(m, 1, lvl: 25, br: 40, fa: 60, gx: 2, gy: 2);
        SeatEnemy(m, 10, lvl: 20, br: 30, fa: 30, gx: 5, gy: 5, marked: true, active: true, nameId: 500);

        var hold = new ProvokeHold(Tier3Kills(), m);
        hold.Tick(DateTime.UtcNow, true);
        Assert.True(IsInvisible(m, 1));

        // A stray player-side mark stranded right before the reset edge (LW-130 / AC 3c), seated
        // AFTER the tick above so ScrubPlayerSideMarks' Tick call site never touches it -- only
        // ResetBattle's own call site (the one the verifier deleted with the suite staying green)
        // can be what clears it here.
        SeatAlly(m, 2, lvl: 26, br: 41, fa: 61, gx: 3, gy: 3);
        SetMark(m, 2, true);

        hold.ResetBattle();

        Assert.False(IsInvisible(m, 1));
        Assert.False(HasMark(m, 10));
        Assert.False(HasMark(m, 2));            // composed layer scrubbed off the stray ally mark
        Assert.False(HasInflictedMark(m, 2));   // inflicted layer scrubbed too
    }

    // ---- Watchdog (criteria 14/17) ----

    [Fact]
    public void Watchdog_fires_after_the_unpaused_cap_with_no_other_reason_and_logs_distinctly()
    {
        var m = new FakeSparseMemory();
        SeatBearer(m, 0, 0);
        SeatAlly(m, 1, lvl: 25, br: 40, fa: 60, gx: 2, gy: 2);
        SeatEnemy(m, 10, lvl: 20, br: 30, fa: 30, gx: 5, gy: 5, marked: true, active: false, nameId: 500);

        var file = new List<string>();
        ModLogger.Instance = new FileConsoleLogger(_ => { }, file.Add) { LogLevel = LogLevel.Debug };
        try
        {
            var hold = new ProvokeHold(Tier3Kills(), m);
            var t0 = DateTime.UtcNow;
            hold.Tick(t0, true);   // arms
            hold.Tick(t0.AddSeconds(Tuning.ProvokeWatchdogSeconds + 1), true);   // unpaused, cap exceeded
        }
        finally { ModLogger.UseNullLogger(); }

        Assert.False(HasMark(m, 10));
        Assert.Contains(file, l => l.Contains("WATCHDOG"));
    }

    [Fact]
    public void Watchdog_does_not_accrue_on_paused_ticks()
    {
        var m = new FakeSparseMemory();
        SeatBearer(m, 0, 0);
        SeatEnemy(m, 10, lvl: 20, br: 30, fa: 30, gx: 5, gy: 5, marked: true, active: false, nameId: 500);

        var hold = new ProvokeHold(Tier3Kills(), m);
        var t0 = DateTime.UtcNow;
        hold.Tick(t0, true);   // arms

        m.U8s[Offsets.PauseFlag] = 1;   // paused
        hold.Tick(t0.AddSeconds(Tuning.ProvokeWatchdogSeconds + 1), true);

        Assert.True(HasMark(m, 10));   // still armed: the huge elapsed gap never accrued while paused
    }

    // ---- A refused guarded write logs distinctly (criterion 17) ----

    [Fact]
    public void A_refused_hide_write_writes_nothing_and_logs_distinctly()
    {
        var m = new FakeSparseMemory();
        SeatBearer(m, 0, 0);
        MemSeats.SeatBand(m, 1, weapon: 5, lvl: 25, br: 40, fa: 60, gx: 2, gy: 2);
        SetSide(m, 1, enemy: false);   // valid ally, but its Invisible address is deliberately NOT Writable
        SeatEnemy(m, 10, lvl: 20, br: 30, fa: 30, gx: 5, gy: 5, marked: true, active: true, nameId: 500);

        var file = new List<string>();
        ModLogger.Instance = new FileConsoleLogger(_ => { }, file.Add) { LogLevel = LogLevel.Debug };
        try
        {
            var hold = new ProvokeHold(Tier3Kills(), m);
            hold.Tick(DateTime.UtcNow, true);
        }
        finally { ModLogger.UseNullLogger(); }

        Assert.Empty(m.Written);
        Assert.Contains(file, l => l.Contains("refused"));
    }

    // ---- LW-130: scrub a mark a player accidentally cast on their own side (docs/PROVOKE_AC.md
    // criterion 3c). ScrubPlayerSideMarks runs every live tick regardless of the hold's own
    // Idle/Armed state, because a player can provoke an ally while a hold on some other enemy is
    // already up (or with no enemy marked at all). ----

    // ---- THE LOAD-BEARING TEST ----

    [Fact]
    public void A_mark_on_a_player_side_seat_is_scrubbed_on_both_layers_while_an_enemys_mark_survives_and_still_arms()
    {
        var m = new FakeSparseMemory();
        SeatBearer(m, 0, 0);
        SeatAlly(m, 1, lvl: 25, br: 40, fa: 60, gx: 2, gy: 2);
        SetMark(m, 1, true);   // the player cast Provoke at their own ally by mistake
        SeatAlly(m, 2, lvl: 26, br: 41, fa: 61, gx: 3, gy: 3);   // hidden-when-armed control seat
        SeatEnemy(m, 10, lvl: 20, br: 30, fa: 30, gx: 5, gy: 5, marked: true, active: true, nameId: 500);

        var hold = new ProvokeHold(Tier3Kills(), m);
        hold.Tick(DateTime.UtcNow, true);

        Assert.False(HasMark(m, 1));            // composed layer scrubbed off the ally
        Assert.False(HasInflictedMark(m, 1));   // inflicted layer scrubbed too
        Assert.True(HasMark(m, 10));            // the enemy's own mark is untouched
        Assert.True(IsInvisible(m, 2));         // the enemy mark still arms the hold normally
    }

    // ---- MASK DISCIPLINE: neighbouring bits on the shared composed/inflicted bytes survive ----

    [Fact]
    public void Scrubbing_a_player_side_mark_touches_only_the_mark_bit_leaving_neighbouring_status_bits_alone()
    {
        var m = new FakeSparseMemory();
        SeatBearer(m, 0, 0);
        SeatAlly(m, 1, lvl: 25, br: 40, fa: 60, gx: 2, gy: 2);
        SetMark(m, 1, true);

        long composedByte = Band.Entry(1) + StatusApply.Composed;
        long inflictedByte = Band.Entry(1) + StatusApply.Inflicted;
        // Composed +0x45 is the SAME byte Dead/Undead/Jump/Charging live on (KillTracker reads
        // it), so a correct clear must leave every one of these set exactly as found.
        m.U8s[composedByte] = (byte)(m.U8(composedByte) | Offsets.AUndeadBit | Offsets.AJumpBit | Offsets.AChargingBit);
        // An unrelated inflicted bit (status id 7, mask 0x01) must also survive untouched.
        byte unrelatedInflictedMask = StatusApply.StatusMask(7);
        m.U8s[inflictedByte] = (byte)(m.U8(inflictedByte) | unrelatedInflictedMask);
        m.WritableAddrs.Add(composedByte);
        m.WritableAddrs.Add(inflictedByte);
        byte composedBefore = m.U8(composedByte);
        byte inflictedBefore = m.U8(inflictedByte);

        var hold = new ProvokeHold(Tier3Kills(), m);
        hold.Tick(DateTime.UtcNow, true);

        byte mask = StatusApply.StatusMask(ProvokeHold.MarkId);
        Assert.Equal(composedBefore & ~mask, m.U8(composedByte) & ~mask);   // every OTHER composed bit unchanged
        Assert.Equal(0, m.U8(composedByte) & mask);                          // only the mark bit cleared
        Assert.Equal(inflictedBefore & ~mask, m.U8(inflictedByte) & ~mask); // every OTHER inflicted bit unchanged
        Assert.Equal(0, m.U8(inflictedByte) & mask);
    }

    // ---- SCRUB WHILE ARMED: a friendly mark landing mid-hold is cleared without disturbing the
    // hold's own armed state on a different (enemy) mark ----

    [Fact]
    public void A_player_side_mark_cast_mid_hold_is_scrubbed_while_the_hold_stays_armed_on_its_own_enemy()
    {
        var m = new FakeSparseMemory();
        SeatBearer(m, 0, 0);
        SeatAlly(m, 2, lvl: 26, br: 41, fa: 61, gx: 3, gy: 3);   // hidden-when-armed control seat
        SeatEnemy(m, 10, lvl: 20, br: 30, fa: 30, gx: 5, gy: 5, marked: true, active: true, nameId: 500);

        var hold = new ProvokeHold(Tier3Kills(), m);
        var t0 = DateTime.UtcNow;
        hold.Tick(t0, true);   // arms on the enemy
        Assert.True(IsInvisible(m, 2));

        SeatAlly(m, 1, lvl: 25, br: 40, fa: 60, gx: 2, gy: 2);
        SetMark(m, 1, true);   // a friendly cast lands mid-hold
        hold.Tick(t0.AddMilliseconds(33), true);

        Assert.False(HasMark(m, 1));      // scrubbed off the ally
        Assert.True(HasMark(m, 10));      // the armed hold's own mark is untouched
        Assert.True(IsInvisible(m, 2));   // still armed: no release, no state reset
    }

    // ---- OFF-FIELD / INVALID SEATS SKIPPED (criterion 7's own gate, reused here) ----

    [Fact]
    public void ScrubPlayerSideMarks_never_writes_to_an_offfield_or_invalid_seat()
    {
        var m = new FakeSparseMemory();
        SeatBearer(m, 0, 0);
        SeatAlly(m, 1, lvl: 25, br: 40, fa: 60, gx: 3, gy: 3);
        SetMark(m, 1, true);
        m.U8s[Band.Entry(1) + Offsets.AGateByte] = Offsets.AGateHiddenValue;   // combat +0x01 == 0xFF

        // bandIdx 2 is never seated at all (lvl/br/fa/mhp all read 0) -> Band.IsValid is false.
        SetMark(m, 2, true);

        var hold = new ProvokeHold(Tier3Kills(), m);
        hold.Tick(DateTime.UtcNow, true);

        Assert.True(HasMark(m, 1));   // off-field: skipped, mark survives untouched
        Assert.True(HasMark(m, 2));   // invalid seat: skipped, mark survives untouched
        Assert.False(m.Written.ContainsKey(Band.Entry(1) + StatusApply.Composed));
        Assert.False(m.Written.ContainsKey(Band.Entry(2) + StatusApply.Composed));
    }

    // ---- NO-OP TICK: nobody player-side marked -> zero writes to any composed status byte ----

    [Fact]
    public void A_tick_with_no_player_side_mark_writes_nothing_to_any_composed_status_byte()
    {
        var m = new FakeSparseMemory();
        SeatBearer(m, 0, 0);
        SeatAlly(m, 1, lvl: 25, br: 40, fa: 60, gx: 2, gy: 2);
        SeatEnemy(m, 10, lvl: 20, br: 30, fa: 30, gx: 5, gy: 5, marked: true, active: true, nameId: 500);

        var hold = new ProvokeHold(Tier3Kills(), m);
        hold.Tick(DateTime.UtcNow, true);

        Assert.False(m.Written.ContainsKey(Band.Entry(0) + StatusApply.Composed));
        Assert.False(m.Written.ContainsKey(Band.Entry(1) + StatusApply.Composed));
    }

    // ---- NOT IN LIVE BATTLE: a false inLive scrubs nothing ----

    [Fact]
    public void ScrubPlayerSideMarks_does_nothing_when_not_in_a_live_battle()
    {
        var m = new FakeSparseMemory();
        SeatBearer(m, 0, 0);
        SeatAlly(m, 1, lvl: 25, br: 40, fa: 60, gx: 2, gy: 2);
        SetMark(m, 1, true);

        var hold = new ProvokeHold(Tier3Kills(), m);
        hold.Tick(DateTime.UtcNow, false);

        Assert.True(HasMark(m, 1));
    }

    // ---- THE BEARER: included, not exempt ----

    [Fact]
    public void A_mark_on_the_bearer_itself_is_scrubbed_too()
    {
        var m = new FakeSparseMemory();
        SeatBearer(m, 0, 0);
        SetMark(m, 0, true);

        var hold = new ProvokeHold(Tier3Kills(), m);
        hold.Tick(DateTime.UtcNow, true);

        Assert.False(HasMark(m, 0));
        Assert.False(HasInflictedMark(m, 0));
    }
}
