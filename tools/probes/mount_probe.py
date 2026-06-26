#!/usr/bin/env python
"""
Mount-Info probe (READ-ONLY). Find the IC combat-struct offset of the FFT "Mount Info" byte so we
can force-mount a unit onto a chocobo (or any rideable) and watch the chaos.

FFT has chocobo mounting. PSX layout (FFTHandsFree BATTLE_STATS_PSX_REFERENCE.md) puts a single
"Mount Info" byte at unit offset 0x182, the first of the Unit-State-Flags cluster:
    bit 0x80 = this unit is RIDING (a mount)
    bit 0x40 = this unit is BEING RIDDEN (the chocobo)
    low 6 bits = the LINKED unit's ENTD/battle slot
IC re-laid the unit struct (CWeapon=0x20, CHp=0x30, ... none match PSX), so the IC offset is unknown.
This finds it: while ANY mount is active (an AI enemy riding an enemy chocobo counts), exactly one unit
has bit 0x80 set and one has bit 0x40 set at the Mount Info offset, and every other unit reads 0 there.
We scan EVERY combat-struct offset for that signature; the matching offset(s) is the Mount Info byte.

Combat struct base = CombatAnchor(0x141855CE0) + (slot-24)*0x200 (Offsets.cs). The band-entry framing
this probe shares starts +0x1C into the combat struct, so combat offset 0x182 == band offset 0x166.
We dump from the COMBAT base so reported offsets are directly comparable to the PSX 0x182.

USAGE (game in a battle):
  python mount_probe.py units                 # list the valid combat structs on the field (sanity)
  python mount_probe.py scan                   # WHILE A MOUNT IS ACTIVE: find the 0x80/0x40 pair offset
  python mount_probe.py dump 0x182             # show the byte at a combat offset for every unit
  python mount_probe.py watch 0x182 30         # poll that offset for 30s (watch it set when a mount happens)
"""
import ctypes as C
from ctypes import wintypes as W
import sys
import time

PROC = "FFT_enhanced"
COMBAT_ANCHOR = 0x141855CE0
STRIDE = 0x200
SLOTS = 49
ANCHOR_SLOT = 24            # the anchor sits at scan-slot 24 (Offsets.CombatSearchSlots)
BAND = 0x1C                 # band-entry framing starts +0x1C into the combat struct
# band-relative fingerprint fields (validity), expressed from the COMBAT base (= band - 0x1C... +0x1C):
A_LEVEL = BAND + 0x0D
A_HP = BAND + 0x14
A_MAXHP = BAND + 0x16
A_BRAVE = BAND + 0x0E
A_FAITH = BAND + 0x10

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


def combat_base(slot):
    return COMBAT_ANCHOR + (slot - ANCHOR_SLOT) * STRIDE


def valid_units(h):
    """Return [(slot, base, struct_bytes)] for combat structs that look like a real unit."""
    out = []
    for s in range(SLOTS):
        base = combat_base(s)
        blob = rd(h, base, STRIDE)
        if not blob:
            continue
        lvl = blob[A_LEVEL]; mhp = blob[A_MAXHP] | (blob[A_MAXHP + 1] << 8)
        hp = blob[A_HP] | (blob[A_HP + 1] << 8)
        br, fa = blob[A_BRAVE], blob[A_FAITH]
        if not (1 <= lvl <= 99 and 1 <= mhp < 9999 and hp <= mhp and 1 <= br <= 100 and 1 <= fa <= 100):
            continue
        out.append((s, base, blob))
    return out


def main():
    argv = sys.argv[1:]
    cmd = argv[0] if argv else "units"
    pid = find_pid(PROC)
    if not pid:
        print(f"{PROC} not running."); return
    h = k32.OpenProcess(PV, False, pid)
    if not h:
        print(f"OpenProcess failed (err {C.get_last_error()})."); return
    print(f"pid {pid}")

    units = valid_units(h)
    print(f"{len(units)} valid combat structs on the field")

    if cmd == "units":
        for s, base, b in units:
            mhp = b[A_MAXHP] | (b[A_MAXHP + 1] << 8)
            print(f"  slot {s:>2} @0x{base:X}  lvl {b[A_LEVEL]} brv {b[A_BRAVE]} fa {b[A_FAITH]} maxhp {mhp}")
        return

    if cmd == "dump":
        off = int(argv[1], 16) if argv[1].lower().startswith("0x") else int(argv[1])
        for s, base, b in units:
            v = b[off]
            tag = ""
            if v & 0x80: tag = f"RIDING (slot {v & 0x3F})"
            elif v & 0x40: tag = f"BEING-RIDDEN (slot {v & 0x3F})"
            print(f"  slot {s:>2}  +0x{off:X} = 0x{v:02X} ({v}){'  <- ' + tag if tag else ''}")
        return

    if cmd == "watch":
        off = int(argv[1], 16) if argv[1].lower().startswith("0x") else int(argv[1])
        secs = int(argv[2]) if len(argv) > 2 else 30
        print(f"watching combat +0x{off:X} across {len(units)} units for {secs}s -- trigger a mount now")
        end = time.time() + secs
        while time.time() < end:
            row = []
            for s, base, _ in units:
                b = rd(h, base + off, 1)
                row.append(f"s{s}={b[0]:02X}" if b else f"s{s}=--")
            print("  " + " ".join(row))
            time.sleep(1.0)
        return

    # cmd == "scan": find offsets carrying the mount signature (>=1 rider 0x80 AND >=1 mount 0x40),
    # ignoring 0x00/0xFF noise; the low 6 bits must point at a plausible slot (< 32).
    print("scanning every combat offset for the 0x80(riding)/0x40(ridden) pair -- a MOUNT MUST BE ACTIVE")
    hits = []
    for off in range(STRIDE):
        riders, mounts = [], []
        noisy = False
        for s, base, b in units:
            v = b[off]
            if v in (0x00, 0xFF):
                continue
            if v & 0x80 and (v & 0x3F) < 32:
                riders.append((s, v & 0x3F))
            elif v & 0x40 and (v & 0x3F) < 32:
                mounts.append((s, v & 0x3F))
            else:
                noisy = True
        # A clean Mount Info offset: at least one rider AND one mount, and not a field where lots of
        # units carry unrelated high-bit values (that is some other byte, not the sparse mount link).
        if riders and mounts and (len(riders) + len(mounts)) <= 4:
            hits.append((off, riders, mounts, noisy))
    if not hits:
        print("no 0x80/0x40 pair found. Is a mount ACTIVE right now? (a unit literally sitting on a")
        print("chocobo). Try `watch 0x166`/`watch 0x182` while you trigger one, or `units` to sanity-check.")
        return
    hits.sort(key=lambda x: (x[3], len(x[1]) + len(x[2])))   # cleanest first
    print(f"{len(hits)} candidate offset(s):")
    for off, riders, mounts, noisy in hits[:20]:
        print(f"  +0x{off:X}: riders {riders} mounts {mounts}{'  (other units noisy here)' if noisy else '  CLEAN'}")
    print("\nThe CLEAN offset whose rider's low-bits point at the mount's slot (and vice versa) is Mount Info.")


if __name__ == "__main__":
    main()
