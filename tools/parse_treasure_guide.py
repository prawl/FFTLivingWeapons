"""Parse a clean one-char-per-tile treasure guide into tile coordinates.

The PSX/FAQ `+-+-+` art draws terrain WALLS, which merge same-height tiles and float the
panel digit between tile slots -- ambiguous by a tile. So we do NOT parse that art. Instead
the guide is rewritten as a clean grid (one printable char per tile), and the engine (x,y)
falls straight out of the geometry, because the coordinate convention is FORCED:

    bottom-right tile = (0,0)  ->  x grows LEFTward, y grows UPward
    x = (width-1) - col        y = (height-1) - row

Grid file format (one or more blocks):

    @map The Siedge Weald
    .....            ; '.' = tile, 'A'-'Z' = a panel, '#' or ' ' = off-map (pad to a rectangle)
    ....A
    ...B.
    @items
    A treasure Bow Gun (Scoutbolt)
    B trap
    @end

Lines starting with ';' are comments. Coords are emitted verified:false -- guide-derived
DRAFTS. Confirm with tools/probes/capture_treasure.py before trusting one as ground truth.

  python tools\\parse_treasure_guide.py guide.txt          # parse + print tiles (dry run)
  python tools\\parse_treasure_guide.py guide.txt --write   # merge drafts into data/trap_treasure_tiles.json
  python tools\\parse_treasure_guide.py --selftest          # prove the coordinate transform

Validation: any map present in ANCHORS (tiles already probe-verified in-game) must reproduce
those exact (x,y), or the parse FAILS loudly -- this catches an x<->y swap or a mis-drawn edge.
"""
import json
import pathlib
import sys

DB_PATH = pathlib.Path(__file__).resolve().parents[1] / "data" / "trap_treasure_tiles.json"

# Ground-truth tiles confirmed in-game (the capture probe). A parsed map listed here MUST
# reproduce every anchor or the convention is wrong. Keyed by IC map name -> {(x,y), ...}.
ANCHORS = {
    "The Siedge Weald": {(0, 1), (1, 9)},
}

TILE_CHARS = set(".# ")  # everything else that's A-Z is a panel; off-map is '#'/' '/'.'


def parse_blocks(text):
    """Yield (map_name, grid_rows, legend{letter:(kind,item)}) per @map block."""
    name = None
    grid = []
    legend = {}
    mode = None  # None | 'grid' | 'items'
    for raw in text.splitlines():
        line = raw.rstrip("\n")
        if line.lstrip().startswith(";"):
            continue
        stripped = line.strip()
        if stripped.startswith("@map"):
            if name is not None:
                yield name, grid, legend
            name, grid, legend, mode = stripped[4:].strip(), [], {}, "grid"
        elif stripped == "@items":
            mode = "items"
        elif stripped == "@end":
            if name is not None:
                yield name, grid, legend
            name, grid, legend, mode = None, [], {}, None
        elif mode == "grid":
            if line.strip():
                grid.append(line)
        elif mode == "items":
            if not stripped:
                continue
            parts = stripped.split(None, 2)
            letter = parts[0]
            kind = parts[1] if len(parts) > 1 else "treasure"
            item = parts[2] if len(parts) > 2 else None
            legend[letter] = (kind, item)
    if name is not None:
        yield name, grid, legend


def tiles_for(name, grid, legend):
    """Clean grid -> [tile dicts]. Raises on a ragged grid (the right/bottom edges anchor 0,0)."""
    if not grid:
        return []
    widths = {len(r) for r in grid}
    if len(widths) != 1:
        raise ValueError(f'"{name}": grid rows differ in width {sorted(widths)} -- pad to a rectangle '
                         f"(the right column is x=0; ragged rows misplace every tile).")
    width = widths.pop()
    height = len(grid)
    out = []
    for r, row in enumerate(grid):
        for c, ch in enumerate(row):
            if ch in TILE_CHARS:
                continue
            x = (width - 1) - c
            y = (height - 1) - r
            kind, item = legend.get(ch, ("treasure", None))
            tile = {"x": x, "y": y, "kind": kind, "verified": False}
            if item:
                tile["item"] = item
            tile["_panel"] = ch
            out.append(tile)
    return out


def validate(name, tiles):
    if name not in ANCHORS:
        return None
    got = {(t["x"], t["y"]) for t in tiles}
    missing = ANCHORS[name] - got
    if missing:
        raise ValueError(f'"{name}": parse did NOT reproduce probe-verified tiles {sorted(missing)} '
                         f"(got {sorted(got)}). The grid is mis-drawn or x/y are swapped.")
    return f'"{name}": reproduced all {len(ANCHORS[name])} anchor tile(s) -- convention OK.'


def merge_into_db(parsed):
    db = json.loads(DB_PATH.read_text(encoding="utf-8")) if DB_PATH.exists() else {}
    added = 0
    for name, tiles in parsed.items():
        entry = db.setdefault(name, {"tiles": []})
        existing = entry.setdefault("tiles", [])
        for t in tiles:
            clean = {k: v for k, v in t.items() if not k.startswith("_")}
            hit = next((e for e in existing if e.get("x") == t["x"] and e.get("y") == t["y"]), None)
            if hit:
                if t.get("item") and not hit.get("item"):
                    hit["item"] = t["item"]  # never downgrade a probe-verified flag
            else:
                existing.append(clean)
                added += 1
        existing.sort(key=lambda e: (e.get("y", 0), e.get("x", 0)))
    if DB_PATH.exists():
        DB_PATH.with_suffix(".json.bak").write_bytes(DB_PATH.read_bytes())
    DB_PATH.write_text(json.dumps(db, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    return added


def selftest():
    # A synthetic 5-wide x 11-tall grid placing A at (0,1) and B at (1,9):
    #   x=0 -> col 4 (width-1);  y=1 -> row 9 (height-2)
    #   x=1 -> col 3 (width-2);  y=9 -> row 1 (height-1-9)
    rows = ["....." for _ in range(11)]
    rows[9] = "....A"
    rows[1] = "...B."
    tiles = tiles_for("The Siedge Weald", rows, {"A": ("treasure", "Bow Gun"), "B": ("treasure", "Escutcheon")})
    coords = {t["_panel"]: (t["x"], t["y"]) for t in tiles}
    assert coords["A"] == (0, 1), coords
    assert coords["B"] == (1, 9), coords
    assert validate("The Siedge Weald", tiles) is not None
    print("selftest OK:", coords)


def main(argv):
    if "--selftest" in argv:
        selftest()
        return 0
    paths = [a for a in argv if not a.startswith("--")]
    if not paths:
        print(__doc__)
        return 2
    text = pathlib.Path(paths[0]).read_text(encoding="utf-8")
    parsed = {}
    for name, grid, legend in parse_blocks(text):
        tiles = tiles_for(name, grid, legend)
        msg = validate(name, tiles)
        if msg:
            print("  " + msg)
        parsed[name] = tiles
        print(f'"{name}": {len(tiles)} tile(s)')
        for t in tiles:
            label = t.get("item", t["kind"])
            print(f"    ({t['x']},{t['y']}) [{t['_panel']}] {t['kind']}: {label}")
    if "--write" in argv:
        added = merge_into_db(parsed)
        print(f"\nmerged {added} new draft tile(s) -> {DB_PATH}")
    else:
        print("\n(dry run -- pass --write to merge drafts into the DB)")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
