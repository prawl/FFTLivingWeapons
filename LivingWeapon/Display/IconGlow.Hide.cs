using System;
using System.Collections.Generic;
using System.Linq;

namespace LivingWeapon;

/// <summary>
/// LW-381 second half: schedules and runs the cache hide (IconCacheHide.cs) that makes a
/// pac-mirrored icon (IconGlow.Pac.cs) visible THIS session instead of only after a relaunch --
/// split out as its own file because it is a distinct concern from the pac-mirror batching next
/// door: that file decides WHAT to write into modded.pac, this one decides WHAT to hide and WHEN
/// to retry a hide a battle interrupted. Fail-soft by design (D2/R11): a hider exception can
/// never surface past <see cref="HideThisPassPatched"/> or <see cref="RunPendingHidesOnly"/> --
/// the loose write and the pac write already succeeded before either ever runs, so a failed hide
/// just means "still restart-only this session", same as any other pac-mirror hiccup.
/// </summary>
internal sealed partial class IconGlow
{
    /// <summary>Called once, at the end of <see cref="FlushPacBatch"/>: hands every surface that
    /// actually landed in modded.pac THIS pass (<c>_pacPatchedThisPass</c>) to <see cref="_hider"/>.</summary>
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

    /// <summary>Shared by <see cref="HideThisPassPatched"/> and <see cref="RunPendingHidesOnly"/>:
    /// folds a hide call's per-surface outcomes into <see cref="_pendingHides"/> -- only
    /// <see cref="HideOutcome.Postponed"/> survives in the set (D3); every other outcome is
    /// terminal for that surface and leaves it.</summary>
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

    /// <summary>Scheduled by IconGlow.cs's Tick() (under the shared <c>_applying</c> latch) only
    /// when there is nothing else to apply and <see cref="_pendingHides"/> is non-empty --
    /// retries a surface an earlier in-battle hide attempt had to postpone.</summary>
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
