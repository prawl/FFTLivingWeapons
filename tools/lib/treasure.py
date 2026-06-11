"""Parse the game's move-find table from MapTrapFormationData.xml.

The file lives at TABLE_DATA / "MapTrapFormationData.xml" (128 MapTrapFormation entries,
Id 0-127).  Each entry has up to 4 item slots (suffixed 1-4):
  X1..X4, Y1..Y4         grid coordinates (0-15)
  TrapFlags1..4          "None" | "DisableTrap" | "Deathtrap" | "SleepingGas" |
                         "SteelNeedle" | "Degenerator" | comma-separated combos
  RareItemId1..4         ItemData id (0 = unused)
  CommonItemId1..4       ItemData id

A slot is considered USED when its RareItemId is non-zero.

Map display names are encoded as inline XML comments on the <Id> line, of the form:
  <!-- EnglishName / JapaneseName / FrenchName / GermanName -->
or for entry 0:
  <!-- Empty/Dummy -->
The English name is the first token before " / ".  We use a regex to extract it; the
comment is fragile (format assumed stable for a given game version) -- the pinned fixture
test in extract_trap_table.py guards against comment-format drift.

Public API
----------
load_trap_table(path=None) -> dict
    Returns {mapId (int): {"name": str | None, "tiles": [tile_dict, ...]}}
    where tile_dict = {"x": int, "y": int, "trapFlags": str,
                       "rareItemId": int, "commonItemId": int}
    Only USED slots (rareItemId != 0) are included; maps with no used slots are present
    with "tiles": [].

is_treasure(tile_dict) -> bool
    True when the tile is a pure treasure (no active trap hazard).
    Definition: TrapFlags == "DisableTrap" exactly.
    "DisableTrap" means the trap mechanism is disabled -- it is a safe loot tile.
    Any other value (None, Deathtrap, SleepingGas, SteelNeedle, Degenerator, or any
    combo containing those) indicates an active hazard; do not mark those.

Pinned fixture (Id 74, The Siedge Weald) -- ground-truth from live probe + hand hover:
    (0,1)  rareItemId=77   TrapFlags=DisableTrap -> is_treasure=True
    (1,9)  rareItemId=128  TrapFlags=DisableTrap -> is_treasure=True
    (5,11) rareItemId=144  TrapFlags=DisableTrap -> is_treasure=True
    (6,6)  rareItemId=157  TrapFlags=None        -> is_treasure=False  (live trap)
"""
import re
import xml.etree.ElementTree as ET
from pathlib import Path

from .paths import TABLE_DATA

_XML_PATH = TABLE_DATA / "MapTrapFormationData.xml"

# Regex to pull the English map name from the inline comment on the <Id> line.
# Matches:  <!-- The Siedge Weald / ... -->   or  <!-- Empty/Dummy -->
# Capture group 1 = the text before the first " / " separator (or the full comment text
# when no " / " is present -- the "Empty/Dummy" case).
_NAME_RE = re.compile(r"<!--\s*(.*?)\s*(?:/[^-].*?)?-->")


def _parse_name_comment(comment_text: str) -> str | None:
    """Extract the English name from a raw comment string (text between <!-- and -->)."""
    text = comment_text.strip()
    # Split on " / " (space-slash-space) to separate locale variants.
    parts = text.split(" / ")
    name = parts[0].strip()
    return name if name else None


def _extract_names_from_raw(raw: str) -> dict[int, str | None]:
    """
    Parse all map names from the raw XML text.
    Returns {mapId: name_str | None}.

    Strategy: find every occurrence of <Id>N</Id> followed (on the same line) by a
    comment <!-- ... -->.  ElementTree strips comments, so we read the raw string.
    """
    names: dict[int, str | None] = {}
    # Match: <Id>NUMBER</Id> then inline comment on the same line (non-greedy).
    pattern = re.compile(
        r"<Id>(\d+)</Id>\s*<!--(.*?)-->",
        re.DOTALL,  # comments may contain newlines in theory, but in practice they don't
    )
    for m in pattern.finditer(raw):
        map_id = int(m.group(1))
        comment_body = m.group(2)
        parts = comment_body.strip().split(" / ")
        name = parts[0].strip() or None
        names[map_id] = name
    return names


def load_trap_table(path: Path | None = None) -> dict[int, dict]:
    """Load and parse MapTrapFormationData.xml.

    Returns {mapId: {"name": str | None, "tiles": [tile_dict, ...]}}
    where tile_dict has keys: x, y, trapFlags, rareItemId, commonItemId.
    Only slots with rareItemId != 0 are included in the tiles list.
    Every mapId 0-127 is present in the result (with an empty tiles list if unused).
    """
    xml_path = Path(path) if path is not None else _XML_PATH
    if not xml_path.exists():
        raise FileNotFoundError(
            f"MapTrapFormationData.xml not found at {xml_path}\n"
            "Install the game + modloader, or run extract_trap_table.py to snapshot."
        )

    raw = xml_path.read_text(encoding="utf-8")
    tree = ET.fromstring(raw)

    entries_elem = tree.find("Entries")
    if entries_elem is None:
        raise ValueError("MapTrapFormationData.xml: <Entries> element missing")

    formations = entries_elem.findall("MapTrapFormation")
    if len(formations) != 128:
        raise ValueError(
            f"MapTrapFormationData.xml: expected 128 MapTrapFormation entries, got {len(formations)}"
        )

    names = _extract_names_from_raw(raw)

    result: dict[int, dict] = {}
    for entry in formations:
        map_id_elem = entry.find("Id")
        if map_id_elem is None:
            raise ValueError("MapTrapFormation entry missing <Id>")
        map_id = int(map_id_elem.text)

        tiles = []
        for i in range(1, 5):
            rare_elem = entry.find(f"RareItemId{i}")
            if rare_elem is None:
                continue
            rare = int(rare_elem.text)
            if rare == 0:
                continue  # unused slot
            tiles.append({
                "x": int(entry.find(f"X{i}").text),
                "y": int(entry.find(f"Y{i}").text),
                "trapFlags": entry.find(f"TrapFlags{i}").text.strip(),
                "rareItemId": rare,
                "commonItemId": int(entry.find(f"CommonItemId{i}").text),
            })

        result[map_id] = {
            "name": names.get(map_id),
            "tiles": tiles,
        }

    return result


def is_treasure(tile: dict) -> bool:
    """True when a tile is a pure loot tile with no active trap hazard.

    DisableTrap means the trap is disabled -- safe to step on, safe to mark.
    Any other TrapFlags value (None, Deathtrap, SleepingGas, SteelNeedle,
    Degenerator, or any combo containing those) = active hazard; do not mark.

    Pinned by Id-74 ground truth:
      (0,1) DisableTrap -> True   (1,9) DisableTrap -> True
      (6,6) None        -> False
    """
    return tile["trapFlags"] == "DisableTrap"
