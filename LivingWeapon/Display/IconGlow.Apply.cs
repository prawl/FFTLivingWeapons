using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace LivingWeapon;

/// <summary>
/// Writes the deployed loose tex that the launch merge re-reads next boot. IconGlow.cs decides whether to
/// apply, IconGlow.Pac.cs mirrors to the pac, IconGlow.Judge.cs judges. Each id is judged
/// once (<see cref="JudgeId"/>) before <see cref="ApplyId"/> writes it, all-or-nothing; a partial failure stays
/// stale so the next diff retries. FIX 1, <c>judgeAllIds</c> on <see cref="ApplyDiff"/> (first apply) judges every
/// manageable id to seed <c>_applied</c>; a divergent JudgeId result heals inline since a later Diff cannot notice it.
/// </summary>
internal sealed partial class IconGlow
{
    private readonly HashSet<int> _unmanaged = new();
    private readonly HashSet<int> _judged = new();
    private readonly HashSet<int> _warnedOnce = new();

    /// <summary>Runs on <see cref="_runBackground"/>; never throws and always clears <see cref="_applying"/>.
    /// <paramref name="judgeAllIds"/> is non-null only on the launch's first apply.</summary>
    private void ApplyDiff(Dictionary<int, int> desired, Dictionary<int, int> changed, List<int>? judgeAllIds)
    {
        int unmanagedBefore = _unmanaged.Count;
        int judged = 0, written = 0;
        _pacPatchedThisApply = 0;
        _pacAlreadyCurrentThisApply = 0;   // Counted separately from a real pac write
        _hidesThisApply = 0;   // Per-pass, or a pass with no hide reprints a stale count
        _pendingPacPatches.Clear();
        _pacPatchedThisPass.Clear();   // this pass' hide-eligible surfaces (RoutePacResult fills it)
        try
        {
            if (judgeAllIds != null)
            {
                foreach (var id in judgeAllIds)
                {
                    if (_judged.Contains(id) || _unmanaged.Contains(id)) continue;
                    bool divergent = JudgeId(id); judged++;
                    if (_unmanaged.Contains(id)) continue;
                    if (!divergent) continue;   // seeded truthfully; a real mismatch surfaces via Diff next tick
                    if (!desired.TryGetValue(id, out var dTier)) continue;
                    // FIX 1, heal here or never
                    int seededNow;
                    lock (_lock) seededNow = _applied.TryGetValue(id, out var t) ? t : 0;
                    if (seededNow != dTier) continue;   // a real mismatch too; the changed-loop below handles it
                    if (ApplyId(id, dTier)) { lock (_lock) _applied[id] = dTier; written++; }
                }
            }
            foreach (var (id, tier) in changed)
            {
                if (_unmanaged.Contains(id)) continue;
                bool divergent = false;
                if (!_judged.Contains(id))
                {
                    divergent = JudgeId(id); judged++;
                    if (_unmanaged.Contains(id)) continue;
                }
                int seeded;
                lock (_lock) seeded = _applied.TryGetValue(id, out var t) ? t : 0;
                if (seeded == tier && !divergent) continue;   // already at the desired tier, no dangling surface
                if (ApplyId(id, tier)) { lock (_lock) _applied[id] = tier; written++; }
            }

            // Flush every queued pac mirror as one store call after all loose writes (see IconGlow.Pac.cs).
            FlushPacBatch();
        }
        finally
        {
            lock (_lock) _applying = false;
            // FIX 3, one Debug line per batch, silent if idle.
            int newlyUnmanaged = _unmanaged.Count - unmanagedBefore;
            if (judged > 0 || written > 0 || newlyUnmanaged > 0)
                ModLogger.Debug(LogVerb.Display,
                    $"IconGlow: judged {judged} icons, wrote {written} ({newlyUnmanaged} unmanaged, {_pacPatchedThisApply} pac-patched, {_pacAlreadyCurrentThisApply} pac-already-current), hide: {_hidesThisApply} surface(s) hidden.");
        }
    }

    /// <summary>Writes every manifest surface for this id to its deployed loose tex. Tier 0 restores the
    /// <see cref="JudgeId"/> snapshot (no-op if already plain, else unmanaged); other tiers write the baked variant.
    /// FIX 2b, an existing backup is sha1-verified first, since a mismatch past JudgeId's FIX 2a re-snapshot would
    /// write older art back; refuse and go unmanaged. True only if every surface wrote. <c>oldBytes</c> is read
    /// for every surface (12 to 51 KB each) so <see cref="QueuePacMirror"/> has the true bytes to search for.</summary>
    private bool ApplyId(int id, int tier)
    {
        bool allOk = true;
        foreach (var entry in _entriesById[id])
        {
            byte[]? oldBytes = _store.ReadDeployedTex(entry);
            byte[]? bytes;
            if (tier == 0)
            {
                bytes = _store.ReadBaseBackup(entry);
                if (bytes == null)
                {
                    bool alreadyPlain = oldBytes != null && oldBytes.Length == entry.Length &&
                        string.Equals(Sha1Hex(oldBytes), entry.BaseSha1, StringComparison.OrdinalIgnoreCase);
                    if (alreadyPlain) continue;   // nothing to write, this surface is already ok
                    MarkUnmanaged(id, "no pristine base copy is available until the next deploy refreshes it");
                    return false;
                }
                if (!string.Equals(Sha1Hex(bytes), entry.BaseSha1, StringComparison.OrdinalIgnoreCase))
                {
                    MarkUnmanaged(id, "its base snapshot is from an older bake; the next deploy refreshes it");
                    return false;
                }
            }
            else
            {
                bytes = _store.ReadVariantTex(entry, tier);
            }
            if (bytes == null || bytes.Length != entry.Length)
            {
                WarnOnce(id, $"IconGlow could not read tier {tier} bytes for icon {id} ({entry.Surface}); will retry.");
                allOk = false;
                continue;
            }
            if (!_store.WriteDeployedTex(entry, bytes))
            {
                WarnOnce(id, $"IconGlow failed to write icon {id} ({entry.Surface}); will retry.");
                allOk = false;
                continue;
            }
            QueuePacMirror(id, entry, oldBytes, bytes);   // loose tex first, then pac (flushed once, see ApplyDiff)
        }
        return allOk;
    }

    /// <summary>Builds the sentence here so every reason reads subject-first.</summary>
    private void MarkUnmanaged(int id, string reason)
    {
        _unmanaged.Add(id);
        WarnOnce(id, $"IconGlow leaves icon {id} plain this launch: {reason}.");
    }

    /// <summary>One warn per icon id per launch.</summary>
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
