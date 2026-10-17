using System;
using System.Collections.Generic;
using System.Linq;

namespace LivingWeapon;

/// <summary>
/// Schedules the icon cache hide (IconCacheHide.cs) and retries hides a battle postponed.
/// Off by default, so a pac-mirrored icon (IconGlow.Pac.cs) shows its new art after a relaunch.
/// A hider exception never escapes <see cref="HideThisPassPatched"/> or <see cref="RunPendingHidesOnly"/>;
/// the pac write already succeeded, so a failure leaves the icon restart-only.
/// </summary>
internal sealed partial class IconGlow
{
    internal static string KeptArtLine(int n) => $"IconGlow: left the drawn art on {n} icon surface(s); the new art shows from the next launch.";

    /// <summary>Hands every surface that landed in modded.pac this pass to <see cref="_hider"/>.
    /// Called at the end of <see cref="FlushPacBatch"/>.</summary>
    private void HideThisPassPatched()
    {
        if (_pacPatchedThisPass.Count == 0) return;
        if (!_hideDrawnIcons)
        {
            ModLogger.Debug(LogVerb.Display, KeptArtLine(_pacPatchedThisPass.Count));
            return;
        }
        var targets = _pacPatchedThisPass.Select(p => new IconCacheTarget(p.Id, p.Surface, p.Entry)).ToList();
        try
        {
            var hideResult = _hider.Hide(targets);
            UpdatePendingHides(targets, hideResult);
            _hidesThisApply = targets.Count(t => hideResult.Get(t.Id, t.Surface)?.Outcome == HideOutcome.Hidden);
        }
        catch (Exception)
        {
            ModLogger.Debug(LogVerb.Display, "IconCacheHide threw while hiding this pass' pac-patched icons; they stay restart-only this session.");
        }
    }

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

            UpdatePendingHides(targets, _hider.Hide(targets));
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
