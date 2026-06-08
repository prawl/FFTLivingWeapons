using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Paints the equip card: the 2-char name suffix (+/+2/+3) and the per-weapon
/// "Kills NNNN" counter, plus the equipped-weapon WP number. The scan (DisplayScan.cs)
/// is cached; painting from the cache is cheap.
///
/// Each weapon paints its OWN count, anchored to its own name. All reads/writes go
/// through Mem (RPM/WPM-backed), so writing into a UI buffer the game just freed is a
/// safe no-op, not a crash. Painting is gated out of battle (Engine decides when, via
/// battleMode) and only repaints on change, so we never hammer churning battlefield
/// buffers.
/// </summary>
internal sealed partial class Display
{
    private const double RescanSeconds = 0.25;  // re-find re-rendered card buffers (cheap: heap-only scan)

    private static readonly string[] ValidSlots = { "  ", "+ ", "+2", "+3" };
    private static readonly List<byte[]> ValidAscii = ByteScan.Slots(1, ValidSlots);
    private static readonly List<byte[]> ValidUtf16 = ByteScan.Slots(2, ValidSlots);

    private readonly Dictionary<int, WeaponMeta> _meta;
    private readonly Dictionary<int, int> _kills;
    private readonly Dictionary<int, List<(int enc, long addr)>> _slots = new();       // id -> suffix slots
    private readonly Dictionary<int, List<(int enc, long addr)>> _killsCache = new();  // id -> "Kills NNNN" digits
    private readonly Dictionary<int, List<(int enc, long addr)>> _grantCache = new();  // id -> "Grant <ability>" label slots
    private HashSet<int> _lastTargets = new();
    private DateTime _lastScan = DateTime.MinValue;
    private int _lastPaintSig = int.MinValue;

    public Display(Dictionary<int, WeaponMeta> meta, Dictionary<int, int> kills)
    {
        _meta = meta;
        _kills = kills;
    }

    /// <summary>Drop the cache + force a rescan next tick (call on battle exit, where the
    /// game reallocates the menu's render buffers).</summary>
    public void Invalidate()
    {
        _slots.Clear();
        _killsCache.Clear();
        _grantCache.Clear();
        _lastTargets = new HashSet<int>();
        _lastScan = DateTime.MinValue;
        _lastPaintSig = int.MinValue;
    }

    public void Tick()
    {
        // Paint the viewed unit's EQUIPPED weapons. The equip mirror holds its loadout in slot
        // order: [0]=right hand (0x141870854), [1]=left/off-hand. A dual-wielder's 2nd weapon sits
        // in [1], so target BOTH hands or the off-hand's card never gets its counter. Still bounded
        // to two weapons -- NOT the all-weapons paint that once wrote into hundreds of stale render
        // copies and crashed. Meta-gating drops a shield in [1]; tier-gating drops un-leveled weapons.
        var targets = new HashSet<int>();
        AddTarget(targets, Mem.U16(Offsets.MirrorWeapon));
        AddTarget(targets, Mem.U16(Offsets.MirrorOffHand));
        if (targets.Count == 0) return;

        int sig = PaintSig(targets);
        bool changed = !targets.SetEquals(_lastTargets);
        // A kill/tier-up while the card is open changes the count -> RESCAN (not just repaint):
        // the game re-renders the name/desc into a fresh buffer on the level-up, so the cached
        // paint address is stale. Re-finding it lands the new +N / count within a tick (~100ms)
        // instead of waiting for the idle rescan.
        bool countChanged = sig != _lastPaintSig;
        bool stale = (DateTime.Now - _lastScan).TotalSeconds > RescanSeconds;
        bool scannedNow = changed || countChanged || stale || _slots.Count == 0;
        if (scannedNow)
        {
            try { Scan(targets, changed); } catch (Exception ex) { Log.Error("scan: " + ex.Message); }
            _lastTargets = targets;
            _lastScan = DateTime.Now;
            // Paint ONLY the sites this scan just verified hold THIS weapon's flavor+counter.
            // Painting cached sites every tick stamped a weapon's count onto heap buffers the
            // game had reused for OTHER cards (every card briefly read the last weapon's count).
            // Scanning re-confirms the flavor is still there, so a reused buffer is never stamped.
            Paint();
        }
        _lastPaintSig = sig;
    }

    private void Paint()
    {
        foreach (var kv in _slots)
        {
            string suffix = Tuning.Suffix[Tuning.TierFor(_kills.TryGetValue(kv.Key, out int k) ? k : 0)];
            foreach (var (enc, addr) in kv.Value)
                if (SlotIntact(addr, enc)) WriteStr(addr, suffix, enc);
        }
        foreach (var kv in _killsCache)
        {
            string d4 = ((_kills.TryGetValue(kv.Key, out int k) ? k : 0) % 10000).ToString("0000");
            foreach (var (enc, addr) in kv.Value)
                if (KillsIntact(addr, enc)) WriteStr(addr, d4, enc);
        }
        // Signature ability label: paint the granted ability's name once its tier is earned, blank
        // below it. Same per-weapon, scan-confirmed, guarded paint as the counter above.
        foreach (var kv in _grantCache)
        {
            if (!_meta.TryGetValue(kv.Key, out var gm)) continue;
            int tier = Tuning.TierFor(_kills.TryGetValue(kv.Key, out int k) ? k : 0);
            string text = Signatures.GrantSlot(Signatures.ShowsGrant(gm.Signature, tier) ? gm.Signature!.DisplayLabel : "");
            foreach (var (enc, addr) in kv.Value)
                if (GrantIntact(addr, enc)) WriteStr(addr, text, enc);
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

    /// <summary>Order-independent hash of the tiered weapons' counts, so a kill (count change)
    /// triggers a repaint without hammering writes every tick.</summary>
    private int PaintSig(HashSet<int> targets)
    {
        int sig = 0;
        foreach (int id in targets)
            sig ^= (id * 397) ^ ((_kills.TryGetValue(id, out int k) ? k : 0) + 1);
        return sig;
    }

    /// <summary>Add a mirror-slot weapon to the paint targets if it's a tracked, leveled weapon --
    /// drops empties (0/0xFFFF), a shield in the off-hand slot (not in meta), and tier-0 weapons.</summary>
    private void AddTarget(HashSet<int> targets, int id)
    {
        if (id > 0 && id < 0xFFFF && _meta.ContainsKey(id)
            && Tuning.TierFor(_kills.TryGetValue(id, out int k) ? k : 0) > 0)
            targets.Add(id);
    }

    /// <summary>A cached suffix slot is still ours only if it still holds a valid slot value.</summary>
    private static bool SlotIntact(long addr, int enc)
    {
        int sw = 2 * enc;
        if (!Mem.Readable(addr, sw)) return false;
        var valid = enc == 1 ? ValidAscii : ValidUtf16;
        return Mem.TryReadBytes(addr, sw, out var got) && ByteScan.MatchesAny(got, 0, valid, sw);
    }

    /// <summary>A cached Kills digit slot is still ours only if "Kills " still sits immediately
    /// before it and four ASCII digits sit at it -- rejects recycled/freed buffers.</summary>
    private static bool KillsIntact(long addr, int enc)
    {
        byte[] pre = ByteScan.Enc("Kills ", enc);
        int dw = 4 * enc;
        long preAddr = addr - pre.Length;
        if (!Mem.TryReadBytes(preAddr, pre.Length + dw, out var got)) return false;
        for (int j = 0; j < pre.Length; j++) if (got[j] != pre[j]) return false;
        for (int d = 0; d < 4; d++)
        {
            if (got[pre.Length + d * enc] is < (byte)'0' or > (byte)'9') return false;
            if (enc == 2 && got[pre.Length + d * enc + 1] != 0) return false;
        }
        return true;
    }

    /// <summary>A cached grant slot is still ours only if "Grant " sits immediately before it and
    /// the slot holds GrantWidth printable/space chars -- rejects recycled/freed buffers.</summary>
    private static bool GrantIntact(long addr, int enc)
    {
        byte[] pre = ByteScan.Enc("Grant ", enc);
        int sw = Signatures.GrantWidth * enc;
        long preAddr = addr - pre.Length;
        if (!Mem.TryReadBytes(preAddr, pre.Length + sw, out var got)) return false;
        for (int j = 0; j < pre.Length; j++) if (got[j] != pre[j]) return false;
        return ByteScan.GrantSlot(got, pre.Length, enc);
    }

    private static void WriteStr(long addr, string s, int enc)
    {
        byte[] bytes = ByteScan.Enc(s, enc);
        if (Mem.Writable(addr, bytes.Length)) Mem.WriteBytes(addr, bytes);
    }
}
