"""Snapshot / diff the live battle menu entry region around 0x14077D010.

The action menu's entry records live here (stride ~0x50: [id u16][..][val]+ pointers into
the 0x14186xxxx unit band). Equipping a support like Reequip adds a NEW command row; diffing
before/after reveals that row's record so we can repoint its command id at Barrage.

Usage:
  python menu_region.py save     # snapshot (menu open, BEFORE equipping Reequip)
  python menu_region.py diff     # rescan + print changed bytes (menu open, AFTER)
"""
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ct_probe import PROC, PV_W, find_pid, k32, rd

BASE = 0x14077CF00
SIZE = 0x600
SNAP = os.path.join(os.path.dirname(os.path.abspath(__file__)), "menu_region_snap.json")

pid = find_pid(PROC)
if not pid:
    sys.exit(f"{PROC} not running")
h = k32.OpenProcess(PV_W, False, pid)
try:
    b = rd(h, BASE, SIZE)
    if not b:
        sys.exit("unreadable")
    mode = sys.argv[1] if len(sys.argv) > 1 else "save"
    if mode == "save":
        json.dump(list(b), open(SNAP, "w"))
        print(f"snapshot saved: {SIZE:#x} bytes from {BASE:#x}")
    else:
        old = json.load(open(SNAP))
        runs = []
        i = 0
        while i < SIZE:
            if i < len(old) and old[i] != b[i]:
                start = i
                while i < SIZE and i < len(old) and old[i] != b[i]:
                    i += 1
                runs.append((start, i))
            else:
                i += 1
        if not runs:
            print("no changes")
        for start, end in runs:
            addr = BASE + start
            o = " ".join(f"{old[j]:02X}" for j in range(start, end))
            n = " ".join(f"{b[j]:02X}" for j in range(start, end))
            print(f"@ {addr:#011x} (+{start:#05x}, {end-start}B):")
            print(f"   old: {o}")
            print(f"   new: {n}")
finally:
    k32.CloseHandle(h)
