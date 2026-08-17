using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-252 stage 2: ActorResolver's turn-queue roster walks (FingerprintPlayer,
/// MainHandFromRoster in ActorResolver.TurnQueue.cs) used to adopt a fingerprint-colliding
/// roster row with the acting unit's OWN identity playing no part -- the "legacy unique armed"
/// fallback (FingerprintPlayer's `armed != null &amp;&amp; !armedAmbiguous` return / MainHandFromRoster's
/// `found == 1 ? rh : 0`) trusted whichever single row happened to match (level,brave,faith),
/// even when a DIFFERENT unit's roster row was the one that matched. Concrete bug: a Dragoon
/// whose live brave drifts mid-battle drops out of his OWN roster row's fingerprint match; an
/// fp-twin Archer's row is the lone survivor, so the Dragoon's Claymore kill credited the
/// Archer's Eclipsebolt. A live probe (2026-08-17) proved party roster nameIds are unique per
/// occupied row and the combat frame mirrors them at Offsets.ANameId (ledger
/// [frame-1fc-nameid-mirror] PROVEN; [party-nameid-unique-key] pending owner flip) -- the fix
/// reads the acting band seat's own frame nameId and requires any roster row a walk considers to
/// carry that SAME nameId once it is readable, else skips the row.
/// </summary>
public class ActorResolverVetoTests
{
    private const int Dragoon = 501;    // the acting unit's OWN roster nameId (frame ANameId mirrors this)
    private const int Archer = 502;     // the fp-twin's roster nameId -- a DIFFERENT unit
    private const int Claymore = 40;    // the Dragoon's actual weapon (the band seat's own AWeapon)
    private const int Eclipsebolt = 45; // the Archer's weapon -- must NEVER be adopted for the Dragoon's turn
    private const int Level = 35;
    private const int Faith = 50;
    private const int DriftedBrave = 68;   // the Dragoon's LIVE brave after mid-battle drift, == the Archer's roster brave
    private const int StaleBrave = 64;     // the Dragoon's pre-battle ROSTER brave (now stale)

    private static readonly HashSet<int> Weapons = new() { Claymore, Eclipsebolt };
    private const int BandSlot = Offsets.SlotsBack + 1;   // any valid real-position band slot

    private static void SetActive(FakeSparseMemory m, int hp, int maxHp, int level)
    {
        m.U16s[Offsets.TurnQueue + Offsets.TqHp]    = (ushort)hp;
        m.U16s[Offsets.TurnQueue + Offsets.TqMaxHp] = (ushort)maxHp;
        m.U16s[Offsets.TurnQueue + Offsets.TqLevel] = (ushort)level;
    }

    /// <summary>The regression shape shared by V1/V2/V4: Dragoon's roster row is stale (brave
    /// StaleBrave, no longer matching the live band seat's DriftedBrave), the Archer's roster row
    /// sits AT the drifted fingerprint by coincidence, and the acting band seat is real-position,
    /// carries the Dragoon's Claymore, and reads (hp,maxHp,level) matching the turn queue.
    /// <paramref name="seedFrameNameId"/> controls whether the band seat's own frame nameId
    /// (Offsets.ANameId) is seeded to the Dragoon's identity (V1/V2) or left unseeded/0 (V4).</summary>
    private static FakeSparseMemory Stage(bool seedFrameNameId)
    {
        var m = new FakeSparseMemory();
        SetActive(m, hp: 300, maxHp: 300, level: Level);
        MemSeats.SeatRoster(m, slot: 0, lvl: Level, br: StaleBrave, fa: Faith, rh: Claymore, nameId: Dragoon);
        MemSeats.SeatRoster(m, slot: 1, lvl: Level, br: DriftedBrave, fa: Faith, rh: Eclipsebolt, nameId: Archer);
        MemSeats.SeatBand(m, BandSlot, weapon: Claymore, lvl: Level, br: DriftedBrave, fa: Faith, gx: 5, gy: 5, hp: 300, maxHp: 300);
        if (seedFrameNameId) MemSeats.SeatFrameNameId(m, BandSlot, Dragoon);
        return m;
    }

    [Fact]
    public void Player_resolve_vetoes_an_fp_colliding_roster_row_with_a_foreign_nameId()
    {
        // [V1, THE regression] Pre-fix: the Dragoon's own roster row already failed
        // RosterFpMatches (brave drifted), so the Archer's row was the lone armed match and its
        // Eclipsebolt got adopted -- TryResolveActingPlayer returned true with {Eclipsebolt}.
        // Fixed: the Archer's row is vetoed too (its own nameId 502 != the seat's 501), so NO
        // roster row survives and the resolve refuses outright.
        var r = new ActorResolver(Stage(seedFrameNameId: true), Weapons);
        bool ok = r.TryResolveActingPlayer(out var ws);
        Assert.False(ok);
        Assert.DoesNotContain(Eclipsebolt, ws);
    }

    [Fact]
    public void MainHand_resolve_vetoes_an_fp_colliding_roster_row_with_a_foreign_nameId()
    {
        // [V2] Mirrors V1 for ResolveActingMainHand: pre-fix, MainHandFromRoster's `found == 1 ?
        // rh : 0` fallback returned the Archer's Eclipsebolt id. Fixed: the veto drops the
        // Archer's row before it can ever set `found`, so this returns 0.
        var r = new ActorResolver(Stage(seedFrameNameId: true), Weapons);
        Assert.Equal(0, r.ResolveActingMainHand());
    }

    [Fact]
    public void Player_and_main_hand_resolve_still_self_adopt_when_the_seat_names_its_own_row()
    {
        // [V3, self-adoption parity] The band seat's frame nameId matches its OWN roster row
        // (no drift this time -- brave, faith, level, and nameId all agree) -- the veto must NOT
        // refuse a unit resolving to its own, correctly fingerprint-matched roster row.
        var m = new FakeSparseMemory();
        SetActive(m, hp: 300, maxHp: 300, level: Level);
        MemSeats.SeatRoster(m, slot: 0, lvl: Level, br: DriftedBrave, fa: Faith, rh: Claymore, nameId: Dragoon);
        MemSeats.SeatBand(m, BandSlot, weapon: Claymore, lvl: Level, br: DriftedBrave, fa: Faith, gx: 5, gy: 5, hp: 300, maxHp: 300);
        MemSeats.SeatFrameNameId(m, BandSlot, Dragoon);
        var r = new ActorResolver(m, Weapons);
        bool ok = r.TryResolveActingPlayer(out var ws);
        Assert.True(ok);
        Assert.Equal(new List<int> { Claymore }, ws);
        Assert.Equal(Claymore, r.ResolveActingMainHand());
    }

    [Fact]
    public void Player_resolve_keeps_todays_legacy_borrow_when_the_seat_nameId_is_unreadable()
    {
        // [V4, the seatNameId-0 degradation] V1's seeding again, but the band seat's own frame
        // nameId is left unseeded (reads 0) -- the veto disarms entirely (seatNameId > 0 is
        // false), so this is a DELIBERATE degradation to today's pre-LW-252 behavior, not a bug:
        // both master and this fixed build return the same {Eclipsebolt} legacy-borrow answer.
        // The veto only closes the hole once the seat's own identity is actually readable.
        var r = new ActorResolver(Stage(seedFrameNameId: false), Weapons);
        bool ok = r.TryResolveActingPlayer(out var ws);
        Assert.True(ok);
        Assert.Equal(new List<int> { Eclipsebolt }, ws);
    }
}
