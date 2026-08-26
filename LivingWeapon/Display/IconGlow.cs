using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace LivingWeapon;

/// <summary>
/// LW-295 cycle B (display path replaced LW-336): out of battle, keeps every weapon's DEPLOYED
/// equip-icon loose tex (modDir/FFTIVC/&lt;baseRel&gt;) matched to the glow-rim variant for its
/// CURRENT kill tier (plain base art at tier 0) -- restart-visible via the modloader's own launch
/// merge, no modded.pac involvement anywhere in this subsystem any more. Ticked only via
/// TickGates.OutOfBattle cadence 30 (Engine.cs) -- kills only change in battle, menus live outside
/// it, and file I/O during a fight buys nothing.
///
/// Lazily loads glow_icons/manifest.json on first tick through <see cref="IIconGlowStore"/> (ALL
/// file I/O goes through that seam; NO Mem/IGameMemory anywhere -- this is plain file I/O and
/// cannot AV). A missing/malformed manifest or an unsupported schemaVersion stands the whole
/// subsystem down PERMANENTLY for this launch with one WARN -- a data problem must never crash or
/// spam. Manifest ids outside the weapon set passed to the constructor are dropped at load
/// (defense against a stale bake). This file is the tick-facing half: manifest load, the
/// desired-vs-applied diff, and starting the background apply. IconGlow.Apply.cs (the real seam,
/// 200-line split) is the background half: first-touch judging each id against its deployed tex
/// and writing the deployed tex itself -- see its own class-level remarks for that contract.
///
/// Desired-vs-applied diffing (IconGlowPolicy.DesiredTiers/Diff) runs on the tick thread (cheap,
/// no I/O); when it is nonempty OR this is the launch's first-ever apply and no apply is already
/// in flight, the actual write work runs on ONE background task (<see cref="_runBackground"/>,
/// real Task.Run in production, an injectable synchronous runner in tests -- mirrors
/// FlightRecorder's own inject-the-IO-seam idiom) so a slow disk never stalls Engine's one shared
/// 33ms tick loop. The task never throws outward and the tick never blocks on it.
///
/// LW-336 fix round, FIX 1 (the ship-blocker): Diff() alone is blind to an id whose DESIRED tier
/// is already 0 -- an unseeded id counts as tier 0 (see IconGlowPolicy.Diff's own remarks), so a
/// tiered tex left on disk from an earlier session (a restored low-kills save, glow_bounce.py 0,
/// a New Game reset followed by a fast quit) would never even enter the diff and would stay
/// rimmed forever. <see cref="_judgedAllOnce"/> closes this: the very FIRST time this launch
/// would otherwise skip scheduling a background apply (an empty diff), it schedules one anyway
/// with every manageable id passed through as <c>judgeAllIds</c>, so IconGlow.Apply.cs's
/// ApplyDiff judges (not necessarily writes) every one of them at least once. That seeds
/// <c>_applied</c> truthfully; if a real mismatch turns up, the NEXT tick's ordinary Diff sees
/// applied != desired and schedules a normal apply that performs the actual write (see
/// ApplyDiff's own remarks for why the write is deliberately deferred to that next pass, except
/// for the divergent-surfaces case that cannot wait).
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
    private bool _judgedAllOnce;   // FIX 1: true once the launch's first background apply is scheduled
    private readonly Dictionary<int, int> _applied = new();

    /// <param name="modDir">Mod deployment directory (held for the manifest-loaded log line only
    /// -- every actual read/write goes through <paramref name="store"/>).</param>
    /// <param name="kills">The shared per-weapon kill tally (Engine._kills), held by reference.</param>
    /// <param name="weaponIds">The known weapon ids (meta.Keys) -- a manifest entry for any other
    /// id is a stale bake and is rejected at load.</param>
    /// <param name="store">Wraps every file access; production passes a FileIconGlowStore, tests
    /// pass an in-memory fake.</param>
    /// <param name="runBackground">Runs the judge-and-write work. Null (production default)
    /// schedules a real Task.Run; tests inject a synchronous or capture-only runner for
    /// determinism.</param>
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

        // FIX 1: an empty diff on every OTHER tick means genuinely nothing to do, but on the
        // launch's first-ever apply it can just as easily mean every id's real on-disk tier
        // happens to be sitting at its unseeded default (0) -- indistinguishable from "nothing
        // to do" without actually reading the files. Force that one full judge through.
        bool firstApply;
        lock (_lock) firstApply = !_judgedAllOnce;
        if (changed.Count == 0 && !firstApply) return;

        List<int>? judgeAllIds = null;
        if (firstApply)
        {
            judgeAllIds = ManageableIds().ToList();
            lock (_lock) _judgedAllOnce = true;
        }

        // This diff can run against a not-yet-seeded _applied on an id's very first tick (it
        // counts a missing entry as tier 0). That is fine: ApplyDiff's background judge corrects
        // _applied from the real deployed tex before ever writing, and if the seeded tier already
        // equals this tier the write is skipped entirely -- see IconGlow.Apply.cs's JudgeId.
        lock (_lock) _applying = true;
        _runBackground(() => ApplyDiff(desired, changed, judgeAllIds));
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
        RejectBasenameCollisions(kept);
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

    /// <summary>FIX 5's tripwire: the base-backup store flattens every entry to just its base
    /// tex FILENAME (FileIconGlowStore.BackupPath uses Path.GetFileName, ignoring the rest of
    /// BaseRel), so two DIFFERENT ids whose baked BaseRel resolves to the same filename would
    /// silently cross-wire each other's pristine-base snapshots. Zero collisions exist in the
    /// real 242-entry manifest today; this guards a future bake rename from ever reaching the
    /// store. Marks every id in a colliding group unmanaged, once, with one warn naming the
    /// filename and every id it collided under -- never a per-icon repeat.</summary>
    private void RejectBasenameCollisions(List<IconGlowEntry> kept)
    {
        var idsByFilename = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in kept)
        {
            var name = Path.GetFileName(e.BaseRel);
            if (!idsByFilename.TryGetValue(name, out var ids)) idsByFilename[name] = ids = new List<int>();
            if (!ids.Contains(e.Id)) ids.Add(e.Id);
        }

        foreach (var (filename, ids) in idsByFilename)
        {
            if (ids.Count < 2) continue;
            foreach (var id in ids) _unmanaged.Add(id);
            ModLogger.Warn(LogVerb.Display,
                $"IconGlow leaves icons {string.Join(", ", ids)} plain this launch: they share the base tex filename \"{filename}\" (a bake bug; the backup store cannot tell them apart).");
        }
    }
}
