using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Paints the equip card: the 2-char name suffix (+/+2/+3), the "Kills NNNN" counter,
/// and the equipped-weapon WP number. Ported from the Python display companion. Runs
/// only OUT of battle. The expensive memory scan is cached; painting from the cache is
/// cheap. CRITICAL: cached addresses can go stale (the game frees/moves UI buffers), so
/// every write is VirtualQuery-guarded AND the suffix slot is re-checked to still hold a
/// valid slot value before we overwrite it -- a recycled buffer is rejected, never poked.
/// </summary>
internal sealed class Display
{
    private const long ChunkSize = 8 * 1024 * 1024;
    private const int Overlap = 4096;              // covers name + slot + the 2KB Kills window
    private const long ScanCap = 6L * 1024 * 1024 * 1024;
    private const double RescanSeconds = 4.0;      // matches the proven cadence

    private static readonly string[] ValidSlots = { "  ", "+ ", "+2", "+3" };
    private static readonly List<byte[]> ValidAscii = ByteScan.Slots(1, ValidSlots);
    private static readonly List<byte[]> ValidUtf16 = ByteScan.Slots(2, ValidSlots);

    private readonly Dictionary<int, WeaponMeta> _meta;
    private readonly Dictionary<int, int> _kills;
    // weapon id -> baked locations: (encoding, suffix-slot address, Kills-digits address or 0)
    private readonly Dictionary<int, List<(int enc, long slot, long kills)>> _cache = new();
    private HashSet<int> _lastTargets = new();
    private DateTime _lastScan = DateTime.MinValue;

    public Display(Dictionary<int, WeaponMeta> meta, Dictionary<int, int> kills)
    {
        _meta = meta;
        _kills = kills;
    }

    public void Tick()
    {
        var targets = new HashSet<int>();
        foreach (var kv in _kills)
            if (Tuning.TierFor(kv.Value) > 0 && _meta.ContainsKey(kv.Key)) targets.Add(kv.Key);
        if (targets.Count == 0) return;

        bool changed = !targets.SetEquals(_lastTargets);
        bool stale = (DateTime.Now - _lastScan).TotalSeconds > RescanSeconds;
        if (changed || stale || _cache.Count == 0)
        {
            try { Scan(targets); } catch (Exception ex) { Log.Error("scan: " + ex.Message); }
            _lastTargets = targets;
            _lastScan = DateTime.Now;
        }
        Paint();
    }

    private void Scan(HashSet<int> targets)
    {
        _cache.Clear();
        var pats = new List<(int id, int enc, byte[] nb)>();
        foreach (int id in targets)
        {
            string name = _meta[id].Name;
            pats.Add((id, 1, ByteScan.Ascii(name)));
            pats.Add((id, 2, ByteScan.Utf16(name)));
            _cache[id] = new List<(int, long, long)>();
        }
        byte[] killsAscii = ByteScan.Ascii("Kills ");
        byte[] killsUtf16 = ByteScan.Utf16("Kills ");

        long scanned = 0;
        foreach (var (rbase, rsize) in Mem.Regions())
        {
            if (scanned >= ScanCap) break;
            long off = 0;
            while (off < rsize && scanned < ScanCap)
            {
                int want = (int)Math.Min(ChunkSize + Overlap, rsize - off);
                if (!Mem.Readable(rbase + off, want)) { off += ChunkSize; continue; }   // re-validate (TOCTOU)
                byte[] buf;
                try { buf = Mem.ReadBytes(rbase + off, want); }
                catch { off += ChunkSize; continue; }
                int searchable = (int)Math.Min(ChunkSize, buf.Length);
                var span = buf.AsSpan();

                foreach (var (id, enc, nb) in pats)
                {
                    if (nb.Length == 0) continue;
                    int sw = 2 * enc;
                    var valid = enc == 1 ? ValidAscii : ValidUtf16;
                    byte[] killsPre = enc == 1 ? killsAscii : killsUtf16;
                    int from = 0;
                    while (from <= buf.Length - nb.Length)
                    {
                        int rel = span.Slice(from).IndexOf(nb.AsSpan());
                        if (rel < 0) break;
                        int i = from + rel;
                        from = i + 1;
                        if (i >= searchable) break;                 // caught in the next chunk
                        int slotPos = i + nb.Length;
                        if (slotPos + sw > buf.Length) continue;
                        if (!ByteScan.MatchesAny(buf, slotPos, valid, sw)) continue;
                        int winEnd = (int)Math.Min((long)i + 2048, buf.Length);
                        int kp = ByteScan.FindIn(buf, killsPre, i, winEnd);
                        long killsAddr = kp >= 0 ? rbase + off + kp + killsPre.Length : 0;
                        _cache[id].Add((enc, rbase + off + slotPos, killsAddr));
                    }
                }
                scanned += searchable;                              // count unique ground, not the overlap
                off += ChunkSize;
            }
        }
    }

    private void Paint()
    {
        foreach (var kv in _cache)
        {
            int kills = _kills.TryGetValue(kv.Key, out int k) ? k : 0;
            int tier = Tuning.TierFor(kills);
            string suffix = Tuning.Suffix[tier];
            string d4 = (kills % 10000).ToString("0000");
            foreach (var (enc, slot, killsAddr) in kv.Value)
            {
                if (SlotIntact(slot, enc)) WriteStr(slot, suffix, enc);   // skip recycled buffers
                if (killsAddr != 0) WriteStr(killsAddr, d4, enc);
            }
        }

        // WP number for the weapon in Ramza's hand (guarded; only when the scratch shows his weapon)
        int rw = Mem.U16(Offsets.RosterBase + Offsets.RRHand);
        if (_meta.TryGetValue(rw, out var m) && Mem.Readable(Offsets.WpScratch, 1))
        {
            int kills = _kills.TryGetValue(rw, out int k) ? k : 0;
            int boosted = Math.Min(255, (int)Math.Round(m.Wp * (1 + Tuning.Factor[Tuning.TierFor(kills)])));
            int cur = Mem.U8(Offsets.WpScratch);
            if ((cur == m.Wp || cur == boosted) && boosted != cur && Mem.Writable(Offsets.WpScratch, 1))
                Mem.W8(Offsets.WpScratch, (byte)boosted);
        }
    }

    /// <summary>A cached suffix slot is still ours only if it still holds a valid slot value.</summary>
    private static bool SlotIntact(long addr, int enc)
    {
        int sw = 2 * enc;
        if (!Mem.Readable(addr, sw)) return false;
        var valid = enc == 1 ? ValidAscii : ValidUtf16;
        return ByteScan.MatchesAny(Mem.ReadBytes(addr, sw), 0, valid, sw);
    }

    private static void WriteStr(long addr, string s, int enc)
    {
        byte[] bytes = ByteScan.Enc(s, enc);
        if (Mem.Writable(addr, bytes.Length)) Mem.WriteBytes(addr, bytes);
    }
}
