"""FUNCTIONAL test for a stolen Haste buff: does the held +0x48/0x08 bit actually speed up CT,
or is it cosmetic like Float?

Haste's ONLY mechanical effect is faster charge-time accumulation: a hasted unit gains CT ~1.5x
faster per active-turn tick than its raw Speed dictates -> it acts more often. There is no special
animation. So the test is: measure each band unit's CT climb rate, normalized by its Speed.

  normalized rate = (CT gained per second) / Speed

For EVERY non-hasted unit this equals the same constant k (the clock's tick rate). A unit whose
Haste bit is FUNCTIONAL reads ~1.5k. Normalizing by Speed cancels per-unit speed differences (and the
DEV build's +3 seed), so a hasted row's `ratio` column reads ~1.50x vs ~1.00x for the rest.

CAVEAT (the reason this probe watches BOTH CT bytes): per Offsets.cs, the two CT candidates read
DIFFERENTLY by side --
  ct25 = band +0x25 (ACtSlam): clean climb for ENEMY units; stale/frozen on the PLAYER's own unit.
  ct09 = band +0x09 (ACtTurn): the proven turn-counter -- clean for ENEMY turns; flat 0 for players.
So a player's own CT is effectively unreadable; the RELIABLE functional test is on an ENEMY. Give an
enemy Haste (tools/probes/give_enemy_buffs.py) + leave a control enemy, and compare their enemy-clean
bytes. The mechanism (bit -> faster CT) is unit-agnostic: if the bit speeds an enemy's CT, it speeds
your wielder's too.

The active-turn clock only TICKS while units are charging with NO menu/animation open (briefly between
turns, and during the enemy phase). Let it RUN -- if every ct value is frozen, you're parked in a menu.

USAGE (game running, in a live battle, clock free-running):
  python haste_ct_probe.py watch [seconds=30] [hz=8] [markBrave,markFaith]
        # live per-unit ct25/ct09/speed/haste dump + a normalized-rate summary. Watch whether the
        # numbers actually CHANGE. e.g.  python haste_ct_probe.py watch 30 8 89,76

Read the summary: on the byte that MOVES for a unit, hasted ~1.50x => Haste WORKS; ~1.00x => cosmetic.
"""
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ct_probe import PROC, PV, find_pid, k32, rd  # noqa: E402  (shared RPM scaffolding)

ANCHOR, STRIDE, ENTRY = 0x14184F890, 0x200, 0x1C
BASE = ANCHOR + ENTRY - 24 * STRIDE   # n=-24 band anchor (== status_probe.py BASE)
SLOTS = 49

O_LVL, O_BRAVE, O_FAITH, O_HP, O_MAXHP = 0x0D, 0x0E, 0x10, 0x14, 0x16
O_CT09, O_SPEED, O_CT25, O_GX, O_GY = 0x09, 0x24, 0x25, 0x33, 0x34
O_HASTE, HASTE_BIT = 0x48, 0x08


def units(h):
    """Yield (key, dict) for every live band unit. key = brave/faith/maxhp fingerprint."""
    for s in range(SLOTS):
        b = rd(h, BASE + s * STRIDE, 0x60)
        if not b:
            continue
        mhp = b[O_MAXHP] | (b[O_MAXHP + 1] << 8)
        lvl, br, fa = b[O_LVL], b[O_BRAVE], b[O_FAITH]
        if not (1 <= mhp < 2000 and 1 <= lvl <= 99 and 1 <= br <= 100 and 1 <= fa <= 100):
            continue
        spd = b[O_SPEED]
        if not (1 <= spd <= 99):          # a sane Speed is required for normalization
            continue
        gx, gy = b[O_GX], b[O_GY]
        yield (f"{br}/{fa}/{mhp}", {
            "br": br, "fa": fa, "lvl": lvl, "spd": spd,
            "hp": b[O_HP] | (b[O_HP + 1] << 8), "mhp": mhp,
            "ct25": b[O_CT25], "ct09": b[O_CT09], "haste": bool(b[O_HASTE] & HASTE_BIT),
            "enemy": (gx == 0 and gy == 0),
        })


def main():
    args = sys.argv[2:] if len(sys.argv) > 1 and sys.argv[1] == "watch" else sys.argv[1:]
    seconds = float(args[0]) if len(args) > 0 else 30
    hz = float(args[1]) if len(args) > 1 else 8
    mark = None
    if len(args) > 2 and "," in args[2]:
        mb, mf = args[2].split(",")
        mark = (int(mb), int(mf))

    pid = find_pid(PROC)
    if not pid:
        sys.exit(f"{PROC} not running")
    h = k32.OpenProcess(PV, False, pid)
    if not h:
        sys.exit("OpenProcess failed")

    # The clock ticks in chunky ~1/sec bursts (mostly frozen while menus/animations are up), so a
    # per-second RATE is unreliable. The robust signal is the SINGLE-TICK CT STEP: CT jumps atomically
    # by its per-tick increment (== Speed for a normal unit, round(Speed*1.5) when Haste is FUNCTIONAL).
    # We collect every positive ct25 delta per unit; the smallest one is the single-tick step.
    steps = {}   # key -> {"u":meta, "deltas":{delta:count}}
    prev = {}    # key -> ct25
    pr_print = {}  # key -> ct25 at last printed line (for an honest RUNNING/IDLE label)
    dt = 1.0 / hz
    end = int(seconds * hz)
    print(f"watching CT for {seconds:.0f}s @ {hz:.0f}Hz. Let the active-turn clock RUN (no menu).")
    print("Per-tick ct25 STEP / Speed: ~1.0 normal, ~1.5 = Haste FUNCTIONAL. (ct09 is flat 0 for players.)\n")
    try:
        for t in range(end):
            snap = dict(units(h))
            for key, u in snap.items():
                d = u["ct25"] - prev.get(key, u["ct25"])
                if d > 0:                          # ignore wraps (act resets CT downward)
                    s = steps.setdefault(key, {"u": u, "deltas": {}})
                    s["deltas"][d] = s["deltas"].get(d, 0) + 1
                    s["u"] = u
                prev[key] = u["ct25"]

            if t % int(hz) == 0:                   # ~1 block/sec: raw values so motion is visible
                moved = any(u["ct25"] != pr_print.get(key) for key, u in snap.items())
                print(f"t={t / hz:5.1f}s  [{'RUNNING' if moved else 'idle'}]")
                for key, u in sorted(snap.items(), key=lambda kv: (kv[1]['enemy'], -kv[1]['spd'])):
                    tag = "H" if u["haste"] else " "
                    m = " <==" if mark and (u["br"], u["fa"]) == mark else ""
                    side = "e" if u["enemy"] else "P"
                    print(f"   {side} {u['br']:>2}/{u['fa']:<2} spd{u['spd']:>2} "
                          f"ct25={u['ct25']:>3} ct09={u['ct09']:>3} {tag}{m}")
                pr_print = {key: u["ct25"] for key, u in snap.items()}
            time.sleep(dt)
    except KeyboardInterrupt:
        print("\n(interrupted -- summarizing what we have)")
    finally:
        k32.CloseHandle(h)
    summarize(steps, mark)


def summarize(steps, mark):
    rows = [(s["u"], min(s["deltas"]), s["deltas"]) for s in steps.values() if s["deltas"]]
    if not rows:
        print("\nNo CT motion captured -- the clock never ticked (parked in a menu the whole run?).")
        return
    print("\n=== RESULT: single-tick CT step per unit (step / Speed) ===")
    print(f"{'unit':10} {'spd':>3} {'step':>4} {'ratio':>6}  flags          (deltas seen)")
    for u, step, hist in sorted(rows, key=lambda r: -(r[1] / r[0]["spd"])):
        ratio = step / u["spd"]
        tags = ["HASTE" if u["haste"] else "     ", "enemy" if u["enemy"] else "player"]
        if u["haste"]:
            tags.append("FUNCTIONAL" if ratio >= 1.3 else "COSMETIC?")
        if mark and (u["br"], u["fa"]) == mark:
            tags.append("<== marked")
        seen = " ".join(f"{d}x{c}" for d, c in sorted(hist.items())[:6])
        print(f"{u['br']}/{u['fa']:<7} {u['spd']:>3} {step:>4} {ratio:>5.2f}x  "
              f"{' '.join(tags):20}  {seen}")
    print("\nstep = smallest positive per-tick CT jump (= effective Speed). Hasted row ~1.5x => Haste "
          "WORKS; ~1.0x => cosmetic, drop it like Float. Watch the row flagged H, not a brave/faith guess.")


if __name__ == "__main__":
    main()
