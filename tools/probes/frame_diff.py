#!/usr/bin/env python
"""
Frame diff capture: sample every combat frame (array 0x141853CE0, 28 x 0x200) at ~30ms and log
byte changes with timestamps. First BASELINE seconds collect the idle-churn offsets (CT ticks,
timers, animation state) which are then suppressed, so what remains is action-driven state --
the instrument for finding where an AI action stores its target (the band +0x4F/0x50 tile bytes
proved to be a player-confirm-path artifact: never written on AI turns).

Read-only. Run it, let ONE enemy act, note who acted / who was hit, stop it, then grep the log
for bytes that took victim-shaped values (tile coords, slot index, uid) just before the hit.

USAGE (game running, IN a live battle):
    python -u frame_diff.py            # 2s baseline, then log non-churn changes until stopped
    python -u frame_diff.py 5          # 5s baseline
"""
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ct_probe import PROC, PV, find_pid, k32, rd

BASE = 0x141853CE0
STRIDE = 0x200
N = 28
PERIOD = 0.03


def main():
    baseline_s = float(sys.argv[1]) if len(sys.argv) > 1 else 2.0
    pid = find_pid(PROC)
    if not pid:
        sys.exit(f"{PROC} not running")
    h = k32.OpenProcess(PV, False, pid)
    last = [None] * N
    churn = set()
    try:
        print(f"baseline {baseline_s:.0f}s -- collecting idle churn offsets...")
        t0 = time.time()
        while time.time() - t0 < baseline_s:
            for i in range(N):
                cur = rd(h, BASE + i * STRIDE, STRIDE)
                if cur is None:
                    continue
                if last[i] is not None:
                    prev = last[i]
                    for o in range(STRIDE):
                        if cur[o] != prev[o]:
                            churn.add((i, o))
                last[i] = cur
            time.sleep(PERIOD)
        by_slot = {}
        for i, o in churn:
            by_slot.setdefault(i, []).append(o)
        print(f"churn: {len(churn)} (slot,offset) pairs suppressed: " +
              "; ".join(f"s{i}:[{','.join(hex(o) for o in sorted(v))}]" for i, v in sorted(by_slot.items())))
        print("capturing -- let ONE enemy act, then stop me. logging non-churn changes:")
        while True:
            for i in range(N):
                cur = rd(h, BASE + i * STRIDE, STRIDE)
                if cur is None or last[i] is None:
                    last[i] = cur
                    continue
                prev = last[i]
                if cur != prev:
                    t = time.strftime("%H:%M:%S") + f".{int(time.time() * 1000) % 1000:03d}"
                    diffs = [(o, prev[o], cur[o]) for o in range(STRIDE)
                             if cur[o] != prev[o] and (i, o) not in churn]
                    if diffs:
                        body = " ".join(f"+{o:#05x}:{a:02X}->{b:02X}" for o, a, b in diffs[:24])
                        more = f" (+{len(diffs) - 24} more)" if len(diffs) > 24 else ""
                        print(f"{t} slot {i:>2} {body}{more}")
                    last[i] = cur
            time.sleep(PERIOD)
    except KeyboardInterrupt:
        pass
    finally:
        k32.CloseHandle(h)


if __name__ == "__main__":
    main()
