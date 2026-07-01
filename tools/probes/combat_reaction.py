#!/usr/bin/env python
"""Live combat-band reaction reader/writer -- read + flip in-battle reaction bits.

The in-battle reaction system is a 4-byte BITFIELD at combat-unit +0x94..+0x97 (one bit per
~25 fixed reactions; NO arbitrary-id channel -- proven 2026-07-01). This probe reads the LIVE
combat band (anchor 0x141855CE0, stride 0x200; offsets from FFTHandsFree BandUnitValidator +
the LivingWeapon ledger) and can OR-set / clear a reaction bit on a chosen unit, HOLDING it
(the field may re-normalize -- same reason reaction-SUPPRESSION had to hold +0x94=0).

Bit map (byte offset within struct, mask) -> reaction, derived from the selector 0x14030B584
disasm (test <mask>,<+0x9x byte>). Only the ones needed for demos are named; others are ids.
Notably: Counter = +0x96 & 0x08 (monsters have this innately); Nature's Wrath = +0x95 & 0x01
(checked BEFORE Counter -> fires instead of it).

USAGE (game running, in a live battle):
  python combat_reaction.py dump
        # list valid on-field slots: team/hp/lvl/br/fa + reaction bytes + decoded reactions.
  python combat_reaction.py setbit <slot> <byteoff_hex> <mask_hex> [seconds=120]
        # OR-set a bit and HOLD it; restores original byte on exit.
        # Nature's Wrath onto slot 5: setbit 5 95 01
  python combat_reaction.py clrbit <slot> <byteoff_hex> <mask_hex> [seconds=120]
Env: FFT_PID overrides the auto-picked pid.
"""
import ctypes as C
from ctypes import wintypes as W
import os
import sys
import time

k32 = C.WinDLL("kernel32", use_last_error=True)
PV = 0x0400 | 0x0010            # QUERY_INFORMATION | VM_READ
PV_W = PV | 0x0020              # + VM_WRITE
BAND, STRIDE, SLOTS = 0x141855CE0, 0x200, 160

# offsets within a combat struct
O_TEAM, O_LVL, O_BR, O_FA = 0x04, 0x29, 0x2B, 0x2D
O_HP, O_MAXHP, O_GX, O_GY = 0x30, 0x32, 0x33, 0x34
O_REACT = 0x94  # 4-byte reaction bitfield +0x94..+0x97

# (byte offset, mask) -> short name (partial; from selector disasm)
BITS = {
    (0x95, 0x02): "CounterTackle", (0x95, 0x01): "Nature'sWrath", (0x95, 0x04): "MagickCounter",
    (0x96, 0x08): "Counter", (0x96, 0x01): "ManaShield",
}


def find_pid():
    if os.environ.get("FFT_PID"):
        return int(os.environ["FFT_PID"])
    import subprocess
    out = subprocess.check_output(
        ["tasklist", "/fi", "imagename eq FFT_enhanced.exe", "/fo", "csv", "/nh"],
        text=True, errors="ignore")
    best, bestmem = None, -1
    for line in out.splitlines():
        p = [x.strip('"') for x in line.split('","')]
        if len(p) >= 5 and p[0].lower().startswith("fft_enhanced"):
            mem = int(p[4].replace(",", "").replace("K", "").replace(" ", "") or 0)
            if mem > bestmem:
                best, bestmem = int(p[1]), mem
    if best is None:
        raise SystemExit("FFT_enhanced.exe not running")
    return best


def rd(h, addr, n):
    buf = C.create_string_buffer(n)
    got = C.c_size_t()
    if k32.ReadProcessMemory(h, C.c_void_p(addr), buf, n, C.byref(got)) and got.value == n:
        return buf.raw
    return None


def wr(h, addr, data):
    got = C.c_size_t()
    return bool(k32.WriteProcessMemory(h, C.c_void_p(addr), data, len(data), C.byref(got)))


def u16(b, o):
    return b[o] | (b[o + 1] << 8)


def decode(react4):
    out = []
    for (off, mask), name in BITS.items():
        if react4[off - O_REACT] & mask:
            out.append(name)
    return out


def valid(b):
    # NOTE: grid offsets unreliable on this band (+0x30..+0x33 = HP/MaxHP u16 pair),
    # so validity is HP + level + brave/faith range only.
    hp, mx, lv, br, fa = u16(b, O_HP), u16(b, O_MAXHP), b[O_LVL], b[O_BR], b[O_FA]
    return (1 <= mx < 2000 and hp <= mx and 1 <= lv <= 99 and 1 <= br <= 100
            and 1 <= fa <= 100)


def cmd_dump(h):
    print(f"combat band @0x{BAND:X} stride 0x{STRIDE:X}; reaction bitfield +0x{O_REACT:02X}\n")
    print(f"{'slot':>4} {'addr':>12} {'team':>4} {'lvl':>3} {'hp':>5}/{'max':<5} "
          f"{'br':>3} {'fa':>3}  react[94..97]  decoded")
    for s in range(SLOTS):
        base = BAND + s * STRIDE
        b = rd(h, base, 0x98)
        if b is None or not valid(b):
            continue
        r = b[O_REACT:O_REACT + 4]
        print(f"{s:>4} 0x{base:X} {b[O_TEAM]:>4} {b[O_LVL]:>3} {u16(b, O_HP):>5}/{u16(b, O_MAXHP):<5} "
              f"{b[O_BR]:>3} {b[O_FA]:>3}  {' '.join(f'{x:02X}' for x in r)}   {','.join(decode(r)) or '-'}")


def cmd_bit(h, slot, off, mask, seconds, set_it):
    base = BAND + slot * STRIDE
    addr = base + off
    orig = rd(h, addr, 1)
    if orig is None:
        print(f"slot {slot} unreadable")
        return
    orig = orig[0]
    tgt = (orig | mask) if set_it else (orig & ~mask & 0xFF)
    nm = BITS.get((off, mask), f"bit 0x{mask:02X}")
    print(f"slot {slot} @0x{addr:X}: {'SET' if set_it else 'CLEAR'} {nm}: 0x{orig:02X} -> 0x{tgt:02X}, "
          f"holding {seconds:.0f}s")
    print(f"  >>> get this unit HIT and watch which reaction fires. Ctrl-C or wait to restore.\n")
    reasserts = 0
    t0 = time.time()
    last = -1
    try:
        while time.time() - t0 < seconds:
            cur = rd(h, addr, 1)
            if cur is not None and cur[0] != tgt:
                wr(h, addr, bytes([tgt]))
                reasserts += 1
            s = int(time.time() - t0)
            if s != last and s % 5 == 0:
                print(f"  t={s:>3}s reasserts={reasserts}")
            last = s
            time.sleep(0.03)
    except KeyboardInterrupt:
        print("(interrupted)")
    finally:
        wr(h, addr, bytes([orig]))
        print(f"\nrestored slot {slot} +0x{off:02X} -> 0x{orig:02X}. reasserts={reasserts} "
              f"(0=held by us; many=engine re-normalizes -> it fights writes).")


def main():
    a = sys.argv
    mode = a[1] if len(a) > 1 else ""
    if mode not in ("dump", "setbit", "clrbit"):
        print(__doc__)
        return
    h = k32.OpenProcess(PV if mode == "dump" else PV_W, False, find_pid())
    if not h:
        raise SystemExit(f"OpenProcess failed err={C.get_last_error()}")
    try:
        if mode == "dump":
            cmd_dump(h)
        else:
            slot = int(a[2])
            off = int(a[3], 16)
            mask = int(a[4], 16)
            secs = float(a[5]) if len(a) > 5 else 120
            cmd_bit(h, slot, off, mask, secs, set_it=(mode == "setbit"))
    finally:
        k32.CloseHandle(h)


if __name__ == "__main__":
    main()
