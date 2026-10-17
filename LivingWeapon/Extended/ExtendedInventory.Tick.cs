using System.Collections.Generic;
using System.Linq;

namespace LivingWeapon;

/// <summary>
/// Tick half of <see cref="ExtendedInventory"/> (Engine phases "extended-caps", "extended-bag",
/// "extended-shops", every 30th tick, once LaunchGuard armed). Steps the post-load cap patches (none
/// since LW-388, the Denuvo-free build patches both damage caps at boot), follows the game's save edges
/// , refreshes the shop-table mirror and runs the LW-368 round 2 relocation tripwire (D8).
/// A save records its bag counts under its key; a load writes them back (an unseen save gets the
/// schema-1 legacy counts once, then the data seed). Since LW-351 the replay runs inside the load
/// detour (<see cref="ReplayOnLoad"/>), which also seats the menu order templates (<see cref="TemplateSeat"/>,
/// fix rounds 5 and 8b); the tick repeats both only for a load the detour did not serve (round 8c).
/// </summary>
internal sealed partial class ExtendedInventory
{
    private readonly List<PendingPatch> _pending;

    /// <summary>the resolution the load detour already applied, handed to the tick's drain
    /// so the key resolves once (a second resolve would spend the one-shot schema-1 migration in the
    /// detour and land the seed on the tick). Written before the load edge is published.</summary>
    private volatile BagReplay.Plan? _detourReplay;
    // Round 8b/8c: what the detour healed, joined and logged once by the tick (the detour never
    // logs on the game's thread).
    private volatile string? _repairNote;
    // Round 8d: what the detour could not do (no marker and no empty word, no room, a refused
    // write). The tick logs it once as Warn, since it no longer re-runs the seat after a served load.
    private volatile string? _refusalNote;

    public bool CapsSettled => _pending.All(p => p.Settled);
    public IReadOnlyList<PendingPatch> PendingCaps => _pending;

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
                    ModLogger.Warn(LogVerb.Engine, $"Extended inventory: the {p.Patch.Label} cap at 0x{p.Patch.Addr:X} reads {p.Observed:X2}, neither vanilla nor ours; not touching it, so the new items may punch instead of swing.");
                    break;
                case PendingPatch.Phase.Unwritable:
                    ModLogger.Warn(LogVerb.Engine, $"Extended inventory: the {p.Patch.Label} cap at 0x{p.Patch.Addr:X} refused the write; the new items may punch instead of swing.");
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
        // LW-368 round 2 (D8) hidden-writer tripwire. Two block reads per 30 ticks; it is the only
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
            // Round 8c: a served load was already seated on the game's thread, and a whole-table
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
