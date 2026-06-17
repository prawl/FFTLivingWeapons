#!/usr/bin/env python
"""
Treasure Master capture-tool RE-ANCHOR finder for FFT:IC 1.5 (READ-ONLY).

Re-finds the 4 moved addresses capture_treasure.py / treasure_flags.py depend on:
  MAP_ID   pre-1.5 0x14077D83C  (u8 battle map id)
  CURSOR_X pre-1.5 0x140C64A54  (u8)   CURSOR_Y pre-1.5 0x140C6496C  (u8)
  TERRAIN  pre-1.5 0x140C65000  (terrain grid)

Verbs:
  peek                 read OLD + PREDICTED addrs, print values (map id should be the live map)
  snap <file>          snapshot the 0x140C6 cursor region to <file> (for the cursor diff)
  diff <f1> <f2>       bytes that changed by +/-1 between two snaps = cursor X / Y candidates
  mapscan <expected>   scan the 0x14077-0x14079 span for a u8 == <expected> near the prediction
RPM only -- cannot crash the game.
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ct_probe import PROC, PV, find_pid, k32, rd

OLD_MAPID, OLD_CX, OLD_CY, OLD_TER = 0x14077D83C, 0x140C64A54, 0x140C6496C, 0x140C65000
D6000, D676C = 0x6000, 0x676C
PRED_MAPID = OLD_MAPID + D6000   # 0x14078383C
PRED_CX = OLD_CX + D676C         # 0x140C6B1C0
PRED_CY = OLD_CY + D676C         # 0x140C6B0D8
PRED_TER = OLD_TER + D676C       # 0x140C6B76C

# cursor region snapshot window (covers old + predicted cursor/terrain)
SNAP_LO, SNAP_HI = 0x140C60000, 0x140C70000


def _h():
    pid = find_pid(PROC)
    if not pid:
        print(f"{PROC} not running"); sys.exit(1)
    return k32.OpenProcess(PV, False, pid)


def u8(h, a):
    b = rd(h, a, 1)
    return b[0] if b else None


def cmd_peek():
    h = _h()
    print("              OLD                PREDICTED (+delta)")
    print(f"MAP_ID    @{OLD_MAPID:011x}={_v(u8(h,OLD_MAPID))}   @{PRED_MAPID:011x}={_v(u8(h,PRED_MAPID))}  (+0x6000)")
    print(f"CURSOR_X  @{OLD_CX:011x}={_v(u8(h,OLD_CX))}   @{PRED_CX:011x}={_v(u8(h,PRED_CX))}  (+0x676C)")
    print(f"CURSOR_Y  @{OLD_CY:011x}={_v(u8(h,OLD_CY))}   @{PRED_CY:011x}={_v(u8(h,PRED_CY))}  (+0x676C)")
    ter = rd(h, PRED_TER, 14)
    print(f"TERRAIN   @{PRED_TER:011x} first14={ter.hex() if ter else 'unreadable'}")
    k32.CloseHandle(h)


def _v(x):
    return "??" if x is None else f"{x:>3}"


def cmd_snap(path):
    h = _h()
    buf = rd(h, SNAP_LO, SNAP_HI - SNAP_LO)
    k32.CloseHandle(h)
    if not buf:
        print("snapshot read failed"); return
    open(path, "wb").write(buf)
    print(f"snapped {len(buf)} bytes @0x{SNAP_LO:011x} -> {path}")


def cmd_diff(f1, f2):
    b1, b2 = open(f1, "rb").read(), open(f2, "rb").read()
    n = min(len(b1), len(b2))
    hits = []
    for i in range(n):
        if b1[i] != b2[i] and abs(b1[i] - b2[i]) <= 3 and b1[i] < 40 and b2[i] < 40:
            hits.append((SNAP_LO + i, b1[i], b2[i]))
    print(f"{len(hits)} byte(s) changed by <=3 (both < 40 = plausible tile coords):")
    for a, v1, v2 in hits:
        rel = a - OLD_CX
        print(f"  @0x{a:011x}  {v1} -> {v2}   (old_CURSOR_X{'+' if rel>=0 else '-'}0x{abs(rel):x})")
    # Highlight pairs 0xE8 apart (the old CURSOR_X - CURSOR_Y spacing).
    addrs = {a for a, _, _ in hits}
    print("\n  pairs 0xE8 apart (X above Y, the old layout):")
    for a, v1, v2 in hits:
        if (a - 0xE8) in addrs:
            print(f"    X@0x{a:011x}  Y@0x{a-0xE8:011x}")


def cmd_diff3(f1, f2, f3):
    """f1=start, f2=moved, f3=moved BACK to start. The cursor returns (b1==b3, b2 differs);
    animation/camera noise does NOT return. Isolates the cursor X/Y bytes."""
    b1, b2, b3 = (open(f, "rb").read() for f in (f1, f2, f3))
    n = min(len(b1), len(b2), len(b3))
    hits = []
    for i in range(n):
        if b1[i] == b3[i] and b1[i] != b2[i] and b1[i] < 40 and b2[i] < 40:
            hits.append((SNAP_LO + i, b1[i], b2[i]))
    print(f"{len(hits)} byte(s): returned to start (b1==b3) AND moved in b2 = cursor-correlated:")
    for a, v1, v2 in hits:
        rel = a - OLD_CX
        print(f"  @0x{a:011x}  start={v1} moved={v2}   (old_CURSOR_X{'+' if rel>=0 else '-'}0x{abs(rel):x})")
    addrs = {a for a, _, _ in hits}
    if hits:
        print("\n  adjacent groupings (cursor X/Y usually sit close):")
        for a, v1, v2 in hits:
            for gap in (1, 2, 3, 4, 0xE8):
                if (a + gap) in addrs:
                    print(f"    @0x{a:011x} & @0x{a+gap:011x}  (gap 0x{gap:x})")


import time

# Clean cursor candidates from the diff3 (camera-block noise at 0x140c6c7xx excluded).
WATCH_CANDS = [0x140c6adac, 0x140c6afb8, 0x140c6b311, 0x140c6b314,
               0x140c6b31c, 0x140c6b7ab, 0x140c6b7c0]


def cmd_watchall(seconds=10.0, hz=5.0):
    """Print live values of the clean cursor candidates while the player moves the cursor.
    The two bytes that track tile movement coherently (range 0..N, +/-1 per tile) are X and Y."""
    h = _h()
    print("   t   " + "  ".join(f"{a & 0xffff:04x}" for a in WATCH_CANDS))
    end = int(seconds * hz)
    last = None
    for t in range(end):
        vals = tuple(u8(h, a) for a in WATCH_CANDS)
        if vals != last:
            print(f"  {t/hz:4.1f}  " + "  ".join(_v(v) for v in vals))
            last = vals
        time.sleep(1.0 / hz)
    k32.CloseHandle(h)


# --- runtime re-anchor: LiveBattleMapId (two-map diff) + TerrainGrid confirm ---
MAPID_LO, MAPID_HI = 0x140770000, 0x1407A0000     # region holding the old map-id 0x14077D83C
TERRAIN_ADDR = 0x140C65000                          # OLD runtime TerrainGrid (now stale/zeros on 1.5)
TERRAIN_CAND = 0x140C6B440                          # 1.5 CONFIRMED grid start +0x6440 (v2 hash matched map80's stored fp)
TERRAIN_LEN = 7 * 208                               # 1456 bytes


_FNV_BASIS, _FNV_PRIME, _FNV_MASK = 0xCBF29CE484222325, 0x100000001B3, 0xFFFFFFFFFFFFFFFF


def _fnv(byts):
    h = _FNV_BASIS
    for b in byts:
        h = ((h ^ b) * _FNV_PRIME) & _FNV_MASK
    return h


def _v3hash(raw):
    return _fnv(raw[i] for i in range(len(raw)) if (i % 7) in (2, 3, 4, 5))


def _v2hash(raw):
    n = len(raw) // 7
    return _fnv(raw[i * 7] for i in range(n))


def cmd_terrainfind(target_hex, ver, span_hex="0x40000"):
    """Scan start offsets across a wide window around TERRAIN_CAND for a 1456-byte block whose
    v<ver> hash matches the stored fingerprint -- pins the EXACT TerrainGrid start byte-for-byte.
    Efficient v2 via a strided slice (field-0 bytes); v3 falls back to per-byte."""
    target = int(target_hex, 16)
    span = int(span_hex, 16)
    h = _h()
    lo = TERRAIN_CAND - span
    buf = rd(h, lo, 2 * span + TERRAIN_LEN)
    k32.CloseHandle(h)
    if not buf:
        print("read failed"); return
    hits = []
    n = len(buf) - TERRAIN_LEN
    if str(ver) == "2":
        for off in range(0, n):
            if _fnv(buf[off:off + TERRAIN_LEN:7]) == target:   # field-0 = every 7th byte
                hits.append(lo + off)
    else:
        for off in range(0, n):
            if _v3hash(buf[off:off + TERRAIN_LEN]) == target:
                hits.append(lo + off)
    print(f"v{ver} hash == 0x{target:016x} scanning +/-0x{span:x}: {len(hits)} start(s)")
    for a in hits:
        rel = a - 0x140C65000
        print(f"  -> TerrainGrid = 0x{a:011x}   (old 0x140C65000 {'+' if rel>=0 else '-'}0x{abs(rel):x})")
    if not hits:
        c = buf[span:span + TERRAIN_LEN]
        print(f"  no match in window -- terrain DATA likely changed on 1.5 (-> use map-id-only).")
        print(f"    live @cand: v2=0x{_fnv(c[::7]):016x}  v3=0x{_v3hash(c):016x}")


def cmd_battlesnap(path):
    """Snapshot the map-id region to <path> and print the live terrain-grid v3 hash.
    Run once per battle (on two different maps) for the LiveBattleMapId diff."""
    h = _h()
    buf = rd(h, MAPID_LO, MAPID_HI - MAPID_LO)
    for label, addr in (("OLD ", TERRAIN_ADDR), ("CAND", TERRAIN_CAND)):
        ter = rd(h, addr, TERRAIN_LEN)
        if ter:
            print(f"terrain {label} @0x{addr:011x}: v3hash=0x{_v3hash(ter):016x}  first14={ter[:14].hex()}")
        else:
            print(f"terrain {label} @0x{addr:011x}: UNREADABLE")
    k32.CloseHandle(h)
    if buf:
        open(path, "wb").write(buf)
        print(f"map-id region snapped ({len(buf)} bytes) -> {path}")
    else:
        print("map-id region read FAILED")


def cmd_mapdiff(fa, ida, fb, idb):
    """Find LiveBattleMapId: the byte == idA in snap A AND == idB in snap B."""
    ida, idb = int(ida), int(idb)
    ba, bb = open(fa, "rb").read(), open(fb, "rb").read()
    n = min(len(ba), len(bb))
    hits = [MAPID_LO + i for i in range(n) if ba[i] == ida and bb[i] == idb]
    print(f"bytes == {ida} (mapA) AND == {idb} (mapB): {len(hits)} hit(s)")
    for a in hits:
        rel = a - 0x14077D83C
        print(f"  @0x{a:011x}   (old_LiveBattleMapId{'+' if rel>=0 else '-'}0x{abs(rel):x})")
    if len(hits) == 1:
        print(f"\n  -> LiveBattleMapId (1.5) = 0x{hits[0]:011x}")


def cmd_mapscan(expected):
    h = _h()
    lo, hi = 0x140780000, 0x1407A0000
    buf = rd(h, lo, hi - lo)
    k32.CloseHandle(h)
    if not buf:
        print("read failed"); return
    hits = [lo + i for i, b in enumerate(buf) if b == expected]
    # rank by closeness to the prediction
    hits.sort(key=lambda a: abs(a - PRED_MAPID))
    print(f"u8 == {expected} in 0x{lo:x}..0x{hi:x}: {len(hits)} hits; nearest to prediction:")
    for a in hits[:12]:
        rel = a - OLD_MAPID
        print(f"  @0x{a:011x}  (old_MAP_ID{'+' if rel>=0 else '-'}0x{abs(rel):x})")


def main():
    a = sys.argv
    m = a[1] if len(a) > 1 else "peek"
    if m == "peek":
        cmd_peek()
    elif m == "snap":
        cmd_snap(a[2])
    elif m == "diff":
        cmd_diff(a[2], a[3])
    elif m == "diff3":
        cmd_diff3(a[2], a[3], a[4])
    elif m == "watchall":
        cmd_watchall(float(a[2]) if len(a) > 2 else 10.0)
    elif m == "battlesnap":
        cmd_battlesnap(a[2])
    elif m == "mapdiff":
        cmd_mapdiff(a[2], a[3], a[4], a[5])
    elif m == "terrainfind":
        cmd_terrainfind(a[2], a[3])
    elif m == "mapscan":
        cmd_mapscan(int(a[2]))
    else:
        print(__doc__)


if __name__ == "__main__":
    main()
