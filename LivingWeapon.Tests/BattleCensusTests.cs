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
    public void Census_roster_pool_entries_carry_brave_and_faith()
    {
        // LW-56 stage 2: brave+faith ride alongside nameId+level so a fingerprint rescue's
        // fp=L{level}B{brave}F{faith} can be cross-checked against the roster row a tape names.
        var m = new FakeSparseMemory();
        string? payload = null;
        var census = new BattleCensus(m, (type, p) => { if (type == "census") payload = p; });

        MemSeats.SeatRoster(m, slot: 0, lvl: 99, br: 89, fa: 76, rh: 80, nameId: 1);

        census.Tick(true);

        Assert.NotNull(payload);
        int idx = payload!.IndexOf(" | roster ");
        Assert.True(idx >= 0, "payload must contain the ' | roster ' separator");
        string rosterPart = payload.Substring(idx + " | roster ".Length);
        Assert.Contains("0:1L99B89F76", rosterPart);
    }

    [Fact]
    public void Census_truncation_headroom_keeps_the_last_roster_rows_hands_intact()
    {
        // LW-56 round 2: raised MaxPayloadChars (1400 -> 1800) when the roster part grew a raw
        // W{rHand},{lHand},{offHand} tail, so a fully-occupied census (max band + all RosterSlots
        // roster rows, every row now carrying its hand ids) never truncates away the LAST roster
        // row's hands. Seats rows symbolically, so the LW-96 window widening (20 -> 50) makes
        // this strictly harder, not stale.
        var m = new FakeSparseMemory();
        string? payload = null;
        var census = new BattleCensus(m, (type, p) => { if (type == "census") payload = p; });

        for (int s = 0; s < Offsets.BandSlots; s++)
            MemSeats.SeatBand(m, s, weapon: 0, lvl: 50, br: 50, fa: 50, gx: 1, gy: 1);
        for (int s = 0; s < Offsets.RosterSlots - 1; s++)
            MemSeats.SeatRoster(m, slot: s, lvl: 99, br: 89, fa: 76, rh: 80, lh: 45, oh: 46, nameId: 1);
        MemSeats.SeatRoster(m, slot: Offsets.RosterSlots - 1, lvl: 99, br: 255, fa: 254, rh: 80, lh: 45, oh: 46, nameId: 999);

        census.Tick(true);

        Assert.NotNull(payload);
        Assert.DoesNotContain("...", payload);   // no truncation at all under the raised cap
        Assert.Contains($"{Offsets.RosterSlots - 1}:999L99B255F254W80,45,46", payload);
    }

    [Fact]
    public void Census_roster_pool_entries_carry_the_raw_hand_ids()
    {
        // LW-56 round 2: the raw u16 hand reads ride alongside nameId/level/brave/faith, sentinels
        // included exactly as memory held them, so the weapon-key rescue's wpn= field can be
        // cross-checked against the exact row it claims to have matched.
        var m = new FakeSparseMemory();
        string? payload = null;
        var census = new BattleCensus(m, (type, p) => { if (type == "census") payload = p; });

        MemSeats.SeatRoster(m, slot: 0, lvl: 99, br: 89, fa: 76, rh: 80, lh: 45, oh: 46, nameId: 1);

        census.Tick(true);

        Assert.NotNull(payload);
        int idx = payload!.IndexOf(" | roster ");
        Assert.True(idx >= 0, "payload must contain the ' | roster ' separator");
        string rosterPart = payload.Substring(idx + " | roster ".Length);
        Assert.Contains("0:1L99B89F76W80,45,46", rosterPart);
    }

    [Fact]
    public void Census_roster_pool_hand_sentinels_ride_through_unnormalized()
    {
        // An empty hand reads its sentinel (0xFFFF) raw, not normalized to 0: the exact evidence a
        // stale-vs-fresh roster row needs on tape.
        var m = new FakeSparseMemory();
        string? payload = null;
        var census = new BattleCensus(m, (type, p) => { if (type == "census") payload = p; });

        MemSeats.SeatRoster(m, slot: 0, lvl: 99, br: 89, fa: 76, rh: 80, nameId: 1);   // lh/oh default to 0xFFFF

        census.Tick(true);

        Assert.NotNull(payload);
        int idx = payload!.IndexOf(" | roster ");
        string rosterPart = payload.Substring(idx + " | roster ".Length);
        Assert.Contains("0:1L99B89F76W80,65535,65535", rosterPart);
    }

    [Fact]
    public void EmitExit_re_emits_after_tick_already_fired()
    {
        // LW-56 D11/A3: EmitExit bypasses the fired latch entirely, so a battle where Tick already
        // fired (coverage completed) produces a SECOND census record on the exit edge.
        var m = new FakeSparseMemory();
        var recorded = new List<(string type, string payload)>();
        var census = new BattleCensus(m, (type, payload) => recorded.Add((type, payload)));

        census.Tick(true);
        Assert.Single(recorded);

        census.EmitExit();

        Assert.Equal(2, recorded.Count);
        Assert.All(recorded, r => Assert.Equal("census", r.type));
    }

    [Fact]
    public void EmitExit_emits_even_when_tick_never_fired()
    {
        // Oracle coverage never completing (the LW-34 over-count) leaves Tick permanently inert
        // for a whole battle; EmitExit must still land a census on the exit edge regardless.
        var m = new FakeSparseMemory();
        var recorded = new List<(string type, string payload)>();
        var census = new BattleCensus(m, (type, payload) => recorded.Add((type, payload)));

        census.Tick(false);
        census.Tick(false);
        Assert.Empty(recorded);

        census.EmitExit();

        Assert.Single(recorded);
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
