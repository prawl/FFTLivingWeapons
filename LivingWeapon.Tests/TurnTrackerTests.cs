using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// Per-unit turn counting behind the IGameMemory fake (no live game). On each rising edge of
/// the global "acted" flag the tracker credits a completed turn to the ACTIVE unit -- resolved
/// FLAGS-FIRST since LW-94 (Band.FlagOwner's exclusive ATurnFlag walk, seeded here via
/// SeatFlagOwner), then the engine actor pointer (Offsets.ActorPtr -> band entry, seeded via
/// SeedU64 with a FRAME base address), then the legacy turn-queue HP/MaxHP/level -> band-entry
/// fingerprint match (the unseeded default in these tests). Credits settle into a unit's
/// pre-level-up key when the band fingerprint drifts mid-battle. Drives timed signatures like
/// Galewind's Speed +3 for the wielder's first 3 turns.
/// </summary>
public class TurnTrackerTests
{
    /// <summary>Seat a unit in BAND slot <paramref name="bandIdx"/> AND make it the active (turn-queue) unit.
    /// TurnTracker now resolves the active unit via the band (not the static array).</summary>
    private static void SetActive(FakeSparseMemory m, int bandIdx, int hp, int maxHp, int level, int brave, int faith)
    {
        long slot = Offsets.BandReadBase + (long)bandIdx * Offsets.CombatStride;
        m.U16s[slot + Offsets.AMaxHp] = (ushort)maxHp;
        m.U16s[slot + Offsets.AHp] = (ushort)hp;
        m.U8s[slot + Offsets.ALevel] = (byte)level;
        m.U8s[slot + Offsets.ABrave] = (byte)brave;
        m.U8s[slot + Offsets.AFaith] = (byte)faith;
        m.U16s[Offsets.TurnQueue + Offsets.TqMaxHp] = (ushort)maxHp;
        m.U16s[Offsets.TurnQueue + Offsets.TqHp] = (ushort)hp;
        m.U16s[Offsets.TurnQueue + Offsets.TqLevel] = (ushort)level;
    }

    private static void Acted(FakeSparseMemory m, int v) => m.U8s[Offsets.Acted] = (byte)v;

    /// <summary>Point Offsets.ActorPtr at band slot <paramref name="bandIdx"/>'s combat FRAME base
    /// (== FrameReadBase + bandIdx*CombatStride, i.e. Band.Entry(bandIdx) - BandEntry).</summary>
    private static void PointAt(FakeSparseMemory m, int bandIdx) =>
        m.SeedU64(Offsets.ActorPtr, (ulong)(Offsets.FrameReadBase + (long)bandIdx * Offsets.CombatStride));

    /// <summary>Seat band slot <paramref name="bandIdx"/> as the PSX turn-flags owner
    /// (Band.FlagOwner): ATurnFlag=1, a sane maxHp/fingerprint so Band.IsValid accepts the
    /// entry, and a grid position (default a real one; pass gx:0, gy:0 for the frozen-mirror
    /// case). Mirrors SetActive's band-entry base arithmetic. nameId defaults to 0 (unset);
    /// pass a nonzero value only where a test needs an explicit cross-unit identity.</summary>
    private static void SeatFlagOwner(FakeSparseMemory m, int bandIdx, int level, int brave, int faith,
                                       int gx = 5, int gy = 5, int nameId = 0)
    {
        long slot = Offsets.BandReadBase + (long)bandIdx * Offsets.CombatStride;
        m.U16s[slot + Offsets.AMaxHp] = 100;
        m.U8s[slot + Offsets.ALevel] = (byte)level;
        m.U8s[slot + Offsets.ABrave] = (byte)brave;
        m.U8s[slot + Offsets.AFaith] = (byte)faith;
        m.U8s[slot + Offsets.AGx] = (byte)gx;
        m.U8s[slot + Offsets.AGy] = (byte)gy;
        m.U8s[slot + Offsets.ATurnFlag] = 1;
        if (nameId != 0) m.U16s[slot + Offsets.ANameId] = (ushort)nameId;
    }

    [Fact]
    public void Counts_one_turn_per_acted_rising_edge()
    {
        var m = new FakeSparseMemory();
        var t = new TurnTracker(m);
        SetActive(m, 5, hp: 100, maxHp: 100, level: 20, brave: 70, faith: 50);
        Acted(m, 0); t.Poll();
        Assert.Equal(0, t.Turns(20, 70, 50));
        Acted(m, 1); t.Poll();                       // rising edge -> +1
        Assert.Equal(1, t.Turns(20, 70, 50));
        t.Poll();                                    // still 1, no new edge
        Assert.Equal(1, t.Turns(20, 70, 50));
        Acted(m, 0); t.Poll();
        Acted(m, 1); t.Poll();                       // rises again -> +1
        Assert.Equal(2, t.Turns(20, 70, 50));
    }

    [Fact]
    public void Tracks_turns_per_unit_independently()
    {
        var m = new FakeSparseMemory();
        var t = new TurnTracker(m);
        SetActive(m, 5, 100, 100, 20, 70, 50); Acted(m, 1); t.Poll();
        Acted(m, 0); t.Poll();
        SetActive(m, 6, 80, 80, 15, 60, 40); Acted(m, 1); t.Poll();
        Assert.Equal(1, t.Turns(20, 70, 50));
        Assert.Equal(1, t.Turns(15, 60, 40));
        Assert.Equal(0, t.Turns(99, 99, 99));        // a unit that never acted
    }

    [Fact]
    public void ResetBattle_clears_counts()
    {
        var m = new FakeSparseMemory();
        var t = new TurnTracker(m);
        SetActive(m, 5, 100, 100, 20, 70, 50); Acted(m, 1); t.Poll();
        Assert.Equal(1, t.Turns(20, 70, 50));
        t.ResetBattle();
        Assert.Equal(0, t.Turns(20, 70, 50));
    }

    [Fact]
    public void GlobalTurns_ticks_on_every_acted_edge_even_when_the_actor_is_unresolvable()
    {
        // The attribution-FREE clock: a turn-edge bumps GlobalTurns whether or not we can fingerprint
        // who acted. This is what lets a buff timer (Larceny) be immune to a parked wielder -- the
        // world's turns keep coming even when the held unit's own turn never does.
        var m = new FakeSparseMemory();
        var t = new TurnTracker(m);
        // Active unit named in the turn queue, but NO matching band entry (attribution fails).
        m.U16s[Offsets.TurnQueue + Offsets.TqMaxHp] = 100;
        m.U16s[Offsets.TurnQueue + Offsets.TqHp] = 100;
        m.U16s[Offsets.TurnQueue + Offsets.TqLevel] = 20;

        Assert.Equal(0, t.GlobalTurns);
        Acted(m, 1); t.Poll();                 // rising edge, unresolvable actor
        Assert.Equal(1, t.GlobalTurns);         // global clock advances anyway...
        Assert.Equal(0, t.Turns(20, 70, 50));   // ...while no per-unit turn is credited
        t.Poll();                               // no new edge -> no change
        Assert.Equal(1, t.GlobalTurns);
        Acted(m, 0); t.Poll();
        Acted(m, 1); t.Poll();                  // another edge
        Assert.Equal(2, t.GlobalTurns);

        t.ResetBattle();
        Assert.Equal(0, t.GlobalTurns);         // cleared on the battle boundary
    }

    [Fact]
    public void Unresolvable_active_unit_counts_nothing()
    {
        var m = new FakeSparseMemory();
        var t = new TurnTracker(m);
        // Active unit named in the turn queue, but NO matching band entry.
        m.U16s[Offsets.TurnQueue + Offsets.TqMaxHp] = 100;
        m.U16s[Offsets.TurnQueue + Offsets.TqHp] = 100;
        m.U16s[Offsets.TurnQueue + Offsets.TqLevel] = 20;
        Acted(m, 1);
        t.Poll();   // must not throw, must not count
        Assert.Equal(0, t.Turns(20, 70, 50));
    }

    [Fact]
    public void Ambiguous_distinct_fingerprints_credits_nothing()
    {
        // Two band entries match the turn-queue HP/MaxHP/level but have DIFFERENT (lvl,brave,faith)
        // fingerprints -- ambiguous actor -> no turn credited (miss beats mis-credit).
        var m = new FakeSparseMemory();
        var t = new TurnTracker(m);
        // Two units, same HP/MaxHP/level, different brave/faith.
        long s1 = Offsets.BandReadBase + (long)5 * Offsets.CombatStride;
        long s2 = Offsets.BandReadBase + (long)6 * Offsets.CombatStride;
        m.U16s[s1 + Offsets.AMaxHp] = 100; m.U16s[s1 + Offsets.AHp] = 100;
        m.U8s[s1 + Offsets.ALevel] = 20; m.U8s[s1 + Offsets.ABrave] = 70; m.U8s[s1 + Offsets.AFaith] = 50;
        m.U16s[s2 + Offsets.AMaxHp] = 100; m.U16s[s2 + Offsets.AHp] = 100;
        m.U8s[s2 + Offsets.ALevel] = 20; m.U8s[s2 + Offsets.ABrave] = 60; m.U8s[s2 + Offsets.AFaith] = 40;
        m.U16s[Offsets.TurnQueue + Offsets.TqMaxHp] = 100;
        m.U16s[Offsets.TurnQueue + Offsets.TqHp] = 100;
        m.U16s[Offsets.TurnQueue + Offsets.TqLevel] = 20;
        Acted(m, 1); t.Poll();

        Assert.Equal(0, t.Turns(20, 70, 50));   // ambiguous -> nothing credited
        Assert.Equal(0, t.Turns(20, 60, 40));
    }

    // ---- Pointer-first attribution (T1-T5) ----

    [Fact]
    public void Pointer_resolves_ambiguous_actor()
    {
        // T1 LOAD-BEARING: the EXACT setup of Ambiguous_distinct_fingerprints_credits_nothing
        // (two band entries, same mhp/hp/lvl, different br/fa -- the TQ-fingerprint path bails)
        // PLUS the engine actor pointer naming s1's frame. Non-vacuity: the sibling test above
        // proves this exact setup credits NOTHING without the pointer.
        var m = new FakeSparseMemory();
        var t = new TurnTracker(m);
        long s1 = Offsets.BandReadBase + (long)5 * Offsets.CombatStride;
        long s2 = Offsets.BandReadBase + (long)6 * Offsets.CombatStride;
        m.U16s[s1 + Offsets.AMaxHp] = 100; m.U16s[s1 + Offsets.AHp] = 100;
        m.U8s[s1 + Offsets.ALevel] = 20; m.U8s[s1 + Offsets.ABrave] = 70; m.U8s[s1 + Offsets.AFaith] = 50;
        m.U16s[s2 + Offsets.AMaxHp] = 100; m.U16s[s2 + Offsets.AHp] = 100;
        m.U8s[s2 + Offsets.ALevel] = 20; m.U8s[s2 + Offsets.ABrave] = 60; m.U8s[s2 + Offsets.AFaith] = 40;
        m.U16s[Offsets.TurnQueue + Offsets.TqMaxHp] = 100;
        m.U16s[Offsets.TurnQueue + Offsets.TqHp] = 100;
        m.U16s[Offsets.TurnQueue + Offsets.TqLevel] = 20;
        PointAt(m, 5);   // engine names s1 as the actor -- resolves the ambiguity
        Acted(m, 1); t.Poll();

        Assert.Equal(1, t.Turns(20, 70, 50));
        Assert.Equal(0, t.Turns(20, 60, 40));
    }

    [Fact]
    public void Pointer_wins_over_disagreeing_fingerprint()
    {
        // Pointer names s1; the TQ tuple unambiguously matches s2 (different maxHp) -> the
        // pointer path wins (D1: pointer wins when both would resolve).
        var m = new FakeSparseMemory();
        var t = new TurnTracker(m);
        long s1 = Offsets.BandReadBase + (long)5 * Offsets.CombatStride;
        long s2 = Offsets.BandReadBase + (long)6 * Offsets.CombatStride;
        m.U16s[s1 + Offsets.AMaxHp] = 300; m.U16s[s1 + Offsets.AHp] = 300;
        m.U8s[s1 + Offsets.ALevel] = 30; m.U8s[s1 + Offsets.ABrave] = 65; m.U8s[s1 + Offsets.AFaith] = 60;
        m.U16s[s2 + Offsets.AMaxHp] = 100; m.U16s[s2 + Offsets.AHp] = 100;
        m.U8s[s2 + Offsets.ALevel] = 20; m.U8s[s2 + Offsets.ABrave] = 60; m.U8s[s2 + Offsets.AFaith] = 40;
        // TQ tuple unambiguously matches s2 only.
        m.U16s[Offsets.TurnQueue + Offsets.TqMaxHp] = 100;
        m.U16s[Offsets.TurnQueue + Offsets.TqHp] = 100;
        m.U16s[Offsets.TurnQueue + Offsets.TqLevel] = 20;
        PointAt(m, 5);   // engine names s1 -- must win over the s2 TQ match
        Acted(m, 1); t.Poll();

        Assert.Equal(1, t.Turns(30, 65, 60));   // s1 credited
        Assert.Equal(0, t.Turns(20, 60, 40));   // s2 NOT credited
    }

    [Fact]
    public void Garbage_pointer_falls_back()
    {
        // Unaligned pointer -> falls back to the TQ path.
        var m1 = new FakeSparseMemory();
        var t1 = new TurnTracker(m1);
        SetActive(m1, 5, hp: 100, maxHp: 100, level: 20, brave: 70, faith: 50);
        m1.SeedU64(Offsets.ActorPtr, (ulong)(Offsets.FrameReadBase + 0x123));   // unaligned
        Acted(m1, 1); t1.Poll();
        Assert.Equal(1, t1.Turns(20, 70, 50));

        // Below FrameReadBase -> falls back.
        var m2 = new FakeSparseMemory();
        var t2 = new TurnTracker(m2);
        SetActive(m2, 5, hp: 100, maxHp: 100, level: 20, brave: 70, faith: 50);
        m2.SeedU64(Offsets.ActorPtr, (ulong)(Offsets.FrameReadBase - Offsets.CombatStride));
        Acted(m2, 1); t2.Poll();
        Assert.Equal(1, t2.Turns(20, 70, 50));

        // Seat >= BandSlots -> falls back.
        var m3 = new FakeSparseMemory();
        var t3 = new TurnTracker(m3);
        SetActive(m3, 5, hp: 100, maxHp: 100, level: 20, brave: 70, faith: 50);
        m3.SeedU64(Offsets.ActorPtr, (ulong)(Offsets.FrameReadBase + (long)Offsets.BandSlots * Offsets.CombatStride));
        Acted(m3, 1); t3.Poll();
        Assert.Equal(1, t3.Turns(20, 70, 50));

        // Garbage pointer AND no TQ match -> nothing credited, no throw.
        var m4 = new FakeSparseMemory();
        var t4 = new TurnTracker(m4);
        m4.SeedU64(Offsets.ActorPtr, (ulong)(Offsets.FrameReadBase + 0x123));
        Acted(m4, 1); t4.Poll();
        Assert.Equal(0, t4.Turns(20, 70, 50));
    }

    [Fact]
    public void Pointer_to_invalid_entry_falls_back()
    {
        // Aligned, in-range pointer, but the pointed-to entry fails Band.IsValid (lvl 0) ->
        // falls back to the TQ path (seeded with a clean unambiguous match).
        var m = new FakeSparseMemory();
        var t = new TurnTracker(m);
        SetActive(m, 5, hp: 100, maxHp: 100, level: 20, brave: 70, faith: 50);
        PointAt(m, 7);   // slot 7 is aligned + in-range but never seeded -> lvl reads 0 -> IsValid fails
        Acted(m, 1); t.Poll();
        Assert.Equal(1, t.Turns(20, 70, 50));
    }

    // ---- flight-recorder tap (optional injected recorder; null default keeps every OTHER test
    // in this file green unmodified -- that fact is the real assertion for this tap seam) ----

    [Fact]
    public void Injected_recorder_receives_acted_edges_and_credit()
    {
        var m = new FakeSparseMemory();
        var recorded = new List<(string type, string payload)>();
        var t = new TurnTracker(m, (type, payload) => recorded.Add((type, payload)));
        SetActive(m, 5, hp: 100, maxHp: 100, level: 20, brave: 70, faith: 50);

        Acted(m, 1); t.Poll();
        Assert.Contains(recorded, r => r.type == "turn" && r.payload.Contains("rising edge"));
        Assert.Contains(recorded, r => r.type == "turn" && r.payload.StartsWith("credit level=20 brave=70 faith=50 count=1"));

        Acted(m, 0); t.Poll();
        Assert.Contains(recorded, r => r.type == "turn" && r.payload.Contains("falling edge"));
    }

    [Fact]
    public void Pointer_credits_despite_garbage_turn_queue()
    {
        // Pointer names s1; the TQ level is seeded 199 (the observed +100 flake, live 2026-07-01)
        // which the fingerprint path would reject outright (level > 99) -- the pointer path
        // does not depend on the TQ tuple at all, so s1 is still credited.
        var m = new FakeSparseMemory();
        var t = new TurnTracker(m);
        long s1 = Offsets.BandReadBase + (long)5 * Offsets.CombatStride;
        m.U16s[s1 + Offsets.AMaxHp] = 100; m.U16s[s1 + Offsets.AHp] = 100;
        m.U8s[s1 + Offsets.ALevel] = 20; m.U8s[s1 + Offsets.ABrave] = 70; m.U8s[s1 + Offsets.AFaith] = 50;
        m.U16s[Offsets.TurnQueue + Offsets.TqMaxHp] = 100;
        m.U16s[Offsets.TurnQueue + Offsets.TqHp] = 100;
        m.U16s[Offsets.TurnQueue + Offsets.TqLevel] = 199;   // the +100 flake
        PointAt(m, 5);
        Acted(m, 1); t.Poll();

        Assert.Equal(1, t.Turns(20, 70, 50));
    }

    // ---- Flags-first attribution (LW-94, T1-T8) ----

    [Fact]
    public void Flags_owner_wins_over_the_actor_pointer()
    {
        // T1 LOAD-BEARING: encodes the live tape (LW-94) -- the engine's own ActorPtr PARKS on
        // the action's TARGET at a caster's acted edge (a healer's own turns were credited to
        // its heal target four times over on tape), so pointer-first mis-credits the target.
        // Flags-first (Band.FlagOwner) names the true actor structurally and must win.
        // Non-vacuity: Pointer_wins_over_disagreeing_fingerprint above proves the pointer alone
        // (no flags owner present) DOES win over a disagreeing signal, so this is a real flip.
        var m = new FakeSparseMemory();
        var t = new TurnTracker(m);
        SeatFlagOwner(m, 5, level: 20, brave: 70, faith: 50);                     // unit A: the true actor
        SetActive(m, 6, hp: 80, maxHp: 80, level: 15, brave: 60, faith: 40);      // unit B: the pointer's (wrong) target
        PointAt(m, 6);                                                            // ActorPtr parked on B
        Acted(m, 1); t.Poll();

        Assert.Equal(1, t.Turns(20, 70, 50));   // A credited
        Assert.Equal(0, t.Turns(15, 60, 40));   // B NOT credited
    }

    [Fact]
    public void Two_disagreeing_flag_owners_falls_through_to_the_pointer()
    {
        // T2: two t=1 owners with distinct fingerprints -> Band.FlagOwner bails (ambiguous,
        // miss beats mis-credit) and the pointer lane resolves instead, unchanged.
        var m = new FakeSparseMemory();
        var t = new TurnTracker(m);
        SeatFlagOwner(m, 5, level: 20, brave: 70, faith: 50);
        SeatFlagOwner(m, 6, level: 15, brave: 60, faith: 40);
        PointAt(m, 6);   // pointer names slot 6 -- credited once the flags lane refuses
        Acted(m, 1); t.Poll();

        Assert.Equal(1, t.Turns(15, 60, 40));
        Assert.Equal(0, t.Turns(20, 70, 50));
    }

    [Fact]
    public void No_flag_owner_falls_through_to_the_pointer()
    {
        // T3: no ATurnFlag anywhere (the unseeded-zero default) -> the flags lane refuses and
        // today's pointer-first behavior is unchanged. Should pass before and after Part 1.
        var m = new FakeSparseMemory();
        var t = new TurnTracker(m);
        SetActive(m, 5, hp: 100, maxHp: 100, level: 20, brave: 70, faith: 50);
        PointAt(m, 5);
        Acted(m, 1); t.Poll();

        Assert.Equal(1, t.Turns(20, 70, 50));
    }

    [Fact]
    public void Zero_position_flag_owner_is_never_a_winner()
    {
        // T4: D2b -- a frozen (0,0) band mirror can carry a stale t=1 with no external anchor
        // to catch it, so it is never a candidate; it can neither win alone nor block the
        // pointer lane from resolving unit B.
        var m = new FakeSparseMemory();
        var t = new TurnTracker(m);
        SeatFlagOwner(m, 5, level: 20, brave: 70, faith: 50, gx: 0, gy: 0);
        SetActive(m, 6, hp: 80, maxHp: 80, level: 15, brave: 60, faith: 40);
        PointAt(m, 6);
        Acted(m, 1); t.Poll();

        Assert.Equal(1, t.Turns(15, 60, 40));
        Assert.Equal(0, t.Turns(20, 70, 50));
    }

    [Fact]
    public void Drift_settle_keeps_crediting_the_pre_levelup_key()
    {
        // T5: LW-94 part 2. A mid-battle level-up drifts the BAND fingerprint up (36 -> 37 on
        // tape) while consumers (Renewal.cs) query the pre-battle ROSTER fingerprint, which
        // stays at the old level for the whole battle -- the count must keep landing on the
        // pre-drift key rather than resetting under a fresh one.
        var m = new FakeSparseMemory();
        var t = new TurnTracker(m);
        SetActive(m, 5, hp: 100, maxHp: 100, level: 36, brave: 70, faith: 50);
        Acted(m, 1); t.Poll();
        Acted(m, 0); t.Poll();
        Acted(m, 1); t.Poll();
        Assert.Equal(2, t.Turns(36, 70, 50));

        Acted(m, 0); t.Poll();
        SetActive(m, 5, hp: 100, maxHp: 100, level: 37, brave: 70, faith: 50);   // mid-battle level-up
        Acted(m, 1); t.Poll();

        Assert.Equal(3, t.Turns(36, 70, 50));
        Assert.Equal(0, t.Turns(37, 70, 50));
    }

    [Fact]
    public void No_existing_lower_level_key_credits_its_own_level()
    {
        // T6: no false settle. The settle rule only fires when the (level-1) key already
        // exists in the map; a genuinely fresh unit at level 37 with no prior level-36 count
        // is credited under its own key (the accepted collision caveat cuts the other way --
        // this proves the common case still keys correctly).
        var m = new FakeSparseMemory();
        var t = new TurnTracker(m);
        SetActive(m, 5, hp: 100, maxHp: 100, level: 37, brave: 70, faith: 50);
        Acted(m, 1); t.Poll();

        Assert.Equal(1, t.Turns(37, 70, 50));
    }

    [Fact]
    public void GlobalTurns_advances_on_a_flags_lane_edge_too()
    {
        // T7: the attribution-free clock doesn't care which lane resolved the actor.
        var m = new FakeSparseMemory();
        var t = new TurnTracker(m);
        SeatFlagOwner(m, 5, level: 20, brave: 70, faith: 50);
        Acted(m, 1); t.Poll();

        Assert.Equal(1, t.GlobalTurns);
    }

    [Fact]
    public void Injected_recorder_tags_a_flags_lane_credit_as_turn_flags()
    {
        // T8: the flight-recorder tap's source tag for the flags lane must read "turn-flags"
        // (matches the existing KillTracker vocabulary), byte-distinct from "actor-ptr" and
        // "tq-fallback" for the other two lanes.
        var m = new FakeSparseMemory();
        var recorded = new List<(string type, string payload)>();
        var t = new TurnTracker(m, (type, payload) => recorded.Add((type, payload)));
        SeatFlagOwner(m, 5, level: 20, brave: 70, faith: 50);

        Acted(m, 1); t.Poll();

        Assert.Contains(recorded, r => r.type == "turn" && r.payload == "credit level=20 brave=70 faith=50 count=1 src=turn-flags");
    }
}
