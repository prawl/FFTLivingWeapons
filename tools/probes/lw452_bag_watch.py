#!/usr/bin/env python
"""READ ONLY. Watch the seven extended weapon counts in the running game and print every change.

Plain language: the mod keeps the counts of its seven new weapons (ids 261 to 267) on a page it
owns; the game's own 261 counts sit in the old block. This prints a timestamped line whenever
any of those bytes moves, on either block, so a formation screen equip, a Retry or a flee can be
matched against the mod's log lines to the millisecond. Writes nothing.

    python tools/probes/lw452_bag_watch.py <relocated page address from the armed line> [seconds]

The page address is the hex in "item count lists relocated to 0x..." in livingweapon.log; the
old block is Offsets.BagCountArray 0x1411A7C00 rebased through lib.livebase.
"""
import ctypes
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from lib.livebase import open_game, rebase  # noqa: E402

OLD_BLOCK = 0x1411A7C00
IDS = range(261, 268)
WINDOW = 272

_k32 = ctypes.windll.kernel32
_k32.ReadProcessMemory.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_size_t,
                                   ctypes.POINTER(ctypes.c_size_t)]


def rd(h, addr, n):
    buf = ctypes.create_string_buffer(n)
    got = ctypes.c_size_t()
    ok = _k32.ReadProcessMemory(h, ctypes.c_void_p(addr), buf, n, ctypes.byref(got))
    return buf.raw[:got.value] if ok else b""


def main():
    if len(sys.argv) < 2:
        sys.exit(__doc__)
    page = int(sys.argv[1], 16)
    seconds = float(sys.argv[2]) if len(sys.argv) > 2 else 120
    pid, h, base = open_game()
    old = rebase(OLD_BLOCK, base)
    print("pid %d, page 0x%X, old block 0x%X, watching %.0f s" % (pid, page, old, seconds))
    last = {}
    end = time.time() + seconds
    while time.time() < end:
        for label, addr in (("page", page), ("old", old)):
            b = rd(h, addr, WINDOW)
            if len(b) < WINDOW:
                continue
            ext = tuple(b[i] for i in IDS)
            van = (b[1], b[33], b[37])
            cur = (ext, van)
            if last.get(label) != cur:
                stamp = time.strftime("%H:%M:%S") + ".%03d" % int((time.time() % 1) * 1000)
                print("%s %-4s ext 261..267=%s  vanilla 1/33/37=%s" % (stamp, label, list(ext), list(van)), flush=True)
                last[label] = cur
        time.sleep(0.02)


if __name__ == "__main__":
    main()
