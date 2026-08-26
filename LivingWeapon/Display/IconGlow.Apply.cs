using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;

namespace LivingWeapon;

/// <summary>
/// LW-336: the background half of <see cref="IconGlow"/> -- IconGlow.cs decides WHETHER to apply
/// something this tick; this file is HOW. Writes the DEPLOYED loose tex
/// (modDir/FFTIVC/&lt;baseRel&gt;), never modded.pac: owner-witnessed 2026-08-26, icon textures
/// cache at first draw for the whole process, so only the loose tex the launch merge re-reads
/// next boot can ever display correctly. Each id is judged once (<see cref="JudgeId"/>: pristine
/// / known tier / foreign per surface, seeding <c>_applied</c> as the surfaces' MINIMUM tier)
/// before <see cref="ApplyId"/> writes it, all-or-nothing; a partial failure leaves it stale so
/// the next diff retries.
/// FIX 1: <c>judgeAllIds</c> on <see cref="ApplyDiff"/> (set only on the launch's first-ever
/// call) judges every manageable id even with an empty <c>changed</c> (a desired tier already
/// matching the assumed default of 0), seeding <c>_applied</c> for a later Diff -- except
/// JudgeId's DIVERGENT return, which a later Diff could never notice, so heals inline.
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
        }
        finally
        {
            lock (_lock) _applying = false;
            // FIX 3: one observable line per batch (Debug tier), never per icon; silent if idle.
            int newlyUnmanaged = _unmanaged.Count - unmanagedBefore;
            if (judged > 0 || written > 0 || newlyUnmanaged > 0)
                ModLogger.Debug(LogVerb.Display, $"IconGlow: judged {judged} icons, wrote {written} ({newlyUnmanaged} unmanaged).");
        }
    }

    /// <summary>First-touch classification for one id: PRISTINE, a known tier, or FOREIGN (any
    /// FOREIGN surface marks the whole id unmanaged). Every PRISTINE surface is then snapshotted
    /// before the id is trusted (FIX 2a: an existing backup is kept only when its own sha1
    /// matches <see cref="IconGlowEntry.BaseSha1"/> -- a rebake can leave an old one behind, and
    /// judge time is the only healable moment, so a stale/missing backup is (re)written here); a
    /// failed snapshot marks the id unmanaged. Returns true iff surfaces were NOT all one tier
    /// (DIVERGENT, see the class remarks).</summary>
    private bool JudgeId(int id)
    {
        var tiers = new List<(IconGlowEntry Entry, byte[] Bytes, int Tier)>();
        foreach (var entry in _entriesById[id])
        {
            byte[]? bytes = _store.ReadDeployedTex(entry);
            int? tier = ClassifyDeployed(entry, bytes);
            if (tier == null)
            {
                MarkUnmanaged(id, "its deployed tex matches neither the plain base nor any baked tier, foreign art");
                return false;
            }
            tiers.Add((entry, bytes!, tier.Value));
        }
        foreach (var (entry, bytes, tier) in tiers)
        {
            if (tier != 0) continue;   // only a PRISTINE surface needs a base snapshot
            var existingBackup = _store.ReadBaseBackup(entry);
            if (existingBackup != null && string.Equals(Sha1Hex(existingBackup), entry.BaseSha1, StringComparison.OrdinalIgnoreCase))
                continue;   // already have a correct one
            if (!_store.WriteBaseBackup(entry, bytes))
            {
                MarkUnmanaged(id, "could not snapshot its pristine base; refusing to overwrite what cannot be restored");
                return false;
            }
        }
        int seeded = tiers.Min(t => t.Tier);
        bool divergent = tiers.Any(t => t.Tier != seeded);
        lock (_lock) _applied[id] = seeded;
        _judged.Add(id);
        return divergent;
    }

    /// <summary>PRISTINE = 0, a recognized baked variant = that tier (1..3), anything else
    /// (missing, wrong length, unreadable, or a hash matching neither) = null (foreign).</summary>
    private static int? ClassifyDeployed(IconGlowEntry entry, byte[]? bytes)
    {
        if (bytes == null || bytes.Length != entry.Length) return null;
        string sha1 = Sha1Hex(bytes);
        if (string.Equals(sha1, entry.BaseSha1, StringComparison.OrdinalIgnoreCase)) return 0;
        foreach (var kv in entry.VariantSha1s)
            if (string.Equals(sha1, kv.Value, StringComparison.OrdinalIgnoreCase) && int.TryParse(kv.Key, out var t))
                return t;
        return null;
    }

    /// <summary>Writes every surface this id has a manifest row for to its DEPLOYED loose tex.
    /// Tier 0 restores the <see cref="JudgeId"/> snapshot (a no-op, absent one, when already
    /// plain -- else unmanaged); anything else writes the baked variant. FIX 2b: an existing
    /// backup is still sha1-verified before writing -- a mismatch past JudgeId's own FIX 2a
    /// re-snapshot means restoring it would silently write OLDER art back; refuse and go
    /// unmanaged. True only when EVERY surface wrote cleanly.</summary>
    private bool ApplyId(int id, int tier)
    {
        bool allOk = true;
        foreach (var entry in _entriesById[id])
        {
            byte[]? bytes;
            if (tier == 0)
            {
                bytes = _store.ReadBaseBackup(entry);
                if (bytes == null)
                {
                    byte[]? deployed = _store.ReadDeployedTex(entry);
                    bool alreadyPlain = deployed != null && deployed.Length == entry.Length &&
                        string.Equals(Sha1Hex(deployed), entry.BaseSha1, StringComparison.OrdinalIgnoreCase);
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
            }
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
