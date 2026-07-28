#!/usr/bin/env python
"""
Floating damage-numeral hunt (READ-ONLY by default; `poke` writes).

WHY THIS EXISTS
---------------
Several shipped effects are SILENT because we cannot spawn a number: Renewal heals adjacent
allies every turn edge and the player sees nothing (LIVE_LEDGER's Renewal row calls the silence
out explicitly). If the engine's floating numeral can be located and driven, every silent
mechanic becomes legible, and that is worth more than any single new signature.

METHOD: known-value narrowing, the classic differential, scripted so it is repeatable.
A hit for N damage puts N somewhere. Scan for N, take another hit for M, keep only the addresses
that now read M. Two rounds usually leaves a handful; three leaves the field.

THE PAUSE TRICK (this is what makes it work)
--------------------------------------------
The numeral is on screen for about a second, so a naive scan races the value's own teardown.
PAUSE THE GAME WHILE THE NUMBER IS STILL VISIBLE. The pause flag at 0x140C6B1C8 reads 1 while a
menu/pause is up (provenance: the body-double spike gated its cold call on exactly this byte).
`status` prints it so you can confirm you are actually paused before spending a scan.

WHAT A HIT LOOKS LIKE
---------------------
Damage lands in more than one place: the victim's HP delta, an action/damage record, and the
render-side numeral. This probe does not assume which is which. It reports every address that
tracked both values, with its region, so the survivors can be told apart by `watch` (the numeral
should appear and vanish; an HP field should persist).

VERBS
-----
  python -u numeral_probe.py status
      Pause flag + region census. Run this first; it costs nothing and proves the probe can read.

  python -u numeral_probe.py scan <N> [--width 1,2,4] [--max-hits 400000]
      First pass. Deal a hit for exactly N, pause while the numeral shows, then run this.
      Writes candidates to numeral_candidates.json beside this file.

  python -u numeral_probe.py narrow <M>
      Second and later passes. Deal a DIFFERENT hit for exactly M, pause, run this. Intersects
      against the saved candidates and rewrites the file. Repeat until the list is small.

  python -u numeral_probe.py watch [--hz 10] [--secs 20]
      Watch every surviving candidate and print each change. This is what separates a transient
      numeral from a persistent HP field.

  python -u numeral_probe.py poke <addr> <value> [--width 2]
      THE PAYOFF TEST, and the only verb that writes. With a numeral on screen and the game
      paused, change the value and look: if the rendered number changes, the numeral is ours.
      Restores the original bytes on exit unless --hold is passed.

NOTES
-----
Addresses in the dynamic 0x43xxxxxxxx region rebase per launch (the repo has been bitten by this
on the callout widgets), so treat any hit there as a per-session address, not a constant. Static
0x140/0x141 hits are the ones worth writing down.
"""
import argparse
import ctypes as C
import ctypes.wintypes as W
import json
import os
import sys
import time
from pathlib import Path

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ct_probe import PROC, PV_W, find_pid, k32, rd, wr  # noqa: E402

CANDIDATE_FILE = Path(__file__).with_name("numeral_candidates.json")

PAUSE_FLAG = 0x140C6B1C8          # u8, 1 while paused / a menu is up
MEM_COMMIT = 0x1000
PAGE_READABLE = 0x02 | 0x04 | 0x20 | 0x40      # R, RW, XR, XRW
PAGE_GUARD = 0x100


class MBI(C.Structure):
    _fields_ = [("BaseAddress", C.c_void_p), ("AllocationBase", C.c_void_p),
                ("AllocationProtect", W.DWORD), ("__align", W.DWORD),
                ("RegionSize", C.c_size_t), ("State", W.DWORD),
                ("Protect", W.DWORD), ("Type", W.DWORD)]


def regions(h, lo=0x140000000, hi=0x450000000):
    """Committed, readable, non-guard regions in [lo, hi). Bounded deliberately: the game's
    interesting memory is the static image (0x140/0x141) plus the dynamic UI/render arena
    (0x43x). Scanning the whole 64-bit space would be slow and mostly zero."""
    out, addr = [], lo
    mbi = MBI()
    while addr < hi:
        if not k32.VirtualQueryEx(h, C.c_void_p(addr), C.byref(mbi), C.sizeof(mbi)):
            addr += 0x1000
            continue
        base = mbi.BaseAddress or addr
        size = mbi.RegionSize or 0x1000
        if (mbi.State == MEM_COMMIT and (mbi.Protect & PAGE_READABLE)
                and not (mbi.Protect & PAGE_GUARD)):
            out.append((base, size))
        addr = base + size
    return out


def pack(value, width):
    return int(value).to_bytes(width, "little")


def scan_value(h, value, widths, max_hits):
    """Every address holding `value` at any requested width. Reads region by region in chunks so
    one unreadable page cannot abort the pass."""
    hits = []
    needles = {w: pack(value, w) for w in widths if value < (1 << (8 * w))}
    if not needles:
        return hits, 0
    scanned = 0
    for base, size in regions(h):
        off = 0
        while off < size:
            n = min(0x100000, size - off)
            buf = rd(h, base + off, n)
            if buf:
                scanned += n
                for w, needle in needles.items():
                    start = 0
                    while True:
                        i = buf.find(needle, start)
                        if i < 0:
                            break
                        # alignment: a numeral is a field, not a byte straddling two others
                        if (base + off + i) % w == 0:
                            hits.append([base + off + i, w])
                            if len(hits) >= max_hits:
                                return hits, scanned
                        start = i + 1
            off += n
    return hits, scanned


def read_value(h, addr, width):
    b = rd(h, addr, width)
    return int.from_bytes(b, "little") if b else None


def load():
    if not CANDIDATE_FILE.exists():
        sys.exit("no candidate file; run `scan <N>` first")
    return json.loads(CANDIDATE_FILE.read_text())


def save(cands, note):
    CANDIDATE_FILE.write_text(json.dumps({"note": note, "candidates": cands}, indent=1))


def open_game(write=False):
    pid = find_pid(PROC)
    if not pid:
        sys.exit(f"{PROC} not running")
    h = k32.OpenProcess(PV_W if write else PV_W, False, pid)
    if not h:
        sys.exit(f"could not open pid {pid}")
    return h


def cmd_status(_):
    h = open_game()
    p = rd(h, PAUSE_FLAG, 1)
    regs = regions(h)
    total = sum(s for _, s in regs)
    print(f"pause flag @0x{PAUSE_FLAG:X} = {p[0] if p else '?'}   "
          f"({'PAUSED, safe to scan' if p and p[0] else 'RUNNING, the numeral will race you'})")
    print(f"committed readable regions in scan window: {len(regs)}  ({total/1048576:.0f} MB)")
    if CANDIDATE_FILE.exists():
        d = json.loads(CANDIDATE_FILE.read_text())
        print(f"saved candidates: {len(d['candidates'])}  ({d.get('note','')})")


def cmd_scan(a):
    h = open_game()
    widths = [int(x) for x in a.width.split(",")]
    t0 = time.time()
    hits, scanned = scan_value(h, a.value, widths, a.max_hits)
    save(hits, f"scan {a.value}")
    print(f"scanned {scanned/1048576:.0f} MB in {time.time()-t0:.1f}s")
    print(f"{len(hits)} address(es) hold {a.value}")
    if len(hits) >= a.max_hits:
        print(f"WARNING: hit the {a.max_hits} cap, so this pass is TRUNCATED and the real "
              f"survivor may have been dropped. Re-run with a larger --max-hits or a rarer value.")
    print("now take a DIFFERENT hit, pause on the numeral, and run: narrow <M>")


def cmd_narrow(a):
    h = open_game()
    d = load()
    keep = []
    for addr, w in d["candidates"]:
        if read_value(h, addr, w) == a.value:
            keep.append([addr, w])
    save(keep, f"{d.get('note','')} -> narrow {a.value}")
    print(f"{len(d['candidates'])} -> {len(keep)} survivors")
    for addr, w in keep[:40]:
        print(f"  0x{addr:X}  u{w*8}")
    if not keep:
        print("nothing survived. Most likely the scan raced the teardown: confirm `status` says "
              "PAUSED before each pass, and that the damage numbers were exactly as typed.")


def cmd_watch(a):
    h = open_game()
    d = load()
    cands = d["candidates"]
    if not cands:
        sys.exit("no survivors to watch")
    last = {}
    print(f"watching {len(cands)} candidate(s) for {a.secs}s at {a.hz}Hz; Ctrl-C to stop")
    end = time.time() + a.secs
    while time.time() < end:
        for addr, w in cands:
            v = read_value(h, addr, w)
            if last.get(addr) != v:
                if addr in last:
                    print(f"  {time.strftime('%H:%M:%S')}  0x{addr:X} u{w*8}: {last[addr]} -> {v}")
                last[addr] = v
        time.sleep(1.0 / a.hz)


def cmd_poke(a):
    h = open_game(write=True)
    old = rd(h, a.addr, a.width)
    if old is None:
        sys.exit(f"0x{a.addr:X} unreadable; refusing to write")
    print(f"0x{a.addr:X} u{a.width*8}: {int.from_bytes(old,'little')} -> {a.value}")
    if not wr(h, a.addr, pack(a.value, a.width)):
        sys.exit("write failed")
    if a.hold:
        print("held (--hold): original bytes NOT restored")
        return
    print("LOOK AT THE SCREEN NOW. Restoring in 5s.")
    time.sleep(5)
    wr(h, a.addr, old)
    print("restored")


COMBAT_BASE, COMBAT_STRIDE = 0x141853CE0, 0x200


def cmd_unitsweep(a):
    """Where in a UNIT's own struct does the displayed number live?

    Static RE of FnNumberPopup (0x140227CF8) showed the value is NOT passed as an argument: at
    the write instruction the function's base pointer was combat slot 16 + 0x1BE, so the number
    is READ OUT OF THE UNIT. Finding that field is what turns "numbers on command" from a
    cold-call problem into a write-then-trigger problem, which is far cheaper.

    Usage: snap the target BEFORE the hit, land a hit for a known amount, then diff.
        unitsweep snap <slot>
        unitsweep diff <slot> <damage>
    """
    h = open_game()
    addr = COMBAT_BASE + a.slot * COMBAT_STRIDE
    snapfile = CANDIDATE_FILE.with_name(f"unitsnap_{a.slot}.bin")
    if a.mode == "snap":
        b = rd(h, addr, COMBAT_STRIDE)
        if not b:
            sys.exit(f"combat slot {a.slot} unreadable at 0x{addr:X}")
        snapfile.write_bytes(b)
        print(f"snapped slot {a.slot} (0x{addr:X}, {COMBAT_STRIDE} bytes) -> {snapfile.name}")
        print("now land a hit on this unit, then: unitsweep diff <slot> <damage>")
        return

    if not snapfile.exists():
        sys.exit("no snapshot; run `unitsweep snap <slot>` first")
    before = snapfile.read_bytes()
    after = rd(h, addr, COMBAT_STRIDE)
    if not after:
        sys.exit("slot unreadable now")
    dmg = a.damage
    print(f"slot {a.slot}: offsets that CHANGED, flagging any that now hold {dmg}")
    for off in range(COMBAT_STRIDE - 1):
        if before[off] == after[off] and before[off + 1] == after[off + 1]:
            continue
        u16_after = after[off] | (after[off + 1] << 8)
        u16_before = before[off] | (before[off + 1] << 8)
        hit = "  <<< HOLDS THE DAMAGE" if u16_after == dmg else ""
        print(f"  +0x{off:03X}  u16 {u16_before} -> {u16_after}{hit}")


"""Per-unit popup record, found by unitsweep 2026-07-27 and corroborated by static RE:
FnNumberPopup's base pointer was combat slot +0x1BE, and the damage landed at +0x1C4 == [rbp+6].
+0x1D8 and +0x1E5 went 0 -> nonzero on the same hit and are the kind/show candidates."""
NUM_VALUE = 0x1C4        # u16, the number that gets displayed
NUM_FLAGS = (0x1E5, 0x1D8)   # candidate show/kind bytes, in the order worth trying


def cmd_popup(a):
    """THE PAYOFF TEST: write the number into a unit and try to make it draw with no cold-call.

    Pick a unit standing idle (not mid-action). If a numeral pops, the whole 'numbers on command'
    problem collapses to two guarded writes and Renewal stops being silent."""
    h = open_game(write=True)
    base = COMBAT_BASE + a.slot * COMBAT_STRIDE
    probe = rd(h, base, COMBAT_STRIDE)
    if not probe:
        sys.exit(f"combat slot {a.slot} unreadable at 0x{base:X}")
    lvl = probe[0x29]
    hp = probe[0x30] | (probe[0x31] << 8)
    mhp = probe[0x32] | (probe[0x33] << 8)
    if not (1 <= lvl <= 99 and 1 <= hp <= mhp <= 9999):
        sys.exit(f"slot {a.slot} is not a sane live unit (lvl {lvl}, hp {hp}/{mhp}); refusing")

    flags = [a.flag] if a.flag is not None else list(NUM_FLAGS)
    saved = {NUM_VALUE: rd(h, base + NUM_VALUE, 2)}
    for f in flags:
        saved[f] = rd(h, base + f, 1)
    print(f"slot {a.slot} (lvl {lvl}, hp {hp}/{mhp}) @0x{base:X}")
    try:
        wr(h, base + NUM_VALUE, pack(a.value, 2))
        print(f"  +0x{NUM_VALUE:03X} := {a.value}")
        for f in flags:
            wr(h, base + f, bytes([a.flagval]))
            print(f"  +0x{f:03X} := 0x{a.flagval:02X}   <-- WATCH THE SCREEN")
            time.sleep(a.dwell)
        print(f"  holding {a.secs}s; Ctrl-C to restore early")
        time.sleep(a.secs)
    except KeyboardInterrupt:
        pass
    finally:
        for off, old in saved.items():
            if old:
                wr(h, base + off, old)
        print("\nrestored every byte written")


def main():
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = p.add_subparsers(dest="cmd", required=True)
    sub.add_parser("status").set_defaults(fn=cmd_status)

    pop = sub.add_parser("popup")
    pop.add_argument("slot", type=lambda x: int(x, 0))
    pop.add_argument("value", type=lambda x: int(x, 0))
    pop.add_argument("--flag", type=lambda x: int(x, 0), default=None,
                     help="single flag offset to try instead of both candidates")
    pop.add_argument("--flagval", type=lambda x: int(x, 0), default=0x80)
    pop.add_argument("--dwell", type=float, default=2.0)
    pop.add_argument("--secs", type=int, default=8)
    pop.set_defaults(fn=cmd_popup)

    u = sub.add_parser("unitsweep")
    u.add_argument("mode", choices=["snap", "diff"])
    u.add_argument("slot", type=lambda x: int(x, 0))
    u.add_argument("damage", nargs="?", type=lambda x: int(x, 0), default=0)
    u.set_defaults(fn=cmd_unitsweep)

    s = sub.add_parser("scan")
    s.add_argument("value", type=lambda x: int(x, 0))
    s.add_argument("--width", default="1,2,4")
    s.add_argument("--max-hits", type=int, default=400000)
    s.set_defaults(fn=cmd_scan)

    n = sub.add_parser("narrow")
    n.add_argument("value", type=lambda x: int(x, 0))
    n.set_defaults(fn=cmd_narrow)

    w = sub.add_parser("watch")
    w.add_argument("--hz", type=int, default=10)
    w.add_argument("--secs", type=int, default=20)
    w.set_defaults(fn=cmd_watch)

    k = sub.add_parser("poke")
    k.add_argument("addr", type=lambda x: int(x, 0))
    k.add_argument("value", type=lambda x: int(x, 0))
    k.add_argument("--width", type=int, default=2)
    k.add_argument("--hold", action="store_true")
    k.set_defaults(fn=cmd_poke)

    a = p.parse_args()
    a.fn(a)


if __name__ == "__main__":
    main()
