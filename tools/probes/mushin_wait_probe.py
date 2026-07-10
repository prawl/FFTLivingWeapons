#!/usr/bin/env python
r"""
SPIKE (read-only): settle the Mushin FULL-WAIT trigger.

Question: when a player unit ends its turn with a FULL WAIT (no move, no act), is there a
PER-UNIT byte that cleanly marks it: distinct from a MOVE-only turn and from an ATTACK?
The release plan named combat +0x1BB but never probed it. Per Offsets.cs the per-unit action
record starts at frame/combat +0x1A0 (AArec), so +0x1BB sits INSIDE the action-record region,
a plausible "what did this unit just do" byte.

This watches, on the Kiku-ichimonji wielder (weapon id 45 by default):
  (1) the per-unit combat-struct block  combat +0x1A0 .. +0x1E0  (covers +0x1BB), AND
  (2) the static `acted` block           0x140782A80 .. 0x140782AC0  (acted @ +0x0C), AND
  (3) global scalars: acted, ActorPtr, BattleMode
so ONE set of three turns tests every wait-detection hypothesis at once.

1.5 offsets (LivingWeapon/Offsets.cs, all CONFIRMED unless noted):
  CombatAnchor 0x141855CE0  stride 0x200  CWeapon +0x20  CLevel +0x29  CHp +0x30
  Acted        0x140782A8C   ActorPtr 0x14186AF68   BattleMode 0x1409069A0

MODES:
  recon                     one-shot state dump: BattleMode, every combat slot's weapon/level/hp,
                            target-weapon matches, acted, ActorPtr.  (Run this FIRST.)
  watch [wid] [secs] [hz]   locate wid's (default 45) combat struct, then STREAM on-change events
                            for regions (1)+(2) with relative timestamps.  Default 240s @ 50Hz.

PROTOCOL (watch): one unit, three turns, ~10s gaps, IN ORDER:
  (A) full WAIT  : no move, no act (just end the turn)
  (B) MOVE only  : move, then wait (no attack)
  (C) ATTACK     : act
Big gaps so the three change-clusters separate cleanly in the timeline.

Read-only.  Largest-working-set pid (the 2-process trap).  No writes, ever.
"""
import ctypes as C
from ctypes import wintypes as W
import sys, time

PROC = "fft_enhanced"

COMBAT_ANCHOR = 0x141855CE0
COMBAT_STRIDE = 0x200
SEARCH_SLOTS  = 24            # scan +/- this many slots around the anchor
C_WEAPON = 0x20              # u16 equipped weapon id (self-map key)
C_LEVEL  = 0x29              # u8
C_HP     = 0x30             # u16

REGION_OFF = 0x1A0           # per-unit block start (relative to combat struct base)
REGION_LEN = 0x40            # 0x1A0..0x1E0: contains +0x1BB
BB_OFF     = 0x1BB           # the named candidate

ACTED    = 0x140782A8C
SBLK     = 0x140782A80        # static acted-block start
SBLK_LEN = 0x40
SBLK_ACTED_IDX = ACTED - SBLK  # 0x0C

ACTOR_PTR = 0x14186AF68
BATTLE_MODE = 0x1409069A0

TURN_QUEUE = 0x1407832A0     # active-unit fingerprint: +0x00 lvl, +0x02 team, +0x0C hp, +0x10 maxhp

k32 = C.WinDLL("kernel32", use_last_error=True)
psapi = C.WinDLL("psapi", use_last_error=True)


class _PMC(C.Structure):
    _fields_ = [("cb", W.DWORD), ("pf", W.DWORD)] + [("a%d" % i, C.c_size_t) for i in range(8)]


def find_pid(name):
    arr = (W.DWORD * 4096)(); need = W.DWORD()
    psapi.EnumProcesses(arr, C.sizeof(arr), C.byref(need))
    best, bw = None, -1
    for i in range(need.value // C.sizeof(W.DWORD)):
        h = k32.OpenProcess(0x0410, False, arr[i])
        if not h:
            continue
        buf = C.create_unicode_buffer(260)
        if psapi.GetModuleBaseNameW(h, None, buf, 260) and buf.value.lower() == name.lower() + ".exe":
            p = _PMC(); p.cb = C.sizeof(p)
            psapi.GetProcessMemoryInfo(h, C.byref(p), p.cb)
            if p.a2 > bw:
                best, bw = arr[i], p.a2
        k32.CloseHandle(h)
    return best


def opener():
    pid = find_pid(PROC)
    if not pid:
        print(f"{PROC}.exe not running"); sys.exit(1)
    h = k32.OpenProcess(0x0410, False, pid)   # QUERY_INFO | VM_READ
    return pid, h


def rd(h, addr, n):
    b = C.create_string_buffer(n); got = C.c_size_t()
    ok = k32.ReadProcessMemory(h, C.c_void_p(addr), b, n, C.byref(got))
    return bytearray(b.raw[:got.value]) if ok and got.value == n else None


def u16(h, addr):
    b = rd(h, addr, 2); return None if b is None else b[0] | (b[1] << 8)


def u8(h, addr):
    b = rd(h, addr, 1); return None if b is None else b[0]


def u64(h, addr):
    b = rd(h, addr, 8); return None if b is None else int.from_bytes(b, "little")


def slot_addr(n):
    # slot index n is measured the same way Offsets does: anchor is n=+ (search center)
    return COMBAT_ANCHOR + n * COMBAT_STRIDE


def scan_matches(h, wid):
    hits = []
    for n in range(-SEARCH_SLOTS, SEARCH_SLOTS + 1):
        base = slot_addr(n)
        w = u16(h, base + C_WEAPON)
        if w is None:
            continue
        lvl = u8(h, base + C_LEVEL); hp = u16(h, base + C_HP)
        sane = (lvl is not None and 1 <= lvl <= 99 and hp is not None and 1 <= hp <= 9999)
        if w == wid and sane:
            hits.append((n, base, lvl, hp))
    return hits


C_GX = 0x4F                 # u8 gx == band AGx(0x33) + BandEntry(0x1C)  (unverified combat framing)
C_GY = 0x50                 # u8 gy == band AGy(0x34) + BandEntry(0x1C)


def recon(h, window=40):
    bm = u8(h, BATTLE_MODE)
    ap = u64(h, ACTOR_PTR)
    ac = u8(h, ACTED)
    tq_lvl = u16(h, TURN_QUEUE + 0x00); tq_team = u16(h, TURN_QUEUE + 0x02)
    tq_hp = u16(h, TURN_QUEUE + 0x0C); tq_max = u16(h, TURN_QUEUE + 0x10)
    print(f"BattleMode = {bm}  ({'IN battle' if bm in (2,3,4) else 'OUT of battle / menu'})")
    print(f"acted@0x{ACTED:X} = {ac}    ActorPtr@0x{ACTOR_PTR:X} = 0x{(ap or 0):X}")
    if ap:
        seat = (ap - (COMBAT_ANCHOR - 24 * COMBAT_STRIDE)) / COMBAT_STRIDE
        print(f"  (ActorPtr -> frame seat index {seat:.2f})")
    print(f"TurnQueue active unit: lvl={tq_lvl} team={tq_team} ({'player' if tq_team==0 else 'enemy' if tq_team==1 else '?'}) hp={tq_hp}/{tq_max}")
    print(f"\ncombat slots within +/-{window} (weapon / lvl / hp / gx,gy / +0x1BB); wid<=511 & sane hp:")
    any_row = False
    for n in range(-window, window + 1):
        base = slot_addr(n)
        w = u16(h, base + C_WEAPON)
        if w is None:
            continue
        lvl = u8(h, base + C_LEVEL); hp = u16(h, base + C_HP)
        gx = u8(h, base + C_GX); gy = u8(h, base + C_GY)
        bb = u8(h, base + BB_OFF)
        # A real unit: plausible weapon id (0..511, 0=unarmed monster ok), sane level+hp, on-map coords.
        sane = (lvl is not None and 1 <= lvl <= 99 and hp is not None and 1 <= hp <= 9999
                and w is not None and w <= 511 and gx is not None and gx <= 40 and gy <= 40)
        if sane:
            any_row = True
            flag = "  <== weapon 45 (KIKU)" if w == 45 else ""
            print(f"  n={n:+3d} @0x{base:X}  wid={w:<4d} lvl={lvl:<3d} hp={hp:<5d} pos=({gx:2d},{gy:2d}) bb=0x{bb:02X}{flag}")
    if not any_row:
        print("  (no sane combat rows: probably not in a battle, or anchor drifted)")
    print("\nDefault target weapon 45 matches:")
    m = scan_matches(h, 45)
    if not m:
        print("  (none: Kiku id 45 not equipped; drive any unit above and pass its wid to `watch`)")
    for (n, base, lvl, hp) in m:
        print(f"  n={n:+3d} @0x{base:X} lvl={lvl} hp={hp}   +0x1BB lives at 0x{base+BB_OFF:X}")


def watch(h, wid, secs, hz):
    hits = scan_matches(h, wid)
    if not hits:
        print(f"no sane combat struct with weapon id {wid} found: run recon; is the wielder in battle?")
        sys.exit(2)
    targets = [{"n": n, "base": base, "reg": base + REGION_OFF, "last": None}
               for (n, base, lvl, hp) in hits]
    print(f"weapon {wid}: {len(targets)} struct(s): watching ALL:")
    for (n, base, lvl, hp) in hits:
        print(f"   n={n:+d} @combat 0x{base:X} (lvl {lvl}, hp {hp})  +0x1BB @0x{base+BB_OFF:X}")
    print(f"per-unit window +0x{REGION_OFF:X}..+0x{REGION_OFF+REGION_LEN:X} "
          f"(candidate +0x{BB_OFF:X} = index +0x{BB_OFF-REGION_OFF:02X}); also gx,gy (+0x{C_GX:02X}/{C_GY:02X}).")
    print(f"STATIC acted block 0x{SBLK:X}..0x{SBLK+SBLK_LEN:X} (acted @ +0x{SBLK_ACTED_IDX:02X}).")
    print(f"{secs}s @ {hz}Hz.  ORDER: (A) WAIT, (B) MOVE, (C) ATTACK, ~10s gaps.\n", flush=True)
    t0 = time.time()
    last_sblk = None
    last_ptr = None
    bases = {t["base"] for t in targets}
    while time.time() - t0 < secs:
        t = time.time() - t0
        for tg in targets:
            reg = rd(h, tg["reg"], REGION_LEN)
            if reg is None or reg == tg["last"]:
                continue
            gx = u8(h, tg["base"] + C_GX); gy = u8(h, tg["base"] + C_GY)
            bb = reg[BB_OFF - REGION_OFF]
            if tg["last"] is None:
                dump = " ".join(f"{reg[i]:02X}" for i in range(REGION_LEN))
                print(f"[{t:7.2f}s] UNIT n={tg['n']:+d} init  bb=0x{bb:02X} pos=({gx},{gy}) | {dump}", flush=True)
            else:
                ch = ", ".join(f"+0x{REGION_OFF+i:03X} {tg['last'][i]:02X}->{reg[i]:02X}"
                               for i in range(REGION_LEN) if tg["last"][i] != reg[i])
                print(f"[{t:7.2f}s] UNIT n={tg['n']:+d}  bb=0x{bb:02X} pos=({gx},{gy})  changed: {ch}", flush=True)
            tg["last"] = reg
        sblk = rd(h, SBLK, SBLK_LEN)
        if sblk is not None and sblk != last_sblk:
            if last_sblk is None:
                dump = " ".join(f"{sblk[i]:02X}" for i in range(SBLK_LEN))
                print(f"[{t:7.2f}s] STAT init  acted=0x{sblk[SBLK_ACTED_IDX]:02X} | {dump}", flush=True)
            else:
                ch = ", ".join(f"+0x{i:02X} {last_sblk[i]:02X}->{sblk[i]:02X}"
                               for i in range(SBLK_LEN) if last_sblk[i] != sblk[i])
                print(f"[{t:7.2f}s] STAT  acted=0x{sblk[SBLK_ACTED_IDX]:02X}  changed: {ch}", flush=True)
            last_sblk = sblk
        ptr = u64(h, ACTOR_PTR)
        if ptr is not None and ptr != last_ptr:
            seat = (ptr - (COMBAT_ANCHOR - 24 * COMBAT_STRIDE)) / COMBAT_STRIDE if ptr else 0
            mine = "  == A KIKU WIELDER" if ptr in bases else ""
            print(f"[{t:7.2f}s] ActorPtr -> 0x{ptr:X} (seat {seat:.2f}){mine}", flush=True)
            last_ptr = ptr
        time.sleep(1.0 / hz)
    print("\ndone.", flush=True)


def main():
    mode = sys.argv[1] if len(sys.argv) > 1 else "recon"
    pid, h = opener()
    print(f"pid {pid} ({PROC})\n")
    if mode == "recon":
        recon(h)
    elif mode == "watch":
        wid = int(sys.argv[2]) if len(sys.argv) > 2 else 45
        secs = int(sys.argv[3]) if len(sys.argv) > 3 else 240
        hz = int(sys.argv[4]) if len(sys.argv) > 4 else 50
        watch(h, wid, secs, hz)
    else:
        print(f"unknown mode {mode!r}; use 'recon' or 'watch [wid] [secs] [hz]'")


if __name__ == "__main__":
    main()
