using System;
using System.Collections.Generic;

namespace LivingWeapon;

// Partial: chunk callback, rotation, and WpScratch paint.  See Display.cs for architecture.
internal sealed partial class Display
{
    /// <summary>
    /// Called by the sweep for every offered chunk.  Discovers kills and suffix sites,
    /// registers them, then paints ONLY the newly-registered sites (not the full cache)
    /// to avoid an O(all-sites) verify storm on every hit chunk.  PaintAll is reserved
    /// for the count-change path in Tick where a known stale value must be pushed everywhere.
    ///
    /// Suffix coverage: the target set is always included; a rotation slice of ids that had
    /// kills hits in this chunk is added on top.  The rotation cursor (_rotCursor) is a
    /// persistent field advanced by the number of non-target ids taken so successive chunks
    /// and passes cycle through all ids -- no chunk has to wait for a new generation.
    /// </summary>
    private void OnChunk(byte[] buf, int lookback, int searchable, long bufBaseAddr)
    {
        var killsHits = new List<CardScanner.KillsHit>();
        CardScanner.FindKills(buf, lookback, searchable, _pats, killsHits);

        // Gather ids that got kills hits in this chunk for the rotation slice.
        var hitIds = new HashSet<int>();
        foreach (var h in killsHits) hitIds.Add(h.Id);

        // Suffix pass: targets UNION a rotation slice of ids that had kills hits.
        var suffixIds = new HashSet<int>(_lastTargets);
        if (hitIds.Count > 0)
            suffixIds.UnionWith(RotationSliceOf(hitIds));

        var suffixHits = new List<CardScanner.SuffixHit>();
        CardScanner.FindSuffixes(buf, lookback, searchable, _pats, suffixIds, suffixHits);

        // Collect newly-registered sites so we paint only those, not the whole cache.
        var newSites = new List<CardSites.Site>();

        foreach (var h in killsHits)
        {
            long slotAddr   = bufBaseAddr + h.SlotPos;
            long anchorAddr = h.FlavorPos >= 0 ? bufBaseAddr + h.FlavorPos : slotAddr;
            var site = new CardSites.Site(h.Id, h.Enc, slotAddr, anchorAddr, IsKills: true);
            if (_sites.Add(site)) newSites.Add(site);
        }

        foreach (var h in suffixHits)
        {
            long slotAddr   = bufBaseAddr + h.SlotPos;
            long anchorAddr = bufBaseAddr + h.NamePos;
            var site = new CardSites.Site(h.Id, h.Enc, slotAddr, anchorAddr, IsKills: false);
            if (_sites.Add(site)) newSites.Add(site);
        }

        if (newSites.Count > 0)
        {
            // Paint only the new sites from this chunk (not the full 512-site cache).
            _sites.Paint(newSites, KillsFor);
            // Mark chunk as hot so it gets priority on the next HotRescanMs interval.
            long chunkStart = bufBaseAddr + lookback;
            _sweep.MarkHot(chunkStart);
        }
    }

    /// <summary>Pick up to RotationSlice non-target ids from this chunk's hit set that have
    /// not had a suffix search this coverage cycle. Coverage is per-ID (a set), never a shared
    /// cursor: a cursor clamped to each chunk's id count let a small render-buffer chunk reset
    /// the position the big master-text chunk was walking, starving tail ids forever (live:
    /// the bows never got their +3). When every id this chunk offers is already covered, its
    /// ids are released and a new cycle starts -- so a fresh render buffer of an already-covered
    /// id waits at most one full cycle, and every id provably gets its turn.</summary>
    private IEnumerable<int> RotationSliceOf(IEnumerable<int> ids)
    {
        // Keep only non-target ids (targets are already in suffixIds unconditionally).
        var nonTargets = new List<int>();
        foreach (int id in ids)
            if (!_lastTargets.Contains(id)) nonTargets.Add(id);
        if (nonTargets.Count == 0) return nonTargets;

        var take = new List<int>(RotationSlice);
        foreach (int id in nonTargets)
        {
            if (_suffixCovered.Contains(id)) continue;
            take.Add(id);
            if (take.Count == RotationSlice) break;
        }
        if (take.Count == 0)
        {
            // Cycle complete for this chunk's ids: release them and start the next round.
            foreach (int id in nonTargets) _suffixCovered.Remove(id);
            foreach (int id in nonTargets)
            {
                take.Add(id);
                if (take.Count == RotationSlice) break;
            }
        }
        foreach (int id in take) _suffixCovered.Add(id);
        return take;
    }

    /// <summary>Write the boosted WP onto the equip card's scratch byte, guarded: only when
    /// the scratch currently holds the natural or already-boosted value (owned by this weapon),
    /// and only when natural != boosted (no pointless write on a tier-0 weapon).</summary>
    private void PaintWpScratch()
    {
        int mirrorId = _mem.U16(Offsets.MirrorWeapon);
        if (!_meta.TryGetValue(mirrorId, out var m)) return;
        if (!_mem.Readable(Offsets.WpScratch, 1)) return;

        int kills   = KillsFor(mirrorId);
        int boosted = Math.Min(255, (int)Math.Round(m.Wp * (1.0 + Tuning.Factor[Tuning.TierFor(kills)])));
        int cur     = _mem.U8(Offsets.WpScratch);

        if (cur != m.Wp && cur != boosted) return;  // not owned by this weapon
        if (boosted == cur) return;                  // already correct

        if (_mem.Writable(Offsets.WpScratch, 1))
            _mem.WriteBytes(Offsets.WpScratch, new[] { (byte)boosted });
    }
}
