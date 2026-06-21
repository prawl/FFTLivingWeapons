"""Diagnose WHY the Coordinator ghost-filter under-catches PLAYER units (FFTMultiplayer).

Context: the v1 hotseat Coordinator's positional draft gates every stamp on a
DeployedLatch -- an identity is "deployed" only after it is EVER seen with the
combat inBattle flag (+0x2E, u16) nonzero. Live it logged `players 2, enemies 1`
and never changed in a 7-player battle: 5 real players never latched, so they got
no stamp. The latch is the SAME pattern the project ledger flags as a trap
(array-inb-flag-pulses: filter by sane bounds + slot-sign, never by the flag).

This probe replicates the Coordinator's EXACT scan (CombatAnchor 0x141855CE0,
stride 0x200, n = -24..+24, IsLiveCombatSlot bounds) so its readings map 1:1 to
what the Coordinator sees, then watches every live slot over time to answer:

  U1 (ghosts?)   Does the PLAYER region (n >= 0) hold duplicate identities
                 (a deployed unit + a stale ghost copy of it)? If not, there is
                 nothing for the latch to filter and positional assignment can run
                 straight over the sane-bounds live set -- no inb gate needed.
  U2 (signal?)   Do player units EVER read inb (+0x2E) nonzero? That is the only
                 thing the latch waits for. If most never do, the latch is the bug.

It also dumps agency / gx,gy / job / hp so a BETTER deployed-vs-ghost
discriminator (if one is even needed) can be eyeballed from real data.

Pure cross-process RPM (read-only, crash-safe). Run DURING a battle, ideally one
with several deployed players:

    python tools\\probes\\deployed_signal_probe.py [watch_seconds]   (default 20)

Offsets (1.5, image base 0x140000000, no ASLR) -- verbatim from the Coordinator:
    CombatAnchor 0x141855CE0  stride 0x200   (= BattleUnitsBase 0x141853CE0 + 0x2000)
    job +0x03  agency +0x05  flags1 +0x06  level +0x29
    origBrave +0x2A  curBrave +0x2B  origFaith +0x2C  curFaith +0x2D
    inBattle +0x2E (u16, PULSES)  hpCur +0x30 (u16)  hpMax +0x32 (u16)
    gx +0x4F  gy +0x50
Identity key = (level, hpMax, origBrave, origFaith) -- exactly DeployedLatch.Id.
"""

import ctypes
import ctypes.wintypes as w
import sys
import time

PROC = "fft_enhanced.exe"
ANCHOR = 0x141855CE0
STRIDE = 0x200
RADIUS = 24                 # n = -24..+24, matching Coordinator.ScanRadius
LEN = 0x60
PERIOD_S = 0.150            # 150ms -- matches Coordinator.PeriodMs

OFF_JOB, OFF_AGENCY, OFF_FLAGS1 = 0x03, 0x05, 0x06
OFF_LEVEL = 0x29
OFF_OBR, OFF_CBR, OFF_OFA, OFF_CFA = 0x2A, 0x2B, 0x2C, 0x2D
OFF_INB = 0x2E
OFF_HPC, OFF_HPM = 0x30, 0x32
OFF_PA, OFF_MA, OFF_SPD = 0x3E, 0x3F, 0x40
OFF_GX, OFF_GY = 0x4F, 0x50

k32 = ctypes.WinDLL("kernel32", use_last_error=True)
psapi = ctypes.WinDLL("psapi", use_last_error=True)
_H = None


def _open():
    arr = (w.DWORD * 4096)()
    need = w.DWORD()
    if not psapi.EnumProcesses(arr, ctypes.sizeof(arr), ctypes.byref(need)):
        return None
    for i in range(need.value // ctypes.sizeof(w.DWORD)):
        h = k32.OpenProcess(0x0410, False, arr[i])  # QUERY_INFO | VM_READ
        if not h:
            continue
        buf = ctypes.create_unicode_buffer(260)
        if psapi.GetModuleBaseNameW(h, None, buf, 260) and buf.value.lower() == PROC.lower():
            return h
        k32.CloseHandle(h)
    return None


def rpm(addr, n):
    if not _H:
        return None
    buf = ctypes.create_string_buffer(n)
    got = ctypes.c_size_t()
    ok = k32.ReadProcessMemory(_H, ctypes.c_void_p(addr), buf, n, ctypes.byref(got))
    return buf.raw if ok and got.value == n else None


def u16(d, off):
    return d[off] | (d[off + 1] << 8)


def is_live(flags1, hpc, hpm, lvl):
    # Verbatim PuppetPolicy.IsLiveCombatSlot -- the Coordinator's CURRENT (loose) liveness gate.
    if flags1 & 0x09:
        return False                      # guest -- excluded
    if hpm < 1 or hpm > 9999:
        return False
    if hpc < 1 or hpc > hpm:
        return False
    if lvl < 1 or lvl > 99:
        return False
    return True


def on_field(obr, ofa, gx, gy, hpm, hpc, lvl):
    # Mirrors MatchPolicy.IsOnFieldUnit -- the STRICTER gate MatchState already uses (brave/faith in
    # [1,100], on-map gx/gy <= 30, hpMax < 2000). The candidate replacement for the inb latch: it accepts
    # real deployed units and rejects off-field / garbage-stat slots WITHOUT needing the inb pulse.
    if hpm < 1 or hpm >= 2000:
        return False
    if hpc > hpm:
        return False
    if lvl < 1 or lvl > 99:
        return False
    if obr < 1 or obr > 100:
        return False
    if ofa < 1 or ofa > 100:
        return False
    if gx > 30 or gy > 30:
        return False
    return True


class Acc:
    """Accumulator for one identity across the watch span."""
    __slots__ = ("side", "slots", "samples", "inb_hits", "inb_max", "last")

    def __init__(self, side):
        self.side = side
        self.slots = set()
        self.samples = 0
        self.inb_hits = 0
        self.inb_max = 0
        self.last = {}


def main():
    global _H
    watch_s = float(sys.argv[1]) if len(sys.argv) > 1 else 20.0
    _H = _open()
    if not _H:
        print(f"{PROC} not running")
        sys.exit(1)

    print(f"Watching combat band @ 0x{ANCHOR:X} (n=-{RADIUS}..+{RADIUS}) for {watch_s:.0f}s "
          f"@ {int(PERIOD_S*1000)}ms -- stand in a battle.\n")

    ids = {}                              # identity tuple -> Acc
    passes = 0
    deadline = time.monotonic() + watch_s
    while time.monotonic() < deadline:
        passes += 1
        for n in range(-RADIUS, RADIUS + 1):
            d = rpm(ANCHOR + n * STRIDE, LEN)
            if d is None:
                continue
            flags1 = d[OFF_FLAGS1]
            lvl = d[OFF_LEVEL]
            hpc, hpm = u16(d, OFF_HPC), u16(d, OFF_HPM)
            if not is_live(flags1, hpc, hpm, lvl):
                continue
            obr, ofa = d[OFF_OBR], d[OFF_OFA]
            inb = u16(d, OFF_INB)
            key = (lvl, hpm, obr, ofa)
            side = "ENEMY" if n < 0 else "PLAYER"
            acc = ids.get(key)
            if acc is None:
                acc = ids[key] = Acc(side)
            acc.slots.add(n)
            acc.samples += 1
            if inb:
                acc.inb_hits += 1
                acc.inb_max = max(acc.inb_max, inb)
            gx, gy = d[OFF_GX], d[OFF_GY]
            acc.last = dict(n=n, job=d[OFF_JOB], agency=d[OFF_AGENCY], flags1=flags1,
                            lvl=lvl, hpc=hpc, hpm=hpm, cbr=d[OFF_CBR], cfa=d[OFF_CFA],
                            obr=obr, ofa=ofa, gx=gx, gy=gy, inb=inb,
                            pa=d[OFF_PA], ma=d[OFF_MA], spd=d[OFF_SPD],
                            onfld=on_field(obr, ofa, gx, gy, hpm, hpc, lvl))
        time.sleep(PERIOD_S)

    if not ids:
        print("No live slots seen the whole watch -- not in a battle, or wrong screen/state.")
        return

    for side in ("PLAYER", "ENEMY"):
        rows = [(k, a) for k, a in ids.items() if a.side == side]
        if not rows:
            continue
        rows.sort(key=lambda ka: ka[1].last["n"])
        print(f"=== {side} identities ({len(rows)}) ===")
        hdr = (f"{'slots(n)':>10} {'job':>4} {'lvl':>3} {'hp':>9} {'br o/c':>7} {'fa o/c':>7} "
               f"{'PA/MA/SP':>9} {'agcy':>5} {'pos':>7} {'onFld':>5} {'inbEver':>7} {'inbRate':>8}")
        print(hdr)
        print("-" * len(hdr))
        for _key, a in rows:
            L = a.last
            slots = ",".join(str(s) for s in sorted(a.slots))
            rate = (a.inb_hits / a.samples * 100.0) if a.samples else 0.0
            pams = f"{L['pa']}/{L['ma']}/{L['spd']}"
            print(f"{slots:>10} 0x{L['job']:02X} {L['lvl']:>3} {L['hpc']:>4}/{L['hpm']:<4} "
                  f"{L['obr']:>3}/{L['cbr']:<3} {L['ofa']:>3}/{L['cfa']:<3} "
                  f"{pams:>9} 0x{L['agency']:02X} ({L['gx']:>2},{L['gy']:>2}) "
                  f"{'Y' if L['onfld'] else 'NO':>5} "
                  f"{'YES' if a.inb_hits else 'no':>7} {rate:>7.0f}%")
        print()

    # ---- Verdict ----
    print("=== VERDICT ===")
    for side in ("PLAYER", "ENEMY"):
        accs = [a for a in ids.values() if a.side == side]
        if not accs:
            continue
        total = len(accs)
        latched = sum(1 for a in accs if a.inb_hits)          # what the inb latch WOULD catch
        dropped = total - latched                             # what the inb latch DROPS
        dupes = [a for a in accs if len(a.slots) > 1]         # identity in >1 slot
        onf = sum(1 for a in accs if a.last.get("onfld"))     # what IsOnFieldUnit catches
        offf = total - onf                                    # loose-pass but off-field/garbage
        print(f"{side}: {total} loose-live identities | inb-latch catches {latched}, DROPS {dropped} | "
              f"on-field catches {onf}, rejects {offf} | {len(dupes)} identity(ies) in >1 slot")
    print()
    pl = [a for a in ids.values() if a.side == "PLAYER"]
    pl_dropped = sum(1 for a in pl if not a.inb_hits)
    pl_dupes = sum(1 for a in pl if len(a.slots) > 1)
    pl_off = sum(1 for a in pl if not a.last.get("onfld"))
    print("Read this as:")
    print(f"  U2 (signal): if PLAYER 'inb-latch DROPS' > 0 ({pl_dropped} here), inb(+0x2E) is NOT a reliable")
    print("              player deployed-signal -- the latch is the live-failure cause. Drop the inb gate.")
    print(f"  U1 (ghosts): if PLAYER '>1 slot' == 0 ({pl_dupes} here), the player region has NO duplicate")
    print("              ghost copies -- there is nothing for a deployed-filter to remove.")
    print(f"  FIX lead:    if 'on-field rejects' caught the bad slots (PLAYER off-field = {pl_off} here) while")
    print("              keeping the real ones, the fix is: swap IsLiveCombatSlot -> IsOnFieldUnit (brave/faith")
    print("              + gx,gy bounds), drop the latch, assign positionally over the on-field set in N order.")


if __name__ == "__main__":
    main()
