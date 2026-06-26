#!/usr/bin/env python
"""
Battle command-menu reader (READ-ONLY). Watch the engine add/remove UI selections (e.g. a chocobo
mount adds a "Dismount" row) by reading the menu's widget arena.

The command menu is a launch-stable HEAP arena of 0x170-byte widget records (docs/UI_GREY_HOLD.md):
    +0x1C  u8   grey/disabled overlay flag
    +0x20  u64  -> a UTF-8 widget IDENTITY name ("RadioButtonBattleMenu#7", "CommandBg#8", "GrayOut#3")
The identity names self-label the arena (so we can validate/find it); the DISPLAYED label text
("Dismount") lives in an unmapped buffer and is NOT readable here. So we observe the menu's ROWS and
their active/grey state -- a row appearing or flipping active is the engine adding a UI selection --
but not the literal label text. MenuSubState (0x140C6B1CC) reads 4 while the command menu is up.

USAGE (game in a battle, a unit's command menu open):
  python ui_menu_probe.py            # dump the arena rows (name + grey) + the active RadioButton count
  python ui_menu_probe.py --base 0xADDR   # if the heap arena moved, point at a re-found base
"""
import ctypes as C
from ctypes import wintypes as W
import sys

PROC = "FFT_enhanced"
ARENA = 0x436BFDE4F0     # docs/UI_GREY_HOLD.md; launch-stable, but --base overrides if it moved
STRIDE = 0x170
RECORDS = 16
GREY = 0x1C
NAMEPTR = 0x20
MENU_SUBSTATE = 0x140C6B1CC
ANCHORS = ("RadioButtonBattleMenu", "CommandBg", "GrayOut", "TextTitle", "WindowNormal")

k32 = C.WinDLL("kernel32", use_last_error=True)
psapi = C.WinDLL("psapi", use_last_error=True)
PV = 0x0010 | 0x0400


class _PMC(C.Structure):
    _fields_ = [("cb", W.DWORD), ("PageFaultCount", W.DWORD),
                ("PeakWorkingSetSize", C.c_size_t), ("WorkingSetSize", C.c_size_t),
                ("Q1", C.c_size_t), ("Q2", C.c_size_t), ("Q3", C.c_size_t),
                ("Q4", C.c_size_t), ("Pf", C.c_size_t), ("PeakPf", C.c_size_t)]


def _ws(pid):
    h = k32.OpenProcess(PV, False, pid)
    if not h:
        return 0
    p = _PMC(); p.cb = C.sizeof(p)
    ok = psapi.GetProcessMemoryInfo(h, C.byref(p), p.cb)
    k32.CloseHandle(h)
    return p.WorkingSetSize if ok else 0


def find_pid(name):
    class PE32(C.Structure):
        _fields_ = [("dwSize", W.DWORD), ("cntUsage", W.DWORD), ("th32ProcessID", W.DWORD),
                    ("th32DefaultHeapID", C.POINTER(C.c_ulong)), ("th32ModuleID", W.DWORD),
                    ("cntThreads", W.DWORD), ("th32ParentProcessID", W.DWORD),
                    ("pcPriClassBase", C.c_long), ("dwFlags", W.DWORD), ("szExeFile", C.c_char * 260)]
    snap = k32.CreateToolhelp32Snapshot(0x2, 0)
    e = PE32(); e.dwSize = C.sizeof(e); m = []
    if k32.Process32First(snap, C.byref(e)):
        while True:
            if name.lower() in e.szExeFile.decode(errors="ignore").lower():
                m.append(e.th32ProcessID)
            if not k32.Process32Next(snap, C.byref(e)):
                break
    k32.CloseHandle(snap)
    return max(m, key=_ws) if m else None


def rd(h, a, n):
    b = (C.c_ubyte * n)(); g = C.c_size_t(0)
    if k32.ReadProcessMemory(h, C.c_void_p(a), b, n, C.byref(g)) and g.value == n:
        return bytes(b)
    return None


def u8(h, a):
    b = rd(h, a, 1); return b[0] if b else None


def u64(h, a):
    b = rd(h, a, 8); return int.from_bytes(b, "little") if b else None


def cstr(h, a, n=48):
    b = rd(h, a, n)
    if not b:
        return None
    z = b.find(0)
    s = b[:z if z >= 0 else n].decode("latin1", "replace")
    return s if s.isprintable() and len(s) >= 2 else None


def main():
    argv = sys.argv[1:]
    base = int(argv[argv.index("--base") + 1], 16) if "--base" in argv else ARENA
    pid = find_pid(PROC)
    if not pid:
        print(f"{PROC} not running."); return
    h = k32.OpenProcess(PV, False, pid)
    if not h:
        print(f"OpenProcess failed (err {C.get_last_error()})."); return
    print(f"pid {pid}   MenuSubState(0x140C6B1CC)={u8(h, MENU_SUBSTATE)}  (4 = command menu up)")

    rows = []
    valid = 0
    for i in range(RECORDS):
        rec = base + i * STRIDE
        grey = u8(h, rec + GREY)
        name = cstr(h, u64(h, rec + NAMEPTR) or 0)
        rows.append((i, rec, grey, name))
        if name and any(name.startswith(a) for a in ANCHORS):
            valid += 1
    if valid < 3:
        print(f"arena at 0x{base:X} did NOT validate ({valid} known widgets) -- it may have moved; "
              f"re-find by AoB on 'RadioButtonBattleMenu' and pass --base.")
        return

    print(f"arena 0x{base:X} ({valid} known widgets):")
    for i, rec, grey, name in rows:
        if name:
            mark = "  <-- ACTIVE row" if (name.startswith("RadioButtonBattleMenu") and grey == 0) else ""
            print(f"  rec {i:>2} @0x{rec:X}  grey={grey}  {name}{mark}")
    active = [n for _, _, g, n in rows if n and n.startswith("RadioButtonBattleMenu") and g == 0]
    greyed = [n for _, _, g, n in rows if n and n.startswith("RadioButtonBattleMenu") and g == 1]
    print(f"\nRadioButtonBattleMenu rows: {len(active)} ACTIVE (grey=0), {len(greyed)} greyed (grey=1).")
    print("Re-run after a menu change (mount -> Dismount) and compare the ACTIVE count/set.")


if __name__ == "__main__":
    main()
