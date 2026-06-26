using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// Kill-attribution logic, driven by a fake memory (no live game). Locks in the
/// FFTHandsFree active-unit resolver:
///   - the acting player is identified by the active struct's HP+MaxHP+level matched
///     against the BAND (live source), then resolved to the roster by a level/brave/faith
///     FINGERPRINT. NOT by the condensed +0x04 "nameId" -- that is a sequential battle
///     index that collides (a Time Mage's index 1 == Ramza's roster nameId 1), which
///     mis-credited every unit's kills to Ramza.
///   - band corpse scan with seen-alive + 3-tick-dead guards (phantom-load race fix).
///   - static array = identity/team oracle (captured while live; survives the restart freeze).
///   - hold that weapon and credit enemy corpses to it; credit each corpse once; never
///     credit a player corpse; bail (no latch) on an ambiguous match.
/// </summary>
public class KillTrackerTests
{
    /// <summary>The active (condensed) struct: which unit's turn it is, by HP/MaxHP/level.
    /// `acted` is the action-complete flag (0x14077CA8C): the latch only captures when it is 1,
    /// so the inter-turn flicker of the struct (acted=0) can't steal credit.</summary>
    private static void SetActive(FakeSparseMemory m, int hp, int maxHp, int level, int team = 0, int acted = 1)
    {
        m.U16s[Offsets.TurnQueue + Offsets.TqTeam] = (ushort)team;
        m.U16s[Offsets.TurnQueue + Offsets.TqHp] = (ushort)hp;
        m.U16s[Offsets.TurnQueue + Offsets.TqMaxHp] = (ushort)maxHp;
        m.U16s[Offsets.TurnQueue + Offsets.TqLevel] = (ushort)level;
        m.U8s[Offsets.Acted] = (byte)acted;
    }

    /// <summary>Write a unit into the BAND entry at band slot <paramref name="slot"/>.
    /// This is the live source for corpse detection and actor resolution.
    /// Pass level/brave/faith to make it matchable as the actor (ActorResolver reads the band).</summary>
    private static void SetUnit(FakeSparseMemory m, int slot, int hp, int maxHp = 400, int gx = 5, int gy = 5,
                                int level = 10, int brave = 50, int faith = 50)
        => MemSeats.SeatBand(m, slot, weapon: 0, lvl: level, br: brave, fa: faith,
                             gx: gx, gy: gy, hp: hp, maxHp: maxHp);

    /// <summary>Write identity fields into the STATIC ARRAY slot so the capture oracle can
    /// classify this as a known enemy. inb defaults to 1 but is NOT required by the capture --
    /// live, the flag pulses 0/1 per unit mid-battle (it is not a membership marker).</summary>
    private static void SetArrayEnemy(FakeSparseMemory m, int slot, int level, int brave, int faith, int maxHp,
                                      int inb = 1)
    {
        long s = Offsets.ArrayReadBase + (long)slot * Offsets.ArrayStride;
        m.U16s[s + Offsets.AInBattle] = (ushort)inb;
        m.U8s[s + Offsets.ALevel] = (byte)level;
        m.U8s[s + Offsets.ABrave] = (byte)brave;
        m.U8s[s + Offsets.AFaith] = (byte)faith;
        m.U16s[s + Offsets.AMaxHp] = (ushort)maxHp;
    }

    /// <summary>Convenience: write BOTH the band entry (liveness) and the static array slot
    /// (identity capture). Enemies in tests must have their identity captured to earn credit.</summary>
    private static void SetEnemy(FakeSparseMemory m, int slot, int hp, int maxHp = 400, int gx = 5, int gy = 5,
                                 int level = 10, int brave = 50, int faith = 50)
    {
        SetUnit(m, slot, hp, maxHp, gx, gy, level, brave, faith);
        if (slot <= Offsets.EnemySlotMax)
            SetArrayEnemy(m, slot, level, brave, faith, maxHp);
    }

    /// <summary>A roster slot keyed by the (level,brave,faith) fingerprint -> its R-hand weapon.
    /// ROffHand (+0x18) is where the live dual-wield off-hand actually sits.</summary>
    private static void SetRoster(FakeSparseMemory m, int slot, int level, int brave, int faith, int weapon,
                                  int lhand = 0xFFFF, int offhand = 0xFFFF)
        => MemSeats.SeatRoster(m, slot, level, brave, faith, weapon, lhand, offhand);

    // Band slot indices for player-side units (arbitrary; just need to be non-enemy for clarity).
    // Enemy band slots can be anywhere in 0..BandSlots-1; slot 0 is a convenient enemy slot.
    private const int Wilham = Offsets.SlotsBack;       // band slot 20 (player-side actor)
    private const int Ramza = Offsets.SlotsBack + 1;    // band slot 21

    // Ids that count as real weapons (meta keys). A hand holding anything NOT in here
    // (e.g. Shield) is never credited. Covers every weapon id used across these tests.
    private static readonly HashSet<int> Weapons = new() { 52, 63, 73, 90 };
    private const int Shield = 200;   // a non-weapon left-hand item -> never credited

    /// <summary>Poll n times with onField=true (builds alive/dead streaks).</summary>
    private static void Settle(KillTracker t, int n = 3) { for (int i = 0; i < n; i++) t.Poll(true); }

    /// <summary>Set a band slot alive (hp>0), settle 3 ticks (seenAlive), then set it dead and
    /// settle 3 ticks (deadStreak). Leaves it ready for credit. Returns tracker for fluent use.</summary>
    private static void AliveThenDead(FakeSparseMemory m, int slot, KillTracker t,
                                      int hp = 300, int maxHp = 400, int level = 10, int brave = 50, int faith = 50)
    {
        SetEnemy(m, slot, hp, maxHp, level: level, brave: brave, faith: faith);
        Settle(t);
        SetUnit(m, slot, hp: 0, maxHp: maxHp, level: level, brave: brave, faith: faith);
        Settle(t);
    }

    [Fact]
    public void Credits_the_acting_players_weapon()
    {
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        SetRoster(m, slot: 3, level: 99, brave: 89, faith: 76, weapon: 52);
        SetUnit(m, Wilham, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetActive(m, hp: 352, maxHp: 352, level: 99);
        var t = new KillTracker(kills, m, Weapons);

        Settle(t);                      // latch acting weapon; enemy slot not yet seen alive
        // Bring enemy alive for 3 ticks, then kill.
        SetEnemy(m, slot: 0, hp: 300);
        Settle(t);                      // seenAlive
        SetUnit(m, slot: 0, hp: 0);
        bool changed = t.Poll(true);    // first dead tick (need 3 total)
        Assert.False(changed);          // not yet
        t.Poll(true); changed = t.Poll(true);   // 3rd dead tick -> credit

        Assert.True(changed);
        Assert.Equal(1, kills.GetValueOrDefault(52));
    }

    [Fact]
    public void Resolved_player_with_untracked_weapon_clears_the_latch()
    {
        // Live bug (2026-06-10): Ramza, wielding only the untracked DLC Akademy Blade, killed a
        // goblin with Throw Stone -- and the kill was credited to the PREVIOUS actor's Scoutbolt
        // through the stale latch. A RESOLVED player whose hands hold no tracked weapon must
        // REPLACE the latch with empty (their kills go honestly uncredited, and the stale main
        // hand stops arming signature modules). The sticky latch survives only for UNRESOLVED
        // acted-periods (enemy actions / the Acted-byte flake).
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        SetRoster(m, slot: 3, level: 99, brave: 89, faith: 76, weapon: 52);    // Wilham, tracked rod
        SetRoster(m, slot: 0, level: 50, brave: 70, faith: 50, weapon: 999);   // "Ramza": untracked DLC blade
        SetUnit(m, Wilham, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetUnit(m, Ramza, hp: 679, maxHp: 679, level: 50, brave: 70, faith: 50);
        SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 1);                // Wilham acts
        var t = new KillTracker(kills, m, Weapons);
        Settle(t);                                                             // latch 52
        Assert.Equal(52, t.LastPlayerMainHand);

        SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 0);                // his turn ends
        Settle(t, KillTracker.UnfreezeTicks);                                  // latch unfreezes
        SetActive(m, hp: 679, maxHp: 679, level: 50, acted: 1);                // "Ramza" acts, untracked
        Settle(t);

        Assert.Equal(0, t.LastPlayerMainHand);                                 // stale main hand cleared

        AliveThenDead(m, slot: 0, t);                                          // his stone kills the gobbo

        Assert.False(kills.ContainsKey(52));   // NOT the previous actor's weapon
        Assert.Empty(kills);                   // nobody credited -- the blade is a museum piece
    }

    [Fact]
    public void Actor_who_leveled_up_mid_battle_still_resolves()
    {
        // Live bug (2026-06-10, the adversarial review's "uncertain" watch item, confirmed):
        // the roster holds a unit's PRE-BATTLE level until battle end, but the live structs
        // update on a mid-battle level-up -- so every action by a freshly-leveled unit failed
        // roster identification, the stale latch survived, and a Phoenix Down kill by Ramza
        // paid out to the Wellspring Rod's wielder. Live level may exceed roster level by a
        // bounded drift (you level up, never down).
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        SetRoster(m, slot: 3, level: 5, brave: 89, faith: 76, weapon: 52);   // roster: still level 5
        SetUnit(m, Wilham, hp: 352, maxHp: 352, level: 6, brave: 89, faith: 76);   // live: leveled to 6
        SetActive(m, hp: 352, maxHp: 352, level: 6, acted: 1);               // acting at level 6
        var t = new KillTracker(kills, m, Weapons);
        Settle(t);

        AliveThenDead(m, slot: 0, t);

        Assert.Equal(1, kills.GetValueOrDefault(52));   // resolved despite the level drift
    }

    [Fact]
    public void Actor_who_leveled_up_mid_battle_still_arms_main_hand_signatures()
    {
        // The OTHER half of the level-drift closure: kill credit goes through FingerprintPlayer
        // (drift-aware), but the MAIN-HAND resolve goes through MainHandFromRoster -- if that
        // walk demands exact level equality, a freshly-leveled wielder keeps earning kills while
        // every main-hand signature (Plague window, Maim, Ricochet...) silently disarms for the
        // rest of the battle. Same scenario as above; the assertion is the main-hand latch.
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        SetRoster(m, slot: 3, level: 5, brave: 89, faith: 76, weapon: 52);          // roster: still level 5
        SetUnit(m, Wilham, hp: 352, maxHp: 352, level: 6, brave: 89, faith: 76);    // live: leveled to 6
        SetActive(m, hp: 352, maxHp: 352, level: 6, acted: 1);                      // acting at level 6
        var t = new KillTracker(kills, m, Weapons);
        Settle(t);

        Assert.Equal(52, t.LastPlayerMainHand);   // signatures stay armed through the level-up
    }

    [Fact]
    public void Level_drift_only_tolerates_upward_and_bounded()
    {
        // A live level BELOW the roster level (impossible for a level-up) or absurdly far above
        // it must NOT match -- the drift window cannot reopen the enemy-collision hole the
        // level checks were added to close.
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        SetRoster(m, slot: 3, level: 10, brave: 89, faith: 76, weapon: 52);
        SetUnit(m, Wilham, hp: 352, maxHp: 352, level: 9, brave: 89, faith: 76);   // BELOW roster
        SetActive(m, hp: 352, maxHp: 352, level: 9, acted: 1);
        var t = new KillTracker(kills, m, Weapons);
        Settle(t);
        AliveThenDead(m, slot: 0, t);
        Assert.Empty(kills);                            // below-roster level never matches
    }

    [Fact]
    public void Enemy_acted_period_keeps_the_latch_sticky()
    {
        // The mirror case must NOT regress: an acting unit matching NO roster fingerprint (an
        // enemy) leaves the previous player's latch in place -- that stickiness is the deliberate
        // mitigation for the Acted-byte flake and for enemy turns between a player's action and
        // the corpse landing.
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        SetRoster(m, slot: 3, level: 99, brave: 89, faith: 76, weapon: 52);
        SetUnit(m, Wilham, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 1);                // player acts
        var t = new KillTracker(kills, m, Weapons);
        Settle(t);                                                             // latch 52

        SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 0);
        Settle(t, KillTracker.UnfreezeTicks);
        SetActive(m, hp: 500, maxHp: 500, level: 30, acted: 1);                // an ENEMY acts (no roster match)
        Settle(t);

        AliveThenDead(m, slot: 0, t);                                          // a corpse lands

        Assert.Equal(1, kills.GetValueOrDefault(52));                          // sticky latch credits the player
    }

    [Fact]
    public void Credits_the_unit_that_acted_not_a_collision_twin()
    {
        // The bug: Wilham (Time Mage, HP 352, Spark Rod 52) and Ramza (Knight, HP 679,
        // Stoneshooter 73) collided on the condensed +0x04 index, so Wilham's kills were
        // credited to Ramza's gun. HP+fingerprint resolution must tell them apart.
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        SetRoster(m, slot: 0, level: 99, brave: 70, faith: 50, weapon: 73);   // Ramza
        SetRoster(m, slot: 3, level: 99, brave: 89, faith: 76, weapon: 52);   // Wilham
        SetUnit(m, Wilham, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetUnit(m, Ramza, hp: 679, maxHp: 679, level: 99, brave: 70, faith: 50);
        SetActive(m, hp: 352, maxHp: 352, level: 99);   // Wilham is acting
        var t = new KillTracker(kills, m, Weapons);
        Settle(t);                      // latch Wilham's Spark Rod (52)

        AliveThenDead(m, slot: 0, t);   // enemy dies

        Assert.Equal(1, kills.GetValueOrDefault(52));    // Spark Rod
        Assert.False(kills.ContainsKey(73));             // NOT Ramza's Stoneshooter
    }

    [Fact]
    public void Does_not_double_credit_the_same_corpse()
    {
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        SetRoster(m, slot: 3, level: 99, brave: 89, faith: 76, weapon: 52);
        SetUnit(m, Wilham, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetActive(m, hp: 352, maxHp: 352, level: 99);
        var t = new KillTracker(kills, m, Weapons);

        Settle(t);
        AliveThenDead(m, slot: 0, t);

        t.Poll(true); t.Poll(true); t.Poll(true);   // still a corpse -- must not count again

        Assert.Equal(1, kills.GetValueOrDefault(52));
    }

    [Fact]
    public void Ignores_player_corpses()
    {
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        SetRoster(m, slot: 3, level: 99, brave: 89, faith: 76, weapon: 52);
        SetUnit(m, Wilham, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetActive(m, hp: 352, maxHp: 352, level: 99);
        var t = new KillTracker(kills, m, Weapons);

        Settle(t);
        // A player-side slot: band slot at EnemySlotMax+5, hp>0 then 0; identity NOT in enemyIds.
        int pSlot = Offsets.EnemySlotMax + 5;
        SetUnit(m, pSlot, hp: 300, maxHp: 400, level: 15, brave: 55, faith: 55);
        Settle(t);
        SetUnit(m, pSlot, hp: 0, maxHp: 400, level: 15, brave: 55, faith: 55);
        Settle(t);

        Assert.False(kills.ContainsKey(52));   // an ally going down is not a weapon's kill
    }

    [Fact]
    public void Holds_the_actors_weapon_when_the_struct_flickers_to_a_non_actor()
    {
        // Live bug: between turns the active struct flickers to another player (cursor/preview)
        // with acted==0. Without the acted gate the latch followed the flicker, so an archer's
        // kill credited whatever weapon was showing at corpse-detection (e.g. a mage's staff).
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        SetRoster(m, slot: 3, level: 99, brave: 89, faith: 76, weapon: 90);   // archer (Yoichi Bow)
        SetRoster(m, slot: 0, level: 99, brave: 70, faith: 50, weapon: 63);   // mage (Blazing Staff)
        SetUnit(m, Wilham, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);  // archer struct
        SetUnit(m, Ramza, hp: 400, maxHp: 400, level: 99, brave: 70, faith: 50);   // mage struct
        SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 1);   // archer ACTS -> latch 90
        var t = new KillTracker(kills, m, Weapons);
        Settle(t);

        // struct flickers to the mage, but the mage has NOT acted (acted==0)
        SetActive(m, hp: 400, maxHp: 400, level: 99, acted: 0);
        AliveThenDead(m, slot: 0, t);

        Assert.Equal(1, kills.GetValueOrDefault(90));   // archer's Yoichi Bow
        Assert.False(kills.ContainsKey(63));            // NOT the flickered mage staff
    }

    [Fact]
    public void Holds_the_latch_when_an_ally_is_hovered_after_acting()
    {
        // The condensed struct follows cursor HOVER (BATTLE_COORDINATES.md), and acted stays 1 for
        // the REST of the turn after the action -- so hovering an ally while picking a post-act move
        // resolves THAT ally. The latch must freeze on the first resolve of the acted-period.
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        SetRoster(m, slot: 3, level: 99, brave: 89, faith: 76, weapon: 90);   // archer: the actor
        SetRoster(m, slot: 0, level: 99, brave: 70, faith: 50, weapon: 63);   // mage: hovered later
        SetUnit(m, Wilham, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetUnit(m, Ramza, hp: 400, maxHp: 400, level: 99, brave: 70, faith: 50);
        SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 1);   // archer acts -> latch 90
        var t = new KillTracker(kills, m, Weapons);
        Settle(t);

        SetActive(m, hp: 400, maxHp: 400, level: 99, acted: 1);   // hover the mage, SAME acted-period
        AliveThenDead(m, slot: 0, t);

        Assert.Equal(1, kills.GetValueOrDefault(90));   // frozen on the actor
        Assert.False(kills.ContainsKey(63));            // the hovered mage never re-latched
    }

    [Fact]
    public void A_drift_dip_in_acted_does_not_open_the_latch()
    {
        // The acted byte transiently reads 0 after a confirmed action (FFTHandsFree's documented
        // byte-drift). A 1-tick dip must NOT end the acted-period and let a hovered ally re-latch.
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        SetRoster(m, slot: 3, level: 99, brave: 89, faith: 76, weapon: 90);
        SetRoster(m, slot: 0, level: 99, brave: 70, faith: 50, weapon: 63);
        SetUnit(m, Wilham, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetUnit(m, Ramza, hp: 400, maxHp: 400, level: 99, brave: 70, faith: 50);
        SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 1);   // archer acts -> latch 90
        var t = new KillTracker(kills, m, Weapons);
        Settle(t);

        SetActive(m, hp: 400, maxHp: 400, level: 99, acted: 0);   // drift dip, mage hovered
        t.Poll(true);                                             // one tick only
        SetActive(m, hp: 400, maxHp: 400, level: 99, acted: 1);   // drift over, hover persists
        AliveThenDead(m, slot: 0, t);

        Assert.Equal(1, kills.GetValueOrDefault(90));
        Assert.False(kills.ContainsKey(63));
    }

    [Fact]
    public void Re_latches_on_the_next_acted_period()
    {
        // The freeze is per acted-period: once acted stays low long enough (a real turn end), the
        // next actor must latch normally.
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        SetRoster(m, slot: 3, level: 99, brave: 89, faith: 76, weapon: 90);   // archer, turn 1
        SetRoster(m, slot: 0, level: 99, brave: 70, faith: 50, weapon: 63);   // mage, turn 2
        SetUnit(m, Wilham, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetUnit(m, Ramza, hp: 400, maxHp: 400, level: 99, brave: 70, faith: 50);
        SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 1);   // archer acts -> latch 90
        var t = new KillTracker(kills, m, Weapons);
        Settle(t);

        SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 0);   // turn really ends
        for (int i = 0; i < 5; i++) t.Poll(true);
        SetActive(m, hp: 400, maxHp: 400, level: 99, acted: 1);   // mage's turn, mage acts
        AliveThenDead(m, slot: 0, t);

        Assert.Equal(1, kills.GetValueOrDefault(63));   // the new period latched the mage
    }

    [Fact]
    public void Latches_regardless_of_the_unreliable_team_field()
    {
        // Live, the condensed struct's team field reads 0/1/3 even for the SAME active player
        // (often 3 in a battle entered straight from a save load). The roster fingerprint -- not
        // team -- decides whether the actor is a player, so the capture must NOT gate on team.
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        SetRoster(m, slot: 3, level: 99, brave: 89, faith: 76, weapon: 52);
        SetUnit(m, Wilham, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetActive(m, hp: 352, maxHp: 352, level: 99, team: 3, acted: 1);   // team reads 3, not 0
        var t = new KillTracker(kills, m, Weapons);

        Settle(t);                      // must still latch -- the actor fingerprints to a roster player
        AliveThenDead(m, slot: 0, t);

        Assert.Equal(1, kills.GetValueOrDefault(52));
    }

    [Fact]
    public void Does_not_latch_when_an_enemy_is_the_active_unit()
    {
        // An enemy's turn: the active struct shows the ENEMY, whose HP/level/fingerprint match no
        // roster player -> resolve returns -1 -> nothing latches (that, not team, is the guard).
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        SetRoster(m, slot: 3, level: 99, brave: 89, faith: 76, weapon: 52);
        SetUnit(m, Wilham, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);   // a player on the field
        SetActive(m, hp: 681, maxHp: 681, level: 93, acted: 1);   // an ENEMY active (no roster match)
        var t = new KillTracker(kills, m, Weapons);

        Settle(t);
        AliveThenDead(m, slot: 0, t);

        Assert.Empty(kills);
    }

    [Fact]
    public void Bails_when_two_units_share_hp_but_resolve_to_different_weapons()
    {
        // Two players at 352/352 lvl99 with different fingerprints -> different weapons.
        // An ambiguous actor must NOT latch -- a missed kill beats a mis-credited one.
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        SetRoster(m, slot: 0, level: 99, brave: 70, faith: 50, weapon: 73);
        SetRoster(m, slot: 3, level: 99, brave: 89, faith: 76, weapon: 52);
        SetUnit(m, Wilham, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetUnit(m, Ramza, hp: 352, maxHp: 352, level: 99, brave: 70, faith: 50);  // same HP!
        SetActive(m, hp: 352, maxHp: 352, level: 99);
        var t = new KillTracker(kills, m, Weapons);

        Settle(t);
        AliveThenDead(m, slot: 0, t);

        Assert.Empty(kills);
    }

    [Fact]
    public void Credits_a_corpse_seen_before_the_actor_latched()
    {
        // The corpse appears BEFORE acted flips to 1 (acted = action-complete, set after the
        // death registers). On the 100ms loop the corpse is often noticed in that gap; it must
        // WAIT for the actor, not be permanently dropped.
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        SetRoster(m, slot: 3, level: 99, brave: 89, faith: 76, weapon: 52);
        SetUnit(m, Wilham, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 0);   // actor has NOT acted yet
        var t = new KillTracker(kills, m, Weapons);

        // The enemy must be seen alive first (seenAlive guard), then dead.
        SetEnemy(m, slot: 0, hp: 300);
        Settle(t, 3);   // seenAlive built during acted=0

        SetUnit(m, slot: 0, hp: 0);   // enemy now dead
        bool first = t.Poll(true);   // dead tick 1: pending, no latch yet
        Assert.False(first);
        t.Poll(true);                // dead tick 2

        SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 1);   // action completes
        bool second = t.Poll(true);   // dead tick 3 -> deadStreak >=3, actor latched -> credit
        Assert.True(second);
        Assert.Equal(1, kills.GetValueOrDefault(52));
    }

    [Fact]
    public void Does_not_credit_a_pending_corpse_after_two_acted_fall_edges()
    {
        // The real expiry is turn-edge-driven: a DoT corpse during ENEMY phases (no player latch)
        // expires when two debounced acted-falling edges pass uncredited -- so the NEXT player's
        // latch can't inherit it. The killer's own action would have latched during its acted
        // period BEFORE its fall, so two falls with no latch means this was never a player kill.
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        SetRoster(m, slot: 3, level: 99, brave: 89, faith: 76, weapon: 52);
        SetUnit(m, Wilham, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);   // a player on field
        SetActive(m, hp: 681, maxHp: 681, level: 93, acted: 0);   // an ENEMY active (no roster match)
        var t = new KillTracker(kills, m, Weapons);

        // Enemy seen alive, then dead (seenAlive guard + deadStreak).
        SetEnemy(m, slot: 0, hp: 300);
        Settle(t, 3);   // seenAlive
        SetUnit(m, slot: 0, hp: 0);
        Settle(t, 3);   // deadStreak >=3, but no latch -> pending

        // Two full enemy acted 1->0 cycles. Each falling edge (acted low for UnfreezeTicks) counts once.
        for (int cycle = 0; cycle < 2; cycle++)
        {
            SetActive(m, hp: 681, maxHp: 681, level: 93, acted: 1);   // enemy acts
            t.Poll(true);
            SetActive(m, hp: 681, maxHp: 681, level: 93, acted: 0);   // acted falls
            for (int i = 0; i < KillTracker.UnfreezeTicks + 1; i++) t.Poll(true);   // debounce the fall
        }

        SetRoster(m, slot: 4, level: 50, brave: 60, faith: 60, weapon: 63);   // a LATER player
        SetUnit(m, Ramza, hp: 400, maxHp: 400, level: 50, brave: 60, faith: 60);
        SetActive(m, hp: 400, maxHp: 400, level: 50, acted: 1);   // the next player's turn latches
        t.Poll(true);

        Assert.Empty(kills);   // the expired corpse is NOT credited to the late player
    }

    [Fact]
    public void Expires_a_pending_corpse_on_the_wall_clock_backstop_with_no_edges()
    {
        // Backstop: with NO acted-falling edges at all (a frozen/softlocked scene), the corpse still
        // expires after PendingTtl ticks so it can't dangle forever.
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        SetRoster(m, slot: 3, level: 99, brave: 89, faith: 76, weapon: 52);
        SetUnit(m, Wilham, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetActive(m, hp: 681, maxHp: 681, level: 93, acted: 0);   // an enemy active, never acts
        var t = new KillTracker(kills, m, Weapons);

        SetEnemy(m, slot: 0, hp: 300);
        Settle(t, 3);  // seenAlive
        SetUnit(m, slot: 0, hp: 0);
        // 3 ticks to get deadStreak (enters pending), then enough ticks to exceed backstop.
        for (int i = 0; i < KillTracker.PendingTtl + 5; i++) t.Poll(true);

        SetRoster(m, slot: 4, level: 50, brave: 60, faith: 60, weapon: 63);
        SetUnit(m, Ramza, hp: 400, maxHp: 400, level: 50, brave: 60, faith: 60);
        SetActive(m, hp: 400, maxHp: 400, level: 50, acted: 1);   // a late actor finally latches
        t.Poll(true);

        Assert.Empty(kills);   // the backstop-expired corpse is NOT credited to the late actor
    }

    // --- re-baseline on reset: corpses present at battle start never credit ---

    [Fact]
    public void Corpses_present_at_reset_are_not_counted_as_kills()
    {
        // A reset (battle enter, or a quick-load) must not let a pre-existing corpse credit -- the
        // seen-alive guard means a pre-existing corpse (never seen alive this battle) is ineligible.
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        SetRoster(m, slot: 3, level: 99, brave: 89, faith: 76, weapon: 52);
        SetUnit(m, Wilham, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 1);   // a player would otherwise be latched
        SetUnit(m, slot: 0, hp: 0);                                // a corpse already on the field at reset
        var t = new KillTracker(kills, m, Weapons);
        t.ResetBattle();
        Settle(t);   // pre-existing corpse never saw alive -> seenAlive=false -> never credits

        Assert.Empty(kills);
    }

    [Fact]
    public void A_unit_dying_after_the_baseline_poll_still_credits()
    {
        // The baseline only protects corpses present at reset; a unit that dies AFTER counts normally.
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        SetRoster(m, slot: 3, level: 99, brave: 89, faith: 76, weapon: 52);
        SetUnit(m, Wilham, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 1);
        var t = new KillTracker(kills, m, Weapons);
        t.ResetBattle();
        Settle(t);   // latch the actor, no enemy yet

        AliveThenDead(m, slot: 0, t);   // now it dies

        Assert.Equal(1, kills.GetValueOrDefault(52));
    }

    // --- corpse-time fallback latch: the first-kill fix ---

    [Fact]
    public void First_kill_fallback_latches_a_stable_actor_with_acted_zero()
    {
        // The first action of a battle: the killing action's `acted` edge arrives seconds AFTER the
        // corpse, with no prior latch. While a corpse is pending and no latch exists, resolve the
        // actor WITHOUT the acted gate -- accept only after 3 consecutive identical non-empty resolves.
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        SetRoster(m, slot: 3, level: 99, brave: 89, faith: 76, weapon: 52);
        SetUnit(m, Wilham, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 0);   // actor present, acted NOT set
        var t = new KillTracker(kills, m, Weapons);

        // Build seenAlive first (enemy alive 3 ticks).
        SetEnemy(m, slot: 0, hp: 300);
        Settle(t, 3);

        // Enemy dies; fallback should activate (pending, no latch).
        SetUnit(m, slot: 0, hp: 0);
        // 3 dead ticks to enter pending, then fallback streak builds.
        Assert.False(t.Poll(true));   // dead tick 1 -> pending
        Assert.False(t.Poll(true));   // dead tick 2
        Assert.False(t.Poll(true));   // dead tick 3 -> deadStreak>=3, enters pending, fallback tick 1
        Assert.False(t.Poll(true));   // fallback streak 2
        Assert.False(t.Poll(true));   // fallback streak 3 -> latch accepted

        bool credited = t.Poll(true); // latch exists -> pending corpse credits

        Assert.True(credited);
        Assert.Equal(1, kills.GetValueOrDefault(52));
    }

    [Fact]
    public void First_kill_fallback_does_not_latch_a_flickering_resolve()
    {
        // A hover that flickers between two units (A,B,A) over the streak window must NOT latch --
        // any disagreement resets the stability streak.
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        SetRoster(m, slot: 3, level: 99, brave: 89, faith: 76, weapon: 52);   // unit A
        SetRoster(m, slot: 0, level: 99, brave: 70, faith: 50, weapon: 63);   // unit B
        SetUnit(m, Wilham, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetUnit(m, Ramza, hp: 400, maxHp: 400, level: 99, brave: 70, faith: 50);
        var t = new KillTracker(kills, m, Weapons);

        SetEnemy(m, slot: 0, hp: 300);
        Settle(t, 3);
        SetUnit(m, slot: 0, hp: 0);
        Settle(t, 3);   // enters pending

        SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 0);   // resolves A
        t.Poll(true);
        SetActive(m, hp: 400, maxHp: 400, level: 99, acted: 0);   // resolves B (flicker)
        t.Poll(true);
        SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 0);   // resolves A again
        t.Poll(true);
        t.Poll(true);

        Assert.Empty(kills);   // no stable 3-in-a-row -> never latched
    }

    [Fact]
    public void First_kill_fallback_does_not_run_once_a_latch_exists()
    {
        // Once a real (acted-gated) latch exists, a HOVER over an ally must not steal it via the
        // fallback path -- the fallback only runs while _lastPlayerWeapons is empty.
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        SetRoster(m, slot: 3, level: 99, brave: 89, faith: 76, weapon: 90);   // archer: the real actor
        SetRoster(m, slot: 0, level: 99, brave: 70, faith: 50, weapon: 63);   // mage: hovered later
        SetUnit(m, Wilham, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetUnit(m, Ramza, hp: 400, maxHp: 400, level: 99, brave: 70, faith: 50);
        SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 1);   // archer acts -> real latch 90
        var t = new KillTracker(kills, m, Weapons);
        Settle(t);

        SetActive(m, hp: 400, maxHp: 400, level: 99, acted: 0);   // hover the mage, acted 0
        AliveThenDead(m, slot: 0, t);
        for (int i = 0; i < 5; i++) t.Poll(true);   // fallback must NOT re-latch the mage

        Assert.Equal(1, kills.GetValueOrDefault(90));   // archer's bow
        Assert.False(kills.ContainsKey(63));            // the hovered mage never stole it
    }

    [Fact]
    public void A_pending_corpse_survives_a_slow_death_animation()
    {
        // The TTL is ticks of the 33ms engine loop. 60 ticks ~ a 2s animation before the actor's
        // acted flag latches -- a realistic wait that the old 100ms-era constant (30) expired at ~1s.
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        SetRoster(m, slot: 3, level: 99, brave: 89, faith: 76, weapon: 52);
        SetUnit(m, Wilham, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 0);
        var t = new KillTracker(kills, m, Weapons);

        SetEnemy(m, slot: 0, hp: 300);
        Settle(t, 3);   // seenAlive
        SetUnit(m, slot: 0, hp: 0);
        Settle(t, 3);   // deadStreak -> pending

        for (int i = 0; i < 57; i++) t.Poll(true);   // ~2s with no actor: still pending

        SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 1);   // animation over, actor latches
        t.Poll(true);

        Assert.Equal(1, kills.GetValueOrDefault(52));
    }

    [Fact]
    public void Credits_both_weapons_of_a_dual_wielder()
    {
        // A dual-wielding unit (a weapon in EACH hand) earns a kill on BOTH blades at once.
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        SetRoster(m, slot: 3, level: 99, brave: 89, faith: 76, weapon: 52, lhand: 90);
        SetUnit(m, Wilham, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetActive(m, hp: 352, maxHp: 352, level: 99);
        var t = new KillTracker(kills, m, Weapons);

        Settle(t);
        AliveThenDead(m, slot: 0, t);

        Assert.Equal(1, kills.GetValueOrDefault(52));   // right-hand weapon
        Assert.Equal(1, kills.GetValueOrDefault(90));   // left-hand weapon -- BOTH counted
    }

    [Fact]
    public void Does_not_credit_a_shield_in_the_off_hand()
    {
        // Normal loadout: weapon in the right hand, SHIELD in the left. Only the weapon counts.
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        SetRoster(m, slot: 3, level: 99, brave: 89, faith: 76, weapon: 52, lhand: Shield);
        SetUnit(m, Wilham, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetActive(m, hp: 352, maxHp: 352, level: 99);
        var t = new KillTracker(kills, m, Weapons);

        Settle(t);
        AliveThenDead(m, slot: 0, t);

        Assert.Equal(1, kills.GetValueOrDefault(52));
        Assert.False(kills.ContainsKey(Shield));   // a shield never earns a kill
    }

    [Fact]
    public void Credits_an_off_hand_weapon_even_with_a_shield_in_the_primary()
    {
        // The degenerate loadout: SHIELD in the right hand, weapon in the left. Trust the item,
        // not the slot -- the weapon counts wherever it sits, the shield never does.
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        SetRoster(m, slot: 3, level: 99, brave: 89, faith: 76, weapon: Shield, lhand: 52);
        SetUnit(m, Wilham, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetActive(m, hp: 352, maxHp: 352, level: 99);
        var t = new KillTracker(kills, m, Weapons);

        Settle(t);
        AliveThenDead(m, slot: 0, t);

        Assert.Equal(1, kills.GetValueOrDefault(52));   // the off-hand weapon still counts
        Assert.False(kills.ContainsKey(Shield));        // the primary shield does not
    }

    [Fact]
    public void Counts_the_same_weapon_in_both_hands_only_once()
    {
        // Two copies of the SAME blade dual-wielded (id 52 in both hands) share one kill counter
        // -- a single kill is +1 for that weapon, NOT +2 (ActorResolver dedups the hand list).
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        SetRoster(m, slot: 3, level: 99, brave: 89, faith: 76, weapon: 52, lhand: 52);
        SetUnit(m, Wilham, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetActive(m, hp: 352, maxHp: 352, level: 99);
        var t = new KillTracker(kills, m, Weapons);

        Settle(t);
        AliveThenDead(m, slot: 0, t);

        Assert.Equal(1, kills.GetValueOrDefault(52));   // ONE kill, not two
    }

    [Fact]
    public void Credits_a_dual_wield_off_hand_at_the_reserved_0x18_slot()
    {
        // LIVE roster dump (FFT:IC): a dual-wielder's SECOND weapon sits at +0x18 -- the field
        // FFTHandsFree labels "reserved" -- NOT +0x16 (which stayed empty). Shields go to +0x1A.
        // This is the real-game path; reading only +0x14/+0x16 credited just the right hand.
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        SetRoster(m, slot: 3, level: 99, brave: 89, faith: 76, weapon: 52, offhand: 90);
        SetUnit(m, Wilham, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetActive(m, hp: 352, maxHp: 352, level: 99);
        var t = new KillTracker(kills, m, Weapons);

        Settle(t);
        AliveThenDead(m, slot: 0, t);

        Assert.Equal(1, kills.GetValueOrDefault(52));   // right hand
        Assert.Equal(1, kills.GetValueOrDefault(90));   // off-hand at +0x18 -- the live bug
    }

    // --- new behavioral facts for the band/capture-hybrid ---

    [Fact]
    public void Load_transient_never_credits()
    {
        // A unit seen alive fewer than 3 consecutive ticks then dead never earns credit --
        // the seenAlive guard defeats phantom kills from load-transient memory noise.
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        SetRoster(m, slot: 3, level: 99, brave: 89, faith: 76, weapon: 52);
        SetUnit(m, Wilham, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 1);
        var t = new KillTracker(kills, m, Weapons);

        SetEnemy(m, slot: 0, hp: 300);
        t.Poll(true);  // alive tick 1 (streak 1, not seenAlive yet)
        t.Poll(true);  // alive tick 2 (streak 2)
        SetUnit(m, slot: 0, hp: 0);   // dies before reaching streak 3
        for (int i = 0; i < 5; i++) t.Poll(true);

        Assert.Empty(kills);   // seenAlive was never set -> credit gate closed
    }

    [Fact]
    public void Slot_identity_swap_resets_seen_alive()
    {
        // If a slot's (level,brave,faith) identity changes while alive, the slot-reuse path
        // resets seenAlive -- the new unit must build its own 3-tick streak from scratch.
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        SetRoster(m, slot: 3, level: 99, brave: 89, faith: 76, weapon: 52);
        SetUnit(m, Wilham, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 1);
        var t = new KillTracker(kills, m, Weapons);

        // Original unit, alive for 3 ticks (seenAlive=true with identity A).
        SetEnemy(m, slot: 0, hp: 300, maxHp: 400, level: 10, brave: 50, faith: 50);
        Settle(t, 3);

        // SWAP: a different unit appears at the same slot (level changes).
        SetEnemy(m, slot: 0, hp: 300, maxHp: 400, level: 15, brave: 55, faith: 55);
        // Only 2 alive ticks of the new identity -> NOT seenAlive.
        t.Poll(true); t.Poll(true);
        SetUnit(m, slot: 0, hp: 0, maxHp: 400, level: 15, brave: 55, faith: 55);
        Settle(t, 3);

        Assert.Empty(kills);   // swap reset seenAlive; new unit only 2 alive ticks -> no credit
    }

    [Fact]
    public void Revive_then_rekill_credits_twice()
    {
        // Belt eviction: when a credited identity is seen alive again (revive), it leaves the
        // belt so a subsequent kill counts normally -- a genuine rekill earns full credit.
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        SetRoster(m, slot: 3, level: 99, brave: 89, faith: 76, weapon: 52);
        SetUnit(m, Wilham, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 1);
        var t = new KillTracker(kills, m, Weapons);
        Settle(t);

        // First kill.
        AliveThenDead(m, slot: 0, t);
        Assert.Equal(1, kills.GetValueOrDefault(52));

        // Revive (seen alive again -> evict from belt + reset deadCredited).
        SetEnemy(m, slot: 0, hp: 300, maxHp: 400, level: 10, brave: 50, faith: 50);
        Settle(t, 3);   // eviction happens once seenAlive again

        // Kill again.
        SetUnit(m, slot: 0, hp: 0, maxHp: 400, level: 10, brave: 50, faith: 50);
        Settle(t, 3);

        Assert.Equal(2, kills.GetValueOrDefault(52));   // both kills count
    }

    [Fact]
    public void Same_identity_alive_at_second_slot_then_dead_is_a_revive_and_credits_again()
    {
        // Per-down credit ruling: if the same (lvl,brave,faith,maxHp) identity is SEEN ALIVE
        // (3-tick streak) at a second band slot after being credited at slot 0, that is treated
        // as a genuine revive -- the identity re-enters the creditable pool. The second death at
        // slot 1 earns a kill. (Compare with the frozen-twin test, where the twin is never seen
        // alive at all -- that case still credits only once.)
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        SetRoster(m, slot: 3, level: 99, brave: 89, faith: 76, weapon: 52);
        SetUnit(m, Wilham, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 1);
        var t = new KillTracker(kills, m, Weapons);
        Settle(t);

        // First kill at slot 0.
        AliveThenDead(m, slot: 0, t);
        Assert.Equal(1, kills.GetValueOrDefault(52));

        // Same identity seen alive (3 ticks) at slot 1 -> revive detected -> creditable again.
        SetEnemy(m, slot: 1, hp: 300, maxHp: 400, level: 10, brave: 50, faith: 50);
        Settle(t, 3);
        // Dies at slot 1 -> second credit earned.
        SetUnit(m, slot: 1, hp: 0, maxHp: 400, level: 10, brave: 50, faith: 50);
        Settle(t, 3);

        Assert.Equal(2, kills.GetValueOrDefault(52));   // revive then re-kill earns the second credit
    }

    [Fact]
    public void Unknown_identity_corpse_never_credits()
    {
        // A band corpse whose identity was NEVER captured in the static array (inb==1) never
        // credits -- the capture is the team oracle (only known enemies count as kills).
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        SetRoster(m, slot: 3, level: 99, brave: 89, faith: 76, weapon: 52);
        SetUnit(m, Wilham, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 1);
        var t = new KillTracker(kills, m, Weapons);
        Settle(t);

        // Band entry exists, but NO array entry (inb==1) for this identity.
        SetUnit(m, slot: 0, hp: 300, maxHp: 400, level: 10, brave: 50, faith: 50);
        // No SetArrayEnemy call -- identity NOT captured.
        Settle(t, 3);   // seenAlive
        SetUnit(m, slot: 0, hp: 0, maxHp: 400, level: 10, brave: 50, faith: 50);
        Settle(t, 3);   // deadStreak

        Assert.Empty(kills);   // unknown identity -> no credit
    }

    [Fact]
    public void Captures_an_enemy_whose_array_flag_reads_zero()
    {
        // Live finding (2026-06-09): the array's "inBattle" u16 PULSES 0/1 per unit mid-battle --
        // roughly half the live enemies read 0 at any instant. The capture must key on sane
        // identity fields in an enemy-side slot, NOT the flag, or those enemies' kills are
        // refused as "not a captured enemy" (the restart-and-first-kill refusal Patrick hit).
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        SetRoster(m, slot: 3, level: 99, brave: 89, faith: 76, weapon: 52);
        SetUnit(m, Wilham, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 1);
        var t = new KillTracker(kills, m, Weapons);
        Settle(t);

        SetUnit(m, slot: 0, hp: 300, maxHp: 400, level: 10, brave: 50, faith: 50);       // band: alive
        SetArrayEnemy(m, slot: 0, level: 10, brave: 50, faith: 50, maxHp: 400, inb: 0);  // flag low
        Settle(t);   // seenAlive + capture window
        SetUnit(m, slot: 0, hp: 0, maxHp: 400, level: 10, brave: 50, faith: 50);
        Settle(t);   // deadStreak -> credit attempt

        Assert.Equal(1, kills.GetValueOrDefault(52));   // captured despite inb==0
    }

    [Fact]
    public void Enemy_colliding_with_roster_fingerprint_still_credits()
    {
        // An enemy whose (lvl,brave,faith) matches a roster unit's fingerprint STILL earns a
        // kill credit when captured in the static array -- capture beats roster coincidence.
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        // Roster player: same level/brave/faith as the enemy below.
        SetRoster(m, slot: 3, level: 10, brave: 50, faith: 50, weapon: 52);
        SetUnit(m, Wilham, hp: 352, maxHp: 352, level: 10, brave: 50, faith: 50);
        SetActive(m, hp: 352, maxHp: 352, level: 10, acted: 1);
        var t = new KillTracker(kills, m, Weapons);
        Settle(t);

        // Enemy at slot 0 shares the same fingerprint as the roster player.
        AliveThenDead(m, slot: 0, t, level: 10, brave: 50, faith: 50);

        Assert.Equal(1, kills.GetValueOrDefault(52));   // capture oracle wins -> credits
    }

    [Fact]
    public void Marking_frozen_while_not_on_field()
    {
        // Alive ticks with onField=false don't build the seenAlive streak; dead ticks also
        // don't build the dead streak. Latch logic still runs (it's not field-gated).
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        SetRoster(m, slot: 3, level: 99, brave: 89, faith: 76, weapon: 52);
        SetUnit(m, Wilham, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 1);
        var t = new KillTracker(kills, m, Weapons);

        // 5 ticks with onField=false: alive ticks don't count toward streak.
        SetEnemy(m, slot: 0, hp: 300);
        for (int i = 0; i < 5; i++) t.Poll(false);   // off-field: no streak progress

        // Now on-field: still need 3 alive ticks from scratch.
        Settle(t, 2);   // only 2 alive ticks on-field
        SetUnit(m, slot: 0, hp: 0);
        Settle(t, 3);   // dead ticks, but seenAlive not yet reached 3

        Assert.Empty(kills);   // off-field ticks didn't count -> seenAlive never set
    }

    // --- LastPlayerMainHand: main-hand id of the most recent acting player ---

    [Fact]
    public void LastPlayerMainHand_is_zero_before_any_actor_latches()
    {
        var m = new FakeSparseMemory();
        var t = new KillTracker(new Dictionary<int, int>(), m, Weapons);
        Assert.Equal(0, t.LastPlayerMainHand);
    }

    [Fact]
    public void LastPlayerMainHand_exposes_RRHand_after_a_latch()
    {
        var m = new FakeSparseMemory();
        SetRoster(m, slot: 3, level: 99, brave: 89, faith: 76, weapon: 52);
        SetUnit(m, Wilham, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 1);
        var t = new KillTracker(new Dictionary<int, int>(), m, Weapons);
        Settle(t);
        Assert.Equal(52, t.LastPlayerMainHand);
    }

    [Fact]
    public void LastPlayerMainHand_for_dual_wielder_is_RRHand_not_offhand()
    {
        // A dual-wielder: both ids appear in LastPlayerWeapons, but LastPlayerMainHand = RRHand only.
        // A Living Weapon earns kills in any hand, but commands its gift only from the main hand.
        var m = new FakeSparseMemory();
        SetRoster(m, slot: 3, level: 99, brave: 89, faith: 76, weapon: 52, offhand: 90);
        SetUnit(m, Wilham, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 1);
        var t = new KillTracker(new Dictionary<int, int>(), m, Weapons);
        Settle(t);
        Assert.Equal(2, t.LastPlayerWeapons.Count);   // both hands credited (kill counting unchanged)
        Assert.Equal(52, t.LastPlayerMainHand);       // main hand is RRHand
    }

    [Fact]
    public void LastPlayerMainHand_resets_to_zero_on_ResetBattle()
    {
        var m = new FakeSparseMemory();
        SetRoster(m, slot: 3, level: 99, brave: 89, faith: 76, weapon: 52);
        SetUnit(m, Wilham, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 1);
        var t = new KillTracker(new Dictionary<int, int>(), m, Weapons);
        Settle(t);
        Assert.NotEqual(0, t.LastPlayerMainHand);
        t.ResetBattle();
        Assert.Equal(0, t.LastPlayerMainHand);
    }

    // --- status-death detection (Spec A + B) ---

    /// <summary>Set the Dead status bit on a band entry at the given slot without touching HP.
    /// When <paramref name="set"/> is false, clears the bit (alive again).</summary>
    private static void SetDeadBit(FakeSparseMemory m, int slot, bool set = true)
    {
        long s = Offsets.BandReadBase + (long)slot * Offsets.CombatStride;
        byte cur = m.U8s.TryGetValue(s + Offsets.ADeadStatus, out var v) ? v : (byte)0;
        m.U8s[s + Offsets.ADeadStatus] = set
            ? (byte)(cur | Offsets.ADeadBit)
            : (byte)(cur & ~Offsets.ADeadBit);
    }

    [Fact]
    public void Status_death_hp_positive_dead_bit_set_enters_corpse_pipeline()
    {
        // Root bug: Phoenix Down on undead kills with a status transition -- HP stays nonzero
        // but the Dead bit fires. The corpse must enter pending and be credited normally.
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        SetRoster(m, slot: 3, level: 99, brave: 89, faith: 76, weapon: 52);
        SetUnit(m, Wilham, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 1);
        var t = new KillTracker(kills, m, Weapons);
        Settle(t);   // latch weapon 52

        // Enemy alive (hp > 0, Dead bit clear) for 3 ticks -> seenAlive.
        SetEnemy(m, slot: 0, hp: 300, maxHp: 400, level: 10, brave: 50, faith: 50);
        Settle(t, 3);

        // Status death: hp stays 300 (never hits 0), Dead bit set.
        SetDeadBit(m, slot: 0, set: true);
        Settle(t, 3);   // 3 dead-by-status ticks -> credit

        Assert.Equal(1, kills.GetValueOrDefault(52));
    }

    [Fact]
    public void Status_death_is_credited_to_the_acting_players_weapon()
    {
        // The attribution path must be the same as the HP==0 path -- the acting weapon earns it.
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        SetRoster(m, slot: 3, level: 99, brave: 89, faith: 76, weapon: 52);
        SetUnit(m, Wilham, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 1);
        var t = new KillTracker(kills, m, Weapons);
        Settle(t);

        SetEnemy(m, slot: 0, hp: 300, maxHp: 400, level: 10, brave: 50, faith: 50);
        Settle(t, 3);
        SetDeadBit(m, slot: 0, set: true);
        Settle(t, 3);

        Assert.Equal(1, kills.GetValueOrDefault(52));
        Assert.Equal(1, kills.Count);   // nothing else credited
    }

    [Fact]
    public void Hp_zero_path_unchanged_after_adding_dead_bit_check()
    {
        // Regression pin: plain hp==0 death (no Dead bit) still credits normally.
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        SetRoster(m, slot: 3, level: 99, brave: 89, faith: 76, weapon: 52);
        SetUnit(m, Wilham, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 1);
        var t = new KillTracker(kills, m, Weapons);
        Settle(t);

        AliveThenDead(m, slot: 0, t);   // hp -> 0, Dead bit never set

        Assert.Equal(1, kills.GetValueOrDefault(52));
    }

    [Fact]
    public void Undead_dies_revives_dies_again_credits_twice()
    {
        // Design ruling: per-down credit. An undead that dies (status or HP), revives, then dies
        // again earns a credit EACH time. The duplicate-identity guard must not block the second death.
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        SetRoster(m, slot: 3, level: 99, brave: 89, faith: 76, weapon: 52);
        SetUnit(m, Wilham, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 1);
        var t = new KillTracker(kills, m, Weapons);
        Settle(t);

        // First death: status-death (hp stays positive, Dead bit fires).
        SetEnemy(m, slot: 0, hp: 300, maxHp: 400, level: 10, brave: 50, faith: 50);
        Settle(t, 3);   // seenAlive
        SetDeadBit(m, slot: 0, set: true);
        Settle(t, 3);   // credited (#1)
        Assert.Equal(1, kills.GetValueOrDefault(52));

        // Revive: hp still 300 (or restored), Dead bit CLEARED -> observed alive again.
        SetDeadBit(m, slot: 0, set: false);
        Settle(t, 3);   // alive streak -> identity re-enters the creditable pool

        // Second death: hp goes to 0 this time.
        SetUnit(m, slot: 0, hp: 0, maxHp: 400, level: 10, brave: 50, faith: 50);
        SetDeadBit(m, slot: 0, set: false);   // bit already clear; hp==0 drives the dead path
        Settle(t, 3);   // credited (#2)

        Assert.Equal(2, kills.GetValueOrDefault(52));
    }

    [Fact]
    public void Dead_identity_at_two_band_slots_credits_exactly_once()
    {
        // Frozen-twin regression: slot 0 dies and is credited; the SAME identity immediately
        // appears dead at slot 1 (a frozen copy from a restart -- never seen alive at slot 1).
        // The twin copy never reads alive in between, so the belt blocks the second attempt.
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        SetRoster(m, slot: 3, level: 99, brave: 89, faith: 76, weapon: 52);
        SetUnit(m, Wilham, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 1);
        var t = new KillTracker(kills, m, Weapons);
        Settle(t);

        // Slot 0: alive then dead -> credit #1.
        AliveThenDead(m, slot: 0, t);
        Assert.Equal(1, kills.GetValueOrDefault(52));

        // Slot 1: same identity, but starts ALREADY dead (frozen twin -- never seen alive at slot 1).
        // SetUnit writes hp=0 directly; no alive ticks at this slot before the dead scan.
        SetUnit(m, slot: 1, hp: 0, maxHp: 400, level: 10, brave: 50, faith: 50);
        SetArrayEnemy(m, slot: 1, level: 10, brave: 50, faith: 50, maxHp: 400);
        Settle(t, 3);   // 3 dead ticks at slot 1, but seenAlive[1] is false -> no credit

        Assert.Equal(1, kills.GetValueOrDefault(52));   // still ONE credit, not two
    }

    [Fact]
    public void Dead_bit_read_unreadable_hp_path_still_works()
    {
        // If the memory guard on the Dead-bit read fails (returns 0 -- mem is already
        // zeroing unregistered addresses in FakeSparseMemory), the hp==0 path must still credit.
        // This is implicitly covered by the FakeSparseMemory returning 0 for unknown addresses --
        // unregistered status bytes return 0, so bit tests return false and only hp decides.
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        SetRoster(m, slot: 3, level: 99, brave: 89, faith: 76, weapon: 52);
        SetUnit(m, Wilham, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 1);
        var t = new KillTracker(kills, m, Weapons);
        Settle(t);

        // The status byte is NOT written for this unit (FakeSparseMemory returns 0 -> Dead bit = 0).
        AliveThenDead(m, slot: 0, t);   // only hp==0 drives death detection

        Assert.Equal(1, kills.GetValueOrDefault(52));
    }

    [Fact]
    public void Ally_with_dead_bit_set_never_credited()
    {
        // An ally whose Dead bit is set (knocked out) must never earn a kill credit.
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        SetRoster(m, slot: 3, level: 99, brave: 89, faith: 76, weapon: 52);
        SetUnit(m, Wilham, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 1);
        var t = new KillTracker(kills, m, Weapons);
        Settle(t);

        // A player-side band slot -- identity NOT in the enemy oracle.
        int pSlot = Offsets.EnemySlotMax + 5;
        SetUnit(m, pSlot, hp: 300, maxHp: 400, level: 15, brave: 55, faith: 55);
        Settle(t, 3);   // seenAlive
        // Set Dead bit but keep hp > 0 (status death on an ally).
        SetDeadBit(m, pSlot, set: true);
        Settle(t, 3);

        Assert.False(kills.ContainsKey(52));   // ally death is never a weapon's kill
    }

    // --- LastActorFingerprint: per-wielder attribution for same-weapon signatures (Larceny) ---

    // Two Arcanum holders share weapon set {30}: we add 30 to the tracked weapons for these tests.
    private static readonly HashSet<int> WeaponsWithArcanum = new() { 30, 52, 63, 73, 90 };

    [Fact]
    public void LastActorFingerprint_latches_the_acting_players_fingerprint()
    {
        var m = new FakeSparseMemory();
        SetRoster(m, slot: 3, level: 99, brave: 89, faith: 76, weapon: 30);
        SetUnit(m, Wilham, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 1);
        var t = new KillTracker(new Dictionary<int, int>(), m, WeaponsWithArcanum);
        Settle(t);

        Assert.Equal((99, 89, 76), t.LastActorFingerprint);
    }

    [Fact]
    public void LastActorFingerprint_refreshes_when_second_same_weapon_wielder_acts()
    {
        // THE REGRESSION GUARD: two players both hold only weapon id 30 (same weapon set);
        // SameSet is true between them. Despite that, LastActorFingerprint must refresh to
        // the SECOND actor's fingerprint when they act in the next acted-period.
        var m = new FakeSparseMemory();
        // Player A: level 99, brave 89, faith 76.
        SetRoster(m, slot: 3, level: 99, brave: 89, faith: 76, weapon: 30);
        // Player B: level 50, brave 60, faith 55 -- different fingerprint, same weapon.
        SetRoster(m, slot: 4, level: 50, brave: 60, faith: 55, weapon: 30);
        SetUnit(m, Wilham, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetUnit(m, Ramza,  hp: 400, maxHp: 400, level: 50, brave: 60, faith: 55);

        // Player A acts first.
        SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 1);
        var t = new KillTracker(new Dictionary<int, int>(), m, WeaponsWithArcanum);
        Settle(t);
        Assert.Equal((99, 89, 76), t.LastActorFingerprint);   // latched to A

        // End A's acted-period (debounced fall).
        SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 0);
        Settle(t, KillTracker.UnfreezeTicks);

        // Player B now acts. B has same weapon set {30} as A (SameSet returns true).
        // LastActorFingerprint must still refresh to B's fingerprint.
        SetActive(m, hp: 400, maxHp: 400, level: 50, acted: 1);
        Settle(t);

        Assert.Equal((50, 60, 55), t.LastActorFingerprint);   // updated to B despite SameSet
    }

    [Fact]
    public void LastActorFingerprint_resets_to_default_on_ResetBattle()
    {
        var m = new FakeSparseMemory();
        SetRoster(m, slot: 3, level: 99, brave: 89, faith: 76, weapon: 30);
        SetUnit(m, Wilham, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 1);
        var t = new KillTracker(new Dictionary<int, int>(), m, WeaponsWithArcanum);
        Settle(t);
        Assert.NotEqual(default, t.LastActorFingerprint);

        t.ResetBattle();
        Assert.Equal(default, t.LastActorFingerprint);
    }

    [Fact]
    public void LastActorFingerprint_is_default_when_acting_unit_is_an_enemy()
    {
        // An enemy's acted-period: the actor resolves to no roster player; fingerprint stays default.
        var m = new FakeSparseMemory();
        SetRoster(m, slot: 3, level: 99, brave: 89, faith: 76, weapon: 30);
        SetUnit(m, Wilham, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        // Active unit: an enemy (no roster match).
        SetActive(m, hp: 681, maxHp: 681, level: 93, acted: 1);
        var t = new KillTracker(new Dictionary<int, int>(), m, WeaponsWithArcanum);
        Settle(t);

        // TryResolveActingPlayer returns false for enemies -> _latched is never set -> fp stays default.
        Assert.Equal(default, t.LastActorFingerprint);
    }

    // --- lethal-actor stamp: kill credit goes to the actor latched at the dead-streak EDGE ---

    [Fact]
    public void Lethal_actor_stamp_wins_over_a_re_latched_weapon_at_confirmation()
    {
        // Bug: a spell-kill (e.g. staff) can confirm dead (deadStreak >= DeadNeeded) AFTER a
        // faster subsequent unit (e.g. bow) re-latched, stealing the credit. Fix: stamp the
        // acting weapon at the dead-streak START (alive->dead transition, deadStreak 0->1) and
        // credit THAT stamp at confirmation, ignoring any later re-latch.
        //
        // Construction via onField=false: the dead branch exits before _deadStreak++ when
        // !onField, so off-field polls freeze the streak. This lets us advance the latch to a
        // second weapon between the stamp edge and the confirmation, constructing the race in
        // unit-test time without needing a 4-tick UnfreezeTicks cycle on-field.
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();

        // Actor 1 (staff, weapon 52): the genuine killer; latches first.
        SetRoster(m, slot: 3, level: 99, brave: 89, faith: 76, weapon: 52);
        SetUnit(m, Wilham, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        // Actor 2 (bow, weapon 90): a faster unit that re-latches before confirmation.
        SetRoster(m, slot: 0, level: 99, brave: 70, faith: 50, weapon: 90);
        SetUnit(m, Ramza, hp: 400, maxHp: 400, level: 99, brave: 70, faith: 50);

        // Actor 1 acts (staff), latch weapon 52.
        SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 1);
        var t = new KillTracker(kills, m, Weapons);
        Settle(t);
        Assert.Equal(new List<int> { 52 }, t.LastPlayerWeapons);

        // Enemy alive for 3 on-field ticks (seenAlive).
        SetEnemy(m, slot: 0, hp: 300);
        Settle(t);

        // Enemy dies: one on-field dead tick advances deadStreak to 1 -- stamp fires here.
        SetUnit(m, slot: 0, hp: 0);
        t.Poll(true);   // deadStreak = 1, stamp = {52}

        // Now switch to off-field polls (streak frozen) and cycle the latch to actor 2 (bow).
        // End actor 1's acted-period (debounced fall needs UnfreezeTicks off-field ticks with acted=0).
        SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 0);
        for (int i = 0; i < KillTracker.UnfreezeTicks; i++) t.Poll(false);   // fall debounce, off-field

        // Actor 2 (bow) now acts -- new acted-period latches weapon 90.
        SetActive(m, hp: 400, maxHp: 400, level: 99, acted: 1);
        t.Poll(false);   // latch re-assigns to 90, still off-field (streak still 1)
        Assert.Equal(new List<int> { 90 }, t.LastPlayerWeapons);   // live latch is now the bow

        // Finish the death confirmation on-field (2 more dead ticks to reach DeadNeeded=3).
        bool changed = t.Poll(true);    // deadStreak = 2
        Assert.False(changed);
        changed = t.Poll(true);         // deadStreak = 3 -> credit fires

        Assert.True(changed);
        // The STAMP (weapon 52, the actual killer) must be credited, not the re-latched bow.
        Assert.Equal(1, kills.GetValueOrDefault(52));
        Assert.False(kills.ContainsKey(90));
    }
}
