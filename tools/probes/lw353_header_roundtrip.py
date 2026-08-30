#!/usr/bin/env python
"""LW-353 READ-ONLY header round-trip diff: which bytes of the save-struct header survive a
save AND its reload (1.5.2).

Plain language: test 2 (2026-08-28 01:xx) proved the save and load hooks now fire, but a saved
file reloads under a DIFFERENT key than it was written with (same play time, different hash), so
the mod never finds a save's own counts and always falls back to the seed. The key hashes the
0xB8 header window +0x100..+0x1B8, and some bytes in it are save-transient (0xFF at the save
edge, 0x00 in the resting / loaded state), so the hash cannot match. This probe records the FULL
header every time it changes, tags each with the live bag[261], and at the end prints, for the
LAST save/load pair it saw, exactly which byte offsets differ. Feed it one save then one load of
the SAME slot; the printed "unstable offsets" are the ones to DROP from the key window.

ANSWER (2026-08-28, from this probe's own captures plus the session's logged keys): exactly three
bytes move, window offsets 0x1A, 0x1C and 0x1D (header +0x11A, +0x11C, +0x11D). The key now
zeroes them, in the mod (Offsets.SaveHeaderVolatileOffs) and here. The printed hex stays RAW,
because diffing raw headers is what this probe is for; only the key line is masked.

Writes nothing. pointer global 0x140D407A0 (Offsets.SaveStructPtr); header +0x100 len 0xB8; play
time u32 at +0x1B4. Usage: python tools/probes/lw353_header_roundtrip.py [seconds=180]
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
BAG = 0x1411A7C00
# The three save-in-flight marker bytes the key ignores (window offsets, so header +0x11A /
# +0x11C / +0x11D): 0xFF while a save is in flight, 0x00 at rest. Lockstep with
# Offsets.SaveHeaderVolatileOffs and LivingWeapon/Extended/SaveEdgeTracker.cs.
VOLATILE_OFFS = (0x1A, 0x1C, 0x1D)


def rd(h, a, n):
    buf = (C.c_ubyte * n)()
    got = C.c_size_t()
    if not k32.ReadProcessMemory(h, C.c_void_p(a), buf, n, C.byref(got)):
        return b""
    return bytes(buf[:got.value])


def key_of(header):
    """The masked key (what the mod stores); the caller still prints the raw header hex."""
    pt = struct.unpack_from("<I", header, PT_OFF - HDR_OFF)[0]
    masked = bytearray(header[:HDR_LEN])
    for off in VOLATILE_OFFS:
        masked[off] = 0
    return pt, "pt%d-%s" % (pt, hashlib.sha1(bytes(masked)).hexdigest()[:12])


def main():
    secs = float(sys.argv[1]) if len(sys.argv) > 1 else 180.0
    h = k32.OpenProcess(0x0410, False, find_pid("FFT_enhanced.exe"))
    print("watching 0x%X for %.0fs; make one SAVE then one LOAD of the same slot..." % (PTR, secs))
    seen = []           # (ts, pt, key, header, bag)
    last = None
    t0 = time.time()
    while time.time() - t0 < secs:
        p = struct.unpack("<Q", rd(h, PTR, 8) or b"\0" * 8)[0]
        if p:
            hdr = rd(h, p + HDR_OFF, HDR_LEN)
            if len(hdr) == HDR_LEN and hdr != last:
                pt, key = key_of(hdr)
                bag = rd(h, BAG + 261, 1)
                bag = bag[0] if bag else -1
                ts = time.strftime("%H:%M:%S")
                print("%s key %s pt%d bag[261]=%d" % (ts, key, pt, bag))
                print("   hdr: %s" % hdr.hex(" "))
                seen.append((ts, pt, key, hdr, bag))
                last = hdr
        time.sleep(0.02)

    print("\n--- round-trip diff ---")
    # group by play time; any play time seen with two different keys is a save/load pair
    by_pt = {}
    for ts, pt, key, hdr, bag in seen:
        by_pt.setdefault(pt, []).append((ts, key, hdr, bag))
    pairs = [(pt, v) for pt, v in by_pt.items() if len({k for _, k, _, _ in v}) > 1]
    if not pairs:
        print("no play time seen under two different keys; save and load the SAME slot once each.")
        return
    for pt, v in pairs:
        keys = [k for _, k, _, _ in v]
        print("play time %d seen under %d keys: %s" % (pt, len(set(keys)), ", ".join(sorted(set(keys)))))
        a, b = v[0][2], v[-1][2]
        diff = [HDR_OFF + i for i in range(HDR_LEN) if a[i] != b[i]]
        print("  unstable header offsets (drop these from the key): %s"
              % (", ".join("0x%X" % d for d in diff) or "none"))
        for i in diff:
            print("    +0x%X: %02x -> %02x" % (i, a[i], b[i]))
    print("\nstable window candidates: everything in +0x100..+0x1B8 NOT listed above,")
    print("play time +0x1B4 is known-stable; the party-name block +0x124.. is the likely key content.")


if __name__ == "__main__":
    main()
