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
        _hits.Clear();
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
            if (SyncHit(hit)) _hits.Add(hit);
        }
    }

    private void Finish()
    {
        _scanning = false;
        ModLogger.Debug(LogVerb.Display, $"attack-card census finished: {_hits.Count} table copies cached for the weapon dossier");
    }
}
