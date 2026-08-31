using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// LW-371: lifts the 140-kinds-at-once ceiling on the two weapon menu order templates (and moves
/// the picker's "every owned item" chart alongside, a third instance of the same shape). Plain
/// language: the game lists the weapons and shields you carry from two fixed-size tables that hold
/// at most 140 kinds plus an end marker, and the game's own chart housekeeper never checks that
/// limit -- a 141st kind is shoved in at the front and the last word falls off the end into
/// whatever data sits next (on the picker side, the helmet chart). This class copies all three
/// charts onto a page the mod owns and re-points the ten places in the game's code that name the
/// old blocks -- three DATA slots, five CODE fields and two CAP SITES (the table lives in
/// TemplateRelocation.Sites.cs, the WHAT half of this partial) -- so every walker, inserter and
/// deleter now runs against 512-word regions holding 510 ids + the marker, instead of the old
/// 141/141/262-word blocks holding 140/140/261 ids + the marker. Same posture as its closest
/// sibling, <see cref="ListRelocation"/>: every
/// field is read back and must still carry its VANILLA value before a single byte is written, and
/// a refusal midway restores every field this call already changed, in reverse, before it returns.
///
/// UNLIKE ListRelocation this is not a pure move: the save file's fields keep today's content (the
/// owner's v1.1 ruling, D4/F5), so the two edges the sync half owns (<see cref="TemplateSync"/>)
/// stay live for as long as the relocation is armed -- Project on save (page -&gt; old block, before
/// the game copies the old block into the struct) and Adopt on load (old block -&gt; page, after the
/// game restores the old block from the struct). Restore only ever puts the ten fields back (D7): a
/// mid-session copy-back would race whatever the game's own threads are doing to the page, so the
/// page itself is leaked by design, the same "never free a code/data page" rule
/// <see cref="INearAllocator"/> already keeps.
///
/// Every memory access goes through the injected <see cref="ICodePatcher"/> (VirtualProtect +
/// RPM/WPM guarded, never a raw pointer deref) and <see cref="INearAllocator"/> (never frees).
/// </summary>
internal sealed partial class TemplateRelocation
{
    // volatile (the ListRelocation idiom): written LAST, after every other field below, so a true
    // read here guarantees PageAddr already published (release/acquire semantics) to whichever
    // thread calls Regions/Installed once armed.
    private volatile bool _installed;

    /// <summary>True once every field points at the new page.</summary>
    public bool Installed => _installed;

    /// <summary>The page all three charts live on once installed; 0 before that.</summary>
    public long PageAddr { get; private set; }

    /// <summary>Null on success, else the refusal (nothing changed). Idempotent once installed.
    /// Order (mirrors ListRelocation.Install): every one of the ten fields must already read
    /// vanilla, THEN the page is allocated and reach-checked, THEN the three charts are copied onto
    /// it (D2/D3, one write per chart, before the first field write -- T1/T11), and only THEN does
    /// the first field get rewritten -- so a refusal at any step before the last leaves every byte
    /// in the process exactly as it was found.</summary>
    public string? Install(ICodePatcher patcher, INearAllocator allocator)
    {
        if (Installed) return null;
        foreach (var s in Slots)
        {
            if (!patcher.TryRead(s.Addr, 8, out var b))
                return $"template-relocation: 0x{s.Addr:X} is unreadable (expected vanilla 0x{s.Vanilla:X})";
            long now = BitConverter.ToInt64(b, 0);
            if (now != s.Vanilla)
                return $"template-relocation: 0x{s.Addr:X} reads 0x{now:X}, expected vanilla 0x{s.Vanilla:X} (already redirected or moved)";
        }
        foreach (var f in RipFields)
        {
            if (!patcher.TryRead(f.Addr, 4, out var b))
                return $"template-relocation: 0x{f.Addr:X} is unreadable (expected vanilla 0x{f.Vanilla:X8})";
            int now = BitConverter.ToInt32(b, 0);
            if (now != f.Vanilla)
                return $"template-relocation: 0x{f.Addr:X} reads 0x{now:X8}, expected vanilla 0x{f.Vanilla:X8} (already redirected or moved)";
        }
        foreach (var c in CapSites)
        {
            if (!patcher.TryRead(c.Addr, c.Vanilla.Length, out var b))
                return $"template-relocation: 0x{c.Addr:X} is unreadable (expected vanilla 0x{Convert.ToHexString(c.Vanilla)})";
            if (!BytesEqual(b, c.Vanilla))
                return $"template-relocation: 0x{c.Addr:X} reads 0x{Convert.ToHexString(b)}, expected vanilla 0x{Convert.ToHexString(c.Vanilla)} (already redirected or moved)";
        }

        long page = allocator.Alloc(PageSize, Offsets.ModuleBase);
        if (page == 0) return "template-relocation: no page within reach of the image base";
        string? reach = CheckReach(page);
        if (reach != null) return reach;

        var spans = new byte[Charts.Length][];
        for (int i = 0; i < Charts.Length; i++)
        {
            if (!patcher.TryRead(Charts[i].OldBase, Charts[i].SpanBytes, out spans[i]))
                return $"template-relocation: the old {Charts[i].Label} span at 0x{Charts[i].OldBase:X} is unreadable";
        }
        for (int i = 0; i < Charts.Length; i++)
        {
            byte[] image = TemplateSync.RegionImage(spans[i], Charts[i].RegionBytes);
            if (!patcher.TryWrite(page + Charts[i].PageOffset, image))
                return $"template-relocation: the {Charts[i].Label} page copy was refused";
        }

        var written = new List<(long Addr, byte[] Old)>(Slots.Length + RipFields.Length + CapSites.Length);
        foreach (var s in Slots)
        {
            if (!patcher.TryWrite(s.Addr, BitConverter.GetBytes(page + PageOffsetOf(s.Chart))))
            { RollBack(patcher, written); return $"template-relocation: field write refused at 0x{s.Addr:X}"; }
            written.Add((s.Addr, BitConverter.GetBytes(s.Vanilla)));
        }
        foreach (var f in RipFields)
        {
            if (!patcher.TryWrite(f.Addr, BitConverter.GetBytes(NewField(f, page))))
            { RollBack(patcher, written); return $"template-relocation: field write refused at 0x{f.Addr:X}"; }
            written.Add((f.Addr, BitConverter.GetBytes(f.Vanilla)));
        }
        foreach (var c in CapSites)
        {
            if (!patcher.TryWrite(c.Addr, c.New))
            { RollBack(patcher, written); return $"template-relocation: field write refused at 0x{c.Addr:X}"; }
            written.Add((c.Addr, c.Vanilla));
        }

        PageAddr = page;
        _installed = true;   // last: publishes PageAddr above (release/acquire)
        return null;
    }

    /// <summary>Puts every field back to its vanilla value; never touches the page (D7: no
    /// copy-back, the page is leaked by design). Idempotent: a call while not installed is a no-op
    /// success, matching <see cref="ListRelocation.Restore"/>.</summary>
    public bool Restore(ICodePatcher patcher)
    {
        if (!Installed) return true;
        bool ok = true;
        foreach (var s in Slots) ok &= patcher.TryWrite(s.Addr, BitConverter.GetBytes(s.Vanilla));
        foreach (var f in RipFields) ok &= patcher.TryWrite(f.Addr, BitConverter.GetBytes(f.Vanilla));
        foreach (var c in CapSites) ok &= patcher.TryWrite(c.Addr, c.Vanilla);
        if (ok) _installed = false;
        return ok;
    }

    /// <summary>The two weapon-chart regions in effect right now, for <see cref="TemplateSeat"/>
    /// and <see cref="OrderRebuildHook"/>: the page regions (capacity <see
    /// cref="PageRegionCapacityWords"/>) once installed, else the vanilla pair
    /// (<see cref="TemplateSeat.WeaponRegions"/>). The all-items chart is never a
    /// <see cref="TemplateSeat.Region"/> -- nothing seats extended ids into it directly, it only
    /// follows the game's own housekeeper through <see cref="TemplateSync"/>.</summary>
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

    /// <summary>D6/REACH, same doctrine as ListRelocation.CheckReach: only the five rip fields need
    /// int32 reach (a slot holds a full 64-bit pointer, so it always reaches). Checked before
    /// anything is written, not even the page fill.</summary>
    private static string? CheckReach(long page)
    {
        foreach (var f in RipFields)
        {
            long distance = page + AllItemsPageOffset + f.Off - (f.Addr + 4);
            if (distance < int.MinValue || distance > int.MaxValue)
                return $"template-relocation: page 0x{page:X} is out of int32 reach of field 0x{f.Addr:X}";
        }
        return null;
    }

    /// <summary>Pure: the int32 one rip field must hold to reach <paramref name="page"/>'s
    /// all-items copy, offset by <see cref="RipField.Off"/>. Callers that write the result must go
    /// through <see cref="CheckReach"/> first (<see cref="Install"/> does); this itself truncates
    /// unconditionally.</summary>
    internal static int NewField(RipField f, long page) => (int)(page + AllItemsPageOffset + f.Off - (f.Addr + 4));

    /// <summary>Pure: LW-372 (D4) -- a <see cref="CapSite"/>'s verify-read compares a variable
    /// number of bytes now (1 for the builder cap, 4 for the insert bound), so the fixed single-byte
    /// compare Install used before this widening is a plain array-equality helper instead.</summary>
    private static bool BytesEqual(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }
}
