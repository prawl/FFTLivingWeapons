using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// LW-371 lifts the 140-kinds ceiling on the two weapon menu order templates and the picker's all-items chart.
/// The game keeps them in fixed tables of 140 kinds plus an end marker (141/141/262 words) and its housekeeper never checks the limit. A 141st kind lands at the front and the last word spills into the next data (the helmet chart, on the picker side).
/// This copies the three charts onto a mod-owned page and re-points ten places in game code (three data slots, five code fields, two cap sites, listed in TemplateRelocation.Sites.cs) at 512-word regions holding 510 ids plus the marker.
/// Like <see cref="ListRelocation"/>, every field must read vanilla before any write, and a refusal midway restores the changed fields in reverse.
/// Unlike it, the save fields keep today's content (D4/F5), so <see cref="TemplateSync"/> keeps Project (page to old block, on save) and Adopt (old block to page, on load) live while armed.
/// Restore puts only the ten fields back (D7) and the page is leaked, as <see cref="INearAllocator"/> does. Memory goes through <see cref="ICodePatcher"/> and <see cref="INearAllocator"/>.
/// </summary>
internal sealed partial class TemplateRelocation
{
    // volatile, written last so a true read means PageAddr is already published
    private volatile bool _installed;

    /// <summary>True once every field points at the new page.</summary>
    public bool Installed => _installed;

    /// <summary>The page all three charts live on once installed; 0 before that.</summary>
    public long PageAddr { get; private set; }

    /// <summary>Null on success, else the refusal (nothing changed). Idempotent. Order as in ListRelocation.Install. Check all ten
    /// fields read vanilla, allocate and reach-check the page, copy the three charts onto it (D2/D3, T1/T11), then rewrite the
    /// fields, so any earlier refusal leaves the process untouched.</summary>
    public string? Install(ICodePatcher patcher, INearAllocator allocator)
    {
        if (Installed) return null;
        // Snapshot each live table once per call, the properties build a fresh array per read
        var slots = Slots;
        var capSites = CapSites;
        var charts = Charts;
        foreach (var s in slots)
        {
            if (!patcher.TryRead(s.Addr, 8, out var b))
                return $"template-relocation: 0x{s.Addr:X} is unreadable (expected vanilla 0x{s.Vanilla:X})";
            long now = BitConverter.ToInt64(b, 0);
            if (now != s.Vanilla)
                return $"template-relocation: 0x{s.Addr:X} reads 0x{now:X}, expected vanilla 0x{s.Vanilla:X} (already redirected or moved)";
        }
        foreach (var f in RipFields)
        {
            if (!patcher.TryRead(Live(f), 4, out var b))
                return $"template-relocation: 0x{Live(f):X} is unreadable (expected vanilla 0x{f.Vanilla:X8})";
            int now = BitConverter.ToInt32(b, 0);
            if (now != f.Vanilla)
                return $"template-relocation: 0x{Live(f):X} reads 0x{now:X8}, expected vanilla 0x{f.Vanilla:X8} (already redirected or moved)";
        }
        foreach (var c in capSites)
        {
            if (!patcher.TryRead(c.Addr, c.Vanilla.Length, out var b))
                return $"template-relocation: 0x{c.Addr:X} is unreadable (expected vanilla 0x{Convert.ToHexString(c.Vanilla)})";
            if (!BytesEqual(b, c.Vanilla))
                return $"template-relocation: 0x{c.Addr:X} reads 0x{Convert.ToHexString(b)}, expected vanilla 0x{Convert.ToHexString(c.Vanilla)} (already redirected or moved)";
        }

        long page = allocator.Alloc(PageSize, ImageBase.Base);
        if (page == 0) return "template-relocation: no page within reach of the image base";
        string? reach = CheckReach(page);
        if (reach != null) return reach;

        var spans = new byte[charts.Length][];
        for (int i = 0; i < charts.Length; i++)
        {
            if (!patcher.TryRead(charts[i].OldBase, charts[i].SpanBytes, out spans[i]))
                return $"template-relocation: the old {charts[i].Label} span at 0x{charts[i].OldBase:X} is unreadable";
        }
        for (int i = 0; i < charts.Length; i++)
        {
            byte[] image = TemplateSync.RegionImage(spans[i], charts[i].RegionBytes);
            if (!patcher.TryWrite(page + charts[i].PageOffset, image))
                return $"template-relocation: the {charts[i].Label} page copy was refused";
        }

        var written = new List<(long Addr, byte[] Old)>(slots.Length + RipFields.Length + capSites.Length);
        foreach (var s in slots)
        {
            if (!patcher.TryWrite(s.Addr, BitConverter.GetBytes(page + PageOffsetOf(s.Chart))))
            { RollBack(patcher, written); return $"template-relocation: field write refused at 0x{s.Addr:X}"; }
            written.Add((s.Addr, BitConverter.GetBytes(s.Vanilla)));
        }
        foreach (var f in RipFields)
        {
            if (!patcher.TryWrite(Live(f), BitConverter.GetBytes(NewField(f, page))))
            { RollBack(patcher, written); return $"template-relocation: field write refused at 0x{Live(f):X}"; }
            written.Add((Live(f), BitConverter.GetBytes(f.Vanilla)));
        }
        foreach (var c in capSites)
        {
            if (!patcher.TryWrite(c.Addr, c.New))
            { RollBack(patcher, written); return $"template-relocation: field write refused at 0x{c.Addr:X}"; }
            written.Add((c.Addr, c.Vanilla));
        }

        PageAddr = page;
        _installed = true;   // last, publishes PageAddr
        return null;
    }

    /// <summary>Puts every field back to vanilla and leaves the page alone (D7). A call while not installed succeeds, like <see cref="ListRelocation.Restore"/>.</summary>
    public bool Restore(ICodePatcher patcher)
    {
        if (!Installed) return true;
        bool ok = true;
        foreach (var s in Slots) ok &= patcher.TryWrite(s.Addr, BitConverter.GetBytes(s.Vanilla));
        foreach (var f in RipFields) ok &= patcher.TryWrite(Live(f), BitConverter.GetBytes(f.Vanilla));
        foreach (var c in CapSites) ok &= patcher.TryWrite(c.Addr, c.Vanilla);
        if (ok) _installed = false;
        return ok;
    }

    /// <summary>The two weapon-chart regions in effect, for <see cref="TemplateSeat"/> and <see cref="OrderRebuildHook"/>. The page regions (capacity
    /// <see cref="PageRegionCapacityWords"/>) once installed, else <see cref="TemplateSeat.WeaponRegions"/>. The all-items chart is not a
    /// <see cref="TemplateSeat.Region"/>, it only follows the game's housekeeper through <see cref="TemplateSync"/>.</summary>
    public TemplateSeat.Region[] Regions => Installed
        ? new[]
        {
            new TemplateSeat.Region(PageAddr + InventoryPageOffset, PageRegionCapacityWords, "the inventory order template"),
            new TemplateSeat.Region(PageAddr + PickerPageOffset, PageRegionCapacityWords, "the equip-picker order template"),
        }
        : TemplateSeat.WeaponRegions;

    private static void RollBack(ICodePatcher patcher, List<(long Addr, byte[] Old)> written)
    {
        for (int i = written.Count - 1; i >= 0; i--) patcher.TryWrite(written[i].Addr, written[i].Old);
    }

    /// <summary>D6/REACH, as in ListRelocation.CheckReach. Only the five rip fields need int32 reach (a slot holds a 64-bit pointer). Runs before any write.</summary>
    private static string? CheckReach(long page)
    {
        foreach (var f in RipFields)
        {
            long distance = page + AllItemsPageOffset + f.Off - (Live(f) + 4);
            if (distance < int.MinValue || distance > int.MaxValue)
                return $"template-relocation: page 0x{page:X} is out of int32 reach of field 0x{Live(f):X}";
        }
        return null;
    }

    /// <summary>The int32 a rip field must hold to reach the page's all-items copy, offset by <see cref="RipField.Off"/>.
    /// Truncates without checking, so <see cref="Install"/> runs <see cref="CheckReach"/> first.</summary>
    internal static int NewField(RipField f, long page) => (int)(page + AllItemsPageOffset + f.Off - (Live(f) + 4));

    /// <summary>LW-388. <see cref="RipField.Addr"/> is PIN space; this is the field's address in the running image.</summary>
    private static long Live(RipField f) => ImageBase.At(f.Addr);

    /// <summary>LW-372 (D4). A <see cref="CapSite"/> verify-read is 1 byte (builder cap) or 4 (insert bound), so compare whole arrays.</summary>
    private static bool BytesEqual(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }
}
