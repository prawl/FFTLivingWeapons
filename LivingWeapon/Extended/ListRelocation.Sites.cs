namespace LivingWeapon;

/// <summary>
/// LW-368 round 2 / round 2b: the 55-entry map of every place in the game's plain code that names
/// the two per-item byte lists (the bag counts at <see cref="Offsets.BagCountArray"/> and the
/// sibling flag list at <see cref="Offsets.SiblingListArray"/>), plus the game-knowledge types
/// <see cref="ListRelocation.Install"/> needs to rewrite them. Split out of ListRelocation.cs the
/// way ExtendedSites.cs is split out of ExtendedInventory.cs: this file is the WHAT (the table),
/// ListRelocation.cs is the lifecycle (Install/Restore/the tripwire). Same partial class, so
/// ListRelocation.cs reaches every name here by plain name.
///
/// Every field is a 4-byte encoding of a distance: a `lea reg,[rip+disp32]` field measures from
/// the NEXT instruction (address + 4, no operand ever trails a `lea`), and an
/// `[idx + rXX + disp32]` field measures from the image base (every such site loads r8/rXX as
/// the image base's own `lea rXX,[rip-...]` first, so the field is really a signed RVA). Both
/// facts are why the field is one int32 rather than a raw pointer, and why relocating the lists
/// is nothing more than rewriting these 55 numbers to point at the new page instead.
///
/// Round 2b's ten additions are different in one way: a `mov [rip+disp], imm` field has an
/// immediate trailing the disp32 (<see cref="Site.Trail"/> counts those bytes, since the rip
/// distance measures from the true next instruction, past the immediate too), and each one
/// targets a byte somewhere INSIDE the list, not byte 0 (<see cref="Site.Off"/>).
///
/// Provenance: the first 45 rows are tools/probes/lw368_count_list_relocate_undo.json.done, the
/// record the live probe wrote back after the owner's five-point check (2026-08-31 02:40-02:55,
/// docs/LIVE_LEDGER.md row [item-count-lists-relocatable]); every (address, old bytes, kind,
/// list) quadruple among them is transcribed from that file, never hand-typed, and T2b pins the
/// transcription against the file itself so the two can never quietly drift apart. The last 10
/// rows are the new-game starting-inventory seed the owner's live pass of the round-2 build
/// caught the sweep missing (a new game would start with an empty bag): the routine at
/// 0x1402842E8 (read live 2026-08-31), the owner's give-all load left exactly these 20 store bytes
/// non-zero in the dead old block after round 2 shipped.
/// </summary>
internal sealed partial class ListRelocation
{
    /// <summary>How a field's 4 bytes resolve to an address: <see cref="RipLea"/> measures from
    /// the byte right after the field (a `lea` operand), <see cref="ImageRelative"/> measures
    /// from <see cref="Offsets.ModuleBase"/> (an RVA fed through a register the game already
    /// loaded with the image base).</summary>
    internal enum SiteKind { RipLea, ImageRelative }

    /// <summary>Which of the two lists a field points at.</summary>
    internal enum ListId { Count, Sibling }

    /// <summary>One field: its address, how to read/write it, which list it names, and the
    /// vanilla int32 it must read before this mod ever touches it (the refusal check's baseline;
    /// also the exact value <see cref="Restore"/> puts back). <see cref="Trail"/> and <see
    /// cref="Off"/> default to 0 for every field from the original 45-site sweep (a rip-lea
    /// field's disp32 has no operand trailing it, and every one of those fields names byte 0 of
    /// its list). The ten round-2b new-game-seed sites are the only ones with either nonzero:
    /// <see cref="Trail"/> is how many immediate-operand bytes follow the disp32 in a
    /// `mov [rip+disp], imm` instruction -- the rip distance must measure past them to the true
    /// next instruction, not just past the disp32 -- and <see cref="Off"/> is the byte offset
    /// INSIDE the list (not byte 0) the field's write actually lands on.</summary>
    internal readonly record struct Site(long Addr, SiteKind Kind, ListId List, int Vanilla, int Trail = 0, int Off = 0);

    /// <summary>The 55 fields: 34 <see cref="SiteKind.RipLea"/> + 21 <see
    /// cref="SiteKind.ImageRelative"/>, 47 <see cref="ListId.Count"/> + 8 <see
    /// cref="ListId.Sibling"/> (D6/T2). The first 45 (order matches the probe's own record) are
    /// every reader/writer the LW-368 round 2 sweep found; the last 10 are round 2b's new-game
    /// starting-inventory seed (T2b: absent from the probe record, each with <see cref="Site.Off"/>
    /// &gt; 0).</summary>
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

        // Round 2b (LW-368): the new-game starting-inventory seed, a run of `mov [rip+disp], imm`
        // stores at the routine that begins 0x1402842E8 (read live 2026-08-31). This is a
        // DIFFERENT routine from the per-item state initialiser above it (Offsets.FnInventoryReset
        // at 0x140284500, the loop that clears the list to its widened bound) -- this one writes
        // fixed default counts into fixed slots, so unlike every other site it targets bytes deep
        // inside the list, not byte 0.
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

    /// <summary>Bytes per list copied onto the new page: 0x105 "list proper" entries (<see
    /// cref="ListProperBytes"/>) plus an 0xB-byte tail that is NOT padding -- a live game state
    /// dword sits at +0x108 of both the count and sibling block (corrected 2026-08-31, LW-368
    /// round 2b; ExtendedSites' per-item state initialiser note carried the old, wrong "padding"
    /// claim and is fixed in the same commit). The copy still takes the full 0x110 bytes so that
    /// dword rides along unmolested on the new page; only the tripwire narrows its window.</summary>
    internal const int BlockBytes = 0x110;

    /// <summary>Bytes the hidden-writer tripwire actually compares (see
    /// ListRelocation.CheckOldBlocks): the list proper only, never the 0xB-byte tail holding the
    /// live +0x108 state dword -- comparing the full <see cref="BlockBytes"/> would false-fire on
    /// that dword's own ordinary churn.</summary>
    internal const int ListProperBytes = 0x105;

    /// <summary>Byte offset of the sibling list's copy inside the new page, relative to the count
    /// list's copy at +0 (D2: a 0x1000-byte page, count at +0x000, sibling at +0x400).</summary>
    internal const int SiblingPageOffset = 0x400;

    /// <summary>The page size requested from the allocator (D2: 0x800 used this round, the rest
    /// reserved for a possible future third list).</summary>
    internal const int PageSize = 0x1000;
}
