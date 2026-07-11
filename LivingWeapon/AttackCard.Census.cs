using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// AttackCard's locate half: mirrors AttackCardSpike's Arm/StepScan shape (the same budgeted,
/// high-address-first ScanCursor walk), generalized off a keypress: armed automatically by
/// Tick() whenever <c>_needsCensus</c> is set (battle enter, or any eviction in
/// AttackCard.Paint.cs's RepaintAll). Every accepted hit is synced immediately (AttackCard.Paint's
/// SyncHit) so a newly-discovered table copy starts in the correct state, not vanilla-by-default
/// until the next repaint.
/// </summary>
internal sealed partial class AttackCard
{
    private void Arm()
    {
        // LW-57: does NOT clear _hits (the warm cache survives any re-census, including one that
        // rearms across a battle edge); FindHits below silently skips any candidate already cached,
        // so a still-live copy is preserved rather than evicted-then-readopted. Evictions already
        // removed bad entries before setting _needsCensus, so nothing stale should be sitting here.
        _rejectedThisCensus = 0;
        _sweepCompleted = false;
        _nextIsRepaint = true;   // the tick right after Arm always repaints first (LW-57 anti-starvation)
        _reader.Snapshot();
        _regionsDesc = ScanCursor.SortDescending(_reader.Regions);
        _cursor = RegionCursor.AtStart(_regionsDesc);
        _scanning = true;
        ModLogger.Debug(LogVerb.Display, "attack-card census armed: scanning committed memory for the Attack menu's table copies");
    }

    private void StepScan()
    {
        try
        {
            var slice = ScanCursor.NextSlice(_regionsDesc, ref _cursor, PerTickBudgetBytes);
            foreach (var (rbase, rend, chunkStart) in slice)
            {
                if (_hits.Count >= HitCap) break;
                int read = _reader.ReadInRegion(chunkStart, rbase, rend, out int lookback, out int searchable);
                if (read == 0) continue;

                long bufBase = chunkStart - lookback;
                int windowEnd = lookback + searchable;
                FindHits(_reader.Buf, lookback, windowEnd, bufBase, enc: 1);
                FindHits(_reader.Buf, lookback, windowEnd, bufBase, enc: 2);
            }

            if (_hits.Count >= HitCap || _cursor.Done) Finish();
        }
        catch (Exception ex)
        {
            ModLogger.Error(LogVerb.Display, "The Attack-card census failed; the desc painter will retry next battle: " + ex.Message);
            // _sweepCompleted deliberately stays false here (same silent-partial-cache class as a
            // battle-edge abort): the next battle edge re-arms a fresh census instead of trusting
            // whatever partial cache this failed sweep left behind.
            _scanning = false;
        }
    }

    private void FindHits(byte[] buf, int lookback, int windowEnd, long bufBase, int enc)
    {
        byte[] pat = AttackCardProbeText.Pattern(enc);
        var positions = new List<int>();
        ByteScan.FindAll(buf, pat, lookback, windowEnd, positions);

        foreach (int pos in positions)
        {
            if (_hits.Count >= HitCap) return;
            if (!AttackCardProbeText.IsStandaloneHit(buf, pos, enc)) continue;

            long labelAddr = bufBase + pos;
            // LW-57: a candidate already cached (Arm no longer clears _hits) is neither adopted nor
            // rejected, just preserved: skip silently, before the enc2 ReadDesc work below and
            // before SyncHit, so a still-live copy is never double-counted or bounced through evict
            // then re-adopt. Silent on purpose, same as the "never makes the cache" rejection case
            // below: it must not touch _rejectedThisCensus, or the LW-69 aggregate line's meaning
            // (rejected == genuinely foreign) would drift.
            if (_hits.Exists(h => h.LabelAddr == labelAddr)) continue;
            int descPos = AttackCardProbeText.DescStart(pos, enc);
            long descAddr = bufBase + descPos;

            // enc1: the split-image mechanism (AttackRow) always targets the vanilla desc's own
            // 73-char footprint: every image is exactly AttackRow.FootprintBytes by construction
            // (AttackRow.Policy.BuildImage), so unlike enc2 below there is no OBSERVED length worth
            // pinning; SyncHit itself does the real (byte-exact, 74-byte) known-image check.
            //
            // enc2: dead path (zero live catalogs; AttackCard.Enc2.cs), kept safe with the ORIGINAL
            // LW-33 pin logic: vanilla-restore is the only write that branch ever attempts, so its
            // own observed footprint still needs the true-length discipline the pre-stage-3 painter
            // used (a genuinely truncated region must never be cached with a bogus longer footprint).
            int cachedFootprint;
            if (enc == 1)
            {
                cachedFootprint = AttackCardText.DefaultBudgetChars;
            }
            else
            {
                var (curText, descChars) = AttackCardProbeText.ReadDesc(buf, descPos, enc, DescCapChars);
                cachedFootprint = curText == AttackCardText.VanillaDesc ? AttackCardText.DefaultBudgetChars : descChars;
            }

            var hit = new Hit { LabelAddr = labelAddr, DescAddr = descAddr, Enc = enc, DescChars = cachedFootprint };
            // Only cache a hit whose footprint already holds a KNOWN image/line (vanilla/current/
            // previous for enc1; vanilla only for enc2): a standalone "Attack" label with unrelated
            // prose after it is some OTHER command's row, not this one, and must never be cached or
            // written (the anchor discipline). SyncHit performs the real check for both encodings.
            // A census candidate that never makes the cache is NOT an eviction (it was never
            // adopted): tallied silently, never logged per-candidate (a sweep walks thousands of
            // foreign "Attack" strings a battle; see Finish's aggregate line below).
            if (SyncHit(hit) == SyncOutcome.Ok) _hits.Add(hit);
            else _rejectedThisCensus++;
        }
    }

    private void Finish()
    {
        _scanning = false;
        // A HitCap finish counts as complete too: cap 32 vs about six live copies per launch, so a
        // degenerate full-cap instant finish self-heals via RepaintAll's own eviction path, not a
        // forced re-arm here.
        _sweepCompleted = true;
        ModLogger.Debug(LogVerb.Display,
            $"attack-card census finished: {_hits.Count} table copies cached for the weapon dossier ({_rejectedThisCensus} candidates rejected)");
    }
}
