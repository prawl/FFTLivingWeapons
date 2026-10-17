using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// AttackCard compose and write: takes the row and tail plan from AttackCard.Resolve.cs (cursor
/// only), rotates the anchor when the composed text changes, and verifies and writes each cached
/// copy through AttackRow, evicting copies that no longer hold a known image.
/// </summary>
internal sealed partial class AttackCard
{
    /// <summary>Recomposes every tick. A content change rotates the anchor and repaints all cached
    /// copies at once; otherwise repaints run at the MaintenanceMs cadence. The Debug lines carry
    /// ComposeCurrentPlan's Note (weapon id and cursor provenance), which the 2026-07-06
    /// wrong-weapon diagnosis lacked.</summary>
    private void RepaintDriver()
    {
        var (plan, note) = ComposeCurrentPlan();
        bool sameAsCurrent = _currentImage != null
            ? plan != null && ByteEq(plan.Value.Image, _currentImage)
            : plan == null;

        if (!sameAsCurrent)
        {
            if (plan != null)
                ModLogger.Debug(LogVerb.Display, $"attack row now carries the acting unit's dossier: {note}");
            else
                ModLogger.Debug(LogVerb.Display, $"attack row reverting to vanilla: {note}");

            if (_currentImage != null) _previousImage = _currentImage;
            _currentImage = plan?.Image;
            _currentRowChars = plan?.RowNameChars ?? 0;
            RepaintAll();
            return;
        }

        long now = _nowMs();
        if (now - _lastMaintenanceMs < MaintenanceMs) return;
        _lastMaintenanceMs = now;
        RepaintAll();
    }

    /// <summary>a copy that fails SyncHit is kept for a grace period (Hit.FirstFailMs, 0 =
    /// healthy) instead of evicted at once, since the usual failure is a transient misread of a live
    /// buffer. Strike tracking is here and not in SyncHit because FindHits in AttackCard.Census.cs
    /// also uses SyncHit and needs instant rejection. Each Hit has its own episode.</summary>
    private void RepaintAll()
    {
        long now = _nowMs();
        List<(Hit hit, SyncOutcome outcome)>? toEvict = null;
        foreach (var hit in _hits)
        {
            var outcome = SyncHit(hit);
            if (outcome == SyncOutcome.Ok) { hit.FirstFailMs = 0; continue; }

            if (hit.FirstFailMs == 0)
            {
                // Episode start. Skip the census flag while a sweep runs, since it visits everything anyway.
                hit.FirstFailMs = now;
                ModLogger.Debug(LogVerb.Display, $"attack row copy unverified ({outcome.Phrase()}): retained pending recovery");
                if (!_scanning) _needsCensus = true;
                continue;
            }

            if (now - hit.FirstFailMs >= Tuning.AttackCardEvictAfterMs)
                (toEvict ??= new List<(Hit, SyncOutcome)>()).Add((hit, outcome));
        }

        if (toEvict == null) return;
        // Evictions are rare, so log each with its reason, plus a flight record with the copy's
        // address (the log line alone never said which copy; 2026-07-21 recon).
        foreach (var (h, outcome) in toEvict)
        {
            ModLogger.Debug(LogVerb.Display, $"attack row copy evicted ({outcome.Phrase()}): re-censusing");
            _recorder?.Invoke("card", $"evicted {outcome.Phrase()} labelAddr=0x{h.LabelAddr:X}");
            _hits.Remove(h);
        }
        // Always arm: an in-flight census snapshot can predate the replacement allocation.
        _needsCensus = true;
    }

    /// <summary>Verifies a cached copy holds a known image, then writes the desired state via AttackRow
    /// unless it already matches. Returns Ok while the copy is live (including a not-yet-writable
    /// retry); any other outcome says why it is foreign and the caller evicts it. enc==2 copies skip
    /// the split image (see AttackCard.cs) and use a vanilla-only sync.</summary>
    private SyncOutcome SyncHit(Hit hit)
    {
        if (hit.Enc == 2) return SyncHitEnc2(hit);

        byte[] labelPattern = AttackCardProbeText.Pattern(1);
        if (!_mem.TryReadBytes(hit.LabelAddr, labelPattern.Length, out var curLabel) || !ByteEq(curLabel, labelPattern))
            return SyncOutcome.LabelGone;   // buffer no longer holds the Attack label

        if (!_mem.TryReadBytes(hit.DescAddr, AttackRow.FootprintBytes, out var footprint)) return SyncOutcome.DescUnreadable;

        bool isKnown = ByteEq(footprint, VanillaImage)
                    || (_currentImage != null && ByteEq(footprint, _currentImage))
                    || (_previousImage != null && ByteEq(footprint, _previousImage));
        if (!isKnown)
        {
            if (_attackRow.Classify(hit.LabelAddr) == AttackRowShape.Ours) _attackRow.Restore(hit.LabelAddr);
            return SyncOutcome.ForeignFootprint;
        }

        // Re-pin to the vanilla desc's 73 chars. Only the enc2 footprint check (SyncHitEnc2)
        // depends on it; enc1 images always fit AttackRow.FootprintBytes.
        hit.DescChars = AttackCardText.DefaultBudgetChars;

        byte[] desiredImage = _currentImage ?? VanillaImage;
        if (ByteEq(footprint, desiredImage)) return SyncOutcome.Ok;   // skip-if-equal

        if (_currentImage != null)
        {
            var shape = _attackRow.Classify(hit.LabelAddr);
            if (shape != AttackRowShape.Vanilla && shape != AttackRowShape.Ours)
                return SyncOutcome.ShapeMismatch;   // record shape disagrees with the footprint: evict
            if (!_mem.Writable(hit.DescAddr, AttackRow.FootprintBytes)) return SyncOutcome.Ok;   // retry next pass
            _attackRow.Paint(hit.LabelAddr, desiredImage, _currentRowChars);
            ModLogger.Debug(LogVerb.Display, "attack row repainted");
            return SyncOutcome.Ok;
        }

        var vshape = _attackRow.Classify(hit.LabelAddr);
        if (vshape == AttackRowShape.Ours)
        {
            _attackRow.Restore(hit.LabelAddr);   // restores whatever the text says, so no painted row is stranded
            ModLogger.Debug(LogVerb.Display, "attack row reverted to vanilla");
            return SyncOutcome.Ok;
        }
        if (vshape == AttackRowShape.Vanilla)
        {
            // Record is vanilla-shaped; rewriting a stale footprint is this caller's job (see AttackRow.Restore).
            if (!_mem.Writable(hit.DescAddr, AttackRow.FootprintBytes)) return SyncOutcome.Ok;
            _mem.WriteBytes(hit.DescAddr, VanillaImage);
            return SyncOutcome.Ok;
        }
        return SyncOutcome.ForeignShape;   // foreign or unreadable shape: evict
    }

    /// <summary>Best-effort vanilla restore for ResetBattle, run before the cache is cleared. Foreign
    /// buffers are left alone; an Ours-shaped record is restored whatever its footprint says. Never throws.</summary>
    private void RestoreVanillaBestEffort()
    {
        foreach (var hit in _hits)
        {
            try
            {
                if (hit.Enc == 2) { RestoreVanillaBestEffortEnc2(hit); continue; }

                byte[] labelPattern = AttackCardProbeText.Pattern(1);
                if (!_mem.TryReadBytes(hit.LabelAddr, labelPattern.Length, out var curLabel) || !ByteEq(curLabel, labelPattern))
                    continue;

                var shape = _attackRow.Classify(hit.LabelAddr);
                if (shape == AttackRowShape.Ours) { _attackRow.Restore(hit.LabelAddr); continue; }
                if (shape != AttackRowShape.Vanilla) continue;   // foreign or unreadable

                if (!_mem.TryReadBytes(hit.DescAddr, AttackRow.FootprintBytes, out var footprint)) continue;
                bool isKnown = ByteEq(footprint, VanillaImage)
                            || (_currentImage != null && ByteEq(footprint, _currentImage))
                            || (_previousImage != null && ByteEq(footprint, _previousImage));
                if (!isKnown) continue;               // foreign text
                if (ByteEq(footprint, VanillaImage)) continue;   // already vanilla

                if (!_mem.Writable(hit.DescAddr, AttackRow.FootprintBytes)) continue;
                _mem.WriteBytes(hit.DescAddr, VanillaImage);
            }
            catch (Exception ex)
            {
                ModLogger.Error(LogVerb.Display, "Restoring the vanilla Attack row failed for one table copy: " + ex.Message);
            }
        }
    }
}
