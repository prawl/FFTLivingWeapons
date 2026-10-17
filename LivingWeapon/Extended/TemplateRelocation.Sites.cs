namespace LivingWeapon;

/// <summary>
/// The ten-field map <see cref="TemplateRelocation.Install"/> rewrites (three pointer-table
/// slots, five rip-relative int32 fields, two menu-list-cap sites) plus the three chart descriptors
/// the lifecycle and <see cref="TemplateSync"/> read. Split from TemplateRelocation.cs the way
/// ListRelocation.Sites.cs is split from ListRelocation.cs; this file is the table, that one the lifecycle.
/// Findings 1, 4 and 5 of LW371_plan.md were re-derived with tools/probes/lw371_order_template_relocate.py --scan.
/// The caps went 159 -> 151 (GS cookie) -> 149 (fnA's two hand-item appends, see CapSites).
/// The cap values widen to 255/256, so <see cref="CapSite"/> holds byte arrays.
/// </summary>
internal sealed partial class TemplateRelocation
{
    /// <summary>Which chart a <see cref="Slot"/> or the page copies target. The all-items chart is
    /// never named by a slot (finding 4); only the five <see cref="RipField"/>s read it.</summary>
    internal enum ChartId { Inventory, Picker, AllItems }

    /// <summary>Byte offset in the granted page where each chart's copy lands (D2). Each chart gets 0x400 bytes.</summary>
    internal const int InventoryPageOffset = 0x000;
    internal const int PickerPageOffset = 0x400;
    internal const int AllItemsPageOffset = 0x800;
    /// <summary>Bytes granted to each chart's copy 512 words, being 510 ids, the marker, and one spare
    /// wall word TemplateSeat never touches (<see cref="PageRegionCapacityWords"/> covers ids plus marker, D2).</summary>
    internal const int RegionBytes = 0x400;
    /// <summary>Page size requested from the allocator. Three 0x400-byte regions plus 0x400 of headroom (D2).</summary>
    internal const int PageSize = 0x1000;
    /// <summary>Capacity handed to <see cref="TemplateSeat.Region"/> for a page chart (D2, v1.2). It counts the
    /// marker slot (<see cref="TemplateSeat.Plan"/> refuses at <c>marker + n &gt; capacity, 1</c>), so 511 means
    /// 510 ids plus the marker. Word 511 of the 512-word region stays outside that bound; Install's page-fill
    /// wall covers it with 0xFFFF.</summary>
    internal const int PageRegionCapacityWords = 511;

    private static int PageOffsetOf(ChartId chart) => chart switch
    {
        ChartId.Inventory => InventoryPageOffset,
        ChartId.Picker => PickerPageOffset,
        _ => AllItemsPageOffset,
    };

    /// <summary>One pointer-table qword that names an OLD chart base today (finding 1) and must be re-pointed
    /// at the page region. Addr and Vanilla are rebased <see cref="Offsets"/> properties; the slot holds a
    /// 64-bit pointer, so it is compared against the rebased template address.</summary>
    internal readonly record struct Slot(long Addr, long Vanilla, ChartId Chart, string Label);

    /// <summary>One rip-relative int32 field in the all-items chart's function (finding 4). <see cref="Off"/> is
    /// the offset from that chart's page target, 0 for four fields and -0x1E6 for the shared rsi base, whose reads
    /// at [rsi+rcx*2+0x1E6] and [rsi+rcx*2+0x1E8] must still land on the page copy. No site has a trailing
    /// immediate, so there is no Trail field. Addr is a preferred-base literal rebased through
    /// <see cref="ImageBase.At"/> at use (TemplateRelocation.Live); Vanilla is a rip displacement and
    /// does not relocate.</summary>
    internal readonly record struct RipField(long Addr, int Vanilla, int Off, string Label);

    /// <summary>One menu-list-cap site (finding 5, D5): the immediate of a six-byte `cmp reg,imm32`, either the
    /// low byte (builder cap, max 0xFF) or the full imm32 (insert bound, value 0x100). Vanilla and New
    /// have equal length, and that length is the byte count Install and Restore use. Addr is a rebased
    /// <see cref="Offsets"/> property.</summary>
    internal readonly record struct CapSite(long Addr, byte[] Vanilla, byte[] New, string Label);

    /// <summary>One chart. Its current base, the byte span of the game's copy, its page offset and region size,
    /// and the word capacity <see cref="TemplateSync.Projection"/> truncates the page side to (old block word
    /// count with marker, 141 / 141 / 262, D4). OldBase is a rebased <see cref="Offsets"/> property.</summary>
    internal readonly record struct Chart(long OldBase, int SpanBytes, int PageOffset, int RegionBytes, int Capacity, string Label);

    /// <summary>The three pointer-table slots (finding 1). Both inventory slots read the same vanilla base.
    /// A property so rebased pins are read after type-init. Labels give housekeeper RVAs (address
    /// minus 0x140000000).</summary>
    internal static Slot[] Slots => new Slot[]
    {
        new(Offsets.InventoryOrderTableSlot, Offsets.InventoryOrderTemplate, ChartId.Inventory,
            "inventory order template slot A (housekeeper RVA 0x285F80)"),
        new(Offsets.InventoryOrderTableSlotB, Offsets.InventoryOrderTemplate, ChartId.Inventory,
            "inventory order template slot B (third housekeeper RVA 0x39684C)"),
        new(Offsets.PickerOrderTableSlot, Offsets.PickerOrderTemplate, ChartId.Picker,
            "picker order template slot (housekeeper RVA 0x28609F)"),
    };

    /// <summary>The five all-items rip fields. Vanilla int32s are pinned by arithmetic in TemplateRelocationTests.
    /// Addrs are preferred-base literals, so a static array is safe; labels give site RVAs.</summary>
    internal static readonly RipField[] RipFields =
    {
        new(0x14028617CL, 0x015EE3C0, -0x1E6, "lea rsi, the shared picker base (site RVA 0x286179)"),
        new(0x1402861A3L, 0x015EE57F, 0, "lea rcx (site RVA 0x2861A0)"),
        new(0x1402861F5L, 0x015EE52D, 0, "mov word ptr [rip+d],bx, 7 bytes, no trailing immediate (site RVA 0x2861F2)"),
        new(0x1402861FEL, 0x015EE524, 0, "lea rdx (site RVA 0x2861FB)"),
        new(0x1402862CEL, 0x015EE454, 0, "lea rcx feeding the rebuild call at RVA 0x2862D2 (type 8 by the disassembly; types 6/7 branch to RVA 0x286342) (site RVA 0x2862CB)"),
    };

    /// <summary>The two menu-list caps. Both widen from vanilla straight to 255/256 (skipping the
    /// earlier 149/150 step), since ListBuilderHook truncates the stack callers fnA/fnB to
    /// <see cref="ListBuilderHook.StackCallerCap"/> (149) before they read a byte past it. The insert bound
    /// stays cap + 1 so its walk reaches the terminator. D8. This widening and the hook install are one
    /// transaction, so TemplateRelocation.Restore puts both sites back to vanilla if the hook cannot arm.
    /// A property because the Addrs are rebased pins.</summary>
    internal static CapSite[] CapSites => new CapSite[]
    {
        new(Offsets.ListBuilderCapByte, new byte[] { 0x91 }, new byte[] { 0xFF },
            "list-builder entry cap (cmp esi,0x91 at RVA 0x288CC1, widened to 255)"),
        new(Offsets.ListInsertBoundByte, new byte[] { 0x92, 0x00, 0x00, 0x00 }, new byte[] { 0x00, 0x01, 0x00, 0x00 },
            "list-insert bound (cmp edx,0x92 at RVA 0x286318, widened to 256 = cap + 1)"),
    };

    /// <summary>The three charts in page order (D2). A property because OldBases are rebased pins.</summary>
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
