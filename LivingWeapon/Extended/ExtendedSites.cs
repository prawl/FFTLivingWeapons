using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Every spot in the game's code that gives up at item id 261, and the one byte that moves it.
/// Most of them hardcode 261 (0x105) on the 1.5.2 build and move up by N, the number of new items.
/// The addresses come from the FFTHandsFree boot arm marker v2 (tools/probes/lw346_capbreak_bootarm.marker.v2.txt)
/// and that rig's CapBreakEquipHook and CapBreakDisplayHook constants, with the old bytes read again from the exe
/// [journal 2026-08-27].
/// Addresses are written against the preferred base 0x140000000 and <see cref="Build"/> moves them
/// to wherever the game actually loaded.
/// </summary>
internal static class ExtendedSites
{
    /// <summary>How many new items fit. 121 is the largest count every site survives, and
    /// <see cref="ExtendedInventoryData"/> and tools/generate.py refuse a bigger table.</summary>
    /// <remarks>Many of the sites are `lea reg,[base+6]` with a signed one byte offset, so 6 + 122 = 0x80 turns
    /// negative and wrecks the bound. The count getter moves its high byte instead (0x103 to 0x203, ids up to 515).
    /// The damage staging cap used to hide behind `xor r15d,0x5E`, Denuvo scrambling a plain `lea r14d,[rbp+6]`.</remarks>
    public const int MaxExtendedCount = 121;

    private enum Widen { PlusN, HighByte, ShopLoop }

    /// <summary>One byte to patch, at an address against the preferred base.</summary>
    private readonly record struct Site(long Addr, byte Old, Widen Kind, string Label);

    /// <summary>Plain code that is safe to patch at boot, before the game runs.</summary>
    private static readonly Site[] BootSites =
    {
        // This is the second byte of `mov r8d,0x103` at 0x1402847FD (immediate at 0x1402847FF..0x140284802).
        // A PlusN on 0x1402847FF as well would move the same number twice, which bit us once.
        // Read back live with N=2 as 41 b8 03 02 00 00, so the bag accessor 0x1411A7C00[id] takes 1..516.
        // ExtendedSitesTests pins it.
        new(0x140284800L, 0x01, Widen.HighByte, "count-getter cap (mov r8d,0x103 -> 0x203)"),
        // The routine that resets every item's state stops at 261 as well.
        // Its `mov edx,0x105` at 0x140284553 is used four times, to clear the byte arrays at
        // 0x1411A7C00 and 0x1411A7700 and to seed the u16 array at 0x1411A7810.
        // Widened, the clear runs into a game dword at +0x108 of each 0x110 byte slot
        // (0x1411A7D08 and 0x1411A7808), ids 264 to 267 and up.
        // ListRelocation.cs moves the lists out of the way, and the memset at 0x1402845E0 re-zeroes the u16 tail.
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
        // Shops only list ids below 0x100 (cmp ebx,0x100 at 0x140288FD9, this is its low byte),
        // so the loop runs to 0x105 + N. Which towns sell what comes from ShopFlagsMirror.
        new(0x140288FDBL, 0x00, Widen.ShopLoop, "shop buy-list loop bound (cmp ebx,0x100)"),
        // These two are why id 262 could not be equipped by anyone. They check for
        // a short list of special ids, not a cap. The can-equip check 0x1402886D0 and its inlined copy
        // at 0x140396F1C test the id against five hardcoded values before reading its job class (1.5.2 exe on disk).
        //   140288709  81 ea 06 01 00 00  sub edx, 0x106   ; id == 262 ?
        //   14028870F  74 48              je special
        //   140288711  83 ea 1a           sub edx, 0x1a    ; == 288 ?
        //   140288716  83 ea 05           sub edx, 5       ; == 293 ?
        //   14028871B  83 ea 08           sub edx, 8       ; == 301 ?
        //   140288720  83 fa 09 / 74 34   cmp edx, 9 ; je  ; == 310 ?
        //   (0x140396F52  81 e9 06 01 00 00  sub ecx, 0x106, then the same four steps)
        // The special path lets in only jobs 0xA1 and 0xA4 (unnamed rows in job.en) and says -1 to the rest.
        // The base game never gets there because its items end at 260. Our second item is 262, so it was
        // refused everywhere, and swapping 261 and 262 made the refusal follow the id.
        // Bumping the second byte (address +3) from 0x01 to 0x02 moves the whole list {262, 288, 293, 301, 310}
        // to {518, 544, 549, 557, 566}, past the last id we hand out (511, tools/generate.py) and still under the 0x3FF mask.
        // It moves the list rather than widening a cap, so the byte is 0x02 whatever N is.
        new(0x14028870CL, 0x01, Widen.HighByte, "reserved-item equality list, can-equip check (sub edx,0x106 -> 0x206)"),
        new(0x140396F55L, 0x01, Widen.HighByte, "reserved-item equality list, inlined twin (sub ecx,0x106 -> 0x206)"),
        // Without these a Sort doubles some items and loses others (1.5.2 exe on disk).
        // When the player gets a new item the game walks the menu order chart to find where it goes.
        // The walk stops at the 0x00FF marker or at the first word >= r14w, and r14d comes from
        // `45 8d 77 06` lea r14d,[r15+6] with r15d = 0xFF, which makes 0x105.
        // There are three copies, 0x140285F80, 0x140286070 (the picker charts) and 0x14039684C, the last reaching the
        // same five inventory charts through the pointer table 0x140689C38 with ownership from the count mirror 0x143C52740.
        // The bound lives in an r14 and r15 pair, so a search for the lea+6 shape alone never finds them.
        // Left alone, a new item stops the walk early and the insert shift starts there, so the id creeps right
        // over its neighbor and finally the end marker, and every id past it goes missing and is added again
        // (one Sort showed five doubled shields and five vanished designs, and an emulation repeated it).
        // Each address is the lea's offset byte (instruction +3).
        // TemplateSeat's repair heals charts that were already damaged.
        new(0x140285FB5L, 0x06, Widen.PlusN, "owned-item template maintainer walk, inventory tables (lea r14d,[r15+6])"),
        new(0x1402860AEL, 0x06, Widen.PlusN, "owned-item template maintainer walk, picker tables (lea r14d,[r15+6])"),
        new(0x140396881L, 0x06, Widen.PlusN, "owned-item template maintainer walk, third copy (function RVA 0x39684C) over the inventory tables (lea r14d,[r15+6])"),
        // Without these a unit holding a new weapon reads as empty handed in nine places
        // (1.5.2 exe on disk, tools/probes/lw365_hand_resolver_scan.py).
        // Six are more hand resolver copies. Each is `mov edx,0xff` then `lea ecx,[rdx+6]` with the offset at lea +2,
        // guarding `cmp ax,dx / je skip` for an empty hand and `cmp cx,ax / ja valid` for id < 0x105.
        // Copies 3, 4 and 6 guard word [rbx+0x20] (fallback [rbx+0x22]), 5 and 7 read a global hand word, and 8 is `45 8d 41 06`.
        // The other three guard the equip slot switch near 0x140306655, where the menu meets the equipped item word
        // and the slot flag byte at [r8+0x1B]. They are `mov r10d,0xff` then `lea eax,[r10+6]` (41 8d 42 06, offset at lea +3),
        // guarding `cmp ax,[r9+slot] / jbe skip` for words +0x1E, +0x1C and +0x1A (flags 0x20, 0x40, 0x80).
        // Three more look like caps and are left alone.
        // 0x1402C8155 (`lea r12d,[rax+6]` in the u16 walker) guards a stack array. The routine clears 0x414 bytes
        // (0x105 dwords) at 0x1402C8119, adds into it by item id, and stops its last scan at 0x1402C81DB with the same
        // register, so widening it writes past the array, the same stack cookie trap as before. That branch also
        // needs 0x140278fec(id) >= 0x64, so a new item only skips a pass the player never sees.
        // 0x1402BD992 (44 8d 4e 06, opcode byte 0x1402BD993, offset byte 0x1402BD995) after `mov r11d,0xff` only looks
        // like one. Rsi is 1 there (mov esi,1 at 0x1402BD974, kept across the call), so the lea makes 7, the number of
        // equip slot words for a `rep stosw` of 0x00FF markers, and the routine already masks hand ids with
        // 0x3FF. Sweeps keep naming one byte or the other, so tests pin both as untouched.
        // 0x1403066BE (`mov eax,0x105` in the same switch) guards word slots [r9+0x22] and [r9+0x26] (flags 8 and 2),
        // not hands, and new items are weapons only (ExtendedWeaponCeilingTests). It needs widening only if new
        // non weapon items ever ship.
        new(0x1402C4EB7L, 0x06, Widen.PlusN, "hand resolver copy 3 (guards word [rbx+0x20]; fallback read [rbx+0x22])"),
        new(0x1402DE6FFL, 0x06, Widen.PlusN, "hand resolver copy 4 (guards word [rbx+0x20], second copy)"),
        new(0x1402FE436L, 0x06, Widen.PlusN, "hand resolver copy 5 (reads a rip-relative global hand word)"),
        new(0x14033B5BBL, 0x06, Widen.PlusN, "hand resolver copy 6 (guards word [rbx+0x20], third copy)"),
        new(0x140378640L, 0x06, Widen.PlusN, "hand resolver copy 7 (reads a rip-relative global hand word, second copy)"),
        new(0x14039636BL, 0x06, Widen.PlusN, "hand resolver copy 8 (lea r8d,[r9+6]; variant encoding 45 8d 41 06)"),
        new(0x1403066A7L, 0x06, Widen.PlusN, "equip-slot switch guard, word +0x1E (lea eax,[r10+6])"),
        new(0x14030671DL, 0x06, Widen.PlusN, "equip-slot switch guard, word +0x1C (lea eax,[r10+6])"),
        new(0x1403067ABL, 0x06, Widen.PlusN, "equip-slot switch guard, word +0x1A (lea eax,[r10+6])"),
        // Without these two a new weapon hits for the wrong damage. They were read offline from the Denuvo free
        // 1.5.2 exe [journal 2026-10-16], where they sit in plain code instead of the protected tail.
        // The first is another hand resolver copy, the shape of copies 1 and 2 (`ba ff 00 00 00` mov edx,0xff, then `8d 4a 06` lea ecx,[rdx+6]),
        // with two calls to Offsets.ThunkWeaponStat within 0x30 bytes after it, as the journal described.
        // The second is in the swing publisher (entry RVA 0x282234). `bd ff 00 00 00` mov ebp,0xff at RVA 0x282287,
        // then `44 8d 75 06` lea r14d,[rbp+6] at RVA 0x2822A1. It copies combat +0x20 and +0x24 to the swing words at
        // RVA 0x7B0764 and 0x7B0766 and swaps in the off hand word for ids at or past the bound (`66 44 3b f0` cmp r14w,ax
        // then `77 14` ja). The old `xor r15d,0x5E` with 0x58 was the protector hiding the same 6 (0x58 ^ 0x5E = 6).
        new(0x14027F002L, 0x06, Widen.PlusN, "damage-path hand resolver (lea ecx,[rdx+6]); plain-text copy of the former protected-tail site, two weapon-stat thunk calls follow"),
        new(0x1402822A4L, 0x06, Widen.PlusN, "damage-staging cap (lea r14d,[rbp+6] after mov ebp,0xff) in the swing publisher; plain copy of the former xor r15d,0x5E site; lea bytes are 44 8d 75 06 at image offsets A1..A4, the disp8 is A4 (confirmed against the exe on disk 2026-10-16)"),
    };

    /// <summary>Sites that only exist once a save has loaded, patched from the tick as
    /// <see cref="PendingPatch"/>es. None on the Denuvo-free build, kept for a page that maps in late.</summary>
    private static readonly Site[] PostLoadSites = { };

    public static List<BytePatch> BootPatches(int extendedCount) => Build(BootSites, extendedCount);
    public static List<BytePatch> PostLoadPatches(int extendedCount) => Build(PostLoadSites, extendedCount);

    private static List<BytePatch> Build(Site[] sites, int n)
    {
        var list = new List<BytePatch>(sites.Length);
        foreach (var s in sites) list.Add(new BytePatch(ImageBase.At(s.Addr), s.Old, NewByte(s.Old, s.Kind, n), s.Label));
        return list;
    }

    private static byte NewByte(byte old, Widen kind, int n) => kind switch
    {
        Widen.PlusN => (byte)(old + n),
        Widen.HighByte => 0x02,
        Widen.ShopLoop => (byte)(0x05 + n),   // 0x100 becomes 0x105 + N and the high byte stays 0x01
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown widening kind"),
    };
}
