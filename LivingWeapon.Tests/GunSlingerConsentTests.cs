using System.Collections.Generic;
using System.IO;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-193/LW-194: the twin-weapon grant's consent rewrite. An EMPTY off-hand invites the twin;
/// anything the player equipped there or in the shield slot (RShield +0x1A, READ ONLY -- this
/// mod never writes it) declines it, in every lane including the in-battle re-assert. See
/// docs/LIVE_LEDGER.md's [twin-grant-inventory-desync], [worldmap-menu-open-byte], and
/// [twin-dualfire-construction-bound] rows for the evidence this rewrite rests on.
///
/// Organised by the plan's TDD test list (lw193 plan v2), one region per numbered item.
/// </summary>
public class GunSlingerConsentTests
{
    private const int BlasterId = 76;
    private const int OutriderPistolId = 71;
    private const int ArbalestId = 79;
    private const ushort EmptyU16 = 0xFFFF;
    private const ushort DualWieldKeyForTest = 477;

    private static readonly HashSet<int> BlasterTwinIds = new() { BlasterId };
    private static readonly HashSet<int> TwoGunTwinIds = new() { OutriderPistolId, ArbalestId };

    // ── Test 1: menu suppression table-driven ─────────────────────────────────────────────
    // menuOpen && !inBattle -> Leave for every state that would otherwise grant, re-assert,
    // restore, or stand down. Sabotage: drop the menuOpen check from either table -> any one of
    // these goes red (the un-suppressed row's real action leaks through).

    [Fact]
    public void MenuOpen_suppresses_offhand_freshGrant()
    {
        var snap = new GunSlingerSnap();
        var action = GunSlingerPolicy.DesiredOffHand(
            mainIsGS: true, twin: BlasterId, off: EmptyU16, shield: EmptyU16, snap: snap,
            menuOpen: true, inBattle: false, twinIds: BlasterTwinIds);
        Assert.Equal(GunSlingerOffAction.Leave, action);
    }

    [Fact]
    public void MenuOpen_suppresses_offhand_reAssert()
    {
        var snap = new GunSlingerSnap { HasOff = true, OrigOff = EmptyU16 };
        var action = GunSlingerPolicy.DesiredOffHand(
            mainIsGS: true, twin: BlasterId, off: EmptyU16, shield: EmptyU16, snap: snap,
            menuOpen: true, inBattle: false, twinIds: BlasterTwinIds);
        Assert.Equal(GunSlingerOffAction.Leave, action);
    }

    [Fact]
    public void MenuOpen_suppresses_offhand_restore()
    {
        var snap = new GunSlingerSnap { HasOff = true, OrigOff = 100 };
        var action = GunSlingerPolicy.DesiredOffHand(
            mainIsGS: false, twin: BlasterId, off: (ushort)BlasterId, shield: EmptyU16, snap: snap,
            menuOpen: true, inBattle: false, twinIds: BlasterTwinIds);
        Assert.Equal(GunSlingerOffAction.Leave, action);
    }

    [Fact]
    public void MenuOpen_suppresses_offhand_standDown()
    {
        // A prior grant is held (HasOff) but the player has since filled the hand themselves.
        var snap = new GunSlingerSnap { HasOff = true, OrigOff = EmptyU16 };
        var action = GunSlingerPolicy.DesiredOffHand(
            mainIsGS: true, twin: BlasterId, off: 200, shield: EmptyU16, snap: snap,
            menuOpen: true, inBattle: false, twinIds: BlasterTwinIds);
        Assert.Equal(GunSlingerOffAction.Leave, action);
    }

    [Fact]
    public void MenuOpen_suppresses_offhand_shieldConsentWithheld()
    {
        var snap = new GunSlingerSnap { HasOff = true, OrigOff = EmptyU16 };
        var action = GunSlingerPolicy.DesiredOffHand(
            mainIsGS: true, twin: BlasterId, off: (ushort)BlasterId, shield: 128, snap: snap,
            menuOpen: true, inBattle: false, twinIds: BlasterTwinIds);
        Assert.Equal(GunSlingerOffAction.Leave, action);
    }

    [Fact]
    public void MenuOpen_suppresses_support_freshGrant()
    {
        var snap = new GunSlingerSnap();
        var action = GunSlingerPolicy.DesiredSupport(
            mainIsGS: true, supp: 0, off: EmptyU16, shield: EmptyU16, snap: snap,
            menuOpen: true, inBattle: false, twinIds: BlasterTwinIds);
        Assert.Equal(GunSlingerSuppAction.Leave, action);
    }

    [Fact]
    public void MenuOpen_suppresses_support_restore()
    {
        var snap = new GunSlingerSnap { HasSupp = true, OrigSupp = 463 };
        var action = GunSlingerPolicy.DesiredSupport(
            mainIsGS: false, supp: 0, off: EmptyU16, shield: EmptyU16, snap: snap,
            menuOpen: true, inBattle: false, twinIds: BlasterTwinIds);
        Assert.Equal(GunSlingerSuppAction.Leave, action);
    }

    // ── Test 3: consent by filled hand (rule 5) ───────────────────────────────────────────
    // A real player item sits in the off-hand while a grant is held: the mod stands down
    // without touching the player's item. Once the hand is emptied again, a fresh grant fires
    // (rule 6b) -- the standdown didn't wall off the wielder forever, just while consent is
    // withheld.

    [Fact]
    public void FilledHand_withGrantHeld_StandDownNoWrite()
    {
        var snap = new GunSlingerSnap { HasOff = true, OrigOff = EmptyU16 };
        var action = GunSlingerPolicy.DesiredOffHand(
            mainIsGS: true, twin: BlasterId, off: 200, shield: EmptyU16, snap: snap,
            menuOpen: false, inBattle: false, twinIds: BlasterTwinIds);
        Assert.Equal(GunSlingerOffAction.StandDownNoWrite, action);
    }

    [Fact]
    public void FilledHand_handEmptiedLater_freshGrant()
    {
        // The caller would have cleared HasOff after the StandDownNoWrite above; simulate that
        // post-state directly and confirm a later empty hand still grants normally.
        var snap = new GunSlingerSnap();
        var action = GunSlingerPolicy.DesiredOffHand(
            mainIsGS: true, twin: BlasterId, off: EmptyU16, shield: EmptyU16, snap: snap,
            menuOpen: false, inBattle: false, twinIds: BlasterTwinIds);
        Assert.Equal(GunSlingerOffAction.SnapshotAndWrite, action);
    }

    // ── Test 4: ownership discriminator (rule 4 vs rule 5) ────────────────────────────────
    // off holding a flagged id is OURS only paired with HasOff; without it, it's a player's own
    // dual-wield build and must never be touched.

    [Fact]
    public void OwnershipDiscriminator_otherTwinId_withHasOff_reStampsCurrentTwin()
    {
        // Main hand swapped 71 -> 79 (Outrider Pistol -> Arbalest); the off-hand still carries
        // OUR stale conjured Outrider Pistol from before the swap.
        var snap = new GunSlingerSnap { HasOff = true, OrigOff = 100 };
        var action = GunSlingerPolicy.DesiredOffHand(
            mainIsGS: true, twin: ArbalestId, off: (ushort)OutriderPistolId, shield: EmptyU16, snap: snap,
            menuOpen: false, inBattle: false, twinIds: TwoGunTwinIds);
        Assert.Equal(GunSlingerOffAction.Write, action);
    }

    [Fact]
    public void OwnershipDiscriminator_twinId_withoutHasOff_neverTouched()
    {
        // off happens to equal the twin id, but no grant is on record for it -- a coincidental
        // player-owned dual-wield build, not our own conjured copy. Never touched.
        var snap = new GunSlingerSnap();
        var action = GunSlingerPolicy.DesiredOffHand(
            mainIsGS: true, twin: BlasterId, off: (ushort)BlasterId, shield: EmptyU16, snap: snap,
            menuOpen: false, inBattle: false, twinIds: BlasterTwinIds);
        Assert.Equal(GunSlingerOffAction.Leave, action);
    }

    // ── Shared integration helpers ────────────────────────────────────────────────────────

    private static Dictionary<int, WeaponMeta> MakeGunMeta() => new()
    {
        [BlasterId] = new WeaponMeta
        {
            Name = "Blaster", Wp = 15, Cat = "Gun", Formula = 4, Flavor = "magickal firearm",
            Signature = new WeaponSignature { AtTier = 3, GunSlinger = true, DisplayLabel = "Gun Slinger" }
        }
    };

    private static Dictionary<int, WeaponMeta> MakeTwoGunMeta() => new()
    {
        [OutriderPistolId] = new WeaponMeta
        {
            Name = "Outrider Pistol", Wp = 7, Cat = "Gun", Formula = 3, Flavor = "wheel-lock",
            Signature = new WeaponSignature { AtTier = 3, GunSlinger = true, DisplayLabel = "Gun Slinger" }
        },
        [ArbalestId] = new WeaponMeta
        {
            Name = "Arbalest", Wp = 7, Cat = "Crossbow", Formula = 1, Flavor = "steel arbalest",
            Signature = new WeaponSignature { AtTier = 3, GunSlinger = true, DisplayLabel = "Crossfire" }
        }
    };

    /// <summary>Seeds a roster row AND marks the menu byte readable+closed (the "world map, menu
    /// closed" default every test but the menu-gate tests themselves wants) plus the shield slot
    /// (default EMPTY -- no shield in the way).</summary>
    private static void SeedRosterSlot(FakeSparseMemory mem, int slot,
        ushort nameId, byte level, ushort rh, ushort off, ushort supp, ushort shield = 0xFFFF)
    {
        long b = Offsets.RosterBase + slot * Offsets.RosterStride;
        mem.U16s[b + Offsets.RNameId]  = nameId;
        mem.U8s[b + Offsets.RLevel]    = level;
        mem.U16s[b + Offsets.RRHand]   = rh;
        mem.U16s[b + Offsets.ROffHand] = off;
        mem.U16s[b + Offsets.RSupport] = supp;
        mem.U16s[b + Offsets.RShield]  = shield;
        mem.MarkWritable(b + Offsets.ROffHand, 2);
        mem.MarkWritable(b + Offsets.RSupport, 2);
        mem.ReadableAddrs.Add(Offsets.MenuOpenFlag);
        mem.U8s[Offsets.MenuOpenFlag] = 0;
    }

    private static void SetMenu(FakeSparseMemory mem, byte value)
    {
        mem.ReadableAddrs.Add(Offsets.MenuOpenFlag);
        mem.U8s[Offsets.MenuOpenFlag] = value;
    }

    // ── Test 2: THE LOAD-BEARING TEST -- the tape's actual destruction lane, through the
    // re-assert. Sabotage: revert rule 3 in the policy -> this test goes red (the fake records
    // the forbidden Write). ──────────────────────────────────────────────────────────────────

    [Fact]
    public void LoadBearing_ReAssertNeverRestampsOverAnOccupiedShield()
    {
        using var temp = TempDirs.Create("gs_test_");
        string dir = temp.Dir;

        var mem = new FakeSparseMemory();
        var kills = new Dictionary<int, int> { [BlasterId] = Tuning.ProdThresholds[2] };
        SeedRosterSlot(mem, slot: 0, nameId: 1, level: 30, rh: BlasterId, off: EmptyU16, supp: 0);

        var gs = new GunSlinger(MakeGunMeta(), kills, dir, mem);
        gs.PrepRoster();   // grant: off-hand = BlasterId, HasOff = true, OrigOff = EMPTY
        long b = Offsets.RosterBase + 0 * Offsets.RosterStride;
        Assert.True(gs.StoreForTest().Get(1).HasOff);

        // The tape's actual sequence (lw193_watch_20260817_055131.log): the game clears the
        // twin, then the player equips a real shield.
        mem.U16s[b + Offsets.ROffHand] = EmptyU16;
        mem.U16s[b + Offsets.RShield] = 128;

        // Menu opens.
        SetMenu(mem, 1);
        mem.WrittenU16.Clear();
        gs.PrepRoster();
        Assert.False(mem.WrittenU16.ContainsKey(b + Offsets.ROffHand), "zero ROffHand writes while a menu is open");
        Assert.False(mem.WrittenU16.ContainsKey(b + Offsets.RShield), "the shield byte must never be written by the mod");

        // Menu closes; let the debounce settle (3 stable passes) before inspecting the real
        // decision the table makes once nothing is suppressing it anymore.
        SetMenu(mem, 0);
        gs.PrepRoster(); gs.PrepRoster(); gs.PrepRoster();
        mem.WrittenU16.Clear();
        gs.PrepRoster();

        Assert.False(mem.WrittenU16.ContainsKey(b + Offsets.ROffHand),
            "no twin re-stamp while the shield sits there (rule 3)");
        Assert.False(mem.WrittenU16.ContainsKey(b + Offsets.RShield), "the shield byte must never be written by the mod");
        Assert.False(gs.StoreForTest().Get(1).HasOff);   // the snap resolves per rule 3: stood down
    }

    // ── Test 5: evaporation REMOVED, owner-AC round -- inverted from the pre-round evaporation
    // tests. The twin now stays visibly stamped through every menu (owner AC item 3: it must be
    // visible in E&A); the duplication defense moved to the watch-only reconcile
    // (GunSlingerReconcileTests.cs). Sabotage: reintroduce an evaporating write on the menuOpen
    // false->true edge -> MenuOpen_withTwinHeld_zeroOffHandWrites_twinRemainsVisible goes red. ──

    [Fact]
    public void MenuOpen_withTwinHeld_zeroOffHandWrites_twinRemainsVisible()
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

        var gs = new GunSlinger(MakeTwoGunMeta(), kills, dir, mem);
        gs.PrepRoster();   // grant

        long b = Offsets.RosterBase + 0 * Offsets.RosterStride;
        Assert.Equal((ushort)OutriderPistolId, mem.U16s[b + Offsets.ROffHand]);

        // Menu opens: the twin holds. NOTHING touches the off-hand -- no evaporation, no write
        // of any kind, and the twin stays visibly stamped in memory the whole time.
        SetMenu(mem, 1);
        mem.WrittenU16.Clear();
        gs.PrepRoster();
        Assert.False(mem.WrittenU16.ContainsKey(b + Offsets.ROffHand), "the menu-open edge must write NOTHING -- evaporation is gone");
        Assert.Equal((ushort)OutriderPistolId, mem.U16s[b + Offsets.ROffHand]);

        // Menu stays open several more passes: still zero writes, twin still visible.
        mem.WrittenU16.Clear();
        gs.PrepRoster();
        gs.PrepRoster();
        Assert.False(mem.WrittenU16.ContainsKey(b + Offsets.ROffHand));
        Assert.Equal((ushort)OutriderPistolId, mem.U16s[b + Offsets.ROffHand]);
        Assert.True(gs.StoreForTest().Get(1).HasOff);
    }

    // The OTHER half of the old evaporation test's job: if the GAME clears the off-hand while a
    // menu is open (the destructive shape evaporation used to preempt), the mod must not
    // re-stamp until a browse screen (see GunSlingerReconcileTests.cs) or the menu closes.
    [Fact]
    public void MenuOpen_gameClearsOffHandMidMenu_noReassertUntilClose()
    {
        using var temp = TempDirs.Create("gs_test_");
        string dir = temp.Dir;

        var mem = new FakeSparseMemory();
        var kills = new Dictionary<int, int> { [BlasterId] = Tuning.ProdThresholds[2] };
        SeedRosterSlot(mem, slot: 0, nameId: 1, level: 30, rh: BlasterId, off: EmptyU16, supp: 0);

        var gs = new GunSlinger(MakeGunMeta(), kills, dir, mem);
        gs.PrepRoster();   // grant

        long b = Offsets.RosterBase + 0 * Offsets.RosterStride;
        SetMenu(mem, 1);
        gs.PrepRoster();   // menu opens; nothing touches the off-hand (established above)

        // The game clears the off-hand while the menu is still open.
        mem.U16s[b + Offsets.ROffHand] = EmptyU16;
        mem.WrittenU16.Clear();
        gs.PrepRoster();
        Assert.False(mem.WrittenU16.ContainsKey(b + Offsets.ROffHand), "no re-stamp while the menu stays open (no browse screen)");

        // Menu closes; once the debounce settles, the re-assert fires normally.
        SetMenu(mem, 0);
        gs.PrepRoster(); gs.PrepRoster(); gs.PrepRoster();
        mem.WrittenU16.Clear();
        gs.PrepRoster();
        Assert.True(mem.WrittenU16.ContainsKey(b + Offsets.ROffHand), "the re-assert fires once the menu closes");
        Assert.Equal((ushort)BlasterId, mem.WrittenU16[b + Offsets.ROffHand]);
    }

    // ── Test 6: debounce and fail-safe ────────────────────────────────────────────────────

    [Fact]
    public void Debounce_onePassFlicker_suppressesThreeMorePasses()
    {
        using var temp = TempDirs.Create("gs_test_");
        string dir = temp.Dir;

        var mem = new FakeSparseMemory();
        var kills = new Dictionary<int, int> { [BlasterId] = Tuning.ProdThresholds[2] };
        // A fresh unit, EMPTY off-hand, no prior grant -- the write under test is the initial
        // SnapshotAndWrite, isolated from anything else the debounce/gate machinery might touch.
        SeedRosterSlot(mem, slot: 0, nameId: 1, level: 30, rh: BlasterId, off: EmptyU16, supp: 0);

        var gs = new GunSlinger(MakeGunMeta(), kills, dir, mem);
        long b = Offsets.RosterBase + 0 * Offsets.RosterStride;

        // A single-pass flicker on the very first (cold-start) read: the byte reads 1 for exactly
        // one PrepRoster call, then back to 0.
        SetMenu(mem, 1);
        gs.PrepRoster();   // cold start trusts raw directly: menuOpen = true, no grant yet
        Assert.False(mem.WrittenU16.ContainsKey(b + Offsets.ROffHand));

        SetMenu(mem, 0);
        gs.PrepRoster();   // a REAL flip restarts the 3-pass window, even though the flicker cleared
        Assert.False(mem.WrittenU16.ContainsKey(b + Offsets.ROffHand), "a single-pass flicker must still suppress for the full debounce window");
        gs.PrepRoster();
        Assert.False(mem.WrittenU16.ContainsKey(b + Offsets.ROffHand));
        gs.PrepRoster();
        Assert.False(mem.WrittenU16.ContainsKey(b + Offsets.ROffHand));

        mem.WrittenU16.Clear();
        gs.PrepRoster();   // the 3-pass window has now elapsed -- the held-back grant finally lands
        Assert.True(mem.WrittenU16.ContainsKey(b + Offsets.ROffHand), "once the debounce window elapses, the write finally lands");
    }

    [Fact]
    public void FailSafe_unreadableMenuByte_suppressesWrites()
    {
        using var temp = TempDirs.Create("gs_test_");
        string dir = temp.Dir;

        var mem = new FakeSparseMemory();
        var kills = new Dictionary<int, int> { [BlasterId] = Tuning.ProdThresholds[2] };
        long b = Offsets.RosterBase + 0 * Offsets.RosterStride;
        mem.U16s[b + Offsets.RNameId] = 1;
        mem.U8s[b + Offsets.RLevel] = 30;
        mem.U16s[b + Offsets.RRHand] = BlasterId;
        mem.U16s[b + Offsets.ROffHand] = EmptyU16;
        mem.U16s[b + Offsets.RSupport] = 0;
        mem.MarkWritable(b + Offsets.ROffHand, 2);
        mem.MarkWritable(b + Offsets.RSupport, 2);
        // Deliberately do NOT mark Offsets.MenuOpenFlag readable -- unreadable must suppress.

        var gs = new GunSlinger(MakeGunMeta(), kills, dir, mem);
        gs.PrepRoster();

        Assert.False(mem.WrittenU16.ContainsKey(b + Offsets.ROffHand),
            "an unreadable menu byte must fail toward suppression, never toward writing");
        Assert.False(mem.WrittenU16.ContainsKey(b + Offsets.RSupport));
    }

    // ── Test 7: legacy migration split ────────────────────────────────────────────────────

    [Fact]
    public void LegacyMigration_realOrigOff_shieldFree_RestoresVerbatim()
    {
        var snap = new GunSlingerSnap { HasOff = true, OrigOff = 150 };   // legacy real gear
        var action = GunSlingerPolicy.DesiredOffHand(
            mainIsGS: false, twin: BlasterId, off: (ushort)BlasterId, shield: EmptyU16, snap: snap,
            menuOpen: false, inBattle: false, twinIds: BlasterTwinIds);
        Assert.Equal(GunSlingerOffAction.Restore, action);
    }

    [Fact]
    public void LegacyMigration_realOrigOff_shieldOccupied_WriteEmptyAndDrop()
    {
        var snap = new GunSlingerSnap { HasOff = true, OrigOff = 150 };
        var action = GunSlingerPolicy.DesiredOffHand(
            mainIsGS: false, twin: BlasterId, off: (ushort)BlasterId, shield: 128, snap: snap,
            menuOpen: false, inBattle: false, twinIds: BlasterTwinIds);
        Assert.Equal(GunSlingerOffAction.WriteEmptyAndDrop, action);
    }

    [Fact]
    public void LegacyMigration_shieldOccupied_WarnNamesDroppedItemId()
    {
        using var temp = TempDirs.Create("gs_test_");
        string dir = temp.Dir;

        var mem = new FakeSparseMemory();
        var kills = new Dictionary<int, int> { [BlasterId] = Tuning.ProdThresholds[2] };
        // rh: 1 (unflagged) -- mainIsGS is false, so this exercises rule 2b, not rule 3a.
        SeedRosterSlot(mem, slot: 0, nameId: 1, level: 30, rh: 1, off: (ushort)BlasterId, supp: 0, shield: 128);

        var gs = new GunSlinger(MakeGunMeta(), kills, dir, mem);
        var snap = gs.StoreForTest().Get(1);
        snap.HasOff = true;
        snap.OrigOff = 150;   // a legacy real off-hand item from before this fix shipped

        using var cap = LogCapture.Start();
        gs.PrepRoster();

        Assert.Contains(cap.File, line => line.Contains("WARN") && line.Contains("150"));

        long b = Offsets.RosterBase + 0 * Offsets.RosterStride;
        Assert.Equal(GunSlingerPolicy.EmptyOffHand, mem.WrittenU16[b + Offsets.ROffHand]);
        Assert.False(gs.StoreForTest().Get(1).HasOff);
    }

    // ── Test 8: in-battle nuances ──────────────────────────────────────────────────────────

    [Fact]
    public void InBattle_shieldOccupied_killsReassert()
    {
        var snap = new GunSlingerSnap { HasOff = true, OrigOff = EmptyU16 };
        var action = GunSlingerPolicy.DesiredOffHand(
            mainIsGS: true, twin: BlasterId, off: EmptyU16, shield: 128, snap: snap,
            menuOpen: false, inBattle: true, twinIds: BlasterTwinIds);
        Assert.NotEqual(GunSlingerOffAction.Write, action);   // no re-stamp -- THE formation fix
    }

    [Fact]
    public void InBattle_shieldFree_reassertFiresAsToday()
    {
        var snap = new GunSlingerSnap { HasOff = true, OrigOff = EmptyU16 };
        var action = GunSlingerPolicy.DesiredOffHand(
            mainIsGS: true, twin: BlasterId, off: EmptyU16, shield: EmptyU16, snap: snap,
            menuOpen: false, inBattle: true, twinIds: BlasterTwinIds);
        Assert.Equal(GunSlingerOffAction.Write, action);
    }

    [Fact]
    public void InBattle_noSnapshotAndWrite()
    {
        var snap = new GunSlingerSnap();
        var action = GunSlingerPolicy.DesiredOffHand(
            mainIsGS: true, twin: BlasterId, off: EmptyU16, shield: EmptyU16, snap: snap,
            menuOpen: false, inBattle: true, twinIds: BlasterTwinIds);
        Assert.Equal(GunSlingerOffAction.Leave, action);
    }

    [Fact]
    public void PrepRoster_inBattle_shieldOccupied_killsReassert_formationFix()
    {
        using var temp = TempDirs.Create("gs_test_");
        string dir = temp.Dir;

        var mem = new FakeSparseMemory();
        var kills = new Dictionary<int, int> { [BlasterId] = Tuning.ProdThresholds[2] };
        SeedRosterSlot(mem, slot: 0, nameId: 1, level: 30, rh: BlasterId, off: EmptyU16, supp: 0);

        var gs = new GunSlinger(MakeGunMeta(), kills, dir, mem);
        gs.PrepRoster();   // out of battle: grant

        long b = Offsets.RosterBase + 0 * Offsets.RosterStride;
        mem.U16s[b + Offsets.ROffHand] = EmptyU16;   // the game cleared the twin
        mem.U16s[b + Offsets.RShield] = 128;         // the player equipped a shield
        mem.WrittenU16.Clear();

        gs.PrepRoster(inBattle: true);   // THE formation fix: a shield in play kills the re-assert

        Assert.False(mem.WrittenU16.ContainsKey(b + Offsets.ROffHand),
            "a shield in play must block the in-battle re-assert, even mid-battle");
        Assert.False(mem.WrittenU16.ContainsKey(b + Offsets.RShield));
    }

    // ── Test 9: support lane mirrors ───────────────────────────────────────────────────────

    [Fact]
    public void Support_consentWithheld_byShield_RestoresIfHeld()
    {
        var snap = new GunSlingerSnap { HasSupp = true, OrigSupp = 463 };
        var action = GunSlingerPolicy.DesiredSupport(
            mainIsGS: true, supp: DualWieldKeyForTest, off: EmptyU16, shield: 128, snap: snap,
            menuOpen: false, inBattle: false, twinIds: BlasterTwinIds);
        Assert.Equal(GunSlingerSuppAction.Restore, action);
    }

    [Fact]
    public void Support_consentWithheld_byPlayerItemInOffHand_RestoresIfHeld()
    {
        var snap = new GunSlingerSnap { HasSupp = true, OrigSupp = 463 };
        var action = GunSlingerPolicy.DesiredSupport(
            mainIsGS: true, supp: DualWieldKeyForTest, off: 200, shield: EmptyU16, snap: snap,
            menuOpen: false, inBattle: false, twinIds: BlasterTwinIds);
        Assert.Equal(GunSlingerSuppAction.Restore, action);
    }

    [Fact]
    public void Support_consentWithheld_noPriorGrant_Leave()
    {
        var snap = new GunSlingerSnap();
        var action = GunSlingerPolicy.DesiredSupport(
            mainIsGS: true, supp: 0, off: 200, shield: EmptyU16, snap: snap,
            menuOpen: false, inBattle: false, twinIds: BlasterTwinIds);
        Assert.Equal(GunSlingerSuppAction.Leave, action);
    }

    // Pins deviation 2 (implementer's report): support's SnapshotAndWrite is gated !inBattle,
    // mirroring the off-hand's rule 6b, even though the spec's literal support table text never
    // states this guard explicitly (only rule 6b got the review's explicit annotation). A
    // mid-battle roster flicker must not seed a support snapshot any more than an off-hand one
    // should. Sabotage: drop the `inBattle ?` ternary's guard in DesiredSupport's last line
    // (always return SnapshotAndWrite) -> this test goes red.
    [Fact]
    public void InBattle_support_noSnapshotAndWrite()
    {
        var snap = new GunSlingerSnap();
        var action = GunSlingerPolicy.DesiredSupport(
            mainIsGS: true, supp: 0, off: EmptyU16, shield: EmptyU16, snap: snap,
            menuOpen: false, inBattle: true, twinIds: BlasterTwinIds);
        Assert.Equal(GunSlingerSuppAction.Leave, action);
    }

    // ── Test 10: integration replay (the run-4 timeline, then the shield-drill timeline end to
    // end with the sack-invariant assertion) ──────────────────────────────────────────────────

    [Fact]
    public void IntegrationReplay_run4Timeline_then_shieldDrill_neverTouchesTheSack()
    {
        using var temp = TempDirs.Create("gs_test_");
        string dir = temp.Dir;

        var mem = new FakeSparseMemory();
        var kills = new Dictionary<int, int> { [OutriderPistolId] = Tuning.ProdThresholds[2] };
        SeedRosterSlot(mem, slot: 0, nameId: 1, level: 30, rh: OutriderPistolId, off: EmptyU16, supp: 0);

        var gs = new GunSlinger(MakeTwoGunMeta(), kills, dir, mem);
        long b = Offsets.RosterBase + 0 * Offsets.RosterStride;

        // Run-4 timeline: grant on the free map.
        gs.PrepRoster();
        Assert.Equal((ushort)OutriderPistolId, mem.U16s[b + Offsets.ROffHand]);

        // Menu opens; the fake game clears the off-hand the same tick a real equip screen would.
        SetMenu(mem, 1);
        mem.U16s[b + Offsets.ROffHand] = EmptyU16;
        mem.WrittenU16.Clear();
        gs.PrepRoster();
        Assert.False(mem.WrittenU16.ContainsKey(b + Offsets.ROffHand), "zero mod writes while the menu is open");

        // Close with hands empty -- the twin re-grants once the debounce settles.
        SetMenu(mem, 0);
        gs.PrepRoster(); gs.PrepRoster(); gs.PrepRoster();
        mem.WrittenU16.Clear();
        gs.PrepRoster();
        Assert.Equal((ushort)OutriderPistolId, mem.WrittenU16[b + Offsets.ROffHand]);

        // The shield-drill timeline: equip a real shield -- the mod never touches the shield
        // byte (it only ever reads RShield).
        mem.U16s[b + Offsets.RShield] = 128;
        mem.WrittenU16.Clear();
        gs.PrepRoster();
        Assert.False(mem.WrittenU16.ContainsKey(b + Offsets.RShield));

        // Sack invariant: the mod owns NO sack writes at all, anywhere in this replay -- it has
        // no code path that ever touches InventoryCountBase. STILL TRUE after the owner-AC round:
        // GunSlinger.Reconcile.cs's refund detector is WATCH-ONLY by the adversarial plan
        // review's ruling (the armed write-back rule was rejected; the owner accepted interim
        // phantom inflation over the risk of an armed rule destroying real property on a false
        // positive) -- it only reads InventoryCountBase (guarded, gated !inBattle) and logs, it
        // never writes there. Bound at 200, not the full 1..315 item-id span: in the REAL memory
        // map RosterBase sits only 0x110 (272) bytes above InventoryCountBase, so a wider scan
        // would re-discover this test's OWN legitimate ROffHand/RSupport writes at slot 0 as
        // false-positive "sack" hits in the fake's flat numeric address space -- an artifact of
        // FakeSparseMemory having no real separate address regions, not a production concern.
        for (int itemId = 0; itemId < 200; itemId++)
        {
            long sackAddr = Offsets.InventoryCountBase + itemId;
            Assert.False(mem.Written.ContainsKey(sackAddr));
            Assert.False(mem.WrittenU16.ContainsKey(sackAddr));
        }
        Assert.Empty(mem.WrittenBytes);
    }

    // ── Battle-end TWILIGHT regression (owner live pass, tape lw193_watch_*.log 10:42:38-
    // 10:43:58): still In (the ~4s exit debounce running) but sustained off the live field, the
    // game clears oh during its own teardown, and the pre-fix re-assert stamped it right back --
    // the post-battle gear reconcile then "returned" that phantom stamp as a genuine sack refund
    // (a mint). The fix: the re-assert Write (off-hand rule 6a, support's mirror) requires NOT
    // sustained-off-field-in-battle. Sabotage: drop the sustainedOffField gate from rule 6a ->
    // Twilight_... goes red (the fake records the forbidden re-stamp). ─────────────────────────

    [Fact]
    public void Twilight_sustainedOffField_suppressesReassert_thenBattleExit_regrantsNormally()
    {
        using var temp = TempDirs.Create("gs_test_");
        string dir = temp.Dir;

        var mem = new FakeSparseMemory();
        var kills = new Dictionary<int, int> { [BlasterId] = Tuning.ProdThresholds[2] };
        SeedRosterSlot(mem, slot: 0, nameId: 1, level: 30, rh: BlasterId, off: EmptyU16, supp: 0);

        var gs = new GunSlinger(MakeGunMeta(), kills, dir, mem);
        gs.PrepRoster();   // out of battle: grant (twin stamped)

        long b = Offsets.RosterBase + 0 * Offsets.RosterStride;
        Assert.Equal((ushort)BlasterId, mem.U16s[b + Offsets.ROffHand]);

        // Battle live, on the field: the game clears oh mid-battle -- the re-assert must still
        // fire (today's proven behavior, unaffected by this fix).
        mem.U16s[b + Offsets.ROffHand] = EmptyU16;
        mem.WrittenU16.Clear();
        gs.PrepRoster(inBattle: true, secondsOffField: 0.0);
        Assert.True(mem.WrittenU16.ContainsKey(b + Offsets.ROffHand), "on-field re-assert must still fire");

        // The battle-end TWILIGHT: still In (the exit debounce is running, ~4s), but the field
        // has been dark well past the hold. The game clears oh during its own teardown -- the
        // re-assert must NOT re-stamp it (that stamp is exactly what got refunded as a mint).
        mem.U16s[b + Offsets.ROffHand] = EmptyU16;
        mem.WrittenU16.Clear();
        gs.PrepRoster(inBattle: true, secondsOffField: 3.0);   // past the 2s hold, still under the ~4s exit debounce
        Assert.False(mem.WrittenU16.ContainsKey(b + Offsets.ROffHand), "twilight: the re-assert must NOT re-stamp");
        Assert.True(gs.StoreForTest().Get(1).HasOff, "the grant is only WITHHELD this tick, not abandoned");

        // The battle-exit edge fires; out of battle, the grant lane re-stamps normally (hands
        // empty) -- the twilight suppression never reaches the out-of-battle lanes.
        mem.WrittenU16.Clear();
        gs.PrepRoster(inBattle: false);
        Assert.True(mem.WrittenU16.ContainsKey(b + Offsets.ROffHand), "out of battle, the grant lane re-stamps normally once the twilight ends");
        Assert.Equal((ushort)BlasterId, mem.WrittenU16[b + Offsets.ROffHand]);
    }

    [Fact]
    public void MidBattleFlicker_offFieldUnderHold_reassertStillFires()
    {
        using var temp = TempDirs.Create("gs_test_");
        string dir = temp.Dir;

        var mem = new FakeSparseMemory();
        var kills = new Dictionary<int, int> { [BlasterId] = Tuning.ProdThresholds[2] };
        SeedRosterSlot(mem, slot: 0, nameId: 1, level: 30, rh: BlasterId, off: EmptyU16, supp: 0);

        var gs = new GunSlinger(MakeGunMeta(), kills, dir, mem);
        gs.PrepRoster();   // out of battle: grant

        long b = Offsets.RosterBase + 0 * Offsets.RosterStride;
        mem.U16s[b + Offsets.ROffHand] = EmptyU16;   // e.g. an enemy turn's mode-1 stretch cleared it
        mem.WrittenU16.Clear();

        // A brief mid-battle mode flicker (enemy turn / targeting / a pause), well under the 2s
        // hold -- this is exactly the shape the 2026-07-04 in-battle re-assert exists to survive.
        gs.PrepRoster(inBattle: true, secondsOffField: 0.6);

        Assert.True(mem.WrittenU16.ContainsKey(b + Offsets.ROffHand), "a brief mid-battle off-field flicker must not starve the re-assert");
        Assert.Equal((ushort)BlasterId, mem.WrittenU16[b + Offsets.ROffHand]);
    }
}
