using System.Collections.Generic;
using System.Linq;
using LivingWeapon;
using Xunit;
using static LivingWeapon.Tests.KillTrackerFixtures;

namespace LivingWeapon.Tests;

/// <summary>
/// Reliquary P1 probe instrumentation (docs/RELIQUARY_AC.md): VictimProbe captures a victim's
/// nameId/job/undead-bit at three lifecycle points (alive, dead-edge, credit) so a later live run
/// can compare which point reads sane identity on a corpse. These tests lock in:
///   - each capture point stores what it observed at THAT tick (not a live re-read later);
///   - the reset paths (identity swap, revive, battle reset) clear stale snapshots;
///   - the flight-recorder tap fires ONLY from LogAtCredit (i.e. only on an actual credit), and its
///     payload proves the alive/edge snapshots are frozen even if game memory churns before credit.
/// Band/roster seeding rides the shared KillTrackerFixtures (LW-160).
/// </summary>
public class VictimProbeTests
{
    private const int Wilham = Offsets.SlotsBack;       // band slot 20 (player-side actor)

    private static readonly HashSet<int> Weapons = new() { 52 };

    // ---- direct VictimProbe unit tests (no KillTracker plumbing needed) ----

    [Fact]
    public void Unreadable_reads_never_throw()
    {
        var m = new FakeSparseMemory();   // nothing marked Readable
        var probe = new VictimProbe(m, recorder: null);
        long addr = Band.Entry(0);

        var ex = Record.Exception(() =>
        {
            probe.CaptureAlive(0, addr);
            probe.CaptureDeadEdge(0, addr);
            probe.LogAtCredit(0);
        });

        Assert.Null(ex);
        Assert.False(probe.AliveSnapshot(0).Has);
        Assert.False(probe.EdgeSnapshot(0).Has);
    }

    // ---- wiring tests: drive the real KillTracker so KillTracker.Corpses.cs's call sites are
    // proven, not just VictimProbe's own storage logic ----

    [Fact]
    public void Alive_capture_stores_victim_fields()
    {
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        SetEnemy(m, slot: 0, hp: 300);
        BandFixtures.SeedVictimFields(m, slot: 0, nameId: 918, job: 99, undead: false);
        var t = new KillTracker(kills, m, Weapons);

        t.Poll(true);   // one consistent alive on-field tick is enough -- fires every such tick

        var snap = t._victimProbe.AliveSnapshot(0);
        Assert.True(snap.Has);
        Assert.Equal((ushort)918, snap.NameId);
        Assert.Equal((byte)99, snap.Job);
        Assert.False(snap.Undead);
    }

    [Fact]
    public void Dead_edge_capture_fires_at_streak_one()
    {
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        SetEnemy(m, slot: 0, hp: 300);
        BandFixtures.SeedVictimFields(m, slot: 0, nameId: 918, job: 99, undead: true);
        var t = new KillTracker(kills, m, Weapons);
        Settle(t);   // 3 alive ticks -> seenAlive (the dead path requires it)

        SetUnit(m, slot: 0, hp: 0);
        t.Poll(true);   // dead tick 1: deadStreak == 1 -> edge capture fires here

        var snap = t._victimProbe.EdgeSnapshot(0);
        Assert.True(snap.Has);
        Assert.Equal((ushort)918, snap.NameId);
        Assert.Equal((byte)99, snap.Job);
        Assert.True(snap.Undead);
    }

    [Fact]
    public void Credit_line_reports_edge_values_even_after_memory_churn()
    {
        // THE LOAD-BEARING TEST: the probe must SNAPSHOT at alive/edge time, not re-read late.
        // Prove it by zeroing the victim's nameId/job bytes AFTER the dead-edge capture but
        // BEFORE credit -- the recorder's alive=/edge= tuples must still show the original
        // values while credit= shows the churned (zeroed) ones.
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        var recorded = new List<(string type, string payload)>();
        SetRoster(m, slot: 3, level: 99, brave: 89, faith: 76, weapon: 52);
        SetUnit(m, Wilham, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetActive(m, hp: 352, maxHp: 352, level: 99);
        SetEnemy(m, slot: 0, hp: 300);
        BandFixtures.SeedVictimFields(m, slot: 0, nameId: 918, job: 99, undead: false);
        var t = new KillTracker(kills, m, Weapons, recorder: (type, payload) => recorded.Add((type, payload)));

        Settle(t);   // latch the actor's weapon + build seenAlive

        SetUnit(m, slot: 0, hp: 0);
        t.Poll(true);   // dead tick 1 -> deadStreak==1, edge snapshot captured with the ORIGINAL values

        // Memory churns between the edge and the credit tick.
        long addr = Band.Entry(0);
        m.U16s[addr + Offsets.ANameId] = 0;
        m.U8s[addr + Puppeteer.JobOff] = 0;

        t.Poll(true);                    // dead tick 2
        bool credited = t.Poll(true);     // dead tick 3 -> DeadNeeded reached -> CreditKill -> LogAtCredit

        Assert.True(credited);
        Assert.Equal(1, kills.GetValueOrDefault(52));
        var rec = Assert.Single(recorded, r => r.type == "victim");
        Assert.Contains("alive=(nameId=918,job=99,undead=0,has=1)", rec.payload);
        Assert.Contains("edge=(nameId=918,job=99,undead=0,has=1)", rec.payload);
        Assert.Contains("credit=(nameId=0,job=0,undead=0,has=1)", rec.payload);
    }

    [Fact]
    public void No_flight_tap_without_credit()
    {
        // A corpse whose identity was never captured in the static array is never credited --
        // LogAtCredit only runs from CreditKill, so no "victim" record should ever appear.
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        var recorded = new List<(string type, string payload)>();
        SetRoster(m, slot: 3, level: 99, brave: 89, faith: 76, weapon: 52);
        SetUnit(m, Wilham, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetActive(m, hp: 352, maxHp: 352, level: 99);
        var t = new KillTracker(kills, m, Weapons, recorder: (type, payload) => recorded.Add((type, payload)));
        Settle(t);

        // Band entry exists but NO array entry (inb) -- unknown identity, never credited.
        SetUnit(m, slot: 0, hp: 300, maxHp: 400, level: 10, brave: 50, faith: 50);
        BandFixtures.SeedVictimFields(m, slot: 0, nameId: 42, job: 7, undead: false);
        Settle(t, 3);   // seenAlive
        SetUnit(m, slot: 0, hp: 0, maxHp: 400, level: 10, brave: 50, faith: 50);
        Settle(t, 3);   // deadStreak, never credited (unknown identity)

        Assert.Empty(kills);
        Assert.DoesNotContain(recorded, r => r.type == "victim");
    }

    // ---- reset paths ----

    [Fact]
    public void Identity_change_clears_snapshots()
    {
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        SetEnemy(m, slot: 0, hp: 300, maxHp: 400, level: 10, brave: 50, faith: 50);
        BandFixtures.SeedVictimFields(m, slot: 0, nameId: 918, job: 99, undead: false);
        var t = new KillTracker(kills, m, Weapons);
        Settle(t, 3);   // seenAlive with identity (10,50,50); alive snapshot captured

        Assert.True(t._victimProbe.AliveSnapshot(0).Has);

        // SWAP: a different unit appears at the same slot (level changes) -- the identity-change
        // branch fires and must clear this slot's snapshots.
        SetEnemy(m, slot: 0, hp: 300, maxHp: 400, level: 15, brave: 55, faith: 55);
        t.Poll(true);

        Assert.False(t._victimProbe.AliveSnapshot(0).Has);
    }

    [Fact]
    public void Revive_clears_snapshots()
    {
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        SetRoster(m, slot: 3, level: 99, brave: 89, faith: 76, weapon: 52);
        SetUnit(m, Wilham, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetActive(m, hp: 352, maxHp: 352, level: 99);
        SetEnemy(m, slot: 0, hp: 300, maxHp: 400, level: 10, brave: 50, faith: 50);
        BandFixtures.SeedVictimFields(m, slot: 0, nameId: 918, job: 99, undead: false);
        var t = new KillTracker(kills, m, Weapons);
        Settle(t);

        SetUnit(m, slot: 0, hp: 0, maxHp: 400, level: 10, brave: 50, faith: 50);
        Settle(t, 3);   // credited; edge snapshot captured

        Assert.True(t._victimProbe.EdgeSnapshot(0).Has);
        Assert.Equal(1, kills.GetValueOrDefault(52));

        // Revive: seen alive again (same identity) -> the revive branch must clear the edge snapshot.
        SetEnemy(m, slot: 0, hp: 300, maxHp: 400, level: 10, brave: 50, faith: 50);
        Settle(t, 3);

        Assert.False(t._victimProbe.EdgeSnapshot(0).Has);
    }

    [Fact]
    public void ResetBattle_clears_snapshots()
    {
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        SetEnemy(m, slot: 0, hp: 300);
        BandFixtures.SeedVictimFields(m, slot: 0, nameId: 918, job: 99, undead: false);
        var t = new KillTracker(kills, m, Weapons);
        Settle(t);

        Assert.True(t._victimProbe.AliveSnapshot(0).Has);

        t.ResetBattle();

        Assert.False(t._victimProbe.AliveSnapshot(0).Has);
    }
}
