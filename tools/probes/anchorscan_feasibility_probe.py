#!/usr/bin/env python
"""
AnchorScan feasibility probe (READ-ONLY -- cannot crash the game, never writes).

LW-82 premise verification, run BEFORE the AnchorScan core is designed/built. The runtime
plan is a boot-time scanner that re-finds anchors inside pin-neighborhood windows; this
probe measures, against the LIVE 1.5.1 process, the two facts that design rests on:

  1. jobcommand: the rec8+rec9 pair signature (jobcommand_find_probe.py's REC8_SIG/REC9_SIG,
     LaunchGuard.Landmarks.cs's Rec8Sig/Rec9Sig) hits EXACTLY ONCE in the pin +/- 4MB window.
     Zero hits = signature wrong / table not built; multiple = the fail-closed ambiguity
     case would fire on a healthy build (design problem).
  2. roster: a shape-only scan (no byte-exact signature exists for save data) for Ramza's
     roster row -- u16 nameId==1 at +0x230, level 1..99 at +0x1D, brave/faith 1..100 at
     +0x1E/+0x1F, sprite < 0x80 at +0x00 (LaunchGuard.ProbeRamzaRosterRow's shape) -- and
     how many candidate bases survive in the pin +/- 4MB window, at BOTH tiers: shape-only
     (measured 766 on 2026-07-14, so shape alone is dead) and the STRICT structural
     confirm (roster_stride_confirm below), which is the rule the runtime spec adopts.

Also measures scan wall-time per window (calibrates the in-process per-tick chunk budget).

USAGE:
  python anchorscan_feasibility_probe.py              # both live window scans (game running)
  python anchorscan_feasibility_probe.py exe <path>   # is the rec8/rec9 content baked into
                                                      # the exe FILE? (offline; no game needed)
"""
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ct_probe import PROC, PV, find_pid, k32, rd

# 1.5.1 pins (Offsets.cs / Barrage.cs, live-verified 2026-07-13, PORT_1.5.1_OFFSETS.md)
ABILITY_BASE = 0x14067E213          # Barrage.AbilityBase (rec0 AbilityId1)
REC = 25
REC8_SIG = bytes(range(150, 158))   # 0x96..0x9D Archer Aim
REC9_SIG = bytes(range(100, 108))   # 0x64..0x6B Monk Martial Arts
ROSTER_BASE = 0x1411A7D10
R_SPRITE, R_LEVEL, R_BRAVE, R_FAITH, R_NAMEID = 0x00, 0x1D, 0x1E, 0x1F, 0x230

WINDOW = 0x400000                   # pin +/- 4MB; 1.5 deltas peaked at +0x6C3C
                                    # (PORT_1.5_OFFSETS.md, LiveBattleMapId), ~151x margin
CHUNK = 0x100000                    # 1MB read chunks
NAMEID_SIG = bytes((1, 0))          # u16 == 1 little-endian (the only byte-stable roster fact)


def scan_window(h, lo, hi, sig, on_hit):
    """Find every offset of <sig> in [lo, hi); call on_hit(addr). Overlapped chunk reads;
    unreadable chunks skipped and counted. Returns (unreadable_chunks, seconds)."""
    seen = set()
    unreadable = 0
    t0 = time.time()
    addr = lo
    while addr < hi:
        n = min(CHUNK + len(sig) - 1, hi - addr)
        buf = rd(h, addr, n)
        if buf is None:
            unreadable += 1
        else:
            pos = buf.find(sig)
            while pos != -1:
                a = addr + pos
                if a not in seen:
                    seen.add(a)
                    on_hit(a, buf, pos)
                pos = buf.find(sig, pos + 1)
        addr += CHUNK
    return unreadable, time.time() - t0


def probe_jobcommand(h):
    print(f"== jobcommand pair signature, window 0x{ABILITY_BASE - WINDOW:012X}..0x{ABILITY_BASE + WINDOW:012X} ==")
    hits = []

    def on_hit(a, buf, pos):
        # rec9's 8 bytes must sit exactly REC after rec8's (confirm may cross the chunk edge;
        # re-read directly in that case).
        tail = buf[pos + REC:pos + REC + 8] if pos + REC + 8 <= len(buf) else rd(h, a + REC, 8)
        if bytes(tail or b"") == REC9_SIG:
            hits.append(a)

    unreadable, secs = scan_window(h, ABILITY_BASE - WINDOW, ABILITY_BASE + WINDOW, REC8_SIG, on_hit)
    expected_rec8 = ABILITY_BASE + 8 * REC
    for a in hits:
        base = a - 8 * REC
        print(f"  pair hit: rec8 at 0x{a:012X} -> base 0x{base:012X} "
              f"(delta from pin {base - ABILITY_BASE:+#x})")
    print(f"  RESULT: {len(hits)} pair hit(s); expected exactly 1 at rec8 0x{expected_rec8:012X}; "
          f"{unreadable} unreadable chunk(s); {secs:.2f}s\n")
    return hits


def probe_roster(h):
    print(f"== roster shape scan, window 0x{ROSTER_BASE - WINDOW:012X}..0x{ROSTER_BASE + WINDOW:012X} ==")
    # Orientation first: what does the PIN row read right now (is a save even loaded)?
    row = rd(h, ROSTER_BASE, 0x232)
    if row:
        print(f"  pin row: sprite=0x{row[R_SPRITE]:02X} level={row[R_LEVEL]} brave={row[R_BRAVE]} "
              f"faith={row[R_FAITH]} nameId={row[R_NAMEID] | (row[R_NAMEID + 1] << 8)}")
    else:
        print("  pin row: UNREADABLE")
    candidates = []   # (base, sprite, level, brave, faith)

    def on_hit(a, buf, pos):
        base = a - R_NAMEID
        if pos >= R_NAMEID:                       # confirm fields sit inside this chunk buffer
            win = buf[pos - R_NAMEID:pos]
        else:                                     # chunk-head edge: one direct re-read
            win = rd(h, base, R_NAMEID)
        if not win or len(win) < R_NAMEID:
            return
        sprite, level, brave, faith = win[R_SPRITE], win[R_LEVEL], win[R_BRAVE], win[R_FAITH]
        if 1 <= level <= 99 and 1 <= brave <= 100 and 1 <= faith <= 100 and sprite < 0x80:
            candidates.append((base, sprite, level, brave, faith))

    unreadable, secs = scan_window(h, ROSTER_BASE - WINDOW, ROSTER_BASE + WINDOW, NAMEID_SIG, on_hit)
    # STRICT tier: the shape-only rules measured 766 candidates on 2026-07-14 (a decoy table
    # near pin-0x3B2xx with 0x18-stride records aliases the lookback), so tier 2 needs the
    # roster's own STRUCTURE: demand slots +1..+3 at stride 0x258 each read row-like too
    # (level 0..99, brave/faith <= 100, nameId < 1024), with slot +1 populated (level 1..99,
    # nameId != 1: "slots +1..+7 = real party", Offsets.cs RosterBase note).
    strict = [c for c in candidates if roster_stride_confirm(h, c[0])]
    for base, sprite, level, brave, faith in candidates[:20]:
        mark = "  <-- PIN" if base == ROSTER_BASE else ""
        print(f"  candidate base 0x{base:012X} (delta {base - ROSTER_BASE:+#x}): "
              f"sprite=0x{sprite:02X} level={level} brave={brave} faith={faith}{mark}")
    if len(candidates) > 20:
        print(f"  ... and {len(candidates) - 20} more")
    hit_pin = any(base == ROSTER_BASE for base, *_ in candidates)
    print(f"  RESULT: {len(candidates)} shape candidate(s); pin {'AMONG THEM' if hit_pin else 'NOT FOUND'}; "
          f"{unreadable} unreadable chunk(s); {secs:.2f}s")
    for base, *_ in strict[:10]:
        mark = "  <-- PIN" if base == ROSTER_BASE else ""
        print(f"  STRICT survivor: 0x{base:012X} (delta {base - ROSTER_BASE:+#x}){mark}")
    strict_pin = any(base == ROSTER_BASE for base, *_ in strict)
    print(f"  STRICT RESULT: {len(strict)} survivor(s); pin {'AMONG THEM' if strict_pin else 'NOT FOUND'}\n")
    return candidates


def roster_stride_confirm(h, base):
    """Structural confirm for a roster-base candidate: 8-byte-aligned base (both known
    historical bases are 16-aligned: pre-1.5's 0x1411A18D0, 1.5/1.5.1's 0x1411A7D10, so
    %8 is the safe demand at n=2 samples; the measured decoy at pin-0x342 sits at
    %8 == 6), slots +1..+3 each read row-like at the 0x258 stride, and AT LEAST ONE of
    slots +1..+3 is a populated non-Ramza party row (any-of, not slot +1 specifically:
    solo-roster and early-prologue saves are unverified territory, so the demand stays as
    weak as the decoy data permits). 2026-07-14 measurement: shape alone = 766
    candidates; + stride structure = 2; + alignment = 1 (the pin)."""
    if base % 8 != 0:
        return False
    populated = False
    for k in (1, 2, 3):
        row = rd(h, base + k * 0x258, 0x232)
        if not row:
            return False
        name_id = row[R_NAMEID] | (row[R_NAMEID + 1] << 8)
        if row[R_LEVEL] > 99 or row[R_BRAVE] > 100 or row[R_FAITH] > 100 or name_id >= 1024:
            return False
        if 1 <= row[R_LEVEL] <= 99 and name_id != 1:
            populated = True
    return populated


def cmd_live():
    pid = find_pid(PROC)
    if not pid:
        print(f"{PROC} not running")
        return
    h = k32.OpenProcess(PV, False, pid)   # READ-ONLY handle
    try:
        probe_jobcommand(h)
        probe_roster(h)
    finally:
        k32.CloseHandle(h)


def cmd_exe(path):
    """Is the jobcommand content baked into the exe file? Present = offline signature
    validation against exe backups is possible; absent = the table is runtime-built and
    signatures can only be validated live (either answer is a usable design fact)."""
    data = open(path, "rb").read()
    print(f"{path}: {len(data)} bytes")
    pos, pairs = 0, []
    while True:
        pos = data.find(REC8_SIG, pos)
        if pos == -1:
            break
        if data[pos + REC:pos + REC + 8] == REC9_SIG:
            pairs.append(pos)
        pos += 1
    lone9 = data.count(REC9_SIG)
    print(f"  rec8+rec9 pair at file offset(s): {[hex(p) for p in pairs] or 'NONE'}")
    print(f"  (lone rec9 sig occurrences anywhere: {lone9})")
    print(f"  VERDICT: table content {'FILE-BAKED' if pairs else 'RUNTIME-BUILT (not in the file image)'}")


if __name__ == "__main__":
    if len(sys.argv) > 2 and sys.argv[1] == "exe":
        cmd_exe(sys.argv[2])
    else:
        cmd_live()
