#!/usr/bin/env python
"""Sparse JobData.xml override: widen generic job loadouts and nudge class evade.

The departures from vanilla live in data/jobs.json, each with a written reason: equip categories
added or stripped on the generic jobs (ids 61 to 93), one story strip, and a class evade floor
that only raises. Class evade is always on, hits from every direction, has no ability slot, and
applies to player and generic enemy units alike. Vanilla rows come from data/vanilla_tables.json
(tools/snapshot_vanilla_tables.py), never the game folder.

A table XML row applies as a whole row writeback at load (ApplyTablePatch, model.X ?? previous.X
across every field), so a CharacterEvasion only row clobbers any other mod's runtime edit to that
same row and load order cannot fix it. This table therefore lists only rows with a real payload,
an equip addition or strip or a floor raise. The floor covers the story ids in jobs.json plus the
generic ids; unknown ids and the enemy only specials keep vanilla. Monsters and the Lucavi boss
forms are skipped. The text is parse checked before writing, because the modloader silently drops
a table whose XML comment contains a double hyphen. Load after other job mods.

--verify rebuilds the text in memory and fails when the committed file differs, ignoring line
endings. --out writes or checks another path. A missing reason in jobs.json exits 1."""
import argparse
import difflib
import json
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from lib import jobrules
from lib.paths import VANILLA_TABLES, MOD_TABLES

OUT = MOD_TABLES / "JobData.xml"

LUCAVI = {62, 64, 65, 67, 69, 73}  # boss forms in the generic id range; enemy only, skip equip edits
# Categories stripped from every equip list. Empty since the rebalance gave axes and flails their
# vanilla identities back; the seam stays for the next category that genuinely empties out.
REMOVE = set()

_snap = json.loads(VANILLA_TABLES.read_text(encoding="utf-8"))
RULES = jobrules.load()
CROSS_ADD = {name: list(job["add"]) for name, job in RULES["generic"].items()}
STRIP = {name: set(job["strip"]) for name, job in RULES["generic"].items()}
STORY_STRIP = {int(jid): set(job["strip"]) for jid, job in RULES["story"].items()}
CEV_FLOOR = RULES["classEvadeFloor"]["value"]
CEV_ALLOW = set(RULES["classEvadeFloor"]["storyAllow"])
jobs = {int(k): v for k, v in _snap["jobs"].items()}
names = {jid: j["name"] for jid, j in jobs.items() if j["name"] != "?"}


def is_human(jid):
    j = jobs.get(jid)
    return j is not None and jid not in LUCAVI and j["monsterGraphic"] == 0


generic = {jid for jid in range(61, 94) if is_human(jid)}   # equippability targets
cev_targets = CEV_ALLOW | generic                           # Only these ids keep the CEV floor
human = [jid for jid in sorted(jobs) if is_human(jid)]      # dodge-dial targets (every human job)


def build():
    """The JobData.xml text and a per-job log of (id, name, note)."""
    out = ['<?xml version="1.0" encoding="utf-8"?>',
           '<!-- FFTLivingWeapons: widened generic-job equip + shields, and a class-evade floor on live-payload jobs (LW-77 pruned the rest). Load after other job mods. -->',
           '<JobTable>', '  <Version>1</Version>', '  <Entries>']
    log = []
    for jid in human:
        job = jobs[jid]
        nm = names.get(jid, "?")
        cur = job["equip"]
        additions = [a for a in CROSS_ADD.get(nm, []) if a not in cur] if jid in generic else []
        strip_generic = STRIP.get(nm, set()) if jid in generic else set()
        strip_story = STORY_STRIP.get(jid, set())
        strip = set(REMOVE) | strip_generic | strip_story   # per-generic-job category strip + id-keyed story-job strips
        final_equip = [c for c in (cur + additions) if c not in strip]
        # A row only earns its EquippableItems element for a REAL payload. A REMOVE-only diff
        # never counted as one (a dropped category that no item ships is vanilla-equal); REMOVE is
        # empty, so today every emitted list is an addition or a real category strip.
        equip_changed = bool(additions) or bool(strip_generic) or bool(strip_story)
        cev_cur = job["cev"]
        cev_new = CEV_FLOOR if jid in cev_targets and cev_cur < CEV_FLOOR else None
        if not equip_changed and cev_new is None:
            continue
        note = ((["equip: " + ", ".join(final_equip)] if equip_changed else [])
                + ([f"C-EV {cev_cur} to {cev_new}"] if cev_new is not None else []))
        out.append('    <Job>')
        out.append(f'      <Id>{jid}</Id> <!-- {nm.replace("--", "-")}: {"; ".join(note)} -->')
        if equip_changed:
            out.append(f'      <EquippableItems>{", ".join(final_equip)}</EquippableItems>')
        if cev_new is not None:
            out.append(f'      <CharacterEvasion>{cev_new}</CharacterEvasion>')
        out.append('    </Job>')
        log.append((jid, nm, "; ".join(note)))
    out += ['  </Entries>', '</JobTable>', '']
    text = "\n".join(out)
    ET.fromstring(text)  # parse-check before shipping (raises on the illegal-'--' bug)
    return text, log


def main(argv=None):
    ap = argparse.ArgumentParser(description="Emit or verify the sparse JobData.xml override.")
    ap.add_argument("--verify", action="store_true", help="compare a fresh build to the file, write nothing")
    ap.add_argument("--out", type=Path, default=MOD_TABLES / "JobData.xml", help="file to write or verify")
    args = ap.parse_args(argv)
    text, log = build()
    if args.verify:
        have = args.out.read_text(encoding="utf-8")   # universal newlines, so CRLF and LF compare equal
        if have == text:
            return 0
        sys.stdout.writelines(difflib.unified_diff(have.splitlines(keepends=True), text.splitlines(keepends=True),
                                                   fromfile=str(args.out), tofile="make_jobequip.py"))
        return 1
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(text, encoding="utf-8")
    bumped = sum(1 for _, _, n in log if "C-EV" in n)
    print(f"Wrote JobData.xml: {len(log)} job edits ({bumped} lifted to the C-EV floor of {CEV_FLOOR}, {len(generic)} generic also get equip/shields)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
