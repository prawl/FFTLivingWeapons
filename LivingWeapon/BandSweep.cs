using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// The shared authoritative-copy sweep behind CharmLock and EagleEye: chunked RPM reads of the
/// +/-1MB window around the combat anchor, scanning every byte offset for a 2-byte AMaxHp match
/// against the candidate fingerprints, then confirming level/brave/faith at the struct-relative
/// offsets. The byMhp candidate grouping and the chunk/overlap loop live HERE, once; each module
/// supplies only its per-hit body (CharmLock: charm-bit check + collect; EagleEye: doom check +
/// guarded write-down). The sweep is heavy -- callers throttle it (~every 6th tick), not this class.
/// </summary>
internal static class BandSweep
{
    /// <summary>Half-width of the swept window around <see cref="Offsets.CombatAnchor"/>.</summary>
    public const long Radius = 0x100000;   // +/-1MB around the combat anchor

    /// <summary>Bytes per TryReadBytes chunk.</summary>
    public const int Chunk = 0x40000;

    /// <summary>Overlap tail read past each chunk so a struct straddling a chunk boundary is
    /// still fully addressable from the chunk it starts in.</summary>
    public const int Overlap = 0x80;

    /// <summary>Run one sweep. For every buffer position whose AMaxHp u16 matches a candidate's
    /// mhp AND whose level/brave/faith bytes confirm that candidate, invoke <paramref name="onHit"/>
    /// with (absAddr = the struct's absolute base address, buf = the chunk buffer, baseIdx = the
    /// struct's base index within buf, fp = the matched fingerprint), then move to the next
    /// position -- at most one candidate can match a position (same-mhp candidates are deduped
    /// and differ in level/brave/faith). No-op when there are no candidates.</summary>
    public static void ForEachFingerprintHit(IGameMemory mem,
        IEnumerable<(int mhp, int lvl, int br, int fa)> fps,
        Action<long, byte[], int, (int mhp, int lvl, int br, int fa)> onHit)
    {
        var byMhp = new Dictionary<int, List<(int mhp, int lvl, int br, int fa)>>();
        foreach (var fp in fps)
        {
            if (!byMhp.TryGetValue(fp.mhp, out var l)) byMhp[fp.mhp] = l = new();
            l.Add(fp);
        }
        if (byMhp.Count == 0) return;
        long lo = Offsets.CombatAnchor - Radius, total = Radius * 2;
        for (long off = 0; off < total; off += Chunk)
        {
            int n = (int)Math.Min(Chunk + Overlap, total - off);
            if (!mem.TryReadBytes(lo + off, n, out byte[] buf)) continue;   // RPM reads across regions
            int lim = Math.Min(Chunk, buf.Length - Overlap);
            for (int i = Offsets.AMaxHp; i < lim; i++)
            {
                int mhp = buf[i] | (buf[i + 1] << 8);
                if (!byMhp.TryGetValue(mhp, out var cands)) continue;
                int b = i - Offsets.AMaxHp;
                foreach (var fp in cands)
                {
                    if (buf[b + Offsets.ALevel] != fp.lvl || buf[b + Offsets.ABrave] != fp.br || buf[b + Offsets.AFaith] != fp.fa) continue;
                    onHit(lo + off + b, buf, b, fp);
                    break;
                }
            }
        }
    }
}
