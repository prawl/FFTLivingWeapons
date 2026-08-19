#!/usr/bin/env python
"""LW-251 round 8 (read-only): locate the HD weapon CLUT in the running game's memory.

The correction that motivates this probe (offline-derived 2026-08-19, on the container the
game actually loads): the battle weapon HD art is g2d entry 161, a 512x512 4bpp sheet with
16 indices, and its colours come from a CLUT the game picks per weapon from g2d entry 156,
a bank of 136 sixteen-colour palettes. That bank shares ZERO bytes with the classic
battle_wep_spr.bin palettes (checked: 0 of 156's palettes overlap). So the earlier boot
race, which hunts battle_wep_spr rows in the exe image, targets the wrong colour source
for HD weapons. This probe hunts the RIGHT source: entry-156 palettes, in every plausible
in-memory format, so we learn WHERE the HD CLUT lives and in WHAT form before spending a
live launch on a write test.

The container the game loads is modded.pac's system/ffto/g2d.dat (2450 entries, no-drift),
NOT the loose FFTIVC/data/enhanced/system/ffto/g2d.dat (1426 entries, drift-16, the ignored
Dec-2025 leftover). Needles were exported from the modded.pac copy into
lw251_hd_clut_needles.json (116 strong palettes, >=8 distinct non-zero colours each).

READ-ONLY: this probe never writes the game's memory. It scans and reports. Run it with the
game running, ideally sitting in a battle with an Archer's bow visible.

USAGE:
  python lw251_hd_clut_scan.py            # scan all formats, report hits grouped by region
  python lw251_hd_clut_scan.py --fmt 555  # restrict to one format (555|rgba|bgra)
Hits print to console and land in lw251_hd_clut_hits.json next to this file.

PRE-REGISTERED READING:
  hits in a committed RW region  -> the HD CLUT is in memory in that format at those
                                    addresses; the boot race (or a post-boot write test)
                                    should target THESE, not battle_wep_spr rows.
  hits ONLY in the g2d container mapping (modded.pac view) -> the palettes exist only as the
                                    stored container copy; the runtime CLUT is transformed
                                    (different format) and needs a wider-format scan.
  no hits in any format           -> the game transforms the palettes at load past raw
                                    recognisability; next step is to break on the sheet's
                                    draw call rather than to pattern-scan the data.
"""
import ctypes as C
from ctypes import wintypes as W
import json, os, struct, sys

PROC = "fft_enhanced.exe"
HERE = os.path.dirname(os.path.abspath(__file__))
NEEDLES = os.path.join(HERE, "lw251_hd_clut_needles.json")
OUT = os.path.join(HERE, "lw251_hd_clut_hits.json")

k32 = C.WinDLL("kernel32", use_last_error=True)
PROCESS_QUERY_VM = 0x0400 | 0x0010  # QUERY_INFORMATION | VM_READ
MEM_COMMIT = 0x1000
PAGE_NOACCESS, PAGE_GUARD = 0x01, 0x100


class MBI(C.Structure):
    _fields_ = [("BaseAddress", C.c_void_p), ("AllocationBase", C.c_void_p),
                ("AllocationProtect", W.DWORD), ("PartitionId", W.WORD),
                ("RegionSize", C.c_size_t), ("State", W.DWORD),
                ("Protect", W.DWORD), ("Type", W.DWORD)]


class PE32(C.Structure):
    _fields_ = [("dwSize", W.DWORD), ("cntUsage", W.DWORD), ("th32ProcessID", W.DWORD),
                ("th32DefaultHeapID", C.POINTER(C.c_ulong)), ("th32ModuleID", W.DWORD),
                ("cntThreads", W.DWORD), ("th32ParentProcessID", W.DWORD),
                ("pcPriClassBase", C.c_long), ("dwFlags", W.DWORD),
                ("szExeFile", C.c_char * 260)]


def find_pid():
    snap = k32.CreateToolhelp32Snapshot(0x2, 0)
    e = PE32(); e.dwSize = C.sizeof(PE32)
    pid = None
    if k32.Process32First(snap, C.byref(e)):
        while True:
            if e.szExeFile.decode(errors="ignore").lower() == PROC:
                pid = e.th32ProcessID; break
            if not k32.Process32Next(snap, C.byref(e)):
                break
    k32.CloseHandle(snap)
    return pid


def rpm(h, addr, size):
    buf = C.create_string_buffer(size); got = C.c_size_t()
    if not k32.ReadProcessMemory(h, C.c_void_p(addr), buf, size, C.byref(got)):
        return None
    return buf.raw[: got.value]


def build_needles(fmts):
    """One needle per HD palette per requested format. 555 = raw BGR555 u16 (as stored);
    rgba/bgra = 8-bit expansions the engine might hold after unpacking."""
    spec = json.load(open(NEEDLES))
    out = []
    for pe in spec["palettes"]:
        p = pe["bgr555"]; row = pe["bank_row"]
        if "555" in fmts:
            out.append(("555", row, struct.pack("<16H", *p)))
        for tag in ("rgba", "bgra"):
            if tag not in fmts:
                continue
            b = bytearray()
            for v in p:
                r = (v & 0x1F) << 3; g = ((v >> 5) & 0x1F) << 3; bl = ((v >> 10) & 0x1F) << 3
                px = [r, g, bl] if tag == "rgba" else [bl, g, r]
                a = 0 if v == 0 else 0xFF
                b += bytes([px[0], px[1], px[2], a])
            out.append((tag, row, bytes(b)))
    return out


def scan(fmts):
    pid = find_pid()
    if not pid:
        sys.exit("game not running")
    h = k32.OpenProcess(PROCESS_QUERY_VM, False, pid)
    if not h:
        sys.exit(f"OpenProcess failed {C.get_last_error()}")
    needles = build_needles(fmts)
    print(f"pid {pid}; {len(needles)} needles across {sorted(fmts)}; scanning...")
    hits = []
    addr = 0
    while addr < 0x7FFFFFFFFFFF:
        mbi = MBI()
        if not k32.VirtualQueryEx(h, C.c_void_p(addr), C.byref(mbi), C.sizeof(mbi)):
            break
        base = mbi.BaseAddress or 0
        size = mbi.RegionSize
        nxt = base + size
        readable = (mbi.State == MEM_COMMIT
                    and not (mbi.Protect & (PAGE_NOACCESS | PAGE_GUARD))
                    and mbi.Protect != 0)
        if readable:
            pos, tail = base, b""
            while pos < nxt:
                chunk = rpm(h, pos, min(8 << 20, nxt - pos))
                if chunk is None:
                    break
                hay = tail + chunk
                for tag, row, needle in needles:
                    o = hay.find(needle)
                    while o != -1:
                        va = pos - len(tail) + o
                        hits.append({"addr": va, "fmt": tag, "bank_row": row,
                                     "region": hex(base), "prot": mbi.Protect,
                                     "type": mbi.Type})
                        o = hay.find(needle, o + 1)
                tail = chunk[-64:]
                pos += len(chunk)
        addr = nxt
    k32.CloseHandle(h)
    json.dump({"hits": hits}, open(OUT, "w"), indent=1)
    # group report
    from collections import Counter
    byreg = Counter((h_["region"], h_["fmt"], hex(h_["type"])) for h_ in hits)
    print(f"\n{len(hits)} hits -> {OUT}")
    for (reg, fmt, typ), c in sorted(byreg.items(), key=lambda kv: -kv[1]):
        print(f"  region {reg} type {typ} fmt {fmt}: {c} palette hits")
    if not hits:
        print("  NO HITS in any format: the runtime CLUT is transformed past raw match; "
              "next step is a draw-call breakpoint, not a data scan.")


if __name__ == "__main__":
    fmts = {"555", "rgba", "bgra"}
    if "--fmt" in sys.argv:
        fmts = {sys.argv[sys.argv.index("--fmt") + 1]}
    scan(fmts)
