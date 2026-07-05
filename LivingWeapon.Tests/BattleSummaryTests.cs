using System;
using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// The pure battle-end match-report composer (logging facelift stage 3). Tier crossings are
/// DERIVED from the credit delta (tierOf(lifetime - battleCredits) vs tierOf(lifetime)), never
/// counted separately; the zero-kill battle gets its own short form; the fallback-attribution
/// clause appears only when the counter is nonzero.
/// </summary>
public class BattleSummaryTests
{
    private static readonly Func<int, string> Names = id => id switch
    {
        9 => "Galewind",
        52 => "Windrunner",
        88 => "Kiyomori",
        _ => "weapon " + id,
    };

    // The production thresholds {5,25,50} via the always-compiled test hook.
    private static int Tier(int kills) => Tuning.TierForIn(kills, Tuning.ProdThresholds);

    private static string Compose(
        Dictionary<int, int> credits, Dictionary<int, int> lifetime,
        List<(int weaponId, VictimClass.Archetype mark)>? marks = null,
        int fallback = 0, int turns = 14)
        => BattleSummary.Compose(credits, lifetime, marks ?? new(), fallback, turns, Names, Tier);

    [Fact]
    public void Zero_kill_battle_gets_the_short_form()
    {
        string s = Compose(new(), new(), turns: 9);
        Assert.Equal("Battle ended: no kills were credited; 9 turns; the kill tally and legends are saved.", s);
    }

    [Fact]
    public void Kills_are_listed_by_name_with_their_battle_counts()
    {
        var credits = new Dictionary<int, int> { [52] = 2, [88] = 1 };
        var lifetime = new Dictionary<int, int> { [52] = 12, [88] = 3 };
        string s = Compose(credits, lifetime);
        Assert.StartsWith("Battle ended: 3 kills credited (Windrunner 2, Kiyomori 1)", s);
        Assert.Contains("; 14 turns; the kill tally and legends are saved.", s);
    }

    [Fact]
    public void A_single_kill_reads_singular()
    {
        var credits = new Dictionary<int, int> { [9] = 1 };
        var lifetime = new Dictionary<int, int> { [9] = 2 };
        string s = Compose(credits, lifetime, turns: 1);
        Assert.Contains("1 kill credited (Galewind 1)", s);
        Assert.Contains("; 1 turn; ", s);
    }

    [Fact]
    public void A_tier_crossing_is_derived_from_the_credit_delta()
    {
        // Windrunner went 4 -> 6 lifetime this battle: tier 0 -> tier 1 under {5,25,50}.
        var credits = new Dictionary<int, int> { [52] = 2 };
        var lifetime = new Dictionary<int, int> { [52] = 6 };
        string s = Compose(credits, lifetime);
        Assert.Contains("1 tier reached (Windrunner tier 1)", s);
    }

    [Fact]
    public void No_crossing_means_no_tier_parenthetical()
    {
        // 10 -> 12: still tier 1. The clause reports zero without naming anyone.
        var credits = new Dictionary<int, int> { [52] = 2 };
        var lifetime = new Dictionary<int, int> { [52] = 12 };
        string s = Compose(credits, lifetime);
        Assert.Contains("0 tiers reached", s);
        Assert.DoesNotContain("Windrunner tier", s);
    }

    [Fact]
    public void Marks_are_named_with_their_titles()
    {
        var credits = new Dictionary<int, int> { [52] = 1 };
        var lifetime = new Dictionary<int, int> { [52] = 30 };
        var marks = new List<(int, VictimClass.Archetype)> { (52, VictimClass.Archetype.Monster) };
        string s = Compose(credits, lifetime, marks);
        Assert.Contains("1 Mark earned (Windrunner the Beastbane)", s);
    }

    [Fact]
    public void Fallback_clause_appears_only_when_nonzero()
    {
        var credits = new Dictionary<int, int> { [52] = 1 };
        var lifetime = new Dictionary<int, int> { [52] = 2 };
        Assert.DoesNotContain("fallback attribution", Compose(credits, lifetime, fallback: 0));
        Assert.Contains("1 kill credited by fallback attribution", Compose(credits, lifetime, fallback: 1));
    }

    [Fact]
    public void Every_pluralized_clause_reads_singular_when_its_own_count_is_one()
    {
        // Regression guard (adversarial verification round, item 3): every noun the composer
        // pluralizes (kill, Mark, tier, turn) must read singular, never "1 Marks"/"1 kills"/
        // "1 tiers"/"1 turns", in a single battle-end line where all four counts are 1 at once.
        var credits = new Dictionary<int, int> { [52] = 1 };
        var lifetime = new Dictionary<int, int> { [52] = 5 };   // 4 -> 5: tier 0 -> tier 1
        var marks = new List<(int, VictimClass.Archetype)> { (52, VictimClass.Archetype.Human) };
        string s = Compose(credits, lifetime, marks, fallback: 1, turns: 1);
        Assert.Equal(
            "Battle ended: 1 kill credited (Windrunner 1), 1 Mark earned (Windrunner the Manslayer), " +
            "1 tier reached (Windrunner tier 1), 1 kill credited by fallback attribution; 1 turn; " +
            "the kill tally and legends are saved.",
            s);
        Assert.DoesNotContain("1 kills", s);
        Assert.DoesNotContain("1 Marks", s);
        Assert.DoesNotContain("1 tiers", s);
        Assert.DoesNotContain("1 turns", s);
    }

    [Fact]
    public void The_line_carries_no_double_dash()
    {
        var credits = new Dictionary<int, int> { [52] = 2, [88] = 1, [9] = 1 };
        var lifetime = new Dictionary<int, int> { [52] = 6, [88] = 25, [9] = 50 };
        var marks = new List<(int, VictimClass.Archetype)> { (9, VictimClass.Archetype.Undead) };
        string s = Compose(credits, lifetime, marks, fallback: 2);
        Assert.DoesNotContain(" -- ", s);
    }
}
