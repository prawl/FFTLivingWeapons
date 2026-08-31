using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// LW-346: the WHAT half of the extended inventory (the game knowledge), kept apart from the
/// lifecycle in ExtendedInventory.cs the way LaunchGuard.Landmarks.cs is kept apart from
/// LaunchGuard.cs. Every BootSites entry but its last two is a single-byte id CAP the 1.5.2 build
/// hardcodes as 261 (0x105) in one encoding or another; each one is widened by the number of
/// extended ids so the game's own loops, guards and clamps admit them (the last two are a
/// reserved-id list moved out of the way once; see NOT A CAP). Provenance per site is the FFTHandsFree
/// boot-arm marker v2 (tools/probes/lw346_capbreak_bootarm.marker.v2.txt, every line
/// owner-observed on 1.5.2 2026-08-26/27) plus the rig's CapBreakEquipHook/CapBreakDisplayHook
/// constants; every old byte was re-read from the 1.5.2 exe on disk 2026-08-27.
///
/// WIDENING RULE: the rig widened every cap by exactly one (a single new id); this table
/// generalises to N. A plain immediate/displacement low byte becomes old + N (LW-368 round 2,
/// P11: eight of these sites are `lea` instructions whose displacement is a single SIGNED byte,
/// not an imm32, so their own byte overflows past N = <see cref="MaxExtendedCount"/> = 121 --
/// that bound, not 0xFF, is what actually caps N; <c>ExtendedInventoryData</c> and
/// tools/generate.py both refuse a shipped table past it). Two sites differ: the count getter's
/// cap is widened on its HIGH byte (0x103 -&gt; 0x203, ids up to 515, fixed) and the
/// damage-staging cap feeds its bound through <c>xor r15d,0x5E</c> with 0x58, so its new byte is
/// 0x58 ^ (6 + N).
///
/// TRAP (LW-351 fix round 3, 2026-08-30): the count getter's entry address 0x140284800 is the
/// SECOND byte of one immediate, <c>mov r8d,0x103</c> at 0x1402847FD, whose bytes run
/// 0x1402847FF..0x140284802. Re-reading that instruction on disk shows 0x103 and invites a
/// second, PlusN entry on the low byte 0x1402847FF; that would widen one immediate twice. The
/// bag accessor it gates (read/adjust 0x1411A7C00[id]) already admits ids 1..516 with the entry
/// below: the running game read 41 b8 03 02 00 00 there on 2026-08-30 (read-only RPM, N=2).
/// ExtendedSitesTests pins both halves.
///
/// NOT A CAP (LW-351 fix round 6, 2026-08-30): two sites below are a RESERVED-ITEM EQUALITY
/// LIST, not a bound. The per-job can-equip check 0x1402886D0 (and an inlined copy of it at
/// 0x140396F1C) tests the item id against five hardcoded ids before it ever reads the item's
/// own job class; read from the 1.5.2 exe on disk:
///   140288709  81 ea 06 01 00 00  sub edx, 0x106   ; id == 262 ?
///   14028870F  74 48              je special
///   140288711  83 ea 1a           sub edx, 0x1a    ; == 288 ?
///   140288716  83 ea 05           sub edx, 5       ; == 293 ?
///   14028871B  83 ea 08           sub edx, 8       ; == 301 ?
///   140288720  83 fa 09 / 74 34   cmp edx, 9 ; je  ; == 310 ?
///   (0x140396F52  81 e9 06 01 00 00  sub ecx, 0x106, then the same four steps)
/// The special path admits only jobs 0xA1 and 0xA4 (unnamed rows in job.en) and answers -1
/// ("cannot be equipped by the current job") for every other job. Vanilla never reaches it
/// (ItemData ends at id 260), but the SECOND extended id is 262, so whichever item sits there
/// is refused everywhere; the owner's swap experiment (261 and 262 exchanged, redeployed)
/// showed the refusal following the id, not the item. The fix moves the list, once, out of
/// the whole extended range: the imm32's second byte 0x01 -&gt; 0x02 makes it {518, 544, 549,
/// 557, 566}, past EXTENDED_LAST_ID (511, tools/generate.py) and inside the 0x3FF id mask.
/// The same byte lands for any N: this is a relocation, not a widening, and its entries say so.
/// </summary>
internal static class ExtendedSites
{
    /// <summary>How many extended items can exist at once. Plain language: past this many, the
    /// game breaks in a way that has nothing to do with save slots or menu space -- eight of the
    /// PlusN sites above are `lea reg,[base+6]` instructions whose displacement is a single
    /// SIGNED byte (P11), and 6 + 122 = 0x80 flips that byte negative, corrupting the bound
    /// instead of raising it. 121 is exact (the reviewer's live decode of every PlusN site,
    /// LW368_plan.md P11); <see cref="ExtendedInventoryData"/> and tools/generate.py both refuse
    /// a shipped table past it.</summary>
    public const int MaxExtendedCount = 121;

    private enum Widen { PlusN, HighByte, XorMask, ShopLoop }

    private readonly record struct Site(long Addr, byte Old, Widen Kind, string Label);

    /// <summary>Boot-safe sites: plain code pages, patched in ExtendedInventory.BootArm before the
    /// game runs.</summary>
    private static readonly Site[] BootSites =
    {
        // the imm32 of `mov r8d,0x103` at 0x1402847FD; see the TRAP note above.
        new(0x140284800L, 0x01, Widen.HighByte, "count-getter cap (mov r8d,0x103 -> 0x203)"),
        // LW-351 fix round 2 (2026-08-30, re-disassembled from the 1.5.2 exe on disk): the
        // builder family at 0x140280000-0x140290000 carries nine imm32 0x105 item-id bounds and
        // this was the one site LW-346 missed. 0x140284553 `mov edx,0x105` loads the bound the
        // per-item state initialiser reuses four times: it clears the id-keyed byte arrays at
        // 0x1411A7C00 and 0x1411A7700 and seeds the id-keyed u16 array at 0x1411A7810. CORRECTED
        // 2026-08-31 (LW-368 round 2b): the widened tail is NOT safe padding -- a live game state
        // dword sits at +0x108 of each 0x110-byte byte-array slot (read live 2026-08-31:
        // 0x1411A7D08 and 0x1411A7808 both carry a `cmp/mov dword` operand elsewhere in the exe,
        // not zero-fill), so the in-place widening overlapped it from the fourth extended id on (id 264; the dword spans 264-267).
        // The LW-368 list relocation (ListRelocation.cs) is what actually removes that overlap,
        // by moving both byte arrays off this block entirely; the u16 array's tail is still
        // re-zeroed by the memset at 0x1402845E0 that immediately follows it.
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
        // LW-354 (2026-08-27 evening, static read): the shop BUY-list builder walks ids 0..0xFF
        // (cmp ebx,0x100 at 0x140288FD9; this is the imm32's low byte); widened to 0x105 + N so
        // the extended ids are candidates. Their town flags come from the ShopFlagsMirror.
        new(0x140288FDBL, 0x00, Widen.ShopLoop, "shop buy-list loop bound (cmp ebx,0x100)"),
        // LW-351 fix round 6: the reserved-item equality list in the can-equip check and its
        // inlined twin (see the NOT A CAP note above). Each address is the SECOND byte of the
        // `sub r32,0x106` imm32 (0x140288709 + 3; 0x140396F52 + 3): 0x106 -> 0x206, so the five
        // reserved ids {262, 288, 293, 301, 310} become {518, 544, 549, 557, 566}. Relocated
        // once, never widened by N (HighByte lands 0x02 for every count).
        new(0x14028870CL, 0x01, Widen.HighByte, "reserved-item equality list, can-equip check (sub edx,0x106 -> 0x206)"),
        new(0x140396F55L, 0x01, Widen.HighByte, "reserved-item equality list, inlined twin (sub ecx,0x106 -> 0x206)"),
        // LW-351 fix round 8 (2026-08-31, read from the 1.5.2 exe on disk): the owned-item
        // template maintainer 0x140285F80 (inventory order tables; its second half 0x140286070
        // serves the picker tables) walks a template to its end before inserting a newly owned
        // id at the front, and that walk stops at the 0x00FF marker OR at the first word >=
        // r14w, where r14d comes from `45 8d 77 06` lea r14d,[r15+6] with r15d = 0xFF (0x105).
        // Three copies exist, all on the same r14/r15 pair: 0x140285F80 (inventory tables),
        // 0x140286070 (picker tables) and 0x14039684C (round 8b; the same five inventory tables
        // through its own pointer table 0x140689C38, owned-ness read from a transaction-time
        // count mirror at 0x143C52740). The sibling walkers (0x140286187, 0x140285EE7) were
        // widened by LW-346; these three were missed because their bound lives in a different
        // register pair (r14 from r15) than the swept lea+6 forms, not because disp8 bounds
        // were unknown (four such entries predate round 8). Unwidened, an extended id inside a
        // template ends the walk early, with two effects. The insert's shift starts AT the stop
        // word, so that id survives and crawls one slot right while the word to its RIGHT is
        // overwritten, and the end marker itself is lost once the crawling id reaches it. The
        // doubles come from the truncated walk failing to FIND the ids past the stop word, so
        // the maintainer re-inserts ids it already lists (the owner's Sort on 2026-08-31 00:11
        // showed five doubled shields and five vanished designs; the emulated sequence
        // reproduces that table word for word). Each address below is the lea's disp8 byte
        // (instruction +3). Templates already damaged are healed by TemplateSeat's repair.
        new(0x140285FB5L, 0x06, Widen.PlusN, "owned-item template maintainer walk, inventory tables (lea r14d,[r15+6])"),
        new(0x1402860AEL, 0x06, Widen.PlusN, "owned-item template maintainer walk, picker tables (lea r14d,[r15+6])"),
        new(0x140396881L, 0x06, Widen.PlusN, "owned-item template maintainer walk, third copy 0x14039684C over the inventory tables (lea r14d,[r15+6])"),
        // LW-365 (2026-08-31, read from the 1.5.2 exe on disk via
        // tools/probes/lw365_hand_resolver_scan.py): the last nine one-byte bounds in the family
        // of routines that read "what is this unit holding". Six are further copies of the hand
        // resolver already in this table (shape `mov edx,0xff` then `lea ecx,[rdx+6]`, disp byte
        // = lea +2, guarding `cmp ax,dx / je skip` for an empty hand and `cmp cx,ax / ja valid`
        // for id < 0x105): three guard word [rbx+0x20] (the fallback read is [rbx+0x22]; copies
        // 3, 4, 6), two read a rip-relative global hand word (copies 5, 7), and copy 8 uses the
        // variant encoding `45 8d 41 06` (lea r8d,[r9+6]) over the same guard. The remaining
        // three are equip-slot switch guards inside the function at ~0x140306655 (a switch
        // mapping a UI code to an equipped-item word plus a slot flag byte at [r8+0x1B]): each is
        // `mov r10d,0xff` then `lea eax,[r10+6]` (41 8d 42 06; disp byte = lea +3), guarding
        // `cmp ax,[r9+slot] / jbe skip` for the word at +0x1E/+0x1C/+0x1A (flags 0x20/0x40/0x80).
        //
        // Two more suspect sites were investigated and are NOT patched. 0x1402C8155 (the u16
        // walker's `lea r12d,[rax+6]`) is WALLED: its 0x105 bound guards a STACK buffer -- the
        // function memsets a 0x414-byte (0x105 dwords) frame array at 0x1402C8119, the walk
        // accumulates into it BY ITEM ID, and the same register bounds the final per-id scan at
        // 0x1402C81DB, so widening the byte would write past the array for extended ids (the
        // LW-371 stack-cookie lesson); the branch also requires callee 0x140278fec(id) >= 0x64,
        // so extended ids merely skip a strip-and-convert pass invisible to the player.
        // The lea at 0x1402BD992 (bytes 44 8d 4e 06; opcode byte 0x1402BD993, disp8 byte
        // 0x1402BD995) after `mov r11d,0xff` is a FALSE POSITIVE, not a cap: rsi is 1 there (mov
        // esi,1 at 0x1402BD974, preserved across the intervening call), so the lea computes 7,
        // the equip-slot word count for a `rep stosw` fill of 0x00FF empty markers, and the
        // routine already masks hand ids with 0x3FF. Never patch EITHER byte: sweeps propose the
        // opcode byte or the disp byte depending on their convention, so both are pinned absent.
        // One more imm32 sibling, 0x1403066BE (`mov eax,0x105`, the same switch as the three
        // guards above) is recorded, not widened: it serves the word slots at
        // [r9+0x22]/[r9+0x26] (flags 8/2), which are not hand slots, and extended kinds are
        // weapons-only today (ExtendedWeaponCeilingTests), so no extended id can reach it;
        // revisit only if extended non-weapon kinds ever ship.
        new(0x1402C4EB7L, 0x06, Widen.PlusN, "hand resolver copy 3 (guards word [rbx+0x20]; fallback read [rbx+0x22])"),
        new(0x1402DE6FFL, 0x06, Widen.PlusN, "hand resolver copy 4 (guards word [rbx+0x20], second copy)"),
        new(0x1402FE436L, 0x06, Widen.PlusN, "hand resolver copy 5 (reads a rip-relative global hand word)"),
        new(0x14033B5BBL, 0x06, Widen.PlusN, "hand resolver copy 6 (guards word [rbx+0x20], third copy)"),
        new(0x140378640L, 0x06, Widen.PlusN, "hand resolver copy 7 (reads a rip-relative global hand word, second copy)"),
        new(0x14039636BL, 0x06, Widen.PlusN, "hand resolver copy 8 (lea r8d,[r9+6]; variant encoding 45 8d 41 06)"),
        new(0x1403066A7L, 0x06, Widen.PlusN, "equip-slot switch guard, word +0x1E (lea eax,[r10+6])"),
        new(0x14030671DL, 0x06, Widen.PlusN, "equip-slot switch guard, word +0x1C (lea eax,[r10+6])"),
        new(0x1403067ABL, 0x06, Widen.PlusN, "equip-slot switch guard, word +0x1A (lea eax,[r10+6])"),
    };

    /// <summary>Copy-protected sites: their pages read vanilla only after a save has loaded, so
    /// they are stepped from the tick loop as <see cref="PendingPatch"/>es.</summary>
    private static readonly Site[] PostLoadSites =
    {
        new(0x14F2EA40FL, 0x06, Widen.PlusN, "damage-path hand resolver (lea ecx,[rdx+6])"),
        new(0x14F45D315L, 0x5E, Widen.XorMask, "damage-staging cap input (xor r15d,0x5E)"),
    };

    public static List<BytePatch> BootPatches(int extendedCount) => Build(BootSites, extendedCount);
    public static List<BytePatch> PostLoadPatches(int extendedCount) => Build(PostLoadSites, extendedCount);

    private static List<BytePatch> Build(Site[] sites, int n)
    {
        var list = new List<BytePatch>(sites.Length);
        foreach (var s in sites) list.Add(new BytePatch(s.Addr, s.Old, NewByte(s.Old, s.Kind, n), s.Label));
        return list;
    }

    private static byte NewByte(byte old, Widen kind, int n) => kind switch
    {
        Widen.PlusN => (byte)(old + n),
        Widen.HighByte => 0x02,
        Widen.ShopLoop => (byte)(0x05 + n),   // 0x100 -> 0x105 + N (the imm32's high byte stays 0x01)
        _ => (byte)(0x58 ^ ((0x58 ^ old) + n)),
    };
}
