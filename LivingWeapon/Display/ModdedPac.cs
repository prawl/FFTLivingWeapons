using System;
using System.Collections.Generic;
using System.IO;

namespace LivingWeapon;

/// <summary>Outcome of <see cref="ModdedPac.Patch"/>. <see cref="IconGlow"/> routes each differently
/// (see IconGlow.Apply.cs): no-op is silent, Unavailable warns once ever, search failures warn per icon.</summary>
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
/// Patches <c>modded.pac</c> in place (same-length overwrite) for the same-session icon
/// repaint (ledger row [live-icon-repaint], PROVEN 2026-09-01; mechanism in IconGlow.cs). Located
/// offsets are cached per icon so a weapon re-tiering skips the multi-MB re-scan.
///
/// One instance per pac path (see <see cref="FileIconGlowStore"/>), reused for the launch. Nothing
/// throws outward: failures are return values, since the loose-tex write is already correct
/// without the pac write (IconGlow.Apply.cs).
///
/// This file picks which offset to trust and when to run a shared <see cref="PacScan"/> pass
/// (LW-381 R2-2); ModdedPac.Io.cs does the byte-level read/compare/write/verify.
/// </summary>
internal sealed partial class ModdedPac
{
    private readonly string _pacPath;
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, long> _offsetCache = new();

    internal ModdedPac(string pacPath) => _pacPath = pacPath;

    /// <summary>
    /// Overwrites the single occurrence of <paramref name="oldBytes"/> with <paramref name="newBytes"/>
    /// (same length). <paramref name="cacheKey"/> is the icon's <c>"{id}:{surface}"</c> key from
    /// <see cref="FileIconGlowStore"/>, used to reuse a located offset. Single-item wrapper over
    /// <see cref="PatchBatch"/>.
    /// </summary>
    internal PacPatchResult Patch(string cacheKey, byte[] oldBytes, byte[] newBytes)
    {
        var results = PatchBatch(new[] { (cacheKey, oldBytes, newBytes) });
        return results.TryGetValue(cacheKey, out var result) ? result : PacPatchResult.Failed;
    }

    /// <summary>
    /// LW-381 R2-2: patches every entry, sharing one pass over the pac for those the offset cache
    /// cannot resolve. Cache-resolved entries are handled first; if all are, no pass runs. Never throws.
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
                // Stale cache (bytes match neither old nor new, likely a mid-session re-merge): drop it and search.
                lock (_cacheLock) _offsetCache.Remove(t.CacheKey);
            }
            unresolved.Add(t);
        }

        if (unresolved.Count > 0) SearchAndPatchUnresolved(unresolved, results);
        return results;
    }

    /// <summary>One read-only <see cref="PacScan.FindBatch"/> pass locates every unresolved entry,
    /// then each is written through <see cref="WriteAndVerify"/> and its offset cached.
    ///
    /// R2-7 (accepted): if the file shrank between the search handle closing and the write handle
    /// opening, the write would extend the file. The only merge that rewrites the pac runs before
    /// this process starts, and deploying while the game runs is forbidden.</summary>
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
