using System;
using System.Collections.Generic;
using System.Linq;

namespace LivingWeapon;

/// <summary>
/// Schedules the cache hide (IconCacheHide.cs) that shows a pac-mirrored icon
/// (IconGlow.Pac.cs) this session instead of after a relaunch, and retries hides a battle postponed.
/// A hider exception never escapes <see cref="HideThisPassPatched"/> or <see cref="RunPendingHidesOnly"/> (D2/R11);
/// the pac write already succeeded, so a failure leaves the icon restart-only.
/// </summary>
internal sealed partial class IconGlow
{
    /// <summary>Hands every surface that landed in modded.pac this pass to <see cref="_hider"/>.
    /// Called at the end of <see cref="FlushPacBatch"/>.</summary>
    private void HideThisPassPatched()
    {
        if (_pacPatchedThisPass.Count == 0) return;
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
    /// <see cref="HideOutcome.Postponed"/> stays queued (D3); other outcomes are final.</summary>
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

    /// <summary>Scheduled by IconGlow.cs Tick() under the <c>_applying</c> latch when nothing else is
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
