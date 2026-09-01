#!/usr/bin/env python
"""LW-365 read-only watch: does the attack-anim publisher's no-weapon flag fire for id 262?

Plain language: the empty-tile fist swing survived the nine widened bounds, and tonight's
disassembly names one more suspect: the attack-animation setup routine keeps its own
"nothing valid in hand" flag right next to the weapon id it publishes for the swing art,
and its bound still says an id at or above 0x105 is not a weapon. This probe watches that
flag byte plus the words around it during ONE empty-tile swing, so the diagnosis is proven
(or broken) before any fix ships.

Stations (all in the render cluster the earlier tapes already watched):
  cluster main/off  0x1407B0764/66   the hand words the publisher copies
  derived id        0x1407B077A      blade id the swing art follows (0 = fists)
  no-weapon flag    0x1407B077C      set by the gate at 0x140309EF6 when NO hand word
                                     passes (empty 0xFF or id >= 0x105)
Declared interpretation, before the swing: on the empty-tile swing with the Ravager (262),
flag goes nonzero and derived stays 0 -> diagnosis PROVEN (the 0x105 imm at 0x140309EF7 is
the cure site). Flag stays 0 while fists still draw -> diagnosis BROKEN, do not build.

Writes nothing. Usage: python tools/probes/lw365_publisher_gate_watch.py [seconds=120]
"""
import struct
import sys
import time

sys.path.insert(0, __file__.rsplit("\\", 1)[0] if "\\" in __file__ else ".")
from lw346_live_disasm import find_pid, rd, k32  # noqa: E402

CLUSTER = 0x1407B0760


def main():
    secs = float(sys.argv[1]) if len(sys.argv) > 1 else 120.0
    h = k32.OpenProcess(0x0010 | 0x0400, False, find_pid("fft_enhanced.exe"))
    print(f"watching cluster {CLUSTER:#x} for {secs:.0f}s", flush=True)
    last = None
    t0 = time.time()
    while time.time() - t0 < secs:
        cl = rd(h, CLUSTER, 0x40)
        if cl is None:
            time.sleep(0.01)
            continue
        main_id, off_id = struct.unpack_from("<HH", cl, 4)
        derived = struct.unpack_from("<H", cl, 0x1A)[0]
        flag = cl[0x1C]
        state = cl
        if state != last:
            t = time.time() - t0
            print(f"{t:8.3f}s main={main_id} off={off_id} derived={derived} "
                  f"noweapon_flag={flag} raw={cl.hex()}", flush=True)
            last = state
        time.sleep(0.0005)
    print("end", flush=True)


if __name__ == "__main__":
    main()
