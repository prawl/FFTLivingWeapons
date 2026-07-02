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


# --- stamp mode: rung 3.5, force the BANNER OBJECT's show-state machine by pure writes ---
# Vector captured live 2026-07-02 17:27 (struct_watch 0x436B4435A0 across a natural Focus
# callout, t=+49.975 show / t=+51.083 hide): flags +0x1A0=1 +0x1A5=0 +0x1DC=1 +0x232=1,
# state byte +0x280 idle 0x0B -> 1 (appearing) -> 2 (showing); anchor coord u16s at
# +0x148/+0x150/+0x158 (left stale on purpose -- bubble reuses the last natural anchor).
# The show CALL (0x140409A00) executed from the DLL renders nothing (V1/V2 blank), so the
# hypothesis is a poll-driven state machine: write the state, let the engine's own update
# animate it. Class identity guard: [obj+0x218] == 0xD (the orchestrator's own check).
S_FLAGS = {0x1A0: 1, 0x1A5: 0, 0x1DC: 1, 0x232: 1}
S_STATE = 0x280
S_SHOW, S_IDLE = 1, 0x0B


def stamp(h, addr):
    if rd(h, addr, 0x290) is None:
        sys.exit("banner object unreadable -- NOT stamping")
    b = rd(h, addr, 0x290)
    if int.from_bytes(b[0x218:0x21C], "little") != 0xD:
        sys.exit(f"[obj+0x218] = {int.from_bytes(b[0x218:0x21C], 'little'):#x} != 0xD -- wrong object, NOT stamping")
    state = b[S_STATE]
    # 1 = actively appearing; 2 is the post-show RESTING state (live capture 2026-07-02:
    # 0x0B -> 1 -> 2 and stays 2 after the hide flags drop) -- 2 is safe to stamp over.
    if state == 1:
        sys.exit(f"state byte +0x280 = {state} (mid-show) -- retry at a quiet moment")
    pre = {off: b[off] for off in S_FLAGS}
    pre[S_STATE] = state
    print(f"pre-stamp: state {state:#x} flags " +
          " ".join(f"+{o:03x}={b[o]}" for o in S_FLAGS))
    for off, val in S_FLAGS.items():
        if not wr(h, addr + off, bytes([val])):
            sys.exit(f"WPM failed at +{off:03x} -- aborting (nothing else written)")
    if not wr(h, addr + S_STATE, bytes([S_SHOW])):
        sys.exit("WPM failed on the state byte")
    print("vector stamped, state -> 1. WATCH THE SCREEN.")
    t0 = time.perf_counter()
    last = S_SHOW
    engaged = False
    while time.perf_counter() - t0 < POKE_TRACE_SECS:
        time.sleep(0.05)
        cur = rd(h, addr + S_STATE, 1)
        if cur is None:
            continue
        if cur[0] != last:
            t = time.perf_counter() - t0
            print(f"t=+{t:5.2f}s state {last:#x} -> {cur[0]:#x}")
            engaged = True
            last = cur[0]
    if engaged:
        print("state machine ADVANCED -- the engine consumed our state (even with no bubble, "
              "that is a live mechanism; bisect flags/coords next).")
    else:
        for off, val in pre.items():
            wr(h, addr + off, bytes([val]))
        print("state byte never moved in 5s -- engine ignored the stamp; pre-state restored. "
              "The update path likely keys off something outside this object.")


# --- stamp2: the FULL show vector -- banner object AND its child animation tracks ---
# Evidence 2026-07-02 18:03 (CE access-watch on child#2+0x6D): the child tracks are polled
# per-frame by REAL in-module code (0x140409ADB, 55 hits/s) and armed by a real setter
# (0x1404099E3) -- the VM only does banner-level bookkeeping. The all-children struct_watch
# (17:44, natural Focus at t=+28.84) gives the exact arm vector:
#   child#2 (appear): +0x68/+0x69/+0x6A = 1 (0x6A is a 1-frame pulse), +0x6D = 1,
#                     lifetime floats +0x4C = 0.0715f / +0x50 = +0x54 = 4.2896f
#   child#7 (loop):   +0x68/+0x69/+0x6A = 1, same floats
# then the banner-object flags (stamp's vector). stamp (banner only) failed because the
# poller saw dead child tracks; this stamps children FIRST, banner flags after.
C_APPEAR_IDX, C_LOOP_IDX = 1, 6            # 0-based slots in the child vector (+0x268)
C_FLOATS = {0x4C: 0x3D926B19, 0x50: 0x40894467, 0x54: 0x40894467}
C_ARM = (0x68, 0x69, 0x6A, 0x6D)


def stamp2(h, addr):
    b = rd(h, addr, 0x290)
    if b is None or int.from_bytes(b[0x218:0x21C], "little") != 0xD:
        sys.exit("banner object failed the +0x218==0xD check -- NOT stamping")
    if b[S_STATE] == 1:
        sys.exit("state byte +0x280 = 1 (mid-show) -- retry at a quiet moment")
    vec = rd(h, addr + 0x268, 16)
    base = int.from_bytes(vec[0:8], "little")
    count = (int.from_bytes(vec[8:16], "little") - base) // 8
    if count != 12:
        sys.exit(f"child vector has {count} entries (expected 12) -- layout drift, NOT stamping")
    ptrs = rd(h, base, 96)
    kids = [int.from_bytes(ptrs[i * 8:i * 8 + 8], "little") for i in range(12)]
    c2, c7 = kids[C_APPEAR_IDX], kids[C_LOOP_IDX]
    pre = {}
    for kid, arm in ((c2, C_ARM), (c7, (0x68, 0x69, 0x6A))):
        kb = rd(h, kid, 0x70)
        if kb is None:
            sys.exit(f"child 0x{kid:X} unreadable -- NOT stamping")
        for off in list(C_FLOATS) + list(arm):
            pre[(kid, off, 4 if off in C_FLOATS else 1)] = kb[off:off + (4 if off in C_FLOATS else 1)]
    ob = rd(h, addr, 0x290)
    for off in list(S_FLAGS) + [S_STATE]:
        pre[(addr, off, 1)] = ob[off:off + 1]
    print(f"children: appear 0x{c2:X} loop 0x{c7:X} -- stamping full vector")
    for kid, arm in ((c2, C_ARM), (c7, (0x68, 0x69, 0x6A))):
        for off, val in C_FLOATS.items():
            wr(h, kid + off, val.to_bytes(4, "little"))
        for off in arm:
            wr(h, kid + off, b"\x01")
    for off, val in S_FLAGS.items():
        wr(h, addr + off, bytes([val]))
    wr(h, addr + S_STATE, bytes([S_SHOW]))
    print("stamped. WATCH THE SCREEN.")
    t0 = time.perf_counter()
    lastf = C_FLOATS[0x50].to_bytes(4, "little")
    engaged = []
    while time.perf_counter() - t0 < POKE_TRACE_SECS:
        time.sleep(0.05)
        f = rd(h, c2 + 0x50, 4)
        s = rd(h, addr + S_STATE, 1)
        if f is not None and f != lastf:
            engaged.append(f"t=+{time.perf_counter() - t0:5.2f}s child#2 timer {lastf.hex()} -> {f.hex()}")
            lastf = f
        if s is not None and s[0] not in (S_SHOW,):
            engaged.append(f"t=+{time.perf_counter() - t0:5.2f}s obj state -> {s[0]:#x}")
            break
    for line in engaged[:10]:
        print(line)
    if engaged:
        print("ENGINE ENGAGED the stamped tracks (fields moving on their own).")
    else:
        for (tgt, off, n), val in pre.items():
            wr(h, tgt + off, val)
        print("nothing moved in 5s -- vector restored; the poller keys on more than these fields.")


# --- stamp3: the 3-byte widget test -- force the TEXT WIDGET itself to its shown state ---
# Widget-neighborhood watch 2026-07-02 18:11 (natural Focus at t=+50.08): the text widget's
# rest state differs from its fully-shown state in exactly THREE bytes -- +0x95 suppress
# latch (1 at rest, 0 shown), +0xC7 alpha (ramps to 0xFF), +0xCC active (1 shown). The
# color/alpha-mult floats animate during the fade but SETTLE at their rest values. If the
# renderer draws from widget state, these three bytes are the whole show (text only -- the
# bubble frame is a sibling widget, same trick later). Restores after the trace window.
W_SUPPRESS, W_ALPHA, W_ACTIVE = 0x95, 0xC7, 0xCC


def stamp3(h, addr):
    b = rd(h, addr, 0xD0)
    if b is None:
        sys.exit("widget unreadable -- NOT stamping")
    # +0x95 is a first-show/reset latch: 1 only before the widget's FIRST show, 0 ever
    # after (live read 18:20 -- post-show rest is 0/0). Only +0xCC discriminates mid-show.
    if b[W_ACTIVE] != 0:
        sys.exit(f"widget mid-show (+0xCC={b[W_ACTIVE]}) -- retry at a quiet moment")
    pre_alpha, pre_sup = b[W_ALPHA], b[W_SUPPRESS]
    print(f"pre-stamp: +0x95={pre_sup} +0xC7={pre_alpha:#04x} +0xCC=0 -- writing shown state")
    wr(h, addr + W_SUPPRESS, b"\x00")
    wr(h, addr + W_ALPHA, b"\xff")
    wr(h, addr + W_ACTIVE, b"\x01")
    print("stamped 3 bytes. WATCH THE SCREEN -- text should be visible NOW if this is the layer.")
    time.sleep(POKE_TRACE_SECS)
    cur = rd(h, addr, 0xD0)
    if cur is not None:
        print(f"after 5s: +0x95={cur[W_SUPPRESS]} +0xC7={cur[W_ALPHA]:#04x} +0xCC={cur[W_ACTIVE]}"
              " (engine rewrites here mean the show/hide path noticed us)")
    wr(h, addr + W_SUPPRESS, bytes([pre_sup]))
    wr(h, addr + W_ALPHA, bytes([pre_alpha]))
    wr(h, addr + W_ACTIVE, b"\x00")
    print("restored rest state.")


# --- stamp4: the FULL CHAIN -- banner flags + child tracks + text-widget bytes at once ---
# Rationale (18:25): every layer stamped alone held or engaged yet drew nothing -- the
# scene-graph hypothesis: parent visibility gates child rendering, so the chain only draws
# when banner object AND animation tracks AND widget are all in shown state simultaneously.
# Usage: stamp4 <bannerObjHex> <widgetHex>. Restores everything at the end of the window.
def stamp4(h, obj, widget):
    b = rd(h, obj, 0x290)
    if b is None or int.from_bytes(b[0x218:0x21C], "little") != 0xD:
        sys.exit("banner object failed the +0x218==0xD check -- NOT stamping")
    wbytes = rd(h, widget, 0xD0)
    if wbytes is None:
        sys.exit("widget unreadable -- NOT stamping")
    if wbytes[W_ACTIVE] != 0 or b[S_STATE] == 1:
        sys.exit("mid-show somewhere (widget +0xCC or obj +0x280) -- retry at a quiet moment")
    vec = rd(h, obj + 0x268, 16)
    base = int.from_bytes(vec[0:8], "little")
    if (int.from_bytes(vec[8:16], "little") - base) // 8 != 12:
        sys.exit("child vector layout drift -- NOT stamping")
    ptrs = rd(h, base, 96)
    kids = [int.from_bytes(ptrs[i * 8:i * 8 + 8], "little") for i in range(12)]
    c2, c7 = kids[C_APPEAR_IDX], kids[C_LOOP_IDX]
    pre_alpha, pre_sup = wbytes[W_ALPHA], wbytes[W_SUPPRESS]
    pre_state = b[S_STATE]
    print(f"full chain: obj 0x{obj:X} kids 0x{c2:X}/0x{c7:X} widget 0x{widget:X}")
    for kid, arm in ((c2, C_ARM), (c7, (0x68, 0x69, 0x6A))):
        for off, val in C_FLOATS.items():
            wr(h, kid + off, val.to_bytes(4, "little"))
        for off in arm:
            wr(h, kid + off, b"\x01")
    wr(h, widget + W_SUPPRESS, b"\x00")
    wr(h, widget + W_ALPHA, b"\xff")
    wr(h, widget + W_ACTIVE, b"\x01")
    for off, val in S_FLAGS.items():
        wr(h, obj + off, bytes([val]))
    wr(h, obj + S_STATE, bytes([S_SHOW]))
    print("FULL CHAIN STAMPED. WATCH THE SCREEN.")
    time.sleep(POKE_TRACE_SECS)
    cur = rd(h, widget, 0xD0)
    curo = rd(h, obj, 0x290)
    if cur is not None and curo is not None:
        print(f"after 5s: widget alpha {cur[W_ALPHA]:#04x} active {cur[W_ACTIVE]} | "
              f"obj state {curo[S_STATE]:#x} act {curo[0x1A0]}")
    # full restore: children flags+floats, widget, obj flags, state back to pre
    for kid, arm in ((c2, C_ARM), (c7, (0x68, 0x69, 0x6A))):
        for off in C_FLOATS:
            wr(h, kid + off, b"\x00\x00\x00\x00")
        for off in arm:
            wr(h, kid + off, b"\x00")
    wr(h, widget + W_SUPPRESS, bytes([pre_sup]))
    wr(h, widget + W_ALPHA, bytes([pre_alpha]))
    wr(h, widget + W_ACTIVE, b"\x00")
    for off in S_FLAGS:
        wr(h, obj + off, b"\x00")
    wr(h, obj + S_STATE, bytes([pre_state]))
    print("restored.")


def main():
    if len(sys.argv) < 3 or sys.argv[1] not in ("info", "watch", "poke", "stamp", "stamp2", "stamp3", "stamp4"):
        sys.exit(__doc__)
    mode, addr = sys.argv[1], int(sys.argv[2], 16)
    h = open_game(write=(mode in ("poke", "stamp", "stamp2", "stamp3", "stamp4")))
    if mode == "info":
        info(h, addr)
    elif mode == "watch":
        watch(h, addr, float(sys.argv[3]) if len(sys.argv) > 3 else 30.0)
    elif mode == "stamp":
        stamp(h, addr)
    elif mode == "stamp2":
        stamp2(h, addr)
    elif mode == "stamp3":
        stamp3(h, addr)
    elif mode == "stamp4":
        if len(sys.argv) < 4:
            sys.exit("stamp4 needs <bannerObjHex> <widgetHex>")
        stamp4(h, addr, int(sys.argv[3], 16))
    else:
        poke(h, addr)


if __name__ == "__main__":
    main()
