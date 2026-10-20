using System;
using System.Collections.Generic;
using System.Linq;
using Reloaded.Hooks.Definitions;

namespace LivingWeapon;

/// <summary>
/// Switches the new items on at boot, all or nothing, and owns every piece that makes them work.
/// BootArm runs from Mod.StartEx and undoes everything at the first refusal. The roster landmark cannot be read
/// that early and the build stamp is only reported, so instead every patch checks its own old bytes before
/// writing. The steps, in ExtendedInventory.Arm.cs, are the caps, the list move (the bag counts then live at
/// <see cref="BagCountBase"/>), the catalog, the accessor hooks, the six detours, then the starting counts.
/// The per tick work is in ExtendedInventory.Tick.cs. With no new items shipped nothing arms and one Debug line says so.
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
    private AccessorEntryHook? _weaponStatHook;   // its stub page holds every new weapon's 8 byte stat row
    private SwingIdFallbackHook? _swingIdFallback;   // a swing at an empty tile finds the weapon in hand
    private CategoryGetterHook? _getterHook;
    private OrderRebuildHook? _orderHook;
    private ListBuilderHook? _listBuilderHook;
    private InventoryResetHook? _resetHook;
    private SaveEdgeHooks? _saveHooks;
    private RewindRestoreHook? _rewindHook;
    private RewindRecomputeStep? _rewindStep;
    private long _rewindTick;
    /// <summary>The save and load hooks fill it and the tick empties it. Tests drive it directly.</summary>
    public SaveEdgeTracker Tracker { get; } = new();

    public IReadOnlyList<ExtendedItemDef> Items => _data.Items;
    public bool Armed { get; private set; }
    public long RetryRewinds => _rewindHook?.CheckpointRestoresSeen ?? 0;
    private readonly DoorCell _door = new(DoorReading.Waiting("the loader has not handed over its hooks yet"));
    /// <summary>Whether the new items are on, set by the boot thread and read by the tick.</summary>
    public DoorReading Door => _door.Value;
    /// <summary>Why the last BootArm did not arm, or null once armed or when nothing ships.</summary>
    public string? Refusal { get; private set; }
    /// <summary>Where the bag counts are right now, our page once the move armed and
    /// <see cref="Offsets.BagCountArray"/> if it did not. Every bag read goes through here so a refused move still works.</summary>
    public long BagCountBase => _relocation.Installed ? _relocation.CountBase : Offsets.BagCountArray;
    /// <summary>Where the two weapon order charts are right now, moved or not. Read them through here
    /// and not through <see cref="TemplateSeat.WeaponRegions"/>, which only knows the old place.</summary>
    public TemplateSeat.Region[] TemplateRegions => _templates.Regions;

    /// <param name="installHooks">Lets tests stand in for the detour install. Null uses
    /// <see cref="DefaultInstallHooks"/>, which refuses when the loader gave no hooks.</param>
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

    /// <summary>Called once from Engine.InjectHooks and safe to call twice. Null hooks means the helper mod is missing.</summary>
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
            // This runs before the launch guard reads the build, so the message cannot blame a game update.
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

    /// <summary>Where a new weapon's stat row lives, so WpTableHold can raise its WP the way it does for the
    /// game's own rows in Offsets.ItemStatsRows. Same layout, Power at +4. -1 when not armed or not one of ours.</summary>
    public long WeaponRowAddr(int id)
    {
        if (!Armed || _weaponStatHook == null || !_weaponStatHook.Installed) return -1;
        int i = ExtendedCatalog.SlotOf(id);
        if (i < 0 || i >= Items.Count) return -1;
        return _weaponStatHook.StubAddr + ThunkStub.RowStubHeader + (long)i * ThunkStub.RowSize;
    }

    private void Refuse(string why, DoorReading door, string line)
    {
        Refusal = why;
        // The parts box shows the door's plain reason, so the raw diagnostic goes only to the log.
        _door.Set(door);
        ModLogger.Warn(LogVerb.Startup, line);
    }
}
