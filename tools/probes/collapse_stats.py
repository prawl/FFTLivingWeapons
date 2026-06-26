"""Live test harness for the low-entropy-roster attribution fix.

Collapses three specific units' brave/faith to identical values in BOTH the roster
(RBrave/RFaith) and the live band (orig brave/faith == combat CBrave/CFaith) so the
(level,brave,faith) actor fingerprint genuinely collides -- reproducing the live bug
where kills expire "could not determine who killed the enemy". HP is left untouched
(unique per unit), so stage-1 (maxHp+hp+level) still finds each acting band entry; only
the band->roster fingerprint collapses. Weapons untouched (the fix disambiguates on them).

Targets are matched by signature so benched units are not disturbed:
  band entry  -> by (maxHp, level)
  roster slot -> by (level, brave, faith)
Prints originals (for manual restore) and reads back after writing.

Usage:  python tools/probes/collapse_stats.py
"""
import ctypes, ctypes.wintypes as w, struct, sys

PROCESS_VM_READ = 0x0010
PROCESS_VM_WRITE = 0x0020
PROCESS_VM_OPERATION = 0x0008
PROCESS_QUERY_INFORMATION = 0x0400
k32 = ctypes.windll.kernel32
psapi = ctypes.windll.psapi

# --- target collapse values ---
BRAVE, FAITH = 65, 65

# --- offsets (1.5, from LivingWeapon/Offsets.cs) ---
ROSTER_BASE, ROSTER_STRIDE, ROSTER_SLOTS = 0x1411A7D10, 0x258, 20
R_LVL, R_BR, R_FA, R_RHAND, R_NAME = 0x1D, 0x1E, 0x1F, 0x14, 0x230

COMBAT_ANCHOR, COMBAT_STRIDE = 0x141855CE0, 0x200
BAND_ENTRY = 0x1C
BAND_READ_BASE = COMBAT_ANCHOR + BAND_ENTRY - 24 * COMBAT_STRIDE
BAND_SLOTS = 49
A_LVL, A_BR_ORIG, A_FA_ORIG = 0x0D, 0x0E, 0x10
A_BR_CUR, A_FA_CUR = 0x0F, 0x11        # current/displayed copies (combat +0x2B / +0x2D)
A_HP, A_MAXHP, A_GX, A_GY = 0x14, 0x16, 0x33, 0x34
A_WEAPON = 0x04                         # band+0x04 == CWeapon (the fix's disambiguator)

# units to collapse, matched by (maxHp, level) in the band and (level,brave,faith) in the roster
TARGETS = [
    ("Reis",  dict(maxHp=714, lvl=94, br=62, fa=64)),
    ("Mel",   dict(maxHp=531, lvl=94, br=67, fa=68)),
    ("Cloud", dict(maxHp=390, lvl=91, br=70, fa=65)),
]


def find_pid_largest(name):
    """Pick the fft process with the LARGEST working set (a duplicate instance silently
    eats writes -- see dual-gun probe note)."""
    arr = (w.DWORD * 4096)(); needed = w.DWORD()
    psapi.EnumProcesses(ctypes.byref(arr), ctypes.sizeof(arr), ctypes.byref(needed))
    best, best_ws = None, -1
    for i in range(needed.value // ctypes.sizeof(w.DWORD)):
        pid = arr[i]
        h = k32.OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, False, pid)
        if not h:
            continue
        buf = ctypes.create_unicode_buffer(260)
        if psapi.GetModuleBaseNameW(h, None, buf, 260) and buf.value.lower() == name.lower():
            class PMC(ctypes.Structure):
                _fields_ = [("cb", w.DWORD), ("PageFaultCount", w.DWORD),
                            ("PeakWorkingSetSize", ctypes.c_size_t), ("WorkingSetSize", ctypes.c_size_t),
                            ("a", ctypes.c_size_t), ("b", ctypes.c_size_t), ("c", ctypes.c_size_t),
                            ("d", ctypes.c_size_t), ("e", ctypes.c_size_t), ("f", ctypes.c_size_t)]
            pmc = PMC(); pmc.cb = ctypes.sizeof(PMC)
            psapi.GetProcessMemoryInfo(h, ctypes.byref(pmc), pmc.cb)
            if pmc.WorkingSetSize > best_ws:
                best_ws = pmc.WorkingSetSize; best = pid
        k32.CloseHandle(h)
    if best is None:
        return None
    return k32.OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ | PROCESS_VM_WRITE | PROCESS_VM_OPERATION, False, best), best


def rpm(h, addr, n):
    buf = ctypes.create_string_buffer(n); got = ctypes.c_size_t()
    if not k32.ReadProcessMemory(h, ctypes.c_void_p(addr), buf, n, ctypes.byref(got)) or got.value != n:
        return None
    return buf.raw


def wb(h, addr, val):
    b = bytes([val & 0xFF]); got = ctypes.c_size_t()
    return bool(k32.WriteProcessMemory(h, ctypes.c_void_p(addr), b, 1, ctypes.byref(got))) and got.value == 1


def main():
    res = find_pid_largest("fft_enhanced.exe")
    if not res:
        print("game not running"); sys.exit(1)
    h, pid = res
    print(f"attached pid {pid}\n")

    # --- ROSTER ---
    print("=== roster: matching + collapsing brave/faith ->", BRAVE, "/", FAITH, "===")
    for name, t in TARGETS:
        hit = False
        for s in range(ROSTER_SLOTS):
            b = ROSTER_BASE + s * ROSTER_STRIDE
            d = rpm(h, b, 0x238)
            if d is None or d[R_LVL] == 0:
                continue
            lvl, br, fa = d[R_LVL], d[R_BR], d[R_FA]
            if (lvl, br, fa) == (t["lvl"], t["br"], t["fa"]):
                rr = struct.unpack_from('<H', d, R_RHAND)[0]
                nm = struct.unpack_from('<H', d, R_NAME)[0]
                ok1 = wb(h, b + R_BR, BRAVE); ok2 = wb(h, b + R_FA, FAITH)
                back = rpm(h, b, 0x20)
                print(f"  {name:6} roster slot {s:2} nameId {nm:3} weapon {rr:3}  "
                      f"was br{br}/fa{fa} -> now br{back[R_BR]}/fa{back[R_FA]}  ({'ok' if ok1 and ok2 else 'WRITE FAIL'})")
                hit = True
        if not hit:
            print(f"  {name:6} roster slot NOT FOUND for (lvl{t['lvl']},br{t['br']},fa{t['fa']})")

    # --- BAND ---
    print("\n=== band: matching + collapsing orig+current brave/faith ===")
    for name, t in TARGETS:
        hit = False
        for s in range(BAND_SLOTS):
            e = BAND_READ_BASE + s * COMBAT_STRIDE
            d = rpm(h, e, 0x100)
            if d is None:
                continue
            mhp = struct.unpack_from('<H', d, A_MAXHP)[0]
            lvl = d[A_LVL]
            if mhp == t["maxHp"] and lvl == t["lvl"]:
                wpn = struct.unpack_from('<H', d, A_WEAPON)[0]
                gx, gy = d[A_GX], d[A_GY]
                br, fa = d[A_BR_ORIG], d[A_FA_ORIG]
                for off in (A_BR_ORIG, A_BR_CUR):
                    wb(h, e + off, BRAVE)
                for off in (A_FA_ORIG, A_FA_CUR):
                    wb(h, e + off, FAITH)
                back = rpm(h, e, 0x20)
                print(f"  {name:6} band slot {s:2} weapon {wpn:3} pos({gx},{gy}) maxHp {mhp}  "
                      f"was br{br}/fa{fa} -> now br{back[A_BR_ORIG]}/fa{back[A_FA_ORIG]} (cur br{back[A_BR_CUR]}/fa{back[A_FA_CUR]})")
                hit = True
        if not hit:
            print(f"  {name:6} band entry NOT FOUND for (maxHp{t['maxHp']},lvl{t['lvl']})")

    print("\nDone. All three now share brave/faith -> the actor fingerprint collides.")
    print("Have each unit land a kill and watch the log: it should credit the RIGHT weapon")
    print("(its own band+0x04), not 'could not determine' and not a collision twin.")
    k32.CloseHandle(h)


if __name__ == "__main__":
    main()
