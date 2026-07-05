#!/usr/bin/env python
"""
CRYSTAL COUNTER PROBE -- find the "3 hearts" death/crystal countdown byte by diffing across its ticks.

Patrick's approach (2026-06-16): rather than hunt the post-battle membership structure, watch a KO'd
unit's LIVE slot while its on-screen heart counter ticks 3->2->1->0, and find the byte that steps with
it. That byte IS the crystallization countdown -- the offset docs/research/NOT_LOSE_WEAPON.md called "unmapped".
If found + pinnable, holding it at 3 is the on-field "never crystallizes" Divine Intervention.

We watch the AUTHORITATIVE band family (0x14184C8AC, stride 0x200) -- the static array 0x140893C00
freezes on restart (LIVE_LEDGER), so a live countdown updates in the band. READ-ONLY (no writes).

The signal vs the noise: a dead unit's CT (+0x09 / +0x25) still cycles fast (0..100) as its "turns"
pass -- that's noise. The heart counter changes SLOWLY (once per its turn) and only ever DECREASES,
ending low. So we flag countdown steps live AND, on exit, print every offset whose value-history is
monotonically non-increasing and bottoms out <=3 -- the crystal counter stands out cleanly there.

USAGE (game running, in a live battle):
  python crystal_counter_probe.py list
      # show every KO'd / dead band unit (slot, br, fa, hp, dead-bit) so you can pick a target.

  python crystal_counter_probe.py watch <br> <fa> [seconds=180]
      # watch that dead unit's 0x200 slot. Let the battle run so its hearts tick 3->2->1. Each
      # ">>> COUNTDOWN" line is a byte that decremented to a small value -- correlate with the
      # on-screen heart drop. Ctrl-C (or timeout) prints the monotonic-decrease summary = the byte.

  python crystal_counter_probe.py pin <br> <fa> [floor=3] [seconds=180]
      # PROVE the pin works: hold the counter (combat +0x07) at >= floor while the unit is dead.
      # GREEN = hearts never reach 0, unit stays a revivable corpse the whole window. WALLED = it
      # crystallizes anyway (the event reads other state -> counter-pin is a dead end).

If NOTHING in the slot steps 3->2->1->0 in sync with the hearts, the counter lives in a SEPARATE
per-unit array (the PSX layout the doc warned about) -> escalate to a wide before/after diff.

FOUND 2026-06-16: the counter IS in the slot, at combat-slot base +0x07 (band entry -0x15).
"""
import pathlib
import struct
import sys
import time

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
from treasure_flags import rpm, ru8, wpm, _require_game

BAND, BSTRIDE, BSLOTS = 0x14184C8AC, 0x200, 49
BAND_ENTRY_OFF = 0x1C
# Combat-slot base +0x07 (== band entry -0x15): the death/crystal "3 hearts" countdown.
# Found live 2026-06-16 -- stepped 3->2->1->0 in sync with the on-screen hearts (crystal_counter watch).
COUNTER_OFF = 0x07
ALVL, ABR, AFA = 0x0D, 0x0E, 0x10
AHP, AMHP = 0x14, 0x16
AGX, AGY = 0x33, 0x34
ADEAD, DEAD_BIT = 0x45, 0x20
BATTLE_MODE, SLOT9 = 0x140900650, 0x14077CA54
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
    elif mode == "watch" and len(nums) >= 2:
        cmd_watch(nums[0], nums[1], float(nums[2]) if len(nums) >= 3 else 180.0)
    elif mode == "pin" and len(nums) >= 2:
        cmd_pin(nums[0], nums[1], nums[2] if len(nums) >= 3 else 3,
                float(nums[3]) if len(nums) >= 4 else 180.0)
    else:
        print(__doc__)


if __name__ == "__main__":
    main()
