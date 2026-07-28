#!/usr/bin/env python
"""
Facing-direction probe (READ-ONLY, no writes anywhere) for the Bulwark back-tile design.

WHAT IT SETTLES (2026-07-28): which field is the unit's FACING, and does it track the Wait
facing wheel live and hold the CONFIRMED direction at turn end. Bulwark plants its block on
the ONE tile BEHIND the wielder at wait-commit, so the confirmed value is the load-bearing one.

CANDIDATES, with provenance:
  1. band +0x35 low 2 bits (combat frame +0x51). FFTHandsFree's FacingByteDecoder.cs reads
     this byte (their static-array slot +0x35, session-30 live-verified on 4 units):
     0=South(+y) 1=West 2=North 3=East. It aligns with the PSX battle struct (FFTHandsFree's
     BATTLE_STATS_PSX_REFERENCE doc: 0x47 X, 0x48 Y, 0x49 elevation/facing bits 0x03)
     exactly as our proven +0x33 gx / +0x34 gy / +0x35 bit7 layer trio does. CAVEAT forcing
     this live pass: HandsFree logged a real anomaly 2026-04-24 "player facing byte read East
     while visually facing West", so the byte may lag the wheel or the static copy may be
     stale for the player. Both copies are watched to split that.
  2. render node +0x2C u16 = facing angle, 0x2000-step diagonals (position-write work,
     LW-58 session). Render-side truth; should rotate live with the wheel.
  3. node +0x2E (unidentified; swapped-along in the position swap) and +0x7C (live-falsified
     as a facing INPUT for animation requests, meaning unknown) print as bonus columns.

RESULT 2026-07-28 (same night; LIVE_LEDGER Uncertain row "Unit FACING is band +0x35 low 2
bits"): SETTLED, band +0x35 low2 IS the facing and tracks the wheel LIVE. Owner rotated
Ramza's Wait wheel: seat 24 band+35 stepped 0x01 W -> 0x02 N pre-confirm (t=282.24s), node
+0x2C mirrored it (W=0x6000, N=0xE000). The static-array copy fingerprint-matched ZERO live
slots (stale/dead on current patch, the likely home of HandsFree's 2026-04-24 East/West
anomaly): consume the BAND copy. Bonus capture: a battle teardown (t=288.75s) zeroes every
seat's +0x35 and node transform for ~250ms then re-stamps INITIAL facings, so a facing
consumer must tolerate a transient 0x00 sweep. The confirm-stamp read was cut off by that
rollback; it folds into the Bulwark owner live pass. The hunt verb was never needed.

VERBS
  python -u facing_probe.py watch [--secs 600]
      On-change table of every live seat: band +0x35 (hex + decoded), the fingerprint-matched
      static-array +0x35, and the seat's render-node +0x2C/+0x2E/+0x7C. Owner protocol: on
      Ramza's turn choose Wait, rotate the facing wheel through all four directions holding
      each ~2s, then CONFIRM one and say the order out loud. Only Ramza's fields should move.
  python -u facing_probe.py hunt --seat N [--secs 90]
      Fallback if nothing above tracks: two-phase idle-noise-masked diff over the whole seat
      struct (0x200) + render node (0x548). Mask 6s of idle, then print unmasked changes.
"""
import argparse
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ct_probe import PROC, PV_W, find_pid, k32, rd  # noqa: E402

SEAT_BASE = 0x141855CE0 + 0x1C - 24 * 0x200   # band-layout combat seats, stride 0x200
SEATS = 49
STATIC_BASE = 0x140893C00                      # the static battle array (HandsFree's copy)
STATIC_SLOTS = range(-20, 12)                  # ct_probe's proven slot window
NODE_HEAD = 0x140D3A410                        # [head] = render-node array, stride 0x548
NODE_STRIDE = 0x548
NODE_BACKREF = 0x148                           # [node+0x148] = combat backref
MAP_WH = 0x140C6AD6A
FACE = {0: "S", 1: "W", 2: "N", 3: "E"}


def open_game():
    pid = find_pid(PROC)
    if not pid:
        sys.exit(f"{PROC} not running")
    h = k32.OpenProcess(PV_W, False, pid)
    if not h:
        sys.exit(f"could not open pid {pid}")
    return h


def u8(h, a):
    b = rd(h, a, 1)
    return b[0] if b else None


def u16(h, a):
    b = rd(h, a, 2)
    return b[0] | (b[1] << 8) if b else None


def u64(h, a):
    b = rd(h, a, 8)
    return int.from_bytes(b, "little") if b else None


def live_seats(h):
    """(seat, gx, gy, lvl, hp, mhp) for plausibly-live on-map units (tile_cal filter)."""
    out = []
    w = u8(h, MAP_WH) or 20
    for s in range(SEATS):
        b = rd(h, SEAT_BASE + s * 0x200, 0x60)
        if not b:
            continue
        lvl = b[0x0D]
        hp = b[0x14] | (b[0x15] << 8)
        mhp = b[0x16] | (b[0x17] << 8)
        gx, gy = b[0x33], b[0x34]
        if 1 <= lvl <= 99 and 1 <= mhp <= 9999 and gx < w and gy < 40 and (gx, gy) != (0, 0):
            out.append((s, gx, gy, lvl, hp, mhp))
    return out


def match_static(h, gx, gy, lvl, mhp):
    """Fingerprint the static-array slot mirroring a seat; None if not exactly one match."""
    hits = []
    for s in STATIC_SLOTS:
        base = STATIC_BASE + s * 0x200
        b = rd(h, base, 0x60)
        if not b:
            continue
        if b[0x0D] == lvl and (b[0x16] | (b[0x17] << 8)) == mhp and b[0x33] == gx and b[0x34] == gy:
            hits.append(base)
    return hits[0] if len(hits) == 1 else None


def find_nodes(h):
    """{seat: node_addr} by backref: [node+0x148] landing inside a seat's 0x200 window."""
    head = u64(h, NODE_HEAD)
    nodes = {}
    if not head or head < 0x10000:
        return nodes
    for i in range(64):
        node = head + i * NODE_STRIDE
        p = u64(h, node + NODE_BACKREF)
        if p is None:
            break
        if p < 0x10000:
            continue
        off = p - SEAT_BASE
        if 0 <= off < SEATS * 0x200:
            nodes[off // 0x200] = node
    return nodes


def decode(raw):
    if raw is None:
        return "??"
    return f"0x{raw:02X} face {FACE.get(raw & 3, '?')} layer {(raw >> 7) & 1}"


def cmd_watch(a):
    h = open_game()
    t0 = time.time()
    seats = live_seats(h)
    if not seats:
        print("no live seats yet (not in a battle?); watching anyway.")
    statics = {s: match_static(h, gx, gy, lvl, mhp) for s, gx, gy, lvl, hp, mhp in seats}
    nodes = find_nodes(h)
    print(f"{len(seats)} live seat(s); static matched {sum(1 for v in statics.values() if v)}; "
          f"nodes resolved {len(nodes)} (backref convention: seat-window hit)")
    for s, gx, gy, lvl, hp, mhp in seats:
        band = u8(h, SEAT_BASE + s * 0x200 + 0x35)
        print(f"  seat {s:>2} ({gx},{gy}) lvl{lvl} hp{hp}/{mhp}  band+35 {decode(band)}"
              f"  node {'@0x%X' % nodes[s] if s in nodes else 'UNRESOLVED'}")
    print("\nROTATE THE WAIT FACING WHEEL NOW (hold each direction ~2s, then confirm).\n")
    last = {}
    end = time.time() + a.secs
    while time.time() < end:
        ts = time.time() - t0
        rescan = False
        for s, gx, gy, lvl, hp, mhp in seats:
            addr = SEAT_BASE + s * 0x200
            band = u8(h, addr + 0x35)
            st = statics.get(s)
            stat = u8(h, st + 0x35) if st else None
            node = nodes.get(s)
            ang = u16(h, node + 0x2C) if node else None
            x2e = u16(h, node + 0x2E) if node else None
            x7c = u16(h, node + 0x7C) if node else None
            cur = (band, stat, ang, x2e, x7c)
            if last.get(s) != cur:
                prev = last.get(s)
                last[s] = cur
                if prev is None:
                    continue  # first sample after (re)scan: baseline, not an event
                parts = [f"[{ts:8.2f}s] seat {s:>2}"]
                if prev[0] != band:
                    parts.append(f"band+35 {decode(prev[0])} -> {decode(band)}")
                if prev[1] != stat:
                    parts.append(f"static+35 {decode(prev[1])} -> {decode(stat)}")
                if prev[2] != ang:
                    parts.append(f"node+2C 0x{(prev[2] or 0):04X} -> 0x{(ang or 0):04X}")
                if prev[3] != x2e:
                    parts.append(f"node+2E 0x{(prev[3] or 0):04X} -> 0x{(x2e or 0):04X}")
                if prev[4] != x7c:
                    parts.append(f"node+7C 0x{(prev[4] or 0):04X} -> 0x{(x7c or 0):04X}")
                print("  ".join(parts), flush=True)
        if not seats or rescan:
            seats = live_seats(h)
            statics = {s: match_static(h, gx, gy, lvl, mhp) for s, gx, gy, lvl, hp, mhp in seats}
            nodes = find_nodes(h)
        time.sleep(0.05)
    print("watch window over.")


def cmd_hunt(a):
    h = open_game()
    seat_addr = SEAT_BASE + a.seat * 0x200
    node = find_nodes(h).get(a.seat)
    regions = [("seat", seat_addr, 0x200)] + ([("node", node, NODE_STRIDE)] if node else [])
    print(f"masking idle noise for 6s over {[r[0] for r in regions]} (do NOTHING in game)...")
    noisy = {r[0]: set() for r in regions}
    base = {r[0]: rd(h, r[1], r[2]) for r in regions}
    end = time.time() + 6
    while time.time() < end:
        for name, addr, size in regions:
            cur = rd(h, addr, size)
            if cur and base[name]:
                noisy[name].update(i for i in range(size) if cur[i] != base[name][i])
                base[name] = cur
        time.sleep(0.05)
    print(f"masked: {sum(len(v) for v in noisy.values())} noisy byte(s). "
          f"NOW rotate the facing wheel; unmasked changes print for {a.secs}s.")
    t0 = time.time()
    while time.time() - t0 < a.secs:
        for name, addr, size in regions:
            cur = rd(h, addr, size)
            if cur and base[name]:
                for i in range(size):
                    if cur[i] != base[name][i] and i not in noisy[name]:
                        print(f"[{time.time() - t0:6.2f}s] {name}+0x{i:03X}: "
                              f"0x{base[name][i]:02X} -> 0x{cur[i]:02X}", flush=True)
                base[name] = cur
        time.sleep(0.05)


def main():
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = p.add_subparsers(dest="cmd", required=True)
    w = sub.add_parser("watch")
    w.add_argument("--secs", type=int, default=600)
    w.set_defaults(fn=cmd_watch)
    ht = sub.add_parser("hunt")
    ht.add_argument("--seat", type=int, required=True)
    ht.add_argument("--secs", type=int, default=90)
    ht.set_defaults(fn=cmd_hunt)
    a = p.parse_args()
    a.fn(a)


if __name__ == "__main__":
    main()
