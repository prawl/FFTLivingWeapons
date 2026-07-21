using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// ActorRegister unit suite (P6 of the kill-attribution plan): the tick-driven ownership
/// tracker behind ActorResolver's register-first preamble. No KillTracker/ActorResolver
/// involved here -- pure register behavior, driven directly against a FakeSparseMemory.
/// </summary>
public class ActorRegisterTests
{
    /// <summary>Point Offsets.ActorPtr at band slot <paramref name="bandIdx"/>'s combat FRAME
    /// base (mirrors TurnTrackerTests.PointAt).</summary>
    private static void PointAt(FakeSparseMemory m, int bandIdx) =>
        m.SeedU64(Offsets.ActorPtr, (ulong)(Offsets.FrameReadBase + (long)bandIdx * Offsets.CombatStride));

    // --- priming (V2) ---

    [Fact]
    public void First_update_never_trusts_the_priming_read()
    {
        var m = new FakeSparseMemory();
        PointAt(m, 5);   // pointer already parked BEFORE any Update() call
        var r = new ActorRegister(m);

        r.Update();   // priming tick

        Assert.False(r.Trusted);
        Assert.False(r.StableSince(int.MaxValue));
    }

    [Fact]
    public void Observed_transition_stamps_a_trusted_arrival()
    {
        var m = new FakeSparseMemory();
        var r = new ActorRegister(m);
        r.Update();          // tick1: priming, pointer unseeded (reads 0)
        PointAt(m, 5);
        r.Update();          // tick2: observed transition 0 -> seat5's entry

        Assert.True(r.Trusted);
        Assert.Equal(2, r.ArrivalTick);
        Assert.Equal(Band.Entry(5), r.CurrentEntry);
    }

    [Fact]
    public void Unchanged_pointer_does_not_restamp_arrival()
    {
        var m = new FakeSparseMemory();
        var r = new ActorRegister(m);
        r.Update();          // tick1 priming
        PointAt(m, 5);
        r.Update();          // tick2 arrival
        r.Update();          // tick3 unchanged
        r.Update();          // tick4 unchanged

        Assert.Equal(2, r.ArrivalTick);
        Assert.Equal(4, r.Tick);
    }

    // --- flight-recorder tap (optional injected recorder; null default keeps every OTHER test in
    // this file green unmodified -- that fact is the real assertion for this tap seam) ---

    [Fact]
    public void Injected_recorder_receives_pointer_transitions()
    {
        var m = new FakeSparseMemory();
        var recorded = new List<(string type, string payload)>();
        var r = new ActorRegister(m, (type, payload) => recorded.Add((type, payload)));
        r.Update();          // tick1: priming -- must NOT record (there is no real transition yet)
        Assert.Empty(recorded);

        PointAt(m, 5);
        r.Update();          // tick2: observed transition 0 -> seat5's entry

        Assert.Contains(recorded, e => e.type == "actor" && e.payload.Contains("pointer transition")
                                        && e.payload.Contains("nameId="));
    }

    // --- StableSince (V3, strict) ---

    [Fact]
    public void StableSince_is_strictly_before_not_at_or_after()
    {
        var m = new FakeSparseMemory();
        var r = new ActorRegister(m);
        r.Update();          // tick1 priming
        PointAt(m, 5);
        r.Update();          // tick2 arrival, ArrivalTick=2

        Assert.False(r.StableSince(2));   // same tick as arrival -> NOT stable (the ambiguous case)
        Assert.False(r.StableSince(1));   // before the arrival even happened -> not stable
        Assert.True(r.StableSince(3));    // strictly after arrival -> stable
    }

    [Fact]
    public void StableSince_false_when_never_trusted()
    {
        var m = new FakeSparseMemory();
        PointAt(m, 5);
        var r = new ActorRegister(m);
        r.Update();   // priming only -- pointer never actually transitions after this
        r.Update();   // unchanged -- still no trusted arrival ever stamped

        Assert.False(r.StableSince(int.MaxValue));
    }

    // --- ResetBattle ---

    [Fact]
    public void ResetBattle_clears_trust_and_re_primes()
    {
        var m = new FakeSparseMemory();
        var r = new ActorRegister(m);
        r.Update();
        PointAt(m, 5);
        r.Update();
        Assert.True(r.Trusted);

        r.ResetBattle();
        Assert.Equal(0, r.Tick);
        Assert.Equal(0, r.CurrentEntry);
        Assert.False(r.Trusted);

        // The pointer is STILL parked at seat 5 (unchanged across the reset) -- the next Update()
        // is a FRESH priming read, not a continuation, and must not trust it either.
        r.Update();   // post-reset tick1: priming
        Assert.False(r.Trusted);
    }

    // --- roster bridge (D1 + V4 stat cross-check + V5 tri-state) ---

    [Fact]
    public void Bridge_resolves_player_on_single_nameid_and_stat_match()
    {
        var m = new FakeSparseMemory();
        MemSeats.SeatBand(m, 5, weapon: 0, lvl: 50, br: 60, fa: 70, gx: 1, gy: 1);
        MemSeats.SeatFrameNameId(m, 5, nameId: 42);
        MemSeats.SeatRoster(m, slot: 2, lvl: 50, br: 60, fa: 70, rh: 999, nameId: 42);
        var r = new ActorRegister(m);
        r.Update();
        PointAt(m, 5);
        r.Update();

        Assert.Equal(RosterBridge.Player, r.CurrentBridge);
        Assert.Equal(Offsets.RosterBase + 2L * Offsets.RosterStride, r.CurrentRosterBase);
    }

    [Fact]
    public void Bridge_resolves_enemy_on_zero_matches()
    {
        var m = new FakeSparseMemory();
        MemSeats.SeatBand(m, 5, weapon: 0, lvl: 50, br: 60, fa: 70, gx: 1, gy: 1);
        MemSeats.SeatFrameNameId(m, 5, nameId: 918);   // no roster slot carries this nameId
        MemSeats.SeatRoster(m, slot: 2, lvl: 50, br: 60, fa: 70, rh: 999, nameId: 42);
        var r = new ActorRegister(m);
        r.Update();
        PointAt(m, 5);
        r.Update();

        Assert.Equal(RosterBridge.Enemy, r.CurrentBridge);
        Assert.Equal(0, r.CurrentRosterBase);
    }

    [Fact]
    public void Bridge_nameid_zero_is_the_trap_and_resolves_unknown()
    {
        // 0==0 must never bridge to Player -- but it also must NOT be treated as a confident
        // "Enemy" (which would authoritatively suppress the TQ fallback for what could be a
        // genuine player whose nameId capture simply failed). It resolves Unknown: the gate is
        // unsatisfied and callers fall through to TQ (P5 of the kill-attribution plan).
        var m = new FakeSparseMemory();
        MemSeats.SeatBand(m, 5, weapon: 0, lvl: 50, br: 60, fa: 70, gx: 1, gy: 1);
        // Frame nameId left unseeded -> reads 0. Roster slot's nameId is also unseeded (0).
        MemSeats.SeatRoster(m, slot: 2, lvl: 50, br: 60, fa: 70, rh: 999);
        var r = new ActorRegister(m);
        r.Update();
        PointAt(m, 5);
        r.Update();

        Assert.Equal(RosterBridge.Unknown, r.CurrentBridge);
    }

    [Fact]
    public void Bridge_resolves_unknown_on_nameid_collision()
    {
        var m = new FakeSparseMemory();
        MemSeats.SeatBand(m, 5, weapon: 0, lvl: 50, br: 60, fa: 70, gx: 1, gy: 1);
        MemSeats.SeatFrameNameId(m, 5, nameId: 42);
        MemSeats.SeatRoster(m, slot: 2, lvl: 50, br: 60, fa: 70, rh: 999, nameId: 42);
        MemSeats.SeatRoster(m, slot: 3, lvl: 50, br: 60, fa: 70, rh: 888, nameId: 42);   // duplicated nameId
        var r = new ActorRegister(m);
        r.Update();
        PointAt(m, 5);
        r.Update();

        Assert.Equal(RosterBridge.Unknown, r.CurrentBridge);
    }

    [Fact]
    public void Bridge_requires_stat_match_even_with_nameid_match()
    {
        // V4: the nameId pool overlap between players and enemies is NOT proven disjoint -- the
        // bridge must ALSO cross-check (level,brave,faith) so a coincidental nameId collision
        // alone can never fabricate a Player match.
        var m = new FakeSparseMemory();
        MemSeats.SeatBand(m, 5, weapon: 0, lvl: 50, br: 60, fa: 70, gx: 1, gy: 1);
        MemSeats.SeatFrameNameId(m, 5, nameId: 42);
        MemSeats.SeatRoster(m, slot: 2, lvl: 20, br: 30, fa: 40, rh: 999, nameId: 42);   // nameId matches, stats don't

        var r = new ActorRegister(m);
        r.Update();
        PointAt(m, 5);
        r.Update();

        Assert.Equal(RosterBridge.Enemy, r.CurrentBridge);
    }

    // --- CurrentFp (V10: lazy, not an arrival snapshot) ---

    [Fact]
    public void CurrentFp_reads_live_off_entry_bytes_not_an_arrival_snapshot()
    {
        var m = new FakeSparseMemory();
        MemSeats.SeatBand(m, 5, weapon: 0, lvl: 50, br: 60, fa: 70, gx: 1, gy: 1);
        var r = new ActorRegister(m);
        r.Update();
        PointAt(m, 5);
        r.Update();
        Assert.Equal((50, 60, 70), r.CurrentFp);

        // Mid-turn level-up: the SAME entry's level changes with no new pointer arrival.
        m.U8s[Band.Entry(5) + Offsets.ALevel] = 51;

        Assert.Equal((51, 60, 70), r.CurrentFp);   // lazy read reflects the change immediately
    }

    [Fact]
    public void CurrentFp_is_default_with_no_owner()
    {
        var m = new FakeSparseMemory();
        var r = new ActorRegister(m);
        r.Update();   // priming only -- pointer reads 0, never trusted

        Assert.Equal(default, r.CurrentFp);
    }

    // --- canonical-signature fingerprint rescue (LW-56 fault 2) ---

    [Fact]
    public void Bridge_rescues_a_canonical_scripted_unit_on_a_unique_fingerprint_match()
    {
        // LOAD-BEARING: a scripted opener unit carries a canonical nameId (== its own job byte)
        // that matches no roster RNameId at all, so Bridge's own nameId+stat loop finds zero
        // matches. The rescue must turn that zero-evidence Enemy into a positively-identified
        // Player via a strict unique bare-fingerprint match plus weapon agreement.
        const int weapon = 64;
        var m = new FakeSparseMemory();
        MemSeats.SeatBand(m, 5, weapon: weapon, lvl: 8, br: 63, fa: 60, gx: 1, gy: 1);
        MemSeats.SeatFrameNameId(m, 5, nameId: 2);
        MemSeats.SeatBandJob(m, 5, job: 2);          // canonical signature: frame nameId == job byte
        MemSeats.SeatRoster(m, slot: 0, lvl: 1, br: 63, fa: 60, rh: weapon, nameId: 1);   // RNameId != 2
        var r = new ActorRegister(m);
        r.Update();
        PointAt(m, 5);
        r.Update();

        Assert.Equal(RosterBridge.Player, r.CurrentBridge);
        Assert.Equal(Offsets.RosterBase + 0L * Offsets.RosterStride, r.CurrentRosterBase);
        Assert.Equal(RescueOutcome.Unique, r.CurrentRescue);
    }

    [Fact]
    public void Bridge_rescue_refuses_a_non_canonical_zero_match_even_on_a_unique_fingerprint_match()
    {
        // THE NON-VACUOUS NEGATIVE: identical geometry to the rescue above, but the job byte is
        // seeded to a DIFFERENT nonzero value than nameId, pinning the actual comparison rather
        // than the fake's default-zero job byte. Breaking only the canonical gate flips exactly
        // this test while the rescue test above stays green.
        const int weapon = 64;
        var m = new FakeSparseMemory();
        MemSeats.SeatBand(m, 5, weapon: weapon, lvl: 8, br: 63, fa: 60, gx: 1, gy: 1);
        MemSeats.SeatFrameNameId(m, 5, nameId: 918);
        MemSeats.SeatBandJob(m, 5, job: 77);         // nonzero but disagrees with nameId, not canonical
        MemSeats.SeatRoster(m, slot: 0, lvl: 1, br: 63, fa: 60, rh: weapon, nameId: 1);
        var r = new ActorRegister(m);
        r.Update();
        PointAt(m, 5);
        r.Update();

        Assert.Equal(RosterBridge.Enemy, r.CurrentBridge);
        Assert.Equal(RescueOutcome.NotCanonical, r.CurrentRescue);
    }

    [Fact]
    public void Bridge_rescue_refuses_an_ambiguous_fingerprint_match()
    {
        const int weapon = 64;
        var m = new FakeSparseMemory();
        MemSeats.SeatBand(m, 5, weapon: weapon, lvl: 8, br: 63, fa: 60, gx: 1, gy: 1);
        MemSeats.SeatFrameNameId(m, 5, nameId: 2);
        MemSeats.SeatBandJob(m, 5, job: 2);
        MemSeats.SeatRoster(m, slot: 0, lvl: 1, br: 63, fa: 60, rh: weapon, nameId: 1);
        MemSeats.SeatRoster(m, slot: 1, lvl: 3, br: 63, fa: 60, rh: weapon, nameId: 1);   // 2nd bare-fp match
        var r = new ActorRegister(m);
        r.Update();
        PointAt(m, 5);
        r.Update();

        Assert.Equal(RosterBridge.Enemy, r.CurrentBridge);
        Assert.Equal(RescueOutcome.Ambiguous, r.CurrentRescue);
    }

    [Fact]
    public void Bridge_rescue_refuses_when_no_fingerprint_row_matches()
    {
        // Round 2: a zero-fp-match refusal now falls through to the weapon key, so this fixture
        // must ALSO deny the weapon key (the roster row holds a DIFFERENT weapon than the band's
        // own) to stay a genuine universal negative, since otherwise the weapon key would rescue it.
        const int weapon = 64, otherWeapon = 12;
        var m = new FakeSparseMemory();
        MemSeats.SeatBand(m, 5, weapon: weapon, lvl: 8, br: 63, fa: 60, gx: 1, gy: 1);
        MemSeats.SeatFrameNameId(m, 5, nameId: 2);
        MemSeats.SeatBandJob(m, 5, job: 2);
        MemSeats.SeatRoster(m, slot: 0, lvl: 1, br: 10, fa: 10, rh: otherWeapon, nameId: 1);   // brave/faith differ, and the hands lack the band weapon
        var r = new ActorRegister(m);
        r.Update();
        PointAt(m, 5);
        r.Update();

        Assert.Equal(RosterBridge.Enemy, r.CurrentBridge);
        Assert.Equal(RescueOutcome.NoMatch, r.CurrentRescue);
    }

    [Fact]
    public void Bridge_rescue_refuses_a_bare_fingerprint_at_drift_ten()
    {
        // Drift boundary: MaxLevelDrift is 9 (one-sided). Live level 11 vs roster level 1 is a
        // drift of 10, one past the allowed window, so LevelMatchesRoster must refuse even
        // though brave/faith agree exactly.
        const int weapon = 64;
        var m = new FakeSparseMemory();
        MemSeats.SeatBand(m, 5, weapon: weapon, lvl: 11, br: 63, fa: 60, gx: 1, gy: 1);
        MemSeats.SeatFrameNameId(m, 5, nameId: 2);
        MemSeats.SeatBandJob(m, 5, job: 2);
        MemSeats.SeatRoster(m, slot: 0, lvl: 1, br: 63, fa: 60, rh: weapon, nameId: 1);
        var r = new ActorRegister(m);
        r.Update();
        PointAt(m, 5);
        r.Update();

        Assert.Equal(RosterBridge.Enemy, r.CurrentBridge);
        Assert.Equal(RescueOutcome.NoMatch, r.CurrentRescue);
    }

    [Fact]
    public void Bridge_rescue_refuses_on_weapon_disagreement()
    {
        const int weapon = 64, otherWeapon = 12;
        var m = new FakeSparseMemory();
        MemSeats.SeatBand(m, 5, weapon: weapon, lvl: 8, br: 63, fa: 60, gx: 1, gy: 1);
        MemSeats.SeatFrameNameId(m, 5, nameId: 2);
        MemSeats.SeatBandJob(m, 5, job: 2);
        MemSeats.SeatRoster(m, slot: 0, lvl: 1, br: 63, fa: 60, rh: otherWeapon, nameId: 1);  // hands lack `weapon`
        var r = new ActorRegister(m);
        r.Update();
        PointAt(m, 5);
        r.Update();

        Assert.Equal(RosterBridge.Enemy, r.CurrentBridge);
        Assert.Equal(RescueOutcome.WpnMismatch, r.CurrentRescue);
    }

    // --- weapon-unique secondary key (LW-56 fault 2, round 2) ---

    [Fact]
    public void Bridge_rescues_a_canonical_unit_on_a_unique_weapon_match_when_the_fingerprint_diverges()
    {
        // THE LIVE GEOMETRY (2026-07-10 opener tape): the scripted trio's fingerprint never
        // matches any roster row (brave/faith diverge from the roster's pre-battle stats), so the
        // fingerprint key finds zero matches; only the weapon key can bridge them.
        // Non-vacuity: breaking only the weapon-key scan (e.g. requiring 2 matches) fails exactly
        // this test while the fp-key Unique positive above stays green.
        const int weapon = 22;
        var m = new FakeSparseMemory();
        MemSeats.SeatBand(m, 5, weapon: weapon, lvl: 9, br: 71, fa: 63, gx: 1, gy: 1);
        MemSeats.SeatFrameNameId(m, 5, nameId: 2);
        MemSeats.SeatBandJob(m, 5, job: 2);
        MemSeats.SeatRoster(m, slot: 0, lvl: 1, br: 70, fa: 70, rh: weapon, nameId: 1);    // fp diverges, weapon agrees
        MemSeats.SeatRoster(m, slot: 1, lvl: 1, br: 1, fa: 1, rh: 999, nameId: 1);         // occupied, does not hold weapon 22
        var r = new ActorRegister(m);
        r.Update();
        PointAt(m, 5);
        r.Update();

        Assert.Equal(RosterBridge.Player, r.CurrentBridge);
        Assert.Equal(Offsets.RosterBase + 0L * Offsets.RosterStride, r.CurrentRosterBase);
        Assert.Equal(RescueOutcome.WeaponUnique, r.CurrentRescue);
    }

    [Fact]
    public void Bridge_weapon_unique_record_carries_the_outcome_and_the_raw_weapon_id()
    {
        // The live PASS criterion for the next tape: rescue=WeaponUnique wpn=22 verbatim.
        const int weapon = 22;
        var m = new FakeSparseMemory();
        var recorded = new List<(string type, string payload)>();
        MemSeats.SeatBand(m, 5, weapon: weapon, lvl: 9, br: 71, fa: 63, gx: 1, gy: 1);
        MemSeats.SeatFrameNameId(m, 5, nameId: 2);
        MemSeats.SeatBandJob(m, 5, job: 2);
        MemSeats.SeatRoster(m, slot: 0, lvl: 1, br: 70, fa: 70, rh: weapon, nameId: 1);
        var r = new ActorRegister(m, (type, payload) => recorded.Add((type, payload)));
        r.Update();
        PointAt(m, 5);
        r.Update();

        Assert.Single(recorded);
        Assert.Contains(" rescue=WeaponUnique", recorded[0].payload);
        Assert.Contains(" wpn=22", recorded[0].payload);
    }

    [Fact]
    public void Bridge_weapon_key_refuses_an_ambiguous_weapon_match()
    {
        const int weapon = 22;
        var m = new FakeSparseMemory();
        MemSeats.SeatBand(m, 5, weapon: weapon, lvl: 9, br: 71, fa: 63, gx: 1, gy: 1);
        MemSeats.SeatFrameNameId(m, 5, nameId: 2);
        MemSeats.SeatBandJob(m, 5, job: 2);
        MemSeats.SeatRoster(m, slot: 0, lvl: 1, br: 70, fa: 70, rh: weapon, nameId: 1);
        MemSeats.SeatRoster(m, slot: 1, lvl: 1, br: 20, fa: 20, rh: weapon, nameId: 1);   // second drift-passing row, same weapon
        var r = new ActorRegister(m);
        r.Update();
        PointAt(m, 5);
        r.Update();

        Assert.Equal(RosterBridge.Enemy, r.CurrentBridge);
        Assert.Equal(RescueOutcome.WeaponAmbiguous, r.CurrentRescue);
    }

    [Fact]
    public void Bridge_weapon_key_honors_the_level_drift()
    {
        // Drift boundary: MaxLevelDrift is 9 (one-sided). Live level 11 vs roster level 1 is a
        // drift of 10, one past the allowed window, so the weapon key must refuse even though it
        // holds the exact band weapon.
        const int weapon = 22;
        var m = new FakeSparseMemory();
        MemSeats.SeatBand(m, 5, weapon: weapon, lvl: 11, br: 71, fa: 63, gx: 1, gy: 1);
        MemSeats.SeatFrameNameId(m, 5, nameId: 2);
        MemSeats.SeatBandJob(m, 5, job: 2);
        MemSeats.SeatRoster(m, slot: 0, lvl: 1, br: 70, fa: 70, rh: weapon, nameId: 1);
        var r = new ActorRegister(m);
        r.Update();
        PointAt(m, 5);
        r.Update();

        Assert.Equal(RosterBridge.Enemy, r.CurrentBridge);
        Assert.Equal(RescueOutcome.NoMatch, r.CurrentRescue);
    }

    [Fact]
    public void Bridge_weapon_key_refuses_an_unarmed_band_weapon()
    {
        var m = new FakeSparseMemory();
        MemSeats.SeatBand(m, 5, weapon: 0xFFFF, lvl: 9, br: 71, fa: 63, gx: 1, gy: 1);
        MemSeats.SeatFrameNameId(m, 5, nameId: 2);
        MemSeats.SeatBandJob(m, 5, job: 2);
        MemSeats.SeatRoster(m, slot: 0, lvl: 1, br: 70, fa: 70, rh: 22, nameId: 1);   // fp diverges too
        var r = new ActorRegister(m);
        r.Update();
        PointAt(m, 5);
        r.Update();

        Assert.Equal(RosterBridge.Enemy, r.CurrentBridge);
        Assert.Equal(RescueOutcome.NoMatch, r.CurrentRescue);
    }

    [Fact]
    public void Bridge_wpn_mismatch_is_terminal_no_weapon_key_fall_through()
    {
        // Row A gives the fingerprint key a UNIQUE match whose hands lack the band weapon
        // (terminal WpnMismatch); row B uniquely holds the band weapon but its fp does not match.
        // The ladder must stop at row A's verdict and never fall through to check row B.
        const int weapon = 22, otherWeapon = 12;
        var m = new FakeSparseMemory();
        MemSeats.SeatBand(m, 5, weapon: weapon, lvl: 8, br: 63, fa: 60, gx: 1, gy: 1);
        MemSeats.SeatFrameNameId(m, 5, nameId: 2);
        MemSeats.SeatBandJob(m, 5, job: 2);
        MemSeats.SeatRoster(m, slot: 0, lvl: 1, br: 63, fa: 60, rh: otherWeapon, nameId: 1);   // row A: unique fp match, hands lack the band weapon
        MemSeats.SeatRoster(m, slot: 1, lvl: 1, br: 1, fa: 1, rh: weapon, nameId: 1);          // row B: uniquely holds the band weapon, fp does not match
        var r = new ActorRegister(m);
        r.Update();
        PointAt(m, 5);
        r.Update();

        Assert.Equal(RosterBridge.Enemy, r.CurrentBridge);
        Assert.Equal(RescueOutcome.WpnMismatch, r.CurrentRescue);   // NOT WeaponUnique
    }

    [Fact]
    public void Bridge_non_canonical_never_reaches_the_weapon_key()
    {
        const int weapon = 22;
        var m = new FakeSparseMemory();
        MemSeats.SeatBand(m, 5, weapon: weapon, lvl: 9, br: 71, fa: 63, gx: 1, gy: 1);
        MemSeats.SeatFrameNameId(m, 5, nameId: 918);
        MemSeats.SeatBandJob(m, 5, job: 5);   // disagrees with nameId, not canonical
        MemSeats.SeatRoster(m, slot: 0, lvl: 1, br: 70, fa: 70, rh: weapon, nameId: 1);   // weapon uniquely held
        var r = new ActorRegister(m);
        r.Update();
        PointAt(m, 5);
        r.Update();

        Assert.Equal(RosterBridge.Enemy, r.CurrentBridge);
        Assert.Equal(RescueOutcome.NotCanonical, r.CurrentRescue);
    }

    // --- oracle exclusion (LW-56 fault 2, A1) ---

    [Fact]
    public void Bridge_oracle_exclusion_refuses_a_canonical_entry_the_oracle_claims_as_enemy()
    {
        const int weapon = 22;
        var m = new FakeSparseMemory();
        MemSeats.SeatBand(m, 5, weapon: weapon, lvl: 9, br: 71, fa: 63, gx: 1, gy: 1, maxHp: 100);
        MemSeats.SeatFrameNameId(m, 5, nameId: 2);
        MemSeats.SeatBandJob(m, 5, job: 2);
        MemSeats.SeatRoster(m, slot: 0, lvl: 1, br: 70, fa: 70, rh: weapon, nameId: 1);   // a unique weapon match exists
        var r = new ActorRegister(m, isOracleEnemy: id => id.lvl == 9 && id.br == 71 && id.fa == 63 && id.maxHp == 100);
        r.Update();
        PointAt(m, 5);
        r.Update();

        Assert.Equal(RosterBridge.Enemy, r.CurrentBridge);
        Assert.Equal(RescueOutcome.OracleEnemy, r.CurrentRescue);
    }

    [Fact]
    public void Bridge_oracle_exclusion_off_when_the_predicate_is_null()
    {
        // Non-vacuity twin: identical geometry to the oracle-exclusion test above, but the
        // predicate is omitted (the every-existing-caller default), so the clause must not fire
        // on its own; the weapon key rescues exactly as it would with the clause never wired.
        const int weapon = 22;
        var m = new FakeSparseMemory();
        MemSeats.SeatBand(m, 5, weapon: weapon, lvl: 9, br: 71, fa: 63, gx: 1, gy: 1, maxHp: 100);
        MemSeats.SeatFrameNameId(m, 5, nameId: 2);
        MemSeats.SeatBandJob(m, 5, job: 2);
        MemSeats.SeatRoster(m, slot: 0, lvl: 1, br: 70, fa: 70, rh: weapon, nameId: 1);
        var r = new ActorRegister(m);   // isOracleEnemy omitted -> null -> clause off

        r.Update();
        PointAt(m, 5);
        r.Update();

        Assert.Equal(RosterBridge.Player, r.CurrentBridge);
        Assert.Equal(RescueOutcome.WeaponUnique, r.CurrentRescue);
    }

    [Fact]
    public void Bridge_rescue_accepts_unarmed_agreement_on_both_sides()
    {
        // The unarmed twin of the load-bearing rescue test: the band weapon reads a sentinel and
        // the matched row's hand set is entirely sentinels too, so both-unarmed still agrees.
        var m = new FakeSparseMemory();
        MemSeats.SeatBand(m, 5, weapon: 0xFFFF, lvl: 8, br: 63, fa: 60, gx: 1, gy: 1);
        MemSeats.SeatFrameNameId(m, 5, nameId: 2);
        MemSeats.SeatBandJob(m, 5, job: 2);
        MemSeats.SeatRoster(m, slot: 0, lvl: 1, br: 63, fa: 60, rh: 0xFFFF, nameId: 1);
        var r = new ActorRegister(m);
        r.Update();
        PointAt(m, 5);
        r.Update();

        Assert.Equal(RosterBridge.Player, r.CurrentBridge);
        Assert.Equal(RescueOutcome.Unique, r.CurrentRescue);
    }

    [Fact]
    public void Bridge_nameid_match_still_wins_over_the_rescue()
    {
        // The rescue must never run when the nameId+stat loop already found a match: matches==1
        // returns Player immediately, RescueCanonical is never called, CurrentRescue stays NotRun.
        var m = new FakeSparseMemory();
        MemSeats.SeatBand(m, 5, weapon: 0, lvl: 50, br: 60, fa: 70, gx: 1, gy: 1);
        MemSeats.SeatFrameNameId(m, 5, nameId: 42);
        MemSeats.SeatBandJob(m, 5, job: 42);          // even canonical, must not matter here
        MemSeats.SeatRoster(m, slot: 2, lvl: 50, br: 60, fa: 70, rh: 999, nameId: 42);   // row A: nameId match
        MemSeats.SeatRoster(m, slot: 7, lvl: 41, br: 60, fa: 70, rh: 888, nameId: 1);    // row B: bare-fp-only
        var r = new ActorRegister(m);
        r.Update();
        PointAt(m, 5);
        r.Update();

        Assert.Equal(RosterBridge.Player, r.CurrentBridge);
        Assert.Equal(Offsets.RosterBase + 2L * Offsets.RosterStride, r.CurrentRosterBase);   // row A, not B
        Assert.Equal(RescueOutcome.NotRun, r.CurrentRescue);
    }

    [Fact]
    public void Bridge_rescued_player_stamps_the_killer_stamp_snapshot()
    {
        const int weapon = 64;
        var m = new FakeSparseMemory();
        MemSeats.SeatBand(m, 5, weapon: weapon, lvl: 8, br: 63, fa: 60, gx: 1, gy: 1);
        MemSeats.SeatFrameNameId(m, 5, nameId: 2);
        MemSeats.SeatBandJob(m, 5, job: 2);
        MemSeats.SeatRoster(m, slot: 0, lvl: 1, br: 63, fa: 60, rh: weapon, nameId: 1);
        var r = new ActorRegister(m);
        r.Update();
        PointAt(m, 5);
        r.Update();

        Assert.Equal((ushort)2, r.LastPlayerNameId);
        Assert.Equal(Offsets.RosterBase + 0L * Offsets.RosterStride, r.LastPlayerRosterBase);
        Assert.Equal(2, r.LastPlayerArrivalTick);
    }

    [Fact]
    public void Rescue_field_rides_only_the_canonical_zero_match_record_in_a_mixed_sequence()
    {
        const int weapon = 64;
        var m = new FakeSparseMemory();
        var recorded = new List<(string type, string payload)>();
        var r = new ActorRegister(m, (type, payload) => recorded.Add((type, payload)));
        r.Update();   // tick1: priming

        // Arrival 1: canonical zero-match rescue.
        MemSeats.SeatBand(m, 5, weapon: weapon, lvl: 8, br: 63, fa: 60, gx: 1, gy: 1);
        MemSeats.SeatFrameNameId(m, 5, nameId: 2);
        MemSeats.SeatBandJob(m, 5, job: 2);
        MemSeats.SeatRoster(m, slot: 0, lvl: 1, br: 63, fa: 60, rh: weapon, nameId: 1);
        PointAt(m, 5);
        r.Update();   // tick2

        // Arrival 2: a generic enemy (nameId unmatched, job disagrees, not canonical).
        MemSeats.SeatBand(m, 6, weapon: 0, lvl: 50, br: 20, fa: 20, gx: 2, gy: 2);
        MemSeats.SeatFrameNameId(m, 6, nameId: 918);
        MemSeats.SeatBandJob(m, 6, job: 5);
        PointAt(m, 6);
        r.Update();   // tick3

        // Arrival 3: idle (entry==0).
        m.SeedU64(Offsets.ActorPtr, 0);
        r.Update();   // tick4

        Assert.Equal(3, recorded.Count);
        Assert.Contains(" rescue=", recorded[0].payload);
        Assert.Contains(" fp=L", recorded[0].payload);
        Assert.Contains(" wpn=", recorded[0].payload);
        Assert.DoesNotContain(" rescue=", recorded[1].payload);
        Assert.DoesNotContain(" wpn=", recorded[1].payload);
        Assert.DoesNotContain(" rescue=", recorded[2].payload);
        Assert.DoesNotContain(" wpn=", recorded[2].payload);

        r.ResetBattle();
        Assert.Equal(RescueOutcome.NotRun, r.CurrentRescue);
    }

    // --- roster span (LW-96: proven-live 50 row bank, roster_span_probe.py 2026-07-21) ---

    [Fact]
    public void Bridge_resolves_player_when_the_matching_row_is_at_slot_49()
    {
        // LOAD-BEARING: identical arrangement to Bridge_resolves_player_on_single_nameid_and_stat_match,
        // except the matching roster row lives at slot 49 (the last row of the proven 50-row bank).
        // Must FAIL before the RosterSlots bump (walk stops at 20) and PASS after.
        var m = new FakeSparseMemory();
        MemSeats.SeatBand(m, 5, weapon: 0, lvl: 50, br: 60, fa: 70, gx: 1, gy: 1);
        MemSeats.SeatFrameNameId(m, 5, nameId: 42);
        MemSeats.SeatRoster(m, slot: 49, lvl: 50, br: 60, fa: 70, rh: 999, nameId: 42);
        var r = new ActorRegister(m);
        r.Update();
        PointAt(m, 5);
        r.Update();

        Assert.Equal(RosterBridge.Player, r.CurrentBridge);
        Assert.Equal(Offsets.RosterBase + 49L * Offsets.RosterStride, r.CurrentRosterBase);
    }

    [Fact]
    public void Bridge_never_walks_into_the_stale_guest_bank_at_slot_50()
    {
        // GUARD: the matching row lives ONLY at slot 50, the first row of the stale guest bank
        // (duplicate identities live there; scanning it is the hazard the 50-row ceiling guards
        // against). Must never bridge as Player, both before and after the RosterSlots bump.
        var m = new FakeSparseMemory();
        MemSeats.SeatBand(m, 5, weapon: 0, lvl: 50, br: 60, fa: 70, gx: 1, gy: 1);
        MemSeats.SeatFrameNameId(m, 5, nameId: 42);
        MemSeats.SeatRoster(m, slot: 50, lvl: 50, br: 60, fa: 70, rh: 999, nameId: 42);
        var r = new ActorRegister(m);
        r.Update();
        PointAt(m, 5);
        r.Update();

        Assert.NotEqual(RosterBridge.Player, r.CurrentBridge);
    }

    // --- OwnershipAge (general staleness metric; no longer the corpse-anchor comparand -- see
    // KillTracker.UpdateCorpseAnchor's register-tick birth stamps) ---

    [Fact]
    public void OwnershipAge_grows_with_ticks_since_arrival()
    {
        var m = new FakeSparseMemory();
        var r = new ActorRegister(m);
        r.Update();     // tick1 priming
        PointAt(m, 5);
        r.Update();     // tick2 arrival
        Assert.Equal(0, r.OwnershipAge);

        r.Update();     // tick3
        r.Update();     // tick4
        Assert.Equal(2, r.OwnershipAge);
    }
}
