#!/usr/bin/env python
"""
Weapon battle-sprite probe (READ-ONLY). Locate the table that ACTUALLY drives the in-battle
weapon graphic (Bug B/D), after we proved ItemData byte 0x01 SpriteID does NOT.

WHY. Recategorizing a flail->Sword lands the item-type byte (0x05) -- equip + skills fixed --
but the weapon still SWINGS like a flail. Setting ItemData SpriteID (byte 0x01) to a sword value
(15) had no effect on the render. Per FFHacktics + an FFHacktics-Discord tip the render comes
from a SEPARATE table, "weapon battle sprite and palette IDs", 2 BYTES PER WEAPON:
    byte 0: palette nibbles  (X0 = weapon palette, 0X = glint palette)
    byte 1: Graphic ID       (clamped to the item-type's section; sword-type -> sword graphics)
  FFHacktics-Discord AOB (PSX values; IC may differ):
    E0 00 F0 02 E0 04 30 06 D0 00 30 02 51 04 40 06

This probe AOB-scans for that table, then cross-references each weapon against the ItemData table
(already located: byte 0x00 Palette, 0x01 SpriteID, 0x04 SecondTableId, 0x05 ItemType) under BOTH
plausible indexings -- by canonical item id, and by the weapon's SecondTableId -- so we can see
which one makes the native swords (ids 19-32) share a sword-section Graphic ID, and read what
Warbrand (id 67) currently points at.

USAGE (game booted, item tables resident -- title/save is fine):
  python weapon_sprite_probe.py                  # scan + cross-ref dump for the weapons of interest
  python weapon_sprite_probe.py --aob "E0 00 .." # override the search signature
  python weapon_sprite_probe.py --base 0xADDR    # 2-byte table base, skip scan
  python weapon_sprite_probe.py --window 0xADDR 256   # raw 16-col hex dump (hunt near ItemData)
  python weapon_sprite_probe.py --range 0 80     # dump the 2-byte entries for an id range

NOTE: indexing is the open question -- the dump prints both id*2 and SecondTableId*2 so a human can
pick the one where swords cluster. Nothing is written. Pairs with item_type_probe.py (byte 0x05).
"""
import ctypes as C
from ctypes import wintypes as W
import sys

PROC = "FFT_enhanced"

# ItemData (12-byte records) -- the table item_type_probe.py locates. We re-find it here to read
# each weapon's type/secondTableId/spriteId for the cross-reference.
ID_REC = 12
ID_PAL = 0x00
ID_SPRITE = 0x01
ID_FLAGS = 0x03
ID_SECOND = 0x04
ID_TYPE = 0x05
ITEMDATA_AOB = "00 00 00 80 00 00 00 00 00 00 00 00 00 01 01 80 01 01 00 00"

# The 2-byte weapon battle-sprite table.
WS_REC = 2
WS_PAL = 0x00
WS_GFX = 0x01
WEAPON_SPRITE_AOB = "E0 00 F0 02 E0 04 30 06 D0 00 30 02 51 04 40 06"

TYPE_NAME = {0x00: "(none)", 0x01: "Knife", 0x02: "NinjaBlade", 0x03: "Sword", 0x04: "KnightSword",
             0x05: "Katana", 0x06: "Axe", 0x07: "Rod", 0x08: "Staff", 0x09: "Flail", 0x0a: "Gun",
             0x0b: "Crossbow", 0x0c: "Bow", 0x0d: "Instrument", 0x0e: "Book", 0x0f: "Polearm",
             0x10: "Pole", 0x11: "Bag"}

# Weapons to read: native sword/knightsword controls + the recategorized set under test.
WATCH = [
    (1, "Dagger (native Knife)"), (2, "MythrilKnife (Knife)"),
    (19, "Vagabond (native Sword)"), (20, "Cleaver (Sword)"), (32, "MateriaBlade (Sword)"),
    (33, "Defender (native KnightSword)"), (35, "Excalibur (KnightSword)"),
    (38, "native Katana"),
    (48, "Terrastaff -> Pole"), (49, "Ravager -> KnightSword"), (50, "Sunderer -> KnightSword"),
    (65, "Zeus Mace (native, untouched)"),
    (67, "Warbrand -> Sword (BUSTED)"), (68, "Bloodlash -> Knife"),
    (69, "Climhazzard -> NinjaBlade"), (70, "Sasori -> Katana"),
]

k32 = C.WinDLL("kernel32", use_last_error=True)
psapi = C.WinDLL("psapi", use_last_error=True)
PV = 0x0010 | 0x0400


class _PMC(C.Structure):
    _fields_ = [("cb", W.DWORD), ("PageFaultCount", W.DWORD),
                ("PeakWorkingSetSize", C.c_size_t), ("WorkingSetSize", C.c_size_t),
                ("QuotaPeakPagedPoolUsage", C.c_size_t), ("QuotaPagedPoolUsage", C.c_size_t),
                ("QuotaPeakNonPagedPoolUsage", C.c_size_t), ("QuotaNonPagedPoolUsage", C.c_size_t),
                ("PagefileUsage", C.c_size_t), ("PeakPagefileUsage", C.c_size_t)]


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


class _MBI(C.Structure):
    _fields_ = [("BaseAddress", C.c_void_p), ("AllocationBase", C.c_void_p),
                ("AllocationProtect", W.DWORD), ("RegionSize", C.c_size_t),
                ("State", W.DWORD), ("Protect", W.DWORD), ("Type", W.DWORD)]


def rd(h, a, n):
    b = (C.c_ubyte * n)(); g = C.c_size_t(0)
    if k32.ReadProcessMemory(h, C.c_void_p(a), b, n, C.byref(g)) and g.value == n:
        return bytes(b)
    return None


def iter_regions(h):
    MEM_COMMIT = 0x1000
    READABLE = 0x02 | 0x04 | 0x20 | 0x40
    PAGE_GUARD = 0x100
    addr = 0; mbi = _MBI()
    while addr < 0x7FFFFFFFFFFF:
        if not k32.VirtualQueryEx(h, C.c_void_p(addr), C.byref(mbi), C.sizeof(mbi)):
            break
        base = mbi.BaseAddress or 0
        size = mbi.RegionSize or 0x1000
        if mbi.State == MEM_COMMIT and (mbi.Protect & READABLE) and not (mbi.Protect & PAGE_GUARD):
            yield base, size
        addr = base + size


def scan(h, aob_hex, validate):
    sig = bytes(int(x, 16) for x in aob_hex.split())
    for base, size in iter_regions(h):
        if size > 64 * 1024 * 1024:
            continue
        blob = rd(h, base, size)
        if not blob:
            continue
        off = blob.find(sig)
        while off != -1:
            cand = base + off
            if validate(h, cand):
                return cand
            off = blob.find(sig, off + 1)
    return None


def itemdata_valid(h, base):
    b = rd(h, base, ID_REC * 70)
    if not b:
        return False
    knife = sum(1 for i in range(1, 11) if (b[i * ID_REC + ID_FLAGS] & 0x80) and b[i * ID_REC + ID_TYPE] == 1)
    sane = all(b[i * ID_REC + ID_TYPE] in TYPE_NAME for i in range(1, 64))
    return knife >= 7 and sane


def find_itemdata(h):
    return scan(h, ITEMDATA_AOB, itemdata_valid)


def itemdata_row(h, base, iid):
    b = rd(h, base + iid * ID_REC, ID_REC)
    if not b:
        return None
    return {"type": b[ID_TYPE], "second": b[ID_SECOND], "sprite": b[ID_SPRITE], "pal": b[ID_PAL]}


def ws_entry(h, base, idx):
    b = rd(h, base + idx * WS_REC, WS_REC)
    if not b:
        return None
    return {"pal": b[WS_PAL], "gfx": b[WS_GFX]}


def main():
    argv = sys.argv[1:]
    ws_base = None; aob = WEAPON_SPRITE_AOB
    if "--aob" in argv:
        aob = argv[argv.index("--aob") + 1]
    if "--base" in argv:
        ws_base = int(argv[argv.index("--base") + 1], 16)

    pid = find_pid(PROC)
    if not pid:
        print(f"{PROC} not running."); return
    h = k32.OpenProcess(PV, False, pid)
    if not h:
        print(f"OpenProcess failed (err {C.get_last_error()})."); return
    print(f"pid {pid} (largest working set)")

    if "--window" in argv:
        i = argv.index("--window"); a = int(argv[i + 1], 16); ln = int(argv[i + 2])
        b = rd(h, a, ln)
        if not b:
            print("read fail"); return
        for o in range(0, ln, 16):
            row = b[o:o + 16]
            print(f"0x{a + o:X}  " + " ".join(f"{x:02X}" for x in row))
        return

    if "--all" in argv:
        # List EVERY occurrence of the weapon-sprite AOB (the engine may copy the const table to the
        # heap at battle-load; the live render may read a copy, not the .rdata original).
        sig = bytes(int(x, 16) for x in aob.split())
        hits = []
        for base, size in iter_regions(h):
            if size > 256 * 1024 * 1024:
                continue
            blob = rd(h, base, size)
            if not blob:
                continue
            off = blob.find(sig)
            while off != -1:
                hits.append(base + off)
                off = blob.find(sig, off + 1)
        print(f"{len(hits)} copies of the weapon-sprite AOB:")
        for hbase in hits:
            gfx67 = rd(h, hbase + 67 * WS_REC + WS_GFX, 1)
            region = "rdata/exe" if 0x140000000 <= hbase < 0x141000000 else "heap/other"
            print(f"  table base 0x{hbase:X}  ({region})  -> Warbrand(67) gfx byte @ 0x{hbase + 67*WS_REC + WS_GFX:X}"
                  f" = {gfx67[0] if gfx67 else '?'}")
        return

    id_base = find_itemdata(h)
    print(f"ItemData base: {'0x%X' % id_base if id_base else 'NOT FOUND'}")

    if ws_base is None:
        print(f"scanning for weapon-sprite table (aob: {aob}) ...")
        # Validate a candidate by reading entries; accept any region the AOB matches (the AOB itself is
        # the signature). We can't structurally verify without knowing IC values, so take first match.
        ws_base = scan(h, aob, lambda _h, _c: True)
    if ws_base is None:
        print("WEAPON-SPRITE TABLE NOT FOUND via AOB (IC values likely differ from the PSX AOB).")
        print("Fallback: the table often sits near ItemData. Hunt with --window around the ItemData base,")
        print("e.g.  --window 0x%X 512   (then re-run with --base 0xADDR once you spot the 2-byte stride)."
              % (id_base or 0x14080000))
        return
    print(f"weapon-sprite table base: 0x{ws_base:X}\n")

    if "--range" in argv:
        i = argv.index("--range"); lo = int(argv[i + 1]); hi = int(argv[i + 2])
        for idx in range(lo, hi + 1):
            e = ws_entry(h, ws_base, idx)
            if e:
                print(f"  idx {idx:>3}  pal=0x{e['pal']:02X}  gfx={e['gfx']:>3} (0x{e['gfx']:02X})")
        return

    print("CROSS-REFERENCE (which indexing makes native swords share a sword-section Graphic ID?):")
    print(f"  {'id':>3}  {'label':<30} {'type':<11} | ItemData spr/pal sec | WS[id*2] gfx/pal | WS[sec*2] gfx/pal")
    for iid, label in WATCH:
        r = itemdata_row(h, id_base, iid) if id_base else None
        t = TYPE_NAME.get(r["type"], f"0x{r['type']:02X}") if r else "?"
        spr = r["sprite"] if r else "?"; pal = r["pal"] if r else "?"; sec = r["second"] if r else None
        by_id = ws_entry(h, ws_base, iid)
        by_sec = ws_entry(h, ws_base, sec) if sec is not None else None
        s_id = f"gfx={by_id['gfx']:>3} pal=0x{by_id['pal']:02X}" if by_id else "----"
        s_sec = f"gfx={by_sec['gfx']:>3} pal=0x{by_sec['pal']:02X}" if by_sec else "----"
        secs = f"{sec:>3}" if sec is not None else "  ?"
        print(f"  {iid:>3}  {label:<30} {t:<11} | spr={spr:<3} pal=0x{pal:02X} sec={secs} | {s_id:<16} | {s_sec}")
    print("\nRead the two right columns: the indexing where ids 19/20/32 (swords) share a small")
    print("graphic cluster AND 33/35 (knight swords) share a different one is the real index. Warbrand")
    print("(67) currently points at a flail-section gfx; the fix value = whatever the native swords use.")
    k32.CloseHandle(h)


if __name__ == "__main__":
    main()
