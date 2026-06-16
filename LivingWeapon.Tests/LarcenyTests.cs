using System;
using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// The pure Larceny decisions: active-gating, enemy-only latching, highest-priority buff selection
/// against the band status bytes, global-turn expiry counting, and the per-wielder steal ledger. The
/// buff transfer itself is exercised through the proven Reraise/Invisible bits so extending coverage to
/// the marquee buffs is purely adding table rows once they're mapped live.
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
        // Only the +0x47 Invisible bit set -> Invisible is picked.
        var buff = LarcenyPolicy.Pick(off => off == Offsets.AInvisible ? Offsets.AInvisibleBit : (byte)0);
        Assert.NotNull(buff);
        Assert.Equal("Invisible", buff!.Value.Name);
        Assert.Equal(Offsets.AInvisible, buff.Value.Off);
        Assert.Equal(Offsets.AInvisibleBit, buff.Value.Mask);
    }

    [Fact]
    public void PickHonoursPriorityOrderWhenSeveralAreSet()
    {
        // Reraise (0x20) and Invisible (0x10) share +0x47; Reraise is listed first -> wins.
        var buff = LarcenyPolicy.Pick(off =>
            off == Offsets.AReraise ? (byte)(Offsets.AReraiseBit | Offsets.AInvisibleBit) : (byte)0);
        Assert.Equal("Reraise", buff!.Value.Name);
    }

    [Fact]
    public void ExpiryIsGlobalTurnsSinceTheSteal()
    {
        // The stolen buff fades after N GLOBAL turn-edges (any unit's turn -- TurnTracker.GlobalTurns),
        // NOT the wielder's own turns and NOT wall-clock: the player can park the wielder so its turns
        // never come (a buff held through 6 sat-out turns, live 2026-06-14), but the world's turn clock
        // keeps ticking, so the theft is always temporary.
        Assert.False(LarcenyPolicy.IsExpired(currentTurn: 0, stolenTurn: 0, turns: 3));   // just stolen
        Assert.False(LarcenyPolicy.IsExpired(2, 0, 3));   // 2 turns elapsed -- not yet
        Assert.True(LarcenyPolicy.IsExpired(3, 0, 3));     // term reached
        Assert.True(LarcenyPolicy.IsExpired(9, 0, 3));     // well past
        Assert.False(LarcenyPolicy.IsExpired(5, 4, 3));   // stolen mid-battle at turn 4: 1 elapsed
        Assert.True(LarcenyPolicy.IsExpired(7, 4, 3));     // stolen at 4, now 7: 3 elapsed -> faded
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
        var invis = (Offsets.AInvisible, Offsets.AInvisibleBit);
        st.Steal(reraise, stolenTurn: 3);
        st.Steal(invis, stolenTurn: 4);
        Assert.Equal(2, st.Held.Count);

        st.Release(reraise);
        Assert.False(st.IsHeld(reraise));
        Assert.True(st.IsHeld(invis));

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
        var invis   = (Offsets.AInvisible, Offsets.AInvisibleBit);

        // Foe 1 has Reraise -> Steal.
        Assert.Equal(LarcenyAction.Steal, LarcenyPolicy.Decide(ledger.IsHeld(reraise), wielderHasBuff: false));
        ledger.Steal(reraise, stolenTurn: 0);
        // Foe 2 has a DIFFERENT buff -> independent ledger key -> also a Steal.
        Assert.Equal(LarcenyAction.Steal, LarcenyPolicy.Decide(ledger.IsHeld(invis), wielderHasBuff: false));
        ledger.Steal(invis, stolenTurn: 0);

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
}
