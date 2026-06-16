using System;
using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LarcenyHoldings: per-wielder steal ledger. Drives the bit ops through real RPM/WPM using
/// GCHandle-pinned buffers (same seam as LarcenyTests), with locate/turnsOf closures standing
/// in for the live band walk.
/// </summary>
public class LarcenyHoldingsTests
{
    private static readonly LiveMemory Live = new();

    // Two wielder fingerprints used across tests.
    private static readonly (int lvl, int br, int fa) FpA = (99, 89, 76);
    private static readonly (int lvl, int br, int fa) FpB = (50, 60, 55);

    // Buff keys used in tests.
    private static readonly (int off, byte mask) Reraise = (Offsets.AReraise, Offsets.AReraiseBit);
    private static readonly (int off, byte mask) Regen   = (Offsets.ARegen,   Offsets.ARegenBit);

    [Fact]
    public void Steal_sets_the_bit_on_the_right_wielders_buffer_and_IsHeld_true()
    {
        using var bufA = PinnedBuf.Of(256);
        var h = new LarcenyHoldings(Live);

        h.Steal(FpA, bufA.Addr, Reraise, stolenTurn: 0);

        Assert.True(h.IsHeld(FpA, Reraise));
        Assert.NotEqual(0, bufA.Bytes[Offsets.AReraise] & Offsets.AReraiseBit);
    }

    [Fact]
    public void Two_wielders_hold_the_same_buff_independently_on_their_own_buffers()
    {
        using var bufA = PinnedBuf.Of(256);
        using var bufB = PinnedBuf.Of(256);
        var h = new LarcenyHoldings(Live);

        h.Steal(FpA, bufA.Addr, Reraise, stolenTurn: 0);
        h.Steal(FpB, bufB.Addr, Reraise, stolenTurn: 0);

        // Both ledgers hold the buff.
        Assert.True(h.IsHeld(FpA, Reraise));
        Assert.True(h.IsHeld(FpB, Reraise));
        // Both buffers carry the bit.
        Assert.NotEqual(0, bufA.Bytes[Offsets.AReraise] & Offsets.AReraiseBit);
        Assert.NotEqual(0, bufB.Bytes[Offsets.AReraise] & Offsets.AReraiseBit);
    }

    [Fact]
    public void Drive_reasserts_a_held_bit_after_it_is_cleared_externally()
    {
        using var bufA = PinnedBuf.Of(256);
        var h = new LarcenyHoldings(Live);
        h.Steal(FpA, bufA.Addr, Reraise, stolenTurn: 0);

        // External clear (engine normalizes the status field).
        bufA.Bytes[Offsets.AReraise] = 0;
        Assert.Equal(0, bufA.Bytes[Offsets.AReraise] & Offsets.AReraiseBit);

        // Drive with a locate closure that returns bufA's address for FpA.
        h.Drive(fp => fp == FpA ? bufA.Addr : 0);

        Assert.NotEqual(0, bufA.Bytes[Offsets.AReraise] & Offsets.AReraiseBit);
    }

    [Fact]
    public void Expire_drops_buff_when_wielders_turns_reach_threshold_and_clears_its_bit()
    {
        using var bufA = PinnedBuf.Of(256);
        using var bufB = PinnedBuf.Of(256);
        var turnsA = 0; var turnsB = 0;
        var h = new LarcenyHoldings(Live);
        const int holdTurns = 3;

        // A steals at turn 0; B steals at turn 0 too.
        h.Steal(FpA, bufA.Addr, Reraise, stolenTurn: 0);
        h.Steal(FpB, bufB.Addr, Reraise, stolenTurn: 0);

        Func<(int, int, int), long>  locate   = fp => fp == FpA ? bufA.Addr : (fp == FpB ? bufB.Addr : 0);
        Func<(int, int, int), int>   turnsOf  = fp => fp == FpA ? turnsA : turnsB;

        // After 2 turns for A, nothing expires yet.
        turnsA = 2;
        h.Expire(locate, turnsOf, holdTurns);
        Assert.True(h.IsHeld(FpA, Reraise));
        Assert.True(h.IsHeld(FpB, Reraise));

        // After 3 turns for A: A's buff expires; B (still 0) keeps its buff.
        turnsA = 3;
        h.Expire(locate, turnsOf, holdTurns);

        Assert.False(h.IsHeld(FpA, Reraise));                                     // A's buff faded
        Assert.Equal(0, bufA.Bytes[Offsets.AReraise] & Offsets.AReraiseBit);      // bit cleared on A's buffer
        Assert.True(h.IsHeld(FpB, Reraise));                                      // B still holds it
        Assert.NotEqual(0, bufB.Bytes[Offsets.AReraise] & Offsets.AReraiseBit);   // B's bit intact
    }

    [Fact]
    public void Expire_removes_the_emptied_wielders_ledger_and_IsHeld_returns_false()
    {
        using var bufA = PinnedBuf.Of(256);
        var turns = 0;
        var h = new LarcenyHoldings(Live);
        h.Steal(FpA, bufA.Addr, Reraise, stolenTurn: 0);

        turns = 3;   // meets the hold threshold
        h.Expire(fp => bufA.Addr, fp => turns, holdTurns: 3);

        Assert.False(h.IsHeld(FpA, Reraise));   // emptied ledger is pruned -> IsHeld false
    }

    [Fact]
    public void Drive_and_Expire_and_ReleaseAll_skip_a_wielder_whose_locate_returns_zero()
    {
        var h = new LarcenyHoldings(Live);
        // Steal against a real buffer first so there is a ledger entry.
        using var buf = PinnedBuf.Of(256);
        h.Steal(FpA, buf.Addr, Reraise, stolenTurn: 0);

        // Now locate returns 0: Drive, Expire, ReleaseAll must all complete without throwing.
        h.Drive(fp => 0);
        h.Expire(fp => 0, fp => 99, holdTurns: 3);
        h.ReleaseAll(fp => 0);
    }

    [Fact]
    public void ReleaseAll_clears_every_held_bit_across_both_wielders()
    {
        using var bufA = PinnedBuf.Of(256);
        using var bufB = PinnedBuf.Of(256);
        var h = new LarcenyHoldings(Live);

        h.Steal(FpA, bufA.Addr, Reraise, stolenTurn: 0);
        h.Steal(FpA, bufA.Addr, Regen,   stolenTurn: 0);
        h.Steal(FpB, bufB.Addr, Reraise, stolenTurn: 0);

        h.ReleaseAll(fp => fp == FpA ? bufA.Addr : (fp == FpB ? bufB.Addr : 0));

        Assert.Equal(0, bufA.Bytes[Offsets.AReraise] & Offsets.AReraiseBit);
        Assert.Equal(0, bufA.Bytes[Offsets.ARegen]   & Offsets.ARegenBit);
        Assert.Equal(0, bufB.Bytes[Offsets.AReraise] & Offsets.AReraiseBit);
    }
}
