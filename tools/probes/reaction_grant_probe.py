#!/usr/bin/env python
"""
Reaction-GRANT probe -- does OR-setting a reaction bit on a LIVE unit make that
reaction actually FIRE, the way OR-setting a support bit makes Concentration take
effect? Specifically: grant Reflexes (ability.en Key 449) and watch it dodge.

THIS IS A SPIKE, NOT A SHIP. The bit-set/hold/restore is mechanical and safe
(RPM/WPM only, cannot crash the game). The ONE thing this probe CANNOT do is judge
the result -- only PATRICK'S EYES can: after `grant`, attack the unit and watch for
the Reflexes parry/evade ("greatly increases physical and magickal parry and evasion
rates"). Many misses in a row = honored; gets hit normally = build-time-only dead end.

WHY THERE'S A REAL CHANCE (and why it's still unproven):
  PROVEN (LIVE_LEDGER 2026-06-09, cripple_probe.py): hold-ZEROING the reaction
  bitfield at combat +0x94 SUPPRESSES Counter at trigger time -- the engine reads the
  LIVE bitfield when a unit is hit, not a build-time copy. That is the suppress
  direction. GRANT is the symmetric claim (OR-set a bit the unit lacks and have the
  engine honor it at the same trigger-time read) and is NOT in the PROVEN ledger.
  Support-grant IS proven symmetric (Concentration via +0x98); this probe tests
  whether reactions behave like supports. That gap is exactly what you're firing.

ENCODING (Offsets.cs + Signatures.SupportBit, same scheme, MSB-first):
  reaction bitfield = combat +0x94 (4 bytes, base id 166) == band +0x78.
  RSM id space = ability.en Key - 256.  Reflexes 449 -> 193.
    pos  = 193 - 166 = 27
    byte = 27 // 8   = 3        (band +0x7B / combat +0x97)
    bit  = 27 %  8   = 3        mask = 0x80 >> 3 = 0x10
  cross-checks: Counter 442->186 = byte2 0x08; First Strike 453->197 = byte3 0x01.

LOCATE: blind-walk the authoritative combat band (poison_probe.bandscan) for
unit-shaped structs and fingerprint by (BRAVE, FAITH) -- the durable roster-matched
keys, NOT the hover/condensed struct (a known trap: it follows the cursor). The band
base bandscan returns = combat base + 0x1C, so reaction sits at band +0x78. We do NOT
touch the static array (it freezes on restart AND its pre-1.5 base is out of the
probes' scan window on the current build); the band scan covers the live region.

USAGE (game running, in a LIVE battle):
  python ct_probe.py dump            # NOTE: may show nothing on 1.5 (static-array base
                                     #   moved); use THIS probe's `dump` instead.
  python reaction_grant_probe.py dump [brave faith]
        # list every band unit's reaction bits decoded by name; with a fingerprint,
        # focuses one unit. Pick a target that does NOT already have Reflexes.
  python reaction_grant_probe.py grant <brave> <faith> [id=193] [seconds=180]
        # OR-set + HOLD the bit on the AUTH band copy (30ms). Prints before/after and
        # the exact REVERT command. THEN: attack that unit and watch for the dodge.
  python reaction_grant_probe.py restore <brave> <faith> [id=193]
        # manual one-shot CLEAR of our bit (if a hard-killed `grant` skipped its
        # finally-restore -- mirrors restore_counter.py for cripple).

DON'T restart the battle mid-probe (the band relocates; just re-run). The grant only
ever sets ONE bit and restores it on exit; it never writes a stat or a wide span.
"""
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ct_probe import PROC, PV, PV_W, k32, rd, u16, wr
from poison_probe import bandscan

import ctypes as C
from ctypes import wintypes as W

# --- reaction bitfield (band-relative; == combat +0x94 - BandEntry 0x1C) ---
REACT_OFF = 0x94 - 0x1C        # 0x78
REACT_N = 4
REACT_BASE = 166               # bit 0 of the reaction field
REFLEXES_RSM = 449 - 256       # 193  (ability.en Key - 256)

# band unit-copy offsets (static-array A* layout; bandscan returns this base)
B_LVL, B_BRAVE, B_FAITH, B_HP, B_MHP, B_GX, B_GY = 0x0D, 0x0E, 0x10, 0x14, 0x16, 0x33, 0x34

# RSM id -> name (ability.en Key - 256). Source: ability.sqlite query 2026-06-27 +
# reaction-suppression-cripple memory. Used only to label a dump; not load-bearing.
REACT_NAMES = {
    180: "Counter Tackle", 185: "Auto-Potion", 186: "Counter",
    193: "Reflexes", 197: "First Strike",
}


def bit_for(rsm_id):
    """(byte_off, mask) of an RSM id in the MSB-first reaction field (base 166)."""
    pos = rsm_id - REACT_BASE
    return pos // 8, 0x80 >> (pos % 8)


def in_field(rsm_id):
    pos = rsm_id - REACT_BASE
    return 0 <= pos < REACT_N * 8


def decode(field):
    """MSB-first 4-byte reaction field -> [rsm ids set] (mirrors Signatures/cripple_probe)."""
    ids = []
    for byte_off in range(REACT_N):
        for bit in range(8):
            if field[byte_off] & (0x80 >> bit):
                ids.append(REACT_BASE + byte_off * 8 + bit)
    return ids


def name_of(rsm_id):
    return REACT_NAMES.get(rsm_id, f"id{rsm_id}")


# --- pid selection: target the LARGEST-working-set FFT_enhanced (dual-gun memory trap:
#     two procs exist; the first match is often the wrong/idle one). Falls back to
#     "most band units found" if working-set query fails. ---
psapi = C.WinDLL("psapi", use_last_error=True)


class PMC(C.Structure):
    _fields_ = [("cb", W.DWORD), ("PageFaultCount", W.DWORD),
                ("PeakWorkingSetSize", C.c_size_t), ("WorkingSetSize", C.c_size_t),
                ("QuotaPeakPagedPoolUsage", C.c_size_t), ("QuotaPagedPoolUsage", C.c_size_t),
                ("QuotaPeakNonPagedPoolUsage", C.c_size_t), ("QuotaNonPagedPoolUsage", C.c_size_t),
                ("PagefileUsage", C.c_size_t), ("PeakPagefileUsage", C.c_size_t)]


def _all_pids(name):
    class PE32(C.Structure):
        _fields_ = [("dwSize", W.DWORD), ("cntUsage", W.DWORD), ("th32ProcessID", W.DWORD),
                    ("th32DefaultHeapID", C.POINTER(C.c_ulong)), ("th32ModuleID", W.DWORD),
                    ("cntThreads", W.DWORD), ("th32ParentProcessID", W.DWORD),
                    ("pcPriClassBase", C.c_long), ("dwFlags", W.DWORD), ("szExeFile", C.c_char * 260)]
    snap = k32.CreateToolhelp32Snapshot(0x2, 0)
    e = PE32(); e.dwSize = C.sizeof(e); pids = []
    if k32.Process32First(snap, C.byref(e)):
        while True:
            if name.lower() in e.szExeFile.decode(errors="ignore").lower():
                pids.append(e.th32ProcessID)
            if not k32.Process32Next(snap, C.byref(e)):
                break
    k32.CloseHandle(snap)
    return pids


def _working_set(pid):
    h = k32.OpenProcess(0x0400 | 0x0010, False, pid)   # QUERY_INFORMATION | VM_READ
    if not h:
        return 0
    try:
        pmc = PMC(); pmc.cb = C.sizeof(pmc)
        if psapi.GetProcessMemoryInfo(h, C.byref(pmc), pmc.cb):
            return pmc.WorkingSetSize
        return 0
    finally:
        k32.CloseHandle(h)


def find_pid_largest():
    pids = _all_pids(PROC)
    if not pids:
        return None
    if len(pids) == 1:
        return pids[0]
    best = max(pids, key=_working_set)
    print(f"  {len(pids)} {PROC} procs {pids}; using largest working set -> pid {best}")
    return best


def locate(h, brave, faith):
    """Return the band base of the live unit matching (brave, faith). Prefers a
    real-position copy (gx|gy != 0) over a frozen (0,0) twin; among reals, the one
    with the most HP. None if no match."""
    best = None
    for addr, (mhp, lvl, br, fa), hp, _mhp in bandscan(h):
        if br != brave or fa != faith:
            continue
        b = rd(h, addr, 0x40)
        real = bool(b) and (b[B_GX] != 0 or b[B_GY] != 0)
        score = (1 if real else 0, hp)
        if best is None or score > best[0]:
            best = (score, addr)
    return best[1] if best else None


def band_ok(h, addr, brave, faith):
    b = rd(h, addr, 0x18)
    return bool(b) and b[B_BRAVE] == brave and b[B_FAITH] == faith


def _dump_one(h, addr, brave, faith):
    b = rd(h, addr, REACT_OFF + REACT_N)
    if not b:
        print(f"  @{addr:012X} unreadable")
        return
    field = b[REACT_OFF:REACT_OFF + REACT_N]
    ids = decode(field)
    hp, mhp, lvl = u16(b, B_HP), u16(b, B_MHP), b[B_LVL]
    hexf = " ".join(f"{x:02X}" for x in field)
    named = ", ".join(f"{i}={name_of(i)}" for i in ids) or "(none)"
    print(f"  @{addr:012X} br/fa={brave}/{faith} lvl={lvl} hp={hp}/{mhp}  "
          f"react[+0x{REACT_OFF:02X}]={hexf}  set: {named}")
    bo, mask = bit_for(REFLEXES_RSM)
    has = bool(field[bo] & mask)
    print(f"      Reflexes(193) -> react byte{bo} mask 0x{mask:02X} (band +0x{REACT_OFF + bo:02X}): "
          f"{'ALREADY SET (pick another target)' if has else 'clear -- good grant target'}")


def cmd_dump(h, brave, faith):
    units = bandscan(h)
    if brave is not None:
        addr = locate(h, brave, faith)
        if addr is None:
            print(f"no band unit with brave/faith = {brave}/{faith} "
                  f"({len(units)} units scanned). Run `dump` with no args to list them.")
            return
        _dump_one(h, addr, brave, faith)
        return
    print(f"{len(units)} unit-shaped band structs. reaction bits +0x{REACT_OFF:02X} (base id 166):\n")
    seen = set()
    for addr, (mhp, lvl, br, fa), hp, _ in sorted(units, key=lambda u: (u[1][2], u[1][3])):
        if (br, fa) in seen:
            continue
        seen.add((br, fa))
        _dump_one(h, addr, br, fa)


def _hold(h, brave, faith, rsm_id, seconds):
    if not in_field(rsm_id):
        print(f"id {rsm_id} is outside the reaction field (166..{166 + REACT_N * 8 - 1}); aborting.")
        return
    byte_off, mask = bit_for(rsm_id)
    addr = locate(h, brave, faith)
    if addr is None:
        print(f"no band unit with brave/faith = {brave}/{faith}; is the battle live? `dump` to list.")
        return
    base = rd(h, addr + REACT_OFF, REACT_N)
    if base is None:
        print("can't read the reaction field; aborting.")
        return
    orig_set = bool(base[byte_off] & mask)
    print(f"target @{addr:012X} br/fa={brave}/{faith}")
    print(f"reaction +0x{REACT_OFF:02X}: {' '.join(f'{x:02X}' for x in base)}  "
          f"(set: {', '.join(name_of(i) for i in decode(base)) or 'none'})")
    if orig_set:
        print(f"WARNING: this unit ALREADY has {name_of(rsm_id)} (id {rsm_id}). The grant proves "
              f"nothing -- pick a unit WITHOUT it (`dump` to find one).")
    print(f"\nGRANTING {name_of(rsm_id)} (id {rsm_id}: react byte{byte_off} mask 0x{mask:02X}, "
          f"band +0x{REACT_OFF + byte_off:02X}) and HOLDING for {seconds:.0f}s.")
    print(f"NOW ATTACK THIS UNIT and watch for the dodge/parry. Reflexes = many evades in a row.")
    print(f"REVERT (if this probe is hard-killed): "
          f"python reaction_grant_probe.py restore {brave} {faith} {rsm_id}\n")
    reasserts = 0
    t0 = time.time()
    last_s = -1
    try:
        while time.time() - t0 < seconds:
            if not band_ok(h, addr, brave, faith):
                na = locate(h, brave, faith)
                if na is None:
                    time.sleep(0.1)
                    continue
                addr = na
            cur = rd(h, addr + REACT_OFF + byte_off, 1)
            if cur is not None and not (cur[0] & mask):
                wr(h, addr + REACT_OFF + byte_off, bytes([cur[0] | mask]))
                reasserts += 1
            s = int(time.time() - t0)
            if s != last_s:
                last_s = s
                hpb = rd(h, addr + B_HP, 2)
                hp = u16(hpb, 0) if hpb else -1
                print(f"  t={s:>3}s  hp={hp:>4}  engine-reasserts={reasserts}")
            time.sleep(0.03)
    except KeyboardInterrupt:
        print("(interrupted)")
    finally:
        if not orig_set:
            cur = rd(h, addr + REACT_OFF + byte_off, 1)
            if cur is not None and (cur[0] & mask):
                wr(h, addr + REACT_OFF + byte_off, bytes([cur[0] & ~mask]))
            print(f"restored reaction byte{byte_off} (cleared id {rsm_id}).")
        else:
            print(f"left id {rsm_id} set (it was already on this unit).")
    print(f"done. engine re-asserted {reasserts}x "
          f"(0 = the bit is ours to hold; many = a normalize source rewrites it -- the hold "
          f"still wins IF the reaction fired while held). VERDICT IS YOUR EYES, not this number.")


def cmd_restore(h, brave, faith, rsm_id):
    byte_off, mask = bit_for(rsm_id)
    addr = locate(h, brave, faith)
    if addr is None:
        print(f"no band unit with brave/faith = {brave}/{faith}.")
        return
    cur = rd(h, addr + REACT_OFF + byte_off, 1)
    if cur is None:
        print("can't read the byte.")
        return
    if cur[0] & mask:
        wr(h, addr + REACT_OFF + byte_off, bytes([cur[0] & ~mask]))
        print(f"cleared {name_of(rsm_id)} (id {rsm_id}) on @{addr:012X}.")
    else:
        print(f"{name_of(rsm_id)} (id {rsm_id}) is not set on @{addr:012X}; nothing to do.")


def main():
    a = sys.argv
    mode = a[1] if len(a) > 1 else ""
    if mode not in ("dump", "grant", "restore"):
        print(__doc__)
        return
    pid = find_pid_largest()
    if not pid:
        print(f"{PROC} not running")
        return
    need_write = mode in ("grant", "restore")
    h = k32.OpenProcess(PV_W if need_write else PV, False, pid)
    if not h:
        print(f"OpenProcess failed err={C.get_last_error()}")
        return
    try:
        if mode == "dump":
            brave = int(a[2]) if len(a) > 2 else None
            faith = int(a[3]) if len(a) > 3 else None
            cmd_dump(h, brave, faith)
        elif mode == "grant":
            brave, faith = int(a[2]), int(a[3])
            rsm_id = int(a[4]) if len(a) > 4 else REFLEXES_RSM
            secs = float(a[5]) if len(a) > 5 else 180
            _hold(h, brave, faith, rsm_id, secs)
        else:
            brave, faith = int(a[2]), int(a[3])
            rsm_id = int(a[4]) if len(a) > 4 else REFLEXES_RSM
            cmd_restore(h, brave, faith, rsm_id)
    finally:
        k32.CloseHandle(h)


if __name__ == "__main__":
    main()
