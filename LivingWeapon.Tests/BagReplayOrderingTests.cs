using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-351 fix rounds 4 and 5: what the mod has to put back the instant a save finishes loading.
///
/// Round 4 (the owner's re-test 4, 2026-08-30) established the first half: the load routine copies
/// the save file's bag over the game's count array and that file carries ids 0..260, so every
/// extended id reads zero the moment the load returns and every menu that asks whether the player
/// owns one answers no. The replay therefore also runs inside the load detour, on the game's own
/// thread, right after the original returns, with the tick as an idempotent fallback.
///
/// Round 5 corrected round 4's explanation of WHY that was not enough. Re-test 5 replayed three
/// Terrastaffs before the game could look and the item was still in neither equip menu; the
/// closing disassembly showed the menu order templates are not rebuilt from the item data during a
/// load at all: the same load routine RESTORES both byte-for-byte out of the save struct, and
/// the serializer writes them back, so they are save state, and a save written before an id ever
/// seated restores a table that will never name it. So the detour now seats owned extended ids in
/// those templates too (<see cref="TemplateSeat"/>), in the same moment, after the restore.
///
/// These tests drive <see cref="SaveEdgeHooks.AfterApply"/> directly (the detour body minus its
/// native trampoline, the seam ReadHeader is already tested through) over a fake process image,
/// and compare the two orders of events.
/// </summary>
public class BagReplayOrderingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lw_bag_replay_" + Guid.NewGuid().ToString("N"));
    public BagReplayOrderingTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private const int Moon = 261, Terra = 262;
    private const long StructAddr = 0x1F0000000L;

    /// <summary>The live arc's shape: two contiguous extended weapons, seeds 1 and 0.</summary>
    private static ExtendedInventoryData.LoadResult TwoItems() => new()
    {
        FolderPresent = true,
        Items = new[]
        {
            new ExtendedItemDef
            {
                Id = Moon, Name = "Moonblade", Category = "Sword", CloneDonor = 37, ArtDonor = 37, SeedCount = 1,
                CatalogRecord = new byte[] { 0x04, 0x16, 0x00, 0x80, 0x25, 0x03, 0x00, 0x00, 0x0A, 0x00, 0x00, 0x00 },
                WeaponRow = new byte[] { 0x01, 0x8E, 0x01, 0xFF, 0x0F, 0x00, 0x00, 0x00 },
            },
            new ExtendedItemDef
            {
                Id = Terra, Name = "Terrastaff", Category = "Pole", CloneDonor = 108, ArtDonor = 108, SeedCount = 0,
                CatalogRecord = new byte[] { 0x04, 0x17, 0x00, 0x80, 0xDC, 0x05, 0x00, 0x00, 0x0A, 0x00, 0x00, 0x00 },
                WeaponRow = new byte[] { 0x01, 0x8E, 0x01, 0xFF, 0x0C, 0x00, 0x00, 0x00 },
            },
        },
    };

    /// <summary>One armed inventory over its own fake image and its own sidecar file, with the
    /// save-edge hooks wired the way ExtendedInventory.DefaultInstallHooks wires them.</summary>
    private sealed class World
    {
        public FakeCodePatcher Image = null!;
        public ExtendedBagSidecar Sidecar = null!;
        public ExtendedInventory Inv = null!;
        public SaveEdgeHooks Hooks = null!;
        public string SidecarPath = "";

        public int Bag(int id) => Image.Bytes[Offsets.BagCountArray + id];
        public Dictionary<int, int> BagState() => Inv.Items.ToDictionary(i => i.Id, i => Bag(i.Id));

        /// <summary>The game just applied a loaded save: the bag now holds the file's counts,
        /// which carry nothing for the extended ids. <paramref name="detour"/> false is the
        /// pre-round-4 order (the edge is only published; the tick does all the work).</summary>
        public void GameApplied(byte[] header, bool detour)
        {
            foreach (var it in Inv.Items) Image.Seed(Offsets.BagCountArray + it.Id, 0);
            Image.Seed(Offsets.SaveStructPtr, BitConverter.GetBytes(StructAddr));
            Image.Seed(StructAddr + Offsets.SaveHeaderKeyOff, header);
            if (detour) Hooks.AfterApply(second: false);
            else Inv.Tracker.OnApplied(header);
        }

        public void Tick() => Inv.StepBagSidecar(new FakeSparseMemory());

        /// <summary>Round 5: give both weapon order templates a table of their own, holding
        /// <paramref name="words"/> and then the 0x00FF end marker. Without this the fake image
        /// has no template regions at all, the guarded read fails and the seat is a silent no-op
        /// (which is what keeps the round-4 tests above counting bag writes only).</summary>
        public void SeedTemplates(params int[] words)
        {
            foreach (var r in TemplateSeat.WeaponRegions)
            {
                var bytes = new byte[r.CapacityWords * 2];
                for (int i = 0; i < words.Length; i++)
                {
                    bytes[i * 2] = (byte)(words[i] & 0xFF);
                    bytes[i * 2 + 1] = (byte)((words[i] >> 8) & 0xFF);
                }
                bytes[words.Length * 2] = (byte)(TemplateSeat.EndMarker & 0xFF);
                bytes[words.Length * 2 + 1] = (byte)(TemplateSeat.EndMarker >> 8);
                Image.Seed(r.Addr, bytes);
            }
        }

        public ushort[] TemplateWords(long addr, int count)
        {
            var b = Image.Read(addr, count * 2);
            return Enumerable.Range(0, count).Select(i => (ushort)(b[i * 2] | (b[i * 2 + 1] << 8))).ToArray();
        }

        /// <summary>Writes that landed inside either order template (the bag writes are elsewhere).</summary>
        public int SeatWrites() => Image.Writes.Count(x =>
            TemplateSeat.WeaponRegions.Any(r => x.Addr >= r.Addr && x.Addr < r.Addr + r.CapacityWords * 2));
    }

    private World NewWorld(string tag, string? sidecarFile = null)
    {
        string path = Path.Combine(_dir, tag + "_" + ExtendedBagSidecar.FileName);
        if (sidecarFile != null) File.WriteAllText(path, sidecarFile);
        var f = ExtendedInventoryTests.VanillaImage();
        var sidecar = ExtendedBagSidecar.Load(path);
        var inv = new ExtendedInventory(f, new FakeNearAllocator(), TwoItems(), sidecar,
            () => new LandmarkReading(LandmarkVerdict.Match), _ => null);
        inv.BootArm(null);
        Assert.True(inv.Armed, inv.Refusal);
        var hooks = new SaveEdgeHooks(f, inv.Tracker, inv.Items.Select(i => i.Id).ToList(), inv.ReplayOnLoad);
        return new World { Image = f, Sidecar = sidecar, Inv = inv, Hooks = hooks, SidecarPath = path };
    }

    private static int Lines(LogCapture cap, string needle) => cap.File.Count(l => l.Contains(needle));

    /// <summary>THE round-4 ordering test (R4-3b). The counts must already be in the bag when
    /// AfterApply returns: the routine it wraps has just overwritten the bag from the save file,
    /// and the menus qualify an item on its bag count, so the tick's window must not be the first
    /// time an extended id is owned again.</summary>
    [Fact]
    public void The_load_detour_puts_the_counts_back_before_any_tick_runs()
    {
        using var cap = LogCapture.Start();
        var w = NewWorld("ordering");
        var hdr = SaveEdgeTrackerTests.Header(2775);
        w.Sidecar.RecordSave(SaveEdgeTracker.KeyFromHeader(hdr), new Dictionary<int, int> { [Moon] = 1, [Terra] = 3 });

        w.GameApplied(hdr, detour: true);

        Assert.Equal(1, w.Bag(Moon));
        Assert.Equal(3, w.Bag(Terra));
        Assert.Equal(0, Lines(cap, "A save was loaded"));   // no tick has run yet: this is the detour's own write

        w.Tick();
        Assert.Equal(3, w.Bag(Terra));                      // the fallback re-places the same counts
        Assert.Equal(1, Lines(cap, "A save was loaded"));
    }

    /// <summary>(R4-3a) The detour runs the SAME policy the tick has always run: for every way a
    /// key can resolve (a recorded save, an unknown save, the schema-1 one-shot migration) the bag
    /// ends up identical whether the replay came from the tick alone or from the detour plus the
    /// tick. The migration row is the one a naive second resolve gets wrong: it would spend the
    /// one-shot inside the detour and then land the seed on the tick.</summary>
    [Fact]
    public void The_detour_resolves_a_key_exactly_the_way_the_tick_always_has()
    {
        using var cap = LogCapture.Start();
        var hdr = SaveEdgeTrackerTests.Header(2775);
        string key = SaveEdgeTracker.KeyFromHeader(hdr);

        foreach (bool detour in new[] { false, true })
        {
            string tag = detour ? "d" : "t";

            var recorded = NewWorld(tag + "_recorded");
            recorded.Sidecar.RecordSave(key, new Dictionary<int, int> { [Moon] = 2, [Terra] = 3 });
            recorded.GameApplied(hdr, detour);
            recorded.Tick();
            Assert.Equal(new Dictionary<int, int> { [Moon] = 2, [Terra] = 3 }, recorded.BagState());

            var unknown = NewWorld(tag + "_unknown");
            unknown.GameApplied(hdr, detour);
            unknown.Tick();
            Assert.Equal(new Dictionary<int, int> { [Moon] = 1, [Terra] = 0 }, unknown.BagState());

            var legacy = NewWorld(tag + "_legacy", "{\"version\":1,\"counts\":{\"261\":2,\"262\":4}}");
            legacy.GameApplied(hdr, detour);
            legacy.Tick();
            Assert.Equal(new Dictionary<int, int> { [Moon] = 2, [Terra] = 4 }, legacy.BagState());
        }
    }

    /// <summary>(R4-3c) Detour then tick is one replay between them: the same state, ONE "a save
    /// was loaded" line (the tick's), and the schema-1 migration spent exactly once.</summary>
    [Fact]
    public void The_detour_and_the_tick_replay_once_between_them()
    {
        using var cap = LogCapture.Start();
        var w = NewWorld("once", "{\"version\":1,\"counts\":{\"261\":2,\"262\":4}}");

        w.GameApplied(SaveEdgeTrackerTests.Header(100), detour: true);
        Assert.Equal(4, w.Bag(Terra));            // the migration ran inside the detour
        w.Tick();
        Assert.Equal(4, w.Bag(Terra));            // and the tick did not undo it with the seed
        Assert.Equal(2, w.Bag(Moon));
        Assert.Equal(1, Lines(cap, "A save was loaded"));
        Assert.Equal(1, Lines(cap, "one-time migration"));

        // The one-shot really was spent once: a second, unknown save gets the data seed, and the
        // file on disk no longer carries the schema-1 counts.
        w.GameApplied(SaveEdgeTrackerTests.Header(200), detour: true);
        Assert.Equal(0, w.Bag(Terra));
        w.Tick();
        Assert.Equal(0, w.Bag(Terra));
        Assert.Equal(1, w.Bag(Moon));
        Assert.Equal(1, Lines(cap, "one-time migration"));
        Assert.Null(ExtendedBagSidecar.Load(w.SidecarPath).TakeLegacy());
    }

    /// <summary>(R4-3d) An unknown save still gets the data seed, once: the detour places it, the
    /// tick's fallback re-places the same byte, and the log names the seed one time.</summary>
    [Fact]
    public void An_unknown_save_still_gets_the_first_copy_seed_once()
    {
        using var cap = LogCapture.Start();
        var w = NewWorld("seed");
        w.Image.Writes.Clear();

        w.GameApplied(SaveEdgeTrackerTests.Header(4242), detour: true);
        Assert.Single(w.Image.Writes.Where(x => x.Addr == Offsets.BagCountArray + Terra));
        // Exactly the N extended bag bytes and nothing else: this world never seeded the order
        // templates, so the round-5 seat's guarded read of them fails and it writes nothing (the
        // seat's own writes are counted in the tests below, which do seed them).
        Assert.Equal(2, w.Image.Writes.Count);

        w.Tick();
        var writes = w.Image.Writes.Where(x => x.Addr == Offsets.BagCountArray + Terra).ToList();
        Assert.Equal(2, writes.Count);                        // the detour's, then the fallback's
        Assert.All(writes, x => Assert.Equal(0, x.Data[0]));   // the seed both times, never a stale count
        Assert.Equal(1, w.Bag(Moon));
        Assert.Equal(1, Lines(cap, "first-copy seed"));
    }

    /// <summary>(R5-2) THE round-5 ordering test. The two order templates are save state: the
    /// routine this detour wraps has just restored both out of the save struct, and the owner's
    /// saves were all written before an extended id was ever in one. So the seat has to happen
    /// here, after that restore, and it has to be done by the time the detour returns, before the
    /// player can open a menu and before the next tick.</summary>
    [Fact]
    public void The_load_detour_seats_an_owned_id_in_both_order_templates_before_any_tick_runs()
    {
        using var cap = LogCapture.Start();
        var w = NewWorld("seat");
        w.SeedTemplates(1, 2, 3);
        var hdr = SaveEdgeTrackerTests.Header(2775);
        w.Sidecar.RecordSave(SaveEdgeTracker.KeyFromHeader(hdr), new Dictionary<int, int> { [Moon] = 0, [Terra] = 3 });

        w.GameApplied(hdr, detour: true);

        foreach (var r in TemplateSeat.WeaponRegions)
            Assert.Equal(new ushort[] { 1, 2, 3, Terra, TemplateSeat.EndMarker }, w.TemplateWords(r.Addr, 5));
        Assert.Equal(2, w.SeatWrites());                   // one write per template, and only there
        Assert.Equal(0, Lines(cap, "A save was loaded"));   // no tick has run yet

        // The tick's fallback runs the same seat and finds nothing left to do.
        w.Tick();
        Assert.Equal(2, w.SeatWrites());
        foreach (var r in TemplateSeat.WeaponRegions)
            Assert.Equal(new ushort[] { 1, 2, 3, Terra, TemplateSeat.EndMarker }, w.TemplateWords(r.Addr, 5));
    }

    /// <summary>THE negative (T-R3-5's vanilla semantics): a template lists items the player owns.
    /// An extended item with an empty bag count stays out of both templates, exactly as a vanilla
    /// item the player has none of does.</summary>
    [Fact]
    public void An_extended_item_the_player_owns_none_of_is_never_seated()
    {
        var w = NewWorld("unowned");
        w.SeedTemplates(1, 2, 3);
        var hdr = SaveEdgeTrackerTests.Header(3000);
        w.Sidecar.RecordSave(SaveEdgeTracker.KeyFromHeader(hdr), new Dictionary<int, int> { [Moon] = 0, [Terra] = 0 });

        w.GameApplied(hdr, detour: true);
        w.Tick();

        Assert.Equal(0, w.SeatWrites());
        foreach (var r in TemplateSeat.WeaponRegions)
            Assert.Equal(new ushort[] { 1, 2, 3, TemplateSeat.EndMarker }, w.TemplateWords(r.Addr, 4));
    }

    /// <summary>The tick alone still seats: a load edge that reached the tracker without the
    /// detour (the pre-round-4 order, and the path every older test drives) ends in the same
    /// tables, and the tick is the half that says so in the log.</summary>
    [Fact]
    public void The_tick_seats_the_templates_too_when_it_is_the_only_path_that_ran()
    {
        using var cap = LogCapture.Start();
        var w = NewWorld("tickseat");
        w.SeedTemplates(1, 2, 3);
        var hdr = SaveEdgeTrackerTests.Header(2775);
        w.Sidecar.RecordSave(SaveEdgeTracker.KeyFromHeader(hdr), new Dictionary<int, int> { [Moon] = 1, [Terra] = 3 });

        w.GameApplied(hdr, detour: false);
        Assert.Equal(0, w.SeatWrites());

        w.Tick();

        foreach (var r in TemplateSeat.WeaponRegions)
            Assert.Equal(new ushort[] { 1, 2, 3, Moon, Terra, TemplateSeat.EndMarker }, w.TemplateWords(r.Addr, 6));
        Assert.Equal(2, w.SeatWrites());
        Assert.Equal(2, Lines(cap, "now lists"));
    }

    /// <summary>The detour skips cleanly when the inventory never armed: same gate the tick uses,
    /// so a refused boot arm still means not one byte of the bag is touched on a load.</summary>
    [Fact]
    public void An_unarmed_inventory_writes_nothing_on_a_load_edge()
    {
        var f = ExtendedInventoryTests.VanillaImage();
        var inv = new ExtendedInventory(f, new FakeNearAllocator(), TwoItems(),
            ExtendedBagSidecar.Load(Path.Combine(_dir, "unarmed_" + ExtendedBagSidecar.FileName)),
            () => new LandmarkReading(LandmarkVerdict.Unreadable), _ => null);
        inv.BootArm(null);
        Assert.False(inv.Armed);

        var hooks = new SaveEdgeHooks(f, inv.Tracker, new[] { Moon, Terra }, inv.ReplayOnLoad);
        f.Seed(Offsets.SaveStructPtr, BitConverter.GetBytes(StructAddr));
        f.Seed(StructAddr + Offsets.SaveHeaderKeyOff, SaveEdgeTrackerTests.Header(9));
        hooks.AfterApply(second: false);

        Assert.Empty(f.Writes);
    }

    /// <summary>(P4-F1, stage 2) Both of the game's load routines are hooked, so ONE logical
    /// load can call AfterApply twice with the same header. Before the cure the first call spent
    /// the schema-1 one-shot and the second, finding the key unknown, re-seeded it: a migrated
    /// count of 4 became the seed's 0 between one routine and the next. The cure records the
    /// legacy counts under the resolved key at migration time, so every later resolve of that key
    /// (the second routine, the tick, the next launch) answers with the same counts.</summary>
    [Fact]
    public void Two_load_routines_on_one_legacy_key_keep_the_migrated_counts()
    {
        using var cap = LogCapture.Start();
        var w = NewWorld("twice", "{\"version\":1,\"counts\":{\"261\":2,\"262\":4}}");
        var hdr = SaveEdgeTrackerTests.Header(2775);
        string key = SaveEdgeTracker.KeyFromHeader(hdr);

        w.GameApplied(hdr, detour: true);
        Assert.Equal(4, w.Bag(Terra));
        w.GameApplied(hdr, detour: true);   // the second routine, same save, same header
        Assert.Equal(4, w.Bag(Terra));      // RED before the cure: re-seeded to 0
        Assert.Equal(2, w.Bag(Moon));
        w.Tick();
        Assert.Equal(new Dictionary<int, int> { [Moon] = 2, [Terra] = 4 }, w.BagState());

        // The sidecar now owns the counts under that key and the one-shot is gone from disk.
        var reloaded = ExtendedBagSidecar.Load(w.SidecarPath);
        Assert.True(reloaded.TryGetSave(key, out var saved));
        Assert.Equal(new Dictionary<int, int> { [Moon] = 2, [Terra] = 4 }, new Dictionary<int, int>(saved));
        Assert.Null(reloaded.TakeLegacy());
    }
}
