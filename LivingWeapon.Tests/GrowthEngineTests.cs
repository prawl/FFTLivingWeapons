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
        mem.ReadableAddrs.Add(s);
        mem.U16s[s + Offsets.CWeapon] = 10;
        mem.U8s[s + Offsets.CLevel] = 30;
        mem.U8s[s + Offsets.CBrave] = 65;
        mem.U8s[s + Offsets.CFaith] = 58;
        mem.U8s[s + Offsets.CPa] = 10;
        mem.U8s[s + Offsets.CMa] = 10;
        Assert.False(GrowthEngine.MatchesEntry(mem, s, level: 30, brave: 65, faith: 58, MakeHands(10), nameId: 298));
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
        mem.ReadableAddrs.Add(s);
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
        mem.ReadableAddrs.Add(Band.Entry(0) + Offsets.AMaxHp);
        MemSeats.SeatFrameNameId(mem, 0, 298);   // the wielder's own nameId
        MemSeats.SeatBand(mem, bandIdx: 1, weapon: 0, lvl: 30, br: 65, fa: 58, gx: 0, gy: 0, hp: 111, maxHp: 200);
        mem.ReadableAddrs.Add(Band.Entry(1) + Offsets.AMaxHp);
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
        mem.ReadableAddrs.Add(Band.Entry(0) + Offsets.AMaxHp);
        MemSeats.SeatFrameNameId(mem, 0, 918);   // only a foreign collider present -- no wielder entry at all

        var (hp, maxHp) = GrowthEngine.ReadHp(mem, 30, 65, 58, rosterNameId: 298);
        Assert.Equal(0, hp);
        Assert.Equal(0, maxHp);
    }
}
