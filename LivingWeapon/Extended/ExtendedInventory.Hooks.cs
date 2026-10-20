using System.Linq;
using Reloaded.Hooks.Definitions;

namespace LivingWeapon;

/// <summary>
/// The detour half of the arm. The six Reloaded.Hooks detours (category getter, menu-order rebuild, list-builder,
/// inventory reset, save edges, battle rewinds) in install order, plus the reverse-order rollback. Split from ExtendedInventory.cs in
/// 2026-08-30. `Install` lives in ExtendedInventory.Arm.cs, tick logic in .Tick.cs.
/// RollBack restores the template relocation first, then the list relocation.
/// </summary>
internal sealed partial class ExtendedInventory
{
    internal const string NoHooksRefusal = "the game-hooks helper mod (reloaded.sharedlib.hooks) is not loaded";

    private string? DefaultInstallHooks(IReloadedHooks? hooks)
    {
        if (hooks == null) return NoHooksRefusal;
        _getterHook = new CategoryGetterHook(_patcher, ExtendedCatalog.FirstExtendedId, PerSlot(i => i.CloneDonor));
        string? why = _getterHook.Install(hooks);
        if (why != null) return why;
        // Seats the owned extended ids into the template before the game's rebuild, so it needs the id count.
        // Ownership via BagCountBase (relocated page once armed). seats into TemplateRegions.
        _orderHook = new OrderRebuildHook(_patcher, extendedCount: Items.Count, bagBase: BagCountBase, regions: () => TemplateRegions);
        why = _orderHook.Install(hooks);
        if (why != null) return why;
        // The list builder's cap is already widened to 255 (TemplateRelocation.CapSites, installed earlier in Arm.cs);
        // that is safe only because this hook keeps the two small-notepad stack callers at 149. A refusal rolls the whole arm back.
        _listBuilderHook = new ListBuilderHook(_patcher, _allocator);
        why = _listBuilderHook.Install(hooks);
        if (why != null) return why;
        // The game's per-item reset zeroed the extended bag counts after one real battle (owner 2026-08-30 23:29); the detour preserves them.
        _resetHook = new InventoryResetHook(_patcher, Items.Count, bagBase: BagCountBase);
        why = _resetHook.Install(hooks);
        if (why != null) return why;
        // Save edges (record counts per save, replay per save); a refusal rolls the whole arm back.
        // Also given the template relocation so serialize can project the page into the old chart blocks and load can adopt them back (TemplateSync).
        var ids = Items.OrderBy(i => i.Id).Select(i => i.Id).ToList();
        var totals = new RewindTotals(_patcher, ids, BagCountBase, nameOf: id => Items.First(i => i.Id == id).Name);
        _saveHooks = new SaveEdgeHooks(_patcher, Tracker, ids, ReplayOnLoad, BagCountBase, _templates,
            key => _sidecar.TryGetSave(key, out _), () => totals.Capture("save"), () => totals.Capture("rewind"), () => totals.Capture("load"));
        why = _saveHooks.Install(hooks);
        if (why != null) return why;
        // A fled battle and a Retry rewind the bag from a snapshot that stops at id 260, so the extended counts are put back here.
        var rewind = _rewindHook = new RewindRestoreHook(_patcher, _saveHooks.AfterRewind, totals);
        why = rewind.Install(hooks);
        if (why != null) return why;
        // The game re derives its own counts from the roster on the retry roads, so each restore edge queues a recompute. The inventory reset is no trigger, it also runs at loads and after real battles.
        _rewindStep = new RewindRecomputeStep(totals, null,
            ("checkpoint restore", () => rewind.CheckpointRestores), ("flee rewind", () => rewind.RewindsHandedOn));
        return null;
    }

    /// <summary>Undo in reverse install order. Stub pages and the catalog buffer are leaked (a game thread may be inside them); every code byte is restored.</summary>
    private void RollBack()
    {
        _rewindStep = null;
        _rewindHook?.Release(); _rewindHook = null;
        _saveHooks?.Release(); _saveHooks = null;
        _resetHook?.Release(); _resetHook = null;
        _listBuilderHook?.Release(); _listBuilderHook = null;
        _orderHook?.Release(); _orderHook = null;
        _getterHook?.Release(); _getterHook = null;
        _swingIdFallback?.Restore(_patcher); _swingIdFallback = null;
        for (int i = _accessorHooks.Count - 1; i >= 0; i--) _accessorHooks[i].Restore(_patcher);
        _accessorHooks.Clear();
        _weaponStatHook = null;
        _shops.Restore(_patcher);
        _catalog.Restore(_patcher);
        _templates.Restore(_patcher);   // undo right before the catalog, its install successor
        _relocation.Restore(_patcher);   // Undo right before the boot cap patches, its install neighbor
        _patches.Rollback(_patcher);
    }
}
