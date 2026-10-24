using System.Collections.Generic;
using LivingWeapon;
using Xunit;
using static LivingWeapon.Tests.KillTrackerFixtures;

namespace LivingWeapon.Tests;

/// <summary>
/// Credit rules that must agree on a Retry and on a changed unit. The swap-branch tests keep battle
/// age under the grace floor and fire no retry hook, so only the identity match can open the latch.
/// </summary>
public class KillTrackerCreditRulesTests
{
    private const int Wilham = Offsets.SlotsBack;
    private const int Weapon = 19;
    private const int Slot = 10;
    private const int NameId = 555;

    private static void AdvanceTicks(KillTracker t, int n) { for (int i = 0; i < n; i++) t.Poll(true); }

    private static void NullPointer(FakeSparseMemory m)
    {
        m.MarkReadable(Offsets.ActorPtr, 8);
        m.SeedU64(Offsets.ActorPtr, 0);
    }

    private static void SetReadableNameId(FakeSparseMemory m, int bandIdx, int nameId)
    {
        SetFrameNameId(m, bandIdx, nameId);
        m.MarkReadable(Band.Entry(bandIdx) + Offsets.ANameId, 2);
    }

    private static KillTracker MakeLatchedTracker(FakeSparseMemory m, Dictionary<int, int> kills,
        List<string>? tape = null)
    {
        SetRoster(m, slot: 3, level: 99, brave: 89, faith: 76, weapon: Weapon);
        SetUnit(m, Wilham, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetActive(m, hp: 352, maxHp: 352, level: 99);
        var t = new KillTracker(kills, m, new HashSet<int> { Weapon },
            recorder: tape == null ? null : (cat, msg) => tape.Add(cat + " " + msg));
        Settle(t);
        return t;
    }

    /// <summary>Credit slot 10 as (10,50,50,400) with nameId 555, then qualify a null inside the
    /// join window while battle age is far under the grace floor. No retry hook is wired.</summary>
    private static KillTracker CreditThenQualifyNull(FakeSparseMemory m, Dictionary<int, int> kills)
    {
        var t = MakeLatchedTracker(m, kills);
        SetReadableNameId(m, Slot, NameId);
        AliveThenDead(m, Slot, t);
        Assert.Equal(1, kills.GetValueOrDefault(Weapon));
        NullPointer(m);
        AdvanceTicks(t, 2);
        PointAt(m, Wilham);
        return t;
    }

    private static void Revive(FakeSparseMemory m, KillTracker t, int nameId, int level = 10, int brave = 70, int maxHp = 400)
    {
        SetUnit(m, Slot, hp: 100, maxHp: maxHp, level: level, brave: brave, faith: 50);
        SetReadableNameId(m, Slot, nameId);
        t.Poll(true);
    }

    [Fact]
    public void A_retry_that_restores_brave_on_the_same_unit_uncredits_through_the_swap_branch()
    {
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        var t = CreditThenQualifyNull(m, kills);

        Revive(m, t, NameId);

        Assert.Equal(0, kills.GetValueOrDefault(Weapon));
        Assert.True(t.Restart.LatchOpen);
    }

    [Fact]
    public void A_swap_with_a_different_nameId_never_uncredits()
    {
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        var t = CreditThenQualifyNull(m, kills);

        Revive(m, t, 777);

        Assert.Equal(1, kills.GetValueOrDefault(Weapon));
        Assert.False(t.Restart.LatchOpen);
    }

    [Fact]
    public void A_swap_with_a_different_max_hp_never_uncredits()
    {
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        var t = CreditThenQualifyNull(m, kills);

        Revive(m, t, NameId, maxHp: 450);

        Assert.Equal(1, kills.GetValueOrDefault(Weapon));
        Assert.False(t.Restart.LatchOpen);
    }

    [Fact]
    public void A_swap_with_a_different_level_never_uncredits()
    {
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        var t = CreditThenQualifyNull(m, kills);

        Revive(m, t, NameId, level: 11);

        Assert.Equal(1, kills.GetValueOrDefault(Weapon));
        Assert.False(t.Restart.LatchOpen);
    }

    [Theory]
    [InlineData(10, 400, 555, 10, 400, 555, true)]    // same level, max hp and nameId
    [InlineData(10, 400, 555, 11, 400, 555, false)]   // level moved
    [InlineData(10, 400, 555, 10, 450, 555, false)]   // max hp moved
    [InlineData(10, 400, 555, 10, 400, 777, false)]   // nameId moved
    [InlineData(10, 400, 0, 10, 400, 0, false)]       // zero on both sides fails closed
    [InlineData(10, 400, 0, 10, 400, 555, false)]     // zero credited side
    [InlineData(10, 400, 555, 10, 400, 0, false)]     // zero presented side
    public void SwapIdentityMatches_compares_level_max_hp_and_nameId_only(
        byte lvlA, ushort mhpA, ushort nameA, byte lvlB, ushort mhpB, ushort nameB, bool expected)
    {
        var credited = (lvlA, (byte)50, (byte)50, mhpA);
        var presented = (lvlB, (byte)90, (byte)10, mhpB);   // brave and faith differ and must be ignored

        Assert.Equal(expected, RestartSentinelPolicy.SwapIdentityMatches(credited, presented, nameA, nameB));
    }

    [Fact]
    public void Two_untracked_buries_leave_the_same_record_so_a_twin_death_is_blocked()
    {
        var kills = new Dictionary<int, int>();
        var tape = new List<string>();
        var m = new FakeSparseMemory();
        var t = MakeLatchedTracker(m, kills, tape);

        const int first = 10, second = 11;
        SetEnemy(m, first, hp: 300, maxHp: 400);
        SetEnemy(m, second, hp: 300, maxHp: 400);
        Settle(t);

        // The dead tick reads 380, so the alive key (10,50,50,400) is not the death key. The array
        // must hold 380 or the death stops at not-tracked-enemy and proves nothing.
        SetArrayEnemy(m, first, level: 10, brave: 50, faith: 50, maxHp: 380);
        m.MarkReadable(Offsets.TurnQueue + Offsets.TqTeam, 2);   // the team read is readable-gated
        SetActive(m, hp: 352, maxHp: 352, level: 99, team: 1);   // enemy turn stamps an untracked verdict
        SetUnit(m, first, hp: 0, maxHp: 380);
        Settle(t);
        Assert.Contains(tape, r => r.Contains("orphan-alive-edge slot=10"));
        Assert.Contains(tape, r => r.Contains("reason=untracked-weapon"));
        Assert.Equal(0, kills.GetValueOrDefault(Weapon));

        SetActive(m, hp: 352, maxHp: 352, level: 99, team: 0);
        SetUnit(m, second, hp: 0, maxHp: 380);
        Settle(t);

        Assert.Contains(tape, r => r.Contains("no-credit slot=11 reason=identity-already-resolved"));
        Assert.Equal(0, kills.GetValueOrDefault(Weapon));
    }

    [Fact]
    public void A_max_hp_drop_while_alive_does_not_orphan_the_real_death()
    {
        var kills = new Dictionary<int, int>();
        var tape = new List<string>();
        var m = new FakeSparseMemory();
        var t = MakeLatchedTracker(m, kills, tape);

        SetEnemy(m, Slot, hp: 300, maxHp: 400);
        Settle(t);
        SetEnemy(m, Slot, hp: 300, maxHp: 350);   // array holds 350 too, so the oracle knows the death key
        Settle(t);
        SetUnit(m, Slot, hp: 0, maxHp: 350);
        Settle(t);

        Assert.Equal(1, kills.GetValueOrDefault(Weapon));
        Assert.DoesNotContain(tape, r => r.Contains("orphan-alive-edge"));
        Assert.Contains(tape, r => r.Contains("max-hp-moved slot=10 mhp=400->350"));
    }

    [Fact]
    public void The_identity_swap_clears_the_fallback_flag_with_the_actor_stamp()
    {
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        var t = MakeLatchedTracker(m, kills);
        AliveThenDead(m, Slot, t);

        t._lethalViaFallback[Slot] = true;
        SetUnit(m, Slot, hp: 100, maxHp: 400, brave: 70);
        t.Poll(true);

        Assert.False(t._lethalViaFallback[Slot]);
    }
}
