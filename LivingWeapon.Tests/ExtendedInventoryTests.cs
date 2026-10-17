using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LivingWeapon;
using Reloaded.Hooks.Definitions;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>LW-346 S4: the boot-arm transaction over the fakes: a vanilla 1.5.2 process image
/// (every patch site's old byte, the ten accessor function entries with their real bytes, the catalog disp32 and
/// the five "+" records) either arms completely or is left byte-for-byte untouched.</summary>
public class ExtendedInventoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lw_ext_inv_" + Guid.NewGuid().ToString("N"));
    public ExtendedInventoryTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    // A property (LW-388 R5): the addresses are rebased pins, so a static readonly array would
    // freeze whatever base was live when the class first loaded.
    // LW-388 R12: built FROM the production table so the fixture cannot drift from it; each
    // entry seeds its real function-entry bytes (the Denuvo-free exe on disk 2026-10-16).
    private static (long Addr, byte[] Bytes)[] Thunks =>
        new[] { ExtendedDonorThunks.WeaponStat }
            .Concat(ExtendedDonorThunks.DonorThunks.Select(t => t.Entry))
            .Select(e => (e.Addr, e.Expected)).ToArray();

    /// <summary>A vanilla 1.5.2 image as far as the boot arm looks (bytes read on disk 2026-08-27).
    /// LW-368 round 2 / round 2b: also seeds all 55 list-relocation fields and the two 0x110-byte
    /// old blocks (bag counts + sibling flags) at their vanilla values, so BootArm still arms end
    /// to end. LW-371: also seeds the ten template-relocation fields and the three old chart spans
    /// (each an empty chart: the 0x00FF marker at word 0, zeros after) -- without this,
    /// <c>_templates.Install</c> refuses on the unreadable/wrong old chart spans and every
    /// BootArm-based test fails <c>Assert.True(inv.Armed)</c>.</summary>
    ///
    /// LW-388: staged at the LIVE addresses of whatever <see cref="ImageBase"/> is current, so a
    /// test that calls <see cref="ImageBase.Set"/> first gets a relocated image. Offsets.* are
    /// LIVE already and seed as-is; the PIN-space tables (the boot sites, ListRelocation.Sites,
    /// TemplateRelocation.RipFields) seed at ImageBase.At(pin). The boot sites are read at the
    /// PREFERRED base (<see cref="PinBootPatches"/>), never through ExtendedSites.Build's own
    /// rebase, so a Build that forgot ImageBase.At would stage and read the same wrong address and
    /// hide the bug; this way it reads an unstaged pin and refuses.</summary>
    internal static FakeCodePatcher VanillaImage()
    {
        var f = new FakeCodePatcher();
        foreach (var p in PinBootPatches(1)) f.Seed(ImageBase.At(p.Addr), p.Old);
        foreach (var (addr, bytes) in Thunks) f.Seed(addr, bytes);
        f.Seed(Offsets.FnSwingPrepIdCopy, SwingIdFallbackHook.ExpectedSite);   // LW-365
        f.Seed(Offsets.ExtCatalogDisp32, 0x10, 0xF9, 0x67, 0x00);
        ShopFlagsMirrorTests.SeedVanillaShopSites(f);
        for (int id = ExtendedCatalog.DlcLo; id <= ExtendedCatalog.DlcHi; id++)
        {
            var rec = new byte[12]; rec[0] = (byte)id;
            f.Seed(Offsets.ExtCatalogBase + (long)id * 12, rec);
        }
        foreach (var site in ListRelocation.Sites) f.Seed(ImageBase.At(site.Addr), BitConverter.GetBytes(site.Vanilla));
        f.Seed(Offsets.BagCountArray, new byte[ListRelocation.BlockBytes]);
        f.Seed(Offsets.SiblingListArray, new byte[ListRelocation.BlockBytes]);
        foreach (var s in TemplateRelocation.Slots) f.Seed(s.Addr, BitConverter.GetBytes(s.Vanilla));
        foreach (var rf in TemplateRelocation.RipFields) f.Seed(ImageBase.At(rf.Addr), BitConverter.GetBytes(rf.Vanilla));
        foreach (var c in TemplateRelocation.CapSites) f.Seed(c.Addr, c.Vanilla);
        foreach (var chart in TemplateRelocation.Charts) f.Seed(chart.OldBase, EmptyChart(chart.SpanBytes));
        return f;
    }

    /// <summary>ExtendedSites.BootPatches as PIN-space records: built with the preferred base in
    /// effect, then the live base is put back. ImageBase is process-wide static state; the suite
    /// runs serially (TestLoggingSetup.cs), so the brief swap is invisible to other tests.</summary>
    internal static List<BytePatch> PinBootPatches(int n)
    {
        long live = ImageBase.Base;
        ImageBase.ResetForTests();
        try { return ExtendedSites.BootPatches(n); }
        finally { if (live != ImageBase.Preferred) ImageBase.Set(live); }
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

    private ExtendedInventory Build(FakeCodePatcher f, ExtendedInventoryData.LoadResult data,
        Func<IReloadedHooks?, string?>? hooks = null, ExtendedBagSidecar? sidecar = null,
        FakeNearAllocator? allocator = null)
        => new(f, allocator ?? new FakeNearAllocator(), data,
            sidecar ?? ExtendedBagSidecar.Load(Path.Combine(_dir, ExtendedBagSidecar.FileName)),
            hooks ?? (_ => null));

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
            var now = f.Read(addr, original.Length);
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
        Assert.Equal(0xFE, f.Bytes[ImageBase.Base + BitConverter.ToInt32(f.Read(Offsets.ShopBuilderLowByteDisp32, 4), 0) + 37 * 2]);   // vanilla half synced
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
    public void Bad_data_or_a_missing_hooks_helper_refuse_before_any_write()
    {
        var f = VanillaImage();
        var bad = Build(f, new ExtendedInventoryData.LoadResult { FolderPresent = true, Errors = new[] { "id 261: no ItemWeaponData.xml row" } });
        bad.BootArm(null);
        Assert.Contains("did not validate", bad.Refusal);

        var noHooks = Build(f, Moonblade(), hooks: h => h == null ? "the game-hooks helper mod (reloaded.sharedlib.hooks) is not loaded" : null);
        noHooks.BootArm(null);
        Assert.Contains("sharedlib.hooks", noHooks.Refusal);
        Assert.False(noHooks.Armed);
        // Every IMAGE byte is back to vanilla (the leaked stub pages outside the image are by design).
        var vanilla = VanillaImage().Bytes;
        Assert.Equal(vanilla, f.Bytes.Where(kv => vanilla.ContainsKey(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value));
    }

    /// <summary>LW-397: the PE build key no longer gates the arm, so a game whose code moved is
    /// refused by the per-site old-byte check alone. End to end: corrupt the FIRST boot patch's old
    /// byte (read from the real table, so the test cannot drift from it), arm, and see the one-line
    /// refusal naming the site and not one byte left written. Sibling proofs: the unit-level
    /// refusal text (BytePatchSetTests.cs:48) and the unreadable-site refusal
    /// (Boot_arm_refuses_cleanly_when_the_image_is_relocated_but_the_base_is_not_resolved).</summary>
    [Fact]
    public void A_moved_patch_site_refuses_the_whole_arm_on_its_old_byte_alone()
    {
        var f = VanillaImage();
        var p = ExtendedSites.BootPatches(1)[0];   // the count-getter cap, live-rebased like BytePatchSet reads it
        Assert.Equal((byte)0x01, p.Old);
        f.Seed(p.Addr, 0x07);   // the byte the moved site would read instead
        var before = Snapshot(f);
        var inv = Build(f, Moonblade());

        inv.BootArm(null);

        Assert.False(inv.Armed);
        Assert.Contains($"{p.Label}: 0x{p.Addr:X} reads 07, expected {p.Old:X2}", inv.Refusal);
        Assert.Equal(before, f.Bytes.Where(kv => before.ContainsKey(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value));
        Assert.Equal((byte)0x07, f.Bytes[p.Addr]);
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
        // The wrong-world case: the nine bytes the old Denuvo exe carries at the sprite-pair address
        // (an E9 thunk plus the next function's bytes), not a partial match of the real entry.
        f.Seed(Offsets.ThunkSpritePair, 0xE9, 0x5B, 0xF2, 0xC0, 0x0F, 0xE3, 0xCF, 0x77, 0x56);
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

    /// <summary>LW-388 (R2): the two damage caps are plain boot sites on the Denuvo-free build
    /// (re-found 2026-10-16 in its .text: the eighth hand-resolver copy, disp8 at 0x14027F002 with
    /// two ThunkWeaponStat calls following, and the swing publisher's lea r14d,[rbp+6], disp8 at
    /// 0x1402822A4; formerly the protected-tail 0x14F2EA40F / 0x14F45D315). BootArm widens both
    /// (VanillaImage seeds 0x06 there through the boot table), nothing waits for a save, and the
    /// post-load step has nothing left to do.</summary>
    [Fact]
    public void Damage_caps_widen_at_boot_and_the_bag_is_untouched_without_an_edge()
    {
        var f = VanillaImage();
        Assert.Equal(0x06, f.Bytes[ImageBase.At(0x14027F002L)]);
        Assert.Equal(0x06, f.Bytes[ImageBase.At(0x1402822A4L)]);
        var inv = Build(f, Moonblade());
        inv.BootArm(null);
        Assert.True(inv.Armed, inv.Refusal);
        Assert.Equal(0x07, f.Bytes[ImageBase.At(0x14027F002L)]);
        Assert.Equal(0x07, f.Bytes[ImageBase.At(0x1402822A4L)]);
        Assert.Empty(inv.PendingCaps);
        int writes = f.Writes.Count;
        inv.StepPostLoadCaps();   // nothing pending: a no-op
        Assert.True(inv.CapsSettled);
        Assert.Equal(writes, f.Writes.Count);

        // LW-353: with no save edge pending, the tick writes nothing (a change in the bag is
        // never recorded on its own any more; only a save edge records, only a load edge replays).
        var mem = new FakeSparseMemory();
        f.Seed(inv.BagCountBase + 261, 2);
        inv.StepBagSidecar(mem);
        string path = Path.Combine(_dir, ExtendedBagSidecar.FileName);
        Assert.False(File.Exists(path));
        Assert.Equal(2, f.Bytes[inv.BagCountBase + 261]);
    }

    // ---- LW-388: the boot arm on an ASLR-relocated image ----

    /// <summary>A plausible ASLR base (64 KB aligned, in the 0x7FF6... range the owner's 2026-10-16
    /// pass saw: 0x7FF65E0F0000); the same value the ImageBase and LaunchGuard suites use.</summary>
    private const long RelocatedBase = 0x7FF6_1234_0000L;
    /// <summary>SizeOfImage of the Denuvo-free 1.5.2 exe (LaunchGuard.ExpectedSizeOfImage, read from
    /// the on-disk PE, docs/research/PORT_1.5.2_NODRM_OFFSETS.md).</summary>
    private const long SizeOfImage = 0x03F9E000L;

    /// <summary>LOAD-BEARING (LW-388 A10/R4): with the image at a random base and every vanilla byte
    /// staged at its LIVE address, BootArm arms end to end; every write lands inside the relocated
    /// image or on a page the allocator granted (none at a preferred-base pin); the allocator was
    /// asked for pages near the LIVE image base and near each LIVE thunk; and the catalog's
    /// image-relative disp32 is measured from the live base. Each of the plan's three verifier
    /// breaks fails this test: ListRelocation.Distance's image-relative branch reverted to the
    /// preferred base (out of int32 reach), TemplateRelocation reading RipField.Addr without
    /// ImageBase.At (unreadable pin), ExtendedSites.Build emitting the pin Addr (unreadable pin).</summary>
    [Fact]
    public void Boot_arm_arms_on_a_relocated_image_and_writes_only_live_addresses()
    {
        try
        {
            Assert.True(ImageBase.Set(RelocatedBase));
            var f = VanillaImage();
            var alloc = new FakeNearAllocator { Mode = FakeAllocMode.RelativeToNear };
            var inv = Build(f, TwoBlades(), allocator: alloc);

            inv.BootArm(null);

            Assert.True(inv.Armed, inv.Refusal);
            Assert.Null(inv.Refusal);
            Assert.NotEmpty(f.Writes);
            var pages = alloc.Requests.Where(r => r.Got != 0).ToList();
            foreach (var (addr, data) in f.Writes)
            {
                bool inImage = addr >= ImageBase.Base && addr + data.Length <= ImageBase.Base + SizeOfImage;
                bool onPage = pages.Any(p => addr >= p.Got && addr + data.Length <= p.Got + p.Size);
                Assert.True(inImage || onPage, $"write at 0x{addr:X} is neither in the relocated image nor on a granted page");
                Assert.False(addr >= ImageBase.Preferred && addr < ImageBase.Preferred + SizeOfImage,
                    $"write at 0x{addr:X} landed on a preferred-base pin");
            }

            var nears = alloc.Requests.Select(r => r.Near).ToHashSet();
            Assert.Contains(ImageBase.Base, nears);
            foreach (var (thunk, _) in Thunks) Assert.Contains(thunk, nears);   // Offsets.Thunk* are LIVE
            Assert.Contains(ImageBase.At(0x1402B8C74L), nears);   // ThunkWeaponStat's documented pin, rebased
            Assert.Contains(Offsets.FnSwingPrepIdCopy, nears);
            Assert.All(nears, n => Assert.True(n >= ImageBase.Base, $"allocation requested near pin-space 0x{n:X}"));

            long catalog = alloc.Requests.Single(r => r.Size == ExtendedCatalog.BufferSize).Got;
            Assert.Equal(catalog - ImageBase.Base, BitConverter.ToInt32(f.Read(Offsets.ExtCatalogDisp32, 4), 0));

            // the widened caps (incl. the two LW-388 damage caps) sit at their LIVE addresses
            foreach (var p in PinBootPatches(2)) Assert.Equal(p.New, f.Bytes[ImageBase.At(p.Addr)]);
            Assert.Equal(0x08, f.Bytes[ImageBase.At(0x14027F002L)]);
            Assert.Equal(0x08, f.Bytes[ImageBase.At(0x1402822A4L)]);
            // the two relocated lists: every rip field measures from its live address, every
            // image-relative field from the live base
            Assert.NotEqual(Offsets.BagCountArray, inv.BagCountBase);
            foreach (var site in ListRelocation.Sites)
            {
                int now = BitConverter.ToInt32(f.Read(ImageBase.At(site.Addr), 4), 0);
                long target = site.Kind == ListRelocation.SiteKind.RipLea
                    ? ImageBase.At(site.Addr) + 4 + site.Trail + now
                    : ImageBase.Base + now;
                long listBase = site.List == ListRelocation.ListId.Count ? inv.BagCountBase : inv.BagCountBase + ListRelocation.SiblingPageOffset;
                Assert.Equal(listBase + site.Off, target);
            }
            Assert.Equal(1, f.Bytes[inv.BagCountBase + 261]);   // the data seed landed on the page
        }
        finally { ImageBase.ResetForTests(); }
    }

    /// <summary>The mirror (the symptom LW-388 fixes): the same relocated staging, but the mod
    /// never learned the live base, so it reads the preferred pins, finds nothing there, and
    /// refuses at the very first read (the count-getter cap) without writing a byte.</summary>
    [Fact]
    public void Boot_arm_refuses_cleanly_when_the_image_is_relocated_but_the_base_is_not_resolved()
    {
        try
        {
            Assert.True(ImageBase.Set(RelocatedBase));
            var f = VanillaImage();   // staged at the relocated addresses
            ImageBase.ResetForTests();   // the mod never learned the live base
            var inv = Build(f, Moonblade(), allocator: new FakeNearAllocator { Mode = FakeAllocMode.RelativeToNear });

            inv.BootArm(null);

            Assert.False(inv.Armed);
            Assert.Contains("count-getter cap", inv.Refusal);
            Assert.Contains("is unreadable", inv.Refusal);
            Assert.Empty(f.Writes);
        }
        finally { ImageBase.ResetForTests(); }
    }

    /// <summary>R1 in test form: a page from the far bottom of the 2 GB window below the image
    /// (what the old bottom-up allocator scan picked under ASLR, where everything below the image
    /// is free) is out of int32 reach of the rip-relative list fields, so the arm refuses with the
    /// reach line and rolls every boot cap back. CheckReach stays the refusal net.</summary>
    [Fact]
    public void Boot_arm_refuses_a_page_out_of_int32_reach_below_a_relocated_image()
    {
        try
        {
            Assert.True(ImageBase.Set(RelocatedBase));
            var f = VanillaImage();
            var inv = Build(f, Moonblade(), allocator: new FakeNearAllocator { Mode = FakeAllocMode.FarBelowNear });

            inv.BootArm(null);

            Assert.False(inv.Armed);
            Assert.Contains("out of int32 reach", inv.Refusal);
            foreach (var p in PinBootPatches(1)) Assert.Equal(p.Old, f.Bytes[ImageBase.At(p.Addr)]);
            Assert.Equal(Offsets.BagCountArray, inv.BagCountBase);
        }
        finally { ImageBase.ResetForTests(); }
    }

    [Fact]
    public void Engine_builds_and_ticks_with_an_unarmed_extended_inventory()
    {
        ModLogger.UseNullLogger();
        using var temp = TempDirs.Create("lw_engine_ext_");
        var f = VanillaImage();
        f.Seed(ExtendedSites.BootPatches(1)[0].Addr, 0x07);   // LW-397: a moved first boot site refuses before any write
        var inv = Build(f, Moonblade());
        var engine = new Engine(EngineTests.NestedModDir(temp), mem: EngineTests.HealthyMemory(), notice: (_, __) => { }, extended: inv);
        engine.InjectHooks(null);
        Assert.False(inv.Armed);
        Assert.Contains("reads 07, expected 01", inv.Refusal);
        for (int i = 0; i < 40; i++) engine.Tick();   // the two rows run and no-op
        Assert.Empty(f.Writes);
        Assert.Contains(engine.Phases, p => p.Name == "extended-caps");
        Assert.Contains(engine.Phases, p => p.Name == "extended-bag");
    }

    [Fact]
    public void StepBagSidecar_reports_a_drained_load_once()
    {
        var f = VanillaImage();
        var inv = Build(f, Moonblade());
        inv.BootArm(null);
        Assert.True(inv.Armed);
        inv.Tracker.OnApplied(SaveEdgeTrackerTests.Header(10));
        Assert.True(inv.StepBagSidecar(new FakeSparseMemory()));
        Assert.False(inv.StepBagSidecar(new FakeSparseMemory()));

        var unarmed = VanillaImage();
        unarmed.Seed(ExtendedSites.BootPatches(1)[0].Addr, 0x07);
        var refused = Build(unarmed, Moonblade());
        refused.BootArm(null);
        refused.Tracker.OnApplied(SaveEdgeTrackerTests.Header(10));
        Assert.False(refused.StepBagSidecar(new FakeSparseMemory()));
    }

    [Fact]
    public void A_drained_save_load_invalidates_the_card_painter()
    {
        ModLogger.UseNullLogger();
        using var temp = TempDirs.Create("lw_engine_load_");
        var f = VanillaImage();
        var inv = Build(f, Moonblade());
        inv.BootArm(null);
        Assert.True(inv.Armed);
        var engine = new Engine(EngineTests.NestedModDir(temp), mem: EngineTests.HealthyMemory(), notice: (_, __) => { }, extended: inv);
        var row = engine.Phases.First(p => p.Name == "extended-bag");

        engine.DisplayForTest._poolLocator.SeedForTest((0x50_0000_0000L, 4096));
        Assert.False(engine.DisplayForTest._poolLocator.RegionsStale);
        inv.Tracker.OnApplied(SaveEdgeTrackerTests.Header(10));
        row.Run(new TickPhaseState());
        Assert.True(engine.DisplayForTest._poolLocator.RegionsStale);

        engine.DisplayForTest._poolLocator.SeedForTest((0x50_0000_0000L, 4096));
        row.Run(new TickPhaseState());
        Assert.False(engine.DisplayForTest._poolLocator.RegionsStale);
    }
}
