"""LW-381 buffer watch: WHAT makes the game recycle a menu icon's resident buffer?

READ ONLY. Opens the game with PROCESS_QUERY_INFORMATION | PROCESS_VM_READ (0x0410) and
nothing else, so it cannot modify the game even by accident.

WHY
---
An icon that re-tiers mid-session gets correct bytes into the deployed .tex and into
modded.pac, and the inventory row keeps drawing the old art for a while before coming good on
its own. The scan/write probes established the shape of it: the engine keeps decoded copies of
each icon in RW heap memory, several per surface, and those buffers CHURN. Across three scans
minutes apart every address moved, copy counts changed, and two buffers were freed out from
under a write mid-experiment. A copy overwritten by hand is refilled from source (a no-kills
icon came back as plain base, which is exactly what the pac holds for it), so the pac IS the
source of truth on reload. What we do not know is what triggers the reload.

This probe answers that by watching, not guessing. It locates the resident copies once, then
polls each address cheaply and timestamps every transition:

    FREED    the buffer is gone (the read fails or the bytes stopped being this icon)
    CHANGED  still this icon, but a different bake than a moment ago
    NEW      a copy appeared at an address we were not watching

Do ONE deliberate thing at a time in game and note the clock: open the inventory list, close
it, move between party menu sections, move the cursor inside the list, open the map, enter a
battle. Then line your actions up against the timestamps. The trigger is whatever is standing
next to a burst of FREED/NEW.

A full sweep costs seconds, so it is only re-run when something actually moves; the steady
state cost is a handful of 64 byte reads per tick.

USAGE (game running)
--------------------
    python tools/probes/lw381_icon_bufwatch.py 4                 # watch icon 4, 120s
    python tools/probes/lw381_icon_bufwatch.py 4 --seconds 300
    python tools/probes/lw381_icon_bufwatch.py 4 --hz 8
"""
import ctypes
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from lw381_icon_memscan import (  # noqa: E402
    CHUNK, TYPE_NAME, find_pid, k32, measure_run, needles_for, read, regions,
)

# A 64 byte needle also matches by accident inside unrelated memory: the alignment pass found
# a 245 byte run (2 percent of a 12384 byte icon) that is not a copy of anything. A real
# resident copy runs 99.3 to 99.8 percent. Without this gate the watch latches onto noise,
# which never changes, and reports nothing for the whole run. Same constant, same reason, as
# lw381_icon_memwrite.MIN_COVERAGE.
MIN_COVERAGE = 0.95


def sweep(h, pins, only=None):
    """{addr: (label, file_off)} for every needle hit that is a REAL copy.

    Hits are coverage filtered (see MIN_COVERAGE): a needle match whose surrounding bytes do
    not also agree with the file is a coincidence, not a buffer, and watching one produces a
    silent run that looks like "nothing ever happens".
    """
    found = {}
    maxlen = max(len(n) for _, n, _, _ in pins)
    regs = only if only is not None else regions(h)
    for base, size, mtype in regs:
        carry, carry_addr, off = b"", base, 0
        while off < size:
            n = min(CHUNK, size - off)
            buf = read(h, base + off, n)
            if buf is None:
                break
            hay, hay_addr = carry + buf, carry_addr
            for label, needle, s, _ in pins:
                i = hay.find(needle)
                while i >= 0:
                    found[hay_addr + i] = (label, s)
                    i = hay.find(needle, i + 1)
            keep = min(len(hay), maxlen - 1)
            carry = hay[len(hay) - keep:] if keep else b""
            carry_addr = hay_addr + len(hay) - keep
            off += n
    real = {}
    blobs = {label: blob for label, _, _, blob in pins}
    for addr, (label, s_off) in found.items():
        lo, hi = measure_run(h, addr, s_off, blobs[label])
        if (hi - lo) / len(blobs[label]) >= MIN_COVERAGE:
            real[addr] = (label, s_off)
    return real


def arenas_for(h, addrs):
    """The committed regions containing the given addresses, for cheap re-sweeps."""
    regs = regions(h)
    keep = []
    for base, size, mtype in regs:
        if any(base <= a < base + size for a in addrs):
            keep.append((base, size, mtype))
    return keep


def main():
    argv, ids, seconds, hz = sys.argv[1:], [], 120.0, 4.0
    i = 0
    while i < len(argv):
        a = argv[i]
        if a == "--seconds":
            seconds = float(argv[i + 1]); i += 2; continue
        if a == "--hz":
            hz = float(argv[i + 1]); i += 2; continue
        if a.startswith("--"):
            print(f"unknown option {a}"); sys.exit(2)
        ids.append(int(a, 0)); i += 1
    if not ids:
        print(__doc__); sys.exit(2)

    pid = find_pid()
    if pid is None:
        print("fft_enhanced.exe is not running."); sys.exit(1)
    h = k32.OpenProcess(0x0410, False, pid)
    if not h:
        print(f"could not open pid {pid} (error {ctypes.get_last_error()})."); sys.exit(1)

    pins = needles_for(set(ids))
    if not pins:
        print("no usable needles for those ids."); sys.exit(1)
    by_needle = {label: (needle, s) for label, needle, s, _ in pins}

    print(f"game pid {pid}; locating resident copies (one full sweep) ...", flush=True)
    watched = sweep(h, pins)
    if not watched:
        # FINDING 2026-09-01: these buffers exist only while a menu is drawing the icon. With
        # the menu closed a full sweep finds nothing at all, which is why the first run of this
        # probe exited rather than watching. Waiting is the useful behavior: the caller opens
        # the list when ready and the watch latches onto the buffers the moment they appear.
        print("no resident copies yet; waiting for a menu to allocate them.", flush=True)
        print("Open the inventory list now (Ctrl+C to give up).", flush=True)
        waited = 0.0
        while not watched and waited < seconds:
            time.sleep(2.0)
            waited += 2.0
            watched = sweep(h, pins)
        if not watched:
            print(f"nothing appeared in {waited:.0f}s; is the icon on screen?")
            sys.exit(1)
        print(f"latched on after {waited:.0f}s\n", flush=True)
        seconds = max(seconds - waited, 60.0)
    arenas = arenas_for(h, watched.keys())
    print(f"watching {len(watched)} copy(ies) across {len(arenas)} arena region(s), "
          f"{seconds:.0f}s at {hz:.0f} Hz\n")
    for addr, (label, _) in sorted(watched.items()):
        print(f"  0x{addr:X}  {label}")
    print("\nDo ONE thing at a time in game and watch the clock.\n")
    print(f"{'time':>8}  {'event':<7} {'address':<14} what")
    print("-" * 74)

    t0 = time.time()
    period = 1.0 / hz
    resweep_due = False
    events = 0
    while time.time() - t0 < seconds:
        now = time.strftime("%H:%M:%S")
        for addr in list(watched.keys()):
            label, s = watched[addr]
            needle, _ = by_needle[label]
            cur = read(h, addr, len(needle))
            if cur is None or cur != needle:
                still = None
                if cur is not None:
                    for lab2, (nd2, s2) in by_needle.items():
                        if cur == nd2:
                            still = lab2
                            break
                if still:
                    print(f"{now}  {'CHANGED':<7} 0x{addr:<12X} {label} -> {still}", flush=True)
                    watched[addr] = (still, by_needle[still][1])
                else:
                    print(f"{now}  {'FREED':<7} 0x{addr:<12X} {label}", flush=True)
                    del watched[addr]
                    resweep_due = True
                events += 1
        if resweep_due:
            fresh = sweep(h, pins, only=arenas)
            for addr, val in fresh.items():
                if addr not in watched:
                    print(f"{now}  {'NEW':<7} 0x{addr:<12X} {val[0]}", flush=True)
                    watched[addr] = val
                    events += 1
            if not watched:
                arenas = arenas_for(h, fresh.keys()) or arenas
            resweep_due = False
        time.sleep(period)

    print("-" * 74)
    print(f"{events} transition(s) in {seconds:.0f}s. Line them up against what you did.")
    k32.CloseHandle(h)


if __name__ == "__main__":
    main()
