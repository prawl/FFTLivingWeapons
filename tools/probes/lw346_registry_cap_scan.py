#!/usr/bin/env python
"""LW-346 registry-cap hunt: find every 261-shaped immediate in the live .code section.

WHY. With the rig boot-armed (all six known registry patches applied before the game
booted) and count[261]=1 surviving the save load, id 261 still does not appear in the
party inventory or the equip picker. So the boot-built registry has at least one more
bound this rig does not patch -- the journal's "possible 5th display cap in 0x140392xxx"
note. This scans the running exe's .code section for every instruction-shaped 0x105
(and 0x104/0x106 for off-by-one encodings) imm32, prints each with context, and marks
the sites the rig already owns, leaving a shortlist of candidate un-patched caps.

READ-ONLY. Requires the game running. Companion of lw346_capbreak_live_confirm.py.
"""
import ctypes as C
import ctypes.wintypes as W
import struct
import sys

PROC = "fft_enhanced.exe"
BASE = 0x140000000

k32 = C.WinDLL("kernel32", use_last_error=True)
PV = 0x0410

KNOWN = {
    0x140284C0A: "equip clamp (+6 disp8, not an imm32 site)",
    0x140284724: "display cap 1",
    0x1402847C9: "display cap 2",
    0x140288CDA: "display cap 3",
    0x140289074: "display cap 4",
    0x140284800: "count-getter cap (0x103 imm, listed for reference)",
}


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


def hx(b):
    return " ".join("%02X" % x for x in b) if b else "?"


def code_section(h):
    hdr = rd(h, BASE, 0x600)
    e_lfanew = struct.unpack_from("<I", hdr, 0x3C)[0]
    nsec = struct.unpack_from("<H", hdr, e_lfanew + 6)[0]
    opt_size = struct.unpack_from("<H", hdr, e_lfanew + 20)[0]
    for i in range(nsec):
        s = e_lfanew + 24 + opt_size + i * 40
        name = hdr[s:s + 8].rstrip(b"\0").decode("ascii", "replace")
        vsize, va = struct.unpack_from("<II", hdr, s + 8)
        if name == ".code":
            return BASE + va, vsize
    sys.exit("no .code section")


def classify(ctx8):
    """ctx8 = the 8 bytes ENDING just before the imm value. Return an instruction
    guess for the byte(s) immediately preceding the imm, or None for 'not an imm'."""
    p1, p2 = ctx8[-1], ctx8[-2]
    if p1 == 0x3D:
        return "cmp eax,imm32"
    if p2 == 0x81 and 0xF8 <= p1 <= 0xFF:
        return f"cmp r32,imm32 (81 {p1:02X})"
    if p2 == 0x81 and 0xC0 <= p1 <= 0xC7:
        return f"add r32,imm32 (81 {p1:02X})"
    if 0xB8 <= p1 <= 0xBF:
        return f"mov r32,imm32 (B{p1 - 0xB8:X})"
    if p2 == 0xC7 and 0xC0 <= p1 <= 0xC7:
        return "mov r/m32,imm32"
    if p1 == 0x68:
        return "push imm32"
    if p2 == 0x41 and 0xB8 <= p1 <= 0xBF:
        return "mov r8d..,imm32 (REX)"
    return None


def main():
    pid = find_pid(PROC)
    if not pid:
        sys.exit(f"{PROC} not running")
    h = k32.OpenProcess(PV, False, pid)
    sec_va, sec_len = code_section(h)
    print(f".code at 0x{sec_va:X} len 0x{sec_len:X}")

    for imm, label in [(0x105, "0x105 = 261"), (0x104, "0x104 = 260"), (0x106, "0x106 = 262")]:
        needle = struct.pack("<I", imm)
        hits = []
        CHUNK = 1 << 20
        off = 0
        while off < sec_len:
            want = min(CHUNK + 3, sec_len - off)
            blob = rd(h, sec_va + off, want)
            if blob:
                p = 0
                while True:
                    i = blob.find(needle, p)
                    if i < 0 or i >= CHUNK: break
                    hits.append(sec_va + off + i)
                    p = i + 1
            off += CHUNK
        print(f"\n== {label}: {len(hits)} raw hit(s)")
        shown = 0
        for va in hits:
            ctx = rd(h, va - 8, 8)
            kind = classify(ctx) if ctx else None
            if kind is None:
                continue  # rel32/data noise; raw count above keeps the denominator honest
            mark = KNOWN.get(va, "")
            aft = rd(h, va + 4, 6)
            print(f"   0x{va:X}  {kind:26s} pre: {hx(ctx)} | imm | post: {hx(aft)}  {('<-- ' + mark) if mark else 'CANDIDATE'}")
            shown += 1
        print(f"   {shown} instruction-shaped (rest are rel32/data noise, listed count only)")
    k32.CloseHandle(h)


if __name__ == "__main__":
    main()
