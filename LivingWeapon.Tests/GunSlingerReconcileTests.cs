using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-193 owner-AC round: the browse-screen stamp gate (Offsets.PartyBrowseFlag) and the
/// watch-only refund reconcile (GunSlinger.Reconcile.cs). See GunSlingerConsentTests.cs for the
/// base consent model this round builds on (unchanged) and its evaporation-removal inversion
/// tests. Organised by the review ruling's test list, one region per item.
/// </summary>
public class GunSlingerReconcileTests
{
    private const int BlasterId = 76;
    private const int OutriderPistolId = 71;
    private const int ArbalestId = 79;
    private const ushort EmptyU16 = 0xFFFF;

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

    private static void SetBrowse(FakeSparseMemory mem, byte value)
    {
        mem.ReadableAddrs.Add(Offsets.PartyBrowseFlag);
        mem.U8s[Offsets.PartyBrowseFlag] = value;
    }

    private static void SetSack(FakeSparseMemory mem, int id, byte value)
    {
        long addr = Offsets.InventoryCountBase + id;
        mem.ReadableAddrs.Add(addr);
        mem.U8s[addr] = value;
    }

    private static long RowAddr(int slot) => Offsets.RosterBase + slot * Offsets.RosterStride;

    // ══════════════════════════════════════════════════════════════════════════════════════
    // Browse-screen gate table (review ruling 2, blocker 4's corrected expression)
    // ══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Grants the twin (menu closed, default), then clears the off-hand as if the game
    /// had done it, ready for the caller to manipulate menu/browse and observe whether a
    /// re-assert lands.</summary>
    private static (GunSlinger gs, FakeSparseMemory mem, long b) GrantThenClearOffHand(string dir)
    {
        var mem = new FakeSparseMemory();
        var kills = new Dictionary<int, int> { [BlasterId] = Tuning.ProdThresholds[2] };
        SeedRosterSlot(mem, slot: 0, nameId: 1, level: 30, rh: BlasterId, off: EmptyU16, supp: 0);
        var gs = new GunSlinger(MakeGunMeta(), kills, dir, mem);
        gs.PrepRoster();   // grant

        long b = RowAddr(0);
        mem.U16s[b + Offsets.ROffHand] = EmptyU16;   // "the game" clears it
        return (gs, mem, b);
    }

    [Fact]
    public void BrowseGate_EAndATabsShop_browseZero_denies()
    {
        using var temp = TempDirs.Create("gs_test_");
        var (gs, mem, b) = GrantThenClearOffHand(temp.Dir);

        SetMenu(mem, 1);
        SetBrowse(mem, 0);   // E&A / Inventory / Chronicle / Options / save / shop all read 0
        mem.WrittenU16.Clear();
        gs.PrepRoster();

        Assert.False(mem.WrittenU16.ContainsKey(b + Offsets.ROffHand), "browse=0 must never unlock writes");
    }

    [Fact]
    public void BrowseGate_browseUnreadable_denies()
    {
        using var temp = TempDirs.Create("gs_test_");
        var (gs, mem, b) = GrantThenClearOffHand(temp.Dir);

        SetMenu(mem, 1);   // browse never seeded -- stays unreadable
        mem.WrittenU16.Clear();
        gs.PrepRoster();

        Assert.False(mem.WrittenU16.ContainsKey(b + Offsets.ROffHand), "an unreadable browse byte must deny, matching [worldmap-menu-open-byte]'s own convention");
    }

    [Fact]
    public void BrowseGate_onePassOfOne_notYetStable_denies()
    {
        using var temp = TempDirs.Create("gs_test_");
        var (gs, mem, b) = GrantThenClearOffHand(temp.Dir);

        SetMenu(mem, 1);
        SetBrowse(mem, 1);
        mem.WrittenU16.Clear();
        gs.PrepRoster();   // first-ever pass reading browse==1 -- not yet 2 consecutive

        Assert.False(mem.WrittenU16.ContainsKey(b + Offsets.ROffHand), "a lone browse=1 reading must not unlock writes (review's 2-pass debounce ruling)");
    }

    [Fact]
    public void BrowseGate_ESCFromEAndA_browse0Then1Then1_stampsOnTheSecondConsecutivePass()
    {
        using var temp = TempDirs.Create("gs_test_");
        var (gs, mem, b) = GrantThenClearOffHand(temp.Dir);

        SetMenu(mem, 1);
        SetBrowse(mem, 0);   // still browsing E&A
        gs.PrepRoster();

        SetBrowse(mem, 1);   // ESCed out, landed on Status/root -- first 1-reading
        mem.WrittenU16.Clear();
        gs.PrepRoster();
        Assert.False(mem.WrittenU16.ContainsKey(b + Offsets.ROffHand), "first consecutive browse=1 pass -- not yet stable");

        mem.WrittenU16.Clear();
        gs.PrepRoster();   // second CONSECUTIVE browse=1 pass -- now stable
        Assert.True(mem.WrittenU16.ContainsKey(b + Offsets.ROffHand), "the second consecutive browse=1 pass unlocks the re-assert");
        Assert.Equal((ushort)BlasterId, mem.WrittenU16[b + Offsets.ROffHand]);
    }

    [Fact]
    public void BrowseGate_menuUnreadable_browseStable_stillDenies_doubleFailSafe()
    {
        // Review blocker 4's corrected expression: readability is checked EXPLICITLY and FIRST.
        // A fully-stable browse=1 reading must not unlock writes if the menu byte itself is
        // unreadable this pass.
        using var temp = TempDirs.Create("gs_test_");
        var (gs, mem, b) = GrantThenClearOffHand(temp.Dir);

        mem.ReadableAddrs.Remove(Offsets.MenuOpenFlag);   // the menu byte becomes unreadable
        SetBrowse(mem, 1);
        gs.PrepRoster();   // browse pass 1
        mem.WrittenU16.Clear();
        gs.PrepRoster();   // browse pass 2 -- browse IS stable now, but the menu byte still is not

        Assert.False(mem.WrittenU16.ContainsKey(b + Offsets.ROffHand),
            "an unreadable menu byte must deny even with a stable browse=1 (the double fail-safe)");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════
    // Watch-only reconcile (review ruling 3)
    // ══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Grants the twin and runs ONE extra pass so the reconcile's per-row memory
    /// re-baselines with OffTwinId actually set to the twin (its first bootstrap pass captures
    /// the PRE-grant raw read, which is EMPTY, not the twin -- see GunSlinger.Reconcile.cs's
    /// class doc). Returns everything a detection-pass test needs.</summary>
    private static (GunSlinger gs, FakeSparseMemory mem, long b, List<(string type, string payload)> flight)
        SetupTwinHeldForReconcile(string dir, Dictionary<int, WeaponMeta> meta, Dictionary<int, int> kills,
            int slot, ushort nameId, int twinId, byte startingSack)
    {
        var mem = new FakeSparseMemory();
        SeedRosterSlot(mem, slot, nameId, level: 30, rh: (ushort)twinId, off: EmptyU16, supp: 0);
        SetSack(mem, twinId, startingSack);
        var flight = new List<(string type, string payload)>();
        var gs = new GunSlinger(meta, kills, dir, mem, recorder: (t, p) => flight.Add((t, p)));
        gs.PrepRoster();   // pass 1: grant fires; row memory bootstraps OffTwinId=0 (pre-grant raw read)
        gs.PrepRoster();   // pass 2: off now reads the twin; row memory re-baselines OffTwinId=twin
        long b = RowAddr(slot);
        Assert.Equal((ushort)twinId, mem.U16s[b + Offsets.ROffHand]);   // sanity: the twin really is held
        return (gs, mem, b, flight);
    }

    [Fact]
    public void Reconcile_directUnequipTapeShape_logsAndRecords_zeroMemoryWrites()
    {
        // Tape shape: main hand UNCHANGED, off-hand cleared by the game, sack[twin] rises by
        // exactly the one phantom. Detection pass runs with the menu open (browse not stable) so
        // the UNRELATED normal re-assert is also suppressed this pass -- isolates the assertion
        // to "the reconcile itself writes nothing", not "nothing in the whole tick writes".
        using var temp = TempDirs.Create("gs_test_");
        var kills = new Dictionary<int, int> { [OutriderPistolId] = Tuning.ProdThresholds[2] };
        var (gs, mem, b, flight) = SetupTwinHeldForReconcile(temp.Dir, MakeTwoGunMeta(), kills, 0, 1, OutriderPistolId, startingSack: 7);

        mem.U16s[b + Offsets.ROffHand] = EmptyU16;   // the game clears it
        SetSack(mem, OutriderPistolId, 8);           // and "returns" the phantom to the sack
        SetMenu(mem, 1);                             // suppress the unrelated re-assert this pass

        using var cap = LogCapture.Start();
        mem.Written.Clear(); mem.WrittenU16.Clear(); mem.WrittenBytes.Clear();
        gs.PrepRoster();

        Assert.Contains(cap.File, line => line.Contains("phantom copy now exists") && !line.Contains("WARN"));
        Assert.Contains(flight, e => e.type == "twin-refund" && e.payload.Contains("mainHandChanged=False"));
        Assert.Empty(mem.Written);
        Assert.Empty(mem.WrittenU16);
        Assert.Empty(mem.WrittenBytes);
    }

    [Fact]
    public void Reconcile_mainSwapTapeShape_stillWouldFire_recordsMainHandChangedTrue()
    {
        // Sabotage-verified by hand during implementation: adding a sack write (a guarded W8) to
        // the would-fire branch turns THIS test red immediately via the Assert.Empty(mem.Written)
        // below -- W8 lands in FakeSparseMemory's u8 Written map, not WrittenU16/WrittenBytes, so
        // asserting only those two would have let a real sack write through unnoticed. The
        // pre-existing sack-invariant integration test (GunSlingerConsentTests.cs) does NOT catch
        // this sabotage -- its own scenario never triggers a reconcile would-fire in the first
        // place, so a write gated behind that condition never executes there.
        using var temp = TempDirs.Create("gs_test_");
        var kills = new Dictionary<int, int>
        {
            [OutriderPistolId] = Tuning.ProdThresholds[2],
            [ArbalestId] = Tuning.ProdThresholds[2],
        };
        var (gs, mem, b, flight) = SetupTwinHeldForReconcile(temp.Dir, MakeTwoGunMeta(), kills, 0, 1, OutriderPistolId, startingSack: 7);

        mem.U16s[b + Offsets.RRHand] = (ushort)ArbalestId;   // the main hand ALSO changed this window
        mem.U16s[b + Offsets.ROffHand] = EmptyU16;
        SetSack(mem, OutriderPistolId, 8);
        SetMenu(mem, 1);

        mem.Written.Clear(); mem.WrittenU16.Clear(); mem.WrittenBytes.Clear();
        gs.PrepRoster();

        // Watch-only never filters on mainHandChanged (review ruling 3) -- it still fires, just
        // with the fact recorded for a human to weigh.
        Assert.Contains(flight, e => e.type == "twin-refund" && e.payload.Contains("mainHandChanged=True"));
        Assert.Empty(mem.Written);
        Assert.Empty(mem.WrittenU16);
        Assert.Empty(mem.WrittenBytes);
    }

    [Fact]
    public void Reconcile_crossRowContext_namesTheOtherRowTouchingTheId()
    {
        using var temp = TempDirs.Create("gs_test_");
        var kills = new Dictionary<int, int> { [OutriderPistolId] = Tuning.ProdThresholds[2] };
        var (gs, mem, b, flight) = SetupTwinHeldForReconcile(temp.Dir, MakeTwoGunMeta(), kills, 0, 1, OutriderPistolId, startingSack: 7);

        // A second, unrelated wielder ALSO carries an Outrider Pistol as their main hand --
        // named as ambiguity context in the record, per review ruling 3.
        SeedRosterSlot(mem, slot: 1, nameId: 2, level: 30, rh: (ushort)OutriderPistolId, off: EmptyU16, supp: 0);

        mem.U16s[b + Offsets.ROffHand] = EmptyU16;
        SetSack(mem, OutriderPistolId, 8);
        SetMenu(mem, 1);

        mem.Written.Clear(); mem.WrittenU16.Clear(); mem.WrittenBytes.Clear();
        gs.PrepRoster();

        Assert.Contains(flight, e => e.type == "twin-refund" && e.payload.Contains("otherRows=[1]"));
        // Same coverage gap the verifier flagged on the mainSwap sibling: a guarded W8 sack write
        // lands in FakeSparseMemory's u8 Written map, not WrittenU16/WrittenBytes, so this must
        // be checked explicitly for the "zero memory writes" claim to actually hold.
        Assert.Empty(mem.Written);
        Assert.Empty(mem.WrittenU16);
        Assert.Empty(mem.WrittenBytes);
    }

    [Fact]
    public void Reconcile_offHandNeverHeldOurTwin_neverFires()
    {
        // Negative: the sack rises, but last pass's off-hand did NOT hold our twin (it held a
        // real player item throughout) -- the base precondition fails structurally.
        using var temp = TempDirs.Create("gs_test_");
        var mem = new FakeSparseMemory();
        var kills = new Dictionary<int, int> { [OutriderPistolId] = Tuning.ProdThresholds[2] };
        SeedRosterSlot(mem, slot: 0, nameId: 1, level: 30, rh: (ushort)OutriderPistolId, off: 200, supp: 0);
        SetSack(mem, OutriderPistolId, 7);
        var flight = new List<(string type, string payload)>();
        var gs = new GunSlinger(MakeTwoGunMeta(), kills, temp.Dir, mem, recorder: (t, p) => flight.Add((t, p)));
        gs.PrepRoster();
        gs.PrepRoster();

        SetSack(mem, OutriderPistolId, 8);   // sack rises for some unrelated reason
        gs.PrepRoster();

        Assert.DoesNotContain(flight, e => e.type == "twin-refund");
    }

    [Fact]
    public void Reconcile_hasOffWasFalse_neverFires()
    {
        // Negative: a player-owned dual-wield build (off happens to equal a twin id, but never
        // granted -- HasOff false) unequips lawfully. No grant on record means no detection.
        using var temp = TempDirs.Create("gs_test_");
        var mem = new FakeSparseMemory();
        var kills = new Dictionary<int, int> { [OutriderPistolId] = Tuning.ProdThresholds[2] };
        SeedRosterSlot(mem, slot: 0, nameId: 1, level: 30, rh: 1 /* unflagged */, off: (ushort)OutriderPistolId, supp: 0);
        SetSack(mem, OutriderPistolId, 7);
        var flight = new List<(string type, string payload)>();
        var gs = new GunSlinger(MakeTwoGunMeta(), kills, temp.Dir, mem, recorder: (t, p) => flight.Add((t, p)));
        gs.PrepRoster();   // never touched (rule 5: player-owned dual-wield build) -- HasOff stays false
        gs.PrepRoster();

        long b = RowAddr(0);
        mem.U16s[b + Offsets.ROffHand] = EmptyU16;   // the player unequips their own pistol lawfully
        SetSack(mem, OutriderPistolId, 8);            // and it's genuinely refunded
        gs.PrepRoster();

        Assert.DoesNotContain(flight, e => e.type == "twin-refund");
    }

    [Fact]
    public void Reconcile_lifecycle_inBattleFlipClearsMemory()
    {
        using var temp = TempDirs.Create("gs_test_");
        var kills = new Dictionary<int, int> { [OutriderPistolId] = Tuning.ProdThresholds[2] };
        var (gs, mem, b, flight) = SetupTwinHeldForReconcile(temp.Dir, MakeTwoGunMeta(), kills, 0, 1, OutriderPistolId, startingSack: 7);

        gs.PrepRoster(inBattle: true, secondsOffField: 0.0);   // flip true -- wipes last-pass memory (bootstraps fresh in-battle, no sack tracked)

        // The detection shape is staged BEFORE the flip back: the flip-to-false pass wipes
        // memory again at ITS OWN start (review blocker 5: the edge-detect fires the instant the
        // param changes), so this exact pass has no prior baseline to compare against even
        // though the shape below WOULD have fired had memory survived the round trip.
        mem.U16s[b + Offsets.ROffHand] = EmptyU16;
        SetSack(mem, OutriderPistolId, 8);
        SetMenu(mem, 1);
        gs.PrepRoster(inBattle: false, secondsOffField: 0.0);

        Assert.DoesNotContain(flight, e => e.type == "twin-refund");
    }

    [Fact]
    public void Reconcile_lifecycle_nameIdChangeClearsRowMemory()
    {
        using var temp = TempDirs.Create("gs_test_");
        var kills = new Dictionary<int, int> { [OutriderPistolId] = Tuning.ProdThresholds[2] };
        var (gs, mem, b, flight) = SetupTwinHeldForReconcile(temp.Dir, MakeTwoGunMeta(), kills, 0, 1, OutriderPistolId, startingSack: 7);

        // A different unit now occupies slot 0 (roster reshuffle).
        mem.U16s[b + Offsets.RNameId] = 99;
        mem.U16s[b + Offsets.ROffHand] = EmptyU16;
        SetSack(mem, OutriderPistolId, 8);
        SetMenu(mem, 1);
        gs.PrepRoster();

        Assert.DoesNotContain(flight, e => e.type == "twin-refund");
    }

    [Fact]
    public void Reconcile_lifecycle_invalidSackReadClearsCache()
    {
        using var temp = TempDirs.Create("gs_test_");
        var kills = new Dictionary<int, int> { [OutriderPistolId] = Tuning.ProdThresholds[2] };
        var (gs, mem, b, flight) = SetupTwinHeldForReconcile(temp.Dir, MakeTwoGunMeta(), kills, 0, 1, OutriderPistolId, startingSack: 7);

        // The sack read goes invalid for one pass (its cached baseline gets dropped, blocker 5).
        mem.ReadableAddrs.Remove(Offsets.InventoryCountBase + OutriderPistolId);
        gs.PrepRoster();

        // The sack read is valid again immediately after, at a HIGHER value -- but there is no
        // cached baseline to compare against right after the gap, so this must not fire.
        SetSack(mem, OutriderPistolId, 8);
        mem.U16s[b + Offsets.ROffHand] = EmptyU16;
        SetMenu(mem, 1);
        gs.PrepRoster();

        Assert.DoesNotContain(flight, e => e.type == "twin-refund");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════
    // Owner AC integration fixtures (plan v3 test 6, adjusted to watch-only)
    // ══════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void AC_Block1_TwinVisibleThroughEAndA_AndAfterBattle()
    {
        using var temp = TempDirs.Create("gs_test_");
        var mem = new FakeSparseMemory();
        var kills = new Dictionary<int, int> { [BlasterId] = Tuning.ProdThresholds[2] };
        SeedRosterSlot(mem, slot: 0, nameId: 1, level: 30, rh: BlasterId, off: EmptyU16, supp: 0);
        var gs = new GunSlinger(MakeGunMeta(), kills, temp.Dir, mem);

        // Grant at Status (menu closed is the free-map default here).
        gs.PrepRoster();
        long b = RowAddr(0);
        Assert.Equal((ushort)BlasterId, mem.U16s[b + Offsets.ROffHand]);

        // Visit E&A (menu open, browse=0): the twin stays visibly stamped -- nothing touches it.
        SetMenu(mem, 1);
        SetBrowse(mem, 0);
        mem.WrittenU16.Clear();
        gs.PrepRoster();
        gs.PrepRoster();
        Assert.False(mem.WrittenU16.ContainsKey(b + Offsets.ROffHand));
        Assert.Equal((ushort)BlasterId, mem.U16s[b + Offsets.ROffHand]);

        // ESC back to Status (AC item 3: pops back in within one browse-stable pass).
        SetBrowse(mem, 1);
        gs.PrepRoster();   // 1st consecutive browse=1 (steady state anyway -- off already == twin, no write needed)
        gs.PrepRoster();   // 2nd consecutive -- stable
        Assert.Equal((ushort)BlasterId, mem.U16s[b + Offsets.ROffHand]);

        // Enter a real battle: fires on the live field (steady state, off already the twin).
        SetMenu(mem, 0);
        gs.PrepRoster(inBattle: true, secondsOffField: 0.0);
        Assert.Equal((ushort)BlasterId, mem.U16s[b + Offsets.ROffHand]);

        // Battle-end twilight (owner tape): the game clears oh during teardown; the twilight
        // hold suppresses the re-assert; then the battle-exit edge fires and the out-of-battle
        // grant lane re-stamps normally.
        mem.U16s[b + Offsets.ROffHand] = EmptyU16;
        mem.WrittenU16.Clear();
        gs.PrepRoster(inBattle: true, secondsOffField: 3.0);
        Assert.False(mem.WrittenU16.ContainsKey(b + Offsets.ROffHand), "twilight suppresses the re-stamp");

        mem.WrittenU16.Clear();
        gs.PrepRoster(inBattle: false);
        Assert.Equal((ushort)BlasterId, mem.WrittenU16[b + Offsets.ROffHand]);

        // Item 6: back in E&A after the battle -- both pistols show (the twin persists visibly).
        SetMenu(mem, 1);
        SetBrowse(mem, 0);
        mem.WrittenU16.Clear();
        gs.PrepRoster();
        Assert.Equal((ushort)BlasterId, mem.U16s[b + Offsets.ROffHand]);
    }

    [Fact]
    public void AC_Block2_OccupiedOffHand_NothingEverHappens()
    {
        using var temp = TempDirs.Create("gs_test_");
        var mem = new FakeSparseMemory();
        var kills = new Dictionary<int, int> { [BlasterId] = Tuning.ProdThresholds[2] };
        SeedRosterSlot(mem, slot: 0, nameId: 1, level: 30, rh: BlasterId, off: 100, supp: 0);   // real off-hand item
        var gs = new GunSlinger(MakeGunMeta(), kills, temp.Dir, mem);
        long b = RowAddr(0);

        gs.PrepRoster();
        Assert.False(mem.WrittenU16.ContainsKey(b + Offsets.ROffHand));

        SetMenu(mem, 1);
        SetBrowse(mem, 1);
        gs.PrepRoster(); gs.PrepRoster();
        Assert.False(mem.WrittenU16.ContainsKey(b + Offsets.ROffHand));

        SetMenu(mem, 0);
        gs.PrepRoster(inBattle: true, secondsOffField: 0.0);
        Assert.False(mem.WrittenU16.ContainsKey(b + Offsets.ROffHand));
        Assert.False(gs.StoreForTest().Get(1).HasOff);
    }

    [Fact]
    public void AC_Block3_TwinDiesWithSource_MainHandEmptied()
    {
        using var temp = TempDirs.Create("gs_test_");
        var mem = new FakeSparseMemory();
        var kills = new Dictionary<int, int> { [BlasterId] = Tuning.ProdThresholds[2] };
        SeedRosterSlot(mem, slot: 0, nameId: 1, level: 30, rh: BlasterId, off: EmptyU16, supp: 0);
        var gs = new GunSlinger(MakeGunMeta(), kills, temp.Dir, mem);
        gs.PrepRoster();   // grant

        long b = RowAddr(0);
        mem.U16s[b + Offsets.RRHand] = 1;   // the wielder unequips the source weapon entirely
        mem.WrittenU16.Clear();
        gs.PrepRoster();

        Assert.True(mem.WrittenU16.ContainsKey(b + Offsets.ROffHand), "the twin must die with its source");
        Assert.Equal(EmptyU16, mem.WrittenU16[b + Offsets.ROffHand]);
        Assert.False(gs.StoreForTest().Get(1).HasOff);
    }

    [Fact]
    public void AC_Block3_PlayerOwnedOffHandWeapon_Untouched()
    {
        using var temp = TempDirs.Create("gs_test_");
        var mem = new FakeSparseMemory();
        var kills = new Dictionary<int, int> { [BlasterId] = Tuning.ProdThresholds[2] };
        // off happens to equal the flagged id, but no grant is on record -- a coincidental
        // player-owned dual-wield build.
        SeedRosterSlot(mem, slot: 0, nameId: 1, level: 30, rh: BlasterId, off: (ushort)BlasterId, supp: 0);
        var gs = new GunSlinger(MakeGunMeta(), kills, temp.Dir, mem);

        gs.PrepRoster();

        long b = RowAddr(0);
        Assert.False(mem.WrittenU16.ContainsKey(b + Offsets.ROffHand));
        Assert.False(gs.StoreForTest().Get(1).HasOff);
    }
}
