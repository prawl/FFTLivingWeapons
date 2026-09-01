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
///   3. LW-368 round 2: the two per-item byte lists (bag counts, sibling flags) copied onto a
///      page the mod owns and every plain-code reference to the old blocks re-pointed there
///      (ListRelocation.cs), so the ceiling past which id N reads Ramza's own roster row is
///      gone and every later step below resolves a bag byte through <see cref="BagCountBase"/>;
///   4. the relocated catalog filled with our records;
///   5. the accessor thunk clones: the weapon-stat thunk answers with our own stat rows, the
///      nine per-category thunks answer as each item's clone/art donor;
///   6. the category-getter, order-rebuild, list-builder (LW-372), reset and save-edge hooks
///      (Reloaded.Hooks, prologue landmarks);
///   7. the bag counts (sidecar value, else the data's first-copy seed), written at
///      <see cref="BagCountBase"/>.
/// Then <see cref="Armed"/> is true and the tick half (ExtendedInventory.Tick.cs) owns the two
/// copy-protected caps and the bag sidecar. With no extended rows shipped, everything above is
/// skipped with one Debug line: the mod behaves exactly as before this system existed.
///
/// The step list itself (<c>Install</c>, <c>InstallClone</c>, <c>SeedBag</c>) lives in
/// ExtendedInventory.Arm.cs (LW-371, verifier NIT-5); this file keeps the fields, the ctor, this
/// sequencing method, the properties and <see cref="Refuse"/>.
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
    private readonly ListRelocation _relocation = new();   // LW-368 round 2
    private readonly TemplateRelocation _templates = new();   // LW-371
    private readonly ExtendedCatalog _catalog = new();
    private readonly ShopFlagsMirror _shops = new();   // LW-354
    private readonly List<ThunkClone> _clones = new();
    private ThunkClone? _weaponStatClone;   // the row stub: its page holds every extended id's 8-byte stats row
    private SwingIdFallbackHook? _swingIdFallback;   // LW-365: empty-tile swing art falls back to the hand id
    private CategoryGetterHook? _getterHook;
    private OrderRebuildHook? _orderHook;
    private ListBuilderHook? _listBuilderHook;   // LW-372
    private InventoryResetHook? _resetHook;   // LW-351 fix round 7
    private SaveEdgeHooks? _saveHooks;   // LW-353
    /// <summary>LW-353: the save/load edge core the hooks feed and the tick drains. Tests drive it directly.</summary>
    public SaveEdgeTracker Tracker { get; } = new();

    public IReadOnlyList<ExtendedItemDef> Items => _data.Items;
    public bool Armed { get; private set; }
    /// <summary>Why the last BootArm did not arm (null once armed, or when nothing is shipped).</summary>
    public string? Refusal { get; private set; }
    /// <summary>LW-368 round 2: where the per-item bag counts live right now -- the relocated
    /// page once the list relocation armed, else <see cref="Offsets.BagCountArray"/> (the
    /// vanilla block). Every reader/writer of a bag byte resolves its base through this, never
    /// the raw offset, so a relocation refusal falls back to the vanilla block transparently.</summary>
    public long BagCountBase => _relocation.Installed ? _relocation.CountBase : Offsets.BagCountArray;
    /// <summary>LW-371: the two weapon order-template regions in effect right now -- the
    /// relocated page's regions once the template relocation armed, else the vanilla pair. Every
    /// seat (boot replay, load replay, the order-rebuild hook) reads through this, never
    /// <see cref="TemplateSeat.WeaponRegions"/> directly, so a relocation refusal falls back to the
    /// vanilla tables transparently.</summary>
    public TemplateSeat.Region[] TemplateRegions => _templates.Regions;

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
            + $"{_patches.AppliedCount} cap patches, {_clones.Count} accessor redirects, swing-id fallback armed, 5 hooks (menu list builder hooked), shop table mirrored, "
            + $"item count lists relocated to 0x{_relocation.PageAddr:X}; "
            + $"menu order charts relocated to 0x{_templates.PageAddr:X}; "
            + "2 damage caps wait for the first save to load.");
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

    private void Refuse(string why)
    {
        Refusal = why;
        ModLogger.Warn(LogVerb.Startup, $"The extended inventory is NOT armed this session ({why}); the new items will not exist in the game until this is resolved.");
    }
}
