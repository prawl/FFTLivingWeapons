#!/usr/bin/env python
"""LW-346: dump the two id-range tables the modloader names in its own log (READ-ONLY).

[fftivc.utility.modloader] Found ItemDataTypeToItemIdRangeData table @ 0x14067FB38
[fftivc.utility.modloader] Found ItemIdRangeToCategoryData   table @ 0x1406804E0

On 1.5 the item category resolve appears to be id-range DATA tables, not the pre-1.5
plain getter function (which is why the June-26 8-candidate getter hunt found nothing
of the right shape). If ItemIdRangeToCategoryData rows are (idStart, idEnd, category)
tuples, extending category coverage to id 261 is a table edit, not a code hook.
This dump exists to learn the row schema from the live bytes.
"""
import ctypes as C
import ctypes.wintypes as W
import sys

PROC = "fft_enhanced.exe"
TABLES = [("ItemDataTypeToItemIdRangeData", 0x14067FB38, 0x120),
          ("ItemIdRangeToCategoryData", 0x1406804E0, 0x200)]

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
    pid = find_pid(PROC)
    if not pid:
        sys.exit(f"{PROC} not running")
    h = k32.OpenProcess(PV, False, pid)
    for name, va, n in TABLES:
        b = rd(h, va, n)
        print(f"== {name} @0x{va:X} ({n} bytes)")
        if not b:
            print("   unreadable")
            continue
        for off in range(0, n, 16):
            row = b[off:off + 16]
            hexs = " ".join(f"{x:02X}" for x in row)
            # also show each 16-byte line as u16 pairs, the likeliest field width
            u16s = " ".join(f"{row[i] | (row[i+1] << 8):5d}" for i in range(0, len(row) - 1, 2))
            print(f"   +{off:04X}: {hexs}   u16: {u16s}")
        print()
    k32.CloseHandle(h)


if __name__ == "__main__":
    main()
