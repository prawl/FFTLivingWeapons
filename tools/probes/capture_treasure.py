"""Treasure Master: capture accurate per-tile (x,y) by hovering the tile in-game.

The ground truth for a treasure tile is NOT a guide -- it is the game's own on-screen
cursor, whose coords are module-static and already VALIDATED against the display:

    cursor X = 0x140C64A54 (u8),  cursor Y = 0x140C6496C (u8)

So the workflow is: stand a unit on the field, hover the treasure tile, and snapshot the
cursor. The PSX/web guides only tell you WHICH maps / WHAT item / roughly where -- their
ASCII/screenshot orientation provably does NOT map to the engine (x,y). We trust the hover.

This probe is READ-ONLY against the game (RPM only); it writes one repo file, the hand-
maintained source data/trap_treasure_tiles.json (atomic, with a .bak), tiles flagged
verified:true because each one was confirmed on the live map.

  python tools\\probes\\capture_treasure.py watch
      Live readout of cursor (x,y). Hover a few tiles and confirm it tracks your hover
      BEFORE capturing -- this is the sanity check that the addresses still hold on this build.

  python tools\\probes\\capture_treasure.py capture "The Siedge Weald"
      Interactive capture for one map. For each treasure tile:
        1. hover the tile in-game,
        2. alt-tab to this terminal (the in-game cursor stays put -- memory persists),
        3. type the item name and press Enter -> snapshots the cursor (x,y) right then.
      Prompt commands:  trap[:name] -> record a trap tile   |   undo -> drop the last
                        list -> show what's captured         |   done -> merge to the DB & exit
                        abort -> exit WITHOUT writing
"""
import ctypes
import ctypes.wintypes as w
import json
import os
import pathlib
import sys
import time

PROCESS_VM_READ = 0x0010
PROCESS_QUERY_INFORMATION = 0x0400

CURSOR_X = 0x140C64A54      # u8, module-static, validated vs the on-screen coord display
CURSOR_Y = 0x140C6496C      # u8

DB_PATH = pathlib.Path(__file__).resolve().parents[2] / "data" / "trap_treasure_tiles.json"

k32 = ctypes.windll.kernel32
psapi = ctypes.windll.psapi


def find_handle(name="fft_enhanced.exe"):
    arr = (w.DWORD * 4096)()
    needed = w.DWORD()
    psapi.EnumProcesses(ctypes.byref(arr), ctypes.sizeof(arr), ctypes.byref(needed))
    want = PROCESS_QUERY_INFORMATION | PROCESS_VM_READ
    for i in range(needed.value // ctypes.sizeof(w.DWORD)):
        h = k32.OpenProcess(want, False, arr[i])
        if not h:
            continue
        buf = ctypes.create_unicode_buffer(260)
        if psapi.GetModuleBaseNameW(h, None, buf, 260) and buf.value.lower() == name.lower():
            return h
        k32.CloseHandle(h)
    return None


HANDLE = find_handle()
if not HANDLE:
    print("game not running (fft_enhanced.exe)")
    sys.exit(1)


def ru8(addr):
    buf = ctypes.create_string_buffer(1)
    got = ctypes.c_size_t()
    if not k32.ReadProcessMemory(HANDLE, ctypes.c_void_p(addr), buf, 1, ctypes.byref(got)) or got.value != 1:
        return None
    return buf.raw[0]


def cursor():
    return ru8(CURSOR_X), ru8(CURSOR_Y)


def load_db():
    return json.loads(DB_PATH.read_text(encoding="utf-8")) if DB_PATH.exists() else {}


def save_db(db):
    if DB_PATH.exists():
        DB_PATH.with_suffix(".json.bak").write_bytes(DB_PATH.read_bytes())
    tmp = DB_PATH.with_suffix(".json.tmp")
    tmp.write_text(json.dumps(db, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    os.replace(tmp, DB_PATH)


def parse_entry(text):
    """A prompt line -> a tile dict (without x,y), or None for a non-tile command."""
    text = text.strip()
    if text.lower().startswith("trap"):
        _, _, name = text.partition(":")
        tile = {"kind": "trap", "verified": True}
        if name.strip():
            tile["item"] = name.strip()
        return tile
    return {"kind": "treasure", "item": text, "verified": True}


def fmt(tile):
    label = tile.get("item", tile["kind"])
    return f"({tile['x']},{tile['y']}) {tile['kind']}: {label}"


cmd = sys.argv[1] if len(sys.argv) > 1 else "watch"

if cmd == "watch":
    print("cursor (x,y) live -- hover tiles, confirm it tracks. Ctrl-C to stop.")
    last = None
    try:
        while True:
            xy = cursor()
            if xy != last:
                x, y = xy
                print(f"  x={x}  y={y}" if x is not None else "  (unreadable -- is a unit on the field?)")
                last = xy
            time.sleep(0.1)
    except KeyboardInterrupt:
        print("\nstopped.")

elif cmd == "capture":
    if len(sys.argv) < 3:
        print('need a map name, e.g.  capture "The Siedge Weald"')
        sys.exit(2)
    map_name = sys.argv[2]
    x0, y0 = cursor()
    if x0 is None:
        print("cursor unreadable -- get a unit on the field and hover a tile first.")
        sys.exit(1)
    print(f'Capturing for "{map_name}". Cursor reads live (now x={x0} y={y0}).')
    print("Hover a tile -> alt-tab here -> type the item (or 'trap') -> Enter.  ('done' to save)\n")
    captured = []
    while True:
        try:
            line = input("item> ").strip()
        except (EOFError, KeyboardInterrupt):
            print("\n(use 'done' to save or 'abort' to discard)")
            continue
        low = line.lower()
        if low == "abort":
            print("aborted -- nothing written.")
            sys.exit(0)
        if low == "done":
            break
        if low == "list":
            for t in captured:
                print("   " + fmt(t))
            if not captured:
                print("   (none yet)")
            continue
        if low == "undo":
            if captured:
                print("   dropped " + fmt(captured.pop()))
            else:
                print("   nothing to undo")
            continue
        if not line:
            continue
        x, y = cursor()
        if x is None:
            print("   cursor unreadable -- hover a tile in-game first, then type here.")
            continue
        tile = parse_entry(line)
        tile["x"], tile["y"] = x, y
        captured.append(tile)
        print("   captured " + fmt(tile))

    if not captured:
        print("nothing captured -- DB unchanged.")
        sys.exit(0)

    db = load_db()
    entry = db.setdefault(map_name, {"tiles": []})
    tiles = entry.setdefault("tiles", [])
    for t in captured:
        existing = next((e for e in tiles if e.get("x") == t["x"] and e.get("y") == t["y"]), None)
        if existing:
            existing.update(t)
        else:
            tiles.append(t)
    tiles.sort(key=lambda e: (e.get("y", 0), e.get("x", 0)))
    save_db(db)
    print(f'\nwrote {len(captured)} tile(s) into "{map_name}" -> {DB_PATH}')
    for t in captured:
        print("   " + fmt(t))

else:
    print(__doc__)
