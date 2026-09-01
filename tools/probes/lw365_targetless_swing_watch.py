#!/usr/bin/env python
"""LW-365 read-only watch: where does the weapon id die on a TARGETLESS swing?

Plain language: attacking an empty tile with an extended weapon swings fists even though every
known id bound is widened. The render-cluster tape shows the cluster's main-hand word going
262 -> 0 for the targetless swing while an enemy swing publishes 262 and draws the blade. This
probe watches the three stations the id passes through, so ONE empty-tile swing shows which
hop zeroes it:
  A. the acting unit's combat hand words (combat +0x20 / +0x24)  - the source
  B. the action template block around 0x14186AFA0 (hands at ..FA2/..FA4, armed byte ..FA1,
     id word mirrored at template +8)                            - the middle
  C. the render cluster 0x1407B0760.. (main/off/derived)         - the destination
Interpretation, declared up front: A=262,B=0 means the template filler caps it; A=262,B=262,
C=0 means the cluster publisher for the targetless lane caps it; A=0 means the combat struct
itself was cleared first (a different lane entirely).

Writes nothing. Usage: python tools/probes/lw365_targetless_swing_watch.py [seconds=90] [combat addr]
"""
import struct
import sys
import time

sys.path.insert(0, __file__.rsplit("\\", 1)[0] if "\\" in __file__ else ".")
from lw346_live_disasm import find_pid, rd, k32  # noqa: E402

CLUSTER = 0x1407B0760
TEMPLATE = 0x14186AF98          # covers ..FA1 armed byte, ..FA2/..FA4 hands, base+8 mirror
TEMPLATE_LEN = 0x30
OBJ_TABLE = 0x141800F50


def main():
    secs = float(sys.argv[1]) if len(sys.argv) > 1 else 90.0
    h = k32.OpenProcess(0x0010 | 0x0400, False, find_pid("fft_enhanced.exe"))
    combat = (int(sys.argv[2], 0) if len(sys.argv) > 2
              else struct.unpack("<Q", rd(h, OBJ_TABLE, 8))[0])
    print(f"combat struct {combat:#x}; watching {secs:.0f}s", flush=True)
    last = None
    t0 = time.time()
    while time.time() - t0 < secs:
        cl = rd(h, CLUSTER, 0x20)
        tp = rd(h, TEMPLATE, TEMPLATE_LEN)
        cb = rd(h, combat + 0x20, 8)
        if cl is None or tp is None or cb is None:
            time.sleep(0.01)
            continue
        main_id, off_id = struct.unpack_from("<HH", cl, 4)
        derived = struct.unpack_from("<H", cl, 0x1A)[0]
        cb_r, cb_l = struct.unpack_from("<HH", cb, 0)
        state = (main_id, off_id, derived, cb_r, cb_l, tp)
        if state != last:
            t = time.time() - t0
            tp_hex = tp.hex()
            print(f"{t:8.3f}s combat(+20/+24)={cb_r}/{cb_l} cluster main={main_id} "
                  f"off={off_id} derived={derived}", flush=True)
            print(f"          template {TEMPLATE:#x}: {tp_hex}", flush=True)
            last = state
        time.sleep(0.0005)
    print("end", flush=True)


if __name__ == "__main__":
    main()
