using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// GrowthEngine.MatchesEntry: the pure predicate behind LocateStruct -- weapon, brave, faith,
/// AND level must all agree before the runtime considers a combat-struct slot the wielder's.
/// Tested via GCHandle-pinned buffers (Mem uses RPM/WPM on our own process, so it works on
/// any committed address -- no live game needed).
///
/// Also pins the scan-order contract: player-side slots (n >= 0 around the combat anchor) are
/// tried before enemy-side, so an enemy that shares the player's full fingerprint cannot be
/// silently adopted. Homogeneous twin tie-break: when all matches share the same identity,
/// prefer the real-position (non-origin) entry; only heterogeneous multi-matches refuse.
/// </summary>
public class GrowthEngineTests
{
    // Pinned buffers are committed addresses in our own process, so the production adapter's
    // RPM/WPM reads work on them for real -- the guard path is exercised, not faked.
    private static readonly LiveMemory Live = new();

    // The combat-struct span MatchesEntry gates with Readable(s, StructSpan)
    // (GrowthEngine.Locate.cs). Well short of the +0x1FC frame nameId, so marking it never
    // covers a deliberately-unreadable nameAddr.
    private const int StructSpan = GrowthEngine.StructSpan;

    /// <summary>Allocate a pinned buffer that looks like a valid combat struct.
    /// level, brave, faith, weapon at the C* offsets; PA/MA in a sane range; gx/gy at
    /// AGx/AGy for the twin tie-break. Caller disposes the PinnedBuf.</summary>
    private static PinnedBuf PinnedStruct(
        int weapon, int level, int brave, int faith, int pa = 10, int ma = 10,
        int gx = 0, int gy = 0)
    {
        var s = PinnedBuf.Of(256);
        var buf = s.Bytes;
        // weapon at CWeapon (0x20), u16 LE
        buf[Offsets.CWeapon]     = (byte)(weapon & 0xFF);
        buf[Offsets.CWeapon + 1] = (byte)((weapon >> 8) & 0xFF);
        // level at CLevel (0x29)
        buf[Offsets.CLevel]  = (byte)level;
        // brave/faith at CBrave/CFaith
        buf[Offsets.CBrave]  = (byte)brave;
        buf[Offsets.CFaith]  = (byte)faith;
        // PA/MA at CPa/CMa (must be in 1..199)
        buf[Offsets.CPa] = (byte)pa;
        buf[Offsets.CMa] = (byte)ma;
        // grid position at AGx/AGy (used by the homogeneous twin tie-break)
        buf[Offsets.AGx] = (byte)gx;
        buf[Offsets.AGy] = (byte)gy;
        return s;
    }

    private static List<(int weapon, WeaponMeta m)> MakeHands(params int[] ids)
    {
        var list = new List<(int, WeaponMeta)>(ids.Length);
        foreach (int id in ids) list.Add((id, new WeaponMeta { Cat = "knife", Formula = 1 }));
        return list;
    }

    /// <summary>S3: a 0x200-byte pinned buffer big enough to carry the frame nameId at
    /// s + Offsets.BandEntry + Offsets.ANameId (== s + 0x1FC) alongside the same C* fields as
    /// <see cref="PinnedStruct"/>. Every tier-1 (nameId &gt; 0) test case must use THIS helper,
    /// never the legacy 256-byte <see cref="PinnedStruct"/> -- that buffer is too small for the
    /// +0x1FC read to safely land inside it.</summary>
    private static PinnedBuf PinnedStruct200(
        int weapon, int level, int brave, int faith, int nameId, int pa = 10, int ma = 10,
        int gx = 0, int gy = 0)
    {
        var s = PinnedBuf.Of(0x200);
        var buf = s.Bytes;
        buf[Offsets.CWeapon]     = (byte)(weapon & 0xFF);
        buf[Offsets.CWeapon + 1] = (byte)((weapon >> 8) & 0xFF);
        buf[Offsets.CLevel]  = (byte)level;
        buf[Offsets.CBrave]  = (byte)brave;
        buf[Offsets.CFaith]  = (byte)faith;
        buf[Offsets.CPa] = (byte)pa;
        buf[Offsets.CMa] = (byte)ma;
        buf[Offsets.AGx] = (byte)gx;
        buf[Offsets.AGy] = (byte)gy;
        int nameOff = Offsets.BandEntry + Offsets.ANameId;   // == 0x1FC
        buf[nameOff]     = (byte)(nameId & 0xFF);
        buf[nameOff + 1] = (byte)((nameId >> 8) & 0xFF);
        return s;
    }

    private static GrowthEngine NewEngine()
        => new(new Dictionary<int, WeaponMeta>(), new Dictionary<int, int>(), new TurnTracker(new FakeSparseMemory()));

    // ---- MatchesEntry: level is part of the predicate ----

    [Fact]
    public void MatchesEntry_true_when_all_fields_agree()
    {
        using var s = PinnedStruct(weapon: 10, level: 30, brave: 65, faith: 58);
        Assert.True(GrowthEngine.MatchesEntry(Live, s.Addr, level: 30, brave: 65, faith: 58, MakeHands(10)));
    }

    [Fact]
    public void MatchesEntry_false_when_level_differs()
    {
        // Same weapon/brave/faith as the player, but a different level -- an enemy collision.
        using var s = PinnedStruct(weapon: 10, level: 25, brave: 65, faith: 58);
        Assert.False(GrowthEngine.MatchesEntry(Live, s.Addr, level: 30, brave: 65, faith: 58, MakeHands(10)));
    }

    [Fact]
    public void MatchesEntry_false_when_brave_differs()
    {
        using var s = PinnedStruct(weapon: 10, level: 30, brave: 66, faith: 58);
        Assert.False(GrowthEngine.MatchesEntry(Live, s.Addr, level: 30, brave: 65, faith: 58, MakeHands(10)));
    }

    [Fact]
    public void MatchesEntry_false_when_weapon_not_in_hands()
    {
        using var s = PinnedStruct(weapon: 99, level: 30, brave: 65, faith: 58);
        Assert.False(GrowthEngine.MatchesEntry(Live, s.Addr, level: 30, brave: 65, faith: 58, MakeHands(10)));
    }

    [Fact]
    public void MatchesEntry_false_when_pa_out_of_sane_range()
    {
        using var s = PinnedStruct(weapon: 10, level: 30, brave: 65, faith: 58, pa: 0);
        Assert.False(GrowthEngine.MatchesEntry(Live, s.Addr, level: 30, brave: 65, faith: 58, MakeHands(10)));
    }

    // ---- ScanStructEntries: ambiguity + player-preference ----
    // This verifies the scan-order contract (player before enemy) and the ambiguity refusal.

    [Fact]
    public void ScanStructEntries_returns_player_slot_when_only_player_matches()
    {
        using var player = PinnedStruct(weapon: 10, level: 30, brave: 65, faith: 58);
        using var enemy  = PinnedStruct(weapon: 10, level: 99, brave: 65, faith: 58);  // enemy: different level
        // player slot (isPlayer=true), enemy slot (isPlayer=false)
        var entries = new[] { (isPlayer: false, addr: enemy.Addr), (isPlayer: true, addr: player.Addr) };
        long result = GrowthEngine.ScanEntries(Live, 30, 65, 58, MakeHands(10), entries);
        Assert.Equal(player.Addr, result);
    }

    [Fact]
    public void ScanStructEntries_returns_player_when_player_and_enemy_share_full_fingerprint()
    {
        // Player-side preference: when both a player and an enemy slot match, take the player
        // (the player-side pass fires first and exits the outer loop before enemy-side is checked).
        using var player = PinnedStruct(weapon: 10, level: 30, brave: 65, faith: 58);
        using var enemy  = PinnedStruct(weapon: 10, level: 30, brave: 65, faith: 58);
        var entries = new[] { (isPlayer: true, addr: player.Addr), (isPlayer: false, addr: enemy.Addr) };
        long result = GrowthEngine.ScanEntries(Live, 30, 65, 58, MakeHands(10), entries);
        Assert.Equal(player.Addr, result);
    }

    [Fact]
    public void ScanStructEntries_refuses_when_two_player_slots_have_different_weapons()
    {
        // Two player-side entries with different weapon ids -> heterogeneous, refuse.
        using var p1 = PinnedStruct(weapon: 10, level: 30, brave: 65, faith: 58, gx: 3, gy: 4);
        using var p2 = PinnedStruct(weapon: 11, level: 30, brave: 65, faith: 58, gx: 5, gy: 6);
        var entries = new[] { (isPlayer: true, addr: p1.Addr), (isPlayer: true, addr: p2.Addr) };
        long result = GrowthEngine.ScanEntries(Live, 30, 65, 58, MakeHands(10, 11), entries);
        Assert.Equal(0, result);
    }

    [Fact]
    public void ScanStructEntries_returns_zero_when_nothing_matches()
    {
        using var s = PinnedStruct(weapon: 99, level: 30, brave: 65, faith: 58);
        var entries = new[] { (isPlayer: true, addr: s.Addr) };
        long result = GrowthEngine.ScanEntries(Live, 30, 65, 58, MakeHands(10), entries);
        Assert.Equal(0, result);
    }

    // ---- ScanStructEntries: homogeneous twin tie-break ----

    [Fact]
    public void ScanStructEntries_prefers_real_position_over_origin_twin()
    {
        // Frozen twins: same identity, one at (0,0), one at a real grid position.
        // The real-position entry should win -- this is the regression for the static-array
        // battle-restart freeze phenomenon (live copy at real pos, frozen copy at (0,0)).
        using var frozen = PinnedStruct(weapon: 10, level: 30, brave: 65, faith: 58, gx: 0, gy: 0);
        using var live   = PinnedStruct(weapon: 10, level: 30, brave: 65, faith: 58, gx: 4, gy: 7);
        var entries = new[] { (isPlayer: true, addr: frozen.Addr), (isPlayer: true, addr: live.Addr) };
        long result = GrowthEngine.ScanEntries(Live, 30, 65, 58, MakeHands(10), entries);
        Assert.Equal(live.Addr, result);
    }

    [Fact]
    public void ScanStructEntries_returns_one_when_all_twins_share_identity_at_origin()
    {
        // Both twins at (0,0) with identical fingerprint: unit on corner tile -- return one
        // deterministically rather than refusing (homogeneous = ONE unit, not two different ones).
        using var t1 = PinnedStruct(weapon: 10, level: 30, brave: 65, faith: 58, gx: 0, gy: 0);
        using var t2 = PinnedStruct(weapon: 10, level: 30, brave: 65, faith: 58, gx: 0, gy: 0);
        var entries = new[] { (isPlayer: true, addr: t1.Addr), (isPlayer: true, addr: t2.Addr) };
        long result = GrowthEngine.ScanEntries(Live, 30, 65, 58, MakeHands(10), entries);
        Assert.NotEqual(0, result);
        Assert.True(result == t1.Addr || result == t2.Addr);
    }

    // ---- LW-252 stage 6, decision-4 exception (b): 2+ REAL-position homogeneous twins with NO
    // verified identity (nameId <= 0) must REFUSE, not guess -- a wrong-unit stat HOLD is a
    // wrong-unit WRITE the engine's per-turn normalize can bake into the save baseline (ledger
    // [per-turn-normalize-repaints-boosted-baseline], UNCERTAIN; LW-90). The single-real-candidate
    // shape (ScanStructEntries_prefers_real_position_over_origin_twin, above) and the both-at-
    // origin shape (ScanStructEntries_returns_one_when_all_twins_share_identity_at_origin, above)
    // already pin the UNCHANGED shapes (X2 parity is covered by the former; no duplicate added). ----

    [Fact]
    public void ScanStructEntries_refuses_two_real_position_homogeneous_twins_with_no_nameId()
    {
        // [X1, THE RED] Two REAL-position entries share the SAME weapon+fp with no verified
        // identity -- genuinely indistinguishable fp-twins. Master: the homogeneous tie-break
        // picks the first real-position entry and would hold a stat on it (a wrong-unit write).
        // Fixed: refuse (miss beats mis-credit).
        using var t1 = PinnedStruct(weapon: 10, level: 30, brave: 65, faith: 58, gx: 3, gy: 4);
        using var t2 = PinnedStruct(weapon: 10, level: 30, brave: 65, faith: 58, gx: 5, gy: 6);
        var entries = new[] { (isPlayer: true, addr: t1.Addr), (isPlayer: true, addr: t2.Addr) };
        long result = GrowthEngine.ScanEntries(Live, 30, 65, 58, MakeHands(10), entries);
        Assert.Equal(0, result);
    }

    [Fact]
    public void ScanEntries_tier1_still_resolves_two_real_position_mirror_copies_sharing_nameId()
    {
        // [X3, parity] Unlike X1, these two real-position entries carry the SAME VERIFIED nameId
        // (a revolving-mirror clone scenario, tier-1) -- the new refusal is scoped to nameId <= 0
        // only; a verified-identity homogeneous multi-match still resolves exactly as before
        // (PickHomogeneousTwin's own mirror dedupe, unchanged).
        using var m1 = PinnedStruct200(weapon: 10, level: 30, brave: 65, faith: 58, nameId: 298, gx: 3, gy: 4);
        using var m2 = PinnedStruct200(weapon: 10, level: 30, brave: 65, faith: 58, nameId: 298, gx: 5, gy: 6);
        var entries = new[] { (isPlayer: true, addr: m1.Addr), (isPlayer: true, addr: m2.Addr) };
        long result = GrowthEngine.ScanEntries(Live, 30, 65, 58, MakeHands(10), entries, nameId: 298, out int matchCount);
        Assert.NotEqual(0, result);
        Assert.True(result == m1.Addr || result == m2.Addr);
        Assert.Equal(2, matchCount);
    }

    // ---- LocateStruct: ambiguous-log throttle ----

    [Fact]
    public void LocateStruct_logs_ambiguous_once_per_battle_per_slot()
    {
        // Heterogeneous multi-match (two different weapons) -> refused + log throttled to once.
        using var p1 = PinnedStruct(weapon: 10, level: 30, brave: 65, faith: 58, gx: 3, gy: 4);
        using var p2 = PinnedStruct(weapon: 11, level: 30, brave: 65, faith: 58, gx: 5, gy: 6);
        var engine = new GrowthEngine(
            new System.Collections.Generic.Dictionary<int, WeaponMeta>(),
            new System.Collections.Generic.Dictionary<int, int>(),
            new TurnTracker(new FakeSparseMemory()));
        var entries = new[] { (isPlayer: true, addr: p1.Addr), (isPlayer: true, addr: p2.Addr) };
        var hands = MakeHands(10, 11);
        // First call: heterogeneous -> refuses, marks the slot as logged.
        engine.LocateIn(0, 30, 65, 58, hands, entries);
        Assert.True(engine.IsAmbiguousLogged(0), "first call should mark slot 0 as logged");
        // Second call same slot: still refused, throttle suppresses a second log entry.
        engine.LocateIn(0, 30, 65, 58, hands, entries);
        Assert.True(engine.IsAmbiguousLogged(0), "second call: slot still logged, no duplicate");
        // ResetBattle clears the throttle for the new battle.
        engine.ResetBattle();
        Assert.False(engine.IsAmbiguousLogged(0), "after ResetBattle throttle should be cleared");
    }

    // The main-hand-only signature gate (A7: a Living Weapon earns kills in any hand, but
    // commands its gift only from the main hand) is exercised through Apply's single RRHand
    // snapshot; the roster-resolve contract is pinned by WielderTests.TryResolveMainHand_*
    // and KillTrackerTests' main-hand latch cases.

    // ==== Two-tier nameId locate (D1/D2/D5/S3/S4/S6) ====

    // ---- MatchesEntry: tier-1 nameId predicate ----

    [Fact]
    public void MatchesEntry_nameId_true_when_frame_nameId_matches()
    {
        using var s = PinnedStruct200(weapon: 10, level: 30, brave: 65, faith: 58, nameId: 298);
        Assert.True(GrowthEngine.MatchesEntry(Live, s.Addr, level: 30, brave: 65, faith: 58, MakeHands(10), nameId: 298));
    }

    [Fact]
    public void MatchesEntry_nameId_false_when_frame_nameId_differs()
    {
        // Same weapon/level/brave/faith as the roster owner, but a FOREIGN frame nameId --
        // an enemy fp-collider today's fingerprint-only predicate could not tell apart.
        using var s = PinnedStruct200(weapon: 10, level: 30, brave: 65, faith: 58, nameId: 918);
        Assert.False(GrowthEngine.MatchesEntry(Live, s.Addr, level: 30, brave: 65, faith: 58, MakeHands(10), nameId: 298));
    }

    [Fact]
    public void MatchesEntry_nameId_mode_unreadable_nameAddr_fails_tier1()
    {
        // FakeSparseMemory gives deterministic control over "readable" (unlike a pinned buffer,
        // whose OOB region is at the mercy of GC layout -- exactly why S3 forbids depending on
        // it): mark the struct span readable but leave s+0x1FC out of ReadableAddrs entirely.
        var mem = new FakeSparseMemory();
        long s = 0x5000_0000;
        mem.MarkReadable(s, StructSpan);   // production gates Readable(s, StructSpan) (GrowthEngine.Locate.cs MatchesEntry)
        mem.U16s[s + Offsets.CWeapon] = 10;
        mem.U8s[s + Offsets.CLevel] = 30;
        mem.U8s[s + Offsets.CBrave] = 65;
        mem.U8s[s + Offsets.CFaith] = 58;
        mem.U8s[s + Offsets.CPa] = 10;
        mem.U8s[s + Offsets.CMa] = 10;
        Assert.False(GrowthEngine.MatchesEntry(mem, s, level: 30, brave: 65, faith: 58, MakeHands(10), nameId: 298));
    }

    // ---- Route: the LANE-DRIVEN decision table (LW-250; supersedes the LW-249 Cat/Formula
    //      chain, which is exactly where the pole mis-route and the four missing-HP weapons'
    //      dead growth both hid). WeaponMeta.Lane -- baked from docs/living_weapon_grid.csv's
    //      locked `grows` design via gen_living_weapon_meta.py's lane_of -- is now the ONLY
    //      routing input past the ownership bails (Afterimage/Ultima/Mushin). Cat and Formula
    //      are routing-DEAD (Route_LaneBeatsCategoryAndFormula pins this directly); the old
    //      Tuning.SkipFormula/IsSpeedFormula/IsCaster/IsMagicCastFormula helpers are deleted.
    //
    //      HONEST-RED SEQUENCE this seat was built under (LW-250 build plan): step 1 added
    //      WeaponMeta.Lane + this RouteMeta helper's optional `lane` param with Route
    //      unchanged (full suite green, the property inert); step 2 wrote
    //      Route_MissingHpWeapon_NowGrowsSpeed and Route_EmptyOrUnknownLane_NoGrowth against
    //      the OLD Route and captured two real failures (formula-67's SkipFormula bail beat
    //      the lane; an empty-lane Sword fell through to PA); step 3 swapped Route to the lane
    //      switch below, deleted the four Tuning helpers, and rewrote every pin here. ----

    private static WeaponMeta RouteMeta(string cat, int formula, string lane = "") =>
        new() { Name = "T", Wp = 5, Cat = cat, Formula = formula, Flavor = "f", Lane = lane };

    [Fact]
    public void Route_LaneSpeed_HoldsSpeedAtSpeedFactor()
    {
        long s = 0x4000;
        Assert.True(GrowthEngine.Route(s, RouteMeta("Sword", 1, lane: "speed"), tier: 2,
            out long addr, out double factor, out var lane));
        Assert.Equal(s + Offsets.CSpeed, addr);
        Assert.Equal(Tuning.SpeedFactor[2], factor);
        Assert.Equal(StatLane.Speed, lane);
    }

    [Fact]
    public void Route_MissingHpWeapon_NowGrowsSpeed()
    {
        // THE LOAD-BEARING TEST (LW-250): formula 67 is missing-HP -- Wrathblade's own damage
        // formula -- and the OLD Route hard-skipped every missing-HP formula (Tuning.
        // SkipFormula) with no way for the grid's locked "Speed" lane to override it. Once
        // Route reads WeaponMeta.Lane instead, a lane of "speed" must win regardless of the
        // formula id. Route_missing_hp_formulas_skip (the old pin for that skip) is RETIRED:
        // after this swap it would pass for the WRONG reason (the empty-lane fail-safe), under
        // a name claiming missing-HP formulas skip growth, which they no longer do.
        long s = 0x4000;
        Assert.True(GrowthEngine.Route(s, RouteMeta("Sword", 67, lane: "speed"), tier: 1,
            out long addr, out double factor, out var lane));
        Assert.Equal(s + Offsets.CSpeed, addr);
        Assert.Equal(Tuning.SpeedFactor[1], factor);
        Assert.Equal(StatLane.Speed, lane);
    }

    [Fact]
    public void Route_LaneMa_HoldsMa()
    {
        long s = 0x4000;
        Assert.True(GrowthEngine.Route(s, RouteMeta("Rod", 1, lane: "ma"), tier: 1,
            out long addr, out double factor, out var lane));
        Assert.Equal(s + Offsets.CMa, addr);
        Assert.Equal(Tuning.Factor[1], factor);
        Assert.Equal(StatLane.Ma, lane);
    }

    [Fact]
    public void Route_LanePa_HoldsPa()
    {
        long s = 0x4000;
        Assert.True(GrowthEngine.Route(s, RouteMeta("Sword", 1, lane: "pa"), tier: 1,
            out long addr, out double factor, out var lane));
        Assert.Equal(s + Offsets.CPa, addr);
        Assert.Equal(Tuning.Factor[1], factor);
        Assert.Equal(StatLane.Pa, lane);
    }

    [Theory]
    [InlineData("")]     // stale-meta defense: an old meta.json / a future bake gap, no lane at all
    [InlineData("hp")]   // a plausible-but-unbaked token: lane_of interim-maps the grid's "HP" token
                          // to "pa" at BAKE time, so Route must never see a raw grows token, and
                          // must fail closed even if one somehow reached it uncased/unmapped
    [InlineData("SPD")]  // garbage: anything outside {"speed","ma","pa"} must fail closed
    public void Route_EmptyOrUnknownLane_NoGrowth(string lane)
    {
        // The stale-meta defense: a PA-category, ordinary-formula weapon with NO real lane
        // baked must NOT fall back to growing PA by category/formula inference. Missing/unknown
        // lane -> no growth, full stop -- covers empty, a plausible-but-unbaked token, and
        // garbage alike, and pins that the out-params are left at their no-write defaults too.
        Assert.False(GrowthEngine.Route(0x4000, RouteMeta("Sword", 1, lane: lane), tier: 1,
            out long addr, out double factor, out _));
        Assert.Equal(0, addr);
        Assert.Equal(0, factor);
    }

    [Theory]
    [InlineData(true, false, false)]   // Afterimage owns Speed (HoldAfterimage)
    [InlineData(false, true, false)]   // Ultima owns PA (HoldUltima)
    [InlineData(false, false, true)]   // Mushin owns PA (HoldMushin)
    public void Route_OwnershipBailsPrecedeLane(bool afterimage, bool ultima, bool mushin)
    {
        // Ordering pin, not red-first (T2/T4 above already carry this seat's red): an owner
        // module's bail must win even when a real lane is baked and ready to route -- Route
        // must never fight HoldAfterimage/HoldUltima/HoldMushin for the same byte.
        var m = RouteMeta("Sword", 1, lane: afterimage ? "speed" : "pa");
        m.Signature = new WeaponSignature { Afterimage = afterimage, Ultima = ultima, Mushin = mushin };
        Assert.False(GrowthEngine.Route(0x4000, m, tier: 1, out _, out _, out _));
    }

    // ---- The following three pins are the LW-249 originals, updated to LANE inputs: the
    //      verdict for each real, unchanged-lane weapon shape (a plain Sword/KnightSword/
    //      Crossbow still PA, a Rod/Staff still MA, an f99 Speed-scaler still Speed) is
    //      preserved -- proving the LW-250 swap changed HOW the verdict is reached, not the
    //      verdict itself for weapons the grid didn't re-lane. ----

    [Theory]
    [InlineData("Pole", 1)]   // LW-250: Poles are PA+MA, interim-mapped to "ma" by lane_of
    [InlineData("Rod", 1)]    // unchanged caster category
    [InlineData("Staff", 1)]  // unchanged caster category
    [InlineData("Gun", 4)]    // LW-250: magic guns are WP+Faith, interim-mapped to "ma"
    public void Route_ma_lane(string cat, int formula)
    {
        long s = 0x4000;
        Assert.True(GrowthEngine.Route(s, RouteMeta(cat, formula, lane: "ma"), tier: 1, out long addr, out _, out var lane));
        Assert.Equal(s + Offsets.CMa, addr);
        Assert.Equal(StatLane.Ma, lane);
    }

    [Theory]
    [InlineData("Sword", 1)]
    [InlineData("KnightSword", 1)]
    [InlineData("Crossbow", 1)]
    public void Route_pa_default(string cat, int formula)
    {
        long s = 0x4000;
        Assert.True(GrowthEngine.Route(s, RouteMeta(cat, formula, lane: "pa"), tier: 1, out long addr, out _, out var lane));
        Assert.Equal(s + Offsets.CPa, addr);
        Assert.Equal(StatLane.Pa, lane);
    }

    [Fact]
    public void Route_speed_formula_routes_speed()
    {
        long s = 0x4000;
        Assert.True(GrowthEngine.Route(s, RouteMeta("Sword", 99, lane: "speed"), tier: 1, out long addr, out double f, out var lane));
        Assert.Equal(s + Offsets.CSpeed, addr);
        Assert.Equal(StatLane.Speed, lane);
        Assert.Equal(Tuning.SpeedFactor[1], f);
    }

    [Fact]
    public void Route_LaneBeatsCategoryAndFormula()
    {
        // THE SEAT'S ACTUAL THESIS: Cat/Formula are routing-dead. A Staff/formula-1 meta would
        // have been the MA-caster default under the old chain -- Lane "speed" wins outright.
        // A Knife/formula-99 meta would have been the speed-formula default -- Lane "pa" wins.
        long s = 0x4000;
        Assert.True(GrowthEngine.Route(s, RouteMeta("Staff", 1, lane: "speed"), tier: 1,
            out long addr1, out _, out var lane1));
        Assert.Equal(s + Offsets.CSpeed, addr1);
        Assert.Equal(StatLane.Speed, lane1);

        Assert.True(GrowthEngine.Route(s, RouteMeta("Knife", 99, lane: "pa"), tier: 1,
            out long addr2, out _, out var lane2));
        Assert.Equal(s + Offsets.CPa, addr2);
        Assert.Equal(StatLane.Pa, lane2);
    }

    // ---- ScanEntries: tier-1 disambiguation + player-first order ----

    [Fact]
    public void ScanEntries_tier1_disambiguates_same_fp_entries_by_nameId()
    {
        // Both entries share the FULL fingerprint AND sit on the PLAYER side (today's predicate
        // would hit the homogeneous twin tie-break and prefer whichever has a real position) --
        // non-vacuity: the collider is the one at a REAL position, so a fp-only scan (no nameId
        // awareness) would wrongly prefer IT over the origin-bound wielder. Only the nameId-exact
        // gate exists to keep the collider out of the candidate pool entirely.
        using var wielder  = PinnedStruct200(weapon: 10, level: 30, brave: 65, faith: 58, nameId: 298);
        using var collider = PinnedStruct200(weapon: 10, level: 30, brave: 65, faith: 58, nameId: 918, gx: 9, gy: 9);
        var entries = new[] { (isPlayer: true, addr: wielder.Addr), (isPlayer: true, addr: collider.Addr) };
        long result = GrowthEngine.ScanEntries(Live, 30, 65, 58, MakeHands(10), entries, nameId: 298, out int matchCount);
        Assert.Equal(wielder.Addr, result);
        Assert.Equal(1, matchCount);
    }

    [Fact]
    public void ScanEntries_tier1_prefers_player_when_both_share_nameId()
    {
        // Player-side pass exits the scan before the enemy-side entry is even considered --
        // unchanged from today's order, now proven under the nameId-exact predicate too.
        using var player = PinnedStruct200(weapon: 10, level: 30, brave: 65, faith: 58, nameId: 298);
        using var enemy  = PinnedStruct200(weapon: 10, level: 30, brave: 65, faith: 58, nameId: 298);
        var entries = new[] { (isPlayer: false, addr: enemy.Addr), (isPlayer: true, addr: player.Addr) };
        long result = GrowthEngine.ScanEntries(Live, 30, 65, 58, MakeHands(10), entries, nameId: 298);
        Assert.Equal(player.Addr, result);
    }

    // ---- LocateIn: two-tier orchestration ----

    [Fact]
    public void LocateIn_tier1_heterogeneous_refusal_does_not_fall_to_tier2()
    {
        // Two DIFFERENT weapons both carrying the roster nameId (a transient mid-equip snapshot):
        // tier 1 sees 2 candidates and refuses (S6) WITHOUT trying tier 2 at all.
        using var p1 = PinnedStruct200(weapon: 10, level: 30, brave: 65, faith: 58, nameId: 298, gx: 3, gy: 4);
        using var p2 = PinnedStruct200(weapon: 11, level: 30, brave: 65, faith: 58, nameId: 298, gx: 5, gy: 6);
        var engine = NewEngine();
        var entries = new[] { (isPlayer: true, addr: p1.Addr), (isPlayer: true, addr: p2.Addr) };
        long result = engine.LocateIn(0, 30, 65, 58, MakeHands(10, 11), entries, rosterNameId: 298);
        Assert.Equal(0, result);
        Assert.True(engine.IsAmbiguousLogged(0));
        Assert.False(engine.IsFallbackLogged(0));   // S6: tier 2 was never attempted
    }

    [Fact]
    public void LocateIn_tier2_veto_excludes_foreign_nameId_keeps_zero_entry()
    {
        // Non-vacuity: the collider sits at a REAL position (the twin tie-break's preferred
        // pick) while the wielder's own zero-nameId entry sits at the origin -- so a fp-only
        // scan (no veto) would wrongly return the collider. The veto must exclude it BEFORE the
        // tie-break ever runs.
        using var collider  = PinnedStruct200(weapon: 10, level: 30, brave: 65, faith: 58, nameId: 918, gx: 9, gy: 9);
        using var zeroEntry = PinnedStruct200(weapon: 10, level: 30, brave: 65, faith: 58, nameId: 0);
        var engine = NewEngine();
        var entries = new[] { (isPlayer: true, addr: collider.Addr), (isPlayer: true, addr: zeroEntry.Addr) };
        long result = engine.LocateIn(0, 30, 65, 58, MakeHands(10), entries, rosterNameId: 298);
        Assert.Equal(zeroEntry.Addr, result);
        Assert.True(engine.IsFallbackLogged(0));
    }

    [Fact]
    public void LocateIn_tier2_veto_passes_entry_with_unreadable_nameId()
    {
        // The same address fails tier 1 (unreadable +0x1FC, D8) AND survives tier 2's veto
        // (an unreadable nameId can't be a foreign collider, so it passes, D2) -- one scenario
        // pinning both halves of the "unreadable" contract at once.
        var mem = new FakeSparseMemory();
        long s = 0x6000_0000;
        mem.MarkReadable(s, StructSpan);   // production gates Readable(s, StructSpan) (GrowthEngine.Locate.cs MatchesEntry)
        mem.U16s[s + Offsets.CWeapon] = 10;
        mem.U8s[s + Offsets.CLevel] = 30;
        mem.U8s[s + Offsets.CBrave] = 65;
        mem.U8s[s + Offsets.CFaith] = 58;
        mem.U8s[s + Offsets.CPa] = 10;
        mem.U8s[s + Offsets.CMa] = 10;
        var engine = new GrowthEngine(new Dictionary<int, WeaponMeta>(), new Dictionary<int, int>(),
                                      new TurnTracker(mem), mem);
        var entries = new[] { (isPlayer: true, addr: s) };
        long result = engine.LocateIn(0, 30, 65, 58, MakeHands(10), entries, rosterNameId: 298);
        Assert.Equal(s, result);
    }

    [Fact]
    public void LocateIn_tier2_veto_preserves_player_first_order()
    {
        // rosterNameId <= 0 (roster capture failed): the veto is inert, tier 2 runs byte-for-
        // byte today's scan -- confirms the vet-then-rescan restructuring left the player-first
        // pass order alone.
        using var player = PinnedStruct200(weapon: 10, level: 30, brave: 65, faith: 58, nameId: 0);
        using var enemy  = PinnedStruct200(weapon: 10, level: 30, brave: 65, faith: 58, nameId: 0);
        var engine = NewEngine();
        var entries = new[] { (isPlayer: false, addr: enemy.Addr), (isPlayer: true, addr: player.Addr) };
        long result = engine.LocateIn(0, 30, 65, 58, MakeHands(10), entries);
        Assert.Equal(player.Addr, result);
    }

    [Fact]
    public void LocateIn_fallback_log_fires_once_per_battle_per_slot()
    {
        using var zeroEntry = PinnedStruct200(weapon: 10, level: 30, brave: 65, faith: 58, nameId: 0);
        var engine = NewEngine();
        var entries = new[] { (isPlayer: true, addr: zeroEntry.Addr) };
        engine.LocateIn(0, 30, 65, 58, MakeHands(10), entries, rosterNameId: 298);
        Assert.True(engine.IsFallbackLogged(0));
        engine.LocateIn(0, 30, 65, 58, MakeHands(10), entries, rosterNameId: 298);   // throttled: no duplicate
        Assert.True(engine.IsFallbackLogged(0));
        engine.ResetBattle();
        Assert.False(engine.IsFallbackLogged(0), "ResetBattle should clear the fallback throttle");
    }

    // ---- LocateStruct: tier-aware cache revalidation (S4) ----

    [Fact]
    public void LocateStruct_tier2_cache_revalidates_via_veto_predicate()
    {
        // Non-vacuity: LiveEntries() scans fixed real-game addresses that are unmapped in the
        // test process, so the ONLY way this can return the pinned buffer's address is the
        // cache fast path -- proving the tier-2 cache was consulted, not a fresh whole-battle
        // rescan.
        using var s = PinnedStruct200(weapon: 10, level: 30, brave: 65, faith: 58, nameId: 0);
        var engine = NewEngine();
        engine.SeedStructForSlotForTest(0, s.Addr, tier: 2);
        long result = engine.LocateStruct(0, 30, 65, 58, MakeHands(10), rosterNameId: 298);
        Assert.Equal(s.Addr, result);
    }

    [Fact]
    public void LocateStruct_tier2_cache_rejected_on_foreign_nameId()
    {
        // A tier-2 cache whose entry now carries a FOREIGN nonzero nameId must be rejected by
        // the veto revalidate -- not blindly reused. The fresh rescan that follows can't find a
        // pinned buffer at the real LiveEntries() addresses, so the miss proves the reject fired.
        using var s = PinnedStruct200(weapon: 10, level: 30, brave: 65, faith: 58, nameId: 918);
        var engine = NewEngine();
        engine.SeedStructForSlotForTest(0, s.Addr, tier: 2);
        long result = engine.LocateStruct(0, 30, 65, 58, MakeHands(10), rosterNameId: 298);
        Assert.Equal(0, result);
    }

    // ==== ReadHp: two-tier-with-veto (D7) ====

    [Fact]
    public void ReadHp_tier1_nameId_match_wins_over_foreign_same_fp_entry()
    {
        // Non-vacuity: the second entry's nameId is 0 (unset), which tier 2's veto would ALSO
        // pass (0 always passes -- it isn't a foreign collider), so tier 2 alone can't tell the
        // two apart and would just take whichever comes LAST in scan order (both at the origin,
        // neither "real"). Only tier 1's EXACT match stops at the wielder's own entry first.
        var mem = new FakeSparseMemory();
        MemSeats.SeatBand(mem, bandIdx: 0, weapon: 0, lvl: 30, br: 65, fa: 58, gx: 0, gy: 0, hp: 222, maxHp: 200);
        mem.MarkReadable(Band.Entry(0) + Offsets.AMaxHp, 2);   // production reads n=2 (GrowthEngine.Signatures.cs ReadHpScan)
        MemSeats.SeatFrameNameId(mem, 0, 298);   // the wielder's own nameId
        MemSeats.SeatBand(mem, bandIdx: 1, weapon: 0, lvl: 30, br: 65, fa: 58, gx: 0, gy: 0, hp: 111, maxHp: 200);
        mem.MarkReadable(Band.Entry(1) + Offsets.AMaxHp, 2);
        MemSeats.SeatFrameNameId(mem, 1, 0);   // anonymous zero-nameId same-fp twin

        var (hp, maxHp) = GrowthEngine.ReadHp(mem, 30, 65, 58, rosterNameId: 298);
        Assert.Equal(222, hp);
        Assert.Equal(200, maxHp);
    }

    [Fact]
    public void ReadHp_tier2_veto_excludes_foreign_entry_when_only_it_exists()
    {
        var mem = new FakeSparseMemory();
        MemSeats.SeatBand(mem, bandIdx: 0, weapon: 0, lvl: 30, br: 65, fa: 58, gx: 0, gy: 0, hp: 111, maxHp: 200);
        mem.MarkReadable(Band.Entry(0) + Offsets.AMaxHp, 2);   // production reads n=2 (GrowthEngine.Signatures.cs ReadHpScan)
        MemSeats.SeatFrameNameId(mem, 0, 918);   // only a foreign collider present -- no wielder entry at all

        var (hp, maxHp) = GrowthEngine.ReadHp(mem, 30, 65, 58, rosterNameId: 298);
        Assert.Equal(0, hp);
        Assert.Equal(0, maxHp);
    }

    [Fact]
    public void ReadHp_returns_miss_for_two_real_position_candidates_with_no_nameId()
    {
        // [LW-252 stage 6, X4] Two real-position band entries share the SAME fp with no roster
        // nameId at all (rosterNameId 0) -- master picks whichever comes LAST in band-slot scan
        // order and hands back ITS hp/maxHp (a wrong-unit read feeding a wrong-unit HP-gated
        // hold). Fixed: (0,0), the same "no info" miss value every consumer already treats a
        // locate-miss as (GrowthEngine.cs, Ultima.cs, Afterimage.cs).
        var mem = new FakeSparseMemory();
        MemSeats.SeatBand(mem, bandIdx: 0, weapon: 0, lvl: 30, br: 65, fa: 58, gx: 3, gy: 4, hp: 111, maxHp: 200);
        mem.MarkReadable(Band.Entry(0) + Offsets.AMaxHp, 2);
        MemSeats.SeatBand(mem, bandIdx: 1, weapon: 0, lvl: 30, br: 65, fa: 58, gx: 5, gy: 6, hp: 222, maxHp: 200);
        mem.MarkReadable(Band.Entry(1) + Offsets.AMaxHp, 2);

        var (hp, maxHp) = GrowthEngine.ReadHp(mem, 30, 65, 58);

        Assert.Equal(0, hp);
        Assert.Equal(0, maxHp);
    }
}
