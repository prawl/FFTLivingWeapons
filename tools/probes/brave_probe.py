#!/usr/bin/env python
"""
SPIKE (READ + WRITE): set a wielder's Brave low to test the Kobu signature (Kiyomori +3),
which raises the wielder's Brave when it strikes a BRAVER foe. A high-Brave wielder never
trips it, so this lowers the Samurai's Brave below the enemies.

WRITE RECIPE (proven in FFTMultiplayer StatHold, 2026-06-20): the EFFECTIVE + DISPLAYED brave
is the CURRENT byte combat +0x2B (faith +0x2D); orig +0x2A/+0x2C is a re-derived snapshot the
engine recomputes per turn and the display ignores. StatHold holds current forever against that
re-normalize. We CAN'T hold here (the hold would fight Kobu's own raise), and a current-only
one-shot gets re-normalized back up from orig on the unit's next turn -- so `set` writes all
THREE consistently (roster +0x1E, combat orig +0x2A, combat current +0x2B) to the same value:
  - keeping orig == roster keeps Kobu's Wielder.Locate fingerprint valid (it matches combat +0x2A
    against roster +0x1E -- see Wielder.Locate / Offsets.ABrave),
  - orig == current means the per-turn re-normalize settles to the LOW value (no hold needed),
  - Kobu seeds its ceiling from current (+0x0F = +0x2B) at +3, then is free to climb it.

KOBU GATE REMINDER: ResolveDeployedMainHand returns 0 (Kobu does nothing) if TWO roster slots
hold the weapon in the MAIN hand. `survey` prints the roster so you can see the count first.

Combat array BASE 0x141853CE0 stride 0x200 (no ASLR). Roster BASE 0x1411A7D10 stride 0x258.
RPM/WPM-guarded; largest-working-set pid (2-proc trap). Read-back after every write.
"""
import ctypes as C
from ctypes import wintypes as W
import sys

PROC = "fft_enhanced"

# combat array
CB, CST, CN = 0x141853CE0, 0x200, 28
C_AGENCY = 0x05
C_WEAPON = 0x20           # u16
C_LEVEL  = 0x29
C_OBRAVE, C_CBRAVE = 0x2A, 0x2B   # orig / CURRENT(displayed) brave
C_OFAITH, C_CFAITH = 0x2C, 0x2D   # orig / CURRENT(displayed) faith
C_GX, C_GY = 0x4F, 0x50           # band AGx/AGy (0x33/0x34) + BandEntry 0x1C
HUMAN = 0x08

# roster
RB, RST, RN = 0x1411A7D10, 0x258, 20
R_RHAND, R_LHAND, R_OFFHAND = 0x14, 0x16, 0x18   # u16
R_LEVEL, R_BRAVE, R_FAITH = 0x1D, 0x1E, 0x1F     # u8

k32 = C.WinDLL("kernel32", use_last_error=True)
psapi = C.WinDLL("psapi", use_last_error=True)
ACCESS = 0x0438


class _PMC(C.Structure):
    _fields_ = [("cb", W.DWORD), ("pf", W.DWORD)] + [("a%d" % i, C.c_size_t) for i in range(8)]


def find_pid(name):
    arr = (W.DWORD * 4096)(); need = W.DWORD()
    psapi.EnumProcesses(arr, C.sizeof(arr), C.byref(need))
    best, bw = None, -1
    for i in range(need.value // C.sizeof(W.DWORD)):
        pid = arr[i]
        h = k32.OpenProcess(0x0410, False, pid)
        if not h:
            continue
        buf = C.create_unicode_buffer(260)
        if psapi.GetModuleBaseNameW(h, None, buf, 260) and buf.value.lower() == name.lower() + ".exe":
            p = _PMC(); p.cb = C.sizeof(p)
            psapi.GetProcessMemoryInfo(h, C.byref(p), p.cb)
            if p.a2 > bw:
                best, bw = pid, p.a2
        k32.CloseHandle(h)
    return best


_H = None


def rpm(addr, n):
    buf = C.create_string_buffer(n); got = C.c_size_t()
    ok = k32.ReadProcessMemory(_H, C.c_void_p(addr), buf, n, C.byref(got))
    return buf.raw if ok and got.value == n else None


def u8(addr):
    d = rpm(addr, 1); return d[0] if d else None


def u16(addr):
    d = rpm(addr, 2); return (d[0] | (d[1] << 8)) if d else None


def w8(addr, val):
    got = C.c_size_t()
    return bool(k32.WriteProcessMemory(_H, C.c_void_p(addr), bytes([val & 0xFF]), 1, C.byref(got))) and got.value == 1


def survey():
    print(f"\nROSTER @ 0x{RB:X} stride 0x{RST:X}")
    hdr = f"{'r':>2} {'lvl':>3} {'rh':>4} {'lh':>5} {'oh':>5} {'Br':>3} {'Fa':>3}"
    print(hdr); print("-" * len(hdr))
    for r in range(RN):
        base = RB + r * RST
        lvl = u8(base + R_LEVEL)
        if lvl is None or not (1 <= lvl <= 99):
            continue
        print(f"{r:>2} {lvl:>3} {u16(base+R_RHAND):>4} {u16(base+R_LHAND):>5} {u16(base+R_OFFHAND):>5} "
              f"{u8(base+R_BRAVE):>3} {u8(base+R_FAITH):>3}")

    print(f"\nCOMBAT @ 0x{CB:X} stride 0x{CST:X}   (oBr/cBr = orig/CURRENT brave; cBr is displayed)")
    hdr = f"{'i':>2} {'addr':>11} {'ctl':>5} {'wpn':>4} {'lvl':>3} {'oBr':>3} {'cBr':>3} {'oFa':>3} {'cFa':>3} {'gx':>3} {'gy':>3}"
    print(hdr); print("-" * len(hdr))
    for i in range(CN):
        base = CB + i * CST
        lvl = u8(base + C_LEVEL)
        if lvl is None or not (1 <= lvl <= 99):
            continue
        ag = u8(base + C_AGENCY)
        ctl = "HUMAN" if (ag is not None and ag & HUMAN) else "AI"
        print(f"{i:>2} 0x{base:09X} {ctl:>5} {u16(base+C_WEAPON):>4} {lvl:>3} "
              f"{u8(base+C_OBRAVE):>3} {u8(base+C_CBRAVE):>3} {u8(base+C_OFAITH):>3} {u8(base+C_CFAITH):>3} "
              f"{u8(base+C_GX):>3} {u8(base+C_GY):>3}")
    print("\nThen: set <weaponId> <brave>   (e.g. set 43 50  -> Kiyomori wielder brave 50)")


def set_brave(weapon, brave):
    if not (1 <= brave <= 100):
        print("brave must be 1..100"); return

    # Roster: every slot with this weapon in the MAIN hand (Kobu only commands the main hand).
    rhits = []
    for r in range(RN):
        base = RB + r * RST
        lvl = u8(base + R_LEVEL)
        if lvl is None or not (1 <= lvl <= 99):
            continue
        if u16(base + R_RHAND) == weapon:
            rhits.append((r, base))
    print(f"roster main-hand wielders of weapon {weapon}: {[r for r, _ in rhits]}")
    if len(rhits) == 0:
        print("  none -- nothing to do (is it equipped in the MAIN hand?)"); return
    if len(rhits) > 1:
        print("  WARNING: >1 main-hand wielder -- Kobu's ResolveDeployedMainHand will BAIL (ambiguous).")
        print("  Unequip Kiyomori from all but one before testing, or Kobu never fires. Setting brave anyway.")

    for r, base in rhits:
        old = u8(base + R_BRAVE)
        w8(base + R_BRAVE, brave)
        print(f"  roster slot {r}: RBrave {old} -> {u8(base + R_BRAVE)}")

    # Combat: every live slot holding this weapon -- set orig (+0x2A) AND current (+0x2B) to match.
    chits = 0
    for i in range(CN):
        base = CB + i * CST
        lvl = u8(base + C_LEVEL)
        if lvl is None or not (1 <= lvl <= 99):
            continue
        if u16(base + C_WEAPON) != weapon:
            continue
        chits += 1
        ob, cb = u8(base + C_OBRAVE), u8(base + C_CBRAVE)
        w8(base + C_OBRAVE, brave)    # keep orig == roster so Wielder.Locate stays valid
        w8(base + C_CBRAVE, brave)    # current is the displayed/effective value Kobu seeds from
        print(f"  combat slot {i} @ 0x{base:09X}: origBrave {ob}->{u8(base+C_OBRAVE)}  curBrave {cb}->{u8(base+C_CBRAVE)}")
    if chits == 0:
        print("  (no live combat slot holds it -- on the world map? brave will apply when battle starts)")
    print(f"\nDone. Strike a foe whose brave > {brave} with Kiyomori at +3 and watch livingweapon.log for 'kobu:'.")


def set_enemies(brave):
    """Set CURRENT brave (+0x2B) on every live AI (non-human) combat slot. Leaves orig (+0x2A) alone
    so Kobu's struck-foe fingerprint (which keys on orig brave) still matches the static-array oracle;
    Kobu copies the CURRENT byte as the climb value. Strike soon -- the engine re-normalizes an enemy's
    current back toward orig on ITS next turn (StatHold finding); a player-turn strike lands before that."""
    if not (1 <= brave <= 100):
        print("brave must be 1..100"); return
    n = 0
    for i in range(CN):
        base = CB + i * CST
        lvl = u8(base + C_LEVEL)
        if lvl is None or not (1 <= lvl <= 99):
            continue
        ob, fa = u8(base + C_OBRAVE), u8(base + C_OFAITH)
        if ob is None or fa is None or not (1 <= ob <= 100) or not (1 <= fa <= 100):
            continue                                   # garbage / non-unit slot
        ag = u8(base + C_AGENCY)
        if ag is not None and (ag & HUMAN):
            continue                                   # skip player units
        cb = u8(base + C_CBRAVE)
        w8(base + C_CBRAVE, brave)
        print(f"  enemy slot {i:>2} @ 0x{base:09X}: curBrave {cb}->{u8(base+C_CBRAVE)} (orig {ob} left as the fingerprint)")
        n += 1
    print(f"\nSet {n} enemies to current brave {brave}. Hit any one with the Samurai (Kiyomori +3) -> "
          f"brave climbs toward {brave} (cap {97}). Watch livingweapon.log for 'kobu:'.")


def main():
    global _H
    pid = find_pid(PROC)
    if not pid:
        print(f"{PROC}.exe not running"); sys.exit(1)
    _H = k32.OpenProcess(ACCESS, False, pid)
    if not _H:
        print(f"OpenProcess failed (err {C.get_last_error()})"); sys.exit(1)
    print(f"pid {pid} (largest working set)")
    a = sys.argv[1:]
    if not a or a[0] == "survey":
        survey()
    elif a[0] == "set":
        set_brave(int(a[1]), int(a[2]))
    elif a[0] == "enemies":
        set_enemies(int(a[1]))
    else:
        print("verbs: survey | set <weaponId> <brave> | enemies <brave>")


if __name__ == "__main__":
    main()
