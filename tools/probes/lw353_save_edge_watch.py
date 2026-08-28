#!/usr/bin/env python
"""LW-353 READ-ONLY watch of the game's transient save struct (1.5.2).

Plain language: while you save or load in the game, this prints the moment the save struct
appears (its pointer global goes non-null), the play time it carries and the per-save key the
mod derives from its header, so the key on the mod's own "save was written / loaded" log lines
can be cross-checked from outside. Writes nothing.

  pointer global 0x141D407A0 (Offsets.SaveStructPtr); header +0x100..+0x1B8; play time u32 at
  +0x1B4 (h*3600 + m*60 + s from the globals 0x141856704 / 0x141856708 / 0x141856700);
  key = pt<playTime>-<first 12 hex of sha1(header)>, the same derivation as
  LivingWeapon/Extended/SaveEdgeTracker.cs (keep the two in lockstep).

Usage: python tools/probes/lw353_save_edge_watch.py [seconds=120]
"""
import ctypes as C
import hashlib
import struct
import sys
import time

sys.path.insert(0, __file__.rsplit("\\", 1)[0] if "\\" in __file__ else ".")
from lw346_live_disasm import find_pid, k32  # noqa: E402

PTR = 0x141D407A0
HDR_OFF, HDR_LEN, PT_OFF = 0x100, 0xB8, 0x1B4
PLAY = (0x141856704, 0x141856708, 0x141856700)   # h, m, s
BAG = 0x1411A7C00


def rd(h, a, n):
    buf = (C.c_ubyte * n)()
    got = C.c_size_t()
    if not k32.ReadProcessMemory(h, C.c_void_p(a), buf, n, C.byref(got)):
        return b""
    return bytes(buf[:got.value])


def key_of(header):
    pt = struct.unpack_from("<I", header, PT_OFF - HDR_OFF)[0]
    return pt, "pt%d-%s" % (pt, hashlib.sha1(header[:HDR_LEN]).hexdigest()[:12])


def main():
    secs = float(sys.argv[1]) if len(sys.argv) > 1 else 120.0
    h = k32.OpenProcess(0x0410, False, find_pid("FFT_enhanced.exe"))
    hh, mm, ss = (struct.unpack("<I", rd(h, a, 4))[0] for a in PLAY)
    print("session play time now %dh%02dm%02ds; watching 0x%X for %.0fs (save or load in the game)..." % (hh, mm, ss, PTR, secs))
    last = 0
    t0 = time.time()
    while time.time() - t0 < secs:
        p = struct.unpack("<Q", rd(h, PTR, 8) or b"\0" * 8)[0]
        if p != last:
            if p:
                hdr = rd(h, p + HDR_OFF, HDR_LEN)
                if len(hdr) == HDR_LEN:
                    pt, key = key_of(hdr)
                    print("%s struct at 0x%X: key %s (play time %ds); bag[261]=%d; header +0x100..+0x120: %s"
                          % (time.strftime("%H:%M:%S"), p, key, pt, rd(h, BAG + 261, 1)[0], hdr[:32].hex(" ")))
                else:
                    print("%s struct at 0x%X: header unreadable" % (time.strftime("%H:%M:%S"), p))
            else:
                print("%s struct pointer cleared" % time.strftime("%H:%M:%S"))
            last = p
        time.sleep(0.02)
    print("done")


if __name__ == "__main__":
    main()
