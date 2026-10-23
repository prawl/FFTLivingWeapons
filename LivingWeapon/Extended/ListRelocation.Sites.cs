namespace LivingWeapon;

/// <summary>
/// Every spot in the game's code that finds the two per item byte lists, so they can all be pointed at our copy.
/// The lists are <see cref="Offsets.BagCountArray"/> and <see cref="Offsets.SiblingListArray"/>, and
/// <see cref="ListRelocation.Install"/> rewrites each field. Every field is a 4 byte distance. A rip `lea` measures
/// from the next instruction and an `[idx + rXX + disp32]` field is an offset from the image base. The ten
/// `mov [rip+disp], imm` sites have an immediate after the field (<see cref="Site.Trail"/>) and hit a byte inside the
/// list (<see cref="Site.Off"/>). The first 45 rows come from tools/probes/lw368_count_list_relocate_undo.json.done,
/// checked live by the owner [ledger item-count-lists-relocatable] and pinned by T2b.
/// The last 10 are the New Game starting items in the routine at 0x1402842E8, read live.
/// </summary>
internal sealed partial class ListRelocation
{
    /// <summary>What a field's distance is measured from, the byte after the field and any immediate trailing it for
    /// <see cref="RipLea"/>, and <see cref="ImageBase.Base"/> for <see cref="ImageRelative"/>.</summary>
    internal enum SiteKind { RipLea, ImageRelative }

    /// <summary>Which of the two lists a field points at.</summary>
    internal enum ListId { Count, Sibling }

    /// <summary>One field and the vanilla int32 it must hold before we touch it, which is also what
    /// <see cref="Restore"/> writes back.</summary>
    /// <remarks><see cref="Trail"/> counts the immediate bytes after the field and <see cref="Off"/> is the byte inside the
    /// list, both 0 except for the ten New Game sites. Addr is against the preferred base, moved through <see cref="ImageBase.At"/> each time.</remarks>
    internal readonly record struct Site(long Addr, SiteKind Kind, ListId List, int Vanilla, int Trail = 0, int Off = 0);

    /// <summary>34 <see cref="SiteKind.RipLea"/> and 21 <see cref="SiteKind.ImageRelative"/>, or 47 <see cref="ListId.Count"/> and
    /// 8 <see cref="ListId.Sibling"/>. The first 45 follow the probe record, the last 10 are not in it and each has <see cref="Site.Off"/> &gt; 0.</summary>
    internal static readonly Site[] Sites =
    {
        new(0x140101001L, SiteKind.RipLea, ListId.Count, 17460219),   // lea rcx, [rip + 0x10a6bfb]
        new(0x14010108FL, SiteKind.RipLea, ListId.Count, 17460077),   // lea r8, [rip + 0x10a6b6d]
        new(0x140154166L, SiteKind.RipLea, ListId.Count, 17119894),   // lea rcx, [rip + 0x1053a96]
        new(0x140206656L, SiteKind.RipLea, ListId.Count, 16389542),   // lea r8, [rip + 0xfa15a6]
        new(0x140219268L, SiteKind.RipLea, ListId.Count, 16312724),   // lea rdx, [rip + 0xf8e994]
        new(0x1402192C0L, SiteKind.RipLea, ListId.Sibling, 16311356),   // lea rdx, [rip + 0xf8e43c]
        new(0x14021B1D1L, SiteKind.RipLea, ListId.Count, 16304683),   // lea rdx, [rip + 0xf8ca2b]
        new(0x14021B232L, SiteKind.RipLea, ListId.Sibling, 16303306),   // lea rdx, [rip + 0xf8c4ca]
        new(0x14021E1C0L, SiteKind.RipLea, ListId.Count, 16292412),   // lea rdx, [rip + 0xf89a3c]
        new(0x140236BABL, SiteKind.ImageRelative, ListId.Count, 18512896),   // movzx eax, byte ptr [rax + rdx + 0x11a7c00]
        new(0x140238ECCL, SiteKind.ImageRelative, ListId.Count, 18512896),   // add byte ptr [rdi + r8 + 0x11a7c00], r13b
        new(0x14023D5A9L, SiteKind.ImageRelative, ListId.Count, 18512896),   // mov al, byte ptr [rcx + rdi + 0x11a7c00]
        new(0x14023D5B7L, SiteKind.ImageRelative, ListId.Count, 18512896),   // mov byte ptr [rcx + rdi + 0x11a7c00], al
        new(0x140279004L, SiteKind.ImageRelative, ListId.Count, 18512896),   // movzx edx, byte ptr [r11 + rbx + 0x11a7c00]
        new(0x140279CA3L, SiteKind.RipLea, ListId.Count, 15916889),   // lea rcx, [rip + 0xf2df59]
        new(0x140279D08L, SiteKind.RipLea, ListId.Sibling, 15915508),   // lea rdx, [rip + 0xf2d9f4]
        new(0x140279E9DL, SiteKind.RipLea, ListId.Count, 15916383),   // lea rcx, [rip + 0xf2dd5f]
        new(0x140279F02L, SiteKind.RipLea, ListId.Sibling, 15915002),   // lea rdx, [rip + 0xf2d7fa]
        new(0x140281696L, SiteKind.ImageRelative, ListId.Count, 18512896),   // mov al, byte ptr [r13 + r8 + 0x11a7c00]
        new(0x1402816B8L, SiteKind.ImageRelative, ListId.Count, 18512896),   // mov byte ptr [r13 + r8 + 0x11a7c00], cl
        new(0x14028175AL, SiteKind.ImageRelative, ListId.Count, 18512896),   // mov al, byte ptr [r12 + r8 + 0x11a7c00]
        new(0x140281770L, SiteKind.ImageRelative, ListId.Count, 18512896),   // mov byte ptr [r12 + r8 + 0x11a7c00], al
        new(0x1402817B9L, SiteKind.ImageRelative, ListId.Count, 18512896),   // mov al, byte ptr [r8 + r9 + 0x11a7c00]
        new(0x1402817CFL, SiteKind.ImageRelative, ListId.Count, 18512896),   // mov byte ptr [r8 + r9 + 0x11a7c00], al
        new(0x14028304EL, SiteKind.ImageRelative, ListId.Count, 18512896),   // cmp byte ptr [rdx + r8 + 0x11a7c00], r15b
        new(0x140284565L, SiteKind.ImageRelative, ListId.Count, 18512896),   // mov byte ptr [rax + r8 + 0x11a7c00], 0
        new(0x140284583L, SiteKind.ImageRelative, ListId.Sibling, 18511616),   // mov byte ptr [rax + r8 + 0x11a7700], 0
        new(0x140284716L, SiteKind.ImageRelative, ListId.Count, 18512896),   // movzx ecx, byte ptr [rax + r14 + 0x11a7c00]
        new(0x140284735L, SiteKind.ImageRelative, ListId.Count, 18512896),   // mov byte ptr [rax + r14 + 0x11a7c00], 0
        new(0x1402847BEL, SiteKind.ImageRelative, ListId.Count, 18512896),   // mov byte ptr [rax + r14 + 0x11a7c00], cl
        new(0x140284816L, SiteKind.RipLea, ListId.Count, 15872998),   // lea r9, [rip + 0xf233e6]
        new(0x140287582L, SiteKind.RipLea, ListId.Sibling, 15860090),   // lea r9, [rip + 0xf2017a]
        new(0x1402C62EEL, SiteKind.ImageRelative, ListId.Count, 18512896),   // inc byte ptr [rbx + r12 + 0x11a7c00]
        new(0x1402CA088L, SiteKind.ImageRelative, ListId.Count, 18512896),   // inc byte ptr [rax + r15 + 0x11a7c00]
        new(0x1402CF226L, SiteKind.RipLea, ListId.Count, 15567318),   // lea rdx, [rip + 0xed89d6]
        new(0x1402CF27EL, SiteKind.RipLea, ListId.Sibling, 15565950),   // lea rdx, [rip + 0xed847e]
        new(0x1402CFB0AL, SiteKind.RipLea, ListId.Count, 15565042),   // lea rcx, [rip + 0xed80f2]
        new(0x1402CFB76L, SiteKind.RipLea, ListId.Sibling, 15563654),   // lea rcx, [rip + 0xed7b86]
        new(0x1402D66B0L, SiteKind.RipLea, ListId.Count, 15537484),   // lea rdx, [rip + 0xed154c]
        new(0x1402EA9ABL, SiteKind.ImageRelative, ListId.Count, 18512896),   // movzx eax, byte ptr [rax + rdx + 0x11a7c00]
        new(0x14030CF1DL, SiteKind.RipLea, ListId.Count, 15314143),   // lea rcx, [rip + 0xe9acdf]
        new(0x14030E8EEL, SiteKind.ImageRelative, ListId.Count, 18512896),   // cmp byte ptr [rax + r13 + 0x11a7c00], bl
        new(0x14031FF45L, SiteKind.RipLea, ListId.Count, 15236279),   // lea r13, [rip + 0xe87cb7]
        new(0x14032026DL, SiteKind.RipLea, ListId.Count, 15235471),   // lea rcx, [rip + 0xe8798f]
        new(0x140396190L, SiteKind.RipLea, ListId.Count, 14752364),   // lea rdx, [rip + 0xe11a6c]

        // New Game's starting items, written by the routine at 0x1402842E8, read live. It stores
        // fixed counts into slots deep inside the list, unlike Offsets.FnInventoryReset at 0x140284500,
        // which clears it, so a search for the list's start never found these.
        new(0x1402842EAL, SiteKind.RipLea, ListId.Count, 0x00F239FE, Trail: 4, Off: 0xF0),   // mov dword ptr [rip+disp], 0x01010205 (ids 240-243)
        new(0x1402842F4L, SiteKind.RipLea, ListId.Count, 0x00F239F8, Trail: 4, Off: 0xF4),   // mov dword ptr [rip+disp], 0x02010101 (ids 244-247)
        new(0x1402842FEL, SiteKind.RipLea, ListId.Count, 0x00F239F2, Trail: 4, Off: 0xF8),   // mov dword ptr [rip+disp], 0x01010101 (ids 248-251)
        new(0x140284309L, SiteKind.RipLea, ListId.Count, 0x00F239ED, Trail: 2, Off: 0xFC),   // mov word ptr [rip+disp], 0x0201 (ids 252-253)
        new(0x140284311L, SiteKind.RipLea, ListId.Count, 0x00F23937, Trail: 1, Off: 0x4D),   // mov byte ptr [rip+disp], 1 (id 77)
        new(0x140284318L, SiteKind.RipLea, ListId.Count, 0x00F23963, Trail: 1, Off: 0x80),   // mov byte ptr [rip+disp], 1 (id 128)
        new(0x14028431FL, SiteKind.RipLea, ListId.Count, 0x00F2396C, Trail: 1, Off: 0x90),   // mov byte ptr [rip+disp], 1 (id 144)
        new(0x140284326L, SiteKind.RipLea, ListId.Count, 0x00F23981, Trail: 1, Off: 0xAC),   // mov byte ptr [rip+disp], 1 (id 172)
        new(0x14028432DL, SiteKind.RipLea, ListId.Count, 0x00F23901, Trail: 1, Off: 0x33),   // mov byte ptr [rip+disp], 1 (id 51)
        new(0x140284334L, SiteKind.RipLea, ListId.Count, 0x00F23902, Trail: 1, Off: 0x3B),   // mov byte ptr [rip+disp], 1 (id 59)
    };

    /// <summary>The 0x105 byte list (<see cref="ListProperBytes"/>) and an 0xB byte tail. The tail is not padding,
    /// it holds a game dword at +0x108, seen live, so the whole 0x110 is copied to take it along.</summary>
    internal const int BlockBytes = 0x110;

    /// <summary>What ListRelocation.CheckOldBlocks compares. The dword at +0x108 changes on its own and would
    /// raise a false alarm, so only the list itself counts.</summary>
    internal const int ListProperBytes = 0x105;

    /// <summary>The counts sit at +0x000 of our page and the second list at +0x400.</summary>
    internal const int SiblingPageOffset = 0x400;

    /// <summary>Page size asked of the allocator. The two 0x110 byte copies at 0 and 0x400 use 0x510, and the rest leaves room for a third list.</summary>
    internal const int PageSize = 0x1000;
}
