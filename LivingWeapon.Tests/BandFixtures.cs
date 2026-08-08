using LivingWeapon;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-149 stage I: shared band/victim seeding fixtures for FakeSparseMemory-based signature and
/// kill-tracker tests, replacing three copy-pasted families the LW-149 audit mapped. CORE + THIN
/// TAILS, not one union signature -- an adversarial plan review found that a single combined
/// SeedBandEntry (every caller's optional extras folded into one parameter list) would drag
/// module-specific offsets (Choir's Non-charge support bit, Sanctuary's crystal-hearts counter,
/// Benediction's HP-writable pair) into a fixture shared by callers that have nothing to do with
/// them. So <see cref="SeedBandEntryCore"/> carries only the 7 fields all three SeedBandEntry
/// copies set identically; BenedictionTests, ChoirTests, and SanctuaryTests each keep their own
/// private SeedBandEntry wrapper (same old name, same old signature, same defaults) that calls the
/// core then applies ONLY its own module-specific tail. No caller line in those three files
/// changed. BandFixturesTests.cs pins the exact address/value set the core produces against the
/// pre-dedup body, so a fixture that marks one extra byte fails loud instead of silently widening
/// a test's premise.
///
/// SeedVictimFields (KillTrackerDeedTests, KillTrackerTests, VictimProbeTests) and SeedAllyFp
/// (BenedictionTests, ChoirTests, SanctuaryTests) were byte-identical across their copies (verified
/// diff), so those fold in whole below; call sites were repointed at BandFixtures directly (the
/// same convention MemSeats already uses in this suite), not left as further private wrappers.
/// ChoirTests' SeedAllyFpAt (idx-parameterized) subsumes SeedAllyFp: SeedAllyFp is a one-line call
/// into SeedAllyFpAt with idx fixed at 1, so both names live here and every caller (the bare-idx
/// SeedAllyFp callers in all three files, and ChoirTests' own SeedAllyFpAt callers) is unaffected.
/// </summary>
internal static class BandFixtures
{
    /// <summary>The 7-field body every SeedBandEntry copy set identically: level, brave, faith,
    /// maxHp (value + Readable), hp (value + Readable), gx, gy. Module-specific tails (a writable
    /// HP pair, a support bit, the dead-status byte, a writable crystal-hearts counter) are
    /// deliberately NOT here -- see the class doc.</summary>
    public static void SeedBandEntryCore(FakeSparseMemory mem, long addr,
        int hp, int maxHp, int lvl, int br, int fa, int gx, int gy)
    {
        mem.U8s[addr + Offsets.ALevel] = (byte)lvl;
        mem.U8s[addr + Offsets.ABrave] = (byte)br;
        mem.U8s[addr + Offsets.AFaith] = (byte)fa;
        mem.U16s[addr + Offsets.AMaxHp] = (ushort)maxHp;
        mem.MarkReadable(addr + Offsets.AMaxHp, 2);   // production reads n=2 (Band.Sanity.cs)
        mem.U16s[addr + Offsets.AHp] = (ushort)hp;
        mem.MarkReadable(addr + Offsets.AHp, 2);      // production reads n=2 (Band.Sanity.cs)
        mem.U8s[addr + Offsets.AGx] = (byte)gx;
        mem.U8s[addr + Offsets.AGy] = (byte)gy;
    }

    /// <summary>Seed the three victim fields (nameId, job, undead bit) at band slot
    /// <paramref name="slot"/>'s entry, marked Readable. Byte-identical fold of
    /// KillTrackerDeedTests.SeedVictimFields, KillTrackerTests.SeedVictimFields, and
    /// VictimProbeTests.SeedVictimFields.</summary>
    public static void SeedVictimFields(FakeSparseMemory m, int slot, ushort nameId, byte job, bool undead)
    {
        long addr = Band.Entry(slot);
        m.U16s[addr + Offsets.ANameId] = nameId;
        m.MarkReadable(addr + Offsets.ANameId, 2);   // production reads n=2 (VictimReader.cs)
        m.U8s[addr + Puppeteer.JobOff] = job;
        m.ReadableAddrs.Add(addr + Puppeteer.JobOff);
        m.U8s[addr + Offsets.ADeadStatus] = undead ? Offsets.AUndeadBit : (byte)0;
        m.ReadableAddrs.Add(addr + Offsets.ADeadStatus);
    }

    /// <summary>Plant the idx-th distinct static-array ally fingerprint (player slot
    /// EnemySlotMax + idx) so Band.AllyFingerprints recognizes a band unit with this
    /// (maxHp,lvl,br,fa) as a healable/protectable ally. Byte-identical fold of
    /// ChoirTests.SeedAllyFpAt.</summary>
    public static void SeedAllyFpAt(FakeSparseMemory mem, int idx, int mhp, int lvl, int br, int fa)
    {
        long slot = Offsets.ArrayReadBase + (long)(Offsets.EnemySlotMax + idx) * Offsets.ArrayStride;
        mem.MarkReadable(slot + Offsets.AMaxHp, 2);   // production reads n=2 (GrowthEngine.Signatures.cs, Band.cs)
        mem.U16s[slot + Offsets.AMaxHp] = (ushort)mhp;
        mem.U8s[slot + Offsets.ALevel] = (byte)lvl;
        mem.U8s[slot + Offsets.ABrave] = (byte)br;
        mem.U8s[slot + Offsets.AFaith] = (byte)fa;
    }

    /// <summary>Plant the FIRST static-array ally fingerprint (idx 1). Byte-identical fold of
    /// BenedictionTests.SeedAllyFp, ChoirTests.SeedAllyFp, and SanctuaryTests.SeedAllyFp -- all
    /// three were SeedAllyFpAt(mem, idx: 1, ...) in disguise.</summary>
    public static void SeedAllyFp(FakeSparseMemory mem, int mhp, int lvl, int br, int fa)
        => SeedAllyFpAt(mem, 1, mhp, lvl, br, fa);

    /// <summary>Plant static-array slot 0's ENEMY fingerprint so the enemy-side oracle
    /// (Band.EnemyFingerprints; Plague keeps its own same-shape scan over the same array)
    /// recognizes a band unit with this (maxHp,lvl,br,fa) as an enemy. LW-157 fold: the three
    /// private SeatEnemyFp copies in MaimTests, PlagueLevelDriftTests, and PuppeteerTests were
    /// byte-identical (verified diff; only the provenance comments differed), and KobuTests
    /// carried a fourth inline copy of the same body.</summary>
    public static void SeatEnemyFp(FakeSparseMemory mem, (int mhp, int lvl, int br, int fa) fp)
    {
        long slot = Offsets.ArrayReadBase;            // static-array slot 0 (enemy side)
        mem.MarkReadable(slot + Offsets.AMaxHp, 2);   // production reads n=2 (Band.cs Fingerprints)
        mem.U16s[slot + Offsets.AMaxHp] = (ushort)fp.mhp;
        mem.U8s[slot + Offsets.ALevel] = (byte)fp.lvl;
        mem.U8s[slot + Offsets.ABrave] = (byte)fp.br;
        mem.U8s[slot + Offsets.AFaith] = (byte)fp.fa;
    }
}
