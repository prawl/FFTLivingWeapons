using System.Collections.Generic;
using System.Linq;

namespace LivingWeapon;

/// <summary>
/// The tick half of <see cref="ExtendedInventory"/> (Engine phases "extended-caps" and
/// "extended-bag", every 30th tick, only once LaunchGuard armed, i.e. a save has loaded):
///   - the two copy-protected damage caps (ExtendedSites.PostLoadPatches) are stepped as
///     <see cref="PendingPatch"/>es until each settles, each settle logged exactly once;
///   - the bag counts of the extended ids are read back and, on any change, written to the
///     LW-348 sidecar so the next boot replays them (the save file cannot carry them).
/// Reads go through <see cref="IGameMemory"/> like every other tick-loop reader.
/// </summary>
internal sealed partial class ExtendedInventory
{
    private readonly List<PendingPatch> _pending;
    private readonly Dictionary<int, int> _lastCounts = new();
    private readonly Dictionary<int, int> _scratch = new();

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

    /// <summary>LW-354: keep the shop-table mirror's vanilla half current (the modloader applies
    /// every mod's ItemShopsData after this arm; a partner mod's shop edits must still land).
    /// Logs once per copy at Debug.</summary>
    public void StepShopSync()
    {
        if (!Armed) return;
        if (_shops.Sync(_patcher))
            ModLogger.Debug(LogVerb.Engine, "Extended inventory: the shop table mirror was refreshed from the game's own table.");
    }

    /// <summary>Read the live bag counts of the extended ids and persist them when any changed.</summary>
    public void StepBagSidecar(IGameMemory mem)
    {
        if (!Armed) return;
        _scratch.Clear();
        bool changed = false;
        foreach (var item in Items)
        {
            int now = mem.U8(Offsets.BagCountArray + item.Id);
            _scratch[item.Id] = now;
            if (!_lastCounts.TryGetValue(item.Id, out int last) || last != now) changed = true;
        }
        if (!changed) return;
        foreach (var kv in _scratch) _lastCounts[kv.Key] = kv.Value;
        _sidecar.Update(_scratch);
        ModLogger.Debug(LogVerb.Save, "Extended-inventory bag counts saved: " + string.Join(", ", Items.Select(i => $"{i.Name} x{_lastCounts[i.Id]}")) + ".");
    }
}
