using System;
using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// The pure Larceny decisions: active-gating, enemy-only latching, highest-priority buff selection
/// against the band status bytes, wielder-turn expiry counting, and the per-wielder steal ledger. The
/// buff transfer itself is exercised through the proven Reraise/Invisible bits so
/// extending coverage to the marquee buffs is purely adding table rows once they're mapped live.
/// </summary>
public class LarcenyTests
{
    [Fact]
    public void IsActiveRequiresTheFlagAndTheEarnedTier()
    {
        var sig = new WeaponSignature { AtTier = 3, LarcenyTurns = 3 };
        Assert.False(LarcenyPolicy.IsActive(sig, tier: 2));
        Assert.True(LarcenyPolicy.IsActive(sig, tier: 3));
        Assert.False(LarcenyPolicy.IsActive(new WeaponSignature { AtTier = 3 }, tier: 3));  // turns 0
        Assert.False(LarcenyPolicy.IsActive(null, tier: 3));
    }

    [Fact]
    public void OnlyEnemiesAreLatched()
    {
        Assert.True(LarcenyPolicy.ShouldLatch(isEnemy: true));
        Assert.False(LarcenyPolicy.ShouldLatch(isEnemy: false));
    }

    [Fact]
    public void PickReturnsNullWhenTheTargetHasNoStealableBuff()
    {
        Assert.Null(LarcenyPolicy.Pick(_ => 0x00));
    }

    [Fact]
    public void PickFindsAStealableBuffByItsBit()
    {
        // Only the +0x48/0x40 Regen bit set -> Regen is picked.
        var buff = LarcenyPolicy.Pick(off => off == Offsets.ARegen ? Offsets.ARegenBit : (byte)0);
        Assert.NotNull(buff);
        Assert.Equal("Regen", buff!.Value.Name);
        Assert.Equal(Offsets.ARegen, buff.Value.Off);
        Assert.Equal(Offsets.ARegenBit, buff.Value.Mask);
    }

    [Fact]
    public void PickHonoursPriorityOrderWhenSeveralAreSet()
    {
        // Reraise outranks Regen (listed first wins) even when both bits are set.
        var top = LarcenyPolicy.Pick(off =>
            off == Offsets.AReraise ? Offsets.AReraiseBit
          : off == Offsets.ARegen ? Offsets.ARegenBit : (byte)0);
        Assert.Equal("Reraise", top!.Value.Name);

        // With no Reraise, Regen is picked.
        var mid = LarcenyPolicy.Pick(off => off == Offsets.ARegen ? Offsets.ARegenBit : (byte)0);
        Assert.Equal("Regen", mid!.Value.Name);
    }

    [Fact]
    public void ExpiryIsWielderTurnsSinceTheSteal()
    {
        // The stolen buff fades after N of the WIELDER's OWN completed turns (TurnTracker.Turns for the
        // wielder's fingerprint -- the proven acted-edge counter; the global-turn clock it replaced did
        // not expire the buff in a normal fight). A deployed wielder always takes turns, so the count
        // always advances -- no wall-clock backstop.
        Assert.False(LarcenyPolicy.IsExpired(currentTurn: 0, stolenTurn: 0, turns: 3));   // just stolen
        Assert.False(LarcenyPolicy.IsExpired(2, 0, 3));   // 2 turns elapsed -- not yet
        Assert.True(LarcenyPolicy.IsExpired(3, 0, 3));     // term reached
        Assert.True(LarcenyPolicy.IsExpired(9, 0, 3));     // well past
        Assert.False(LarcenyPolicy.IsExpired(5, 4, 3));   // stolen mid-battle at turn 4: 1 elapsed
        Assert.True(LarcenyPolicy.IsExpired(7, 4, 3));     // stolen at 4, now 7: 3 elapsed -> faded
        // Stale baseline: a new battle reset the wielder-turn count to 0 under a buff stolen at turn 7
        // in the prior fight -> drop it immediately (the carryover would never expire otherwise).
        Assert.True(LarcenyPolicy.IsExpired(currentTurn: 0, stolenTurn: 7, turns: 3));
    }

    [Fact]
    public void StealLedgerHoldsTheStealTurnAndNeverResetsAnActiveHold()
    {
        var st = new LarcenyState();
        var reraise = (Offsets.AReraise, Offsets.AReraiseBit);
        Assert.False(st.IsHeld(reraise));

        st.Steal(reraise, stolenTurn: 5);
        Assert.True(st.IsHeld(reraise));
        Assert.Equal(5, st.StolenAt(reraise));

        st.Steal(reraise, stolenTurn: 12);   // re-steal while held: the baseline must NOT move
        Assert.Equal(5, st.StolenAt(reraise));
    }

    [Fact]
    public void StealLedgerTracksSeveralBuffsAndReleasesThemIndependently()
    {
        var st = new LarcenyState();
        var reraise = (Offsets.AReraise, Offsets.AReraiseBit);
        var regen = (Offsets.ARegen, Offsets.ARegenBit);
        st.Steal(reraise, stolenTurn: 3);
        st.Steal(regen, stolenTurn: 4);
        Assert.Equal(2, st.Held.Count);

        st.Release(reraise);
        Assert.False(st.IsHeld(reraise));
        Assert.True(st.IsHeld(regen));

        st.Clear();
        Assert.Empty(st.Held);
    }

    // ── Guarded bit ops through the real RPM/WPM path (pinned in-process buffers stand in for the
    //    enemy/wielder band entries, exactly like MaimTests). ──
    private static readonly LiveMemory Live = new();

    [Fact]
    public void HasBitReadsTheStatusBit()
    {
        using var unit = PinnedBuf.Of(256);
        Assert.False(LarcenyPolicy.HasBit(Live, unit.Addr, Offsets.AReraise, Offsets.AReraiseBit));
        unit.Bytes[Offsets.AReraise] = Offsets.AReraiseBit;
        Assert.True(LarcenyPolicy.HasBit(Live, unit.Addr, Offsets.AReraise, Offsets.AReraiseBit));
    }

    [Fact]
    public void SetBitOrsTheBitInWithoutClobberingNeighbours()
    {
        using var unit = PinnedBuf.Of(256);
        unit.Bytes[Offsets.AReraise] = Offsets.AInvisibleBit;   // a different bit in the same byte is already set
        LarcenyPolicy.SetBit(Live, unit.Addr, Offsets.AReraise, Offsets.AReraiseBit);
        Assert.Equal((byte)(Offsets.AReraiseBit | Offsets.AInvisibleBit), unit.Bytes[Offsets.AReraise]);
    }

    [Fact]
    public void ClearBitClearsOnlyItsOwnBit()
    {
        using var unit = PinnedBuf.Of(256);
        unit.Bytes[Offsets.AReraise] = (byte)(Offsets.AReraiseBit | Offsets.AInvisibleBit);
        LarcenyPolicy.ClearBit(Live, unit.Addr, Offsets.AReraise, Offsets.AReraiseBit);
        Assert.Equal(Offsets.AInvisibleBit, unit.Bytes[Offsets.AReraise]);   // Invisible survives
    }

    [Fact]
    public void StealTransfersTheBitFromFoeToWielder()
    {
        // The end-to-end transfer: strip the foe's bit, grant it to the wielder.
        using var foe = PinnedBuf.Of(256);
        using var wielder = PinnedBuf.Of(256);
        foe.Bytes[Offsets.AReraise] = Offsets.AReraiseBit;   // the foe has Reraise

        LarcenyPolicy.ClearBit(Live, foe.Addr, Offsets.AReraise, Offsets.AReraiseBit);
        LarcenyPolicy.SetBit(Live, wielder.Addr, Offsets.AReraise, Offsets.AReraiseBit);

        Assert.Equal(0, foe.Bytes[Offsets.AReraise]);                       // taken from the foe
        Assert.Equal(Offsets.AReraiseBit, wielder.Bytes[Offsets.AReraise]); // worn by the wielder
    }

    // ── Multi-target sweep: one action damages several buffed foes. The runtime applies Decide once
    //    per struck foe, latching on Steal -- so these lock the DECISION SEQUENCE across the sweep.
    //    (The byte-level strip/grant of a single transfer is locked above; one byte-level sweep test
    //    below confirms a skipped duplicate is genuinely left unstripped.) ──

    [Fact]
    public void DecideMapsHeldAndWielderHasToTheStruckFoeAction()
    {
        // already stole this buff -> leave duplicate foes' copies alone (Skip); "held" wins even if
        // the bit somehow reads clear (defensive).
        Assert.Equal(LarcenyAction.Skip,   LarcenyPolicy.Decide(alreadyHeld: true,  wielderHasBuff: true));
        Assert.Equal(LarcenyAction.Skip,   LarcenyPolicy.Decide(alreadyHeld: true,  wielderHasBuff: false));
        // the wielder already owns it (its OWN buff) -> strip the foe but never latch (Dispel).
        Assert.Equal(LarcenyAction.Dispel, LarcenyPolicy.Decide(alreadyHeld: false, wielderHasBuff: true));
        // a free buff -> strip the foe + grant + latch on the wielder (Steal).
        Assert.Equal(LarcenyAction.Steal,  LarcenyPolicy.Decide(alreadyHeld: false, wielderHasBuff: false));
    }

    [Fact]
    public void SweepOfTwoFoesWithTheSameBuffStealsOnlyOneAndSkipsTheRest()
    {
        var ledger = new LarcenyState();
        var reraise = (Offsets.AReraise, Offsets.AReraiseBit);
        bool wielderHas = false;   // the wielder starts without the buff

        // Foe 1: not held + wielder lacks it -> Steal (the steal grants the bit and latches it).
        Assert.Equal(LarcenyAction.Steal, LarcenyPolicy.Decide(ledger.IsHeld(reraise), wielderHas));
        ledger.Steal(reraise, stolenTurn: 0); wielderHas = true;

        // Foes 2 and 3 in the SAME sweep carry the SAME buff -> already held -> Skip each.
        Assert.Equal(LarcenyAction.Skip, LarcenyPolicy.Decide(ledger.IsHeld(reraise), wielderHas));
        Assert.Equal(LarcenyAction.Skip, LarcenyPolicy.Decide(ledger.IsHeld(reraise), wielderHas));
        Assert.Single(ledger.Held);   // exactly one buff lifted from the whole sweep
    }

    [Fact]
    public void SweepOfTwoFoesWithDifferentBuffsStealsBoth()
    {
        var ledger = new LarcenyState();
        var reraise = (Offsets.AReraise, Offsets.AReraiseBit);
        var regen   = (Offsets.ARegen, Offsets.ARegenBit);

        // Foe 1 has Reraise -> Steal.
        Assert.Equal(LarcenyAction.Steal, LarcenyPolicy.Decide(ledger.IsHeld(reraise), wielderHasBuff: false));
        ledger.Steal(reraise, stolenTurn: 0);
        // Foe 2 has a DIFFERENT buff -> independent ledger key -> also a Steal.
        Assert.Equal(LarcenyAction.Steal, LarcenyPolicy.Decide(ledger.IsHeld(regen), wielderHasBuff: false));
        ledger.Steal(regen, stolenTurn: 0);

        Assert.Equal(2, ledger.Held.Count);   // the wielder wears both stolen buffs at once
    }

    [Fact]
    public void SweepDispelsEveryDuplicateWhenTheWielderAlreadyOwnsTheBuff()
    {
        // The wielder has its OWN Reraise (never stolen -> never in the ledger). Every struck Reraise
        // foe is DISPELLED: its bit is stripped but nothing is latched, so expiry can never clear the
        // wielder's own enchantment.
        var ledger = new LarcenyState();
        var reraise = (Offsets.AReraise, Offsets.AReraiseBit);
        const bool wielderHas = true;

        Assert.Equal(LarcenyAction.Dispel, LarcenyPolicy.Decide(ledger.IsHeld(reraise), wielderHas));
        Assert.Equal(LarcenyAction.Dispel, LarcenyPolicy.Decide(ledger.IsHeld(reraise), wielderHas));  // foe 2: also dispelled
        Assert.Empty(ledger.Held);   // nothing stolen -> nothing to fade off the wielder
    }

    [Fact]
    public void SweepLeavesTheSecondSameBuffFoeUnstrippedAtTheByteLevel()
    {
        // Two foes both carry Reraise; the wielder has none. The sweep steals from the FIRST and,
        // finding the buff already held, leaves the SECOND foe's Reraise untouched (Skip == no strip).
        using var foe1 = PinnedBuf.Of(256);
        using var foe2 = PinnedBuf.Of(256);
        using var wielder = PinnedBuf.Of(256);
        foe1.Bytes[Offsets.AReraise] = Offsets.AReraiseBit;
        foe2.Bytes[Offsets.AReraise] = Offsets.AReraiseBit;
        var ledger = new LarcenyState();
        var reraise = (Offsets.AReraise, Offsets.AReraiseBit);

        // Foe 1 -> Steal: strip foe 1, grant + latch on the wielder.
        Assert.Equal(LarcenyAction.Steal, LarcenyPolicy.Decide(ledger.IsHeld(reraise),
            LarcenyPolicy.HasBit(Live, wielder.Addr, Offsets.AReraise, Offsets.AReraiseBit)));
        LarcenyPolicy.ClearBit(Live, foe1.Addr, Offsets.AReraise, Offsets.AReraiseBit);
        LarcenyPolicy.SetBit(Live, wielder.Addr, Offsets.AReraise, Offsets.AReraiseBit);
        ledger.Steal(reraise, stolenTurn: 0);

        // Foe 2 -> Skip: the buff is already held, so the loop's `continue` means NO strip.
        Assert.Equal(LarcenyAction.Skip, LarcenyPolicy.Decide(ledger.IsHeld(reraise),
            LarcenyPolicy.HasBit(Live, wielder.Addr, Offsets.AReraise, Offsets.AReraiseBit)));

        Assert.Equal(0, foe1.Bytes[Offsets.AReraise]);                       // foe 1 lost it
        Assert.Equal(Offsets.AReraiseBit, foe2.Bytes[Offsets.AReraise]);     // foe 2 KEEPS it
        Assert.Equal(Offsets.AReraiseBit, wielder.Bytes[Offsets.AReraise]);  // the wielder wears one copy
        Assert.Single(ledger.Held);
    }

    // ── LW-158: the stateful Observe-driven steal pin. Everything above drives the pure
    //    LarcenyPolicy statics; nothing exercised Larceny.Tick's own detection path, which rides
    //    the shared HpDeltaState HP-drop core (HpDeltaState.cs, subclass RicochetState). That core
    //    had five pinned consumers and this unpinned sixth: a dead-Observe sabotage turned 41
    //    tests red across five sibling suites while Larceny broke silently. These pins stage the
    //    full trigger (tier gate, acting-main-hand + Acted gates, wielder locate, band walk,
    //    enemy-fingerprint filter) on a FakeSparseMemory, Kobu-style (KobuTests.Build -- the same
    //    strike-detection family), and assert the steal's real observable: the buff bit LEAVING
    //    the struck foe and LANDING on the wielder. ──

    private const int ArcanumId = 30;

    private static (Larceny larceny, FakeSparseMemory mem, long wielderEntry, long enemyEntry)
        BuildStateful(int wielderSlot = 24, int enemySlot = 20)
    {
        var mem = new FakeSparseMemory();
        var meta = new Dictionary<int, WeaponMeta>
        {
            [ArcanumId] = new WeaponMeta
            {
                Name = "Arcanum", Wp = 12, Cat = "Ninja Blade", Formula = 1,
                Flavor = "blade of forbidden sigils",
                Signature = new WeaponSignature { AtTier = 3, LarcenyTurns = 3, DisplayLabel = "Larceny" }
            }
        };
        var kills = new Dictionary<int, int> { [ArcanumId] = Tuning.ProdThresholds[2] };  // tier 3
        var tracker = new KillTracker(new Dictionary<int, int>(), mem, new HashSet<int>());
        tracker._lastPlayerMainHand = ArcanumId;   // the wielder is the last to act (acting-main-hand gate)
        tracker._lastActorFp = (30, 70, 60);       // the per-period fingerprint latch names the wielder
        mem.U8s[Offsets.Acted] = 1;                // and it is acting this turn
        mem.MarkReadable(Offsets.Acted, 1);        // production guards the acted read (Larceny.cs, the actedByte Readable pre-filter)

        // Wielder: roster slot + band entry (Wielder.Locate resolves the acting fingerprint to
        // this band address; SeatRoster's nameId 0 keeps the tier-2 fingerprint-scan path).
        long wielder = Band.Entry(wielderSlot);
        MemSeats.SeatRoster(mem, 0, lvl: 30, br: 70, fa: 60, rh: ArcanumId);
        MemSeats.SeatBand(mem, wielderSlot, weapon: ArcanumId, lvl: 30, br: 70, fa: 60,
                          gx: 2, gy: 2, hp: 200, maxHp: 300);
        mem.MarkReadable(wielder + Offsets.AMaxHp, 2);   // production reads n=2 (Band.Sanity.cs)
        mem.MarkReadable(wielder + Offsets.AHp, 2);      // production reads n=2 (Band.Sanity.cs)
        // The steal's grant target: SetBit pre-filters Readable(a,1) && Writable(a,1)
        // (LarcenyPolicy.SetBit); HasBit needs the same 1-byte read.
        mem.MarkReadable(wielder + Offsets.AReraise, 1);
        mem.MarkWritable(wielder + Offsets.AReraise, 1);

        // Struck enemy: band entry visible in the band walk, carrying a holdable Reraise buff.
        long enemy = Band.Entry(enemySlot);
        MemSeats.SeatBand(mem, enemySlot, weapon: 0, lvl: 40, br: 75, fa: 55,
                          gx: 4, gy: 5, hp: 200, maxHp: 400);
        mem.MarkReadable(enemy + Offsets.AMaxHp, 2);   // production reads n=2 (Band.Sanity.cs)
        mem.MarkReadable(enemy + Offsets.AHp, 2);      // production reads n=2 (Band.Sanity.cs)
        // The pre-hit buff snapshot reads each Stealable offset behind a 1-byte Readable guard
        // (Larceny.cs, the Pick lambda); the strip is ClearBit's Readable+Writable pre-filter.
        mem.U8s[enemy + Offsets.AReraise] = Offsets.AReraiseBit;
        mem.MarkReadable(enemy + Offsets.AReraise, 1);
        mem.MarkWritable(enemy + Offsets.AReraise, 1);

        // Static-array enemy fingerprint at slot 0 (Band.EnemyFingerprints, the enemy oracle --
        // same seeding as KobuTests.Build / MaimTests.SeatEnemyFp).
        long arrSlot = Offsets.ArrayReadBase;
        mem.MarkReadable(arrSlot + Offsets.AMaxHp, 2);   // production reads n=2 (Band.cs, the Fingerprints sweep)
        mem.U16s[arrSlot + Offsets.AMaxHp] = 400;
        mem.U8s[arrSlot + Offsets.ALevel]  = 40;
        mem.U8s[arrSlot + Offsets.ABrave]  = 75;
        mem.U8s[arrSlot + Offsets.AFaith]  = 55;

        var larceny = new Larceny(meta, kills, tracker, new TurnTracker(mem), mem);
        return (larceny, mem, wielder, enemy);
    }

    [Fact]
    public void Tick_steals_the_struck_foes_buff_onto_the_wielder_across_the_hp_drop()
    {
        var (larceny, mem, wielderEntry, enemy) = BuildStateful();

        larceny.Tick(onField: true);              // tick 1: baseline enemy HP 200 + pre-hit buff snapshot
        mem.U16s[enemy + Offsets.AHp] = 160;      // hit: HP dropped 40
        larceny.Tick(onField: true);              // tick 2: Observe reports the drop -> the steal fires

        // The steal's observable effect is the bit TRANSFER: stripped off the struck foe...
        Assert.True(mem.Written.ContainsKey(enemy + Offsets.AReraise),
            "the struck foe's Reraise bit must be stripped (LarcenyPolicy.ClearBit write)");
        Assert.Equal(0, mem.U8s[enemy + Offsets.AReraise] & Offsets.AReraiseBit);
        // ...and granted + held on the wielder.
        Assert.True(mem.Written.ContainsKey(wielderEntry + Offsets.AReraise),
            "the wielder must be granted the stolen Reraise bit (LarcenyHoldings.Steal -> SetBit write)");
        Assert.Equal(Offsets.AReraiseBit,
            mem.U8s[wielderEntry + Offsets.AReraise] & Offsets.AReraiseBit);
    }

    [Fact]
    public void Tick_steals_nothing_while_the_foes_hp_holds_steady()
    {
        // The other direction of the same pin: with every gate open but NO HP drop, Observe stays
        // silent and neither side's status byte is ever written.
        var (larceny, mem, wielderEntry, enemy) = BuildStateful();

        larceny.Tick(onField: true);   // baseline
        larceny.Tick(onField: true);   // same HP re-read: not an event
        larceny.Tick(onField: true);

        Assert.False(mem.Written.ContainsKey(enemy + Offsets.AReraise),
            "no HP drop -- the foe's buff must not be stripped");
        Assert.False(mem.Written.ContainsKey(wielderEntry + Offsets.AReraise),
            "no HP drop -- the wielder must not be granted anything");
    }
}
