#!/usr/bin/env python
"""Sparse JobCommandData.xml override: scrub Equip Axes from every learnable support slot.

Axes and flails were repurposed into other weapon categories (see make_jobequip.py REMOVE set).
No axe exists in the mod, so any RSM slot carrying ability 460 (Equip Axes) is dead JP -- the
player can spend the cost and receive a support that equips a weapon class that doesn't ship.
Zeroing those slots closes the trap.

Scope: every JobCommandData record where any ReactionSupportMovementId slot == 460.
Vanilla FFT only offered Equip Axes via the Squire skillset, but IC put it in 47 records covering
every knight-line, IC-exclusive base job, and all Ramza story variants. All are cleared here.

The modloader re-derives ExtendReactionSupportMovementIdFlagBits from the actual id values, so the
sparse output only needs to ship the zeroed slot; no manual flag manipulation required.

Parse-checked before writing (modloader silently drops tables whose XML comments contain '--')."""
import xml.etree.ElementTree as ET
import re
from pathlib import Path

VANILLA = Path(
    r"C:\program files (x86)\steam\steamapps\common"
    r"\FINAL FANTASY TACTICS - The Ivalice Chronicles"
    r"\Reloaded\Mods\FFTIVC_Mod_Loader\TableData\JobCommandData.xml"
)
OUT = (
    Path(__file__).resolve().parent.parent
    / "mod" / "FFTIVC" / "tables" / "enhanced" / "JobCommandData.xml"
)

EQUIP_AXES = 460  # Ability-en Key 460 = "Equip Axes"
RSM_SLOTS  = 6    # ReactionSupportMovementId1 .. 6

raw  = VANILLA.read_text(encoding="utf-8")
# Extract id -> comment label from the raw text for annotation
labels = {int(m.group(1)): m.group(2).strip()
          for m in re.finditer(r"<Id>(\d+)</Id>\s*<!--\s*([^/\n]+?)\s*/", raw)}

root = ET.parse(VANILLA).getroot()

affected = []  # list of (record_id, flag_text, [(slot_index, old_value), ...])
for entry in root.iter("JobCommand"):
    rec_id = int(entry.findtext("Id"))
    hits   = []
    for i in range(1, RSM_SLOTS + 1):
        val_text = (entry.findtext(f"ReactionSupportMovementId{i}") or "0").strip()
        if val_text and int(val_text) == EQUIP_AXES:
            hits.append((i, int(val_text)))
    if hits:
        # The RSMIdN property SETTERS in the modloader model recompute the matching bit of
        # ExtendReactionSupportMovementIdFlagBits on every assignment -- by dereferencing it
        # (`...FlagBits!.Value`). A sparse record WITHOUT that element leaves it null, the
        # setter throws, and the whole table is dropped (live YAXPropertyCannotBeAssignedTo,
        # 2026-06-10). So every record must carry the vanilla flag text, ORDERED BEFORE the
        # RSM elements; assigning the zeroed slot then auto-clears its own extend bit.
        flag_text = " ".join((entry.findtext("ExtendReactionSupportMovementIdFlagBits") or "0").split())
        affected.append((rec_id, flag_text, hits))

if not affected:
    raise SystemExit("no records carrying Equip Axes found -- check VANILLA path or ability id")

out = [
    '<?xml version="1.0" encoding="utf-8"?>',
    "<!-- FFTItemOverhaul: zero Equip Axes (id 460) out of every learnable RSM slot.",
    "     Axes were repurposed into other weapon types; the support ability is dead JP. -->",
    "<JobCommandTable>",
    "  <Version>1</Version>",
    "  <Entries>",
]

# No inline comments inside the entries (kept out since the first repair attempt); the real
# load-bearing detail is the flag element ORDER -- see the comment where flag_text is captured.
for rec_id, flag_text, hits in sorted(affected):
    out.append(f"    <JobCommand>")
    out.append(f"      <Id>{rec_id}</Id>")
    out.append(f"      <ExtendReactionSupportMovementIdFlagBits>{flag_text}</ExtendReactionSupportMovementIdFlagBits>")
    for i, _old in hits:
        out.append(f"      <ReactionSupportMovementId{i}>0</ReactionSupportMovementId{i}>")
    out.append(f"    </JobCommand>")

out += ["  </Entries>", "</JobCommandTable>", ""]
text = "\n".join(out)

# Parse-check: the modloader silently drops a table whose comments contain '--'
# (double hyphens are illegal in XML comments). ET.fromstring raises if the XML is malformed.
ET.fromstring(text)

OUT.parent.mkdir(parents=True, exist_ok=True)
OUT.write_text(text, encoding="utf-8")

print(
    f"Wrote JobCommandData.xml: {len(affected)} records patched, "
    f"{sum(len(h) for _, _, h in affected)} RSM slot(s) zeroed (Equip Axes id {EQUIP_AXES})"
)
