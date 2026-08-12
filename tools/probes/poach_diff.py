"""LW-167 premise probe: find the Poacher's Den carcass-store write by diffing the save
block across a single vanilla poach.

The hypothesis (pre-registered): a vanilla poach (killer has Poach support, standard Attack,
monster victim, vanilla-formula weapon) increments a per-carcass count somewhere in the save
data block that also holds the item inventory (0x1411A7C00, count[id] u8) and the roster
(0x1411A7D10). Expected observation: 1-2 changed bytes at base + f(species, rarity) inside
the scanned window, repeatable across species with a consistent stride. Falsifier: ZERO
changed bytes in the window across a confirmed carcass-obtained message, which means the Den
store lives outside this window and the hunt escalates (wider window, then CE).

Read-only. Usage, driven from a shell while the owner plays:
  python tools\\probes\\poach_diff.py snap          # take/overwrite the baseline snapshot
  python tools\\probes\\poach_diff.py diff          # diff live memory vs the baseline
  python tools\\probes\\poach_diff.py diff --resnap # diff, then make the live state the new baseline

Snapshot lives in %TEMP%\\fft_poach_snap.bin (window base+size in the sidecar json).
Window: 0x14119F000 .. 0x1411C0000 (132KB): the save block. Battle-live unit structs
(band/combat, 0x14184xxxx+) are deliberately OUTSIDE it, so mid-battle the window is
near-quiet and a poach write stands out.
"""
import json
import os
import sys
import tempfile

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import dualgun_probe as d

BASE = 0x14119F000
SIZE = 0x21000
SNAP = os.path.join(tempfile.gettempdir(), "fft_poach_snap.bin")
META = SNAP + ".json"


def open_handle():
    pid = d.find_pid(d.PROC)
    if not pid:
        print("game not running"); sys.exit(1)
    return d.k32.OpenProcess(0x0400 | 0x0010, False, pid)


def read_window(h):
    data = d.rd(h, BASE, SIZE)
    if data is None:
        print("window unreadable"); sys.exit(1)
    return data


def cmd_snap(h):
    data = read_window(h)
    with open(SNAP, "wb") as f:
        f.write(data)
    with open(META, "w") as f:
        json.dump({"base": BASE, "size": SIZE}, f)
    print(f"baseline: {SIZE:#x} bytes @ {BASE:#x} -> {SNAP}")


MASK = SNAP + ".mask.json"


def load_mask():
    if os.path.exists(MASK):
        with open(MASK) as f:
            return set(json.load(f))
    return set()


def cmd_noise(h):
    """Accumulate ambient-churn offsets (changed vs baseline with NO poach) into the mask."""
    if not os.path.exists(SNAP):
        print("no baseline; run snap first"); sys.exit(1)
    with open(SNAP, "rb") as f:
        old = f.read()
    new = read_window(h)
    mask = load_mask()
    fresh = [i for i in range(min(len(old), len(new))) if old[i] != new[i] and i not in mask]
    mask.update(fresh)
    with open(MASK, "w") as f:
        json.dump(sorted(mask), f)
    print(f"mask: +{len(fresh)} new noisy offset(s), {len(mask)} total")


def cmd_diff(h, resnap):
    if not os.path.exists(SNAP):
        print("no baseline; run snap first"); sys.exit(1)
    with open(SNAP, "rb") as f:
        old = f.read()
    new = read_window(h)
    mask = load_mask()
    diffs = [(i, old[i], new[i]) for i in range(min(len(old), len(new)))
             if old[i] != new[i] and i not in mask]
    print(f"{len(diffs)} changed byte(s) (mask hides {len(mask)} known-noisy offsets)")
    for i, was, now in diffs[:80]:
        addr = BASE + i
        # name the two known landmarks so a diff line self-locates
        tag = ""
        if 0x1411A7C00 <= addr < 0x1411A7D10:
            tag = f"  <- item inventory count[{addr - 0x1411A7C00}]"
        elif 0x1411A7D10 <= addr < 0x1411A7D10 + 0x258 * 20:
            slot, off = divmod(addr - 0x1411A7D10, 0x258)
            tag = f"  <- roster slot {slot} +0x{off:X}"
        print(f"  {addr:#x} (win+{i:#x}): {was} -> {now}{tag}")
    if len(diffs) > 80:
        print(f"  ... and {len(diffs) - 80} more")
    if resnap:
        cmd_snap(h)


def main():
    if len(sys.argv) < 2 or sys.argv[1] not in ("snap", "diff", "noise"):
        print(__doc__); sys.exit(1)
    h = open_handle()
    if sys.argv[1] == "snap":
        cmd_snap(h)
    elif sys.argv[1] == "noise":
        cmd_noise(h)
    else:
        cmd_diff(h, "--resnap" in sys.argv)


if __name__ == "__main__":
    main()
