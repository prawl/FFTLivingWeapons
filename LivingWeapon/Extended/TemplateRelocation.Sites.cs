namespace LivingWeapon;

/// <summary>
/// LW-371 (plan v1.1): the ten-field map <see cref="TemplateRelocation.Install"/> rewrites (three
/// pointer-table slots, five rip-relative int32 fields, two menu-list-cap bytes) plus the three
/// chart descriptors the lifecycle and <see cref="TemplateSync"/> both read. Split out the way
/// ListRelocation.Sites.cs is split from ListRelocation.cs: this file is the WHAT (the table),
/// TemplateRelocation.cs is the lifecycle (Install/Restore/Regions). Same partial class, so
/// TemplateRelocation.cs reaches every name here by plain name.
///
/// Provenance: LW371_plan.md findings 1 (the slot sweep), 4 (the all-items chart and its five
/// fields) and 5 (the two menu-list caps and their stack room), all re-derived against the live
/// process by tools/probes/lw371_order_template_relocate.py --scan, and the reviewer's RE
/// re-check table (reproduced at the end of the plan) for the exact site bytes and the GS-cookie
/// arithmetic that corrected the v1.0 caps: 159 -> 151 (the cookie) -> 149 (fnA's two hand-item
/// appends, v1.3, see the CapBytes doc below).
/// </summary>
internal sealed partial class TemplateRelocation
{
    /// <summary>Which chart a <see cref="Slot"/> or the page copies target. The all-items chart is
    /// never named by a slot (finding 4: its only readers are the five <see cref="RipField"/>s).</summary>
    internal enum ChartId { Inventory, Picker, AllItems }

    /// <summary>Byte offset inside the granted page each chart's copy lands at (D2): 0x000 bytes
    /// of headroom per chart, 0x400 bytes each, +0xC00 left unused this round.</summary>
    internal const int InventoryPageOffset = 0x000;
    internal const int PickerPageOffset = 0x400;
    internal const int AllItemsPageOffset = 0x800;
    /// <summary>Bytes granted to each chart's copy on the page: 512 words total -- 510 ids + the
    /// marker (<see cref="PageRegionCapacityWords"/> = 511, the CAPACITY FRAME, D2) plus one
    /// further wall word TemplateSeat never touches. The physical byte count a chart's
    /// <see cref="Chart.PageOffset"/> region spans.</summary>
    internal const int RegionBytes = 0x400;
    /// <summary>The page requested from the allocator: three 0x400-byte regions plus 0x400 bytes
    /// of headroom (D2).</summary>
    internal const int PageSize = 0x1000;
    /// <summary>Capacity handed to <see cref="TemplateSeat.Region"/> for a page chart once
    /// installed (D2 CAPACITY FRAME, v1.2): a <see cref="TemplateSeat.Region"/>'s CapacityWords
    /// COUNTS THE MARKER SLOT (<see cref="TemplateSeat.Plan"/> refuses at
    /// <c>marker + n &gt; capacity - 1</c>), so 511 here means 510 ids PLUS the marker, not 511
    /// ids -- word 511 of the physical 512-word region is the one word past that bound, left
    /// outside <see cref="TemplateSeat"/>'s own capacity on purpose; Install's page-fill wall
    /// already covers it with 0xFFFF, so nothing ever reads it as meaningful.</summary>
    internal const int PageRegionCapacityWords = 511;

    private static int PageOffsetOf(ChartId chart) => chart switch
    {
        ChartId.Inventory => InventoryPageOffset,
        ChartId.Picker => PickerPageOffset,
        _ => AllItemsPageOffset,
    };

    /// <summary>One pointer-table qword: the address that names one of the two OLD chart bases
    /// today (finding 1) and must be re-pointed at the matching page region.</summary>
    internal readonly record struct Slot(long Addr, long Vanilla, ChartId Chart, string Label);

    /// <summary>One rip-relative int32 field of the all-items chart's own function (finding 4,
    /// reviewer (a)). <see cref="Off"/> is the byte offset from the all-items chart's OWN page
    /// target (0 for four of the five fields; -0x1E6 for the shared rsi base, whose only uses are
    /// [rsi+rcx*2+0x1E6] and [rsi+rcx*2+0x1E8], so it must land 0x1E6 bytes BEFORE the chart so
    /// those indexed reads still resolve onto the page copy). None of the five carries a trailing
    /// immediate (the mov-word site's operand is a register, not an imm), so unlike
    /// ListRelocation's round-2b sites this record needs no Trail field.</summary>
    internal readonly record struct RipField(long Addr, int Vanilla, int Off, string Label);

    /// <summary>One menu-list-cap byte (finding 5, D5): the imm32 low byte of a `cmp reg,imm32`
    /// six-byte instruction.</summary>
    internal readonly record struct CapByte(long Addr, byte Vanilla, byte New, string Label);

    /// <summary>One chart: where it lives today, how many bytes the game's own copy spans, where
    /// its page copy lands, how many bytes that region gets, and the word capacity
    /// <see cref="TemplateSync.Projection"/> truncates the PAGE side to (the old block's own total
    /// word count, marker included -- 141 / 141 / 262, D4).</summary>
    internal readonly record struct Chart(long OldBase, int SpanBytes, int PageOffset, int RegionBytes, int Capacity, string Label);

    /// <summary>The three pointer-table slots (finding 1). Both inventory slots read the same
    /// vanilla base today; only the label tells them apart.</summary>
    internal static readonly Slot[] Slots =
    {
        new(Offsets.InventoryOrderTableSlot, Offsets.InventoryOrderTemplate, ChartId.Inventory,
            "inventory order template slot A (housekeeper 0x140285F80)"),
        new(Offsets.InventoryOrderTableSlotB, Offsets.InventoryOrderTemplate, ChartId.Inventory,
            "inventory order template slot B (third housekeeper 0x14039684C)"),
        new(Offsets.PickerOrderTableSlot, Offsets.PickerOrderTemplate, ChartId.Picker,
            "picker order template slot (housekeeper 0x14028609F)"),
    };

    /// <summary>The five all-items rip fields, reviewer (a)'s table verbatim (vanilla int32s are
    /// pinned by arithmetic in TemplateRelocationTests, not hand-typed here).</summary>
    internal static readonly RipField[] RipFields =
    {
        new(0x14028617CL, 0x015EE3C0, -0x1E6, "lea rsi, the shared picker base (site 0x140286179)"),
        new(0x1402861A3L, 0x015EE57F, 0, "lea rcx (site 0x1402861A0)"),
        new(0x1402861F5L, 0x015EE52D, 0, "mov word ptr [rip+d],bx, 7 bytes, no trailing immediate (site 0x1402861F2)"),
        new(0x1402861FEL, 0x015EE524, 0, "lea rdx (site 0x1402861FB)"),
        new(0x1402862CEL, 0x015EE454, 0, "lea rcx feeding the rebuild call at 0x1402862D2 (type 8 by the disassembly; types 6/7 branch to 0x140286342) (site 0x1402862CB)"),
    };

    /// <summary>The two menu-list caps (finding 5, D5, v1.3 P14). 149, not 151: the picker's
    /// weapons-only list caller fnA (entry 0x1402875A4) appends the unit's two hand items AFTER
    /// the capped builder list (stores at 0x14028778C and 0x1402877A9, each only if the validity
    /// thunk 0x1402B8EE8 accepts the id) and THEN writes the 0xFFFF terminator (0x1402877C0), so
    /// its 0x130-byte stack buffer must hold cap + 3 words, not cap + 1: (0x95 + 3) * 2 = 0x130
    /// exactly; 0x97 (151) would land the terminator on the GS cookie at (151 + 3) * 2 = 0x134.</summary>
    internal static readonly CapByte[] CapBytes =
    {
        new(Offsets.ListBuilderCapByte, 0x91, 0x95, "list-builder entry cap (cmp esi,0x91 at 0x140288CC1)"),
        new(Offsets.ListInsertBoundByte, 0x92, 0x96, "list-insert bound (cmp edx,0x92 at 0x140286318)"),
    };

    /// <summary>The three charts, in page order (D2).</summary>
    internal static readonly Chart[] Charts =
    {
        new(Offsets.InventoryOrderTemplate, Offsets.InventoryOrderTemplateWords * 2, InventoryPageOffset,
            RegionBytes, Offsets.InventoryOrderTemplateWords, "the inventory order template"),
        new(Offsets.PickerOrderTemplate, Offsets.PickerOrderTemplateWords * 2, PickerPageOffset,
            RegionBytes, Offsets.PickerOrderTemplateWords, "the equip-picker order template"),
        new(Offsets.PickerAllItemsTemplate, Offsets.PickerAllItemsTemplateWords * 2, AllItemsPageOffset,
            RegionBytes, Offsets.PickerAllItemsTemplateWords, "the picker all-items template"),
    };
}
