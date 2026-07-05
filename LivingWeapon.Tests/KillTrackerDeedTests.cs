using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// Reliquary Phase 1 capture wiring (docs/RELIQUARY_AC.md): KillTracker.Corpses.cs stamps a
/// per-slot victim snapshot (<c>_victimAtEdge</c>) at the SAME dead-edge tick VictimProbe already
/// captures (deadStreak==1), via the shared VictimReader.Read. KillTracker.cs's CreditKill
/// consumes it EXACTLY ONCE (both the credited and no-credit branches clear it) and reports it to
/// an injected IDeedSink (null-safe -- every existing call site that omits `deeds` behaves
/// byte-identically). Mirrors the fake-memory band/roster seeding pattern from KillTrackerTests.cs
/// and VictimProbeTests.cs.
/// </summary>
public class KillTrackerDeedTests
{
    private static void SetActive(FakeSparseMemory m, int hp, int maxHp, int level, int team = 0, int acted = 1)
    {
        m.U16s[Offsets.TurnQueue + Offsets.TqTeam] = (ushort)team;
        m.U16s[Offsets.TurnQueue + Offsets.TqHp] = (ushort)hp;
        m.U16s[Offsets.TurnQueue + Offsets.TqMaxHp] = (ushort)maxHp;
        m.U16s[Offsets.TurnQueue + Offsets.TqLevel] = (ushort)level;
        m.U8s[Offsets.Acted] = (byte)acted;
    }

    private static void SetUnit(FakeSparseMemory m, int slot, int hp, int maxHp = 400, int gx = 5, int gy = 5,
                                int level = 10, int brave = 50, int faith = 50, int weapon = 0)
        => MemSeats.SeatBand(m, slot, weapon: weapon, lvl: level, br: brave, fa: faith,
                             gx: gx, gy: gy, hp: hp, maxHp: maxHp);

    private static void SetArrayEnemy(FakeSparseMemory m, int slot, int level, int brave, int faith, int maxHp,
                                      int inb = 1)
    {
        long s = Offsets.ArrayReadBase + (long)slot * Offsets.ArrayStride;
        m.U16s[s + Offsets.AInBattle] = (ushort)inb;
        m.U8s[s + Offsets.ALevel] = (byte)level;
        m.U8s[s + Offsets.ABrave] = (byte)brave;
        m.U8s[s + Offsets.AFaith] = (byte)faith;
        m.U16s[s + Offsets.AMaxHp] = (ushort)maxHp;
    }

    private static void SetEnemy(FakeSparseMemory m, int slot, int hp, int maxHp = 400, int gx = 5, int gy = 5,
                                 int level = 10, int brave = 50, int faith = 50)
    {
        SetUnit(m, slot, hp, maxHp, gx, gy, level, brave, faith);
        if (slot <= Offsets.EnemySlotMax)
            SetArrayEnemy(m, slot, level, brave, faith, maxHp);
    }

    private static void SetRoster(FakeSparseMemory m, int slot, int level, int brave, int faith, int weapon,
                                  int lhand = 0xFFFF, int offhand = 0xFFFF, int nameId = 0)
        => MemSeats.SeatRoster(m, slot, level, brave, faith, weapon, lhand, offhand, nameId);

    private const int Wilham = Offsets.SlotsBack;       // band slot 20 (player-side actor)
    private const int Ramza = Offsets.SlotsBack + 1;    // band slot 21

    private static readonly HashSet<int> Weapons = new() { 52, 90, 63 };

    private static void Settle(KillTracker t, int n = 3) { for (int i = 0; i < n; i++) t.Poll(true); }

    /// <summary>Seed the three victim fields at band slot <paramref name="slot"/>'s entry, marked
    /// Readable (mirrors VictimProbeTests.SeedVictimFields).</summary>
    private static void SeedVictimFields(FakeSparseMemory m, int slot, ushort nameId, byte job, bool undead)
    {
        long addr = Band.Entry(slot);
        m.U16s[addr + Offsets.ANameId] = nameId;
        m.ReadableAddrs.Add(addr + Offsets.ANameId);
        m.U8s[addr + Puppeteer.JobOff] = job;
        m.ReadableAddrs.Add(addr + Puppeteer.JobOff);
        m.U8s[addr + Offsets.ADeadStatus] = undead ? Offsets.AUndeadBit : (byte)0;
        m.ReadableAddrs.Add(addr + Offsets.ADeadStatus);
    }

    private sealed class FakeDeedSink : IDeedSink
    {
        public readonly List<(int weaponId, VictimSnapshot victim)> Deeds = new();
        public readonly List<int> Misses = new();
        public void RecordDeed(int weaponId, in VictimSnapshot victim) => Deeds.Add((weaponId, victim));
        public void DeedMiss(int slot) => Misses.Add(slot);
    }

    // ---- capture ----

    [Fact]
    public void Edge_snapshot_stored_per_slot()
    {
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        SetEnemy(m, slot: 0, hp: 300);
        SeedVictimFields(m, slot: 0, nameId: 918, job: 99, undead: true);
        var t = new KillTracker(kills, m, Weapons);
        Settle(t);   // seenAlive

        SetUnit(m, slot: 0, hp: 0);
        t.Poll(true);   // dead tick 1: deadStreak == 1 -> edge capture fires here

        var snap = t._victimAtEdge[0];
        Assert.True(snap.Has);
        Assert.Equal((ushort)918, snap.NameId);
        Assert.Equal((byte)99, snap.Job);
        Assert.True(snap.Undead);
    }

    [Fact]
    public void Snapshot_consumed_exactly_once_at_credit()
    {
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        var sink = new FakeDeedSink();
        SetRoster(m, slot: 3, level: 99, brave: 89, faith: 76, weapon: 52);
        SetUnit(m, Wilham, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetActive(m, hp: 352, maxHp: 352, level: 99);
        SetEnemy(m, slot: 0, hp: 300);
        SeedVictimFields(m, slot: 0, nameId: 918, job: 77, undead: false);
        var t = new KillTracker(kills, m, Weapons, recorder: null, deeds: sink);

        Settle(t);
        SetUnit(m, slot: 0, hp: 0);
        Settle(t, 3);   // credited

        Assert.Equal(1, kills.GetValueOrDefault(52));
        Assert.Single(sink.Deeds);
        Assert.Equal(52, sink.Deeds[0].weaponId);
        Assert.Equal((ushort)918, sink.Deeds[0].victim.NameId);
        Assert.False(t._victimAtEdge[0].Has);   // consumed -- cleared at CreditKill

        // Further polls after a fully-credited/dead-credited corpse never re-consume or re-report.
        t.Poll(true); t.Poll(true);
        Assert.Single(sink.Deeds);
    }

    [Fact]
    public void Dual_credit_kill_records_deed_on_both_weapons()
    {
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        var sink = new FakeDeedSink();
        SetRoster(m, slot: 3, level: 99, brave: 89, faith: 76, weapon: 52, lhand: 90);
        SetUnit(m, Wilham, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetActive(m, hp: 352, maxHp: 352, level: 99);
        SetEnemy(m, slot: 0, hp: 300);
        SeedVictimFields(m, slot: 0, nameId: 918, job: 77, undead: false);
        var t = new KillTracker(kills, m, Weapons, recorder: null, deeds: sink);

        Settle(t);
        SetUnit(m, slot: 0, hp: 0);
        Settle(t, 3);

        Assert.Equal(1, kills.GetValueOrDefault(52));
        Assert.Equal(1, kills.GetValueOrDefault(90));
        Assert.Equal(2, sink.Deeds.Count);
        Assert.Contains(sink.Deeds, d => d.weaponId == 52 && d.victim.NameId == 918);
        Assert.Contains(sink.Deeds, d => d.weaponId == 90 && d.victim.NameId == 918);
    }

    [Fact]
    public void CreditKill_without_snapshot_records_no_deed_and_does_not_throw()
    {
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        var sink = new FakeDeedSink();
        SetRoster(m, slot: 3, level: 99, brave: 89, faith: 76, weapon: 52);
        SetUnit(m, Wilham, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetActive(m, hp: 352, maxHp: 352, level: 99);
        SetEnemy(m, slot: 0, hp: 300);   // NOTE: victim fields NOT seeded -- nameId unreadable -> no snapshot
        var t = new KillTracker(kills, m, Weapons, recorder: null, deeds: sink);

        Settle(t);
        SetUnit(m, slot: 0, hp: 0);

        bool credited = false;
        var ex = Record.Exception(() => { Settle(t, 3); credited = kills.GetValueOrDefault(52) == 1; });

        Assert.Null(ex);
        Assert.True(credited);                     // tally still increments exactly as today
        Assert.Empty(sink.Deeds);                   // no deed recorded
        Assert.Equal(new[] { 0 }, sink.Misses);      // one deed-miss for slot 0
    }

    [Fact]
    public void Null_sink_changes_nothing()
    {
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        SetRoster(m, slot: 3, level: 99, brave: 89, faith: 76, weapon: 52);
        SetUnit(m, Wilham, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetActive(m, hp: 352, maxHp: 352, level: 99);
        SetEnemy(m, slot: 0, hp: 300);
        SeedVictimFields(m, slot: 0, nameId: 918, job: 77, undead: false);
        var t = new KillTracker(kills, m, Weapons);   // deeds omitted -> null, default

        var ex = Record.Exception(() =>
        {
            Settle(t);
            SetUnit(m, slot: 0, hp: 0);
            Settle(t, 3);
        });

        Assert.Null(ex);
        Assert.Equal(1, kills.GetValueOrDefault(52));
    }

    // ---- reset paths (mirrors VictimProbeTests' four reset-path tests, but for _victimAtEdge) ----

    [Fact]
    public void Identity_change_clears_the_edge_snapshot()
    {
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        SetEnemy(m, slot: 0, hp: 300, maxHp: 400, level: 10, brave: 50, faith: 50);
        SeedVictimFields(m, slot: 0, nameId: 918, job: 99, undead: false);
        var t = new KillTracker(kills, m, Weapons);
        Settle(t, 3);   // seenAlive with identity (10,50,50)

        SetUnit(m, slot: 0, hp: 0, maxHp: 400, level: 10, brave: 50, faith: 50);
        t.Poll(true);   // dead tick 1 -> deadStreak==1 -> edge snapshot captured

        Assert.True(t._victimAtEdge[0].Has);

        // SWAP: a different unit appears ALIVE at the same slot (level changes) -- the
        // identity-change branch fires and must clear this slot's edge snapshot too.
        SetEnemy(m, slot: 0, hp: 300, maxHp: 400, level: 15, brave: 55, faith: 55);
        t.Poll(true);

        Assert.False(t._victimAtEdge[0].Has);
    }

    [Fact]
    public void Revive_clears_a_stale_edge_snapshot_left_by_an_uncredited_death()
    {
        // A death whose identity is unknown to the oracle is NEVER credited, so CreditKill's
        // consume-once never runs -- the edge snapshot would strand forever without the revive
        // clear (KillTracker.Corpses.cs's alive-branch reset, mirroring _lethalActor's own clear).
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        SetUnit(m, slot: 0, hp: 300, maxHp: 400, level: 10, brave: 50, faith: 50);   // no array entry -> unknown identity
        SeedVictimFields(m, slot: 0, nameId: 42, job: 7, undead: false);
        var t = new KillTracker(kills, m, Weapons);
        Settle(t, 3);   // seenAlive

        SetUnit(m, slot: 0, hp: 0, maxHp: 400, level: 10, brave: 50, faith: 50);
        Settle(t, 3);   // deadStreak reaches DeadNeeded, but never credited (unknown identity)

        Assert.Empty(kills);
        Assert.True(t._victimAtEdge[0].Has);   // captured at the edge, never consumed

        // Revive: seen alive again (same identity) -> the revive branch must clear it.
        SetUnit(m, slot: 0, hp: 300, maxHp: 400, level: 10, brave: 50, faith: 50);
        Settle(t, 3);

        Assert.False(t._victimAtEdge[0].Has);
    }

    [Fact]
    public void ResetBattle_clears_the_edge_snapshot()
    {
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        SetEnemy(m, slot: 0, hp: 300);
        SeedVictimFields(m, slot: 0, nameId: 918, job: 99, undead: false);
        var t = new KillTracker(kills, m, Weapons);
        Settle(t, 3);

        SetUnit(m, slot: 0, hp: 0);
        t.Poll(true);   // deadStreak==1 -> edge captured, not yet credited (DeadNeeded==3)

        Assert.True(t._victimAtEdge[0].Has);

        t.ResetBattle();

        Assert.False(t._victimAtEdge[0].Has);
    }

    [Fact]
    public void Pending_expiry_clears_the_edge_snapshot()
    {
        // Mirrors Expires_a_pending_corpse_on_the_wall_clock_backstop_with_no_edges
        // (KillTrackerTests.cs): no acted-falling edges at all -- the corpse expires on the
        // PendingTtl wall-clock backstop, and that no-credit path must clear the edge snapshot.
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        SetRoster(m, slot: 3, level: 99, brave: 89, faith: 76, weapon: 52);
        SetUnit(m, Wilham, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetActive(m, hp: 681, maxHp: 681, level: 93, acted: 0);   // an enemy active, never acts -- no latch
        var t = new KillTracker(kills, m, Weapons);

        SetEnemy(m, slot: 0, hp: 300);
        SeedVictimFields(m, slot: 0, nameId: 918, job: 99, undead: false);
        Settle(t, 3);
        SetUnit(m, slot: 0, hp: 0);
        t.Poll(true); t.Poll(true); t.Poll(true); t.Poll(true); t.Poll(true);   // past DeadNeeded, still pending

        Assert.True(t._victimAtEdge[0].Has);   // captured at the edge tick, still pending (not yet expired)

        for (int i = 0; i < KillTracker.PendingTtl + 5; i++) t.Poll(true);   // exceed the backstop

        Assert.Empty(kills);
        Assert.False(t._victimAtEdge[0].Has);
    }
}
