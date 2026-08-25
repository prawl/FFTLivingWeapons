using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;

namespace LivingWeapon;

/// <summary>
/// LW-295 cycle B: the background half of <see cref="IconGlow"/> -- everything IconGlow.Tick's
/// injected <c>_runBackground</c> actually runs off the tick thread. Real seam from the tick-
/// facing half (IconGlow.cs): that file decides WHETHER to apply something this tick; this file
/// is HOW an apply actually touches modded.pac.
///
/// Builds the pac needle index on first use only -- ONE full pac read, then each manifest icon's
/// needle searched in turn, sequentially, over that same in-memory buffer -- and keeps every
/// offset found for the WHOLE launch, never re-searching (U10: after the first splice the base
/// needle is gone from that offset, so a re-search would wrongly stand a managed icon down on its
/// very next tier change). An icon is judged independently the first time it is touched: its
/// deployed loose base tex must match the manifest's baked length AND sha1 (a stale bake must
/// not splice an old-body rim over new art, U11), and its bytes must occur in modded.pac EXACTLY
/// ONCE (IconGlowPolicy.NeedleVerdict) -- 0
/// or 2+ hits both degrade it to unmanaged rather than guess an offset. A failed read or write
/// degrades that one icon for this attempt only (one WARN per id per launch, however many times
/// it keeps failing) and is retried on the next diff, never tight-looped.
/// </summary>
internal sealed partial class IconGlow
{
    private readonly struct IndexedIcon
    {
        public readonly long Offset;
        public readonly int Length;
        public IndexedIcon(long offset, int length) { Offset = offset; Length = length; }
    }

    private bool _indexBuilt;
    private readonly Dictionary<(int Id, string Surface), IndexedIcon> _index = new();
    private readonly HashSet<int> _unmanaged = new();
    private readonly HashSet<int> _warnedOnce = new();

    /// <summary>Runs on <see cref="_runBackground"/>. Never throws outward; <see cref="_applying"/>
    /// always clears, in flight or not.</summary>
    private void ApplyDiff(Dictionary<int, int> changed)
    {
        try
        {
            if (!_indexBuilt) BuildIndex();
            foreach (var (id, tier) in changed)
            {
                if (_unmanaged.Contains(id)) continue;
                if (SpliceId(id, tier)) lock (_lock) _applied[id] = tier;
            }
        }
        finally { lock (_lock) _applying = false; }
    }

    private void BuildIndex()
    {
        _indexBuilt = true;
        byte[]? pac = _store.ReadPac();
        if (pac == null)
        {
            // One root cause (the pac itself is unreadable) degrades every id at once -- warn
            // ONCE for all of them, not one WARN per id, while still marking each one unmanaged
            // individually so retry semantics and per-id state (ManageableIds, ApplyDiff's skip)
            // stay exactly as if MarkUnmanaged had been called per id.
            var ids = _entriesById.Keys.ToList();
            foreach (var id in ids) _unmanaged.Add(id);
            ModLogger.Warn(LogVerb.Display,
                $"IconGlow could not read modded.pac; all {ids.Count} icons stay plain this launch.");
            return;
        }

        foreach (var entry in _manifest!.Icons)
        {
            if (_unmanaged.Contains(entry.Id)) continue;
            byte[]? baseBytes = _store.ReadBaseTex(entry);
            if (baseBytes == null || baseBytes.Length != entry.Length)
            {
                MarkUnmanaged(entry.Id, $"its deployed base tex is missing or the wrong length ({entry.Surface})");
                continue;
            }
            if (!string.Equals(Sha1Hex(baseBytes), entry.BaseSha1, StringComparison.OrdinalIgnoreCase))
            {
                MarkUnmanaged(entry.Id, $"its deployed base tex does not match the manifest's baked hash, a stale glow bake ({entry.Surface})");
                continue;
            }
            int hits = IconGlowPolicy.FindNeedle(pac, baseBytes, out long offset);
            if (IconGlowPolicy.ClassifyHitCount(hits) != IconGlowPolicy.NeedleVerdict.FoundOnce)
            {
                // FindNeedle stops counting at 2 (IconGlow.Policy.cs), so 2 here means "2 or
                // more" -- say so instead of implying an exact count it never measured.
                string countPhrase = hits >= 2 ? $"at least {hits} times" : $"{hits} times";
                MarkUnmanaged(entry.Id, $"its base art was found {countPhrase} in modded.pac, not exactly once ({entry.Surface})");
                continue;
            }
            _index[(entry.Id, entry.Surface)] = new IndexedIcon(offset, entry.Length);
        }
    }

    /// <summary>Sentence built here, never at a call site, so every unmanaged reason reads as one
    /// subject-first line ("IconGlow leaves icon N plain..."), not a bare module-name label.</summary>
    private void MarkUnmanaged(int id, string reason)
    {
        _unmanaged.Add(id);
        WarnOnce(id, $"IconGlow leaves icon {id} plain this launch: {reason}.");
    }

    /// <summary>Splices every surface (card + small) this id has a manifest row for. Only marks
    /// the id applied when EVERY surface wrote cleanly -- a partial failure leaves it stale so the
    /// next diff retries (harmlessly re-writing whichever surface already succeeded too).</summary>
    private bool SpliceId(int id, int tier)
    {
        bool allOk = true;
        foreach (var entry in _entriesById[id])
        {
            if (!_index.TryGetValue((id, entry.Surface), out var idx)) { allOk = false; continue; }
            byte[]? bytes = tier == 0 ? _store.ReadBaseTex(entry) : _store.ReadVariantTex(entry, tier);
            if (bytes == null || bytes.Length != idx.Length)
            {
                WarnOnce(id, $"IconGlow could not read tier {tier} bytes for icon {id} ({entry.Surface}); will retry.");
                allOk = false;
                continue;
            }
            if (!_store.WriteAt(idx.Offset, bytes))
            {
                WarnOnce(id, $"IconGlow failed to write icon {id} ({entry.Surface}) into modded.pac; will retry.");
                allOk = false;
            }
        }
        return allOk;
    }

    /// <summary>One WARN per icon id per launch, however many times it keeps failing/retrying.</summary>
    private void WarnOnce(int id, string message)
    {
        lock (_lock) { if (!_warnedOnce.Add(id)) return; }
        ModLogger.Warn(LogVerb.Display, message);
    }

    private static string Sha1Hex(byte[] bytes)
    {
        using var sha1 = SHA1.Create();
        return Convert.ToHexString(sha1.ComputeHash(bytes)).ToLowerInvariant();
    }
}
