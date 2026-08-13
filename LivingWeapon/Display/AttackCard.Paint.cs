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

    /// <summary>LW-91: a cached copy that fails SyncHit is RETAINED under a per-Hit strike
    /// episode (Hit.FirstFailMs, 0 = healthy) instead of instantly evicted -- the dominant live
    /// failure is a TRANSIENT misread of a buffer that is still perfectly live, and instant
    /// eviction threw away that handle only to spend a whole census re-finding it. All strike
    /// bookkeeping lives HERE, never in SyncHit itself (SyncHit is also the census adoption check,
    /// AttackCard.Census.cs's FindHits, which must keep its own instant-reject semantics for a
    /// candidate that never made the cache). Per-Hit and never cross-hit: one flaky copy can never
    /// advance or reset a healthy sibling's own episode.</summary>
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
                // Episode start (0 -> 1): the copy stays cached through the whole strike run below.
                // !_scanning stops an already-in-flight sweep from latching a pointless back-to-back
                // second sweep once it Finishes -- the in-flight one visits everything anyway (and
                // still keeps repainting this same hit via RepaintDriver's own alternation).
                hit.FirstFailMs = now;
                ModLogger.Debug(LogVerb.Display, $"attack row copy unverified ({outcome.Phrase()}): retained pending recovery");
                if (!_scanning) _needsCensus = true;
                continue;
            }

            if (now - hit.FirstFailMs >= Tuning.AttackCardEvictAfterMs)
                (toEvict ??= new List<(Hit, SyncOutcome)>()).Add((hit, outcome));
        }

        if (toEvict == null) return;
        // A real eviction (a copy that struck for the whole grace window) is rare, bounded by
        // HitCap, and diagnostically valuable, unlike a census candidate's silent rejection
        // (FindHits below): log it, one line per copy, naming the reason, plus a flight record
        // carrying the same reason and the copy's own address (the address-identity gap the
        // 2026-07-21 recon session flagged: the log line alone never said WHICH copy).
        foreach (var (h, outcome) in toEvict)
        {
            ModLogger.Debug(LogVerb.Display, $"attack row copy evicted ({outcome.Phrase()}): re-censusing");
            _recorder?.Invoke("card", $"evicted {outcome.Phrase()} labelAddr=0x{h.LabelAddr:X}");
            _hits.Remove(h);
        }
        // Arm consumed the early episode-start flag; the in-flight census snapshot can predate the
        // replacement allocation (today's guarantee, kept) -- unconditional, unlike the episode-
        // start arm above.
        _needsCensus = true;
    }

    /// <summary>Verify one cached copy still holds a known image, then (skip-if-equal) write the
    /// desired state via AttackRow. Returns SyncOutcome.Ok when the copy is still live, written or
    /// not (including a transient not-yet-writable retry); any other outcome names why the copy is
    /// foreign (caller evicts it). enc==2 copies never participate in the split-image mechanism at
    /// all (see AttackCard.cs's class doc); they get their own vanilla-only sync.</summary>
    private SyncOutcome SyncHit(Hit hit)
    {
        if (hit.Enc == 2) return SyncHitEnc2(hit);

        byte[] labelPattern = AttackCardProbeText.Pattern(1);
        if (!_mem.TryReadBytes(hit.LabelAddr, labelPattern.Length, out var curLabel) || !ByteEq(curLabel, labelPattern))
            return SyncOutcome.LabelGone;   // race guard: this buffer no longer holds the Attack label at all

        if (!_mem.TryReadBytes(hit.DescAddr, AttackRow.FootprintBytes, out var footprint)) return SyncOutcome.DescUnreadable;

        bool isKnown = ByteEq(footprint, VanillaImage)
                    || (_currentImage != null && ByteEq(footprint, _currentImage))
                    || (_previousImage != null && ByteEq(footprint, _previousImage));
        if (!isKnown)
        {
            if (_attackRow.Classify(hit.LabelAddr) == AttackRowShape.Ours) _attackRow.Restore(hit.LabelAddr);
            return SyncOutcome.ForeignFootprint;
        }

        // LW-33 (kept): this read is a FULL live read that just confirmed footprint is one of the
        // three known images; re-pin to the vanilla desc's own 73 chars every time. Meaningful for
        // the enc2 path's own footprint check (SyncHitEnc2 below); a split image's own write is
        // "fits by construction" (every image is exactly AttackRow.FootprintBytes), so this pin is
        // inert for enc1 but kept for shape/field-parity across both encodings.
        hit.DescChars = AttackCardText.DefaultBudgetChars;

        byte[] desiredImage = _currentImage ?? VanillaImage;
        if (ByteEq(footprint, desiredImage)) return SyncOutcome.Ok;   // skip-if-equal

        if (_currentImage != null)
        {
            var shape = _attackRow.Classify(hit.LabelAddr);
            if (shape != AttackRowShape.Vanilla && shape != AttackRowShape.Ours)
                return SyncOutcome.ShapeMismatch;   // record-shape mismatch with the footprint's story: evict, hands off
            if (!_mem.Writable(hit.DescAddr, AttackRow.FootprintBytes)) return SyncOutcome.Ok;   // transient: retry next pass
            _attackRow.Paint(hit.LabelAddr, desiredImage, _currentRowChars);
            ModLogger.Debug(LogVerb.Display, "attack row repainted");
            return SyncOutcome.Ok;
        }

        // Desired: vanilla plan.
        var vshape = _attackRow.Classify(hit.LabelAddr);
        if (vshape == AttackRowShape.Ours)
        {
            _attackRow.Restore(hit.LabelAddr);   // the strand killer: restores regardless of what the text says
            ModLogger.Debug(LogVerb.Display, "attack row reverted to vanilla");
            return SyncOutcome.Ok;
        }
        if (vshape == AttackRowShape.Vanilla)
        {
            // Record already vanilla-shaped; a stale non-vanilla FOOTPRINT under it (the "text-only
            // restore" case) is this caller's own decision, per AttackRow.Restore's class doc.
            if (!_mem.Writable(hit.DescAddr, AttackRow.FootprintBytes)) return SyncOutcome.Ok;
            _mem.WriteBytes(hit.DescAddr, VanillaImage);
            return SyncOutcome.Ok;
        }
        return SyncOutcome.ForeignShape;   // Foreign/Unreadable record shape: evict, hands off
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
