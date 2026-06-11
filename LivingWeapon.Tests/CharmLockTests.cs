using System;
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
    /// assert the actual held bytes instead of trusting an unmapped fake address. Caller disposes.</summary>
    private static PinnedBuf MappedEnemy((int mhp, int lvl, int br, int fa) fp, bool charmed)
    {
        var enemy = PinnedBuf.Of(256);
        var buf = enemy.Bytes;
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
        return enemy;
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
        Assert.Equal(expected, CtTurns.IsTurn(last, cur));
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

    [Fact]
    public void Tick_timeout_drops_the_lock_via_deactivate_leaving_the_held_bytes_untouched()
    {
        using var enemy = MappedEnemy((100, 20, 70, 50), charmed: true);
        var cl = New();
        cl.AdoptOrTransfer(new[] { (enemy.Addr, (100, 20, 70, 50)) });
        Assert.NotNull(cl.LockedFingerprint);

        var t0 = new DateTime(2026, 1, 1);
        cl.Heartbeat(t0);
        cl.Tick(t0.AddMilliseconds(CharmLock.TimeoutMs + 1000), inLive: true);   // no beat for the window -> timeout
        Assert.Null(cl.LockedFingerprint);                          // lock dropped, no more spam

        // The timeout path (Deactivate) drops tracking WITHOUT touching the held bytes; a fall-through
        // to Drive WOULD force-clear the charm bit. A still-set bit proves it was the timeout branch --
        // delete that branch and Drive(0) clears the bit, failing this assert.
        Assert.NotEqual(0, Charm(enemy.Bytes, CharmLock.CharmStatusOff));
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
        using var enemyA = MappedEnemy((100, 20, 70, 50), charmed: true);
        using var enemyB = MappedEnemy((120, 25, 60, 40), charmed: true);
        var cl = New();
        cl.AdoptOrTransfer(new[] { (enemyA.Addr, (100, 20, 70, 50)) });   // lock A
        cl.AdoptOrTransfer(new[] { (enemyB.Addr, (120, 25, 60, 40)) });   // charm B -> transfer, drop A

        Assert.Equal((120, 25, 60, 40), cl.LockedFingerprint!.Value);
        Assert.Equal(0, Charm(enemyA.Bytes, CharmLock.CharmStatusOff));   // A's charm dropped
        Assert.Equal(0, Charm(enemyA.Bytes, CharmLock.CharmAllegOff));    // and its allegiance flag
        Assert.NotEqual(0, Charm(enemyB.Bytes, CharmLock.CharmStatusOff));// B untouched -> still charmed
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
        using var enemy = MappedEnemy((100, 20, 70, 50), charmed: true);
        var cl = New();
        cl.AdoptOrTransfer(new[] { (enemy.Addr, (100, 20, 70, 50)) });

        var t0 = new DateTime(2026, 1, 1);
        cl.Heartbeat(t0);
        // Tick with fresh heartbeat (timeout not expired) but inLive=false
        cl.Tick(t0.AddMilliseconds(100), inLive: false);

        // Charm bytes must not have been touched (no Drive write)
        Assert.NotEqual(0, Charm(enemy.Bytes, CharmLock.CharmStatusOff));
        Assert.NotEqual(0, Charm(enemy.Bytes, CharmLock.CharmAllegOff));
        // Lock is still armed (no release)
        Assert.NotNull(cl.LockedFingerprint);
    }

    [Fact]
    public void Tick_inLive_false_releases_lock_after_timeout()
    {
        // Even with inLive=false the heartbeat-timeout release path must still fire.
        using var enemy = MappedEnemy((100, 20, 70, 50), charmed: true);
        var cl = New();
        cl.AdoptOrTransfer(new[] { (enemy.Addr, (100, 20, 70, 50)) });

        var t0 = new DateTime(2026, 1, 1);
        cl.Heartbeat(t0);
        // Well past the timeout, inLive=false
        cl.Tick(t0.AddMilliseconds(CharmLock.TimeoutMs + 1000), inLive: false);

        Assert.Null(cl.LockedFingerprint);
        // Deactivate path: bytes untouched (not Drive-cleared)
        Assert.NotEqual(0, Charm(enemy.Bytes, CharmLock.CharmStatusOff));
    }

    [Fact]
    public void Tick_inLive_true_runs_Drive_and_clears_when_no_lock_turns_active()
    {
        // With inLive=true the full pipeline runs (Drive is not skipped). With no Galewind
        // equipped (empty meta/kills), lockTurns==0, so Drive clears the charm bytes and
        // releases the lock -- the opposite of what inLive=false does (writes nothing).
        using var enemy = MappedEnemy((100, 20, 70, 50), charmed: true);
        var cl = New();
        cl.AdoptOrTransfer(new[] { (enemy.Addr, (100, 20, 70, 50)) });
        Assert.NotNull(cl.LockedFingerprint);

        var t0 = new DateTime(2026, 1, 1);
        cl.Heartbeat(t0);
        // Tick in-live: Drive runs, lockTurns==0 so hold=false, charm bytes cleared
        cl.Tick(t0.AddMilliseconds(100), inLive: true);

        Assert.Equal(0, Charm(enemy.Bytes, CharmLock.CharmStatusOff));   // Drive cleared it
        Assert.Null(cl.LockedFingerprint);                        // lock released after 0 turns
    }
}
