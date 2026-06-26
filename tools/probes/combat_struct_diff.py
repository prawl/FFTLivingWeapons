#!/usr/bin/env python
"""
Combat-struct diff (READ-ONLY). Be CERTAIN the recat blade graphic is asset-bound, not a live field.

We proved: item-type override lands (skills/equip/animation fixed); the legacy 2-byte weapon-sprite
table is vestigial in IC (writing it does nothing, nothing reads it); and poking the live equipped
item-id does NOT change the swing model. Remaining possibility to rule out: a per-unit "resolved
weapon graphic/model" field (or pointer) in the in-battle combat struct that differs between a
flail-art weapon and a real sword.

This dumps the per-unit combat/band struct for TWO deployed units -- one holding a recategorized
weapon (default Warbrand id 67, busted), one holding a native sword (default Cleaver id 20, clean) --
and diffs them byte-for-byte. If the ONLY weapon-linked differences are the item-id + derived stats,
the model is asset-bound (the WEAPON_VISUALS wall). If a distinct graphic/model byte or pointer
differs, that is the live lever to chase.

Units are auto-identified: roster slot whose rHand == the target id -> its (level,brave,faith)
fingerprint -> the matching band entry. Deploy BOTH units in the same battle first.

GROUND TRUTH (LivingWeapon/Offsets.cs, 1.5-confirmed):
  ROSTER @0x1411A7D10 stride 0x258: +0x14 rHand u16, +0x1D level, +0x1E brave, +0x1F faith, +0x230 nameId
  BAND base = 0x141855CE0 + 0x1C - 24*0x200, stride 0x200, 49 slots:
    +0x0D level u8, +0x0E brave u8, +0x10 faith u8, +0x14 hp u16, +0x16 maxhp u16, +0x33 gx, +0x34 gy

USAGE (in a battle, both units deployed):
  python combat_struct_diff.py                 # auto: diff Warbrand(67) vs Cleaver(20) combat structs
  python combat_struct_diff.py --ids 67 20     # pick the two weapon item-ids to compare
  python combat_struct_diff.py --list          # just list roster weapons + band fingerprints
  python combat_struct_diff.py --follow 0xPTR   # hexdump 64 bytes at a pointer found in the diff
"""
import ctypes as C
from ctypes import wintypes as W
import sys

PROC = "FFT_enhanced"
ROSTER_BASE = 0x1411A7D10
ROSTER_STRIDE = 0x258
ROSTER_SLOTS = 20
R_RHAND = 0x14
R_LEVEL = 0x1D
R_BRAVE = 0x1E
R_FAITH = 0x1F
R_NAMEID = 0x230
EMPTY = (0x00FF, 0xFFFF)

COMBAT_ANCHOR = 0x141855CE0
COMBAT_STRIDE = 0x200
BAND_ENTRY = 0x1C
BAND_BASE = COMBAT_ANCHOR + BAND_ENTRY - 24 * COMBAT_STRIDE
BAND_SLOTS = 49
A_LEVEL = 0x0D
A_BRAVE = 0x0E
A_FAITH = 0x10
A_HP = 0x14
A_MAXHP = 0x16
A_GX = 0x33
A_GY = 0x34
MAX_DRIFT = 9

# Offsets we already understand, to annotate the diff (so a NEW differing offset stands out).
KNOWN = {0x0D: "level", 0x0E: "brave", 0x10: "faith", 0x14: "hp", 0x16: "maxhp", 0x2A: "origBrave",
         0x2B: "curBrave", 0x2C: "origFaith", 0x2D: "curFaith", 0x33: "gx", 0x34: "gy",
         0x3E: "PA", 0x3F: "MA", 0x40: "Speed", 0x41: "schedCT", 0x45: "statusBits",
         0x48: "status", 0x49: "status", 0x4A: "timer", 0x54: "allegiance", 0x94: "reaction"}

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


def rd(h, a, n):
    b = (C.c_ubyte * n)(); g = C.c_size_t(0)
    if k32.ReadProcessMemory(h, C.c_void_p(a), b, n, C.byref(g)) and g.value == n:
        return bytes(b)
    return None


def u8(h, a):
    b = rd(h, a, 1); return b[0] if b else None


def u16(h, a):
    b = rd(h, a, 2); return (b[0] | (b[1] << 8)) if b else None


def roster_slots(h):
    out = []
    for s in range(ROSTER_SLOTS):
        base = ROSTER_BASE + s * ROSTER_STRIDE
        rh = u16(h, base + R_RHAND)
        if rh is None or rh in EMPTY:
            continue
        out.append({"slot": s, "rhand": rh, "nameId": u16(h, base + R_NAMEID),
                    "lvl": u8(h, base + R_LEVEL), "brv": u8(h, base + R_BRAVE), "fa": u8(h, base + R_FAITH)})
    return out


def band_entries(h):
    out = []
    for i in range(BAND_SLOTS):
        base = BAND_BASE + i * COMBAT_STRIDE
        lvl = u8(h, base + A_LEVEL); mhp = u16(h, base + A_MAXHP); hp = u16(h, base + A_HP)
        if lvl is None or mhp is None or not (1 <= lvl <= 99) or not (1 <= mhp <= 9999) or hp is None or hp > mhp:
            continue
        out.append({"i": i, "base": base, "lvl": lvl, "brv": u8(h, base + A_BRAVE),
                    "fa": u8(h, base + A_FAITH), "hp": hp, "mhp": mhp,
                    "gx": u8(h, base + A_GX), "gy": u8(h, base + A_GY)})
    return out


def match_band(rs, bands):
    """roster fingerprint (lvl/brv/fa, live lvl may exceed roster lvl by <=MAX_DRIFT) -> band entry."""
    best = None
    for b in bands:
        if b["brv"] == rs["brv"] and b["fa"] == rs["fa"] and 0 <= (b["lvl"] - rs["lvl"]) <= MAX_DRIFT:
            if best is None or b["lvl"] < best["lvl"]:
                best = b
    return best


def main():
    argv = sys.argv[1:]
    ids = (67, 20)
    if "--ids" in argv:
        i = argv.index("--ids"); ids = (int(argv[i + 1]), int(argv[i + 2]))

    pid = find_pid(PROC)
    if not pid:
        print(f"{PROC} not running."); return
    h = k32.OpenProcess(PV, False, pid)
    if not h:
        print(f"OpenProcess failed (err {C.get_last_error()})."); return
    print(f"pid {pid}")

    if "--follow" in argv:
        a = int(argv[argv.index("--follow") + 1], 16)
        b = rd(h, a, 64)
        if b:
            for o in range(0, 64, 16):
                print(f"0x{a + o:X}  " + " ".join(f"{x:02X}" for x in b[o:o + 16]))
        else:
            print("read fail")
        return

    rs = roster_slots(h)
    bands = band_entries(h)

    if "--list" in argv or not rs:
        print("ROSTER (occupied):")
        for r in rs:
            print(f"  slot {r['slot']:>2} nameId {r['nameId']:>3} rHand {r['rhand']:>3}  "
                  f"lvl {r['lvl']} brv {r['brv']} fa {r['fa']}")
        print("BAND (valid):")
        for b in bands:
            print(f"  i {b['i']:>2} @0x{b['base']:X} lvl {b['lvl']} brv {b['brv']} fa {b['fa']} "
                  f"hp {b['hp']}/{b['mhp']} pos ({b['gx']},{b['gy']})")
        if "--list" in argv:
            return

    def find_unit(wid):
        cands = [r for r in rs if r["rhand"] == wid]
        for r in cands:
            bnd = match_band(r, bands)
            if bnd:
                return r, bnd
        return (cands[0] if cands else None), None

    ra, ba = find_unit(ids[0])
    rb, bb = find_unit(ids[1])
    print(f"\nUnit A (weapon id {ids[0]}): roster {ra['slot'] if ra else '?'} -> band "
          f"{'i%d @0x%X' % (ba['i'], ba['base']) if ba else 'NOT MATCHED (is it deployed?)'}")
    print(f"Unit B (weapon id {ids[1]}): roster {rb['slot'] if rb else '?'} -> band "
          f"{'i%d @0x%X' % (bb['i'], bb['base']) if bb else 'NOT MATCHED (is it deployed?)'}")
    if not (ba and bb):
        print("\nDeploy BOTH units (one holding each weapon id) in the same battle, then re-run. "
              "Use --list to see what is on the field.")
        return

    da = rd(h, ba["base"], COMBAT_STRIDE)
    db = rd(h, bb["base"], COMBAT_STRIDE)
    if not (da and db):
        print("struct read fail"); return

    print(f"\nDIFF combat structs (0x200 bytes)  A=id{ids[0]}  B=id{ids[1]}  -- offset: A vs B")
    diffs = 0
    for o in range(COMBAT_STRIDE):
        if da[o] != db[o]:
            diffs += 1
            tag = KNOWN.get(o, "")
            print(f"  +0x{o:02X}  {da[o]:3d} (0x{da[o]:02X})  vs  {db[o]:3d} (0x{db[o]:02X})   {tag}")
    print(f"\n{diffs} differing bytes. Differences that are NOT level/brave/faith/hp/stats/status/pos/CT are")
    print("candidates for a weapon graphic/model field. A differing 4/8-byte run at an aligned offset may be a")
    print("POINTER to a per-unit render object -- read it as little-endian and --follow it. If every diff is an")
    print("understood gameplay field, the blade model is asset-bound (not a live field) -> the visuals wall.")
    k32.CloseHandle(h)


if __name__ == "__main__":
    main()
