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
  B. GRID frame:   the walkability grid 0x140D8DCB0 (8 B/tile), idx = x + y*W + layer*0x100.
     (BASE AND BIT CORRECTED 2026-07-28 per docs/LIVE_LEDGER.md: this file shipped with the
     contradicted base 0x140D8DCC0 and bit 0x01, which wrote every veto 2 tiles east of target;
     the ledger's four-sided lock-in settled 0x140D8DCB0 and walkability = byte +6 bit 0x02.)
     Proven consumers of byte +6 bit 0x02: the move-range builder (softlock, range hole) and
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
       Vetoes (byte+6 |= 2) the grid idx computed from THAT SEAT's combat gx/gy, holds until
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

Every write is byte +6 bit 0x02 only, original byte saved, restored on release or window end;
the release-file pattern exists because a hard kill skips finally-restores (proven tonight:
stale bit0 dirt survived a battle RETRY and had to be hand-cleared).
"""
import argparse
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ct_probe import PROC, PV_W, find_pid, k32, rd, wr  # noqa: E402

GRID = 0x140D8DCB0   # CORRECTED 2026-07-28 (was 0x140D8DCC0, the 2-tiles-east decoy; LIVE_LEDGER)
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
                if c and not (c[0] & 2):
                    wr(h, addr, bytes([c[0] | 2]))
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
    if w is None:
        print("MAP_WH unreadable; refusing to compute a tile index (no write attempted).")
        sys.exit(1)
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
    if orig is None:
        print(f"addr 0x{addr:X} (idx 0x{idx:X}) unreadable; refusing to write a wrong-tile veto.")
        sys.exit(1)
    wr(h, addr, bytes([orig | 2]))
    hold_loop(h, [(addr, orig)],
              f"veto held on seat {s}'s own tile ({gx},{gy}) layer {lay} (idx 0x{idx:X}, "
              f"addr 0x{addr:X}, orig 0x{orig:02X}). WHICH unit is stuck?")


def cmd_dwell(a):
    # NOTE (recovered 2026-07-28): the committed file was TRUNCATED mid-cmd_stick (an
    # interrupted write on the Bulwark night), so this verb never ran; rebuilt from the
    # protocol doc above. The closed-loop eyeball test: dwell ~1.2s on a tile, the probe
    # vetoes the CURSOR-frame idx (shifted by --dx/--dy while testing a hypothesis), holds
    # 6s, restores, keeps going.
    h = open_game()
    w = u8(h, MAP_WH)
    if w is None:
        print("MAP_WH unreadable; refusing to compute any tile index (no write attempted).")
        sys.exit(1)
    print("WARNING: this verb assumes layer 0 for every veto -- the cursor frame carries no "
          "layer bit (unlike a unit's own combat gx/gy, whose layer rides band +0x35 bit 0x80). "
          "On a bridge-deck map (units stacked at the same x,y on two layers) a dwell veto may "
          "land on the wrong deck; treat dwell results there as untrustworthy.")
    print(f"dwell mode: hover any tile ~1.2s to veto it (dx={a.dx} dy={a.dy}); Ctrl-C or touch "
          f"{RELEASE} to stop.")
    last = None
    stable_since = 0.0
    fired = False
    try:
        while True:
            if os.path.exists(RELEASE):
                print("release file seen; stopping.")
                try:
                    os.remove(RELEASE)
                except OSError:
                    pass
                return
            cx, cy = u8(h, CUR_X), u8(h, CUR_Y)
            if (cx, cy) != last:
                last = (cx, cy)
                stable_since = time.time()
                fired = False
            elif not fired and cx is not None and time.time() - stable_since > 1.2:
                fired = True
                tx, ty = cx + a.dx, cy + a.dy
                idx = tx + ty * w   # layer 0: the dwell test targets ground tiles
                addr = GRID + idx * 8 + 6
                orig = u8(h, addr)
                if orig is None:
                    print(f"  ({tx},{ty}) idx 0x{idx:X}: unreadable, skipped")
                    continue
                released_early = False
                # The write + hold + restore live in one try/finally so a Ctrl-C (or any other
                # exception) during the 6s hold still restores this byte: the finally clause runs
                # BEFORE KeyboardInterrupt propagates, so by the time the outer except below prints
                # anything, this veto (if any was live) is already gone.
                try:
                    wr(h, addr, bytes([orig | 2]))
                    print(f"  veto ({tx},{ty}) idx 0x{idx:X} addr 0x{addr:X} for 6s -- try to select the hovered tile NOW")
                    t0 = time.time()
                    while time.time() - t0 < 6.0:
                        if os.path.exists(RELEASE):
                            released_early = True
                            break
                        c = rd(h, addr, 1)
                        if c and not (c[0] & 2):
                            wr(h, addr, bytes([c[0] | 2]))
                        time.sleep(0.1)
                finally:
                    wr(h, addr, bytes([orig]))
                    print("  restored")
                if released_early:
                    print("release file seen mid-hold; stopping.")
                    try:
                        os.remove(RELEASE)
                    except OSError:
                        pass
                    return
            time.sleep(0.15)
    except KeyboardInterrupt:
        print("stopped (Ctrl-C): any veto that was in progress has just been restored by its "
              "own finally block above; nothing is left held.")


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)
    sub.add_parser("hover").set_defaults(fn=cmd_hover)
    p_stick = sub.add_parser("stick")
    p_stick.add_argument("seat", type=int)
    p_stick.set_defaults(fn=cmd_stick)
    p_dwell = sub.add_parser("dwell")
    p_dwell.add_argument("--dx", type=int, default=0)
    p_dwell.add_argument("--dy", type=int, default=0)
    p_dwell.set_defaults(fn=cmd_dwell)
    a = ap.parse_args()
    a.fn(a)


if __name__ == "__main__":
    main()