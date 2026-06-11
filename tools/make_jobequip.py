#!/usr/bin/env python
"""Sparse JobData.xml override: widen generic-job loadouts + nudge class evade.
  - cross-equips + a Shield on generic player jobs (ids 61-93)
  - CharacterEvasion floored to CEV_FLOOR on every human job (raises only). Class evade is
    always-on, all-directions, no ability slot, and applies to player + generic enemies alike.
Skips monsters and the Lucavi boss forms. Parse-checks before writing (the modloader silently
drops a table whose XML comment contains a double hyphen). Load after other job mods."""
import xml.etree.ElementTree as ET
import re
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from lib.paths import TABLE_DATA, MOD_TABLES

VANILLA = TABLE_DATA / "JobData.xml"
OUT = MOD_TABLES / "JobData.xml"

# weapon cross-equips by job name, applied to GENERIC player jobs only (don't rewrite story-char loadouts)
CROSS = {
    "Archer": ["Gun"], "Black Mage": ["Knife"], "Time Mage": ["Rod"],
    "White Mage": ["Rod"], "Thief": ["Crossbow", "Bow"], "Orator": ["Sword", "Book"],
    "Geomancer": ["Pole"], "Dragoon": ["Sword"], "Samurai": ["Polearm"], "Squire": ["Crossbow"],
}
INNATE = {}                # per-job signature innate abilities (job name -> AbilityData ids); none yet
CEV_FLOOR = 8              # floor every human job's class evade to this (raises only; vanilla squishies sit at 5)
LUCAVI = {62, 64, 65, 67, 69, 73}  # boss forms in the generic id range; enemy-only, skip equip edits
REMOVE = {"Axe", "Flail"}  # emptied categories (all axes/flails repurposed into other weapons); strip from every equip list
# Daggers grew teeth (signatures), so narrow generic dagger access. Per-GENERIC-job category strip
# (story chars keep their loadouts). Keep Knife on Thief/Chemist/Black Mage (+ Ninja, the dual-blade
# class). Dancer also equips knives in vanilla -- left alone pending a call.
STRIP = {"Squire": {"Knife"}, "Orator": {"Knife"}}
# Story-job strips keyed by numeric id, bypassing the name-keyed STRIP path so the name "Squire"
# (which matches story ids 1/2/4/7) never inadvertently loses categories here.
# id 3 = Gallant Knight: knives are out (signatures make them a distinct identity).
STORY_STRIP = {3: {"Knife"}}
# Generic jobs EXCLUDED from the blanket Shield addition. Vanilla gives none of these a
# shield: the Squire/Chemist exclusion matches Ramza's shieldless story squire, and Black
# Mage came off after live play showed a shielded BLM too durable for its damage output
# (2026-06-10). Knight's Equip Shield support is the designed path for all three.
NO_SHIELD = {"Squire", "Chemist", "Black Mage"}

raw = VANILLA.read_text(encoding="utf-8")
names = {int(m.group(1)): m.group(2).strip() for m in re.finditer(r"<Id>(\d+)</Id>\s*<!--\s*([^/]+?)\s*/", raw)}
jobs = {int(j.findtext("Id")): j for j in ET.parse(VANILLA).getroot().find("Entries").findall("Job")}


def is_human(jid):
    j = jobs.get(jid)
    return j is not None and jid not in LUCAVI and int((j.findtext("MonsterGraphic") or "0").strip() or 0) == 0


generic = {jid for jid in range(61, 94) if is_human(jid)}   # equippability targets
human = [jid for jid in sorted(jobs) if is_human(jid)]      # dodge-dial targets (every human job)

out = ['<?xml version="1.0" encoding="utf-8"?>',
       '<!-- FFTItemOverhaul: widened generic-job equip + shields, and a class-evade floor on every human job. Load after other job mods. -->',
       '<JobTable>', '  <Version>1</Version>', '  <Entries>']
log = []
for jid in human:
    job = jobs[jid]
    nm = names.get(jid, "?")
    cur = [c.strip() for c in (job.findtext("EquippableItems") or "").split(",") if c.strip()]
    shield = [] if nm in NO_SHIELD else ["Shield"]
    additions = [a for a in (list(CROSS.get(nm, [])) + shield) if a not in cur] if jid in generic else []
    strip = set(REMOVE) | (STRIP.get(nm, set()) if jid in generic else set()) | STORY_STRIP.get(jid, set())   # per-generic-job category strip + id-keyed story-job strips
    final_equip = [c for c in (cur + additions) if c not in strip]
    equip_changed = final_equip != cur
    innate = []
    have = [int((job.findtext(f"InnateAbilityId{i}") or "0").strip() or 0) for i in (1, 2, 3, 4)]
    free = [i for i in (1, 2, 3, 4) if have[i - 1] == 0]
    for aid in INNATE.get(nm, []):
        if aid in have or not free:
            continue
        innate.append((free.pop(0), aid))
    cev_cur = int((job.findtext("CharacterEvasion") or "0").strip() or 0)
    cev_new = CEV_FLOOR if cev_cur < CEV_FLOOR else None
    if not equip_changed and not innate and cev_new is None:
        continue
    note = ((["equip: " + ", ".join(final_equip)] if equip_changed else [])
            + ([f"C-EV {cev_cur} to {cev_new}"] if cev_new is not None else [])
            + ([f"innate {','.join(str(a) for _, a in innate)}"] if innate else []))
    out.append('    <Job>')
    out.append(f'      <Id>{jid}</Id> <!-- {nm.replace("--", "-")}: {"; ".join(note)} -->')
    if equip_changed:
        out.append(f'      <EquippableItems>{", ".join(final_equip)}</EquippableItems>')
    if cev_new is not None:
        out.append(f'      <CharacterEvasion>{cev_new}</CharacterEvasion>')
    for slot, aid in innate:
        out.append(f'      <InnateAbilityId{slot}>{aid}</InnateAbilityId{slot}>')
    out.append('    </Job>')
    log.append((jid, nm, "; ".join(note)))
out += ['  </Entries>', '</JobTable>', '']
text = "\n".join(out)
ET.fromstring(text)  # parse-check before shipping (raises on the illegal-'--' bug)
OUT.parent.mkdir(parents=True, exist_ok=True)
OUT.write_text(text, encoding="utf-8")
bumped = sum(1 for _, _, n in log if "C-EV" in n)
print(f"Wrote JobData.xml: {len(log)} job edits ({bumped} lifted to the C-EV floor of {CEV_FLOOR}, {len(generic)} generic also get equip/shields)")
