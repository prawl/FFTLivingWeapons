using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// The battle-exit match-report counters (logging facelift stage 3) and the armed-gate latch:
/// KillTracker.BattleCredits (per-battle per-weapon credits), FallbackCredits (credits whose
/// culprit latch was a turn-queue-fallback resolve), AnyTrackedWeaponThisBattle (the sticky
/// per-battle "a Living Weapon is fielded" gate), and the victim identity on the credit line.
/// CreditKill is driven directly (it is the internal seam ScanCorpses calls), mirroring the
/// KillTrackerTests convention of poking internal per-slot state.
/// </summary>
public class KillTrackerBattleCountersTests
{
    private static readonly HashSet<int> Weapons = new() { 9, 52 };

    private static KillTracker Make(out Dictionary<int, int> kills)
    {
        kills = new Dictionary<int, int>();
        return new KillTracker(kills, new FakeSparseMemory(), Weapons);
    }

    // --- LW-56: the credit-time live-wielder gate, wired with the PRODUCTION HasLiveWielder
    // delegate over a shared FakeSparseMemory (not a lambda constant), matching how Engine wires
    // it in production (Wielder.HasLiveWielder over the same live memory every other consumer
    // uses). ---

    private sealed class FakeDeedSink : IDeedSink
    {
        public readonly List<(int weaponId, VictimSnapshot victim)> Deeds = new();
        public readonly List<int> Misses = new();
        public void RecordDeed(int weaponId, in VictimSnapshot victim) => Deeds.Add((weaponId, victim));
        public void DeedMiss(int slot) => Misses.Add(slot);
    }

    private static KillTracker MakeGated(out Dictionary<int, int> kills, out FakeSparseMemory mem,
                                          out List<(string type, string payload)> recorded,
                                          out FakeDeedSink deeds)
    {
        kills = new Dictionary<int, int>();
        var fake = new FakeSparseMemory();
        mem = fake;
        var rec = new List<(string, string)>();
        recorded = rec;
        var sink = new FakeDeedSink();
        deeds = sink;
        return new KillTracker(kills, fake, Weapons, recorder: (t, p) => rec.Add((t, p)), deeds: sink,
                                hasLiveWielder: id => Wielder.HasLiveWielder(fake, id));
    }

    [Fact]
    public void LW56_stale_latch_weapon_with_no_live_wielder_is_refused_at_credit_time()
    {
        // No roster row anywhere holds weapon 9: a stale latch naming it (e.g. surviving a
        // mis-timed New Game) has no live wielder to credit. This test must fail if the
        // CreditGate call in CreditKill is removed.
        var t = MakeGated(out var kills, out _, out var recorded, out var deeds);
        var file = new List<string>();
        var prior = ModLogger.Instance;
        ModLogger.Instance = new FileConsoleLogger(_ => { }, file.Add);
        try
        {
            bool changed = t.CreditKill(0, 5, 5, new List<int> { 9 });

            Assert.False(changed);
            Assert.False(kills.ContainsKey(9));
            Assert.Empty(deeds.Deeds);
            Assert.Contains(recorded, r => r.type == "kill" && r.payload == "no-credit slot=0 reason=no-live-wielder weapon=9");
            Assert.Contains(file, l => l.Contains("[kill]") && l.Contains("stale attribution"));
        }
        finally { ModLogger.Instance = prior; }
    }

    [Fact]
    public void LW56_weapon_with_a_live_wielder_credits_normally_through_the_gate()
    {
        // Over-refusal control: identical geometry to the load-bearing test above, but this time
        // the weapon IS on a deployed roster row backed by a band entry.
        var t = MakeGated(out var kills, out var mem, out var recorded, out var deeds);
        MemSeats.SeatRoster(mem, slot: 0, lvl: 50, br: 60, fa: 70, rh: 9);
        MemSeats.SeatBand(mem, bandIdx: 0, weapon: 9, lvl: 50, br: 60, fa: 70, gx: 3, gy: 3);
        t._victimAtEdge[0] = new VictimSnapshot(true, 405, 82, false);

        bool changed = t.CreditKill(0, 5, 5, new List<int> { 9 });

        Assert.True(changed);
        Assert.Equal(1, kills[9]);
        Assert.Equal(1, t.BattleCredits[9]);
        Assert.Single(deeds.Deeds);
        Assert.DoesNotContain(recorded, r => r.payload.Contains("no-live-wielder"));
    }

    [Fact]
    public void LW56_dual_wield_offhand_culprit_still_credits_through_the_gate()
    {
        // Dual-wield off-hand pin: the culprit id lives in ROffHand of a deployed roster row,
        // whose band entry's own weapon field mirrors a DIFFERENT main-hand id. Kills any
        // band-only truth source.
        var t = MakeGated(out var kills, out var mem, out _, out _);
        MemSeats.SeatRoster(mem, slot: 2, lvl: 31, br: 65, fa: 58, rh: 1, oh: 9);
        MemSeats.SeatBand(mem, bandIdx: 5, weapon: 1, lvl: 31, br: 65, fa: 58, gx: 4, gy: 7);

        bool changed = t.CreditKill(3, 4, 7, new List<int> { 9 });

        Assert.True(changed);
        Assert.Equal(1, kills[9]);
    }

    [Fact]
    public void LW56_duplicate_roster_copy_does_not_poison_the_deployed_copys_credit()
    {
        // Duplicate-copy pin (the reviewed blocker): two roster rows hold the weapon id, only one
        // is deployed. Kills any TryResolve-based implementation with a found>1 bail.
        var t = MakeGated(out var kills, out var mem, out _, out _);
        MemSeats.SeatRoster(mem, slot: 0, lvl: 99, br: 97, fa: 75, rh: 9);   // deployed
        MemSeats.SeatRoster(mem, slot: 3, lvl: 99, br: 89, fa: 76, rh: 9);   // benched duplicate
        MemSeats.SeatBand(mem, bandIdx: 12, weapon: 9, lvl: 99, br: 97, fa: 75, gx: 3, gy: 5);

        bool changed = t.CreditKill(1, 3, 5, new List<int> { 9 });

        Assert.True(changed);
        Assert.Equal(1, kills[9]);
    }

    [Fact]
    public void LW56_mixed_partition_credits_the_survivor_and_refuses_the_other()
    {
        var t = MakeGated(out var kills, out var mem, out var recorded, out _);
        MemSeats.SeatRoster(mem, slot: 0, lvl: 50, br: 60, fa: 70, rh: 9);   // deployed
        MemSeats.SeatBand(mem, bandIdx: 0, weapon: 9, lvl: 50, br: 60, fa: 70, gx: 3, gy: 3);
        // weapon 52: no roster row holds it at all.

        bool changed = t.CreditKill(2, 4, 4, new List<int> { 9, 52 });

        Assert.True(changed);
        Assert.Equal(1, kills[9]);
        Assert.False(kills.ContainsKey(52));
        Assert.Equal(1, t.BattleCredits[9]);
        Assert.False(t.BattleCredits.ContainsKey(52));
        Assert.Single(recorded, r => r.type == "kill" && r.payload.Contains("no-live-wielder") && r.payload.Contains("weapon=52"));
    }

    [Fact]
    public void Null_hasLiveWielder_delegate_credits_regardless_of_live_wielder_state()
    {
        // Regression: a KillTracker constructed without the new parameter (every pre-LW-56 call
        // site) credits exactly as before, gate off.
        var t = Make(out var kills);
        bool changed = t.CreditKill(0, 5, 5, new List<int> { 9 });
        Assert.True(changed);
        Assert.Equal(1, kills[9]);
    }

    [Fact]
    public void CreditKill_increments_the_per_battle_credit_ledger()
    {
        var t = Make(out var kills);
        t.CreditKill(0, 5, 5, new List<int> { 9 });
        t.CreditKill(1, 6, 6, new List<int> { 9 });
        t.CreditKill(2, 7, 7, new List<int> { 52 });
        Assert.Equal(2, t.BattleCredits[9]);
        Assert.Equal(1, t.BattleCredits[52]);
        Assert.Equal(2, kills[9]);   // the lifetime tally moves in lockstep
    }

    [Fact]
    public void A_dual_wield_credit_counts_both_weapons_in_the_ledger()
    {
        var t = Make(out _);
        t.CreditKill(0, 5, 5, new List<int> { 9, 52 });
        Assert.Equal(1, t.BattleCredits[9]);
        Assert.Equal(1, t.BattleCredits[52]);
    }

    [Fact]
    public void ResetBattle_clears_the_per_battle_ledger_but_not_the_lifetime_tally()
    {
        var t = Make(out var kills);
        t.CreditKill(0, 5, 5, new List<int> { 9 });
        t.ResetBattle();
        Assert.Empty(t.BattleCredits);
        Assert.Equal(1, kills[9]);
    }

    [Fact]
    public void FallbackCredits_counts_only_credits_flagged_viaFallback()
    {
        var t = Make(out _);
        t.CreditKill(0, 5, 5, new List<int> { 9 }, viaFallback: true);
        t.CreditKill(1, 6, 6, new List<int> { 9 }, viaFallback: false);
        t.CreditKill(2, 7, 7, new List<int> { 9, 52 }, viaFallback: true);   // one CORPSE, one count
        Assert.Equal(2, t.FallbackCredits);
        t.ResetBattle();
        Assert.Equal(0, t.FallbackCredits);
    }

    [Fact]
    public void The_armed_gate_starts_quiet_and_resets_per_battle()
    {
        var t = Make(out _);
        Assert.False(t.AnyTrackedWeaponThisBattle);
        t.ResetBattle();
        Assert.False(t.AnyTrackedWeaponThisBattle);
    }

    [Fact]
    public void The_credit_line_names_the_victim_from_the_captured_snapshot()
    {
        var t = Make(out _);
        var file = new List<string>();
        var prior = ModLogger.Instance;
        ModLogger.Instance = new FileConsoleLogger(_ => { }, file.Add);
        try
        {
            t._victimAtEdge[0] = new VictimSnapshot(true, 405, 82, false);   // job 82 Summoner -> caster
            t.CreditKill(0, 7, 6, new List<int> { 9 });
            Assert.Contains(file, l => l.Contains("[kill]")
                && l.Contains("claims kill number 1")
                && l.Contains("felling a caster at (7,6)"));
            // The two-line id pattern: the ids ride the trace companion, not the console line.
            Assert.Contains(file, l => l.Contains("[trace]") && l.Contains("victim nameId 405"));
        }
        finally { ModLogger.Instance = prior; }
    }

    [Fact]
    public void The_credit_line_says_an_enemy_when_no_snapshot_was_captured()
    {
        var t = Make(out _);
        var file = new List<string>();
        var prior = ModLogger.Instance;
        ModLogger.Instance = new FileConsoleLogger(_ => { }, file.Add);
        try
        {
            t.CreditKill(0, 3, 4, new List<int> { 52 });
            Assert.Contains(file, l => l.Contains("felling an enemy at (3,4)"));
        }
        finally { ModLogger.Instance = prior; }
    }

    // --- the resolve-source phrases behind the owner-flagged no-credit line ---
    // (InlineData can't carry the internal UntrackedReason enum on a public test method
    // signature, CS0051 -- pass the int value and cast inside, the LoggerTests idiom.)

    [Theory]
    [InlineData((int)UntrackedReason.ChargedAction, "a charged-action landing")]
    [InlineData((int)UntrackedReason.EnemyTurn, "an enemy-turn team read")]
    [InlineData((int)UntrackedReason.ActedLatch, "the acted-period latch")]
    [InlineData((int)UntrackedReason.ActorRegister, "the actor register")]
    public void Each_untracked_reason_names_its_resolve_source(int reason, string phrase)
        => Assert.Equal(phrase, KillTracker.ResolveSourcePhrase((UntrackedReason)reason));
}
