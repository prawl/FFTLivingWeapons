#!/usr/bin/env python
"""
Item-type probe (READ-ONLY). Settle Bug B: do our category overrides reach the engine?

THE QUESTION. The rebalance recategorizes axe/flail-family weapons into sword-family types
(items.json `categoryOverride`): Warbrand 67 (Iron Flail -> Sword), Bloodlash 68 (Flail ->
Knife), Climhazzard 69 (Morning Star -> NinjaBlade), Sasori 70 (Scorpion Tail -> Katana),
Terrastaff 48 (Battle Axe -> Pole), Ravager 49 / Sunderer 50 (Axe -> KnightSword). In game,
a recategorized weapon swings with the WRONG animation and loses skill access -- as if the
engine still thinks it's a flail/axe. The lever per FFHacktics (FFT/TIC data tables) is a
SINGLE byte:

  Item Data record (12 bytes/id), TableAddress varies in IC -- AOB-located here:
    0x00 Palette   0x01 SpriteID   0x02 ReqLevel   0x03 TypeFlags(0x80=Weapon)
    0x04 SecondTableId   *** 0x05 ITEM TYPE ***   0x06 -   0x07 Attributes
    0x08 Price(u16)   0x0a ShopAvail   0x0b -
  Item Type values: 01 Knife 02 NinjaBlade 03 Sword 04 KnightSword 05 Katana 06 Axe
                    07 Rod 08 Staff 09 Flail 0a Gun 0b Crossbow 0c Bow 0d Instrument
                    0e Book 0f Polearm 10 Pole 11 Bag 12 Cloth 13 Shield 14 Helmet ...

byte 0x05 gates equip-class (JobData 0x09-0x0a is a bitmask keyed on this exact type),
attack animation, and weapon-skill access. Our pipeline maps categoryOverride -> ItemData.xml
<ItemCategory> -> byte 0x05. So IF the modloader write lands, byte 0x05 for Warbrand reads
03 (Sword). IF a base-nex stomp (the same one that ate <Palette>/<Price> per WEAPON_VISUALS
Ask B) wins, it reads 09 (Flail) and Bug B is a write-that-never-lands.

VERDICT this probe prints: for each recategorized id, OVERRIDE LANDED (byte 0x05 == expected
override type) or STOMPED (byte 0x05 == the vanilla base type). Untouched controls (Vagabond
19 Sword, Defender 33 KnightSword, Zeus Mace 65 Flail) confirm the table base is right.

USAGE (game running; the item table is resident on the title screen / in a save -- no battle
needed):
  python item_type_probe.py                 # AOB-scan, locate table, dump + verdict
  python item_type_probe.py --base 0xADDR   # skip the scan, read at a known table base
  python item_type_probe.py --aob "00 00 00 80 ..."   # override the search signature
  python item_type_probe.py --dump 0 122    # raw-dump item-type byte for an id range

NOTE: the ItemData table is indexed by the CANONICAL item id (== items.json id == ItemData.xml
<Id>), NOT the remapped id an equip slot stores (the Stoneshooter-reads-67 trap in
actor_attrib_probe.py is an equip-slot quirk, not a table-index one).
"""
import ctypes as C
from ctypes import wintypes as W
import sys

PROC = "FFT_enhanced"
REC = 12          # bytes per item record
T_TYPE = 0x05     # item-type byte offset within a record
T_FLAGS = 0x03    # TypeFlags (0x80 = Weapon)
T_SPRITE = 0x01

TYPE_NAME = {0x01: "Knife", 0x02: "NinjaBlade", 0x03: "Sword", 0x04: "KnightSword",
             0x05: "Katana", 0x06: "Axe", 0x07: "Rod", 0x08: "Staff", 0x09: "Flail",
             0x0a: "Gun", 0x0b: "Crossbow", 0x0c: "Bow", 0x0d: "Instrument", 0x0e: "Book",
             0x0f: "Polearm", 0x10: "Pole", 0x11: "Bag", 0x12: "Cloth", 0x13: "Shield",
             0x14: "Helmet", 0x15: "Hat", 0x16: "HairAdornment", 0x17: "Armor",
             0x18: "Clothing", 0x19: "Robe", 0x1a: "Shoes", 0x1b: "Armguard", 0x1c: "Ring",
             0x1d: "Armlet", 0x1e: "Cloak", 0x1f: "Perfume", 0x20: "Throwing",
             0x21: "Bomb", 0x22: "Item", 0x00: "(none)"}
NAME_TYPE = {v: k for k, v in TYPE_NAME.items()}

# Recategorized weapons: id -> (label, vanilla base type, items.json categoryOverride type).
# VERDICT compares the live byte 0x05 to the override (LANDED) vs the base (STOMPED).
RECAT = {
    48: ("Terrastaff (was Battle Axe)", "Axe", "Pole"),
    49: ("Ravager (was Giant's Axe)", "Axe", "KnightSword"),
    50: ("Sunderer (was Slasher)", "Axe", "KnightSword"),
    67: ("Warbrand (was Iron Flail)", "Flail", "Sword"),
    68: ("Bloodlash (was Flail of Flame)", "Flail", "Knife"),
    69: ("Climhazzard (was Morning Star)", "Flail", "NinjaBlade"),
    70: ("Sasori (was Scorpion Tail)", "Flail", "Katana"),
}
# Untouched controls: id -> (label, expected type). Confirm the table base is right.
CONTROL = {
    19: ("Vagabond (native Sword)", "Sword"),
    33: ("Defender (native KnightSword)", "KnightSword"),
    65: ("Zeus Mace (native Flail, NOT overridden)", "Flail"),
}

# Default AOB = the FFHacktics FFT/TIC item-data table head (null record + the first knives).
# byte3=0x80 (Weapon) every 12 bytes, byte5 climbs 00,01,01,01 (none then knives). Prices may
# differ in IC, so the scan keys on the STRUCTURE (see scan_table), with this as a fast first try.
DEFAULT_AOB = "00 00 00 80 00 00 00 00 00 00 00 00 00 01 01 80 01 01 00 00"

# --- kernel32 / psapi RPM (read-only; largest-working-set PID, the dual-proc trap) ---
k32 = C.WinDLL("kernel32", use_last_error=True)
psapi = C.WinDLL("psapi", use_last_error=True)
PV = 0x0010 | 0x0400   # VM_READ | QUERY_INFORMATION


class _PMC(C.Structure):
    _fields_ = [("cb", W.DWORD), ("PageFaultCount", W.DWORD),
                ("PeakWorkingSetSize", C.c_size_t), ("WorkingSetSize", C.c_size_t),
                ("QuotaPeakPagedPoolUsage", C.c_size_t), ("QuotaPagedPoolUsage", C.c_size_t),
                ("QuotaPeakNonPagedPoolUsage", C.c_size_t), ("QuotaNonPagedPoolUsage", C.c_size_t),
                ("PagefileUsage", C.c_size_t), ("PeakPagefileUsage", C.c_size_t)]


def _working_set(pid):
    h = k32.OpenProcess(PV, False, pid)
    if not h:
        return 0
    pmc = _PMC(); pmc.cb = C.sizeof(pmc)
    ok = psapi.GetProcessMemoryInfo(h, C.byref(pmc), pmc.cb)
    k32.CloseHandle(h)
    return pmc.WorkingSetSize if ok else 0


def find_pid(name):
    class PE32(C.Structure):
        _fields_ = [("dwSize", W.DWORD), ("cntUsage", W.DWORD), ("th32ProcessID", W.DWORD),
                    ("th32DefaultHeapID", C.POINTER(C.c_ulong)), ("th32ModuleID", W.DWORD),
                    ("cntThreads", W.DWORD), ("th32ParentProcessID", W.DWORD),
                    ("pcPriClassBase", C.c_long), ("dwFlags", W.DWORD), ("szExeFile", C.c_char * 260)]
    snap = k32.CreateToolhelp32Snapshot(0x2, 0)
    e = PE32(); e.dwSize = C.sizeof(e); matches = []
    if k32.Process32First(snap, C.byref(e)):
        while True:
            if name.lower() in e.szExeFile.decode(errors="ignore").lower():
                matches.append(e.th32ProcessID)
            if not k32.Process32Next(snap, C.byref(e)):
                break
    k32.CloseHandle(snap)
    return max(matches, key=_working_set) if matches else None


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
    """Yield (base, size) for committed, readable, non-guard regions."""
    MEM_COMMIT = 0x1000
    READABLE = 0x02 | 0x04 | 0x20 | 0x40  # R, RW, ExR, ExRW
    PAGE_GUARD = 0x100
    addr = 0
    mbi = _MBI()
    while addr < 0x7FFFFFFFFFFF:
        if not k32.VirtualQueryEx(h, C.c_void_p(addr), C.byref(mbi), C.sizeof(mbi)):
            break
        base = mbi.BaseAddress or 0
        size = mbi.RegionSize or 0x1000
        if (mbi.State == MEM_COMMIT and (mbi.Protect & READABLE)
                and not (mbi.Protect & PAGE_GUARD)):
            yield base, size
        addr = base + size


def looks_like_table(h, base):
    """A real ItemData base: byte3 has the Weapon bit and byte5 is a valid weapon type for the
    first several knife ids, and types stay in-range across the weapon block. Rejects the many
    coincidental AOB hits that aren't the table."""
    blob = rd(h, base, REC * 70)
    if not blob:
        return False
    # ids 1..10 are the vanilla knives -> Weapon flag set, type == Knife(1)
    knife_ok = sum(1 for i in range(1, 11)
                   if (blob[i * REC + T_FLAGS] & 0x80) and blob[i * REC + T_TYPE] == NAME_TYPE["Knife"])
    # across ids 1..63 every weapon-flagged row must carry a known type id
    sane = all((blob[i * REC + T_TYPE] in TYPE_NAME) for i in range(1, 64))
    return knife_ok >= 7 and sane


def scan_table(h, aob_hex):
    sig = bytes(int(x, 16) for x in aob_hex.split())
    n = len(sig)
    for base, size in iter_regions(h):
        if size > 64 * 1024 * 1024:   # the item table lives in a normal data region, not a huge heap
            continue
        blob = rd(h, base, size)
        if not blob:
            continue
        off = blob.find(sig)
        while off != -1:
            cand = base + off
            if looks_like_table(h, cand):
                return cand
            off = blob.find(sig, off + 1)
    return None


def type_at(h, base, iid):
    b = rd(h, base + iid * REC, REC)
    if not b:
        return None
    return {"type": b[T_TYPE], "flags": b[T_FLAGS], "sprite": b[T_SPRITE]}


def main():
    argv = sys.argv[1:]
    base = None
    aob = DEFAULT_AOB
    dump = None
    if "--base" in argv:
        base = int(argv[argv.index("--base") + 1], 16)
    if "--aob" in argv:
        aob = argv[argv.index("--aob") + 1]
    if "--dump" in argv:
        i = argv.index("--dump")
        dump = (int(argv[i + 1]), int(argv[i + 2]))

    pid = find_pid(PROC)
    if not pid:
        print(f"{PROC} not running."); return
    h = k32.OpenProcess(PV, False, pid)
    if not h:
        print(f"OpenProcess failed (err {C.get_last_error()})."); return
    print(f"pid {pid} (largest working set)")

    if base is None:
        print(f"scanning for ItemData table (aob: {aob}) ...")
        base = scan_table(h, aob)
        if base is None:
            print("TABLE NOT FOUND. Try a custom --aob from a known item record, or --base 0xADDR.")
            print("Tip: ids 1-10 are knives (type 01); dump a candidate with --dump 0 12.")
            return
    print(f"ItemData table base: 0x{base:X}\n")

    if dump:
        lo, hi = dump
        for iid in range(lo, hi + 1):
            r = type_at(h, base, iid)
            if r:
                t = TYPE_NAME.get(r["type"], f"0x{r['type']:02X}")
                wpn = "W" if r["flags"] & 0x80 else " "
                print(f"  id {iid:>3} [{wpn}] type={t:<12} sprite={r['sprite']}")
        return

    print("CONTROLS (untouched -- must match expected; else the table base is wrong):")
    base_ok = True
    for iid, (label, exp) in CONTROL.items():
        r = type_at(h, base, iid)
        got = TYPE_NAME.get(r["type"], f"0x{r['type']:02X}") if r else "READ-FAIL"
        ok = r and r["type"] == NAME_TYPE.get(exp)
        base_ok = base_ok and ok
        print(f"  id {iid:>3} {label:<42} type={got:<12} expect={exp:<12} {'OK' if ok else 'MISMATCH'}")
    print()

    print("RECATEGORIZED (the verdict):")
    for iid, (label, vanilla, override) in RECAT.items():
        r = type_at(h, base, iid)
        if not r:
            print(f"  id {iid:>3} {label:<34} READ-FAIL"); continue
        got_id = r["type"]
        got = TYPE_NAME.get(got_id, f"0x{got_id:02X}")
        if got_id == NAME_TYPE.get(override):
            verdict = f"OVERRIDE LANDED (={override})"
        elif got_id == NAME_TYPE.get(vanilla):
            verdict = f"STOMPED (still {vanilla}; our <ItemCategory>{override}> never reached byte 0x05)"
        else:
            verdict = f"UNEXPECTED (neither {override} nor {vanilla})"
        print(f"  id {iid:>3} {label:<34} type={got:<12} sprite={r['sprite']:<3} {verdict}")

    print()
    if not base_ok:
        print("WARNING: a control mismatched -- the located base may be wrong; trust the verdict only "
              "once all controls read OK.")
    else:
        print("Controls all OK -> base is trustworthy. If recat rows read STOMPED, Bug B = a "
              "write-that-never-lands: fix the source the engine actually reads, not ItemData.xml.")
    k32.CloseHandle(h)


if __name__ == "__main__":
    main()
