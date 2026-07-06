using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// AttackCard's compose/write half: consumes the resolve half's row+tail plan (CURSOR-ONLY, see
/// AttackCard.Resolve.cs), drives the three-way anchor rotation on a genuine compose-change, and
/// does the guarded per-copy verify+write (or eviction, when a cached copy no longer holds a
/// known image) via AttackRow's shape-gated I/O.
/// </summary>
internal sealed partial class AttackCard
{
    /// <summary>Recompose every tick (cheap: no memory access beyond the cursor/roster reads
    /// already happening elsewhere this tick). A genuine content change rotates the anchor and
    /// repaints every cached copy immediately; an unchanged compose falls back to the throttled
    /// maintenance cadence (Display.MaintenanceMs's own pattern) so drift/new copies still converge.
    /// The compose-change Debug lines carry ComposeCurrentPlan's Note (weapon id + cursor
    /// provenance on a paint; "no cursor answer" vs "resolved but composes vanilla" on a revert):
    /// the evidence chain the 2026-07-06 wrong-weapon live diagnosis needed and lacked.</summary>
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

    private void RepaintAll()
    {
        List<Hit>? toEvict = null;
        foreach (var hit in _hits)
            if (!SyncHit(hit)) (toEvict ??= new List<Hit>()).Add(hit);

        if (toEvict == null) return;
        foreach (var h in toEvict) _hits.Remove(h);
        _needsCensus = true;   // re-census after any eviction (the anchor discipline: never guess a foreign buffer)
    }

    /// <summary>Verify one cached copy still holds a known image, then (skip-if-equal) write the
    /// desired state via AttackRow. Returns false when the copy is foreign (caller evicts it); true
    /// otherwise, written or not. enc==2 copies never participate in the split-image mechanism at
    /// all (see AttackCard.cs's class doc); they get their own vanilla-only sync.</summary>
    private bool SyncHit(Hit hit)
    {
        if (hit.Enc == 2) return SyncHitEnc2(hit);

        byte[] labelPattern = AttackCardProbeText.Pattern(1);
        if (!_mem.TryReadBytes(hit.LabelAddr, labelPattern.Length, out var curLabel) || !ByteEq(curLabel, labelPattern))
            return false;   // race guard: this buffer no longer holds the Attack label at all

        if (!_mem.TryReadBytes(hit.DescAddr, AttackRow.FootprintBytes, out var footprint)) return false;

        bool isKnown = ByteEq(footprint, VanillaImage)
                    || (_currentImage != null && ByteEq(footprint, _currentImage))
                    || (_previousImage != null && ByteEq(footprint, _previousImage));
        if (!isKnown)
        {
            if (_attackRow.Classify(hit.LabelAddr) == AttackRowShape.Ours) _attackRow.Restore(hit.LabelAddr);
            ModLogger.Debug(LogVerb.Display, "attack row footprint no longer holds a known image: evicting the cached copy for a later re-census");
            return false;
        }

        // LW-33 (kept): this read is a FULL live read that just confirmed footprint is one of the
        // three known images; re-pin to the vanilla desc's own 73 chars every time. Meaningful for
        // the enc2 path's own footprint check (SyncHitEnc2 below); a split image's own write is
        // "fits by construction" (every image is exactly AttackRow.FootprintBytes), so this pin is
        // inert for enc1 but kept for shape/field-parity across both encodings.
        hit.DescChars = AttackCardText.DefaultBudgetChars;

        byte[] desiredImage = _currentImage ?? VanillaImage;
        if (ByteEq(footprint, desiredImage)) return true;   // skip-if-equal

        if (_currentImage != null)
        {
            var shape = _attackRow.Classify(hit.LabelAddr);
            if (shape != AttackRowShape.Vanilla && shape != AttackRowShape.Ours)
            {
                ModLogger.Debug(LogVerb.Display, "attack row's record no longer matches a writable shape: evicting the cached copy");
                return false;   // record-shape mismatch with the footprint's story: evict, hands off
            }
            if (!_mem.Writable(hit.DescAddr, AttackRow.FootprintBytes)) return true;   // transient: retry next pass
            _attackRow.Paint(hit.LabelAddr, desiredImage, _currentRowChars);
            ModLogger.Debug(LogVerb.Display, "attack row repainted");
            return true;
        }

        // Desired: vanilla plan.
        var vshape = _attackRow.Classify(hit.LabelAddr);
        if (vshape == AttackRowShape.Ours)
        {
            _attackRow.Restore(hit.LabelAddr);   // the strand killer: restores regardless of what the text says
            ModLogger.Debug(LogVerb.Display, "attack row reverted to vanilla");
            return true;
        }
        if (vshape == AttackRowShape.Vanilla)
        {
            // Record already vanilla-shaped; a stale non-vanilla FOOTPRINT under it (the "text-only
            // restore" case) is this caller's own decision, per AttackRow.Restore's class doc.
            if (!_mem.Writable(hit.DescAddr, AttackRow.FootprintBytes)) return true;
            _mem.WriteBytes(hit.DescAddr, VanillaImage);
            return true;
        }
        return false;   // Foreign/Unreadable record shape: evict, hands off
    }

    /// <summary>Best-effort vanilla restore for ResetBattle: only touches a copy that currently
    /// holds a KNOWN image (vanilla itself, current, or previous); a foreign buffer is left alone.
    /// An Ours-shaped record is ALWAYS restored regardless of what its footprint currently says (the
    /// strand killer), run BEFORE the cache is cleared. Never throws.</summary>
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
                if (shape != AttackRowShape.Vanilla) continue;   // Foreign/Unreadable: leave it alone

                if (!_mem.TryReadBytes(hit.DescAddr, AttackRow.FootprintBytes, out var footprint)) continue;
                bool isKnown = ByteEq(footprint, VanillaImage)
                            || (_currentImage != null && ByteEq(footprint, _currentImage))
                            || (_previousImage != null && ByteEq(footprint, _previousImage));
                if (!isKnown) continue;               // foreign text under a vanilla-shaped record: leave it alone
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
