"""Render-node world-coordinate fit table (2026-07-10, LW-65 companion to node_move_watch.py).

For every unit render node (list head 0x140D3A410, +0x148 combat back-pointer), print the
position fields against the known tile so the linear map verifies at a glance:
  node +0x4C u16 = world X = 28*tileX + 14
  node +0x50 u16 = world Y = 28*tileY + 14
  node +0x4E u16 (signed) = world Z = -12 * height (negative-up)
  node +0x88/+0x89/+0x8A = the AI tile key (x, y, layer); +0x2C u16 = facing angle (0x2000 steps)
Read-only; run mid-battle. A row whose world fields break the formula means the unit is
mid-animation (lerping) or the node was hand-built without the tile stamp (the LW-58 double).

    python tools\\probes\\node_world_fit.py
"""
import pathlib
import struct
import sys

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
from battle_cheats import rpm, ru8, ru16, _require_game

UNITS = 0x141853CE0
HEAD = 0x140D3A410

def u64(a):
    b = rpm(a, 8)
    return None if b is None else struct.unpack("<Q", b)[0]

def main():
    _require_game()
    print(f"{'slot':>4} {'tile':>9} {'worldX+4C':>9} {'fit':>5} {'worldY+50':>9} {'fit':>5} "
          f"{'worldZ+4E':>9} {'facing+2C':>9}")
    cur = u64(HEAD)
    for _ in range(64):
        if not cur:
            break
        c = u64(cur + 0x148)
        off = (c or 0) - UNITS
        if c and 0 <= off < 21 * 0x200 and off % 0x200 == 0:
            s = off // 0x200
            tx, ty = ru8(cur + 0x88), ru8(cur + 0x89)
            wx, wy = ru16(cur + 0x4C), ru16(cur + 0x50)
            wz = ru16(cur + 0x4E)
            wz = wz - 65536 if wz > 32767 else wz
            fx = "OK" if wx == 28 * tx + 14 else "??"
            fy = "OK" if wy == 28 * ty + 14 else "??"
            print(f"{s:>4} ({tx:>2},{ty:>2})  {wx:>9} {fx:>5} {wy:>9} {fy:>5} {wz:>9} "
                  f"{ru16(cur + 0x2C):>9}")
        cur = u64(cur)

if __name__ == "__main__":
    main()
