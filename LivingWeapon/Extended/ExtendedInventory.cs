using System;
using System.Collections.Generic;
using System.Linq;
using Reloaded.Hooks.Definitions;

namespace LivingWeapon;

/// <summary>
/// LW-346 extended inventory composition root and boot-arm lifecycle. BootArm runs in Mod.StartEx as one
/// transaction that rolls back on the first refusal. No whole-image check gates it (the roster landmark is
/// unreadable that early, and since LW-397 the build stamp is a report line), so each patch site verifies
/// its old bytes before writing. Steps are in ExtendedInventory.Arm.cs: cap patches, the LW-368
/// round 2 list relocation (bag counts then live at <see cref="BagCountBase"/>), catalog fill, accessor
/// hooks, category/order/list-builder/reset/save-edge hooks, then the bag seed. The tick half is
/// ExtendedInventory.Tick.cs. With no extended rows shipped the whole arm is skipped with one Debug line.
/// </summary>
internal sealed partial class ExtendedInventory
{
    private readonly ICodePatcher _patcher;
    private readonly INearAllocator _allocator;
    private readonly ExtendedInventoryData.LoadResult _data;
    private readonly ExtendedBagSidecar _sidecar;
    private readonly Func<IReloadedHooks?, string?> _installHooks;

    private readonly BytePatchSet _patches = new();
    private readonly ListRelocation _relocation = new();   // LW-368 round 2
    private readonly TemplateRelocation _templates = new();   // LW-371
    private readonly ExtendedCatalog _catalog = new();
    private readonly ShopFlagsMirror _shops = new();   // LW-354
    private readonly List<AccessorEntryHook> _accessorHooks = new();
    private AccessorEntryHook? _weaponStatHook;   // the row stub: its page holds every extended id's 8-byte stats row
    private SwingIdFallbackHook? _swingIdFallback;   // empty-tile swing art falls back to the hand id
    private CategoryGetterHook? _getterHook;
    private OrderRebuildHook? _orderHook;
    private ListBuilderHook? _listBuilderHook;   // LW-372
    private InventoryResetHook? _resetHook;   // LW-351 fix round 7
    private SaveEdgeHooks? _saveHooks;   // LW-353
    /// <summary>the save/load edge core the hooks feed and the tick drains. Tests drive it directly.</summary>
    public SaveEdgeTracker Tracker { get; } = new();

    public IReadOnlyList<ExtendedItemDef> Items => _data.Items;
    public bool Armed { get; private set; }
    /// <summary>Why the last BootArm did not arm (null once armed, or when nothing is shipped).</summary>
    public string? Refusal { get; private set; }
    /// <summary>LW-368 round 2: where the bag counts live now, the relocated page if it armed, else
    /// <see cref="Offsets.BagCountArray"/>. Resolve every bag byte through this so a refused relocation falls back.</summary>
    public long BagCountBase => _relocation.Installed ? _relocation.CountBase : Offsets.BagCountArray;
    /// <summary>the weapon order-template regions in effect, relocated if that armed, else the
    /// vanilla pair. Read through this, not <see cref="TemplateSeat.WeaponRegions"/>.</summary>
    public TemplateSeat.Region[] TemplateRegions => _templates.Regions;

    /// <param name="installHooks">Test seam for the Reloaded.Hooks install step; null uses
    /// <see cref="DefaultInstallHooks"/>, which refuses a null hooks controller.</param>
    public ExtendedInventory(ICodePatcher patcher, INearAllocator allocator, ExtendedInventoryData.LoadResult data,
        ExtendedBagSidecar sidecar, Func<IReloadedHooks?, string?>? installHooks = null)
    {
        _patcher = patcher;
        _allocator = allocator;
        _data = data;
        _sidecar = sidecar;
        _installHooks = installHooks ?? DefaultInstallHooks;
        _pending = ExtendedSites.PostLoadPatches(Math.Max(1, data.Items.Count)).Select(p => new PendingPatch(p)).ToList();
    }

    /// <summary>Runs once from Engine.InjectHooks (Mod.StartEx) and is idempotent. Null hooks means the helper mod is absent, so nothing arms.</summary>
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
        string? why = Install(hooks);
        if (why != null) { RollBack(); Refuse(why); return; }
        SeedBag();
        Armed = true;
        Refusal = null;
        ModLogger.Event(LogVerb.Startup,
            $"Extended inventory armed: {Items.Count} new item(s) [{string.Join(", ", Items.Select(i => $"{i.Name} (id {i.Id})"))}], "
            + $"{_patches.AppliedCount} cap patches, {_accessorHooks.Count} accessor redirects, swing-id fallback armed, 5 hooks (menu list builder hooked), shop table mirrored, "
            + $"item count lists relocated to 0x{_relocation.PageAddr:X}; "
            + $"menu order charts relocated to 0x{_templates.PageAddr:X}"
            + (_pending.Count == 0 ? "." : $"; {_pending.Count} damage cap(s) wait for the first save to load."));
    }

    /// <summary>Address of an extended id's 8-byte ITEM_WEAPON_DATA row in the weapon-stat stub page, or -1
    /// when unarmed or not ours. WpTableHold writes its WP bump there (Offsets.ItemStatsRows has no row for
    /// these ids). Same layout as the resident table, Power at +4.</summary>
    public long WeaponRowAddr(int id)
    {
        if (!Armed || _weaponStatHook == null || !_weaponStatHook.Installed) return -1;
        int i = id - ExtendedCatalog.FirstExtendedId;
        if (i < 0 || i >= Items.Count) return -1;
        return _weaponStatHook.StubAddr + ThunkStub.RowStubHeader + (long)i * ThunkStub.RowSize;
    }

    private void Refuse(string why)
    {
        Refusal = why;
        ModLogger.Warn(LogVerb.Startup, $"The extended inventory is NOT armed this session ({why}); the new items will not exist in the game until this is resolved.");
    }
}
