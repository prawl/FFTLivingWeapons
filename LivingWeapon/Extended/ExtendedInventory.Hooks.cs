using System.Linq;
using Reloaded.Hooks.Definitions;

namespace LivingWeapon;

/// <summary>
/// The detour half of the arm. The five Reloaded.Hooks detours (category getter, menu-order rebuild, LW-372 list-builder,
/// inventory reset, save edges) in install order, plus the reverse-order rollback. Split from ExtendedInventory.cs in
/// LW-351 fix round 7 (2026-08-30). `Install` lives in ExtendedInventory.Arm.cs, tick logic in .Tick.cs.
/// RollBack restores the template relocation first, then the list relocation (D2/LW-371).
/// </summary>
internal sealed partial class ExtendedInventory
{
    private string? DefaultInstallHooks(IReloadedHooks? hooks)
    {
        if (hooks == null) return "the game-hooks helper mod (reloaded.sharedlib.hooks) is not loaded";
        _getterHook = new CategoryGetterHook(_patcher, ExtendedCatalog.FirstExtendedId, Items.OrderBy(i => i.Id).Select(i => i.CloneDonor).ToArray());
        string? why = _getterHook.Install(hooks);
        if (why != null) return why;
        // Fix round 7. Seats the owned extended ids into the template before the game's rebuild, so it needs the id count.
        // LW-368 round 2. Ownership via BagCountBase (relocated page once armed). seats into TemplateRegions.
        _orderHook = new OrderRebuildHook(_patcher, extendedCount: Items.Count, bagBase: BagCountBase, regions: () => TemplateRegions);
        why = _orderHook.Install(hooks);
        if (why != null) return why;
        // The list builder's cap is already widened to 255 (TemplateRelocation.CapSites, installed earlier in Arm.cs);
        // that is safe only because this hook keeps the two small-notepad stack callers at 149. A refusal rolls the whole arm back.
        _listBuilderHook = new ListBuilderHook(_patcher, _allocator);
        why = _listBuilderHook.Install(hooks);
        if (why != null) return why;
        // Fix round 7. The game's per-item reset zeroed the extended bag counts after one real battle (owner 2026-08-30 23:29); the detour preserves them.
        _resetHook = new InventoryResetHook(_patcher, Items.Count, bagBase: BagCountBase);
        why = _resetHook.Install(hooks);
        if (why != null) return why;
        // Save edges (record counts per save, replay per save); a refusal rolls the whole arm back.
        // Also given the template relocation so serialize can project the page into the old chart blocks and load can adopt them back (TemplateSync).
        _saveHooks = new SaveEdgeHooks(_patcher, Tracker, Items.OrderBy(i => i.Id).Select(i => i.Id).ToList(), ReplayOnLoad, BagCountBase, _templates);
        return _saveHooks.Install(hooks);
    }

    /// <summary>Undo in reverse install order. Stub pages and the catalog buffer are leaked (a game thread may be inside them); every code byte is restored.</summary>
    private void RollBack()
    {
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
        _relocation.Restore(_patcher);   // LW-368 round 2. Undo right before the boot cap patches, its install neighbor
        _patches.Rollback(_patcher);
    }
}
