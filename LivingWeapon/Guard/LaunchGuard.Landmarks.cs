namespace LivingWeapon;

/// <summary>
/// LW-83 seam. The expected-value constants and the roster-row probe of <see cref="LaunchGuard"/>, split out of
/// LaunchGuard.cs so that file keeps the lifecycle code. Same partial class.
/// For LW-392 the roster probe's Unreadable verdict carries a Detail (slot empty vs page unreadable) that
/// LaunchGuard.Stall.cs uses to word the long-wait message.
/// For LW-112 the JobCommand rec8/rec9 constants stay here although the kit-lane guard (LaunchGuard.KitLane.cs)
/// reads them, since AnchorScout reuses the two signature arrays as its re-find spec.
/// </summary>
internal sealed partial class LaunchGuard
{
    // PE build key of the Denuvo-free 1.5.2 exe (Steam buildid 24304444, stamped 2026-07-20 16:17:31 UTC,
    // SHA256 937233F7..., installed 2026-10-15), read from the on-disk PE per docs/PATCH_REANCHOR.md
    // (docs/research/PORT_1.5.2_NODRM_OFFSETS.md). A report line since LW-397. This build has ASLR (DllCharacteristics
    // 0x8160), so the header is read at ImageBase.Base (LW-387, ported by LW-388). Earlier builds (stamp / size):
    // Denuvo 1.5.2 0x6A5EA53C / 0x18D78000 (buildid 24304240, docs/research/PORT_1.5.2_OFFSETS.md), 1.5.1 0x6A3C5497 /
    // 0x1878E000 (docs/research/PORT_1.5.1_OFFSETS.md), 1.5 0x6A0F86A9 / 0x190EB000 (docs/research/PORT_1.5_OFFSETS.md:11-15),
    // pre-1.5 0x690C1269 / 0x156C8000. Both fields differ on every pair. PE FileVersion reads 1.0.0.0 on every build
    // (docs/research/PORT_1.5.md:96-98). The Denuvo-free build is the same compile as wrapped 1.5.2, so every pinned RVA is unchanged.
    internal const uint ExpectedTimeDateStamp = 0x6A5E4A1B;
    internal const uint ExpectedSizeOfImage = 0x03F9E000;

    // JobCommand rec 8 (Aim, Archer) and rec 9 (Martial Arts, Monk) ability-id bytes, at Barrage.AbilityBase
    // (0x14067E213, Barrage.cs:43) + rec * Barrage.RecSize (25, Barrage.cs:44). The re-find signature is
    // tools/probes/jobcommand_find_probe.py:44-45 (REC8_SIG/REC9_SIG). Rec 14 (Steal) is skipped, its bytes rest on one dump note.
    private static long Rec8Addr => Barrage.AbilityBase + 8L * Barrage.RecSize;
    private static long Rec9Addr => Barrage.AbilityBase + 9L * Barrage.RecSize;
    // Aim ability ids 150..157 (0x96..0x9D), Martial Arts ids 100..107 (0x64..0x6B), as in the probe's REC8_SIG/REC9_SIG.
    // File-baked static image data, present from module-map time (checked 2026-07-14 by
    // tools/probes/anchorscan_feasibility_probe.py against exe backup file offset 0x67D6DB), so AnchorScout.cs can
    // re-find it with no save loaded, unlike the roster gate below. Internal because AnchorScout shares these as its spec.
    internal static readonly byte[] Rec8Sig = { 0x96, 0x97, 0x98, 0x99, 0x9A, 0x9B, 0x9C, 0x9D };
    internal static readonly byte[] Rec9Sig = { 0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6A, 0x6B };

    // Ramza roster row shape at RosterBase slot 0 (Offsets.cs:94/120, unitid_probe captures, Offsets.cs ANameId doc,
    // docs/research/SPRITE_SWAP.md:41-52). Ramza's sprite is 0x01-0x06 across chapters; story bodies stay under 0x80, monsters are 0x80 or above.
    private const int RamzaExpectedNameId = 1;
    private const byte MonsterSpriteFloor = 0x80;
    private const byte MaxPlausibleBraveFaith = 100;

    // 30 steps at the 33ms Engine tick (Engine.cs:19, PollMs) is about one second before a permanent stand-down.
    private const int MismatchDebounce = 30;

    private LandmarkReading ProbeRamzaRosterRow()
    {
        long rb = Offsets.RosterBase;
        int level = _mem.U8(rb + Offsets.RLevel);
        if (level < 1 || level > 99)   // unpopulated slot (title screen) or an unreadable page
            // LW-392 a mapped zero byte is the ordinary title screen (slot empty); only an unreadable page is "page unreadable" (LaunchGuard.Stall.cs).
            return new LandmarkReading(LandmarkVerdict.Unreadable,
                _mem.TryReadBytes(rb + Offsets.RLevel, 1, out _) ? RosterSlotEmpty : RosterPageUnreadable);

        int nameId = _mem.U16(rb + Offsets.RNameId);
        byte sprite = _mem.U8(rb + Offsets.RSprite);
        int brave = _mem.U8(rb + Offsets.RBrave);
        int faith = _mem.U8(rb + Offsets.RFaith);

        if (nameId == RamzaExpectedNameId && sprite < MonsterSpriteFloor
            && brave <= MaxPlausibleBraveFaith && faith <= MaxPlausibleBraveFaith)
            return LandmarkVerdict.Match;

        return new LandmarkReading(LandmarkVerdict.Mismatch,
            $"observed nameId={nameId} sprite=0x{sprite:X2} brave={brave} faith={faith} "
            + "(expected nameId=1, sprite<0x80, brave<=100, faith<=100)");
    }
}
