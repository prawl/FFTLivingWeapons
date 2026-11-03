#!/usr/bin/env python
"""The text rules the comment and style contract tests hold, as plain functions over file text.

style_scan.py walks a repo and asks these; nothing here touches the file system. Each function
mirrors one test: measure() the comment budget test's reading of a C# file, first_sentence() its
first summary sentence, comment_part() and comment_refs() the comment contract's ticket and review
scan, python_layout() and overlong_prose() the style contract's layout and width counts.
"""
import os
import re

WIDTH, FIRST = 100, 110
TELL = re.compile(r"load-bearing|deliberately|by design|review blocker|verifier NIT|owner ruling|owner direction|precedent|doctrine", re.I)
TICKET = re.compile(r"\b(R\d+-\d+[a-z]?|[A-Z]{2}-\d+)\b")
REVIEW = re.compile(r"\b(round\s?\d+|reviewer|verifier)\b", re.I)
ROUND_NAME = re.compile(r"(?<![a-z])round\d+", re.I)
TYPE_NAME = re.compile(r"\b(class|record|struct|interface|enum)\s+(\w+)")
TOP_DEF = re.compile(r"^(def |async def |class |@)")
TYPE_DECL = re.compile(r"^\s*(?:(?:public|internal|private|protected|static|sealed|abstract|unsafe|readonly|ref|file|new)\s+)*"
                       r"(partial\s+)?(?:class|struct|interface|enum|record(?:\s+(?:class|struct))?)\s+(\w+)")


def skip_string(line, i):
    p, verbatim = i - 1, False
    while p >= 0 and line[p] in "@$":
        verbatim = verbatim or line[p] == "@"
        p -= 1
    j = i + 1
    while j < len(line):
        if verbatim and line[j] == '"' and line[j + 1:j + 2] == '"':
            j += 2
        elif line[j] == '"':
            return j + 1
        else:
            j += 2 if (line[j] == "\\" and not verbatim) else 1
    return j


def comment_part(path, line, state):
    """The comment text of one line, string literals skipped. state[0] carries an open block on."""
    cs = path.endswith(".cs")
    op, cl = ("/*", "*/") if cs else ("<#", "#>")
    out, i = [], 0
    while i < len(line):
        if state[0]:
            k = line.find(cl, i)
            if k < 0:
                return "".join(out) + line[i:]
            out.append(line[i:k] + " ")
            i, state[0] = k + 2, False
        elif line[i] == '"' or (not cs and line[i] == "'"):
            i = skip_string(line.replace("'", '"') if line[i] == "'" else line, i)
        elif cs and line[i] == "'" and (line[i + 2:i + 3] == "'" or (line[i + 1:i + 2] == "\\" and line[i + 3:i + 4] == "'")):
            i += 4 if line[i + 1] == "\\" else 3
        elif cs and line.startswith("//", i):
            return "".join(out) + line[i + 2:]
        elif not cs and line[i] == "#" and not (path.endswith(".ps1") and i > 0 and line[i - 1] == "<"):
            return "".join(out) + line[i + 1:]
        elif line.startswith(op, i) and (cs or path.endswith(".ps1")):
            i, state[0] = i + 2, True
        else:
            i += 1
    return "".join(out)


def partial_type(text):
    for raw in text.split("\n"):
        if is_comment(raw.strip()):
            continue
        m = TYPE_DECL.match(raw)
        if m:
            return m.group(2) if m.group(1) else None
    return None


def is_comment(s, python=False):
    return s.startswith("#" if python else ("//", "/*", "*"))


def measure(text, python=False):
    """Comment lines, code lines, the longest comment block and the lines whose comment argues or carries a colon or dash."""
    comment = code = run = block = 0
    hits = []
    for n, raw in enumerate(text.split("\n"), 1):
        s = raw.strip()
        if not s:
            continue
        if is_comment(s, python):
            comment, run, ctext = comment + 1, run + 1, s
            block = max(block, run)
        else:
            code, run, k = code + 1, 0, raw.find(" #" if python else " //")
            ctext = raw[k:] if k >= 0 else None
        if ctext is None:
            continue
        body = re.sub(r"<[^>]+>|\"[^\"]*\"|`[^`]*`", "#", ctext)
        body = re.sub(r"\b0x[0-9A-Fa-f]+\b|\b\d+:\d+\b|\b[\w.]+\.(cs|py|md|ps1):\d+", "#", body)
        prose = re.sub(r"^(?:///|//|/\*|\*|#)\s*-\s", "", body)
        what = ["a colon"] * len(re.findall(r"\w: ", body)) + ["a dash"] * len(re.findall(r"(?<=[A-Za-z0-9)]) - (?=[A-Za-z0-9(])|\u2014| -- ", prose))
        hits += [(n, "%s inside a comment sentence (the owner writes neither)" % w) for w in what]
        hits += [(n, "the tell '%s' in a comment; a comment says what the code does or why, it never argues" % t) for t in TELL.findall(body)]
    return comment, code, block, hits


def first_sentence(text, python=False):
    """The first sentence of the first summary block, or of a Python module's docstring; None without one."""
    if python:
        m = re.match(r"(?:#[^\n]*\n|\s*\n)*\s*[rRuUbB]{0,2}(\"\"\"|''')(.*?)\1", text, re.S)
        flat = re.sub(r"\s+", " ", m.group(2)).strip() if m else ""
    else:
        m = re.search(r"///\s*<summary>(.*?)</summary>", text, re.S)
        flat = re.sub(r"\s+", " ", re.sub(r"\s*///\s*", " ", m.group(1))).strip() if m else ""
    return re.split(r"(?<=[.!?])\s", flat)[0] if flat else None


def python_layout(lines):
    found, run = [], 0
    for i, l in enumerate(lines):
        run = run + 1 if not l.strip() else 0
        if run == 3:
            found.append((i + 1, "three blank lines in a row"))
        if not TOP_DEF.match(l) or (i > 0 and lines[i - 1].startswith("@")):
            continue
        j = i - 1
        gap = longest = 0
        while j >= 0 and (not lines[j].strip() or lines[j].lstrip().startswith("#")):
            gap = gap + 1 if not lines[j].strip() else 0
            longest, j = max(longest, gap), j - 1
        if j >= 0 and longest < 2:
            found.append((i + 1, "a top level definition with fewer than two blank lines above it"))
    return found


def overlong_prose(lines, ledger_head):
    out, fenced = [], False
    for n, l in enumerate(lines, 1):
        if l.lstrip().startswith("```"):
            fenced = not fenced
        elif not fenced and len(l) > WIDTH and not l.lstrip().startswith("|") and not ledger_head.match(l):
            out.append(n)
    return out


def comment_refs(rel, lines):
    """A ticket id, a review round, a reviewer or a verifier named in a comment, a type or the file name."""
    if ROUND_NAME.search(os.path.splitext(os.path.basename(rel))[0]):
        yield 1, "file name carries a review round"
    state = [False]
    for n, line in enumerate(lines, 1):
        if rel.endswith(".cs"):
            for m in TYPE_NAME.finditer(line):
                if ROUND_NAME.search(m.group(2)):
                    yield n, "type name %s carries a review round" % m.group(2)
        part = comment_part(rel, line, state)
        hit = TICKET.search(part) or REVIEW.search(part)
        if hit:
            yield n, "'%s' in a comment; say what the code does or why, not which ticket or review asked" % hit.group(0)
