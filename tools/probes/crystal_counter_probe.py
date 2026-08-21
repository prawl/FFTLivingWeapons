#!/usr/bin/env python
"""
CRYSTAL COUNTER PROBE -- the "3 hearts" death/crystal countdown byte, and how to switch it OFF.

The counter is combat-slot base +0x07 (== band entry -0x15). Found 2026-06-16 by watching a KO'd
unit's slot while its on-screen hearts ticked 3->2->1->0 and finding the byte that stepped with it.

2026-08-21, THE BIG ONE: the game has its OWN "no crystallization" state and it is 0xFF in this
same byte. Owner noticed a KO'd Ramza shows no hearts at all in the first battle; a dump there
read 255 on EVERY unit on the field, against 3 on every unit of a normal battle. Verified live,
both directions, on a guest unit mid-countdown:

    byte tracks the display exactly   3 hearts -> 3, 2 hearts -> 2
    write 0xFF  -> hearts VANISH outright (not "255 hearts"), and the game does NOT re-write it
    write 2     -> hearts come straight back, two of them

So suppression is a single write and it is REVERSIBLE, which is what a signature needs in order to
lift when its bearer dies or unequips. This supersedes the per-tick counter-pin Sanctuary ships
today (docs/TODO.md LW-299).

Petrify is NOT death for this purpose: a petrified unit keeps full HP, keeps the dead bit clear,
and its counter is never armed (measured same session, zero bytes changed in combat +0x00..0x17).

NOT read-only any more. list/watch/dump/diff read; pin/suppress/set WRITE to the live game.

USAGE (game running, in a live battle):
  list                          every KO'd / dead band unit, so you can pick a target
  watch <br> <fa> [secs]        watch a dead unit's slot; ">>> COUNTDOWN" = a byte stepping down
  dump <tag>                    snapshot +0x07 (and combat +0x00..0x17) for EVERY unit -> json
  diff <tagA> <tagB>            compare two dumps, keyed by SLOT (see cmd_diff for why not stats)
  pin <br> <fa> [floor] [secs]  hold the counter at >= floor (the OLD mechanism)
  suppress <br> <fa> [secs]     write 0xFF and watch whether the game claws it back
  set <br> <fa> <value>         write any value; use to restore, or to test 0x7F vs 0xFF

STILL OPEN: whether 0xFF is a true sentinel or merely "greater than 3" (write 0x7F to find out),
and revive / battle-exit behaviour on a unit that was suppressed and restored.
"""
import json
import pathlib
import struct
import sys
import time

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
# Was treasure_flags, which went with the Treasure Master module (LW-10, 2026-08-14). This probe
# has been un-runnable since that removal; battle_cheats carries the same four helpers with the
# same signatures, so this is a repoint and not a rewrite.
from battle_cheats import rpm, ru8, wpm, _require_game

BSLOTS = 49
BAND_ENTRY_OFF = 0x1C
# BAND was hardcoded 0x14184C8AC, a PRE-1.5 address, and stayed stale through the 1.5 re-anchor
# (+0x6450). The probe therefore read 49 empty slots and reported "0 band unit(s)" while sitting in
# a perfectly good battle: a silent wrong answer, not an error. Derive it instead, from the same
# Offsets.cs the runtime uses, so a future re-anchor moves this with it. This mirrors
# Offsets.BandReadBase (CombatAnchor + BandEntry - 24*CombatStride); that constant is an expression
# and lib.offsets only parses literals, so the arithmetic is repeated here rather than read.
# Combat-slot base +0x07 (== band entry -0x15): the death/crystal "3 hearts" countdown.
# Found live 2026-06-16 -- stepped 3->2->1->0 in sync with the on-screen hearts (crystal_counter watch).
COUNTER_OFF = 0x07
ALVL, ABR, AFA = 0x0D, 0x0E, 0x10
AHP, AMHP = 0x14, 0x16
AGX, AGY = 0x33, 0x34
ADEAD, DEAD_BIT = 0x45, 0x20
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1]))
from lib import offsets as _offsets
BATTLE_MODE, SLOT9, _CANCHOR, _CSTRIDE, _BENTRY = _offsets.require(
    ["BattleMode", "Slot9", "CombatAnchor", "CombatStride", "BandEntry"])   # LW-41: from Offsets.cs, not stale copies
BAND, BSTRIDE = _CANCHOR + _BENTRY - 24 * _CSTRIDE, _CSTRIDE
TICK, LOG_EVERY = 0.05, 0.0


def u16(a):
    b = rpm(a, 2)
    return struct.unpack("<H", b)[0] if b else None


def entry_addr(s):
    return BAND + s * BSTRIDE


def read_unit(s):
    a = entry_addr(s)
    lvl, br, fa = ru8(a + ALVL), ru8(a + ABR), ru8(a + AFA)
    mhp, gx, gy, hp, dead = u16(a + AMHP), ru8(a + AGX), ru8(a + AGY), u16(a + AHP), ru8(a + ADEAD)
    if None in (lvl, br, fa, mhp, gx, gy):
        return None
    if not (1 <= lvl <= 99 and 1 <= br <= 100 and 1 <= fa <= 100 and 1 <= mhp < 2000
            and gx <= 30 and gy <= 30):
        return None
    return {"slot": s, "addr": a, "base": a - BAND_ENTRY_OFF, "lvl": lvl, "br": br, "fa": fa,
            "hp": hp, "dead": bool(dead is not None and (dead & DEAD_BIT)), "gx": gx, "gy": gy}


def dead_units():
    out = []
    for s in range(BSLOTS):
        u = read_unit(s)
        if u and (u["dead"] or u["hp"] == 0):
            out.append(u)
    return out


def gate():
    _require_game()
    s9 = rpm(SLOT9, 4)
    s9 = struct.unpack("<I", s9)[0] if s9 else 0
    bm = rpm(BATTLE_MODE, 4)
    bm = struct.unpack("<I", bm)[0] if bm else 0
    if s9 != 0xFFFFFFFF or bm == 0:
        print(f"need a live battle (battleMode={bm}, slot9={s9:#x}).")
        sys.exit(1)


def fmt_off(i):
    off = i - BAND_ENTRY_OFF
    return f"+0x{off:02x}" if off >= 0 else f"hdr+0x{i:02x}"


def cmd_list():
    gate()
    dead = dead_units()
    print(f"=== {len(dead)} KO'd / dead band unit(s) ===")
    for u in dead:
        flag = "DEAD" if u["dead"] else "hp0 "
        print(f"  s{u['slot']:>2} br={u['br']:>3} fa={u['fa']:>3} lvl={u['lvl']:>2} "
              f"hp={u['hp']:>3} ({u['gx']},{u['gy']}) {flag}")
    print("\nPick one whose HEARTS are visibly counting down, then:")
    print("  python crystal_counter_probe.py watch <br> <fa>")


def cmd_dump(tag):
    """Snapshot the countdown byte for EVERY band unit, alive or dead, and save it for diffing.

    WHY (owner observation 2026-08-21): in the first battle a KO'd Ramza shows NO hearts at all,
    where a normal battle shows three. If the game already has a "no countdown" state, we would
    rather write THAT than keep re-pinning the counter to 3 every tick the way Sanctuary does.

    Two hypotheses this separates, and they need different fixes:
      H1 PER-UNIT SENTINEL. +0x07 holds a distinguished value (0, 0xFF, something >3) meaning
         "this unit never crystallizes". If so the fix is one write per protected unit and the
         tick-by-tick pin can retire.
      H2 PER-BATTLE SUPPRESSION. +0x07 looks identical in both battles and the countdown is
         gated somewhere else entirely (encounter data, a story-battle flag, the UI layer).
         Then the counter is a red herring for "disable" and only the pin works on the field.

    Dump in BOTH places, then diff the two json files. The neighbour bytes are carried because if
    H1 is false the flag is plausibly next door, and re-walking the owner through a second capture
    costs far more than reading 16 extra bytes now. READ-ONLY.
    """
    gate()
    rows = []
    for s in range(BSLOTS):
        u = read_unit(s)
        if not u:
            continue
        base = u["base"]
        nb = rpm(base, 0x18)
        rows.append({
            "slot": u["slot"], "br": u["br"], "fa": u["fa"], "lvl": u["lvl"],
            "hp": u["hp"], "mhp": u16(u["addr"] + AMHP), "dead": u["dead"],
            "gx": u["gx"], "gy": u["gy"],
            "counter_0x07": ru8(base + COUNTER_OFF),
            "base_0x00_0x17": list(nb) if nb else None,
        })
    print(f"=== {len(rows)} band unit(s), countdown byte at combat +0x07 ===")
    print(f"{'slot':>4} {'br':>3} {'fa':>3} {'lvl':>3} {'hp':>4}/{'mhp':<4} {'dead':>5} {'+0x07':>6}")
    for r in rows:
        print(f"{r['slot']:>4} {r['br']:>3} {r['fa']:>3} {r['lvl']:>3} "
              f"{r['hp']:>4}/{str(r['mhp']):<4} {str(r['dead']):>5} {str(r['counter_0x07']):>6}")
    out = pathlib.Path(__file__).resolve().parent / f"crystal_dump_{tag}.json"
    out.write_text(json.dumps(rows, indent=1), encoding="utf-8")
    print(f"\nsaved -> {out}")
    print("Capture the OTHER battle too, then: python crystal_counter_probe.py diff <tagA> <tagB>")


def cmd_diff(a, b):
    """Diff two dumps on the countdown byte, matching units by brave+faith+level.

    Prints only what DIFFERS, because the whole question is whether the suppressed battle holds a
    different value. A clean "no unit differs on +0x07" is a real result: it kills H1 and sends the
    hunt to the neighbour bytes, then off-slot entirely.
    """
    d = pathlib.Path(__file__).resolve().parent
    ra = json.loads((d / f"crystal_dump_{a}.json").read_text(encoding="utf-8"))
    rb = json.loads((d / f"crystal_dump_{b}.json").read_text(encoding="utf-8"))
    # Key by SLOT, not by br+fa+lvl. The band carries MIRROR seats that clone a real unit's
    # stats exactly (band-mirror-frame note), so a stat key collides and the mirror silently
    # overwrites the real seat in the lookup. That produced a confident FALSE reading on
    # 2026-08-21 ("petrify moved +0x07 from 3 to 0") that the raw table flatly contradicted.
    # Slots are stable within a battle, which is the only case where a per-unit diff is meaningful.
    key = lambda r: r["slot"]
    mb = {key(r): r for r in rb}
    print(f"=== +0x07 diff: {a} vs {b} ===")
    shared = 0
    for r in ra:
        o = mb.get(key(r))
        if not o:
            continue
        shared += 1
        if r["counter_0x07"] != o["counter_0x07"]:
            print(f"  br={r['br']} fa={r['fa']} lvl={r['lvl']}: "
                  f"{a}={r['counter_0x07']} {b}={o['counter_0x07']}  "
                  f"(dead {r['dead']} vs {o['dead']})")
    print(f"{shared} unit(s) matched across both dumps.")
    print("\nNeighbour bytes that differ on a unit present in both (H1 fallback):")
    for r in ra:
        o = mb.get(key(r))
        if not o or not r["base_0x00_0x17"] or not o["base_0x00_0x17"]:
            continue
        d2 = [i for i in range(0x18) if r["base_0x00_0x17"][i] != o["base_0x00_0x17"][i]]
        if d2:
            print(f"  br={r['br']} fa={r['fa']}: offsets {[hex(i) for i in d2]}")


def cmd_suppress(br, fa, seconds=60.0):
    """THE DECISIVE WRITE: put 0xFF in a KO'd unit's +0x07 and see if the hearts vanish.

    Reading established (2026-08-21) that a battle where nobody crystallizes carries +0x07 == 255
    on EVERY unit, while a normal battle carries 3. That is correlation only. Two readings still
    fit and they are different mechanisms:
      A. 0xFF is a real sentinel the countdown checks  -> writing it here KILLS the hearts outright.
      B. 0xFF is merely "uninitialised" and that battle suppresses crystallization elsewhere
         -> writing it here reads as 255 HEARTS, so the display shows a count that simply never
         runs out, or shows something nonsensical.
    Both leave us a working weapon; only A is a mechanism worth putting in the ledger, and only A
    lets Sanctuary retire its per-tick pin for a single write.

    The screen is the instrument here, not this script. The script reports whether the value STICKS
    (a game that rewrites it every turn tells us the countdown owns the byte) and the owner reports
    what the hearts actually did. Writes 0xFF once, then only re-writes if the game clobbers it.
    """
    gate()
    u = _locate(br, fa)
    if u is None:
        print(f"no band unit with brave={br} faith={fa}. Run `list` or `dump`.")
        sys.exit(1)
    base = u["base"]
    pre = ru8(base + COUNTER_OFF)
    print(f"=== SUPPRESS s{u['slot']} br={br} fa={fa} lvl={u['lvl']} hp={u['hp']} @ {base:#014x} ===")
    print(f"  +0x07 before write: {pre}   (expect 3 in a normal battle)")
    if not wpm(base + COUNTER_OFF, bytes([0xFF])):
        print("  WRITE FAILED (guarded refusal). Nothing changed.")
        sys.exit(1)
    print(f"  wrote 0xFF, reads back: {ru8(base + COUNTER_OFF)}")
    print("\n  >>> LOOK AT THE SCREEN NOW. Which is it?")
    print("      (a) hearts GONE entirely            -> 0xFF is a real suppress sentinel")
    print("      (b) hearts still there, not ticking -> 0xFF is just a big number")
    print("      (c) something else / garbled        -> say exactly what\n")
    rewrites, last = 0, 0xFF
    t0 = time.time()
    while time.time() - t0 < seconds:
        v = ru8(base + COUNTER_OFF)
        if v is None:
            print("  slot went unreadable (battle ended?)")
            break
        if v != last:
            print(f"  [{time.time()-t0:6.1f}s] value changed {last} -> {v}"
                  + ("   <-- THE GAME IS WRITING THIS BYTE" if v != 0xFF else ""))
            if v != 0xFF:
                rewrites += 1
                wpm(base + COUNTER_OFF, bytes([0xFF]))
            last = v
        time.sleep(0.25)
    print(f"\n  done. game overwrote our value {rewrites} time(s).")
    print("  0 rewrites = the byte is ours to set. Many = the countdown owns it and re-pins.")


def cmd_set(br, fa, value):
    """Write an arbitrary value to a unit's +0x07 and report the before/after.

    Exists for the REVERSE direction. Suppressing hearts with 0xFF is only half a shippable
    mechanism: Sanctuary must LIFT the moment its bearer dies or unequips, so putting a real
    countdown BACK has to work as reliably as taking it away. If 0xFF is a one-way door the
    feature cannot honour its own contract and the per-tick pin has to stay.

    Also the cheap way to test whether 0xFF is a genuine sentinel or merely "greater than 3":
    write 0x7F and see whether the hearts stay gone or come back as some large count.
    """
    gate()
    u = _locate(br, fa)
    if u is None:
        print(f"no band unit with brave={br} faith={fa}. Run `list` or `dump`.")
        sys.exit(1)
    base = u["base"]
    pre = ru8(base + COUNTER_OFF)
    if not wpm(base + COUNTER_OFF, bytes([value & 0xFF])):
        print("  WRITE FAILED (guarded refusal). Nothing changed.")
        sys.exit(1)
    post = ru8(base + COUNTER_OFF)
    print(f"=== SET s{u['slot']} br={br} fa={fa} dead={u['dead']} hp={u['hp']} ===")
    print(f"  +0x07: {pre} -> {post}")
    print("\n  >>> LOOK AT THE SCREEN. Did the hearts come back, and how many?")


def _locate(br, fa):
    cands = [u for u in (read_unit(s) for s in range(BSLOTS)) if u and u["br"] == br and u["fa"] == fa]
    real = [u for u in cands if u["gx"] or u["gy"]]
    cands = real or cands
    if not cands:
        return None
    return cands[0]


def cmd_watch(br, fa, seconds):
    gate()
    u = _locate(br, fa)
    if u is None:
        print(f"no band unit with brave={br} faith={fa}. Run `list`.")
        sys.exit(1)
    base = u["base"]
    prev = rpm(base, 0x200)
    if prev is None:
        print("could not read the unit slot.")
        sys.exit(1)
    print(f"=== WATCH dead unit s{u['slot']} br={br} fa={fa} @ {base:#014x} for {seconds:.0f}s ===")
    print("Let the battle run so its hearts tick down. ' >>> COUNTDOWN' = a byte stepping to a small")
    print("value (correlate with the on-screen heart drop). Ctrl-C for the monotonic-decrease summary.\n")

    history = {}     # offset -> [values], first entry = original
    end = time.monotonic() + seconds
    try:
        while time.monotonic() < end:
            cur = rpm(base, 0x200)
            if cur is None or ru8(base + BAND_ENTRY_OFF + ABR) != br:
                v = _locate(br, fa)
                if v is None:
                    print("target left the band (revived/cleared). Stopping.")
                    break
                base = v["base"]
                cur = rpm(base, 0x200)
                if cur is None:
                    continue
            for i in range(0x200):
                if cur[i] != prev[i]:
                    history.setdefault(i, [prev[i]]).append(cur[i])
                    if cur[i] == prev[i] - 1 and cur[i] <= 5:
                        t = seconds - (end - time.monotonic())
                        print(f"  >>> COUNTDOWN  {fmt_off(i):>9}  {prev[i]} -> {cur[i]}   [+{t:5.1f}s]")
            prev = cur
            time.sleep(TICK)
    except KeyboardInterrupt:
        pass

    print("\n=== monotonic-decrease summary (crystal-counter candidates) ===")
    found = False
    for off in sorted(history):
        seq = []
        for v in history[off]:
            if not seq or seq[-1] != v:
                seq.append(v)
        if len(seq) >= 2 and all(seq[k] >= seq[k + 1] for k in range(len(seq) - 1)) and seq[-1] <= 3:
            found = True
            print(f"  {fmt_off(off):>9}  {' -> '.join(map(str, seq))}")
    if not found:
        print("  (none) -> no slot byte stepped cleanly down to <=3. The counter is likely in a")
        print("  SEPARATE per-unit array (PSX layout) -> we escalate to a wide before/after diff.")


def cmd_pin(br, fa, floor, seconds):
    """Hold the counter (combat +0x07) at >= floor while the unit is dead, to PROVE whether pinning
    it prevents crystallization. floor=3 keeps a wide margin (engine dips it to 2, we restore to 3
    long before its next turn). GREEN = the unit stays a revivable corpse for the whole window;
    WALLED = it crystallizes anyway (the event reads other state -> abandon counter-pin)."""
    gate()
    u = _locate(br, fa)
    if u is None:
        print(f"no band unit with brave={br} faith={fa}. Run `list`.")
        sys.exit(1)
    base = u["base"]
    print(f"=== PIN crystal counter (combat +0x07) at >= {floor} on dead unit s{u['slot']} "
          f"br={br} fa={fa} ===")
    print(f"holding up to {seconds:.0f}s. WATCH the hearts: do they stay >=1 forever? Try a Phoenix")
    print("Down -- does it still revive? (Ctrl-C to stop.)\n")
    end = time.monotonic() + seconds
    last, holds = 0.0, 0
    try:
        while time.monotonic() < end:
            v = _locate(br, fa)
            if v is None:
                print("  unit LEFT the band -> it crystallized or was revived. If it CRYSTALLIZED, "
                      "the pin FAILED (event reads other state).")
                break
            base = v["base"]
            cur = ru8(base + COUNTER_OFF)
            if cur is not None and cur < floor and wpm(base + COUNTER_OFF, bytes([floor & 0xFF])):
                holds += 1
            now = time.monotonic()
            if now - last >= 0.5:
                last = now
                print(f"  +0x07={ru8(base + COUNTER_OFF)} hp={u16(base + BAND_ENTRY_OFF + AHP)} "
                      f"holds={holds}  [+{seconds-(end-now):5.1f}s]")
            time.sleep(TICK)
    except KeyboardInterrupt:
        pass
    finally:
        print("stopped holding; the counter resumes its natural countdown.")


def main():
    mode = sys.argv[1] if len(sys.argv) > 1 else ""
    rest = sys.argv[2:]
    nums = [int(x) for x in rest if x.lstrip("-").isdigit()]
    if mode == "list":
        cmd_list()
    elif mode == "set" and len(nums) >= 3:
        cmd_set(nums[0], nums[1], nums[2])
    elif mode == "suppress" and len(nums) >= 2:
        cmd_suppress(nums[0], nums[1], float(nums[2]) if len(nums) >= 3 else 60.0)
    elif mode == "dump":
        cmd_dump(rest[0] if rest else "x")
    elif mode == "diff" and len(rest) >= 2:
        cmd_diff(rest[0], rest[1])
    elif mode == "watch" and len(nums) >= 2:
        cmd_watch(nums[0], nums[1], float(nums[2]) if len(nums) >= 3 else 180.0)
    elif mode == "pin" and len(nums) >= 2:
        cmd_pin(nums[0], nums[1], nums[2] if len(nums) >= 3 else 3,
                float(nums[3]) if len(nums) >= 4 else 180.0)
    else:
        print(__doc__)


if __name__ == "__main__":
    main()
