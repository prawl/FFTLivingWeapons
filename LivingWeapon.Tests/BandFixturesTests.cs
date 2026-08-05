using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-149 stage I load-bearing pin. BandFixtures.cs folds three copy-pasted Seed* fixture
/// families (SeedVictimFields, the SeedBandEntry core, SeedAllyFp/SeedAllyFpAt) out of
/// KillTrackerDeedTests/KillTrackerTests/VictimProbeTests and
/// BenedictionTests/ChoirTests/SanctuaryTests. A fixture dedup is invisible to every existing
/// assertion right up until it marks one address too many or too few -- at which point it
/// silently changes a DIFFERENT test's premise (a negative test whose whole point is that some
/// address is NOT marked). So this file does not re-assert behavior; it pins the exact
/// ReadableAddrs/WritableAddrs SETS and the exact U8s/U16s VALUES the pre-dedup code produced,
/// for one representative call per family, against what the new shared fixture produces. "Exact"
/// means exact: a fixture that marks a superset (one extra byte) must fail here even though every
/// pre-existing test would keep passing.
///
/// Each OLD_* helper below is a verbatim copy of the pre-dedup helper body (or, for the
/// SeedBandEntry family, the 7-line core subset all three pre-dedup copies shared identically --
/// verified by direct diff before the fold; BandFixtures.cs doc has the audit
/// note). The tail-specific bits (Benediction's writable HP pair, Choir's support-bit tail,
/// Sanctuary's dead-bit/crystal-hearts tail) live on in each of those three test files' own THIN
/// private wrapper and are exercised by those files' existing tests, not duplicated here.
///
/// Proven non-vacuous 2026-08-04: SeedBandEntryCore_matches_old_shared_body_exactly was run RED
/// against a deliberately-mutated BandFixtures.SeedBandEntryCore that marked one extra address
/// (Offsets.AGx added to ReadableAddrs, an address the old body never touched), then GREEN again
/// once the mutation was reverted.
/// </summary>
public class BandFixturesTests
{
    private static void AssertExactMatch(FakeSparseMemory oldMem, FakeSparseMemory newMem)
    {
        Assert.True(oldMem.ReadableAddrs.SetEquals(newMem.ReadableAddrs),
            "ReadableAddrs sets differ between the old code path and the new fixture");
        Assert.True(oldMem.WritableAddrs.SetEquals(newMem.WritableAddrs),
            "WritableAddrs sets differ between the old code path and the new fixture");

        Assert.Equal(oldMem.U8s.Count, newMem.U8s.Count);
        foreach (var kv in oldMem.U8s)
            Assert.Equal(kv.Value, newMem.U8s[kv.Key]);

        Assert.Equal(oldMem.U16s.Count, newMem.U16s.Count);
        foreach (var kv in oldMem.U16s)
            Assert.Equal(kv.Value, newMem.U16s[kv.Key]);
    }

    // ---- Family 1: SeedVictimFields (KillTrackerDeedTests / KillTrackerTests / VictimProbeTests) ----

    /// <summary>Verbatim copy of the pre-dedup SeedVictimFields body (all three copies were
    /// byte-identical).</summary>
    private static void OldSeedVictimFields(FakeSparseMemory m, int slot, ushort nameId, byte job, bool undead)
    {
        long addr = Band.Entry(slot);
        m.U16s[addr + Offsets.ANameId] = nameId;
        m.ReadableAddrs.Add(addr + Offsets.ANameId);
        m.U8s[addr + Puppeteer.JobOff] = job;
        m.ReadableAddrs.Add(addr + Puppeteer.JobOff);
        m.U8s[addr + Offsets.ADeadStatus] = undead ? Offsets.AUndeadBit : (byte)0;
        m.ReadableAddrs.Add(addr + Offsets.ADeadStatus);
    }

    [Fact]
    public void SeedVictimFields_matches_old_body_exactly()
    {
        var oldMem = new FakeSparseMemory();
        var newMem = new FakeSparseMemory();

        // Representative call, lifted from KillTrackerDeedTests.Edge_snapshot_stored_per_slot.
        OldSeedVictimFields(oldMem, slot: 0, nameId: 918, job: 99, undead: true);
        BandFixtures.SeedVictimFields(newMem, slot: 0, nameId: 918, job: 99, undead: true);

        AssertExactMatch(oldMem, newMem);
    }

    // ---- Family 2: SeedBandEntry's shared core (Benediction/Choir/Sanctuary) ----

    /// <summary>Verbatim copy of the 7-line body all three pre-dedup SeedBandEntry copies shared
    /// identically (statement order normalized to Choir/Sanctuary's, verified semantically
    /// equivalent to Benediction's own order since every statement targets a distinct address).
    /// Deliberately excludes every tail (writable HP, support bit, dead bit, crystal hearts) --
    /// those are NOT part of the shared fixture and stay pinned by each file's own existing
    /// tests.</summary>
    private static void OldSeedBandEntryCore(FakeSparseMemory mem, long addr,
        int hp, int maxHp, int lvl, int br, int fa, int gx, int gy)
    {
        mem.U8s[addr + Offsets.ALevel] = (byte)lvl;
        mem.U8s[addr + Offsets.ABrave] = (byte)br;
        mem.U8s[addr + Offsets.AFaith] = (byte)fa;
        mem.U16s[addr + Offsets.AMaxHp] = (ushort)maxHp;
        mem.ReadableAddrs.Add(addr + Offsets.AMaxHp);
        mem.U16s[addr + Offsets.AHp] = (ushort)hp;
        mem.ReadableAddrs.Add(addr + Offsets.AHp);
        mem.U8s[addr + Offsets.AGx] = (byte)gx;
        mem.U8s[addr + Offsets.AGy] = (byte)gy;
    }

    [Fact]
    public void SeedBandEntryCore_matches_old_shared_body_exactly()
    {
        var oldMem = new FakeSparseMemory();
        var newMem = new FakeSparseMemory();
        long addr = Band.Entry(26);

        // Representative call, lifted from SanctuaryTests.BuildActive's bearer seed.
        OldSeedBandEntryCore(oldMem, addr, hp: 200, maxHp: 200, lvl: 30, br: 60, fa: 55, gx: 3, gy: 3);
        BandFixtures.SeedBandEntryCore(newMem, addr, hp: 200, maxHp: 200, lvl: 30, br: 60, fa: 55, gx: 3, gy: 3);

        AssertExactMatch(oldMem, newMem);
    }

    // ---- Family 3: SeedAllyFp (Benediction/Choir/Sanctuary) ----

    /// <summary>Verbatim copy of the pre-dedup SeedAllyFp body (all three copies were
    /// byte-identical; each was SeedAllyFpAt with idx fixed at 1).</summary>
    private static void OldSeedAllyFp(FakeSparseMemory mem, int mhp, int lvl, int br, int fa)
    {
        long slot = Offsets.ArrayReadBase + (long)(Offsets.EnemySlotMax + 1) * Offsets.ArrayStride;
        mem.ReadableAddrs.Add(slot + Offsets.AMaxHp);
        mem.U16s[slot + Offsets.AMaxHp] = (ushort)mhp;
        mem.U8s[slot + Offsets.ALevel] = (byte)lvl;
        mem.U8s[slot + Offsets.ABrave] = (byte)br;
        mem.U8s[slot + Offsets.AFaith] = (byte)fa;
    }

    [Fact]
    public void SeedAllyFp_matches_old_body_exactly()
    {
        var oldMem = new FakeSparseMemory();
        var newMem = new FakeSparseMemory();

        // Representative call, lifted from ChoirTests.BuildActive's ally-fingerprint seed.
        OldSeedAllyFp(oldMem, mhp: 150, lvl: 20, br: 50, fa: 55);
        BandFixtures.SeedAllyFp(newMem, mhp: 150, lvl: 20, br: 50, fa: 55);

        AssertExactMatch(oldMem, newMem);
    }
}
