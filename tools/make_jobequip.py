#!/usr/bin/env python
"""Generate a sparse JobData.xml that widens per-job equippability:
  - targeted weapon cross-equips, applied by job NAME (so both gender/variant entries get them)
  - a universal Shield for every generic player job
Excludes the Lucavi boss forms that share the generic id range (enemy-only). Parse-checked before
write to guard the modloader '--'-in-comment bug. Load AFTER other job mods (e.g. Regabonds)."""
import xml.etree.ElementTree as ET
import re
from pathlib import Path

VANILLA = Path(r"C:\program files (x86)\steam\steamapps\common\FINAL FANTASY TACTICS - The Ivalice Chronicles\Reloaded\Mods\FFTIVC_Mod_Loader\TableData\JobData.xml")
OUT = Path(__file__).resolve().parent.parent / "mod" / "FFTIVC" / "tables" / "enhanced" / "JobData.xml"

# weapon cross-equips, keyed by job name (hits every matching generic entry, both genders)
CROSS = {
    "Knight": ["Axe"], "Archer": ["Gun"], "Black Mage": ["Knife"], "Time Mage": ["Rod"],
    "White Mage": ["Rod"], "Thief": ["Bow", "Crossbow"], "Orator": ["Sword", "Book"],
    "Geomancer": ["Pole"], "Dragoon": ["Sword"], "Samurai": ["Polearm"], "Squire": ["Crossbow"],
}
LUCAVI = {62, 64, 65, 67, 69, 73}  # boss forms sharing the 61-93 id range; enemy-only, skip

raw = VANILLA.read_text(encoding="utf-8")
names = {int(m.group(1)): m.group(2).strip() for m in re.finditer(r"<Id>(\d+)</Id>\s*<!--\s*([^/]+?)\s*/", raw)}
jobs = {int(j.findtext("Id")): j for j in ET.parse(VANILLA).getroot().find("Entries").findall("Job")}

generic = [jid for jid in range(61, 94)
           if jid not in LUCAVI and jid in jobs
           and int((jobs[jid].findtext("MonsterGraphic") or "0").strip() or 0) == 0]

out = ['<?xml version="1.0" encoding="utf-8"?>',
       '<!-- FFTItemOverhaul: widened per-job equippability + universal shields (sparse). Load AFTER other job mods. -->',
       '<JobTable>', '  <Version>1</Version>', '  <Entries>']
log = []
for jid in generic:
    nm = names.get(jid, "?")
    adds = list(CROSS.get(nm, [])) + ["Shield"]
    cur = [c.strip() for c in (jobs[jid].findtext("EquippableItems") or "").split(",") if c.strip()]
    new_adds = [a for a in adds if a not in cur]
    if not new_adds:
        continue
    cur += new_adds
    out += ['    <Job>',
            f'      <Id>{jid}</Id> <!-- {nm.replace("--", "-")}: +{" +".join(new_adds)} -->',
            f'      <EquippableItems>{", ".join(cur)}</EquippableItems>',
            '    </Job>']
    log.append((jid, nm, new_adds))
out += ['  </Entries>', '</JobTable>', '']
text = "\n".join(out)
ET.fromstring(text)  # parse-check before shipping (raises on the illegal-'--' bug)
OUT.parent.mkdir(parents=True, exist_ok=True)
OUT.write_text(text, encoding="utf-8")
print(f"Wrote JobData.xml: {len(log)} generic-job equippability edits")
for jid, nm, a in log:
    print(f"  {nm:14} (id {jid}) += {', '.join(a)}")
