#!/usr/bin/env python
"""LW-353 READ-ONLY watch of the game's save struct (1.5.2).

Plain language: while you save or load in the game, this prints the per-save key the mod derives
from the save struct's header, so the key on the mod's own "save was written / loaded" log lines
can be cross-checked from outside. Writes nothing.

  pointer global 0x140D407A0 (Offsets.SaveStructPtr), corrected 2026-08-27 late night: the first
  build read 0x141D407A0, one digit off. The struct is NOT transient. The pointer normally holds
  the static image buffer 0x142C81C80 (Offsets.SaveStructStatic), so it usually does not move at
  all across a save or a load; what changes is the HEADER inside the struct. This watch therefore
  reports BOTH: a pointer change and any change in the 0xB8 header window.

  header +0x100..+0x1B8; play time u32 at +0x1B4 (h*3600 + m*60 + s from the globals 0x141856704 /
  0x141856708 / 0x141856700); key = pt<playTime>-<first 12 hex of sha1(header with the three
  save-in-flight marker bytes zeroed)>, the same derivation as
  LivingWeapon/Extended/SaveEdgeTracker.cs (keep the two in lockstep). The play time
  in the header is the one from the LAST save or load, not the running session clock.

  Same-read rule: the loop reads the pointer and the header ONCE per pass and compares that read
  against the last one it printed; when they differ it hands those very bytes to the printer, which
  reads nothing itself. The line you see is therefore the read that tripped the change, never a
  later re-read of a value that may already have moved on.

Usage: python tools/probes/lw353_save_edge_watch.py [seconds=120]
"""
import ctypes as C
import hashlib
import struct
import sys
import time

sys.path.insert(0, __file__.rsplit("\\", 1)[0] if "\\" in __file__ else ".")
from lw346_live_disasm import find_pid, k32  # noqa: E402

PTR = 0x140D407A0
HDR_OFF, HDR_LEN, PT_OFF = 0x100, 0xB8, 0x1B4
PLAY = (0x141856704, 0x141856708, 0x141856700)   # h, m, s
# Window offsets 0x1A / 0x1C / 0x1D (header +0x11A / +0x11C / +0x11D) read 0xFF while a save is
# in flight and 0x00 at rest, so the same save hashed two ways; the key zeroes them, exactly as
# Offsets.SaveHeaderVolatileOffs does in the mod. Keep the two lists in lockstep.
VOLATILE_OFFS = (0x1A, 0x1C, 0x1D)
BAG = 0x1411A7C00


def rd(h, a, n):
    buf = (C.c_ubyte * n)()
    got = C.c_size_t()
    if not k32.ReadProcessMemory(h, C.c_void_p(a), buf, n, C.byref(got)):
        return b""
    return bytes(buf[:got.value])


def key_of(header):
    pt = struct.unpack_from("<I", header, PT_OFF - HDR_OFF)[0]
    masked = bytearray(header[:HDR_LEN])
    for off in VOLATILE_OFFS:
        masked[off] = 0
    return pt, "pt%d-%s" % (pt, hashlib.sha1(bytes(masked)).hexdigest()[:12])


def read_state(h):
    """The ONE read per pass: the pointer, plus the 0xB8 header when the pointer resolves.

    A failed or short header read comes back as b"" so a partial read cannot flap the comparison.
    """
    p = struct.unpack("<Q", rd(h, PTR, 8) or b"\0" * 8)[0]
    hdr = rd(h, p + HDR_OFF, HDR_LEN) if p else b""
    return p, (hdr if len(hdr) == HDR_LEN else b"")


def bag_count(h):
    """Sampled at print time only; it is not part of the compared read."""
    b = rd(h, BAG + 261, 1)
    return b[0] if b else -1


def show(prefix, p, hdr, bag):
    """One line for the state the caller already holds. Reads nothing (the same-read rule)."""
    stamp = time.strftime("%H:%M:%S")
    if not p:
        print("%s %s: struct pointer null" % (stamp, prefix))
        return
    if len(hdr) != HDR_LEN:
        print("%s %s: struct at 0x%X: header unreadable" % (stamp, prefix, p))
        return
    pt, key = key_of(hdr)
    print("%s %s: struct at 0x%X: key %s (play time %ds); bag[261]=%d; header +0x100..+0x120: %s"
          % (stamp, prefix, p, key, pt, bag, hdr[:32].hex(" ")))


def main():
    secs = float(sys.argv[1]) if len(sys.argv) > 1 else 120.0
    h = k32.OpenProcess(0x0410, False, find_pid("FFT_enhanced.exe"))
    hh, mm, ss = (struct.unpack("<I", rd(h, a, 4))[0] for a in PLAY)
    print("session play time now %dh%02dm%02ds; watching 0x%X for %.0fs (save or load in the game)..."
          % (hh, mm, ss, PTR, secs))
    last = read_state(h)
    show("initial state", last[0], last[1], bag_count(h))
    t0 = time.time()
    while time.time() - t0 < secs:
        cur = read_state(h)
        if cur != last:
            show("pointer changed" if cur[0] != last[0] else "header changed",
                 cur[0], cur[1], bag_count(h))
            last = cur
        time.sleep(0.02)
    print("done")


if __name__ == "__main__":
    main()
