using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace LivingWeapon;

/// <summary>
/// Keeps each weapon's equip icon on the glow variant for its current kill tier, plain art at tier 0.
/// Out of battle it works on the deployed loose tex (modDir/FFTIVC/&lt;baseRel&gt;).
/// The modded.pac mirror (IconGlow.Apply.cs, IconGlow.Pac.cs) shows a change in the same session, ledger row
/// [live-icon-repaint], proven 2026-09-01. Ticked via TickGates.OutOfBattle cadence 30 (Engine.cs).
/// glow_icons/manifest.json loads lazily through <see cref="IIconGlowStore"/>; a bad manifest or schemaVersion stands
/// the subsystem down for the launch with one warn. Tick diffs tiers and starts the apply on <see cref="_runBackground"/>
/// so slow disk never stalls the 33ms loop; a patched surface goes to <see cref="_hider"/> only when hideDrawnIcons is on.
/// </summary>
internal sealed partial class IconGlow
{
    private readonly string _modDir;
    private readonly Dictionary<int, int> _kills;              // shared reference, Engine._kills
    private readonly HashSet<int> _weaponIds;                  // the meta id set -- U9's rejection filter
    private readonly IIconGlowStore _store;
    private readonly IIconCacheHider _hider;   // hides a re-tiered icon's cache records when _hideDrawnIcons is on
    private readonly bool _hideDrawnIcons;
    private readonly Action<Action> _runBackground;

    private readonly object _lock = new();
    private IconGlowManifest? _manifest;
    private Dictionary<int, List<IconGlowEntry>> _entriesById = new();
    private bool _standDown;
    private bool _applying;
    private bool _judgedAllOnce;   // FIX 1. True once the launch's first background apply is scheduled
    private readonly Dictionary<int, int> _applied = new();

    // Surfaces whose hide call returned Postponed (battle in progress), retried by
    // RunPendingHidesOnly in IconGlow.Hide.cs. Every other hider result leaves the set.
    private readonly HashSet<(int Id, string Surface)> _pendingHides = new();

    /// <param name="modDir">Mod deployment directory, used only in the manifest-loaded log line.</param>
    /// <param name="kills">The shared per-weapon kill tally (Engine._kills), held by reference.</param>
    /// <param name="weaponIds">The known weapon ids (meta.Keys); manifest entries for other ids are a stale bake and are dropped.</param>
    /// <param name="store">Wraps every file access; tests pass an in-memory fake.</param>
    /// <param name="hider">hides a pac-patched icon's cache records. Tests that do not exercise it pass a NoopIconCacheHider.</param>
    /// <param name="runBackground">Runs the judge-and-write work. Null schedules Task.Run; tests inject a synchronous runner.</param>
    /// <param name="hideDrawnIcons">true lets a patched surface go to the hider; production leaves it off because only the inventory is proven to reload a hidden icon.</param>
    public IconGlow(string modDir, Dictionary<int, int> kills, IEnumerable<int> weaponIds,
        IIconGlowStore store, IIconCacheHider hider, Action<Action>? runBackground = null, bool hideDrawnIcons = false)
    {
        _modDir = modDir;
        _kills = kills;
        _weaponIds = new HashSet<int>(weaponIds);
        _store = store;
        _hider = hider;
        _hideDrawnIcons = hideDrawnIcons;
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

        // An id whose desired tier is 0 never shows up in Diff (unseeded counts as 0), so a
        // tiered tex left by an earlier session would stay rimmed. The launch's first apply therefore
        // judges every manageable id; a real mismatch is written by the next tick's ordinary diff.
        // PlaythroughReset clears Engine._kills in place, so the next diff sees tier 0 and self-heals.
        bool firstApply;
        lock (_lock) firstApply = !_judgedAllOnce;
        if (changed.Count == 0 && !firstApply)
        {
            // A surface postponed by an in-battle hide still needs a retry (RunPendingHidesOnly
            // in IconGlow.Hide.cs), run under the same _applying latch.
            bool hasPendingHides;
            lock (_lock) hasPendingHides = _pendingHides.Count > 0;
            if (!hasPendingHides) return;
            lock (_lock) _applying = true;
            _runBackground(RunPendingHidesOnly);
            return;
        }

        List<int>? judgeAllIds = null;
        if (firstApply)
        {
            judgeAllIds = ManageableIds().ToList();
            lock (_lock) _judgedAllOnce = true;
        }

        // _applied may be unseeded here; the background judge corrects it from the deployed tex
        // before writing (IconGlow.Apply.cs JudgeId).
        lock (_lock) _applying = true;
        _runBackground(() => ApplyDiff(desired, changed, judgeAllIds));
    }

    private IEnumerable<int> ManageableIds() => _entriesById.Keys.Where(id => !_unmanaged.Contains(id));
}
