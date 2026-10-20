using System.Collections.Generic;
using LivingWeapon;
using Xunit;
using static LivingWeapon.Tests.KillTrackerFixtures;

namespace LivingWeapon.Tests;

/// <summary>The battle exit settle pays a corpse seen dying on the field when the victory screen
/// beat the DeadNeeded streak.</summary>
public class KillTrackerSettleTests
{
    private const int Weapon = 19;
    private const int Enemy = 10;

    private static KillTracker Latched(FakeSparseMemory m, Dictionary<int, int> kills)
    {
        SetRoster(m, slot: 3, level: 99, brave: 89, faith: 76, weapon: Weapon);
        SetUnit(m, Offsets.SlotsBack, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetActive(m, hp: 352, maxHp: 352, level: 99);
        var t = new KillTracker(kills, m, new HashSet<int> { Weapon });
        Settle(t);
        return t;
    }

    private static void Kill(FakeSparseMemory m, KillTracker t, int slot, int deadFieldPolls)
    {
        SetEnemy(m, slot, hp: 300);
        Settle(t);
        SetUnit(m, slot, hp: 0);
        for (int i = 0; i < deadFieldPolls; i++) t.Poll(true);
    }

    private static void FieldDown(KillTracker t) { for (int i = 0; i < 4; i++) t.Poll(false); }

    [Fact]
    public void A_death_seen_on_the_field_for_fewer_than_DeadNeeded_polls_is_paid_at_exit()
    {
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        var t = Latched(m, kills);
        Kill(m, t, Enemy, deadFieldPolls: 2);
        FieldDown(t);
        Assert.Equal(0, kills.GetValueOrDefault(Weapon));

        Assert.True(t.SettleCorpsesAtBattleEnd());

        Assert.Equal(1, kills.GetValueOrDefault(Weapon));
        Assert.Equal(1, t.BattleCredits.GetValueOrDefault(Weapon));
    }

    [Fact]
    public void A_death_first_seen_while_the_field_is_down_is_not_settled()
    {
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        var t = Latched(m, kills);
        Kill(m, t, Enemy, deadFieldPolls: 0);
        FieldDown(t);

        Assert.False(t.SettleCorpsesAtBattleEnd());
        Assert.Equal(0, kills.GetValueOrDefault(Weapon));
    }

    [Fact]
    public void A_dead_party_member_is_not_paid()
    {
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        var t = Latched(m, kills);
        const int party = Offsets.SlotsBack + 1;
        SetUnit(m, party, hp: 200);
        Settle(t);
        SetUnit(m, party, hp: 0);
        t.Poll(true);
        t.Poll(true);
        FieldDown(t);

        Assert.False(t.SettleCorpsesAtBattleEnd());
        Assert.Equal(0, kills.GetValueOrDefault(Weapon));
    }

    [Fact]
    public void A_corpse_already_paid_on_the_field_is_not_paid_twice()
    {
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        var t = Latched(m, kills);
        Kill(m, t, Enemy, deadFieldPolls: 4);
        Assert.Equal(1, kills.GetValueOrDefault(Weapon));
        FieldDown(t);

        using var cap = LogCapture.Start(LogLevel.Debug);
        Assert.False(t.SettleCorpsesAtBattleEnd());
        Assert.Equal(1, kills.GetValueOrDefault(Weapon));
        Assert.DoesNotContain(cap.File, l => l.Contains("Blocked a repeat credit"));
    }

    [Fact]
    public void With_no_culprit_and_no_live_latch_the_settle_pays_nothing()
    {
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        SetRoster(m, slot: 3, level: 99, brave: 89, faith: 76, weapon: Weapon);
        var t = new KillTracker(kills, m, new HashSet<int> { Weapon });
        Kill(m, t, Enemy, deadFieldPolls: 2);
        FieldDown(t);

        Assert.False(t.SettleCorpsesAtBattleEnd());
        Assert.Equal(0, kills.GetValueOrDefault(Weapon));
    }

    [Fact]
    public void A_corpse_judged_on_the_field_and_left_pending_is_not_resolved_at_exit()
    {
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        SetRoster(m, slot: 3, level: 99, brave: 89, faith: 76, weapon: Weapon);
        var t = new KillTracker(kills, m, new HashSet<int> { Weapon });
        Kill(m, t, Enemy, deadFieldPolls: 4);
        Assert.Equal(0, kills.GetValueOrDefault(Weapon));
        SetUnit(m, Offsets.SlotsBack, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetActive(m, hp: 352, maxHp: 352, level: 99);
        FieldDown(t);

        Assert.False(t.SettleCorpsesAtBattleEnd());
        Assert.Equal(0, kills.GetValueOrDefault(Weapon));
    }

    private static List<string> SettleTape(FakeSparseMemory m, Dictionary<int, int> kills, bool latched, int deadFieldPolls)
    {
        var tape = new List<string>();
        SetRoster(m, slot: 3, level: 99, brave: 89, faith: 76, weapon: Weapon);
        if (latched)
        {
            SetUnit(m, Offsets.SlotsBack, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
            SetActive(m, hp: 352, maxHp: 352, level: 99);
        }
        var t = new KillTracker(kills, m, new HashSet<int> { Weapon }, recorder: (k, v) => tape.Add(k + " " + v));
        Settle(t);
        Kill(m, t, Enemy, deadFieldPolls);
        FieldDown(t);
        t.SettleCorpsesAtBattleEnd();
        return tape.FindAll(l => l.Contains("settle-at-exit"));
    }

    [Fact]
    public void A_corpse_left_pending_on_the_field_writes_no_settle_record()
    {
        var m = new FakeSparseMemory();
        Assert.Empty(SettleTape(m, new Dictionary<int, int>(), latched: false, deadFieldPolls: 4));
    }

    [Fact]
    public void A_qualifying_corpse_writes_exactly_one_settle_record()
    {
        var m = new FakeSparseMemory();
        Assert.Single(SettleTape(m, new Dictionary<int, int>(), latched: true, deadFieldPolls: 2));
    }

    [Fact]
    public void Settling_twice_pays_once()
    {
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        var t = Latched(m, kills);
        Kill(m, t, Enemy, deadFieldPolls: 2);
        FieldDown(t);

        Assert.True(t.SettleCorpsesAtBattleEnd());
        Assert.False(t.SettleCorpsesAtBattleEnd());
        Assert.Equal(1, kills.GetValueOrDefault(Weapon));
    }

    private static BannerToast Toast(Dictionary<int, int> kills) =>
        new(new Dictionary<int, WeaponMeta> { [Weapon] = new WeaponMeta { Name = "Test Blade" } }, kills, enabled: true);

    [Fact]
    public void The_exit_settle_hands_the_kill_to_the_banner_queue_so_no_later_battle_announces_it()
    {
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        var t = Latched(m, kills);
        var toast = Toast(kills);
        Kill(m, t, Enemy, deadFieldPolls: 2);
        FieldDown(t);

        Assert.True(BattleExitSettle.Run(t, toast, forced: false));
        Assert.True(toast.HasPending);

        toast.DropPendingAtBattleEnd();
        toast.Tick(tallyChanged: true);
        Assert.False(toast.HasPending);
    }

    [Fact]
    public void A_forced_new_game_exit_does_not_settle()
    {
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        var t = Latched(m, kills);
        var toast = Toast(kills);
        Kill(m, t, Enemy, deadFieldPolls: 2);
        FieldDown(t);

        Assert.False(BattleExitSettle.Run(t, toast, forced: true));
        Assert.Equal(0, kills.GetValueOrDefault(Weapon));
        Assert.False(toast.HasPending);
    }
}
