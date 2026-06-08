using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// Per-unit turn counting behind the IGameMemory fake (no live game). The tracker credits a
/// completed turn to the ACTIVE unit (resolved by the turn-queue HP/MaxHP/level -> static-array
/// slot -> level/brave/faith fingerprint) on each rising edge of the global "acted" flag.
/// Drives timed signatures like Galewind's Speed +3 for the wielder's first 3 turns.
/// </summary>
public class TurnTrackerTests
{
    private sealed class FakeMemory : IGameMemory
    {
        public readonly System.Collections.Generic.Dictionary<long, ushort> U16s = new();
        public readonly System.Collections.Generic.Dictionary<long, byte> U8s = new();
        public byte U8(long a) => U8s.TryGetValue(a, out var v) ? v : (byte)0;
        public ushort U16(long a) => U16s.TryGetValue(a, out var v) ? v : (ushort)0;
    }

    /// <summary>Seat a unit in static-array slot `arrIdx` AND make it the active (turn-queue) unit.</summary>
    private static void SetActive(FakeMemory m, int arrIdx, int hp, int maxHp, int level, int brave, int faith)
    {
        long slot = Offsets.ArrayReadBase + (long)arrIdx * Offsets.ArrayStride;
        m.U16s[slot + Offsets.AMaxHp] = (ushort)maxHp;
        m.U16s[slot + Offsets.AHp] = (ushort)hp;
        m.U8s[slot + Offsets.ALevel] = (byte)level;
        m.U8s[slot + Offsets.ABrave] = (byte)brave;
        m.U8s[slot + Offsets.AFaith] = (byte)faith;
        m.U16s[Offsets.TurnQueue + Offsets.TqMaxHp] = (ushort)maxHp;
        m.U16s[Offsets.TurnQueue + Offsets.TqHp] = (ushort)hp;
        m.U16s[Offsets.TurnQueue + Offsets.TqLevel] = (ushort)level;
    }

    private static void Acted(FakeMemory m, int v) => m.U8s[Offsets.Acted] = (byte)v;

    [Fact]
    public void Counts_one_turn_per_acted_rising_edge()
    {
        var m = new FakeMemory();
        var t = new TurnTracker(m);
        SetActive(m, 5, hp: 100, maxHp: 100, level: 20, brave: 70, faith: 50);
        Acted(m, 0); t.Poll();
        Assert.Equal(0, t.Turns(20, 70, 50));
        Acted(m, 1); t.Poll();                       // rising edge -> +1
        Assert.Equal(1, t.Turns(20, 70, 50));
        t.Poll();                                    // still 1, no new edge
        Assert.Equal(1, t.Turns(20, 70, 50));
        Acted(m, 0); t.Poll();
        Acted(m, 1); t.Poll();                       // rises again -> +1
        Assert.Equal(2, t.Turns(20, 70, 50));
    }

    [Fact]
    public void Tracks_turns_per_unit_independently()
    {
        var m = new FakeMemory();
        var t = new TurnTracker(m);
        SetActive(m, 5, 100, 100, 20, 70, 50); Acted(m, 1); t.Poll();
        Acted(m, 0); t.Poll();
        SetActive(m, 6, 80, 80, 15, 60, 40); Acted(m, 1); t.Poll();
        Assert.Equal(1, t.Turns(20, 70, 50));
        Assert.Equal(1, t.Turns(15, 60, 40));
        Assert.Equal(0, t.Turns(99, 99, 99));        // a unit that never acted
    }

    [Fact]
    public void ResetBattle_clears_counts()
    {
        var m = new FakeMemory();
        var t = new TurnTracker(m);
        SetActive(m, 5, 100, 100, 20, 70, 50); Acted(m, 1); t.Poll();
        Assert.Equal(1, t.Turns(20, 70, 50));
        t.ResetBattle();
        Assert.Equal(0, t.Turns(20, 70, 50));
    }

    [Fact]
    public void Unresolvable_active_unit_counts_nothing()
    {
        var m = new FakeMemory();
        var t = new TurnTracker(m);
        // Active unit named in the turn queue, but NO matching static-array slot.
        m.U16s[Offsets.TurnQueue + Offsets.TqMaxHp] = 100;
        m.U16s[Offsets.TurnQueue + Offsets.TqHp] = 100;
        m.U16s[Offsets.TurnQueue + Offsets.TqLevel] = 20;
        Acted(m, 1);
        t.Poll();   // must not throw, must not count
        Assert.Equal(0, t.Turns(20, 70, 50));
    }
}
