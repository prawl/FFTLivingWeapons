#!/usr/bin/env python
"""
SPIKE (read-only): learn how to detect a FULL WAIT (no act AND no move) for the Mushin signature.

Watches the two battle-state bytes FFTHandsFree's BattleTracker uses:
  acted = 0x140782A8C   (== LivingWeapon Offsets.Acted)
  moved = 0x140782A9C
BattleTracker notes "acted=1 moved=16 on a fresh unmoved turn", so these are NOT simple 0/1 flags --
we watch the live transitions to learn the real values for each action.

PROTOCOL (you narrate; I read): on one unit, in three separate turns, do:
  (A) full WAIT  -- no move, no act (just end the turn)
  (B) MOVE only  -- move, then wait (no attack)
  (C) ATTACK     -- act (move or not)
Tell me which you're doing; I print acted/moved (dec + hex) whenever EITHER changes. We diff the three
to find the "did nothing" signature (the value pair that means no-act-no-move).

Usage:  python tools\probes\acted_moved_watch.py [secs=180] [hz=20]
Read-only; largest-working-set pid (2-proc trap). No writes.
"""
import ctypes as C
from ctypes import wintypes as W
import sys, time

PROC = "fft_enhanced"
ACTED = 0x140782A8C
MOVED = 0x140782A9C

k32 = C.WinDLL("kernel32", use_last_error=True)
psapi = C.WinDLL("psapi", use_last_error=True)


class _PMC(C.Structure):
    _fields_ = [("cb", W.DWORD), ("pf", W.DWORD)] + [("a%d" % i, C.c_size_t) for i in range(8)]


def find_pid(name):
    arr = (W.DWORD * 4096)(); need = W.DWORD()
    psapi.EnumProcesses(arr, C.sizeof(arr), C.byref(need))
    best, bw = None, -1
    for i in range(need.value // C.sizeof(W.DWORD)):
        h = k32.OpenProcess(0x0410, False, arr[i])
        if not h:
            continue
        buf = C.create_unicode_buffer(260)
        if psapi.GetModuleBaseNameW(h, None, buf, 260) and buf.value.lower() == name.lower() + ".exe":
            p = _PMC(); p.cb = C.sizeof(p)
            psapi.GetProcessMemoryInfo(h, C.byref(p), p.cb)
            if p.a2 > bw:
                best, bw = arr[i], p.a2
        k32.CloseHandle(h)
    return best


def main():
    secs = int(sys.argv[1]) if len(sys.argv) > 1 else 180
    hz = int(sys.argv[2]) if len(sys.argv) > 2 else 20
    pid = find_pid(PROC)
    if not pid:
        print(f"{PROC}.exe not running"); sys.exit(1)
    h = k32.OpenProcess(0x0410, False, pid)   # QUERY_INFO | VM_READ
    print(f"pid {pid}; watching acted@0x{ACTED:X} + moved@0x{MOVED:X} for {secs}s\n")
    print("Narrate each turn: (A) full wait / (B) move-only / (C) attack. Changes:\n")

    def rd(addr):
        b = C.create_string_buffer(1); got = C.c_size_t()
        ok = k32.ReadProcessMemory(h, C.c_void_p(addr), b, 1, C.byref(got))
        return b.raw[0] if ok and got.value == 1 else None

    last = None
    t0 = time.time()
    while time.time() - t0 < secs:
        a, m = rd(ACTED), rd(MOVED)
        cur = (a, m)
        if cur != last:
            t = time.time() - t0
            print(f"  [{t:6.2f}s] acted={a} (0x{a:02X})   moved={m} (0x{m:02X})" if a is not None and m is not None
                  else f"  [{t:6.2f}s] acted={a} moved={m}")
            last = cur
        time.sleep(1.0 / hz)
    print("\ndone.")


if __name__ == "__main__":
    main()
