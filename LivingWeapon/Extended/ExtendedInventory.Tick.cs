using System.Collections.Generic;
using System.Linq;

namespace LivingWeapon;

/// <summary>
/// Keeps the new items' counts with the right save file as the player saves and loads.
/// Runs as the Engine phases "extended-caps", "extended-bag" and "extended-shops", every 30th tick from the first.
/// It also steps the caps that wait for a save to load (none on the Denuvo-free build), refreshes the shop
/// mirror and watches the old item lists for a writer nobody moved.
/// A save files its counts under its key and a load writes them back. A save we never saw gets the old
/// schema 1 counts once, then the starting counts from the data. The load detour does the replay itself
/// (<see cref="ReplayOnLoad"/>) and seats the menu order charts (<see cref="TemplateSeat"/>), so the
/// tick applies the counts again as a harmless fallback and repeats the seat only for a load the detour missed.
/// </summary>
internal sealed partial class ExtendedInventory
{
    private readonly List<PendingPatch> _pending;

    /// <summary>What the load detour already put back, so the tick reuses it instead of looking the key up again.
    /// Set before the load is published.</summary>
    /// <remarks>The old schema 1 counts can be used only once, and a second lookup would hand the tick the
    /// starting counts instead.</remarks>
    private volatile BagReplay.Plan? _detourReplay;
    // What the detour repaired, logged once by the tick because the detour never logs on the
    // game's thread.
    private volatile string? _repairNote;
    // What the detour could not do (no marker and no empty word, no room, a refused write). The tick
    // warns about it once, since it does not seat again after a load the detour handled.
    private volatile string? _refusalNote;

    public bool CapsSettled => _pending.All(p => p.Settled);
    public IReadOnlyList<PendingPatch> PendingCaps => _pending;

    // The label and the address go in the detail tail, where only a bug report needs them.
    internal static string CapWarn(PendingPatch p)
    {
        string where = $"{p.Patch.Label} at 0x{p.Patch.Addr:X}";
        return p.State == PendingPatch.Phase.Foreign
            ? "A spot in the game's item code was already changed by something else, so it is left alone and the new items may punch instead of swinging."
                + PartsCopy.Detail($"{where} reads {p.Observed:X2}")
            : "A spot in the game's item code could not be changed, so the new items may punch instead of swinging."
                + PartsCopy.Detail($"{where} refused the write");
    }

    /// <summary>Tries the caps that wait for a save to load, until each one has settled.</summary>
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

    /// <summary>Copies the game's own shop flags into our mirror, in case something changed them.</summary>
    public void StepShopSync()
    {
        if (!Armed) return;
        if (_shops.Sync(_patcher))
            ModLogger.Debug(LogVerb.Engine, "Extended inventory: the shop table mirror was refreshed from the game's own table.");
    }

    /// <summary>After a rewind, works the new weapons' bag counts out again from what the party holds (RewindRecomputeStep).</summary>
    public void StepRewindRecompute()
    {
        if (!Armed) return;
        _rewindStep?.Step(++_rewindTick);
    }

    /// <summary>Puts the new items back right after a load, inside the load detour on the game's thread (SaveEdgeHooks.AfterApply).
    /// The game has just overwritten the bag and both menu order charts from the save, so this restores both; the tick writes the log line.</summary>
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

    /// <summary>Handles the latest save and load. The load goes first, so a save right after a load in the same 30 ticks
    /// records the restored counts. <paramref name="mem"/> is unused and only fits the tick's signature. True when this call handled a load.</summary>
    public bool StepBagSidecar(IGameMemory mem)
    {
        bool loaded = false;
        if (!Armed) return false;
        // Something still writing the old lists means a code reference the relocation missed, and
        // these two reads every 30 ticks are the only way to notice.
        string? tripwire = _relocation.CheckOldBlocks(_patcher);
        if (tripwire != null) ModLogger.Warn(LogVerb.Save, tripwire);
        if (Tracker.TryTakePendingLoad(out string loadKey))
        {
            loaded = true;
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
            // When the detour handled the load it already seated on the game's thread, and a second
            // whole chart write from this thread could collide with the game shifting that chart.
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
        return loaded;
    }
}
