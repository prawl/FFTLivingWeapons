using System;
using System.Collections.Generic;
using System.Linq;

namespace LivingWeapon;

/// <summary>
/// Schedules the icon cache hide (IconCacheHide.cs) and retries hides a battle postponed.
/// Hides only while the heal hook is armed, so a hidden icon is redrawn the same session; otherwise the art waits for a relaunch.
/// A hider exception never escapes <see cref="HideThisPassPatched"/> or <see cref="RunPendingHidesOnly"/>;
/// the pac write already succeeded, so a failure leaves the icon on its old art until a relaunch.
/// </summary>
internal sealed partial class IconGlow
{
    internal static string KeptArtLine(int n) => $"IconGlow: left the drawn art on {n} icon surface(s); the new art shows from the next launch.";

    /// <summary>Hands every surface that landed in modded.pac this pass to <see cref="_hider"/>.
    /// Called at the end of <see cref="FlushPacBatch"/>.</summary>
    private void HideThisPassPatched()
    {
        if (_pacPatchedThisPass.Count == 0) return;
        if (!CanHide)
        {
            ModLogger.Debug(LogVerb.Display, KeptArtLine(_pacPatchedThisPass.Count));
            return;
        }
        var targets = _pacPatchedThisPass.Select(p => new IconCacheTarget(p.Id, p.Surface, p.Entry)).ToList();
        if (WaitForQuiet(targets, announce: true)) return;
        try
        {
            _healer!.Track(PartsUris(targets));
            var hideResult = _hider.Hide(targets);
            UpdatePendingHides(targets, hideResult);
            _hidesThisApply = targets.Count(t => hideResult.Get(t.Id, t.Surface)?.Outcome == HideOutcome.Hidden);
        }
        catch (Exception)
        {
            ModLogger.Debug(LogVerb.Display, "IconCacheHide threw while hiding this pass' pac-patched icons; they stay restart-only this session.");
        }
    }

    private const long StarvedAfterMs = 10_000;
    private long _parkedSinceMs = long.MinValue;
    private bool _starvedLogged;

    internal Func<long> Clock { get; set; } = () => Environment.TickCount64;

    /// <summary>Parks the targets while a screen is loading. True means the hide must not run now.</summary>
    private bool WaitForQuiet(List<IconCacheTarget> targets, bool announce)
    {
        if (_healer!.BuildersQuiet)
        {
            _parkedSinceMs = long.MinValue;
            return false;
        }
        long now = Clock();
        lock (_lock)
        {
            foreach (var t in targets) _pendingHides.Add((t.Id, t.Surface));
            if (_parkedSinceMs == long.MinValue) _parkedSinceMs = now;
        }
        if (announce) ModLogger.Debug(LogVerb.Display, "IconGlow: hide waits, a screen is still loading.");
        if (!_starvedLogged && now - _parkedSinceMs > StarvedAfterMs)
        {
            _starvedLogged = true;
            ModLogger.Event(LogVerb.Display, $"Same session glow rings have waited {(now - _parkedSinceMs) / 1000} seconds for the menus to go quiet; they show after the next quiet moment.");
        }
        return true;
    }

    private bool CanHide => _hideDrawnIcons && _healer?.Armed == true;

    /// <summary>The cache name each target's parts records carry, which is what the heal hook matches.</summary>
    private static IEnumerable<string> PartsUris(IEnumerable<IconCacheTarget> targets)
        => targets.SelectMany(t => IconCacheHidePolicy.Needles(t.Id)
            .Where(n => n.Kind == NeedleKind.PartsUri && n.Surface == t.Surface).Select(n => n.Text));

    /// <summary>Folds a hide call's per-surface outcomes into <see cref="_pendingHides"/>. Only
    /// <see cref="HideOutcome.Postponed"/> stays queued; other outcomes are final.</summary>
    private void UpdatePendingHides(List<IconCacheTarget> targets, IconCacheHideResult hideResult)
    {
        lock (_lock)
        {
            foreach (var t in targets)
            {
                bool postponed = hideResult.Get(t.Id, t.Surface)?.Outcome == HideOutcome.Postponed;
                if (postponed) _pendingHides.Add((t.Id, t.Surface));
                else _pendingHides.Remove((t.Id, t.Surface));
            }
        }
    }

    /// <summary>Scheduled by IconGlow.cs Tick under the <c>_applying</c> latch when nothing else is
    /// pending; retries surfaces an earlier in-battle hide postponed.</summary>
    private void RunPendingHidesOnly()
    {
        try
        {
            List<(int Id, string Surface)> pending;
            lock (_lock) pending = _pendingHides.ToList();
            if (pending.Count == 0) return;

            var targets = new List<IconCacheTarget>();
            foreach (var (id, surface) in pending)
            {
                if (!_entriesById.TryGetValue(id, out var entries)) continue;
                var entry = entries.FirstOrDefault(e => e.Surface == surface);
                if (entry != null) targets.Add(new IconCacheTarget(id, surface, entry));
            }

            if (targets.Count == 0)
            {
                lock (_lock) foreach (var p in pending) _pendingHides.Remove(p);
                return;
            }

            if (!CanHide)
            {
                lock (_lock) foreach (var p in pending) _pendingHides.Remove(p);
                ModLogger.Debug(LogVerb.Display, KeptArtLine(targets.Count));
                return;
            }
            if (WaitForQuiet(targets, announce: false)) return;
            _healer!.Track(PartsUris(targets));
            var result = _hider.Hide(targets);
            UpdatePendingHides(targets, result);
            int hidden = targets.Count(t => result.Get(t.Id, t.Surface)?.Outcome == HideOutcome.Hidden);
            if (hidden > 0) ModLogger.Debug(LogVerb.Display, $"IconGlow: pending retry, hide: {hidden} surface(s) hidden.");
        }
        catch (Exception)
        {
            ModLogger.Debug(LogVerb.Display, "IconCacheHide threw during a pending-hide retry; it stays queued for the next attempt.");
        }
        finally
        {
            lock (_lock) _applying = false;
        }
    }
}
