using System.Linq;
using Reloaded.Hooks.Definitions;

namespace LivingWeapon;

/// <summary>
/// The detour half of the arm: the four Reloaded.Hooks detours the extended inventory installs
/// (category getter, menu-order rebuild, inventory reset, save edges), in install order, and
/// the reverse-order rollback every refusal takes. Split from ExtendedInventory.cs in LW-351
/// fix round 7 (2026-08-30) when the inventory-reset hook joined; the cap patches, catalog,
/// shop mirror and thunk clones stay in the base file (Install), the tick logic in .Tick.cs.
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
        _orderHook = new OrderRebuildHook(_patcher, extendedCount: Items.Count);
        why = _orderHook.Install(hooks);
        if (why != null) return why;
        // Fix round 7: the game's per-item reset zeroes the bag array below its widened bound
        // (every extended count went to x0 after one real battle, owner 2026-08-30 23:29); the
        // detour keeps the extended bytes across the call.
        _resetHook = new InventoryResetHook(_patcher, Items.Count);
        why = _resetHook.Install(hooks);
        if (why != null) return why;
        // LW-353: the save edges (record counts per save, replay per save); a refusal here rolls
        // the whole arm back like any other piece, because an armed inventory whose counts vanish
        // on every load is worse than no inventory.
        _saveHooks = new SaveEdgeHooks(_patcher, Tracker, Items.OrderBy(i => i.Id).Select(i => i.Id).ToList(), ReplayOnLoad);
        return _saveHooks.Install(hooks);
    }

    /// <summary>Undo in reverse install order. Stub pages and the catalog buffer are leaked by
    /// design (a game thread may be inside them); every code byte goes back.</summary>
    private void RollBack()
    {
        _saveHooks?.Release(); _saveHooks = null;
        _resetHook?.Release(); _resetHook = null;
        _orderHook?.Release(); _orderHook = null;
        _getterHook?.Release(); _getterHook = null;
        for (int i = _clones.Count - 1; i >= 0; i--) _clones[i].Restore(_patcher);
        _clones.Clear();
        _weaponStatClone = null;
        _shops.Restore(_patcher);
        _catalog.Restore(_patcher);
        _patches.Rollback(_patcher);
    }
}
