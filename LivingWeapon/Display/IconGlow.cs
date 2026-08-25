using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace LivingWeapon;

/// <summary>
/// LW-295 cycle B: out of battle, keeps every weapon's equip icon inside
/// &lt;game&gt;/data/enhanced/modded.pac spliced to the glow-rim variant matching its CURRENT kill
/// tier (base art at tier 0). Ticked only via TickGates.OutOfBattle cadence 30 (Engine.cs) --
/// kills only change in battle, menus live outside it, and file I/O during a fight buys nothing.
///
/// Lazily loads glow_icons/manifest.json on first tick through <see cref="IIconGlowStore"/> (ALL
/// file I/O goes through that seam; NO Mem/IGameMemory anywhere -- this is plain file I/O and
/// cannot AV). A missing/malformed manifest or an unsupported schemaVersion stands the whole
/// subsystem down PERMANENTLY for this launch with one WARN -- a data problem must never crash or
/// spam. Manifest ids outside the weapon set passed to the constructor are dropped at load
/// (defense against a stale bake). This file is the tick-facing half: manifest load, the
/// desired-vs-applied diff, and starting the background apply. IconGlow.Apply.cs (the real seam,
/// 200-line split) is the background half: building the pac needle index once per launch and
/// actually splicing bytes -- see its own class-level remarks for that contract.
///
/// Desired-vs-applied diffing (IconGlowPolicy.DesiredTiers/Diff) runs on the tick thread (cheap,
/// no I/O); when it is nonempty and no apply is already in flight, the actual splice work runs on
/// ONE background task (<see cref="_runBackground"/>, real Task.Run in production, an injectable
/// synchronous runner in tests -- mirrors FlightRecorder's own inject-the-IO-seam idiom) so a slow
/// disk never stalls Engine's one shared 33ms tick loop. The task never throws outward and the
/// tick never blocks on it.
///
/// NO PlaythroughReset hook is needed: PlaythroughReset clears the shared kills dict IN PLACE
/// (Engine._kills), so the very next out-of-battle diff sees every id back at tier 0 and
/// self-heals -- there is nothing for a reset edge to tell this class that the next tick doesn't
/// already discover on its own.
/// </summary>
internal sealed partial class IconGlow
{
    private readonly string _modDir;
    private readonly Dictionary<int, int> _kills;              // shared reference, Engine._kills
    private readonly HashSet<int> _weaponIds;                  // the meta id set -- U9's rejection filter
    private readonly IIconGlowStore _store;
    private readonly Action<Action> _runBackground;

    private readonly object _lock = new();
    private IconGlowManifest? _manifest;
    private Dictionary<int, List<IconGlowEntry>> _entriesById = new();
    private bool _standDown;
    private bool _applying;
    private readonly Dictionary<int, int> _applied = new();

    /// <param name="modDir">Mod deployment directory (held for the manifest-loaded log line only
    /// -- every actual read/write goes through <paramref name="store"/>).</param>
    /// <param name="kills">The shared per-weapon kill tally (Engine._kills), held by reference.</param>
    /// <param name="weaponIds">The known weapon ids (meta.Keys) -- a manifest entry for any other
    /// id is a stale bake and is rejected at load.</param>
    /// <param name="store">Wraps every file access; production passes a FileIconGlowStore, tests
    /// pass an in-memory fake.</param>
    /// <param name="runBackground">Runs the splice work. Null (production default) schedules a
    /// real Task.Run; tests inject a synchronous or capture-only runner for determinism.</param>
    public IconGlow(string modDir, Dictionary<int, int> kills, IEnumerable<int> weaponIds,
        IIconGlowStore store, Action<Action>? runBackground = null)
    {
        _modDir = modDir;
        _kills = kills;
        _weaponIds = new HashSet<int>(weaponIds);
        _store = store;
        _runBackground = runBackground ?? (a => Task.Run(a));
    }

    public void Tick()
    {
        if (_standDown) return;
        EnsureManifestLoaded();
        if (_standDown) return;

        lock (_lock) { if (_applying) return; }

        var desired = IconGlowPolicy.DesiredTiers(_kills, ManageableIds());
        Dictionary<int, int> changed;
        lock (_lock) changed = IconGlowPolicy.Diff(_applied, desired);
        if (changed.Count == 0) return;

        lock (_lock) _applying = true;
        _runBackground(() => ApplyDiff(changed));
    }

    private IEnumerable<int> ManageableIds() => _entriesById.Keys.Where(id => !_unmanaged.Contains(id));

    private void EnsureManifestLoaded()
    {
        if (_manifest != null || _standDown) return;
        var manifest = _store.ReadManifest();
        if (manifest == null) { StandDown("glow_icons/manifest.json is missing or malformed"); return; }
        if (manifest.SchemaVersion != 1) { StandDown($"manifest schemaVersion {manifest.SchemaVersion} is not the supported version 1"); return; }

        var kept = new List<IconGlowEntry>();
        int rejected = 0;
        foreach (var icon in manifest.Icons)
        {
            if (_weaponIds.Contains(icon.Id)) kept.Add(icon);
            else rejected++;
        }
        if (rejected > 0)
            ModLogger.Warn(LogVerb.Display, $"IconGlow's manifest listed {rejected} icon entr{(rejected == 1 ? "y" : "ies")} for weapon ids outside the current build; ignoring them (defense against a stale bake).");

        manifest.Icons = kept;
        var byId = new Dictionary<int, List<IconGlowEntry>>();
        foreach (var e in kept)
        {
            if (!byId.TryGetValue(e.Id, out var list)) byId[e.Id] = list = new List<IconGlowEntry>();
            list.Add(e);
        }
        _entriesById = byId;
        _manifest = manifest;
        ModLogger.Debug(LogVerb.Display, $"IconGlow: manifest loaded ({kept.Count} icon entr{(kept.Count == 1 ? "y" : "ies")}) from {_modDir}\\glow_icons.");
    }

    private void StandDown(string reason)
    {
        _standDown = true;
        ModLogger.Warn(LogVerb.Display, $"IconGlow standing down: {reason}. Weapon icons will not glow this launch (data-only failure, never a crash).");
    }
}
