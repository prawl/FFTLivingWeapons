using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// Module-level coverage for the Provoke hold (LW-123 arc 2a) against FakeSparseMemory band seats:
/// the SLICE facade (the load-bearing behavior), the WINDOW fallback, the identical-enemy turn-count
/// guard, the transient-miss debounce, every release path, and the criteria the plan calls out by
/// number (3/3b/4/5/6/7/8/9/10/11/12/13/14/17). Pure-decision coverage lives in
/// ProvokeHoldPolicyTests.cs.
/// </summary>
public class ProvokeHoldTests
{
    private const int DefenderId = Provoke.DefenderId;   // 33
    /// <summary>Every test here constructs the hold through this factory, which opts INTO the
    /// feature (enabled: true) explicitly rather than leaning on the compiled default. The default
    /// is ON again in this build (Tuning.ProvokeEnabled, re-armed for the acceptance pass, commit
    /// 43de63e; see Provoke_is_armed_in_this_build), but that switch gates SHIPPING, not the logic:
    /// the arc 2a behavior below must stay fully covered whichever way the switch is thrown, so the
    /// suite would go quietly vacuous the moment it is next flipped off if these tests inherited the
    /// compiled default instead of pinning true.</summary>
    private static ProvokeHold Hold(Dictionary<int, int> kills, FakeSparseMemory m,
        bool? sliceMode = null, int? provokeTurns = null, bool enabled = true)
        => new ProvokeHold(kills, m, sliceMode, provokeTurns, enabled);

    private static Dictionary<int, int> Tier3Kills() => new() { [DefenderId] = 999 };

    private static void SeatBearer(FakeSparseMemory m, int rosterSlot, int bandIdx,
        int lvl = 30, int br = 50, int fa = 50, int gx = 1, int gy = 1)
    {
        MemSeats.SeatRoster(m, rosterSlot, lvl, br, fa, rh: DefenderId);
        MemSeats.SeatBand(m, bandIdx, DefenderId, lvl, br, fa, gx, gy);
        SetSide(m, bandIdx, enemy: false);
        // Stage the bearer's own Invisible byte writable (mirrors SeatAlly) so "the bearer is never
        // hidden" assertions actually bite: without this the fake silently refuses a wrongful write
        // and IsInvisible reads false for the wrong reason (a bearer-exclusion bug would go uncaught).
        StageInvisible(m, bandIdx);
    }

    private static void SeatAlly(FakeSparseMemory m, int bandIdx, int lvl, int br, int fa, int gx, int gy, int weapon = 5)
    {
        MemSeats.SeatBand(m, bandIdx, weapon, lvl, br, fa, gx, gy);
        SetSide(m, bandIdx, enemy: false);
        StageInvisible(m, bandIdx);
    }

    private static void SeatEnemy(FakeSparseMemory m, int bandIdx, int lvl, int br, int fa, int gx, int gy,
        bool marked = false, bool active = false, int nameId = 0)
    {
        MemSeats.SeatBand(m, bandIdx, weapon: 0, lvl, br, fa, gx, gy);
        SetSide(m, bandIdx, enemy: true);
        if (nameId != 0) MemSeats.SeatFrameNameId(m, bandIdx, nameId);
        // "active" now seats the engine's ACTOR POINTER on this seat during an enemy turn -- the
        // proven signal (LIVE_LEDGER "actor pointer names the acting unit" 2026-07-01) that replaces
        // the per-unit ATurnFlag byte, observed flaky live 2026-07-22 (hid 0 units one attempt,
        // missed the turn-done edge another -> 30s watchdog).
        if (active) PointActorAtEnemyTurn(m, bandIdx);
        if (marked) SetMark(m, bandIdx, true);
        // Stage the enemy's own Invisible byte writable (mirrors SeatAlly) so "the enemy side is
        // never touched" assertions actually bite: without this a broken player-side filter that
        // leaks an enemy into the hide list gets silently swallowed by the fake's write refusal.
        StageInvisible(m, bandIdx);
    }

    private static void SetSide(FakeSparseMemory m, int bandIdx, bool enemy) =>
        m.U8s[Band.Entry(bandIdx) + Offsets.AFriendFoe] = enemy ? Offsets.AFriendFoeEnemyBit : (byte)0;

    /// <summary>Point the engine's actor pointer (Offsets.ActorPtr) at bandIdx's combat frame -- the
    /// round-trip of Band.ActorEntry: frame = Band.Entry(bandIdx) - BandEntry, so ActorEntry resolves
    /// back to exactly Band.Entry(bandIdx). Mirrors IaiTests.PointActorAt. Only ONE unit can be the
    /// actor at a time (a single global pointer) -- last writer wins, which is fine since these tests
    /// seat at most one active enemy.
    ///
    /// LW-131: no longer writes TqTeam. TickArmed's turn gate now asks Band.FlagOwner whether a
    /// player-side seat owns the turn instead of reading the cursor/team field, and every fixture
    /// here defaults to nobody owning the turn flag, which is exactly the "no player-side seat"
    /// condition the fix treats as an enemy turn -- so no extra write is needed to express it.</summary>
    private static void PointActorAtEnemyTurn(FakeSparseMemory m, int bandIdx)
    {
        long frame = Band.Entry(bandIdx) - Offsets.BandEntry;
        m.SeedU64(Offsets.ActorPtr, (ulong)frame);
    }

    /// <summary>Clear the actor pointer so it resolves to nobody (Band.ActorEntry returns 0) --
    /// "this enemy's turn ends" for the actor-pointer signal.</summary>
    private static void ClearActor(FakeSparseMemory m) => m.SeedU64(Offsets.ActorPtr, 0);

    /// <summary>LW-131: seat the engine's per-unit turn flag (Offsets.ATurnFlag) on an already-seated
    /// band entry, expressing "it is this seat's turn" for TickArmed's PlayerSideOwnsTurn gate.
    /// Band.FlagOwner also requires Band.IsValid and a real (nonzero) AGx/AGy, which every Seat*
    /// helper in this file already provides, so no extra seating is needed here.</summary>
    private static void SetTurnFlagOwner(FakeSparseMemory m, int bandIdx) =>
        m.U8s[Band.Entry(bandIdx) + Offsets.ATurnFlag] = 1;

    private static void SetMark(FakeSparseMemory m, int bandIdx, bool on)
    {
        long e = Band.Entry(bandIdx);
        long composedAddr = e + StatusApply.Composed + StatusApply.StatusByte(ProvokeHold.MarkId);
        long inflictedAddr = e + StatusApply.Inflicted + StatusApply.StatusByte(ProvokeHold.MarkId);
        byte mask = StatusApply.StatusMask(ProvokeHold.MarkId);
        m.U8s[composedAddr] = (byte)(m.U8(composedAddr) | (on ? mask : (byte)0));
        m.U8s[inflictedAddr] = (byte)(m.U8(inflictedAddr) | (on ? mask : (byte)0));
        m.WritableAddrs.Add(composedAddr);
        m.WritableAddrs.Add(inflictedAddr);
    }

    private static void StageInvisible(FakeSparseMemory m, int bandIdx) =>
        m.WritableAddrs.Add(Band.Entry(bandIdx) + Offsets.AInvisible);

    private static bool IsInvisible(FakeSparseMemory m, int bandIdx) =>
        (m.U8(Band.Entry(bandIdx) + Offsets.AInvisible) & Offsets.AInvisibleBit) != 0;

    private static bool HasMark(FakeSparseMemory m, int bandIdx) =>
        (m.U8(Band.Entry(bandIdx) + StatusApply.Composed + StatusApply.StatusByte(ProvokeHold.MarkId))
            & StatusApply.StatusMask(ProvokeHold.MarkId)) != 0;

    /// <summary>The INFLICTED-layer twin of <see cref="HasMark"/> (LW-130: a correct clear must
    /// scrub both layers, not just the composed one ClearMark's own doc calls out).</summary>
    private static bool HasInflictedMark(FakeSparseMemory m, int bandIdx) =>
        (m.U8(Band.Entry(bandIdx) + StatusApply.Inflicted + StatusApply.StatusByte(ProvokeHold.MarkId))
            & StatusApply.StatusMask(ProvokeHold.MarkId)) != 0;

    // ---- THE SHIP SWITCH (LW-133): disabled means inert, not merely quiet ----

    /// <summary>The switch has to survive being turned back off, which is why these two tests pass
    /// enabled: false explicitly rather than leaning on the compiled default (2.3.2 shipped it off,
    /// LW-133; it is on again for the LW-123 acceptance pass). What it buys: this hold gates on the
    /// MARK BIT rather than on meta[33].Signature, so pulling the Defender's signature block alone
    /// would leave it live, and any enemy found carrying status id 0 would still hide the party for
    /// a player whose Defender reached tier 3 on kills alone. "Vanilla never sets status id 0" is
    /// an UNVERIFIED premise (the cast-nothing control battle that would settle it has not been
    /// run), so a release must be able to opt out of resting on it. With the switch off the hold
    /// must not arm, must hide nobody, and must not write at all.</summary>
    [Fact]
    public void Disabled_hold_never_arms_even_with_a_marked_enemy_and_a_tier3_bearer()
    {
        var m = new FakeSparseMemory();
        SeatBearer(m, rosterSlot: 0, bandIdx: 0);
        SeatAlly(m, bandIdx: 1, lvl: 25, br: 40, fa: 60, gx: 2, gy: 2);
        SeatEnemy(m, bandIdx: 10, lvl: 20, br: 30, fa: 30, gx: 5, gy: 5, marked: true, active: true);

        var hold = Hold(Tier3Kills(), m, enabled: false);
        hold.Tick(DateTime.UtcNow, inLive: true);

        Assert.False(IsInvisible(m, 1));    // the ally stays visible: nothing was hidden
        Assert.False(IsInvisible(m, 0));
        Assert.True(HasMark(m, 10));        // and the enemy's own mark is left alone, not scrubbed
    }

    /// <summary>The same switch must also silence the every-tick ScrubPlayerSideMarks write, which
    /// runs independently of hold state: with Provoke switched off the mod has no business clearing
    /// a status bit it did not set.</summary>
    [Fact]
    public void Disabled_hold_does_not_scrub_a_player_side_mark()
    {
        var m = new FakeSparseMemory();
        SeatBearer(m, rosterSlot: 0, bandIdx: 0);
        SeatAlly(m, bandIdx: 1, lvl: 25, br: 40, fa: 60, gx: 2, gy: 2);
        SetMark(m, 1, true);

        var hold = Hold(Tier3Kills(), m, enabled: false);
        hold.Tick(DateTime.UtcNow, inLive: true);

        Assert.True(HasMark(m, 1));
        Assert.True(HasInflictedMark(m, 1));
    }

    /// <summary>The compiled default is ON again, re-armed for the LW-123 acceptance pass after
    /// 2.3.2 shipped it off (LW-133). Pinned in BOTH directions on purpose: flipping this switch
    /// either way has to be a deliberate edit that turns this test red, never something that rides
    /// along on an unrelated change.</summary>
    [Fact]
    public void Provoke_is_armed_in_this_build()
        => Assert.True(Tuning.ProvokeEnabled);

    /// <summary>THE THREE-PART CONTRACT, mechanised. Arming Provoke is not one edit: the switch
    /// above turns the hold on, and the Defender's items.json signature block is what grants the
    /// command that plants the mark the hold reads. Either half alone is a broken build. Switch on
    /// with no signature block = a hold watching for a mark the player has no way to cast, which is
    /// live behaviour resting on the unverified "vanilla never sets status id 0" premise for no
    /// benefit at all; signature block with the switch off = a command that visibly does nothing.
    /// (The third part, the item.en.nxd card text, is gated by tools/audit_nxd_bakes.py instead:
    /// it is a baked binary, outside this suite's reach.)</summary>
    [Fact]
    public void The_baked_meta_signature_block_agrees_with_the_ship_switch()
    {
        var meta = MetaLoader.Load(RepoLivingWeaponDir());
        Assert.True(meta.ContainsKey(DefenderId), "meta.json has no Defender (id 33) row at all");
        var sig = meta[DefenderId].Signature;

        if (Tuning.ProvokeEnabled)
        {
            Assert.NotNull(sig);
            Assert.Equal(ProvokePolicy.ProvokeAbilityId, sig!.GrantCommandAbilityId);
            Assert.Equal(3, sig.AtTier);
        }
        else
        {
            Assert.Null(sig);
        }
    }

    /// <summary>Walk up from the test bin dir to the repo's LivingWeapon/ dir, the one holding the
    /// build-generated meta.json (same walk as MetaSchemaTests.RepoMetaPath).</summary>
    private static string RepoLivingWeaponDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "LivingWeapon", "meta.json");
            if (File.Exists(candidate)) return Path.GetDirectoryName(candidate)!;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("LivingWeapon/meta.json not found above the test bin dir");
    }

    // ---- THE LOAD-BEARING TEST (SLICE mode) ----

    [Fact]
    public void SliceMode_hides_everyone_but_the_bearer_while_the_marked_enemy_is_active()
    {
        var m = new FakeSparseMemory();
        SeatBearer(m, rosterSlot: 0, bandIdx: 0);
        SeatAlly(m, bandIdx: 1, lvl: 25, br: 40, fa: 60, gx: 2, gy: 2);
        SeatEnemy(m, bandIdx: 10, lvl: 20, br: 30, fa: 30, gx: 5, gy: 5, marked: true, active: true);

        var hold = Hold(Tier3Kills(), m);
        hold.Tick(DateTime.UtcNow, inLive: true);

        Assert.True(IsInvisible(m, 1));     // the other player unit is hidden
        Assert.False(IsInvisible(m, 0));    // the bearer itself is never hidden
        Assert.False(IsInvisible(m, 10));   // the enemy side is never touched
    }

    // ---- THE FACADE TEST (SLICE mode, the design pivot) ----

    [Fact]
    public void SliceMode_flags_nobody_when_a_different_enemy_is_active_not_the_marked_one()
    {
        var m = new FakeSparseMemory();
        SeatBearer(m, 0, 0);
        SeatAlly(m, 1, lvl: 25, br: 40, fa: 60, gx: 2, gy: 2);
        SeatEnemy(m, 10, lvl: 20, br: 30, fa: 30, gx: 5, gy: 5, marked: true, active: false, nameId: 500);
        SeatEnemy(m, 11, lvl: 22, br: 35, fa: 33, gx: 6, gy: 6, marked: false, active: true, nameId: 501);

        // Explicit sliceMode: true -- the shipped default is now WINDOW (Tuning.ProvokeSliceMode,
        // flipped 2026-07-22 after the slice turn-start race lost live), so this SLICE-specific
        // facade test opts in rather than relying on the default.
        var hold = Hold(Tier3Kills(), m, sliceMode: true);
        hold.Tick(DateTime.UtcNow, true);

        Assert.False(IsInvisible(m, 1));   // nobody hidden: the ACTIVE unit is not the marked one
    }

    // ---- WINDOW-mode load-bearing test ----

    [Fact]
    public void WindowMode_hides_on_any_enemy_turn_via_TqTeam()
    {
        var m = new FakeSparseMemory();
        SeatBearer(m, 0, 0);
        SeatAlly(m, 1, lvl: 25, br: 40, fa: 60, gx: 2, gy: 2);
        SeatEnemy(m, 10, lvl: 20, br: 30, fa: 30, gx: 5, gy: 5, marked: true, active: false);
        m.U16s[Offsets.TurnQueue + Offsets.TqMaxHp] = 100;
        m.U16s[Offsets.TurnQueue + Offsets.TqLevel] = 10;
        m.U16s[Offsets.TurnQueue + Offsets.TqTeam] = 1;

        var hold = Hold(Tier3Kills(), m, sliceMode: false);
        hold.Tick(DateTime.UtcNow, true);

        Assert.True(IsInvisible(m, 1));
        Assert.False(IsInvisible(m, 0));
        Assert.False(IsInvisible(m, 10));
    }

    // ---- Identical-enemy turn-count (guards the BLOCKER fix, decision 10) ----

    [Fact]
    public void Identical_enemy_twin_taking_its_turn_does_not_advance_the_marked_turn_count()
    {
        var m = new FakeSparseMemory();
        SeatBearer(m, 0, 0);
        SeatEnemy(m, 10, lvl: 20, br: 30, fa: 30, gx: 5, gy: 5, marked: true, active: false, nameId: 500);
        SeatEnemy(m, 11, lvl: 20, br: 30, fa: 30, gx: 6, gy: 6, marked: false, active: false, nameId: 501);   // fp-identical twin

        var hold = Hold(Tier3Kills(), m);   // default ProvokeTurns = 1
        var t0 = DateTime.UtcNow;
        hold.Tick(t0, true);   // arms (marked not active)

        PointActorAtEnemyTurn(m, 11);   // the TWIN becomes the actor on an enemy turn -- its fp
                                         // collides with the marked enemy, but nameId (501 vs 500) must not
        hold.Tick(t0.AddMilliseconds(33), true);
        ClearActor(m);   // the TWIN's turn ends -- must not count as the marked enemy's
        hold.Tick(t0.AddMilliseconds(66), true);

        // An identity match keyed on the fp alone (ignoring nameId) would have collided on the
        // twin, released at ProvokeTurns=1, and scrubbed the mark. Assert it did NOT.
        Assert.True(HasMark(m, 10));
    }

    // ---- Struck-victim park does not miscount (the PlayerSideOwnsTurn gate is load-bearing) ----
    //
    // Live testing 2026-07-22 proved the engine's actor pointer PARKS ON STRUCK VICTIMS: during a
    // PLAYER turn it can name the marked enemy the instant it gets hit, without that ever being the
    // enemy's own turn. LW-131 replaced the old "&& TqTeam==1" gate (suspected, not confirmed, of
    // reading the AI's targeting cursor rather than the turn owner -- docs/TODO.md LW-131) with
    // PlayerSideOwnsTurn (ProvokeHold.Scan.cs), which asks whether a player-side seat currently owns
    // the engine's per-unit turn flag. Dropping that gate makes THIS test fail the same way the old
    // one guarded against.

    [Fact]
    public void Actor_pointer_parked_on_the_marked_enemy_during_a_player_turn_does_not_count_as_its_turn()
    {
        var m = new FakeSparseMemory();
        SeatBearer(m, 0, 0);
        SeatAlly(m, 1, lvl: 25, br: 40, fa: 60, gx: 2, gy: 2);
        SeatEnemy(m, 10, lvl: 20, br: 30, fa: 30, gx: 5, gy: 5, marked: true, active: false, nameId: 500);

        var hold = Hold(Tier3Kills(), m);   // default ProvokeTurns = 1
        var t0 = DateTime.UtcNow;
        hold.Tick(t0, true);   // arms; nobody active yet

        SetTurnFlagOwner(m, 1);   // the ally owns the turn flag: this genuinely is a player turn
        // The actor pointer parks on the marked enemy -- it was just STRUCK mid-player-turn, not
        // taking its own turn.
        long frame = Band.Entry(10) - Offsets.BandEntry;
        m.SeedU64(Offsets.ActorPtr, (ulong)frame);
        hold.Tick(t0.AddMilliseconds(33), true);
        Assert.True(HasMark(m, 10));   // a player-turn park must not read as the enemy's own turn

        ClearActor(m);   // the pointer moves off it
        hold.Tick(t0.AddMilliseconds(66), true);

        Assert.True(HasMark(m, 10));   // no EnemyTurnDone release: _markedTurns never incremented
    }

    // ---- LW-131: the turn gate asks "does a player-side seat own the turn" (Band.FlagOwner via
    // PlayerSideOwnsTurn), not the cursor/team field. The working suspicion, not a settled fact
    // (docs/TODO.md LW-131), is that the AI's targeting cursor sits on the PLAYER unit an enemy is
    // attacking mid-action, so TurnQueue +0x02 reads PLAYER even though the enemy genuinely owns the
    // turn -- under that reading the old gate would never count that turn, and the hold would run
    // all the way to its thirty second watchdog. Nobody has hovered a player unit during an enemy
    // turn and read +0x02 to check; a LIVE_LEDGER row (docs/LIVE_LEDGER.md, Proven, dated
    // 2026-06-16) calls that field turn-stable, but its own experiment never covered that case, so
    // the evidence reads just as well the other way. The fix below does not depend on which reading
    // is right: PlayerSideOwnsTurn reads no cursor field at all, and this test pins the behaviour it
    // guarantees either way. docs/LIVE_LEDGER.md's Uncertain LW-87 flag-owner row (2026-07-21, not
    // owner-flipped) and docs/TODO.md's LW-131 flight-tape analysis are the evidence trail. ----

    [Fact]
    public void Enemy_turn_counts_even_when_the_cursor_team_field_reads_player_LW131()
    {
        var m = new FakeSparseMemory();
        SeatBearer(m, 0, 0);
        SeatEnemy(m, 10, lvl: 20, br: 30, fa: 30, gx: 5, gy: 5, marked: true, active: true, nameId: 500);
        // The suspected live symptom (docs/TODO.md LW-131, never directly observed): the AI's cursor
        // sits on the player unit it targets, so the cursor team field reads PLAYER even though this
        // genuinely is the marked enemy's own turn. This fixture has the marked enemy carry its OWN
        // turn flag -- one shape an enemy turn can take, not the only one Band.FlagOwner has to
        // handle: a zero-owner record is a real case too, per Band.FlagOwner's own doc comment (the
        // battle-opening edge) -- so this exercises PlayerSideOwnsTurn's "!IsEnemySide" half, not the
        // separate "nobody owns the flag" fail-open path (see the Nobody_owning_... test below).
        SetTurnFlagOwner(m, 10);
        m.U16s[Offsets.TurnQueue + Offsets.TqTeam] = 0;

        var hold = Hold(Tier3Kills(), m);   // default ProvokeTurns = 1
        var t0 = DateTime.UtcNow;
        hold.Tick(t0, true);   // arms; marked enemy is already the actor

        ClearActor(m);   // the marked enemy's turn ends
        hold.Tick(t0.AddMilliseconds(33), true);

        Assert.False(HasMark(m, 10));   // the turn WAS counted: released at ProvokeTurns=1, mark scrubbed
    }

    [Fact]
    public void Player_side_flag_owner_closes_the_gate_even_when_the_cursor_team_field_reads_enemy_LW131()
    {
        var m = new FakeSparseMemory();
        SeatBearer(m, 0, 0);
        SeatAlly(m, 1, lvl: 25, br: 40, fa: 60, gx: 2, gy: 2);
        SeatEnemy(m, 10, lvl: 20, br: 30, fa: 30, gx: 5, gy: 5, marked: true, active: false, nameId: 500);

        var hold = Hold(Tier3Kills(), m);   // default ProvokeTurns = 1
        var t0 = DateTime.UtcNow;
        hold.Tick(t0, true);   // arms

        SetTurnFlagOwner(m, 1);            // the ally owns the turn: this IS a player turn
        m.U16s[Offsets.TurnQueue + Offsets.TqTeam] = 1;   // pins that the team field no longer drives the decision
        long frame = Band.Entry(10) - Offsets.BandEntry;
        m.SeedU64(Offsets.ActorPtr, (ulong)frame);        // the marked enemy is the actor (struck, not its turn)
        hold.Tick(t0.AddMilliseconds(33), true);
        Assert.True(HasMark(m, 10));   // not counted: a player-side seat owns the turn

        ClearActor(m);
        hold.Tick(t0.AddMilliseconds(66), true);
        Assert.True(HasMark(m, 10));   // still not counted: no EnemyTurnDone edge ever fired
    }

    [Fact]
    public void Nobody_owning_the_turn_flag_fails_open_to_an_enemy_turn_by_design_LW131()
    {
        // PlayerSideOwnsTurn's documented FAIL-OPEN direction (ProvokeHold.Scan.cs): when
        // Band.FlagOwner finds nobody carrying the turn flag, the gate treats that as "not a player
        // turn" and opens, rather than refusing to count the turn. Deliberate, not an accident --
        // see the helper's doc comment for why that direction is safe.
        var m = new FakeSparseMemory();
        SeatBearer(m, 0, 0);
        SeatEnemy(m, 10, lvl: 20, br: 30, fa: 30, gx: 5, gy: 5, marked: true, active: true, nameId: 500);
        // Nobody seats a turn flag anywhere -- the fixture default.

        var hold = Hold(Tier3Kills(), m);   // default ProvokeTurns = 1
        var t0 = DateTime.UtcNow;
        hold.Tick(t0, true);   // arms; marked enemy already the actor

        ClearActor(m);
        hold.Tick(t0.AddMilliseconds(33), true);

        Assert.False(HasMark(m, 10));   // counted: fail-open let the turn through
    }

    [Fact]
    public void Enemy_side_flag_owner_does_not_close_the_gate_LW131()
    {
        var m = new FakeSparseMemory();
        SeatBearer(m, 0, 0);
        SeatEnemy(m, 10, lvl: 20, br: 30, fa: 30, gx: 5, gy: 5, marked: true, active: true, nameId: 500);
        SeatEnemy(m, 11, lvl: 22, br: 35, fa: 33, gx: 6, gy: 6, marked: false, active: false, nameId: 501);
        SetTurnFlagOwner(m, 11);   // a DIFFERENT enemy owns the turn flag, not a player-side seat

        var hold = Hold(Tier3Kills(), m);   // default ProvokeTurns = 1
        var t0 = DateTime.UtcNow;
        hold.Tick(t0, true);   // arms; marked enemy (10) already the actor

        ClearActor(m);
        hold.Tick(t0.AddMilliseconds(33), true);

        Assert.False(HasMark(m, 10));   // counted: an enemy-side flag owner is not a player turn
    }

    [Fact]
    public void Two_disagreeing_player_side_flag_owners_fail_open_by_design_LW131()
    {
        // PlayerSideOwnsTurn's OTHER documented fail-open path (ProvokeHold.Scan.cs): not just
        // "nobody owns the flag", but Band.FlagOwner (LivingWeapon/Band.cs) also bails AMBIGUOUS
        // when TWO player-side seats both carry ATurnFlag==1 with valid, real positions and
        // DIFFERENT identities. An ambiguous read is treated the same as "nobody owns it": the gate
        // opens rather than refusing to count the turn. Deliberate, not an accident -- see the
        // helper's doc comment.
        var m = new FakeSparseMemory();
        SeatBearer(m, 0, 0);
        SeatAlly(m, 1, lvl: 25, br: 40, fa: 60, gx: 2, gy: 2);
        SeatAlly(m, 2, lvl: 41, br: 12, fa: 77, gx: 3, gy: 3);   // different fingerprint, nameId 0 on both
        SeatEnemy(m, 10, lvl: 20, br: 30, fa: 30, gx: 5, gy: 5, marked: true, active: true, nameId: 500);
        SetTurnFlagOwner(m, 1);
        SetTurnFlagOwner(m, 2);   // two seats both claim the flag with disagreeing identities

        var hold = Hold(Tier3Kills(), m);   // default ProvokeTurns = 1
        var t0 = DateTime.UtcNow;
        hold.Tick(t0, true);   // arms; marked enemy already the actor

        ClearActor(m);
        hold.Tick(t0.AddMilliseconds(33), true);

        Assert.False(HasMark(m, 10));   // counted: an ambiguous flag read fails open, same as nobody owning it
    }

    // ---- Transient-miss non-release (guards the debounce) ----

    [Fact]
    public void Transient_locate_miss_does_not_release_before_the_debounce_is_exhausted()
    {
        var m = new FakeSparseMemory();
        SeatBearer(m, 0, 0);
        SeatAlly(m, 1, lvl: 25, br: 40, fa: 60, gx: 2, gy: 2);
        SeatEnemy(m, 10, lvl: 20, br: 30, fa: 30, gx: 5, gy: 5, marked: true, active: true, nameId: 500);

        var hold = Hold(Tier3Kills(), m);
        var t0 = DateTime.UtcNow;
        hold.Tick(t0, true);   // arms; marked active -> ally hidden
        Assert.True(IsInvisible(m, 1));

        long markedEntry = Band.Entry(10);
        // Band.IsValid now fails (gy>30) -> transiently unlocatable via the band-scan lookup used
        // for release/EnemyGone. The actor pointer is a stable global, not a scan, so it still
        // resolves this exact address and its identity fields (nameId/mhp/lvl/br/fa) are untouched.
        m.U8s[markedEntry + Offsets.AGy] = 99;

        for (int i = 1; i < Tuning.ProvokeMarkedMissTicks; i++)
            hold.Tick(t0.AddMilliseconds(33 * i), true);
        Assert.True(IsInvisible(m, 1));   // still under the debounce: armed, hide/reveal keeps running

        hold.Tick(t0.AddMilliseconds(33 * Tuning.ProvokeMarkedMissTicks), true);   // the Nth consecutive miss
        Assert.False(IsInvisible(m, 1));   // now released (EnemyGone)
    }

    // ---- SLICE reveal is automatic (criterion 5), not a release ----

    [Fact]
    public void SliceMode_reveals_the_instant_the_marked_enemy_stops_being_active_while_still_armed()
    {
        var m = new FakeSparseMemory();
        SeatBearer(m, 0, 0);
        SeatAlly(m, 1, lvl: 25, br: 40, fa: 60, gx: 2, gy: 2);
        SeatEnemy(m, 10, lvl: 20, br: 30, fa: 30, gx: 5, gy: 5, marked: true, active: true, nameId: 500);

        // provokeTurns=2 isolates SLICE's own reveal from the coincident EnemyTurnDone release the
        // shipped default of 1 would otherwise trigger on this exact edge. Explicit sliceMode: true
        // because the shipped default is now WINDOW (see the facade test above).
        var hold = Hold(Tier3Kills(), m, sliceMode: true, provokeTurns: 2);
        var t0 = DateTime.UtcNow;
        hold.Tick(t0, true);
        Assert.True(IsInvisible(m, 1));

        ClearActor(m);   // the marked enemy's own turn ends -- the actor pointer moves off it
        hold.Tick(t0.AddMilliseconds(33), true);

        Assert.False(IsInvisible(m, 1));   // revealed automatically -- no per-turn toggle needed
        Assert.True(HasMark(m, 10));       // still armed (1 of 2 turns) -- this was SLICE's reveal, not a release
    }

    // ---- WINDOW ActionFor wiring (LW-135: the turn flag, never the cursor/team field) ----

    [Fact]
    public void WindowMode_reveals_the_moment_a_player_side_seat_takes_the_turn()
    {
        var m = new FakeSparseMemory();
        SeatBearer(m, 0, 0);
        SeatAlly(m, 1, lvl: 25, br: 40, fa: 60, gx: 2, gy: 2);
        SeatEnemy(m, 10, lvl: 20, br: 30, fa: 30, gx: 5, gy: 5, marked: true, active: false, nameId: 500);

        var hold = Hold(Tier3Kills(), m, sliceMode: false);
        var t0 = DateTime.UtcNow;
        hold.Tick(t0, true);               // nobody owns the turn flag: the enemy phase
        Assert.True(IsInvisible(m, 1));

        SetTurnFlagOwner(m, 1);            // the ally's own turn opens
        hold.Tick(t0.AddMilliseconds(33), true);
        Assert.False(IsInvisible(m, 1));   // criterion 5: your units are targetable on your turns
    }

    /// <summary>Bias-to-hidden, restated for the flag walk: an ENEMY seat owning the turn flag, and
    /// an ambiguous walk where two seats disagree, must both leave the party hidden. Band.FlagOwner
    /// bails ambiguous on the second arrangement, which is the fail-open case ActionFor turns into
    /// Hide.</summary>
    [Fact]
    public void WindowMode_keeps_units_hidden_when_the_flag_is_enemy_owned_or_unreadable()
    {
        var m = new FakeSparseMemory();
        SeatBearer(m, 0, 0);
        SeatAlly(m, 1, lvl: 25, br: 40, fa: 60, gx: 2, gy: 2);
        SeatEnemy(m, 10, lvl: 20, br: 30, fa: 30, gx: 5, gy: 5, marked: true, active: false, nameId: 500);
        SetTurnFlagOwner(m, 10);   // the marked ENEMY owns the flag: unambiguously its turn

        var hold = Hold(Tier3Kills(), m, sliceMode: false);
        var t0 = DateTime.UtcNow;
        hold.Tick(t0, true);
        Assert.True(IsInvisible(m, 1));

        SetTurnFlagOwner(m, 1);    // now TWO seats claim it, one per side: ambiguous, fail open
        hold.Tick(t0.AddMilliseconds(33), true);
        Assert.True(IsInvisible(m, 1));   // stays hidden rather than leaking a hidden unit's turn
    }

    // ---- FeignDeath's own Invisible bit is never set or cleared by us (criterion 11) ----

    [Fact]
    public void A_unit_already_invisible_before_arm_is_never_set_and_never_cleared()
    {
        var m = new FakeSparseMemory();
        SeatBearer(m, 0, 0);
        SeatAlly(m, 1, lvl: 25, br: 40, fa: 60, gx: 2, gy: 2);
        long invisAddr = Band.Entry(1) + Offsets.AInvisible;
        m.U8s[invisAddr] = Offsets.AInvisibleBit;   // already invisible (FeignDeath's, say)
        SeatEnemy(m, 10, lvl: 20, br: 30, fa: 30, gx: 5, gy: 5, marked: true, active: true, nameId: 500);

        var hold = Hold(Tier3Kills(), m);   // default ProvokeTurns = 1
        var t0 = DateTime.UtcNow;
        hold.Tick(t0, true);   // arm + hide step: band1 already invisible -> skipped, never written
        Assert.False(m.Written.ContainsKey(invisAddr));

        ClearActor(m);   // marked enemy's turn ends (actor pointer moves off it) -> EnemyTurnDone release
        hold.Tick(t0.AddMilliseconds(33), true);

        Assert.False(m.Written.ContainsKey(invisAddr));   // release never touched it either
        Assert.True(IsInvisible(m, 1));                    // still invisible -- exactly as found
    }

    // ---- Reaching ProvokeTurns releases + clears our bits + scrubs the mark (criteria 3/3b) ----

    [Fact]
    public void Reaching_ProvokeTurns_releases_clears_our_bits_and_scrubs_the_mark_leaving_dead_bits_alone()
    {
        var m = new FakeSparseMemory();
        SeatBearer(m, 0, 0);
        SeatAlly(m, 1, lvl: 25, br: 40, fa: 60, gx: 2, gy: 2);
        SeatEnemy(m, 10, lvl: 20, br: 30, fa: 30, gx: 5, gy: 5, marked: true, active: true, nameId: 500);
        long composedByte = Band.Entry(10) + StatusApply.Composed;
        m.U8s[composedByte] = (byte)(m.U8(composedByte) | Offsets.AUndeadBit);   // pre-set on the SAME byte the mark shares

        var hold = Hold(Tier3Kills(), m);   // default ProvokeTurns = 1
        var t0 = DateTime.UtcNow;
        hold.Tick(t0, true);
        Assert.True(IsInvisible(m, 1));

        ClearActor(m);   // one turn-end edge (the actor pointer moves off the marked enemy) -> EnemyTurnDone
        hold.Tick(t0.AddMilliseconds(33), true);

        Assert.False(IsInvisible(m, 1));
        Assert.False(HasMark(m, 10));
        long inflictedByte = Band.Entry(10) + StatusApply.Inflicted;
        Assert.Equal(0, m.U8(inflictedByte) & StatusApply.StatusMask(ProvokeHold.MarkId));
        Assert.NotEqual(0, m.U8(composedByte) & Offsets.AUndeadBit);   // Dead/Undead untouched
    }

    // ---- Bearer safety releases (criteria 3 / 10) ----

    [Fact]
    public void Bearer_unequipping_the_defender_mid_hold_releases()
    {
        var m = new FakeSparseMemory();
        SeatBearer(m, 0, 0);
        SeatAlly(m, 1, lvl: 25, br: 40, fa: 60, gx: 2, gy: 2);
        SeatEnemy(m, 10, lvl: 20, br: 30, fa: 30, gx: 5, gy: 5, marked: true, active: true, nameId: 500);

        var hold = Hold(Tier3Kills(), m);
        var t0 = DateTime.UtcNow;
        hold.Tick(t0, true);
        Assert.True(IsInvisible(m, 1));

        m.U16s[Offsets.RosterBase + Offsets.RRHand] = 0xFFFF;   // roster slot 0 no longer wields the Defender
        hold.Tick(t0.AddMilliseconds(33), true);

        Assert.False(IsInvisible(m, 1));
        Assert.False(HasMark(m, 10));
    }

    [Fact]
    public void Bearer_dying_mid_hold_releases()
    {
        var m = new FakeSparseMemory();
        SeatBearer(m, 0, 0);
        SeatAlly(m, 1, lvl: 25, br: 40, fa: 60, gx: 2, gy: 2);
        SeatEnemy(m, 10, lvl: 20, br: 30, fa: 30, gx: 5, gy: 5, marked: true, active: true, nameId: 500);

        var hold = Hold(Tier3Kills(), m);
        var t0 = DateTime.UtcNow;
        hold.Tick(t0, true);
        Assert.True(IsInvisible(m, 1));

        m.U16s[Band.Entry(0) + Offsets.AHp] = 0;   // bearer's own band entry HP -> 0
        hold.Tick(t0.AddMilliseconds(33), true);

        Assert.False(IsInvisible(m, 1));
        Assert.False(HasMark(m, 10));
    }

    // ---- Enemy dies mid-hold releases (mark-clear attempted) ----

    [Fact]
    public void Enemy_dying_mid_hold_releases_and_attempts_the_mark_clear()
    {
        var m = new FakeSparseMemory();
        SeatBearer(m, 0, 0);
        SeatAlly(m, 1, lvl: 25, br: 40, fa: 60, gx: 2, gy: 2);
        SeatEnemy(m, 10, lvl: 20, br: 30, fa: 30, gx: 5, gy: 5, marked: true, active: true, nameId: 500);

        var hold = Hold(Tier3Kills(), m);
        var t0 = DateTime.UtcNow;
        hold.Tick(t0, true);
        Assert.True(IsInvisible(m, 1));

        m.U16s[Band.Entry(10) + Offsets.AHp] = 0;   // enemy HP hits 0, still otherwise locatable
        hold.Tick(t0.AddMilliseconds(33), true);

        Assert.False(IsInvisible(m, 1));
        Assert.False(HasMark(m, 10));
    }

    // ---- Ghost seat / guest side coverage (criteria 6/7) ----

    [Fact]
    public void Ghost_seat_with_a_real_position_is_never_flagged()
    {
        var m = new FakeSparseMemory();
        SeatBearer(m, 0, 0);
        SeatAlly(m, 1, lvl: 25, br: 40, fa: 60, gx: 3, gy: 3);
        SeatAlly(m, 2, lvl: 26, br: 41, fa: 61, gx: 4, gy: 4);
        m.U8s[Band.Entry(2) + Offsets.AGateByte] = Offsets.AGateHiddenValue;   // combat +0x01 == 0xFF
        SeatEnemy(m, 10, lvl: 20, br: 30, fa: 30, gx: 5, gy: 5, marked: true, active: true, nameId: 500);

        var hold = Hold(Tier3Kills(), m);
        hold.Tick(DateTime.UtcNow, true);

        Assert.True(IsInvisible(m, 1));
        Assert.False(IsInvisible(m, 2));
    }

    [Fact]
    public void Guest_outside_the_classic_seat_range_is_hidden_alongside_the_party()
    {
        var m = new FakeSparseMemory();
        SeatBearer(m, 0, 0);
        SeatAlly(m, 40, lvl: 28, br: 44, fa: 66, gx: 7, gy: 7);   // a "guest" far from the usual party range
        SeatEnemy(m, 10, lvl: 20, br: 30, fa: 30, gx: 5, gy: 5, marked: true, active: true, nameId: 500);

        var hold = Hold(Tier3Kills(), m);
        hold.Tick(DateTime.UtcNow, true);

        Assert.True(IsInvisible(m, 40));
    }

    // ---- Enter/exit sweep (criteria 12/13) ----

    [Fact]
    public void ResetBattle_reveals_every_ever_flagged_identity_and_scrubs_a_live_mark()
    {
        var m = new FakeSparseMemory();
        SeatBearer(m, 0, 0);
        SeatAlly(m, 1, lvl: 25, br: 40, fa: 60, gx: 2, gy: 2);
        SeatEnemy(m, 10, lvl: 20, br: 30, fa: 30, gx: 5, gy: 5, marked: true, active: true, nameId: 500);

        var hold = Hold(Tier3Kills(), m);
        hold.Tick(DateTime.UtcNow, true);
        Assert.True(IsInvisible(m, 1));

        // A stray player-side mark stranded right before the reset edge (LW-130 / AC 3c), seated
        // AFTER the tick above so ScrubPlayerSideMarks' Tick call site never touches it -- only
        // ResetBattle's own call site (the one the verifier deleted with the suite staying green)
        // can be what clears it here.
        SeatAlly(m, 2, lvl: 26, br: 41, fa: 61, gx: 3, gy: 3);
        SetMark(m, 2, true);

        hold.ResetBattle();

        Assert.False(IsInvisible(m, 1));
        Assert.False(HasMark(m, 10));
        Assert.False(HasMark(m, 2));            // composed layer scrubbed off the stray ally mark
        Assert.False(HasInflictedMark(m, 2));   // inflicted layer scrubbed too
    }

    // ---- Watchdog (criteria 14/17) ----

    [Fact]
    public void Watchdog_fires_after_the_unpaused_cap_with_no_other_reason_and_logs_distinctly()
    {
        var m = new FakeSparseMemory();
        SeatBearer(m, 0, 0);
        SeatAlly(m, 1, lvl: 25, br: 40, fa: 60, gx: 2, gy: 2);
        SeatEnemy(m, 10, lvl: 20, br: 30, fa: 30, gx: 5, gy: 5, marked: true, active: false, nameId: 500);

        var file = new List<string>();
        ModLogger.Instance = new FileConsoleLogger(_ => { }, file.Add) { LogLevel = LogLevel.Debug };
        try
        {
            var hold = Hold(Tier3Kills(), m);
            var t0 = DateTime.UtcNow;
            hold.Tick(t0, true);   // arms
            hold.Tick(t0.AddSeconds(Tuning.ProvokeWatchdogSeconds + 1), true);   // unpaused, cap exceeded
        }
        finally { ModLogger.UseNullLogger(); }

        Assert.False(HasMark(m, 10));
        Assert.Contains(file, l => l.Contains("WATCHDOG"));
    }

    [Fact]
    public void Watchdog_does_not_accrue_on_paused_ticks()
    {
        var m = new FakeSparseMemory();
        SeatBearer(m, 0, 0);
        SeatEnemy(m, 10, lvl: 20, br: 30, fa: 30, gx: 5, gy: 5, marked: true, active: false, nameId: 500);

        var hold = Hold(Tier3Kills(), m);
        var t0 = DateTime.UtcNow;
        hold.Tick(t0, true);   // arms

        m.U8s[Offsets.PauseFlag] = 1;   // paused
        hold.Tick(t0.AddSeconds(Tuning.ProvokeWatchdogSeconds + 1), true);

        Assert.True(HasMark(m, 10));   // still armed: the huge elapsed gap never accrued while paused
    }

    // ---- LW-135: WINDOW's hide/reveal must not ride the cursor/team field ----
    //
    // THE LIVE FAILURE, owner pass 2026-07-27: the hold armed on a goblin, saw its turn, and
    // released as EnemyTurnDone 1.6s later, all correct -- and hid NOBODY ("0 units were ever
    // hidden"), so the goblin attacked an ally exactly as if the mod were not installed. Turn
    // detection was never the problem; the HIDE was. WindowAction read Offsets.TqTeam, and during
    // an enemy's action the cursor sits on the player unit being targeted, so that field reads
    // PLAYER, ActionFor returned Reveal, and the party stayed visible for the whole enemy turn.
    //
    // Why the suite did not catch it: every other WINDOW fixture leaves the TurnQueue struct
    // unseeded, which reads as garbage, and ActionFor's bias-to-hidden turned garbage into Hide.
    // The one test that seeded a SANE queue seeded team=1 as well, which is the reading the live
    // game does not produce. This test seeds the arrangement that actually happens.

    [Fact]
    public void WindowMode_hides_during_an_enemy_turn_even_when_the_cursor_field_reads_player()
    {
        var m = new FakeSparseMemory();
        SeatBearer(m, 0, 0);
        SeatAlly(m, 1, lvl: 25, br: 40, fa: 60, gx: 2, gy: 2);
        SeatEnemy(m, 10, lvl: 20, br: 30, fa: 30, gx: 5, gy: 5, marked: true, active: true, nameId: 500);

        // A SANE condensed-TurnQueue read whose team says PLAYER: the cursor parked on the ally the
        // acting enemy is about to hit. No player-side seat owns the engine's per-unit turn flag,
        // which is what "it is genuinely an enemy's turn" looks like (SetTurnFlagOwner is not called).
        m.U16s[Offsets.TurnQueue + Offsets.TqMaxHp] = 100;
        m.U16s[Offsets.TurnQueue + Offsets.TqLevel] = 25;
        m.U16s[Offsets.TurnQueue + Offsets.TqTeam] = 0;

        var hold = Hold(Tier3Kills(), m, sliceMode: false, provokeTurns: 5);
        hold.Tick(DateTime.UtcNow, true);

        Assert.True(IsInvisible(m, 1));    // the ally is hidden: this is the whole feature
        Assert.False(IsInvisible(m, 0));   // the bearer never is
    }

    [Fact]
    public void WindowMode_reveals_on_the_players_own_turn_even_if_the_cursor_field_says_enemy()
    {
        var m = new FakeSparseMemory();
        SeatBearer(m, 0, 0);
        SeatAlly(m, 1, lvl: 25, br: 40, fa: 60, gx: 2, gy: 2);
        SeatEnemy(m, 10, lvl: 20, br: 30, fa: 30, gx: 5, gy: 5, marked: true, active: false, nameId: 500);
        SetTurnFlagOwner(m, 1);   // the ally owns the turn flag: it really is the player's turn
        m.U16s[Offsets.TurnQueue + Offsets.TqMaxHp] = 100;
        m.U16s[Offsets.TurnQueue + Offsets.TqLevel] = 20;
        m.U16s[Offsets.TurnQueue + Offsets.TqTeam] = 1;   // cursor hovering an enemy on your own turn

        var hold = Hold(Tier3Kills(), m, sliceMode: false, provokeTurns: 5);
        hold.Tick(DateTime.UtcNow, true);

        Assert.False(IsInvisible(m, 1));   // criterion 5: nobody is hidden on your own turn
    }

    // ---- LW-136: a mark that lands while the hold CANNOT arm must not strand the enemy ----
    //
    // The realistic way in is TWO DEPLOYED DEFENDERS. Provoke.cs grants the command off the FIRST
    // roster row holding id 33 in a main hand, while the hold resolves its bearer through
    // Wielder.ResolveDeployedMainHand, which returns 0 on two deployed wielders because the pair is
    // genuinely ambiguous. So the command is castable while the hold refuses to arm, and the mark
    // it plants never expires (Counter 0) and refuses every recast on that enemy at 0% for the rest
    // of the battle (docs/PROVOKE_AC.md, "the mark never expires"). The hold's own release path
    // cannot help: it never armed. Scrubbing on the same miss-tick debounce the marked-enemy locate
    // already uses is what keeps a transient bearer miss from eating a legitimate mark.

    /// <summary>Two deployed main-hand Defenders with distinct fingerprints, which is exactly the
    /// ambiguity ResolveDeployedMainHand bails on.</summary>
    private static void SeatTwoBearers(FakeSparseMemory m)
    {
        SeatBearer(m, rosterSlot: 0, bandIdx: 0);
        SeatBearer(m, rosterSlot: 1, bandIdx: 1, lvl: 31, br: 51, fa: 51, gx: 2, gy: 1);
    }

    [Fact]
    public void Two_deployed_defenders_cannot_arm_the_hold_and_must_not_strand_the_mark()
    {
        var m = new FakeSparseMemory();
        SeatTwoBearers(m);
        SeatEnemy(m, 10, lvl: 20, br: 30, fa: 30, gx: 5, gy: 5, marked: true, active: true, nameId: 500);

        var hold = Hold(Tier3Kills(), m);
        var t0 = DateTime.UtcNow;
        for (int i = 0; i <= Tuning.ProvokeMarkedMissTicks; i++)
            hold.Tick(t0.AddMilliseconds(33 * i), true);

        Assert.False(HasMark(m, 10));            // composed layer scrubbed: the enemy can be shouted at again
        Assert.False(HasInflictedMark(m, 10));   // inflicted layer too, same asymmetry ClearMark exists for
        Assert.False(IsInvisible(m, 0));         // and nothing was ever hidden: the hold genuinely never armed
    }

    [Fact]
    public void A_mark_is_not_scrubbed_before_the_debounce_in_case_the_bearer_read_was_transient()
    {
        var m = new FakeSparseMemory();
        SeatTwoBearers(m);
        SeatEnemy(m, 10, lvl: 20, br: 30, fa: 30, gx: 5, gy: 5, marked: true, active: true, nameId: 500);

        var hold = Hold(Tier3Kills(), m);
        var t0 = DateTime.UtcNow;
        for (int i = 0; i < Tuning.ProvokeMarkedMissTicks - 1; i++)
            hold.Tick(t0.AddMilliseconds(33 * i), true);

        Assert.True(HasMark(m, 10));   // still there: a one-tick bearer miss must not eat a real cast
    }

    [Fact]
    public void A_mark_that_can_arm_is_never_scrubbed_by_the_unarmable_sweep()
    {
        var m = new FakeSparseMemory();
        SeatBearer(m, 0, 0);   // exactly ONE bearer: the hold arms normally
        SeatAlly(m, 1, lvl: 25, br: 40, fa: 60, gx: 2, gy: 2);
        SeatEnemy(m, 10, lvl: 20, br: 30, fa: 30, gx: 5, gy: 5, marked: true, active: true, nameId: 500);

        var hold = Hold(Tier3Kills(), m, provokeTurns: 5);   // stays armed across every tick below
        var t0 = DateTime.UtcNow;
        for (int i = 0; i <= Tuning.ProvokeMarkedMissTicks; i++)
            hold.Tick(t0.AddMilliseconds(33 * i), true);

        Assert.True(HasMark(m, 10));     // the armed hold owns the mark; only its own release clears it
        Assert.True(IsInvisible(m, 1));
    }

    // ---- Criterion 15: arm, release AND the watchdog each name the provoked enemy's TILE ----
    //
    // Why these exist at all: nothing asserted the CONTENT of any arm or release line, so the
    // release lines shipped naming only the reason and the unit count while the arm line named the
    // tile. Reading a live log then meant guessing which enemy a release belonged to (the arm line
    // is the only other place the tile appears, and a battle can arm more than once). The count
    // half of the criterion is already carried by the "N units" text these lines have always had.

    private const string ExpectedTile = "tile 5,5";   // every fixture below seats the marked enemy at gx=5, gy=5

    [Fact]
    public void The_arm_line_names_the_provoked_enemys_tile()
    {
        var m = new FakeSparseMemory();
        SeatBearer(m, 0, 0);
        SeatEnemy(m, 10, lvl: 20, br: 30, fa: 30, gx: 5, gy: 5, marked: true, active: false, nameId: 500);

        var file = new List<string>();
        ModLogger.Instance = new FileConsoleLogger(_ => { }, file.Add) { LogLevel = LogLevel.Debug };
        try
        {
            Hold(Tier3Kills(), m).Tick(DateTime.UtcNow, true);   // arms
        }
        finally { ModLogger.UseNullLogger(); }

        Assert.Contains(file, l => l.Contains("provoke hold arm") && l.Contains(ExpectedTile));
    }

    [Fact]
    public void A_normal_release_line_names_the_provoked_enemys_tile()
    {
        var m = new FakeSparseMemory();
        SeatBearer(m, 0, 0);
        SeatAlly(m, 1, lvl: 25, br: 40, fa: 60, gx: 2, gy: 2);
        SeatEnemy(m, 10, lvl: 20, br: 30, fa: 30, gx: 5, gy: 5, marked: true, active: true, nameId: 500);

        var file = new List<string>();
        ModLogger.Instance = new FileConsoleLogger(_ => { }, file.Add) { LogLevel = LogLevel.Debug };
        try
        {
            var hold = Hold(Tier3Kills(), m);   // default ProvokeTurns = 1
            var t0 = DateTime.UtcNow;
            hold.Tick(t0, true);        // arms with the marked enemy acting
            ClearActor(m);              // its turn ends
            hold.Tick(t0.AddMilliseconds(33), true);   // releases EnemyTurnDone
        }
        finally { ModLogger.UseNullLogger(); }

        Assert.Contains(file, l => l.Contains("release reason=EnemyTurnDone") && l.Contains(ExpectedTile));
    }

    [Fact]
    public void The_watchdog_release_line_names_the_provoked_enemys_tile()
    {
        var m = new FakeSparseMemory();
        SeatBearer(m, 0, 0);
        SeatEnemy(m, 10, lvl: 20, br: 30, fa: 30, gx: 5, gy: 5, marked: true, active: false, nameId: 500);

        var file = new List<string>();
        ModLogger.Instance = new FileConsoleLogger(_ => { }, file.Add) { LogLevel = LogLevel.Debug };
        try
        {
            var hold = Hold(Tier3Kills(), m);
            var t0 = DateTime.UtcNow;
            hold.Tick(t0, true);   // arms
            hold.Tick(t0.AddSeconds(Tuning.ProvokeWatchdogSeconds + 1), true);
        }
        finally { ModLogger.UseNullLogger(); }

        Assert.Contains(file, l => l.Contains("WATCHDOG") && l.Contains(ExpectedTile));
    }

    /// <summary>The case that decides the implementation: an EnemyGone release has NO band entry
    /// left to read a tile off, so the line has to name the LAST KNOWN tile rather than skip it or
    /// print zeroes. This fixture corrupts the marked enemy's own gy to 99 to make it unlocatable
    /// (Transient_locate_miss_does_not_release_before_the_debounce_is_exhausted's trick), which also
    /// means a naive re-read at release time would print the corrupt 5,99 instead of 5,5.</summary>
    [Fact]
    public void A_release_after_the_marked_enemy_vanishes_still_names_its_last_known_tile()
    {
        var m = new FakeSparseMemory();
        SeatBearer(m, 0, 0);
        SeatEnemy(m, 10, lvl: 20, br: 30, fa: 30, gx: 5, gy: 5, marked: true, active: true, nameId: 500);

        var file = new List<string>();
        ModLogger.Instance = new FileConsoleLogger(_ => { }, file.Add) { LogLevel = LogLevel.Debug };
        try
        {
            var hold = Hold(Tier3Kills(), m);
            var t0 = DateTime.UtcNow;
            hold.Tick(t0, true);   // arms at 5,5
            m.U8s[Band.Entry(10) + Offsets.AGy] = 99;   // now unlocatable by the band scan
            for (int i = 1; i <= Tuning.ProvokeMarkedMissTicks; i++)
                hold.Tick(t0.AddMilliseconds(33 * i), true);
        }
        finally { ModLogger.UseNullLogger(); }

        Assert.Contains(file, l => l.Contains("release reason=EnemyGone") && l.Contains(ExpectedTile));
    }

    // ---- A refused guarded write logs distinctly (criterion 17) ----

    [Fact]
    public void A_refused_hide_write_writes_nothing_and_logs_distinctly()
    {
        var m = new FakeSparseMemory();
        SeatBearer(m, 0, 0);
        MemSeats.SeatBand(m, 1, weapon: 5, lvl: 25, br: 40, fa: 60, gx: 2, gy: 2);
        SetSide(m, 1, enemy: false);   // valid ally, but its Invisible address is deliberately NOT Writable
        SeatEnemy(m, 10, lvl: 20, br: 30, fa: 30, gx: 5, gy: 5, marked: true, active: true, nameId: 500);

        var file = new List<string>();
        ModLogger.Instance = new FileConsoleLogger(_ => { }, file.Add) { LogLevel = LogLevel.Debug };
        try
        {
            var hold = Hold(Tier3Kills(), m);
            hold.Tick(DateTime.UtcNow, true);
        }
        finally { ModLogger.UseNullLogger(); }

        Assert.Empty(m.Written);
        Assert.Contains(file, l => l.Contains("refused"));
    }

    // ---- LW-130: scrub a mark a player accidentally cast on their own side (docs/PROVOKE_AC.md
    // criterion 3c). ScrubPlayerSideMarks runs every live tick regardless of the hold's own
    // Idle/Armed state, because a player can provoke an ally while a hold on some other enemy is
    // already up (or with no enemy marked at all). ----

    // ---- THE LOAD-BEARING TEST ----

    [Fact]
    public void A_mark_on_a_player_side_seat_is_scrubbed_on_both_layers_while_an_enemys_mark_survives_and_still_arms()
    {
        var m = new FakeSparseMemory();
        SeatBearer(m, 0, 0);
        SeatAlly(m, 1, lvl: 25, br: 40, fa: 60, gx: 2, gy: 2);
        SetMark(m, 1, true);   // the player cast Provoke at their own ally by mistake
        SeatAlly(m, 2, lvl: 26, br: 41, fa: 61, gx: 3, gy: 3);   // hidden-when-armed control seat
        SeatEnemy(m, 10, lvl: 20, br: 30, fa: 30, gx: 5, gy: 5, marked: true, active: true, nameId: 500);

        var hold = Hold(Tier3Kills(), m);
        hold.Tick(DateTime.UtcNow, true);

        Assert.False(HasMark(m, 1));            // composed layer scrubbed off the ally
        Assert.False(HasInflictedMark(m, 1));   // inflicted layer scrubbed too
        Assert.True(HasMark(m, 10));            // the enemy's own mark is untouched
        Assert.True(IsInvisible(m, 2));         // the enemy mark still arms the hold normally
    }

    // ---- MASK DISCIPLINE: neighbouring bits on the shared composed/inflicted bytes survive ----

    [Fact]
    public void Scrubbing_a_player_side_mark_touches_only_the_mark_bit_leaving_neighbouring_status_bits_alone()
    {
        var m = new FakeSparseMemory();
        SeatBearer(m, 0, 0);
        SeatAlly(m, 1, lvl: 25, br: 40, fa: 60, gx: 2, gy: 2);
        SetMark(m, 1, true);

        long composedByte = Band.Entry(1) + StatusApply.Composed;
        long inflictedByte = Band.Entry(1) + StatusApply.Inflicted;
        // Composed +0x45 is the SAME byte Dead/Undead/Jump/Charging live on (KillTracker reads
        // it), so a correct clear must leave every one of these set exactly as found.
        m.U8s[composedByte] = (byte)(m.U8(composedByte) | Offsets.AUndeadBit | Offsets.AJumpBit | Offsets.AChargingBit);
        // An unrelated inflicted bit (status id 7, mask 0x01) must also survive untouched.
        byte unrelatedInflictedMask = StatusApply.StatusMask(7);
        m.U8s[inflictedByte] = (byte)(m.U8(inflictedByte) | unrelatedInflictedMask);
        m.WritableAddrs.Add(composedByte);
        m.WritableAddrs.Add(inflictedByte);
        byte composedBefore = m.U8(composedByte);
        byte inflictedBefore = m.U8(inflictedByte);

        var hold = Hold(Tier3Kills(), m);
        hold.Tick(DateTime.UtcNow, true);

        byte mask = StatusApply.StatusMask(ProvokeHold.MarkId);
        Assert.Equal(composedBefore & ~mask, m.U8(composedByte) & ~mask);   // every OTHER composed bit unchanged
        Assert.Equal(0, m.U8(composedByte) & mask);                          // only the mark bit cleared
        Assert.Equal(inflictedBefore & ~mask, m.U8(inflictedByte) & ~mask); // every OTHER inflicted bit unchanged
        Assert.Equal(0, m.U8(inflictedByte) & mask);
    }

    // ---- SCRUB WHILE ARMED: a friendly mark landing mid-hold is cleared without disturbing the
    // hold's own armed state on a different (enemy) mark ----

    [Fact]
    public void A_player_side_mark_cast_mid_hold_is_scrubbed_while_the_hold_stays_armed_on_its_own_enemy()
    {
        var m = new FakeSparseMemory();
        SeatBearer(m, 0, 0);
        SeatAlly(m, 2, lvl: 26, br: 41, fa: 61, gx: 3, gy: 3);   // hidden-when-armed control seat
        SeatEnemy(m, 10, lvl: 20, br: 30, fa: 30, gx: 5, gy: 5, marked: true, active: true, nameId: 500);

        var hold = Hold(Tier3Kills(), m);
        var t0 = DateTime.UtcNow;
        hold.Tick(t0, true);   // arms on the enemy
        Assert.True(IsInvisible(m, 2));

        SeatAlly(m, 1, lvl: 25, br: 40, fa: 60, gx: 2, gy: 2);
        SetMark(m, 1, true);   // a friendly cast lands mid-hold
        hold.Tick(t0.AddMilliseconds(33), true);

        Assert.False(HasMark(m, 1));      // scrubbed off the ally
        Assert.True(HasMark(m, 10));      // the armed hold's own mark is untouched
        Assert.True(IsInvisible(m, 2));   // still armed: no release, no state reset
    }

    // ---- OFF-FIELD / INVALID SEATS SKIPPED (criterion 7's own gate, reused here) ----

    [Fact]
    public void ScrubPlayerSideMarks_never_writes_to_an_offfield_or_invalid_seat()
    {
        var m = new FakeSparseMemory();
        SeatBearer(m, 0, 0);
        SeatAlly(m, 1, lvl: 25, br: 40, fa: 60, gx: 3, gy: 3);
        SetMark(m, 1, true);
        m.U8s[Band.Entry(1) + Offsets.AGateByte] = Offsets.AGateHiddenValue;   // combat +0x01 == 0xFF

        // bandIdx 2 is never seated at all (lvl/br/fa/mhp all read 0) -> Band.IsValid is false.
        SetMark(m, 2, true);

        var hold = Hold(Tier3Kills(), m);
        hold.Tick(DateTime.UtcNow, true);

        Assert.True(HasMark(m, 1));   // off-field: skipped, mark survives untouched
        Assert.True(HasMark(m, 2));   // invalid seat: skipped, mark survives untouched
        Assert.False(m.Written.ContainsKey(Band.Entry(1) + StatusApply.Composed));
        Assert.False(m.Written.ContainsKey(Band.Entry(2) + StatusApply.Composed));
    }

    // ---- NO-OP TICK: nobody player-side marked -> zero writes to any composed status byte ----

    [Fact]
    public void A_tick_with_no_player_side_mark_writes_nothing_to_any_composed_status_byte()
    {
        var m = new FakeSparseMemory();
        SeatBearer(m, 0, 0);
        SeatAlly(m, 1, lvl: 25, br: 40, fa: 60, gx: 2, gy: 2);
        SeatEnemy(m, 10, lvl: 20, br: 30, fa: 30, gx: 5, gy: 5, marked: true, active: true, nameId: 500);

        var hold = Hold(Tier3Kills(), m);
        hold.Tick(DateTime.UtcNow, true);

        Assert.False(m.Written.ContainsKey(Band.Entry(0) + StatusApply.Composed));
        Assert.False(m.Written.ContainsKey(Band.Entry(1) + StatusApply.Composed));
    }

    // ---- NOT IN LIVE BATTLE: a false inLive scrubs nothing ----

    [Fact]
    public void ScrubPlayerSideMarks_does_nothing_when_not_in_a_live_battle()
    {
        var m = new FakeSparseMemory();
        SeatBearer(m, 0, 0);
        SeatAlly(m, 1, lvl: 25, br: 40, fa: 60, gx: 2, gy: 2);
        SetMark(m, 1, true);

        var hold = Hold(Tier3Kills(), m);
        hold.Tick(DateTime.UtcNow, false);

        Assert.True(HasMark(m, 1));
    }

    // ---- THE BEARER: included, not exempt ----

    [Fact]
    public void A_mark_on_the_bearer_itself_is_scrubbed_too()
    {
        var m = new FakeSparseMemory();
        SeatBearer(m, 0, 0);
        SetMark(m, 0, true);

        var hold = Hold(Tier3Kills(), m);
        hold.Tick(DateTime.UtcNow, true);

        Assert.False(HasMark(m, 0));
        Assert.False(HasInflictedMark(m, 0));
    }
}
