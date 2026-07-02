#!/usr/bin/env python
"""READ-ONLY: scan fft_enhanced.exe executable memory for direct E8 rel32 calls (and E9 jmps)
targeting given absolute addresses. Usage: caller_scan.py <hextarget> [<hextarget> ...]"""
import ctypes as C
from ctypes import wintypes as W
import struct
import sys

k32 = C.WinDLL("kernel32", use_last_error=True)
psapi = C.WinDLL("psapi", use_last_error=True)


class _PMC(C.Structure):
    _fields_ = [("cb", W.DWORD), ("pf", W.DWORD)] + [("a%d" % i, C.c_size_t) for i in range(8)]


class MBI(C.Structure):
    _fields_ = [("BaseAddress", C.c_void_p), ("AllocationBase", C.c_void_p),
                ("AllocationProtect", W.DWORD), ("PartitionId", W.WORD),
                ("RegionSize", C.c_size_t), ("State", W.DWORD),
                ("Protect", W.DWORD), ("Type", W.DWORD)]


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


EXEC_PROT = {0x10, 0x20, 0x40, 0x80}  # EXECUTE / EXECUTE_READ / EXECUTE_READWRITE / EXECUTE_WRITECOPY

pid = find_pid("fft_enhanced")
if not pid:
    print("not running"); sys.exit(1)
h = k32.OpenProcess(0x0410, False, pid)
print(f"pid {pid}")
targets = [int(a, 16) for a in sys.argv[1:]]

# Walk executable regions inside the main-module range (image base 0x140000000, no ASLR).
LO, HI = 0x140000000, 0x150000000
addr = LO
regions = []
mbi = MBI()
while addr < HI:
    if not k32.VirtualQueryEx(h, C.c_void_p(addr), C.byref(mbi), C.sizeof(mbi)):
        break
    base = mbi.BaseAddress or 0
    size = mbi.RegionSize
    if mbi.State == 0x1000 and (mbi.Protect & 0xF0) and not (mbi.Protect & 0x100):
        regions.append((base, size))
    addr = base + size

print(f"{len(regions)} executable regions, total {sum(s for _, s in regions) / 1e6:.1f} MB")

hits = {t: [] for t in targets}
CH = 0x400000
for base, size in regions:
    off = 0
    while off < size:
        n = min(CH, size - off) + 5   # overlap so calls straddling chunk edges are caught
        n = min(n, size - off)
        buf = C.create_string_buffer(n); got = C.c_size_t()
        if not k32.ReadProcessMemory(h, C.c_void_p(base + off), buf, n, C.byref(got)) or got.value != n:
            off += CH; continue
        data = buf.raw
        pos = data.find(b"\xe8")
        while pos != -1:
            if pos + 5 <= len(data):
                rel = struct.unpack_from("<i", data, pos + 1)[0]
                tgt = base + off + pos + 5 + rel
                if tgt in hits:
                    hits[tgt].append(("call", base + off + pos))
            pos = data.find(b"\xe8", pos + 1)
        pos = data.find(b"\xe9")
        while pos != -1:
            if pos + 5 <= len(data):
                rel = struct.unpack_from("<i", data, pos + 1)[0]
                tgt = base + off + pos + 5 + rel
                if tgt in hits:
                    hits[tgt].append(("jmp", base + off + pos))
            pos = data.find(b"\xe9", pos + 1)
        off += CH - 5 if CH < size - off + 5 else CH

for t in targets:
    print(f"\ntarget {t:#x}: {len(hits[t])} direct sites")
    for kind, a in sorted(set(hits[t])):
        print(f"  {kind} at {a:#x}")
