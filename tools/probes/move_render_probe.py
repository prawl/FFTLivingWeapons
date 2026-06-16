#!/usr/bin/env python
"""
Move / render-sync discovery probe -- find the RENDER position a forced move
must also write so the SPRITE follows. This is the wall behind guaranteed
Knockback / teleport-other / displacement (the doors mid-battle repositioning
opens).

BACKGROUND. Writing grid gx/gy (band +0x33/+0x34) IS engine-authoritative --
AI paths and the turn logic act from the new tile -- but the RENDERER never
re-derives, so the sprite stays put (knockback_probe.py proved this and PARKED
it). The engine's OWN Move action repositions the sprite cleanly, so a separate
render/world position must exist. FFTHandsFree locates the ACTIVE-unit world
coords at 0x14077D360 (X, u16) / 0x14077D362 (Y, u16), noted "stale after move".

TWO EXPERIMENTS:
  (1) ACTIVE-UNIT world write -- write 0x14077D360/62 and watch whether the
      active unit's sprite follows. Sprite moves -> world coords drive the
      draw; hunt the PER-UNIT copy next. Reverts instantly -> the coords are
      re-derived each frame from a scene struct we have to find instead.
  (2) LEGIT-MOVE slot diff -- snapshot a unit's full 0x200 band+static slot,
      make a NORMAL in-game Move with it, snapshot again, diff. Any byte that
      tracks the destination besides gridX/gridY (+0x33/+0x34), facing (+0x35),
      or CT (+0x25) is a candidate per-unit render/animation field. Then `poke`
      that field alongside a forced gx/gy write and watch the sprite.

RUN IN A THROWAWAY BATTLE. RPM/WPM only -- a value the engine dislikes is the
ANSWER, not a crash. Snapshots land in %TEMP%/fft_probes/ (never in the repo).

USAGE (game running, a unit's turn live):
  python ct_probe.py dump                              # find a target's mhp/lvl
  python move_render_probe.py world                    # read active-unit world X/Y
  python move_render_probe.py worldpush <dx> <dy>      # nudge world coords, watch sprite
  python move_render_probe.py snap <mhp> <lvl> before  # dump full slot
  #   ... now make a NORMAL Move with that unit in-game, end its move ...
  python move_render_probe.py snap <mhp> <lvl> after   # dump again
  python move_render_probe.py diff before after        # changed offsets (candidates)
  python move_render_probe.py poke <mhp> <lvl> 0xNN <val> [band|static]  # test a candidate
"""
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ct_probe import PROC, PV_W, find_pid, k32, rd, u16, wr
from poison_probe import locate_blocking, band_ok

WORLD_X = 0x14077D360   # active-unit world/render X (u16) -- FFTHandsFree finding ("stale after move")
WORLD_Y = 0x14077D362   # active-unit world/render Y (u16)
SLOT = 0x200            # one unit's band/static slot stride
GX, GY, FACE, CT = 0x33, 0x34, 0x35, 0x25   # known position-adjacent fields (label them in the diff)
SNAPDIR = os.path.join(os.environ.get("TEMP", "/tmp"), "fft_probes")


def snap_path(tag):
    return os.path.join(SNAPDIR, f"move_snap_{tag}.bin")


def _w16(h, addr, val):
    wr(h, addr, (val & 0xFFFF).to_bytes(2, "little"))


def cmd_world(h):
    buf = rd(h, WORLD_X, 4)   # X at +0, Y at +2
    if not buf:
        print("could not read the active-unit world coords (no live turn?)")
        return
    print(f"active-unit world  X={u16(buf, 0)} Y={u16(buf, 2)}  (@{WORLD_X:08X}/{WORLD_Y:08X})")


def cmd_worldpush(h, dx, dy):
    buf = rd(h, WORLD_X, 4)
    if not buf:
        print("could not read world coords -- need a unit actively taking its turn")
        return
    ox, oy = u16(buf, 0), u16(buf, 2)
    nx, ny = ox + dx, oy + dy
    print(f"world ({ox},{oy}) -> ({nx},{ny})   [restore: worldpush {ox - nx} {oy - ny}]")
    _w16(h, WORLD_X, nx)
    _w16(h, WORLD_Y, ny)
    print("watching 10s -- DID THE ACTIVE UNIT'S SPRITE MOVE? (instant revert = re-derived each frame)")
    t0, last = time.time(), None
    while time.time() - t0 < 10:
        cur = rd(h, WORLD_X, 4)
        cxy = (u16(cur, 0), u16(cur, 2)) if cur else (None, None)
        if cxy != last:
            print(f"  t={time.time() - t0:4.1f}s  world={cxy}")
            last = cxy
        time.sleep(0.1)
    print("watch over. VERDICT IS VISUAL: did the sprite track the write?")


def cmd_snap(h, mhp, lvl, tag):
    os.makedirs(SNAPDIR, exist_ok=True)
    u = locate_blocking(h, mhp, lvl)
    blob = b""
    for name in ("band", "static"):
        b = rd(h, u[name], SLOT) or (b"\x00" * SLOT)
        blob += b
    with open(snap_path(tag), "wb") as f:
        f.write(blob)
    bb = rd(h, u["band"], SLOT) or (b"\x00" * SLOT)
    print(f"snap '{tag}' -> {snap_path(tag)}   (band gx={bb[GX]} gy={bb[GY]} face={bb[FACE]})")


def cmd_diff(tag_a, tag_b):
    try:
        a = open(snap_path(tag_a), "rb").read()
        b = open(snap_path(tag_b), "rb").read()
    except OSError as e:
        print(f"missing snapshot: {e}")
        return
    label = {GX: " <- gridX", GY: " <- gridY", FACE: " <- facing", CT: " <- CT"}
    for half, name in enumerate(("band", "static")):
        seg_a = a[half * SLOT:(half + 1) * SLOT]
        seg_b = b[half * SLOT:(half + 1) * SLOT]
        print(f"\n=== {name} slot diff ({tag_a} -> {tag_b}) ===")
        changed = False
        for off in range(min(len(seg_a), len(seg_b))):
            if seg_a[off] != seg_b[off]:
                changed = True
                print(f"  +0x{off:02X}: {seg_a[off]:3d} -> {seg_b[off]:3d}{label.get(off, '')}")
        if not changed:
            print("  (no byte changed -- render coords live OUTSIDE this slot; widen the hunt)")
    print("\nCandidates = changed bytes that are NOT gridX/gridY/facing/CT. Test one with `poke`,")
    print("then force a gx/gy move (knockback_probe.py push) and see if the sprite follows.")


def cmd_poke(h, mhp, lvl, off, val, which):
    u = locate_blocking(h, mhp, lvl)
    base = u[which]
    cur = rd(h, base + off, 1)
    if not cur:
        print(f"can't read {which} +0x{off:02X}")
        return
    old = cur[0]
    print(f"{which} +0x{off:02X}: {old} -> {val}   [restore: poke {mhp} {lvl} 0x{off:02X} {old} {which}]")
    wr(h, base + off, bytes([val & 0xFF]))
    print("watch the sprite. (RPM/WPM only -- if the engine rejects it, that's the answer.)")


def main():
    a = sys.argv
    mode = a[1] if len(a) > 1 else ""
    if mode not in ("world", "worldpush", "snap", "diff", "poke"):
        print(__doc__)
        return
    if mode == "diff":
        cmd_diff(a[2], a[3])
        return
    pid = find_pid(PROC)
    if not pid:
        print(f"{PROC} not running")
        return
    h = k32.OpenProcess(PV_W, False, pid)
    if not h:
        print("OpenProcess failed")
        return
    try:
        if mode == "world":
            cmd_world(h)
        elif mode == "worldpush":
            cmd_worldpush(h, int(a[2]), int(a[3]))
        elif mode == "snap":
            cmd_snap(h, int(a[2]), int(a[3]), a[4])
        elif mode == "poke":
            which = (a[6] if len(a) > 6 else "band").lower()
            cmd_poke(h, int(a[2]), int(a[3]), int(a[4], 0), int(a[5], 0), which)
    finally:
        k32.CloseHandle(h)


if __name__ == "__main__":
    main()
