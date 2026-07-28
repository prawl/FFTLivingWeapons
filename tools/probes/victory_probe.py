#!/usr/bin/env python
"""
Victory-condition / living-enemy-count hunt (`scan`/`narrow`/`watch` READ-ONLY; `poke` writes).

WHY: hiding the last enemy WINS THE BATTLE (owner live 2026-07-22, battle_toolbag hazard note).
So the engine's win check is continuously consuming unit state our writes can reach. What we do
NOT know is the mechanism: a CACHED "living enemies" counter somewhere in statics, or a fresh
walk of the logic list every check. The toolbag note says "walks the same logic list", but that
was inference from one observation, not a located mechanism.

WHAT EACH ANSWER IS WORTH:
  - A cached counter EXISTS  -> two abilities fall out for one byte: poke 0 = instant rout
    ("Banish the armies"), and pin >0 while the field empties = the battle cannot end while the
    blade is drawn. Plus the guard every shipped Vanish effect needs.
  - ZERO survivors after honest narrowing -> the check walks the list, no cheap lever, and any
    "battle continues" mechanic must keep a REAL unit alive instead (hide/reserve a straggler,
    which battle_toolbag can already do). Also an answer; write it to the wall list and stop.

METHOD: known-value narrowing over the STATIC image only (0x140700000..0x141900000, ~18 MB --
every battle global this repo has ever found lives there; the dynamic 0x43x arena rebases per
launch and cannot hold a shippable lever anyway). The count changes on death AND on hide, so
you get two independent narrowing edges per battle.

SEQUENCE (any battle; count the living enemies on screen first):
  1. python -u victory_probe.py scan 8          # 8 = living enemies right now
  2. kill one enemy, then:  narrow 7
  3. hide one (battle_toolbag.py hide <slot>), then:  narrow 6
  4. watch                                       # confirm survivors track kills live
  5. the payoff pokes, owner-eyes only, THROWAWAY save:
       poke <addr> 0            -> instant victory?
       poke <addr> 2 --hold 60  -> kill the last enemy while held: does the battle refuse to end?

CAVEAT: dead-and-crystallized vs dead-and-ticking vs hidden may be counted differently; if
narrowing dies at step 3 but survived step 2, re-scan and narrow on kills only -- a
kills-only counter is still the same lever with a different trigger.
"""
import argparse
import json
import os
import pathlib
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ct_probe import PROC, PV_W, find_pid, k32, rd, wr  # noqa: E402

LO, HI = 0x140700000, 0x141900000
CAND = pathlib.Path(os.environ.get("TEMP", ".")) / "fft_probes" / "victory_candidates.json"


def open_game():
    pid = find_pid(PROC)
    if not pid:
        sys.exit(f"{PROC} not running")
    h = k32.OpenProcess(PV_W, False, pid)
    if not h:
        sys.exit(f"could not open pid {pid}")
    return h


def read_value(h, addr, width):
    b = rd(h, addr, width)
    return int.from_bytes(b, "little") if b else None


def cmd_scan(a):
    h = open_game()
    hits = []
    needles = {w: a.value.to_bytes(w, "little") for w in (1, 2, 4) if a.value < (1 << (8 * w))}
    t0 = time.time()
    addr = LO
    while addr < HI:
        n = min(0x100000, HI - addr)
        buf = rd(h, addr, n)
        if buf:
            for w, needle in needles.items():
                start = 0
                while True:
                    i = buf.find(needle, start)
                    if i < 0:
                        break
                    if (addr + i) % w == 0:
                        hits.append([addr + i, w])
                    start = i + 1
        addr += n
    CAND.parent.mkdir(parents=True, exist_ok=True)
    CAND.write_text(json.dumps({"note": f"scan {a.value}", "candidates": hits}))
    print(f"scanned {(HI - LO) / 1048576:.0f} MB statics in {time.time() - t0:.1f}s: "
          f"{len(hits)} address(es) hold {a.value}")
    print("now change the count (kill or hide one enemy) and run: narrow <new count>")


def cmd_narrow(a):
    h = open_game()
    d = json.loads(CAND.read_text())
    keep = [[addr, w] for addr, w in d["candidates"] if read_value(h, addr, w) == a.value]
    CAND.write_text(json.dumps({"note": d["note"] + f" -> {a.value}", "candidates": keep}))
    print(f"{len(d['candidates'])} -> {len(keep)} survivors")
    for addr, w in keep[:40]:
        print(f"  0x{addr:X}  u{w * 8}")
    if not keep:
        print("zero survivors. If this happened on a HIDE edge but kills narrowed fine, re-scan "
              "and narrow on kills only. Zero on kills too = no cached counter: the win check "
              "walks the list, and that is the answer to record.")


def cmd_watch(a):
    h = open_game()
    d = json.loads(CAND.read_text())
    cands = d["candidates"]
    if not cands:
        sys.exit("no survivors to watch")
    last = {}
    print(f"watching {len(cands)} candidate(s) for {a.secs}s; kill or hide something")
    end = time.time() + a.secs
    while time.time() < end:
        for addr, w in cands:
            v = read_value(h, addr, w)
            if last.get(addr) != v:
                if addr in last:
                    print(f"  {time.strftime('%H:%M:%S')}  0x{addr:X} u{w * 8}: {last[addr]} -> {v}")
                last[addr] = v
        time.sleep(0.1)


def cmd_poke(a):
    h = open_game()
    old = rd(h, a.addr, a.width)
    if old is None:
        sys.exit(f"0x{a.addr:X} unreadable")
    was = int.from_bytes(old, "little")
    new = a.value.to_bytes(a.width, "little")
    print(f"0x{a.addr:X} u{a.width * 8}: {was} -> {a.value}"
          + (f", holding {a.hold}s" if a.hold else " (one-shot)"))
    wr(h, a.addr, new)
    try:
        if a.hold:
            end = time.time() + a.hold
            while time.time() < end:
                cur = rd(h, a.addr, a.width)
                if cur != new:
                    wr(h, a.addr, new)
                time.sleep(0.05)
    except KeyboardInterrupt:
        pass
    finally:
        if a.hold or a.restore:
            wr(h, a.addr, old)
            print(f"restored {was}")
        else:
            print("left in place (pass --restore to undo a one-shot)")


def main():
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = p.add_subparsers(dest="cmd", required=True)

    s = sub.add_parser("scan")
    s.add_argument("value", type=lambda x: int(x, 0))
    s.set_defaults(fn=cmd_scan)

    n = sub.add_parser("narrow")
    n.add_argument("value", type=lambda x: int(x, 0))
    n.set_defaults(fn=cmd_narrow)

    w = sub.add_parser("watch")
    w.add_argument("--secs", type=int, default=30)
    w.set_defaults(fn=cmd_watch)

    k = sub.add_parser("poke")
    k.add_argument("addr", type=lambda x: int(x, 0))
    k.add_argument("value", type=lambda x: int(x, 0))
    k.add_argument("--width", type=int, default=1)
    k.add_argument("--hold", type=int, default=0)
    k.add_argument("--restore", action="store_true")
    k.set_defaults(fn=cmd_poke)

    a = p.parse_args()
    a.fn(a)


if __name__ == "__main__":
    main()
