using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// The shared wielder locator behind the weapon-keyed signatures (Life Sap and friends):
/// resolve the SINGLE roster unit holding a weapon into its fingerprint + hand ids, then
/// find that unit's LIVE band entry with the ExtraTurn-proven twin filter (a real-position
/// match beats the frozen (0,0) roster duplicate; a surviving tie means no match -- a miss
/// beats acting on a stranger). All reads ride IGameMemory so the fake drives the tests.
/// </summary>
public class WielderTests
{
    private const int Weapon = 56;   // any catalogued id; tests use the Umbral Rod's

    private sealed class FakeMemory : IGameMemory
    {
        public readonly Dictionary<long, ushort> U16s = new();
        public readonly Dictionary<long, byte> U8s = new();
        public byte U8(long a) => U8s.TryGetValue(a, out var v) ? v : (byte)0;
        public ushort U16(long a) => U16s.TryGetValue(a, out var v) ? v : (ushort)0;
    }

    private static void SeatRoster(FakeMemory m, int slot, int lvl, int br, int fa,
                                   int rh, int lh = 0xFFFF, int oh = 0xFFFF)
    {
        long rb = Offsets.RosterBase + (long)slot * Offsets.RosterStride;
        m.U8s[rb + Offsets.RLevel] = (byte)lvl;
        m.U8s[rb + Offsets.RBrave] = (byte)br;
        m.U8s[rb + Offsets.RFaith] = (byte)fa;
        m.U16s[rb + Offsets.RRHand] = (ushort)rh;
        m.U16s[rb + Offsets.RLHand] = (ushort)lh;
        m.U16s[rb + Offsets.ROffHand] = (ushort)oh;
    }

    private static void SeatBand(FakeMemory m, int bandIdx, int weapon, int lvl, int br, int fa,
                                 int gx, int gy, int hp = 100, int maxHp = 100)
    {
        long e = Band.Entry(bandIdx);
        m.U16s[e + (Offsets.CWeapon - Offsets.BandEntry)] = (ushort)weapon;
        m.U8s[e + Offsets.ALevel] = (byte)lvl;
        m.U8s[e + Offsets.ABrave] = (byte)br;
        m.U8s[e + Offsets.AFaith] = (byte)fa;
        m.U8s[e + Offsets.AGx] = (byte)gx;
        m.U8s[e + Offsets.AGy] = (byte)gy;
        m.U16s[e + Offsets.AHp] = (ushort)hp;
        m.U16s[e + Offsets.AMaxHp] = (ushort)maxHp;
    }

    // ---- TryResolve: roster sweep for the single wielder ----

    [Fact]
    public void TryResolve_false_when_nobody_wields_the_weapon()
    {
        var m = new FakeMemory();
        SeatRoster(m, 0, lvl: 20, br: 70, fa: 50, rh: 1);   // wields something else
        Assert.False(Wielder.TryResolve(m, Weapon, out _, new List<int>()));
    }

    [Fact]
    public void TryResolve_finds_the_single_wielder_fingerprint_and_hands()
    {
        var m = new FakeMemory();
        SeatRoster(m, 2, lvl: 31, br: 65, fa: 58, rh: Weapon);
        var hands = new List<int>();
        Assert.True(Wielder.TryResolve(m, Weapon, out var fp, hands));
        Assert.Equal((31, 65, 58), fp);
        Assert.Contains(Weapon, hands);
        Assert.DoesNotContain(0xFFFF, hands);   // empty hands filtered out
    }

    [Fact]
    public void TryResolve_false_when_two_units_wield_it()
    {
        var m = new FakeMemory();
        SeatRoster(m, 0, lvl: 20, br: 70, fa: 50, rh: Weapon);
        SeatRoster(m, 1, lvl: 25, br: 60, fa: 40, rh: Weapon);
        Assert.False(Wielder.TryResolve(m, Weapon, out _, new List<int>()));
    }

    [Fact]
    public void TryResolve_skips_empty_roster_slots()
    {
        var m = new FakeMemory();
        SeatRoster(m, 0, lvl: 0, br: 70, fa: 50, rh: Weapon);    // level 0 = empty slot
        Assert.False(Wielder.TryResolve(m, Weapon, out _, new List<int>()));
    }

    [Fact]
    public void TryResolve_finds_an_offhand_wielder()
    {
        var m = new FakeMemory();
        SeatRoster(m, 3, lvl: 40, br: 80, fa: 45, rh: 1, oh: Weapon);
        var hands = new List<int>();
        Assert.True(Wielder.TryResolve(m, Weapon, out var fp, hands));
        Assert.Equal((40, 80, 45), fp);
        Assert.Contains(Weapon, hands);
        Assert.Contains(1, hands);
    }

    // ---- Locate: band walk with the twin filter ----

    [Fact]
    public void Locate_finds_the_band_entry_by_weapon_and_fingerprint()
    {
        var m = new FakeMemory();
        SeatBand(m, 5, Weapon, lvl: 31, br: 65, fa: 58, gx: 4, gy: 7);
        long e = Wielder.Locate(m, Weapon, new[] { Weapon }, (31, 65, 58));
        Assert.Equal(Band.Entry(5), e);
    }

    [Fact]
    public void Locate_prefers_the_real_position_over_the_frozen_origin_twin()
    {
        var m = new FakeMemory();
        SeatBand(m, 3, Weapon, lvl: 31, br: 65, fa: 58, gx: 0, gy: 0);   // frozen twin at (0,0)
        SeatBand(m, 9, Weapon, lvl: 31, br: 65, fa: 58, gx: 6, gy: 2);   // live copy
        long e = Wielder.Locate(m, Weapon, new[] { Weapon }, (31, 65, 58));
        Assert.Equal(Band.Entry(9), e);
    }

    [Fact]
    public void Locate_returns_zero_on_a_surviving_tie()
    {
        var m = new FakeMemory();
        SeatBand(m, 3, Weapon, lvl: 31, br: 65, fa: 58, gx: 4, gy: 4);
        SeatBand(m, 9, Weapon, lvl: 31, br: 65, fa: 58, gx: 6, gy: 2);
        Assert.Equal(0, Wielder.Locate(m, Weapon, new[] { Weapon }, (31, 65, 58)));
    }

    [Fact]
    public void Locate_rejects_a_fingerprint_mismatch()
    {
        var m = new FakeMemory();
        SeatBand(m, 5, Weapon, lvl: 31, br: 66, fa: 58, gx: 4, gy: 7);   // brave off by one
        Assert.Equal(0, Wielder.Locate(m, Weapon, new[] { Weapon }, (31, 65, 58)));
    }

    [Fact]
    public void Locate_matches_the_other_hand_when_the_weapon_rides_offhand()
    {
        // Combat struct +0x20 holds the RIGHT hand for a dual-wielder; the locator must accept
        // any of the wielder's hands, with an exact weapon match outranking a hand match.
        var m = new FakeMemory();
        SeatBand(m, 7, 1, lvl: 40, br: 80, fa: 45, gx: 3, gy: 3);   // entry shows the main hand (id 1)
        long e = Wielder.Locate(m, Weapon, new[] { 1, Weapon }, (40, 80, 45));
        Assert.Equal(Band.Entry(7), e);
    }
}
