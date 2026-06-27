using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// Band-confirmed-UNARMED disambiguation in ActorResolver (symmetric to the b42f77a armed track).
/// An unarmed actor (band weapon +0x04 == 0) that shares (level,brave,faith) with an armed roster
/// neighbor must resolve to EMPTY -- NOT borrow the neighbor's weapon (live mis-credit: Warlock's
/// Staff id 60). The EMPTY resolve then routes the kill to KillTracker's _lethalUntracked no-credit path.
/// </summary>
public class ActorResolverUnarmedTests
{
    // The collider MUST be in the tracked set or the tests are vacuous (an untracked collider
    // resolves Empty even WITHOUT the guard). 60 = Warlock's Staff (the live mis-credit).
    private const int ColliderWeapon = 60;
    private const int OwnWeapon = 35;     // a tracked weapon for the armed-actor control (R3)
    private const int Untracked = 999;    // the summoner's own broken/untracked gear -> empty hands
    private static readonly HashSet<int> Weapons = new() { ColliderWeapon, OwnWeapon, 73, 90 };

    private const int BandSlot = Offsets.SlotsBack + 1;   // any valid band slot

    private static void SetActive(FakeSparseMemory m, int hp, int maxHp, int level)
    {
        m.U16s[Offsets.TurnQueue + Offsets.TqHp]    = (ushort)hp;
        m.U16s[Offsets.TurnQueue + Offsets.TqMaxHp] = (ushort)maxHp;
        m.U16s[Offsets.TurnQueue + Offsets.TqLevel] = (ushort)level;
    }

    // R1 HEADLINE (load-bearing, non-vacuous): unarmed summoner + own empty-hands slot + colliding ARMED slot.
    [Fact]
    public void Unarmed_actor_with_colliding_armed_slot_resolves_EMPTY()
    {
        var m = new FakeSparseMemory();
        SetActive(m, hp: 400, maxHp: 400, level: 50);
        MemSeats.SeatBand(m, BandSlot, weapon: 0, lvl: 50, br: 55, fa: 60, gx: 5, gy: 5, hp: 400, maxHp: 400);
        MemSeats.SeatRoster(m, slot: 0, lvl: 50, br: 55, fa: 60, rh: Untracked);        // summoner own -> empty hands
        MemSeats.SeatRoster(m, slot: 1, lvl: 50, br: 55, fa: 60, rh: ColliderWeapon);   // armed mage collision
        var r = new ActorResolver(m, Weapons);
        bool ok = r.TryResolveActingPlayer(out var ws);
        Assert.True(ok);     // resolved as a roster player...
        Assert.Empty(ws);    // ...but EMPTY. Delete the guard line -> ws == { 60 } -> this fails.
    }

    // R2: same setup -> ResolveActingMainHand returns 0 (the mirror edit).
    [Fact]
    public void Unarmed_actor_with_colliding_armed_slot_has_no_main_hand()
    {
        var m = new FakeSparseMemory();
        SetActive(m, hp: 400, maxHp: 400, level: 50);
        MemSeats.SeatBand(m, BandSlot, weapon: 0, lvl: 50, br: 55, fa: 60, gx: 5, gy: 5, hp: 400, maxHp: 400);
        MemSeats.SeatRoster(m, slot: 0, lvl: 50, br: 55, fa: 60, rh: Untracked);
        MemSeats.SeatRoster(m, slot: 1, lvl: 50, br: 55, fa: 60, rh: ColliderWeapon);
        var r = new ActorResolver(m, Weapons);
        Assert.Equal(0, r.ResolveActingMainHand());   // delete the mirror guard -> returns 60
    }

    // R3 CONTROL: an ARMED actor (band weapon = tracked) still resolves its own weapon (guard must NOT fire).
    [Fact]
    public void Armed_actor_still_resolves_its_weapon()
    {
        var m = new FakeSparseMemory();
        SetActive(m, hp: 400, maxHp: 400, level: 50);
        MemSeats.SeatBand(m, BandSlot, weapon: OwnWeapon, lvl: 50, br: 55, fa: 60, gx: 5, gy: 5, hp: 400, maxHp: 400);
        MemSeats.SeatRoster(m, slot: 0, lvl: 50, br: 55, fa: 60, rh: OwnWeapon);
        var r = new ActorResolver(m, Weapons);
        bool ok = r.TryResolveActingPlayer(out var ws);
        Assert.True(ok);
        Assert.Equal(new List<int> { OwnWeapon }, ws);
    }

    // R5 BOUNDARY (not load-bearing): actorUnarmed but NO own empty-hands slot (only a colliding armed
    // slot) -> falls through to the armed path, returns the collider. Documents the strict emptyMatch gate
    // (degrade to existing behavior; never a NEW mis-credit).
    [Fact]
    public void Unarmed_actor_without_own_empty_slot_falls_through_to_armed()
    {
        var m = new FakeSparseMemory();
        SetActive(m, hp: 400, maxHp: 400, level: 50);
        MemSeats.SeatBand(m, BandSlot, weapon: 0, lvl: 50, br: 55, fa: 60, gx: 5, gy: 5, hp: 400, maxHp: 400);
        MemSeats.SeatRoster(m, slot: 1, lvl: 50, br: 55, fa: 60, rh: ColliderWeapon);   // ONLY the armed collider
        var r = new ActorResolver(m, Weapons);
        bool ok = r.TryResolveActingPlayer(out var ws);
        Assert.True(ok);
        Assert.Equal(new List<int> { ColliderWeapon }, ws);   // unchanged from current behavior
    }
}
