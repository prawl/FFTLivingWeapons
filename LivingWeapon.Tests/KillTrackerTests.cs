using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// Kill-attribution logic, driven by a fake memory (no live game). Locks in the
/// FFTHandsFree active-unit resolver:
///   - the acting player is identified by the active struct's HP+MaxHP+level matched
///     against the battle array, then resolved to the roster by a level/brave/faith
///     FINGERPRINT. NOT by the condensed +0x04 "nameId" -- that is a sequential battle
///     index that collides (a Time Mage's index 1 == Ramza's roster nameId 1), which
///     mis-credited every unit's kills to Ramza.
///   - hold that weapon and credit enemy corpses to it; credit each corpse once; never
///     credit a player corpse; bail (no latch) on an ambiguous match.
/// </summary>
public class KillTrackerTests
{
    /// <summary>Address -> value, for the reads KillTracker does.</summary>
    private sealed class FakeMemory : IGameMemory
    {
        public readonly Dictionary<long, ushort> U16s = new();
        public readonly Dictionary<long, byte> U8s = new();
        public byte U8(long a) => U8s.TryGetValue(a, out var v) ? v : (byte)0;
        public ushort U16(long a) => U16s.TryGetValue(a, out var v) ? v : (ushort)0;
    }

    /// <summary>The active (condensed) struct: which unit's turn it is, by HP/MaxHP/level.
    /// `acted` is the action-complete flag (0x14077CA8C): the latch only captures when it is 1,
    /// so the inter-turn flicker of the struct (acted=0) can't steal credit.</summary>
    private static void SetActive(FakeMemory m, int hp, int maxHp, int level, int team = 0, int acted = 1)
    {
        m.U16s[Offsets.TurnQueue + Offsets.TqTeam] = (ushort)team;
        m.U16s[Offsets.TurnQueue + Offsets.TqHp] = (ushort)hp;
        m.U16s[Offsets.TurnQueue + Offsets.TqMaxHp] = (ushort)maxHp;
        m.U16s[Offsets.TurnQueue + Offsets.TqLevel] = (ushort)level;
        m.U8s[Offsets.Acted] = (byte)acted;
    }

    /// <summary>A battle-array slot. Pass level/brave/faith to make it matchable as the actor.</summary>
    private static void SetUnit(FakeMemory m, int slot, int hp, int maxHp = 400, int gx = 5, int gy = 5,
                                int level = 0, int brave = 0, int faith = 0)
    {
        long s = Offsets.ArrayReadBase + (long)slot * Offsets.ArrayStride;
        m.U16s[s + Offsets.AInBattle] = 1;
        m.U16s[s + Offsets.AMaxHp] = (ushort)maxHp;
        m.U16s[s + Offsets.AHp] = (ushort)hp;
        m.U8s[s + Offsets.AGx] = (byte)gx;
        m.U8s[s + Offsets.AGy] = (byte)gy;
        m.U8s[s + Offsets.ALevel] = (byte)level;
        m.U8s[s + Offsets.ABrave] = (byte)brave;
        m.U8s[s + Offsets.AFaith] = (byte)faith;
    }

    /// <summary>A roster slot keyed by the (level,brave,faith) fingerprint -> its R-hand weapon.</summary>
    private static void SetRoster(FakeMemory m, int slot, int level, int brave, int faith, int weapon)
    {
        long b = Offsets.RosterBase + (long)slot * Offsets.RosterStride;
        m.U8s[b + Offsets.RLevel] = (byte)level;
        m.U8s[b + Offsets.RBrave] = (byte)brave;
        m.U8s[b + Offsets.RFaith] = (byte)faith;
        m.U16s[b + Offsets.RRHand] = (ushort)weapon;
    }

    private const int Wilham = Offsets.SlotsBack;       // first player-side array slot (slot 20)
    private const int Ramza = Offsets.SlotsBack + 1;

    [Fact]
    public void Credits_the_acting_players_weapon()
    {
        var kills = new Dictionary<int, int>();
        var m = new FakeMemory();
        SetRoster(m, slot: 3, level: 99, brave: 89, faith: 76, weapon: 52);
        SetUnit(m, Wilham, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetActive(m, hp: 352, maxHp: 352, level: 99);
        var t = new KillTracker(kills, m);

        t.Poll();                       // capture acting weapon (no corpse yet)
        SetUnit(m, slot: 0, hp: 0);     // an enemy slot drops to 0 HP
        bool changed = t.Poll();        // detect corpse + credit

        Assert.True(changed);
        Assert.Equal(1, kills.GetValueOrDefault(52));
    }

    [Fact]
    public void Credits_the_unit_that_acted_not_a_collision_twin()
    {
        // The bug: Wilham (Time Mage, HP 352, Spark Rod 52) and Ramza (Knight, HP 679,
        // Stoneshooter 73) collided on the condensed +0x04 index, so Wilham's kills were
        // credited to Ramza's gun. HP+fingerprint resolution must tell them apart.
        var kills = new Dictionary<int, int>();
        var m = new FakeMemory();
        SetRoster(m, slot: 0, level: 99, brave: 70, faith: 50, weapon: 73);   // Ramza
        SetRoster(m, slot: 3, level: 99, brave: 89, faith: 76, weapon: 52);   // Wilham
        SetUnit(m, Wilham, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetUnit(m, Ramza, hp: 679, maxHp: 679, level: 99, brave: 70, faith: 50);
        SetActive(m, hp: 352, maxHp: 352, level: 99);   // Wilham is acting
        var t = new KillTracker(kills, m);
        t.Poll();                                       // latch Wilham's Spark Rod (52)

        SetUnit(m, slot: 0, hp: 0);                      // an enemy dies on Wilham's turn
        t.Poll();

        Assert.Equal(1, kills.GetValueOrDefault(52));    // Spark Rod
        Assert.False(kills.ContainsKey(73));             // NOT Ramza's Stoneshooter
    }

    [Fact]
    public void Does_not_double_credit_the_same_corpse()
    {
        var kills = new Dictionary<int, int>();
        var m = new FakeMemory();
        SetRoster(m, slot: 3, level: 99, brave: 89, faith: 76, weapon: 52);
        SetUnit(m, Wilham, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetActive(m, hp: 352, maxHp: 352, level: 99);
        var t = new KillTracker(kills, m);

        SetUnit(m, slot: 0, hp: 0);
        t.Poll();
        t.Poll();   // still a corpse -- must not count twice
        t.Poll();

        Assert.Equal(1, kills.GetValueOrDefault(52));
    }

    [Fact]
    public void Ignores_player_corpses()
    {
        var kills = new Dictionary<int, int>();
        var m = new FakeMemory();
        SetRoster(m, slot: 3, level: 99, brave: 89, faith: 76, weapon: 52);
        SetUnit(m, Wilham, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetActive(m, hp: 352, maxHp: 352, level: 99);
        var t = new KillTracker(kills, m);

        t.Poll();
        SetUnit(m, slot: Offsets.EnemySlotMax + 5, hp: 0);   // a PLAYER-side slot dies
        t.Poll();

        Assert.False(kills.ContainsKey(52));   // an ally going down is not a weapon's kill
    }

    [Fact]
    public void Holds_the_actors_weapon_when_the_struct_flickers_to_a_non_actor()
    {
        // Live bug: between turns the active struct flickers to another player (cursor/preview)
        // with acted==0. Without the acted gate the latch followed the flicker, so an archer's
        // kill credited whatever weapon was showing at corpse-detection (e.g. a mage's staff).
        var kills = new Dictionary<int, int>();
        var m = new FakeMemory();
        SetRoster(m, slot: 3, level: 99, brave: 89, faith: 76, weapon: 90);   // archer (Yoichi Bow)
        SetRoster(m, slot: 0, level: 99, brave: 70, faith: 50, weapon: 63);   // mage (Blazing Staff)
        SetUnit(m, Wilham, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);  // archer struct
        SetUnit(m, Ramza, hp: 400, maxHp: 400, level: 99, brave: 70, faith: 50);   // mage struct
        SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 1);   // archer ACTS -> latch 90
        var t = new KillTracker(kills, m);
        t.Poll();

        // struct flickers to the mage, but the mage has NOT acted (acted==0)
        SetActive(m, hp: 400, maxHp: 400, level: 99, acted: 0);
        SetUnit(m, slot: 0, hp: 0);   // the archer's target dies now
        t.Poll();

        Assert.Equal(1, kills.GetValueOrDefault(90));   // archer's Yoichi Bow
        Assert.False(kills.ContainsKey(63));            // NOT the flickered mage staff
    }

    [Fact]
    public void Latches_regardless_of_the_unreliable_team_field()
    {
        // Live, the condensed struct's team field reads 0/1/3 even for the SAME active player
        // (often 3 in a battle entered straight from a save load). The roster fingerprint -- not
        // team -- decides whether the actor is a player, so the capture must NOT gate on team.
        var kills = new Dictionary<int, int>();
        var m = new FakeMemory();
        SetRoster(m, slot: 3, level: 99, brave: 89, faith: 76, weapon: 52);
        SetUnit(m, Wilham, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetActive(m, hp: 352, maxHp: 352, level: 99, team: 3, acted: 1);   // team reads 3, not 0
        var t = new KillTracker(kills, m);

        t.Poll();                       // must still latch -- the actor fingerprints to a roster player
        SetUnit(m, slot: 0, hp: 0);
        t.Poll();

        Assert.Equal(1, kills.GetValueOrDefault(52));
    }

    [Fact]
    public void Does_not_latch_when_an_enemy_is_the_active_unit()
    {
        // An enemy's turn: the active struct shows the ENEMY, whose HP/level/fingerprint match no
        // roster player -> resolve returns -1 -> nothing latches (that, not team, is the guard).
        var kills = new Dictionary<int, int>();
        var m = new FakeMemory();
        SetRoster(m, slot: 3, level: 99, brave: 89, faith: 76, weapon: 52);
        SetUnit(m, Wilham, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);   // a player on the field
        SetActive(m, hp: 681, maxHp: 681, level: 93, acted: 1);   // an ENEMY active (no roster match)
        var t = new KillTracker(kills, m);

        t.Poll();
        SetUnit(m, slot: 0, hp: 0);   // an enemy dies on the enemy's turn
        t.Poll();

        Assert.Empty(kills);
    }

    [Fact]
    public void Bails_when_two_units_share_hp_but_resolve_to_different_weapons()
    {
        // Two players at 352/352 lvl99 with different fingerprints -> different weapons.
        // An ambiguous actor must NOT latch -- a missed kill beats a mis-credited one.
        var kills = new Dictionary<int, int>();
        var m = new FakeMemory();
        SetRoster(m, slot: 0, level: 99, brave: 70, faith: 50, weapon: 73);
        SetRoster(m, slot: 3, level: 99, brave: 89, faith: 76, weapon: 52);
        SetUnit(m, Wilham, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetUnit(m, Ramza, hp: 352, maxHp: 352, level: 99, brave: 70, faith: 50);  // same HP!
        SetActive(m, hp: 352, maxHp: 352, level: 99);
        var t = new KillTracker(kills, m);

        t.Poll();
        SetUnit(m, slot: 0, hp: 0);
        t.Poll();

        Assert.Empty(kills);
    }

    [Fact]
    public void Credits_a_corpse_seen_before_the_actor_latched()
    {
        // The corpse appears BEFORE acted flips to 1 (acted = action-complete, set after the
        // death registers). On the 100ms loop the corpse is often noticed in that gap; it must
        // WAIT for the actor, not be permanently dropped.
        var kills = new Dictionary<int, int>();
        var m = new FakeMemory();
        SetRoster(m, slot: 3, level: 99, brave: 89, faith: 76, weapon: 52);
        SetUnit(m, Wilham, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 0);   // actor has NOT acted yet
        SetUnit(m, slot: 0, hp: 0);                                // enemy already dead
        var t = new KillTracker(kills, m);

        bool first = t.Poll();        // corpse seen, no latch -> held pending, not credited/dropped
        Assert.False(first);
        Assert.Empty(kills);

        SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 1);   // action completes -> actor latches
        bool second = t.Poll();       // the pending corpse is credited now
        Assert.True(second);
        Assert.Equal(1, kills.GetValueOrDefault(52));
    }

    [Fact]
    public void Does_not_credit_a_pending_corpse_after_it_expires()
    {
        // A corpse with no actor for too long is given up, so a much-later actor (next turn)
        // can't inherit a stale kill.
        var kills = new Dictionary<int, int>();
        var m = new FakeMemory();
        SetRoster(m, slot: 3, level: 99, brave: 89, faith: 76, weapon: 52);
        SetUnit(m, Wilham, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 0);   // no actor
        SetUnit(m, slot: 0, hp: 0);
        var t = new KillTracker(kills, m);
        for (int i = 0; i < 40; i++) t.Poll();   // pending corpse exceeds its TTL -> given up

        SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 1);   // a late actor finally latches
        t.Poll();

        Assert.Empty(kills);   // the expired corpse is NOT credited to the late actor
    }
}
