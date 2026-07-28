#!/usr/bin/env python
"""
Tile coordinate-system calibration (`hover` READ-ONLY; `stick`/`dwell` write with guaranteed
file-signalled restore -- NEVER hard-kill these, touch %TEMP%/release_veto or the repo /tmp
release file instead).

WHY THIS EXISTS (2026-07-28, the Bulwark night)
-----------------------------------------------
Three tile coordinate FRAMES were conflated all night and produced apparently-contradictory
results (+2-in-x on some vetoes, exact on others, a softlock attributed to the wrong unit):

  A. COMBAT frame: unit gx/gy at combat +0x4F/+0x50 (band +0x33/+0x34).
  B. GRID frame:   the walkability grid 0x140D8DCC0 (8 B/tile), idx = x + y*W + layer*0x100.
     Proven consumers of byte +6 bit 0x01: the move-range builder (softlock, range hole) and
     the cursor mask (tile unselectable).
  C. CURSOR frame: the hover globals 0x140C6AFB8 (X) / 0x140C6ADAC (Y), which both the owner's
     eyeballs and several probes have used as "the" coordinates.

A==B was proven once (vetoing the idx computed from a unit's own combat coords softlocked that
unit), but two observations landed "+2 in x" from the intended tile, which is consistent with
C != A or with observation error. This probe measures every pairwise offset EMPIRICALLY, with
identity-based questions (which UNIT is stuck) instead of coordinate-based ones (which tile),
because coordinate answers are exactly what got polluted.

THE PROTOCOL
------------
  1. python tile_cal.py hover
       READ-ONLY. Continuously samples the cursor globals and every live unit's combat gx/gy.
       In game, park the hover cursor squarely ON each of your units, one at a time, ~2s each.
       When the cursor is stationary the probe prints the cursor frame value beside every
       unit's combat value and the implied delta. Three units nail cursor<->combat exactly,
       including any x/y swap. No writes.

  2. python tile_cal.py stick <seat>
       Vetoes (byte+6 |= 1) the grid idx computed from THAT SEAT's combat gx/gy, holds until
       released. The one question, identity-based: WHICH unit cannot move / cannot be selected?
       If it is that seat's unit, grid==combat with zero offset for this tile; repeat on a
       second seat elsewhere on the map to rule out coincidence. Release:
       create the release file printed at start (touch it), which restores the byte FIRST.

  3. python tile_cal.py dwell [--dx N --dy N]
       The closed-loop eyeball test, AFTER 1+2 fixed the transforms: watches the cursor; when
       you dwell on any tile ~1.2s it vetoes the grid idx computed from the CURSOR frame
       (optionally shifted by --dx/--dy while testing a hypothesis), holds 6s, restores, and
       keeps going. You hover a tile, then immediately try to select it: if selection refuses
       on exactly the hovered tile, the cursor->grid transform is right; if the refusal shows
       up elsewhere, say where, and the delta IS the correction. Several tiles across corners
       and center make it conclusive.

Every write is byte +6 bit 0x01 only, original byte saved, restored on release or window end;
the release-file pattern exists because a hard kill skips finally-restores (proven tonight:
stale bit0 dirt survived a battle RETRY and had to be hand-cleared).
"""
import argparse
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ct_probe import PROC, PV_W, find_pid, k32, rd, wr  # noqa: E402

GRID = 0x140D8DCC0
CUR_X, CUR_Y = 0x140C6AFB8, 0x140C6ADAC
MAP_WH = 0x140C6AD6A
SEAT_BASE = 0x141855CE0 + 0x1C - 24 * 0x200
RELEASE = os.path.join(os.environ.get("TEMP", "/tmp"), "release_veto")


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


def units(h):
    """Live on-map units: (seat, gx, gy, layer, lvl, hp, mhp)."""
    out = []
    w = u8(h, MAP_WH) or 20
    for s in range(49):
        b = rd(h, SEAT_BASE + s * 0x200, 0x60)
        if not b:
            continue
        lvl = b[0x0D]
        hp = b[0x14] | (b[0x15] << 8)
        mhp = b[0x16] | (b[0x17] << 8)
        gx, gy = b[0x33], b[0x34]
        if 1 <= lvl <= 99 and 1 <= mhp <= 9999 and gx < w and gy < 40 and (gx, gy) != (0, 0):
            out.append((s, gx, gy, (b[0x35] >> 7) & 1, lvl, hp, mhp))
    return out


def cmd_hover(a):
    h = open_game()
    print("park the hover cursor ON each of your units, ~2s each; Ctrl-C to stop.\n")
    last = None
    stable_since = 0.0
    reported = set()
    while True:
        cx, cy = u8(h, CUR_X), u8(h, CUR_Y)
        if (cx, cy) != last:
            last = (cx, cy)
            stable_since = time.time()
            reported.discard("cur")
        elif time.time() - stable_since > 1.0 and "cur" not in reported and cx is not None:
            reported.add("cur")
            print(f"cursor stationary at CURSOR-frame ({cx},{cy}):")
            for s, gx, gy, lay, lvl, hp, mhp in units(h):
                dx, dy = cx - gx, cy - gy
                tag = "  <== EXACT" if (dx, dy) == (0, 0) else (f"  delta ({dx:+d},{dy:+d})" if abs(dx) <= 2 and abs(dy) <= 2 else "")
                if tag:
                    print(f"    seat {s:>2} combat ({gx},{gy}) lvl{lvl} hp{hp}/{mhp}{tag}")
        time.sleep(0.15)


def hold_loop(h, held, note):
    print(f"{note}\nrelease: create {RELEASE}  (touch it); restore happens BEFORE exit.")
    try:
        while not os.path.exists(RELEASE):
            for addr, orig in held:
                c = rd(h, addr, 1)
                if c and not (c[0] & 1):
                    wr(h, addr, bytes([c[0] | 1]))
            time.sleep(0.1)
    except KeyboardInterrupt:
        pass
    finally:
        for addr, orig in held:
            wr(h, addr, bytes([orig]))
        print("restored")
        try:
            os.remove(RELEASE)
        except OSError:
            pass


def cmd_stick(a):
    h = open_game()
    w = u8(h, MAP_WH)
    target = None
    for s, gx, gy, lay, lvl, hp, mhp in units(h):
        if s == a.seat:
            target = (s, gx, gy, lay, lvl, hp, mhp)
    if not target:
        print("seat not live; live units:")
        for s, gx, gy, lay, lvl, hp, mhp in units(h):
            print(f"  seat {s:>2} combat ({gx},{gy}) lvl{lvl} hp{hp}/{mhp}")
        sys.exit(1)
    s, gx, gy, lay, lvl, hp, mhp = target
    idx = gx + gy * w + lay * 0x100
    addr = GRID + idx * 8 + 6
    orig = u8(h, addr)
    wr(h, addr, bytes([orig | 1