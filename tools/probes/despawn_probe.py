"""DESPAWN PROBE -- try to remove a live unit from the battlefield COMPLETELY (not just KO it).

WHY: HP=0 + dead-bit (battle_cheats kill_all) makes a unit count as DEFEATED -- native Victory fires
when a whole side is down -- but it leaves a standing corpse with the 3-heart crystal countdown over its
head. Dead, not GONE. For a clean 3v3 the undrafted extras must be REMOVED from the field.

A raw post-construction memory write cannot remove a SPRITE (the battlefield render is welded at
construction -- the same wall that blocks live re-skin; see fftmp-live-repaint-shallow). The one path that
despawns a unit cleanly is the engine's OWN event: CRYSTALLIZATION. When a dead unit's crystal/heart
counter (combat base +0x07, == band entry -0x15; found live 2026-06-16, crystal_counter_probe.py) reaches
0 on the unit's turn, the engine crystallizes it and it LEAVES the band -- and because that is engine-driven,
the sprite is removed by the engine, not by us.

So the candidate remove-primitive is:  HP=0  +  dead-bit (band +0x45 | 0x20)  +  crystal counter driven low,
then the unit crystallizes away on its next turn. This probe applies that to ONE target and WATCHES the
band, to answer:
  Q1  Does driving the counter low actually crystallize the unit (it leaves the band) -- and after how long
      / how many turns? (the counter decrements on the unit's OWN turn, so end turns to advance it.)
  Q2  Does the SPRITE vanish on screen (true engine despawn), or linger (render wall)?
  Q3  What residue is left -- a crystal / treasure tile on the square?

Reuses the PROVEN battle_cheats harness (guarded RPM/WPM, band addressing, _is_valid_entry). The only
writes are to the validated target entry. Run in a LIVE battle:

    python tools\\probes\\despawn_probe.py list
        # every live band unit: slot, side, br/fa, lvl, hp, pos, dead-bit, crystal-counter. Pick a target.

    python tools\\probes\\despawn_probe.py remove <brave> <faith> [seconds=120] [start_ctr=1]
        # apply HP=0 + dead-bit + set crystal counter = start_ctr (default 1 -> crystallizes on the unit's
        # NEXT turn via the natural decrement; pass 0 to try an immediate trigger). Then HOLD dead+HP0
        # (so it cannot revive) WITHOUT touching the counter again, and watch. END TURNS in game to advance
        # the dead unit's turn. Reports the instant its band entry disappears (despawn) and prints
        # hp/dead/counter/pos each second so you can correlate with the screen. Ctrl+C to stop.

    python tools\\probes\\despawn_probe.py --selftest
        # offline: verify the counter offset + harness import. No game required.
"""
import pathlib
import sys
import time

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
from battle_cheats import (
    rpm, ru8, ru16, wu8, wu16, _require_game,
    _band_entry_addr, _is_valid_entry, BAND_SLOTS, PLAYER_SLOT_THRESHOLD,
    A_LEVEL, A_BRAVE, A_FAITH, A_HP, A_MAXHP, A_GX, A_GY, A_DEAD_STATUS, A_DEAD_BIT,
)

# Crystal/heart countdown, band-entry-relative. crystal_counter_probe.py (2026-06-16): the counter sits at
# combat base +0x07, and battle_cheats addresses by BAND ENTRY = combat base + 0x1C, so entry-relative it is
# 0x07 - 0x1C = -0x15. At 0 on the unit's turn -> crystallize -> the unit leaves the band (engine despawn).
A_COUNTER = -0x15


def _read(s, e):
    return dict(
        slot=s, n=s - 24,
        side="PLAYER" if s >= PLAYER_SLOT_THRESHOLD else "ENEMY",
        lvl=ru8(e + A_LEVEL), br=ru8(e + A_BRAVE), fa=ru8(e + A_FAITH),
        hp=ru16(e + A_HP), mhp=ru16(e + A_MAXHP),
        gx=ru8(e + A_GX), gy=ru8(e + A_GY),
        dead=ru8(e + A_DEAD_STATUS), ctr=ru8(e + A_COUNTER),
    )


def cmd_list():
    _require_game()
    print(f"{'slot':>4} {'n':>4} {'side':<6} {'br':>3} {'fa':>3} {'lvl':>3} "
          f"{'hp':>9} {'pos':>8} {'ctr':>4} {'dead':>5}")
    print("-" * 60)
    any_seen = False
    for s in range(BAND_SLOTS):
        e = _band_entry_addr(s)
        if not _is_valid_entry(e):
            continue
        any_seen = True
        u = _read(s, e)
        dead = "Y" if (u["dead"] is not None and u["dead"] & A_DEAD_BIT) else ""
        hp = f"{u['hp']}/{u['mhp']}"
        print(f"{u['slot']:>4} {u['n']:>+4} {u['side']:<6} {u['br']:>3} {u['fa']:>3} {u['lvl']:>3} "
              f"{hp:>9} ({u['gx']:>2},{u['gy']:>2}) {str(u['ctr']):>4} {dead:>5}")
    if not any_seen:
        print("No live band units -- not in a battle, or wrong screen.")
        return
    print("\nPick one (note its br/fa): python despawn_probe.py remove <brave> <faith>")


def _locate(br, fa):
    """Find the band entry for brave/faith; prefer a real-position (gx/gy != 0,0) copy over a frozen twin."""
    cands = []
    for s in range(BAND_SLOTS):
        e = _band_entry_addr(s)
        if not _is_valid_entry(e):
            continue
        if ru8(e + A_BRAVE) == br and ru8(e + A_FAITH) == fa:
            cands.append((s, e))
    if not cands:
        return None
    real = [(s, e) for s, e in cands if (ru8(e + A_GX) or ru8(e + A_GY))]
    return (real or cands)[0]


def cmd_remove(br, fa, seconds, start_ctr):
    _require_game()
    loc = _locate(br, fa)
    if loc is None:
        print(f"no live unit with brave={br} faith={fa}. Run `list`.")
        sys.exit(1)
    s, e = loc
    u = _read(s, e)
    print(f"=== REMOVE target s{s} (n={u['n']:+d}) {u['side']} br={br} fa={fa} "
          f"lvl={u['lvl']} hp={u['hp']}/{u['mhp']} pos=({u['gx']},{u['gy']}) ctr={u['ctr']} ===")

    # One-shot apply: HP=0, set dead bit, set the crystal counter to start_ctr. We do NOT re-assert the
    # counter afterward -- we want the engine to tick it down to 0 and crystallize on the unit's turn.
    wu16(e + A_HP, 0)
    d = ru8(e + A_DEAD_STATUS)
    wu8(e + A_DEAD_STATUS, (d | A_DEAD_BIT) if d is not None else A_DEAD_BIT)
    wu8(e + A_COUNTER, start_ctr & 0xFF)
    print(f"applied: HP->0, dead-bit set, counter->{start_ctr}. Now END TURNS in game to advance this unit's")
    print("turn and WATCH it: does the body crystallize and VANISH? Any crystal/treasure tile left?\n")

    end = time.monotonic() + seconds
    last_log = 0.0
    while time.monotonic() < end:
        v = _locate(br, fa)
        if v is None:
            print(">>> target LEFT THE BAND -- it despawned (crystallized/cleared). Removal WORKED.")
            print("    Confirm on screen: sprite gone? crystal/treasure tile left behind?")
            return
        s, e = v
        # Hold ONLY the death state so it cannot revive; leave the counter alone so it can reach 0.
        if (ru16(e + A_HP) or 0) > 0:
            wu16(e + A_HP, 0)
        d = ru8(e + A_DEAD_STATUS)
        if d is not None and not (d & A_DEAD_BIT):
            wu8(e + A_DEAD_STATUS, d | A_DEAD_BIT)
        now = time.monotonic()
        if now - last_log >= 1.0:
            last_log = now
            cu = _read(s, e)
            print(f"  in band: hp={cu['hp']} dead=0x{(cu['dead'] or 0):02X} ctr={cu['ctr']} "
                  f"pos=({cu['gx']},{cu['gy']}) [+{seconds - (end - now):.0f}s]")
        time.sleep(0.1)

    print("\ntarget STILL in the band after the watch -- driving the counter low did NOT despawn it in time.")
    print("Notes to report: did the counter ever tick down on its turns? did the sprite change at all?")
    print("If the counter never moved, crystallization is gated on state we have not set -> we try the next lever.")


def _selftest():
    ok = True
    # Counter offset: combat +0x07 == band entry -0x15 (crystal_counter_probe.py).
    if A_COUNTER == -0x15:
        print("  A_COUNTER == -0x15 (combat +0x07)  OK")
    else:
        print(f"  A_COUNTER: FAIL expected -0x15 got {A_COUNTER}")
        ok = False
    # Dead-bit constant imported from battle_cheats.
    if A_DEAD_BIT == 0x20:
        print("  A_DEAD_BIT == 0x20  OK")
    else:
        print(f"  A_DEAD_BIT: FAIL expected 0x20 got 0x{A_DEAD_BIT:02X}")
        ok = False
    # Band addressing import works: slot 24 is the anchor (n=0).
    a24 = _band_entry_addr(24)
    a23 = _band_entry_addr(23)
    if a24 - a23 == 0x200:
        print(f"  band stride OK (slot24=0x{a24:X})")
    else:
        print(f"  band stride: FAIL slot24-slot23 = 0x{a24 - a23:X} (want 0x200)")
        ok = False
    return ok


def main():
    args = sys.argv[1:]
    if not args or args[0] in ("-h", "--help", "help"):
        print(__doc__)
        return
    if args[0] == "--selftest":
        print("despawn_probe self-test (no game required) ...")
        sys.exit(0 if _selftest() else 1)
    if args[0] == "list":
        cmd_list()
        return
    if args[0] == "remove":
        nums = [int(x) for x in args[1:] if x.lstrip("-").isdigit()]
        if len(nums) < 2:
            print("usage: despawn_probe.py remove <brave> <faith> [seconds=120] [start_ctr=1]")
            sys.exit(2)
        seconds = float(nums[2]) if len(nums) >= 3 else 120.0
        start_ctr = nums[3] if len(nums) >= 4 else 1
        cmd_remove(nums[0], nums[1], seconds, start_ctr)
        return
    print(f"unknown verb: {args[0]!r}")
    print(__doc__)
    sys.exit(2)


if __name__ == "__main__":
    main()
