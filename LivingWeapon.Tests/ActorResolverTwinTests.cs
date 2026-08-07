using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-154 pre-refactor pins for ActorResolver's turn-queue TWIN FILTER: the discard-and-restart
/// bookkeeping that keeps a frozen (0,0) mirror seat from stealing (or spoiling) credit when the
/// real-position copy of the acting unit is also on the band. Written GREEN against the
/// triplicated pre-LW-154 bodies, so the extraction into one shared home must keep them green
/// and a sabotage of the shared filter must turn exactly these red.
///
/// Both orderings are pinned per public resolver, because they exercise DIFFERENT halves of the
/// filter: a (0,0) twin at a LOWER band slot than the real entry exercises the RESTART (the
/// walk has already accumulated the twin's answer and must throw it away when the real seat
/// appears), while a twin at a HIGHER slot exercises the SKIP (a real match already latched, so
/// later (0,0) seats are ignored). In every scenario the twin resolves to a DIFFERENT answer
/// than the real seat, so a broken filter is visible as ambiguity (a refused resolve), never as
/// a silent pass.
/// </summary>
public class ActorResolverTwinTests
{
    private const int TwinWeapon = 52;    // what the frozen (0,0) mirror's fingerprint resolves to
    private const int RealWeapon = 73;    // what the real-position seat's fingerprint resolves to
    private const int ThirdWeapon = 90;   // the double-twin ambiguity pin's real-seat answer
    private static readonly HashSet<int> Weapons = new() { TwinWeapon, RealWeapon, ThirdWeapon };

    private const int LowSlot = 3;                        // walked BEFORE HighSlot
    private const int HighSlot = Offsets.SlotsBack + 1;   // walked AFTER LowSlot

    private static void SetActive(FakeSparseMemory m, int hp, int maxHp, int level)
    {
        m.U16s[Offsets.TurnQueue + Offsets.TqHp]    = (ushort)hp;
        m.U16s[Offsets.TurnQueue + Offsets.TqMaxHp] = (ushort)maxHp;
        m.U16s[Offsets.TurnQueue + Offsets.TqLevel] = (ushort)level;
    }

    /// <summary>Stage the acting unit twice on the band: a frozen (0,0) mirror whose
    /// (brave,faith) resolves to TwinWeapon's roster slot, and a real-position seat whose
    /// (brave,faith) resolves to RealWeapon's. Same (hp,maxHp,level) as the turn queue, so BOTH
    /// pass the fingerprint match and only the twin filter tells them apart.</summary>
    private static FakeSparseMemory Stage(int twinSlot, int realSlot)
    {
        var m = new FakeSparseMemory();
        SetActive(m, hp: 400, maxHp: 400, level: 50);
        MemSeats.SeatBand(m, twinSlot, weapon: TwinWeapon, lvl: 50, br: 55, fa: 60, gx: 0, gy: 0, hp: 400, maxHp: 400);
        MemSeats.SeatBand(m, realSlot, weapon: RealWeapon, lvl: 50, br: 70, fa: 40, gx: 5, gy: 5, hp: 400, maxHp: 400);
        MemSeats.SeatRoster(m, slot: 0, lvl: 50, br: 55, fa: 60, rh: TwinWeapon);
        MemSeats.SeatRoster(m, slot: 1, lvl: 50, br: 70, fa: 40, rh: RealWeapon);
        return m;
    }

    // ---- RESTART: the (0,0) twin sits at a LOWER slot, so the walk accumulates its answer
    // first and must discard it when the real-position seat appears. ----

    [Fact]
    public void Weapons_resolve_restarts_on_the_real_seat_after_a_frozen_twin()
    {
        var r = new ActorResolver(Stage(twinSlot: LowSlot, realSlot: HighSlot), Weapons);
        Assert.True(r.TryResolveActingPlayer(out var ws));
        Assert.Equal(new List<int> { RealWeapon }, ws);   // no restart -> ambiguous -> false
    }

    [Fact]
    public void MainHand_resolve_restarts_on_the_real_seat_after_a_frozen_twin()
    {
        var r = new ActorResolver(Stage(twinSlot: LowSlot, realSlot: HighSlot), Weapons);
        Assert.Equal(RealWeapon, r.ResolveActingMainHand());   // no restart -> ambiguous -> 0
    }

    [Fact]
    public void Fingerprint_resolve_restarts_on_the_real_seat_after_a_frozen_twin()
    {
        var r = new ActorResolver(Stage(twinSlot: LowSlot, realSlot: HighSlot), Weapons);
        Assert.True(r.TryResolveActingFingerprint(out var fp));
        Assert.Equal((50, 70, 40), fp);   // the REAL seat's fingerprint, not the twin's
    }

    // ---- SKIP: the real seat sits at a LOWER slot, so it latches first and the later (0,0)
    // twin must be ignored outright. ----

    [Fact]
    public void Weapons_resolve_skips_a_frozen_twin_after_the_real_seat()
    {
        var r = new ActorResolver(Stage(twinSlot: HighSlot, realSlot: LowSlot), Weapons);
        Assert.True(r.TryResolveActingPlayer(out var ws));
        Assert.Equal(new List<int> { RealWeapon }, ws);   // no skip -> ambiguous -> false
    }

    [Fact]
    public void MainHand_resolve_skips_a_frozen_twin_after_the_real_seat()
    {
        var r = new ActorResolver(Stage(twinSlot: HighSlot, realSlot: LowSlot), Weapons);
        Assert.Equal(RealWeapon, r.ResolveActingMainHand());
    }

    [Fact]
    public void Fingerprint_resolve_skips_a_frozen_twin_after_the_real_seat()
    {
        var r = new ActorResolver(Stage(twinSlot: HighSlot, realSlot: LowSlot), Weapons);
        Assert.True(r.TryResolveActingFingerprint(out var fp));
        Assert.Equal((50, 70, 40), fp);
    }

    // ---- RosterFpMatches pins (LW-154): each field of the shared roster fingerprint rule must
    // be load-bearing on its own. A sabotage probe during the extraction found FAITH had no pin
    // anywhere in the suite: dropping it from the rule left all 3036 tests green. These stage
    // two armed roster units identical in everything BUT faith, with the acting unit's band
    // weapon left as the empty-hand sentinel so the legacy armed path (where a fingerprint
    // collision means ambiguity, not a weapon-track rescue) is the one deciding. ----

    [Fact]
    public void Faith_alone_tells_two_roster_units_apart_for_weapons()
    {
        var m = new FakeSparseMemory();
        SetActive(m, hp: 400, maxHp: 400, level: 50);
        MemSeats.SeatBand(m, HighSlot, weapon: 0xFFFF, lvl: 50, br: 55, fa: 60, gx: 5, gy: 5, hp: 400, maxHp: 400);
        MemSeats.SeatRoster(m, slot: 0, lvl: 50, br: 55, fa: 60, rh: TwinWeapon);   // the actor: fa 60
        MemSeats.SeatRoster(m, slot: 1, lvl: 50, br: 55, fa: 90, rh: RealWeapon);   // faith-only sibling
        var r = new ActorResolver(m, Weapons);
        Assert.True(r.TryResolveActingPlayer(out var ws));   // faith-blind rule -> ambiguous -> false
        Assert.Equal(new List<int> { TwinWeapon }, ws);
    }

    [Fact]
    public void Main_hand_restart_clears_ambiguity_accumulated_across_two_conflicting_twins()
    {
        // Verifier-requested pin (LW-154): ResolveActingMainHand's restart must clear BOTH its
        // accumulation and its ambiguous flag. Two conflicting (0,0) twins accumulate
        // ambiguity, then the real seat restarts; drop the `ambiguous = false` half of the
        // reset and the stale flag would zero out the real seat's clean answer. (The weapons
        // resolver has no equivalent pin on purpose: it refuses on the twins' set mismatch
        // BEFORE the real seat is reached, pre-existing behavior.)
        var m = new FakeSparseMemory();
        SetActive(m, hp: 400, maxHp: 400, level: 50);
        MemSeats.SeatBand(m, LowSlot, weapon: TwinWeapon, lvl: 50, br: 55, fa: 60, gx: 0, gy: 0, hp: 400, maxHp: 400);
        MemSeats.SeatBand(m, LowSlot + 1, weapon: RealWeapon, lvl: 50, br: 70, fa: 40, gx: 0, gy: 0, hp: 400, maxHp: 400);
        MemSeats.SeatBand(m, HighSlot, weapon: ThirdWeapon, lvl: 50, br: 30, fa: 30, gx: 5, gy: 5, hp: 400, maxHp: 400);
        MemSeats.SeatRoster(m, slot: 0, lvl: 50, br: 55, fa: 60, rh: TwinWeapon);
        MemSeats.SeatRoster(m, slot: 1, lvl: 50, br: 70, fa: 40, rh: RealWeapon);
        MemSeats.SeatRoster(m, slot: 2, lvl: 50, br: 30, fa: 30, rh: ThirdWeapon);
        var r = new ActorResolver(m, Weapons);
        Assert.Equal(ThirdWeapon, r.ResolveActingMainHand());
    }

    [Fact]
    public void Faith_alone_tells_two_roster_units_apart_for_main_hand()
    {
        var m = new FakeSparseMemory();
        SetActive(m, hp: 400, maxHp: 400, level: 50);
        MemSeats.SeatBand(m, HighSlot, weapon: 0xFFFF, lvl: 50, br: 55, fa: 60, gx: 5, gy: 5, hp: 400, maxHp: 400);
        MemSeats.SeatRoster(m, slot: 0, lvl: 50, br: 55, fa: 60, rh: TwinWeapon);
        MemSeats.SeatRoster(m, slot: 1, lvl: 50, br: 55, fa: 90, rh: RealWeapon);
        var r = new ActorResolver(m, Weapons);
        Assert.Equal(TwinWeapon, r.ResolveActingMainHand());   // faith-blind rule -> found==2 -> 0
    }
}
