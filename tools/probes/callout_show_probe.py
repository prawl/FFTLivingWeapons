#!/usr/bin/env python
"""
Callout forced-show probe -- rungs 1 and 2 of the on-command ladder (AC 2026-07-02).

Constants are the live-proven identity facts from LivingWeapon/BannerPipe.cs (spike
provenance BannerSpike, 2026-07-02): holder vtable 0x140718278, id qword 0x999 at +0x08,
show flag byte at +0x88 (pulses 1 for the bubble's ~1s life during a natural callout),
text len/cap u32s at +0x30/+0x38. battleMode sentinel from Offsets.cs (0x1409069A0,
1.5-CONFIRMED -- the old 0x140900650 is stale): 0 = world map, 1/5 = enemy turns /
animations / cast targeting, 2/3/4 = live player-turn frames.

The DLL locates the holder once per launch and logs it -- grep the mod's livingweapon.log
for "banner-pipe: callout holder located at 0x" and pass that address here.

  callout_show_probe.py info  <hexaddr>          READ-ONLY: validate identity + field dump
  callout_show_probe.py watch <hexaddr> [secs]   READ-ONLY: byte-diff the holder struct.
                                                 Run across a NATURAL cast -> the rung-2
                                                 vector: holder-local fields the show path
                                                 touches, at 50ms granularity (sub-50ms
                                                 pulses and out-of-holder state -- e.g. the
                                                 orchestrator side, 0x140111E89 -- are
                                                 invisible here; those need a CE write-
                                                 breakpoint follow-up, not this diff)
  callout_show_probe.py poke  <hexaddr>          WRITE: set show flag +0x88 = 1, trace 5s.
                                                 RUNG 1: if the renderer polls the flag,
                                                 the bubble appears NOW.

poke preconditions (each failure mode below produced a false verdict in dry review):
  * DEV (LWDEV) build with BannerToasts on -- the F2 test key is dev-only; a -Prod build
    ignores F2 entirely.
  * In battle, on YOUR OWN idle turn (battleMode 2/3/4). The probe refuses mode 0 (the DLL
    never ticks out of battle, so the poked rise commits nothing and the flag reads as a
    dead mirror) and warns on 1/5 (a NATURAL callout can fire in those frames and confound
    both the clear and the bubble).
  * Press F2 BEFORE EVERY poke: each rise dequeues the toast head, so a burnt queue means
    the next poked rise commits nothing. A bubble with WRONG/stale text still proves the
    renderer-polls fact -- the commit plumbing is a separate, already-proven fact. Confirm
    the DLL half fired by grepping livingweapon.log for "hijacked onto the next callout".
  * Repeat 2-3 clean runs before writing the ledger row. A flag clear alone is never proof
    the RENDERER consumed it (the DLL commit path or a natural lifecycle could also have
    authored it) -- the ledger claim stays at "consistent with" until the screen agrees
    across repeats.

Never stomps a natural show: refuses to poke while the flag is already up, never restores
over a value the engine moved, and restores our own 1 -> 0 (identity re-checked, write
verified) on EVERY exit path including Ctrl+C -- a flag left parked at 1 would blind the
shipped DLL's rising-edge hijack for the rest of the launch. The WPM itself is a safe
heap-data poke; the game's REACTION to the forced state is the experiment.
"""
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ct_probe import PV, PV_W, find_pid, k32, rd, wr

HOLDER_VTABLE = 0x140718278
HOLDER_ID = 0x999
F_LEN, F_CAP, F_FLAG = 0x30, 0x38, 0x88
BATTLE_MODE = 0x1409069A0     # Offsets.cs BattleMode, 1.5-CONFIRMED
WATCH_SPAN = 0x200            # generous cover of the holder object for the rung-2 diff
POKE_TRACE_SECS = 5.0


def u32(b, o):
    return int.from_bytes(b[o:o + 4], "little")


def u64(b, o):
    return int.from_bytes(b[o:o + 8], "little")


def open_game(write=False):
    pid = find_pid("fft_enhanced")
    if not pid:
        sys.exit("FFT_enhanced.exe not running")
    h = k32.OpenProcess(PV_W if write else PV, False, pid)
    if not h:
        sys.exit("OpenProcess failed")
    return h


def identity(h, addr):
    b = rd(h, addr, 0x10)
    if b is None:
        return False, "holder unreadable"
    if u64(b, 0) != HOLDER_VTABLE:
        return False, f"vtable {u64(b, 0):#x} != {HOLDER_VTABLE:#x}"
    if u64(b, 8) != HOLDER_ID:
        return False, f"id {u64(b, 8):#x} != {HOLDER_ID:#x}"
    return True, "ok"


def battle_mode(h):
    b = rd(h, BATTLE_MODE, 1)
    return b[0] if b is not None else -1


def info(h, addr):
    ok, why = identity(h, addr)
    print(f"identity: {why}   battleMode: {battle_mode(h)}")
    if not ok:
        return
    b = rd(h, addr, 0xA0)
    if b is None:
        print("field read failed")
        return
    print(f"len +0x30 = {u32(b, F_LEN):#x}   cap +0x38 = {u32(b, F_CAP):#x}   "
          f"flag +0x88 = {b[F_FLAG]}")
    for row in range(0, 0xA0, 16):
        print(f"  +{row:03x}: {b[row:row + 16].hex(' ')}")


def watch(h, addr, secs):
    ok, why = identity(h, addr)
    if not ok:
        sys.exit(f"identity check FAILED ({why}) -- wrong address? re-grep the log")
    prev = rd(h, addr, WATCH_SPAN)
    if prev is None:
        sys.exit(f"initial {WATCH_SPAN:#x}-byte read failed -- the span may cross an "
                 "uncommitted page; shrink WATCH_SPAN and rerun")
    print(f"watching holder {addr:#x} +{WATCH_SPAN:#x} for {secs:.0f}s -- "
          "trigger a NATURAL callout (any ability cast) now")
    lost = 0
    t0 = time.perf_counter()
    while time.perf_counter() - t0 < secs:
        time.sleep(0.05)
        cur = rd(h, addr, WATCH_SPAN)
        t = time.perf_counter() - t0
        if cur is None:
            # keep the old baseline: changes spanning the gap still surface on the next
            # good read (with coarser timing), instead of silently vanishing
            lost += 1
            print(f"t=+{t:7.3f}s sample LOST -- next diff spans the gap")
            continue
        if cur != prev:
            i = 0
            while i < WATCH_SPAN:
                if cur[i] != prev[i]:
                    j = i
                    while j < WATCH_SPAN and cur[j] != prev[j]:
                        j += 1
                    print(f"t=+{t:7.3f}s +{i:03x}..+{j - 1:03x}: "
                          f"{prev[i:j].hex()} -> {cur[i:j].hex()}")
                    i = j
                else:
                    i += 1
            prev = cur
    print(f"watch done -- {lost} lost sample(s). The rung-2 vector = the holder-local "
          "fields above beyond +0x88, at 50ms granularity (sub-50ms pulses invisible).")


def poke(h, addr):
    ok, why = identity(h, addr)
    if not ok:
        sys.exit(f"identity check FAILED ({why}) -- NOT poking")
    mode = battle_mode(h)
    if mode <= 0:
        sys.exit(f"battleMode {mode} (not in a battle) -- the DLL never ticks here, so the "
                 "poked rise commits nothing and any verdict is false. Enter a battle.")
    if mode in (1, 5):
        print(f"WARNING: battleMode {mode} (animation/targeting frames) -- a NATURAL "
              "callout can fire here and confound the verdict. The clean recipe pokes on "
              "your OWN idle turn (mode 2/3/4). Continuing anyway.")
    b = rd(h, addr, 0x90)
    if b is None:
        sys.exit("field read failed -- NOT poking")
    if b[F_FLAG] == 1:
        sys.exit("show flag already up -- a natural callout is in flight; retry at a quiet moment")
    if b[F_FLAG] != 0:
        sys.exit(f"flag reads {b[F_FLAG]} (expected 0 or 1) -- unknown state, NOT poking")
    print(f"pre-poke: battleMode {mode}, len {u32(b, F_LEN):#x} cap {u32(b, F_CAP):#x}, flag 0")
    if not wr(h, addr + F_FLAG, b"\x01"):
        sys.exit("WPM failed")
    print("flag -> 1 (written). WATCH THE SCREEN.")
    t0 = time.perf_counter()
    last, cleared_at, rose_again = 1, None, False
    try:
        while time.perf_counter() - t0 < POKE_TRACE_SECS:
            time.sleep(0.05)
            cur = rd(h, addr + F_FLAG, 1)
            if cur is None:
                continue
            if cur[0] != last:
                t = time.perf_counter() - t0
                print(f"t=+{t:5.2f}s flag {last} -> {cur[0]}")
                if cur[0] == 0 and cleared_at is None:
                    cleared_at = t
                elif cur[0] != 0 and cleared_at is not None:
                    rose_again = True
                last = cur[0]
    finally:
        # verdict + guarded restore -- this block also runs on Ctrl+C, so the flag is never
        # left parked at 1 (which would blind the DLL's rising-edge hijack all launch)
        if cleared_at is None:
            ok2, why2 = identity(h, addr)
            cur = rd(h, addr + F_FLAG, 1) if ok2 else None
            if not ok2:
                print(f"holder no longer validates ({why2}) -- NOT restoring")
            elif cur is not None and cur[0] == 1:
                print("flag restored to 0" if wr(h, addr + F_FLAG, b"\x00")
                      else "restore FAILED (WPM refused -- page gone?) -- flag may still be 1")
            else:
                print("flag no longer holds our 1 -- engine moved it; leaving it alone")
            print("Flag never cleared at 20Hz. Fork on the SCREEN: bubble showed the whole "
                  "trace -> the byte is LEVEL-polled, rung-1 PASS (the writer clears it at "
                  "end-of-life in natural flow); no bubble -> dead mirror byte, escalate to "
                  "`watch` across a natural cast (rung 2).")
        else:
            print(f"flag cleared at +{cleared_at:.2f}s -- CONSISTENT with the engine "
                  "consuming the rise, but not proof of authorship (the DLL commit path or "
                  "a natural lifecycle could also have cleared it). Verdict = screen + "
                  "2-3 clean repeats.")
            if rose_again:
                print("flag ROSE AGAIN during the trace -- a natural show is likely in "
                      "flight; this run is CONTAMINATED, rerun on a quiet turn.")
        print("Post-run: grep livingweapon.log for \"hijacked onto the next callout\" to "
              "confirm the DLL committed OUR text on the poked rise; re-press F2 before "
              "every new attempt (each rise burns the queued toast).")


def main():
    if len(sys.argv) < 3 or sys.argv[1] not in ("info", "watch", "poke"):
        sys.exit(__doc__)
    mode, addr = sys.argv[1], int(sys.argv[2], 16)
    h = open_game(write=(mode == "poke"))
    if mode == "info":
        info(h, addr)
    elif mode == "watch":
        watch(h, addr, float(sys.argv[3]) if len(sys.argv) > 3 else 30.0)
    else:
        poke(h, addr)


if __name__ == "__main__":
    main()
