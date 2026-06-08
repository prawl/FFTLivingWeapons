using System;
using System.Runtime.InteropServices;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// The pure turn-counting behind Galewind's charm-lock. We count the locked enemy's own turns off
/// its CT (charge time, +0x25 on the struct we hold): CT climbs to full, sits there during the turn,
/// then resets when it acts. A turn = a reset from (near-)full to notably lower. The memory
/// reads/writes (find the copy, hold the bytes) are integration; this nails the count + clear timing.
///
/// Also covers the anti-cheese invariant (only ONE enemy is ever charm-LOCKED -- newest charm wins,
/// the previous is dropped) and the heartbeat timeout (the lock deactivates when no live-battlefield
/// heartbeat has arrived for a while, so it stops spamming after a battle ends). The lock bookkeeping
/// is exercised directly: Mem reads/writes against unmapped addresses are safe no-ops in the test
/// process (RPM/WPM return false), so AdoptOrTransfer's state transitions are observable.
/// </summary>
public class CharmLockTests
{
    private static CharmLock New() => new(new(), new());

    /// <summary>A pinned buffer laid out like an authoritative enemy copy: fingerprint bytes at the
    /// array offsets, charm + allegiance bits optionally set. Mem (RPM/WPM on our OWN process) reads
    /// and writes it by address, so CharmLock's Valid()/SetCharm operate on it for real -- letting us
    /// assert the actual held bytes instead of trusting an unmapped fake address. Caller frees the handle.</summary>
    private static (long addr, byte[] buf, GCHandle h) MappedEnemy((int mhp, int lvl, int br, int fa) fp, bool charmed)
    {
        var buf = new byte[256];
        buf[Offsets.AMaxHp] = (byte)(fp.mhp & 0xFF);
        buf[Offsets.AMaxHp + 1] = (byte)((fp.mhp >> 8) & 0xFF);
        buf[Offsets.ALevel] = (byte)fp.lvl;
        buf[Offsets.ABrave] = (byte)fp.br;
        buf[Offsets.AFaith] = (byte)fp.fa;
        if (charmed)
        {
            buf[CharmLock.CharmStatusOff] |= CharmLock.CharmBit;
            buf[CharmLock.CharmAllegOff] |= CharmLock.CharmBit;
        }
        var h = GCHandle.Alloc(buf, GCHandleType.Pinned);
        return (h.AddrOfPinnedObject().ToInt64(), buf, h);
    }

    private static int Charm(byte[] buf, int off) => buf[off] & CharmLock.CharmBit;

    [Theory]
    [InlineData(100, 10, true)]    // full -> reset = a completed turn
    [InlineData(95, 0, true)]
    [InlineData(90, 69, true)]     // dropped below the floor
    [InlineData(90, 70, false)]    // not a big enough drop (still mid/charging)
    [InlineData(80, 5, false)]     // wasn't full when it dropped -> not our reset edge
    [InlineData(50, 100, false)]   // climbing, not a turn
    [InlineData(100, 100, false)]  // still full (mid-turn), not reset yet
    [InlineData(0, 0, false)]
    public void IsTurn_detects_a_CT_reset_from_full(int last, int cur, bool expected)
    {
        Assert.Equal(expected, CharmLock.IsTurn(last, cur));
    }

    // --- #7 heartbeat timeout: no live-battlefield beat for the timeout window -> deactivate ---

    [Theory]
    [InlineData(0, false)]
    [InlineData(1999, false)]
    [InlineData(2000, false)]    // exactly the timeout is not yet PAST it
    [InlineData(2001, true)]
    [InlineData(9000, true)]
    public void HeartbeatExpired_trips_only_after_the_timeout(int elapsedMs, bool expected)
    {
        var t0 = new DateTime(2026, 1, 1);
        Assert.Equal(expected, CharmLock.HeartbeatExpired(t0.AddMilliseconds(elapsedMs), t0, 2000));
    }

    [Theory]   // #1 fix: feed the beat through cast/attack targeting so a live lock isn't false-dropped
    [InlineData(0xFFu, 1, true)]    // cast targeting (battleMode 1) -- slot0==0xFF keeps the beat alive
    [InlineData(0xFFu, 5, true)]    // cursor on caster's tile during a cast
    [InlineData(0xFFu, 0, true)]    // in-battle marker present even if battleMode momentarily reads 0
    [InlineData(0u, 2, true)]       // active move turn
    [InlineData(0u, 3, true)]       // action menu
    [InlineData(0u, 4, true)]       // instant targeting
    [InlineData(0u, 0, false)]      // post-battle world map: no marker + battleMode 0 -> beat stops -> timeout
    [InlineData(0u, 1, false)]      // battleMode 1 without the marker (doesn't occur live; conservative)
    public void InLiveBattle_keeps_the_beat_through_targeting_but_stops_on_the_world_map(uint slot0, int battleMode, bool expected)
        => Assert.Equal(expected, CharmLock.InLiveBattle(slot0, battleMode));

    [Fact]
    public void Tick_timeout_drops_the_lock_via_deactivate_leaving_the_held_bytes_untouched()
    {
        var (addr, buf, h) = MappedEnemy((100, 20, 70, 50), charmed: true);
        try
        {
            var cl = New();
            cl.AdoptOrTransfer(new[] { (addr, (100, 20, 70, 50)) });
            Assert.NotNull(cl.LockedFingerprint);

            var t0 = new DateTime(2026, 1, 1);
            cl.Heartbeat(t0);
            cl.Tick(t0.AddMilliseconds(CharmLock.TimeoutMs + 1000));   // no beat for the window -> timeout
            Assert.Null(cl.LockedFingerprint);                          // lock dropped, no more spam

            // The timeout path (Deactivate) drops tracking WITHOUT touching the held bytes; a fall-through
            // to Drive WOULD force-clear the charm bit. A still-set bit proves it was the timeout branch --
            // delete that branch and Drive(0) clears the bit, failing this assert.
            Assert.NotEqual(0, Charm(buf, CharmLock.CharmStatusOff));
        }
        finally { h.Free(); }
    }

    // --- #1 anti-cheese: exactly one enemy locked, newest charm wins, previous dropped ---

    [Fact]
    public void AdoptOrTransfer_locks_a_single_enemy()
    {
        var cl = New();
        cl.AdoptOrTransfer(new[] { (1000L, (100, 20, 70, 50)) });
        Assert.Equal((100, 20, 70, 50), cl.LockedFingerprint!.Value);
    }

    [Fact]
    public void AdoptOrTransfer_newest_charm_steals_the_lock()
    {
        var cl = New();
        cl.AdoptOrTransfer(new[] { (1000L, (100, 20, 70, 50)) });
        cl.AdoptOrTransfer(new[] { (2000L, (120, 25, 60, 40)) });   // a different enemy charmed
        Assert.Equal((120, 25, 60, 40), cl.LockedFingerprint!.Value);   // lock moved off the first
    }

    [Fact]
    public void AdoptOrTransfer_same_enemy_keeps_the_lock()
    {
        var cl = New();
        cl.AdoptOrTransfer(new[] { (1000L, (100, 20, 70, 50)) });
        cl.AdoptOrTransfer(new[] { (1000L, (100, 20, 70, 50)) });   // same fp re-detected, no churn
        Assert.Equal((100, 20, 70, 50), cl.LockedFingerprint!.Value);
    }

    [Fact]
    public void AdoptOrTransfer_locks_only_one_when_several_are_charmed()
    {
        // #4: a normal (breakable) charm may coexist -- only ONE is ever the held lock; the rest
        // are left untouched as ordinary charms. With nothing locked yet, the first is adopted.
        var cl = New();
        cl.AdoptOrTransfer(new[] { (1000L, (100, 20, 70, 50)), (2000L, (120, 25, 60, 40)) });
        Assert.Equal((100, 20, 70, 50), cl.LockedFingerprint!.Value);
    }

    [Fact]
    public void AdoptOrTransfer_clears_the_previous_enemys_charm_bytes_on_transfer()
    {
        // The real anti-cheese drop: locking a new enemy force-clears the PREVIOUS one's charm +
        // allegiance bytes (so it reverts to hostile), while leaving the new enemy charmed.
        var (addrA, bufA, hA) = MappedEnemy((100, 20, 70, 50), charmed: true);
        var (addrB, bufB, hB) = MappedEnemy((120, 25, 60, 40), charmed: true);
        try
        {
            var cl = New();
            cl.AdoptOrTransfer(new[] { (addrA, (100, 20, 70, 50)) });   // lock A
            cl.AdoptOrTransfer(new[] { (addrB, (120, 25, 60, 40)) });   // charm B -> transfer, drop A

            Assert.Equal((120, 25, 60, 40), cl.LockedFingerprint!.Value);
            Assert.Equal(0, Charm(bufA, CharmLock.CharmStatusOff));   // A's charm dropped
            Assert.Equal(0, Charm(bufA, CharmLock.CharmAllegOff));    // and its allegiance flag
            Assert.NotEqual(0, Charm(bufB, CharmLock.CharmStatusOff));// B untouched -> still charmed
        }
        finally { hA.Free(); hB.Free(); }
    }

    // --- the pure newest-wins decision behind the single lock ---

    [Fact]
    public void Decide_adopts_the_first_charm_when_nothing_is_locked()
    {
        Assert.True(CharmLock.Decide(null, new[] { (100, 20, 70, 50) }, out var t, out bool drop));
        Assert.Equal((100, 20, 70, 50), t);
        Assert.False(drop);
    }

    [Fact]
    public void Decide_transfers_to_a_different_enemy_and_drops_the_previous()
    {
        Assert.True(CharmLock.Decide((100, 20, 70, 50), new[] { (120, 25, 60, 40) }, out var t, out bool drop));
        Assert.Equal((120, 25, 60, 40), t);
        Assert.True(drop);
    }

    [Fact]
    public void Decide_keeps_the_lock_when_only_the_current_enemy_is_charmed()
    {
        Assert.False(CharmLock.Decide((100, 20, 70, 50), new[] { (100, 20, 70, 50) }, out _, out _));
    }

    [Fact]
    public void Decide_does_nothing_when_nothing_is_charmed()
    {
        Assert.False(CharmLock.Decide((100, 20, 70, 50), Array.Empty<(int, int, int, int)>(), out _, out _));
    }
}
