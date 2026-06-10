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

    /// <summary>Pick a rotation slice of up to RotationSlice ids from the given set,
    /// advancing the persistent _rotCursor by the number of non-target ids taken so
    /// each successive chunk/pass covers a different window of ids.</summary>
    private IEnumerable<int> RotationSliceOf(IEnumerable<int> ids)
    {
        var arr = new List<int>(ids);
        if (arr.Count == 0) return arr;

        // Keep only non-target ids for the rotation slice (targets are already in suffixIds).
        var nonTargets = new List<int>();
        foreach (int id in arr)
            if (!_lastTargets.Contains(id)) nonTargets.Add(id);

        if (nonTargets.Count == 0) return arr;

        int count = nonTargets.Count;
        if (_rotCursor >= count) _rotCursor = 0;

        var result = new List<int>(RotationSlice);
        int taken = 0;
        for (int i = 0; i < count && taken < RotationSlice; i++)
        {
            result.Add(nonTargets[(_rotCursor + i) % count]);
            taken++;
        }
        // Advance cursor by how many non-target ids were taken this pass.
        _rotCursor = (_rotCursor + taken) % Math.Max(1, count);
        return result;
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
