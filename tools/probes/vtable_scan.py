#!/usr/bin/env python
"""READ-ONLY: scan fft_enhanced heap for objects by vtable pointer. For each hit, print
distinguishing fields so a unique locate fingerprint can be designed.
Usage: vtable_scan.py"""
import ctypes as C
from ctypes import wintypes as W
import struct

k32 = C.WinDLL("kernel32", use_last_error=True)
psapi = C.WinDLL("psapi", use_last_error=True)

HOLDER_VT = 0x140718278   # text-holder widget class (callout text)
WIDGET_VT = 0x140721DA0   # NineGrid balloon frame widget class


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


pid = find_pid("fft_enhanced")
h = k32.OpenProcess(0x0410, False, pid)
print(f"pid {pid}")

pat_h = struct.pack("<Q", HOLDER_VT)
pat_w = struct.pack("<Q", WIDGET_VT)
holder_hits, widget_hits = [], []

mbi = MBI()
addr = 0x10000
scanned = 0
while addr < 0x7FFFFFFFFFFF:
    if not k32.VirtualQueryEx(h, C.c_void_p(addr), C.byref(mbi), C.sizeof(mbi)):
        break
    base = mbi.BaseAddress or 0
    size = mbi.RegionSize
    # committed, writable, private or mapped -- heap-ish
    if mbi.State == 0x1000 and mbi.Protect in (0x04, 0x08) and size < 0x40000000:
        off = 0
        CH = 0x1000000
        while off < size:
            n = min(CH + 8, size - off)
            buf = C.create_string_buffer(n); got = C.c_size_t()
            if k32.ReadProcessMemory(h, C.c_void_p(base + off), buf, n, C.byref(got)) and got.value == n:
                data = buf.raw
                scanned += n
                p = data.find(pat_h)
                while p != -1:
                    if (base + off + p) % 8 == 0:
                        holder_hits.append(base + off + p)
                    p = data.find(pat_h, p + 1)
                p = data.find(pat_w)
                while p != -1:
                    if (base + off + p) % 8 == 0:
                        widget_hits.append(base + off + p)
                    p = data.find(pat_w, p + 1)
            off += CH
    addr = base + size

print(f"scanned {scanned / 1e9:.2f} GB writable heap")
print(f"\nHOLDER vtable hits: {len(holder_hits)}")
sbuf = C.create_string_buffer(0x70); got = C.c_size_t()
for a in holder_hits[:40]:
    if k32.ReadProcessMemory(h, C.c_void_p(a), sbuf, 0x70, C.byref(got)) and got.value == 0x70:
        d = sbuf.raw
        oid = struct.unpack_from("<Q", d, 0x08)[0]
        slen = struct.unpack_from("<Q", d, 0x30)[0]
        cap = struct.unpack_from("<Q", d, 0x38)[0]
        if slen < 15:
            preview = d[0x20:0x20 + max(slen, 0)].decode("ascii", "replace")
        else:
            sptr = struct.unpack_from("<Q", d, 0x20)[0]
            pb = C.create_string_buffer(32)
            preview = ""
            if k32.ReadProcessMemory(h, C.c_void_p(sptr), pb, 32, C.byref(got)):
                preview = pb.raw.split(b"\0")[0][:30].decode("ascii", "replace")
        print(f"  {a:#x}: id={oid:#x} len={slen} cap={cap} text='{preview}'")
print(f"\nWIDGET vtable hits: {len(widget_hits)}")
for a in widget_hits[:40]:
    if k32.ReadProcessMemory(h, C.c_void_p(a), sbuf, 0x70, C.byref(got)) and got.value == 0x70:
        d = sbuf.raw
        f30 = struct.unpack_from("<I", d, 0x30)[0]
        f38 = struct.unpack_from("<I", d, 0x38)[0]
        f68 = d[0x68], d[0x69], d[0x6A]
        print(f"  {a:#x}: +30={f30:#x} +38={f38:#x} +68 trio={f68}")
