"""THE TURN ORDER ARRAY (LW-118): can we read, and eventually REORDER, the Combat Timeline?

WHY THIS IS A DISCOVERY PROBE AND NOT A WRITE TOOL. The ledger's row is UNCERTAIN, dated
2026-06-16, and its own address is written with a tilde: "Combat Timeline is a 4-byte-record
array at ~0x140d3a04c ... record byte0 = CT (0x64 = 100), byte1 = a locator (matched the
clone's gx)". Everything about that sentence is a hypothesis: the address is approximate, the
field meanings come from one enrolldiff session, and the whole thing predates the 1.5.1
re-anchor. The re-anchor lesson (game-1-5-1 memory) is explicit: VERIFY AT THE OLD ADDRESS
BEFORE SCANNING. So round 1 reads and correlates only. No write verb exists in this file yet,
and none should until `dump` proves the array is real, current, and understood.

THE CORRELATION TEST (what makes a read decisive): every live unit's CT and tile X are
readable from the combat structs we already trust. If the candidate region really is the
timeline, its records must contain those exact pairs. Agreement across five-plus units is
proof by construction; one or two matches is coincidence.

PROTOCOL: CT marches while the battle clock runs, so a read and its cross-check can disagree
purely from timing. Sit on your own unit's OPEN MENU (the clock freezes, as the LW-113 sweep
established) before running dump or find.

    python tools\\probes\\turn_queue_probe.py dump [count]     # candidate records vs live units
    python tools\\probes\\turn_queue_probe.py find [+-window]  # fingerprint-scan for the array
    python tools\\probes\\turn_queue_probe.py watch [secs]     # changes only, while the clock runs
"""
import pathlib
import struct
import sys
import time

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
from battle_cheats import rpm, ru8, _require_game

UNITS = 0x141853CE0
HEAD = 0x140D3A410
CT = 0x41           # combat CT byte (verified in-tree 2026-07-22 against Offsets.ACtSlam)
GX = 0x4F           # combat logic tile X
CANDIDATE = 0x140D3A04C   # the ledger's approximate base; treat as a hypothesis, not a fact
RECORD = 4


def u64(a):
    b = rpm(a, 8)
    return None if b is None else struct.unpack("<Q", b)[0]


def live_units():
    """[(slot, ct, gx)] for every noded unit, read from the structs we already trust."""
    out = []
    cur = u64(HEAD)
    for _ in range(64):
        if not cur:
            break
        c = u64(cur + 0x148)
        off = (c or 0) - UNITS
        if c and 0 <= off < 21 * 0x200 and off % 0x200 == 0:
            out.append((off // 0x200, ru8(c + CT), ru8(c + GX)))
        cur = u64(cur)
    return out


def cmd_dump(count):
    units = live_units()
    print("live units (slot, ct, gx):")
    for s, ct, gx in units:
        print(f"  slot {s:>2}  ct={ct:>3}  gx={gx:>2}")
    buf = rpm(CANDIDATE, count * RECORD)
    if buf is None:
        print(f"\ncandidate {CANDIDATE:#x} unreadable: the address is stale or wrong. Run `find`.")
        return
    print(f"\ncandidate records at {CANDIDATE:#x}:")
    cts = {ct for _, ct, _ in units}
    gxs = {gx for _, _, gx in units}
    hits = 0
    for i in range(count):
        r = buf[i * RECORD:(i + 1) * RECORD]
        mark = ""
        if r[0] in cts and r[1] in gxs:
            mark = "  <-- matches a live (ct,gx) pair"
            hits += 1
        print(f"  [{i:>2}] {r.hex(' ')}{mark}")
    print(f"\n{hits} of {count} records match a live unit's (ct,gx). Five or more = the array is real "
          f"and current; zero or one = coincidence, run `find`.")


def cmd_find(window):
    """Fingerprint scan: hunt a run of 4-byte records whose (byte0,byte1) pairs reproduce the
    live units' (ct,gx) set. Bounded to a window around the candidate so this stays cheap; the
    re-anchor playbook says a moved anchor usually moves a little, not a lot."""
    units = live_units()
    want = {(ct, gx) for _, ct, gx in units if ct or gx}
    if len(want) < 3:
        print("need at least 3 distinct live (ct,gx) pairs to fingerprint; is a battle running?")
        return
    print(f"scanning {CANDIDATE - window:#x}..{CANDIDATE + window:#x} for >=3 of {len(want)} pairs")
    best = (0, None)
    for base in range(CANDIDATE - window, CANDIDATE + window, RECORD):
        buf = rpm(base, RECORD * len(units) * 2)
        if buf is None:
            continue
        # DISTINCT pairs matched, not total hits: a region full of one common (ct,gx) repeat
        # would otherwise outscore the real array.
        seen = {(buf[i * RECORD], buf[i * RECORD + 1]) for i in range(len(buf) // RECORD)}
        found = len(seen & want)
        if found > best[0]:
            best = (found, base)
    if best[1] is None or best[0] < 3:
        print("no candidate window matched. The array may have moved far, changed shape, or the "
              "June record model may be wrong. That is a result: ledger it before hunting wider.")
        return
    print(f"best match: {best[0]} pair hits at {best[1]:#x} "
          f"({'the ledger address' if best[1] == CANDIDATE else f'{best[1] - CANDIDATE:+#x} from the ledger address'})")
    print("re-run `dump` against that base if it differs, and update the ledger row either way.")


def cmd_watch(secs):
    """Sample the candidate region while the clock RUNS (close your menu). If this is the
    timeline, byte0s should march upward as CT accrues and reshuffle as turns resolve."""
    span = 24 * RECORD
    prev = None
    t0 = time.time()
    print(f"watching {CANDIDATE:#x} for {secs:.0f}s, changes only (close your menu so CT accrues)")
    try:
        while time.time() - t0 < secs:
            buf = rpm(CANDIDATE, span)
            if buf is not None and prev is not None and buf != prev:
                diffs = [(i, prev[i], buf[i]) for i in range(span) if prev[i] != buf[i]]
                head = ", ".join(f"+{i:#04x} {a:#04x}->{b:#04x}" for i, a, b in diffs[:6])
                print(f"  +{time.time() - t0:6.2f}s  {len(diffs)} byte(s): {head}")
            prev = buf
            time.sleep(0.2)
    except KeyboardInterrupt:
        print("stopped.")
    print("READ: byte0s marching upward = CT accrual, i.e. this IS the timeline. Static bytes "
          "while turns visibly pass = wrong region.")


def main():
    _require_game()
    a = sys.argv[1:]
    if a and a[0] == "dump":
        cmd_dump(int(a[1]) if len(a) > 1 else 24)
    elif a and a[0] == "find":
        cmd_find(int(a[1], 0) if len(a) > 1 else 0x2000)
    elif a and a[0] == "watch":
        cmd_watch(float(a[1]) if len(a) > 1 else 30.0)
    else:
        print(__doc__)


if __name__ == "__main__":
    main()
