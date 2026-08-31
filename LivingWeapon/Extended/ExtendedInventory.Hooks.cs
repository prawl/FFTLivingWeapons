using System.Linq;
using Reloaded.Hooks.Definitions;

namespace LivingWeapon;

/// <summary>
/// The detour half of the arm: the five Reloaded.Hooks detours the extended inventory installs
/// (category getter, menu-order rebuild, LW-372's list-builder, inventory reset, save edges), in
/// install order, and the reverse-order rollback every refusal takes. Split from ExtendedInventory.cs
/// in LW-351 fix round 7 (2026-08-30) when the inventory-reset hook joined; the step list that
/// installs the cap patches, list relocation, template relocation, catalog, shop mirror and thunk
/// clones (`Install`) lives in ExtendedInventory.Arm.cs (LW-371, verifier NIT-5), the tick logic
/// in .Tick.cs. RollBack restores the two relocations here, in REVERSE install order (D2/LW-371):
/// the template relocation first (right after the catalog, its install successor), then the
/// list relocation (right before the boot cap patches, its install predecessor) -- the only
/// other place either one is touched.
/// </summary>
internal sealed partial class ExtendedInventory
{
    private string? DefaultInstallHooks(IReloadedHooks? hooks)
    {
        if (hooks == null) return "the game-hooks helper mod (reloaded.sharedlib.hooks) is not loaded";
        _getterHook = new CategoryGetterHook(_patcher, ExtendedCatalog.FirstExtendedId, Items.OrderBy(i => i.Id).Select(i => i.CloneDonor).ToArray());
        string? why = _getterHook.Install(hooks);
        if (why != null) return why;
        // Fix round 7: the rebuild hook seats the owned extended ids into the template it is
        // handed before the game's own rebuild runs, so it needs to know how many ids exist.
        // LW-368 round 2: reads bag ownership through BagCountBase (the relocated page once armed).
        // LW-371: seats into TemplateRegions (the page regions once the template relocation armed).
        _orderHook = new OrderRebuildHook(_patcher, extendedCount: Items.Count, bagBase: BagCountBase, regions: () => TemplateRegions);
        why = _orderHook.Install(hooks);
        if (why != null) return why;
        // LW-372 (D4/D8): the shared list builder's own cap already widened to 255 (TemplateRelocation.CapSites,
        // installed earlier in Arm.cs's Install), which is only safe because this hook keeps the
        // two small-notepad stack callers truncated to 149 -- one transaction, so a refusal here
        // rolls the whole arm back and the cap sites go back to vanilla too.
        _listBuilderHook = new ListBuilderHook(_patcher, _allocator);
        why = _listBuilderHook.Install(hooks);
        if (why != null) return why;
        // Fix round 7: the game's per-item reset zeroes the bag array below its widened bound
        // (every extended count went to x0 after one real battle, owner 2026-08-30 23:29); the
        // detour keeps the extended bytes across the call.
        _resetHook = new InventoryResetHook(_patcher, Items.Count, bagBase: BagCountBase);
        why = _resetHook.Install(hooks);
        if (why != null) return why;
        // LW-353: the save edges (record counts per save, replay per save); a refusal here rolls
        // the whole arm back like any other piece, because an armed inventory whose counts vanish
        // on every load is worse than no inventory. LW-371: also given the template relocation, so
        // the serialize detour can project the page down into the old chart blocks and the load
        // detour can adopt the restored blocks back onto the page (TemplateSync).
        _saveHooks = new SaveEdgeHooks(_patcher, Tracker, Items.OrderBy(i => i.Id).Select(i => i.Id).ToList(), ReplayOnLoad, BagCountBase, _templates);
        return _saveHooks.Install(hooks);
    }

    /// <summary>Undo in reverse install order. Stub pages and the catalog buffer are leaked by
    /// design (a game thread may be inside them); every code byte goes back.</summary>
    private void RollBack()
    {
        _saveHooks?.Release(); _saveHooks = null;
        _resetHook?.Release(); _resetHook = null;
        _listBuilderHook?.Release(); _listBuilderHook = null;
        _orderHook?.Release(); _orderHook = null;
        _getterHook?.Release(); _getterHook = null;
        for (int i = _clones.Count - 1; i >= 0; i--) _clones[i].Restore(_patcher);
        _clones.Clear();
        _weaponStatClone = null;
        _shops.Restore(_patcher);
        _catalog.Restore(_patcher);
        _templates.Restore(_patcher);   // LW-371: undo right before the catalog, its install successor
        _relocation.Restore(_patcher);   // LW-368 round 2: undo right before the boot cap patches, its install neighbor
        _patches.Rollback(_patcher);
    }
}
