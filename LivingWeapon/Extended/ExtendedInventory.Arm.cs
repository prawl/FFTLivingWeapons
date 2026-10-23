using System;
using System.Linq;
using Reloaded.Hooks.Definitions;

namespace LivingWeapon;

/// <summary>
/// The boot steps in the order they must run, and the starting counts for a new game.
/// Caps first, then the list move, the chart move, the catalog, the shops, the accessor hooks and the detours.
/// <c>RollBack</c> in ExtendedInventory.Hooks.cs undoes them in reverse.
/// </summary>
internal sealed partial class ExtendedInventory
{
    private string? Install(IReloadedHooks? hooks)
    {
        int n = Items.Count;
        string? why = _patches.Apply(_patcher, ExtendedSites.BootPatches(n));
        if (why != null) return why;
        // Right after the caps, because every later step finds the bag counts
        // through BagCountBase.
        why = _relocation.Install(_patcher, _allocator);
        if (why != null) return why;
        // Before the catalog, because TemplateRegions points at the moved charts
        // from here on.
        why = _templates.Install(_patcher, _allocator);
        if (why != null) return why;
        why = _catalog.Install(_patcher, _allocator, Items.Select(i => (i.Id, i.CatalogRecord)).ToList());
        if (why != null) return why;
        why = _shops.Install(_patcher, _allocator, Items.Select(i => (i.Id, i.ShopFlags)).ToList());
        if (why != null) return why;

        int lo = ExtendedCatalog.FirstExtendedId;
        var rows = PerSlot(i => i.WeaponRow);
        var cloneDonors = PerSlot(i => i.CloneDonor);
        var artDonors = PerSlot(i => i.ArtDonor);
        var weaponStat = new AccessorEntryHook(ExtendedDonorThunks.WeaponStat);
        why = InstallAccessorHook(weaponStat, t => ThunkStub.EmitRowStub(lo, rows, t));
        if (why != null) return why;
        _weaponStatHook = weaponStat;
        foreach (var (entry, usesArt) in ExtendedDonorThunks.HookedDonorEntries)
        {
            var donors = usesArt ? artDonors : cloneDonors;
            why = InstallAccessorHook(new AccessorEntryHook(entry), t => ThunkStub.EmitDonorStub(lo, donors, t));
            if (why != null) return why;
        }

        // A new weapon can only be drawn once the accessor hooks are in, so this comes
        // right after them and before the detours.
        var fallback = new SwingIdFallbackHook();
        why = fallback.Install(_patcher, _allocator, lo, Items.Count);
        if (why != null) return why;
        _swingIdFallback = fallback;

        return _installHooks(hooks);
    }

    private T[] PerSlot<T>(Func<ExtendedItemDef, T> pick) => Items.OrderBy(i => i.Id).Select(pick).ToArray();

    private string? InstallAccessorHook(AccessorEntryHook hook, Func<long, byte[]> emit)
    {
        string? why = hook.Install(_patcher, _allocator, emit);
        if (why == null) _accessorHooks.Add(hook);
        return why;
    }

    /// <summary>No save is loaded yet at boot, so this fills the bag a new game starts with. A loaded save
    /// brings its own counts back through the load edge (ExtendedInventory.Tick.cs).</summary>
    private void SeedBag()
    {
        foreach (var item in Items)
        {
            if (!_patcher.TryWrite(BagCountBase + item.Id, new[] { (byte)item.SeedCount }))
                ModLogger.Warn(LogVerb.Save, $"Could not place {item.SeedCount} {item.Name} in the bag at boot (write refused).");
        }
        ModLogger.Event(LogVerb.Save, "Extended-inventory bag counts seeded for a new game: "
            + string.Join(", ", Items.Select(i => $"{i.Name} x{i.SeedCount}")) + $"; saved games replay their own (sidecar: {_sidecar.LoadedFrom}, {_sidecar.SaveCount} save(s) known).");
    }
}
