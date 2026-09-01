using System;
using System.Collections.Generic;
using System.Linq;

namespace LivingWeapon;

/// <summary>
/// LW-381 R2-2 (200-line split from IconGlow.Apply.cs): the first-touch CLASSIFICATION half of
/// the judge/apply pair described in that file's class remarks -- given an id's currently
/// deployed bytes, decide what tier they represent (or that they are foreign) and seed
/// <c>_applied</c> truthfully, all BEFORE any write is ever considered. <see cref="ApplyId"/> (in
/// IconGlow.Apply.cs) is the other half: given a tier already decided, write it. A real seam:
/// judging never touches <c>WriteDeployedTex</c>/<c>PatchPac</c>, applying never re-derives a
/// tier from a hash.
/// </summary>
internal sealed partial class IconGlow
{
    /// <summary>First-touch classification for one id: PRISTINE, a known tier, or FOREIGN (any
    /// FOREIGN surface marks the whole id unmanaged). Every PRISTINE surface is then snapshotted
    /// before the id is trusted (FIX 2a: an existing backup is kept only when its own sha1
    /// matches <see cref="IconGlowEntry.BaseSha1"/> -- a rebake can leave an old one behind, and
    /// judge time is the only healable moment, so a stale/missing backup is (re)written here); a
    /// failed snapshot marks the id unmanaged. Returns true iff surfaces were NOT all one tier
    /// (DIVERGENT, see IconGlow.Apply.cs's class remarks).</summary>
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
}
