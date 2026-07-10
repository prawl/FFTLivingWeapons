using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// Reliquary P2 probe instrumentation (docs/RELIQUARY_AC.md): BattleCensus dumps every band unit's
/// (nameId, job, lvl, br, fa, hp, mhp, gx, gy) and every occupied roster slot's nameId, once per
/// battle, on the first true reading of EnemyOracle.CoverageDone. These tests lock in:
///   - the once-per-battle fire gate (the load-bearing test -- this is what keeps the probe from
///     flooding the log/flight ring every tick once coverage is done);
///   - band + roster identity fields land in the recorder payload;
///   - invalid band slots are skipped;
///   - ResetBattle re-arms the gate;
///   - unreadable/unseeded memory never throws.
/// Mirrors the fake-memory band/roster seeding idiom from VictimProbeTests.cs.
/// </summary>
public class BattleCensusTests
{
    /// <summary>Seed a band slot with a valid identity (Band.IsValid) plus the two guarded probe
    /// fields (nameId, job) marked Readable -- mirrors VictimProbeTests.SeedVictimFields.</summary>
    private static void SeedBandSlot(FakeSparseMemory m, int slot, ushort nameId, byte job,
                                      int lvl, int br, int fa, int hp, int maxHp, int gx, int gy)
    {
        MemSeats.SeatBand(m, slot, weapon: 0, lvl: lvl, br: br, fa: fa, gx: gx, gy: gy, hp: hp, maxHp: maxHp);
        long addr = Band.Entry(slot);
        m.U16s[addr + Offsets.ANameId] = nameId;
        m.ReadableAddrs.Add(addr + Offsets.ANameId);
        m.U8s[addr + Puppeteer.JobOff] = job;
        m.ReadableAddrs.Add(addr + Puppeteer.JobOff);
    }

    [Fact]
    public void Census_fires_exactly_once_on_coverage_edge()
    {
        var m = new FakeSparseMemory();
        var recorded = new List<(string type, string payload)>();
        var census = new BattleCensus(m, (type, payload) => recorded.Add((type, payload)));

        for (int i = 0; i < 5; i++) census.Tick(false);
        Assert.Empty(recorded);

        census.Tick(true);
        Assert.Single(recorded);

        for (int i = 0; i < 5; i++) census.Tick(true);
        Assert.Single(recorded);
    }

    [Fact]
    public void Census_reads_band_identity_fields()
    {
        var m = new FakeSparseMemory();
        string? payload = null;
        var census = new BattleCensus(m, (type, p) => { if (type == "census") payload = p; });

        SeedBandSlot(m, slot: 25, nameId: 935, job: 104, lvl: 32, br: 68, fa: 55, hp: 438, maxHp: 438, gx: 6, gy: 7);

        census.Tick(true);

        Assert.NotNull(payload);
        Assert.Contains("s25:935/104", payload);
    }

    [Fact]
    public void Census_includes_roster_pool()
    {
        var m = new FakeSparseMemory();
        string? payload = null;
        var census = new BattleCensus(m, (type, p) => { if (type == "census") payload = p; });

        MemSeats.SeatRoster(m, slot: 0, lvl: 99, br: 89, fa: 76, rh: 80, nameId: 1);

        census.Tick(true);

        Assert.NotNull(payload);
        int idx = payload!.IndexOf(" | roster ");
        Assert.True(idx >= 0, "payload must contain the ' | roster ' separator");
        string rosterPart = payload.Substring(idx + " | roster ".Length);
        Assert.Contains("0:1", rosterPart);
    }

    [Fact]
    public void Census_roster_pool_entries_carry_the_slots_level()
    {
        // LW-56: a stale roster is visible on tape by nameId AND level together (the incident's
        // stale roster would show slot0 level 99 where a fresh opener roster shows level 1).
        var m = new FakeSparseMemory();
        string? payload = null;
        var census = new BattleCensus(m, (type, p) => { if (type == "census") payload = p; });

        MemSeats.SeatRoster(m, slot: 0, lvl: 99, br: 89, fa: 76, rh: 80, nameId: 1);

        census.Tick(true);

        Assert.NotNull(payload);
        int idx = payload!.IndexOf(" | roster ");
        Assert.True(idx >= 0, "payload must contain the ' | roster ' separator");
        string rosterPart = payload.Substring(idx + " | roster ".Length);
        Assert.Contains("0:1L99", rosterPart);
    }

    [Fact]
    public void ResetBattle_rearms()
    {
        var m = new FakeSparseMemory();
        int fired = 0;
        var census = new BattleCensus(m, (type, p) => { if (type == "census") fired++; });

        census.Tick(true);
        Assert.Equal(1, fired);

        census.ResetBattle();
        census.Tick(true);

        Assert.Equal(2, fired);
    }

    [Fact]
    public void Unreadable_memory_never_throws()
    {
        var m = new FakeSparseMemory();   // nothing marked valid or Readable
        var census = new BattleCensus(m, recorder: null);

        var ex = Record.Exception(() => census.Tick(true));

        Assert.Null(ex);
    }

    [Fact]
    public void Census_skips_invalid_band_slots()
    {
        var m = new FakeSparseMemory();
        string? payload = null;
        var census = new BattleCensus(m, (type, p) => { if (type == "census") payload = p; });

        // Slot 3: nameId seeded, but lvl left at 0 (unseeded) -- Band.IsValid rejects lvl < 1,
        // so this slot must never reach the log/payload despite its nameId being readable.
        long addr = Band.Entry(3);
        m.U16s[addr + Offsets.ANameId] = 777;
        m.ReadableAddrs.Add(addr + Offsets.ANameId);

        census.Tick(true);

        Assert.NotNull(payload);
        Assert.DoesNotContain("777", payload);
    }
}
