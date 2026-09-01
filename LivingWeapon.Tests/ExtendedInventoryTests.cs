using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LivingWeapon;
using Reloaded.Hooks.Definitions;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>LW-346 S4: the boot-arm transaction over the fakes: a vanilla 1.5.2 process image
/// (every patch site's old byte, the ten E9 thunks with their real bytes, the catalog disp32 and
/// the five "+" records) either arms completely or is left byte-for-byte untouched.</summary>
public class ExtendedInventoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lw_ext_inv_" + Guid.NewGuid().ToString("N"));
    public ExtendedInventoryTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static readonly (long Addr, byte[] Bytes)[] Thunks =
    {
        (Offsets.ThunkWeaponStat, new byte[] { 0xE9, 0xE9, 0xC8, 0xBC, 0x0F }),
        (Offsets.ThunkValidity, new byte[] { 0xE9, 0x7E, 0x9D, 0xC1, 0x0F }),
        (Offsets.ThunkTypeProbe, new byte[] { 0xE9, 0xC3, 0x45, 0xC3, 0x0F }),
        (Offsets.ThunkRangeIndex, new byte[] { 0xE9, 0x5E, 0xDF, 0xBA, 0x0F }),
        (Offsets.ThunkSpritePair, new byte[] { 0xE9, 0x5B, 0xF2, 0xC0, 0x0F }),
        (Offsets.ThunkRangeBase, new byte[] { 0xE9, 0x4C, 0x73, 0xBB, 0x0F }),
        (Offsets.ThunkSibling1, new byte[] { 0xE9, 0x17, 0xEF, 0xBD, 0x0F }),
        (Offsets.ThunkSibling2, new byte[] { 0xE9, 0x43, 0x41, 0xBF, 0x0F }),
        (Offsets.ThunkSibling3, new byte[] { 0xE9, 0x8B, 0xF6, 0xBF, 0x0F }),
        (Offsets.ThunkSibling4, new byte[] { 0xE9, 0x32, 0x56, 0xC0, 0x0F }),
    };

    /// <summary>A vanilla 1.5.2 image as far as the boot arm looks (bytes read on disk 2026-08-27).
    /// LW-368 round 2 / round 2b: also seeds all 55 list-relocation fields and the two 0x110-byte
    /// old blocks (bag counts + sibling flags) at their vanilla values, so BootArm still arms end
    /// to end. LW-371: also seeds the ten template-relocation fields and the three old chart spans
    /// (each an empty chart: the 0x00FF marker at word 0, zeros after) -- without this,
    /// <c>_templates.Install</c> refuses on the unreadable/wrong old chart spans and every
    /// BootArm-based test fails <c>Assert.True(inv.Armed)</c>.</summary>
    internal static FakeCodePatcher VanillaImage()
    {
        var f = new FakeCodePatcher();
        foreach (var p in ExtendedSites.BootPatches(1)) f.Seed(p.Addr, p.Old);
        foreach (var (addr, bytes) in Thunks) f.Seed(addr, bytes);
        f.Seed(Offsets.FnSwingPrepIdCopy, SwingIdFallbackHook.ExpectedSite);   // LW-365
        f.Seed(Offsets.ExtCatalogDisp32, 0x10, 0xF9, 0x67, 0x00);
        ShopFlagsMirrorTests.SeedVanillaShopSites(f);
        for (int id = ExtendedCatalog.DlcLo; id <= ExtendedCatalog.DlcHi; id++)
        {
            var rec = new byte[12]; rec[0] = (byte)id;
            f.Seed(Offsets.ExtCatalogBase + (long)id * 12, rec);
        }
        foreach (var site in ListRelocation.Sites) f.Seed(site.Addr, BitConverter.GetBytes(site.Vanilla));
        f.Seed(Offsets.BagCountArray, new byte[ListRelocation.BlockBytes]);
        f.Seed(Offsets.SiblingListArray, new byte[ListRelocation.BlockBytes]);
        foreach (var s in TemplateRelocation.Slots) f.Seed(s.Addr, BitConverter.GetBytes(s.Vanilla));
        foreach (var rf in TemplateRelocation.RipFields) f.Seed(rf.Addr, BitConverter.GetBytes(rf.Vanilla));
        foreach (var c in TemplateRelocation.CapSites) f.Seed(c.Addr, c.Vanilla);
        foreach (var chart in TemplateRelocation.Charts) f.Seed(chart.OldBase, EmptyChart(chart.SpanBytes));
        return f;
    }

    /// <summary>An empty order-chart span: the 0x00FF end marker at word 0, zeros after (matches
    /// what the game's copy-protected new-game initializer leaves before anything is owned --
    /// 0x150C9FB15 for the inventory chart, 0x150C9FCAD..CC9 for the picker's sub-tables, plan
    /// finding 3; <c>InventoryResetHook</c> never touches a template at all, only the bag array).</summary>
    private static byte[] EmptyChart(int spanBytes)
    {
        var bytes = new byte[spanBytes];
        bytes[0] = (byte)(TemplateSeat.EndMarker & 0xFF);
        bytes[1] = (byte)(TemplateSeat.EndMarker >> 8);
        return bytes;
    }

    private static Dictionary<long, byte> Snapshot(FakeCodePatcher f) => new(f.Bytes);

    private static ExtendedInventoryData.LoadResult Moonblade() => new()
    {
        FolderPresent = true,
        Items = new[]
        {
            new ExtendedItemDef
            {
                Id = 261, Name = "Moonblade", Category = "Sword", CloneDonor = 37, ArtDonor = 37, SeedCount = 1,
                CatalogRecord = new byte[] { 0x04, 0x16, 0x00, 0x80, 0x25, 0x03, 0x00, 0x00, 0x0A, 0x00, 0x00, 0x00 },
                WeaponRow = new byte[] { 0x01, 0x8E, 0x01, 0xFF, 0x0F, 0x00, 0x00, 0x00 },
            },
        },
    };

    /// <summary>LW-365 F1 (v1.2): two items (ids 261, 262) so lo != hi in the fallback stub's
    /// baked range -- Moonblade() alone has lo == hi and cannot catch a swapped lo/count. Same
    /// records as Moonblade(), copied, with the second item's Id/Name changed.</summary>
    private static ExtendedInventoryData.LoadResult TwoBlades() => new()
    {
        FolderPresent = true,
        Items = new[]
        {
            new ExtendedItemDef
            {
                Id = 261, Name = "Moonblade", Category = "Sword", CloneDonor = 37, ArtDonor = 37, SeedCount = 1,
                CatalogRecord = new byte[] { 0x04, 0x16, 0x00, 0x80, 0x25, 0x03, 0x00, 0x00, 0x0A, 0x00, 0x00, 0x00 },
                WeaponRow = new byte[] { 0x01, 0x8E, 0x01, 0xFF, 0x0F, 0x00, 0x00, 0x00 },
            },
            new ExtendedItemDef
            {
                Id = 262, Name = "Ravager", Category = "Sword", CloneDonor = 37, ArtDonor = 37, SeedCount = 1,
                CatalogRecord = new byte[] { 0x04, 0x16, 0x00, 0x80, 0x25, 0x03, 0x00, 0x00, 0x0A, 0x00, 0x00, 0x00 },
                WeaponRow = new byte[] { 0x01, 0x8E, 0x01, 0xFF, 0x0F, 0x00, 0x00, 0x00 },
            },
        },
    };

    private static Func<LandmarkReading> Match => () => new LandmarkReading(LandmarkVerdict.Match);

    private ExtendedInventory Build(FakeCodePatcher f, ExtendedInventoryData.LoadResult data,
        Func<LandmarkReading>? pe = null, Func<IReloadedHooks?, string?>? hooks = null, ExtendedBagSidecar? sidecar = null,
        FakeNearAllocator? allocator = null)
        => new(f, allocator ?? new FakeNearAllocator(), data,
            sidecar ?? ExtendedBagSidecar.Load(Path.Combine(_dir, ExtendedBagSidecar.FileName)),
            pe ?? Match, hooks ?? (_ => null));

    [Fact]
    public void Arms_the_whole_set_over_a_vanilla_image_and_seeds_the_bag()
    {
        var f = VanillaImage();
        var alloc = new FakeNearAllocator();
        var inv = Build(f, Moonblade(), allocator: alloc);
        inv.BootArm(null);
        Assert.True(inv.Armed, inv.Refusal);
        Assert.Null(inv.Refusal);
        foreach (var p in ExtendedSites.BootPatches(1)) Assert.Equal(p.New, f.Bytes[p.Addr]);
        Assert.NotEqual(0x10, f.Bytes[Offsets.ExtCatalogDisp32]);   // catalog redirected
        foreach (var (addr, original) in Thunks)
        {
            var now = f.Read(addr, 5);
            Assert.True(ThunkStub.IsJmpRel32(now, addr, out long stub));
            Assert.NotEqual(original, now);
            Assert.True(f.Read(stub, 2).SequenceEqual(new byte[] { 0x89, 0xC8 }));   // a stub sits at the target
        }
        // LW-368 round 2: the seed lands on the RELOCATED page, never the vanilla block.
        Assert.NotEqual(Offsets.BagCountArray, inv.BagCountBase);
        Assert.Equal(1, f.Bytes[inv.BagCountBase + 261]);   // the data seed (no sidecar entry)
        Assert.Equal(0, f.Bytes[Offsets.BagCountArray + 261]);   // ... not the vanilla block, which stays zero
        Assert.Equal(0xE9, f.Bytes[Offsets.FnSwingPrepIdCopy]);   // LW-365: swing-id fallback armed
        // LW-365 fix round: pin the production wiring's lo/hi bounds baked into the fallback stub
        // (the last allocator request whose Near is the swing-id site is the fallback's own page).
        var fallbackPage = alloc.Requests.Last(r => r.Near == Offsets.FnSwingPrepIdCopy).Got;
        Assert.Equal(ExtendedCatalog.FirstExtendedId, BitConverter.ToInt32(f.Read(fallbackPage + 0x21, 4), 0));
        Assert.Equal(ExtendedCatalog.FirstExtendedId + inv.Items.Count - 1, BitConverter.ToInt32(f.Read(fallbackPage + 0x28, 4), 0));
        Assert.NotEqual(0x90, f.Bytes[Offsets.ShopBuilderLowByteDisp32]);   // shop table mirrored (LW-354)
        inv.StepShopSync();
        Assert.Equal(0xFE, f.Bytes[Offsets.ModuleBase + BitConverter.ToInt32(f.Read(Offsets.ShopBuilderLowByteDisp32, 4), 0) + 37 * 2]);   // vanilla half synced
        inv.BootArm(null);   // idempotent
        Assert.True(inv.Armed);
        // growth polish: the WP hold's extended-row resolver points inside the weapon-stat stub page
        Assert.True(ThunkStub.IsJmpRel32(f.Read(Offsets.ThunkWeaponStat, 5), Offsets.ThunkWeaponStat, out long stubPage));
        Assert.Equal(stubPage + ThunkStub.RowStubHeader, inv.WeaponRowAddr(261));
        Assert.Equal(0x0F, f.Bytes[inv.WeaponRowAddr(261) + Offsets.ItemStatsWpOff]);   // Power 15 sits at +4
        Assert.Equal(-1, inv.WeaponRowAddr(262));
        Assert.Equal(-1, inv.WeaponRowAddr(37));
    }

    /// <summary>LW-365 F1 (v1.2): the one-item Moonblade() fixture has lo == hi in the fallback
    /// stub's baked range, so a swapped lo/count could not be caught. TwoBlades() (ids 261, 262)
    /// pins both ends distinctly: +0x21..+0x24 must decode to lo (261), +0x28..+0x2B to hi (262).</summary>
    [Fact]
    public void Arms_two_items_and_bakes_lo_and_hi_into_the_fallback_page()
    {
        var f = VanillaImage();
        var alloc = new FakeNearAllocator();
        var inv = Build(f, TwoBlades(), allocator: alloc);

        inv.BootArm(null);

        Assert.True(inv.Armed, inv.Refusal);
        var fallbackPage = alloc.Requests.Last(r => r.Near == Offsets.FnSwingPrepIdCopy).Got;
        Assert.Equal(261, BitConverter.ToInt32(f.Read(fallbackPage + 0x21, 4), 0));
        Assert.Equal(262, BitConverter.ToInt32(f.Read(fallbackPage + 0x28, 4), 0));
    }

    [Fact]
    public void Boot_always_places_the_data_seed_a_loaded_save_replays_its_own_counts()
    {
        var f = VanillaImage();
        var sidecar = ExtendedBagSidecar.Load(Path.Combine(_dir, ExtendedBagSidecar.FileName));
        var hdrA = SaveEdgeTrackerTests.Header(1000);
        sidecar.RecordSave(SaveEdgeTracker.KeyFromHeader(hdrA), new Dictionary<int, int> { [261] = 3 });
        var inv = Build(f, Moonblade(), sidecar: sidecar);
        inv.BootArm(null);
        Assert.True(inv.Armed);
        Assert.Equal(1, f.Bytes[inv.BagCountBase + 261]);   // boot = the seed (no save is loaded yet)

        // The game applies save A: its bag holds the file's 261 counts (nothing for ours) ...
        f.Seed(inv.BagCountBase + 261, 0);
        inv.Tracker.OnApplied(hdrA);
        inv.StepBagSidecar(new FakeSparseMemory());
        Assert.Equal(3, f.Bytes[inv.BagCountBase + 261]);   // ... and the replay puts A's own count back

        // A save never seen before (slot B) gets the seed, never A's count.
        f.Seed(inv.BagCountBase + 261, 0);
        inv.Tracker.OnApplied(SaveEdgeTrackerTests.Header(2000));
        inv.StepBagSidecar(new FakeSparseMemory());
        Assert.Equal(1, f.Bytes[inv.BagCountBase + 261]);

        // The player buys one and saves: the serialize edge records 2 under the new key.
        f.Seed(inv.BagCountBase + 261, 2);
        var hdrB2 = SaveEdgeTrackerTests.Header(2600);
        inv.Tracker.OnSerialized(hdrB2, new Dictionary<int, int> { [261] = 2 });
        inv.StepBagSidecar(new FakeSparseMemory());
        Assert.True(ExtendedBagSidecar.Load(Path.Combine(_dir, ExtendedBagSidecar.FileName)).TryGetSave(SaveEdgeTracker.KeyFromHeader(hdrB2), out var rec) && rec[261] == 2);

        // A load that follows a save in the same window is drained FIRST, so the save that
        // came after it records the replayed state, not the file's zero.
        f.Seed(inv.BagCountBase + 261, 0);
        inv.Tracker.OnApplied(hdrA);
        inv.Tracker.OnSerialized(SaveEdgeTrackerTests.Header(1001), new Dictionary<int, int> { [261] = 3 });
        inv.StepBagSidecar(new FakeSparseMemory());
        Assert.Equal(3, f.Bytes[inv.BagCountBase + 261]);
    }

    [Fact]
    public void A_schema_1_sidecar_feeds_the_first_unknown_save_once_then_the_seed()
    {
        string path = Path.Combine(_dir, ExtendedBagSidecar.FileName);
        File.WriteAllText(path, "{\"version\":1,\"counts\":{\"261\":2}}");
        var f = VanillaImage();
        var inv = Build(f, Moonblade(), sidecar: ExtendedBagSidecar.Load(path));
        inv.BootArm(null);
        inv.Tracker.OnApplied(SaveEdgeTrackerTests.Header(10));
        inv.StepBagSidecar(new FakeSparseMemory());
        Assert.Equal(2, f.Bytes[inv.BagCountBase + 261]);   // the pre-LW-353 count, once
        inv.Tracker.OnApplied(SaveEdgeTrackerTests.Header(20));
        inv.StepBagSidecar(new FakeSparseMemory());
        Assert.Equal(1, f.Bytes[inv.BagCountBase + 261]);   // then the seed
        Assert.Null(ExtendedBagSidecar.Load(path).TakeLegacy());  // and the file no longer carries it
    }

    [Fact]
    public void Nothing_shipped_means_off_with_no_refusal_and_no_writes()
    {
        var f = VanillaImage();
        var inv = Build(f, new ExtendedInventoryData.LoadResult());
        Assert.Equal(Offsets.BagCountArray, inv.BagCountBase);   // before any arm attempt: the vanilla block
        inv.BootArm(null);
        Assert.False(inv.Armed);
        Assert.Null(inv.Refusal);
        Assert.Empty(f.Writes);
        Assert.Equal(-1, inv.WeaponRowAddr(261));   // unarmed: the WP hold gets no row
        Assert.Equal(Offsets.BagCountArray, inv.BagCountBase);   // still the vanilla block: nothing shipped, nothing armed
        var empty = Build(f, new ExtendedInventoryData.LoadResult { FolderPresent = true });
        empty.BootArm(null);
        Assert.False(empty.Armed);
        Assert.Empty(f.Writes);
    }

    [Fact]
    public void Bad_data_a_wrong_build_or_an_unreadable_image_refuse_before_any_write()
    {
        var f = VanillaImage();
        var bad = Build(f, new ExtendedInventoryData.LoadResult { FolderPresent = true, Errors = new[] { "id 261: no ItemWeaponData.xml row" } });
        bad.BootArm(null);
        Assert.Contains("did not validate", bad.Refusal);

        var patched = Build(f, Moonblade(), pe: () => new LandmarkReading(LandmarkVerdict.Mismatch, "expected X observed Y"));
        patched.BootArm(null);
        Assert.Contains("does not match", patched.Refusal);
        Assert.Contains("observed Y", patched.Refusal);

        var blind = Build(f, Moonblade(), pe: () => new LandmarkReading(LandmarkVerdict.Unreadable));
        blind.BootArm(null);
        Assert.Contains("build-key", blind.Refusal);

        var noHooks = Build(f, Moonblade(), hooks: h => h == null ? "the game-hooks helper mod (reloaded.sharedlib.hooks) is not loaded" : null);
        noHooks.BootArm(null);
        Assert.Contains("sharedlib.hooks", noHooks.Refusal);
        Assert.False(noHooks.Armed);
        // Every IMAGE byte is back to vanilla (the leaked stub pages outside the image are by design).
        var vanilla = VanillaImage().Bytes;
        Assert.Equal(vanilla, f.Bytes.Where(kv => vanilla.ContainsKey(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value));
    }

    [Fact]
    public void A_refusal_late_in_the_sequence_rolls_every_earlier_step_back()
    {
        var f = VanillaImage();
        var before = Snapshot(f);
        var inv = Build(f, Moonblade(), hooks: _ => "order-rebuild: 0x140285DF0 does not carry the expected prologue");
        inv.BootArm(null);
        Assert.False(inv.Armed);
        Assert.Contains("prologue", inv.Refusal);
        Assert.Equal(before, f.Bytes.Where(kv => before.ContainsKey(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value));
        // LW-368 round 2: the list relocation itself succeeds before the order-rebuild hook
        // refuses, then RollBack restores it, so BagCountBase falls back to the vanilla block.
        Assert.Equal(Offsets.BagCountArray, inv.BagCountBase);
        Assert.Equal(0, f.Bytes[inv.BagCountBase + 261]);   // never seeded (still the vanilla zero-fill)
    }

    /// <summary>LW-365: the swing-id fallback installs before the hooks step (Arm.cs order), so a
    /// hooks refusal must roll it back too, in RollBack's reverse order (right before the clones
    /// loop) -- the site goes back to its original 7-byte movzx, never left mid-jump.</summary>
    [Fact]
    public void A_hooks_refusal_restores_the_swing_id_fallback_site_to_its_original_seven_bytes()
    {
        var f = VanillaImage();
        var inv = Build(f, Moonblade(), hooks: _ => "order-rebuild: 0x140285DF0 does not carry the expected prologue");
        inv.BootArm(null);
        Assert.False(inv.Armed);
        Assert.Equal(SwingIdFallbackHook.ExpectedSite, f.Read(Offsets.FnSwingPrepIdCopy, SwingIdFallbackHook.ExpectedSite.Length));
    }

    /// <summary>U6 (v1.1 strengthened, LW-372): the D8 transaction is asserted on BYTES, not just
    /// the Armed flag. Drives the REAL <see cref="ListBuilderHook.Install"/> (not a canned refusal
    /// string) against a genuinely corrupted prologue, so this also proves Install's own prologue
    /// check is what refuses. TemplateRelocation's cap sites are already widened (255/256) by the
    /// time the hooks step runs (Install order: cap patches -&gt; list relocation -&gt; template
    /// relocation -&gt; catalog -&gt; shops -&gt; clones -&gt; hooks), so RollBack must put them back to
    /// TRUE vanilla, not the LW-371 149/150 waypoint.</summary>
    [Fact]
    public void A_list_builder_prologue_mismatch_refuses_and_the_cap_sites_read_vanilla_bytes()
    {
        var f = VanillaImage();
        var badPrologue = (byte[])ListBuilderHook.ExpectedPrologue.Clone();
        badPrologue[0] = 0x90;
        f.Seed(Offsets.FnListBuilder, badPrologue);
        // The null-forgiving `h!` is safe here: badPrologue makes ShouldArm fail inside Install
        // before Install ever touches `hooks`, so the null BootArm(null) passes through this
        // lambda is never dereferenced.
        var inv = Build(f, Moonblade(), hooks: h => new ListBuilderHook(f).Install(h!));

        inv.BootArm(null);

        Assert.False(inv.Armed);
        Assert.Contains("list-builder", inv.Refusal);
        Assert.Contains("prologue", inv.Refusal);
        Assert.Equal((byte)0x91, f.Bytes[Offsets.ListBuilderCapByte]);
        Assert.Equal(new byte[] { 0x92, 0x00, 0x00, 0x00 }, f.Read(Offsets.ListInsertBoundByte, 4));
    }

    [Fact]
    public void A_moved_thunk_or_a_foreign_cap_byte_refuses_and_restores()
    {
        var f = VanillaImage();
        f.Seed(Offsets.ThunkSpritePair, 0x48, 0x83, 0xEC, 0x28, 0x44);   // not an E9 thunk any more
        var before = Snapshot(f);
        var inv = Build(f, Moonblade());
        inv.BootArm(null);
        Assert.False(inv.Armed);
        Assert.Contains("sprite-pair", inv.Refusal);
        Assert.Equal(before, f.Bytes.Where(kv => before.ContainsKey(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value));

        var g = VanillaImage();
        g.Seed(0x140285E2D, 0x06);   // the research marker still armed beside us
        var inv2 = Build(g, Moonblade());
        inv2.BootArm(null);
        Assert.False(inv2.Armed);
        Assert.Contains("default-order table-scan guard", inv2.Refusal);
        Assert.Empty(g.Writes);
    }

    /// <summary>LW-368 round 2 (T10): a field the list relocation owns is already moved. The
    /// relocation step runs right after the boot cap patches, so its refusal must roll THOSE
    /// back too, not just leave its own 55 fields untouched -- "nothing earlier remains applied".</summary>
    [Fact]
    public void A_relocation_field_refusal_rolls_back_the_boot_cap_patches_too()
    {
        var f = VanillaImage();
        f.Seed(ListRelocation.Sites[0].Addr, BitConverter.GetBytes(ListRelocation.Sites[0].Vanilla + 1));   // already moved
        var before = Snapshot(f);
        var inv = Build(f, Moonblade());

        inv.BootArm(null);

        Assert.False(inv.Armed);
        Assert.Contains("list-relocation", inv.Refusal);
        Assert.Equal(before, f.Bytes.Where(kv => before.ContainsKey(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value));
        // The boot cap patches ran and succeeded before the relocation step; RollBack must undo
        // them too, so none of ExtendedSites.BootPatches(1) is left applied.
        foreach (var p in ExtendedSites.BootPatches(1)) Assert.Equal(p.Old, f.Bytes[p.Addr]);
        Assert.Equal(Offsets.BagCountArray, inv.BagCountBase);
        Assert.Equal(0, f.Bytes[inv.BagCountBase + 261]);   // SeedBag never ran (still the vanilla zero-fill)
    }

    /// <summary>LW-371 (T9d): a field the TEMPLATE relocation owns is already moved. Its Install
    /// step runs right after the list relocation (D2), so its refusal must roll THAT back too
    /// (and the boot cap patches before it) -- the same "nothing earlier remains applied"
    /// contract T10 pins for the list relocation, one link further down the chain.</summary>
    [Fact]
    public void A_moved_template_slot_refuses_the_whole_arm_and_rolls_back_byte_identically()
    {
        var f = VanillaImage();
        f.Seed(TemplateRelocation.Slots[0].Addr, BitConverter.GetBytes(TemplateRelocation.Slots[0].Vanilla + 1));   // already moved
        var before = Snapshot(f);
        var inv = Build(f, Moonblade());

        inv.BootArm(null);

        Assert.False(inv.Armed);
        Assert.Contains("template-relocation", inv.Refusal);
        Assert.Equal(before, f.Bytes.Where(kv => before.ContainsKey(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value));
        // Everything that ran before the template relocation -- the boot cap patches AND the list
        // relocation, both of which succeed on a vanilla image -- must be rolled back too.
        foreach (var p in ExtendedSites.BootPatches(1)) Assert.Equal(p.Old, f.Bytes[p.Addr]);
        foreach (var site in ListRelocation.Sites) Assert.Equal(BitConverter.GetBytes(site.Vanilla), f.Read(site.Addr, 4));
        Assert.Equal(Offsets.BagCountArray, inv.BagCountBase);
        Assert.Equal(0, f.Bytes[inv.BagCountBase + 261]);   // SeedBag never ran (still the vanilla zero-fill)
        Assert.Equal(TemplateSeat.WeaponRegions, inv.TemplateRegions);   // the vanilla pair, never the page
    }

    [Fact]
    public void Post_load_caps_settle_once_their_pages_read_vanilla_and_the_bag_is_untouched_without_an_edge()
    {
        var f = VanillaImage();
        var inv = Build(f, Moonblade());
        inv.StepPostLoadCaps();   // before arming: nothing
        Assert.False(inv.CapsSettled);
        inv.BootArm(null);
        Assert.True(inv.Armed);
        inv.StepPostLoadCaps();   // pages still copy-protected (unreadable): still waiting
        Assert.False(inv.CapsSettled);
        f.Seed(0x14F2EA40F, 0x06);
        f.Seed(0x14F45D315, 0x5E);
        inv.StepPostLoadCaps();
        Assert.True(inv.CapsSettled);
        Assert.Equal(0x07, f.Bytes[0x14F2EA40F]);
        Assert.Equal(0x5F, f.Bytes[0x14F45D315]);

        // LW-353: with no save edge pending, the tick writes nothing (a change in the bag is
        // never recorded on its own any more; only a save edge records, only a load edge replays).
        var mem = new FakeSparseMemory();
        f.Seed(inv.BagCountBase + 261, 2);
        inv.StepBagSidecar(mem);
        string path = Path.Combine(_dir, ExtendedBagSidecar.FileName);
        Assert.False(File.Exists(path));
        Assert.Equal(2, f.Bytes[inv.BagCountBase + 261]);
    }

    [Fact]
    public void Engine_builds_and_ticks_with_an_unarmed_extended_inventory()
    {
        ModLogger.UseNullLogger();
        using var temp = TempDirs.Create("lw_engine_ext_");
        var f = VanillaImage();
        var inv = Build(f, Moonblade(), pe: () => new LandmarkReading(LandmarkVerdict.Unreadable));
        var engine = new Engine(EngineTests.NestedModDir(temp), mem: EngineTests.HealthyMemory(), notice: (_, __) => { }, extended: inv);
        engine.InjectHooks(null);
        Assert.False(inv.Armed);
        Assert.Contains("build-key", inv.Refusal);
        for (int i = 0; i < 40; i++) engine.Tick();   // the two rows run and no-op
        Assert.Empty(f.Writes);
        Assert.Contains(engine.Phases, p => p.Name == "extended-caps");
        Assert.Contains(engine.Phases, p => p.Name == "extended-bag");
    }
}
