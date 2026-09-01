using System;
using System.Collections.Generic;
using System.IO;

namespace LivingWeapon;

/// <summary>Every outcome <see cref="ModdedPac.Patch"/> can hand back -- deliberately richer than
/// a bool, since the caller (<see cref="IconGlow"/>) treats "the pac write didn't happen" very
/// differently depending on WHY: an idle no-op is silent, an environment that will never have a
/// pac (<see cref="Unavailable"/>) warns once ever, and a search-shaped failure warns once per
/// icon (see IconGlow.Apply.cs for the exact routing).</summary>
internal enum PacPatchResult
{
    /// <summary>Wrote successfully and read the new bytes back byte-exact.</summary>
    Patched,
    /// <summary>The bytes at the (cached) location already equal the target; nothing to write.</summary>
    AlreadyCurrent,
    /// <summary>No usable pac file this launch (missing, or could not be opened at all).</summary>
    Unavailable,
    /// <summary><paramref name="oldBytes"/> was not found anywhere in the file.</summary>
    NotFound,
    /// <summary><paramref name="oldBytes"/> was found more than once; refusing to guess which.</summary>
    Ambiguous,
    /// <summary>A length mismatch, or any I/O step (open/read/write/verify) failed.</summary>
    Failed,
}

/// <summary>
/// LW-381: the file-side half of the same-session icon repaint (ledger row [live-icon-repaint],
/// PROVEN 2026-09-01 -- see IconGlow.cs's class remarks for the mechanism this rests on).
/// Patches one already-open <c>modded.pac</c> in place, same-length overwrite only, and caches
/// each successfully-located offset so repeat patches to the same logical icon (a weapon
/// re-tiering more than once in a session) skip the multi-MB re-scan.
///
/// One instance is owned per pac path (see <see cref="FileIconGlowStore"/>'s lazy resolve-once
/// construction) and reused for the life of the launch -- the cache is the entire point of that
/// reuse. Every public entry point is wrapped so nothing here ever throws outward; a failure is a
/// return value, never an exception, because a pac write is a same-session nicety layered on top
/// of the loose-tex write that is already correct today (see IconGlow.Apply.cs).
///
/// Split across two files (LW-381 R2-2, 200-line trigger): this file decides WHICH offset to
/// trust for each entry and WHEN a shared <see cref="PacScan"/> pass is needed (the caching and
/// batching policy); ModdedPac.Io.cs is the physical byte-level half -- given an offset already
/// believed correct, read/compare/write/verify the bytes actually there. See that file's own
/// remarks for why this is a real seam.
/// </summary>
internal sealed partial class ModdedPac
{
    private readonly string _pacPath;
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, long> _offsetCache = new();

    internal ModdedPac(string pacPath) => _pacPath = pacPath;

    /// <summary>
    /// Overwrites the single occurrence of <paramref name="oldBytes"/> in the pac with
    /// <paramref name="newBytes"/> (same length required). <paramref name="cacheKey"/> identifies
    /// the logical icon (see <see cref="FileIconGlowStore"/>'s <c>"{id}:{surface}"</c> key) so a
    /// second call for the same icon can reuse a previously located offset instead of re-scanning
    /// the whole file. A thin single-item wrapper over <see cref="PatchBatch"/>; see the class
    /// remarks and <see cref="PacPatchResult"/> for what each outcome means to the caller.
    /// </summary>
    internal PacPatchResult Patch(string cacheKey, byte[] oldBytes, byte[] newBytes)
    {
        var results = PatchBatch(new[] { (cacheKey, oldBytes, newBytes) });
        return results.TryGetValue(cacheKey, out var result) ? result : PacPatchResult.Failed;
    }

    /// <summary>
    /// LW-381 R2-2: patches every <paramref name="triples"/> entry, sharing ONE pass over the pac
    /// for whatever cannot be resolved from the offset cache. Each cache-resolved entry is
    /// checked and (if needed) written independently first and NEVER enters the shared pass --
    /// if every entry is cache-resolved, no pass happens at all. Never throws; see
    /// <see cref="PacPatchResult"/> for the per-entry outcomes.
    /// </summary>
    internal IReadOnlyDictionary<string, PacPatchResult> PatchBatch(IReadOnlyList<(string CacheKey, byte[] OldBytes, byte[] NewBytes)> triples)
    {
        var results = new Dictionary<string, PacPatchResult>();
        var unresolved = new List<(string CacheKey, byte[] OldBytes, byte[] NewBytes)>();

        foreach (var t in triples)
        {
            if (t.NewBytes == null || t.OldBytes == null || t.NewBytes.Length != t.OldBytes.Length)
            {
                results[t.CacheKey] = PacPatchResult.Failed;
                continue;
            }

            long? cached;
            lock (_cacheLock) cached = _offsetCache.TryGetValue(t.CacheKey, out var off) ? off : (long?)null;
            if (cached.HasValue)
            {
                var cachedResult = TryPatchAtOffset(cached.Value, t.OldBytes, t.NewBytes, out bool staleCache);
                if (!staleCache) { results[t.CacheKey] = cachedResult; continue; }
                // The cache went stale (bytes there match neither old nor new -- most likely a
                // deploy re-merged the pac mid-session): drop it and fall through to the shared
                // pass rather than trusting a location that is demonstrably no longer valid.
                lock (_cacheLock) _offsetCache.Remove(t.CacheKey);
            }
            unresolved.Add(t);
        }

        if (unresolved.Count > 0) SearchAndPatchUnresolved(unresolved, results);
        return results;
    }

    /// <summary>The shared search: ONE read-only pass (<see cref="PacScan.FindBatch"/>) locates
    /// every still-unresolved entry's offset, then each is written through the same verified
    /// path as the cached fast path. A successful write caches its offset for the next call on
    /// that entry's cache key.
    ///
    /// R2-7 (accepted, not a defect): there is a theoretical TOCTOU window between this search's
    /// read handle closing and <see cref="WriteAndVerify"/>'s write handle opening -- if the file
    /// shrank in between, seeking to the found offset and writing would extend the file rather
    /// than patch it in place, the one length-changing scenario this design allows. Practically
    /// unreachable (the launch merge that could shrink/rewrite the pac runs only before this
    /// process starts, and deploying while the game runs is forbidden), but named here rather
    /// than left silent.</summary>
    private void SearchAndPatchUnresolved(List<(string CacheKey, byte[] OldBytes, byte[] NewBytes)> unresolved, Dictionary<string, PacPatchResult> results)
    {
        IReadOnlyList<long> offsets;
        try
        {
            using var stream = new FileStream(_pacPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var needles = new byte[unresolved.Count][];
            for (int i = 0; i < unresolved.Count; i++) needles[i] = unresolved[i].OldBytes;
            offsets = PacScan.FindBatch(stream, needles);
        }
        catch
        {
            foreach (var t in unresolved) results[t.CacheKey] = PacPatchResult.Unavailable;
            return;
        }

        for (int i = 0; i < unresolved.Count; i++)
        {
            var t = unresolved[i];
            long offset = offsets[i];
            if (offset == PacScan.NotFound) { results[t.CacheKey] = PacPatchResult.NotFound; continue; }
            if (offset == PacScan.Ambiguous) { results[t.CacheKey] = PacPatchResult.Ambiguous; continue; }

            var result = WriteAndVerify(offset, t.NewBytes);
            if (result == PacPatchResult.Patched)
                lock (_cacheLock) _offsetCache[t.CacheKey] = offset;
            results[t.CacheKey] = result;
        }
    }

}
