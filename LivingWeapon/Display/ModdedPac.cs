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
/// repaint (ledger row [live-icon-repaint], proven 2026-09-01; mechanism in IconGlow.cs). Located
/// offsets are cached per icon so a weapon re-tiering skips the multi-MB re-scan.
///
/// One instance per pac path (see <see cref="FileIconGlowStore"/>), reused for the launch. Nothing
/// throws outward. Failures are return values, since the loose-tex write is already correct
/// without the pac write (IconGlow.Apply.cs).
///
/// This file picks which offset to trust and when to run a shared <see cref="PacScan"/> pass;
/// ModdedPac.Io.cs does the byte-level read/compare/write/verify.
/// </summary>
internal sealed partial class ModdedPac
{
    private readonly string _pacPath;
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, long> _offsetCache = new();

    internal ModdedPac(string pacPath) => _pacPath = pacPath;

    private readonly Dictionary<string, IconGlowWriteError> _lastFailure = new();

    /// <summary>Why the last write for this key failed, or null once it has landed.</summary>
    internal IconGlowWriteError? LastFailure(string cacheKey)
    {
        lock (_cacheLock) return _lastFailure.TryGetValue(cacheKey, out var e) ? e : null;
    }

    private PacPatchResult Fail(string cacheKey, string type, int win32, string message)
    {
        lock (_cacheLock) _lastFailure[cacheKey] = new IconGlowWriteError(type, win32, message);
        return PacPatchResult.Failed;
    }

    private void ClearFailure(string cacheKey)
    {
        lock (_cacheLock) _lastFailure.Remove(cacheKey);
    }

    /// <summary>
    /// Overwrites the single occurrence of <paramref name="oldBytes"/> with <paramref name="newBytes"/>
    /// (same length). <paramref name="cacheKey"/> is the icon's <c>"{id}:{surface}"</c> key from
    /// <see cref="FileIconGlowStore"/>, used to reuse a located offset. Single-item wrapper over
    /// <see cref="PatchBatch"/>.
    /// </summary>
    internal PacPatchResult Patch(string cacheKey, byte[] oldBytes, byte[] newBytes, byte[]? priorTarget = null)
    {
        var results = PatchBatch(new[] { (cacheKey, oldBytes, newBytes, priorTarget) });
        return results.TryGetValue(cacheKey, out var result) ? result : PacPatchResult.Failed;
    }

    /// <summary>
    /// Patches every entry, sharing one pass over the pac for those the offset cache
    /// cannot resolve. Cache-resolved entries are handled first; if all are, no pass runs. Never throws.
    /// </summary>
    internal IReadOnlyDictionary<string, PacPatchResult> PatchBatch(IReadOnlyList<(string CacheKey, byte[] OldBytes, byte[] NewBytes, byte[]? PriorTarget)> triples)
    {
        var results = new Dictionary<string, PacPatchResult>();
        var unresolved = new List<(string CacheKey, byte[] OldBytes, byte[] NewBytes, byte[]? PriorTarget)>();

        foreach (var t in triples)
        {
            if (t.NewBytes == null || t.OldBytes == null || t.NewBytes.Length != t.OldBytes.Length)
            {
                results[t.CacheKey] = Fail(t.CacheKey, "LengthMismatch", 0, "old and new byte lengths differ");
                continue;
            }

            var prior = t.PriorTarget?.Length == t.OldBytes.Length ? t.PriorTarget : null;
            long? cached;
            lock (_cacheLock) cached = _offsetCache.TryGetValue(t.CacheKey, out var off) ? off : (long?)null;
            if (cached.HasValue)
            {
                var cachedResult = TryPatchAtOffset(t.CacheKey, cached.Value, t.OldBytes, t.NewBytes, prior, out bool staleCache);
                if (!staleCache) { results[t.CacheKey] = cachedResult; continue; }
                // Stale cache (bytes match neither old nor new, likely a mid-session re-merge): drop it and search.
                lock (_cacheLock) _offsetCache.Remove(t.CacheKey);
            }
            unresolved.Add((t.CacheKey, t.OldBytes, t.NewBytes, prior));
        }

        if (unresolved.Count > 0) SearchAndPatchUnresolved(unresolved, results);
        return results;
    }

    /// <summary>One read-only <see cref="PacScan.FindBatch"/> pass locates every unresolved entry,
    /// then each is written through <see cref="WriteAndVerify"/> and its offset cached.
    ///
    /// Accepted residual. If the file shrank between the search handle closing and the write handle
    /// opening, the write would extend the file. The only merge that rewrites the pac runs before
    /// this process starts, and deploying while the game runs is forbidden.</summary>
    private void SearchAndPatchUnresolved(List<(string CacheKey, byte[] OldBytes, byte[] NewBytes, byte[]? PriorTarget)> unresolved, Dictionary<string, PacPatchResult> results)
    {
        IReadOnlyList<long> offsets;
        try
        {
            using var stream = new FileStream(_pacPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var needles = new byte[unresolved.Count * 3][];
            for (int i = 0; i < unresolved.Count; i++)
            {
                needles[i * 3] = unresolved[i].NewBytes;
                needles[i * 3 + 1] = unresolved[i].OldBytes;
                needles[i * 3 + 2] = unresolved[i].PriorTarget!;
            }
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
            long found = offsets[i * 3], atOld = offsets[i * 3 + 1], atPrior = offsets[i * 3 + 2];
            if (found >= 0)
            {
                lock (_cacheLock) _offsetCache[t.CacheKey] = found;
                ClearFailure(t.CacheKey);
                results[t.CacheKey] = PacPatchResult.AlreadyCurrent;
                continue;
            }
            long offset = atOld >= 0 ? atOld : atPrior;
            if (offset < 0)
            {
                bool ambiguous = found == PacScan.Ambiguous || atOld == PacScan.Ambiguous || atPrior == PacScan.Ambiguous;
                results[t.CacheKey] = ambiguous ? PacPatchResult.Ambiguous : PacPatchResult.NotFound;
                continue;
            }

            var result = WriteAndVerify(t.CacheKey, offset, t.NewBytes);
            if (result == PacPatchResult.Patched)
                lock (_cacheLock) _offsetCache[t.CacheKey] = offset;
            results[t.CacheKey] = result;
        }
    }

}
