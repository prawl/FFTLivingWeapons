#!/usr/bin/env python
r"""Turtle Ramza. Stamps one shield id into every equipment slot of roster slot 0 (owner curiosity 2026-10-19).

Roster row layout (Offsets.cs, 1.5 base 0x1411A7D10, stride 0x258): +0x12 accessory, +0x14 right hand,
+0x16 left hand, +0x18 dual wield off hand, +0x1A shield. Head and body are not pinned in Offsets.cs; this
probe reads +0x0E and +0x10 as the guess (the u16 pair right before the accessory) and prints them so the
owner can confirm against Ramza's real helm and body ids before the write lands.

  python tools\probes\shield_everywhere_probe.py                # dump slot 0 +0x08..+0x1C, write nothing
  python tools\probes\shield_everywhere_probe.py --write 128    # Tideward Shield (id 128) into every slot
  python tools\probes\shield_everywhere_probe.py --undo         # put back the bytes saved by the last --write

World map only; the roster row is the world map truth and battle units are a different struct.
"""
import argparse
import json
import os
import sys
import tempfile

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
from ct_probe import PROC, PV_W, find_pid, k32, rd, wr  # noqa: E402
from lib.livebase import module_base, rebase  # noqa: E402

ROSTER_BASE = 0x1411A7D10
SLOTS = {"head?": 0x0E, "body?": 0x10, "accessory": 0x12, "rhand": 0x14, "lhand": 0x16, "offhand": 0x18, "shield": 0x1A}
UNDO = os.path.join(tempfile.gettempdir(), "shield_everywhere_undo.json")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--write", type=int, help="shield item id to stamp into every slot")
    ap.add_argument("--undo", action="store_true")
    ap.add_argument("--slots", default="head?,body?,accessory,rhand,lhand,offhand,shield")
    a = ap.parse_args()

    pid = find_pid(PROC)
    if not pid:
        sys.exit(f"{PROC} not running")
    h = k32.OpenProcess(PV_W, False, pid)
    try:
        base = module_base(h, "fft_enhanced.exe")
        if base is None:
            sys.exit("could not read the module base")
        row = rebase(ROSTER_BASE, base)
        before = rd(h, row + 0x08, 0x18)
        if before is None:
            sys.exit("roster read failed")
        print(f"live base {base:#x}, slot 0 row {row:#x}")
        print("  +0x08.. " + " ".join(f"{b:02x}" for b in before))
        for name, off in SLOTS.items():
            v = before[off - 0x08] | (before[off - 0x07] << 8)
            print(f"  {name:10s} +{off:#04x} = {v}")

        if a.undo:
            saved = json.load(open(UNDO))
            for off_s, v in saved.items():
                off = int(off_s)
                wr(h, row + off, bytes([v & 0xFF, (v >> 8) & 0xFF]))
            print(f"restored {len(saved)} slots from {UNDO}")
            return
        if a.write is None:
            print("dry run, nothing written")
            return

        wanted = [s.strip() for s in a.slots.split(",")]
        saved = {}
        for name in wanted:
            off = SLOTS[name]
            saved[str(off)] = before[off - 0x08] | (before[off - 0x07] << 8)
        json.dump(saved, open(UNDO, "w"))
        for name in wanted:
            off = SLOTS[name]
            wr(h, row + off, bytes([a.write & 0xFF, (a.write >> 8) & 0xFF]))
        after = rd(h, row + 0x08, 0x18)
        print("  after   " + " ".join(f"{b:02x}" for b in after))
        print(f"wrote id {a.write} into {wanted}; undo file {UNDO}")
    finally:
        k32.CloseHandle(h)


if __name__ == "__main__":
    main()
