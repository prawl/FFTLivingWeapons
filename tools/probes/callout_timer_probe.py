"""READ-ONLY watcher for the callout linger timer + selector surface (Track B dig part 2).

Confirms two disasm-derived claims live (docs/CALLOUT_BANNER_JOURNEY.md, "Track B dig part 2"):
  1. [ctrl+0xE8] (u32) is the LINGER COUNTDOWN -- ShowBubbleCallout 0x1400EF494 arms it to 0x78
     (120 ticks) at show start and a per-tick routine decrements it to auto-dismiss.
  2. The manager singleton's selector I/O block: [mgr+0x3514] (u8 state; 4 = plain bubble,
     3 = voiced flavor) and [mgr+0x3518] (u32 chosen callout id), written by the Denuvo-VM
     selector 0x1400EE264 during a natural show.

ctrl resolves via the normative FOUR-U64-deref chain (CalloutDelivery.LogComparison):
  ctrl = [[[[0x143CD9DA8] + 0x10] + 0x48] + 0x58]      mgr = [0x143CD9DC0]
Also samples [ctrl+0xE0] (u8 show flag) so timer edges can be correlated with visibility.

Usage (game running; trigger a natural callout -- any attack/ability -- while it watches):
  python -u tools\\probes\\callout_timer_probe.py [seconds=120]

Prints one line per TRANSITION of any watched field (plus a heartbeat every 10s). 100%% read-only.
"""
import pathlib
import struct
import sys
import time

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
from treasure_flags import rpm, _require_game

ROOT = 0x143CD9DA8
MGR_SLOT = 0x143CD9DC0


def u64(a):
    b = rpm(a, 8)
    return struct.unpack("<Q", b)[0] if b else None


def u32(a):
    b = rpm(a, 4)
    return struct.unpack("<I", b)[0] if b else None


def u8(a):
    b = rpm(a, 1)
    return b[0] if b else None


def resolve_ctrl():
    g = u64(ROOT)
    p = u64(g + 0x10) if g else None
    subsys = u64(p + 0x48) if p else None
    return u64(subsys + 0x58) if subsys else None


def main():
    seconds = float(sys.argv[1]) if len(sys.argv) > 1 else 120.0
    _require_game()
    ctrl = resolve_ctrl()
    mgr = u64(MGR_SLOT)
    if not ctrl or not mgr:
        print(f"resolve failed: ctrl={ctrl} mgr={mgr} (in battle? game running?)")
        sys.exit(1)
    print(f"watching ctrl=0x{ctrl:X} (+0xE8 timer, +0xE0 flag) mgr=0x{mgr:X} (+0x3514 state, +0x3518 id) for {seconds:.0f}s")

    last = None
    t0 = time.time()
    beat = t0
    while time.time() - t0 < seconds:
        cur = (u32(ctrl + 0xE8), u8(ctrl + 0xE0), u8(mgr + 0x3514), u32(mgr + 0x3518))
        if cur != last:
            e8, e0, st, cid = cur
            print(f"{time.time()-t0:8.3f}s  timer={e8 if e8 is not None else '?':>5}  flag={e0}  selState={st}  calloutId={cid if cid is not None else '?':#x}"
                  if cid is not None else f"{time.time()-t0:8.3f}s  timer={e8}  flag={e0}  selState={st}  calloutId=?")
            last = cur
        now = time.time()
        if now - beat >= 10.0:
            print(f"{now-t0:8.3f}s  (heartbeat -- still watching)")
            beat = now
        time.sleep(0.008)   # ~125 Hz; a 33ms game tick cannot slip through
    print("done.")


if __name__ == "__main__":
    main()
