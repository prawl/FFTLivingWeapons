#!/usr/bin/env python
r"""Read only. Snapshots the equipment screen object behind the 0x1402D6FB8 row refresher so two states can be diffed.

Live 2026-10-19 that chain resolved to nothing on the world map Equipment and Abilities screen, whose
state is static (0x141876E90 block, row records at 0x1437F6670); for that screen diff the whole .data
section instead (the recipe that found the Doublehand row skip).

The row refresher 0x1402D6FB8 reaches the screen object through a manager chain read live on
2026-10-19, [[[0x143CD9DA8] + 0x10] + 0x140] + 0x78. The row table sits at +0x20D8 (five
{int32 id, int32 n} entries), the widget rows start at +0x1288, the nav block at +0x1F88 and the
cached both hands byte at +0x238F. Everything from +0 to +0x2800 is dumped so a diff is not
limited to the fields already named. Also dumps the static equip words 0x141876C40..0x141876D00.

  python tools\probes\eqscreen_snapshot.py snap A        # writes <scratch>\eqscreen_A.bin
  python tools\probes\eqscreen_snapshot.py diff A B      # lists every byte that differs
"""
import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
from lib.livebase import open_game, rebase  # noqa: E402
import ctypes as C  # noqa: E402

MANAGER = 0x143CD9DA8
STATIC = 0x141876C40
STATIC_LEN = 0xC0
OBJ_LEN = 0x2800
OUT = os.environ.get("EQSNAP_DIR", os.path.join(os.environ.get("TEMP", "."), "eqscreen"))


def rd(h, a, n):
    b = (C.c_ubyte * n)()
    g = C.c_size_t()
    ok = C.windll.kernel32.ReadProcessMemory(h, C.c_void_p(a), b, n, C.byref(g))
    return bytes(b[:g.value]) if ok else b""


def q(h, a):
    b = rd(h, a, 8)
    return int.from_bytes(b, "little") if len(b) == 8 else 0


def screen_object(h, base):
    m = q(h, rebase(MANAGER, base))
    p1 = q(h, m + 0x10) if m else 0
    p2 = q(h, p1 + 0x140) if p1 else 0
    return q(h, p2 + 0x78) if p2 else 0


def snap(name):
    pid, h, base = open_game()
    obj = screen_object(h, base)
    print(f"base {base:#x} screen object {obj:#x}")
    os.makedirs(OUT, exist_ok=True)
    body = rd(h, obj, OBJ_LEN) if obj else b""
    stat = rd(h, rebase(STATIC, base), STATIC_LEN)
    with open(os.path.join(OUT, f"eqscreen_{name}.bin"), "wb") as f:
        f.write(obj.to_bytes(8, "little") + body + stat)
    print(f"wrote {name}: object {len(body):#x} bytes, static {len(stat):#x} bytes")
    if body:
        print("row table +0x20D8:", [(int.from_bytes(body[0x20D8 + i * 8:0x20D8 + i * 8 + 4], "little", signed=True),
                                      int.from_bytes(body[0x20DC + i * 8:0x20DC + i * 8 + 4], "little")) for i in range(5)])
        print(f"cached both hands +0x238F = {body[0x238F]}")
    print("static C50 u16 =", int.from_bytes(stat[0x10:0x12], "little"), " hands C64/C66 =",
          int.from_bytes(stat[0x24:0x26], "little"), int.from_bytes(stat[0x26:0x28], "little"))


def diff(a, b):
    da = open(os.path.join(OUT, f"eqscreen_{a}.bin"), "rb").read()
    db = open(os.path.join(OUT, f"eqscreen_{b}.bin"), "rb").read()
    oa, ob = int.from_bytes(da[:8], "little"), int.from_bytes(db[:8], "little")
    print(f"object {oa:#x} vs {ob:#x}{'  (DIFFERENT OBJECT)' if oa != ob else ''}")
    da, db = da[8:], db[8:]
    n = min(len(da), len(db))
    runs = []
    i = 0
    while i < n:
        if da[i] != db[i]:
            j = i
            while j < n and da[j] != db[j]:
                j += 1
            runs.append((i, j))
            i = j
        else:
            i += 1
    for s, e in runs:
        where = f"obj+{s:#x}" if s < OBJ_LEN else f"static {STATIC + s - OBJ_LEN:#x}"
        print(f"{where:<20} {da[s:e].hex()}  ->  {db[s:e].hex()}")
    print(f"{len(runs)} differing runs")


if __name__ == "__main__":
    if len(sys.argv) >= 3 and sys.argv[1] == "snap":
        snap(sys.argv[2])
    elif len(sys.argv) >= 4 and sys.argv[1] == "diff":
        diff(sys.argv[2], sys.argv[3])
    else:
        sys.exit(__doc__)
