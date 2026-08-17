using System.Collections.Generic;
using System.IO;
using System.Reflection;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// Blaster (id 76) +3 "Gun Slinger": writes a twin Blaster into the wielder's roster
/// off-hand (ROffHand +0x18, u16) and Dual Wield (support Key 477) into the roster support
/// slot (RSupport +0x0A, u16 ability Key) between battles, with snapshot+restore of the
/// originals.
///
/// Organised into:
///   Stage-1  W16 seam (FakeSparseMemory round-trip)
///   Stage-2  Policy pure decisions (GunSlinger.Policy)
///   Stage-2a Legacy support-Key migration (pure function)
///   Stage-2b Snapshot save/load round-trip
///   Stage-3  Integration through FakeSparseMemory roster scan
/// </summary>
public class GunSlingerTests
{
    // LW-193: shared defaults for the Stage-2 Policy call sites below -- a "consenting" out-of-
    // battle world: no menu open, no shield in the way, and a twinIds set containing whichever
    // single weapon id that test's `twin` argument uses (76, the Blaster, throughout Stage-2).
    private const ushort EmptyShieldForTest = 0xFFFF;
    private static readonly HashSet<int> BlasterTwinIds = new() { 76 };

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
            mainIsGS: true, twin: 76, off: 0xFFFF, shield: EmptyShieldForTest, snap: snap,
            menuOpen: false, inBattle: false, twinIds: BlasterTwinIds);
        Assert.Equal(GunSlingerOffAction.SnapshotAndWrite, action);
    }

    [Fact]
    public void Policy_OffHand_off_equals_twin_Leave()
    {
        var snap = new GunSlingerSnap();
        var action = GunSlingerPolicy.DesiredOffHand(
            mainIsGS: true, twin: 76, off: 76, shield: EmptyShieldForTest, snap: snap,
            menuOpen: false, inBattle: false, twinIds: BlasterTwinIds);
        Assert.Equal(GunSlingerOffAction.Leave, action);
    }

    // LW-193 consent model (reversed from the pre-fix behavior this test used to pin): a real
    // player item with no snap on record withholds consent outright -- it is never grabbed.
    [Fact]
    public void Policy_OffHand_realItem_noSnap_declines_Leave()
    {
        var snap = new GunSlingerSnap();
        var action = GunSlingerPolicy.DesiredOffHand(
            mainIsGS: true, twin: 76, off: 100, shield: EmptyShieldForTest, snap: snap,
            menuOpen: false, inBattle: false, twinIds: BlasterTwinIds);
        Assert.Equal(GunSlingerOffAction.Leave, action);
    }

    // Re-assert path: snap exists, off != twin -> Write (never re-snapshot)
    [Fact]
    public void Policy_OffHand_hasSnap_offNotTwin_Write()
    {
        var snap = new GunSlingerSnap { HasOff = true, OrigOff = 100 };
        var action = GunSlingerPolicy.DesiredOffHand(
            mainIsGS: true, twin: 76, off: 0xFFFF, shield: EmptyShieldForTest, snap: snap,
            menuOpen: false, inBattle: false, twinIds: BlasterTwinIds);
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
            mainIsGS: true, twin: 76, off: 0xFFFF, shield: EmptyShieldForTest, snap: snap,
            menuOpen: false, inBattle: false, twinIds: BlasterTwinIds);
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
            mainIsGS: false, twin: 76, off: 76, shield: EmptyShieldForTest, snap: snap,
            menuOpen: false, inBattle: false, twinIds: BlasterTwinIds);
        Assert.Equal(GunSlingerOffAction.Restore, action);
    }

    [Fact]
    public void Policy_OffHand_notMainIsGS_noSnap_Leave()
    {
        var snap = new GunSlingerSnap();
        var action = GunSlingerPolicy.DesiredOffHand(
            mainIsGS: false, twin: 76, off: 0xFFFF, shield: EmptyShieldForTest, snap: snap,
            menuOpen: false, inBattle: false, twinIds: BlasterTwinIds);
        Assert.Equal(GunSlingerOffAction.Leave, action);
    }

    // Validity gate: off reads 0x0000 (garbage) while mainIsGS & noSnap -> Leave
    [Fact]
    public void Policy_OffHand_garbage_zero_noSnap_Leave()
    {
        var snap = new GunSlingerSnap();
        var action = GunSlingerPolicy.DesiredOffHand(
            mainIsGS: true, twin: 76, off: 0, shield: EmptyShieldForTest, snap: snap,
            menuOpen: false, inBattle: false, twinIds: BlasterTwinIds);
        Assert.Equal(GunSlingerOffAction.Leave, action);
    }

    // Validity gate: off reads 65000 (>315) while mainIsGS & noSnap -> Leave
    [Fact]
    public void Policy_OffHand_garbage_tooLarge_noSnap_Leave()
    {
        var snap = new GunSlingerSnap();
        var action = GunSlingerPolicy.DesiredOffHand(
            mainIsGS: true, twin: 76, off: 65000, shield: EmptyShieldForTest, snap: snap,
            menuOpen: false, inBattle: false, twinIds: BlasterTwinIds);
        Assert.Equal(GunSlingerOffAction.Leave, action);
    }

    // ── Stage-2: GunSlinger.Policy -- support decisions ───────────────────────
    //
    // LW-168 v2 (owner-observed live 2026-08-12): the roster picked-support field at +0x0A is a
    // u16 ABILITY KEY, not a u8 id. An empty support slot reads u16 0; Dual Wield is Key 477
    // (live support id 221 + 256).

    private const ushort DualWieldKey = 477;

    // A low-byte residue (e.g. the v1 bug's Toadja Key 221) is snapshottable like any value; it
    // round-trips verbatim.
    [Fact]
    public void Policy_Supp_mainIsGS_lowByteResidue_noSnap_SnapshotAndWrite()
    {
        var snap = new GunSlingerSnap();
        var action = GunSlingerPolicy.DesiredSupport(
            mainIsGS: true, supp: 221, off: EmptyShieldForTest, shield: EmptyShieldForTest, snap: snap,
            menuOpen: false, inBattle: false, twinIds: BlasterTwinIds);
        Assert.Equal(GunSlingerSuppAction.SnapshotAndWrite, action);
    }

    [Fact]
    public void Policy_Supp_mainIsGS_alreadyDualWield_Leave()
    {
        var snap = new GunSlingerSnap();
        var action = GunSlingerPolicy.DesiredSupport(
            mainIsGS: true, supp: DualWieldKey, off: EmptyShieldForTest, shield: EmptyShieldForTest, snap: snap,
            menuOpen: false, inBattle: false, twinIds: BlasterTwinIds);
        Assert.Equal(GunSlingerSuppAction.Leave, action);
    }

    [Fact]
    public void Policy_Supp_mainIsGS_realSupp_noSnap_SnapshotAndWrite()
    {
        var snap = new GunSlingerSnap();
        var action = GunSlingerPolicy.DesiredSupport(
            mainIsGS: true, supp: 463, off: EmptyShieldForTest, shield: EmptyShieldForTest, snap: snap,
            menuOpen: false, inBattle: false, twinIds: BlasterTwinIds);
        Assert.Equal(GunSlingerSuppAction.SnapshotAndWrite, action);
    }

    [Fact]
    public void Policy_Supp_hasSnap_suppNotDualWield_Write()
    {
        var snap = new GunSlingerSnap { HasSupp = true, OrigSupp = 463 };
        var action = GunSlingerPolicy.DesiredSupport(
            mainIsGS: true, supp: 0, off: EmptyShieldForTest, shield: EmptyShieldForTest, snap: snap,
            menuOpen: false, inBattle: false, twinIds: BlasterTwinIds);
        Assert.Equal(GunSlingerSuppAction.Write, action);
    }

    [Fact]
    public void Policy_Supp_notMainIsGS_hasSnap_Restore()
    {
        var snap = new GunSlingerSnap { HasSupp = true, OrigSupp = 463 };
        var action = GunSlingerPolicy.DesiredSupport(
            mainIsGS: false, supp: 0, off: EmptyShieldForTest, shield: EmptyShieldForTest, snap: snap,
            menuOpen: false, inBattle: false, twinIds: BlasterTwinIds);
        Assert.Equal(GunSlingerSuppAction.Restore, action);
    }

    [Fact]
    public void Policy_Supp_notMainIsGS_noSnap_Leave()
    {
        var snap = new GunSlingerSnap();
        var action = GunSlingerPolicy.DesiredSupport(
            mainIsGS: false, supp: 0, off: EmptyShieldForTest, shield: EmptyShieldForTest, snap: snap,
            menuOpen: false, inBattle: false, twinIds: BlasterTwinIds);
        Assert.Equal(GunSlingerSuppAction.Leave, action);
    }

    // LOAD-BEARING KEYSTONE (LW-168): an EMPTY support slot reads u16 0 live (owner-diagnosed
    // 2026-08-12). The old policy rejected 0 as garbage, so a unit with no support ability
    // never received Dual Wield. If Policy returns Leave here, this test must fail.
    [Fact]
    public void Policy_Supp_mainIsGS_emptyZero_noSnap_SnapshotAndWrite()
    {
        var snap = new GunSlingerSnap();
        var action = GunSlingerPolicy.DesiredSupport(
            mainIsGS: true, supp: 0, off: EmptyShieldForTest, shield: EmptyShieldForTest, snap: snap,
            menuOpen: false, inBattle: false, twinIds: BlasterTwinIds);
        Assert.Equal(GunSlingerSuppAction.SnapshotAndWrite, action);
    }

    // ── Stage-2a: legacy support-Key migration (pure function) ───────────────
    // Legacy gunslinger.json files (release 2.3.2 and earlier) recorded only the LOW BYTE of the
    // support Key (the field was misread as u8). MigrateLegacySupp maps a legacy low byte back to
    // its real u16 Key, the phantom 255 sentinel to the true empty (0), and passes current-format
    // values through untouched.
    [Theory]
    [InlineData(207, 463)]   // legacy low byte of a real support Key maps up
    [InlineData(198, 454)]   // band edge (low)
    [InlineData(254, 510)]   // band edge (high)
    [InlineData(255, 0)]     // the old code's phantom empty sentinel maps to the true empty
    [InlineData(0, 0)]       // empty passes through
    [InlineData(463, 463)]   // current-format Key passes through untouched
    [InlineData(477, 477)]   // current-format Key (Dual Wield) passes through untouched
    public void Policy_MigrateLegacySupp(int stored, ushort expected)
    {
        Assert.Equal(expected, GunSlingerPolicy.MigrateLegacySupp(stored));
    }

    // ── Stage-2b: snapshot round-trip ─────────────────────────────────────────

    [Fact]
    public void Snapshot_SaveLoad_roundtrip()
    {
        using var temp = TempDirs.Create("gs_test_");
        string dir = temp.Dir;

        var store = new GunSlingerStore(dir);
        var snap = store.Get(nameId: 1);
        snap.HasOff = true; snap.OrigOff = 100;
        snap.HasSupp = true; snap.OrigSupp = 463;
        store.Save();

        var store2 = new GunSlingerStore(dir);
        var snap2 = store2.Get(nameId: 1);
        Assert.True(snap2.HasOff);
        Assert.Equal(100, snap2.OrigOff);
        Assert.True(snap2.HasSupp);
        Assert.Equal(463, snap2.OrigSupp);
    }

    [Fact]
    public void Snapshot_SaveLoad_bak_fallback()
    {
        using var temp = TempDirs.Create("gs_test_");
        string dir = temp.Dir;

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

    // LW-168 v2: a legacy gunslinger.json (release 2.3.2 and earlier) recorded only the LOW BYTE
    // of the support Key. Loading one must migrate it to the real u16 Key at the deserialization
    // boundary, not plant the low byte verbatim.
    [Fact]
    public void Snapshot_Load_migrates_legacy_lowByte_origSupp()
    {
        using var temp = TempDirs.Create("gs_test_");
        string dir = temp.Dir;

        var primary = Path.Combine(dir, "gunslinger.json");
        File.WriteAllText(primary, "{\"1\":{\"hasSupp\":true,\"origSupp\":207}}");

        var store = new GunSlingerStore(dir);
        var snap = store.Get(nameId: 1);
        Assert.True(snap.HasSupp);
        Assert.Equal(463, snap.OrigSupp);
    }

    // ── Stage-3: integration through roster scan ──────────────────────────────

    private const int BlasterId = 76;
    private const ushort EmptyU16 = 0xFFFF;

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
        ushort nameId, byte level, ushort rh, ushort off, ushort supp)
    {
        long b = Offsets.RosterBase + slot * Offsets.RosterStride;
        mem.U16s[b + Offsets.RNameId]   = nameId;
        mem.U8s[b + Offsets.RLevel]     = level;
        mem.U16s[b + Offsets.RRHand]    = rh;
        mem.U16s[b + Offsets.ROffHand]  = off;
        mem.U16s[b + Offsets.RSupport]  = supp;
        // Mark off-hand and support writable
        mem.MarkWritable(b + Offsets.ROffHand, 2);   // production gates the u16 twin write with n=2 (GunSlinger.cs WriteOffHand)
        mem.MarkWritable(b + Offsets.RSupport, 2);   // production gates the u16 support write with n=2 (GunSlinger.cs WriteSupport)
        // LW-193: the menu gate is unreadable-means-suppress (fail toward not writing), so every
        // test that wants the pre-existing "world map, menu closed" behavior must mark it
        // readable+closed explicitly -- a shared, idempotent one-liner here rather than a dozen
        // per-test copies. Tests that exercise the menu gate itself override this afterward.
        mem.ReadableAddrs.Add(Offsets.MenuOpenFlag);
        mem.U8s[Offsets.MenuOpenFlag] = 0;
    }

    // Integration keystone: main=Blaster/off=EMPTY/supp=EMPTY -> off==76, supp==477 (Dual Wield Key)
    [Fact]
    public void PrepRoster_equips_twin_and_dualwield_when_Blaster_main_tier3()
    {
        using var temp = TempDirs.Create("gs_test_");
        string dir = temp.Dir;

        var mem = new FakeSparseMemory();
        var kills = new Dictionary<int, int> { [BlasterId] = Tuning.ProdThresholds[2] }; // tier 3
        SeedRosterSlot(mem, slot: 0, nameId: 1, level: 30, rh: BlasterId,
                       off: EmptyU16, supp: 0);

        var gs = new GunSlinger(MakeGunMeta(), kills, dir, mem);
        gs.PrepRoster();

        long b = Offsets.RosterBase + 0 * Offsets.RosterStride;
        Assert.True(mem.WrittenU16.ContainsKey(b + Offsets.ROffHand),
            "GunSlinger must write off-hand");
        Assert.Equal((ushort)BlasterId, mem.WrittenU16[b + Offsets.ROffHand]);
        Assert.True(mem.WrittenU16.ContainsKey(b + Offsets.RSupport),
            "GunSlinger must write support");
        Assert.Equal(DualWieldKey, mem.WrittenU16[b + Offsets.RSupport]);
    }

    // LW-193 consent model (reversed from the pre-fix behavior this test used to pin, when a real
    // off-hand item got grabbed and snapshotted -- exactly the [twin-grant-inventory-desync]
    // pattern): a real item in the off-hand withholds consent. Nothing is written, nothing is
    // snapshotted, and Dual Wield is withheld right alongside it (the off-hand feeds the
    // SUPPORT consent check too).
    [Fact]
    public void PrepRoster_realOffhand_declines_grant_no_write()
    {
        using var temp = TempDirs.Create("gs_test_");
        string dir = temp.Dir;

        var mem = new FakeSparseMemory();
        var kills = new Dictionary<int, int> { [BlasterId] = Tuning.ProdThresholds[2] };
        SeedRosterSlot(mem, slot: 0, nameId: 1, level: 30, rh: BlasterId,
                       off: 100, supp: 463);

        var gs = new GunSlinger(MakeGunMeta(), kills, dir, mem);
        gs.PrepRoster();

        long b = Offsets.RosterBase + 0 * Offsets.RosterStride;
        Assert.False(mem.WrittenU16.ContainsKey(b + Offsets.ROffHand),
            "a real off-hand item must never be overwritten by the grant");
        Assert.False(mem.WrittenU16.ContainsKey(b + Offsets.RSupport),
            "Dual Wield must not be granted while the off-hand withholds consent");

        var snap = gs.StoreForTest().Get(nameId: 1);
        Assert.False(snap.HasOff);
        Assert.False(snap.HasSupp);
    }

    // Integration: unequip Blaster -> restore original off-hand (EMPTY, the only state that ever
    // invites the grant under the consent model) and support.
    [Fact]
    public void PrepRoster_restores_originals_when_Blaster_unequipped()
    {
        using var temp = TempDirs.Create("gs_test_");
        string dir = temp.Dir;

        var mem = new FakeSparseMemory();
        var kills = new Dictionary<int, int> { [BlasterId] = Tuning.ProdThresholds[2] };
        SeedRosterSlot(mem, slot: 0, nameId: 1, level: 30, rh: BlasterId,
                       off: EmptyU16, supp: 463);

        var gs = new GunSlinger(MakeGunMeta(), kills, dir, mem);
        gs.PrepRoster();  // snapshot (OrigOff = EMPTY) + write twin/Dual Wield

        // Now switch to a different main-hand weapon
        long b = Offsets.RosterBase + 0 * Offsets.RosterStride;
        mem.U16s[b + Offsets.RRHand]   = 77;         // different gun
        mem.U16s[b + Offsets.ROffHand] = BlasterId;  // twin still there
        mem.U16s[b + Offsets.RSupport] = DualWieldKey;

        mem.Written.Clear();
        mem.WrittenU16.Clear();

        gs.PrepRoster();  // should restore EMPTY and 463

        Assert.True(mem.WrittenU16.ContainsKey(b + Offsets.ROffHand),
            "must restore off-hand to original");
        Assert.Equal(EmptyU16, mem.WrittenU16[b + Offsets.ROffHand]);
        Assert.True(mem.WrittenU16.ContainsKey(b + Offsets.RSupport),
            "must restore support to original");
        Assert.Equal((ushort)463, mem.WrittenU16[b + Offsets.RSupport]);
    }

    // LW-168: a BARE unit (support slot reads u16 0, the live empty) gets Dual Wield, the
    // snapshot records the true empty, and unequipping restores 0 rather than a phantom ability.
    [Fact]
    public void PrepRoster_bare_unit_gets_dualwield_and_restore_writes_true_empty()
    {
        using var temp = TempDirs.Create("gs_test_");
        string dir = temp.Dir;

        var mem = new FakeSparseMemory();
        var kills = new Dictionary<int, int> { [BlasterId] = Tuning.ProdThresholds[2] };
        SeedRosterSlot(mem, slot: 0, nameId: 1, level: 30, rh: BlasterId,
                       off: EmptyU16, supp: 0);   // NO support ability equipped

        var gs = new GunSlinger(MakeGunMeta(), kills, dir, mem);
        gs.PrepRoster();

        long b = Offsets.RosterBase + 0 * Offsets.RosterStride;
        Assert.True(mem.WrittenU16.ContainsKey(b + Offsets.RSupport),
            "a bare unit must receive Dual Wield (the live empty slot reads 0)");
        Assert.Equal(DualWieldKey, mem.WrittenU16[b + Offsets.RSupport]);

        var snap = gs.StoreForTest().Get(nameId: 1);
        Assert.True(snap.HasSupp);
        Assert.Equal((ushort)0, snap.OrigSupp);   // the TRUE empty is what was snapshotted

        // Unequip the pistol: restore must write the true empty (0) back, not a phantom Key.
        mem.U16s[b + Offsets.RRHand] = 77;
        mem.Written.Clear();
        mem.WrittenU16.Clear();

        gs.PrepRoster();

        Assert.True(mem.WrittenU16.ContainsKey(b + Offsets.RSupport),
            "must restore the support slot on unequip");
        Assert.Equal((ushort)0, mem.WrittenU16[b + Offsets.RSupport]);
    }

    // ── In-battle RE-ASSERT-ONLY guard (2026-07-04: "twin pistol only works out of battle") ──

    // In battle, a slot the game clobbered back to EMPTY gets the twin RE-WRITTEN (the actual fix).
    [Fact]
    public void PrepRoster_inBattle_reasserts_a_clobbered_twin()
    {
        using var temp = TempDirs.Create("gs_test_");
        string dir = temp.Dir;

        var mem = new FakeSparseMemory();
        var kills = new Dictionary<int, int> { [BlasterId] = Tuning.ProdThresholds[2] };
        SeedRosterSlot(mem, slot: 0, nameId: 1, level: 30, rh: BlasterId,
                       off: EmptyU16, supp: 0);
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

    // LOAD-BEARING SAFETY: in battle, a fresh GunSlinger main-hand with NO snapshot must NOT
    // snapshot/write -- a mid-battle roster read that flickered could otherwise persist garbage as
    // the player's "original gear". Fails without the in-battle SnapshotAndWrite suppression.
    [Fact]
    public void PrepRoster_inBattle_never_snapshots_fresh()
    {
        using var temp = TempDirs.Create("gs_test_");
        string dir = temp.Dir;

        var mem = new FakeSparseMemory();
        var kills = new Dictionary<int, int> { [BlasterId] = Tuning.ProdThresholds[2] };
        SeedRosterSlot(mem, slot: 0, nameId: 1, level: 30, rh: BlasterId,
                       off: 100, supp: 463);   // a REAL off-hand, no prior snapshot
        var gs = new GunSlinger(MakeGunMeta(), kills, dir, mem);

        gs.PrepRoster(inBattle: true);   // in battle: must leave everything alone

        long b = Offsets.RosterBase + 0 * Offsets.RosterStride;
        Assert.False(mem.WrittenU16.ContainsKey(b + Offsets.ROffHand),
            "in battle with no snapshot, the off-hand must be left untouched");
        var snap = gs.StoreForTest().Get(nameId: 1);
        Assert.False(snap.HasOff);   // NOTHING captured to the persistent store
    }

    // LW-193 blocker-1 resolution (reversed from the pre-fix "never restores in battle" blanket
    // guard this test used to pin): with the shield slot empty and the off-hand still holding
    // OUR own twin, a Restore is now PERMITTED in battle -- the previous blanket suppression is
    // replaced by the consent table's rule 2b, which cares only about consent (shield/OrigOff),
    // never about whether a battle is live. See
    // GunSlingerConsentTests.PrepRoster_inBattle_shieldOccupied_killsReassert_formationFix
    // (GunSlingerConsentTests.cs) for the case that DOES still block (a shield in the way) --
    // it pins the RE-ASSERT lane (rule 3), not this test's Restore lane (rule 2b).
    [Fact]
    public void PrepRoster_inBattle_restore_permitted_when_shield_free()
    {
        using var temp = TempDirs.Create("gs_test_");
        string dir = temp.Dir;

        var mem = new FakeSparseMemory();
        var kills = new Dictionary<int, int> { [BlasterId] = Tuning.ProdThresholds[2] };
        SeedRosterSlot(mem, slot: 0, nameId: 1, level: 30, rh: BlasterId,
                       off: EmptyU16, supp: 0);
        var gs = new GunSlinger(MakeGunMeta(), kills, dir, mem);
        gs.PrepRoster();   // out of battle: snapshot established (OrigOff = EMPTY)

        long b = Offsets.RosterBase + 0 * Offsets.RosterStride;
        mem.U16s[b + Offsets.RRHand] = 77;   // switched off the Blaster; the twin (76) still sits in the off-hand
        mem.WrittenU16.Clear();

        gs.PrepRoster(inBattle: true);   // in battle, shield empty: restore fires

        Assert.True(mem.WrittenU16.ContainsKey(b + Offsets.ROffHand),
            "in battle with the shield slot empty, a Restore is now permitted (LW-193 blocker 1)");
        Assert.Equal(EmptyU16, mem.WrittenU16[b + Offsets.ROffHand]);
        Assert.False(gs.StoreForTest().Get(nameId: 1).HasOff);   // snapshot cleared
    }

    // Tier guard: below tier 3 -> no writes even if Blaster is equipped
    [Fact]
    public void PrepRoster_no_writes_when_tier_below_3()
    {
        using var temp = TempDirs.Create("gs_test_");
        string dir = temp.Dir;

        var mem = new FakeSparseMemory();
        var kills = new Dictionary<int, int> { [BlasterId] = 0 };  // tier 0
        SeedRosterSlot(mem, slot: 0, nameId: 1, level: 30, rh: BlasterId,
                       off: EmptyU16, supp: 0);

        var gs = new GunSlinger(MakeGunMeta(), kills, dir, mem);
        gs.PrepRoster();

        long b = Offsets.RosterBase + 0 * Offsets.RosterStride;
        Assert.False(mem.WrittenU16.ContainsKey(b + Offsets.ROffHand),
            "must not write off-hand below tier 3");
        Assert.False(mem.WrittenU16.ContainsKey(b + Offsets.RSupport),
            "must not write support below tier 3");
    }

    // ── LW-171 "Crossfire": multiple flagged gun-slinger weapons, each wielder's own twin ────
    //
    // Outrider Pistol (id 71) "Gun Slinger" and Arbalest (id 79) "Crossfire" are both flagged
    // gunSlinger in meta.json. The runtime must resolve EVERY flagged id, not just one, and each
    // wielder must be twinned with THEIR OWN main-hand weapon, never another unit's.

    private const int OutriderPistolId = 71;
    private const int ArbalestId = 79;

    private static Dictionary<int, WeaponMeta> MakeTwoGunMeta() => new()
    {
        [OutriderPistolId] = new WeaponMeta
        {
            Name = "Outrider Pistol", Wp = 7, Cat = "Gun", Formula = 3,
            Flavor = "wheel-lock",
            Signature = new WeaponSignature { AtTier = 3, GunSlinger = true, DisplayLabel = "Gun Slinger" }
        },
        [ArbalestId] = new WeaponMeta
        {
            Name = "Arbalest", Wp = 7, Cat = "Crossbow", Formula = 1,
            Flavor = "steel arbalest",
            Signature = new WeaponSignature { AtTier = 3, GunSlinger = true, DisplayLabel = "Crossfire" }
        }
    };

    // LOAD-BEARING (LW-171): two DIFFERENT flagged weapons, two DIFFERENT wielders. Under the
    // single-_twinId code only whichever id ResolveTwinId resolved first ever equips a twin --
    // the other wielder's off-hand/support are silently left alone. Confirm RED before
    // implementing (report which assertion failed).
    [Fact]
    public void PrepRoster_two_flagged_weapons_each_unit_gets_own_twin()
    {
        using var temp = TempDirs.Create("gs_test_");
        string dir = temp.Dir;

        var mem = new FakeSparseMemory();
        var kills = new Dictionary<int, int>
        {
            [OutriderPistolId] = Tuning.ProdThresholds[2],
            [ArbalestId] = Tuning.ProdThresholds[2],
        };
        SeedRosterSlot(mem, slot: 0, nameId: 1, level: 30, rh: OutriderPistolId, off: EmptyU16, supp: 0);
        SeedRosterSlot(mem, slot: 1, nameId: 2, level: 30, rh: ArbalestId, off: EmptyU16, supp: 0);

        var gs = new GunSlinger(MakeTwoGunMeta(), kills, dir, mem);
        gs.PrepRoster();

        long bA = Offsets.RosterBase + 0 * Offsets.RosterStride;
        long bB = Offsets.RosterBase + 1 * Offsets.RosterStride;

        Assert.True(mem.WrittenU16.ContainsKey(bA + Offsets.ROffHand),
            "unit A (Outrider Pistol) must get its own twin");
        Assert.Equal((ushort)OutriderPistolId, mem.WrittenU16[bA + Offsets.ROffHand]);
        Assert.True(mem.WrittenU16.ContainsKey(bA + Offsets.RSupport));
        Assert.Equal(DualWieldKey, mem.WrittenU16[bA + Offsets.RSupport]);

        Assert.True(mem.WrittenU16.ContainsKey(bB + Offsets.ROffHand),
            "unit B (Arbalest) must get its own twin, not unit A's pistol nor nothing");
        Assert.Equal((ushort)ArbalestId, mem.WrittenU16[bB + Offsets.ROffHand]);
        Assert.True(mem.WrittenU16.ContainsKey(bB + Offsets.RSupport));
        Assert.Equal(DualWieldKey, mem.WrittenU16[bB + Offsets.RSupport]);
    }

    // Swap: a unit already dual-wielding the Outrider Pistol re-equips the Arbalest -- the
    // off-hand twin must switch too (the Write re-assert branch uses the WIELDER's own main-hand
    // id, not a single module-global twin), and the stored OrigOff must not be disturbed.
    [Fact]
    public void PrepRoster_swap_between_flagged_weapons_writes_new_twin()
    {
        using var temp = TempDirs.Create("gs_test_");
        string dir = temp.Dir;

        var mem = new FakeSparseMemory();
        var kills = new Dictionary<int, int>
        {
            [OutriderPistolId] = Tuning.ProdThresholds[2],
            [ArbalestId] = Tuning.ProdThresholds[2],
        };
        // Main-hand already Arbalest at tier 3; off-hand still holds the OLD twin (Outrider
        // Pistol) left over from before the swap.
        SeedRosterSlot(mem, slot: 0, nameId: 1, level: 30, rh: ArbalestId,
                       off: (ushort)OutriderPistolId, supp: DualWieldKey);

        var gs = new GunSlinger(MakeTwoGunMeta(), kills, dir, mem);
        var snap = gs.StoreForTest().Get(nameId: 1);
        snap.HasOff = true;
        snap.OrigOff = 100;   // the real gear from before Gun Slinger ever engaged

        gs.PrepRoster();

        long b = Offsets.RosterBase + 0 * Offsets.RosterStride;
        Assert.True(mem.WrittenU16.ContainsKey(b + Offsets.ROffHand));
        Assert.Equal((ushort)ArbalestId, mem.WrittenU16[b + Offsets.ROffHand]);
        Assert.Equal(100, gs.StoreForTest().Get(nameId: 1).OrigOff);   // untouched by the re-assert
    }

    // LW-193 consent model (reversed from the pre-fix behavior this test used to pin -- its old
    // name called the real item "shield off-hand", but it was a plain real weapon id, id 111,
    // grabbed exactly the way [twin-grant-inventory-desync] describes): a real item in the
    // off-hand withholds consent outright. Nothing is written, nothing is snapshotted.
    [Fact]
    public void PrepRoster_realItem_offhand_declines_grant_no_write()
    {
        using var temp = TempDirs.Create("gs_test_");
        string dir = temp.Dir;

        var mem = new FakeSparseMemory();
        var kills = new Dictionary<int, int>
        {
            [OutriderPistolId] = Tuning.ProdThresholds[2],
            [ArbalestId] = Tuning.ProdThresholds[2],
        };
        SeedRosterSlot(mem, slot: 0, nameId: 1, level: 30, rh: ArbalestId, off: 111, supp: 0);

        var gs = new GunSlinger(MakeTwoGunMeta(), kills, dir, mem);
        gs.PrepRoster();

        long b = Offsets.RosterBase + 0 * Offsets.RosterStride;
        Assert.False(mem.WrittenU16.ContainsKey(b + Offsets.ROffHand),
            "a real off-hand item must never be overwritten by the grant");
        var snap = gs.StoreForTest().Get(nameId: 1);
        Assert.False(snap.HasOff);
    }

    // Ownership discriminator at the integration level (rule 4 vs rule 5, mirrors the Policy-level
    // GunSlingerConsentTests.OwnershipDiscriminator_twinId_withoutHasOff_neverTouched): the OTHER
    // flagged weapon sitting in the off-hand with NO grant on record is a player-owned dual-wield
    // build, not our own conjured copy -- never touched. Renamed from
    // PrepRoster_flagged_weapon_in_offhand_is_snapshotted_and_overwritten, whose premise (grabbing
    // it) this deliberately reverses.
    [Fact]
    public void PrepRoster_flagged_weapon_in_offhand_without_grant_is_never_touched()
    {
        using var temp = TempDirs.Create("gs_test_");
        string dir = temp.Dir;

        var mem = new FakeSparseMemory();
        var kills = new Dictionary<int, int>
        {
            [OutriderPistolId] = Tuning.ProdThresholds[2],
            [ArbalestId] = Tuning.ProdThresholds[2],
        };
        SeedRosterSlot(mem, slot: 0, nameId: 1, level: 30, rh: ArbalestId,
                       off: (ushort)OutriderPistolId, supp: 0);

        var gs = new GunSlinger(MakeTwoGunMeta(), kills, dir, mem);
        gs.PrepRoster();

        long b = Offsets.RosterBase + 0 * Offsets.RosterStride;
        Assert.False(mem.WrittenU16.ContainsKey(b + Offsets.ROffHand),
            "a player-owned dual-wield build (the other flagged weapon, no grant on record) must never be touched");
        var snap = gs.StoreForTest().Get(nameId: 1);
        Assert.False(snap.HasOff);
    }

    // ── LW-252: an unseeded roster row (nameId 0) must never touch the snapshot store ─────────
    //
    // GunSlingerStore.Get CREATES on read and keys snapshots by nameId (GunSlinger.Store.cs's
    // own class doc). Two unseeded rows (nameId 0 -- a Mem fail-safe transient, or a genuinely
    // stale read) therefore share ONE snapshot: whichever row's SnapshotAndWrite runs first
    // captures ITS gear into the shared object, and any OTHER unseeded row that later reads
    // snap.HasOff==true takes the Write/Restore branch instead of its own SnapshotAndWrite --
    // its own real gear is never recorded, and a later restore hands back the FIRST row's
    // originals into the SECOND row's save-persistent roster slot. Never observed live (real
    // saves' nameIds are all seeded, per this session's probe), but the guard is cheap and the
    // write is unacceptable.

    private static Dictionary<int, GunSlingerSnap> Snaps(GunSlingerStore store) =>
        (Dictionary<int, GunSlingerSnap>)typeof(GunSlingerStore)
            .GetField("_snaps", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(store)!;

    [Fact]
    public void PrepRoster_never_shares_a_key0_snapshot_between_two_unseeded_wielders()
    {
        // [LW-252 G1, THE red] Two unseeded (nameId 0) roster rows, both wielding the SAME
        // GunSlinger-flagged weapon with their own DISTINCT real off-hand items. Pre-fix, both
        // rows key into the SAME shared key-0 snapshot: row A's SnapshotAndWrite captures A's
        // own original (100) into it; row B then sees snap.HasOff already true (the SAME shared
        // object) and takes the Write branch instead of its own SnapshotAndWrite -- its real
        // off-hand (200) is overwritten and never recorded anywhere. Fixed: `nameId == 0` skips
        // BOTH rows outright, unconditionally (no lookahead/collision detection needed) --
        // _store.Get(0) is never called, so neither row is touched and no key-0 entry exists.
        using var temp = TempDirs.Create("gs_test_");
        string dir = temp.Dir;

        var mem = new FakeSparseMemory();
        var kills = new Dictionary<int, int> { [BlasterId] = Tuning.ProdThresholds[2] };   // tier 3
        SeedRosterSlot(mem, slot: 0, nameId: 0, level: 30, rh: BlasterId, off: 100, supp: 0);
        SeedRosterSlot(mem, slot: 1, nameId: 0, level: 30, rh: BlasterId, off: 200, supp: 0);

        var gs = new GunSlinger(MakeGunMeta(), kills, dir, mem);
        gs.PrepRoster();

        long bA = Offsets.RosterBase + 0 * Offsets.RosterStride;
        long bB = Offsets.RosterBase + 1 * Offsets.RosterStride;
        Assert.False(mem.WrittenU16.ContainsKey(bA + Offsets.ROffHand),
            "an unseeded row must never be touched, even alone");
        Assert.False(mem.WrittenU16.ContainsKey(bB + Offsets.ROffHand),
            "an unseeded row must never be touched, even sharing key 0 with another");
        Assert.False(mem.WrittenU16.ContainsKey(bA + Offsets.RSupport));
        Assert.False(mem.WrittenU16.ContainsKey(bB + Offsets.RSupport));

        Assert.False(Snaps(gs.StoreForTest()).ContainsKey(0), "Get(0) must never be called");
    }

    // [LW-252 G3, parity] Checked first per spec: PrepRoster_snapshots_and_overwrites_real_offhand
    // and PrepRoster_restores_originals_when_Blaster_unequipped (Stage-3 section above) already
    // drive a properly-seeded row (nameId: 1) through the full snapshot+restore round trip end
    // to end, and both are in the MUST-NOT-TOUCH set below (green before and after, unmodified)
    // -- that coverage already IS this guard's parity pin (nameId > 0 must never trip the new
    // `continue`), so no duplicate test is added here.

    [Fact]
    public void Ctor_purges_a_pre_existing_key0_snapshot_warns_once_and_persists_the_purge()
    {
        // [LW-252 G2] A key-0 snapshot already on disk (from before this fix shipped, or any
        // other stale-nameId hazard) is unrecoverable-by-construction -- it cannot be safely
        // attributed to either unit that shared it. The ctor purges it, warns exactly ONCE
        // (naming the hazard), and forces a Save() immediately so the warning fires once EVER,
        // not once per launch. An unrelated seeded entry (key 1) must survive untouched.
        using var temp = TempDirs.Create("gs_test_");
        string dir = temp.Dir;
        var primary = Path.Combine(dir, "gunslinger.json");
        File.WriteAllText(primary, "{\"0\":{\"hasOff\":true,\"origOff\":100},\"1\":{\"hasOff\":true,\"origOff\":200}}");

        using var cap = LogCapture.Start();
        var store = new GunSlingerStore(dir);

        Assert.Single(cap.File);
        Assert.Contains("WARN", cap.File[0]);

        var snaps = Snaps(store);
        Assert.False(snaps.ContainsKey(0));
        Assert.True(snaps.ContainsKey(1));   // the unrelated seeded entry survives untouched

        // Save was invoked: the purge is already on disk (a fresh reload would not re-warn).
        string onDisk = File.ReadAllText(primary);
        Assert.DoesNotContain("\"0\":", onDisk);
        Assert.Contains("\"1\":", onDisk);
    }
}
