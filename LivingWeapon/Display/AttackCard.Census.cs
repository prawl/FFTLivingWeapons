using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// AttackCard's locate half: the budgeted, high-address-first ScanCursor walk from AttackCardSpike, armed by
/// Tick() whenever <c>_needsCensus</c> is set (battle enter, or an eviction in AttackCard.Paint.cs's RepaintAll).
/// Each accepted hit is synced at once (AttackCard.Paint's SyncHit).
/// </summary>
internal sealed partial class AttackCard
{
    private void Arm()
    {
        // Keeps _hits across a re-census; FindHits skips cached candidates, so a live copy is
        // not evicted and re-adopted.
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
            // _sweepCompleted stays false so the next battle edge re-arms a fresh census instead of
            // trusting a partial cache.
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
            // An already-cached candidate is skipped silently, before ReadDesc and SyncHit. It must
            // not touch _rejectedThisCensus, or the LW-69 aggregate line (rejected == foreign) would drift.
            if (_hits.Exists(h => h.LabelAddr == labelAddr)) continue;
            int descPos = AttackCardProbeText.DescStart(pos, enc);
            long descAddr = bufBase + descPos;

            // enc1: every image is AttackRow.FootprintBytes (AttackRow.Policy.BuildImage), the vanilla
            // desc's 73-char footprint, so there is no observed length to pin; SyncHit does the byte-exact
            // 74-byte known-image check.
            // enc2: dead path (no live catalogs; AttackCard.Enc2.cs). Keeps the original LW-33 pin logic:
            // its only write is vanilla-restore, so it needs the true observed length.
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
            // Cache only a hit holding a known image/line (vanilla/current/previous for enc1, vanilla
            // for enc2); a standalone "Attack" label followed by other prose is another command's row.
            // A candidate that misses the cache was never adopted, so it is tallied, not logged (a sweep
            // sees thousands of foreign "Attack" strings; see Finish).
            if (SyncHit(hit) == SyncOutcome.Ok) _hits.Add(hit);
            else _rejectedThisCensus++;
        }
    }

    private void Finish()
    {
        _scanning = false;
        // A HitCap finish counts as complete: cap 32 vs about six live copies, so a full-cap finish
        // self-heals via RepaintAll's eviction path.
        _sweepCompleted = true;
        ModLogger.Debug(LogVerb.Display,
            $"attack-card census finished: {_hits.Count} table copies cached for the weapon dossier ({_rejectedThisCensus} candidates rejected)");
    }
}
