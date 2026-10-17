using System;
using System.Linq;
using Reloaded.Hooks.Definitions;

namespace LivingWeapon;

/// <summary>
/// The arm half of the boot transaction. The ordered step list (<see cref="Install"/>, with its
/// accessor-hook helper <see cref="InstallAccessorHook"/>) and the boot placement (<see cref="SeedBag"/>).
/// Split from ExtendedInventory.cs in LW-371, as ExtendedInventory.Hooks.cs was carved out in LW-351.
/// Install order (D2) is cap patches, list relocation, template relocation, catalog, shops,
/// accessor hooks, hooks; <c>RollBack</c> in Hooks.cs undoes it in reverse.
/// </summary>
internal sealed partial class ExtendedInventory
{
    private string? Install(IReloadedHooks? hooks)
    {
        int n = Items.Count;
        string? why = _patches.Apply(_patcher, ExtendedSites.BootPatches(n));
        if (why != null) return why;
        // LW-368 round 2 (D3): the list relocation goes right after the cap patches so later steps
        // can resolve bag bytes through BagCountBase.
        why = _relocation.Install(_patcher, _allocator);
        if (why != null) return why;
        // The template relocation follows the list relocation, before the catalog.
        // TemplateRegions resolves through it from here on.
        why = _templates.Install(_patcher, _allocator);
        if (why != null) return why;
        why = _catalog.Install(_patcher, _allocator, Items.Select(i => (i.Id, i.CatalogRecord)).ToList());
        if (why != null) return why;
        why = _shops.Install(_patcher, _allocator, Items.Select(i => (i.Id, i.ShopFlags)).ToList());
        if (why != null) return why;

        int lo = ExtendedCatalog.FirstExtendedId;
        var rows = Items.OrderBy(i => i.Id).Select(i => i.WeaponRow).ToArray();
        var cloneDonors = Items.OrderBy(i => i.Id).Select(i => i.CloneDonor).ToArray();
        var artDonors = Items.OrderBy(i => i.Id).Select(i => i.ArtDonor).ToArray();
        var weaponStat = new AccessorEntryHook(ExtendedDonorThunks.WeaponStat);
        why = InstallAccessorHook(weaponStat, t => ThunkStub.EmitRowStub(lo, rows, t));
        if (why != null) return why;
        _weaponStatHook = weaponStat;
        foreach (var (entry, usesArt) in ExtendedDonorThunks.DonorThunks)
        {
            var donors = usesArt ? artDonors : cloneDonors;
            why = InstallAccessorHook(new AccessorEntryHook(entry), t => ThunkStub.EmitDonorStub(lo, donors, t));
            if (why != null) return why;
        }

        // The swing-id fallback needs the accessor hooks (they make an extended id drawable),
        // so it goes right after them and before the detours.
        var fallback = new SwingIdFallbackHook();
        why = fallback.Install(_patcher, _allocator, lo, Items.Count);
        if (why != null) return why;
        _swingIdFallback = fallback;

        return _installHooks(hooks);
    }

    private string? InstallAccessorHook(AccessorEntryHook hook, Func<long, byte[]> emit)
    {
        string? why = hook.Install(_patcher, _allocator, emit);
        if (why == null) _accessorHooks.Add(hook);
        return why;
    }

    /// <summary>Boot placement. The data seed for every extended id. No save is loaded at boot, so
    /// this is a new game's bag; a loaded save replays its own counts through the load edge
    /// (ExtendedInventory.Tick.cs, LW-353).</summary>
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
