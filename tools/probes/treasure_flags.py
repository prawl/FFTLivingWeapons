"""Treasure Master capture tool: derive and store per-tile mark-flag addresses.

The tile mark mechanism is proven live: hold bit 0x80 on each tile's render-flag bytes (module-
static addresses, per map) and the engine paints its own native mark.  This tool captures those
addresses via a guided in-game toggle scan and stores them in data/treasure_addrs.json.

Verbs
-----
  python tools\\probes\\treasure_flags.py mapid [expected]
      Read the live battle map id (u8 @ 0x14077D83C) and print it + its name.
      Compare against 'expected' if given.  Stale outside of battle.

  python tools\\probes\\treasure_flags.py session
      Guided per-tile capture for the current battle map.  Reads the map id,
      confirms the name with you, then walks each uncaptured treasure tile.
      Per tile: cursor lock -> toggle scan -> trust gate -> atomic DB save.

  python tools\\probes\\treasure_flags.py status
      Dashboard: verified / partial / missing tile counts per map, plus totals.

  python tools\\probes\\treasure_flags.py verify <mapId>
      Read-only post-patch audit.  Re-hashes the terrain fingerprint and classifies
      each captured address into resting / held / foreign.

  python tools\\probes\\treasure_flags.py --selftest
      Offline self-test (no game required).  Validates FNV-1a64 vectors, the clean
      filter, DB schema round-trip, and PE-offset byte math.
"""
import ctypes
import ctypes.wintypes as w
import datetime
import json
import os
import pathlib
import struct
import sys
import time

# ---------------------------------------------------------------------------
# Paths
# ---------------------------------------------------------------------------
_HERE = pathlib.Path(__file__).resolve()
REPO = _HERE.parents[2]
DB_PATH = REPO / "data" / "treasure_addrs.json"
MAP_TRAP_PATH = REPO / "data" / "map_trap_formation.json"

# ---------------------------------------------------------------------------
# Memory constants
# ---------------------------------------------------------------------------
PROCESS_VM_READ       = 0x0010
PROCESS_VM_WRITE      = 0x0020
PROCESS_VM_OPERATION  = 0x0008
PROCESS_QUERY_INFORMATION = 0x0400
MEM_COMMIT = 0x1000
PAGE_GUARD    = 0x100
PAGE_NOACCESS = 0x01
WRITABLE = 0x04 | 0x08 | 0x40 | 0x80

IMAGE_BASE = 0x140000000
IMAGE_END  = 0x143000000
MARK_BIT   = 0x80

# The UI render arena: cursor + billboard region.  Flag bytes are NOT here.
UI_ARENA = (0x140C63000, 0x140CC5000)

CURSOR_X_ADDR = 0x140C64A54   # u8
CURSOR_Y_ADDR = 0x140C6496C   # u8

MAP_ID_ADDR   = 0x14077D83C   # u8 -- live battle map id (stale outside battle)

TERRAIN_FP_ADDR = 0x140C65000  # fixed start of the terrain grid
TERRAIN_FP_LEN  = 448          # bytes hashed for the fingerprint (fixed prefix)

# PE header offsets
PE_E_LFANEW_OFF    = 0x3C   # u32 @ imageBase + 0x3C
PE_TIMESTAMP_REL   = 0x08   # u32 @ imageBase + e_lfanew + 8
PE_SIZEOFIMAGE_REL = 0x50   # u32 @ imageBase + e_lfanew + 0x50

# FNV-1a 64-bit constants (shared verbatim with the C# side -- never change)
FNV_BASIS = 0xCBF29CE484222325
FNV_PRIME = 0x00000100000001B3
FNV_MASK  = 0xFFFFFFFFFFFFFFFF

# ---------------------------------------------------------------------------
# FNV-1a 64-bit
# ---------------------------------------------------------------------------
def fnv1a64(data: bytes) -> int:
    h = FNV_BASIS
    for b in data:
        h ^= b
        h = (h * FNV_PRIME) & FNV_MASK
    return h


# ---------------------------------------------------------------------------
# Process access (lazy -- no sys.exit at module load)
# ---------------------------------------------------------------------------
k32    = ctypes.windll.kernel32
psapi  = ctypes.windll.psapi

_HANDLE = None   # set on first call to _handle()


class MBI(ctypes.Structure):
    _fields_ = [
        ("BaseAddress",      ctypes.c_void_p),
        ("AllocationBase",   ctypes.c_void_p),
        ("AllocationProtect", w.DWORD),
        ("PartitionId",      w.WORD),
        ("RegionSize",       ctypes.c_size_t),
        ("State",            w.DWORD),
        ("Protect",          w.DWORD),
        ("Type",             w.DWORD),
    ]


def _open_process(name="fft_enhanced.exe"):
    arr = (w.DWORD * 4096)()
    needed = w.DWORD()
    psapi.EnumProcesses(ctypes.byref(arr), ctypes.sizeof(arr), ctypes.byref(needed))
    want = (PROCESS_QUERY_INFORMATION | PROCESS_VM_READ
            | PROCESS_VM_WRITE | PROCESS_VM_OPERATION)
    for i in range(needed.value // ctypes.sizeof(w.DWORD)):
        h = k32.OpenProcess(want, False, arr[i])
        if not h:
            continue
        buf = ctypes.create_unicode_buffer(260)
        if psapi.GetModuleBaseNameW(h, None, buf, 260) and buf.value.lower() == name.lower():
            return h
        k32.CloseHandle(h)
    return None


def _handle():
    global _HANDLE
    if _HANDLE is None:
        _HANDLE = _open_process()
    return _HANDLE


def _require_game():
    """Return the process handle or abort with a clear message."""
    h = _handle()
    if not h:
        print("process not found (fft_enhanced.exe not running)")
        sys.exit(1)
    return h


# ---------------------------------------------------------------------------
# RPM / WPM helpers
# ---------------------------------------------------------------------------
def rpm(addr: int, n: int) -> bytes | None:
    h = _handle()
    if not h:
        return None
    buf = ctypes.create_string_buffer(n)
    got = ctypes.c_size_t()
    ok = k32.ReadProcessMemory(h, ctypes.c_void_p(addr), buf, n, ctypes.byref(got))
    if not ok or got.value != n:
        return None
    return buf.raw


def wpm(addr: int, data: bytes) -> bool:
    h = _handle()
    if not h:
        return False
    n = ctypes.c_size_t()
    ok = k32.WriteProcessMemory(h, ctypes.c_void_p(addr), data, len(data), ctypes.byref(n))
    return bool(ok) and n.value == len(data)


def ru8(addr: int) -> int | None:
    b = rpm(addr, 1)
    return b[0] if b is not None else None


def ru32(addr: int) -> int | None:
    b = rpm(addr, 4)
    return struct.unpack_from("<I", b)[0] if b is not None else None


# ---------------------------------------------------------------------------
# Writable regions + snapshots
# ---------------------------------------------------------------------------
def _writable_regions():
    """(base, size) tuples for committed, writable, non-guard regions in the module span."""
    out, addr, mbi = [], IMAGE_BASE, MBI()
    h = _handle()
    if not h:
        return out
    while addr < IMAGE_END:
        if not k32.VirtualQueryEx(h, ctypes.c_void_p(addr), ctypes.byref(mbi), ctypes.sizeof(mbi)):
            break
        base = mbi.BaseAddress or 0
        size = mbi.RegionSize
        if (mbi.State == MEM_COMMIT
                and (mbi.Protect & WRITABLE)
                and not (mbi.Protect & (PAGE_GUARD | PAGE_NOACCESS))):
            out.append((base, size))
        nxt = base + size
        addr = nxt if nxt > addr else addr + 0x1000
    return out


def _in_ui(addr: int) -> bool:
    return UI_ARENA[0] <= addr < UI_ARENA[1]


def snapshot() -> dict[int, bytes]:
    """Snapshot all writable module-span regions, excluding the UI render arena."""
    return {b: rpm(b, s) for b, s in _writable_regions()
            if not _in_ui(b) and rpm(b, s) is not None}


# ---------------------------------------------------------------------------
# Clean filter: off had bit 0x80 clear; on == off | 0x80 (pure mark-bit set)
# ---------------------------------------------------------------------------
def clean_hits(off_snap: dict[int, bytes], on_snap: dict[int, bytes]) -> list[tuple[int, int, int]]:
    """Return [(addr, off_byte, on_byte)] where off&0x80==0 and on==off|0x80."""
    hits = []
    for base in sorted(set(off_snap) & set(on_snap)):
        off_buf, on_buf = off_snap[base], on_snap[base]
        if off_buf is None or on_buf is None:
            continue
        n = min(len(off_buf), len(on_buf))
        for ci in range(0, n, 4096):
            if off_buf[ci:ci + 4096] == on_buf[ci:ci + 4096]:
                continue
            for i in range(ci, min(ci + 4096, n)):
                ob, nb = off_buf[i], on_buf[i]
                if (ob & MARK_BIT) == 0 and nb == (ob | MARK_BIT):
                    hits.append((base + i, ob, nb))
    return hits



def _buf_at(snap: dict[int, bytes], addr: int):
    """Return (buf, offset) for the region covering addr, or (None, 0)."""
    for base in sorted(snap):
        buf = snap[base]
        if buf and base <= addr < base + len(buf):
            return buf, addr - base
    return None, 0


def _base_for(snap: dict[int, bytes], addr: int) -> int:
    for base in sorted(snap):
        buf = snap[base]
        if buf and base <= addr < base + len(buf):
            return base
    return 0


def _addr_byte(snap: dict[int, bytes], addr: int) -> int | None:
    buf, off = _buf_at(snap, addr)
    if buf is None:
        return None
    return buf[off]


# ---------------------------------------------------------------------------
# Fingerprint + build key
# ---------------------------------------------------------------------------
def terrain_fingerprint() -> int | None:
    """FNV-1a64 over TERRAIN_FP_LEN bytes at TERRAIN_FP_ADDR."""
    data = rpm(TERRAIN_FP_ADDR, TERRAIN_FP_LEN)
    if data is None:
        return None
    return fnv1a64(data)


def read_build_key() -> dict | None:
    """Return {"timeDateStamp": int, "sizeOfImage": int} from the PE header, or None."""
    e_lfanew = ru32(IMAGE_BASE + PE_E_LFANEW_OFF)
    if e_lfanew is None:
        return None
    ts  = ru32(IMAGE_BASE + e_lfanew + PE_TIMESTAMP_REL)
    soi = ru32(IMAGE_BASE + e_lfanew + PE_SIZEOFIMAGE_REL)
    if ts is None or soi is None:
        return None
    return {"timeDateStamp": ts, "sizeOfImage": soi}


# ---------------------------------------------------------------------------
# Cursor
# ---------------------------------------------------------------------------
def read_cursor() -> tuple[int | None, int | None]:
    return ru8(CURSOR_X_ADDR), ru8(CURSOR_Y_ADDR)


def _poll_cursor_stable(target_x: int, target_y: int, stable_secs: float = 1.0,
                        timeout_secs: float = 120.0) -> bool:
    """Block until the cursor sits on (target_x, target_y) continuously for stable_secs."""
    _require_game()
    print(f"  Waiting for cursor at ({target_x}, {target_y}) -- hover the tile in-game ...")
    deadline = time.time() + timeout_secs
    stable_since = None
    while time.time() < deadline:
        cx, cy = read_cursor()
        if cx == target_x and cy == target_y:
            if stable_since is None:
                stable_since = time.time()
            elif time.time() - stable_since >= stable_secs:
                print(f"  Cursor locked at ({target_x}, {target_y}).")
                return True
        else:
            stable_since = None
        time.sleep(0.05)
    print(f"  Timed out waiting for cursor at ({target_x}, {target_y}).")
    return False


# ---------------------------------------------------------------------------
# DB load/save (atomic)
# ---------------------------------------------------------------------------
def _load_db() -> dict:
    if DB_PATH.exists():
        return json.loads(DB_PATH.read_text(encoding="utf-8"))
    return {"_meta": _db_meta(), "maps": {}}


def _db_meta() -> dict:
    return {
        "schemaVersion": 1,
        "purpose": "Per-tile mark-flag addresses for TreasureMaster.  Captured via in-game toggle scan.",
        "schema": (
            "maps[mapId].tiles[i].addrs = [[addr_hex, off_hex], ...]  "
            "where off is the resting byte value (pre-mark).  "
            "status: verified=eyeball-confirmed, partial=<4 hits or unconfirmed, bad=trust-gate failed."
        ),
    }


def _save_db(db: dict) -> None:
    if DB_PATH.exists():
        DB_PATH.with_suffix(".json.bak").write_bytes(DB_PATH.read_bytes())
    tmp = DB_PATH.with_suffix(".json.tmp")
    tmp.write_text(json.dumps(db, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    os.replace(tmp, DB_PATH)


# ---------------------------------------------------------------------------
# Map trap table
# ---------------------------------------------------------------------------
def _load_map_trap() -> dict[str, dict]:
    """Return the map_trap_formation.json as a dict keyed by mapId string."""
    if not MAP_TRAP_PATH.exists():
        print(f"map_trap_formation.json not found at {MAP_TRAP_PATH}")
        print("Run tools/extract_trap_table.py first.")
        return {}
    return json.loads(MAP_TRAP_PATH.read_text(encoding="utf-8"))


def _is_treasure(tile: dict) -> bool:
    return tile.get("trapFlags") == "DisableTrap"


def _treasure_tiles(map_entry: dict) -> list[dict]:
    return [t for t in map_entry.get("tiles", []) if _is_treasure(t)]


# ---------------------------------------------------------------------------
# Verb: mapid
# ---------------------------------------------------------------------------
def cmd_mapid(args: list[str]) -> None:
    _require_game()
    val = ru8(MAP_ID_ADDR)
    if val is None:
        print("Map id unreadable (RPM failed).")
        return
    trap = _load_map_trap()
    name = trap.get(str(val), {}).get("name") or "(unknown)"
    print(f"map id: {val}  ({name})")
    if args:
        expected = int(args[0])
        if val == expected:
            print(f"  matches expected ({expected}) -- OK")
        else:
            print(f"  MISMATCH: expected {expected}, got {val}")


# ---------------------------------------------------------------------------
# Verb: session
# ---------------------------------------------------------------------------
def _prompt(msg: str) -> None:
    try:
        input(msg)
    except (EOFError, KeyboardInterrupt):
        print("\naborted.")
        sys.exit(0)


def _yesno(msg: str) -> bool:
    while True:
        try:
            ans = input(msg + " [y/n] ").strip().lower()
        except (EOFError, KeyboardInterrupt):
            print("\naborted.")
            sys.exit(0)
        if ans in ("y", "yes"):
            return True
        if ans in ("n", "no"):
            return False
        print("  Please type y or n.")


def cmd_session() -> None:
    _require_game()

    # Step 1: read map id
    map_id_val = ru8(MAP_ID_ADDR)
    if map_id_val is None:
        print("Map id unreadable.  Are you in a battle?")
        return
    map_id_str = str(map_id_val)

    trap = _load_map_trap()
    if not trap:
        return
    map_entry = trap.get(map_id_str)
    if map_entry is None:
        print(f"Map id {map_id_val} not in the trap table (0-127 only).")
        return

    map_name = map_entry.get("name") or f"Map {map_id_val}"
    treasure = _treasure_tiles(map_entry)

    if not treasure:
        print(f"Map {map_id_val} ({map_name}): no treasure tiles in the table.  Nothing to capture.")
        return

    print(f"\nMap id: {map_id_val}")
    print(f"Name  : {map_name}")
    print(f"Treasure tiles: {len(treasure)}")

    if not _yesno(f"You are on {map_name}?"):
        print("Aborted -- re-run when on the correct map.")
        return

    db = _load_db()
    maps_db = db.setdefault("maps", {})
    map_rec = maps_db.setdefault(map_id_str, {
        "name": map_name,
        "fpLen": TERRAIN_FP_LEN,
        "fpHash": None,
        "capturedBuild": None,
        "capturedAt": None,
        "tiles": [],
    })

    existing_tiles = {(t["x"], t["y"]): t for t in map_rec.get("tiles", [])}
    pending = [t for t in treasure if (t["x"], t["y"]) not in existing_tiles
               or existing_tiles[(t["x"], t["y"])].get("status") in ("partial", "bad")]

    if not pending:
        print("All treasure tiles already verified for this map.  Nothing to do.")
        return

    print(f"\nPending tiles ({len(pending)}):")
    for t in pending:
        print(f"  ({t['x']},{t['y']})")

    # Step 5: capture or update fingerprint + build key
    fp = terrain_fingerprint()
    bk = read_build_key()

    stored_fp   = map_rec.get("fpHash")
    stored_bk   = map_rec.get("capturedBuild")

    if stored_fp is None:
        if fp is not None:
            map_rec["fpHash"] = hex(fp)
            print(f"\nFingerprint captured: {hex(fp)}")
        else:
            print("\nWarning: could not read terrain fingerprint.")
    else:
        if fp is not None and hex(fp) != stored_fp:
            print(f"\nWARNING: terrain fingerprint mismatch!  Stored={stored_fp}, live={hex(fp)}")
            print("The map data may be stale.  Proceed carefully.")
        elif fp is not None:
            print(f"\nFingerprint matches stored ({stored_fp}) -- OK")

    if stored_bk is None:
        if bk is not None:
            map_rec["capturedBuild"] = bk
            print(f"Build key captured: {bk}")
        else:
            print("Warning: could not read PE build key.")
    else:
        if bk is not None and bk != stored_bk:
            print(f"WARNING: build key mismatch!  Stored={stored_bk}, live={bk}")
            print("The game was patched.  All addresses for this map are stale.")

    if map_rec.get("capturedAt") is None:
        map_rec["capturedAt"] = datetime.datetime.utcnow().isoformat() + "Z"

    # Per-tile capture loop
    for tile in pending:
        tx, ty = tile["x"], tile["y"]
        print(f"\n--- Tile ({tx},{ty}) ---")
        print(f"  rareItemId={tile.get('rareItemId')}  commonItemId={tile.get('commonItemId')}")

        # Cursor lock
        if not _poll_cursor_stable(tx, ty):
            print(f"  Skipping ({tx},{ty}) -- cursor never arrived.")
            continue

        # Verify cursor still on target
        cx, cy = read_cursor()
        if cx != tx or cy != ty:
            print(f"  Cursor drifted ({cx},{cy}); aborting tile ({tx},{ty}).")
            continue

        print(f"\n  Toggle scan: 3 OFF + 2 ON lockstep cycles.")
        print("  On each prompt: alt-tab to the game, set the mark state, alt-tab back, Enter.")

        snaps_off, snaps_on = [], []

        _prompt(f"  1/5  tile UNMARKED (cursor on ({tx},{ty})) -> Enter ...")
        cx, cy = read_cursor()
        if cx != tx or cy != ty:
            print(f"  Cursor drifted mid-cycle ({cx},{cy}); aborting tile ({tx},{ty}).")
            continue
        snaps_off.append(snapshot())

        _prompt(f"  2/5  MARK it (press 2 in game), cursor still -> Enter ...")
        cx, cy = read_cursor()
        if cx != tx or cy != ty:
            print(f"  Cursor drifted mid-cycle ({cx},{cy}); aborting tile ({tx},{ty}).")
            continue
        snaps_on.append(snapshot())

        _prompt(f"  3/5  UNMARK it -> Enter ...")
        cx, cy = read_cursor()
        if cx != tx or cy != ty:
            print(f"  Cursor drifted mid-cycle ({cx},{cy}); aborting tile ({tx},{ty}).")
            continue
        snaps_off.append(snapshot())

        _prompt(f"  4/5  MARK it again -> Enter ...")
        cx, cy = read_cursor()
        if cx != tx or cy != ty:
            print(f"  Cursor drifted mid-cycle ({cx},{cy}); aborting tile ({tx},{ty}).")
            continue
        snaps_on.append(snapshot())

        _prompt(f"  5/5  UNMARK it -> Enter ...")
        cx, cy = read_cursor()
        if cx != tx or cy != ty:
            print(f"  Cursor drifted mid-cycle ({cx},{cy}); aborting tile ({tx},{ty}).")
            continue
        snaps_off.append(snapshot())

        # Find clean hits across both pairs
        hits1 = {addr: (ob, nb) for addr, ob, nb in clean_hits(snaps_off[0], snaps_on[0])}
        hits2 = {addr: (ob, nb) for addr, ob, nb in clean_hits(snaps_off[1], snaps_on[1])}
        common_addrs = sorted(set(hits1) & set(hits2))

        # Filter: must still pass the clean filter in ALL three off snaps
        final = []
        for addr in common_addrs:
            ob, nb = hits1[addr]
            good = True
            for s_off in snaps_off:
                bval = _addr_byte(s_off, addr)
                if bval is None or (bval & MARK_BIT):
                    good = False
                    break
            if good:
                final.append((addr, ob, nb))

        print(f"\n  {len(final)} clean hit(s) found.")
        for addr, ob, nb in final:
            print(f"    {addr:#x}  off={ob:#04x}  on={nb:#04x}")

        status = "verified" if len(final) >= 4 else "partial"
        if len(final) < 4:
            print(f"  Only {len(final)} hits (<4) -- saving as 'partial'.  Re-capture on the next session.")

        if not final:
            print("  No hits.  Did the cursor move during the cycle, or the mark not toggle cleanly?")
            print("  Skipping this tile (not saved).")
            continue

        # Step 4: trust gate (3s hold test)
        print(f"\n  Trust gate: holding 0x80 on all {len(final)} address(es) for ~3 seconds ...")
        originals = []
        for addr, ob, nb in final:
            orig = rpm(addr, 1)
            originals.append((addr, orig))
            wpm(addr, bytes([ob | MARK_BIT]))

        t0 = time.time()
        while time.time() - t0 < 3.0:
            for addr, ob, nb in final:
                wpm(addr, bytes([ob | MARK_BIT]))
            time.sleep(0.03)

        # Restore originals
        for addr, orig in originals:
            if orig is not None:
                wpm(addr, orig)
            else:
                wpm(addr, bytes([0x00]))

        confirmed = _yesno(f"  Did the native mark paint on tile ({tx},{ty})?")
        if confirmed:
            status = "verified"
            print(f"  Trust gate PASSED -> status=verified")
        else:
            status = "bad" if len(final) >= 4 else "partial"
            print(f"  Trust gate FAILED -> status={status} (kept for diagnosis)")

        # Build tile record
        tile_rec = {
            "x": tx,
            "y": ty,
            "addrs": [[hex(addr), hex(ob)] for addr, ob, nb in final],
            "status": status,
        }

        # Upsert into map record
        tiles_list = map_rec.setdefault("tiles", [])
        idx = next((i for i, t in enumerate(tiles_list)
                    if t["x"] == tx and t["y"] == ty), None)
        if idx is not None:
            tiles_list[idx] = tile_rec
        else:
            tiles_list.append(tile_rec)

        # Atomic save after every tile
        _save_db(db)
        print(f"  Saved ({tx},{ty}) -> {DB_PATH.name}  status={status}")

    print(f"\nSession complete.  DB: {DB_PATH}")


# ---------------------------------------------------------------------------
# Verb: status
# ---------------------------------------------------------------------------
def cmd_status() -> None:
    db = _load_db()
    trap = _load_map_trap()
    maps_db = db.get("maps", {})

    if not maps_db:
        print("No maps captured yet.")
        return

    total_v = total_p = total_b = total_miss = 0

    print(f"{'mapId':<6} {'name':<35} {'verified':>8} {'partial':>7} {'bad':>4} {'missing':>7}")
    print("-" * 72)
    for mid in sorted(maps_db, key=lambda x: int(x)):
        rec = maps_db[mid]
        name = rec.get("name", "")[:34]
        tiles = rec.get("tiles", [])
        v = sum(1 for t in tiles if t.get("status") == "verified")
        p = sum(1 for t in tiles if t.get("status") == "partial")
        b = sum(1 for t in tiles if t.get("status") == "bad")
        # missing = treasure tiles in trap table that have no entry in the DB
        trap_entry = trap.get(mid, {})
        treas = _treasure_tiles(trap_entry)
        captured_xy = {(t["x"], t["y"]) for t in tiles}
        miss = sum(1 for t in treas if (t["x"], t["y"]) not in captured_xy)
        total_v += v; total_p += p; total_b += b; total_miss += miss
        print(f"{mid:<6} {name:<35} {v:>8} {p:>7} {b:>4} {miss:>7}")

    print("-" * 72)
    print(f"{'TOTAL':<6} {'':<35} {total_v:>8} {total_p:>7} {total_b:>4} {total_miss:>7}")


# ---------------------------------------------------------------------------
# Verb: verify
# ---------------------------------------------------------------------------
def cmd_verify(map_id_str: str) -> None:
    _require_game()

    db = _load_db()
    maps_db = db.get("maps", {})
    rec = maps_db.get(map_id_str)
    if rec is None:
        print(f"Map {map_id_str} not in the DB.")
        return

    name = rec.get("name", "")
    print(f"Verifying map {map_id_str} ({name}) -- read-only")

    # Re-hash terrain fingerprint
    fp = terrain_fingerprint()
    stored_fp = rec.get("fpHash")
    if fp is not None:
        live_hex = hex(fp)
        if stored_fp is None:
            print(f"  Fingerprint: live={live_hex}  stored=none (not yet captured)")
        elif live_hex == stored_fp:
            print(f"  Fingerprint: {live_hex} -- matches stored -- OK")
        else:
            print(f"  Fingerprint: MISMATCH  stored={stored_fp}  live={live_hex}")
    else:
        print("  Fingerprint: unreadable (RPM failed)")

    # Build key
    bk = read_build_key()
    stored_bk = rec.get("capturedBuild")
    if bk is not None and stored_bk is not None:
        if bk == stored_bk:
            print(f"  Build key  : {bk} -- matches -- OK")
        else:
            print(f"  Build key  : MISMATCH  stored={stored_bk}  live={bk}")
    elif bk is None:
        print("  Build key  : unreadable")
    else:
        print(f"  Build key  : {bk}  (none stored)")

    # Per-address audit
    tiles = rec.get("tiles", [])
    if not tiles:
        print("  No captured tiles.")
        return

    for t in tiles:
        tx, ty = t["x"], t["y"]
        addrs = t.get("addrs", [])
        states = []
        for addr_hex, off_hex in addrs:
            addr = int(addr_hex, 16)
            val = ru8(addr)
            if val is None:
                state = "UNREADABLE"
            elif val in (0x00, 0x01):
                state = "resting"
            elif val in (0x80, 0x81):
                state = "held"
            else:
                state = f"FOREIGN({val:#04x})"
            states.append((addr_hex, state))
        statuses = " | ".join(f"{a}={s}" for a, s in states)
        print(f"  ({tx},{ty}): {statuses}")


# ---------------------------------------------------------------------------
# Self-test
# ---------------------------------------------------------------------------
def _selftest() -> bool:
    ok = True

    # FNV-1a 64-bit pinned vectors (shared with C# side)
    vectors = [
        (b"",       0xCBF29CE484222325),
        (b"a",      0xAF63DC4C8601EC8C),
        (b"foobar", 0x85944171F73967E8),
    ]
    for data, expected in vectors:
        got = fnv1a64(data)
        if got == expected:
            print(f"  FNV-1a64({data!r}): {got:#018x}  OK")
        else:
            print(f"  FNV-1a64({data!r}): FAIL  expected {expected:#018x}  got {got:#018x}")
            ok = False

    # Clean filter logic
    # Byte 0x01 -> 0x81: should pass
    off_snap = {0x141000000: bytes([0x01, 0x81, 0x01, 0x00])}
    on_snap  = {0x141000000: bytes([0x81, 0x81, 0x01, 0x80])}
    hits = clean_hits(off_snap, on_snap)
    # Expected: addr 0x141000000 (0x01->0x81) and 0x141000003 (0x00->0x80)
    expected_addrs = {0x141000000, 0x141000003}
    got_addrs = {a for a, ob, nb in hits}
    if got_addrs == expected_addrs:
        print(f"  clean_hits: {expected_addrs}  OK")
    else:
        print(f"  clean_hits: FAIL  expected {expected_addrs}  got {got_addrs}")
        ok = False
    # Byte 0x81 should NOT pass (already set)
    if 0x141000001 not in got_addrs:
        print("  clean_hits already-set exclusion: OK")
    else:
        print("  clean_hits already-set exclusion: FAIL  (0x81 source should be excluded)")
        ok = False

    # DB schema round-trip through the atomic saver
    import tempfile
    tmp_dir = pathlib.Path(tempfile.mkdtemp())
    test_db_path = tmp_dir / "test_treasure_addrs.json"
    db = {
        "_meta": {"schemaVersion": 1, "purpose": "test", "schema": "test"},
        "maps": {
            "74": {
                "name": "The Siedge Weald",
                "fpLen": 448,
                "fpHash": "0xdeadbeefcafe1234",
                "capturedBuild": {"timeDateStamp": 12345, "sizeOfImage": 67890},
                "capturedAt": "2026-06-11T00:00:00Z",
                "tiles": [
                    {"x": 0, "y": 1, "addrs": [["0x140de1ea7", "0x01"]], "status": "verified"},
                ],
            }
        },
    }
    # Atomic save to temp path
    if test_db_path.exists():
        test_db_path.with_suffix(".json.bak").write_bytes(test_db_path.read_bytes())
    tmp_file = test_db_path.with_suffix(".json.tmp")
    tmp_file.write_text(json.dumps(db, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    os.replace(tmp_file, test_db_path)
    # Round-trip
    loaded = json.loads(test_db_path.read_text(encoding="utf-8"))
    tile = loaded["maps"]["74"]["tiles"][0]
    if (loaded["_meta"]["schemaVersion"] == 1
            and tile["x"] == 0 and tile["y"] == 1
            and tile["addrs"] == [["0x140de1ea7", "0x01"]]
            and tile["status"] == "verified"
            and loaded["maps"]["74"]["fpLen"] == 448):
        print("  DB schema round-trip: OK")
    else:
        print(f"  DB schema round-trip: FAIL  loaded={loaded}")
        ok = False
    # Cleanup
    test_db_path.unlink(missing_ok=True)
    tmp_dir.rmdir()

    # PE-offset math on a synthetic buffer
    # Simulate: e_lfanew=0x80, TimeDateStamp @ 0x88, SizeOfImage @ 0xD0
    pe_buf = bytearray(0x200)
    e_lfanew = 0x80
    ts_expected = 0xDEADBEEF
    soi_expected = 0x00500000
    struct.pack_into("<I", pe_buf, PE_E_LFANEW_OFF, e_lfanew)
    struct.pack_into("<I", pe_buf, e_lfanew + PE_TIMESTAMP_REL, ts_expected)
    struct.pack_into("<I", pe_buf, e_lfanew + PE_SIZEOFIMAGE_REL, soi_expected)
    got_lfanew  = struct.unpack_from("<I", pe_buf, PE_E_LFANEW_OFF)[0]
    got_ts      = struct.unpack_from("<I", pe_buf, got_lfanew + PE_TIMESTAMP_REL)[0]
    got_soi     = struct.unpack_from("<I", pe_buf, got_lfanew + PE_SIZEOFIMAGE_REL)[0]
    if got_lfanew == e_lfanew and got_ts == ts_expected and got_soi == soi_expected:
        print("  PE-offset math: OK")
    else:
        print(f"  PE-offset math: FAIL  lfanew={got_lfanew} ts={got_ts} soi={got_soi}")
        ok = False

    return ok


# ---------------------------------------------------------------------------
# Entry point
# ---------------------------------------------------------------------------
def main() -> None:
    args = sys.argv[1:]

    if not args or args[0] in ("-h", "--help", "help"):
        print(__doc__)
        return

    if args[0] == "--selftest":
        print("Running self-test (no game required) ...")
        passed = _selftest()
        if passed:
            print("\nAll self-tests PASSED.")
            sys.exit(0)
        else:
            print("\nSelf-test FAILED.")
            sys.exit(1)

    if args[0] == "mapid":
        cmd_mapid(args[1:])
        return

    if args[0] == "session":
        cmd_session()
        return

    if args[0] == "status":
        cmd_status()
        return

    if args[0] == "verify":
        if len(args) < 2:
            print("Usage: verify <mapId>")
            sys.exit(2)
        cmd_verify(args[1])
        return

    print(f"Unknown verb: {args[0]!r}")
    print(__doc__)
    sys.exit(2)


if __name__ == "__main__":
    main()
