#!/usr/bin/env python
"""
Condensed/turn-queue layout probe (FFT:IC 1.5). Read-only RPM -- cannot crash the game.

WHY: the actor resolver identifies who's acting by filtering BAND entries on the active unit's
(maxHp, hp, level) read from the condensed/turn-queue struct, then maps brave/faith -> roster.
GODMODE holds every player at HP==MaxHP==999, so that (maxHp,hp,level) key collides across the
whole party -> the band walk matches multiple units -> ambiguous -> resolve returns (0,0,0) ->
the actor latch goes stale (every hit mis-credits the last cleanly-resolved weapon, live: [w:89]).
Two real same-level/full-HP party members collide the same way; godmode just makes it permanent.

THE FIX needs an actor key godmode does NOT touch: brave/faith. They are unique per party member
and untouched by the HP hold. The condensed struct only has level/team/nameId/hp/maxHp mapped
today (Offsets.cs) -- brave/faith were never found in it, which is the whole reason the code
round-trips through the HP-keyed band walk. THIS PROBE FINDS THEM.

HOW: run it WITHOUT godmode so the band match is unambiguous and can supply ground-truth
brave/faith. On each active-unit change it (1) reads the condensed (maxHp,hp,level), (2) finds the
UNIQUE band entry matching it, (3) takes that entry's brave/faith as truth, (4) dumps a 0x40-byte
condensed window and records every offset whose byte == brave and every offset whose byte == faith.
Across several different acting units the TRUE offsets are the ones that match on EVERY sample
(printed as the candidate intersection). Those become Offsets.TqBrave / Offsets.TqFaith, and the
resolver reads the fingerprint straight from the condensed struct -- no HP, godmode-proof.

USAGE (game running, live battle, GODMODE OFF, several distinct party members + enemies):
  python tools\\probes\\condensed_fp_probe.py [seconds=180] [hz=20]
Then in-game: take turns with as many DIFFERENT units as you can (different brave/faith). The more
distinct acting units, the tighter the intersection. Read the SUMMARY.
"""
import ctypes as C
from ctypes import wintypes as W
import sys
import time

PROC = "FFT_enhanced"

# --- condensed/turn-queue struct (Offsets.cs TurnQueue) ---
COND = 0x1407832A0
TQ_LEVEL, TQ_TEAM, TQ_NAMEID, TQ_HP, TQ_MAXHP = 0x00, 0x02, 0x04, 0x0C, 0x10
WIN_LEN = 0x140   # widened: brave/faith were NOT in the first 0x40, search further out + for position

# --- band (authoritative live structs; matches battle_cheats.py / Offsets.cs) ---
COMBAT_ANCHOR = 0x141855CE0
BAND_ENTRY    = 0x1C
COMBAT_STRIDE = 0x200
BAND_SLOTS    = 49
BAND_READ_BASE = COMBAT_ANCHOR + BAND_ENTRY - 24 * COMBAT_STRIDE
A_LEVEL, A_BRAVE, A_FAITH, A_HP, A_MAXHP, A_GX, A_GY = 0x0D, 0x0E, 0x10, 0x14, 0x16, 0x33, 0x34

k32 = C.WinDLL("kernel32", use_last_error=True)
PV = 0x0010 | 0x0400   # VM_READ | QUERY_INFORMATION (read-only)


def find_pid(name):
    class PE32(C.Structure):
        _fields_ = [("dwSize", W.DWORD), ("cntUsage", W.DWORD), ("th32ProcessID", W.DWORD),
                    ("th32DefaultHeapID", C.POINTER(C.c_ulong)), ("th32ModuleID", W.DWORD),
                    ("cntThreads", W.DWORD), ("th32ParentProcessID", W.DWORD),
                    ("pcPriClassBase", C.c_long), ("dwFlags", W.DWORD), ("szExeFile", C.c_char * 260)]
    snap = k32.CreateToolhelp32Snapshot(0x2, 0)
    e = PE32(); e.dwSize = C.sizeof(e); pid = None
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


def u8(h, a):
    b = rd(h, a, 1); return b[0] if b else -1


def u16(h, a):
    b = rd(h, a, 2); return (b[0] | (b[1] << 8)) if b else -1


def band_entry(s):
    return BAND_READ_BASE + s * COMBAT_STRIDE


def valid_entry(h, e):
    mhp = u16(h, e + A_MAXHP)
    lvl = u8(h, e + A_LEVEL)
    return mhp is not None and 0 < mhp < 2000 and 1 <= lvl <= 99


def find_unique_band(h, maxhp, hp, level):
    """The single band entry matching (maxHp, hp, level). Returns (brave, faith, gx, gy) or None
    when zero or MULTIPLE match (ambiguous -- can't trust as ground truth, so the sample is skipped)."""
    hits = []
    for s in range(BAND_SLOTS):
        e = band_entry(s)
        if not valid_entry(h, e):
            continue
        if u16(h, e + A_MAXHP) != maxhp: continue
        if u16(h, e + A_HP) != hp:       continue
        if u8(h, e + A_LEVEL) != level:  continue
        hits.append((u8(h, e + A_BRAVE), u8(h, e + A_FAITH), u8(h, e + A_GX), u8(h, e + A_GY)))
    return hits[0] if len(hits) == 1 else None


def main():
    secs = float(sys.argv[1]) if len(sys.argv) > 1 else 180
    hz   = float(sys.argv[2]) if len(sys.argv) > 2 else 20
    pid = find_pid(PROC)
    if not pid:
        sys.exit(f"{PROC} not running")
    h = k32.OpenProcess(PV, False, pid)
    if not h:
        sys.exit(f"OpenProcess failed err={C.get_last_error()}")

    print(f"watching condensed struct ({COND:#x}) for {secs:.0f}s @ {hz:.0f}Hz.  GODMODE MUST BE OFF.")
    print("IN-GAME: take turns with as many DIFFERENT party members as you can.\n")

    t0 = time.time()
    dt = 1.0 / hz
    last_key = None
    samples = 0
    skipped_ambig = 0
    # offset-set intersections across every clean sample (None = not seeded yet)
    brave_cands = faith_cands = pos_cands = None
    nameid_log = []      # (nameId, brave, faith) per resolved unit -- is +0x04 a usable unique key?
    dumped = False
    try:
        while time.time() - t0 < secs:
            level = u16(h, COND + TQ_LEVEL)
            hp    = u16(h, COND + TQ_HP)
            maxhp = u16(h, COND + TQ_MAXHP)
            key = (level, hp, maxhp)
            if key != last_key and maxhp and 0 < maxhp < 2000 and 1 <= level <= 99:
                last_key = key
                truth = find_unique_band(h, maxhp, hp, level)
                if truth is None:
                    skipped_ambig += 1
                else:
                    brave, faith, gx, gy = truth
                    win = rd(h, COND, WIN_LEN)
                    if win is not None and brave >= 0 and faith >= 0:
                        b_off = {i for i, v in enumerate(win) if v == brave}
                        f_off = {i for i, v in enumerate(win) if v == faith}
                        # position PAIR: gx at off, gy at off+1 (adjacent) -- kills small-value noise
                        p_off = {i for i in range(len(win) - 1) if win[i] == gx and win[i + 1] == gy}
                        brave_cands = b_off if brave_cands is None else (brave_cands & b_off)
                        faith_cands = f_off if faith_cands is None else (faith_cands & f_off)
                        pos_cands   = p_off if pos_cands   is None else (pos_cands   & p_off)
                        nameid = u16(h, COND + TQ_NAMEID)
                        nameid_log.append((nameid, brave, faith))
                        samples += 1
                        t = time.time() - t0
                        print(f"t={t:6.1f}s  acting unit lvl={level} hp={hp}/{maxhp} brave={brave} "
                              f"faith={faith} pos=({gx},{gy}) nameId={nameid}")
                        print(f"    brave@{sorted('0x%02x'%o for o in b_off)}  "
                              f"faith@{sorted('0x%02x'%o for o in f_off)}  "
                              f"pos(gx,gy)@{sorted('0x%02x'%o for o in p_off)}")
                        if not dumped:
                            hexs = " ".join(f"{v:02X}" for v in win)
                            print(f"    --- condensed window dump [{COND:#x}..+{WIN_LEN:#x}] ---")
                            for r in range(0, len(win), 16):
                                print(f"      +{r:03x}: {' '.join(f'{v:02X}' for v in win[r:r+16])}")
                            dumped = True
            time.sleep(dt)
    except KeyboardInterrupt:
        print("(interrupted)")
    finally:
        k32.CloseHandle(h)

    print("\n==== SUMMARY ====")
    print(f"clean samples = {samples}   ambiguous (skipped, multiple band matches) = {skipped_ambig}")
    if samples < 2:
        print("NEED >=2 distinct acting units for a useful intersection -- run again, take more turns.")
        return
    bc = sorted("0x%02x" % o for o in (brave_cands or set()))
    fc = sorted("0x%02x" % o for o in (faith_cands or set()))
    pc = sorted("0x%02x" % o for o in (pos_cands or set()))
    print(f"TqBrave  candidate offset(s) (matched on ALL {samples} samples): {bc or 'NONE'}")
    print(f"TqFaith  candidate offset(s) (matched on ALL {samples} samples): {fc or 'NONE'}")
    print(f"TqPos    candidate (gx,gy) pair offset(s)  (ALL {samples} samples): {pc or 'NONE'}")
    if len(bc) == 1 and len(fc) == 1:
        print(f"  -> BEST: fingerprint in the condensed struct -- TqBrave={bc[0]}, TqFaith={fc[0]}.")
        print("     Read (level,brave,faith) straight from it -> roster. Drop the HP-keyed band walk.")
    elif len(pc) == 1:
        print(f"  -> POSITION key: the active unit's (gx,gy) is at condensed+{pc[0]}. Match the band")
        print("     entry by POSITION (unique per tile, godmode-proof) instead of (maxHp,hp,level).")
    elif bc or fc or pc:
        print("  -> partial: take MORE turns with more-varied units to shrink the surviving sets to one.")
    else:
        print("  -> NONE of brave/faith/position are in the condensed struct's first "
              f"{WIN_LEN:#x} bytes. The struct is a minimal turn header; disambiguate via a")
        print("     band-side acting/CT marker instead (rethink needed -- bring me the window dump).")
    # Is +0x04 (nameId) a usable unique per-unit key after all?
    seen = {}; collide = False
    for nid, br, fa in nameid_log:
        if nid in seen and seen[nid] != (br, fa):
            collide = True
        seen[nid] = (br, fa)
    print(f"nameId(+0x04) check: {'COLLIDES across units (trap confirmed)' if collide else 'distinct per unit so far'}")


if __name__ == "__main__":
    main()
