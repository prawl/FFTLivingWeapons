using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-152: four tests closing verified-test-blind corners the LW-150 S3 (ActedPeriodLatch
/// mirror-outputs)/S4 (KillTracker.Corpses.cs dispatcher) splits opened -- each pinned against a
/// SPECIFIC named mutation the existing suite did not catch (docs/TODO.md LW-152). Local helpers
/// mirror KillTrackerTests' private statics (SetActive/SetRoster/SetUnit/SetEnemy/Settle) rather
/// than reusing them, matching this suite's existing per-file convention (e.g.
/// KillTrackerBattleCountersTests.cs's own Make/MakeGated helpers) -- every file that drives
/// KillTracker.Poll keeps its own thin wrapper over MemSeats/Offsets.
/// </summary>
public class KillCreditCoverageTests
{
    private static void SetActive(FakeSparseMemory m, int hp, int maxHp, int level, int team = 0, int acted = 1)
    {
        m.U16s[Offsets.TurnQueue + Offsets.TqTeam] = (ushort)team;
        m.U16s[Offsets.TurnQueue + Offsets.TqHp] = (ushort)hp;
        m.U16s[Offsets.TurnQueue + Offsets.TqMaxHp] = (ushort)maxHp;
        m.U16s[Offsets.TurnQueue + Offsets.TqLevel] = (ushort)level;
        m.U8s[Offsets.Acted] = (byte)acted;
    }

    private static void SetUnit(FakeSparseMemory m, int slot, int hp, int maxHp = 400, int gx = 5, int gy = 5,
                                int level = 10, int brave = 50, int faith = 50, int weapon = 0)
        => MemSeats.SeatBand(m, slot, weapon: weapon, lvl: level, br: brave, fa: faith,
                             gx: gx, gy: gy, hp: hp, maxHp: maxHp);

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

    private static void SetEnemy(FakeSparseMemory m, int slot, int hp, int maxHp = 400, int gx = 5, int gy = 5,
                                 int level = 10, int brave = 50, int faith = 50)
    {
        SetUnit(m, slot, hp, maxHp, gx, gy, level, brave, faith);
        if (slot <= Offsets.EnemySlotMax)
            SetArrayEnemy(m, slot, level, brave, faith, maxHp);
    }

    private static void SetRoster(FakeSparseMemory m, int slot, int level, int brave, int faith, int weapon,
                                  int nameId = 0)
        => MemSeats.SeatRoster(m, slot, level, brave, faith, weapon, nameId: nameId);

    private static void PointAt(FakeSparseMemory m, int bandIdx) =>
        m.SeedU64(Offsets.ActorPtr, (ulong)(Offsets.FrameReadBase + (long)bandIdx * Offsets.CombatStride));

    private static void SetFrameNameId(FakeSparseMemory m, int bandIdx, int nameId) =>
        MemSeats.SeatFrameNameId(m, bandIdx, nameId);

    private const int Wilham = Offsets.SlotsBack;   // band slot 20 (player-side actor)

    private static readonly HashSet<int> Weapons = new() { 52 };

    private static void Settle(KillTracker t, int n = 3) { for (int i = 0; i < n; i++) t.Poll(true); }

    // ------------------------------------------------------------------------------------------
    // Test 1: the S4 dispatcher's deliberate orphan fall-through, with NO culprit stamped at all.
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void Orphan_fallthrough_with_no_culprit_stamped_goes_pending_not_dropped()
    {
        // S4 (LW-150) fall-through pin: HandleOrphanAliveEdge's final `return true`
        // (KillTracker.Corpses.cs) is a DELIBERATE fall-through into the shared ResolveCredit
        // machinery for the one case none of the existing LW-65 orphan tests drive: the orphan
        // repairs the belt but finds NO culprit stamped at all -- (_lethalActor[s] ??
        // _lastPlayerWeapons).Count == 0 AND _lethalUntracked[s] == UntrackedReason.None. Every
        // existing orphan test either has a live latch backing the corpse (credits immediately) or
        // a stamped untracked verdict (buries immediately); this one has neither, because nobody
        // ever latches an actor this battle at all (acted never rises, so _periodOpen stays false
        // and StampCulpritFromHypothesisOnly's period gate bails without stamping anything).
        //
        // MEASURED current behavior (run this scenario against today's code before trusting the
        // asserts below): the fall-through lands in ResolveCredit's own empty-culprit branch, which
        // is the ordinary "corpse fell, waiting to identify the killer" PENDING path -- the same
        // outcome a same-shaped non-orphan death would produce. Not a credit, not a bury: a pending
        // corpse that survives to be resolved later, not silently dropped.
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        var recorded = new List<(string type, string payload)>();
        var t = new KillTracker(kills, m, Weapons, recorder: (ty, p) => recorded.Add((ty, p)));
        // Deliberately no SetRoster/SetActive: nobody ever latches, so _lastPlayerWeapons stays
        // empty and _periodOpen stays false for the whole test.

        // Alive-mark under maxHp=107 (mirrors KillTrackerTests' LW65 orphan fixtures).
        SetEnemy(m, slot: 0, hp: 300, maxHp: 107, level: 20, brave: 20, faith: 20);
        Settle(t, 3);   // seenAlive -> belt[(20,20,20,107)] = true

        // Drift: same identity (lvl/br/fa unchanged), maxHp now 71 -- seenAlive/slotId untouched.
        SetUnit(m, slot: 0, hp: 300, maxHp: 71, level: 20, brave: 20, faith: 20);
        SetArrayEnemy(m, slot: 0, level: 20, brave: 20, faith: 20, maxHp: 71);   // oracle tracks (20,20,20,71) too

        // Death under the drifted maxHp -- the belt has no entry for (20,20,20,71): the ABSENT orphan path.
        SetUnit(m, slot: 0, hp: 0, maxHp: 71, level: 20, brave: 20, faith: 20);
        Settle(t, 3);

        Assert.Empty(kills);   // nobody was ever latched -- no credit possible
        Assert.Contains(recorded, r => r.type == "kill" && r.payload == "orphan-alive-edge slot=0 mhp=71");
        Assert.Contains(recorded, r => r.type == "kill" && r.payload == "pending slot=0 at=(5,5) status=0");
        Assert.True(t._pending[0]);   // the corpse survives the fall-through -- not silently dropped
    }

    // ------------------------------------------------------------------------------------------
    // Test 2: ApplyLatchOutputs' AnyTrackedWeaponThisBattle copy-back (S3, LW-150).
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void AnyTrackedWeaponThisBattle_copies_back_onto_the_tracker_when_a_tracked_weapon_latches()
    {
        // ActedPeriodLatch.Step sets ActedPeriodOutputs.AnyTrackedWeaponThisBattle the moment a
        // resolved actor holds a tracked weapon (ActedPeriodLatch.cs line ~122); KillTracker's own
        // AnyTrackedWeaponThisBattle property (the logging facelift's armed-gate, read by EnemyOracle
        // and the [kill] console gate) only ever changes via ApplyLatchOutputs' copy-back. Every
        // existing pin of this property (KillTrackerBattleCountersTests.The_armed_gate_starts_quiet_
        // and_resets_per_battle; ActedPeriodLatchTests' several AnyTrackedWeaponThisBattle asserts)
        // reads either the false-at-rest default or the ActedPeriodOutputs carrier directly -- never
        // KillTracker's own property AFTER a real Poll-driven latch. This drives a real latch through
        // KillTracker.Poll and reads the tracker's own property.
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        SetRoster(m, slot: 3, level: 99, brave: 89, faith: 76, weapon: 52);
        SetUnit(m, Wilham, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 1);
        var t = new KillTracker(kills, m, Weapons);

        Assert.False(t.AnyTrackedWeaponThisBattle);

        Settle(t);   // latches weapon 52 (a tracked weapon) via the acted-period latch

        Assert.True(t.AnyTrackedWeaponThisBattle);
    }

    // ------------------------------------------------------------------------------------------
    // Test 3: ApplyLatchOutputs' _latchViaFallback copy-back (S3, LW-150), via FirstKillFallback.
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void LatchViaFallback_copies_back_and_feeds_FallbackCredits_through_the_first_kill_fallback()
    {
        // Exact fixture shape of KillTrackerTests.FirstKillFallback_falls_back_to_pure_TQ_outside_
        // any_period (acted NEVER rises this battle, so the register gate is closed by construction
        // and the first-kill fallback resolves via the pure TQ walk after a 3-tick stability streak).
        // That existing test only pins the credited weapon id; it never reads _latchViaFallback or
        // FallbackCredits, so the mirror-outputs copy-back of LatchViaFallback specifically
        // (ActedPeriodLatch.cs's FirstKillFallback sets ActedPeriodOutputs.LatchViaFallback = true;
        // KillTracker.ApplyLatchOutputs must copy it onto _latchViaFallback for ResolveCredit's
        // `_lethalActor[s] != null ? _lethalViaFallback[s] : _latchViaFallback` read to see it) is
        // untested. KillTrackerBattleCountersTests.FallbackCredits_counts_only_credits_flagged_
        // viaFallback drives CreditKill directly with an explicit viaFallback argument, bypassing the
        // latch/Poll pipeline entirely -- it cannot catch a dropped copy-back either.
        const int wa = 52;
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        SetRoster(m, slot: 3, level: 99, brave: 89, faith: 76, weapon: wa, nameId: 501);
        SetUnit(m, Wilham, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 0);   // actor present, acted NEVER set to 1
        var t = new KillTracker(kills, m, Weapons);

        t.Poll(true);
        SetFrameNameId(m, Wilham, 501);
        PointAt(m, Wilham);
        t.Poll(true);   // register is stable+bridged from here on, but acted never rises -> no period

        SetEnemy(m, slot: 0, hp: 300);
        Settle(t, 3);
        SetUnit(m, slot: 0, hp: 0);
        Assert.False(t.Poll(true));   // dead tick 1
        Assert.False(t.Poll(true));   // dead tick 2
        Assert.False(t.Poll(true));   // dead tick 3 -> pending
        Assert.False(t.Poll(true));   // fallback streak building
        Assert.False(t.Poll(true));   // fallback streak reaches threshold -> latch accepted (via TQ)

        bool credited = t.Poll(true);

        Assert.True(credited);
        Assert.Equal(1, kills.GetValueOrDefault(wa));
        Assert.True(t._latchViaFallback);       // the copied-back field itself
        Assert.Equal(1, t.FallbackCredits);     // the downstream observable CreditKill's viaFallback feeds
    }

    // ------------------------------------------------------------------------------------------
    // Test 4: ApplyLatchOutputs' _actorTag copy-back (S3, LW-150), observed via BattleLog.
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void ActorTag_copies_back_and_reaches_the_battle_log_weapon_tag()
    {
        // _actorTag (KillTracker.cs) has exactly one downstream reader: ScanFieldEvidence
        // (KillTracker.Corpses.cs) passes it into BattleLog.Observe as the "(weapons: ...)" suffix
        // on a damage/heal event line. No existing test drives a real Poll-based latch AND an
        // observed HP change on the same tracker with events wired -- KillTrackerTests.Kill_diag_
        // line_emitted_under_verbose wires BattleLog but only pins the AREC diagnostic line, and
        // BattleLogTests drives BattleLog.Observe directly with a literal actor string, never through
        // KillTracker's own latch/copy-back. This drives both: latch a tracked weapon through Poll,
        // then observe a live enemy's HP drop and read the actor tag off the emitted log line.
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        var lines = new List<string>();
        var events = new BattleLog(true, lines.Add);
        SetRoster(m, slot: 3, level: 99, brave: 89, faith: 76, weapon: 52);
        SetUnit(m, Wilham, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetActive(m, hp: 352, maxHp: 352, level: 99, acted: 1);
        var t = new KillTracker(kills, m, Weapons, events);

        Settle(t);   // latches weapon 52 -> _actorTag == "52"

        SetEnemy(m, slot: 0, hp: 300);
        t.Poll(true);   // first sighting of slot 0 -- BattleLog baselines silently

        SetUnit(m, slot: 0, hp: 250);   // damage, same identity (level/brave/faith/maxHp unchanged)
        t.Poll(true);   // ScanFieldEvidence tags this tick's damage line with the latched actor

        Assert.Contains(lines, l => l.Contains("damage 50") && l.Contains("(weapons: 52)"));
    }
}
