using System.Collections.Generic;
using System.Linq;

namespace LivingWeapon;

/// <summary>
/// The per-tick half of <see cref="ExtendedInventory"/>, which follows the game's save edges.
/// It runs as Engine phases "extended-caps", "extended-bag" and "extended-shops", every 30th tick from the first.
/// It steps the post-load cap patches (none on the Denuvo-free build, which patches both damage caps at boot),
/// refreshes the shop-table mirror and runs the relocation tripwire.
/// A save records its bag counts under its key; a load writes them back (an unseen save gets the
/// schema-1 legacy counts once, then the data seed). The replay runs inside the load
/// detour (<see cref="ReplayOnLoad"/>), which also seats the menu order templates (<see cref="TemplateSeat"/>,
/// fix rounds 5 and 8b); the tick repeats both only for a load the detour did not serve.
/// </summary>
internal sealed partial class ExtendedInventory
{
    private readonly List<PendingPatch> _pending;

    /// <summary>the resolution the load detour already applied, handed to the tick's drain
    /// so the key resolves once (a second resolve would spend the one-shot schema-1 migration in the
    /// detour and land the seed on the tick). Written before the load edge is published.</summary>
    private volatile BagReplay.Plan? _detourReplay;
    // Round 8b/8c. What the detour healed, joined and logged once by the tick (the detour never
    // logs on the game's thread).
    private volatile string? _repairNote;
    // Round 8d. What the detour could not do (no marker and no empty word, no room, a refused
    // write). The tick logs it once as Warn, since it no longer re-runs the seat after a served load.
    private volatile string? _refusalNote;

    public bool CapsSettled => _pending.All(p => p.Settled);
    public IReadOnlyList<PendingPatch> PendingCaps => _pending;

    // The label and the address stay in the detail tail, where only a bug report needs them.
    internal static string CapWarn(PendingPatch p)
    {
        string where = $"{p.Patch.Label} at 0x{p.Patch.Addr:X}";
        return p.State == PendingPatch.Phase.Foreign
            ? "A spot in the game's item code was already changed by something else, so it is left alone and the new items may punch instead of swinging."
                + PartsCopy.Detail($"{where} reads {p.Observed:X2}")
            : "A spot in the game's item code could not be changed, so the new items may punch instead of swinging."
                + PartsCopy.Detail($"{where} refused the write");
    }

    /// <summary>Step the copy-protected caps. No-op until armed or once all are settled.</summary>
    public void StepPostLoadCaps()
    {
        if (!Armed) return;
        foreach (var p in _pending)
        {
            if (!p.Step(_patcher)) continue;
            switch (p.State)
            {
                case PendingPatch.Phase.Applied:
                    ModLogger.Event(LogVerb.Engine, $"Extended inventory: the {p.Patch.Label} cap is widened (the new items now deal weapon damage).");
                    break;
                case PendingPatch.Phase.AlreadyPatched:
                    ModLogger.Event(LogVerb.Engine, $"Extended inventory: the {p.Patch.Label} cap was already widened by something else (the research rig?); leaving it.");
                    break;
                case PendingPatch.Phase.Foreign:
                case PendingPatch.Phase.Unwritable:
                    ModLogger.Warn(LogVerb.Engine, CapWarn(p));
                    break;
            }
        }
    }

    /// <summary>keep the shop-table mirror's vanilla half current.</summary>
    public void StepShopSync()
    {
        if (!Armed) return;
        if (_shops.Sync(_patcher))
            ModLogger.Debug(LogVerb.Engine, "Extended inventory: the shop table mirror was refreshed from the game's own table.");
    }

    /// <summary>load-edge repair, run inside the game's load detour (SaveEdgeHooks.AfterApply)
    /// on the game's thread. The load routine has just overwritten the bag and restored both menu order
    /// templates from the save, so this puts back the extended ids' bag bytes and their template slots.
    /// Writes go through the guarded patcher and the tick owns the log line. Skips when not armed or
    /// nothing is shipped.</summary>
    internal void ReplayOnLoad(string key)
    {
        if (!Armed || Items.Count == 0) return;
        var plan = BagReplay.Resolve(_sidecar, Items, key);
        BagReplay.Apply(_patcher, Items, plan, BagCountBase);
        TemplateSeat.Apply(_patcher, BagReplay.OwnedIds(plan, Items),
            onRefused: why => _refusalNote = _refusalNote == null ? why : _refusalNote + " | " + why,
            onRepaired: note => _repairNote = _repairNote == null ? note : _repairNote + " | " + note,
            regions: TemplateRegions, firstStaleId: ExtendedCatalog.FirstExtendedId + Items.Count);
        _detourReplay = plan;
    }

    /// <summary>drain the save edges, load first so a save that followed a load in the same
    /// 30-tick window records the replayed counts. <paramref name="mem"/> is unused and kept as the
    /// tick-loop seam.</summary>
    public void StepBagSidecar(IGameMemory mem)
    {
        if (!Armed) return;
        // Hidden-writer tripwire. Two block reads per 30 ticks; it is the only
        // detector for a code reference the relocation's static sweep missed.
        string? tripwire = _relocation.CheckOldBlocks(_patcher);
        if (tripwire != null) ModLogger.Warn(LogVerb.Save, tripwire);
        if (Tracker.TryTakePendingLoad(out string loadKey))
        {
            var fromDetour = _detourReplay;
            _detourReplay = null;
            bool detourServed = fromDetour != null && fromDetour.Key == loadKey;
            var plan = detourServed ? fromDetour! : BagReplay.Resolve(_sidecar, Items, loadKey);
            BagReplay.Apply(_patcher, Items, plan, BagCountBase, (item, n) =>
                ModLogger.Warn(LogVerb.Save, $"Could not place {n} {item.Name} in the bag after the load (write refused)."));
            ModLogger.Event(LogVerb.Save, $"A save was loaded (key {loadKey}); extended-inventory bag counts placed from {plan.Source}: "
                + BagReplay.Describe(plan, Items) + ".");
            string? note = _repairNote;
            _repairNote = null;
            if (note != null) ModLogger.Event(LogVerb.Save, "A damaged menu order table was healed on the load: " + note);
            string? refused = _refusalNote;
            _refusalNote = null;
            if (refused != null) ModLogger.Warn(LogVerb.Save, refused);
            // Round 8c. A served load was already seated on the game's thread, and a whole-table
            // write from the engine thread could race the game's maintainer mid-shift.
            if (!detourServed)
                TemplateSeat.Apply(_patcher, BagReplay.OwnedIds(plan, Items),
                    why => ModLogger.Warn(LogVerb.Save, why), what => ModLogger.Event(LogVerb.Save, what),
                    what => ModLogger.Event(LogVerb.Save, "A damaged menu order table was healed on the load: " + what),
                    regions: TemplateRegions, firstStaleId: ExtendedCatalog.FirstExtendedId + Items.Count);
        }
        if (Tracker.TryTakePendingSave(out string saveKey, out var saved))
        {
            _sidecar.RecordSave(saveKey, saved);
            ModLogger.Event(LogVerb.Save, $"A save was written (key {saveKey}); extended-inventory bag counts recorded for it: "
                + string.Join(", ", Items.Select(i => $"{i.Name} x{(saved.TryGetValue(i.Id, out int c) ? c : 0)}")) + ".");
        }
    }
}
