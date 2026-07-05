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
