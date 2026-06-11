using System;
using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// ExtraTurn Tick integration: the shared-locator path (Wielder.TryResolve + Wielder.Locate)
/// threaded through IGameMemory. Tests that were blocked before the fork was deleted:
///   - Zwill in the off-hand arms correctly (the fork hard-read RRHand only).
///   - Identical-twin tie-break fires (the fork lacked it -- extra turns silently failed when
///     the battle placed the wielder on tile (0,0)).
///   - GateLost fires and restores CT when the Zwill leaves the wielder's hands.
/// All tests use FakeSparseMemory + a pinned kill tally so no game memory is needed.
/// </summary>
public class ExtraTurnIntegrationTests
{
    private static void SeatBand(FakeSparseMemory m, int bandIdx, int weapon, int lvl, int br, int fa,
                                  int gx, int gy, int hp = 100, int maxHp = 100,
                                  int ctTurn = 0)
    {
        MemSeats.SeatBand(m, bandIdx, weapon, lvl, br, fa, gx, gy, hp, maxHp, ctTurn);
        // mark the CtSlam offset as writable so the slam guard passes
        m.WritableAddrs.Add(Band.Entry(bandIdx) + ExtraTurn.CtOff);
    }

    private static ExtraTurn MakeExtra(FakeSparseMemory mem, int kills = 0)
    {
        var dict = new Dictionary<int, int>();
        if (kills > 0) dict[ExtraTurn.ZwillId] = kills;
        return new ExtraTurn(dict, mem);
    }

    // ---- Zwill in the off-hand: signatures fire from the main hand only ----
    // A Living Weapon earns kills in any hand, but commands its gift only from the main hand.

    [Fact]
    public void Does_not_arm_when_zwill_is_in_the_offhand_only()
    {
        var m = new FakeSparseMemory();
        // RRHand = some rod (id 56), ROffHand = ZwillId -- offhand does not activate the grant
        MemSeats.SeatRoster(m, 0, lvl: 30, br: 65, fa: 58, rh: 56, oh: ExtraTurn.ZwillId);
        SeatBand(m, 15, weapon: 56, lvl: 30, br: 65, fa: 58, gx: 4, gy: 3);

        var kills = new Dictionary<int, int> { [ExtraTurn.ZwillId] = Tuning.KillThresholds[ExtraTurn.AtTier - 1] };
        var extra = new ExtraTurn(kills, m);

        extra.Tick(DateTime.Now);
        Assert.Equal(GrantState.Idle, extra.State);

        kills[ExtraTurn.ZwillId]++;
        extra.Tick(DateTime.Now);
        // Offhand Zwill: TryResolveMainHand returns false -> gate lost -> stays Idle, no CT slam
        Assert.Equal(GrantState.Idle, extra.State);
    }

    // ---- Identical-twin tie-break (missing from the old fork) ----

    [Fact]
    public void Locates_wielder_when_both_twins_sit_at_origin()
    {
        var m = new FakeSparseMemory();
        MemSeats.SeatRoster(m, 0, lvl: 30, br: 89, fa: 76, rh: ExtraTurn.ZwillId);
        // Two identical entries at (0,0): the corner-tile twin scenario that broke the old fork.
        SeatBand(m, 25, weapon: ExtraTurn.ZwillId, lvl: 30, br: 89, fa: 76, gx: 0, gy: 0);
        SeatBand(m, 28, weapon: ExtraTurn.ZwillId, lvl: 30, br: 89, fa: 76, gx: 0, gy: 0);

        int threshold = Tuning.KillThresholds[ExtraTurn.AtTier - 1];
        var kills = new Dictionary<int, int> { [ExtraTurn.ZwillId] = threshold };
        var extra = new ExtraTurn(kills, m);

        // Tick 1: establishes _lastCount = threshold (no fresh kill yet since lastCount was -1).
        extra.Tick(DateTime.Now);
        Assert.Equal(GrantState.Idle, extra.State);

        // Tick 2: a new kill arrives -> arms the grant.
        kills[ExtraTurn.ZwillId] = threshold + 1;
        extra.Tick(DateTime.Now);
        Assert.Equal(GrantState.Arming, extra.State);

        // Tick 3: Wielder.Locate fires; the twin tie-break must return a non-zero entry.
        // A locate failure leaves _base == 0 (the old fork's bug). Success leaves it non-zero.
        extra.Tick(DateTime.Now);
        Assert.NotEqual(0, extra.LocatedBase);
    }

    // ---- GateLost restores CT ----

    [Fact]
    public void GateLost_restores_ct_when_zwill_leaves_the_wielder_hands()
    {
        var m = new FakeSparseMemory();
        MemSeats.SeatRoster(m, 0, lvl: 30, br: 65, fa: 58, rh: ExtraTurn.ZwillId);
        SeatBand(m, 15, weapon: ExtraTurn.ZwillId, lvl: 30, br: 65, fa: 58, gx: 4, gy: 3);

        int threshold = Tuning.KillThresholds[ExtraTurn.AtTier - 1];
        var kills = new Dictionary<int, int> { [ExtraTurn.ZwillId] = threshold };
        var extra = new ExtraTurn(kills, m);

        // Tick 1: establish baseline kill count (_lastCount = threshold, state stays Idle).
        extra.Tick(DateTime.Now);
        Assert.Equal(GrantState.Idle, extra.State);

        // Tick 2: increment kills -> fresh kill -> arms the grant.
        kills[ExtraTurn.ZwillId] = threshold + 1;
        extra.Tick(DateTime.Now);
        Assert.Equal(GrantState.Arming, extra.State);

        // Tick 3: locate fires and _base is set; write CT=SlamCt happens on Owed/Pinning.
        // First force locate by ticking once more.
        extra.Tick(DateTime.Now);

        // Now drop the Zwill: TierFor fails -> GateLost -> Release writes CT=0.
        kills.Remove(ExtraTurn.ZwillId);
        m.U16s[Offsets.RosterBase + Offsets.RRHand] = 56;  // rod, not Zwill
        extra.Tick(DateTime.Now);

        Assert.Equal(GrantState.Idle, extra.State);
        // Release(GateLost) must have written CT=0 to the located entry.
        Assert.True(m.Written.ContainsKey(Band.Entry(15) + ExtraTurn.CtOff),
            "expected CT restore write on GateLost");
        Assert.Equal(0, m.Written[Band.Entry(15) + ExtraTurn.CtOff]);
    }
}
