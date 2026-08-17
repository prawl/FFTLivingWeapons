using System;
using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-252 stage 5, Part D: consumer edge tests pinning the REASON WielderKeyedStore's law
/// exists -- each one reproduces the exact hazard the store's resolution law (see its class doc)
/// is built to close. D4 (Iai_seeded_fp_twins...) is the stage's headline red: master shares one
/// HoldState between two seeded fp-twins; this stage closes it. D5 pins the deliberate mixed-pair
/// exception (WielderKeyedStore's own law: no readable identity on either side means nothing to
/// key them apart) as byte-identical to master. D1-D3 pin the transient-recovery guarantee (a
/// one-tick nameId dropout must never reset/duplicate/jump state) through three different
/// consumers: TurnTracker directly, then HealPulse and GrowthEngine.Afterimage, which both
/// depend on TurnTracker's query key matching its own credit key.
/// </summary>
public class WielderKeyedStoreConsumerTests
{
    // ---- shared turn-crediting helper (mirrors TurnTrackerTests.SeatFlagOwner + Acted edges) ----

    /// <summary>Credit <paramref name="count"/> real turns to a wielder at <paramref name="fp"/>
    /// via the flags lane (Band.FlagOwner), with that wielder's OWN frame nameId seeded to
    /// <paramref name="nameId"/> -- the same mechanism TurnTracker.Poll's credit site uses live.</summary>
    private static void CreditTurns(FakeSparseMemory mem, TurnTracker turns,
        (int lvl, int br, int fa) fp, int nameId, int count)
    {
        long slot = Offsets.BandReadBase + (long)5 * Offsets.CombatStride;
        mem.U16s[slot + Offsets.AMaxHp] = 100;
        mem.U8s[slot + Offsets.ALevel] = (byte)fp.lvl;
        mem.U8s[slot + Offsets.ABrave] = (byte)fp.br;
        mem.U8s[slot + Offsets.AFaith] = (byte)fp.fa;
        mem.U8s[slot + Offsets.AGx] = 5;
        mem.U8s[slot + Offsets.AGy] = 5;
        mem.U8s[slot + Offsets.ATurnFlag] = 1;
        mem.U16s[slot + Offsets.ANameId] = (ushort)nameId;
        for (int i = 0; i < count; i++)
        {
            mem.U8s[Offsets.Acted] = 0; turns.Poll();
            mem.U8s[Offsets.Acted] = 1; turns.Poll();   // rising edge -> +1
        }
    }

    // ---- D1: TurnTracker continuity across a transient nameId-0 query ----

    [Fact]
    public void TurnTracker_query_stays_continuous_across_a_transient_nameId_zero_tick()
    {
        const int NameId = 601;
        var fp = (lvl: 30, br: 65, fa: 60);
        var mem = new FakeSparseMemory();
        var turns = new TurnTracker(mem);
        CreditTurns(mem, turns, fp, NameId, count: 2);

        Assert.Equal(2, turns.Turns(NameId, fp.lvl, fp.br, fp.fa));
        // A transient tick reads nameId 0 (the fp lane): side-map recovery finds the SAME
        // (sole) registered state at this fp -- no reset, no drop.
        Assert.Equal(2, turns.Turns(0, fp.lvl, fp.br, fp.fa));
        // Querying with the real nameId again: unchanged, same object all along.
        Assert.Equal(2, turns.Turns(NameId, fp.lvl, fp.br, fp.fa));
    }

    // ---- D2: HealPulse must not fire a spurious pulse on the transient-0 recovery tick ----

    [Fact]
    public void HealPulse_transient_nameId_zero_tick_produces_no_spurious_turn_edge()
    {
        const int WeaponId = 61;   // Mending Staff (Renewal's config)
        const int NameId = 602;
        var fp = (lvl: 30, br: 65, fa: 60);
        var mem = new FakeSparseMemory();
        var turns = new TurnTracker(mem);
        var meta = new Dictionary<int, WeaponMeta>
        {
            [WeaponId] = new WeaponMeta
            {
                Name = "Mending Staff", Wp = 8, Cat = "Staff", Formula = 6, Flavor = "renewal staff",
                Signature = new WeaponSignature { AtTier = 3, RegenAuraRadius = 1, DisplayLabel = "Renewal" }
            }
        };
        var kills = new Dictionary<int, int> { [WeaponId] = Tuning.ProdThresholds[2] };
        const int wielderSlot = 24;
        long wielderEntry = Band.Entry(wielderSlot);
        MemSeats.SeatRoster(mem, 0, lvl: fp.lvl, br: fp.br, fa: fp.fa, rh: WeaponId, nameId: NameId);
        MemSeats.SeatBand(mem, wielderSlot, weapon: WeaponId, lvl: fp.lvl, br: fp.br, fa: fp.fa,
                          gx: 5, gy: 5, hp: 300, maxHp: 300);
        // An ally with a healable deficit within radius 1 (Chebyshev), positively identified as
        // a static-array PLAYER (Band.AllyFingerprints requires it -- HealPulse's class doc: "the
        // dead are never healed... positively identified against the static-array PLAYER slots"),
        // so a firing pulse is directly observable as an HP change.
        const int allySlot = 25;
        long allyEntry = Band.Entry(allySlot);
        BandFixtures.SeedAllyFpAt(mem, idx: 1, mhp: 300, lvl: 20, br: 50, fa: 50);
        MemSeats.SeatBand(mem, allySlot, weapon: 0, lvl: 20, br: 50, fa: 50, gx: 5, gy: 6, hp: 100, maxHp: 300);
        mem.MarkWritable(allyEntry + Offsets.AHp, 2);   // production writes n=2 (BandHeal.cs WriteHp)

        var renewal = new Renewal(meta, kills, turns, mem);

        renewal.Tick(true);   // activation + baseline (_lastTurns primes to 0, no edge)
        Assert.Equal(100, mem.U16s[allyEntry + Offsets.AHp]);   // no pulse yet

        CreditTurns(mem, turns, fp, NameId, count: 1);
        renewal.Tick(true);   // real turn edge: turns 0 -> 1, pulses
        int hpAfterRealPulse = mem.U16s[allyEntry + Offsets.AHp];
        Assert.True(hpAfterRealPulse > 100, "the genuine turn edge must pulse");

        // Transient: the roster row's OWN nameId glitches to 0 for exactly one tick (simulating
        // a Mem fail-safe read on Wielder.RosterNameId's roster scan).
        long rb = Offsets.RosterBase + 0 * Offsets.RosterStride;
        mem.U16s[rb + Offsets.RNameId] = 0;
        renewal.Tick(true);   // must recover the SAME turn count (1) via side-map -- no edge
        Assert.Equal(hpAfterRealPulse, mem.U16s[allyEntry + Offsets.AHp]);   // unchanged: no spurious pulse

        // Restore the real nameId; still no new turn, still no edge.
        mem.U16s[rb + Offsets.RNameId] = (ushort)NameId;
        renewal.Tick(true);
        Assert.Equal(hpAfterRealPulse, mem.U16s[allyEntry + Offsets.AHp]);

        // Continuity proof: a genuinely NEW turn after the transient still pulses correctly.
        CreditTurns(mem, turns, fp, NameId, count: 1);
        renewal.Tick(true);
        Assert.True(mem.U16s[allyEntry + Offsets.AHp] > hpAfterRealPulse, "counting must resume normally after the transient");
    }

    // ---- D3: Afterimage ramp must not jump on a transient-0 GrowthEngine tick ----

    [Fact]
    public void Afterimage_ramp_does_not_jump_on_a_transient_nameId_zero_tick()
    {
        const int NameId = 603;
        var fp = (lvl: 30, br: 65, fa: 60);
        var mem = new FakeSparseMemory();
        var turns = new TurnTracker(mem);
        var engine = new GrowthEngine(new Dictionary<int, WeaponMeta>(), new Dictionary<int, int>(), turns, mem);
        var m = new WeaponMeta
        {
            Name = "Swiftedge", Wp = 10, Cat = "Sword", Formula = 99, Flavor = "afterimage blade",
            Signature = new WeaponSignature { AtTier = 3, Afterimage = true, DisplayLabel = "Afterimage" }
        };
        const long structBase = 0x7200_0000;
        long addr = structBase + Offsets.CSpeed;
        mem.WritableAddrs.Add(addr);
        mem.U8s[addr] = 10;   // natural Speed

        CreditTurns(mem, turns, fp, NameId, count: 2);   // 2 real turns banked before the ramp ever looks

        engine.HoldAfterimage(structBase, m, tier: 3, level: fp.lvl, brave: fp.br, faith: fp.fa, rosterNameId: NameId);   // capture (Seeded=false, stacks stay 0)
        engine.HoldAfterimage(structBase, m, tier: 3, level: fp.lvl, brave: fp.br, faith: fp.fa, rosterNameId: NameId);   // observes turns=2 -> ramps by 2
        byte speedAfterRamp = mem.U8s[addr];
        int atCapSpeed = 10 + Tuning.AfterimageSpeedCap * Tuning.AfterimageSpeedPerTurn;
        Assert.True(speedAfterRamp > 10 && speedAfterRamp < atCapSpeed,
            "test precondition: a genuine partial (non-cap, non-zero) ramp");

        // Transient: the roster-side nameId GrowthEngine.Apply would have read this tick glitches
        // to 0 (simulated directly on the HoldAfterimage call, mirroring GrowthEngine.Apply's own
        // per-tick rb+RNameId read landing on a Mem fail-safe 0).
        engine.HoldAfterimage(structBase, m, tier: 3, level: fp.lvl, brave: fp.br, faith: fp.fa, rosterNameId: 0);

        Assert.Equal(speedAfterRamp, mem.U8s[addr]);   // unchanged -- side-map recovery, no jump, nowhere near cap
        Assert.NotEqual((byte)atCapSpeed, mem.U8s[addr]);
    }

    // ---- D4/D5: Iai twin split (the headline red) + the deliberate mixed-pair parity ----

    private const int AmeNoMurakumoId = 42;

    private static (FakeSparseMemory mem, Iai iai, long entryA, long entryB) BuildTwinPair(int nameIdA, int nameIdB)
    {
        var mem = new FakeSparseMemory();
        var meta = new Dictionary<int, WeaponMeta>
        {
            [AmeNoMurakumoId] = new WeaponMeta
            {
                Name = "Ame-no-Murakumo", Wp = 10, Cat = "Katana", Formula = 1, Flavor = "gathering-storm blade",
                Signature = new WeaponSignature { AtTier = 3, Iai = true, DisplayLabel = "Iai" }
            }
        };
        var kills = new Dictionary<int, int> { [AmeNoMurakumoId] = Tuning.ProdThresholds[2] };
        var fp = (lvl: 30, br: 65, fa: 60);
        const int slotA = 24, slotB = 25;
        long entryA = Band.Entry(slotA), entryB = Band.Entry(slotB);

        MemSeats.SeatRoster(mem, 0, lvl: fp.lvl, br: fp.br, fa: fp.fa, rh: AmeNoMurakumoId, nameId: nameIdA);
        MemSeats.SeatRoster(mem, 1, lvl: fp.lvl, br: fp.br, fa: fp.fa, rh: AmeNoMurakumoId, nameId: nameIdB);
        MemSeats.SeatBand(mem, slotA, weapon: AmeNoMurakumoId, lvl: fp.lvl, br: fp.br, fa: fp.fa,
                          gx: 2, gy: 2, hp: 200, maxHp: 300, speed: 8);    // A's own natural Speed
        MemSeats.SeatBand(mem, slotB, weapon: AmeNoMurakumoId, lvl: fp.lvl, br: fp.br, fa: fp.fa,
                          gx: 3, gy: 3, hp: 200, maxHp: 300, speed: 12);   // B's own natural Speed, DISTINCT
        if (nameIdA != 0) MemSeats.SeatFrameNameId(mem, slotA, nameIdA);
        if (nameIdB != 0) MemSeats.SeatFrameNameId(mem, slotB, nameIdB);
        mem.WritableAddrs.Add(entryA + Offsets.ASpeed);
        mem.WritableAddrs.Add(entryB + Offsets.ASpeed);

        var iai = new Iai(meta, kills, mem);
        return (mem, iai, entryA, entryB);
    }

    [Fact]
    public void Iai_seeded_fp_twins_get_independent_hold_states()
    {
        // [LW-252 D4, THE HEADLINE RED] Two roster rows share the SAME (level,brave,faith) but
        // carry DISTINCT seeded nameIds. Master: both collide on the fp-keyed _holds dictionary
        // -- whichever wielder's tick runs first (roster order: A) captures NaturalSpeed into
        // the SHARED HoldState, so B's hold target is computed from A's natural (8), not its own
        // (12): entryB would read 8, not 12. Fixed: independent HoldStates, one per nameId.
        var (mem, iai, entryA, entryB) = BuildTwinPair(nameIdA: 801, nameIdB: 802);

        iai.Tick(true, DateTime.UtcNow);

        Assert.Equal((byte)8, mem.U8s[entryA + Offsets.ASpeed]);    // A held at its OWN natural
        Assert.Equal((byte)12, mem.U8s[entryB + Offsets.ASpeed]);   // B held at ITS OWN natural -- not A's
    }

    [Fact]
    public void Iai_nameId_zero_pair_shares_one_hold_state_matching_todays_behavior()
    {
        // [LW-252 D5, parity] Two genuinely-unseeded (nameId 0) wielders sharing a fingerprint,
        // staggered so they are never SIMULTANEOUSLY band-resolvable (two real-position same-
        // weapon-same-fp entries at once is an unrelated, pre-existing Wielder.Locate ambiguity
        // refusal -- D4's build avoids it via nameId; this test avoids it by having A leave
        // before B arrives, the same way a real "wielder A benched, wielder B fielded" turnover
        // would look). Byte-identical to master: WielderKeyedStore's own law (no readable
        // identity on either side means nothing to key them apart) makes B's tick land on A's
        // ALREADY-CAPTURED state via the shared byFp entry.
        var mem = new FakeSparseMemory();
        var meta = new Dictionary<int, WeaponMeta>
        {
            [AmeNoMurakumoId] = new WeaponMeta
            {
                Name = "Ame-no-Murakumo", Wp = 10, Cat = "Katana", Formula = 1, Flavor = "gathering-storm blade",
                Signature = new WeaponSignature { AtTier = 3, Iai = true, DisplayLabel = "Iai" }
            }
        };
        var kills = new Dictionary<int, int> { [AmeNoMurakumoId] = Tuning.ProdThresholds[2] };
        var fp = (lvl: 30, br: 65, fa: 60);
        const int slotA = 24, slotB = 25, OtherWeapon = 99;
        long entryA = Band.Entry(slotA), entryB = Band.Entry(slotB);

        MemSeats.SeatRoster(mem, 0, lvl: fp.lvl, br: fp.br, fa: fp.fa, rh: AmeNoMurakumoId, nameId: 0);
        MemSeats.SeatBand(mem, slotA, weapon: AmeNoMurakumoId, lvl: fp.lvl, br: fp.br, fa: fp.fa,
                          gx: 2, gy: 2, hp: 200, maxHp: 300, speed: 8);   // A's own natural Speed
        mem.WritableAddrs.Add(entryA + Offsets.ASpeed);
        var iai = new Iai(meta, kills, mem);

        iai.Tick(true, DateTime.UtcNow);
        Assert.Equal((byte)8, mem.U8s[entryA + Offsets.ASpeed]);   // A captured its own natural

        // A leaves the field (roster row 0 switches weapons, its old band entry goes invalid);
        // B appears at a DIFFERENT roster row/band slot with the SAME fp, ALSO nameId 0, and its
        // OWN distinct natural Speed.
        mem.U16s[Offsets.RosterBase + 0 * Offsets.RosterStride + Offsets.RRHand] = OtherWeapon;
        mem.U8s[entryA + Offsets.ALevel] = 0;
        MemSeats.SeatRoster(mem, 1, lvl: fp.lvl, br: fp.br, fa: fp.fa, rh: AmeNoMurakumoId, nameId: 0);
        MemSeats.SeatBand(mem, slotB, weapon: AmeNoMurakumoId, lvl: fp.lvl, br: fp.br, fa: fp.fa,
                          gx: 3, gy: 3, hp: 200, maxHp: 300, speed: 12);   // B's own natural Speed, DISTINCT
        mem.WritableAddrs.Add(entryB + Offsets.ASpeed);

        iai.Tick(true, DateTime.UtcNow);

        // B inherits A's ALREADY-CAPTURED NaturalSpeed (8) via the shared byFp state -- NOT its
        // own (12) -- master's exact fp-only-dictionary behavior, preserved for this pair.
        Assert.Equal((byte)8, mem.U8s[entryB + Offsets.ASpeed]);
    }
}
