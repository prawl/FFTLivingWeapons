namespace LivingWeapon;

/// <summary>
/// LW-83 seam: the WHAT half of <see cref="LaunchGuard"/>, split out of LaunchGuard.cs so that
/// file stays the LIFECYCLE half (construction, Step, the hook-arm handshake, the armed/stand-down
/// edges) under the 200-line house guideline. Holds the three landmarks' expected-value constants,
/// the two composite Probe* methods (JobCommand rec8/rec9, Ramza's roster row), and the
/// observed-vs-expected detail formatting each composes on a mismatch. Same partial class as
/// LaunchGuard.cs, so LaunchGuard.cs's constructor still reaches every constant and probe here by
/// plain name: this is a real data/game-knowledge-vs-behavior split, not a state-machine clone.
/// </summary>
internal sealed partial class LaunchGuard
{
    // PE fingerprint of the 1.5.1 exe (Steam buildid 23901820, exe stamped 2026-07-13),
    // re-derived from the on-disk PE per docs/PATCH_REANCHOR.md. 1.5 values were 0x6A0F86A9 /
    // 0x190EB000 (docs/research/PORT_1.5_OFFSETS.md:11-15; exe SHA256 3625FD9B...); the 1.5.1
    // layout audit (docs/research/PORT_1.5.1_OFFSETS.md) found every other absolute unchanged.
    // Pre-1.5 values differ in BOTH fields (0x690C1269 / 0x156C8000), so either field alone
    // would catch a rollback; PE FileVersion is NOT usable (reads 1.0.0.0 on every build,
    // docs/research/PORT_1.5.md:96-98).
    internal const uint ExpectedTimeDateStamp = 0x6A3C5497;
    internal const uint ExpectedSizeOfImage = 0x1878E000;
    private const long ModuleBase = 0x140000000L;

    // JobCommand table, rec 8 (Aim, Archer) + rec 9 (Martial Arts, Monk) ability-id bytes. Window
    // addresses = Barrage.AbilityBase (0x14067E213, Barrage.cs:43) + rec * Barrage.RecSize (25,
    // Barrage.cs:44). The unique-hit re-find signature that located AbilityBase after the 1.5
    // recompile is tools/probes/jobcommand_find_probe.py:44-45 (REC8_SIG/REC9_SIG). Rec 14 (Steal)
    // is deliberately dropped: its exact bytes rest on one dump note, weaker provenance than the
    // probe-verified pair above.
    private static readonly long Rec8Addr = Barrage.AbilityBase + 8L * Barrage.RecSize;
    private static readonly long Rec9Addr = Barrage.AbilityBase + 9L * Barrage.RecSize;
    // Aim ability ids 150..157 (0x96..0x9D), Martial Arts ability ids 100..107 (0x64..0x6B): the
    // same bytes as jobcommand_find_probe.py's REC8_SIG/REC9_SIG.
    private static readonly byte[] Rec8Sig = { 0x96, 0x97, 0x98, 0x99, 0x9A, 0x9B, 0x9C, 0x9D };
    private static readonly byte[] Rec9Sig = { 0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6A, 0x6B };

    // Ramza roster row shape at RosterBase slot 0 (Offsets.cs:94). Provenance: Offsets.cs:94/120,
    // unitid_probe captures (Offsets.cs's ANameId doc), docs/research/SPRITE_SWAP.md:41-52 (Ramza
    // sprite 0x01-0x06 across chapters; story bodies stay under 0x80, monsters are >= 0x80, in
    // every chapter).
    private const int RamzaExpectedNameId = 1;
    private const byte MonsterSpriteFloor = 0x80;
    private const byte MaxPlausibleBraveFaith = 100;

    // At the 33ms Engine tick (Engine.cs:19, PollMs), 30 consecutive mismatching Steps is about
    // 30 * 33ms = 990ms: roughly one second before a permanent stand-down.
    private const int MismatchDebounce = 30;

    private LandmarkReading ProbeJobCommandTable()
    {
        // BOOT-WINDOW SAFETY: a loaded save implies the boot-built JobCommand table (learn
        // screens work), so gate on Ramza's roster row being populated at all before reading the
        // signature windows. Game knowledge stays here in the adapter; FingerprintGuard's core
        // stays agnostic of it.
        int level = _mem.U8(Offsets.RosterBase + Offsets.RLevel);
        if (level < 1 || level > 99) return LandmarkVerdict.Unreadable;

        var rec8 = _jobCommandRec8.Probe();
        var rec9 = _jobCommandRec9.Probe();
        if (rec8.Verdict == LandmarkVerdict.Match && rec9.Verdict == LandmarkVerdict.Match)
            return LandmarkVerdict.Match;
        // LW-83: only the mismatching rec(s) contribute their inner detail, so one rec's failure
        // doesn't drag the other rec's unrelated match state into the diagnostic.
        if (rec8.Verdict == LandmarkVerdict.Mismatch || rec9.Verdict == LandmarkVerdict.Mismatch)
        {
            string detail = rec8.Verdict == LandmarkVerdict.Mismatch && rec9.Verdict == LandmarkVerdict.Mismatch
                ? $"rec8: {rec8.Detail}; rec9: {rec9.Detail}"
                : rec8.Verdict == LandmarkVerdict.Mismatch ? $"rec8: {rec8.Detail}" : $"rec9: {rec9.Detail}";
            return new LandmarkReading(LandmarkVerdict.Mismatch, detail);
        }
        return LandmarkVerdict.Unreadable;
    }

    private LandmarkReading ProbeRamzaRosterRow()
    {
        long rb = Offsets.RosterBase;
        int level = _mem.U8(rb + Offsets.RLevel);
        if (level < 1 || level > 99) return LandmarkVerdict.Unreadable;   // unpopulated slot (title screen)

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
