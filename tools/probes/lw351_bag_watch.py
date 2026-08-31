#!/usr/bin/env python
"""LW-351 stage-2 live pass: watch the extended-inventory bag counts and say WHEN they change.

WHY. On the 2026-08-30 stage-2 pass every extended bag count (ids 261-268) read 0 after a real
battle while the vanilla counts survived, and the owner's purchases "disappeared". Nothing in
the mod logs a bag write it did not make, so this polls the eight bytes of the game's own bag
array at 0x1411A7C00 + id and prints a timestamped line on every change, next to the mod's own
log the owner can line up by the clock (battle start, battle end, formation, save edges).

READ-ONLY. Requires the game running. Ctrl+C to stop.
  python tools/probes/lw351_bag_watch.py            # ids 261..268
  python tools/probes/lw351_bag_watch.py 261 262    # a subset
"""
import ctypes as C
import ctypes.wintypes as W
import sys
import time

PROC = "fft_enhanced.exe"
BAG = 0x1411A7C00          # the per-id owned-count byte array (LW-346 CountArrayBase)
FIRST, LAST = 261, 268

k32 = C.WinDLL("kernel32", use_last_error=True)
PV = 0x0410


class PE32(C.Structure):
    _fields_ = [("dwSize", W.DWORD), ("cntUsage", W.DWORD), ("th32ProcessID", W.DWORD),
                ("th32DefaultHeapID", C.POINTER(C.c_ulong)), ("th32ModuleID", W.DWORD),
                ("cntThreads", W.DWORD), ("th32ParentProcessID", W.DWORD),
                ("pcPriClassBase", C.c_long), ("dwFlags", W.DWORD),
                ("szExeFile", C.c_char * 260)]


def find_pid(name):
    snap = k32.CreateToolhelp32Snapshot(2, 0)
    e = PE32(); e.dwSize = C.sizeof(PE32)
    pid = None
    if k32.Process32First(snap, C.byref(e)):
        while True:
            if name.lower() in e.szExeFile.decode(errors="ignore").lower():
                pid = e.th32ProcessID; break
            if not k32.Process32Next(snap, C.byref(e)): break
    k32.CloseHandle(snap)
    return pid


def rd(h, a, n):
    buf = (C.c_ubyte * n)(); g = C.c_size_t(0)
    if k32.ReadProcessMemory(h, C.c_void_p(a), buf, n, C.byref(g)) and g.value == n:
        return bytes(buf)
    return None


def main():
    ids = [int(x) for x in sys.argv[1:]] or list(range(FIRST, LAST + 1))
    pid = find_pid(PROC)
    if not pid:
        sys.exit(f"{PROC} not running")
    h = k32.OpenProcess(PV, False, pid)
    lo, hi = min(ids), max(ids)
    last = None
    print(f"watching bag[{lo}..{hi}] at 0x{BAG + lo:X} (read-only, 20 ms); Ctrl+C stops")
    try:
        while True:
            b = rd(h, BAG + lo, hi - lo + 1)
            if b is None:
                cur = None
            else:
                cur = tuple(b[i - lo] for i in ids)
            if cur != last:
                stamp = time.strftime("%H:%M:%S") + f".{int(time.time() * 1000) % 1000:03d}"
                if cur is None:
                    print(f"[{stamp}] bag page unreadable")
                else:
                    print(f"[{stamp}] " + "  ".join(f"{i}={v}" for i, v in zip(ids, cur)))
                last = cur
            time.sleep(0.02)
    except KeyboardInterrupt:
        pass
    finally:
        k32.CloseHandle(h)


if __name__ == "__main__":
    main()
