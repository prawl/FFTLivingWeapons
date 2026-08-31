using System.Linq;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>LW-346 S4: the patch-site table, pinned against the research rig's marker v2 (one
/// extended id, every line owner-observed 2026-08-26/27) and generalised to N ids.</summary>
public class ExtendedSitesTests
{
    [Fact]
    public void One_extended_id_reproduces_the_rig_marker_byte_for_byte()
    {
        var boot = ExtendedSites.BootPatches(1).ToDictionary(p => p.Addr, p => p);
        Assert.Equal(26, boot.Count);
        // the rig's six built-ins
        Assert.Equal((0x01, 0x02), (boot[0x140284800].Old, boot[0x140284800].New));
        foreach (long a in new[] { 0x140284724L, 0x1402847C9L, 0x140288CDAL, 0x140289074L })
            Assert.Equal((0x05, 0x06), (boot[a].Old, boot[a].New));
        Assert.Equal((0x06, 0x07), (boot[0x140284C0A].Old, boot[0x140284C0A].New));
        // the thirteen marker lines
        Assert.Equal((0x05, 0x06), (boot[0x140397121].Old, boot[0x140397121].New));
        Assert.Equal((0x05, 0x06), (boot[0x140287570].Old, boot[0x140287570].New));
        Assert.Equal((0x05, 0x06), (boot[0x140285E2D].Old, boot[0x140285E2D].New));
        Assert.Equal((0x06, 0x07), (boot[0x140286187].Old, boot[0x140286187].New));
        Assert.Equal((0x05, 0x06), (boot[0x1402862F7].Old, boot[0x1402862F7].New));
        Assert.Equal((0x06, 0x07), (boot[0x140285EE7].Old, boot[0x140285EE7].New));
        Assert.Equal((0x05, 0x06), (boot[0x14030CED3].Old, boot[0x14030CED3].New));
        Assert.Equal((0x04, 0x05), (boot[0x140101071].Old, boot[0x140101071].New));
        Assert.Equal((0x05, 0x06), (boot[0x1403602F4].Old, boot[0x1403602F4].New));
        Assert.Equal((0x06, 0x07), (boot[0x140226FDF].Old, boot[0x140226FDF].New));
        Assert.Equal((0x05, 0x06), (boot[0x14028024F].Old, boot[0x14028024F].New));
        Assert.Equal((0x04, 0x05), (boot[0x1401ED95B].Old, boot[0x1401ED95B].New));
        Assert.Equal((0x04, 0x05), (boot[0x1401ED982].Old, boot[0x1401ED982].New));
        Assert.Equal((0x00, 0x06), (boot[0x140288FDB].Old, boot[0x140288FDB].New));   // LW-354: shop loop 0x100 -> 0x106
        // LW-351 fix round 6: the two reserved-item equality lists, relocated (not widened)
        Assert.Equal((0x01, 0x02), (boot[0x14028870C].Old, boot[0x14028870C].New));
        Assert.Equal((0x01, 0x02), (boot[0x140396F55].Old, boot[0x140396F55].New));
        Assert.All(boot.Values, p => Assert.False(string.IsNullOrWhiteSpace(p.Label)));

        var post = ExtendedSites.PostLoadPatches(1).ToDictionary(p => p.Addr, p => p);
        Assert.Equal(2, post.Count);
        Assert.Equal((0x06, 0x07), (post[0x14F2EA40F].Old, post[0x14F2EA40F].New));
        Assert.Equal((0x5E, 0x5F), (post[0x14F45D315].Old, post[0x14F45D315].New));
    }

    [Fact]
    public void Seven_extended_ids_widen_every_cap_by_seven_except_the_two_special_encodings()
    {
        var boot = ExtendedSites.BootPatches(7).ToDictionary(p => p.Addr, p => p);
        Assert.Equal(0x0C, boot[0x140285E2D].New);   // 0x105 + 7 = 0x10C
        Assert.Equal(0x0D, boot[0x140284C0A].New);   // lea disp 6 + 7
        Assert.Equal(0x0B, boot[0x140101071].New);   // 0x104 + 7
        Assert.Equal(0x02, boot[0x140284800].New);   // high-byte widening is fixed (ids up to 515)
        Assert.Equal(0x0C, boot[0x140288FDB].New);   // shop loop 0x100 -> 0x10C
        Assert.Equal(0x02, boot[0x14028870C].New);   // reserved-item list: relocated once, N-independent
        Assert.Equal(0x02, boot[0x140396F55].New);
        var post = ExtendedSites.PostLoadPatches(7).ToDictionary(p => p.Addr, p => p);
        Assert.Equal(0x0D, post[0x14F2EA40F].New);
        Assert.Equal(0x58 ^ 13, post[0x14F45D315].New);   // r15 = 0x58 ^ imm must equal 6 + 7
    }

    [Fact]
    public void Fix_round_2_pins_every_builder_family_id_bound_for_two_extended_ids()
    {
        // LW-351 fix round 2. The builder family at 0x140280000-0x140290000 carries nine imm32
        // 0x105 item-id bounds (re-disassembled from the 1.5.2 exe on disk 2026-08-30; each
        // address below is the imm32's LOW byte, whose offset inside the instruction varies by
        // encoding: mov r32,imm32 = +1, REX mov r64/r8d = +2, cmp eax,imm32 = +1,
        // cmp r/m32,imm32 = +2). Eight were already in the table; the ninth is 0x140284554,
        // the per-item state initialiser's shared bound (0x140284553 `mov edx,0x105`), which
        // clears the id-keyed byte arrays at 0x1411A7C00 and 0x1411A7700 and seeds the id-keyed
        // u16 array at 0x1411A7810. Two extended ids means every one of them reads 0x07.
        var two = ExtendedSites.BootPatches(2).ToDictionary(p => p.Addr, p => p);
        foreach (long a in new[]
                 {
                     0x14028024FL,   // S1 hand resolver copy 2 (mov r8d,0x105)
                     0x140284554L,   // S2 per-item state initialiser bound (mov edx,0x105)
                     0x140284724L,   // S3 per-item array bounds check (cmp rax,0x105)
                     0x1402847C9L,   // S4 per-item decay loop bound (cmp ebx,0x105)
                     0x140285E2DL,   // S5 default-order template walk guard (mov eax,0x105)
                     0x1402862F7L,   // S6 acquired-sort template walk guard (mov eax,0x105)
                     0x140287570L,   // S7 per-item byte getter id filter (mov eax,0x105)
                     0x140288CDAL,   // S8 default-order template builder bound (cmp edi,0x105)
                     0x140289074L,   // S9 second template builder bound (cmp ebx,0x105)
                 })
            Assert.Equal((0x05, 0x07), (two[a].Old, two[a].New));
    }

    [Fact]
    public void The_new_initialiser_bound_widens_by_one_for_a_single_extended_id()
    {
        var one = ExtendedSites.BootPatches(1).ToDictionary(p => p.Addr, p => p);
        Assert.Equal((0x05, 0x06), (one[0x140284554].Old, one[0x140284554].New));
        Assert.False(string.IsNullOrWhiteSpace(one[0x140284554].Label));
    }

    [Fact]
    public void The_builder_family_jcc_displacements_are_never_patched()
    {
        // The same 0x105 byte scan also hits three `jcc rel32` displacements whose low byte is
        // 0x05: 0x14028511A je, 0x140288115 jne, 0x140288EDE jge. Widening one of those bytes
        // retargets a branch instead of raising a bound, so pin them out of both tables for good.
        var addrs = ExtendedSites.BootPatches(2).Select(p => p.Addr)
            .Concat(ExtendedSites.PostLoadPatches(2).Select(p => p.Addr)).ToHashSet();
        foreach (long a in new[]
                 {
                     0x14028511AL, 0x14028511BL, 0x14028511CL,
                     0x140288115L, 0x140288116L, 0x140288117L,
                     0x140288EDEL, 0x140288EE0L, 0x140288EE1L,
                 })
            Assert.DoesNotContain(a, addrs);
    }

    [Fact]
    public void The_bag_accessor_cap_is_widened_once_on_its_high_byte_and_never_on_its_low_byte()
    {
        // LW-351 fix round 3. Re-derived from the 1.5.2 exe on disk 2026-08-30:
        //   1402847F8  b8 ff 03 00 00     mov eax, 0x3ff
        //   1402847FD  41 b8 03 01 00 00  mov r8d, 0x103        <- THE id cap's imm32
        //   140284803  66 23 c8           and cx, ax            (mask the id to 10 bits)
        //   140284806  8d 41 ff           lea eax, [rcx - 1]
        //   140284809  66 41 3b c0        cmp ax, r8w
        //   14028480D  77 1c              ja -> xor eax,eax; ret  (id-1 over the cap = "none")
        //   14028480F  movzx r8d,cx; lea r9,[0x1411A7C00]; movzx ecx,byte[r8+r9];
        //              add ecx,edx; cmovns eax,ecx; mov byte[r8+r9],al; ret
        // i.e. the BAG accessor: read-and-adjust the per-id count in the array at 0x1411A7C00,
        // clamped at zero. The imm32 occupies 0x1402847FF..0x140284802, so its LOW byte is
        // 0x1402847FF and the address the table has carried since LW-346 (0x140284800) is that
        // same immediate's SECOND byte. The count-getter entry therefore already widens THIS
        // instruction, to 0x0203 (ids 1..516); a PlusN entry on 0x1402847FF would patch one
        // immediate twice. Live confirmation 2026-08-30 (read-only RPM, mod armed with N=2):
        // the running game reads 41 b8 03 02 00 00 here, so 261 and 262 already pass this gate
        // and it cannot be what drops them from the menu templates.
        var boot = ExtendedSites.BootPatches(2);
        var addrs = boot.Select(p => p.Addr).ToHashSet();
        var getter = boot.Single(p => p.Addr == 0x140284800L);
        Assert.Equal((0x01, 0x02), (getter.Old, getter.New));
        foreach (long a in new[] { 0x1402847FDL, 0x1402847FEL, 0x1402847FFL, 0x140284801L, 0x140284802L })
            Assert.DoesNotContain(a, addrs);
    }

    [Fact]
    public void Fix_round_3_sweep_hits_and_the_graceful_0x104_cap_are_never_patched()
    {
        // LW-351 fix round 3. The 0x103/0x104 value sweep over the builder region also hit four
        // `jcc rel32` displacements that merely happen to equal 0x103, disassembled from disk
        // 2026-08-30: jne at 0x14028094C, jne at 0x14028353C, je at 0x14028E5F9 and jne at
        // 0x140364875 (their rel32 bytes are listed below), plus one MID-INSTRUCTION hit at
        // 0x14028B80C, which is the high byte of the RIP displacement in
        // `c7 05 8f 5c 4b 03  01 00 00 00  mov dword [rip+0x34B5C8F], 1` at 0x14028B807 running
        // into that instruction's own imm32. Writing any of them retargets a branch or corrupts
        // a store instead of raising a bound.
        //
        // 0x14036B430 is different: it IS a real id cap, `cmp dword [rbx], 0x104` at
        // 0x14036B42E inside the item-info record builder 0x14036B2D0, and it is left unpatched
        // BY DECISION, not by ignorance. The cap-passing path is
        // `movsxd rax,[rbx]; mov cl, byte[rax*2 + 0x1411A7810]` (byte 0 of the id-keyed u16
        // array); the fallback is `mov cl, r12b`, with r12 zeroed at 0x14036B35B. Live
        // read-only RPM 2026-08-30: 0x1411A7810 reads 0x0000 for ids 261 and 262 (vanilla rows
        // read 0x0101, and ids 257/260 already read 0x0100), so byte 0 is 0x00 for every
        // extended id and BOTH paths store the same 0x00 into the record at +0x21. Widening
        // that cap cannot change a byte the game draws. Note also that the address the
        // fix-round notes carried, 0x14036B432, is the immediate's THIRD byte, not its low one.
        var addrs = ExtendedSites.BootPatches(2).Select(p => p.Addr)
            .Concat(ExtendedSites.PostLoadPatches(2).Select(p => p.Addr)).ToHashSet();
        foreach (long a in new[]
                 {
                     0x14028094EL, 0x14028094FL, 0x140280950L, 0x140280951L,   // jne 0x14028094C
                     0x14028353EL, 0x14028353FL, 0x140283540L, 0x140283541L,   // jne 0x14028353C
                     0x14028E5FBL, 0x14028E5FCL, 0x14028E5FDL, 0x14028E5FEL,   // je  0x14028E5F9
                     0x140364877L, 0x140364878L, 0x140364879L, 0x14036487AL,   // jne 0x140364875
                     0x14028B80CL, 0x14028B80DL, 0x14028B80EL, 0x14028B80FL,   // mid-instruction
                     0x14036B430L, 0x14036B431L, 0x14036B432L, 0x14036B433L,   // the graceful cap
                 })
            Assert.DoesNotContain(a, addrs);
    }

    [Fact]
    public void Fix_round_6_relocates_both_reserved_item_equality_lists_past_the_extended_range_once()
    {
        // LW-351 fix round 6. The per-job can-equip check 0x1402886D0 and an inlined copy of it
        // at 0x140396F1C both test the item id against a hardcoded RESERVED list, not a bound:
        //   140288709  81 ea 06 01 00 00  sub edx, 0x106   ; id == 262 ?
        //   14028870F  74 48              je special
        //   140288711  83 ea 1a           sub edx, 0x1a    ; == 288 ?
        //   140288716  83 ea 05           sub edx, 5       ; == 293 ?
        //   14028871B  83 ea 08           sub edx, 8       ; == 301 ?
        //   140288720  83 fa 09 / 74 34   cmp edx, 9 ; je  ; == 310 ?
        // (0x140396F52 `81 e9 06 01 00 00 sub ecx,0x106` then the same four steps). The special
        // path admits only jobs 0xA1 and 0xA4 and refuses every other job, which is exactly the
        // "This cannot be equipped by the current job" the owner saw for whichever item sat at
        // id 262 (the swap experiment: Terrastaff at 261 equipped, Moonblade at 262 refused).
        // The fix raises the imm32's SECOND byte 0x01 -> 0x02 so the list reads {518, 544, 549,
        // 557, 566}: past EXTENDED_LAST_ID (511) and unreachable by vanilla (ItemData ends at
        // 260). It is NOT widened by N: the same byte lands for one, two or seven items.
        foreach (int n in new[] { 1, 2, 7 })
        {
            var boot = ExtendedSites.BootPatches(n).ToDictionary(p => p.Addr, p => p);
            Assert.Equal((0x01, 0x02), (boot[0x14028870C].Old, boot[0x14028870C].New));
            Assert.Equal((0x01, 0x02), (boot[0x140396F55].Old, boot[0x140396F55].New));
            Assert.Contains("reserved", boot[0x14028870C].Label);
            Assert.Contains("reserved", boot[0x140396F55].Label);
            Assert.DoesNotContain("cap", boot[0x14028870C].Label);
            Assert.DoesNotContain("cap", boot[0x140396F55].Label);
        }
    }

    [Fact]
    public void Fix_round_8_widens_all_three_owned_item_template_maintainer_walks()
    {
        // LW-351 fix round 8 (2026-08-31). The maintainer 0x140285F80 (inventory order tables;
        // its second half 0x140286070 serves the picker tables) walks a template to its end
        // before inserting a newly owned id at the front, and its walk stops at the 0x00FF
        // marker OR at the first word >= r14w, where r14d = `lea r14d,[r15+6]` with r15d = 0xFF:
        // 0x105, read from disk at 0x140285FB2, 0x1402860AB and (round 8b, the third copy
        // 0x14039684C over the same five inventory tables through its own pointer table
        // 0x140689C38) 0x14039687E, all `45 8d 77 06`. None of the three disp8 bytes was in the
        // table (the sibling walkers 0x140286187 and 0x140285EE7 were; these three sit in a
        // different register pair, r14 from r15, than the swept lea+6 forms), so an extended id
        // inside a template ended the walk early. The insert's shift starts AT the stop word, so
        // that extended id survives and crawls one slot right while the word to its RIGHT is
        // overwritten, and the end marker itself is lost once the crawling id reaches it; the
        // doubles come from the truncated walk failing to FIND the ids past the stop word, so
        // the maintainer re-inserts ids it already lists. The owner's Sort on 2026-08-31 00:11
        // showed five doubled shields and five vanished designs; the emulated sequence
        // reproduces that table word for word.
        foreach (int n in new[] { 1, 2, 7 })
        {
            var boot = ExtendedSites.BootPatches(n).ToDictionary(p => p.Addr, p => p);
            foreach (long a in new[] { 0x140285FB5L, 0x1402860AEL, 0x140396881L })
            {
                Assert.Equal((0x06, 0x06 + n), (boot[a].Old, boot[a].New));
                Assert.Contains("maintainer", boot[a].Label);
            }
        }
    }

    [Fact]
    public void Fix_round_6_zlib_constants_and_the_chains_other_bytes_are_never_patched()
    {
        // LW-351 fix round 6. The live 0x106 scan surfaced five `cmp r32,0x106 ; jae` sites at
        // 0x1404101E7, 0x1404101FC, 0x1404105F8, 0x14041060D and 0x14040FCC1 that a first
        // reading called an equip gate. They are zlib: function 0x140410190 is deflate_fast /
        // deflate_slow ([rbx+0x9C] = lookahead, [rbx+0x94] = strstart, [rbx+0x68] = head,
        // [rbx+0x60] = prev, [rbx+0x70] = ins_h, `cmp eax,3` = MIN_MATCH) and 0x14040FB50 is
        // fill_window (`mov eax,0x102` = MAX_MATCH); 0x106 = MIN_LOOKAHEAD = 258 + 3 + 1. The
        // three `2d 06 01 00 00 sub eax,0x106` at 0x140410290, 0x1404106C9 and 0x1404F5EBC are
        // w_size - MIN_LOOKAHEAD. Writing any of those bytes corrupts the game's compressor.
        // The two reserved-list chains are patched on ONE byte each (the imm32's second byte);
        // their low bytes and the imm8 steps must stay vanilla or the list changes shape, and
        // 0x140101071 (the adjust-count cap, already in the table) must never gain a twin.
        var addrs = ExtendedSites.BootPatches(2).Select(p => p.Addr)
            .Concat(ExtendedSites.PostLoadPatches(2).Select(p => p.Addr)).ToHashSet();
        foreach (long a in new[]
                 {
                     0x1404101E6L, 0x1404101E7L, 0x1404101E8L, 0x1404101E9L, 0x1404101EAL,   // zlib cmp eax,0x106
                     0x1404101FBL, 0x1404101FCL, 0x1404101FDL, 0x1404101FEL, 0x1404101FFL,
                     0x1404105F7L, 0x1404105F8L, 0x1404105F9L, 0x1404105FAL, 0x1404105FBL,
                     0x14041060CL, 0x14041060DL, 0x14041060EL, 0x14041060FL, 0x140410610L,
                     0x14040FCBEL, 0x14040FCBFL, 0x14040FCC0L, 0x14040FCC1L, 0x14040FCC2L, 0x14040FCC3L, 0x14040FCC4L,   // cmp r9d,0x106
                     0x140410291L, 0x140410292L, 0x140410293L, 0x140410294L,   // sub eax,0x106 imm bytes
                     0x1404106CAL, 0x1404106CBL, 0x1404106CCL, 0x1404106CDL,
                     0x1404F5EBDL, 0x1404F5EBEL, 0x1404F5EBFL, 0x1404F5EC0L,
                     0x140288709L, 0x14028870AL, 0x14028870BL, 0x14028870DL, 0x14028870EL,   // chain 1: everything but the second imm byte
                     0x140288713L, 0x140288718L, 0x14028871DL, 0x140288722L,
                     0x140396F52L, 0x140396F53L, 0x140396F54L, 0x140396F56L, 0x140396F57L,   // chain 2
                     0x140396F5CL, 0x140396F61L, 0x140396F66L, 0x140396F6BL,
                     0x140285FB2L, 0x140285FB3L, 0x140285FB4L,   // round 8: the lea's opcode/modrm bytes, only the disp8 moves
                     0x1402860ABL, 0x1402860ACL, 0x1402860ADL,
                     0x14039687EL, 0x14039687FL, 0x140396880L,   // round 8b: the third copy's lea, same rule
                 })
            Assert.DoesNotContain(a, addrs);
        Assert.Single(ExtendedSites.BootPatches(2), p => p.Addr == 0x140101071L);
        Assert.DoesNotContain(ExtendedSites.BootPatches(2), p => p.Addr == 0x140101070L || p.Addr == 0x140101072L);
    }

    [Fact]
    public void Donor_thunks_cover_the_nine_per_category_accessors_with_only_the_sprite_pair_on_the_art_donor()
    {
        Assert.Equal(9, ExtendedSites.DonorThunks.Length);
        Assert.Single(ExtendedSites.DonorThunks, t => t.UsesArtDonor);
        Assert.Equal(Offsets.ThunkSpritePair, ExtendedSites.DonorThunks.Single(t => t.UsesArtDonor).Addr);
        Assert.DoesNotContain(ExtendedSites.DonorThunks, t => t.Addr == Offsets.ThunkWeaponStat);   // the row stub, not a donor
        Assert.Equal(9, ExtendedSites.DonorThunks.Select(t => t.Addr).Distinct().Count());
    }
}
