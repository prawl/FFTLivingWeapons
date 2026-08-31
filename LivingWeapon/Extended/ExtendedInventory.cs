using System;
using System.Collections.Generic;
using System.Linq;
using Reloaded.Hooks.Definitions;

namespace LivingWeapon;

/// <summary>
/// LW-346: the extended inventory's composition root and boot-arm lifecycle. Takes the items
/// the data layer loaded and, in Mod.StartEx (before the game runs an instruction), installs
/// every piece the FFTHandsFree research rig proved on 1.5.2, as ONE transaction with full
/// rollback on the first refusal:
///   1. the PE build-key landmark must Match (the roster landmark LaunchGuard waits for cannot
///      be read this early; a patched game refuses here with one line);
///   2. the boot-safe cap patches (ExtendedSites.BootPatches, old bytes verified);
///   3. the relocated catalog filled with our records;
///   4. the accessor thunk clones: the weapon-stat thunk answers with our own stat rows, the
///      nine per-category thunks answer as each item's clone/art donor;
///   5. the category-getter and order-rebuild hooks (Reloaded.Hooks, prologue landmarks);
///   6. the bag counts (sidecar value, else the data's first-copy seed).
/// Then <see cref="Armed"/> is true and the tick half (ExtendedInventory.Tick.cs) owns the two
/// copy-protected caps and the bag sidecar. With no extended rows shipped, everything above is
/// skipped with one Debug line: the mod behaves exactly as before this system existed.
/// </summary>
internal sealed partial class ExtendedInventory
{
    private readonly ICodePatcher _patcher;
    private readonly INearAllocator _allocator;
    private readonly ExtendedInventoryData.LoadResult _data;
    private readonly ExtendedBagSidecar _sidecar;
    private readonly Func<LandmarkReading> _peKeyProbe;
    private readonly Func<IReloadedHooks?, string?> _installHooks;

    private readonly BytePatchSet _patches = new();
    private readonly ExtendedCatalog _catalog = new();
    private readonly ShopFlagsMirror _shops = new();   // LW-354
    private readonly List<ThunkClone> _clones = new();
    private ThunkClone? _weaponStatClone;   // the row stub: its page holds every extended id's 8-byte stats row
    private CategoryGetterHook? _getterHook;
    private OrderRebuildHook? _orderHook;
    private SaveEdgeHooks? _saveHooks;   // LW-353
    /// <summary>LW-353: the save/load edge core the hooks feed and the tick drains. Tests drive it directly.</summary>
    public SaveEdgeTracker Tracker { get; } = new();

    public IReadOnlyList<ExtendedItemDef> Items => _data.Items;
    public bool Armed { get; private set; }
    /// <summary>Why the last BootArm did not arm (null once armed, or when nothing is shipped).</summary>
    public string? Refusal { get; private set; }

    /// <param name="installHooks">Test seam (internal): the step that installs the two
    /// Reloaded.Hooks detours, returning null on success or the refusal. Null (production) uses
    /// <see cref="DefaultInstallHooks"/>, which refuses a null hooks controller.</param>
    public ExtendedInventory(ICodePatcher patcher, INearAllocator allocator, ExtendedInventoryData.LoadResult data,
        ExtendedBagSidecar sidecar, Func<LandmarkReading> peKeyProbe, Func<IReloadedHooks?, string?>? installHooks = null)
    {
        _patcher = patcher;
        _allocator = allocator;
        _data = data;
        _sidecar = sidecar;
        _peKeyProbe = peKeyProbe;
        _installHooks = installHooks ?? DefaultInstallHooks;
        _pending = ExtendedSites.PostLoadPatches(Math.Max(1, data.Items.Count)).Select(p => new PendingPatch(p)).ToList();
    }

    private string? DefaultInstallHooks(IReloadedHooks? hooks)
    {
        if (hooks == null) return "the game-hooks helper mod (reloaded.sharedlib.hooks) is not loaded";
        _getterHook = new CategoryGetterHook(_patcher, ExtendedCatalog.FirstExtendedId, Items.OrderBy(i => i.Id).Select(i => i.CloneDonor).ToArray());
        string? why = _getterHook.Install(hooks);
        if (why != null) return why;
        _orderHook = new OrderRebuildHook(_patcher);
        why = _orderHook.Install(hooks);
        if (why != null) return why;
        // LW-353: the save edges (record counts per save, replay per save); a refusal here rolls
        // the whole arm back like any other piece, because an armed inventory whose counts vanish
        // on every load is worse than no inventory.
        _saveHooks = new SaveEdgeHooks(_patcher, Tracker, Items.OrderBy(i => i.Id).Select(i => i.Id).ToList(), ReplayOnLoad);
        return _saveHooks.Install(hooks);
    }

    /// <summary>Runs once from Engine.InjectHooks (Mod.StartEx). Idempotent. Null hooks = the
    /// hooks helper mod is absent: nothing is armed, one warning says why.</summary>
    public void BootArm(IReloadedHooks? hooks)
    {
        if (Armed) return;
        if (!_data.FolderPresent || (_data.Ok && _data.Items.Count == 0))
        {
            ModLogger.Debug(LogVerb.Startup, "No extended-inventory items are shipped; the extended inventory stays off.");
            return;
        }
        if (!_data.Ok)
        {
            Refuse("the extended-inventory tables did not validate: " + string.Join("; ", _data.Errors));
            return;
        }
        var pe = _peKeyProbe();
        if (pe.Verdict != LandmarkVerdict.Match)
        {
            Refuse(pe.Verdict == LandmarkVerdict.Unreadable
                ? "the game image could not be read for the build-key check"
                : "the game build does not match (" + pe.Detail + ")");
            return;
        }
        string? why = Install(hooks);
        if (why != null) { RollBack(); Refuse(why); return; }
        SeedBag();
        Armed = true;
        Refusal = null;
        ModLogger.Event(LogVerb.Startup,
            $"Extended inventory armed: {Items.Count} new item(s) [{string.Join(", ", Items.Select(i => $"{i.Name} (id {i.Id})"))}], "
            + $"{_patches.AppliedCount} cap patches, {_clones.Count} accessor redirects, 2 menu hooks, shop table mirrored; "
            + "2 damage caps wait for the first save to load.");
    }

    private string? Install(IReloadedHooks? hooks)
    {
        int n = Items.Count;
        string? why = _patches.Apply(_patcher, ExtendedSites.BootPatches(n));
        if (why != null) return why;
        why = _catalog.Install(_patcher, _allocator, Items.Select(i => (i.Id, i.CatalogRecord)).ToList());
        if (why != null) return why;
        why = _shops.Install(_patcher, _allocator, Items.Select(i => (i.Id, i.ShopFlags)).ToList());
        if (why != null) return why;

        int lo = ExtendedCatalog.FirstExtendedId;
        var rows = Items.OrderBy(i => i.Id).Select(i => i.WeaponRow).ToArray();
        var cloneDonors = Items.OrderBy(i => i.Id).Select(i => i.CloneDonor).ToArray();
        var artDonors = Items.OrderBy(i => i.Id).Select(i => i.ArtDonor).ToArray();
        var weaponStat = new ThunkClone(Offsets.ThunkWeaponStat, "weapon-stat");
        why = InstallClone(weaponStat, t => ThunkStub.EmitRowStub(lo, rows, t));
        if (why != null) return why;
        _weaponStatClone = weaponStat;
        foreach (var (addr, label, usesArt) in ExtendedSites.DonorThunks)
        {
            var donors = usesArt ? artDonors : cloneDonors;
            why = InstallClone(new ThunkClone(addr, label), t => ThunkStub.EmitDonorStub(lo, donors, t));
            if (why != null) return why;
        }

        return _installHooks(hooks);
    }

    private string? InstallClone(ThunkClone clone, Func<long, byte[]> emit)
    {
        string? why = clone.Install(_patcher, _allocator, emit);
        if (why == null) _clones.Add(clone);
        return why;
    }

    /// <summary>Undo in reverse install order. Stub pages and the catalog buffer are leaked by
    /// design (a game thread may be inside them); every code byte goes back.</summary>
    private void RollBack()
    {
        _saveHooks?.Release(); _saveHooks = null;
        _orderHook?.Release(); _orderHook = null;
        _getterHook?.Release(); _getterHook = null;
        for (int i = _clones.Count - 1; i >= 0; i--) _clones[i].Restore(_patcher);
        _clones.Clear();
        _weaponStatClone = null;
        _shops.Restore(_patcher);
        _catalog.Restore(_patcher);
        _patches.Rollback(_patcher);
    }

    /// <summary>The address of an extended id's 8-byte ITEM_WEAPON_DATA row inside the weapon-stat
    /// stub page (what the game's weapon-stat accessor returns for that id), or -1 when the
    /// inventory is not armed or the id is not one of ours. WpTableHold writes its turn-scoped
    /// WP bump there for a wp/wp+faith-lane extended weapon (the resident table has no row for
    /// these ids, Offsets.ItemStatsRows). Same 8-byte layout as the resident table: Power at +4.</summary>
    public long WeaponRowAddr(int id)
    {
        if (!Armed || _weaponStatClone == null || !_weaponStatClone.Installed) return -1;
        int i = id - ExtendedCatalog.FirstExtendedId;
        if (i < 0 || i >= Items.Count) return -1;
        return _weaponStatClone.StubAddr + ThunkStub.RowStubHeader + (long)i * ThunkStub.RowSize;
    }

    /// <summary>Boot placement: the data seed for every extended id. No save is loaded at boot,
    /// so this is the bag a NEW game starts with; a loaded save replays its own recorded counts
    /// through the load edge (ExtendedInventory.Tick.cs, LW-353).</summary>
    private void SeedBag()
    {
        foreach (var item in Items)
        {
            if (!_patcher.TryWrite(Offsets.BagCountArray + item.Id, new[] { (byte)item.SeedCount }))
                ModLogger.Warn(LogVerb.Save, $"Could not place {item.SeedCount} {item.Name} in the bag at boot (write refused).");
        }
        ModLogger.Event(LogVerb.Save, "Extended-inventory bag counts seeded for a new game: "
            + string.Join(", ", Items.Select(i => $"{i.Name} x{i.SeedCount}")) + $"; saved games replay their own (sidecar: {_sidecar.LoadedFrom}, {_sidecar.SaveCount} save(s) known).");
    }

    private void Refuse(string why)
    {
        Refusal = why;
        ModLogger.Warn(LogVerb.Startup, $"The extended inventory is NOT armed this session ({why}); the new items will not exist in the game until this is resolved.");
    }
}
