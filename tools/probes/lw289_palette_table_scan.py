#!/usr/bin/env python
"""LW-289 round 5 (READ-ONLY): where does the running game keep the weapon palette table?

THE STATE OF THE HUNT. Battle weapon colour comes from the palette block of FFTPack file 71
(PROVEN live). Which of the sixteen palettes a weapon uses is KNOWN and matches the classic PSX
BATTLE.BIN item-graphics record, confirmed four for four against owner captures. What is NOT known
is where the game reads that assignment from, and without a write lever the feature is capped at
thirteen shared palettes for a hundred and twenty seven weapons.

THREE LEVERS ARE ALREADY DEAD, all tested live with untouched controls in the same battle and with
the serve proven from the loader log:
  ItemData <Palette>   inert for battle; it keys the MENU ICON clut instead
  ItemData <SpriteID>  inert for battle; it is the menu icon graphic
  battle_bin.bin       served from our copy five times, two bytes changed at the correct
                       offsets, nothing moved on screen
So the remaster re-baked this data somewhere of its own. It is in the exe image or in a table the
exe builds at load. This probe looks for it in the running process.

THE NEEDLE IS UNUSUALLY GOOD. The weapon palette nibble X takes only values 3 to 15 across all 127
weapons, never 0, 1 or 2, and the sequence is long. A 127-byte run with that alphabet does not
occur by chance in generic data, so a full-length hit is decisive rather than suggestive.

WHY IT SEARCHES FOR THE MODDED BYTES TOO. The install currently carries our modified
battle_bin.bin (items 19 and 26 changed). Searching for both variants turns one scan into a
discriminator:
  VANILLA pattern found, MODDED not      -> a second, baked copy exists that our file override
                                            never touches. That copy is the write target.
  MODDED pattern found, VANILLA not      -> the game does load our file into memory and simply
                                            does not consult it for colour. The palette source is
                                            some other structure entirely and this needle is the
                                            wrong shape.
  BOTH found, at different addresses     -> our file is loaded AND a separate baked copy exists.
                                            The baked one is the write target. Best outcome.
  NEITHER found in any form              -> the table is not stored as a contiguous byte run.
                                            Next step is widened or strided forms, then a
                                            breakpoint on the draw path rather than a data scan.

FORMS SEARCHED. The same logical table can be laid out several ways, so each is a separate needle:
  psx_record   the full 254-byte XY ZZ block exactly as BATTLE.BIN ships it
  xy_packed    just the 127 XY bytes, ZZ stripped
  x_u8         one byte per weapon, the palette nibble widened to a byte
  x_u16/x_u32  the same widened to 2 or 4 bytes little endian, which is what a remaster that
               re-typed a packed nibble field into a struct field would most likely produce
Each form is also searched from item 0 rather than item 1, since a rebaked table may include the
"Nothing Equipped" row that the PSX block starts after.

READ-ONLY. This probe opens the process with QUERY_INFORMATION | VM_READ only and never writes.

USAGE:
  python lw289_palette_table_scan.py                # full-length needles, all forms
  python lw289_palette_table_scan.py --partial      # also 48-byte windows, for a moved or
                                                    #   truncated table; noisier, run second
  python lw289_palette_table_scan.py --selftest     # pure checks, no game needed
Hits land in lw289_palette_table_hits.json beside this file.
"""
import ctypes as C
import json
import os
import struct
import sys
from ctypes import wintypes as W

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from lw289_battle_bin_palette_map import (  # noqa: E402
    FIRST_ITEM, LAST_ITEM, extract, gate, record_offset,
)

PROC = "fft_enhanced.exe"
HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "lw289_palette_table_hits.json")
PARTIAL_LEN = 48          # long enough to stay specific with a 13-value alphabet
MODDED = {19: (8, 2), 26: (5, 0)}   # what lw289_battle_bin_write.py deployed

k32 = C.WinDLL("kernel32", use_last_error=True)
PROCESS_QUERY_VM = 0x0400 | 0x0010
MEM_COMMIT = 0x1000
PAGE_NOACCESS, PAGE_GUARD = 0x01, 0x100
PROT = {0x01: "NOACCESS", 0x02: "R", 0x04: "RW", 0x08: "RWC", 0x10: "X",
        0x20: "RX", 0x40: "RWX", 0x80: "RWXC"}


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
    e = PE32()
    e.dwSize = C.sizeof(PE32)
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


def rpm(h, addr, size):
    buf = C.create_string_buffer(size)
    got = C.c_size_t()
    if not k32.ReadProcessMemory(h, C.c_void_p(addr), buf, size, C.byref(got)):
        return None
    return buf.raw[: got.value]


def records(raw, modded):
    """(X, Y, ZZ) per item id, optionally with the round-4 treatment applied so we can tell a
    loaded copy of OUR file apart from a baked copy of the vanilla data."""
    out = {}
    for iid in range(FIRST_ITEM, LAST_ITEM + 1):
        off = record_offset(iid)
        b0, b1 = raw[off], raw[off + 1]
        x, y = b0 >> 4, b0 & 0xF
        if modded and iid in MODDED:
            x, y = MODDED[iid]
        out[iid] = (x, y, b1)
    return out


def build_needles(raw):
    """{needle_bytes: label}. Every form, both variants, both starting items."""
    out = {}
    for variant, modded in (("vanilla", False), ("modded", True)):
        rec = records(raw, modded)
        for start, tag in ((FIRST_ITEM, "from1"), (0, "from0")):
            ids = [i for i in range(start, LAST_ITEM + 1) if i in rec or i == 0]
            xs = [rec.get(i, (0, 0, 0))[0] for i in ids]
            ys = [rec.get(i, (0, 0, 0))[1] for i in ids]
            zz = [rec.get(i, (0, 0, 0))[2] for i in ids]
            forms = {
                "psx_record": bytes(b for x, y, z in zip(xs, ys, zz) for b in ((x << 4) | y, z)),
                "xy_packed": bytes((x << 4) | y for x, y in zip(xs, ys)),
                "x_u8": bytes(xs),
                "x_u16": struct.pack(f"<{len(xs)}H", *xs),
                "x_u32": struct.pack(f"<{len(xs)}I", *xs),
            }
            for form, data in forms.items():
                out.setdefault(data, f"{variant}/{tag}/{form}/full")
    return out


def add_partials(needles, raw):
    """A 48-byte window from the middle of each full needle, for a table that was relocated,
    truncated, or that starts at some other item. Noisier by construction: a shorter needle over a
    13-value alphabet is far likelier to collide, so these are reported separately and are a lead,
    not a finding."""
    extra = {}
    for data, label in list(needles.items()):
        if len(data) <= PARTIAL_LEN:
            continue
        mid = (len(data) // 2) - (PARTIAL_LEN // 2)
        mid -= mid % 4          # keep widened forms element aligned
        window = data[mid:mid + PARTIAL_LEN]
        if window not in needles:
            extra[window] = label.replace("/full", f"/partial@{mid}")
    needles.update(extra)
    return len(extra)


def scan(partial):
    pid = find_pid()
    if not pid:
        sys.exit(f"{PROC} is not running; launch the game (the menu is fine) and rerun")
    h = k32.OpenProcess(PROCESS_QUERY_VM, False, pid)
    if not h:
        sys.exit(f"OpenProcess failed {C.get_last_error()}")
    work = os.path.join(os.environ.get("TEMP", "."), "lw289")
    raw = extract(work)
    gate(raw)
    needles = build_needles(raw)
    n_full = len(needles)
    n_part = add_partials(needles, raw) if partial else 0
    items = list(needles.items())
    longest = max(len(n) for n, _ in items)
    print(f"pid {pid}; {n_full} full needles"
          + (f" + {n_part} partial windows" if n_part else "")
          + f"; longest {longest} B; scanning", flush=True)

    hits, scanned, regions = [], 0, 0
    addr = 0
    while addr < 0x7FFFFFFFFFFF:
        mbi = MBI()
        if not k32.VirtualQueryEx(h, C.c_void_p(addr), C.byref(mbi), C.sizeof(mbi)):
            break
        base = mbi.BaseAddress or 0
        size = mbi.RegionSize
        nxt = base + size
        if (mbi.State == MEM_COMMIT and mbi.Protect
                and not (mbi.Protect & (PAGE_NOACCESS | PAGE_GUARD))):
            regions += 1
            pos, tail = base, b""
            while pos < nxt:
                chunk = rpm(h, pos, min(8 << 20, nxt - pos))
                if chunk is None:
                    break
                hay = tail + chunk
                scanned += len(chunk)
                for needle, label in items:
                    o = hay.find(needle)
                    while o != -1:
                        hits.append({"addr": pos - len(tail) + o, "label": label,
                                     "len": len(needle), "prot": PROT.get(mbi.Protect, hex(mbi.Protect)),
                                     "region": base, "regionSize": size})
                        o = hay.find(needle, o + 1)
                tail = hay[-(longest - 1):] if longest > 1 else b""
                pos += len(chunk)
        addr = nxt if nxt > addr else addr + 0x1000
    k32.CloseHandle(h)
    return hits, scanned, regions


def report(hits, scanned, regions):
    print(f"scanned {scanned / (1 << 20):.0f} MB across {regions} committed regions")
    full = [x for x in hits if x["label"].endswith("/full")]
    part = [x for x in hits if not x["label"].endswith("/full")]
    if not hits:
        print("\nNO HITS in any form. The table is not a contiguous byte run of these shapes.")
        print("Pre-registered next step: widened or strided layouts, then a breakpoint on the")
        print("weapon draw path rather than a data scan.")
        return
    for tag, group in (("FULL-LENGTH HITS (decisive)", full),
                       ("partial-window hits (leads only, a 48 B window can collide)", part)):
        if not group:
            continue
        print(f"\n{tag}: {len(group)}")
        for x in sorted(group, key=lambda z: z["addr"]):
            print(f"   0x{x['addr']:012X}  {x['prot']:4}  {x['len']:4} B  {x['label']}"
                  f"   region 0x{x['region']:X}+0x{x['regionSize']:X}")
    got = {x["label"].split("/")[0] for x in full}
    print()
    if "vanilla" in got and "modded" not in got:
        print("READING: a BAKED copy of the vanilla data exists that our file override never")
        print("reaches. That copy is the write target. This is the outcome we wanted.")
    elif "modded" in got and "vanilla" not in got:
        print("READING: only OUR file is in memory. The game loads it and does not consult it for")
        print("colour, so the palette source is a different structure and this needle is the")
        print("wrong shape.")
    elif got >= {"vanilla", "modded"}:
        print("READING: our file is loaded AND a separate baked copy exists. The baked one, at the")
        print("address that does NOT match our modded bytes, is the write target.")
    else:
        print("READING: partial windows only. Treat as a lead; widen and re-scan before believing.")


def selftest():
    fake = bytearray(0x40000)
    for iid in range(FIRST_ITEM, LAST_ITEM + 1):
        off = record_offset(iid)
        fake[off] = ((3 + iid % 13) << 4) | (iid % 3)
        fake[off + 1] = iid % 251
    van = records(bytes(fake), False)
    mod = records(bytes(fake), True)
    assert len(van) == LAST_ITEM - FIRST_ITEM + 1, "wrong record count"
    assert all(3 <= x <= 15 for x, _, _ in van.values()), "fixture X out of the real alphabet"
    for iid, (x, y) in MODDED.items():
        assert mod[iid][0] == x and mod[iid][1] == y, f"treatment not applied to {iid}"
        assert mod[iid][2] == van[iid][2], "treatment must not touch the graphic byte"
    diff = [i for i in van if van[i] != mod[i]]
    assert set(diff) == set(MODDED), f"variants differ at {diff}, expected {sorted(MODDED)}"
    needles = build_needles(bytes(fake))
    # The two variants MUST produce different bytes, or the discriminator is vacuous.
    by = {}
    for data, label in needles.items():
        by[label] = data
    for tag in ("from1", "from0"):
        for form in ("psx_record", "xy_packed", "x_u8", "x_u16", "x_u32"):
            a = by.get(f"vanilla/{tag}/{form}/full")
            b = by.get(f"modded/{tag}/{form}/full")
            assert a and b, f"missing needle {tag}/{form}"
            assert a != b, f"{tag}/{form}: vanilla and modded needles are identical, the scan " \
                           f"could not tell a loaded override from a baked copy"
    lens = {len(d) for d in needles}
    assert min(lens) >= 100, "a full needle is suspiciously short"
    n = add_partials(dict(needles), bytes(fake))
    assert n > 0, "partial windows produced nothing"
    print("selftest OK")
    print(f"  {len(needles)} full needles, lengths {sorted(lens)}")


def main():
    if "--selftest" in sys.argv:
        selftest()
        return
    selftest()
    hits, scanned, regions = scan("--partial" in sys.argv)
    report(hits, scanned, regions)
    json.dump(hits, open(OUT, "w", encoding="utf-8"), indent=1)
    print(f"\nwrote {OUT}")


if __name__ == "__main__":
    main()
