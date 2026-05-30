# FFT Item Overhaul

A build-diversity rebalance of **every item** in Final Fantasy Tactics: The Ivalice Chronicles.
Pure-data mod (no DLL) over Nenkai's `fftivc.utility.modloader`.

**Thesis:** Weapon Power / HP climb *gently*; every item's longevity lives in its **non-WP dimensions**
(evasion, element, on-hit status, niche). A new weapon is rarely a strict upgrade — old gear stays
relevant. See [`docs/DESIGN.md`](docs/DESIGN.md).

## Status
Pilot: **knives** (item ids 1–10). Design in progress.

## How it's built
```
data/items.json            # SOURCE OF TRUTH — every item's stats + new name + identity
  └─ tools/generate.py      # → mod/FFTIVC/tables/enhanced/ItemWeaponData.xml + out/names.json
  └─ tools/analyze.py       # build-diversity GATE: fails if any item is strictly dominated
  └─ tools/scan_conflicts.py# lists installed mods that edit the same item ids (disable them)
```

- `python tools/analyze.py` — prove no item is strictly dominated (exit 1 if it is).
- `python tools/generate.py` — emit the modloader table(s) from `items.json`.
- `python tools/scan_conflicts.py` — find conflicting item mods to disable.

## Compatibility
This is **the authoritative item mod** — disable other item-rebalance mods (Regabonds Rebalance,
Equipment Replacer). It composes cleanly with non-item mods (Level Scaling, job/skill/spell mods).

## Names / icons
New item names go in `item.en.nxd`; menu icons are `ei_<id>_uitx.tex`. (Rename/icon pipeline: WIP.)
