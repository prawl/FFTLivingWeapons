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
}
