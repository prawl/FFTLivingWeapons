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
            var (curText, descChars) = AttackCardProbeText.ReadDesc(buf, descPos, enc, DescCapChars);
            long descAddr = bufBase + descPos;

            // Pin the cached footprint to the vanilla desc's own length (73) whenever the desc
            // ALREADY holds one of the three known lines, instead of trusting whatever length
            // happens to be live right now. The vanilla case is a direct observation (every
            // legitimate copy's desc field starts as the census-proven 73-char VanillaDesc,
            // AttackCardText's class doc). The current/previous case is induction, not
            // observation: a SHORTER composed line can only be sitting in this exact byte range
            // because an earlier footprint-checked write (SyncHit, AttackCard.Paint.cs) landed it
            // there over a buffer that was originally vanilla, so the true footprint is still 73,
            // never the shorter live length. Without this pin, a mid-battle re-census (any
            // RepaintAll eviction re-arms one) that finds its OWN already-painted copy would cache
            // the short length as the footprint, then silently refuse both a longer repaint and
            // the battle-exit vanilla restore forever (FitsFootprint would never pass again). A
            // desc matching none of the three stays foreign and uncached exactly as before
            // (SyncHit below still returns false for it).
            bool isKnownLine = curText == AttackCardText.VanillaDesc || curText == _current || curText == _previous;
            int cachedFootprint = isKnownLine ? AttackCardText.DefaultBudgetChars : descChars;

            var hit = new Hit { LabelAddr = labelAddr, DescAddr = descAddr, Enc = enc, DescChars = cachedFootprint };
            // Only cache a hit whose desc already holds a KNOWN line (vanilla/current/previous):
            // a standalone "Attack" label with unrelated prose after it is some OTHER command's
            // row, not this one, and must never be cached or written (the anchor discipline).
            if (SyncHit(hit)) _hits.Add(hit);
        }
    }

    private void Finish()
    {
        _scanning = false;
        ModLogger.Debug(LogVerb.Display, $"attack-card census finished: {_hits.Count} table copies cached for the weapon dossier");
    }
}
