using System.Linq;
using Reloaded.Hooks.Definitions;

namespace LivingWeapon;

/// <summary>
/// The six detours the new items need, in the order they go in, and the undo for the whole arm.
/// They are the category getter, the menu order rebuild, the list builder, the inventory reset, the save edges
/// and the battle rewinds.
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
        // It seats the owned new items into the chart before the game rebuilds a menu, so it needs to know how
        // many there are, where the bag is and where the charts are.
        _orderHook = new OrderRebuildHook(_patcher, extendedCount: Items.Count, bagBase: BagCountBase, regions: () => TemplateRegions);
        why = _orderHook.Install(hooks);
        if (why != null) return why;
        // The list builder's cap was already raised to 255 in Arm.cs, which is only safe once this hook keeps
        // the two small stack callers at 149, so a refusal here rolls the whole arm back.
        _listBuilderHook = new ListBuilderHook(_patcher, _allocator);
        why = _listBuilderHook.Install(hooks);
        if (why != null) return why;
        // Without this the new items read x0 after every real battle, seen live.
        _resetHook = new InventoryResetHook(_patcher, Items.Count, bagBase: BagCountBase);
        why = _resetHook.Install(hooks);
        if (why != null) return why;
        // Each save files the new items' counts under its own key and each load puts them back. They get the
        // chart move too, so a save carries our charts into the file and a load brings them back (TemplateSync).
        var ids = Items.OrderBy(i => i.Id).Select(i => i.Id).ToList();
        var totals = new RewindTotals(_patcher, ids, BagCountBase, nameOf: id => Items.First(i => i.Id == id).Name);
        _saveHooks = new SaveEdgeHooks(_patcher, Tracker, ids, ReplayOnLoad, BagCountBase, _templates,
            key => _sidecar.TryGetSave(key, out _), () => totals.Capture("save"), () => totals.Capture("rewind"), () => totals.Capture("load"));
        why = _saveHooks.Install(hooks);
        if (why != null) return why;
        // Fleeing a battle or picking Retry rewinds the bag from a snapshot that stops at id 260, so ours are put back here.
        var rewind = _rewindHook = new RewindRestoreHook(_patcher, _saveHooks.AfterRewind, totals);
        why = rewind.Install(hooks);
        if (why != null) return why;
        // After a retry the game works its own counts out again from the roster, so every restore queues the same for ours. The inventory reset is left out because it also runs at loads and after real battles.
        _rewindStep = new RewindRecomputeStep(totals, null,
            ("checkpoint restore", () => rewind.CheckpointRestores), ("flee rewind", () => rewind.RewindsHandedOn));
        return null;
    }

    /// <summary>Undoes the arm in reverse. Every code byte goes back, but the stub pages and catalog buffer stay allocated because a game thread may still be running in them.</summary>
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
        _templates.Restore(_patcher);
        _relocation.Restore(_patcher);
        _patches.Rollback(_patcher);
    }
}
