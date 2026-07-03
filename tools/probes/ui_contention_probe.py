"""READ-ONLY UI-contention watcher: hunt the prompt banner's show flag (callout arc, Beast 2).

The on-demand toast design needs a "screen surface busy" predicate. Our bubble's controller is
  p = [[0x143CD9DA8] + 0x10];  ours = [[p + 0x48] + 0x58]   (flag +0xE0, linger +0xE8)
and the wrapper-family recon says SIBLING callout subsystems hang off the same parent:
  [p + 0x58] (ctrl id 0xBE7), [p + 0x68] (0x17CD), [p + 0x90] (0x1904, voiced child at +0xA0).
Hypothesis: the "Select a tile and press F to move" overlay is one of these siblings with an
analogous show flag. This probe samples, for ours + each sibling candidate, the 0x30-byte window
[+0xD0, +0x100) (bracketing the known flag/timer offsets) plus the battleMode sentinel, printing
a timestamped line on ANY change. Correlate timestamps with the scripted user actions.

Usage (game running, in battle):
  python -u tools\\probes\\ui_contention_probe.py [seconds=240]
"""
import pathlib
import struct
import sys
import time

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
from treasure_flags import rpm, _require_game

ROOT = 0x143CD9DA8
BATTLE_MODE = 0x1409069A0   # sentinel (Offsets.cs authoritative)
WIN_LO, WIN_HI = 0xD0, 0x100


def u64(a):
    b = rpm(a, 8)
    return struct.unpack("<Q", b)[0] if b else None


def resolve_targets():
    """(label, address) pairs: each candidate controller's watch window base."""
    g = u64(ROOT)
    p = u64(g + 0x10) if g else None
    if not p:
        return []
    out = []
    for off in (0x48, 0x58, 0x68, 0x90):
        child = u64(p + off)
        if not child:
            continue
        out.append((f"child{off:02X}", child))
        for hop in (0x58, 0xA0):        # +0x58 = ours' controller hop; +0xA0 = voiced sub-object
            sub = u64(child + hop)
            if sub and sub > 0x10000:
                out.append((f"c{off:02X}+{hop:02X}", sub))
    return out


def main():
    seconds = float(sys.argv[1]) if len(sys.argv) > 1 else 240.0
    _require_game()
    targets = resolve_targets()
    if not targets:
        print("parent chain unresolved (in battle? game running?)")
        sys.exit(1)
    for lab, addr in targets:
        print(f"  target {lab:10s} = 0x{addr:X}  (window +0x{WIN_LO:X}..+0x{WIN_HI:X})")

    last = {}
    t0 = time.time()
    beat = t0
    while time.time() - t0 < seconds:
        el = time.time() - t0
        bm = rpm(BATTLE_MODE, 1)
        key = ("battleMode",)
        cur = bm[0] if bm else None
        if last.get(key) != cur:
            print(f"{el:8.3f}s  battleMode {last.get(key)} -> {cur}")
            last[key] = cur
        for lab, addr in targets:
            win = rpm(addr + WIN_LO, WIN_HI - WIN_LO)
            if win is None:
                continue
            prev = last.get(lab)
            if prev is not None and prev != win:
                diffs = [f"+0x{WIN_LO + i:X}:{prev[i]:02X}->{win[i]:02X}"
                         for i in range(len(win)) if win[i] != prev[i]]
                print(f"{el:8.3f}s  {lab}: {' '.join(diffs[:12])}{' ...' if len(diffs) > 12 else ''}")
            last[lab] = win
        now = time.time()
        if now - beat >= 15.0:
            print(f"{now-t0:8.3f}s  (heartbeat)")
            beat = now
        time.sleep(0.016)   # ~60 Hz
    print("done.")


if __name__ == "__main__":
    main()
