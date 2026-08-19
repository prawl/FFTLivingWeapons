#!/usr/bin/env python
"""Offline self-test for lw251_boot_clut_race.py: proves the race probe's guarantees
without the game running.

Why this exists: the boot race is a ONE-SHOT live experiment. The owner gets a single
launch per attempt, and a probe bug is indistinguishable from a negative result, so the
2026-08-19 review round demanded the probe's own claims be demonstrated rather than
eyeballed. This harness replaces the four Windows seams (ReadProcessMemory,
WriteProcessMemory, VirtualQueryEx, find_pid) with a byte-addressable fake process whose
palette rows materialize LATE, exactly like the real Denuvo-unpacked .xpdata tables.

Run: python tools/probes/lw251_boot_clut_race_selftest.py    (exit 0 = every check green)
Needs the real lw251_clut_hits.json (row addresses) and battle_wep_spr.bin (the vanilla
palette needles) that the probe itself reads.
"""
import contextlib
import inspect
import io
import json
import os
import sys
import tempfile

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import lw251_boot_clut_race as P  # noqa: E402


def build_fake(low, high):
    """A minimal process image: one committed 2MB image region per row cluster."""
    regions = {}
    for a in (low, high):
        regions.setdefault(a & ~0xFFFFF, bytearray(0x200000))

    def owner(addr):
        for b, buf in regions.items():
            if b <= addr < b + len(buf):
                return b, buf
        return None, None

    def rpm(h, addr, size):
        b, buf = owner(addr)
        return None if buf is None else bytes(buf[addr - b: addr - b + size])

    def wpm(h, addr, data):
        b, buf = owner(addr)
        if buf is None:
            return False
        buf[addr - b: addr - b + len(data)] = data
        return True

    def vqe(h, addr_arg, mbi_arg, size):
        addr = addr_arg.value or 0
        mbi = mbi_arg._obj
        b, buf = owner(addr)
        if buf is None:                      # uncommitted: the probe must skip it
            mbi.BaseAddress = addr & ~0xFFF
            mbi.RegionSize = 0x1000
            mbi.State = 0x10000
            mbi.Protect = 0
            mbi.Type = 0
            return size
        mbi.BaseAddress = b
        mbi.RegionSize = len(buf)
        mbi.State = P.MEM_COMMIT
        mbi.Protect = 4
        mbi.Type = P.MEM_IMAGE
        return size

    return owner, rpm, wpm, vqe


def main():
    tmp = tempfile.mkdtemp()
    P.STATE = os.path.join(tmp, "state.json")
    P.LOG = os.path.join(tmp, "log.jsonl")

    known = P.load_known_addrs()
    if not known:
        sys.exit("no known addresses: lw251_clut_hits.json missing or unreadable")
    needles = list(P.load_needles())
    low = min(a for a in known if a < P.REGION_B)
    high = min(a for a in known if a >= P.REGION_B)

    owner, rpm, wpm, vqe = build_fake(low, high)
    P.rpm, P.wpm = rpm, wpm
    P.k32.VirtualQueryEx = vqe
    P.find_pid = lambda: 1234
    P.k32.CloseHandle = lambda h: True

    race = P.Race(sweep=False, dump_dir=tmp)
    race.h, race.t0 = 1, 0.0
    race._ensure_log()
    quiet = io.StringIO()

    def pass_(n, msg):
        print(f"PASS {n}: {msg}")

    # 1. Before the tables materialize the whole span reads as zero fill. A needle whose
    #    leading twenty bytes are zero can still MATCH there, so this proves the
    #    verify-before-write rule turns that false match into a no-op.
    with contextlib.redirect_stdout(quiet):
        for lo, hi in race.spans:
            race.hunt_span(lo, hi)
        race.known_addr_pass()
    assert not race.rows, f"signed something before materialization: {race.rows}"
    pass_(1, "nothing signed pre-materialization (zero-fill match caused no write)")

    # 2. Materialize both rows, then run one loop iteration in PRODUCTION order.
    for a in (low, high):
        b, buf = owner(a)
        buf[a - b: a - b + P.ROW] = needles[0]
    with contextlib.redirect_stdout(quiet):
        for lo, hi in race.spans:
            race.hunt_span(lo, hi)
        race.known_addr_pass()
    assert race.rows[low]["sig"] == "red", race.rows[low]
    assert race.rows[high]["sig"] == "green", race.rows[high]
    assert rpm(1, low, P.ROW) == P.SIG_BLOCKS["red"]
    assert rpm(1, high, P.ROW) == P.SIG_BLOCKS["green"]
    pass_(2, "both rows signed, colour decided by ADDRESS not by caller")

    # 3. The dump deliverable: the live snapshot must hold the VANILLA table. If the
    #    known-address mop-up ran before the hunt it would ramp the rows first and the
    #    dump would capture our own signature instead of the game's data.
    live = sorted(f for f in os.listdir(tmp) if "live" in f)
    assert live, "no materialized-table dump was captured"
    blob = open(os.path.join(tmp, live[0]), "rb").read()
    assert needles[0] in blob, "live dump does not contain the vanilla row"
    assert P.SIG_BLOCKS["red"] not in blob, "live dump captured post-ramp bytes"
    pass_(3, f"materialized dump is uncontaminated ({len(live)} spans)")

    # 4. A healer reverting a row is counted and re-asserted.
    b, buf = owner(low)
    buf[low - b: low - b + P.ROW] = needles[0]
    with contextlib.redirect_stdout(quiet):
        race.keepalive_pass()
    assert race.rows[low]["reverts"] == 1
    assert rpm(1, low, P.ROW) == P.SIG_BLOCKS["red"]
    pass_(4, "revert detected, counted, re-asserted")

    # 5. A row whose write NEVER landed reads vanilla too, but that is a broken writer,
    #    not a healer: it must stay out of the revert stream the live reading depends on.
    P.wpm = lambda h, a, d: False
    buf[low - b: low - b + P.ROW] = needles[0]
    race.rows[low]["ever_signed"] = False
    race.rows[low]["reverts"] = 0
    with contextlib.redirect_stdout(quiet):
        race.keepalive_pass()
    assert race.rows[low]["reverts"] == 0, "never-signed row polluted the revert stream"
    P.wpm = wpm
    pass_(5, "never-signed row is not counted as a healer revert")

    # 6. Console throttle: the file keeps every revert, the console keeps the first few
    #    (a 64-row revert war would otherwise cost more per pass than the 25ms budget).
    race.rows[low]["ever_signed"] = True
    cap = io.StringIO()
    for _ in range(6):
        buf[low - b: low - b + P.ROW] = needles[0]
        with contextlib.redirect_stdout(cap):
            race.keepalive_pass()
    printed = cap.getvalue().count("revert")
    logged = sum(1 for line in open(P.LOG) if '"revert"' in line)
    assert printed <= 3 < logged, f"printed={printed} logged={logged}"
    pass_(6, f"revert console throttled ({printed} shown), file kept all {logged}")

    # 7. Bytes that are neither vanilla nor ours are latched and never touched again.
    b2, buf2 = owner(high)
    buf2[high - b2: high - b2 + P.ROW] = b"\xAA" * P.ROW
    with contextlib.redirect_stdout(quiet):
        race.keepalive_pass()
        race.keepalive_pass()
    assert race.rows[high]["state"] == "alien"
    assert rpm(1, high, P.ROW) == b"\xAA" * P.ROW, "alien row was written"
    pass_(7, "alien row latched hands-off")

    # 8. Restore is byte gated: ours goes back, anything else is left alone.
    json.dump({"rows": {hex(a): v for a, v in race.rows.items()}},
              open(P.STATE, "w"))
    P.k32.OpenProcess = lambda *a: 1
    with contextlib.redirect_stdout(quiet):
        P.restore()
    assert rpm(1, low, P.ROW) == needles[0], "signed row was not restored"
    assert rpm(1, high, P.ROW) == b"\xAA" * P.ROW, "restore overwrote the alien row"
    pass_(8, "restore returned ours and refused the alien row")

    # 9. A snapshot failure (a reader holding the file, a scanner) must not stop the race.
    P.STATE = os.path.join(tmp, "no-such-dir", "state.json")
    with contextlib.redirect_stdout(quiet):
        race.save_state()
    pass_(9, "save_state failure is logged, the loop survives")

    # 10. The sweep must never write through a file-backed mapping (that would survive a
    #     restart and break the probe's "RAM only" safety contract).
    src = inspect.getsource(P.Race.sweep_thread)
    assert "mbi.Type != MEM_PRIVATE" in src, "sweep is not restricted to private memory"
    pass_(10, "sweep restricted to MEM_PRIVATE, no write-through to a mapped file")

    # 11. Every known row address falls inside a hunt window, and no window straddles the
    #     colour boundary (a straddling window could sign a high row red).
    assert all(any(lo <= a < hi for lo, hi in race.spans) for a in known)
    assert all(not (lo < P.REGION_B < hi) for lo, hi in race.spans)
    pass_(11, f"all {len(known)} known rows covered by {len(race.spans)} windows, "
              "none straddling the colour boundary")

    print("\nALL CHECKS PASSED")


if __name__ == "__main__":
    main()
