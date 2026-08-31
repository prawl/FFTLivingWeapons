#!/usr/bin/env python
"""LW-351: watch the two weapon order templates and say WHICH WORDS change and WHEN.

WHY (2026-08-31 00:10). After the stage-2 pass the inventory order template at 0x1407B2550
had lost its 0x00FF end marker (129 ids, then zeros: FnOrderRebuild 0x140285DF0 walks a
template until the first 0x00FF or a word >= its widened bound, so the zeros became id-0 rows
in the party inventory and the game crashed on one), and the equip-picker template at
0x141874540 carried seven junk shield ids (137..143) after the Bloodlash. The save struct
round-trips all 141 words of each (load-apply copies struct+0x8A6C..+0x8B86 into 0x1407B2550,
read from disk this session), so the damage happened IN-SESSION and was then saved. Nothing
in the mod logs a template write it did not make, so this polls both regions every 50 ms and
prints every changed word (index, old -> new) with a timestamp the owner can line up with the
action he just took and with livingweapon.log.

READ-ONLY. Requires the game running. Ctrl+C to stop.
  python tools/probes/lw351_template_watch.py
"""
import ctypes as C
import ctypes.wintypes as W
import struct
import sys
import time

PROC = "fft_enhanced.exe"
REGIONS = (("inventory template", 0x1407B2550, 148), ("picker template", 0x141874540, 148))
MARKER = 0x00FF

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


def words(h, a, n):
    b = rd(h, a, n * 2)
    return list(struct.unpack("<%dH" % n, b)) if b else None


def stamp():
    return time.strftime("%H:%M:%S") + f".{int(time.time() * 1000) % 1000:03d}"


def describe(w):
    marks = [i for i, x in enumerate(w) if x == MARKER]
    return f"marker at {marks[0] if marks else 'NONE'}"


def main():
    pid = find_pid(PROC)
    if not pid:
        sys.exit(f"{PROC} not running")
    h = k32.OpenProcess(PV, False, pid)
    last = {}
    for label, addr, n in REGIONS:
        w = words(h, addr, n)
        last[label] = w
        print(f"[{stamp()}] {label} 0x{addr:X}: {describe(w) if w else 'unreadable'}")
    print("watching (50 ms); Ctrl+C stops")
    try:
        while True:
            for label, addr, n in REGIONS:
                w = words(h, addr, n)
                old = last[label]
                if w is None or old is None or w == old:
                    if w is None and old is not None:
                        print(f"[{stamp()}] {label}: page unreadable")
                    last[label] = w
                    continue
                diffs = [(i, old[i], w[i]) for i in range(n) if old[i] != w[i]]
                shown = ", ".join(f"[{i}] {a:04X}->{b:04X}" for i, a, b in diffs[:24])
                more = f" (+{len(diffs) - 24} more)" if len(diffs) > 24 else ""
                print(f"[{stamp()}] {label}: {len(diffs)} word(s) changed: {shown}{more}; now {describe(w)}")
                last[label] = w
            time.sleep(0.05)
    except KeyboardInterrupt:
        pass
    finally:
        k32.CloseHandle(h)


if __name__ == "__main__":
    main()
