namespace LivingWeapon;

/// <summary>
/// Where the game keeps its three menu order charts and every spot in its code that points at them.
/// <see cref="TemplateRelocation.Install"/> rewrites ten fields, three pointer table slots, five
/// rip relative int32 fields and two menu list caps, and the lifecycle and <see cref="TemplateSync"/>
/// read the three chart descriptors. The addresses were re-derived with
/// tools/probes/lw371_order_template_relocate.py --scan. The list cap went 159, then 151 to stay clear of
/// the stack cookie, then 149 because fnA appends two hand items (see CapSites). It now widens to
/// 255 and 256, which no longer fits a byte, so <see cref="CapSite"/> holds byte arrays.
/// </summary>
internal sealed partial class TemplateRelocation
{
    /// <summary>Which chart a <see cref="Slot"/> or a page copy belongs to. No slot names the
    /// all items chart, only the five <see cref="RipField"/>s read it.</summary>
    internal enum ChartId { Inventory, Picker, AllItems }

    /// <summary>Where each chart's copy starts in our page, 0x400 bytes apart.</summary>
    internal const int InventoryPageOffset = 0x000;
    internal const int PickerPageOffset = 0x400;
    internal const int AllItemsPageOffset = 0x800;
    /// <summary>512 words per chart, which is 510 ids, the end marker, and one spare word
    /// TemplateSeat never touches (<see cref="PageRegionCapacityWords"/> counts the ids and the marker).</summary>
    internal const int RegionBytes = 0x400;
    /// <summary>Three 0x400 byte charts and 0x400 bytes to spare.</summary>
    internal const int PageSize = 0x1000;
    /// <summary>The marker takes a slot too (<see cref="TemplateSeat.Plan"/> refuses once <c>marker + n &gt; capacity, 1</c>), so 511 is
    /// 510 ids plus the marker. Word 511 of the 512 sits past that bound as a wall, and Install fills it with 0xFFFF.</summary>
    internal const int PageRegionCapacityWords = 511;

    private static int PageOffsetOf(ChartId chart) => chart switch
    {
        ChartId.Inventory => InventoryPageOffset,
        ChartId.Picker => PickerPageOffset,
        _ => AllItemsPageOffset,
    };

    /// <summary>A pointer table qword that points at a chart where the game keeps it and must
    /// point at our copy instead. It holds a full 64 bit pointer, so Vanilla is checked against the
    /// rebased chart address.</summary>
    internal readonly record struct Slot(long Addr, long Vanilla, ChartId Chart, string Label);

    /// <summary>A rip relative int32 in the all items chart's routine. <see cref="Off"/> is the
    /// distance from the chart's copy, 0 for four fields and -0x1E6 for the shared rsi base, so its reads
    /// at [rsi+rcx*2+0x1E6] and [rsi+rcx*2+0x1E8] still land in the copy. None has an immediate after it.
    /// Addr is written against the preferred base and moved through <see cref="ImageBase.At"/> when used,
    /// while Vanilla is a rip distance and stays put.</summary>
    internal readonly record struct RipField(long Addr, int Vanilla, int Off, string Label);

    /// <summary>The immediate of a six byte `cmp reg,imm32` that caps a menu list. The builder cap
    /// uses just the low byte (at most 0xFF) and the insert bound the whole imm32 (0x100). Vanilla and New
    /// are the same length, and that length is how many bytes Install and Restore write.</summary>
    internal readonly record struct CapSite(long Addr, byte[] Vanilla, byte[] New, string Label);

    /// <summary>One chart, with where the game keeps it, how many bytes that is, where our copy goes, and
    /// how many words <see cref="TemplateSync.Projection"/> keeps when it copies back
    /// (the old size with the marker, 141, 141 and 262).</summary>
    internal readonly record struct Chart(long OldBase, int SpanBytes, int PageOffset, int RegionBytes, int Capacity, string Label);

    /// <summary>Both inventory slots start at the same chart. A property so the addresses are
    /// read after the image base is known. The labels give each owning routine's RVA.</summary>
    internal static Slot[] Slots => new Slot[]
    {
        new(Offsets.InventoryOrderTableSlot, Offsets.InventoryOrderTemplate, ChartId.Inventory,
            "inventory order template slot A (housekeeper RVA 0x285F80)"),
        new(Offsets.InventoryOrderTableSlotB, Offsets.InventoryOrderTemplate, ChartId.Inventory,
            "inventory order template slot B (third housekeeper RVA 0x39684C)"),
        new(Offsets.PickerOrderTableSlot, Offsets.PickerOrderTemplate, ChartId.Picker,
            "picker order template slot (housekeeper RVA 0x28609F)"),
    };

    /// <summary>TemplateRelocationTests checks each Vanilla int32 by arithmetic. These addresses are
    /// against the preferred base, so a plain static array is fine.</summary>
    internal static readonly RipField[] RipFields =
    {
        new(0x14028617CL, 0x015EE3C0, -0x1E6, "lea rsi, the shared picker base (site RVA 0x286179)"),
        new(0x1402861A3L, 0x015EE57F, 0, "lea rcx (site RVA 0x2861A0)"),
        new(0x1402861F5L, 0x015EE52D, 0, "mov word ptr [rip+d],bx, 7 bytes, no trailing immediate (site RVA 0x2861F2)"),
        new(0x1402861FEL, 0x015EE524, 0, "lea rdx (site RVA 0x2861FB)"),
        new(0x1402862CEL, 0x015EE454, 0, "lea rcx feeding the rebuild call at RVA 0x2862D2 (type 8 by the disassembly; types 6/7 branch to RVA 0x286342) (site RVA 0x2862CB)"),
    };

    /// <summary>Both caps jump straight from vanilla to 255 and 256, which is safe only because ListBuilderHook cuts the two
    /// small stack callers, fnA and fnB, back to <see cref="ListBuilderHook.StackCallerCap"/> (149) before they read past it.</summary>
    /// <remarks>The insert bound stays one more than the cap so its walk reaches the terminator. The caps and the hook go in
    /// together, and TemplateRelocation.Restore puts both caps back if the hook cannot arm. A property because the Addrs are rebased pins.</remarks>
    internal static CapSite[] CapSites => new CapSite[]
    {
        new(Offsets.ListBuilderCapByte, new byte[] { 0x91 }, new byte[] { 0xFF },
            "list-builder entry cap (cmp esi,0x91 at RVA 0x288CC1, widened to 255)"),
        new(Offsets.ListInsertBoundByte, new byte[] { 0x92, 0x00, 0x00, 0x00 }, new byte[] { 0x00, 0x01, 0x00, 0x00 },
            "list-insert bound (cmp edx,0x92 at RVA 0x286318, widened to 256 = cap + 1)"),
    };

    /// <summary>In page order. A property because the addresses are rebased.</summary>
    internal static Chart[] Charts => new Chart[]
    {
        new(Offsets.InventoryOrderTemplate, Offsets.InventoryOrderTemplateWords * 2, InventoryPageOffset,
            RegionBytes, Offsets.InventoryOrderTemplateWords, "the inventory order template"),
        new(Offsets.PickerOrderTemplate, Offsets.PickerOrderTemplateWords * 2, PickerPageOffset,
            RegionBytes, Offsets.PickerOrderTemplateWords, "the equip-picker order template"),
        new(Offsets.PickerAllItemsTemplate, Offsets.PickerAllItemsTemplateWords * 2, AllItemsPageOffset,
            RegionBytes, Offsets.PickerAllItemsTemplateWords, "the picker all-items template"),
    };
}
