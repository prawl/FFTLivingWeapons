"""Render-node move watch: the instrument that cracked the render position (2026-07-10, LW-65).

Two-phase byte watch on ONE unit's render node (list head 0x140D3A410, node +0x148 = combat
back-pointer, 0x548 bytes). Phase 1 (~6s): the unit stands IDLE; every offset that changes goes
into the NOISE MASK (idle-animation counters). Phase 2 (90s): the operator moves the unit one
tile in game; offsets that edge now but were quiet at idle are movement-driven fields. This walk
surfaced the world coords (node +0x4C/+0x4E/+0x50 = world X/Z/Y; X=28x+14, Y=28y+14, Z=-12h),
the engine-maintained AI tile key (+0x88/89/8A), and the +0x320/328/330/338 quad-bound block.
Read-only. Full findings: docs/MECHANICS.md (the teleport lever) + the LW-65 ledger row.

    python tools\\probes\\node_move_watch.py <combat-slot 0..20>
"""
import pathlib
import struct
import sys
import time

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
from battle_cheats import rpm, _require_game

UNITS = 0x141853CE0
HEAD = 0x140D3A410
NODE_SIZE = 0x548

def u64(a):
    b = rpm(a, 8)
    return None if b is None else struct.unpack("<Q", b)[0]

def main():
    slot = int(sys.argv[1]) if len(sys.argv) > 1 else 16
    _require_game()
    combat = UNITS + slot * 0x200
    node = None
    cur = u64(HEAD)
    for _ in range(64):
        if not cur:
            break
        if u64(cur + 0x148) == combat:
            node = cur
            break
        cur = u64(cur)
    if node is None:
        print(f"slot {slot} has no node in the list; aborting.")
        sys.exit(1)
    print(f"watching slot {slot}'s node 0x{node:X}. Phase 1: keep the unit IDLE ~6s (noise mask)...",
          flush=True)

    snap = rpm(node, NODE_SIZE)
    noise = set()
    end = time.monotonic() + 6.0
    while time.monotonic() < end:
        time.sleep(0.1)
        cur_b = rpm(node, NODE_SIZE)
        if cur_b is None:
            continue
        for i in range(NODE_SIZE):
            if cur_b[i] != snap[i]:
                noise.add(i)
        snap = cur_b
    print(f"noise mask: {len(noise)} offsets (idle animation). Phase 2: MOVE THE UNIT ONE TILE NOW "
          "(90s window). Reporting only non-noise edges...", flush=True)

    hits = {}
    end = time.monotonic() + 90.0
    while time.monotonic() < end:
        time.sleep(0.1)
        cur_b = rpm(node, NODE_SIZE)
        if cur_b is None:
            continue
        for i in range(NODE_SIZE):
            if cur_b[i] != snap[i] and i not in noise:
                if i not in hits:
                    hits[i] = 0
                    print(f"  node+0x{i:03X}: {snap[i]:02X} -> {cur_b[i]:02X}", flush=True)
                hits[i] += 1
        snap = cur_b

    if not hits:
        print("no non-noise offsets changed; did the unit move?")
        return
    ks = sorted(hits)
    runs, start, prev = [], ks[0], ks[0]
    for k in ks[1:]:
        if k == prev + 1:
            prev = k
            continue
        runs.append((start, prev))
        start = prev = k
    runs.append((start, prev))
    print("\n=== movement-driven fields (contiguous non-noise spans) ===")
    for a, b in runs:
        print(f"  node+0x{a:03X}..0x{b:03X} ({b - a + 1}B, {sum(hits[i] for i in range(a, b + 1))} edges)")

if __name__ == "__main__":
    main()
