using System;
using System.Collections.Generic;
using System.Linq;

namespace LivingWeapon;

/// <summary>
/// Split from IconGlow.Apply.cs. Classifies an id's deployed bytes (tier or foreign)
/// and seeds <c>_applied</c> before any write. <see cref="ApplyId"/> in IconGlow.Apply.cs writes
/// a tier already decided; judging never writes and applying never re-derives a tier.
/// </summary>
internal sealed partial class IconGlow
{
    /// <summary>First-touch classification for one id (pristine, a known tier, or foreign, which
    /// marks the whole id unmanaged). Pristine surfaces get a base snapshot; an existing backup is
    /// kept only if its sha1 matches <see cref="IconGlowEntry.BaseSha1"/>, since a rebake can leave
    /// a stale one. A failed snapshot marks the id unmanaged. Returns true if the surfaces are not
    /// all one tier (divergent, see IconGlow.Apply.cs).</summary>
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
            if (tier != 0) continue;   // only a pristine surface needs a base snapshot
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

    /// <summary>Pristine = 0, a recognized baked variant = that tier (1..3), anything else
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
