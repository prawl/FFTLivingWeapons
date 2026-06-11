"""Snapshot/diff the roster block to see exactly what an ability purchase changes.

Usage:
  python purchase_diff.py save        # snapshot all 20 roster records (0x258 each)
  python purchase_diff.py diff        # re-read and print every changed byte per slot
"""
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ct_probe import PROC, PV_W, find_pid, k32, rd

ROSTER = 0x1411A18D0
STRIDE = 0x258
SLOTS = 20
SNAP = os.path.join(os.path.dirname(os.path.abspath(__file__)), "purchase_snap.json")

pid = find_pid(PROC)
if not pid:
    sys.exit(f"{PROC} not running")
h = k32.OpenProcess(PV_W, False, pid)
try:
    blocks = {}
    for s in range(SLOTS):
        b = rd(h, ROSTER + s * STRIDE, STRIDE)
        if b:
            blocks[str(s)] = list(b)
    mode = sys.argv[1] if len(sys.argv) > 1 else "save"
    if mode == "save":
        json.dump(blocks, open(SNAP, "w"))
        print(f"snapshot saved ({len(blocks)} slots x {STRIDE:#x} bytes)")
    else:
        old = json.load(open(SNAP))
        any_diff = False
        for s, cur in blocks.items():
            prev = old.get(s)
            if not prev:
                continue
            for off in range(STRIDE):
                if prev[off] != cur[off]:
                    any_diff = True
                    print(f"slot {s} +{off:#05x}: {prev[off]:02X} -> {cur[off]:02X}"
                          f"  ({prev[off]:08b} -> {cur[off]:08b})")
        if not any_diff:
            print("no differences")
finally:
    k32.CloseHandle(h)
