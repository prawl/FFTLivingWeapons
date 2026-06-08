using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// The committed-memory scan that locates the equip card's paintable spots. Split out
/// of Display.cs to keep each file under the 200-line house limit.
///
/// Two passes, anchored differently on purpose:
///   1. SUFFIX -> a weapon name + a valid 2-char slot. The +/+2/+3 badge sits exactly at
///      name+len, so the name is the right anchor for it.
///   2. KILLS  -> a literal "Kills " + 4 digits, tied to a weapon by the NEAREST FLAVOR
///      line before it. The card's "Kills " line lives in the description buffer, far from
///      (and often before) the name copy, so name-anchoring grabbed the WRONG weapon's
///      line. The flavor line leads that same description block, is stable across leveling,
///      and is unique enough -- so the nearest flavor before a "Kills " is that weapon's own.
/// All reads go through Mem (RPM-backed): a chunk freed mid-scan is a caught miss, not a crash.
/// </summary>
internal sealed partial class Display
{
    private const long ChunkSize = 8 * 1024 * 1024;
    private const int Overlap = 4096;
    private const long ScanCap = 6L * 1024 * 1024 * 1024;
    private const int FlavorWindow = 2048;   // how far before "Kills " the flavor line may sit
    private const double FullScanSeconds = 90.0;   // re-discover the hot-region set this often

    // The full heap is ~2.6GB -> a full scan is ~9s. But card text lives in only a few regions.
    // Learn them once (full scan), then rescan ONLY those each cycle (tens of ms). Refresh the
    // set occasionally in case the game grows new heap arenas.
    private List<(long baseAddr, long size)> _hotRegions = new();
    private DateTime _lastFullScan = DateTime.MinValue;

    private void Scan(HashSet<int> targets, bool log)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();   // DIAGNOSTIC: measure heap-scan cost
        _slots.Clear();
        _killsCache.Clear();
        var names = new List<(int id, int enc, byte[] b)>();
        var flavors = new List<(int id, int enc, byte[] b)>();
        foreach (int id in targets)
        {
            var m = _meta[id];
            names.Add((id, 1, ByteScan.Ascii(m.Name)));
            names.Add((id, 2, ByteScan.Utf16(m.Name)));
            if (!string.IsNullOrEmpty(m.Flavor))
            {
                flavors.Add((id, 1, ByteScan.Ascii(m.Flavor)));
                flavors.Add((id, 2, ByteScan.Utf16(m.Flavor)));
            }
            _slots[id] = new List<(int, long)>();
            _killsCache[id] = new List<(int, long)>();
        }
        byte[] killsAscii = ByteScan.Ascii("Kills ");
        byte[] killsUtf16 = ByteScan.Utf16("Kills ");

        bool full = _hotRegions.Count == 0 || (DateTime.Now - _lastFullScan).TotalSeconds > FullScanSeconds;
        var regions = full ? Mem.Regions() : (IEnumerable<(long, long)>)_hotRegions;
        var newHot = new List<(long, long)>();   // regions found to hold card text this scan

        long scanned = 0;
        foreach (var (rbase, rsize) in regions)
        {
            if (full && scanned >= ScanCap) break;
            int before = SiteCount(targets);
            long off = 0;
            while (off < rsize && (!full || scanned < ScanCap))
            {
                int want = (int)Math.Min(ChunkSize + Overlap, rsize - off);
                if (!Mem.Readable(rbase + off, want)) { off += ChunkSize; continue; }
                byte[] buf;
                try { buf = Mem.ReadBytes(rbase + off, want); }
                catch { off += ChunkSize; continue; }
                int searchable = (int)Math.Min(ChunkSize, buf.Length);
                ScanNames(buf, searchable, rbase, off, names);
                ScanKills(buf, searchable, rbase, off, flavors, killsAscii, killsUtf16);
                scanned += searchable;
                off += ChunkSize;
            }
            if (SiteCount(targets) > before) newHot.Add((rbase, rsize));   // region holds card text
        }
        if (full) { _hotRegions = newHot; _lastFullScan = DateTime.Now; }

        if (log)
            foreach (int id in targets)
                Log.Info($"display: {_meta[id].Name} nameSites={_slots[id].Count} killSites={_killsCache[id].Count} (paint targets, not kills)");
        Log.Info($"scan {sw.ElapsedMilliseconds}ms {(full ? "FULL" : "hot")} {scanned / (1024 * 1024)}MB, {_hotRegions.Count} hot regions");   // DIAGNOSTIC
    }

    /// <summary>Total paint sites currently found for the target weapons (used to mark hot regions).</summary>
    private int SiteCount(HashSet<int> targets)
    {
        int n = 0;
        foreach (int id in targets) n += _slots[id].Count + _killsCache[id].Count;
        return n;
    }

    /// <summary>Pass 1: weapon name + valid 2-char slot -> per-weapon suffix target.</summary>
    private void ScanNames(byte[] buf, int searchable, long rbase, long off,
                           List<(int id, int enc, byte[] b)> names)
    {
        var span = buf.AsSpan();
        foreach (var (id, enc, nb) in names)
        {
            if (nb.Length == 0) continue;
            int sw = 2 * enc;
            var valid = enc == 1 ? ValidAscii : ValidUtf16;
            int from = 0;
            while (from <= buf.Length - nb.Length)
            {
                int rel = span.Slice(from).IndexOf(nb.AsSpan());
                if (rel < 0) break;
                int i = from + rel;
                from = i + 1;
                if (i >= searchable) break;
                int slotPos = i + nb.Length;
                if (slotPos + sw > buf.Length) continue;
                if (!ByteScan.MatchesAny(buf, slotPos, valid, sw)) continue;
                _slots[id].Add((enc, rbase + off + slotPos));
            }
        }
    }

    /// <summary>Pass 2: "Kills " + 4 digits -> counter, tied to the nearest preceding flavor
    /// line (same encoding) within FlavorWindow -- i.e. the weapon whose description it ends.</summary>
    private void ScanKills(byte[] buf, int searchable, long rbase, long off,
                           List<(int id, int enc, byte[] b)> flavors, byte[] ka, byte[] ku)
    {
        var span = buf.AsSpan();
        foreach (int enc in new[] { 1, 2 })
        {
            byte[] pre = enc == 1 ? ka : ku;
            int dw = 4 * enc;
            int from = 0;
            while (from <= buf.Length - pre.Length)
            {
                int rel = span.Slice(from).IndexOf(pre.AsSpan());
                if (rel < 0) break;
                int i = from + rel;
                from = i + 1;
                if (i >= searchable) break;
                int digPos = i + pre.Length;
                if (digPos + dw > buf.Length) continue;
                if (!FourDigits(buf, digPos, enc)) continue;

                int winStart = Math.Max(0, i - FlavorWindow);
                int bestPos = -1, bestId = -1;
                foreach (var (id, fenc, fb) in flavors)
                {
                    if (fenc != enc || fb.Length == 0 || i - winStart < fb.Length) continue;
                    int pos = buf.AsSpan(winStart, i - winStart).LastIndexOf(fb.AsSpan());
                    if (pos < 0) continue;
                    pos += winStart;
                    if (pos > bestPos) { bestPos = pos; bestId = id; }
                }
                if (bestId >= 0) _killsCache[bestId].Add((enc, rbase + off + digPos));
            }
        }
    }

    private static bool FourDigits(byte[] buf, int pos, int enc)
    {
        for (int d = 0; d < 4; d++)
        {
            if (buf[pos + d * enc] is < (byte)'0' or > (byte)'9') return false;
            if (enc == 2 && buf[pos + d * enc + 1] != 0) return false;
        }
        return true;
    }
}
