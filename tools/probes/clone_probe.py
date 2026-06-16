#!/usr/bin/env python
"""
CLONE PROBE -- the spawn-a-unit WRITE test (step 2 of the spawn-an-enemy probe ladder).

formation_diff.py proved the engine builds a unit by filling a RESIDENT, zeroed slot IN PLACE
(no allocation/relocation) -- the full ~0x200 recipe, fields named. This probe tries to do the
same by hand: copy a STANDING donor unit's whole combat slot into an EMPTY slot mid-battle and
watch whether the engine adopts it. Cloning (vs forging from scratch) sidesteps the two open
unknowns at once: the donor's SPRITE is already resident (so it can render), and we copy a REAL
combat HEADER (the 0x1C pointer/handle zone we cannot fabricate).

This makes a UNIT on the donor's own side -- it does NOT flip team (that wall is separate and
known-dead). The question here is the prior one: can a hand-written slot become a real combatant
AT ALL? If yes, "spawn an ally" is reachable and "spawn an enemy" reduces to the team problem.

TWO RUNGS (default = the safe one):
  data-only (DEFAULT): write only the band-entry region (slot 0x1C onward); leave the target's
      0x1C header as its original zeros. CANNOT crash on stale pointers. Likely inert (no valid
      header) -- but if the engine adopts it anyway, the header doesn't matter. Establishes a
      no-crash baseline + the memory-level reaction.
  --full: also copy the donor's 0x1C combat header (real handles). The actual test -- and the
      crash risk, because two slots now point at the same sub-objects. Fire it watching the game.

SAFETY: gates on a LIVE battlefield (battleMode 2/3/4); REFUSES a non-empty target; snapshots the
target's original 0x200 and RESTORES it on exit (Ctrl-C/timeout/crash-free path) so no phantom is
stranded. Every write goes through the guarded wpm. The probe also polls the target after the
write and reports the engine's REACTION (slot zeroed back = rejected; inBattle/CT set = adopted)
-- a memory-level signal independent of what you see on screen (you are still the visual oracle).

USAGE (game running, IN a live battle):
  python clone_probe.py slots
      # list occupied donors (br,fa) + empty target slot indices.

  python clone_probe.py dryrun <donor_br> <donor_fa> <target_slot>
      # read-only: confirm the donor resolves, the target is empty, and show the payload summary.
      # Writes NOTHING.

  python clone_probe.py clone <donor_br> <donor_fa> <target_slot> [--full] [--hold] [--corpse] [--pos X Y] [seconds=20]
      # THE TEST. Copy donor -> target_slot. Default data-only (safe); --full incl. header.
      # --hold re-writes each tick (the engine may overwrite an unrecognized slot). --pos places
      # the clone on a chosen tile (default: donor's tile, x+1) so it doesn't perfectly overlap.
      # --corpse: target a DEAD unit's slot instead of an empty one. A slot that held a unit who
      #   died still owns its battle-init GRAPHIC OBJECT; if that survives death, cloning a donor
      #   in should RENDER (using the corpse's sprite). GREEN here = spawn-into-a-corpse works and
      #   we never need to hunt the graphic table. Restores the slot (empty or the corpse) on exit.

  python clone_probe.py enrolldiff <donor_br> <donor_fa> <empty_slot> [--full] [--wide]
      # PROBE 1: clone into an empty slot and diff memory before/after to find the EXTERNAL table
      # the engine writes when it enrolls the unit (the slot-indexed timeline/graphic binding). Runs
      # a no-clone control pass first to subtract churn. Targeted 3-region scan by default; --wide
      # snapshots the whole writable span. Survivors at a regular stride = the table; a qword into a
      # loaded region = the graphic-object link to forge. Restart the battle between runs.
"""
import pathlib
import struct
import sys
import time

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
from treasure_flags import rpm, wpm, ru8, _require_game, snapshot as wide_snapshot

BAND, BSTRIDE, BSLOTS = 0x14184C8AC, 0x200, 49   # band-entry base (== feign_probe), entry-relative offsets
BAND_ENTRY_OFF = 0x1C                             # combat header occupies entry-0x1C .. entry
ALVL, ABR, AFA = 0x0D, 0x0E, 0x10
AHP, AMHP = 0x14, 0x16
AINB = 0x12                                       # u16 inBattle flag (engine sets to 1 on adopt)
AGX, AGY = 0x33, 0x34
ACT = 0x09                                        # u8 CT (read)
ADEAD, DEAD_BIT = 0x45, 0x20                      # u8 status byte / Dead flag (corpse detection)
BATTLE_MODE, SLOT9 = 0x140900650, 0x14077CA54
HOLD_TICK, LOG_EVERY = 0.01, 0.5


def u16(a):
    b = rpm(a, 2)
    return struct.unpack("<H", b)[0] if b else None


def entry_addr(s):
    return BAND + s * BSTRIDE


def read_unit(s):
    a = entry_addr(s)
    lvl, br, fa = ru8(a + ALVL), ru8(a + ABR), ru8(a + AFA)
    mhp, gx, gy, hp = u16(a + AMHP), ru8(a + AGX), ru8(a + AGY), u16(a + AHP)
    dead = ru8(a + ADEAD)
    if None in (lvl, br, fa, mhp, gx, gy):
        return None
    sane = (1 <= lvl <= 99 and 1 <= br <= 100 and 1 <= fa <= 100
            and 1 <= mhp < 2000 and gx <= 30 and gy <= 30)
    empty = (lvl == 0 and br == 0)
    return {"slot": s, "addr": a, "base": a - BAND_ENTRY_OFF, "lvl": lvl, "br": br, "fa": fa,
            "mhp": mhp, "hp": hp, "gx": gx, "gy": gy, "occupied": sane, "empty": empty,
            "dead": bool(dead is not None and (dead & DEAD_BIT))}


def gate():
    _require_game()
    bm = rpm(BATTLE_MODE, 4)
    s9 = rpm(SLOT9, 4)
    bm = struct.unpack("<I", bm)[0] if bm else 0
    s9 = struct.unpack("<I", s9)[0] if s9 else 0
    if s9 != 0xFFFFFFFF or bm not in (2, 3, 4):
        print(f"need a LIVE battlefield (battleMode 2/3/4) for this write test. "
              f"Got battleMode={bm}, slot9={s9:#x}. Load into combat first.")
        sys.exit(1)


def find_donor(br, fa):
    units = [read_unit(s) for s in range(BSLOTS)]
    cands = [u for u in units if u and u["occupied"] and not u["dead"] and u["br"] == br and u["fa"] == fa]
    real = [u for u in cands if u["gx"] or u["gy"]]   # drop the frozen (0,0) twin if a real-pos copy exists
    cands = real or cands
    if not cands:
        return None, f"no occupied unit with brave={br} faith={fa}. Run `slots`."
    if len(cands) > 1:
        where = ", ".join(f"s{u['slot']}@({u['gx']},{u['gy']})" for u in cands)
        return None, f"ambiguous donor ({len(cands)}): {where}. Pick a unique (br,fa)."
    return cands[0], None


def cmd_slots():
    _require_game()
    units = [read_unit(s) for s in range(BSLOTS)]
    live = [u for u in units if u and u["occupied"] and not u["dead"]]
    corpses = [u for u in units if u and u["occupied"] and u["dead"]]
    empty = [u["slot"] for u in units if u and u["empty"]]
    print(f"=== band slots ({len(live)} live donor(s), {len(corpses)} corpse(s), "
          f"{len(empty)} empty target(s)) ===")
    for u in live:
        twin = " (0,0-twin)" if not (u["gx"] or u["gy"]) else ""
        print(f"  DONOR   s{u['slot']:>2}  br={u['br']:>3} fa={u['fa']:>3} lvl={u['lvl']:>2} "
              f"mhp={u['mhp']:>4} @({u['gx']},{u['gy']}){twin}")
    for u in corpses:
        print(f"  CORPSE  s{u['slot']:>2}  br={u['br']:>3} fa={u['fa']:>3} lvl={u['lvl']:>2} "
              f"mhp={u['mhp']:>4} @({u['gx']},{u['gy']})  <- --corpse target")
    print(f"  EMPTY target slots: {empty}")


def cmd_dryrun(br, fa, tslot):
    _require_game()
    donor, err = find_donor(br, fa)
    if err:
        print(err)
        sys.exit(1)
    target = read_unit(tslot)
    print(f"=== DRY RUN (writes nothing) ===")
    print(f"  donor : s{donor['slot']} br={donor['br']} fa={donor['fa']} lvl={donor['lvl']} "
          f"mhp={donor['mhp']} @({donor['gx']},{donor['gy']})  combat base {donor['base']:#014x}")
    if target is None:
        print(f"  target: slot {tslot} unreadable.")
        sys.exit(1)
    state = "EMPTY (ok)" if target["empty"] else f"OCCUPIED (REFUSED) lvl={target['lvl']} br={target['br']}"
    print(f"  target: slot {tslot} @ combat base {target['base']:#014x} -- {state}")
    blob = rpm(donor["base"], 0x200)
    print(f"  payload: {len(blob) if blob else 0} bytes from the donor combat slot "
          f"(data-only writes [0x1c:0x200]; --full writes [0:0x200]).")


def _poll(base, seconds, expected, payload=None, write_start=0):
    """Watch the target. The TRUE adoption signal is the engine changing bytes AWAY from what we
    wrote (our own copied bytes -- inBattle, hp -- do NOT count) and/or CT climbing toward a turn.
    `expected` = the full 0x200 the slot should read right after our write. payload!=None re-writes
    each tick (--hold) -- which masks engine writes, so prefer no --hold for the adoption test."""
    a = base + BAND_ENTRY_OFF
    end = time.monotonic() + seconds
    last = 0.0
    ct0 = ru8(a + ACT)
    hold_bytes = bytes(payload[write_start:]) if payload is not None else None  # ctypes needs bytes, not bytearray
    print(f"  watching slot for {seconds:.0f}s (CT0={ct0}; Ctrl-C to stop)...")
    while time.monotonic() < end:
        if hold_bytes is not None:
            wpm(base + write_start, hold_bytes)
        now = time.monotonic()
        if now - last >= LOG_EVERY:
            last = now
            live = rpm(base, 0x200)
            lvl, ct, hp = ru8(a + ALVL), ru8(a + ACT), u16(a + AHP)
            if lvl == 0:
                print("    [ZEROED -> engine REJECTED / reclaimed the slot]")
                continue
            diffs = [i for i in range(0x200) if live[i] != expected[i]] if live else []
            ct_tag = f"CT {ct0}->{ct}" + ("  CLIMBING(scheduled!)" if (ct and ct != ct0) else "")
            etag = (f"ENGINE touched {len(diffs)} byte(s): {[hex(i) for i in diffs[:8]]}"
                    if diffs else "no engine writes")
            print(f"    lvl={lvl} hp={hp} {ct_tag} | {etag}")
        time.sleep(HOLD_TICK)


def cmd_clone(args):
    gate()
    full = "--full" in args
    hold = "--hold" in args
    corpse = "--corpse" in args
    pos = None
    if "--pos" in args:
        i = args.index("--pos")
        pos = (int(args[i + 1]), int(args[i + 2]))
    nums = [int(x) for x in args if not x.startswith("--") and x.lstrip("-").isdigit()]
    # strip --pos's two values from the positional pool
    if pos:
        for v in pos:
            if v in nums:
                nums.remove(v)
    if len(nums) < 3:
        print("usage: clone <donor_br> <donor_fa> <target_slot> [--full] [--hold] [--pos X Y] [seconds]")
        sys.exit(1)
    br, fa, tslot = nums[0], nums[1], nums[2]
    seconds = float(nums[3]) if len(nums) >= 4 else 20.0

    donor, err = find_donor(br, fa)
    if err:
        print(err)
        sys.exit(1)
    target = read_unit(tslot)
    if target is None:
        print(f"target slot {tslot} unreadable.")
        sys.exit(1)
    if corpse:
        if not target["dead"]:
            print(f"--corpse needs a DEAD unit's slot (Dead bit +0x45/0x20 set). Slot {tslot}: "
                  f"empty={target['empty']} dead={target['dead']}. Run `slots` for CORPSE rows.")
            sys.exit(1)
    elif not target["empty"]:
        print(f"target slot {tslot} is not empty (refusing). Use --corpse to target a dead unit's "
              f"slot, or pick an EMPTY slot from `slots`.")
        sys.exit(1)

    donor_blob = rpm(donor["base"], 0x200)
    target_orig = rpm(target["base"], 0x200)
    if donor_blob is None or target_orig is None:
        print("could not read donor/target slot.")
        sys.exit(1)

    payload = bytearray(donor_blob)
    gx, gy = pos if pos else (min(donor["gx"] + 1, 30), donor["gy"])
    payload[BAND_ENTRY_OFF + AGX] = gx & 0xFF
    payload[BAND_ENTRY_OFF + AGY] = gy & 0xFF

    start = 0 if full else BAND_ENTRY_OFF
    expected = bytearray(target_orig)        # the slot should read this right after our write
    expected[start:0x200] = payload[start:0x200]
    tkind = "CORPSE" if corpse else "empty"
    flav = "--full (HEADER INCL -- crash risk)" if full else "data-only (safe)"
    print(f"=== CLONE {flav}{' [--corpse: reuse a dead slot graphic]' if corpse else ''} ===")
    print(f"  donor s{donor['slot']} (br={br} fa={fa}) -> {tkind} slot {tslot} @ ({gx},{gy})")
    print(f"  writing donor[{start:#x}:0x200] ({0x200 - start} bytes) to {target['base'] + start:#014x}")
    ok = wpm(target["base"] + start, bytes(payload[start:]))
    print(f"  write: {'ok' if ok else 'FAIL'}")

    try:
        _poll(target["base"], seconds, bytes(expected), payload=(payload if hold else None), write_start=start)
    except KeyboardInterrupt:
        print("\n  (interrupted)")
    finally:
        wpm(target["base"], target_orig)   # restore the slot to its original bytes (empty, or the corpse)
        lvl_after = ru8(entry_addr(tslot) + ALVL)
        print(f"  RESTORED slot {tslot} to original ({tkind}; lvl now {lvl_after}). "
              f"NOTE: enrollment can latch -- restart the battle to fully clear.")


# ---------------------------------------------------------------------------
# PROBE 1 -- enrollment diff: find WHERE the engine wrote when it adopted our clone.
# That external write maps the slot-indexed timeline/graphic table we need to forge.
# ---------------------------------------------------------------------------
ENROLL_REGIONS = [
    (0x14077D000, 0x1000, "turnqueue/render"),   # TurnQueue 0x14077D2A0 + render coords 0x14077D360/62
    (0x140890000, 0x8000, "static-array nbhd"),  # static array 0x140891600 + any adjacent pointer table
    (0x141870000, 0x1000, "ui-scratch"),         # equip/UI mirror scratch
]


def _snap_targeted():
    return {b: rpm(b, s) for b, s, _ in ENROLL_REGIONS}


def _diff_addrs(a, b):
    """Set of addresses where snapshots a and b differ (chunked: skip identical 4096-blocks)."""
    out = set()
    for base in set(a) & set(b):
        ba, bb = a[base], b[base]
        if ba is None or bb is None:
            continue
        n = min(len(ba), len(bb))
        for ci in range(0, n, 4096):
            if ba[ci:ci + 4096] == bb[ci:ci + 4096]:
                continue
            for i in range(ci, min(ci + 4096, n)):
                if ba[i] != bb[i]:
                    out.add(base + i)
    return out


def _byte(snap, addr):
    for base, buf in snap.items():
        if buf and base <= addr < base + len(buf):
            return buf[addr - base]
    return 0


def _qword(snap, addr):
    for base, buf in snap.items():
        if buf and base <= addr <= base + len(buf) - 8:
            return int.from_bytes(buf[addr - base:addr - base + 8], "little")
    return None


def _regname(addr):
    for b, s, name in ENROLL_REGIONS:
        if b <= addr < b + s:
            return name
    return "wide"


def _runs(addrs):
    runs = []
    for a in sorted(addrs):
        if runs and a == runs[-1][-1] + 1:
            runs[-1].append(a)
        else:
            runs.append([a])
    return runs


def cmd_enrolldiff(args):
    """Clone a donor into an empty slot and diff memory before/after to localize the EXTERNAL
    table the engine writes when it enrolls the unit (the timeline/graphic binding). A no-clone
    control pass first learns the churn (RNG/CT/frame counters) so we can subtract it. Targeted
    3-neighborhood scan by default; --wide snapshots the whole writable span (slower)."""
    gate()
    full = "--full" in args
    wide = "--wide" in args
    nums = [int(x) for x in args if not x.startswith("--") and x.lstrip("-").isdigit()]
    if len(nums) < 3:
        print("usage: enrolldiff <donor_br> <donor_fa> <empty_slot> [--full] [--wide]")
        sys.exit(1)
    br, fa, tslot = nums[0], nums[1], nums[2]
    donor, err = find_donor(br, fa)
    if err:
        print(err)
        sys.exit(1)
    target = read_unit(tslot)
    if target is None or not target["empty"]:
        print(f"enrolldiff needs an EMPTY target slot. slot {tslot}: empty={target and target['empty']}.")
        sys.exit(1)

    snapfn = wide_snapshot if wide else _snap_targeted
    WAIT = 2.5
    print(f"=== ENROLL DIFF ({'WIDE whole-span' if wide else 'targeted 3-region'}) ===")
    print("  control pass (no clone) -- learning churn...")
    c0 = snapfn()
    time.sleep(WAIT)
    c1 = snapfn()
    churn = _diff_addrs(c0, c1)
    del c0, c1
    print(f"    churn addresses to ignore: {len(churn)}")

    donor_blob = rpm(donor["base"], 0x200)
    target_orig = rpm(target["base"], 0x200)
    payload = bytearray(donor_blob)
    payload[BAND_ENTRY_OFF + AGX] = min(donor["gx"] + 1, 30) & 0xFF
    payload[BAND_ENTRY_OFF + AGY] = donor["gy"] & 0xFF
    start = 0 if full else BAND_ENTRY_OFF

    print(f"  clone pass: donor s{donor['slot']} -> slot {tslot} ({'full' if full else 'data-only'})...")
    b0 = snapfn()
    wpm(target["base"] + start, bytes(payload[start:]))
    time.sleep(WAIT)
    b1 = snapfn()
    lo, hi = target["base"], target["base"] + 0x200
    survivors = [a for a in (_diff_addrs(b0, b1) - churn) if not (lo <= a < hi)]

    wpm(target["base"], target_orig)   # restore; enrollment may latch (restart to clear)

    if not survivors:
        print("\n  NO external survivors. The engine wrote NOTHING outside our slot when it enrolled\n"
              "  the unit -> the timeline/graphic binding is computed on-demand or built by an init\n"
              "  function we can't reach by writing. (Try --wide once before concluding.)")
        return
    print(f"\n  {len(survivors)} external survivor byte(s) (changed ONLY on clone):")
    for run in _runs(survivors):
        bef = bytes(_byte(b0, x) for x in run).hex(" ")
        aft = bytes(_byte(b1, x) for x in run).hex(" ")
        print(f"    {run[0]:#013x}..{run[-1]:#013x} [{_regname(run[0])}]  {bef} -> {aft}")
    ptrs = []
    for a in survivors:
        if a % 8:
            continue
        q = _qword(b1, a)
        if q and 0x140000000 <= q < 0x150000000 and _qword(b0, a) != q:
            ptrs.append((a, q))
    if ptrs:
        print("\n  POINTER-like writes (qword into module range) -- prime graphic/timeline table leads:")
        for a, q in ptrs:
            note = " -> OUR clone slot!" if lo <= q < hi else (
                " -> donor slot" if donor["base"] <= q < donor["base"] + 0x200 else "")
            print(f"    {a:#013x} = {q:#013x}{note}")
    print("\n  Survivors at a regular stride = the slot-indexed table. A pointer into a loaded\n"
          "  region = the graphic-object link. Restart the battle before the next run (enroll latches).")


def main():
    mode = sys.argv[1] if len(sys.argv) > 1 else ""
    rest = sys.argv[2:]
    if mode == "slots":
        cmd_slots()
    elif mode == "dryrun" and len(rest) >= 3:
        cmd_dryrun(int(rest[0]), int(rest[1]), int(rest[2]))
    elif mode == "clone" and len(rest) >= 3:
        cmd_clone(rest)
    elif mode == "enrolldiff" and len(rest) >= 3:
        cmd_enrolldiff(rest)
    else:
        print(__doc__)


if __name__ == "__main__":
    main()
