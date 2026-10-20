using System;
using System.Collections.Generic;
using System.Linq;
using Reloaded.Hooks.Definitions;

namespace LivingWeapon;

/// <summary>
/// Extended inventory composition root and boot-arm lifecycle. BootArm runs in Mod.StartEx as one
/// transaction that rolls back on the first refusal. No whole-image check gates it (the roster landmark is
/// unreadable that early, and the build stamp is a report line), so each patch site verifies
/// its old bytes before writing. Steps are in ExtendedInventory.Arm.cs. Cap patches, the
/// list relocation (bag counts then live at <see cref="BagCountBase"/>), catalog fill, accessor
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
    private readonly bool _defaultInstall;

    private readonly BytePatchSet _patches = new();
    private readonly ListRelocation _relocation = new();
    private readonly TemplateRelocation _templates = new();
    private readonly ExtendedCatalog _catalog = new();
    private readonly ShopFlagsMirror _shops = new();
    private readonly List<AccessorEntryHook> _accessorHooks = new();
    private AccessorEntryHook? _weaponStatHook;   // the row stub. Its page holds every extended id's 8-byte stats row
    private SwingIdFallbackHook? _swingIdFallback;   // empty-tile swing art falls back to the hand id
    private CategoryGetterHook? _getterHook;
    private OrderRebuildHook? _orderHook;
    private ListBuilderHook? _listBuilderHook;
    private InventoryResetHook? _resetHook;
    private SaveEdgeHooks? _saveHooks;
    private RewindRestoreHook? _rewindHook;
    private RewindRecomputeStep? _rewindStep;
    private long _rewindTick;
    /// <summary>the save/load edge core the hooks feed and the tick drains. Tests drive it directly.</summary>
    public SaveEdgeTracker Tracker { get; } = new();

    public IReadOnlyList<ExtendedItemDef> Items => _data.Items;
    public bool Armed { get; private set; }
    public long RetryRewinds => _rewindHook?.CheckpointRestoresSeen ?? 0;
    private readonly DoorCell _door = new(DoorReading.Waiting("the loader has not handed over its hooks yet"));
    /// <summary>The extended inventory part's door, written by the boot arm thread and read by the engine tick.</summary>
    public DoorReading Door => _door.Value;
    /// <summary>Why the last BootArm did not arm (null once armed, or when nothing is shipped).</summary>
    public string? Refusal { get; private set; }
    /// <summary>Where the bag counts live now, the relocated page if it armed, else
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
        _defaultInstall = installHooks == null;
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
            _door.Set(DoorReading.Absent("no new items are shipped in this build"));
            return;
        }
        if (!_data.Ok)
        {
            string errors = string.Join("; ", _data.Errors);
            Refuse("the extended-inventory tables did not validate: " + errors,
                DoorReading.FailedPlain("the new item data shipped with this build did not validate"),
                PartsCopy.DisabledPlain("The new items are paused for this session because the item data shipped with this mod did not load correctly; everything else keeps working.", errors));
            return;
        }
        if (hooks == null && _defaultInstall)
        {
            Refuse(NoHooksRefusal, DoorReading.Absent("the loader gave no hooks"),
                "The new items are paused for this session because the game-hooks helper mod (reloaded.sharedlib.hooks) is not enabled; everything else keeps working. " + PartsCopy.HooksWhatToDo);
            return;
        }
        string? why = Install(hooks);
        if (why != null)
        {
            RollBack();
            // The boot arm has no build hedge because it runs from the loader handshake and never reads the guard.
            Refuse(why, DoorReading.Failed("the game code the new items plug into is not where this mod expects it"),
                PartsCopy.Disabled("The new items are paused for this session because the game code they plug into is not where this mod expects it; everything else keeps working.", "", "", why));
            return;
        }
        SeedBag();
        Armed = true;
        Refusal = null;
        _door.Set(DoorReading.Open());
        ModLogger.Event(LogVerb.Startup,
            $"Extended inventory armed: {Items.Count} new item(s) [{string.Join(", ", Items.Select(i => $"{i.Name} (id {i.Id})"))}], "
            + $"{_patches.AppliedCount} cap patches, {_accessorHooks.Count} accessor redirects, swing-id fallback armed, 6 hooks (menu list builder hooked), shop table mirrored, "
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

    private void Refuse(string why, DoorReading door, string line)
    {
        Refusal = why;
        // The box shows the door's reason, so the raw install diagnostic stays in the log line.
        _door.Set(door);
        ModLogger.Warn(LogVerb.Startup, line);
    }
}
