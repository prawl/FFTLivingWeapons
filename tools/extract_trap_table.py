#!/usr/bin/env python
"""Snapshot MapTrapFormationData.xml to data/map_trap_formation.json.

The snapshot is committed so CI (and the gen_treasure_db.py bake) never need
the game install.  Run this once per game version when the table changes.

Usage:
  python tools\\extract_trap_table.py             # write data/map_trap_formation.json
  python tools\\extract_trap_table.py --selftest  # run built-in assertions, no write

Sanity gate (always active, even during --selftest):
  Map Id 74 ("The Siedge Weald") must contain:
    slot (0,1) rareItemId=77   TrapFlags=DisableTrap  -> is_treasure=True
    slot (1,9) rareItemId=128  TrapFlags=DisableTrap  -> is_treasure=True
    slot (6,6) rareItemId=157  TrapFlags=None         -> is_treasure=False
  These are probe-verified ground truth (live RE, 2026-06-11).  Any mismatch
  means the table format or content changed; abort rather than silently ship
  wrong data.

TrapFlags finding (encoded in lib/treasure.is_treasure):
  Id 74 tiles (0,1), (1,9), (5,11) all carry TrapFlags=DisableTrap -- the
  three pure-loot treasure tiles.  Tile (6,6) carries TrapFlags=None -- the
  live trap tile (no loot protection, the trap triggers on step).
  Conclusion: DisableTrap = trap disabled = safe loot; anything else = hazard.
  is_treasure() tests for exactly "DisableTrap" (combos like "Degenerator,
  DisableTrap" are treated as hazardous since an active component remains).
"""
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from lib.paths import ROOT, TABLE_DATA
from lib.treasure import is_treasure, load_trap_table

_XML_PATH = TABLE_DATA / "MapTrapFormationData.xml"
_OUT_PATH = ROOT / "data" / "map_trap_formation.json"

# Ground-truth tiles for the sanity gate (Id 74, The Siedge Weald).
# Probe-verified live 2026-06-11.
_GATE_74 = [
    {"x": 0, "y": 1,  "rareItemId": 77,  "trapFlags": "DisableTrap", "is_treasure": True},
    {"x": 1, "y": 9,  "rareItemId": 128, "trapFlags": "DisableTrap", "is_treasure": True},
    {"x": 6, "y": 6,  "rareItemId": 157, "trapFlags": "None",        "is_treasure": False},
]


def _sanity_gate(table: dict) -> None:
    """Abort (SystemExit 1) if Id-74 ground-truth tiles are missing or wrong."""
    entry = table.get(74)
    if entry is None:
        sys.exit("SANITY GATE FAIL: map Id 74 not present in parsed table")

    tiles_by_xy = {(t["x"], t["y"]): t for t in entry["tiles"]}

    for spec in _GATE_74:
        key = (spec["x"], spec["y"])
        tile = tiles_by_xy.get(key)
        if tile is None:
            sys.exit(
                f"SANITY GATE FAIL: Id 74 tile {key} not found in table. "
                "Has MapTrapFormationData.xml changed?"
            )
        if tile["rareItemId"] != spec["rareItemId"]:
            sys.exit(
                f"SANITY GATE FAIL: Id 74 tile {key} rareItemId "
                f"expected {spec['rareItemId']}, got {tile['rareItemId']}"
            )
        if tile["trapFlags"] != spec["trapFlags"]:
            sys.exit(
                f"SANITY GATE FAIL: Id 74 tile {key} trapFlags "
                f"expected {spec['trapFlags']!r}, got {tile['trapFlags']!r}"
            )
        actual_treasure = is_treasure(tile)
        if actual_treasure != spec["is_treasure"]:
            sys.exit(
                f"SANITY GATE FAIL: Id 74 tile {key} is_treasure "
                f"expected {spec['is_treasure']}, got {actual_treasure}. "
                "is_treasure() definition may need updating."
            )


def _selftest(table: dict) -> None:
    """Lightweight assertions covering name parsing and is_treasure vs the Id-74 fixture."""
    errors = []

    # --- Name parsing ---
    name_74 = table[74]["name"]
    if name_74 != "The Siedge Weald":
        errors.append(f"Name parse: Id 74 expected 'The Siedge Weald', got {name_74!r}")

    name_0 = table[0]["name"]
    if name_0 != "Empty/Dummy":
        errors.append(f"Name parse: Id 0 expected 'Empty/Dummy', got {name_0!r}")

    name_1 = table[1]["name"]
    if name_1 != "Eagrose Castle Gate":
        errors.append(f"Name parse: Id 1 expected 'Eagrose Castle Gate', got {name_1!r}")

    # --- is_treasure against Id-74 fixture ---
    tiles_74 = {(t["x"], t["y"]): t for t in table[74]["tiles"]}

    for spec in _GATE_74:
        key = (spec["x"], spec["y"])
        tile = tiles_74[key]  # sanity gate already verified these exist
        got = is_treasure(tile)
        if got != spec["is_treasure"]:
            errors.append(
                f"is_treasure Id 74 {key}: expected {spec['is_treasure']}, got {got}"
            )

    # --- DisableTrap tiles are treasure; trap-flag tiles are not ---
    disable_trap_tile = {"trapFlags": "DisableTrap", "x": 0, "y": 0, "rareItemId": 1, "commonItemId": 0}
    none_tile         = {"trapFlags": "None",        "x": 0, "y": 0, "rareItemId": 1, "commonItemId": 0}
    deathtrap_tile    = {"trapFlags": "Deathtrap",   "x": 0, "y": 0, "rareItemId": 1, "commonItemId": 0}
    combo_tile        = {"trapFlags": "Degenerator, DisableTrap", "x": 0, "y": 0, "rareItemId": 1, "commonItemId": 0}

    if not is_treasure(disable_trap_tile):
        errors.append("is_treasure: DisableTrap should be True")
    if is_treasure(none_tile):
        errors.append("is_treasure: None should be False")
    if is_treasure(deathtrap_tile):
        errors.append("is_treasure: Deathtrap should be False")
    if is_treasure(combo_tile):
        errors.append("is_treasure: 'Degenerator, DisableTrap' should be False (hazard present)")

    # --- Every mapId 0-127 present ---
    missing = [i for i in range(128) if i not in table]
    if missing:
        errors.append(f"Table missing mapIds: {missing[:10]}{'...' if len(missing)>10 else ''}")

    if errors:
        print("SELFTEST FAILED:")
        for e in errors:
            print(f"  {e}")
        sys.exit(1)

    print("selftest passed")


def _load_or_abort() -> dict:
    """Load the XML, aborting loudly on missing file or shape change."""
    if not _XML_PATH.exists():
        sys.exit(
            f"ERROR: MapTrapFormationData.xml not found at:\n  {_XML_PATH}\n"
            "Game install or modloader missing.  Run on a box with the game installed\n"
            "to refresh data/map_trap_formation.json."
        )

    try:
        table = load_trap_table(_XML_PATH)
    except (ValueError, KeyError) as exc:
        sys.exit(f"ERROR: XML shape changed or parse failed: {exc}")

    if len(table) != 128:
        sys.exit(f"ERROR: expected 128 map entries, parsed {len(table)}")

    return table


def main() -> None:
    selftest_only = "--selftest" in sys.argv

    table = _load_or_abort()
    _sanity_gate(table)

    maps_with_tiles = sum(1 for v in table.values() if v["tiles"])
    total_tiles = sum(len(v["tiles"]) for v in table.values())
    treasure_tiles = sum(
        1 for v in table.values() for t in v["tiles"] if is_treasure(t)
    )
    trap_tiles = total_tiles - treasure_tiles

    # Report the TrapFlags finding for the Id-74 probe tiles.
    tiles_74 = {(t["x"], t["y"]): t for t in table[74]["tiles"]}
    tile_01  = tiles_74[(0, 1)]
    tile_19  = tiles_74[(1, 9)]
    tile_66  = tiles_74[(6, 6)]
    print("TrapFlags finding (Id 74 / The Siedge Weald):")
    print(f"  (0,1) TrapFlags={tile_01['trapFlags']!r}  -> is_treasure={is_treasure(tile_01)}")
    print(f"  (1,9) TrapFlags={tile_19['trapFlags']!r}  -> is_treasure={is_treasure(tile_19)}")
    print(f"  (6,6) TrapFlags={tile_66['trapFlags']!r}        -> is_treasure={is_treasure(tile_66)}")
    print("Conclusion: DisableTrap = trap disabled = pure loot. "
          "None/Deathtrap/SleepingGas/SteelNeedle/Degenerator = active hazard.")
    print()
    print(f"Maps with at least one tile: {maps_with_tiles}/128")
    print(f"Total tiles: {total_tiles}  (treasure={treasure_tiles}, trap/hazard={trap_tiles})")

    if selftest_only:
        _selftest(table)
        return

    # Serialise: convert int keys to strings for JSON.
    out = {str(k): v for k, v in sorted(table.items())}
    _OUT_PATH.write_text(json.dumps(out, indent=2, ensure_ascii=False), encoding="utf-8")
    print(f"Wrote {_OUT_PATH}")

    _selftest(table)


if __name__ == "__main__":
    main()
