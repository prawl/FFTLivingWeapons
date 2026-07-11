using System;
using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// Ame-no-Murakumo's "Iai" signature: opening-turn Speed hold. At +3, every deployed
/// main-hand wielder's Speed is held at field-max + IaiSpeedMargin from battle-open so
/// it takes the opening turn; Speed reverts to natural on the engine actor-pointer release
/// signal (arrival at the acting entry, or an Acted-edge while already pointing there),
/// backstopped by the wall-clock cap.
///
/// Pure policy in Iai.Policy.cs: ShouldHold (tier gate), Target (Speed target from natural,
/// fieldMax, margin), ReleaseSignalById (PRIMARY release decision, v2 2026-07-01 -- identity-
/// matched via the acting entry's frame nameId back-reference, survives the mirror-churn
/// ambiguity a live 2x-wielder repro exposed), and ReleaseSignal (the original ADDRESS-compare
/// decision, now the fallback for a hold whose roster-nameId capture failed). Integration tests
/// use FakeSparseMemory; the release signal is driven by seeding Offsets.ActorPtr (PointActorAt),
/// Offsets.Acted (SetActed), and -- for the identity path -- a roster + frame nameId pair
/// (MemSeats.SeatRoster's nameId param + MemSeats.SeatFrameNameId) -- no CT/ACtSlam machinery
/// anywhere here. MirrorChurn_release_still_fires_via_identity and
/// Cap_fires_even_when_wielder_never_resolves are the two LOAD-BEARING tests for the v2 rebuild:
/// they reproduce the mirror-churn ambiguity (Wielder.Locate returning 0 for a wielder mid-hold)
/// that starved both the release AND the wall-clock cap under v1.
/// </summary>
public class IaiTests
{
    private const int AmeNoMurakumoId = 42;
    private const int OtherWeaponId   = 56;

    // ---- Policy tests (pure): ShouldHold + Target ----

    [Fact]
    public void Target_natural_above_field_returns_natural()
    {
        // Non-vacuous: naive `fieldMax + margin` = 9, which is BELOW natural=10.
        // A correct implementation returns natural=10; the naive one fails this test.
        Assert.Equal(10, Iai.Target(natural: 10, fieldMax: 8, margin: 1));
    }

    [Fact]
    public void Target_natural_equal_field_returns_field_plus_margin()
        => Assert.Equal(9, Iai.Target(natural: 8, fieldMax: 8, margin: 1));

    [Fact]
    public void Target_natural_below_field_returns_field_plus_margin()
        => Assert.Equal(9, Iai.Target(natural: 5, fieldMax: 8, margin: 1));

    [Fact]
    public void ShouldHold_true_at_tier_3()
        => Assert.True(Iai.ShouldHold(tier: 3, atTier: 3));

    [Fact]
    public void ShouldHold_false_below_tier()
        => Assert.False(Iai.ShouldHold(tier: 2, atTier: 3));

    // ---- Policy tests (pure): ReleaseSignal truth table ----

    private const long Wielder = 0x1000, Other = 0x2000;

    [Fact]
    public void ReleaseSignal_arrival_fires()
        // S1: curActing transitions TO the wielder's entry (from something else, or from 0).
        => Assert.True(Iai.ReleaseSignal(prevActing: 0, curActing: Wielder, prevActed: false, curActed: false, wielderEntry: Wielder));

    [Fact]
    public void ReleaseSignal_acted_edge_match_fires()
        // S2: already pointing at the wielder (no transition), Acted rises 0->1.
        => Assert.True(Iai.ReleaseSignal(prevActing: Wielder, curActing: Wielder, prevActed: false, curActed: true, wielderEntry: Wielder));

    [Fact]
    public void ReleaseSignal_static_equal_no_fire()
        // Already pointing at the wielder, no transition, no Acted edge -> no fire.
        => Assert.False(Iai.ReleaseSignal(prevActing: Wielder, curActing: Wielder, prevActed: true, curActed: true, wielderEntry: Wielder));

    [Fact]
    public void ReleaseSignal_arrival_at_other_entry_no_fire()
        // A pointer transition to a NON-wielder entry must never fire.
        => Assert.False(Iai.ReleaseSignal(prevActing: 0, curActing: Other, prevActed: false, curActed: false, wielderEntry: Wielder));

    [Fact]
    public void ReleaseSignal_acted_edge_with_mismatch_no_fire()
        // Acted rises 0->1, but the pointer names a DIFFERENT entry -> no fire.
        => Assert.False(Iai.ReleaseSignal(prevActing: Other, curActing: Other, prevActed: false, curActed: true, wielderEntry: Wielder));

    [Fact]
    public void ReleaseSignal_curActing_zero_never_fires_even_matching_a_zero_sentinel()
        // Defensive: curActing==0 must never fire, even if wielderEntry is degenerately 0.
        => Assert.False(Iai.ReleaseSignal(prevActing: 5, curActing: 0, prevActed: false, curActed: true, wielderEntry: 0));

    // ---- Policy tests (pure): ReleaseSignalById truth table (v2 identity-match release) ----

    private const int WielderNameId = 298, OtherNameId = 301;

    [Fact]
    public void ReleaseSignalById_arrival_with_matching_id_fires()
        // S1: curActing transitions (from 0), actingNameId matches holdNameId.
        => Assert.True(Iai.ReleaseSignalById(prevActing: 0, curActing: Wielder, prevActed: false, curActed: false,
            actingNameId: WielderNameId, holdNameId: WielderNameId));

    [Fact]
    public void ReleaseSignalById_acted_edge_with_matching_id_fires()
        // S2: already pointing at a matching-id entry (no transition), Acted rises 0->1.
        => Assert.True(Iai.ReleaseSignalById(prevActing: Wielder, curActing: Wielder, prevActed: false, curActed: true,
            actingNameId: WielderNameId, holdNameId: WielderNameId));

    [Fact]
    public void ReleaseSignalById_id_mismatch_no_fire_even_with_arrival()
        // A pointer transition whose frame nameId does NOT match the hold's captured nameId.
        => Assert.False(Iai.ReleaseSignalById(prevActing: 0, curActing: Wielder, prevActed: false, curActed: false,
            actingNameId: OtherNameId, holdNameId: WielderNameId));

    [Fact]
    public void ReleaseSignalById_holdNameId_zero_never_fires_even_matching_zero()
        // The 0==0 trap: a failed capture (holdNameId 0, e.g. Wielder.RosterNameId's non-ambiguous-
        // but-unseeded case) must never match an equally-unreadable acting frame (actingNameId 0),
        // even on an arrival transition. This is the load-bearing guard, not a corner case.
        => Assert.False(Iai.ReleaseSignalById(prevActing: 0, curActing: Wielder, prevActed: false, curActed: false,
            actingNameId: 0, holdNameId: 0));

    [Fact]
    public void ReleaseSignalById_curActing_zero_never_fires()
        // Defensive: curActing==0 must never fire, even with a matching nameId pair.
        => Assert.False(Iai.ReleaseSignalById(prevActing: Wielder, curActing: 0, prevActed: false, curActed: true,
            actingNameId: WielderNameId, holdNameId: WielderNameId));

    // ---- Build helper ----

    /// <summary>
    /// One wielder (roster slot 0, band slot 24, naturalSpeed), a non-Iai field unit
    /// (band slot 0, speed=fieldUnitSpeed). Offsets.ActorPtr is NOT seeded by default (reads
    /// 0 -> Band.ActorEntry returns 0 -> never matches the wielder) -- tests that want a
    /// release must call PointActorAt/SetActed explicitly. nameId defaults to 0 (unseeded roster
    /// nameId -> Wielder.RosterNameId returns 0 -> the release/cap path falls back to
    /// address-matching, i.e. v1 semantics) -- pass a positive nameId to seed BOTH the roster
    /// copy (arm-time capture) and the frame back-reference (the acting pointer's read) and
    /// exercise the identity path instead.
    /// </summary>
    private static (Iai iai, FakeSparseMemory mem, long wielderEntry, (int lvl, int br, int fa) fp)
        Build(int kills = -1, int wielderSlot = 24, int naturalSpeed = 8, int fieldUnitSpeed = 6, int nameId = 0)
    {
        var mem = new FakeSparseMemory();
        var meta = new Dictionary<int, WeaponMeta>
        {
            [AmeNoMurakumoId] = new WeaponMeta
            {
                Name = "Ame-no-Murakumo", Wp = 10, Cat = "Katana", Formula = 1,
                Flavor = "gathering-storm blade",
                Signature = new WeaponSignature { AtTier = 3, Iai = true, DisplayLabel = "Iai" }
            }
        };
        var killDict = new Dictionary<int, int>();
        killDict[AmeNoMurakumoId] = kills >= 0 ? kills : Tuning.ProdThresholds[2];

        var fp = (lvl: 30, br: 65, fa: 60);
        long wielder = Band.Entry(wielderSlot);

        MemSeats.SeatRoster(mem, 0, lvl: fp.lvl, br: fp.br, fa: fp.fa, rh: AmeNoMurakumoId, nameId: nameId);
        MemSeats.SeatBand(mem, wielderSlot, weapon: AmeNoMurakumoId,
                          lvl: fp.lvl, br: fp.br, fa: fp.fa,
                          gx: 2, gy: 2, hp: 200, maxHp: 300, speed: naturalSpeed);
        if (nameId != 0) MemSeats.SeatFrameNameId(mem, wielderSlot, nameId);
        mem.WritableAddrs.Add(wielder + Offsets.ASpeed);

        MemSeats.SeatBand(mem, 0, weapon: OtherWeaponId,
                          lvl: 20, br: 50, fa: 50,
                          gx: 5, gy: 5, hp: 100, maxHp: 100, speed: fieldUnitSpeed);

        var iai = new Iai(meta, killDict, mem);
        return (iai, mem, wielder, fp);
    }

    /// <summary>Point Offsets.ActorPtr at <paramref name="bandEntry"/>'s combat frame (the
    /// inverse of Band.ActorEntry: frame = bandEntry - BandEntry).</summary>
    private static void PointActorAt(FakeSparseMemory mem, long bandEntry) =>
        mem.SeedU64(Offsets.ActorPtr, (ulong)(bandEntry - Offsets.BandEntry));

    /// <summary>Clear the pointer so it resolves to nothing (Band.ActorEntry returns 0).</summary>
    private static void PointActorNowhere(FakeSparseMemory mem) =>
        mem.SeedU64(Offsets.ActorPtr, 0);

    private static void SetActed(FakeSparseMemory mem, int v) => mem.U8s[Offsets.Acted] = (byte)v;

    // ---- MIRROR CHURN (v2 identity-match release, the two LOAD-BEARING tests) ----
    //
    // Root cause (live-diagnosed 2026-07-01, unitid_probe.py watch/find, two battles): a
    // revolving engine MIRROR frame (band seat 28) clones different real units over time WITH
    // real positions. When it mirrors an Iai wielder, Wielder.Locate sees TWO real-position
    // entries with identical (weapon,brave,faith) and ambiguity-bails (returns 0) -- the wielder
    // drops out of Iai's per-tick _wielders list. v1 evaluated release/cap INSIDE the _wielders
    // loop, so a wielder that stops resolving never releases and never caps. v2 moves both checks
    // to a pass over EVERY unreleased HoldState and identity-matches the acting entry's frame
    // nameId back-reference (Offsets.ANameId) against the hold's roster-nameId capture
    // (Wielder.RosterNameId) instead of requiring the wielder to be in _wielders at all.

    [Fact]
    public void MirrorChurn_release_still_fires_via_identity()
    {
        var mem  = new FakeSparseMemory();
        var meta = new Dictionary<int, WeaponMeta>
        {
            [AmeNoMurakumoId] = new WeaponMeta
            {
                Name = "Ame-no-Murakumo", Wp = 10, Cat = "Katana", Formula = 1,
                Flavor = "gathering-storm blade",
                Signature = new WeaponSignature { AtTier = 3, Iai = true, DisplayLabel = "Iai" }
            }
        };
        var kills = new Dictionary<int, int> { [AmeNoMurakumoId] = Tuning.ProdThresholds[2] };

        const int nameId = 298;
        const int naturalSpeed = 8;

        // The real wielder: band 24, roster slot 0. Roster nameId + frame nameId both seeded --
        // the arm-time capture reads the roster copy, the acting pointer's read observes the frame copy.
        MemSeats.SeatRoster(mem, 0, lvl: 30, br: 65, fa: 60, rh: AmeNoMurakumoId, nameId: nameId);
        long wielderEntry = Band.Entry(24);
        MemSeats.SeatBand(mem, 24, weapon: AmeNoMurakumoId,
                          lvl: 30, br: 65, fa: 60, gx: 2, gy: 2, hp: 200, maxHp: 300, speed: naturalSpeed);
        MemSeats.SeatFrameNameId(mem, 24, nameId);
        mem.WritableAddrs.Add(wielderEntry + Offsets.ASpeed);

        // A non-Iai field unit sets a real field-max so the hold is visibly boosting (11) before release.
        MemSeats.SeatBand(mem, 0, weapon: OtherWeaponId,
                          lvl: 20, br: 50, fa: 50, gx: 5, gy: 5, hp: 100, maxHp: 100, speed: 10);

        var iai = new Iai(meta, kills, mem);
        var now = DateTime.UtcNow;

        // Tick 1: arm + prime (no mirror yet; pointer unseeded -> 0).
        iai.Tick(onField: true, now);
        Assert.Equal((byte)(10 + Tuning.IaiSpeedMargin), mem.U8s[wielderEntry + Offsets.ASpeed]);

        // Seed the MIRROR: same weapon/lvl/br/fa, a REAL position, same frame nameId. From here
        // on Wielder.Locate sees two real-position candidates sharing (weapon,brave,faith) and
        // ambiguity-bails -- the wielder vanishes from _wielders every subsequent tick.
        MemSeats.SeatBand(mem, 20, weapon: AmeNoMurakumoId,
                          lvl: 30, br: 65, fa: 60, gx: 9, gy: 9, hp: 200, maxHp: 300, speed: naturalSpeed);
        MemSeats.SeatFrameNameId(mem, 20, nameId);

        // Tick 2: pointer still 0 -- no release, no crash.
        mem.Written.Clear();
        iai.Tick(onField: true, now);
        Assert.False(mem.Written.ContainsKey(wielderEntry + Offsets.ASpeed));

        // Tick 3: the pointer arrives at the wielder's REAL frame (not the mirror's). Even though
        // the wielder is NOT in _wielders this tick (still ambiguous), the release must fire via
        // the frame nameId identity match, and the restore write must land on the REAL entry.
        //
        // NON-VACUITY: v1 (address-match evaluated only over resolved _wielders) fails here --
        // the wielder is absent from _wielders, so the release check never even runs.
        PointActorAt(mem, wielderEntry);
        iai.Tick(onField: true, now);
        Assert.True(mem.Written.ContainsKey(wielderEntry + Offsets.ASpeed));
        Assert.Equal((byte)naturalSpeed, mem.U8s[wielderEntry + Offsets.ASpeed]);
    }

    [Fact]
    public void Cap_fires_even_when_wielder_never_resolves()
    {
        var mem  = new FakeSparseMemory();
        var meta = new Dictionary<int, WeaponMeta>
        {
            [AmeNoMurakumoId] = new WeaponMeta
            {
                Name = "Ame-no-Murakumo", Wp = 10, Cat = "Katana", Formula = 1,
                Flavor = "gathering-storm blade",
                Signature = new WeaponSignature { AtTier = 3, Iai = true, DisplayLabel = "Iai" }
            }
        };
        var kills = new Dictionary<int, int> { [AmeNoMurakumoId] = Tuning.ProdThresholds[2] };

        const int naturalSpeed = 8;

        MemSeats.SeatRoster(mem, 0, lvl: 30, br: 65, fa: 60, rh: AmeNoMurakumoId);   // nameId left 0 (fallback path)
        long wielderEntry = Band.Entry(24);
        MemSeats.SeatBand(mem, 24, weapon: AmeNoMurakumoId,
                          lvl: 30, br: 65, fa: 60, gx: 2, gy: 2, hp: 200, maxHp: 300, speed: naturalSpeed);
        mem.WritableAddrs.Add(wielderEntry + Offsets.ASpeed);

        MemSeats.SeatBand(mem, 0, weapon: OtherWeaponId,
                          lvl: 20, br: 50, fa: 50, gx: 5, gy: 5, hp: 100, maxHp: 100, speed: 10);

        var iai = new Iai(meta, kills, mem);
        var startNow = DateTime.UtcNow;

        // Tick 1: arm + prime -- records LastEntry = wielderEntry.
        iai.Tick(onField: true, startNow);
        Assert.True(mem.Written.ContainsKey(wielderEntry + Offsets.ASpeed));

        // Seed the mirror: the wielder becomes unresolvable (ambiguity bail) from here on, so it
        // never again appears in _wielders for the rest of the battle.
        MemSeats.SeatBand(mem, 20, weapon: AmeNoMurakumoId,
                          lvl: 30, br: 65, fa: 60, gx: 9, gy: 9, hp: 200, maxHp: 300, speed: naturalSpeed);

        // Advance past the wall-clock cap. No pointer signal at all (ActorPtr stays unseeded/0).
        //
        // NON-VACUITY: v1 fails here -- the cap check lived INSIDE the resolved-wielder loop, so
        // a wielder that never resolves again never even reaches the cap check.
        var capTime = startNow.AddSeconds(Tuning.IaiHoldCapSeconds + 1);
        mem.Written.Clear();
        iai.Tick(onField: true, capTime);

        Assert.True(mem.Written.ContainsKey(wielderEntry + Offsets.ASpeed),
            "cap must fire via the hold's LastEntry even though the wielder never resolved this tick");
        Assert.Equal((byte)naturalSpeed, mem.U8s[wielderEntry + Offsets.ASpeed]);
    }

    [Fact]
    public void Failed_nameId_capture_never_matches_zero()
    {
        // Roster nameId left UNSEEDED (reads 0): Wielder.RosterNameId's single matching slot
        // returns the raw read, 0 -- NOT -1 -- so this pins the "holdNameId > 0" guard rather
        // than the "-1 = ambiguous" path. The acting entry's OWN frame nameId, and a completely
        // unrelated seat's frame nameId, are ALSO left unseeded (both read 0): if the identity
        // path ever ran with a failed (0) capture, a 0==0 match against either seat would fire a
        // spurious release. It must not -- only the address-fallback path may release.
        var mem  = new FakeSparseMemory();
        var meta = new Dictionary<int, WeaponMeta>
        {
            [AmeNoMurakumoId] = new WeaponMeta
            {
                Name = "Ame-no-Murakumo", Wp = 10, Cat = "Katana", Formula = 1,
                Flavor = "gathering-storm blade",
                Signature = new WeaponSignature { AtTier = 3, Iai = true, DisplayLabel = "Iai" }
            }
        };
        var kills = new Dictionary<int, int> { [AmeNoMurakumoId] = Tuning.ProdThresholds[2] };

        MemSeats.SeatRoster(mem, 0, lvl: 30, br: 65, fa: 60, rh: AmeNoMurakumoId);   // nameId left 0
        long wielderEntry = Band.Entry(24);
        MemSeats.SeatBand(mem, 24, weapon: AmeNoMurakumoId,
                          lvl: 30, br: 65, fa: 60, gx: 2, gy: 2, hp: 200, maxHp: 300, speed: 8);
        mem.WritableAddrs.Add(wielderEntry + Offsets.ASpeed);
        // Acting entry's own frame nameId left unseeded too (reads 0) -- the trap this test pins.
        MemSeats.SeatFrameNameId(mem, 24, 0);

        long otherEntry = Band.Entry(0);
        MemSeats.SeatBand(mem, 0, weapon: OtherWeaponId,
                          lvl: 20, br: 50, fa: 50, gx: 5, gy: 5, hp: 100, maxHp: 100, speed: 10);
        // The 'other' seat's frame nameId is unseeded too (0) -- if identity ran on a failed
        // capture, arriving HERE would falsely 0==0-match the wielder's hold.

        var iai = new Iai(meta, kills, mem);
        var now = DateTime.UtcNow;

        iai.Tick(onField: true, now);   // arm + prime; RosterNameId capture -> 0 (failed)
        Assert.Equal((byte)(10 + Tuning.IaiSpeedMargin), mem.U8s[wielderEntry + Offsets.ASpeed]);   // held

        // The hold loop keeps re-writing the BOOSTED target every tick until released -- so the
        // non-vacuous check is the WRITTEN VALUE (still boosted = no release), not mere presence
        // in mem.Written (which is always true while held, release or not).
        // a failed nameId capture (0) must never match another seat's unreadable (0) frame nameId
        PointActorAt(mem, otherEntry);
        iai.Tick(onField: true, now);
        Assert.Equal((byte)(10 + Tuning.IaiSpeedMargin), mem.U8s[wielderEntry + Offsets.ASpeed]);

        // Pointer arrives at the wielder's OWN entry ADDRESS -- address-fallback releases (v1 semantics preserved).
        PointActorAt(mem, wielderEntry);
        iai.Tick(onField: true, now);
        Assert.Equal((byte)8, mem.U8s[wielderEntry + Offsets.ASpeed]);
    }

    // ---- Integration: basic hold ----

    [Fact]
    public void Hold_writes_target_speed_every_tick()
    {
        // fieldUnitSpeed=6, margin=1 -> fieldMax=6, target=max(8,7)=8 (natural wins since natural>fieldMax+1)
        var (iai, mem, wielderEntry, _) = Build(naturalSpeed: 8, fieldUnitSpeed: 6);
        var now = DateTime.UtcNow;

        iai.Tick(onField: true, now);
        Assert.Equal((byte)8, mem.U8s[wielderEntry + Offsets.ASpeed]);

        mem.Written.Clear();
        iai.Tick(onField: true, now);
        Assert.True(mem.Written.ContainsKey(wielderEntry + Offsets.ASpeed));
    }

    [Fact]
    public void Hold_raises_speed_above_field_max_when_natural_is_slower()
    {
        var (iai, mem, wielderEntry, _) = Build(naturalSpeed: 5, fieldUnitSpeed: 10);

        iai.Tick(onField: true, DateTime.UtcNow);

        Assert.Equal((byte)(10 + Tuning.IaiSpeedMargin), mem.U8s[wielderEntry + Offsets.ASpeed]);
    }

    [Fact]
    public void Hold_does_not_lower_naturally_faster_wielder()
    {
        var (iai, mem, wielderEntry, _) = Build(naturalSpeed: 15, fieldUnitSpeed: 6);

        iai.Tick(onField: true, DateTime.UtcNow);

        Assert.Equal((byte)15, mem.U8s[wielderEntry + Offsets.ASpeed]);
    }

    // ---- Multi-wielder non-escalation (F2) ----

    [Fact]
    public void MultiWielder_NoEscalation_many_ticks()
    {
        var mem  = new FakeSparseMemory();
        var meta = new Dictionary<int, WeaponMeta>
        {
            [AmeNoMurakumoId] = new WeaponMeta
            {
                Name = "Ame-no-Murakumo", Wp = 10, Cat = "Katana", Formula = 1,
                Flavor = "gathering-storm blade",
                Signature = new WeaponSignature { AtTier = 3, Iai = true, DisplayLabel = "Iai" }
            }
        };
        var kills = new Dictionary<int, int> { [AmeNoMurakumoId] = Tuning.ProdThresholds[2] };

        MemSeats.SeatRoster(mem, 0, lvl: 30, br: 65, fa: 60, rh: AmeNoMurakumoId);
        long entryA = Band.Entry(24);
        MemSeats.SeatBand(mem, 24, weapon: AmeNoMurakumoId,
                          lvl: 30, br: 65, fa: 60, gx: 3, gy: 3, hp: 200, maxHp: 300, speed: 7);
        mem.WritableAddrs.Add(entryA + Offsets.ASpeed);

        MemSeats.SeatRoster(mem, 1, lvl: 35, br: 70, fa: 55, rh: AmeNoMurakumoId);
        long entryB = Band.Entry(22);
        MemSeats.SeatBand(mem, 22, weapon: AmeNoMurakumoId,
                          lvl: 35, br: 70, fa: 55, gx: 8, gy: 8, hp: 180, maxHp: 250, speed: 8);
        mem.WritableAddrs.Add(entryB + Offsets.ASpeed);

        MemSeats.SeatBand(mem, 0, weapon: OtherWeaponId,
                          lvl: 20, br: 50, fa: 50, gx: 5, gy: 5, hp: 100, maxHp: 100, speed: 10);

        var iai = new Iai(meta, kills, mem);
        var now = DateTime.UtcNow;

        for (int i = 0; i < 5; i++) iai.Tick(onField: true, now);

        int spdA = mem.U8s[entryA + Offsets.ASpeed];
        int spdB = mem.U8s[entryB + Offsets.ASpeed];
        int cap  = 10 + Tuning.IaiSpeedMargin;

        Assert.True(spdA <= cap, $"Wielder A Speed {spdA} exceeded field-max+margin={cap} -- escalation bug (F2)");
        Assert.True(spdB <= cap, $"Wielder B Speed {spdB} exceeded field-max+margin={cap} -- escalation bug (F2)");
    }

    // ---- MULTI-WIELDER INDEPENDENCE VIA THE ACTOR POINTER (I1, the load-bearing test) ----

    [Fact]
    public void TwoWielders_release_independently_via_pointer()
    {
        // Two wielders, DIFFERENT roster fps, SAME hp/maxHp/level (the live repro shape --
        // the old turn-queue fingerprint path could not tell them apart). No Acted flag
        // involved at all: this proves S1 (arrival) alone, and wait-only-turn coverage.
        //
        // Non-vacuity: any fingerprint-based release, or a release that fires for EVERY
        // wielder on ANY pointer transition, fails this -- it either releases both when only
        // A's turn arrives, or misses A because the pointer briefly reads an enemy frame.
        var mem  = new FakeSparseMemory();
        var meta = new Dictionary<int, WeaponMeta>
        {
            [AmeNoMurakumoId] = new WeaponMeta
            {
                Name = "Ame-no-Murakumo", Wp = 10, Cat = "Katana", Formula = 1,
                Flavor = "gathering-storm blade",
                Signature = new WeaponSignature { AtTier = 3, Iai = true, DisplayLabel = "Iai" }
            }
        };
        var kills = new Dictionary<int, int> { [AmeNoMurakumoId] = Tuning.ProdThresholds[2] };

        // Wielder A: roster 0, band 24, natural Speed 7. Distinct roster+frame nameId (298) --
        // seeded so this test exercises the v2 identity-match release path.
        MemSeats.SeatRoster(mem, 0, lvl: 30, br: 65, fa: 60, rh: AmeNoMurakumoId, nameId: 298);
        long entryA = Band.Entry(24);
        MemSeats.SeatBand(mem, 24, weapon: AmeNoMurakumoId,
                          lvl: 30, br: 65, fa: 60, gx: 3, gy: 3, hp: 200, maxHp: 300, speed: 7);
        MemSeats.SeatFrameNameId(mem, 24, 298);
        mem.WritableAddrs.Add(entryA + Offsets.ASpeed);

        // Wielder B: roster 1, band 22, natural Speed 8 -- SAME hp/maxHp/level as A, different fp
        // AND a different nameId (301).
        MemSeats.SeatRoster(mem, 1, lvl: 30, br: 70, fa: 55, rh: AmeNoMurakumoId, nameId: 301);
        long entryB = Band.Entry(22);
        MemSeats.SeatBand(mem, 22, weapon: AmeNoMurakumoId,
                          lvl: 30, br: 70, fa: 55, gx: 8, gy: 8, hp: 200, maxHp: 300, speed: 8);
        MemSeats.SeatFrameNameId(mem, 22, 301);
        mem.WritableAddrs.Add(entryB + Offsets.ASpeed);

        // Non-Iai field unit: Speed=10 (the true field max)
        MemSeats.SeatBand(mem, 0, weapon: OtherWeaponId,
                          lvl: 20, br: 50, fa: 50, gx: 5, gy: 5, hp: 100, maxHp: 100, speed: 10);
        long entryEnemy = Band.Entry(0);

        var iai = new Iai(meta, kills, mem);
        var now = DateTime.UtcNow;

        // Tick 1: prime (pointer unseeded -> reads 0). Both wielders arm + hold.
        iai.Tick(onField: true, now);
        Assert.Equal((byte)(10 + Tuning.IaiSpeedMargin), mem.U8s[entryA + Offsets.ASpeed]);
        Assert.Equal((byte)(10 + Tuning.IaiSpeedMargin), mem.U8s[entryB + Offsets.ASpeed]);

        // Pointer arrives at A's frame -> A releases (reverts to natural 7); B stays held.
        PointActorAt(mem, entryA);
        iai.Tick(onField: true, now);
        Assert.Equal((byte)7, mem.U8s[entryA + Offsets.ASpeed]);
        Assert.Equal((byte)(10 + Tuning.IaiSpeedMargin), mem.U8s[entryB + Offsets.ASpeed]);

        // Pointer moves to an enemy frame -> B still held (A already released, untouched).
        PointActorAt(mem, entryEnemy);
        iai.Tick(onField: true, now);
        Assert.Equal((byte)(10 + Tuning.IaiSpeedMargin), mem.U8s[entryB + Offsets.ASpeed]);

        // Pointer arrives at B's frame -> B releases (reverts to natural 8).
        PointActorAt(mem, entryB);
        iai.Tick(onField: true, now);
        Assert.Equal((byte)8, mem.U8s[entryB + Offsets.ASpeed]);
    }

    // ---- I2: stale-equal pointer releases only on the Acted edge ----

    [Fact]
    public void StaleEqual_releases_on_acted_edge()
    {
        // The pointer already equals the wielder's entry from the FIRST tick (no arrival
        // transition ever happens). Several static ticks must NOT release; only the Acted
        // 0->1 edge (S2) does. nameId seeded -- semantics unchanged under the identity path.
        var (iai, mem, wielderEntry, _) = Build(naturalSpeed: 5, fieldUnitSpeed: 10, nameId: 298);
        var now = DateTime.UtcNow;
        PointActorAt(mem, wielderEntry);   // stale-equal from tick 1

        iai.Tick(onField: true, now);   // primed -- no release evaluated
        Assert.Equal((byte)(10 + Tuning.IaiSpeedMargin), mem.U8s[wielderEntry + Offsets.ASpeed]);

        for (int i = 0; i < 3; i++)
        {
            iai.Tick(onField: true, now);   // static equal, Acted still 0 -> no release
            Assert.Equal((byte)(10 + Tuning.IaiSpeedMargin), mem.U8s[wielderEntry + Offsets.ASpeed]);
        }

        SetActed(mem, 1);   // genuine 0->1 edge while already pointing at the wielder
        iai.Tick(onField: true, now);
        Assert.Equal((byte)5, mem.U8s[wielderEntry + Offsets.ASpeed]);
    }

    // ---- I2b (F5, the OTHER load-bearing test): dual priming ----

    [Fact]
    public void FirstTick_staleEqual_withActedAlreadyHigh_does_not_falseRelease()
    {
        // The pointer is ALREADY stale-equal to the wielder AND Acted already reads 1 on the
        // very first evaluated tick (both prev values must be primed together). Non-vacuity:
        // an implementation that primes only prevActing (defaulting prevActed=false) sees a
        // FALSE S2 edge here and releases instantly on tick 1 -- this test pins that bug.
        // nameId seeded -- semantics unchanged under the identity path.
        var (iai, mem, wielderEntry, _) = Build(naturalSpeed: 5, fieldUnitSpeed: 10, nameId: 298);
        var now = DateTime.UtcNow;
        PointActorAt(mem, wielderEntry);
        SetActed(mem, 1);

        iai.Tick(onField: true, now);   // priming tick -- must NOT release
        Assert.Equal((byte)(10 + Tuning.IaiSpeedMargin), mem.U8s[wielderEntry + Offsets.ASpeed]);

        iai.Tick(onField: true, now);   // still no edge (Acted stayed 1, pointer stayed equal)
        Assert.Equal((byte)(10 + Tuning.IaiSpeedMargin), mem.U8s[wielderEntry + Offsets.ASpeed]);

        // A genuine edge: Acted drops then rises again.
        SetActed(mem, 0);
        iai.Tick(onField: true, now);
        Assert.Equal((byte)(10 + Tuning.IaiSpeedMargin), mem.U8s[wielderEntry + Offsets.ASpeed]);

        SetActed(mem, 1);
        iai.Tick(onField: true, now);   // the NEXT genuine 0->1 edge -> release
        Assert.Equal((byte)5, mem.U8s[wielderEntry + Offsets.ASpeed]);
    }

    // ---- I3: level-up drift does not block the pointer release (the anti-Option-A property) ----

    [Fact]
    public void LevelUp_midHold_still_releases()
    {
        // nameId constant while the band level drifts -- identity is drift-proof, the WHOLE point.
        var (iai, mem, wielderEntry, _) = Build(naturalSpeed: 5, fieldUnitSpeed: 10, nameId: 298);
        var now = DateTime.UtcNow;

        iai.Tick(onField: true, now);   // armed + held; primed (pointer unseeded)

        // Simulate mid-battle level-up (band-entry drifts; roster row 0 stays lvl=30).
        mem.U8s[wielderEntry + Offsets.ALevel] = 31;
        mem.U16s[wielderEntry + Offsets.AMaxHp] = 320;

        // Pointer arrives at the wielder's entry -- release compares the ADDRESS-derived identity
        // (frame nameId), not a fingerprint, so the drift above must not block the release.
        PointActorAt(mem, wielderEntry);
        iai.Tick(onField: true, now);

        Assert.Equal((byte)5, mem.U8s[wielderEntry + Offsets.ASpeed]);
    }

    // ---- I4: an enemy turn (pointer at a non-wielder entry) releases nobody ----

    [Fact]
    public void Enemy_turn_no_release()
    {
        var mem  = new FakeSparseMemory();
        var meta = new Dictionary<int, WeaponMeta>
        {
            [AmeNoMurakumoId] = new WeaponMeta
            {
                Name = "Ame-no-Murakumo", Wp = 10, Cat = "Katana", Formula = 1,
                Flavor = "gathering-storm blade",
                Signature = new WeaponSignature { AtTier = 3, Iai = true, DisplayLabel = "Iai" }
            }
        };
        var kills = new Dictionary<int, int> { [AmeNoMurakumoId] = Tuning.ProdThresholds[2] };

        MemSeats.SeatRoster(mem, 0, lvl: 30, br: 65, fa: 60, rh: AmeNoMurakumoId, nameId: 298);
        long entryA = Band.Entry(24);
        MemSeats.SeatBand(mem, 24, weapon: AmeNoMurakumoId,
                          lvl: 30, br: 65, fa: 60, gx: 3, gy: 3, hp: 200, maxHp: 300, speed: 7);
        MemSeats.SeatFrameNameId(mem, 24, 298);
        mem.WritableAddrs.Add(entryA + Offsets.ASpeed);

        MemSeats.SeatRoster(mem, 1, lvl: 35, br: 70, fa: 55, rh: AmeNoMurakumoId, nameId: 301);
        long entryB = Band.Entry(22);
        MemSeats.SeatBand(mem, 22, weapon: AmeNoMurakumoId,
                          lvl: 35, br: 70, fa: 55, gx: 8, gy: 8, hp: 200, maxHp: 300, speed: 8);
        MemSeats.SeatFrameNameId(mem, 22, 301);
        mem.WritableAddrs.Add(entryB + Offsets.ASpeed);

        MemSeats.SeatBand(mem, 0, weapon: OtherWeaponId,
                          lvl: 20, br: 50, fa: 50, gx: 5, gy: 5, hp: 100, maxHp: 100, speed: 10);
        long entryEnemy = Band.Entry(0);
        MemSeats.SeatFrameNameId(mem, 0, 999);   // enemy frame nameId distinct from either wielder's

        var iai = new Iai(meta, kills, mem);
        var now = DateTime.UtcNow;

        iai.Tick(onField: true, now);   // prime + arm both

        PointActorAt(mem, entryEnemy);   // enemy's turn -- frame nameId (999) matches neither hold
        SetActed(mem, 1);
        iai.Tick(onField: true, now);

        Assert.Equal((byte)(10 + Tuning.IaiSpeedMargin), mem.U8s[entryA + Offsets.ASpeed]);
        Assert.Equal((byte)(10 + Tuning.IaiSpeedMargin), mem.U8s[entryB + Offsets.ASpeed]);
    }

    // ---- Capture-before-write: revert writes the ORIGINAL natural (F5) ----

    [Fact]
    public void CaptureBeforeWrite_revert_to_original_natural_not_inflated()
    {
        const int naturalSpeed = 5;
        var (iai, mem, wielderEntry, _) = Build(naturalSpeed: naturalSpeed, fieldUnitSpeed: 10);
        var now = DateTime.UtcNow;

        // Five boosted ticks (primed on tick 1, pointer stays unseeded/0); Speed held at 11 each tick.
        for (int i = 0; i < 5; i++) iai.Tick(onField: true, now);
        Assert.Equal(10 + Tuning.IaiSpeedMargin, (int)mem.U8s[wielderEntry + Offsets.ASpeed]);

        // Trigger release via pointer arrival.
        PointActorAt(mem, wielderEntry);
        iai.Tick(onField: true, now);

        // Must write back 5 (original natural), not 11.
        Assert.Equal((byte)naturalSpeed, mem.U8s[wielderEntry + Offsets.ASpeed]);
    }

    // ---- Level-up regression: band drift must not rotate the roster-fp key (F1) ----

    [Fact]
    public void LevelUpMidHold_roster_fp_key_does_not_rotate()
    {
        const int naturalSpeed = 5;
        var (iai, mem, wielderEntry, _) = Build(naturalSpeed: naturalSpeed, fieldUnitSpeed: 10);
        var now = DateTime.UtcNow;

        // Arm, then release via pointer arrival.
        iai.Tick(onField: true, now);
        PointActorAt(mem, wielderEntry);
        iai.Tick(onField: true, now);   // released: Speed reverted to natural

        // Simulate mid-battle level-up (band-entry drifts; roster row 0 stays lvl=30).
        mem.U8s[wielderEntry + Offsets.ALevel] = 31;
        mem.U16s[wielderEntry + Offsets.AMaxHp] = 320;

        // Released state must persist -- no re-arm, no write.
        mem.Written.Clear();
        iai.Tick(onField: true, now);
        Assert.False(mem.Written.ContainsKey(wielderEntry + Offsets.ASpeed),
            "roster-fp key must not rotate on level-up; released hold must persist");
    }

    // ---- Wall-clock cap (safety terminator) ----

    [Fact]
    public void ReleaseCap_reverts_speed_and_stops_holding()
    {
        // No pointer signal at all (ActorPtr unseeded, always 0) -> only the cap fires.
        var (iai, mem, wielderEntry, _) = Build(naturalSpeed: 5, fieldUnitSpeed: 10);
        var startNow = DateTime.UtcNow;

        iai.Tick(onField: true, startNow);   // armed; Speed = 11
        Assert.Equal(10 + Tuning.IaiSpeedMargin, (int)mem.U8s[wielderEntry + Offsets.ASpeed]);

        var capTime = startNow.AddSeconds(Tuning.IaiHoldCapSeconds + 1);
        mem.Written.Clear();
        iai.Tick(onField: true, capTime);   // cap fires -> reverts to natural(5)

        Assert.True(mem.Written.ContainsKey(wielderEntry + Offsets.ASpeed));
        Assert.Equal((byte)5, mem.U8s[wielderEntry + Offsets.ASpeed]);

        // No further writes after cap release.
        mem.Written.Clear();
        iai.Tick(onField: true, capTime);
        Assert.False(mem.Written.ContainsKey(wielderEntry + Offsets.ASpeed));
    }

    // ---- ResetBattle ----

    [Fact]
    public void ResetBattle_clears_holds_so_next_battle_arms_again()
    {
        const int naturalSpeed = 5;
        var (iai, mem, wielderEntry, _) = Build(naturalSpeed: naturalSpeed, fieldUnitSpeed: 10);
        var now = DateTime.UtcNow;

        // Battle 1: arm, then release via pointer arrival.
        iai.Tick(onField: true, now);
        PointActorAt(mem, wielderEntry);
        iai.Tick(onField: true, now);   // released

        mem.Written.Clear();
        iai.Tick(onField: true, now);
        Assert.False(mem.Written.ContainsKey(wielderEntry + Offsets.ASpeed),
            "should stay released within the same battle");

        // Battle 2: reset then re-arm.
        iai.ResetBattle();
        mem.U8s[wielderEntry + Offsets.ASpeed] = naturalSpeed;   // restore natural
        PointActorNowhere(mem);   // clear the pointer so battle 2 re-primes on a clean slate
        mem.Written.Clear();

        iai.Tick(onField: true, now);
        Assert.True(mem.Written.ContainsKey(wielderEntry + Offsets.ASpeed),
            "after ResetBattle, wielder must be re-armed on the next tick");
    }

    // ---- Gate: off-field ----

    [Fact]
    public void Gate_offField_no_write()
    {
        var (iai, mem, wielderEntry, _) = Build();
        iai.Tick(onField: false, DateTime.UtcNow);
        Assert.False(mem.Written.ContainsKey(wielderEntry + Offsets.ASpeed));
    }

    // ---- Gate: tier < 3 ----

    [Fact]
    public void Gate_below_tier3_no_write()
    {
        var (iai, mem, wielderEntry, _) = Build(kills: Tuning.ProdThresholds[1]);   // tier 2
        iai.Tick(onField: true, DateTime.UtcNow);
        Assert.False(mem.Written.ContainsKey(wielderEntry + Offsets.ASpeed));
    }

    // ---- Gate: offhand-only wielder ----

    [Fact]
    public void Gate_offhand_only_no_write()
    {
        var mem  = new FakeSparseMemory();
        var meta = new Dictionary<int, WeaponMeta>
        {
            [AmeNoMurakumoId] = new WeaponMeta
            {
                Name = "Ame-no-Murakumo", Wp = 10, Cat = "Katana", Formula = 1,
                Flavor = "gathering-storm blade",
                Signature = new WeaponSignature { AtTier = 3, Iai = true, DisplayLabel = "Iai" }
            }
        };
        var kills = new Dictionary<int, int> { [AmeNoMurakumoId] = Tuning.ProdThresholds[2] };

        MemSeats.SeatRoster(mem, 0, lvl: 30, br: 65, fa: 60, rh: OtherWeaponId, oh: AmeNoMurakumoId);
        long wielder = Band.Entry(24);
        MemSeats.SeatBand(mem, 24, weapon: OtherWeaponId, lvl: 30, br: 65, fa: 60,
                          gx: 2, gy: 2, hp: 200, maxHp: 300, speed: 8);
        mem.WritableAddrs.Add(wielder + Offsets.ASpeed);

        var iai = new Iai(meta, kills, mem);
        iai.Tick(onField: true, DateTime.UtcNow);

        Assert.False(mem.Written.ContainsKey(wielder + Offsets.ASpeed),
            "offhand-only wielder must not trigger the Speed hold");
    }

    // ---- Gate: missing meta ----

    [Fact]
    public void Gate_missing_meta_no_crash_no_write()
    {
        var mem   = new FakeSparseMemory();
        var meta  = new Dictionary<int, WeaponMeta>();
        var kills = new Dictionary<int, int> { [AmeNoMurakumoId] = Tuning.ProdThresholds[2] };
        var iai   = new Iai(meta, kills, mem);

        iai.Tick(onField: true, DateTime.UtcNow);

        Assert.Empty(mem.Written);
    }

    // ---- Gate: null signature ----

    [Fact]
    public void Gate_null_signature_no_crash_no_write()
    {
        var mem  = new FakeSparseMemory();
        var meta = new Dictionary<int, WeaponMeta>
        {
            [AmeNoMurakumoId] = new WeaponMeta
            {
                Name = "Ame-no-Murakumo", Wp = 10, Cat = "Katana", Formula = 1,
                Flavor = "gathering-storm blade",
                Signature = null
            }
        };
        var kills = new Dictionary<int, int> { [AmeNoMurakumoId] = Tuning.ProdThresholds[2] };
        var iai   = new Iai(meta, kills, mem);

        iai.Tick(onField: true, DateTime.UtcNow);

        Assert.Empty(mem.Written);
    }

    // ---- LW-71: turn-flags corroboration (Band.FlagOwner, LW-63) overrides the parked pointer ----
    //
    // Bug: the engine actor pointer PARKS on struck units. An enemy striking the wielder before
    // its own opening turn makes the pointer transition to the wielder's frame, a false S1
    // arrival, releasing the hold mid-enemy-turn and defeating the signature. Fix: every
    // release is corroborated against Band.FlagOwner (the per-unit PSX turn flag, structurally
    // naming whichever unit's turn is OPEN) via Iai.Policy.FlagCorroboration/ReleaseDecision.

    // ---- Pure: FlagCorroboration truth table ----

    [Fact]
    public void FlagCorroboration_unresolved_is_indeterminate()
        => Assert.Equal(Iai.FlagVerdict.Indeterminate,
            Iai.FlagCorroboration(ownerResolved: false, ownerIdentityKnown: true, ownerIsWielder: true));

    [Fact]
    public void FlagCorroboration_resolved_but_identity_unknown_is_indeterminate()
        => Assert.Equal(Iai.FlagVerdict.Indeterminate,
            Iai.FlagCorroboration(ownerResolved: true, ownerIdentityKnown: false, ownerIsWielder: true));

    [Fact]
    public void FlagCorroboration_resolved_matching_wielder_confirms()
        => Assert.Equal(Iai.FlagVerdict.Confirm,
            Iai.FlagCorroboration(ownerResolved: true, ownerIdentityKnown: true, ownerIsWielder: true));

    [Fact]
    public void FlagCorroboration_resolved_mismatching_wielder_refuses()
        => Assert.Equal(Iai.FlagVerdict.Refuse,
            Iai.FlagCorroboration(ownerResolved: true, ownerIdentityKnown: true, ownerIsWielder: false));

    // ---- Pure: ReleaseDecision truth table ----

    [Fact]
    public void ReleaseDecision_confirm_fires_without_legacy_signal()
        => Assert.True(Iai.ReleaseDecision(legacySignal: false, flags: Iai.FlagVerdict.Confirm));

    [Fact]
    public void ReleaseDecision_refuse_blocks_even_with_firing_legacy_signal()
        // The load-bearing row: a firing legacy signal (the parked-pointer arrival) must NOT
        // release when the flags verdict names a different unit's turn as open.
        => Assert.False(Iai.ReleaseDecision(legacySignal: true, flags: Iai.FlagVerdict.Refuse));

    [Fact]
    public void ReleaseDecision_indeterminate_passes_through_true_legacy()
        => Assert.True(Iai.ReleaseDecision(legacySignal: true, flags: Iai.FlagVerdict.Indeterminate));

    [Fact]
    public void ReleaseDecision_indeterminate_passes_through_false_legacy()
        => Assert.False(Iai.ReleaseDecision(legacySignal: false, flags: Iai.FlagVerdict.Indeterminate));

    // ---- Stateful: the load-bearing repro ----

    [Fact]
    public void Parked_pointer_arrival_during_enemy_turn_does_not_release()
    {
        // The real turn owner is an ENEMY (band 0, t==1, a distinct nameId). The engine's actor
        // pointer nonetheless PARKS on the wielder's own frame (a struck-victim arrival):
        // exactly the false S1 signal LW-71 exists to override.
        //
        // NON-VACUITY: this test MUST fail against the unmodified (pre-LW-71) Tick, which
        // releases on the pointer arrival alone with no turn-flags corroboration at all.
        var mem  = new FakeSparseMemory();
        var meta = new Dictionary<int, WeaponMeta>
        {
            [AmeNoMurakumoId] = new WeaponMeta
            {
                Name = "Ame-no-Murakumo", Wp = 10, Cat = "Katana", Formula = 1,
                Flavor = "gathering-storm blade",
                Signature = new WeaponSignature { AtTier = 3, Iai = true, DisplayLabel = "Iai" }
            }
        };
        var kills = new Dictionary<int, int> { [AmeNoMurakumoId] = Tuning.ProdThresholds[2] };

        const int wielderNameId = 298, strikerNameId = 301;
        const int naturalSpeed = 8;

        MemSeats.SeatRoster(mem, 0, lvl: 30, br: 65, fa: 60, rh: AmeNoMurakumoId, nameId: wielderNameId);
        long wielderEntry = Band.Entry(24);
        MemSeats.SeatBand(mem, 24, weapon: AmeNoMurakumoId,
                          lvl: 30, br: 65, fa: 60, gx: 2, gy: 2, hp: 200, maxHp: 300, speed: naturalSpeed);
        MemSeats.SeatFrameNameId(mem, 24, wielderNameId);
        mem.WritableAddrs.Add(wielderEntry + Offsets.ASpeed);

        // The striker: real position, sane fingerprint, ATurnFlag==1 (its turn is really open),
        // a nameId distinct from the wielder's.
        long strikerEntry = Band.Entry(0);
        MemSeats.SeatBand(mem, 0, weapon: OtherWeaponId,
                          lvl: 20, br: 50, fa: 50, gx: 5, gy: 5, hp: 100, maxHp: 100, speed: 10);
        MemSeats.SeatFrameNameId(mem, 0, strikerNameId);
        mem.U8s[strikerEntry + Offsets.ATurnFlag] = 1;

        var iai = new Iai(meta, kills, mem);
        var now = DateTime.UtcNow;

        iai.Tick(onField: true, now);   // arm + prime
        Assert.Equal((byte)(10 + Tuning.IaiSpeedMargin), mem.U8s[wielderEntry + Offsets.ASpeed]);

        // The pointer PARKS on the wielder's frame even though the striker still holds the real
        // open turn (t==1 there, not at the wielder).
        PointActorAt(mem, wielderEntry);
        iai.Tick(onField: true, now);

        Assert.Equal((byte)(10 + Tuning.IaiSpeedMargin), mem.U8s[wielderEntry + Offsets.ASpeed]);
    }

    [Fact]
    public void Turn_flags_release_fires_even_when_pointer_is_parked_elsewhere()
    {
        // The wielder's own entry holds the turn flag (t==1, the real turn owner), while the
        // actor pointer stays parked on an unrelated entry for the whole test (no arrival, no
        // Acted edge, so the legacy signal never fires). The flags verdict alone must release, and
        // the restore must land on the FLAG OWNER's address, not the parked pointer's: pins the
        // Confirm-restore-to-flagEntry rule against a naive `acting + ASpeed` restore.
        //
        // NON-VACUITY: this test MUST fail against the unmodified (pre-LW-71) Tick, which never
        // releases without a firing legacy pointer signal.
        var mem  = new FakeSparseMemory();
        var meta = new Dictionary<int, WeaponMeta>
        {
            [AmeNoMurakumoId] = new WeaponMeta
            {
                Name = "Ame-no-Murakumo", Wp = 10, Cat = "Katana", Formula = 1,
                Flavor = "gathering-storm blade",
                Signature = new WeaponSignature { AtTier = 3, Iai = true, DisplayLabel = "Iai" }
            }
        };
        var kills = new Dictionary<int, int> { [AmeNoMurakumoId] = Tuning.ProdThresholds[2] };

        const int wielderNameId = 298;
        const int naturalSpeed = 8;
        const int parkedSpeed = 10;

        MemSeats.SeatRoster(mem, 0, lvl: 30, br: 65, fa: 60, rh: AmeNoMurakumoId, nameId: wielderNameId);
        long wielderEntry = Band.Entry(24);
        MemSeats.SeatBand(mem, 24, weapon: AmeNoMurakumoId,
                          lvl: 30, br: 65, fa: 60, gx: 2, gy: 2, hp: 200, maxHp: 300, speed: naturalSpeed);
        MemSeats.SeatFrameNameId(mem, 24, wielderNameId);
        mem.WritableAddrs.Add(wielderEntry + Offsets.ASpeed);
        mem.U8s[wielderEntry + Offsets.ATurnFlag] = 1;   // the wielder's own turn is really OPEN

        long parkedEntry = Band.Entry(0);
        MemSeats.SeatBand(mem, 0, weapon: OtherWeaponId,
                          lvl: 20, br: 50, fa: 50, gx: 5, gy: 5, hp: 100, maxHp: 100, speed: parkedSpeed);
        // Writable on purpose: a naive restore to the parked pointer's address must LAND here
        // and trip the untouched assert below, not get silently skipped by the Writable guard.
        mem.WritableAddrs.Add(parkedEntry + Offsets.ASpeed);

        var iai = new Iai(meta, kills, mem);
        var now = DateTime.UtcNow;

        // The pointer parks on the OTHER entry from the very first tick and never moves.
        PointActorAt(mem, parkedEntry);

        iai.Tick(onField: true, now);   // arm + prime
        Assert.Equal((byte)(10 + Tuning.IaiSpeedMargin), mem.U8s[wielderEntry + Offsets.ASpeed]);

        iai.Tick(onField: true, now);   // no pointer transition, no Acted edge, flags alone release

        Assert.Equal((byte)naturalSpeed, mem.U8s[wielderEntry + Offsets.ASpeed]);
        Assert.Equal((byte)parkedSpeed, mem.U8s[parkedEntry + Offsets.ASpeed]);
    }

    [Fact]
    public void Zero_turn_flags_preserve_the_legacy_pointer_release()
    {
        // No band entry anywhere has ATurnFlag==1 (the tape-verified battle-opening zero-t
        // record). FlagOwner resolves nothing -> Indeterminate -> the legacy pointer signal must
        // still govern, exactly as before LW-71: release must never starve on a genuinely
        // zero-t tick.
        var (iai, mem, wielderEntry, _) = Build(naturalSpeed: 5, fieldUnitSpeed: 10, nameId: 298);
        var now = DateTime.UtcNow;

        iai.Tick(onField: true, now);   // arm + prime

        PointActorAt(mem, wielderEntry);   // S1 arrival, the only signal available
        iai.Tick(onField: true, now);

        Assert.Equal((byte)5, mem.U8s[wielderEntry + Offsets.ASpeed]);
    }

    // ---- Stateful: address-fallback lane (identity capture failed, nameId left 0) ----

    [Fact]
    public void AddressFallback_flagOwner_elsewhere_refuses_despite_pointer_arrival()
    {
        // Identity capture failed (nameId left 0 in Build) -> address-fallback path. The flag
        // owner resolves to a DIFFERENT band entry than the hold's LastEntry, so the verdict must
        // REFUSE even though the pointer arrives at the wielder (a firing legacy signal).
        var (iai, mem, wielderEntry, _) = Build(naturalSpeed: 5, fieldUnitSpeed: 10);   // nameId=0 default
        var now = DateTime.UtcNow;
        long otherEntry = Band.Entry(0);

        iai.Tick(onField: true, now);   // arm + prime; records LastEntry = wielderEntry

        mem.U8s[otherEntry + Offsets.ATurnFlag] = 1;   // the real turn owner is the OTHER entry
        PointActorAt(mem, wielderEntry);               // parked-victim arrival at the wielder
        iai.Tick(onField: true, now);

        Assert.Equal((byte)(10 + Tuning.IaiSpeedMargin), mem.U8s[wielderEntry + Offsets.ASpeed]);
    }

    [Fact]
    public void AddressFallback_flagOwner_at_lastEntry_confirms_release()
    {
        // Identity capture failed (nameId left 0) -> address-fallback path. The flag owner
        // resolves to the hold's own LastEntry (the wielder's real address) -> Confirm releases,
        // even with no pointer signal at all.
        var (iai, mem, wielderEntry, _) = Build(naturalSpeed: 5, fieldUnitSpeed: 10);   // nameId=0 default
        var now = DateTime.UtcNow;

        iai.Tick(onField: true, now);   // arm + prime; records LastEntry = wielderEntry
        Assert.Equal((byte)(10 + Tuning.IaiSpeedMargin), mem.U8s[wielderEntry + Offsets.ASpeed]);

        mem.U8s[wielderEntry + Offsets.ATurnFlag] = 1;   // the wielder's own turn is really open
        iai.Tick(onField: true, now);   // no pointer signal at all, flags verdict alone releases

        Assert.Equal((byte)5, mem.U8s[wielderEntry + Offsets.ASpeed]);
    }
}
