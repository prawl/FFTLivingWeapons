using System.Text.Json;
using System.Text.RegularExpressions;
using ModGates.Tests;

namespace ModGates.Selftest;

/// <summary>
/// Proves the merge lost nothing. Every fact from the five original copies, listed in
/// docs/merge/originals.json, must land in a shared fact, stay local, or be refused on purpose,
/// as docs/merge/mapping.json says. A shared fact must exist and carry every example case.
/// </summary>
public class MergeLedgerTests
{
    private sealed record Original(string Family, string Repo, string Fact, string[] Cases);

    private sealed record Plan(string Shared, Dictionary<string, string> Renamed, Dictionary<string, string> Local, Dictionary<string, string> Wontfix);

    private static readonly Regex Attr = new(@"\[(Fact|Theory)[^\]]*\]", RegexOptions.Compiled);
    private static readonly Regex Sig = new(@"public\s+(?:static\s+)?(?:async\s+)?\w+\s+(\w+)\s*\(", RegexOptions.Compiled);
    private static readonly Regex Inline = new(@"^\[InlineData\((.*)\)\]\s*(?://.*)?$", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private static string Root => RepoRoot.Locate();

    /// <summary>Ids, prefixes and dates differ between copies without changing the lesson, so they are folded away.</summary>
    private static string Mask(string s)
    {
        s = Regex.Replace(s, @"\b(BB|LW|TM|CC|OC|ZZ|QQ|X)-\d+", "X-N");
        s = Regex.Replace(s, @"\b(BB|LW|TM|CC|OC|ZZ|QQ)\b", "X");
        s = Regex.Replace(s, @"\d{4}-\d{1,2}-\d{1,2}", "DATE");
        return Regex.Replace(s, @"\s+", " ").Trim();
    }

    private static Dictionary<string, List<string>> SharedFacts(string file)
    {
        string text = File.ReadAllText(Path.Combine(Root, file));
        var facts = new Dictionary<string, List<string>>();
        foreach (Match m in Attr.Matches(text))
        {
            var sig = Sig.Match(text, m.Index + m.Length);
            if (!sig.Success) continue;
            string between = text.Substring(m.Index, sig.Index - m.Index);
            var cases = between.Split('\n').Select(l => Inline.Match(l.Trim())).Where(c => c.Success).Select(c => Mask(c.Groups[1].Value)).ToList();
            facts[sig.Groups[1].Value] = cases;
        }
        return facts;
    }

    [Fact]
    public void Every_original_fact_of_a_merged_family_landed_somewhere_with_all_its_cases()
    {
        var originals = JsonSerializer.Deserialize<List<Original>>(File.ReadAllText(Path.Combine(Root, "docs", "merge", "originals.json")), Json)!;
        var plans = JsonSerializer.Deserialize<Dictionary<string, Plan>>(File.ReadAllText(Path.Combine(Root, "docs", "merge", "mapping.json")), Json)!;

        var problems = new List<string>();
        int placed = 0;
        foreach (var (family, plan) in plans)
        {
            var shared = SharedFacts(plan.Shared);
            foreach (var o in originals.Where(o => o.Family == family))
            {
                if (plan.Local.ContainsKey(o.Fact) || plan.Wontfix.ContainsKey(o.Fact)) { placed++; continue; }
                string target = plan.Renamed.TryGetValue(o.Fact, out var renamed) ? renamed : o.Fact;
                if (!shared.TryGetValue(target, out var cases))
                {
                    problems.Add($"{family} {o.Repo} {o.Fact}: no shared fact named {target} in {plan.Shared}");
                    continue;
                }
                foreach (string c in o.Cases.Select(Mask).Where(c => !cases.Contains(c)))
                    problems.Add($"{family} {o.Repo} {o.Fact}: case not carried: {c}");
                placed++;
            }
        }

        int total = originals.Count(o => plans.ContainsKey(o.Family));
        Assert.True(problems.Count == 0, string.Join("\n", problems));
        Assert.Equal(total, placed);
    }

    [Fact]
    public void Every_fact_on_the_shared_side_of_a_merged_family_traces_back_to_an_original_or_is_declared_new()
    {
        var originals = JsonSerializer.Deserialize<List<Original>>(File.ReadAllText(Path.Combine(Root, "docs", "merge", "originals.json")), Json)!;
        var plans = JsonSerializer.Deserialize<Dictionary<string, Plan>>(File.ReadAllText(Path.Combine(Root, "docs", "merge", "mapping.json")), Json)!;
        var orphans = new List<string>();
        foreach (var (family, plan) in plans)
        {
            var known = originals.Where(o => o.Family == family).Select(o => plan.Renamed.TryGetValue(o.Fact, out var r) ? r : o.Fact).ToHashSet();
            foreach (string fact in SharedFacts(plan.Shared).Keys)
                if (!known.Contains(fact) && !plan.Renamed.ContainsKey("+" + fact))
                    orphans.Add($"{family}: {fact} is new; declare it in mapping.json renamed as \"+{fact}\": \"<why>\"");
        }
        Assert.True(orphans.Count == 0, string.Join("\n", orphans));
    }
}
