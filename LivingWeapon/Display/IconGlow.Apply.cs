using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace LivingWeapon;

/// <summary>
/// LW-336 (pac mirror split to IconGlow.Pac.cs, LW-381; judging split to IconGlow.Judge.cs,
/// LW-381 R2-2): IconGlow.cs decides WHETHER to apply something this tick; this file is HOW to
/// get the DEPLOYED loose tex right, the durable half the launch merge re-reads next boot
/// (correct even if IconGlow.Pac.cs's same-session mirror never ran). Each id is judged once
/// (<see cref="JudgeId"/>, in IconGlow.Judge.cs: pristine / known tier / foreign per surface,
/// seeding <c>_applied</c> as the surfaces' MINIMUM tier) before <see cref="ApplyId"/> writes it,
/// all-or-nothing; a partial failure leaves it stale so the next diff retries.
/// FIX 1: <c>judgeAllIds</c> on <see cref="ApplyDiff"/> (first apply only) judges every
/// manageable id even with an empty <c>changed</c>, seeding <c>_applied</c> for a later Diff --
/// except JudgeId's DIVERGENT return, which a later Diff could never notice, so heals inline.
/// </summary>
internal sealed partial class IconGlow
{
    private readonly HashSet<int> _unmanaged = new();
    private readonly HashSet<int> _judged = new();
    private readonly HashSet<int> _warnedOnce = new();

    /// <summary>Runs on <see cref="_runBackground"/>; never throws, <see cref="_applying"/> always
    /// clears. <paramref name="changed"/> is the pre-computed diff; <paramref name="judgeAllIds"/>
    /// non-null only on the launch's first apply, looking targets up in <paramref name="desired"/>.</summary>
    private void ApplyDiff(Dictionary<int, int> desired, Dictionary<int, int> changed, List<int>? judgeAllIds)
    {
        int unmanagedBefore = _unmanaged.Count;
        int judged = 0, written = 0;
        _pacPatchedThisApply = 0;
        _pacAlreadyCurrentThisApply = 0;   // R2-6: counted separately from a real pac write
        _hidesThisApply = 0;   // A3-1 (round 2): per-pass, or a pass with no hide reprints a stale count
        _pendingPacPatches.Clear();
        _pacPatchedThisPass.Clear();   // LW-381: this pass' hide-eligible surfaces (RoutePacResult fills it)
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
                    // narrower cousin (FIX 1): heal right here, or never -- see class remarks
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

            // R2-2: every surface queued by QueuePacMirror across BOTH loops above flushes as
            // ONE store call here -- strictly after every loose write in this pass, per the
            // load-bearing order (see IconGlow.Pac.cs's class remarks).
            FlushPacBatch();
        }
        finally
        {
            lock (_lock) _applying = false;
            // FIX 3: one observable line per batch (Debug tier), never per icon; silent if idle.
            int newlyUnmanaged = _unmanaged.Count - unmanagedBefore;
            if (judged > 0 || written > 0 || newlyUnmanaged > 0)
                ModLogger.Debug(LogVerb.Display,
                    $"IconGlow: judged {judged} icons, wrote {written} ({newlyUnmanaged} unmanaged, {_pacPatchedThisApply} pac-patched, {_pacAlreadyCurrentThisApply} pac-already-current), hide: {_hidesThisApply} surface(s) hidden.");
        }
    }

    /// <summary>Writes every surface this id has a manifest row for to its DEPLOYED loose tex.
    /// Tier 0 restores the <see cref="JudgeId"/> snapshot (a no-op, absent one, when already
    /// plain -- else unmanaged); anything else writes the baked variant. FIX 2b: an existing
    /// backup is still sha1-verified before writing -- a mismatch past JudgeId's own FIX 2a
    /// re-snapshot means restoring it would silently write OLDER art back; refuse and go
    /// unmanaged. True only when EVERY surface wrote cleanly. <c>oldBytes</c> is read once per
    /// surface and reused by the tier-0 check below AND <see cref="QueuePacMirror"/> (LW-381).
    /// R2-7 (accepted, not a defect): this read now happens for EVERY surface of every apply,
    /// where round 1 only read it inside the tier-0 no-backup sub-case -- 12 to 51 KB per surface
    /// per apply, accepted in exchange for having the true <c>oldBytes</c> to search for.</summary>
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
            QueuePacMirror(id, entry, oldBytes, bytes);   // ORDER LOAD-BEARING: loose tex first, pac second (flushed once, see ApplyDiff)
        }
        return allOk;
    }

    /// <summary>Sentence built here, never at a call site, so every reason reads subject-first.</summary>
    private void MarkUnmanaged(int id, string reason)
    {
        _unmanaged.Add(id);
        WarnOnce(id, $"IconGlow leaves icon {id} plain this launch: {reason}.");
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
