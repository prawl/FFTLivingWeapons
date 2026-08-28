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

    /// <summary>A vanilla 1.5.2 image as far as the boot arm looks (bytes read on disk 2026-08-27).</summary>
    private static FakeCodePatcher VanillaImage()
    {
        var f = new FakeCodePatcher();
        foreach (var p in ExtendedSites.BootPatches(1)) f.Seed(p.Addr, p.Old);
        foreach (var (addr, bytes) in Thunks) f.Seed(addr, bytes);
        f.Seed(Offsets.ExtCatalogDisp32, 0x10, 0xF9, 0x67, 0x00);
        ShopFlagsMirrorTests.SeedVanillaShopSites(f);
        for (int id = ExtendedCatalog.DlcLo; id <= ExtendedCatalog.DlcHi; id++)
        {
            var rec = new byte[12]; rec[0] = (byte)id;
            f.Seed(Offsets.ExtCatalogBase + (long)id * 12, rec);
        }
        return f;
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

    private static Func<LandmarkReading> Match => () => new LandmarkReading(LandmarkVerdict.Match);

    private ExtendedInventory Build(FakeCodePatcher f, ExtendedInventoryData.LoadResult data,
        Func<LandmarkReading>? pe = null, Func<IReloadedHooks?, string?>? hooks = null, ExtendedBagSidecar? sidecar = null)
        => new(f, new FakeNearAllocator(), data,
            sidecar ?? ExtendedBagSidecar.Load(Path.Combine(_dir, ExtendedBagSidecar.FileName)),
            pe ?? Match, hooks ?? (_ => null));

    [Fact]
    public void Arms_the_whole_set_over_a_vanilla_image_and_seeds_the_bag()
    {
        var f = VanillaImage();
        var inv = Build(f, Moonblade());
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
        Assert.Equal(1, f.Bytes[Offsets.BagCountArray + 261]);   // the data seed (no sidecar entry)
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

    [Fact]
    public void The_sidecar_count_beats_the_data_seed_at_boot()
    {
        var f = VanillaImage();
        var sidecar = ExtendedBagSidecar.Load(Path.Combine(_dir, ExtendedBagSidecar.FileName));
        sidecar.Update(new Dictionary<int, int> { [261] = 3 });
        var inv = Build(f, Moonblade(), sidecar: sidecar);
        inv.BootArm(null);
        Assert.True(inv.Armed);
        Assert.Equal(3, f.Bytes[Offsets.BagCountArray + 261]);
    }

    [Fact]
    public void Nothing_shipped_means_off_with_no_refusal_and_no_writes()
    {
        var f = VanillaImage();
        var inv = Build(f, new ExtendedInventoryData.LoadResult());
        inv.BootArm(null);
        Assert.False(inv.Armed);
        Assert.Null(inv.Refusal);
        Assert.Empty(f.Writes);
        Assert.Equal(-1, inv.WeaponRowAddr(261));   // unarmed: the WP hold gets no row
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
        Assert.False(f.Bytes.ContainsKey(Offsets.BagCountArray + 261));   // never seeded
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

    [Fact]
    public void Post_load_caps_settle_once_their_pages_read_vanilla_and_the_bag_sidecar_follows_changes()
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

        var mem = new FakeSparseMemory();
        mem.U8s[Offsets.BagCountArray + 261] = 1;
        inv.StepBagSidecar(mem);
        string path = Path.Combine(_dir, ExtendedBagSidecar.FileName);
        Assert.False(File.Exists(path));   // unchanged since boot: no write
        mem.U8s[Offsets.BagCountArray + 261] = 2;
        inv.StepBagSidecar(mem);
        Assert.True(File.Exists(path));
        Assert.Equal(2, ExtendedBagSidecar.Load(path).ResolveBootCount(261, 1));
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
