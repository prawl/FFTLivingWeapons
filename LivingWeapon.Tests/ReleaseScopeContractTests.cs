using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// The RELEASE_SCOPE ledger enforcement gate (docs/RELEASE_SCOPE.md + docs/archive/SMOKE_TEST_2.3.0.md),
/// the TodoContractTests enforcer pattern (LW-84) applied to the ship gate doc. Same
/// walk-up-from-the-test-bin-dir RepoRoot idiom, same section-extraction plus
/// top-level-entry-grouping style as TodoContractTests' SplitSections/GroupTopLevelEntries,
/// adapted to RELEASE_SCOPE.md's checkbox boxes instead of TODO.md's bulleted work items.
///
/// R0. Shape (both docs, every raw line): any line carrying a checkbox token (regex `- \[.?\]`)
///     must match `^- \[[ x]\] ` and carry EXACTLY ONE such token. Catches `- [X]`, `- []`, an
///     indented box, and two boxes packed on one line.
/// R1. An IN-list box naming at least one SHIPPED id and no TODO-open id must be ticked `[x]`
///     (the drift class this gate exists to catch: a box that shipped but was never ticked). The
///     "no open id" clause is a deliberate refinement so a box citing a shipped dependency
///     alongside its own still-open id is not forced to lie. A WONTFIX/RETRACTED-only id never
///     forces a tick (it never shipped). Deferral pointers ("backlog LW-n" / "Backlog LW-n") are
///     exempt: they name where deferred work lives, not a claim about this box's own state, so
///     they never count toward the id set this rule tests. Deliberately scoped to RELEASE_SCOPE's
///     IN region only, never the smoke doc, whose boxes are owner live re-verifications (a shipped
///     TODO exit does not imply the live pass ran).
/// R2. A ticked box (in either doc) naming a TODO-open id, other than through a deferral pointer,
///     is red (a checked box must not lean on unfinished work).
/// R3. Every ticked box (in either doc) cites provenance in its text: a commit hash (with at least
///     one digit, so an all-letter hex-lookalike word like "defaced" does not count) or an ISO
///     date. Vibes do not count.
/// R4. Every LW-id cited anywhere in RELEASE_SCOPE.md or docs/archive/SMOKE_TEST_2.3.0.md, including
///     pointer-form citations, exists in docs/TODO.md or docs/CHANGELOG.md under ANY disposition
///     (no phantom or retired ids).
/// R5. Parser sanity floors (anti-vacuity, the LW-21 lesson): the scope IN region must yield at
///     least 20 boxes and the smoke file at least 40, so a silent parser regression cannot turn
///     every rule above vacuously green.
/// </summary>
public class ReleaseScopeContractTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "docs", "TODO.md")) &&
                Directory.Exists(Path.Combine(dir.FullName, "LivingWeapon")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("repo root (docs/TODO.md + LivingWeapon/) not found above the test bin dir");
    }

    // Any LW-<n> mention, bracketed or bare: used for R4 (phantom ids), where a pointer-form
    // citation still counts (a deferred item must still exist SOMEWHERE in the ledger).
    private static readonly Regex AnyIdRegex = new(@"\bLW-(\d+)\b", RegexOptions.Compiled);

    // A "subject" citation: an LW-<n> mention NOT immediately preceded by the WHOLE word
    // "backlog" (the \b inside the lookbehind keeps an embedded tail like "megabacklog LW-5"
    // from reading as a pointer; casing covers exactly "backlog"/"Backlog", the only two forms
    // the docs ever write). "(backlog LW-47)" points at where deferred work lives; it is not a
    // claim that THIS box's own state depends on LW-47, so R1/R2 must not treat it as one. A box
    // citing the same id both as a pointer and bare still counts it (at least one non-pointer
    // occurrence is enough).
    private static readonly Regex SubjectIdRegex = new(@"(?<!\b[Bb]acklog )\bLW-(\d+)\b", RegexOptions.Compiled);

    // TODO open ids key off the bracketed [LW-<n>] form inside Now + Backlog.
    private static readonly Regex BracketIdRegex = new(@"\[LW-(\d+)\]", RegexOptions.Compiled);

    // CHANGELOG scans key off a bracket sitting at a top-level entry's own head (Multiline ^ over
    // the whole file text), so an id merely mentioned inside another entry's prose (CHANGELOG's
    // LW-63 entry cites "direct LW-7 fuel") never reads as though LW-7 itself exited. Two separate
    // patterns: SHIPPED-only (R1's "did this actually ship" test) versus any disposition (R4's
    // "does this id have a home anywhere" test): a WONTFIX or RETRACTED id must never force a
    // tick, but it is still a KNOWN id, not a phantom one.
    private static readonly Regex ChangelogShippedIdRegex = new(
        @"^- \[LW-(\d+)\] SHIPPED ", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex ChangelogAnyIdRegex = new(
        @"^- \[LW-(\d+)\]", RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex TickedFirstLineRegex = new(@"^- \[x\]", RegexOptions.Compiled);

    // Provenance: an ISO date, or a hex-looking token 7-40 chars long that contains at least one
    // digit (the digit lookahead rejects an all-letter hex-lookalike word like "defaced", which
    // would otherwise false-positive as a commit hash).
    private static readonly Regex ProvenanceRegex = new(
        @"\b(?=[0-9a-f]*\d)[0-9a-f]{7,40}\b|\b\d{4}-\d{2}-\d{2}\b", RegexOptions.Compiled);

    // Checkbox shape (R0): the token itself, and the one well-formed rendering of it.
    private static readonly Regex CheckboxTokenRegex = new(@"- \[.?\]", RegexOptions.Compiled);
    private static readonly Regex WellFormedCheckboxLineRegex = new(@"^- \[[ x]\] ", RegexOptions.Compiled);

    // --- Pure parsing helpers (mirrors TodoContractTests.SplitSections / GroupTopLevelEntries) ---

    /// <summary>Lines strictly between a line starting with "## IN" and the next line starting
    /// with "## DEFERRED" (both header lines excluded).</summary>
    internal static List<string> ExtractInSectionLines(IReadOnlyList<string> lines)
    {
        var result = new List<string>();
        bool inSection = false;
        foreach (var line in lines)
        {
            if (line.StartsWith("## IN")) { inSection = true; continue; }
            if (line.StartsWith("## DEFERRED")) { inSection = false; continue; }
            if (inSection) result.Add(line);
        }
        return result;
    }

    /// <summary>Groups a file's (or a region's) lines into per-box blocks: each block starts at a
    /// top-level `- ` line (column 0, no leading whitespace) and swallows only INDENTED
    /// continuation lines that follow. Unlike TODO.md (entries packed back to back, no
    /// intervening prose), these docs carry free-floating section prose, `### N.` subsection
    /// headers, and blank lines between boxes, so any unindented non-`- ` line (or a blank line)
    /// closes the box instead of being swallowed as its continuation; otherwise a one-line box
    /// (say "Clean DEV redeploy...") would silently absorb the NEXT subsection's entire intro
    /// paragraph, including whatever commit hash or date that paragraph happens to cite, and
    /// falsely read as having its own provenance.</summary>
    internal static List<List<string>> GroupBoxes(IReadOnlyList<string> body)
    {
        var groups = new List<List<string>>();
        List<string>? current = null;
        foreach (var line in body)
        {
            if (line.StartsWith("- "))
            {
                current = new List<string> { line };
                groups.Add(current);
            }
            else if (current is not null && line.Length > 0 && char.IsWhiteSpace(line[0]))
            {
                current.Add(line);
            }
            else
            {
                current = null;
            }
        }
        return groups;
    }

    private static List<List<string>> RealInBoxes(string repoRoot)
    {
        var lines = File.ReadAllLines(Path.Combine(repoRoot, "docs", "RELEASE_SCOPE.md"));
        return GroupBoxes(ExtractInSectionLines(lines));
    }

    private static List<List<string>> RealSmokeBoxes(string repoRoot)
    {
        var lines = File.ReadAllLines(Path.Combine(repoRoot, "docs", "archive", "SMOKE_TEST_2.3.0.md"));
        return GroupBoxes(lines);
    }

    /// <summary>OpenIds: the [LW-&lt;n&gt;] ids on TOP-LEVEL entry lines of TODO.md's Now and
    /// Backlog sections (TodoContractTests' SplitSections + GroupTopLevelEntries, each group's
    /// FIRST line only). Continuation prose is deliberately excluded: a future backlog note
    /// citing a bracketed shipped id in a continuation line (say a cross-reference to [LW-50])
    /// would otherwise mark that shipped id as open, and under R2's strict ticked-open rule that
    /// would red a truthful tick.</summary>
    internal static HashSet<string> OpenIdsFrom(IReadOnlyList<string> todoLines)
    {
        var ids = new HashSet<string>();
        foreach (var (header, body) in TodoContractTests.SplitSections(todoLines))
        {
            if (!header.StartsWith("## Now") && header != "## Backlog") continue;
            foreach (var entry in TodoContractTests.GroupTopLevelEntries(body))
            {
                var m = BracketIdRegex.Match(entry[0]);
                if (m.Success) ids.Add(m.Groups[1].Value);
            }
        }
        return ids;
    }

    private static HashSet<string> TodoOpenIds(string repoRoot)
        => OpenIdsFrom(File.ReadAllLines(Path.Combine(repoRoot, "docs", "TODO.md")));

    private static HashSet<string> ChangelogShippedIds(string repoRoot)
    {
        string text = File.ReadAllText(Path.Combine(repoRoot, "docs", "CHANGELOG.md"));
        return ChangelogShippedIdRegex.Matches(text).Select(m => m.Groups[1].Value).ToHashSet();
    }

    private static HashSet<string> ChangelogAllIds(string repoRoot)
    {
        string text = File.ReadAllText(Path.Combine(repoRoot, "docs", "CHANGELOG.md"));
        return ChangelogAnyIdRegex.Matches(text).Select(m => m.Groups[1].Value).ToHashSet();
    }

    // --- Pure predicates (each Theory/Fact-staged below against synthetic strings) ---

    internal static bool IsTicked(IReadOnlyList<string> box) => TickedFirstLineRegex.IsMatch(box[0]);

    /// <summary>Box TEXT: each line trimmed, joined with a single space, so a citation or a date
    /// wrapped across two lines still reads as one contiguous match.</summary>
    private static string BoxText(IEnumerable<string> boxLines)
        => string.Join(" ", boxLines.Select(l => l.Trim()));

    internal static HashSet<string> CitedIds(IEnumerable<string> boxLines)
        => AnyIdRegex.Matches(BoxText(boxLines)).Select(m => m.Groups[1].Value).ToHashSet();

    /// <summary>Cited ids excluding deferral pointers ("backlog LW-n"): see SubjectIdRegex.</summary>
    internal static HashSet<string> SubjectIds(IEnumerable<string> boxLines)
        => SubjectIdRegex.Matches(BoxText(boxLines)).Select(m => m.Groups[1].Value).ToHashSet();

    internal static bool HasProvenance(IEnumerable<string> boxLines)
        => ProvenanceRegex.IsMatch(BoxText(boxLines));

    internal static bool IsWellFormedCheckboxLine(string line)
    {
        int tokenCount = CheckboxTokenRegex.Matches(line).Count;
        if (tokenCount == 0) return true;
        return tokenCount == 1 && WellFormedCheckboxLineRegex.IsMatch(line);
    }

    /// <summary>R1's core predicate: the box cites at least one shipped subject id and no
    /// still-open subject id (a deferral pointer does not count as citing either).</summary>
    internal static bool MustBeTicked(IEnumerable<string> subjectIds, ISet<string> shippedIds, ISet<string> todoOpenIds)
        => subjectIds.Any(shippedIds.Contains) && !subjectIds.Any(todoOpenIds.Contains);

    /// <summary>R2's core predicate: a ticked box that still names an open subject id.</summary>
    internal static bool TickedNamesOpenId(bool ticked, IEnumerable<string> subjectIds, ISet<string> todoOpenIds)
        => ticked && subjectIds.Any(todoOpenIds.Contains);

    // --- R0: shape, both docs, every raw line ---

    [Theory]
    [InlineData("- [ ] ok", true)]
    [InlineData("- [x] ok", true)]
    [InlineData("- [X] text", false)]                      // capital X is not a well-formed tick
    [InlineData("- [] text", false)]                        // empty box, no space or x
    [InlineData("  - [ ] indented", false)]                 // not column 0
    [InlineData("- [ ] a.  - [ ] b", false)]                 // two tokens packed on one line
    [InlineData("a line with no checkbox token at all", true)]
    public void IsWellFormedCheckboxLine_shape_cases(string line, bool expected)
        => Assert.Equal(expected, IsWellFormedCheckboxLine(line));

    [Fact]
    public void R0_every_checkbox_line_in_both_docs_is_well_formed()
    {
        string repoRoot = RepoRoot();
        var violations = new List<string>();
        foreach (var (name, path) in new[]
        {
            ("RELEASE_SCOPE.md", Path.Combine(repoRoot, "docs", "RELEASE_SCOPE.md")),
            ("SMOKE_TEST_2.3.0.md", Path.Combine(repoRoot, "docs", "archive", "SMOKE_TEST_2.3.0.md")),
        })
        {
            var lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                if (!IsWellFormedCheckboxLine(lines[i]))
                    violations.Add($"{name}:{i + 1}: {lines[i]}");
            }
        }
        Assert.True(violations.Count == 0, "Malformed checkbox lines:\n" + string.Join("\n", violations));
    }

    // --- R1: real-file test (scope IN region only; never the smoke doc) ---

    [Fact]
    public void R1_every_IN_box_citing_a_shipped_id_with_no_open_id_is_ticked()
    {
        string repoRoot = RepoRoot();
        var shippedIds = ChangelogShippedIds(repoRoot);
        var todoIds = TodoOpenIds(repoRoot);
        var boxes = RealInBoxes(repoRoot);
        Assert.NotEmpty(boxes);

        var violations = new List<string>();
        foreach (var box in boxes)
        {
            var subjects = SubjectIds(box);
            if (MustBeTicked(subjects, shippedIds, todoIds) && !IsTicked(box))
                violations.Add(box[0]);
        }

        Assert.True(violations.Count == 0,
            "IN boxes citing a SHIPPED id with no open id, still unticked:\n" + string.Join("\n", violations));
    }

    // --- R2: real-file test (both docs' boxes) ---

    [Fact]
    public void R2_no_ticked_box_in_either_doc_names_a_TODO_open_id()
    {
        string repoRoot = RepoRoot();
        var todoIds = TodoOpenIds(repoRoot);

        var violations = new List<string>();
        foreach (var (name, boxes) in new (string, List<List<string>>)[]
        {
            ("RELEASE_SCOPE.md", RealInBoxes(repoRoot)),
            ("SMOKE_TEST_2.3.0.md", RealSmokeBoxes(repoRoot)),
        })
        {
            Assert.NotEmpty(boxes);
            foreach (var box in boxes)
            {
                var subjects = SubjectIds(box);
                if (TickedNamesOpenId(IsTicked(box), subjects, todoIds))
                    violations.Add($"{name}: {box[0]}");
            }
        }

        Assert.True(violations.Count == 0,
            "Ticked boxes that still name a TODO-open id:\n" + string.Join("\n", violations));
    }

    // --- R3: real-file test (both docs' boxes) ---

    [Fact]
    public void R3_every_ticked_box_in_either_doc_cites_provenance()
    {
        string repoRoot = RepoRoot();

        var violations = new List<string>();
        foreach (var (name, boxes) in new (string, List<List<string>>)[]
        {
            ("RELEASE_SCOPE.md", RealInBoxes(repoRoot)),
            ("SMOKE_TEST_2.3.0.md", RealSmokeBoxes(repoRoot)),
        })
        {
            Assert.NotEmpty(boxes);
            violations.AddRange(boxes.Where(IsTicked).Where(b => !HasProvenance(b)).Select(b => $"{name}: {b[0]}"));
        }

        Assert.True(violations.Count == 0,
            "Ticked boxes with no commit hash or ISO date cited:\n" + string.Join("\n", violations));
    }

    // --- R4: real-file test (both docs, full text, any disposition) ---

    [Fact]
    public void R4_every_LW_id_cited_in_scope_or_smoke_docs_exists_in_TODO_or_CHANGELOG()
    {
        string repoRoot = RepoRoot();
        var known = TodoOpenIds(repoRoot);
        known.UnionWith(ChangelogAllIds(repoRoot));

        var violations = new List<string>();
        foreach (var (name, path) in new[]
        {
            ("RELEASE_SCOPE.md", Path.Combine(repoRoot, "docs", "RELEASE_SCOPE.md")),
            ("SMOKE_TEST_2.3.0.md", Path.Combine(repoRoot, "docs", "archive", "SMOKE_TEST_2.3.0.md")),
        })
        {
            Assert.True(File.Exists(path), $"{name} does not exist");
            string text = File.ReadAllText(path);
            foreach (Match m in AnyIdRegex.Matches(text))
            {
                string id = m.Groups[1].Value;
                if (!known.Contains(id)) violations.Add($"{name}: LW-{id}");
            }
        }

        Assert.True(violations.Count == 0,
            "Phantom or retired LW-ids cited with no home in TODO.md or CHANGELOG.md:\n" + string.Join("\n", violations));
    }

    // --- R5: parser sanity floors (anti-vacuity) ---

    [Fact]
    public void R5_parser_sanity_floors_catch_a_silent_scan_regression()
    {
        string repoRoot = RepoRoot();
        int scopeBoxCount = RealInBoxes(repoRoot).Count;
        int smokeBoxCount = RealSmokeBoxes(repoRoot).Count;

        Assert.True(scopeBoxCount >= 20,
            $"RELEASE_SCOPE.md IN region yielded only {scopeBoxCount} boxes (floor 20); the box parser may be broken");
        Assert.True(smokeBoxCount >= 40,
            $"SMOKE_TEST_2.3.0.md yielded only {smokeBoxCount} boxes (floor 40); the box parser may be broken");
    }

    // --- HasProvenance: staged cases ---

    [Theory]
    [InlineData("Closed 2026-07-14.", true)]
    [InlineData("Shipped e77b9d7.", true)]
    [InlineData("A stray word: defaced.", false)]           // all-letter hex lookalike, no digit
    [InlineData("Bad date shape 2026-7-05.", false)]         // single-digit month, not ISO
    [InlineData("", false)]
    public void HasProvenance_shape_cases(string text, bool expected)
        => Assert.Equal(expected, HasProvenance(new[] { text }));

    // --- SubjectIds: staged deferral-pointer exemption cases ---

    [Theory]
    [InlineData("deferred (backlog LW-47) for later", new string[] { })]
    [InlineData("this rides backlog LW-6 for now", new string[] { })]
    [InlineData("the bare citation is LW-47 itself", new string[] { "47" })]
    [InlineData("capitalized pointer: Backlog LW-47", new string[] { })]
    [InlineData("backlog LW-47 here, and bare LW-47 there", new string[] { "47" })]
    [InlineData("an embedded tail: megabacklog LW-5 stays a subject", new string[] { "5" })]
    [InlineData("Backlog LW-7 stays a pointer", new string[] { })]
    public void SubjectIds_excludes_backlog_deferral_pointers(string text, string[] expectedSubjects)
        => Assert.Equal(expectedSubjects.ToHashSet(), SubjectIds(new[] { text }));

    [Fact]
    public void SubjectIds_pointer_exemption_survives_a_line_wrap()
    {
        // The docs wrap at about 100 cols, so "backlog" can end one line and the id start the
        // next; BoxText's trim-plus-single-space join must keep the pair contiguous so the
        // exemption still applies across the wrap.
        var wrapped = new List<string>
        {
            "- [x] **Murasame id41** signature is DEFERRED out of 2.3.0 (tracked in the backlog",
            "      LW-47); its capstone stays pure-growth for now.",
        };

        Assert.Empty(SubjectIds(wrapped));
        Assert.Contains("47", CitedIds(wrapped));
    }

    // --- OpenIdsFrom: staged continuation-prose exclusion ---

    [Fact]
    public void OpenIdsFrom_ignores_bracketed_ids_cited_only_in_continuation_prose()
    {
        // A continuation line cross-referencing a shipped id ([LW-50] here) must not mark that
        // id as open; only each entry's own first line contributes its id.
        var fakeTodo = new[]
        {
            "# TODO",
            "",
            "## Now (release: 9.9.9)",
            "",
            "- **[LW-1] Some open item** (opened 2026-07-14) [QUEUED]",
            "  - Done means: something; contrast with the shipped [LW-50] fix.",
            "  - Verify: something else.",
            "",
            "## Backlog",
            "",
            "- [LW-2] 2026-07-14: another open item.",
            "  continuation prose also citing [LW-50] must not read as open.",
            "",
            "## Walled",
        };

        var open = OpenIdsFrom(fakeTodo);

        Assert.Contains("1", open);
        Assert.Contains("2", open);
        Assert.DoesNotContain("50", open);
    }

    // --- R1: staged violation, drives MustBeTicked directly ---

    [Fact]
    public void R1_staged_a_box_citing_only_a_shipped_id_forces_a_tick()
    {
        var shippedIds = new HashSet<string> { "99" };
        var todoIds = new HashSet<string> { "50" };

        var citedButUnticked = new List<string> { "- [ ] Some finished thing (LW-99)." };
        var fixedByTicking = new List<string> { "- [x] Some finished thing (LW-99), shipped abc1234 2026-07-01." };
        var citesOpenIdToo = new List<string> { "- [ ] Depends on open work too (LW-99, LW-50)." };

        Assert.True(MustBeTicked(SubjectIds(citedButUnticked), shippedIds, todoIds));
        Assert.False(IsTicked(citedButUnticked), "the drift: R1 forces this box and it is not ticked");

        Assert.True(MustBeTicked(SubjectIds(fixedByTicking), shippedIds, todoIds));
        Assert.True(IsTicked(fixedByTicking), "annotated: forced and ticked");

        Assert.False(MustBeTicked(SubjectIds(citesOpenIdToo), shippedIds, todoIds),
            "an open id present anywhere in the box means R1 does not force a tick");
    }

    [Fact]
    public void R1_staged_a_box_citing_only_a_WONTFIX_or_RETRACTED_id_is_not_forced_to_tick()
    {
        // A WONTFIX/RETRACTED id never enters ChangelogShippedIds (it never shipped), so a box
        // naming only one is never forced to tick, mirroring that real-file exclusion.
        var shippedIds = new HashSet<string>();
        var todoIds = new HashSet<string>();
        var wontfixOnly = new List<string> { "- [ ] Some abandoned idea (LW-27)." };

        Assert.False(MustBeTicked(SubjectIds(wontfixOnly), shippedIds, todoIds));
    }

    [Fact]
    public void R1_staged_a_pointer_form_citation_of_a_shipped_id_does_not_force_a_tick()
    {
        var shippedIds = new HashSet<string> { "82" };
        var todoIds = new HashSet<string>();
        var pointerOnly = new List<string> { "- [ ] See the shipped scout (backlog LW-82) for context." };

        Assert.False(MustBeTicked(SubjectIds(pointerOnly), shippedIds, todoIds));
    }

    // --- R2: staged violation, drives TickedNamesOpenId directly ---

    [Fact]
    public void R2_staged_a_ticked_box_naming_an_open_id_is_flagged()
    {
        var todoIds = new HashSet<string> { "50" };

        var prematurelyTicked = new List<string> { "- [x] Prematurely ticked (LW-50)." };
        var honestlyUnticked = new List<string> { "- [ ] Honestly still open (LW-50)." };

        Assert.True(TickedNamesOpenId(IsTicked(prematurelyTicked), SubjectIds(prematurelyTicked), todoIds));
        Assert.False(TickedNamesOpenId(IsTicked(honestlyUnticked), SubjectIds(honestlyUnticked), todoIds));
    }

    [Fact]
    public void R2_staged_a_pointer_form_citation_of_an_open_id_is_not_flagged()
    {
        var todoIds = new HashSet<string> { "47" };
        var pointerOnly = new List<string> { "- [x] Deferred out of scope (backlog LW-47); handled elsewhere." };

        Assert.False(TickedNamesOpenId(IsTicked(pointerOnly), SubjectIds(pointerOnly), todoIds));
    }

    [Fact]
    public void R2_staged_a_ticked_box_citing_only_a_WONTFIX_id_is_not_flagged()
    {
        var todoIds = new HashSet<string>();          // a WONTFIX id is never open
        var wontfixCited = new List<string> { "- [x] Some abandoned idea, closed (LW-27), 2026-07-06." };

        Assert.False(TickedNamesOpenId(IsTicked(wontfixCited), SubjectIds(wontfixCited), todoIds));
    }

    // --- R3: staged violation, drives HasProvenance directly ---

    [Fact]
    public void R3_staged_a_ticked_box_with_no_provenance_is_flagged()
    {
        var noProof = new List<string> { "- [x] Done, trust me." };
        var hashProof = new List<string> { "- [x] Done (shipped abc1234)." };
        var dateProof = new List<string> { "- [x] Done (closed 2026-07-01)." };

        Assert.False(HasProvenance(noProof));
        Assert.True(HasProvenance(hashProof));
        Assert.True(HasProvenance(dateProof));
    }

    // --- R4: staged violation, drives the known-id membership check directly ---

    [Fact]
    public void R4_staged_a_phantom_id_not_in_TODO_or_CHANGELOG_is_flagged()
    {
        var known = new HashSet<string> { "50", "51" };
        string text = "See LW-50 for the real work and the phantom LW-999 mention.";

        var cited = AnyIdRegex.Matches(text).Select(m => m.Groups[1].Value).ToHashSet();
        var unknown = cited.Where(id => !known.Contains(id)).ToList();

        Assert.Contains("999", unknown);
        Assert.DoesNotContain("50", unknown);
    }
}
