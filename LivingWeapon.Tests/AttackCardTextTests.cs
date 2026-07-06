using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// AttackCardText.Compose is LW-31 stage 2's pure compose policy (docs/TODO.md): the Attack-menu
/// hover-card desc becomes "{name}{suffix} Kills: {kills}." plus two additive, fixed-priority,
/// budget-gated clauses (" {Mark}." then " {SigName} armed."). Total-order coverage: every
/// clause-drop boundary, the budget edge at exactly 73, the kills==0/null-Mark null rule, and the
/// vanilla constant's own footprint invariant.
/// </summary>
public class AttackCardTextTests
{
    [Fact]
    public void VanillaDesc_is_exactly_73_chars()
    {
        Assert.Equal(73, AttackCardText.VanillaDesc.Length);
        Assert.Equal(AttackCardText.DefaultBudgetChars, AttackCardText.VanillaDesc.Length);
    }

    [Fact]
    public void Null_when_zero_kills_and_no_mark()
    {
        Assert.Null(AttackCardText.Compose("Windrunner", "  ", kills: 0, markLabel: null, sigName: null));
    }

    [Fact]
    public void Null_when_zero_kills_and_no_mark_even_with_a_sig_name()
    {
        // sigName alone (no Mark) at zero kills is still nothing-earned-yet -> null.
        Assert.Null(AttackCardText.Compose("Windrunner", "  ", kills: 0, markLabel: null, sigName: "Concentration"));
    }

    [Fact]
    public void Composes_with_zero_kills_when_a_mark_is_present()
    {
        // Degenerate (kills tally and Mark count are different countables in principle), but the
        // compose contract only nulls on kills<=0 AND markLabel==null; a present Mark alone is
        // enough to compose. Must not throw or misformat.
        string? line = AttackCardText.Compose("Windrunner", "  ", kills: 0, markLabel: "Beastbane", sigName: null);
        Assert.NotNull(line);
        Assert.Equal("Windrunner Kills: 0. Beastbane.", line);
    }

    [Fact]
    public void Head_only_identity_and_kills_no_suffix()
    {
        string? line = AttackCardText.Compose("Windrunner", "  ", kills: 5, markLabel: null, sigName: null);
        Assert.Equal("Windrunner Kills: 5.", line);
    }

    [Fact]
    public void Suffix_is_trimmed_and_concatenated_directly_onto_the_name()
    {
        Assert.Equal("Windrunner+ Kills: 5.", AttackCardText.Compose("Windrunner", "+ ", 5, null, null));
        Assert.Equal("Windrunner+3 Kills: 5.", AttackCardText.Compose("Windrunner", "+3", 5, null, null));
        Assert.Equal("Windrunner Kills: 5.", AttackCardText.Compose("Windrunner", "  ", 5, null, null));
    }

    [Fact]
    public void Full_line_includes_mark_and_sig_clauses_in_order_when_everything_fits()
    {
        string? line = AttackCardText.Compose("Windrunner", "+3", 42, "Beastbane", "Concentration", budgetChars: 200);
        Assert.Equal("Windrunner+3 Kills: 42. Beastbane. Concentration armed.", line);
    }

    [Fact]
    public void Mark_clause_present_without_sig_clause()
    {
        string? line = AttackCardText.Compose("Windrunner", "+3", 42, "Beastbane", null, budgetChars: 200);
        Assert.Equal("Windrunner+3 Kills: 42. Beastbane.", line);
    }

    [Fact]
    public void Sig_clause_present_without_mark_clause()
    {
        string? line = AttackCardText.Compose("Windrunner", "+3", 42, null, "Concentration", budgetChars: 200);
        Assert.Equal("Windrunner+3 Kills: 42. Concentration armed.", line);
    }

    [Fact]
    public void Drops_the_sig_clause_when_it_alone_overflows_the_budget()
    {
        // head+mark = "Windrunner+3 Kills: 42. Beastbane." (35 chars). Adding " Concentration armed."
        // (21 more = 56) still fits a wide budget but not this narrow one.
        string headPlusMark = "Windrunner+3 Kills: 42. Beastbane.";
        Assert.Equal(34, headPlusMark.Length);
        string? line = AttackCardText.Compose("Windrunner", "+3", 42, "Beastbane", "Concentration", budgetChars: 40);
        Assert.Equal(headPlusMark, line);
        Assert.DoesNotContain("armed", line);
    }

    [Fact]
    public void Drops_both_mark_and_sig_clauses_when_only_the_head_fits()
    {
        string head = "Windrunner+3 Kills: 42.";
        Assert.Equal(23, head.Length);
        string? line = AttackCardText.Compose("Windrunner", "+3", 42, "Beastbane", "Concentration", budgetChars: 30);
        Assert.Equal(head, line);
        Assert.DoesNotContain("Beastbane", line);
        Assert.DoesNotContain("armed", line);
    }

    [Fact]
    public void Never_truncates_mid_clause_at_the_sig_boundary()
    {
        // One char short of the full line: must fall back cleanly to head+mark, never a truncated
        // fragment of the sig clause.
        string full = "Windrunner+3 Kills: 42. Beastbane. Concentration armed.";
        string headPlusMark = "Windrunner+3 Kills: 42. Beastbane.";
        string? line = AttackCardText.Compose("Windrunner", "+3", 42, "Beastbane", "Concentration", budgetChars: full.Length - 1);
        Assert.Equal(headPlusMark, line);
    }

    [Fact]
    public void Null_when_even_the_head_does_not_fit_the_budget()
    {
        string head = "Windrunner+3 Kills: 42.";
        string? line = AttackCardText.Compose("Windrunner", "+3", 42, "Beastbane", "Concentration", budgetChars: head.Length - 1);
        Assert.Null(line);
    }

    [Fact]
    public void Budget_edge_exactly_73_the_default_footprint()
    {
        // A head of exactly 73 chars must still compose (inclusive boundary); the default budget
        // parameter matches the vanilla desc's own footprint.
        string name = new string('X', 73 - " Kills: 999.".Length);
        string head = $"{name} Kills: 999.";
        Assert.Equal(73, head.Length);

        string? atDefault = AttackCardText.Compose(name, "  ", 999, null, null);
        Assert.Equal(head, atDefault);

        string? oneOver = AttackCardText.Compose(name + "X", "  ", 999, null, null);
        Assert.Null(oneOver);
    }

    [Fact]
    public void Full_line_fits_exactly_at_its_own_length_boundary()
    {
        string full = "Windrunner+3 Kills: 42. Beastbane. Concentration armed.";
        string? atExact = AttackCardText.Compose("Windrunner", "+3", 42, "Beastbane", "Concentration", budgetChars: full.Length);
        Assert.Equal(full, atExact);
    }
}
