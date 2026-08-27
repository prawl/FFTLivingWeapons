using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// LW-346: the WHAT half of the extended inventory (the game knowledge), kept apart from the
/// lifecycle in ExtendedInventory.cs the way LaunchGuard.Landmarks.cs is kept apart from
/// LaunchGuard.cs. Every site below is a single-byte id CAP the 1.5.2 build hardcodes as 261
/// (0x105) in one encoding or another; each one is widened by the number of extended ids so the
/// game's own loops, guards and clamps admit them. Provenance per site is the FFTHandsFree
/// boot-arm marker v2 (tools/probes/lw346_capbreak_bootarm.marker.v2.txt, every line
/// owner-observed on 1.5.2 2026-08-26/27) plus the rig's CapBreakEquipHook/CapBreakDisplayHook
/// constants; every old byte was re-read from the 1.5.2 exe on disk 2026-08-27.
///
/// WIDENING RULE: the rig widened every cap by exactly one (a single new id); this table
/// generalises to N. A plain immediate/displacement low byte becomes old + N (no site's low byte
/// can carry past 0xFF for N up to 250). Two sites differ: the count getter's cap is widened on
/// its HIGH byte (0x103 -&gt; 0x203, ids up to 515, fixed) and the damage-staging cap feeds its
/// bound through <c>xor r15d,0x5E</c> with 0x58, so its new byte is 0x58 ^ (6 + N).
/// </summary>
internal static class ExtendedSites
{
    private enum Widen { PlusN, HighByte, XorMask, ShopLoop }

    private readonly record struct Site(long Addr, byte Old, Widen Kind, string Label);

    /// <summary>Boot-safe sites: plain code pages, patched in ExtendedInventory.BootArm before the
    /// game runs.</summary>
    private static readonly Site[] BootSites =
    {
        new(0x140284800L, 0x01, Widen.HighByte, "count-getter cap (mov r8d,0x103 -> 0x203)"),
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

    /// <summary>The accessor thunks every per-category lookup goes through, with which donor
    /// table each one answers from: the art donor draws the swing, the clone donor answers for
    /// everything else (type, validity, range, the sibling per-item tables).</summary>
    public static readonly (long Addr, string Label, bool UsesArtDonor)[] DonorThunks =
    {
        (Offsets.ThunkValidity, "validity", false),
        (Offsets.ThunkTypeProbe, "type-probe", false),
        (Offsets.ThunkRangeIndex, "range-index", false),
        (Offsets.ThunkRangeBase, "range-base", false),
        (Offsets.ThunkSibling1, "sibling-1", false),
        (Offsets.ThunkSibling2, "sibling-2", false),
        (Offsets.ThunkSibling3, "sibling-3", false),
        (Offsets.ThunkSibling4, "sibling-4", false),
        (Offsets.ThunkSpritePair, "sprite-pair", true),
    };
}
