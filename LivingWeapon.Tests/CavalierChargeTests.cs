using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// Eight-Fluted Pole +3 "Cavalier's Charge": Speed +3 while mounted on a chocobo.
/// Exercises HoldTimedStat's mount-gated path -- combat +0x1B4 bit 0x80 (proven live
/// 2026-06-26) replaces the forTurns gate. The `turns` argument is ignored when Mounted.
/// Break check for verifier: forcing the mount read to 0 must make the keystone fail.
/// </summary>
public class CavalierChargeTests
{
    private static readonly WeaponSignature MountedSig = new()
    {
        AtTier = 3, Mounted = true, Stat = "Speed", StatBonus = 3
    };

    private static GrowthEngine MakeEngine(FakeSparseMemory mem)
        => new GrowthEngine(
               new Dictionary<int, WeaponMeta>(),
               new Dictionary<int, int>(),
               new TurnTracker(mem),
               mem);

    // ---- KEYSTONE: mount bit 0x80 set -> Speed becomes natural+3 ----

    [Fact]
    public void MountedSig_boosts_speed_when_mount_bit_set()
    {
        var mem = new FakeSparseMemory();
        long s = 0x1000_0000;
        mem.U8s[s + Offsets.CSpeed] = 8;
        mem.U8s[s + Offsets.CMount] = Offsets.CMountRidingBit;   // 0x80 = riding
        mem.WritableAddrs.Add(s + Offsets.CSpeed);

        var engine = MakeEngine(mem);
        engine.HoldTimedStat(s, MountedSig, tier: 3, turns: 99);  // turns is irrelevant for mounted sig

        Assert.Equal(11, mem.Written[s + Offsets.CSpeed]);         // 8 + 3
    }

    // ---- not mounted -> no boost ----

    [Fact]
    public void MountedSig_no_boost_when_not_mounted()
    {
        var mem = new FakeSparseMemory();
        long s = 0x1000_0000;
        mem.U8s[s + Offsets.CSpeed] = 8;
        mem.U8s[s + Offsets.CMount] = 0x00;   // not riding
        mem.WritableAddrs.Add(s + Offsets.CSpeed);

        var engine = MakeEngine(mem);
        engine.HoldTimedStat(s, MountedSig, tier: 3, turns: 0);

        Assert.False(mem.Written.ContainsKey(s + Offsets.CSpeed));
    }

    // ---- mount set then cleared -> boost then revert ----

    [Fact]
    public void MountedSig_reverts_speed_on_dismount()
    {
        var mem = new FakeSparseMemory();
        long s = 0x1000_0000;
        mem.U8s[s + Offsets.CSpeed] = 8;
        mem.U8s[s + Offsets.CMount] = Offsets.CMountRidingBit;
        mem.WritableAddrs.Add(s + Offsets.CSpeed);

        var engine = MakeEngine(mem);
        engine.HoldTimedStat(s, MountedSig, tier: 3, turns: 0);   // first call: mounted -> boost to 11
        Assert.Equal(11, mem.U8s[s + Offsets.CSpeed]);

        mem.U8s[s + Offsets.CMount] = 0x00;                        // dismount
        engine.HoldTimedStat(s, MountedSig, tier: 3, turns: 0);   // second call: not mounted -> revert to 8
        Assert.Equal(8, mem.U8s[s + Offsets.CSpeed]);
    }

    // ---- tier < AtTier -> no boost even while mounted ----

    [Fact]
    public void MountedSig_no_boost_when_tier_below_atTier()
    {
        var mem = new FakeSparseMemory();
        long s = 0x1000_0000;
        mem.U8s[s + Offsets.CSpeed] = 8;
        mem.U8s[s + Offsets.CMount] = Offsets.CMountRidingBit;
        mem.WritableAddrs.Add(s + Offsets.CSpeed);

        var engine = MakeEngine(mem);
        engine.HoldTimedStat(s, MountedSig, tier: 2, turns: 0);   // tier 2 < AtTier 3

        Assert.False(mem.Written.ContainsKey(s + Offsets.CSpeed));
    }
}
