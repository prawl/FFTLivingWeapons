using System.Collections.Generic;
using System.IO;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// Blaster (id 76) +3 "Gun Slinger": writes a twin Blaster into the wielder's roster
/// off-hand (ROffHand +0x18, u16) and Dual Wield (support 221) into the roster support
/// slot (RSupport +0x0A, u8) between battles, with snapshot+restore of the originals.
///
/// Organised into:
///   Stage-1  W16 seam (FakeSparseMemory round-trip)
///   Stage-2  Policy pure decisions (GunSlinger.Policy)
///   Stage-2b Snapshot save/load round-trip
///   Stage-3  Integration through FakeSparseMemory roster scan
/// </summary>
public class GunSlingerTests
{
    // ── Stage-1: W16 seam ──────────────────────────────────────────────────────

    [Fact]
    public void FakeSparseMemory_W16_roundtrip_via_U16()
    {
        var mem = new FakeSparseMemory();
        mem.W16(0x1000L, 0x004C);
        Assert.Equal(0x004C, mem.U16(0x1000L));
    }

    [Fact]
    public void FakeSparseMemory_W16_records_in_WrittenU16()
    {
        var mem = new FakeSparseMemory();
        mem.W16(0x2000L, 0xABCD);
        Assert.True(mem.WrittenU16.ContainsKey(0x2000L), "W16 must record the write in WrittenU16");
        Assert.Equal(0xABCD, mem.WrittenU16[0x2000L]);
    }

    [Fact]
    public void FakeSparseMemory_W16_does_not_clobber_adjacent_U16()
    {
        var mem = new FakeSparseMemory();
        mem.U16s[0x3002L] = 0x1111;
        mem.W16(0x3000L, 0x0076);
        Assert.Equal(0x1111, mem.U16(0x3002L));  // neighbor untouched
    }

    // ── Stage-2: GunSlinger.Policy -- off-hand decisions ──────────────────────

    // LOAD-BEARING KEYSTONE: mainIsGS=true, off=EMPTY(0xFFFF), no snap -> SnapshotAndWrite.
    // If Policy always returns Leave, this test must fail.
    [Fact]
    public void Policy_OffHand_mainIsGS_emptyOff_noSnap_SnapshotAndWrite()
    {
        var snap = new GunSlingerSnap();
        var action = GunSlingerPolicy.DesiredOffHand(
            mainIsGS: true, twin: 76, off: 0xFFFF, snap: snap);
        Assert.Equal(GunSlingerOffAction.SnapshotAndWrite, action);
    }

    [Fact]
    public void Policy_OffHand_off_equals_twin_Leave()
    {
        var snap = new GunSlingerSnap();
        var action = GunSlingerPolicy.DesiredOffHand(
            mainIsGS: true, twin: 76, off: 76, snap: snap);
        Assert.Equal(GunSlingerOffAction.Leave, action);
    }

    [Fact]
    public void Policy_OffHand_realItem_noSnap_SnapshotAndWrite()
    {
        var snap = new GunSlingerSnap();
        var action = GunSlingerPolicy.DesiredOffHand(
            mainIsGS: true, twin: 76, off: 100, snap: snap);
        Assert.Equal(GunSlingerOffAction.SnapshotAndWrite, action);
    }

    // Re-assert path: snap exists, off != twin -> Write (never re-snapshot)
    [Fact]
    public void Policy_OffHand_hasSnap_offNotTwin_Write()
    {
        var snap = new GunSlingerSnap { HasOff = true, OrigOff = 100 };
        var action = GunSlingerPolicy.DesiredOffHand(
            mainIsGS: true, twin: 76, off: 0xFFFF, snap: snap);
        Assert.Equal(GunSlingerOffAction.Write, action);
    }

    // Re-assert does not overwrite existing snapshot
    [Fact]
    public void Policy_OffHand_reAssert_snapOrigUnchanged()
    {
        // Off is currently EMPTY (snap was taken when off was 100); engine changed it -> re-assert
        var snap = new GunSlingerSnap { HasOff = true, OrigOff = 100 };
        // Decision should be Write, not SnapshotAndWrite
        var action = GunSlingerPolicy.DesiredOffHand(
            mainIsGS: true, twin: 76, off: 0xFFFF, snap: snap);
        Assert.Equal(GunSlingerOffAction.Write, action);
        // The snap object itself must not have been touched by the policy
        Assert.Equal(100, snap.OrigOff);
        Assert.True(snap.HasOff);
    }

    [Fact]
    public void Policy_OffHand_notMainIsGS_hasSnap_Restore()
    {
        var snap = new GunSlingerSnap { HasOff = true, OrigOff = 100 };
        var action = GunSlingerPolicy.DesiredOffHand(
            mainIsGS: false, twin: 76, off: 76, snap: snap);
        Assert.Equal(GunSlingerOffAction.Restore, action);
    }

    [Fact]
    public void Policy_OffHand_notMainIsGS_noSnap_Leave()
    {
        var snap = new GunSlingerSnap();
        var action = GunSlingerPolicy.DesiredOffHand(
            mainIsGS: false, twin: 76, off: 0xFFFF, snap: snap);
        Assert.Equal(GunSlingerOffAction.Leave, action);
    }

    // Validity gate: off reads 0x0000 (garbage) while mainIsGS & noSnap -> Leave
    [Fact]
    public void Policy_OffHand_garbage_zero_noSnap_Leave()
    {
        var snap = new GunSlingerSnap();
        var action = GunSlingerPolicy.DesiredOffHand(
            mainIsGS: true, twin: 76, off: 0, snap: snap);
        Assert.Equal(GunSlingerOffAction.Leave, action);
    }

    // Validity gate: off reads 65000 (>315) while mainIsGS & noSnap -> Leave
    [Fact]
    public void Policy_OffHand_garbage_tooLarge_noSnap_Leave()
    {
        var snap = new GunSlingerSnap();
        var action = GunSlingerPolicy.DesiredOffHand(
            mainIsGS: true, twin: 76, off: 65000, snap: snap);
        Assert.Equal(GunSlingerOffAction.Leave, action);
    }

    // ── Stage-2: GunSlinger.Policy -- support decisions ───────────────────────

    private const byte DualWieldId = 221;
    private const byte EmptySuppSentinel = 0xFF;

    [Fact]
    public void Policy_Supp_mainIsGS_empty_noSnap_SnapshotAndWrite()
    {
        var snap = new GunSlingerSnap();
        var action = GunSlingerPolicy.DesiredSupport(
            mainIsGS: true, supp: EmptySuppSentinel, snap: snap);
        Assert.Equal(GunSlingerSuppAction.SnapshotAndWrite, action);
    }

    [Fact]
    public void Policy_Supp_mainIsGS_alreadyDualWield_Leave()
    {
        var snap = new GunSlingerSnap();
        var action = GunSlingerPolicy.DesiredSupport(
            mainIsGS: true, supp: DualWieldId, snap: snap);
        Assert.Equal(GunSlingerSuppAction.Leave, action);
    }

    [Fact]
    public void Policy_Supp_mainIsGS_realSupp_noSnap_SnapshotAndWrite()
    {
        var snap = new GunSlingerSnap();
        var action = GunSlingerPolicy.DesiredSupport(
            mainIsGS: true, supp: 213, snap: snap);
        Assert.Equal(GunSlingerSuppAction.SnapshotAndWrite, action);
    }

    [Fact]
    public void Policy_Supp_hasSnap_suppNotDualWield_Write()
    {
        var snap = new GunSlingerSnap { HasSupp = true, OrigSupp = 213 };
        var action = GunSlingerPolicy.DesiredSupport(
            mainIsGS: true, supp: EmptySuppSentinel, snap: snap);
        Assert.Equal(GunSlingerSuppAction.Write, action);
    }

    [Fact]
    public void Policy_Supp_notMainIsGS_hasSnap_Restore()
    {
        var snap = new GunSlingerSnap { HasSupp = true, OrigSupp = 213 };
        var action = GunSlingerPolicy.DesiredSupport(
            mainIsGS: false, supp: DualWieldId, snap: snap);
        Assert.Equal(GunSlingerSuppAction.Restore, action);
    }

    [Fact]
    public void Policy_Supp_notMainIsGS_noSnap_Leave()
    {
        var snap = new GunSlingerSnap();
        var action = GunSlingerPolicy.DesiredSupport(
            mainIsGS: false, supp: EmptySuppSentinel, snap: snap);
        Assert.Equal(GunSlingerSuppAction.Leave, action);
    }

    // Validity gate for support: refuse 0 (garbage)
    [Fact]
    public void Policy_Supp_garbage_zero_noSnap_Leave()
    {
        var snap = new GunSlingerSnap();
        var action = GunSlingerPolicy.DesiredSupport(
            mainIsGS: true, supp: 0, snap: snap);
        Assert.Equal(GunSlingerSuppAction.Leave, action);
    }

    // ── Stage-2b: snapshot round-trip ─────────────────────────────────────────

    [Fact]
    public void Snapshot_SaveLoad_roundtrip()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gs_test_" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new GunSlingerStore(dir);
            var snap = store.Get(nameId: 1);
            snap.HasOff = true; snap.OrigOff = 100;
            snap.HasSupp = true; snap.OrigSupp = 213;
            store.Save();

            var store2 = new GunSlingerStore(dir);
            var snap2 = store2.Get(nameId: 1);
            Assert.True(snap2.HasOff);
            Assert.Equal(100, snap2.OrigOff);
            Assert.True(snap2.HasSupp);
            Assert.Equal(213, snap2.OrigSupp);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Snapshot_SaveLoad_bak_fallback()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gs_test_" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new GunSlingerStore(dir);
            var snap = store.Get(nameId: 2);
            snap.HasOff = true; snap.OrigOff = 77;
            store.Save();

            // Corrupt the primary; .bak should contain the good data
            var primary = Path.Combine(dir, "gunslinger.json");
            File.WriteAllText(primary, "CORRUPT{{{");

            var store2 = new GunSlingerStore(dir);
            var snap2 = store2.Get(nameId: 2);
            Assert.True(snap2.HasOff);
            Assert.Equal(77, snap2.OrigOff);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // ── Stage-3: integration through roster scan ──────────────────────────────

    private const int BlasterId = 76;
    private const ushort EmptyU16 = 0xFFFF;
    private const byte EmptyU8 = 0xFF;

    private static Dictionary<int, WeaponMeta> MakeGunMeta() => new()
    {
        [BlasterId] = new WeaponMeta
        {
            Name = "Blaster", Wp = 15, Cat = "Gun", Formula = 4,
            Flavor = "magickal firearm",
            Signature = new WeaponSignature { AtTier = 3, GunSlinger = true, DisplayLabel = "Gun Slinger" }
        }
    };

    // Seed a roster slot in FakeSparseMemory
    private static void SeedRosterSlot(FakeSparseMemory mem, int slot,
        ushort nameId, byte level, ushort rh, ushort off, byte supp)
    {
        long b = Offsets.RosterBase + slot * Offsets.RosterStride;
        mem.U16s[b + Offsets.RNameId]   = nameId;
        mem.U8s[b + Offsets.RLevel]     = level;
        mem.U16s[b + Offsets.RRHand]    = rh;
        mem.U16s[b + Offsets.ROffHand]  = off;
        mem.U8s[b + Offsets.RSupport]   = supp;
        // Mark off-hand and support writable
        mem.WritableAddrs.Add(b + Offsets.ROffHand);
        mem.WritableAddrs.Add(b + Offsets.RSupport);
    }

    // Integration keystone: main=Blaster/off=EMPTY/supp=EMPTY -> off==76, supp==221
    [Fact]
    public void PrepRoster_equips_twin_and_dualwield_when_Blaster_main_tier3()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gs_test_" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var mem = new FakeSparseMemory();
            var kills = new Dictionary<int, int> { [BlasterId] = Tuning.ProdThresholds[2] }; // tier 3
            SeedRosterSlot(mem, slot: 0, nameId: 1, level: 30, rh: BlasterId,
                           off: EmptyU16, supp: EmptyU8);

            var gs = new GunSlinger(MakeGunMeta(), kills, dir, mem);
            gs.PrepRoster();

            long b = Offsets.RosterBase + 0 * Offsets.RosterStride;
            Assert.True(mem.WrittenU16.ContainsKey(b + Offsets.ROffHand),
                "GunSlinger must write off-hand");
            Assert.Equal((ushort)BlasterId, mem.WrittenU16[b + Offsets.ROffHand]);
            Assert.True(mem.Written.ContainsKey(b + Offsets.RSupport),
                "GunSlinger must write support");
            Assert.Equal((byte)DualWieldId, mem.Written[b + Offsets.RSupport]);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // Integration: real item in off-hand gets snapshotted and overwritten
    [Fact]
    public void PrepRoster_snapshots_and_overwrites_real_offhand()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gs_test_" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var mem = new FakeSparseMemory();
            var kills = new Dictionary<int, int> { [BlasterId] = Tuning.ProdThresholds[2] };
            SeedRosterSlot(mem, slot: 0, nameId: 1, level: 30, rh: BlasterId,
                           off: 100, supp: 213);

            var gs = new GunSlinger(MakeGunMeta(), kills, dir, mem);
            gs.PrepRoster();

            long b = Offsets.RosterBase + 0 * Offsets.RosterStride;
            Assert.Equal((ushort)BlasterId, mem.WrittenU16[b + Offsets.ROffHand]);
            Assert.Equal((byte)DualWieldId, mem.Written[b + Offsets.RSupport]);

            // snapshot should have originals
            var store = gs.StoreForTest();
            var snap = store.Get(nameId: 1);
            Assert.True(snap.HasOff);
            Assert.Equal(100, snap.OrigOff);
            Assert.True(snap.HasSupp);
            Assert.Equal(213, snap.OrigSupp);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // Integration: unequip Blaster -> restore original off-hand and support
    [Fact]
    public void PrepRoster_restores_originals_when_Blaster_unequipped()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gs_test_" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var mem = new FakeSparseMemory();
            var kills = new Dictionary<int, int> { [BlasterId] = Tuning.ProdThresholds[2] };
            SeedRosterSlot(mem, slot: 0, nameId: 1, level: 30, rh: BlasterId,
                           off: 100, supp: 213);

            var gs = new GunSlinger(MakeGunMeta(), kills, dir, mem);
            gs.PrepRoster();  // snapshot + write twin/221

            // Now switch to a different main-hand weapon
            long b = Offsets.RosterBase + 0 * Offsets.RosterStride;
            mem.U16s[b + Offsets.RRHand]   = 77;         // different gun
            mem.U16s[b + Offsets.ROffHand] = BlasterId;  // twin still there
            mem.U8s[b + Offsets.RSupport]  = DualWieldId;

            mem.Written.Clear();
            mem.WrittenU16.Clear();

            gs.PrepRoster();  // should restore 100 and 213

            Assert.True(mem.WrittenU16.ContainsKey(b + Offsets.ROffHand),
                "must restore off-hand to original");
            Assert.Equal((ushort)100, mem.WrittenU16[b + Offsets.ROffHand]);
            Assert.True(mem.Written.ContainsKey(b + Offsets.RSupport),
                "must restore support to original");
            Assert.Equal((byte)213, mem.Written[b + Offsets.RSupport]);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // ── In-battle RE-ASSERT-ONLY guard (2026-07-04: "twin pistol only works out of battle") ──

    // In battle, a slot the game clobbered back to EMPTY gets the twin RE-WRITTEN (the actual fix).
    [Fact]
    public void PrepRoster_inBattle_reasserts_a_clobbered_twin()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gs_test_" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var mem = new FakeSparseMemory();
            var kills = new Dictionary<int, int> { [BlasterId] = Tuning.ProdThresholds[2] };
            SeedRosterSlot(mem, slot: 0, nameId: 1, level: 30, rh: BlasterId,
                           off: EmptyU16, supp: EmptyU8);
            var gs = new GunSlinger(MakeGunMeta(), kills, dir, mem);
            gs.PrepRoster();   // out of battle: snapshot + write twin (snap.HasOff now true)

            long b = Offsets.RosterBase + 0 * Offsets.RosterStride;
            mem.U16s[b + Offsets.ROffHand] = EmptyU16;   // the game clobbered the off-hand back to empty
            mem.WrittenU16.Clear();

            gs.PrepRoster(inBattle: true);   // in battle: re-assert only

            Assert.True(mem.WrittenU16.ContainsKey(b + Offsets.ROffHand),
                "in battle, a clobbered twin must be re-written");
            Assert.Equal((ushort)BlasterId, mem.WrittenU16[b + Offsets.ROffHand]);
        }
        finally { Directory.Delete(dir, true); }
    }

    // LOAD-BEARING SAFETY: in battle, a fresh GunSlinger main-hand with NO snapshot must NOT
    // snapshot/write -- a mid-battle roster read that flickered could otherwise persist garbage as
    // the player's "original gear". Fails without the in-battle SnapshotAndWrite suppression.
    [Fact]
    public void PrepRoster_inBattle_never_snapshots_fresh()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gs_test_" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var mem = new FakeSparseMemory();
            var kills = new Dictionary<int, int> { [BlasterId] = Tuning.ProdThresholds[2] };
            SeedRosterSlot(mem, slot: 0, nameId: 1, level: 30, rh: BlasterId,
                           off: 100, supp: 213);   // a REAL off-hand, no prior snapshot
            var gs = new GunSlinger(MakeGunMeta(), kills, dir, mem);

            gs.PrepRoster(inBattle: true);   // in battle: must leave everything alone

            long b = Offsets.RosterBase + 0 * Offsets.RosterStride;
            Assert.False(mem.WrittenU16.ContainsKey(b + Offsets.ROffHand),
                "in battle with no snapshot, the off-hand must be left untouched");
            var snap = gs.StoreForTest().Get(nameId: 1);
            Assert.False(snap.HasOff);   // NOTHING captured to the persistent store
        }
        finally { Directory.Delete(dir, true); }
    }

    // In battle, an unequipped GunSlinger must NOT restore (that touches the store + the real gear).
    [Fact]
    public void PrepRoster_inBattle_never_restores()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gs_test_" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var mem = new FakeSparseMemory();
            var kills = new Dictionary<int, int> { [BlasterId] = Tuning.ProdThresholds[2] };
            SeedRosterSlot(mem, slot: 0, nameId: 1, level: 30, rh: BlasterId,
                           off: 100, supp: 213);
            var gs = new GunSlinger(MakeGunMeta(), kills, dir, mem);
            gs.PrepRoster();   // out of battle: snapshot established

            long b = Offsets.RosterBase + 0 * Offsets.RosterStride;
            mem.U16s[b + Offsets.RRHand] = 77;   // switched off the Blaster
            mem.WrittenU16.Clear();

            gs.PrepRoster(inBattle: true);   // in battle: restore is suppressed

            Assert.False(mem.WrittenU16.ContainsKey(b + Offsets.ROffHand),
                "in battle, a Restore must be suppressed (the off-hand is left as-is)");
            Assert.True(gs.StoreForTest().Get(nameId: 1).HasOff);   // snapshot NOT cleared
        }
        finally { Directory.Delete(dir, true); }
    }

    // Tier guard: below tier 3 -> no writes even if Blaster is equipped
    [Fact]
    public void PrepRoster_no_writes_when_tier_below_3()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gs_test_" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var mem = new FakeSparseMemory();
            var kills = new Dictionary<int, int> { [BlasterId] = 0 };  // tier 0
            SeedRosterSlot(mem, slot: 0, nameId: 1, level: 30, rh: BlasterId,
                           off: EmptyU16, supp: EmptyU8);

            var gs = new GunSlinger(MakeGunMeta(), kills, dir, mem);
            gs.PrepRoster();

            long b = Offsets.RosterBase + 0 * Offsets.RosterStride;
            Assert.False(mem.WrittenU16.ContainsKey(b + Offsets.ROffHand),
                "must not write off-hand below tier 3");
            Assert.False(mem.Written.ContainsKey(b + Offsets.RSupport),
                "must not write support below tier 3");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
