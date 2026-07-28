#!/usr/bin/env python
"""
Weather state hunt (`snap`/`cmp`/`griddiff` READ-ONLY; `poke` writes).

WHY: weather has never been touched in this repo, yet it demonstrably exists in memory --
LIVE INCIDENT #5 (LIVE_LEDGER Contradicted section) found RAIN perturbing the hashed terrain
fields on maps 74/76/79/81, which is what broke the terrain fingerprint. So rain reaches
readable state. The open question is where the WEATHER STATE byte lives and whether writing it
starts/stops the rain. A byte that starts a storm on demand is pure spectacle (a sword that
summons rain), and lightning/water formulas key off weather in FFT.

METHOD: STABLE-DIFFERENTIAL SNAPSHOTS. A naive clear-vs-rain diff drowns in frame counters and
RNG. So `snap` takes SIX samples over ~2.5s and records only the bytes that were IDENTICAL in
all six -- the stable state. `cmp` then reports bytes stable under BOTH labels but DIFFERENT
between them: state flags, not noise. The weather byte must be in that set (weather is rolled
per battle entry and constant for the whole fight).

REGIONS: the battle-statics cluster 0x140C00000..0x140D00000 (cursor, terrain grid, pause flag,
move lists, quad arena -- every battle global this repo has found lives here or nearby) plus the
BattleMode neighborhood 0x140900000..0x140910000.

SEQUENCE (needs one CLEAR battle and one RAINY battle, ideally the SAME map -- the incident-5
maps 74/76/79/81 roll rain often; re-enter until it rains):
  1. clear battle:  python -u weather_probe.py snap clear
  2. rainy battle, same map:  python -u weather_probe.py snap rain
  3. python -u weather_probe.py cmp clear rain        # the state bytes that differ
  4. python -u weather_probe.py griddiff clear rain   # bonus: rain's per-tile terrain signature
  5. rainy battle:  poke <addr> <clearValue> --hold 30    # does the rain STOP?
     clear battle:  poke <addr> <rainValue>  --hold 30    # does it START? (the real prize)

Same-map matters: a cross-map cmp also catches map id, layout, and geometry, and the weather
byte hides in the pile. Same map twice leaves a short list.
"""
import argparse
import json
import os
import pathlib
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ct_probe import PROC, PV_W, find_pid, k32, rd, wr  # noqa: E402

REGIONS = [(0x140C00000, 0x100000), (0x140900000, 0x10000)]
GRID, REC, GRID_TILES = 0x140C65000, 7, 256
SAMPLES, GAP = 6, 0.4
SNAP_DIR = pathlib.Path(os.environ.get("TEMP", ".")) / "fft_probes" / "weather"


def open_game():
    pid = find_pid(PROC)
    if not pid:
        sys.exit(f"{PROC} not running")
    h = k32.OpenProcess(PV_W, False, pid)
    if not h:
        sys.exit(f"could not open pid {pid}")
    return h


def cmd_snap(a):
    h = open_game()
    SNAP_DIR.mkdir(parents=True, exist_ok=True)
    for ri, (base, span) in enumerate(REGIONS):
        samples = []
        for s in range(SAMPLES):
            buf = rd(h, base, span)
            if not buf:
                sys.exit(f"region 0x{base:X} unreadable")
            samples.append(buf)
            if s < SAMPLES - 1:
                time.sleep(GAP)
        first = samples[0]
        mask = bytearray(span)          # 1 = stable across all samples
        for off in range(span):
            b0 = first[off]
            if all(smp[off] == b0 for smp in samples[1:]):
                mask[off] = 1
        stable = sum(mask)
        (SNAP_DIR / f"{a.label}_r{ri}.bin").write_bytes(first)
        (SNAP_DIR / f"{a.label}_r{ri}.mask").write_bytes(bytes(mask))
        print(f"region 0x{base:X}: {stable}/{span} bytes stable over {SAMPLES} samples")
    print(f"saved -> {SNAP_DIR}\\{a.label}_r*.bin/.mask")


def load(label, ri):
    b = SNAP_DIR / f"{label}_r{ri}.bin"
    m = SNAP_DIR / f"{label}_r{ri}.mask"
    if not b.exists() or not m.exists():
        sys.exit(f"missing snapshot '{label}'; run `snap {label}` in the right battle first")
    return b.read_bytes(), m.read_bytes()


def cmd_cmp(a):
    total = 0
    for ri, (base, span) in enumerate(REGIONS):
        A, MA = load(a.a, ri)
        B, MB = load(a.b, ri)
        print(f"\n=== region 0x{base:X}: stable-in-both bytes that DIFFER ===")
        run, shown = None, 0
        for off in range(span):
            differs = MA[off] and MB[off] and A[off] != B[off]
            if differs and run is None:
                run = off
            elif not differs and run is not None:
                n = off - run
                print(f"  0x{base + run:X}  {n:>3}B   {A[run:off].hex(' ')[:60]}  ->  {B[run:off].hex(' ')[:60]}")
                run, shown, total = None, shown + 1, total + 1
                if shown >= a.limit:
                    print(f"  ... capped at {a.limit} runs")
                    break
    if total == 0:
        print("\nno stable differences at all -- were both snaps taken IN battle, on the same map?")
    else:
        print(f"\n{total} stable-different run(s). The weather byte is in this list; poke each "
              f"with --hold and watch the sky.")


def cmd_griddiff(a):
    # The terrain grid lives inside region 0; decode its slice per-tile.
    base0 = REGIONS[0][0]
    A, MA = load(a.a, 0)
    B, MB = load(a.b, 0)
    g0 = GRID - base0
    changed = 0
    print("rain's per-tile terrain signature (incident-5's perturbation, made explicit):")
    for i in range(GRID_TILES):
        o = g0 + i * REC
        ra, rb = A[o:o + REC], B[o:o + REC]
        if ra != rb and all(MA[o + k] and MB[o + k] for k in range(REC)):
            fields = " ".join(f"f{k}:{ra[k]}->{rb[k]}" for k in range(REC) if ra[k] != rb[k])
            print(f"  tile [{i:>3}]  {fields}")
            changed += 1
    print(f"{changed} tile(s) differ stably")


def cmd_poke(a):
    h = open_game()
    old = rd(h, a.addr, 1)
    if old is None:
        sys.exit(f"0x{a.addr:X} unreadable")
    print(f"0x{a.addr:X}: {old[0]} -> {a.value}, holding {a.hold}s. WATCH THE SKY / the rain audio.")
    try:
        end = time.time() + a.hold
        while time.time() < end:
            cur = rd(h, a.addr, 1)
            if cur and cur[0] != a.value:
                wr(h, a.addr, bytes([a.value]))
            time.sleep(0.05)
    except KeyboardInterrupt:
        pass
    finally:
        wr(h, a.addr, old)
        print(f"\nrestored {old[0]}")


def main():
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = p.add_subparsers(dest="cmd", required=True)

    s = sub.add_parser("snap")
    s.add_argument("label")
    s.set_defaults(fn=cmd_snap)

    c = sub.add_parser("cmp")
    c.add_argument("a")
    c.add_argument("b")
    c.add_argument("--limit", type=int, default=120)
    c.set_defaults(fn=cmd_cmp)

    g = sub.add_parser("griddiff")
    g.add_argument("a")
    g.add_argument("b")
    g.set_defaults(fn=cmd_griddiff)

    k = sub.add_parser("poke")
    k.add_argument("addr", type=lambda x: int(x, 0))
    k.add_argument("value", type=lambda x: int(x, 0))
    k.add_argument("--hold", type=int, default=30)
    k.set_defaults(fn=cmd_poke)

    a = p.parse_args()
    a.fn(a)


if __name__ == "__main__":
    main()
