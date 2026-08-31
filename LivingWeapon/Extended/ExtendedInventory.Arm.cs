using System;
using System.Linq;
using Reloaded.Hooks.Definitions;

namespace LivingWeapon;

/// <summary>
/// The arm half of the boot transaction: the ordered step list (<see cref="Install"/>, its thunk-
/// clone helper <see cref="InstallClone"/>) and the boot placement (<see cref="SeedBag"/>). Split
/// from ExtendedInventory.cs in LW-371 (verifier NIT-5, 201 lines) the way ExtendedInventory.Hooks.cs
/// already carved the detour half out in LW-351 fix round 7: the base file keeps the fields, the
/// ctor, <c>BootArm</c> (the outer sequencing: PE-key check, this file's <c>Install</c>, the arm/
/// refuse decision), <c>WeaponRowAddr</c>, <c>Refuse</c> and the public properties;
/// <c>ExtendedInventory.Hooks.cs</c> keeps <c>DefaultInstallHooks</c> and <c>RollBack</c>, the
/// four-hook tail this file's <c>Install</c> ends by calling into. Same D2 install order as
/// always: cap patches -&gt; list relocation -&gt; TEMPLATE relocation -&gt; catalog -&gt; shops -&gt;
/// clones -&gt; hooks; <c>RollBack</c> (Hooks.cs) undoes it in reverse.
/// </summary>
internal sealed partial class ExtendedInventory
{
    private string? Install(IReloadedHooks? hooks)
    {
        int n = Items.Count;
        string? why = _patches.Apply(_patcher, ExtendedSites.BootPatches(n));
        if (why != null) return why;
        // LW-368 round 2 (D3): the list relocation lands right after the boot cap patches and
        // before everything else, so every later step that touches a bag byte can resolve its
        // base through BagCountBase from the moment it runs.
        why = _relocation.Install(_patcher, _allocator);
        if (why != null) return why;
        // LW-371: the template relocation lands right after the list relocation, before the
        // catalog -- D2's install order (cap patches -> list relocation -> TEMPLATE relocation ->
        // catalog -> shops -> clones -> hooks). TemplateRegions resolves through it from here on.
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
        var weaponStat = new ThunkClone(Offsets.ThunkWeaponStat, "weapon-stat");
        why = InstallClone(weaponStat, t => ThunkStub.EmitRowStub(lo, rows, t));
        if (why != null) return why;
        _weaponStatClone = weaponStat;
        foreach (var (addr, label, usesArt) in ExtendedSites.DonorThunks)
        {
            var donors = usesArt ? artDonors : cloneDonors;
            why = InstallClone(new ThunkClone(addr, label), t => ThunkStub.EmitDonorStub(lo, donors, t));
            if (why != null) return why;
        }

        return _installHooks(hooks);
    }

    private string? InstallClone(ThunkClone clone, Func<long, byte[]> emit)
    {
        string? why = clone.Install(_patcher, _allocator, emit);
        if (why == null) _clones.Add(clone);
        return why;
    }

    /// <summary>Boot placement: the data seed for every extended id. No save is loaded at boot,
    /// so this is the bag a NEW game starts with; a loaded save replays its own recorded counts
    /// through the load edge (ExtendedInventory.Tick.cs, LW-353).</summary>
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
