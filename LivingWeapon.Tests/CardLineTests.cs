using System.Collections.Generic;
using System.IO;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// CardLine.Compose is Reliquary Phase 1's pure card-flavor composer (docs/RELIQUARY_AC.md
/// Display section). Total order: highest-count archetype AMONG EARNED MARKS > last-victim >
/// null (decision 8: no deeds ever recorded -> always null, baked flavor stays). Forms are tried
/// in FIXED priority (A, B, C, D -- NOT length-sorted), so a budget can never invert the total
/// order by letting a lower-priority form "win" just because it happens to be shorter.
/// </summary>
public class CardLineTests
{
    private const byte Archer = 77;    // Human
    private const byte Dragoon = 87;   // Human
    private const byte BlackMage = 80; // Caster

    private static WeaponLegend Legend(int lastVictimJob = -1,
                                        params (VictimClass.Archetype archetype, int count)[] marks)
    {
        var w = new WeaponLegend();
        if (lastVictimJob >= 0)
        {
            w.LastVictimJob = (byte)lastVictimJob;
            w.LastVictimCls = (int)VictimClass.Classify((byte)lastVictimJob, undead: false);
        }
        foreach (var (a, count) in marks)
        {
            w.Counts[(int)a] = count;
            w.Marks.Add((int)a);
        }
        return w;
    }

    /// <summary>Build a legend whose last victim is UNDEAD (Classify needs the undead bool, not
    /// just a job byte -- Legend() above always passes undead:false).</summary>
    private static WeaponLegend UndeadLegend(byte lastVictimJob, params (VictimClass.Archetype archetype, int count)[] marks)
    {
        var w = new WeaponLegend();
        w.LastVictimJob = lastVictimJob;
        w.LastVictimCls = (int)VictimClass.Archetype.Undead;
        foreach (var (a, count) in marks)
        {
            w.Counts[(int)a] = count;
            w.Marks.Add((int)a);
        }
        return w;
    }

    [Fact]
    public void Compose_returns_null_with_no_deeds()
    {
        var legend = new WeaponLegend();   // LastVictimCls == -1: no RecordDeed ever ran
        Assert.Null(CardLine.Compose("Windrunner", totalKills: 40, legend, budgetChars: 200));
    }

    [Fact]
    public void Compose_prefers_earned_mark_over_last_victim()
    {
        // Marks earned (Human, count 25) but the MOST RECENT victim is a caster (Black Mage) --
        // the total order still prefers the earned Mark's narration over the raw last-victim form.
        var legend = Legend(lastVictimJob: BlackMage, (VictimClass.Archetype.Human, 25));

        string? line = CardLine.Compose("Windrunner", totalKills: 999, legend, budgetChars: 200);

        Assert.NotNull(line);
        Assert.Contains("Manslayer", line);
        Assert.Contains("25 men felled", line);
    }

    [Fact]
    public void Below_threshold_counts_compose_last_victim_not_mark()
    {
        // Counts > 0 but Marks is EMPTY (below Tuning.MarkThresholds) -- must compose the
        // last-victim form, never reference an un-earned title.
        var w = new WeaponLegend();
        w.LastVictimJob = Archer;
        w.LastVictimCls = (int)VictimClass.Archetype.Human;
        w.Counts[(int)VictimClass.Archetype.Human] = 1;   // below threshold; Marks stays empty

        string? line = CardLine.Compose("Windrunner", totalKills: 3, w, budgetChars: 200);

        Assert.NotNull(line);
        Assert.DoesNotContain("Manslayer", line);
        Assert.Contains("an Archer", line);
    }

    [Fact]
    public void Compose_breaks_mark_tie_by_archetype_order()
    {
        // Human (1) and Monster (2) both earned at the SAME count -- Human wins (lower enum value).
        var legend = Legend(lastVictimJob: Archer,
            (VictimClass.Archetype.Monster, 25), (VictimClass.Archetype.Human, 25));

        string? line = CardLine.Compose("Windrunner", totalKills: 50, legend, budgetChars: 200);

        Assert.NotNull(line);
        Assert.Contains("Manslayer", line);
        Assert.DoesNotContain("Beastbane", line);
    }

    [Fact]
    public void Padded_to_exact_budget()
    {
        var legend = Legend(lastVictimJob: Archer, (VictimClass.Archetype.Human, 25));
        string? line = CardLine.Compose("Windrunner", totalKills: 999, legend, budgetChars: 150);
        Assert.NotNull(line);
        Assert.Equal(150, line!.Length);
    }

    [Fact]
    public void Forms_fixed_priority_not_length()
    {
        // A=60, B=43, C=42, D=25 (Windrunner/Beastbane/340 beasts/a Dragoon/999 kills). Budget 49
        // fits BOTH B (43) and D (25) -- fixed priority (A,B,C,D) must pick B, the higher-priority
        // Mark form, never "the shortest that fits" (which would wrongly pick D).
        var legend = Legend(lastVictimJob: Dragoon, (VictimClass.Archetype.Monster, 340));

        string? line = CardLine.Compose("Windrunner", totalKills: 999, legend, budgetChars: 49);

        Assert.NotNull(line);
        Assert.StartsWith("Windrunner, Beastbane -- 340 beasts felled.", line);
    }

    [Fact]
    public void Forms_degrade_by_budget()
    {
        // Same scenario as Forms_fixed_priority_not_length: A=60, B=43, C=42, D=25.
        var legend = Legend(lastVictimJob: Dragoon, (VictimClass.Archetype.Monster, 340));

        string? wide = CardLine.Compose("Windrunner", totalKills: 999, legend, budgetChars: 100);
        Assert.NotNull(wide);
        Assert.StartsWith("Windrunner, Beastbane -- 340 beasts felled; last, a Dragoon.", wide);

        string? mid = CardLine.Compose("Windrunner", totalKills: 999, legend, budgetChars: 50);
        Assert.NotNull(mid);
        Assert.StartsWith("Windrunner, Beastbane -- 340 beasts felled.", mid);
        Assert.DoesNotContain("last,", mid);

        string? narrow = CardLine.Compose("Windrunner", totalKills: 999, legend, budgetChars: 30);
        Assert.NotNull(narrow);
        Assert.StartsWith("Windrunner -- 999 felled.", narrow);
        Assert.DoesNotContain("Beastbane", narrow);
    }

    [Fact]
    public void Sasukes_blade_26_budget_is_always_null()
    {
        // Real repo data (docs/RELIQUARY_P1_PLAN.md's known data fact): Sasuke's Blade's baked
        // flavor is 26 chars; even the bare form D ("Sasuke's Blade -- 0 felled.") is 27 -- one
        // over budget -- so this weapon can NEVER compose, under any deed state.
        var legend = Legend(lastVictimJob: Archer, (VictimClass.Archetype.Human, 999));
        Assert.Null(CardLine.Compose("Sasuke's Blade", totalKills: 1, legend, budgetChars: 26));

        var noVictim = new WeaponLegend();
        Assert.Null(CardLine.Compose("Sasuke's Blade", totalKills: 1, noVictim, budgetChars: 26));
    }

    [Fact]
    public void Ascii_only_enforced()
    {
        // A non-ASCII weapon name must never compose -- ByteScan.Ascii DROPS non-ASCII chars,
        // which would desync the 8-bit vs UTF-16 encoded lengths (load-bearing guard). Escaped
        // (\u00e9 = e-acute) rather than a literal accented char, per the ASCII-only house rule.
        var legend = Legend(lastVictimJob: Archer, (VictimClass.Archetype.Human, 25));
        string? line = CardLine.Compose("Bl\u00e9zaine", totalKills: 5, legend, budgetChars: 200);
        Assert.Null(line);
    }

    [Fact]
    public void IsPaintable_rejects_wrong_length_non_ascii_and_em_dash()
    {
        // Escaped code points (per the ASCII-only house rule): \u00e9 = e-acute, \u2014 = em dash.
        Assert.True(CardLine.IsPaintable("abc  ", 5));
        Assert.False(CardLine.IsPaintable("abc", 5));                        // wrong length
        Assert.False(CardLine.IsPaintable("ab\u00e9  ", 5));            // non-ASCII
        Assert.False(CardLine.IsPaintable("a\u2014b  ", 5));            // em dash, never allowed
    }

    // ---- repo-data invariants (mirrors MetaSchemaTests.RepoMetaPath) ----

    private static string RepoMetaPath()
    {
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "LivingWeapon", "meta.json");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("LivingWeapon/meta.json not found above the test bin dir");
    }

    [Fact]
    public void Digit_headroom_property_all_121()
    {
        // No weapon may sit in the form-D digit-rollover window: either it can NEVER compose form
        // D (permanently null, like Sasuke's Blade) or it can compose it even at 5 digits worth of
        // kills (permanently fits) -- never "fits today, goes null after enough kills" (there is no
        // repaint-back-to-baked path, so that would strand a stale line).
        var map = MetaLoader.Load(Path.GetDirectoryName(RepoMetaPath())!);
        int checkedCount = 0;
        foreach (var (id, m) in map)
        {
            if (string.IsNullOrEmpty(m.Flavor)) continue;
            checkedCount++;
            int fixedLen = m.Name.Length + 12;   // "{name} -- " (4) + " felled." (8) == 12
            int budget = m.Flavor.Length;
            bool neverFits = budget < fixedLen + 1;
            bool fitsAt5Digits = budget >= fixedLen + 5;
            Assert.True(neverFits || fitsAt5Digits,
                $"weapon {id} ({m.Name}) sits in the form-D digit-rollover window (budget={budget}, fixed={fixedLen})");
        }
        Assert.True(checkedCount >= 100, $"expected the full living-weapon set, got {checkedCount}");
    }

    [Fact]
    public void Earned_lines_unique_and_prefix_clean_across_all_weapons_including_baked()
    {
        // Mirrors analyze.py's check_unique_flavor, extended over the archetype/last-victim
        // composed-line cross-product (at each weapon's own padded budget) UNION the 121 baked
        // flavor lines -- two weapons sharing (or prefix-colliding on) a card-visible line
        // resurrects the pre-Display-v2 shared-kills cross-attribution bug.
        var map = MetaLoader.Load(Path.GetDirectoryName(RepoMetaPath())!);
        var lines = new List<(int id, string kind, string text)>();

        foreach (var (id, m) in map)
        {
            if (string.IsNullOrEmpty(m.Flavor)) continue;
            int budget = m.Flavor.Length;
            lines.Add((id, "baked", m.Flavor.TrimEnd()));

            var lastVictimOnly = new WeaponLegend { LastVictimJob = Archer, LastVictimCls = (int)VictimClass.Archetype.Human };
            string? c = CardLine.Compose(m.Name, 7, lastVictimOnly, budget);
            if (c != null) lines.Add((id, "last-victim", c.TrimEnd()));

            foreach (VictimClass.Archetype a in new[]
                     {
                         VictimClass.Archetype.Caster, VictimClass.Archetype.Human,
                         VictimClass.Archetype.Monster, VictimClass.Archetype.Undead,
                     })
            {
                WeaponLegend legend = a switch
                {
                    VictimClass.Archetype.Caster => Legend(BlackMage, (a, 25)),
                    VictimClass.Archetype.Monster => Legend(100, (a, 25)),
                    VictimClass.Archetype.Undead => UndeadLegend(Archer, (a, 25)),
                    _ => Legend(Archer, (a, 25)),
                };
                string? mark = CardLine.Compose(m.Name, 25, legend, budget);
                if (mark != null) lines.Add((id, "mark-" + a, mark.TrimEnd()));
            }
        }

        // Global uniqueness.
        var seen = new Dictionary<string, (int id, string kind)>();
        foreach (var (id, kind, text) in lines)
        {
            var key = text.ToLowerInvariant();
            Assert.False(seen.TryGetValue(key, out var prior) && prior.id != id,
                $"weapon {id} ({kind}) collides with weapon {(seen.TryGetValue(key, out var p) ? p.id : -1)}: \"{text}\"");
            seen[key] = (id, kind);
        }

        // Prefix-clean: no distinct-weapon line may be a strict prefix of another.
        for (int i = 0; i < lines.Count; i++)
        {
            for (int j = 0; j < lines.Count; j++)
            {
                if (i == j || lines[i].id == lines[j].id) continue;
                string a = lines[i].text, b = lines[j].text;
                if (a.Length < b.Length && b.StartsWith(a, System.StringComparison.OrdinalIgnoreCase))
                    Assert.Fail($"weapon {lines[i].id} ({lines[i].kind}) \"{a}\" is a prefix of weapon {lines[j].id} ({lines[j].kind}) \"{b}\"");
            }
        }
    }
}
