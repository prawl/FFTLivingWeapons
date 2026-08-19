#!/usr/bin/env python
"""LW-251 round 3: hunt the live weapon CLUT in the running game's memory.

Rounds 1-2 proved the battle equipment sheet is g2d tex_161 (4bpp indices, PROVEN,
ledger [g2d-equipment-sheet-override]) but its COLOURS come from a palette asset that is
neither battle_wep_spr.bin's palette block (2026-06-01 magenta test) nor the six
palette-shaped g2d entries beside the sheets (2026-08-18 signature round, all inert for
weapons). So: stop guessing files, find the palette in RAM. We know the vanilla CLUT
values exactly (the classic bin's 16 palettes of 16 BGR555 colours); the live process
must hold the real working copy in SOME format. Scan for it, poke each hit with a loud
signature, watch the bow.

WRITES MEMORY on poke (probe protocol: owner-sanctioned session, undo file + restore
verb; originals saved before every write). Scan is read-only.

USAGE (game running, ideally sitting in a battle with a bow archer visible):
  python lw251_clut_ram_hunt.py scan            # find CLUT copies, list hits
  python lw251_clut_ram_hunt.py poke <idx>      # overwrite hit #idx with RED ramps
  python lw251_clut_ram_hunt.py poke <idx> --sig green|blue|magenta
  python lw251_clut_ram_hunt.py restore         # put every poked byte back
Hits and undo state persist in lw251_clut_hits.json next to this file.
"""
import ctypes as C
from ctypes import wintypes as W
import json
import os
import struct
import sys

PROC = "fft_enhanced.exe"
BIN = r"C:\Users\ptyRa\OneDrive\Desktop\Pac Files\0002\fftpack\unit\battle_wep_spr.bin"
STATE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "lw251_clut_hits.json")

k32 = C.WinDLL("kernel32", use_last_error=True)
PROCESS_ALL = 0x0010 | 0x0020 | 0x0008 | 0x0400
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
                pid = e.th32ProcessID
                break
            if not k32.Process32Next(snap, C.byref(e)):
                break
    k32.CloseHandle(snap)
    return pid


def open_proc():
    pid = find_pid()
    if not pid:
        sys.exit("game not running")
    h = k32.OpenProcess(PROCESS_ALL, False, pid)
    if not h:
        sys.exit(f"OpenProcess failed {C.get_last_error()}")
    return h, pid


def rpm(h, addr, size):
    buf = C.create_string_buffer(size)
    got = C.c_size_t()
    if not k32.ReadProcessMemory(h, C.c_void_p(addr), buf, size, C.byref(got)):
        return None
    return buf.raw[: got.value]


def wpm(h, addr, data):
    old = W.DWORD()
    k32.VirtualProtectEx(h, C.c_void_p(addr), len(data), 0x40, C.byref(old))
    got = C.c_size_t()
    ok = k32.WriteProcessMemory(h, C.c_void_p(addr), data, len(data), C.byref(got))
    if old.value:
        k32.VirtualProtectEx(h, C.c_void_p(addr), len(data), old.value, C.byref(W.DWORD()))
    return bool(ok) and got.value == len(data)


def load_pal555():
    """Classic bin palette block: 16 palettes x 16 BGR555 u16."""
    raw = open(BIN, "rb").read(512)
    return [list(struct.unpack_from("<16H", raw, p * 32)) for p in range(16)]


def needles():
    """Search needles per format. Colours 1..15 of pal0 (skip the zero slot) plus the
    same span of pal1 as a confirmatory second anchor."""
    pals = load_pal555()
    out = []
    for pi in (0, 1):
        v555 = struct.pack("<15H", *pals[pi][1:])
        out.append((f"pal{pi}-555", v555, "555", pi))
        for order, tag in (((0, 1, 2), "rgba"), ((2, 1, 0), "bgra")):
            b = bytearray()
            for v in pals[pi][1:]:
                ch = [(v & 0x1F) << 3, ((v >> 5) & 0x1F) << 3, ((v >> 10) & 0x1F) << 3]
                b += bytes([ch[order[0]], ch[order[1]], ch[order[2]], 0xFF])
            out.append((f"pal{pi}-{tag}ff", bytes(b), tag, pi))
    return out


def scan():
    h, pid = open_proc()
    ns = needles()
    print(f"pid {pid}; scanning for {len(ns)} needle variants...")
    hits = []
    addr = 0
    max_addr = 0x7FFFFFFFFFFF
    CH = 16 * 1024 * 1024
    while addr < max_addr:
        mbi = MBI()
        if not k32.VirtualQueryEx(h, C.c_void_p(addr), C.byref(mbi), C.sizeof(mbi)):
            break
        base = mbi.BaseAddress or 0
        size = mbi.RegionSize
        nxt = base + size
        readable = (mbi.State == MEM_COMMIT and not (mbi.Protect & (PAGE_NOACCESS | PAGE_GUARD))
                    and mbi.Protect != 0)
        if readable:
            pos = base
            tail = b""
            while pos < nxt:
                chunk = rpm(h, pos, min(CH, nxt - pos))
                if chunk is None:
                    break
                hay = tail + chunk
                for name, needle, fmt, pi in ns:
                    o = hay.find(needle)
                    while o != -1:
                        va = pos - len(tail) + o
                        hits.append({"addr": va, "pattern": name, "fmt": fmt, "pal": pi,
                                     "region": hex(base), "prot": mbi.Protect,
                                     "type": mbi.Type})
                        o = hay.find(needle, o + 1)
                tail = chunk[-128:]
                pos += len(chunk)
        addr = nxt
    k32.CloseHandle(h)
    # collapse: pal1 hits that sit 32 bytes after a pal0 hit confirm a full block
    for hh in hits:
        hh["confirmed_block"] = any(
            o["pal"] == 1 and o["fmt"] == hh["fmt"] and
            o["addr"] - hh["addr"] in (32, 62, 64, 120, 124, 128)
            for o in hits) if hh["pal"] == 0 else False
    json.dump({"hits": hits, "undo": []}, open(STATE, "w"), indent=1)
    print(f"{len(hits)} hits -> {STATE}")
    for i, hh in enumerate(hits):
        print(f"  [{i}] {hex(hh['addr'])} {hh['pattern']} region {hh['region']} "
              f"prot {hex(hh['prot'])} type {hex(hh['type'])}"
              + (" FULL-BLOCK" if hh.get("confirmed_block") else ""))


SIGS = {"red": (1, 0.12, 0.12), "green": (0.12, 1, 0.12), "blue": (0.15, 0.3, 1),
        "magenta": (1, 0.12, 1)}


def sig_block(fmt, mult, n_colors=15):
    """A loud luminance-ramp signature covering colours 1..15 in the hit's format."""
    out = bytearray()
    for k in range(n_colors):
        lum = 8 + int(k * (23 / max(1, n_colors - 1)))  # 8..31 ramp
        r, g, b = (min(31, int(lum * m)) for m in mult)
        if fmt == "555":
            out += struct.pack("<H", r | (g << 5) | (b << 10))
        else:
            ch = [r << 3, g << 3, b << 3]
            if fmt == "bgra":
                ch = ch[::-1]
            out += bytes([ch[0], ch[1], ch[2], 0xFF])
    return bytes(out)


def poke(idx, sig):
    st = json.load(open(STATE))
    hh = st["hits"][idx]
    h, _ = open_proc()
    blob = sig_block(hh["fmt"], SIGS[sig])
    orig = rpm(h, hh["addr"], len(blob))
    if orig is None:
        sys.exit("read-back failed at hit address")
    if not wpm(h, hh["addr"], blob):
        sys.exit("write failed (protection?)")
    st["undo"].append({"addr": hh["addr"], "bytes": orig.hex()})
    json.dump(st, open(STATE, "w"), indent=1)
    k32.CloseHandle(h)
    print(f"poked hit [{idx}] at {hex(hh['addr'])} with {sig.upper()} ramp "
          f"({len(blob)} bytes, undo saved). Watch the bow; if static, re-enter the "
          f"battle view (menu round-trip) to force a palette re-read.")


def restore():
    """Verified restore: only write the original bytes back if the address still holds
    one of OUR signature blocks. A heap page freed and re-used since the poke would
    otherwise get 30 bytes of stale palette written into whatever lives there now."""
    st = json.load(open(STATE))
    h, _ = open_proc()
    sigs = [sig_block("555", m) for m in SIGS.values()]
    ok = skipped = 0
    for u in st["undo"]:
        orig = bytes.fromhex(u["bytes"])
        cur = rpm(h, u["addr"], len(orig))
        if cur is None or cur not in sigs:
            skipped += 1
            continue
        if wpm(h, u["addr"], orig):
            ok += 1
    k32.CloseHandle(h)
    print(f"restored {ok}, skipped {skipped} (page changed or unreadable; left alone)")
    st["undo"] = []
    json.dump(st, open(STATE, "w"), indent=1)


if __name__ == "__main__":
    if len(sys.argv) < 2 or sys.argv[1] not in ("scan", "poke", "restore"):
        sys.exit(__doc__)
    if sys.argv[1] == "scan":
        scan()
    elif sys.argv[1] == "poke":
        sig = sys.argv[sys.argv.index("--sig") + 1] if "--sig" in sys.argv else "red"
        poke(int(sys.argv[2]), sig)
    else:
        restore()
