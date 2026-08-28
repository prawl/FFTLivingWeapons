using System.Collections.Generic;
using System.Linq;

namespace LivingWeapon;

/// <summary>
/// The tick half of <see cref="ExtendedInventory"/> (Engine phases "extended-caps",
/// "extended-bag" and "extended-shops", every 30th tick, only once LaunchGuard armed):
///   - the two copy-protected damage caps (ExtendedSites.PostLoadPatches) are stepped as
///     <see cref="PendingPatch"/>es until each settles, each settle logged exactly once;
///   - the bag counts of the extended ids follow the game's own save edges (LW-353): when the
///     game serialised a save, the counts of that instant are recorded under that save's key;
///     when the game applied a loaded save (its bag now holds the file's 261 counts and nothing
///     for ours), that save's recorded counts are written back into the bag. A save never seen
///     before gets the schema-1 legacy counts once, then the data seed.
///   - the shop-table mirror's vanilla half follows the game's own table.
/// Reads go through <see cref="IGameMemory"/> like every other tick-loop reader; the replay
/// write goes through the guarded patcher (the same seam the boot placement uses).
/// </summary>
internal sealed partial class ExtendedInventory
{
    private readonly List<PendingPatch> _pending;

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

    /// <summary>LW-353: drain the save edges. The load edge is drained first so a save that
    /// followed a load in the same 30-tick window records the replayed counts, not the
    /// file's zeros. <paramref name="mem"/> is unused today (the edges carry their own reads)
    /// and kept so the phase's seam stays the tick-loop one.</summary>
    public void StepBagSidecar(IGameMemory mem)
    {
        if (!Armed) return;
        if (Tracker.TryTakePendingLoad(out string loadKey))
        {
            IReadOnlyDictionary<int, int> counts;
            string source;
            if (_sidecar.TryGetSave(loadKey, out var known)) { counts = known; source = "its own recorded counts"; }
            else
            {
                var legacy = _sidecar.TakeLegacy();
                if (legacy != null) { counts = legacy; source = "the pre-LW-353 counts (one-time migration)"; _sidecar.PersistAfterLegacyTaken(); }
                else { counts = Items.ToDictionary(i => i.Id, i => i.SeedCount); source = "the first-copy seed (this save was never seen with the mod running)"; }
            }
            foreach (var item in Items)
            {
                int n = counts.TryGetValue(item.Id, out int c) ? c : item.SeedCount;
                if (!_patcher.TryWrite(Offsets.BagCountArray + item.Id, new[] { (byte)n }))
                    ModLogger.Warn(LogVerb.Save, $"Could not place {n} {item.Name} in the bag after the load (write refused).");
            }
            ModLogger.Event(LogVerb.Save, $"A save was loaded (key {loadKey}); extended-inventory bag counts placed from {source}: "
                + string.Join(", ", Items.Select(i => $"{i.Name} x{(counts.TryGetValue(i.Id, out int c) ? c : i.SeedCount)}")) + ".");
        }
        if (Tracker.TryTakePendingSave(out string saveKey, out var saved))
        {
            _sidecar.RecordSave(saveKey, saved);
            ModLogger.Event(LogVerb.Save, $"A save was written (key {saveKey}); extended-inventory bag counts recorded for it: "
                + string.Join(", ", Items.Select(i => $"{i.Name} x{(saved.TryGetValue(i.Id, out int c) ? c : 0)}")) + ".");
        }
    }
}
