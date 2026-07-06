using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// AttackCardTail.ComposeTail is LW-31 stage 3's pure tail-compose policy (docs/TODO.md): once
/// AttackRow.Policy.ComposeRow has already renamed the row itself (weapon name + trimmed tier
/// suffix, or "Fists"), this composes only the DESCRIPTION TAIL that follows it in the split image.
///
/// THE KILLS CLAUSE (owner decision 2026-07-06) is a TIER-PROGRESS METER, not a flat count: below
/// max tier, "Kills: {kills}/{nextThreshold} to {nextSuffix}" (nextThreshold/nextSuffix off
/// Tuning's own kill-tier thresholds/Suffix array); at max tier, plain "Kills: {kills}" (nowhere
/// further to climb). Zero kills is no longer special for a living (named) weapon: the meter
/// renders from kill zero exactly like any other below-max count.
///
/// THE SIGNATURE CLAUSE (owner decision 2026-07-06, same day) is one slot with two mutually
/// exclusive faces, keyed off the same Signatures.Earned check the old sig clause used: earned
/// renders "{sigLabel} armed"; LOCKED (a real signature the wielder hasn't reached the tier for
/// yet) renders a tease, "Unlocks {sigLabel}", instead. No signature at all (sigLabel null) means
/// no clause, meter only.
///
/// PUNCTUATION (owner nitpick 2026-07-06, same day): clauses are joined by exactly ". " and the
/// returned tail NEVER ends with a trailing period (the complaint was specifically the meter's
/// "+." ending). GenericTail/FistTail keep their periods: they are fixed full sentences, not
/// composed clause chains.
///
/// Both the optional Mark clause (LW-35 hides it from the production call site, but the pure
/// contract stays symmetric) and the signature clause stay purely additive and tried in the SAME
/// FIXED PRIORITY as before (never sorted by length): head, then Mark, then signature. Trailing
/// clauses are dropped WHOLE, from the tail inward, when they would overflow the budget; a clause
/// is NEVER truncated mid-way.
/// </summary>
public class AttackCardTailTests
{
    // ---- The tier-progress meter itself (owner's worked examples, production thresholds {5,25,50}) ----

    [Theory]
    [InlineData(0, "Kills: 0/5 to +")]
    [InlineData(1, "Kills: 1/5 to +")]
    [InlineData(4, "Kills: 4/5 to +")]
    [InlineData(5, "Kills: 5/25 to +2")]
    [InlineData(6, "Kills: 6/25 to +2")]
    [InlineData(24, "Kills: 24/25 to +2")]
    [InlineData(25, "Kills: 25/50 to +3")]
    [InlineData(34, "Kills: 34/50 to +3")]
    [InlineData(49, "Kills: 49/50 to +3")]
    [InlineData(50, "Kills: 50")]
    [InlineData(55, "Kills: 55")]
    public void ComposeTail_meter_matches_the_owners_worked_examples(int kills, string expectedHead)
    {
        string line = AttackCardTail.ComposeTail(kills, markLabel: null, sigLabel: null, sigEarned: false, budgetChars: 200);
        Assert.Equal(expectedHead, line);
    }

    [Fact]
    public void Zero_kills_is_not_special_the_meter_still_renders()
    {
        // The zero-kill generic-line rule DIED 2026-07-06: a living (named) weapon always gets the
        // meter, never AttackCardTail.GenericTail, even at kill zero.
        string line = AttackCardTail.ComposeTail(kills: 0, markLabel: null, sigLabel: null, sigEarned: false, budgetChars: 51);
        Assert.Equal("Kills: 0/5 to +", line);
        Assert.NotEqual(AttackCardTail.GenericTail, line);
    }

    [Fact]
    public void ComposeHead_degrades_gracefully_under_the_dev_curve()
    {
        // Sanity for a DEV-flavored compile (LWDEV, thresholds {1,2,3}): exercised directly against
        // Tuning.DevThresholds since tests compile under prod (mirrors TuningTests' own
        // NextThresholdForIn/DevThresholds checks). "Kills: 0/1 to +" is the owner's own worked
        // dev-curve example (2026-07-06, no trailing period).
        Assert.Equal("Kills: 0/1 to +", AttackCardTail.ComposeHead(0, Tuning.DevThresholds, Tuning.Suffix));
        Assert.Equal("Kills: 1/2 to +2", AttackCardTail.ComposeHead(1, Tuning.DevThresholds, Tuning.Suffix));
        Assert.Equal("Kills: 2/3 to +3", AttackCardTail.ComposeHead(2, Tuning.DevThresholds, Tuning.Suffix));
        Assert.Equal("Kills: 3", AttackCardTail.ComposeHead(3, Tuning.DevThresholds, Tuning.Suffix));
    }

    [Fact]
    public void Head_only_when_no_mark_and_no_signature()
    {
        string line = AttackCardTail.ComposeTail(kills: 5, markLabel: null, sigLabel: null, sigEarned: false, budgetChars: 51);
        Assert.Equal("Kills: 5/25 to +2", line);
    }

    [Fact]
    public void The_composed_tail_never_ends_with_a_period()
    {
        // The owner nitpick itself, swept across every clause combination (2026-07-06).
        foreach (int kills in new[] { 0, 3, 34, 55 })
            foreach (string? mark in new[] { null, "Beastbane" })
                foreach (bool earned in new[] { false, true })
                {
                    string line = AttackCardTail.ComposeTail(kills, mark, "Gun Slinger", earned, budgetChars: 200);
                    Assert.False(line.EndsWith("."), $"'{line}' ends with a period");
                }
    }

    // ---- Mark clause (LW-35 hides it from production, but the pure contract stays symmetric) ----

    [Fact]
    public void Mark_clause_composes_on_top_of_the_zero_kill_meter()
    {
        string line = AttackCardTail.ComposeTail(kills: 0, markLabel: "Beastbane", sigLabel: null, sigEarned: false, budgetChars: 51);
        Assert.Equal("Kills: 0/5 to +. Beastbane", line);
    }

    [Fact]
    public void Zero_kills_composes_with_a_signature_clause_too()
    {
        string line = AttackCardTail.ComposeTail(kills: 0, markLabel: null, sigLabel: "Concentration", sigEarned: true, budgetChars: 51);
        Assert.Equal("Kills: 0/5 to +. Concentration armed", line);
    }

    [Fact]
    public void Mark_clause_present_without_sig_clause()
    {
        string line = AttackCardTail.ComposeTail(42, "Beastbane", sigLabel: null, sigEarned: false, budgetChars: 200);
        Assert.Equal("Kills: 42/50 to +3. Beastbane", line);
    }

    // ---- The signature clause's two mutually exclusive faces ----

    [Fact]
    public void Earned_sig_clause_present_without_mark_clause()
    {
        string line = AttackCardTail.ComposeTail(42, markLabel: null, sigLabel: "Concentration", sigEarned: true, budgetChars: 200);
        Assert.Equal("Kills: 42/50 to +3. Concentration armed", line);
    }

    [Fact]
    public void Locked_sig_shows_the_unlocks_tease_instead_of_armed()
    {
        // The owner's own worked "locked" example (2026-07-06, no-trailing-period wording pass).
        string line = AttackCardTail.ComposeTail(34, markLabel: null, sigLabel: "Gun Slinger", sigEarned: false, budgetChars: 200);
        Assert.Equal("Kills: 34/50 to +3. Unlocks Gun Slinger", line);
    }

    [Fact]
    public void Earned_sig_at_max_tier_matches_the_owners_worked_example()
    {
        // The owner's own worked "earned" example (2026-07-06, no-trailing-period wording pass:
        // "Kills: 20. Gun Slinger armed" transposed onto this suite's 55-kill fixture).
        string line = AttackCardTail.ComposeTail(55, markLabel: null, sigLabel: "Gun Slinger", sigEarned: true, budgetChars: 200);
        Assert.Equal("Kills: 55. Gun Slinger armed", line);
    }

    [Fact]
    public void No_signature_is_meter_only_regardless_of_the_earned_flag()
    {
        // sigLabel null means "this weapon has no signature at all": sigEarned must be ignored,
        // never fabricating a clause from a stray true.
        Assert.Equal("Kills: 34/50 to +3",
            AttackCardTail.ComposeTail(34, markLabel: null, sigLabel: null, sigEarned: true, budgetChars: 200));
        Assert.Equal("Kills: 34/50 to +3",
            AttackCardTail.ComposeTail(34, markLabel: null, sigLabel: null, sigEarned: false, budgetChars: 200));
    }

    [Fact]
    public void Mark_and_locked_sig_clauses_combine_in_fixed_priority()
    {
        string line = AttackCardTail.ComposeTail(0, "Beastbane", "Gun Slinger", sigEarned: false, budgetChars: 200);
        Assert.Equal("Kills: 0/5 to +. Beastbane. Unlocks Gun Slinger", line);
    }

    [Fact]
    public void Full_line_includes_mark_and_earned_sig_clauses_in_order_when_everything_fits()
    {
        string line = AttackCardTail.ComposeTail(42, "Beastbane", "Concentration", sigEarned: true, budgetChars: 200);
        Assert.Equal("Kills: 42/50 to +3. Beastbane. Concentration armed", line);
    }

    // ---- Budget-drop rules (whole clause, from the tail inward, never mid-clause) ----

    [Fact]
    public void Drops_the_earned_sig_clause_when_it_alone_overflows_the_budget()
    {
        string headPlusMark = "Kills: 42/50 to +3. Beastbane";
        string line = AttackCardTail.ComposeTail(42, "Beastbane", "Concentration", sigEarned: true, budgetChars: headPlusMark.Length);
        Assert.Equal(headPlusMark, line);
        Assert.DoesNotContain("armed", line);
    }

    [Fact]
    public void Drops_the_locked_tease_clause_when_it_alone_overflows_the_budget()
    {
        string headPlusMark = "Kills: 42/50 to +3. Beastbane";
        string line = AttackCardTail.ComposeTail(42, "Beastbane", "Gun Slinger", sigEarned: false, budgetChars: headPlusMark.Length);
        Assert.Equal(headPlusMark, line);
        Assert.DoesNotContain("Unlocks", line);
    }

    [Fact]
    public void Drops_both_mark_and_sig_clauses_when_only_the_head_fits()
    {
        string head = "Kills: 42/50 to +3";
        string line = AttackCardTail.ComposeTail(42, "Beastbane", "Concentration", sigEarned: true, budgetChars: head.Length);
        Assert.Equal(head, line);
        Assert.DoesNotContain("Beastbane", line);
        Assert.DoesNotContain("armed", line);
    }

    [Fact]
    public void Never_truncates_mid_clause_at_the_sig_boundary()
    {
        // One char short of the full line: must fall back cleanly to head+mark, never a truncated
        // fragment of the sig clause.
        string full = "Kills: 42/50 to +3. Beastbane. Concentration armed";
        string headPlusMark = "Kills: 42/50 to +3. Beastbane";
        string line = AttackCardTail.ComposeTail(42, "Beastbane", "Concentration", sigEarned: true, budgetChars: full.Length - 1);
        Assert.Equal(headPlusMark, line);
    }

    [Fact]
    public void Generic_when_even_the_head_does_not_fit_the_budget()
    {
        // The ONLY remaining GenericTail trigger: a budget too tight for the mandatory head itself.
        string head = "Kills: 42/50 to +3";
        string line = AttackCardTail.ComposeTail(42, "Beastbane", "Concentration", sigEarned: true, budgetChars: head.Length - 1);
        Assert.Equal(AttackCardTail.GenericTail, line);
    }

    [Fact]
    public void Budget_edge_is_inclusive_at_the_heads_own_length()
    {
        string head = "Kills: 42/50 to +3";
        Assert.Equal(head, AttackCardTail.ComposeTail(42, null, null, sigEarned: false, budgetChars: head.Length));
        Assert.Equal(AttackCardTail.GenericTail, AttackCardTail.ComposeTail(42, null, null, sigEarned: false, budgetChars: head.Length - 1));
    }

    [Fact]
    public void Full_line_fits_exactly_at_its_own_length_boundary()
    {
        string full = "Kills: 42/50 to +3. Beastbane. Concentration armed";
        string line = AttackCardTail.ComposeTail(42, "Beastbane", "Concentration", sigEarned: true, budgetChars: full.Length);
        Assert.Equal(full, line);
    }

    [Fact]
    public void GenericTail_and_FistTail_are_the_owner_specified_constants()
    {
        // Both KEEP their trailing periods (owner direction 2026-07-06): they are fixed full
        // sentences, not composed clause chains, and the nitpick was specifically the meter's
        // "+." ending.
        Assert.Equal("Attacks with the equipped weapon.", AttackCardTail.GenericTail);
        Assert.Equal("Attacks with bare fists.", AttackCardTail.FistTail);
    }
}
