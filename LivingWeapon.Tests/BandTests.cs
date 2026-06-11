using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// Band.EnemyFingerprints / Band.AllyFingerprints: the ONE static-array fingerprint sweep
/// behind CharmLock/EagleEye/Maim/Ricochet (enemy side) and Wyrmblood (ally side).
/// Slot split: 0..EnemySlotMax = enemies, EnemySlotMax+1..NSlots-1 = players.
/// Filter contract (every live-proven scan shipped with it): mhp 1..2000 INCLUSIVE
/// (deliberately different from Band.IsValid's exclusive band-entry bound -- documented
/// drift, not to be aligned without a live probe), level 1..99, brave/faith capped at 100
/// (no lower bound -- the static array is the oracle, not the phantom filter the band needs).
/// </summary>
public class BandTests
{
    /// <summary>Static-array fake: seeded U8/U16 reads, everything Readable (the array is
    /// always mapped in the fake -- the filter, not the guard, is under test here).</summary>
    private sealed class ArrayFake : IGameMemory
    {
        public readonly Dictionary<long, byte> U8s = new();
        public readonly Dictionary<long, ushort> U16s = new();
        public byte U8(long a) => U8s.TryGetValue(a, out var v) ? v : (byte)0;
        public ushort U16(long a) => U16s.TryGetValue(a, out var v) ? v : (ushort)0;
        public bool Readable(long a, int n) => true;
    }

    private static void Seat(ArrayFake m, int slot, int mhp, int lvl, int br, int fa)
    {
        long b = Offsets.ArrayReadBase + (long)slot * Offsets.ArrayStride;
        m.U16s[b + Offsets.AMaxHp] = (ushort)mhp;
        m.U8s[b + Offsets.ALevel] = (byte)lvl;
        m.U8s[b + Offsets.ABrave] = (byte)br;
        m.U8s[b + Offsets.AFaith] = (byte)fa;
    }

    [Fact]
    public void EnemyFingerprints_collects_only_the_enemy_slots()
    {
        var m = new ArrayFake();
        Seat(m, 0, mhp: 100, lvl: 10, br: 60, fa: 50);                       // enemy
        Seat(m, Offsets.EnemySlotMax, mhp: 200, lvl: 20, br: 70, fa: 40);    // last enemy slot
        Seat(m, Offsets.EnemySlotMax + 1, mhp: 300, lvl: 30, br: 80, fa: 30); // first PLAYER slot
        var set = Band.EnemyFingerprints(m);
        Assert.Equal(2, set.Count);
        Assert.Contains((100, 10, 60, 50), set);
        Assert.Contains((200, 20, 70, 40), set);
        Assert.DoesNotContain((300, 30, 80, 30), set);
    }

    [Fact]
    public void AllyFingerprints_collects_only_the_player_slots()
    {
        var m = new ArrayFake();
        Seat(m, Offsets.EnemySlotMax, mhp: 100, lvl: 10, br: 60, fa: 50);     // enemy
        Seat(m, Offsets.EnemySlotMax + 1, mhp: 300, lvl: 30, br: 80, fa: 30); // first player slot
        Seat(m, Offsets.NSlots - 1, mhp: 400, lvl: 40, br: 90, fa: 20);       // last player slot
        var set = Band.AllyFingerprints(m);
        Assert.Equal(2, set.Count);
        Assert.Contains((300, 30, 80, 30), set);
        Assert.Contains((400, 40, 90, 20), set);
        Assert.DoesNotContain((100, 10, 60, 50), set);
    }

    [Fact]
    public void Fingerprint_sweep_accepts_mhp_2000_inclusive()
    {
        // The documented drift: 2000 passes HERE (every live scan's bound) while
        // Band.IsValid rejects a band entry at mhp >= 2000. Do not "fix" either side.
        var m = new ArrayFake();
        Seat(m, 0, mhp: 2000, lvl: 50, br: 70, fa: 70);
        Seat(m, 1, mhp: 2001, lvl: 50, br: 70, fa: 70);   // above the bound -> phantom
        var set = Band.EnemyFingerprints(m);
        Assert.Contains((2000, 50, 70, 70), set);
        Assert.DoesNotContain((2001, 50, 70, 70), set);
        Assert.Single(set);
    }

    [Theory]
    [InlineData(0, 50, 70, 70)]      // mhp 0: empty slot
    [InlineData(100, 0, 70, 70)]     // level 0: phantom
    [InlineData(100, 100, 70, 70)]   // level above 99: garbage
    [InlineData(100, 50, 101, 70)]   // brave above 100: garbage
    [InlineData(100, 50, 70, 101)]   // faith above 100: garbage
    public void Fingerprint_sweep_rejects_phantom_slots(int mhp, int lvl, int br, int fa)
    {
        var m = new ArrayFake();
        Seat(m, 0, mhp, lvl, br, fa);
        Assert.Empty(Band.EnemyFingerprints(m));
    }

    [Fact]
    public void Fingerprint_sweep_accepts_brave_and_faith_at_exactly_100()
    {
        // Praise/Steel reach 100; the cap is inclusive (same rationale as Band.IsValid).
        var m = new ArrayFake();
        Seat(m, 0, mhp: 100, lvl: 50, br: 100, fa: 100);
        Assert.Contains((100, 50, 100, 100), Band.EnemyFingerprints(m));
    }

    [Fact]
    public void Fingerprint_sweep_dedups_identical_units()
    {
        // Two slots carrying the same fingerprint collapse to one entry (the HashSet is
        // content-identical to the old List+Contains dedup the per-module copies used).
        var m = new ArrayFake();
        Seat(m, 0, mhp: 150, lvl: 25, br: 65, fa: 55);
        Seat(m, 1, mhp: 150, lvl: 25, br: 65, fa: 55);
        Assert.Single(Band.EnemyFingerprints(m));
    }

    [Fact]
    public void Fingerprint_sweep_skips_unreadable_slots()
    {
        // FakeSparseMemory's default Readable is false: the guard gate stays in the sweep.
        var m = new FakeSparseMemory();
        long b = Offsets.ArrayReadBase;
        m.U16s[b + Offsets.AMaxHp] = 100;
        m.U8s[b + Offsets.ALevel] = 10;
        m.U8s[b + Offsets.ABrave] = 60;
        m.U8s[b + Offsets.AFaith] = 50;
        Assert.Empty(Band.EnemyFingerprints(m));
    }
}
