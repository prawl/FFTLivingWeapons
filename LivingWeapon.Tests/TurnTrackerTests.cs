using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// Per-unit turn counting behind the IGameMemory fake (no live game). The tracker credits a
/// completed turn to the ACTIVE unit (resolved by the turn-queue HP/MaxHP/level -> BAND entry
/// -> level/brave/faith fingerprint) on each rising edge of the global "acted" flag.
/// Drives timed signatures like Galewind's Speed +3 for the wielder's first 3 turns.
/// </summary>
public class TurnTrackerTests
{
    /// <summary>Seat a unit in BAND slot <paramref name="bandIdx"/> AND make it the active (turn-queue) unit.
    /// TurnTracker now resolves the active unit via the band (not the static array).</summary>
    private static void SetActive(FakeSparseMemory m, int bandIdx, int hp, int maxHp, int level, int brave, int faith)
    {
        long slot = Offsets.BandReadBase + (long)bandIdx * Offsets.CombatStride;
        m.U16s[slot + Offsets.AMaxHp] = (ushort)maxHp;
        m.U16s[slot + Offsets.AHp] = (ushort)hp;
        m.U8s[slot + Offsets.ALevel] = (byte)level;
        m.U8s[slot + Offsets.ABrave] = (byte)brave;
        m.U8s[slot + Offsets.AFaith] = (byte)faith;
        m.U16s[Offsets.TurnQueue + Offsets.TqMaxHp] = (ushort)maxHp;
        m.U16s[Offsets.TurnQueue + Offsets.TqHp] = (ushort)hp;
        m.U16s[Offsets.TurnQueue + Offsets.TqLevel] = (ushort)level;
    }

    private static void Acted(FakeSparseMemory m, int v) => m.U8s[Offsets.Acted] = (byte)v;

    [Fact]
    public void Counts_one_turn_per_acted_rising_edge()
    {
        var m = new FakeSparseMemory();
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
        var m = new FakeSparseMemory();
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
        var m = new FakeSparseMemory();
        var t = new TurnTracker(m);
        SetActive(m, 5, 100, 100, 20, 70, 50); Acted(m, 1); t.Poll();
        Assert.Equal(1, t.Turns(20, 70, 50));
        t.ResetBattle();
        Assert.Equal(0, t.Turns(20, 70, 50));
    }

    [Fact]
    public void Unresolvable_active_unit_counts_nothing()
    {
        var m = new FakeSparseMemory();
        var t = new TurnTracker(m);
        // Active unit named in the turn queue, but NO matching band entry.
        m.U16s[Offsets.TurnQueue + Offsets.TqMaxHp] = 100;
        m.U16s[Offsets.TurnQueue + Offsets.TqHp] = 100;
        m.U16s[Offsets.TurnQueue + Offsets.TqLevel] = 20;
        Acted(m, 1);
        t.Poll();   // must not throw, must not count
        Assert.Equal(0, t.Turns(20, 70, 50));
    }

    [Fact]
    public void Ambiguous_distinct_fingerprints_credits_nothing()
    {
        // Two band entries match the turn-queue HP/MaxHP/level but have DIFFERENT (lvl,brave,faith)
        // fingerprints -- ambiguous actor -> no turn credited (miss beats mis-credit).
        var m = new FakeSparseMemory();
        var t = new TurnTracker(m);
        // Two units, same HP/MaxHP/level, different brave/faith.
        long s1 = Offsets.BandReadBase + (long)5 * Offsets.CombatStride;
        long s2 = Offsets.BandReadBase + (long)6 * Offsets.CombatStride;
        m.U16s[s1 + Offsets.AMaxHp] = 100; m.U16s[s1 + Offsets.AHp] = 100;
        m.U8s[s1 + Offsets.ALevel] = 20; m.U8s[s1 + Offsets.ABrave] = 70; m.U8s[s1 + Offsets.AFaith] = 50;
        m.U16s[s2 + Offsets.AMaxHp] = 100; m.U16s[s2 + Offsets.AHp] = 100;
        m.U8s[s2 + Offsets.ALevel] = 20; m.U8s[s2 + Offsets.ABrave] = 60; m.U8s[s2 + Offsets.AFaith] = 40;
        m.U16s[Offsets.TurnQueue + Offsets.TqMaxHp] = 100;
        m.U16s[Offsets.TurnQueue + Offsets.TqHp] = 100;
        m.U16s[Offsets.TurnQueue + Offsets.TqLevel] = 20;
        Acted(m, 1); t.Poll();

        Assert.Equal(0, t.Turns(20, 70, 50));   // ambiguous -> nothing credited
        Assert.Equal(0, t.Turns(20, 60, 40));
    }
}
