using System.Collections.Generic;
using System.Linq;

namespace LivingWeapon;

/// <summary>
/// The tick half of <see cref="ExtendedInventory"/> (Engine phases "extended-caps",
/// "extended-bag" and "extended-shops", every 30th tick, only once LaunchGuard armed):
///   - the two copy-protected damage caps (ExtendedSites.PostLoadPatches) are stepped as
///     <see cref="PendingPatch"/>es until each settles, each settle logged exactly once;
///   - the bag counts of the extended ids follow the game's own save edges (LW-353): when the
///     game serialized a save, the counts of that instant are recorded under that save's key;
///     when the game applied a loaded save (its bag now holds the file's 261 counts and nothing
///     for ours), that save's recorded counts are written back into the bag. A save never seen
///     before gets the schema-1 legacy counts once, then the data seed. Since LW-351 the replay
///     itself already ran INSIDE the load detour (<see cref="ReplayOnLoad"/>, the game's own
///     thread, the instant the load routine finished overwriting the bag from the file); the tick
///     re-applies the same resolution as an idempotent fallback and owns the one log line, and
///     both paths also seat the owned extended ids in the menu order templates the same load
///     restored out of the save struct (<see cref="TemplateSeat"/>, fix round 5).
///   - the shop-table mirror's vanilla half follows the game's own table.
/// Reads go through <see cref="IGameMemory"/> like every other tick-loop reader; the replay
/// write goes through the guarded patcher (the same seam the boot placement uses).
/// </summary>
internal sealed partial class ExtendedInventory
{
    private readonly List<PendingPatch> _pending;

    /// <summary>LW-351: the resolution the load DETOUR already applied on the game's thread,
    /// handed forward so the tick's drain re-uses it instead of resolving the same key twice (a
    /// second resolve would spend the one-shot schema-1 migration in the detour and then land the
    /// seed on the tick). Written BEFORE the tracker publishes the load edge, so the tick that
    /// takes that edge always sees it; a plain reference, read once and cleared by the drain.</summary>
    private volatile BagReplay.Plan? _detourReplay;

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

    /// <summary>LW-354: keep the shop-table mirror's vanilla half current.</summary>
    public void StepShopSync()
    {
        if (!Armed) return;
        if (_shops.Sync(_patcher))
            ModLogger.Debug(LogVerb.Engine, "Extended inventory: the shop table mirror was refreshed from the game's own table.");
    }

    /// <summary>LW-351: the load-edge repair, run INSIDE the game's load detour
    /// (SaveEdgeHooks.AfterApply) on the game's own thread, immediately after the original returns.
    /// That routine has just overwritten the bag from the save file (which knows nothing of the
    /// extended ids) and restored both menu order templates out of the save struct (which, for any
    /// save written before a new id ever seated, do not name it), so this puts both back: the
    /// extended ids' bag bytes and, for the ids the player owns, their place in each template. Every
    /// write goes through the same guarded patcher the boot placement uses, and it never logs: the
    /// tick owns the one line. Skips cleanly when the inventory is not armed or nothing is shipped,
    /// which is the same gate the tick's own drain sits behind.</summary>
    internal void ReplayOnLoad(string key)
    {
        if (!Armed || Items.Count == 0) return;
        var plan = BagReplay.Resolve(_sidecar, Items, key);
        BagReplay.Apply(_patcher, Items, plan);
        TemplateSeat.Apply(_patcher, BagReplay.OwnedIds(plan, Items));
        _detourReplay = plan;
    }

    /// <summary>LW-353: drain the save edges. The load edge is drained first so a save that
    /// followed a load in the same 30-tick window records the replayed counts, not the
    /// file's zeros. <paramref name="mem"/> is unused today (the edges carry their own reads)
    /// and kept so the phase's seam stays the tick-loop one.</summary>
    public void StepBagSidecar(IGameMemory mem)
    {
        if (!Armed) return;
        if (Tracker.TryTakePendingLoad(out string loadKey))
        {
            var fromDetour = _detourReplay;
            _detourReplay = null;
            var plan = fromDetour != null && fromDetour.Key == loadKey ? fromDetour : BagReplay.Resolve(_sidecar, Items, loadKey);
            BagReplay.Apply(_patcher, Items, plan, (item, n) =>
                ModLogger.Warn(LogVerb.Save, $"Could not place {n} {item.Name} in the bag after the load (write refused)."));
            ModLogger.Event(LogVerb.Save, $"A save was loaded (key {loadKey}); extended-inventory bag counts placed from {plan.Source}: "
                + BagReplay.Describe(plan, Items) + ".");
            TemplateSeat.Apply(_patcher, BagReplay.OwnedIds(plan, Items),
                why => ModLogger.Warn(LogVerb.Save, why), what => ModLogger.Event(LogVerb.Save, what));
        }
        if (Tracker.TryTakePendingSave(out string saveKey, out var saved))
        {
            _sidecar.RecordSave(saveKey, saved);
            ModLogger.Event(LogVerb.Save, $"A save was written (key {saveKey}); extended-inventory bag counts recorded for it: "
                + string.Join(", ", Items.Select(i => $"{i.Name} x{(saved.TryGetValue(i.Id, out int c) ? c : 0)}")) + ".");
        }
    }
}
