#!/usr/bin/env python
"""
Code health report, written once per release so the trend builds itself.

Each release gets one record, docs/health/vX.Y.Z.json, holding a snapshot of the production code at
that release and what changed during the cycle since the previous release. docs/HEALTH.md is rebuilt
from those records every time, so never edit it by hand.

What it measures:
  * Snapshot: production and test size, test-to-production ratio, comment share, silent (empty)
    catches, methods over 60 code lines, and duplicated 8-line blocks.
  * Cycle: commits that touched production code, how many read like a repair (called fix-shaped, the
    subject starts with Stop, Fix, Restore, Correct, Guard, Re-anchor... or says bug, crash, wrong,
    no longer), hotspots (cycle commits times branch points), and fix rate per domain folder.
    Commits touching more than 8 production files count as sweeps and are left out of fix counts.
    Renames are followed, so a file moved into a folder keeps its history.

How it runs, so nobody has to remember it:
  * Publish.ps1 calls `report --write` after a package passes verification, which writes the record
    for the version being packaged. In GitHub Actions it runs on tag pushes only and also writes the
    run's step summary.
  * tools/pipeline.ps1 calls `status` on every build (BuildLinked.ps1 and Publish.ps1). It stays
    quiet while a version is in development and prints a warning box when a tagged release has no
    record, or when a record is written but not committed.

Usage:
  python tools/health.py report --version 2.4.0 --write     # write the record and rebuild HEALTH.md
  python tools/health.py report --version 2.4.0             # print the section, write nothing
  python tools/health.py report --version 2.3.4 --ref v2.3.4 --write   # backfill an old release
  python tools/health.py status [--committed] [--strict]    # the reminder; --strict exits 1
  python tools/health.py --selftest
"""
import argparse
import collections
import datetime
import io
import json
import os
import re
import subprocess
import sys
import tarfile

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PROD = "LivingWeapon/"
TESTS = "LivingWeapon.Tests/"
HEALTH_MD = "docs/HEALTH.md"
HEALTH_DIR = "docs/health"
SWEEP = 8
LONG_METHOD = 60
DUP_WINDOW = 8

FIX = re.compile(
    r"^(Stop|Fix|Correct|Restore|Guard|Prevent|Re-anchor|Repair|Revert|Undo)\b"
    r"|\b(fix(es|ed)?|bug|crash\w*|regress\w*|broken|wrong|hotfix|no longer)\b"
    r"|^Keep .*\b(from|instead|honest|alive|working|watching|painting|inside|when|after|across|through)\b", re.I)
BRANCH = re.compile(r"\b(if|for|foreach|while|case|catch)\b|&&|\|\|")
EMPTY_CATCH = re.compile(r"catch(\s*\([^)]*\))?\s*\{\s*(/\*[^*]*\*/)?\s*\}")
CATCH = re.compile(r"\bcatch\b")
MODS = r"(?:public|internal|private|protected|static|override|virtual|sealed|async|unsafe|extern|new|partial)"
METHOD = re.compile(r"^\s*(?:%s\s+)+(?:[\w<>\[\],?. ]+\s+)?(\w+)\s*\([^;]*$" % MODS)


def git(*args, check=True):
    out = subprocess.run(["git", "-C", REPO] + list(args), capture_output=True)
    if check and out.returncode != 0:
        raise RuntimeError("git %s failed: %s" % (" ".join(args), out.stderr.decode("utf-8", "replace").strip()))
    return out.stdout.decode("utf-8", "replace")


def vkey(tag):
    """(2, 3, 4) for v2.3.4 or 2.3.4; None for anything that is not a release version."""
    m = re.fullmatch(r"v?(\d+)\.(\d+)\.(\d+)", tag.strip())
    return tuple(int(x) for x in m.groups()) if m else None


def release_tags():
    return sorted((t for t in git("tag", "-l", "v*").split() if vkey(t)), key=vkey)


def pick_previous(version, tags, is_ancestor):
    """The newest release tag older than version that is an ancestor of the report's ref."""
    want = vkey(version)
    older = [t for t in tags if vkey(t) < want]
    for t in reversed(older):
        if is_ancestor(t):
            return t
    return None


def read_tree(ref):
    """Every production and test .cs file at ref, as {path: text}."""
    raw = subprocess.run(["git", "-C", REPO, "archive", ref, PROD.rstrip("/"), TESTS.rstrip("/")],
                         capture_output=True)
    if raw.returncode != 0:
        raise RuntimeError("git archive %s failed: %s" % (ref, raw.stderr.decode("utf-8", "replace")))
    files = {}
    with tarfile.open(fileobj=io.BytesIO(raw.stdout), mode="r:") as tar:
        for member in tar.getmembers():
            if member.isfile() and member.name.endswith(".cs"):
                files[member.name] = tar.extractfile(member).read().decode("utf-8-sig", "replace")
    return files


def is_comment(line):
    return line.strip().startswith(("//", "/*", "*"))


def code_lines(text):
    return [l for l in text.split("\n") if l.strip() and not is_comment(l)]


def branch_points(text):
    return len(BRANCH.findall("\n".join(code_lines(text))))


def method_lengths(text):
    """Code-line length of each method and constructor body, by brace matching."""
    lines = text.split("\n")
    out, i = [], 0
    while i < len(lines):
        m = METHOD.match(lines[i])
        if not m or "=>" in lines[i] or is_comment(lines[i]):
            i += 1
            continue
        depth, started, j = 0, False, i
        while j < len(lines):
            bare = re.sub(r'"(?:\\.|[^"\\])*"|//.*', "", lines[j])
            for ch in bare:
                if ch == "{":
                    depth += 1
                    started = True
                elif ch == "}":
                    depth -= 1
            if started and depth <= 0:
                break
            if not started and bare.strip().endswith(";"):
                break
            j += 1
        if started:
            out.append((m.group(1), len(code_lines("\n".join(lines[i:j + 1])))))
            i = j + 1
        else:
            i += 1
    return out


def duplicated_share(texts):
    """Share of code lines sitting inside an 8-line block that appears more than once."""
    windows = collections.defaultdict(list)
    norm = {}
    for path, text in texts.items():
        rows = [re.sub(r"\s+", " ", l).strip() for l in code_lines(text)]
        rows = [r for r in rows if len(r) > 3 and not r.startswith(("using ", "{", "}"))]
        norm[path] = rows
        for k in range(len(rows) - DUP_WINDOW + 1):
            windows[tuple(rows[k:k + DUP_WINDOW])].append((path, k))
    dup = set()
    for spots in windows.values():
        if len(spots) > 1:
            for path, k in spots:
                dup.update((path, k + t) for t in range(DUP_WINDOW))
    total = sum(len(r) for r in norm.values())
    return (len(dup) / total) if total else 0.0


def domain_of(path):
    parts = path[len(PROD):].split("/")
    return parts[0] if len(parts) > 1 else "(root)"


def dev_only(text):
    """True for a file wrapped whole in #if LWDEV, since it compiles out of release builds."""
    first = next((l.strip() for l in text.split("\n") if l.strip()), "")
    return first.startswith("#if LWDEV")


def snapshot(files):
    prod = {p: t for p, t in files.items() if p.startswith(PROD)}
    tests = {p: t for p, t in files.items() if p.startswith(TESTS)}
    prod_lines = sum(len(t.split("\n")) for t in prod.values())
    test_lines = sum(len(t.split("\n")) for t in tests.values())
    comments = sum(sum(1 for l in t.split("\n") if is_comment(l)) for t in prod.values())
    methods = [n for t in prod.values() for _, n in method_lengths(t)]
    return {
        "prod_files": len(prod),
        "prod_lines": prod_lines,
        "code_lines": sum(len(code_lines(t)) for t in prod.values()),
        "test_files": len(tests),
        "test_lines": test_lines,
        "test_ratio": round(test_lines / prod_lines, 2) if prod_lines else 0,
        "comment_share": round(comments / prod_lines, 3) if prod_lines else 0,
        "empty_catches": sum(len(EMPTY_CATCH.findall(t)) for t in prod.values()),
        "catches": sum(len(CATCH.findall(t)) for t in prod.values()),
        "methods": len(methods),
        "long_methods": sum(1 for n in methods if n > LONG_METHOD),
        "duplication": round(duplicated_share(prod), 4),
    }


def cycle(since, ref, prod_files):
    """Commits in since..ref, credited to each production file under its name at ref."""
    rng = "%s..%s" % (since, ref) if since else ref
    raw = git("log", rng, "--no-merges", "-M", "--name-status", "--format=@@%H\t%s")
    alias = {f: f for f in prod_files}
    per = collections.defaultdict(lambda: [0, 0])
    total = prod_commits = fix_commits = 0
    for block in raw.split("@@")[1:]:
        lines = block.strip("\n").split("\n")
        subject = lines[0].split("\t", 1)[1] if "\t" in lines[0] else ""
        touched = set()
        for row in lines[1:]:
            parts = row.split("\t")
            if len(parts) < 2:
                continue
            if parts[0].startswith("R") and len(parts) >= 3:
                cur = alias.get(parts[2])
                if cur:
                    alias[parts[1]] = cur
            else:
                cur = alias.get(parts[-1])
            if cur:
                touched.add(cur)
        total += 1
        if not touched:
            continue
        prod_commits += 1
        is_fix = len(touched) <= SWEEP and bool(FIX.search(subject))
        fix_commits += is_fix
        for f in touched:
            per[f][0] += 1
            per[f][1] += is_fix
    return total, prod_commits, fix_commits, per


def build_record(version, ref):
    tags = release_tags()
    since = pick_previous(version, tags, lambda t: subprocess.run(
        ["git", "-C", REPO, "merge-base", "--is-ancestor", t, ref], capture_output=True).returncode == 0)
    files = read_tree(ref)
    prod = {p: t for p, t in files.items() if p.startswith(PROD)}
    total, prod_commits, fix_commits, per = cycle(since, ref, set(prod))
    # Dev spikes compile out of release builds, so they never rank as hotspots. Ties break on the
    # path so a record rebuilt later comes out the same.
    hot = sorted(((p, c, fx, branch_points(prod[p]), len(code_lines(prod[p])))
                  for p, (c, fx) in per.items() if not dev_only(prod[p])),
                 key=lambda r: (-r[1] * max(r[3], 1), r[0]))
    domains = collections.defaultdict(lambda: [0, 0, 0, 0])
    for p, text in prod.items():
        d = domains[domain_of(p)]
        d[0] += 1
        d[1] += len(code_lines(text))
        c, fx = per.get(p, (0, 0))
        d[2] += c
        d[3] += fx
    date = git("log", "-1", "--format=%cs", ref).strip()
    return {
        "version": version.lstrip("v"),
        "ref": ref,
        "sha": git("rev-parse", "--short=8", ref + "^{commit}").strip(),
        "date": date,
        "since": since,
        "snapshot": snapshot(files),
        "cycle": {
            "commits": total,
            "prod_commits": prod_commits,
            "fix_commits": fix_commits,
            "fix_share": round(fix_commits / prod_commits, 3) if prod_commits else 0,
        },
        "hotspots": [{"file": p[len(PROD):], "commits": c, "fixes": fx, "branches": b, "code": n}
                     for p, c, fx, b, n in hot[:10]],
        "domains": [{"domain": k, "files": v[0], "code": v[1], "changes": v[2], "fixes": v[3]}
                    for k, v in sorted(domains.items(), key=lambda kv: (-kv[1][2], kv[0]))],
    }


def load_records(base=REPO):
    folder = os.path.join(base, HEALTH_DIR)
    recs = []
    if os.path.isdir(folder):
        for name in os.listdir(folder):
            if name.endswith(".json") and vkey(name[:-5]):
                with open(os.path.join(folder, name), encoding="utf-8") as h:
                    recs.append(json.load(h))
    return sorted(recs, key=lambda r: vkey(r["version"]), reverse=True)


def pct(x, digits=0):
    return ("%." + str(digits) + "f%%") % (100 * x)


def num(x):
    return "{:,}".format(x)


def change(now, before, fmt, better_low):
    if before is None:
        return ""
    diff = now - before
    if fmt(abs(diff)) == fmt(0):
        return "same"
    arrow = "up" if diff > 0 else "down"
    if better_low is None:
        return "%s %s" % (arrow, fmt(abs(diff)))
    good = (diff < 0) == better_low
    return "%s %s%s" % (arrow, fmt(abs(diff)), "" if good else " (worse)")


def points(x, digits=0):
    return ("%." + str(digits) + "f pts") % (100 * x)


# label, record key, value format, change format, lower is better (None means neutral)
SNAPSHOT_ROWS = [
    ("Production code lines (not blank, not comment)", "code_lines", num, num, None),
    ("Test lines per production line", "test_ratio", lambda x: "%.2f" % x, lambda x: "%.2f" % x, False),
    ("Comment share", "comment_share", lambda x: pct(x), lambda x: points(x), None),
    ("Silent (empty) catches", "empty_catches", num, num, True),
    ("Methods over %d code lines" % LONG_METHOD, "long_methods", num, num, True),
    ("Code in repeated 8-line blocks", "duplication", lambda x: pct(x, 1), lambda x: points(x, 1), True),
]

# A patch release of a commit or two says nothing about fix rate, so "last cycle" means the most
# recent earlier release with at least this many production commits.
COMPARABLE_CYCLE = 10


def comparable(records, index):
    for r in records[index + 1:]:
        if r["cycle"]["prod_commits"] >= COMPARABLE_CYCLE:
            return r
    return None


def render_section(rec, prev, cmp=None):
    s, c = rec["snapshot"], rec["cycle"]
    ps = prev["snapshot"] if prev else {}
    out = ["## v%s, %s" % (rec["version"], rec["date"]), ""]
    span = "since %s" % rec["since"] if rec["since"] else "from the first commit"
    lead = ("%s production commit%s %s, %s of them fix-shaped" % (
        num(c["prod_commits"]), "" if c["prod_commits"] == 1 else "s", span, pct(c["fix_share"])))
    if cmp:
        lead += " (%s in v%s)" % (pct(cmp["cycle"]["fix_share"]), cmp["version"])
    top = rec["hotspots"][0]["file"] if rec["hotspots"] else None
    out.append(lead + "." + (" Top hotspot: %s." % top if top else ""))
    out += ["", "| Measure | This release | Change since last |", "| --- | --- | --- |"]
    for label, key, fmt, dfmt, better_low in SNAPSHOT_ROWS:
        delta = change(s[key], ps.get(key), dfmt, better_low)
        out.append("| %s | %s | %s |" % (label, fmt(s[key]), delta))
    out += ["", "Hotspots this cycle (commits times branch points):", "",
            "| File | Commits | Fix-shaped | Branch points | Code lines |", "| --- | --- | --- | --- | --- |"]
    for h in rec["hotspots"]:
        out.append("| %s | %d | %d | %d | %d |" % (h["file"], h["commits"], h["fixes"], h["branches"], h["code"]))
    out += ["", "Domains this cycle:", "",
            "| Domain | Files | Code lines | Changes | Fix-shaped | Fix rate |", "| --- | --- | --- | --- | --- | --- |"]
    for d in rec["domains"]:
        rate = pct(d["fixes"] / d["changes"]) if d["changes"] else "-"
        out.append("| %s | %d | %s | %d | %d | %s |" % (d["domain"], d["files"], num(d["code"]), d["changes"], d["fixes"], rate))
    out.append("")
    return out


def render_md(records):
    out = ["# Code Health", "",
           "STATUS: CONTRACT (generated by tools/health.py at every release; never edit by hand)", "",
           "Written by tools/health.py once per release (Publish.ps1 runs it) and rebuilt from docs/health/*.json.",
           "Do not edit this file by hand; the next release overwrites it. tools/health.py explains every measure.", "",
           "## Trend", "",
           "| Release | Date | Code lines | Tests per line | Comments | Silent catches | Long methods | Repeated code | Fix-shaped of commits | Top hotspot |",
           "| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |"]
    for r in records:
        s = r["snapshot"]
        top = r["hotspots"][0]["file"] if r["hotspots"] else "-"
        out.append("| v%s | %s | %s | %.2f | %s | %d | %d | %s | %s | %s |" % (
            r["version"], r["date"], num(s["code_lines"]), s["test_ratio"], pct(s["comment_share"]),
            s["empty_catches"], s["long_methods"], pct(s["duplication"], 1),
            "%s of %d" % (pct(r["cycle"]["fix_share"]), r["cycle"]["prod_commits"]), top))
    out.append("")
    for k, r in enumerate(records):
        out += render_section(r, records[k + 1] if k + 1 < len(records) else None, comparable(records, k))
    return "\n".join(out)


def banner(lines, warn):
    width = max(len(l) for l in lines) + 4
    edge = "!" * width if warn else "=" * width
    return "\n".join([edge] + ["| " + l.ljust(width - 4) + " |" for l in lines] + [edge])


def cmd_report(args):
    version = (args.version or mod_version()).lstrip("v")
    ref = args.ref or "HEAD"
    rec = build_record(version, ref)
    records = [r for r in load_records() if r["version"] != rec["version"]] + [rec]
    records.sort(key=lambda r: vkey(r["version"]), reverse=True)
    k = [r["version"] for r in records].index(rec["version"])
    prev = records[k + 1] if k + 1 < len(records) else None
    cmp = comparable(records, k)
    section = "\n".join(render_section(rec, prev, cmp))
    if args.write:
        os.makedirs(os.path.join(REPO, HEALTH_DIR), exist_ok=True)
        with open(os.path.join(REPO, HEALTH_DIR, "v%s.json" % rec["version"]), "w", encoding="utf-8", newline="\n") as h:
            json.dump(rec, h, indent=1)
            h.write("\n")
        with open(os.path.join(REPO, HEALTH_MD), "w", encoding="utf-8", newline="\n") as h:
            h.write(render_md(records))
    if args.summary:
        with open(args.summary, "a", encoding="utf-8") as h:
            h.write("# Code health, v%s\n\n%s\n" % (rec["version"], section))
    s, c = rec["snapshot"], rec["cycle"]
    lines = ["CODE HEALTH v%s (%s, since %s)" % (rec["version"], rec["sha"], rec["since"] or "the start"),
             "%d production commits, %s fix-shaped%s" % (c["prod_commits"], pct(c["fix_share"]),
                 " (%s in v%s)" % (pct(cmp["cycle"]["fix_share"]), cmp["version"]) if cmp else ""),
             "Top hotspot: %s" % (rec["hotspots"][0]["file"] if rec["hotspots"] else "-"),
             "Silent catches %d, long methods %d, repeated code %s" % (s["empty_catches"], s["long_methods"], pct(s["duplication"], 1))]
    if args.write:
        lines.append("Wrote %s and %s/v%s.json. Commit them with the release." % (HEALTH_MD, HEALTH_DIR, rec["version"]))
    print(banner(lines, warn=False))
    if not args.write:
        print("\n" + section)
    return 0


def mod_version():
    with open(os.path.join(REPO, "mod", "ModConfig.json"), encoding="utf-8-sig") as h:
        return json.load(h)["ModVersion"]


def status_problems(tags, have, uncommitted, first_tracked):
    """What the reminder must say. tags are the release tags, have the versions with a record,
    uncommitted the versions whose record exists but is not committed, and first_tracked the oldest
    version with a record (older tags are history, not debt)."""
    problems = []
    if first_tracked is None:
        return ["No health records yet. Run: python tools/health.py report --write"]
    for t in tags:
        v = t.lstrip("v")
        if vkey(v) >= vkey(first_tracked) and v not in have:
            problems.append("%s was released with no health record. Run: python tools/health.py report --version %s --ref %s --write" % (t, v, t))
    if uncommitted:
        names = ", ".join("v" + v for v in sorted(uncommitted, key=vkey))
        problems.append("Written but not committed: %s. Commit docs/health and docs/HEALTH.md." % names)
    return problems


def cmd_status(args):
    tags = release_tags()
    if args.committed:
        listing = git("ls-tree", "--name-only", "HEAD", HEALTH_DIR + "/", check=False).split()
        have = {os.path.basename(p)[:-5].lstrip("v") for p in listing if p.endswith(".json") and vkey(os.path.basename(p)[:-5])}
        uncommitted = set()
    else:
        have = {r["version"] for r in load_records()}
        dirty = git("status", "--porcelain", "--untracked-files=all", "--", HEALTH_DIR, HEALTH_MD, check=False).splitlines()
        uncommitted = {m.group(1) for l in dirty for m in [re.search(r"v(\d+\.\d+\.\d+)\.json", l)] if m}
    first = min(have, key=vkey) if have else None
    problems = status_problems(tags, have, uncommitted, first)
    if problems:
        print(banner(["CODE HEALTH REMINDER"] + problems, warn=True))
        if os.environ.get("GITHUB_ACTIONS"):
            for p in problems:
                print("::warning title=Code health::%s" % p)
        return 1 if args.strict else 0
    latest = max(have, key=vkey)
    print("  -> Code health: last record v%s. The next one is written when Publish.ps1 packages v%s." % (latest, mod_version()))
    return 0


def selftest():
    cases = []

    def check(name, ok):
        cases.append((name, bool(ok)))

    check("vkey orders releases", sorted(["v2.10.0", "v2.3.4", "v2.3.10"], key=vkey) == ["v2.3.4", "v2.3.10", "v2.10.0"])
    check("vkey refuses a non-release tag", vkey("v2.4.0-beta") is None)
    tags = ["v2.3.3", "v2.3.4", "v2.4.0"]
    check("previous release skips the version itself", pick_previous("2.4.0", tags, lambda t: True) == "v2.3.4")
    check("previous release must be an ancestor", pick_previous("2.4.0", tags, lambda t: t != "v2.3.4") == "v2.3.3")
    check("no previous release before the first", pick_previous("2.3.3", tags, lambda t: True) is None)
    for s in ["Stop a Retry from paying a kill twice", "Fix the twin grant on 1.5.2",
              "Keep new weapons in the bag when a battle is fled or retried",
              "Re-anchor the roster read", "The banner no longer shows a battle late"]:
        check("fix-shaped: " + s, FIX.search(s))
    for s in ["Add a probe for the status engine", "Tick the roster drill box",
              "Move the Grows line up", "Keep the readme current"]:
        check("not fix-shaped: " + s, not FIX.search(s))
    code = "class A {\n  // if this\n  void M() {\n    if (a && b) { x(); }\n    try { y(); } catch (Exception) { }\n  }\n}\n"
    check("branch points skip comments", branch_points(code) == 3)
    check("empty catch found", len(EMPTY_CATCH.findall(code)) == 1)
    check("empty catch with a note found", len(EMPTY_CATCH.findall("catch (Exception) { /* why */ }")) == 1)
    check("a catch with a body is not empty", not EMPTY_CATCH.findall("catch (Exception ex) { Log(ex); }"))
    long_body = "  internal void Big()\n  {\n" + "".join("    x%d();\n" % i for i in range(70)) + "  }\n"
    ctor = "  public Engine(int a,\n      int b)\n  {\n    a = b;\n  }\n"
    lens = dict(method_lengths(long_body + ctor))
    check("long method measured", lens.get("Big", 0) > LONG_METHOD)
    check("constructor measured", "Engine" in lens and lens["Engine"] < 10)
    block = "".join("int v%d = %d;\n" % (i, i) for i in range(10))
    check("repeated block found", duplicated_share({"a.cs": block, "b.cs": block}) == 1.0)
    check("unique code not repeated", duplicated_share({"a.cs": block, "b.cs": block.replace("int", "long")}) == 0.0)
    check("a whole-file LWDEV spike is dev only", dev_only("\n#if LWDEV\nusing System;\n"))
    check("a normal file is not dev only", not dev_only("using System;\n#if LWDEV\n"))
    check("domain of a root file", domain_of(PROD + "Engine.cs") == "(root)")
    check("domain of a folder file", domain_of(PROD + "Kills/KillTracker.cs") == "Kills")
    check("status asks for a first record", status_problems(tags, set(), set(), None))
    check("status quiet when every tracked tag has a record", not status_problems(tags, {"2.3.3", "2.3.4", "2.4.0"}, set(), "2.3.3"))
    check("status flags a released tag with no record", any("v2.4.0" in p for p in status_problems(tags, {"2.3.3", "2.3.4"}, set(), "2.3.3")))
    check("status ignores tags older than the first record", not status_problems(["v1.0.0", "v2.3.4"], {"2.3.4"}, set(), "2.3.4"))
    check("status flags an uncommitted record", any("not committed" in p for p in status_problems(tags, {"2.3.3", "2.3.4", "2.4.0"}, {"2.4.0"}, "2.3.3")))
    rec = {"version": "2.4.0", "date": "2026-10-20", "since": "v2.3.4", "sha": "abc",
           "snapshot": {"code_lines": 100, "test_ratio": 2.0, "comment_share": 0.2, "empty_catches": 4,
                        "long_methods": 1, "duplication": 0.01},
           "cycle": {"prod_commits": 10, "fix_commits": 2, "fix_share": 0.2},
           "hotspots": [{"file": "Engine.cs", "commits": 5, "fixes": 1, "branches": 9, "code": 50}],
           "domains": [{"domain": "(root)", "files": 1, "code": 50, "changes": 5, "fixes": 1}]}
    old = json.loads(json.dumps(rec))
    old["version"], old["snapshot"]["empty_catches"] = "2.3.4", 2
    md = render_md([rec, old])
    check("trend has a row per release", "| v2.4.0 |" in md and "| v2.3.4 |" in md)
    check("a rise in silent catches reads as worse", "up 2 (worse)" in md)
    check("first release shows no change column", md.count("## v") == 2)
    tiny = json.loads(json.dumps(old))
    tiny["version"], tiny["cycle"]["prod_commits"], tiny["cycle"]["fix_share"] = "2.3.3", 1, 0.0
    check("a tiny patch cycle is skipped for comparison", comparable([rec, tiny, old], 0) is old)
    check("share changes read in points", change(0.24, 0.38, points, None) == "down 14 pts")
    check("a change below display precision reads same", change(0.0080, 0.0083, lambda x: points(x, 1), True) == "same")
    passed = sum(ok for _, ok in cases)
    for name, ok in cases:
        if not ok:
            print("FAIL: " + name)
    print("health selftest: %d/%d passed." % (passed, len(cases)))
    return 0 if passed == len(cases) else 1


def main():
    if "--selftest" in sys.argv[1:]:
        return selftest()
    ap = argparse.ArgumentParser(description="Per-release code health report.")
    sub = ap.add_subparsers(dest="cmd", required=True)
    rp = sub.add_parser("report")
    rp.add_argument("--version", help="release version, default mod/ModConfig.json ModVersion")
    rp.add_argument("--ref", help="commit or tag to measure, default HEAD")
    rp.add_argument("--write", action="store_true", help="write the record and rebuild docs/HEALTH.md")
    rp.add_argument("--summary", help="also append the section to this markdown file (GitHub step summary)")
    sp = sub.add_parser("status")
    sp.add_argument("--committed", action="store_true", help="judge the committed tree, not the working tree")
    sp.add_argument("--strict", action="store_true", help="exit 1 when there is something to remind about")
    args = ap.parse_args()
    if args.cmd == "report":
        return cmd_report(args)
    try:
        return cmd_status(args)
    except (RuntimeError, OSError, ValueError, KeyError) as exc:
        # The reminder must never stop a build; say why it could not look and move on.
        print("  -> Code health reminder could not run: %s" % exc)
        return 0


if __name__ == "__main__":
    sys.exit(main())
