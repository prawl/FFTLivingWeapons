using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Shared sweep behind EagleEye: chunked reads of the +/-1MB window around the combat anchor, matching
/// 2-byte AMaxHp against candidate fingerprints and confirming level/brave/faith at the struct-relative
/// offsets. The caller supplies the per-hit body. Heavy, so callers throttle it (about every 6th tick).
/// </summary>
internal static class BandSweep
{
    /// <summary>Half-width of the swept window around <see cref="Offsets.CombatAnchor"/>.</summary>
    public const long Radius = 0x100000;   // +/-1MB around the combat anchor

    /// <summary>Bytes per TryReadBytes chunk.</summary>
    public const int Chunk = 0x40000;

    /// <summary>Bytes read past each chunk so a struct straddling a boundary stays addressable.</summary>
    public const int Overlap = 0x80;

    /// <summary>Calls <paramref name="onHit"/> for each position whose AMaxHp matches a candidate and whose
    /// level/brave/faith confirm it, passing (absAddr = struct base address, buf = chunk buffer, baseIdx =
    /// struct base index in buf, fp = matched fingerprint). At most one candidate matches a position.</summary>
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
