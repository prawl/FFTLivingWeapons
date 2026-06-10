using System.Collections.Generic;
using System.Runtime.InteropServices;
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
    /// <summary>Allocate a pinned buffer that looks like a valid combat struct.
    /// level, brave, faith, weapon at the C* offsets; PA/MA in a sane range; gx/gy at
    /// AGx/AGy for the twin tie-break. Caller owns the GCHandle and must free it.</summary>
    private static (long addr, byte[] buf, GCHandle h) PinnedStruct(
        int weapon, int level, int brave, int faith, int pa = 10, int ma = 10,
        int gx = 0, int gy = 0)
    {
        var buf = new byte[256];
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
        var h = GCHandle.Alloc(buf, GCHandleType.Pinned);
        return (h.AddrOfPinnedObject().ToInt64(), buf, h);
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
        var (addr, _, h) = PinnedStruct(weapon: 10, level: 30, brave: 65, faith: 58);
        try
        {
            Assert.True(GrowthEngine.MatchesEntry(addr, level: 30, brave: 65, faith: 58, MakeHands(10)));
        }
        finally { h.Free(); }
    }

    [Fact]
    public void MatchesEntry_false_when_level_differs()
    {
        // Same weapon/brave/faith as the player, but a different level -- an enemy collision.
        var (addr, _, h) = PinnedStruct(weapon: 10, level: 25, brave: 65, faith: 58);
        try
        {
            Assert.False(GrowthEngine.MatchesEntry(addr, level: 30, brave: 65, faith: 58, MakeHands(10)));
        }
        finally { h.Free(); }
    }

    [Fact]
    public void MatchesEntry_false_when_brave_differs()
    {
        var (addr, _, h) = PinnedStruct(weapon: 10, level: 30, brave: 66, faith: 58);
        try
        {
            Assert.False(GrowthEngine.MatchesEntry(addr, level: 30, brave: 65, faith: 58, MakeHands(10)));
        }
        finally { h.Free(); }
    }

    [Fact]
    public void MatchesEntry_false_when_weapon_not_in_hands()
    {
        var (addr, _, h) = PinnedStruct(weapon: 99, level: 30, brave: 65, faith: 58);
        try
        {
            Assert.False(GrowthEngine.MatchesEntry(addr, level: 30, brave: 65, faith: 58, MakeHands(10)));
        }
        finally { h.Free(); }
    }

    [Fact]
    public void MatchesEntry_false_when_pa_out_of_sane_range()
    {
        var (addr, _, h) = PinnedStruct(weapon: 10, level: 30, brave: 65, faith: 58, pa: 0);
        try
        {
            Assert.False(GrowthEngine.MatchesEntry(addr, level: 30, brave: 65, faith: 58, MakeHands(10)));
        }
        finally { h.Free(); }
    }

    // ---- ScanStructEntries: ambiguity + player-preference ----
    // This verifies the scan-order contract (player before enemy) and the ambiguity refusal.

    [Fact]
    public void ScanStructEntries_returns_player_slot_when_only_player_matches()
    {
        var (pAddr, _, ph) = PinnedStruct(weapon: 10, level: 30, brave: 65, faith: 58);
        var (eAddr, _, eh) = PinnedStruct(weapon: 10, level: 99, brave: 65, faith: 58);  // enemy: different level
        try
        {
            // player slot (isPlayer=true), enemy slot (isPlayer=false)
            var entries = new[] { (isPlayer: false, addr: eAddr), (isPlayer: true, addr: pAddr) };
            long result = GrowthEngine.ScanStructEntries(30, 65, 58, MakeHands(10), entries);
            Assert.Equal(pAddr, result);
        }
        finally { ph.Free(); eh.Free(); }
    }

    [Fact]
    public void ScanStructEntries_returns_player_when_player_and_enemy_share_full_fingerprint()
    {
        // Player-side preference: when both a player and an enemy slot match, take the player
        // (the player-side pass fires first and exits the outer loop before enemy-side is checked).
        var (pAddr, _, ph) = PinnedStruct(weapon: 10, level: 30, brave: 65, faith: 58);
        var (eAddr, _, eh) = PinnedStruct(weapon: 10, level: 30, brave: 65, faith: 58);
        try
        {
            var entries = new[] { (isPlayer: true, addr: pAddr), (isPlayer: false, addr: eAddr) };
            long result = GrowthEngine.ScanStructEntries(30, 65, 58, MakeHands(10), entries);
            Assert.Equal(pAddr, result);
        }
        finally { ph.Free(); eh.Free(); }
    }

    [Fact]
    public void ScanStructEntries_refuses_when_two_player_slots_have_different_weapons()
    {
        // Two player-side entries with different weapon ids -> heterogeneous, refuse.
        var (p1, _, h1) = PinnedStruct(weapon: 10, level: 30, brave: 65, faith: 58, gx: 3, gy: 4);
        var (p2, _, h2) = PinnedStruct(weapon: 11, level: 30, brave: 65, faith: 58, gx: 5, gy: 6);
        try
        {
            var entries = new[] { (isPlayer: true, addr: p1), (isPlayer: true, addr: p2) };
            long result = GrowthEngine.ScanStructEntries(30, 65, 58, MakeHands(10, 11), entries);
            Assert.Equal(0, result);
        }
        finally { h1.Free(); h2.Free(); }
    }

    [Fact]
    public void ScanStructEntries_returns_zero_when_nothing_matches()
    {
        var (addr, _, h) = PinnedStruct(weapon: 99, level: 30, brave: 65, faith: 58);
        try
        {
            var entries = new[] { (isPlayer: true, addr) };
            long result = GrowthEngine.ScanStructEntries(30, 65, 58, MakeHands(10), entries);
            Assert.Equal(0, result);
        }
        finally { h.Free(); }
    }

    // ---- ScanStructEntries: homogeneous twin tie-break ----

    [Fact]
    public void ScanStructEntries_prefers_real_position_over_origin_twin()
    {
        // Frozen twins: same identity, one at (0,0), one at a real grid position.
        // The real-position entry should win -- this is the regression for the static-array
        // battle-restart freeze phenomenon (live copy at real pos, frozen copy at (0,0)).
        var (frozen, _, hF) = PinnedStruct(weapon: 10, level: 30, brave: 65, faith: 58, gx: 0, gy: 0);
        var (live,   _, hL) = PinnedStruct(weapon: 10, level: 30, brave: 65, faith: 58, gx: 4, gy: 7);
        try
        {
            var entries = new[] { (isPlayer: true, addr: frozen), (isPlayer: true, addr: live) };
            long result = GrowthEngine.ScanStructEntries(30, 65, 58, MakeHands(10), entries);
            Assert.Equal(live, result);
        }
        finally { hF.Free(); hL.Free(); }
    }

    [Fact]
    public void ScanStructEntries_returns_one_when_all_twins_share_identity_at_origin()
    {
        // Both twins at (0,0) with identical fingerprint: unit on corner tile -- return one
        // deterministically rather than refusing (homogeneous = ONE unit, not two different ones).
        var (t1, _, h1) = PinnedStruct(weapon: 10, level: 30, brave: 65, faith: 58, gx: 0, gy: 0);
        var (t2, _, h2) = PinnedStruct(weapon: 10, level: 30, brave: 65, faith: 58, gx: 0, gy: 0);
        try
        {
            var entries = new[] { (isPlayer: true, addr: t1), (isPlayer: true, addr: t2) };
            long result = GrowthEngine.ScanStructEntries(30, 65, 58, MakeHands(10), entries);
            Assert.NotEqual(0, result);
            Assert.True(result == t1 || result == t2);
        }
        finally { h1.Free(); h2.Free(); }
    }

    // ---- LocateStruct: ambiguous-log throttle ----

    [Fact]
    public void LocateStruct_logs_ambiguous_once_per_battle_per_slot()
    {
        // Heterogeneous multi-match (two different weapons) -> refused + log throttled to once.
        var (p1, _, h1) = PinnedStruct(weapon: 10, level: 30, brave: 65, faith: 58, gx: 3, gy: 4);
        var (p2, _, h2) = PinnedStruct(weapon: 11, level: 30, brave: 65, faith: 58, gx: 5, gy: 6);
        try
        {
            var engine = new GrowthEngine(
                new System.Collections.Generic.Dictionary<int, WeaponMeta>(),
                new System.Collections.Generic.Dictionary<int, int>(),
                new TurnTracker(new FakeMemForThrottle()));
            var entries = new[] { (isPlayer: true, addr: p1), (isPlayer: true, addr: p2) };
            var hands = MakeHands(10, 11);
            // First call: heterogeneous -> refuses, marks the slot as logged.
            engine.ScanWithThrottle(slot: 0, level: 30, brave: 65, faith: 58, hands, entries);
            Assert.True(engine.IsAmbiguousLogged(0), "first call should mark slot 0 as logged");
            // Second call same slot: still refused, throttle suppresses a second log entry.
            engine.ScanWithThrottle(slot: 0, level: 30, brave: 65, faith: 58, hands, entries);
            Assert.True(engine.IsAmbiguousLogged(0), "second call: slot still logged, no duplicate");
            // ResetBattle clears the throttle for the new battle.
            engine.ResetBattle();
            Assert.False(engine.IsAmbiguousLogged(0), "after ResetBattle throttle should be cleared");
        }
        finally { h1.Free(); h2.Free(); }
    }

    // ---- IsSignatureMainHand: signatures fire from the main hand only (A7) ----
    // A Living Weapon earns kills in any hand, but commands its gift only from the main hand.

    private static (long addr, byte[] buf, GCHandle h) PinnedRoster(int rh, int oh = 0xFFFF)
    {
        // Fake a minimal roster slot: RRHand at +0x14, ROffHand at +0x18 (u16 LE).
        var buf = new byte[0x20];
        buf[Offsets.RRHand]     = (byte)(rh & 0xFF);
        buf[Offsets.RRHand + 1] = (byte)((rh >> 8) & 0xFF);
        buf[Offsets.ROffHand]     = (byte)(oh & 0xFF);
        buf[Offsets.ROffHand + 1] = (byte)((oh >> 8) & 0xFF);
        var h = GCHandle.Alloc(buf, GCHandleType.Pinned);
        return (h.AddrOfPinnedObject().ToInt64(), buf, h);
    }

    [Fact]
    public void IsSignatureMainHand_true_when_weapon_is_RRHand()
    {
        var (addr, _, h) = PinnedRoster(rh: 10);
        try { Assert.True(GrowthEngine.IsSignatureMainHand(addr, weaponId: 10)); }
        finally { h.Free(); }
    }

    [Fact]
    public void IsSignatureMainHand_false_when_weapon_is_only_offhand()
    {
        // Signature stays silent for an offhand weapon; growth (PA/MA/Speed factor) still applies.
        var (addr, _, h) = PinnedRoster(rh: 1, oh: 10);
        try { Assert.False(GrowthEngine.IsSignatureMainHand(addr, weaponId: 10)); }
        finally { h.Free(); }
    }

    [Fact]
    public void IsSignatureMainHand_false_when_weapon_absent()
    {
        var (addr, _, h) = PinnedRoster(rh: 2, oh: 3);
        try { Assert.False(GrowthEngine.IsSignatureMainHand(addr, weaponId: 10)); }
        finally { h.Free(); }
    }

    /// <summary>Minimal IGameMemory stub that returns 0 for everything -- enough to construct
    /// a TurnTracker for the throttle test (which never drives a real Apply tick).</summary>
    private sealed class FakeMemForThrottle : IGameMemory
    {
        public byte U8(long _) => 0;
        public ushort U16(long _) => 0;
    }
}
