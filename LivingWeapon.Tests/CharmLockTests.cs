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

    [Theory]   // the stuck-sentinel contract: slot0==0xFF alone is NOT proof of a live battle --
               // QUITTING a battle leaves it stuck at 0xFF on the world map (probe-verified
               // 2026-06-10; a normal victory clears it to 0x66). The marker only counts when a
               // mode-0 frame has an excuse: paused, or a real event id (mid-battle dialogue).
    [InlineData(0xFFu, 1, false, 0xFFFF, true)]    // cast targeting (battleMode 1) with the marker
    [InlineData(0xFFu, 5, false, 0xFFFF, true)]    // cursor on caster's tile during a cast
    [InlineData(0xFFu, 0, true, 0xFFFF, true)]     // paused mid-battle dip -- excused
    [InlineData(0xFFu, 0, false, 401, true)]       // mid-battle dialogue (real event id) -- excused
    [InlineData(0xFFu, 0, false, 0xFFFF, false)]   // QUIT TRAP: stuck marker, mode 0, no excuse -> not live
    [InlineData(0u, 2, false, 0xFFFF, true)]       // active move turn
    [InlineData(0u, 3, false, 0xFFFF, true)]       // action menu
    [InlineData(0u, 4, false, 0xFFFF, true)]       // instant targeting
    [InlineData(0u, 0, false, 0xFFFF, false)]      // post-battle world map
    [InlineData(0u, 1, false, 0xFFFF, false)]      // battleMode 1 without the marker (conservative)
    public void InLiveBattle_requires_a_live_mode_or_an_excused_marker(uint slot0, int battleMode, bool paused, int eventId, bool expected)
        => Assert.Equal(expected, CharmLock.InLiveBattle(slot0, battleMode, paused, eventId));

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
            cl.Tick(t0.AddMilliseconds(CharmLock.TimeoutMs + 1000), inLive: true);   // no beat for the window -> timeout
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

    // --- A2: Tick(now, inLive=false) must be a no-op outside live battle ---

    [Fact]
    public void Tick_inLive_false_issues_no_writes_when_lock_is_armed()
    {
        // An armed lock with a valid heartbeat but inLive=false should write nothing and leave the
        // held bytes untouched. No Detect, no Drive, no SetCharm calls.
        var (addr, buf, h) = MappedEnemy((100, 20, 70, 50), charmed: true);
        try
        {
            var cl = New();
            cl.AdoptOrTransfer(new[] { (addr, (100, 20, 70, 50)) });

            var t0 = new DateTime(2026, 1, 1);
            cl.Heartbeat(t0);
            // Tick with fresh heartbeat (timeout not expired) but inLive=false
            cl.Tick(t0.AddMilliseconds(100), inLive: false);

            // Charm bytes must not have been touched (no Drive write)
            Assert.NotEqual(0, Charm(buf, CharmLock.CharmStatusOff));
            Assert.NotEqual(0, Charm(buf, CharmLock.CharmAllegOff));
            // Lock is still armed (no release)
            Assert.NotNull(cl.LockedFingerprint);
        }
        finally { h.Free(); }
    }

    [Fact]
    public void Tick_inLive_false_releases_lock_after_timeout()
    {
        // Even with inLive=false the heartbeat-timeout release path must still fire.
        var (addr, buf, h) = MappedEnemy((100, 20, 70, 50), charmed: true);
        try
        {
            var cl = New();
            cl.AdoptOrTransfer(new[] { (addr, (100, 20, 70, 50)) });

            var t0 = new DateTime(2026, 1, 1);
            cl.Heartbeat(t0);
            // Well past the timeout, inLive=false
            cl.Tick(t0.AddMilliseconds(CharmLock.TimeoutMs + 1000), inLive: false);

            Assert.Null(cl.LockedFingerprint);
            // Deactivate path: bytes untouched (not Drive-cleared)
            Assert.NotEqual(0, Charm(buf, CharmLock.CharmStatusOff));
        }
        finally { h.Free(); }
    }

    [Fact]
    public void Tick_inLive_true_runs_Drive_and_clears_when_no_lock_turns_active()
    {
        // With inLive=true the full pipeline runs (Drive is not skipped). With no Galewind
        // equipped (empty meta/kills), lockTurns==0, so Drive clears the charm bytes and
        // releases the lock -- the opposite of what inLive=false does (writes nothing).
        var (addr, buf, h) = MappedEnemy((100, 20, 70, 50), charmed: true);
        try
        {
            var cl = New();
            cl.AdoptOrTransfer(new[] { (addr, (100, 20, 70, 50)) });
            Assert.NotNull(cl.LockedFingerprint);

            var t0 = new DateTime(2026, 1, 1);
            cl.Heartbeat(t0);
            // Tick in-live: Drive runs, lockTurns==0 so hold=false, charm bytes cleared
            cl.Tick(t0.AddMilliseconds(100), inLive: true);

            Assert.Equal(0, Charm(buf, CharmLock.CharmStatusOff));   // Drive cleared it
            Assert.Null(cl.LockedFingerprint);                        // lock released after 0 turns
        }
        finally { h.Free(); }
    }
}
