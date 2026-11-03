#!/usr/bin/env python
"""A one second read of the comment and style rules, so a slip shows the moment it is written.

  python gates/tools/style_scan.py                 # the whole repo, as the contract tests read it
  python gates/tools/style_scan.py FILE [FILE...]  # only these files, each under the rules that cover it

Prints file, line and rule for every hit and exits 1; prints nothing and exits 0 when clean. The C#
contract tests stay the verdict. This is the same text rules answered in a second: a colon, a dash
or a tell phrase inside a production comment, a ticket id or review talk in any comment, a first
summary sentence over 110 characters, a comment budget past its frozen row, comment lines over 100
past the style.json count, Python layout, C# double blank lines, doc prose over 100 past the
style.json count, and an em dash in the ledger. The shared setup runs it after every edit through
its PostToolUse hook, and a build may run it before dotnet test. Where things are comes from
rules.json at the repo root, the same file the tests read. The rules themselves live in
style_rules.py beside this file.
"""
import json
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from style_rules import WIDTH, FIRST, comment_refs, first_sentence, measure, overlong_prose, partial_type, python_layout

NEW_RATIO, NEW_BLOCK = 1 / 3, 8
PRUNED = {"bin", "obj", ".git", ".claude", "__pycache__", "gates", "TestResults"}
BUDGET_HINT = "docs/health/files.json holds the row; health.py floor --write moves a shrunk or new one"
STYLE_HINT = "docs/health/style.json holds the count, hand written"


class Repo:
    """The rules, baselines and file lists one scan reads, loaded once."""

    def __init__(self, root):
        self.root = root
        with open(os.path.join(root, "rules.json"), encoding="utf-8-sig") as h:
            rules = json.load(h)
        self.prod, self.tests = rules["prod"].strip("/") + "/", rules["tests"].strip("/") + "/"
        self.py_prod = [f.strip("/") + "/" for f in rules.get("pythonProd", [])]
        self.em_dash = [e.strip("/") + ("/" if os.path.isdir(os.path.join(root, e.strip("/"))) else "") for e in rules.get("emDashScope", [])]
        self.prefix, self.exempt = rules.get("ticketPrefix", "XX"), rules.get("noSummaryExempt", {})
        self.pruned = PRUNED | set(rules.get("prunedDirs", []))
        base = os.path.join(root, rules.get("baselines", "docs/health"))
        self.rows = self.load(os.path.join(base, "files.json"), {"columns": [], "files": {}})
        self.style = {k.lower(): v for k, v in self.load(os.path.join(base, "style.json"), {}).items()}
        self.ledger_head = re.compile(r"^- \*\*\[%s-\d+\] .*\(opened \d{4}-\d{2}-\d{2}\)" % re.escape(self.prefix))
        self.cache = {}

    @staticmethod
    def load(path, default):
        if not os.path.isfile(path):
            return default
        with open(path, encoding="utf-8-sig") as h:
            return json.load(h)

    def walk(self, folder, suffix, deep=True):
        top = os.path.join(self.root, folder)
        for here, dirs, names in os.walk(top):
            dirs[:] = [d for d in dirs if d not in self.pruned] if deep else []
            for n in sorted(names):
                if n.endswith(suffix):
                    yield os.path.relpath(os.path.join(here, n), self.root).replace(os.sep, "/")

    def text(self, rel):
        if rel not in self.cache:
            with open(os.path.join(self.root, rel), encoding="utf-8-sig", errors="replace") as h:
                self.cache[rel] = h.read().replace("\r\n", "\n")
        return self.cache[rel]

    def every_file(self):
        """The files the whole repo scan reads, the same set the contract tests walk."""
        out = set(self.walk(self.prod, ".cs")) | set(self.walk(self.tests, ".cs")) | set(self.walk("tools", (".cs", ".py")))
        out |= set(self.walk("tools", ".ps1", deep=False)) | set(self.walk(".", ".ps1", deep=False))
        out |= set(self.walk("docs", ".md", deep=False)) | {"README.md"}
        for folder in self.py_prod:
            out |= set(self.walk(folder, ".py"))
        for entry in self.em_dash:
            out |= set(self.walk(entry, "")) if entry.endswith("/") else {entry}
        return sorted(r for r in out if os.path.isfile(os.path.join(self.root, r)))

    def python_prod(self, rel):
        return rel.endswith(".py") and any(rel.startswith(f) for f in self.py_prod)

    def in_em_dash_scope(self, rel):
        return any(rel == e or (e.endswith("/") and rel.startswith(e)) for e in self.em_dash)

    def row_key(self, rel):
        name = partial_type(self.text(rel))
        return "partial " + name if name else rel[len(self.prod):]

    def row(self, key):
        if key not in self.rows.get("files", {}):
            return None
        return dict(zip(self.rows["columns"], self.rows["files"][key]))


def budget(repo, rel, python=False):
    """The frozen comment budget of the row rel belongs to, every part of a partial type summed."""
    key = rel if python else repo.row_key(rel)
    parts = [r for r in repo.walk(repo.prod, ".cs") if repo.row_key(r) == key] if key.startswith("partial ") else [rel]
    comment = code = block = 0
    for part in parts:
        c, k, b, _ = measure(repo.text(part), python)
        comment, code, block = comment + c, code + k, max(block, b)
    row, name = repo.row(key), key if len(parts) == 1 else "%s (%s)" % (key, ", ".join(parts))
    if row is None:
        if code and comment / code > NEW_RATIO:
            yield 1, "%s is new with %d comment lines over %d code lines, the rule is one in three; %s" % (name, comment, code, BUDGET_HINT)
        if block > NEW_BLOCK:
            yield 1, "%s is new with a comment block of %d lines, the rule is %d; %s" % (name, block, NEW_BLOCK, BUDGET_HINT)
        return
    if comment > row["comments"]:
        yield 1, "%s has %d comment lines, its row allows %d; %s" % (name, comment, row["comments"], BUDGET_HINT)
    if block > row["block"]:
        yield 1, "%s has a comment block of %d lines, its row allows %d; %s" % (name, block, row["block"], BUDGET_HINT)


def scan_file(repo, rel):
    text, name = repo.text(rel), os.path.basename(rel)
    if "\0" in text:
        return
    lines = text.split("\n")
    if repo.in_em_dash_scope(rel):
        yield from ((n, "an em dash inside the repo's em dash scope; write a comma or a period") for n, l in enumerate(lines, 1) if "\u2014" in l)
    prod, tests, py = rel.startswith(repo.prod), rel.startswith(repo.tests), repo.python_prod(rel)
    under_tools, top = rel.startswith("tools/"), "/" not in rel
    if (rel.endswith(".cs") and prod) or py:
        yield from measure(text, py)[3]
        yield from budget(repo, rel, py)
        first = first_sentence(text, py)
        if first is None and name not in repo.exempt and name != "AssemblyInfo.cs":
            yield 1, "no %s; open the file with one short plain sentence" % ("module docstring" if py else "summary block")
        elif first is not None and (len(first) > FIRST or not first.endswith(".")):
            yield 1, "first sentence is %d characters, the rule is %d ending in a period" % (len(first), FIRST)
    if rel.endswith(".cs") and prod:
        over = [n for n, l in enumerate(lines, 1) if l.lstrip().startswith("//") and len(l) > WIDTH]
        frozen = repo.style.get("comments", {}).get(name, 0)
        if len(over) > frozen:
            yield from ((n, "comment line over %d characters, %d of them against %d frozen; %s" % (WIDTH, len(over), frozen, STYLE_HINT)) for n in over)
    if rel.endswith(".cs") and (prod or tests or under_tools):
        yield from ((n + 1, "two blank lines in a row") for n in range(1, len(lines) - 1) if not lines[n].strip() and not lines[n - 1].strip())
    if (rel.endswith(".cs") and (prod or tests)) or py or (rel.endswith((".py", ".ps1")) and (top or (rel.count("/") == 1 and under_tools))):
        if name != "CommentContractTests.cs":
            yield from comment_refs(rel, lines)
    if rel.endswith(".py") and (under_tools or py):
        yield from python_layout(lines)
    if rel.endswith(".md") and (rel == "README.md" or (rel.startswith("docs/") and rel.count("/") == 1 and name != "HEALTH.md")):
        over, frozen = overlong_prose(lines, repo.ledger_head), repo.style.get("docs", {}).get(rel, 0)
        if len(over) > frozen:
            yield from ((n, "prose line over %d characters, %d of them against %d frozen; %s" % (WIDTH, len(over), frozen, STYLE_HINT)) for n in over)
        if name in ("TODO.md", "CHANGELOG.md"):
            yield from ((n, "an em dash or a double dash separator in the ledger") for n, l in enumerate(lines, 1) if "\u2014" in l or " -- " in l)


def main(argv):
    root = os.path.abspath(argv[argv.index("--repo") + 1]) if "--repo" in argv else None
    paths = [a for k, a in enumerate(argv) if a != "--repo" and (k == 0 or argv[k - 1] != "--repo")]
    if root is None:
        root = os.path.dirname(os.path.abspath(paths[0])) if paths else os.getcwd()
        while not os.path.isfile(os.path.join(root, "rules.json")):
            if os.path.dirname(root) == root:
                print("style_scan: no rules.json above %s" % (paths[0] if paths else os.getcwd()))
                return 2
            root = os.path.dirname(root)
    repo = Repo(root)
    rels = [os.path.relpath(os.path.abspath(p), root).replace(os.sep, "/") for p in paths] or repo.every_file()
    hits = []
    for rel in rels:
        if os.path.isfile(os.path.join(root, rel)) and not any(part in repo.pruned for part in rel.split("/")[:-1]):
            hits += [(rel, n, what) for n, what in scan_file(repo, rel)]
    for rel, n, what in sorted(set(hits)):
        print("%s:%d: %s" % (rel, n, what))
    return 1 if hits else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
