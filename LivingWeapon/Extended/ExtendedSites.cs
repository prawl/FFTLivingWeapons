using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// The game knowledge behind the extended inventory; the lifecycle is in ExtendedInventory.cs.
/// Every BootSites entry except the two reserved-list ones is a one-byte id cap the 1.5.2 build
/// hardcodes as 261 (0x105), widened by the extended-id count N. Provenance is the FFTHandsFree
/// boot-arm marker v2 (tools/probes/lw346_capbreak_bootarm.marker.v2.txt, observed 2026-08-26/27)
/// plus the rig's CapBreakEquipHook/CapBreakDisplayHook constants; old bytes re-read from the exe 2026-08-27.
/// Every <see cref="Site.Addr"/> is PIN space (preferred base 0x140000000); <see cref="Build"/> rebases it
/// through <see cref="ImageBase.At"/> so each <see cref="BytePatch.Addr"/> is live.
/// </summary>
internal static class ExtendedSites
{
    /// <summary>How many extended items can exist at once. PlusN sites add N to a plain immediate or
    /// displacement byte, and eight are `lea reg,[base+6]` with a signed disp8, so
    /// 6 + 122 = 0x80 flips negative and corrupts the bound. 121 is the reviewer's decode of every PlusN
    /// site; <see cref="ExtendedInventoryData"/> and tools/generate.py refuse a larger table.
    /// The count getter widens its high byte instead (0x103 -&gt; 0x203, ids up to 515). The damage-staging
    /// cap's old `xor r15d,0x5E` was Denuvo obfuscation; the Denuvo-free build has plain `lea r14d,[rbp+6]`.</summary>
    public const int MaxExtendedCount = 121;

    private enum Widen { PlusN, HighByte, ShopLoop }

    /// <summary>One single-byte patch site. Addr is PIN space (see the class summary).</summary>
    private readonly record struct Site(long Addr, byte Old, Widen Kind, string Label);

    /// <summary>Boot-safe sites. Plain code pages, patched in ExtendedInventory.BootArm before the
    /// game runs.</summary>
    private static readonly Site[] BootSites =
    {
        // Imm32 of `mov r8d,0x103` at 0x1402847FD (bytes 0x1402847FF..0x140284802); this entry is its second byte.
        // Do not add a PlusN on 0x1402847FF, that would widen the immediate twice (bit on 2026-08-30).
        // The game read 41 b8 03 02 00 00 here (read-only RPM, N=2), so bag accessor 0x1411A7C00[id] admits 1..516.
        // ExtendedSitesTests pins this.
        new(0x140284800L, 0x01, Widen.HighByte, "count-getter cap (mov r8d,0x103 -> 0x203)"),
        // Found 2026-08-30, the one of nine imm32 0x105 bounds in the 0x140280000-0x140290000
        // builder family the first pass missed. 0x140284553 `mov edx,0x105` is reused four times by the per-item
        // state initialiser. It clears the byte arrays at 0x1411A7C00 and 0x1411A7700 and seeds the u16 array at 0x1411A7810.
        // Corrected 2026-08-31. The widened tail overlaps a game state dword at +0x108 of each
        // 0x110-byte slot (0x1411A7D08 and 0x1411A7808, read 2026-08-31), from id 264 (the dword spans 264-267).
        // The list relocation (ListRelocation.cs) removes the overlap; the u16 tail is re-zeroed by the memset at 0x1402845E0.
        new(0x140284554L, 0x05, Widen.PlusN, "per-item state initialiser bound (mov edx,0x105)"),
        new(0x140284724L, 0x05, Widen.PlusN, "display iteration cap 1"),
        new(0x1402847C9L, 0x05, Widen.PlusN, "display iteration cap 2"),
        new(0x140288CDAL, 0x05, Widen.PlusN, "display iteration cap 3"),
        new(0x140289074L, 0x05, Widen.PlusN, "display iteration cap 4"),
        new(0x140284C0AL, 0x06, Widen.PlusN, "equip clamp (lea ecx,[rdx+6])"),
        new(0x140397121L, 0x05, Widen.PlusN, "sorted-list loop bound (cmp edi,0x105)"),
        new(0x140287570L, 0x05, Widen.PlusN, "shop-path sibling getter cap"),
        new(0x140285E2DL, 0x05, Widen.PlusN, "default-order table-scan guard"),
        new(0x140286187L, 0x06, Widen.PlusN, "acquired-list maintainer walk (lea edi,[rbp+6])"),
        new(0x1402862F7L, 0x05, Widen.PlusN, "acquired-sort move-to-front scan guard"),
        new(0x140285EE7L, 0x06, Widen.PlusN, "delete-from-list scan guard (lea r10d,[r11+6])"),
        new(0x14030CED3L, 0x05, Widen.PlusN, "return-to-bag cap"),
        new(0x140101071L, 0x04, Widen.PlusN, "adjust-count cap (mov eax,0x104)"),
        new(0x1403602F4L, 0x05, Widen.PlusN, "roster-to-battle converter cap"),
        new(0x140226FDFL, 0x06, Widen.PlusN, "hand resolver copy 1 (lea ecx,[rdx+6])"),
        new(0x14028024FL, 0x05, Widen.PlusN, "hand resolver copy 2 (mov r8d,0x105)"),
        new(0x1401ED95BL, 0x04, Widen.PlusN, "attacker validity inline check, right hand (cmp ax,4)"),
        new(0x1401ED982L, 0x04, Widen.PlusN, "attacker validity inline check, left hand (cmp ax,4)"),
        // The shop BUY-list builder walks ids 0..0xFF (cmp ebx,0x100 at
        // 0x140288FD9; this is the imm32's low byte), widened to 0x105 + N. Town flags come from the ShopFlagsMirror.
        new(0x140288FDBL, 0x00, Widen.ShopLoop, "shop buy-list loop bound (cmp ebx,0x100)"),
        // Found 2026-08-30. These two are a reserved-item equality list, not a cap. The per-job
        // can-equip check 0x1402886D0 and its inlined copy at 0x140396F1C compare the item id against five hardcoded
        // ids before reading the item's job class (1.5.2 exe on disk):
        //   140288709  81 ea 06 01 00 00  sub edx, 0x106   ; id == 262 ?
        //   14028870F  74 48              je special
        //   140288711  83 ea 1a           sub edx, 0x1a    ; == 288 ?
        //   140288716  83 ea 05           sub edx, 5       ; == 293 ?
        //   14028871B  83 ea 08           sub edx, 8       ; == 301 ?
        //   140288720  83 fa 09 / 74 34   cmp edx, 9 ; je  ; == 310 ?
        //   (0x140396F52  81 e9 06 01 00 00  sub ecx, 0x106, then the same four steps)
        // The special path admits only jobs 0xA1 and 0xA4 (unnamed rows in job.en) and answers -1 for every other job.
        // Vanilla never reaches it (ItemData ends at id 260), but the second extended id is 262, so that item was refused
        // everywhere; the 261/262 swap test showed the refusal following the id.
        // Fix. The imm32's second byte (address +3) goes 0x01 -> 0x02, moving the list {262, 288, 293, 301, 310} to
        // {518, 544, 549, 557, 566}, past EXTENDED_LAST_ID (511, tools/generate.py) and inside the 0x3FF id mask.
        // A relocation, so the byte is 0x02 for any N.
        new(0x14028870CL, 0x01, Widen.HighByte, "reserved-item equality list, can-equip check (sub edx,0x106 -> 0x206)"),
        new(0x140396F55L, 0x01, Widen.HighByte, "reserved-item equality list, inlined twin (sub ecx,0x106 -> 0x206)"),
        // Found 2026-08-31 (1.5.2 exe on disk). The owned-item template maintainer 0x140285F80
        // (inventory order tables; its second half 0x140286070 serves the picker tables) walks a template before
        // inserting a newly owned id at the front. The walk stops at the 0x00FF marker or the first word >= r14w,
        // where r14d comes from `45 8d 77 06` lea r14d,[r15+6] with r15d = 0xFF (0x105).
        // Three copies 0x140285F80, 0x140286070, and 0x14039684C (the same five inventory tables via
        // pointer table 0x140689C38, owned-ness from the count mirror at 0x143C52740). Earlier sweeps missed them
        // because the bound sits in an r14/r15 pair rather than the lea+6 forms swept before.
        // Unwidened, an extended id ends the walk early. The insert shift starts at the stop word, so that id crawls
        // right, overwrites its neighbor, and eventually the end marker, and ids past the stop word are not found so
        // they get re-inserted (the Sort at 2026-08-31 00:11 showed five doubled shields and five vanished designs,
        // reproduced by emulating the sequence). Each address is the lea's disp8 byte (instruction +3).
        // Templates already damaged are healed by TemplateSeat's repair.
        new(0x140285FB5L, 0x06, Widen.PlusN, "owned-item template maintainer walk, inventory tables (lea r14d,[r15+6])"),
        new(0x1402860AEL, 0x06, Widen.PlusN, "owned-item template maintainer walk, picker tables (lea r14d,[r15+6])"),
        new(0x140396881L, 0x06, Widen.PlusN, "owned-item template maintainer walk, third copy (function RVA 0x39684C) over the inventory tables (lea r14d,[r15+6])"),
        // Found 2026-08-31 (1.5.2 exe on disk, tools/probes/lw365_hand_resolver_scan.py). The last nine one-byte
        // bounds in the "what is this unit holding" routines. Six are more hand-resolver copies (`mov edx,0xff`,
        // `lea ecx,[rdx+6]`, disp byte = lea +2, guarding `cmp ax,dx / je skip` for an empty hand and `cmp cx,ax / ja valid`
        // for id < 0x105): copies 3, 4, 6 guard word [rbx+0x20] (fallback [rbx+0x22]), 5 and 7 read a rip-relative
        // global hand word, and 8 uses `45 8d 41 06` (lea r8d,[r9+6]).
        // The other three are equip-slot switch guards in the function at ~0x140306655 (UI code to equipped-item word
        // plus slot flag byte at [r8+0x1B]): `mov r10d,0xff`, `lea eax,[r10+6]` (41 8d 42 06; disp byte = lea +3),
        // guarding `cmp ax,[r9+slot] / jbe skip` for words +0x1E/+0x1C/+0x1A (flags 0x20/0x40/0x80).
        // Investigated and not patched:
        // 0x1402C8155 (the u16 walker's `lea r12d,[rax+6]`) is walled. Its 0x105 bound guards a stack buffer. The
        // function memsets a 0x414-byte (0x105 dwords) frame array at 0x1402C8119, accumulates into it by item id, and
        // bounds the final scan at 0x1402C81DB with the same register, so widening would write past the array (the
        // stack-cookie lesson). The branch also needs callee 0x140278fec(id) >= 0x64, so extended ids only skip
        // a pass the player never sees.
        // 0x1402BD992 (bytes 44 8d 4e 06; opcode byte 0x1402BD993, disp8 byte 0x1402BD995) after `mov r11d,0xff` is a
        // false positive. Rsi is 1 there (mov esi,1 at 0x1402BD974, kept across the call), so the lea gives 7, the
        // equip-slot word count for a `rep stosw` fill of 0x00FF markers, and the routine already masks hand ids with
        // 0x3FF. Neither byte is patched; sweeps name either one, so both are pinned absent.
        // 0x1403066BE (`mov eax,0x105`, same switch) is recorded, not widened. It serves word slots [r9+0x22]/[r9+0x26]
        // (flags 8/2), not hand slots, and extended kinds are weapons-only (ExtendedWeaponCeilingTests). Revisit if
        // non-weapon extended kinds ship.
        new(0x1402C4EB7L, 0x06, Widen.PlusN, "hand resolver copy 3 (guards word [rbx+0x20]; fallback read [rbx+0x22])"),
        new(0x1402DE6FFL, 0x06, Widen.PlusN, "hand resolver copy 4 (guards word [rbx+0x20], second copy)"),
        new(0x1402FE436L, 0x06, Widen.PlusN, "hand resolver copy 5 (reads a rip-relative global hand word)"),
        new(0x14033B5BBL, 0x06, Widen.PlusN, "hand resolver copy 6 (guards word [rbx+0x20], third copy)"),
        new(0x140378640L, 0x06, Widen.PlusN, "hand resolver copy 7 (reads a rip-relative global hand word, second copy)"),
        new(0x14039636BL, 0x06, Widen.PlusN, "hand resolver copy 8 (lea r8d,[r9+6]; variant encoding 45 8d 41 06)"),
        new(0x1403066A7L, 0x06, Widen.PlusN, "equip-slot switch guard, word +0x1E (lea eax,[r10+6])"),
        new(0x14030671DL, 0x06, Widen.PlusN, "equip-slot switch guard, word +0x1C (lea eax,[r10+6])"),
        new(0x1403067ABL, 0x06, Widen.PlusN, "equip-slot switch guard, word +0x1A (lea eax,[r10+6])"),
        // Read offline 2026-10-16 from the Denuvo-free 1.5.2 exe (Steam buildid 24304444). The two damage
        // caps once in the copy-protected tail are plain .text on this build, so they join the boot set.
        // First. The eighth hand-resolver copy (`ba ff 00 00 00` mov edx,0xff, then `8d 4a 06` lea ecx,[rdx+6]),
        // followed within 0x30 bytes by two calls to Offsets.ThunkWeaponStat, matching the journal's "right before two
        // weapon-stat thunk calls".
        // Second. The swing publisher with plain entry RVA 0x282234: `bd ff 00 00 00` mov ebp,0xff at RVA 0x282287,
        // `44 8d 75 06` lea r14d,[rbp+6] at RVA 0x2822A1. It stores combat +0x20/+0x24 to the swing words at RVA
        // 0x7B0764/0x7B0766 and substitutes the off-hand word for ids at or above the bound (`66 44 3b f0` cmp r14w,ax /
        // `77 14` ja). The old `xor r15d,0x5E` with 0x58 was the protector's obfuscation of the same 6 (0x58 ^ 0x5E = 6).
        new(0x14027F002L, 0x06, Widen.PlusN, "damage-path hand resolver (lea ecx,[rdx+6]); plain-text copy of the former protected-tail site, two weapon-stat thunk calls follow"),
        new(0x1402822A4L, 0x06, Widen.PlusN, "damage-staging cap (lea r14d,[rbp+6] after mov ebp,0xff) in the swing publisher; plain copy of the former xor r15d,0x5E site; lea bytes are 44 8d 75 06 at image offsets A1..A4, the disp8 is A4 (confirmed against the exe on disk 2026-10-16)"),
    };

    /// <summary>Copy-protected sites, stepped from the tick loop as <see cref="PendingPatch"/>es once a save has
    /// loaded. Empty on the Denuvo-free build (no protected tail; its two former entries are boot
    /// sites above). The empty array and the pending machinery are kept in case a late-mapped page appears.</summary>
    private static readonly Site[] PostLoadSites = { };

    public static List<BytePatch> BootPatches(int extendedCount) => Build(BootSites, extendedCount);
    public static List<BytePatch> PostLoadPatches(int extendedCount) => Build(PostLoadSites, extendedCount);

    private static List<BytePatch> Build(Site[] sites, int n)
    {
        var list = new List<BytePatch>(sites.Length);
        // Site.Addr is PIN space; the patch carries the live address.
        foreach (var s in sites) list.Add(new BytePatch(ImageBase.At(s.Addr), s.Old, NewByte(s.Old, s.Kind, n), s.Label));
        return list;
    }

    private static byte NewByte(byte old, Widen kind, int n) => kind switch
    {
        Widen.PlusN => (byte)(old + n),
        Widen.HighByte => 0x02,
        Widen.ShopLoop => (byte)(0x05 + n),   // 0x100 -> 0x105 + N (the imm32's high byte stays 0x01)
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown widening kind"),
    };
}
