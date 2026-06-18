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
/// the previous is dropped) and the LIVENESS model (no heartbeat: an inLive=false tick -- world map
/// or a between-turn mode-0 lull -- simply IDLES and preserves the lock; teardown is ResetBattle on
/// the battle-exit edge). The lock bookkeeping is exercised directly: Mem reads/writes against
/// unmapped addresses are safe no-ops in the test process (RPM/WPM return false), so AdoptOrTransfer's
/// state transitions are observable.
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

    // --- liveness: a long inLive=false lull must NOT drop the lock (no heartbeat timeout) ---

    [Fact]
    public void Tick_long_inLive_false_lull_preserves_the_lock_no_heartbeat_timeout()
    {
        // REGRESSION (the live charm-break): the old 2s heartbeat timed the lock out during the
        // between-turn mode-0 lulls (~4s on 1.5), dropping the charm mid-combat so the next hit broke
        // it. There is no heartbeat now -- an inLive=false stretch of ANY length just idles and
        // PRESERVES the lock and its held bytes; only ResetBattle (battle-exit edge) tears it down.
        using var enemy = MappedEnemy((100, 20, 70, 50), charmed: true);
        var cl = New();
        cl.AdoptOrTransfer(new[] { (enemy.Addr, (100, 20, 70, 50)) });
        var t0 = new DateTime(2026, 1, 1);
        for (int s = 0; s < 30; s++) cl.Tick(t0.AddSeconds(s), inLive: false);   // 30s of mode-0 lull
        Assert.NotNull(cl.LockedFingerprint);                                     // lock survived the lull
        Assert.NotEqual(0, Charm(enemy.Bytes, CharmLock.CharmStatusOff));         // status byte untouched
        Assert.NotEqual(0, Charm(enemy.Bytes, CharmLock.CharmAllegOff));          // control byte untouched
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
        Assert.Equal(0, Charm(enemyA.Bytes, CharmLock.CharmStatusOff));   // A's charm status dropped
        Assert.Equal(0, Charm(enemyA.Bytes, CharmLock.CharmAllegOff));    // and its AI-control/allegiance bit
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
        // An armed lock ticked with inLive=false (world map / mode-0 lull) writes nothing and leaves
        // the held bytes untouched. No Detect, no Drive, no SetCharm calls -- it idles, and the lock
        // stays armed (only ResetBattle releases it).
        using var enemy = MappedEnemy((100, 20, 70, 50), charmed: true);
        var cl = New();
        cl.AdoptOrTransfer(new[] { (enemy.Addr, (100, 20, 70, 50)) });

        var t0 = new DateTime(2026, 1, 1);
        cl.Tick(t0.AddMilliseconds(100), inLive: false);

        // Charm bytes must not have been touched (no Drive write)
        Assert.NotEqual(0, Charm(enemy.Bytes, CharmLock.CharmStatusOff));
        Assert.NotEqual(0, Charm(enemy.Bytes, CharmLock.CharmAllegOff));
        // Lock is still armed (no release)
        Assert.NotNull(cl.LockedFingerprint);
    }

    [Fact]
    public void Drive_hold_restamps_both_the_charm_status_and_the_control_byte()
    {
        // FIX 1 (revert of the wrong fix-2): charm is TWO pieces of state on the authoritative copy --
        // +0x49 bit 0x20 = status/icon, +0x54 bit 0x20 = AI control/allegiance. The breaking hit clears
        // BOTH, so the hold must re-stamp BOTH. +0x54's low bits drift like an engine counter; Force ORs
        // ONLY the 0x20 bit, leaving them intact. Seed +0x54 = 0x1D (control bit CLEAR, counter = 0x1D --
        // a real value the live probe saw it drift to): after the hold it must read 0x3D (0x1D | 0x20),
        // proving the control bit was OR-set while the drifting counter bits were preserved.
        using var enemy = MappedEnemy((100, 20, 70, 50), charmed: false);
        enemy.Bytes[CharmLock.CharmAllegOff] = 0x1D;
        var cl = New();
        cl.AdoptOrTransfer(new[] { (enemy.Addr, (100, 20, 70, 50)) });
        cl.Drive(3);   // hold: counted(0) < lockTurns(3) -> SetCharm(addr, hold:true)
        Assert.NotEqual(0, Charm(enemy.Bytes, CharmLock.CharmStatusOff));  // +0x49 status held
        Assert.Equal(0x3D, enemy.Bytes[CharmLock.CharmAllegOff]);          // +0x54 control bit OR-set, counter kept
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
        // Tick in-live: Drive runs, lockTurns==0 so hold=false, charm bytes cleared
        cl.Tick(t0.AddMilliseconds(100), inLive: true);

        Assert.Equal(0, Charm(enemy.Bytes, CharmLock.CharmStatusOff));   // Drive cleared it
        Assert.Null(cl.LockedFingerprint);                        // lock released after 0 turns
    }
}
