#!/usr/bin/env python
"""READ-ONLY: raw hexdump of live fft_enhanced memory. Usage: hexdump.py <hexaddr> <hexlen>"""
import ctypes as C
from ctypes import wintypes as W
import sys

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


pid = find_pid("fft_enhanced")
if not pid:
    print("not running"); sys.exit(1)
h = k32.OpenProcess(0x0410, False, pid)
base, length = int(sys.argv[1], 16), int(sys.argv[2], 16)
buf = C.create_string_buffer(length); got = C.c_size_t()
if not k32.ReadProcessMemory(h, C.c_void_p(base), buf, length, C.byref(got)):
    print("RPM failed"); sys.exit(1)
d = buf.raw
for off in range(0, length, 16):
    row = d[off:off + 16]
    hexs = " ".join(f"{b:02x}" for b in row)
    asc = "".join(chr(b) if 32 <= b < 127 else "." for b in row)
    print(f"{base + off:#x}  {hexs:<48}  {asc}")
