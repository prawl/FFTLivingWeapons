using System.Collections.Generic;
using LivingWeapon;
using Xunit;
using static LivingWeapon.Tests.KillTrackerFixtures;

namespace LivingWeapon.Tests;

/// <summary>
/// A guest or an ally dying must never pay a weapon, even when the enemy oracle captured its
/// identity. The side bit is judged by its last reading while the unit was alive.
/// </summary>
public class KillTrackerSideGateTests
{
    private const int Weapon = 19;
    private const int Victim = 10;

    private static long SideByte(int slot) => Band.Entry(slot) + Offsets.AFriendFoe;

    private static KillTracker Latched(FakeSparseMemory m, Dictionary<int, int> kills, List<string> tape)
    {
        SetRoster(m, slot: 3, level: 99, brave: 89, faith: 76, weapon: Weapon);
        SetUnit(m, Offsets.SlotsBack, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetActive(m, hp: 352, maxHp: 352, level: 99);
        var t = new KillTracker(kills, m, new HashSet<int> { Weapon }, recorder: (cat, msg) => tape.Add(cat + " " + msg));
        Settle(t);
        return t;
    }

    [Fact]
    public void A_guest_the_oracle_captured_never_pays_when_it_dies()
    {
        var kills = new Dictionary<int, int>();
        var tape = new List<string>();
        var m = new FakeSparseMemory();
        var t = Latched(m, kills, tape);
        SetEnemy(m, Victim, hp: 300);
        m.U8s[SideByte(Victim)] = 0;   // the oracle captured the identity, the unit reads player side
        Settle(t);

        SetUnit(m, Victim, hp: 0);
        Settle(t);

        Assert.Equal(0, kills.GetValueOrDefault(Weapon));
        Assert.Contains(tape, l => l.Contains($"no-credit slot={Victim} reason=player-side"));
    }

    [Fact]
    public void An_enemy_with_the_side_bit_set_pays_once()
    {
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        var t = Latched(m, kills, new List<string>());
        SetEnemy(m, Victim, hp: 300);
        Settle(t);

        SetUnit(m, Victim, hp: 0);
        Settle(t);

        Assert.Equal(1, kills.GetValueOrDefault(Weapon));
    }

    [Fact]
    public void The_side_bit_read_at_death_does_not_matter()
    {
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        var t = Latched(m, kills, new List<string>());
        SetEnemy(m, Victim, hp: 300);
        Settle(t);

        SetUnit(m, Victim, hp: 0);
        m.U8s[SideByte(Victim)] = 0;   // a corpse may read anything, only the living reading counts
        Settle(t);

        Assert.Equal(1, kills.GetValueOrDefault(Weapon));
    }
}
