#!/usr/bin/env python
"""READ-ONLY: watch a struct block in fft_enhanced memory and log per-byte changes.
Usage: struct_watch.py <hexaddr> <hexlen> <seconds> [<out>]"""
import ctypes as C
from ctypes import wintypes as W
import sys
import time
from datetime import datetime

k32 = C.WinDLL("kernel32", use_last_error=True)
psapi = C.WinDLL("psapi", use_last_error=True)


class _PMC(C.Structure):
    _fields_ = [("cb", W.DWORD), ("pf", W.DWORD)] + [("a%d" % i, C.c_size_t) for i in range(8)]


def find_pid(name):
    arr = (W.DWORD * 4096)(); need = W.DWORD()
    psapi.EnumProcesses(arr, C.sizeof(arr), C.byref(need))
    best, bw = None, -1
    for i in range(need.value // C.sizeof(W.DWORD)):
        pid = arr[i]
        h = k32.OpenProcess(0x0410, False, pid)
        if not h:
            continue
        buf = C.create_unicode_buffer(260)
        if psapi.GetModuleBaseNameW(h, None, buf, 260) and buf.value.lower() == name.lower() + ".exe":
            p = _PMC(); p.cb = C.sizeof(p)
            psapi.GetProcessMemoryInfo(h, C.byref(p), p.cb)
            if p.a2 > bw:
                best, bw = pid, p.a2
        k32.CloseHandle(h)
    return best


def main():
    addr, length, secs = int(sys.argv[1], 16), int(sys.argv[2], 16), float(sys.argv[3])
    out = open(sys.argv[4], "a", buffering=1) if len(sys.argv) > 4 else sys.stdout
    pid = find_pid("fft_enhanced")
    if not pid:
        out.write("not running\n"); sys.exit(1)
    h = k32.OpenProcess(0x0410, False, pid)
    out.write(f"{datetime.now().strftime('%H:%M:%S.%f')[:-3]} watching {addr:#x} +{length:#x} for {secs}s\n")
    buf = C.create_string_buffer(length); got = C.c_size_t()

    def snap():
        ok = k32.ReadProcessMemory(h, C.c_void_p(addr), buf, length, C.byref(got))
        return buf.raw if ok and got.value == length else None

    prev = snap()
    t0 = time.perf_counter()
    while time.perf_counter() - t0 < secs:
        time.sleep(0.05)
        cur = snap()
        if cur is None or prev is None:
            prev = cur; continue
        if cur != prev:
            t = time.perf_counter() - t0
            # group contiguous changed offsets into runs
            runs = []
            i = 0
            while i < length:
                if cur[i] != prev[i]:
                    j = i
                    while j < length and cur[j] != prev[j]:
                        j += 1
                    runs.append((i, j))
                    i = j
                else:
                    i += 1
            for a, b in runs:
                out.write(f"t=+{t:7.3f}s +{a:03x}..+{b - 1:03x}: "
                          f"{prev[a:b].hex()} -> {cur[a:b].hex()}\n")
            prev = cur
    out.write("watch done\n")


if __name__ == "__main__":
    main()
