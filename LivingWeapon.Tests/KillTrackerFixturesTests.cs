using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-160 load-bearing pin. KillTrackerFixtures.cs folds the seeding-helper family the nine
/// KillTracker.Poll-driving suites (KillTrackerTests, KillTrackerStampTests, KillTrackerDeedTests,
/// KillCreditCoverageTests, CounterAttributionTests, DelayedActorTests, CrossTurnSummonTests,
/// SummonerAttributionTests, VictimProbeTests) each hand-copied under the now-retired per-file
/// mirroring convention (owner call, 2026-08-08). Same rationale as BandFixturesTests.cs: a
/// fixture dedup is invisible to every existing assertion until it marks or writes one address
/// too many or too few, at which point it silently changes a DIFFERENT test's premise. So this
/// file pins the exact ReadableAddrs/WritableAddrs SETS and U8s/U16s VALUES the pre-fold helper
/// bodies produced against what the shared fixture produces, one representative call per family.
///
/// Each Old* helper below is a verbatim copy of a pre-fold body. Where the nine files carried two
/// SHAPES of the same helper (the five KillTrackerTests-lineage copies with gx/gy/team parameters,
/// and the four DelayedActorTests-lineage variants without them), the variant shape gets its own
/// pin proving it equals the canonical fixture at the variant's own parameter surface -- that is
/// the one place the fold claims two different source texts were equivalent, so it is the one
/// place a pin must hold the claim up.
///
/// Settle and AliveThenDead are deliberately NOT pinned here: they write no memory of their own
/// (Settle is n Poll(true) calls; AliveThenDead composes SetEnemy/SetUnit/Settle, all pinned or
/// trivial), and their behavior is exercised by every corpse-maturing test in the nine suites.
/// </summary>
public class KillTrackerFixturesTests
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

    // ---- Family 1: SetActive (all nine files) ----

    /// <summary>The pre-fold SetActive body (the five KillTrackerTests-lineage copies were
    /// byte-identical; the DelayedActorTests lineage hardcoded team 0 and CounterAttributionTests
    /// listed team after acted -- every cross-variant call site passes those by name, so one
    /// canonical body covers all nine).</summary>
    private static void OldSetActive(FakeSparseMemory m, int hp, int maxHp, int level, int team = 0, int acted = 1)
    {
        m.U16s[Offsets.TurnQueue + Offsets.TqTeam] = (ushort)team;
        m.U16s[Offsets.TurnQueue + Offsets.TqHp] = (ushort)hp;
        m.U16s[Offsets.TurnQueue + Offsets.TqMaxHp] = (ushort)maxHp;
        m.U16s[Offsets.TurnQueue + Offsets.TqLevel] = (ushort)level;
        m.U8s[Offsets.Acted] = (byte)acted;
    }

    [Fact]
    public void SetActive_matches_old_body_exactly()
    {
        var oldMem = new FakeSparseMemory();
        var newMem = new FakeSparseMemory();

        // Representative call, lifted from CounterAttributionTests' enemy-turn edge (team 1, acted 1).
        OldSetActive(oldMem, hp: 150, maxHp: 250, level: 20, team: 1, acted: 1);
        KillTrackerFixtures.SetActive(newMem, hp: 150, maxHp: 250, level: 20, team: 1, acted: 1);

        AssertExactMatch(oldMem, newMem);
    }

    // ---- Family 2: SetArrayEnemy (KillTracker/Stamp/Deed/Coverage/VictimProbe standalone;
    //      inlined in the DelayedActor-lineage SetEnemy variants) ----

    /// <summary>The pre-fold SetArrayEnemy body (all five standalone copies byte-identical).</summary>
    private static void OldSetArrayEnemy(FakeSparseMemory m, int slot, int level, int brave, int faith, int maxHp,
                                         int inb = 1)
    {
        long s = Offsets.ArrayReadBase + (long)slot * Offsets.ArrayStride;
        m.U16s[s + Offsets.AInBattle] = (ushort)inb;
        m.U8s[s + Offsets.ALevel] = (byte)level;
        m.U8s[s + Offsets.ABrave] = (byte)brave;
        m.U8s[s + Offsets.AFaith] = (byte)faith;
        m.U16s[s + Offsets.AMaxHp] = (ushort)maxHp;
    }

    [Fact]
    public void SetArrayEnemy_matches_old_body_exactly()
    {
        var oldMem = new FakeSparseMemory();
        var newMem = new FakeSparseMemory();

        // Representative call, lifted from KillTrackerTests' capture-oracle seeding.
        OldSetArrayEnemy(oldMem, slot: 0, level: 10, brave: 50, faith: 50, maxHp: 400);
        KillTrackerFixtures.SetArrayEnemy(newMem, slot: 0, level: 10, brave: 50, faith: 50, maxHp: 400);

        AssertExactMatch(oldMem, newMem);
    }

    // ---- Family 3: SetEnemy, both source shapes ----

    /// <summary>The pre-fold DelayedActorTests-lineage SetEnemy variant (DelayedActor/CrossTurnSummon/
    /// Summoner/CounterAttribution), which INLINED the band seat and the static-array block instead
    /// of composing SetUnit + SetArrayEnemy the way the KillTrackerTests lineage does. This pin is
    /// the fold's central equivalence claim: two different source texts, one output.</summary>
    private static void OldVariantSetEnemy(FakeSparseMemory m, int bandSlot, int hp, int maxHp = 400,
                                           int level = 10, int brave = 50, int faith = 50)
    {
        MemSeats.SeatBand(m, bandSlot, weapon: 0, lvl: level, br: brave, fa: faith,
                          gx: 5, gy: 5, hp: hp, maxHp: maxHp);
        if (bandSlot <= Offsets.EnemySlotMax)
        {
            long s = Offsets.ArrayReadBase + (long)bandSlot * Offsets.ArrayStride;
            m.U16s[s + Offsets.AInBattle] = 1;
            m.U8s[s + Offsets.ALevel]     = (byte)level;
            m.U8s[s + Offsets.ABrave]     = (byte)brave;
            m.U8s[s + Offsets.AFaith]     = (byte)faith;
            m.U16s[s + Offsets.AMaxHp]    = (ushort)maxHp;
        }
    }

    [Fact]
    public void SetEnemy_variant_shape_matches_fixture_exactly_on_enemy_slot()
    {
        var oldMem = new FakeSparseMemory();
        var newMem = new FakeSparseMemory();

        // Representative call, lifted from CrossTurnSummonTests' second-victim seed.
        OldVariantSetEnemy(oldMem, bandSlot: 1, hp: 280, maxHp: 380, level: 12, brave: 55, faith: 45);
        KillTrackerFixtures.SetEnemy(newMem, slot: 1, hp: 280, maxHp: 380, level: 12, brave: 55, faith: 45);

        AssertExactMatch(oldMem, newMem);
    }

    [Fact]
    public void SetEnemy_variant_shape_matches_fixture_exactly_on_player_side_slot()
    {
        var oldMem = new FakeSparseMemory();
        var newMem = new FakeSparseMemory();

        // Player-side slot (> EnemySlotMax): the array block must NOT fire in either shape.
        OldVariantSetEnemy(oldMem, bandSlot: Offsets.SlotsBack, hp: 300);
        KillTrackerFixtures.SetEnemy(newMem, slot: Offsets.SlotsBack, hp: 300);

        AssertExactMatch(oldMem, newMem);
    }

    // ---- Family 4: PointAt (KillTracker/Stamp/Coverage/DelayedActor) ----

    /// <summary>The pre-fold PointAt body (all four copies byte-identical): seed Offsets.ActorPtr
    /// with band slot bandIdx's combat FRAME base.</summary>
    private static void OldPointAt(FakeSparseMemory m, int bandIdx) =>
        m.SeedU64(Offsets.ActorPtr, (ulong)(Offsets.FrameReadBase + (long)bandIdx * Offsets.CombatStride));

    [Fact]
    public void PointAt_matches_old_body_exactly()
    {
        var oldMem = new FakeSparseMemory();
        var newMem = new FakeSparseMemory();

        OldPointAt(oldMem, bandIdx: 21);
        KillTrackerFixtures.PointAt(newMem, bandIdx: 21);

        AssertExactMatch(oldMem, newMem);
    }

    // ---- Family 5: SetJumpBit (Stamp/CounterAttribution/DelayedActor/CrossTurnSummon/
    //      Summoner/KillTrackerTests) ----

    /// <summary>The pre-fold SetJumpBit body (all six copies byte-identical bar the slot
    /// parameter's name; every call site passes it positionally).</summary>
    private static void OldSetJumpBit(FakeSparseMemory m, int bandSlot, bool set = true)
    {
        long addr = Band.Entry(bandSlot);
        byte cur = m.U8s.TryGetValue(addr + Offsets.ADeadStatus, out var v) ? v : (byte)0;
        m.U8s[addr + Offsets.ADeadStatus] = set
            ? (byte)(cur | Offsets.AJumpBit)
            : (byte)(cur & ~Offsets.AJumpBit);
    }

    [Fact]
    public void SetJumpBit_set_preserves_existing_bits_exactly_like_old_body()
    {
        var oldMem = new FakeSparseMemory();
        var newMem = new FakeSparseMemory();

        // Pre-seed a foreign bit so the OR-set (not clobber) semantics are part of the pin.
        long addr = Band.Entry(20) + Offsets.ADeadStatus;
        oldMem.U8s[addr] = 0x80;
        newMem.U8s[addr] = 0x80;

        OldSetJumpBit(oldMem, 20, set: true);
        KillTrackerFixtures.SetJumpBit(newMem, 20, set: true);

        AssertExactMatch(oldMem, newMem);
    }

    [Fact]
    public void SetJumpBit_clear_matches_old_body_exactly()
    {
        var oldMem = new FakeSparseMemory();
        var newMem = new FakeSparseMemory();

        long addr = Band.Entry(20) + Offsets.ADeadStatus;
        oldMem.U8s[addr] = (byte)(Offsets.AJumpBit | 0x80);
        newMem.U8s[addr] = (byte)(Offsets.AJumpBit | 0x80);

        OldSetJumpBit(oldMem, 20, set: false);
        KillTrackerFixtures.SetJumpBit(newMem, 20, set: false);

        AssertExactMatch(oldMem, newMem);
    }

    // ---- Family 6: the MemSeats delegations (SetUnit / SetRoster / SetFrameNameId) ----
    // One pin for the widest delegation: the fixture must forward every parameter unchanged.
    // (SetRoster/SetFrameNameId forward to the same MemSeats seats the old one-line wrappers
    // did; SetUnit is the one whose parameter list is longest, so it is the one pinned.)

    [Fact]
    public void SetUnit_forwards_every_parameter_to_SeatBand_unchanged()
    {
        var oldMem = new FakeSparseMemory();
        var newMem = new FakeSparseMemory();

        // Representative call, lifted from KillTrackerTests' weapon-disambiguation seeding.
        MemSeats.SeatBand(oldMem, 21, weapon: 52, lvl: 99, br: 89, fa: 76,
                          gx: 7, gy: 9, hp: 352, maxHp: 352);
        KillTrackerFixtures.SetUnit(newMem, slot: 21, hp: 352, maxHp: 352, gx: 7, gy: 9,
                                    level: 99, brave: 89, faith: 76, weapon: 52);

        AssertExactMatch(oldMem, newMem);
    }
}
