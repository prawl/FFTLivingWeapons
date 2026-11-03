#!/usr/bin/env python
"""The rulebook's fingerprint list, so a mod's copy cannot drift from what was published.

  python tools/manifest.py write            # rewrite MANIFEST.sha256 from the tracked files here
  python tools/manifest.py verify [--dir D] # every listed file present and unchanged, nothing unlisted

Text is hashed with CRLF folded to LF, so a checkout's line endings do not count as a change.
"""
import hashlib
import os
import subprocess
import sys

MANIFEST = "MANIFEST.sha256"
SKIP_DIRS = {"bin", "obj", ".vs", ".git", "__pycache__", "TestResults"}


def digest(path):
    data = open(path, "rb").read()
    if b"\0" not in data:
        data = data.replace(b"\r\n", b"\n")
    return hashlib.sha256(data).hexdigest()


def tracked_files(root):
    """Every file git would commit, tracked or not yet added, minus what .gitignore hides."""
    out = subprocess.run(["git", "-C", root, "ls-files", "--cached", "--others", "--exclude-standard"],
                         capture_output=True, text=True, check=True).stdout
    return sorted(set(p for p in out.split("\n") if p and p != MANIFEST and os.path.isfile(os.path.join(root, p))))


def present_files(root):
    """What a reader of this copy would see. Inside a checkout that is git's view, so an ignored local file is not drift."""
    if os.path.exists(os.path.join(root, ".git")):
        return tracked_files(root)
    found = []
    for folder, dirs, names in os.walk(root):
        dirs[:] = [d for d in dirs if d not in SKIP_DIRS]
        for name in names:
            rel = os.path.relpath(os.path.join(folder, name), root).replace(os.sep, "/")
            if rel != MANIFEST:
                found.append(rel)
    return sorted(found)


def read_manifest(root):
    path = os.path.join(root, MANIFEST)
    if not os.path.isfile(path):
        return None
    rows = {}
    for line in open(path, encoding="utf-8"):
        line = line.strip()
        if line:
            h, rel = line.split("  ", 1)
            rows[rel] = h
    return rows


def write(root):
    rows = ["%s  %s" % (digest(os.path.join(root, rel)), rel) for rel in tracked_files(root)]
    with open(os.path.join(root, MANIFEST), "w", encoding="utf-8", newline="\n") as h:
        h.write("\n".join(rows) + "\n")
    print("wrote %s with %d files" % (MANIFEST, len(rows)))
    return 0


def verify(root):
    listed = read_manifest(root)
    if listed is None:
        print("MANIFEST GATE: no %s in %s; the rulebook copy is incomplete." % (MANIFEST, root))
        return 1
    problems = []
    for rel, h in listed.items():
        path = os.path.join(root, rel)
        if not os.path.isfile(path):
            problems.append("missing: " + rel)
        elif digest(path) != h:
            problems.append("changed: " + rel)
    for rel in present_files(root):
        if rel not in listed:
            problems.append("unlisted: " + rel)
    if problems:
        print("MANIFEST GATE: the rulebook copy in %s differs from what was published." % root)
        for p in problems:
            print("  " + p)
        print("Take the fix upstream in the rulebook repo, or pull the version that has it. Never edit gates/ in a mod.")
        return 1
    print("  -> Rulebook fingerprints match, %d files." % len(listed))
    return 0


def main(argv):
    if len(argv) < 1 or argv[0] not in ("write", "verify"):
        print(__doc__)
        return 2
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    if "--dir" in argv:
        root = os.path.abspath(argv[argv.index("--dir") + 1])
    return write(root) if argv[0] == "write" else verify(root)


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
