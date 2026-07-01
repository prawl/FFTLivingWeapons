#!/usr/bin/env python
"""
Warbrand damage-type probe. Settle: what makes recat flail id 67 roll Rand(1..PA)*WP
instead of deterministic PA*WP -- and is there a holdable byte that flips it?

BACKGROUND (2026-07-01). The rebalance recategorizes Iron Flail (global id 67) -> Sword
via items.json `categoryOverride`, which writes ItemData byte 0x05 = ITEM TYPE. item_type_probe
already confirmed that write LANDS: id 67 reads Sword (0x03) at ItemData+0x05 live, so equip
class / weapon-skill access / swing MOTION are all correct. BUT damage is still swingy
(Rand(1..PA)*WP, the flail curve) even after swapping the XML <Formula> 1->2. So the damage
routine does NOT read ItemData+0x05 -- it reads the weapon's type from somewhere else that our
override never reached.

FINDINGS (live run 2026-07-01, both suspects DEAD -- kept here as the record):

  (A) A SEPARATE type byte in the ItemWeaponData *stats* record. RE journals GUESSED the layout
      as [0]=RANGE [1]=type/category? [2]=FORMULA [3]=0xFF [4]=WP [5]=EVADE [6]=ELEM [7]=onHit.
      DISPROVEN: byte[1] is ATTACKFLAGS, not type. Native swords read 0x8C
      (Striking|TwoSwords|TwoHands); id 67 reads 0x8E; recat axes read 0x87 -- all have the 0x80
      flag bit and none map to the type enum (0x01-0x22). Every byte of the 8-byte record is
      accounted for (range/attackflags/formula/0xFF/wp/evade/elem/onhit); there is NO type byte
      to hold. So the damage type source is baked to the item id/sprite, not any data byte.

  (B) A stale SECOND copy of the ItemData table. DISPROVEN: the AOB scan finds exactly ONE base.

WHAT'S LEFT is the FORMULA byte (stats offset 2), the one live-writable lever. Native
deterministic swords (Broadsword 19 / Arcanum 30) use FORMULA 1; id 67 was switched 1->2 by a
prior session on a (likely mistaken) "formula 1 == Rand" read. The `241/241/315` numbers logged
under formula 1 are NOT multiples of WP(15) -> they were never Rand(1..PA)*WP; they look like a
deterministic hit + a crit. Test it live with NO restart: `--hold 2=1` reverts id 67 to formula
1 in memory; swing Warbrand and watch. Deterministic ~PA*WP -> the 1->2 swap was the regression,
just revert it in items.json. Still swingy -> formula 1 is type-branching to Rand (id-baked),
and the real fix is relocate-onto-a-native-sword-id or a damage code-cave hook (like the art hook).

USAGE (game running; the item tables are resident on the title screen or in a save -- no battle
needed to READ; you need a unit swinging Warbrand to see the DAMAGE effect of a --hold):
  python warbrand_damage_probe.py                     # locate tables, dump + auto-diff, recommend
  python warbrand_damage_probe.py --itemdata-base 0xADDR   # skip the ItemData AOB scan
  python warbrand_damage_probe.py --stat-base 0xADDR --stride N   # override stats table guess
  python warbrand_damage_probe.py --hold 1=3          # HOLD id 67 stats byte[1]=0x03 (Sword); swing & watch
  python warbrand_damage_probe.py --hold 1=3 5=... --id 67        # hold multiple offsets on any id
  python warbrand_damage_probe.py --hold-itemdata 5=3            # HOLD id 67 ItemData+0x05=Sword in ALL found tables
Ctrl-C stops a hold and RESTORES the original byte(s). Reads are always safe; writes are
VirtualProtectEx-guarded cross-process (an AV cannot occur in our address space).
Env: FFT_PID overrides the auto-picked pid (largest-working-set FFT_enhanced.exe).
"""
import ctypes as C
import os
import sys
import time
import xml.etree.ElementTree as ET
from ctypes import wintypes as W

PROC = "FFT_enhanced"

# --- ItemData table (12-byte records; the one item_type_probe locates) ---
REC = 12
T_SPRITE = 0x01
T_FLAGS = 0x03        # 0x80 = Weapon
T_SECOND = 0x04       # SecondTableId -> index into the ItemWeaponData stats table
T_TYPE = 0x05         # ITEM TYPE (Sword=03, Flail=09, ...)

TYPE_NAME = {0x01: "Knife", 0x02: "NinjaBlade", 0x03: "Sword", 0x04: "KnightSword",
             0x05: "Katana", 0x06: "Axe", 0x07: "Rod", 0x08: "Staff", 0x09: "Flail",
             0x0a: "Gun", 0x0b: "Crossbow", 0x0c: "Bow", 0x0d: "Instrument", 0x0e: "Book",
             0x0f: "Polearm", 0x10: "Pole", 0x11: "Bag", 0x12: "Cloth", 0x00: "(none)"}
NAME_TYPE = {v: k for k, v in TYPE_NAME.items()}
DEFAULT_AOB = "00 00 00 80 00 00 00 00 00 00 00 00 00 01 01 80 01 01 00 00"

# --- ItemWeaponData stats table (RE journals: 0x14080F690 + secondId*8) ---
STAT_BASE = 0x14080F690
STRIDE = 8
# decoded offsets within a stats record. byte[1] was a SUSPECTED type byte; the live run proved
# it is ATTACKFLAGS (0x8C=Striking|TwoSwords|TwoHands on swords). There is NO type byte here.
S_RANGE, S_AFLAGS, S_FORMULA, S_CONST, S_WP, S_EVADE, S_ELEM, S_OPT = 0, 1, 2, 3, 4, 5, 6, 7

TARGET = 67  # Warbrand (was Iron Flail)
# id -> label; native swords + flails frame the sword-vs-flail diff, recats are the subjects.
PROBES = {
    19: "Broadsword (native Sword)",
    30: "Arcanum (native Sword, Runeblade)",
    33: "Defender (native KnightSword)",
    65: "Zeus Mace (native Flail, NOT recat)",
    48: "Terrastaff (recat Axe->Pole)",
    49: "Ravager (recat Axe->KnightSword)",
    50: "Sunderer (recat Axe->KnightSword)",
    67: "Warbrand (recat Flail->Sword) <== TARGET",
    68: "Bloodlash (recat Flail->Knife)",
    69: "Climhazzard (recat Flail->NinjaBlade)",
    70: "Sasori (recat Flail->Katana)",
}
NATIVE_SWORDS = (19, 30)   # to learn the 'Sword' value of the stats type byte
NATIVE_FLAILS = (65,)      # to learn the 'Flail' value

# ---------------- kernel32 RPM/WPM (guarded) ----------------
k32 = C.WinDLL("kernel32", use_last_error=True)
PV = 0x0010 | 0x0400                    # VM_READ | QUERY_INFORMATION
PW = 0x0008 | 0x0010 | 0x0020 | 0x0400  # VM_OPERATION|VM_READ|VM_WRITE|QUERY_INFORMATION
PAGE_RW = 0x04
k32.OpenProcess.restype = W.HANDLE
k32.OpenProcess.argtypes = [W.DWORD, W.BOOL, W.DWORD]
k32.VirtualProtectEx.argtypes = [W.HANDLE, W.LPVOID, C.c_size_t, W.DWORD, C.POINTER(W.DWORD)]


class _PMC(C.Structure):
    _fields_ = [("cb", W.DWORD), ("PageFaultCount", W.DWORD),
                ("PeakWorkingSetSize", C.c_size_t), ("WorkingSetSize", C.c_size_t),
                ("QuotaPeakPagedPoolUsage", C.c_size_t), ("QuotaPagedPoolUsage", C.c_size_t),
                ("QuotaPeakNonPagedPoolUsage", C.c_size_t), ("QuotaNonPagedPoolUsage", C.c_size_t),
                ("PagefileUsage", C.c_size_t), ("PeakPagefileUsage", C.c_size_t)]


class _MBI(C.Structure):
    _fields_ = [("BaseAddress", C.c_void_p), ("AllocationBase", C.c_void_p),
                ("AllocationProtect", W.DWORD), ("RegionSize", C.c_size_t),
                ("State", W.DWORD), ("Protect", W.DWORD), ("Type", W.DWORD)]


def _working_set(pid):
    psapi = C.WinDLL("psapi", use_last_error=True)
    h = k32.OpenProcess(PV, False, pid)
    if not h:
        return 0
    pmc = _PMC(); pmc.cb = C.sizeof(pmc)
    ok = psapi.GetProcessMemoryInfo(h, C.byref(pmc), pmc.cb)
    k32.CloseHandle(h)
    return pmc.WorkingSetSize if ok else 0


def find_pid(name):
    if os.environ.get("FFT_PID"):
        return int(os.environ["FFT_PID"])

    class PE32(C.Structure):
        _fields_ = [("dwSize", W.DWORD), ("cntUsage", W.DWORD), ("th32ProcessID", W.DWORD),
                    ("th32DefaultHeapID", C.POINTER(C.c_ulong)), ("th32ModuleID", W.DWORD),
                    ("cntThreads", W.DWORD), ("th32ParentProcessID", W.DWORD),
                    ("pcPriClassBase", C.c_long), ("dwFlags", W.DWORD),
                    ("szExeFile", C.c_char * 260)]
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


def rd(h, a, n):
    b = (C.c_ubyte * n)(); g = C.c_size_t(0)
    if k32.ReadProcessMemory(h, C.c_void_p(a), b, n, C.byref(g)) and g.value == n:
        return bytes(b)
    return None


def wr_guarded(h, a, data):
    """VirtualProtectEx(PAGE_READWRITE) -> WriteProcessMemory -> restore. Data page, no flush."""
    n = len(data)
    old = W.DWORD(0)
    if not k32.VirtualProtectEx(h, C.c_void_p(a), n, PAGE_RW, C.byref(old)):
        return False
    try:
        buf = (C.c_ubyte * n)(*data); w = C.c_size_t(0)
        ok = bool(k32.WriteProcessMemory(h, C.c_void_p(a), buf, n, C.byref(w))) and w.value == n
    finally:
        tmp = W.DWORD(0)
        k32.VirtualProtectEx(h, C.c_void_p(a), n, old.value, C.byref(tmp))
    return ok


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


def looks_like_itemdata(h, base):
    blob = rd(h, base, REC * 70)
    if not blob:
        return False
    knife_ok = sum(1 for i in range(1, 11)
                   if (blob[i * REC + T_FLAGS] & 0x80) and blob[i * REC + T_TYPE] == NAME_TYPE["Knife"])
    sane = all(blob[i * REC + T_TYPE] in TYPE_NAME for i in range(1, 64))
    return knife_ok >= 7 and sane


def scan_itemdata_all(h, aob_hex):
    """Yield EVERY ItemData base that passes the structural check (Suspect B: dual tables)."""
    sig = bytes(int(x, 16) for x in aob_hex.split())
    found = []
    for base, size in iter_regions(h):
        if size > 64 * 1024 * 1024:
            continue
        blob = rd(h, base, size)
        if not blob:
            continue
        off = blob.find(sig)
        while off != -1:
            cand = base + off
            if looks_like_itemdata(h, cand) and cand not in found:
                found.append(cand)
            off = blob.find(sig, off + 1)
    return found


# ---------------- stats-table validation against the shipped XML ----------------
def load_expected_stats():
    here = os.path.dirname(os.path.abspath(__file__))
    xml = os.path.join(here, "..", "..", "mod", "FFTIVC", "tables", "enhanced", "ItemWeaponData.xml")
    exp = {}
    try:
        root = ET.parse(xml).getroot()
    except Exception as e:
        print(f"  (could not parse {xml}: {e})")
        return exp
    for w in root.iter("ItemWeapon"):
        def g(tag, d=0):
            t = w.find(tag)
            return int(t.text) if t is not None and t.text and t.text.strip().isdigit() else d
        iid = g("Id", -1)
        if iid >= 0:
            exp[iid] = {"range": g("Range"), "formula": g("Formula"),
                        "wp": g("Power"), "evade": g("Evasion")}
    return exp


def second_id(h, itemdata_base, iid):
    r = rd(h, itemdata_base + iid * REC, REC)
    return r[T_SECOND] if r else None


def stat_rec(h, base, stride, sid, n=16):
    return rd(h, base + sid * stride, n)


def validate_stats(h, itemdata_base, base, stride, exp):
    """Score how many ids' live stats record (at guessed base/stride/layout) matches the XML."""
    hits = tot = 0
    for iid, e in exp.items():
        sid = second_id(h, itemdata_base, iid)
        if sid is None:
            continue
        rec = stat_rec(h, base, stride, sid, stride)
        if not rec or len(rec) < 8:
            continue
        tot += 1
        if (rec[S_RANGE] == e["range"] and rec[S_FORMULA] == e["formula"]
                and rec[S_WP] == e["wp"] and rec[S_EVADE] == e["evade"]):
            hits += 1
    return hits, tot


# ---------------- reporting ----------------
def hx(b):
    return " ".join(f"{x:02X}" for x in b)


def diagnose(h, itemdata_bases, stat_base, stride, exp):
    primary = itemdata_bases[0]

    # Suspect B: do multiple ItemData copies disagree on the target's type?
    if len(itemdata_bases) > 1:
        print(f"** {len(itemdata_bases)} ItemData copies found -- checking Suspect B (stale copy) **")
        for b in itemdata_bases:
            r = rd(h, b + TARGET * REC, REC)
            t = TYPE_NAME.get(r[T_TYPE], f"0x{r[T_TYPE]:02X}") if r else "?"
            print(f"   base 0x{b:X}: id {TARGET} type = {t}")
        types = {rd(h, b + TARGET * REC, REC)[T_TYPE] for b in itemdata_bases}
        if len(types) > 1:
            print("   >>> COPIES DISAGREE. Damage likely reads the Flail copy. "
                  "Try: --hold-itemdata 5=3  (forces byte 0x05=Sword in every copy).")
        print()

    print(f"ItemData base 0x{primary:X}   ItemWeaponData stats base 0x{stat_base:X} stride {stride}")
    print(f"{'id':>3}  {'label':<34} {'IData.type':<12} {'sid':>3}  stats-record (hex, decoded)")
    print("-" * 100)
    recs = {}
    for iid, label in PROBES.items():
        idata = rd(h, primary + iid * REC, REC)
        if not idata:
            print(f"{iid:>3}  {label:<34} READ-FAIL"); continue
        itype = TYPE_NAME.get(idata[T_TYPE], f"0x{idata[T_TYPE]:02X}")
        sid = idata[T_SECOND]
        rec = stat_rec(h, stat_base, stride, sid, stride)
        recs[iid] = rec
        if not rec:
            print(f"{iid:>3}  {label:<34} {itype:<12} {sid:>3}  STATS READ-FAIL"); continue
        dec = (f"rng={rec[S_RANGE]} aflags={rec[S_AFLAGS]:#04x} "
               f"form={rec[S_FORMULA]} wp={rec[S_WP]} ev={rec[S_EVADE]}")
        print(f"{iid:>3}  {label:<34} {itype:<12} {sid:>3}  {hx(rec[:stride])}   {dec}")

    # byte[1] is AttackFlags, not type (proven). The one live-writable lever left is the FORMULA
    # byte. Native deterministic swords use formula 1; recommend live-reverting id 67 to it.
    print()
    print("** No type byte in the stats record (byte[1]=AttackFlags). Live lever = FORMULA (offset 2). **")
    sword_rec = next((recs.get(i) for i in NATIVE_SWORDS if recs.get(i)), None)
    tgt_rec = recs.get(TARGET)
    if sword_rec and tgt_rec:
        print(f"   native Sword formula={sword_rec[S_FORMULA]}  |  id {TARGET} formula={tgt_rec[S_FORMULA]}")
        if tgt_rec[S_FORMULA] != sword_rec[S_FORMULA]:
            print(f"   >>> LIVE TEST (no restart): python warbrand_damage_probe.py "
                  f"--hold {S_FORMULA}={sword_rec[S_FORMULA]}")
            print(f"       then swing Warbrand. Deterministic ~PA*WP -> revert items.json to "
                  f"formula {sword_rec[S_FORMULA]}. Still swingy -> id-baked (relocate / code-hook).")
        else:
            print(f"   id {TARGET} already on the native-sword formula; if still swingy the branch "
                  "is id-baked -> relocate onto a native sword id, or a damage code-cave hook.")
    else:
        print("   could not read both a native-sword and the target stats record.")


def parse_sets(args):
    """'1=3' '5=9' -> [(1,3),(5,9)] (offset=value, both decimal or 0x hex)."""
    out = []
    for a in args:
        o, v = a.split("=")
        out.append((int(o, 0), int(v, 0)))
    return out


def hold_stats(h, stat_base, stride, itemdata_base, iid, sets):
    sid = second_id(h, itemdata_base, iid)
    if sid is None:
        print("could not read SecondTableId for id", iid); return
    addr = stat_base + sid * stride
    orig = rd(h, addr, stride)
    print(f"HOLD id {iid} stats @0x{addr:X} (sid {sid}); original = {hx(orig)}")
    _hold_loop(h, [(addr + o, v, orig[o]) for o, v in sets])


def hold_itemdata(h, itemdata_bases, iid, sets):
    targets = []
    for base in itemdata_bases:
        addr = base + iid * REC
        orig = rd(h, addr, REC)
        for o, v in sets:
            targets.append((addr + o, v, orig[o]))
        print(f"HOLD id {iid} ItemData @0x{addr:X}; original = {hx(orig)}")
    _hold_loop(h, targets)


def _hold_loop(h, targets):
    """targets: list of (abs_addr, new_val, orig_val). Re-write every 100ms; restore on exit."""
    print("writing + holding (Ctrl-C to stop and restore) ...")
    try:
        first = True
        while True:
            for addr, val, _ in targets:
                ok = wr_guarded(h, addr, bytes([val & 0xFF]))
                if first:
                    now = rd(h, addr, 1)
                    print(f"   @0x{addr:X} <- {val:#04x}  ({'OK' if ok and now and now[0] == (val & 0xFF) else 'WRITE-FAIL'})")
            first = False
            time.sleep(0.1)
    except KeyboardInterrupt:
        for addr, _, orig in targets:
            wr_guarded(h, addr, bytes([orig & 0xFF]))
        print("\nrestored originals.")


def main():
    argv = sys.argv[1:]

    def opt(flag):
        return argv[argv.index(flag) + 1] if flag in argv else None

    itemdata_base = int(opt("--itemdata-base"), 16) if opt("--itemdata-base") else None
    stat_base = int(opt("--stat-base"), 16) if opt("--stat-base") else STAT_BASE
    stride = int(opt("--stride")) if opt("--stride") else STRIDE
    iid = int(opt("--id")) if opt("--id") else TARGET

    def collect(flag):
        if flag not in argv:
            return None
        vals = []
        for a in argv[argv.index(flag) + 1:]:
            if a.startswith("--"):
                break
            vals.append(a)
        return vals

    hold = collect("--hold")
    hold_id = collect("--hold-itemdata")

    pid = find_pid(PROC)
    if not pid:
        print(f"{PROC} not running."); return
    h = k32.OpenProcess(PW, False, pid)
    if not h:
        print(f"OpenProcess failed (err {C.get_last_error()}) -- run elevated?"); return
    print(f"pid {pid} (largest working set)\n")

    # locate ItemData (one or many)
    if itemdata_base is not None:
        itemdata_bases = [itemdata_base]
    else:
        print("scanning for ItemData table(s) ...")
        itemdata_bases = scan_itemdata_all(h, DEFAULT_AOB)
        if not itemdata_bases:
            print("ItemData NOT FOUND. Pass --itemdata-base 0xADDR "
                  "(get it from item_type_probe.py)."); k32.CloseHandle(h); return
        print(f"found {len(itemdata_bases)} base(s): " + ", ".join(f"0x{b:X}" for b in itemdata_bases) + "\n")

    if hold:
        hold_stats(h, stat_base, stride, itemdata_bases[0], iid, parse_sets(hold))
    elif hold_id:
        hold_itemdata(h, itemdata_bases, iid, parse_sets(hold_id))
    else:
        # validate the stats table guess before trusting the dump
        exp = load_expected_stats()
        if exp:
            hits, tot = validate_stats(h, itemdata_bases[0], stat_base, stride, exp)
            print(f"stats-table check @0x{stat_base:X} stride {stride}: "
                  f"{hits}/{tot} ids match the shipped XML "
                  f"({'TRUSTWORTHY' if tot and hits >= 0.8 * tot else 'SUSPECT -- try --stat-base/--stride'})\n")
        diagnose(h, itemdata_bases, stat_base, stride, exp)

    k32.CloseHandle(h)


if __name__ == "__main__":
    main()
