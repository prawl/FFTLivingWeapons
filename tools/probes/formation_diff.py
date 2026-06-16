#!/usr/bin/env python
"""
FORMATION DIFF probe -- photograph the engine BUILDING a unit, instead of guessing the struct.

PREMISE (Patrick's idea, 2026-06-16): rather than reason about what a spawned unit's struct
SHOULD look like, watch the engine instantiate one for real. Snapshot the candidate roster
regions before and after a unit appears -- on the pre-battle UNIT PLACEMENT screen, or across
the battle-start boundary -- and diff. The bytes that flip from "empty slot" to "live unit"
ARE the from-scratch spawn recipe, and the set of jobs the placement screen lets you deploy
tells us which SPRITES are resident (the suspected hard wall on a mid-battle spawn).

This is step 1 of the spawn-an-enemy probe ladder (see the spawn feasibility verdict):
  1. (THIS) formation/placement diff -> the unit-instantiation recipe + resident-sprite truth.
  2. reinforcement-reveal diff (feign_probe-style) on a map with a scripted mid-battle join
     -> proves whether "make present" is a holdable flag or an engine function call.

READ-ONLY. This probe NEVER writes game memory (no wpm import) -- it only snapshots + diffs,
so it is safe by construction.

It is deliberately STRUCTURE-AGNOSTIC and does NOT gate on the in-battle sentinels, because we
do not yet know WHICH structure the placement screen populates, nor what battleMode/slot9 read
there. It snapshots THREE candidate regions and reports the sentinels so we learn both at once
(addresses + framing verified vs LivingWeapon/Offsets.cs and feign_probe.py):

  band   0x14184C8AC  49 x 0x200   the LIVE in-battle band (entry-relative offsets)
  array  0x140891600  30 x 0x200   the static unit array (same entry framing)
  roster 0x1411A18D0  20 x 0x258   the world-map party roster (its own framing)

USAGE (game running):
  python formation_diff.py regions
      # read-only: dump sentinels (battleMode/slot9/slot0) + per-region occupied-slot summary
      # RIGHT NOW. Run it on the placement screen first to see what's already populated.

  python formation_diff.py snap <label>
      # save all three regions to OS temp as fft_formation_<label>.pkl, with a state summary.

  python formation_diff.py diff <before> [after]
      # diff a saved <before> snapshot against <after> (a saved label) OR against LIVE memory
      # (if <after> omitted). Per slot: SPAWN (empty->occupied), VANISH, or CHANGED, with every
      # changed byte annotated by known field. SPAWN rows are the instantiation recipe.

RECOMMENDED CAPTURE SEQUENCE for the spawn question:
  - On the unit-placement screen, BEFORE placing the last unit:  snap place_before
  - Place the unit, then:                                         diff place_before
  - Just before "Begin Battle":                                   snap prebattle
  - On turn 1 of the live battle:                                 snap inbattle ; diff prebattle inbattle
    (the prebattle->inbattle diff is the battle-load instantiation -- the sprite-load era.)
"""
import pathlib
import pickle
import sys
import tempfile

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
from treasure_flags import rpm, _require_game  # rpm(addr,n)->bytes|None ; _require_game() attaches

# --- battle-state sentinels (read + reported, never gated on -- we want to LEARN them here) ---
BATTLE_MODE, SLOT9, SLOT0 = 0x140900650, 0x14077CA54, 0x14077CA30

# entry-relative offsets shared by the band AND the static array (verified vs Offsets.cs / feign_probe).
# (offset, size, name). Multi-byte fields decode little-endian.
ENTRY_FIELDS = [
    (0x04, 2, "weapon"),  # entry+0x04 == combat-slot 0x20 (CWeapon); confirmed live 2026-06-16
    (0x09, 1, "CT(read)"), (0x0D, 1, "level"), (0x0E, 1, "brave"), (0x10, 1, "faith"),
    (0x12, 2, "inBattle"), (0x14, 2, "HP"), (0x16, 2, "MaxHP"), (0x18, 2, "MP"), (0x1A, 2, "MaxMP"),
    (0x22, 1, "PA"), (0x23, 1, "MA"), (0x24, 1, "Speed"),  # == combat 0x3e/0x3f/0x40; confirmed 2026-06-16
    (0x25, 1, "CT(slam)"),  # == combat 0x41 (ACtSlam); battle-start seeds it
    (0x33, 1, "gx"), (0x34, 1, "gy"), (0x38, 1, "team/alleg"),
    (0x45, 1, "dead/undead"), (0x47, 1, "reraise/invis/float"),
    (0x48, 1, "poison/regen/protect/shell/haste"), (0x49, 1, "doom/charm/reflect"),
    (0x4A, 1, "poisonTimer"),
]
# roster (0x1411A18D0) has its OWN framing -- see Offsets.cs RosterBase block.
ROSTER_FIELDS = [
    (0x0A, 1, "support"), (0x12, 2, "accessory"), (0x14, 2, "RHand"), (0x16, 2, "LHand"),
    (0x18, 2, "offHand"), (0x1A, 2, "shield"), (0x1D, 1, "level"), (0x1E, 1, "brave"),
    (0x1F, 1, "faith"), (0x230, 2, "nameId"),
]

REGIONS = [
    {"name": "band",   "base": 0x14184C8AC, "stride": 0x200, "count": 49, "lvl": 0x0D, "fields": ENTRY_FIELDS},
    {"name": "array",  "base": 0x140891600, "stride": 0x200, "count": 30, "lvl": 0x0D, "fields": ENTRY_FIELDS},
    {"name": "roster", "base": 0x1411A18D0, "stride": 0x258, "count": 20, "lvl": 0x1D, "fields": ROSTER_FIELDS},
]


def _u(addr, n):
    b = rpm(addr, n)
    return int.from_bytes(b, "little") if b else None


def _f(blob, off, size):
    return int.from_bytes(blob[off:off + size], "little")


def _occupied(reg, blob):
    """A slot holds a REAL unit, not stale/overlapping garbage. Mirrors feign_probe._read_unit:
    level + brave + faith (+ MaxHP + grid pos for the entry regions) must all read in-range.
    A bare level-byte check alone flags garbage slots whose +0x0D happens to land in 1..99."""
    if blob is None:
        return False
    if reg["name"] == "roster":
        lvl, br, fa = blob[0x1D], blob[0x1E], blob[0x1F]
        return 1 <= lvl <= 99 and 1 <= br <= 100 and 1 <= fa <= 100
    lvl, br, fa = blob[0x0D], blob[0x0E], blob[0x10]
    mhp, gx, gy = _f(blob, 0x16, 2), blob[0x33], blob[0x34]
    return (1 <= lvl <= 99 and 1 <= br <= 100 and 1 <= fa <= 100
            and 1 <= mhp < 2000 and gx <= 30 and gy <= 30)


def _sentinels():
    return {"battleMode": _u(BATTLE_MODE, 4), "slot9": _u(SLOT9, 4), "slot0": _u(SLOT0, 4)}


def _read_live():
    regions = {}
    for reg in REGIONS:
        regions[reg["name"]] = [rpm(reg["base"] + s * reg["stride"], reg["stride"])
                                for s in range(reg["count"])]
    return {"meta": _sentinels(), "regions": regions}


def _summ(reg, blob):
    """One-line picture of an occupied slot (region-specific)."""
    if reg["name"] == "roster":
        return (f"lvl={blob[0x1D]:>2} br={blob[0x1E]:>3} fa={blob[0x1F]:>3} "
                f"RHand={_f(blob, 0x14, 2):>4} nameId={_f(blob, 0x230, 2):>4}")
    return (f"lvl={blob[0x0D]:>2} br={blob[0x0E]:>3} fa={blob[0x10]:>3} "
            f"hp={_f(blob, 0x14, 2):>4}/{_f(blob, 0x16, 2):<4} "
            f"({blob[0x33]:>2},{blob[0x34]:>2}) team={blob[0x38]:#04x}")


def _path(label):
    return pathlib.Path(tempfile.gettempdir()) / f"fft_formation_{label}.pkl"


def _sentinel_str(meta):
    def h(v):
        return "??" if v is None else f"{v:#x}"
    return f"battleMode={h(meta['battleMode'])} slot9={h(meta['slot9'])} slot0={h(meta['slot0'])}"


# ---------------------------------------------------------------------------
# verbs
# ---------------------------------------------------------------------------
def cmd_regions():
    """Read-only: what's populated RIGHT NOW, across all three regions + the sentinels."""
    _require_game()
    live = _read_live()
    print("=== live regions @ now ===")
    print(f"  sentinels: {_sentinel_str(live['meta'])}\n")
    for reg in REGIONS:
        slots = live["regions"][reg["name"]]
        occ = [(s, b) for s, b in enumerate(slots) if _occupied(reg, b)]
        unreadable = sum(1 for b in slots if b is None)
        print(f"[{reg['name']}] base={reg['base']:#x} stride={reg['stride']:#x} "
              f"count={reg['count']}  occupied={len(occ)}  unreadable={unreadable}")
        for s, b in occ:
            print(f"    slot {s:>2}  {_summ(reg, b)}")
        print()


def cmd_snap(label):
    """Save all three regions + sentinels to OS temp."""
    _require_game()
    live = _read_live()
    p = _path(label)
    p.write_bytes(pickle.dumps(live))
    print(f"snapshot '{label}' saved: {p}")
    print(f"  sentinels: {_sentinel_str(live['meta'])}")
    for reg in REGIONS:
        slots = live["regions"][reg["name"]]
        occ = sum(1 for b in slots if _occupied(reg, b))
        unreadable = sum(1 for b in slots if b is None)
        print(f"  {reg['name']:<7} occupied={occ:<3} unreadable={unreadable}")
    print("\nNow change state (place a unit / start the battle), then:")
    print(f"  python formation_diff.py diff {label}")


def _annot_changes(reg, b, a, changed):
    """Print changed bytes: known fields decoded before->after, unknown bytes listed (capped)."""
    touched, unknown = {}, []
    for off in changed:
        hit = next(((fo, sz, nm) for fo, sz, nm in reg["fields"] if fo <= off < fo + sz), None)
        if hit:
            touched[hit] = None
        else:
            unknown.append(off)
    for fo, sz, nm in sorted(touched):
        print(f"      +{fo:#05x} {nm:<32} {_f(b, fo, sz):>5} -> {_f(a, fo, sz):>5}")
    if unknown:
        shown = ", ".join(f"+{o:#x}:{b[o]:#04x}->{a[o]:#04x}" for o in unknown[:32])
        more = f"  (+{len(unknown) - 32} more)" if len(unknown) > 32 else ""
        print(f"      unknown bytes ({len(unknown)}): {shown}{more}")


def _diff_slot(reg, b, a):
    if b is None and a is None:
        return None
    if b is None:
        return ("APPEARED", b, a, None)
    if a is None:
        return ("UNMAPPED", b, a, None)
    changed = [i for i in range(min(len(b), len(a))) if b[i] != a[i]]
    if not changed:
        return None
    bo, ao = _occupied(reg, b), _occupied(reg, a)
    kind = "SPAWN" if (not bo and ao) else "VANISH" if (bo and not ao) else "CHANGED"
    return (kind, b, a, changed)


def cmd_diff(before_label, after_label):
    """Diff a saved <before> against <after> (label) or LIVE memory (omit after)."""
    _require_game()
    bp = _path(before_label)
    if not bp.exists():
        print(f"no snapshot '{before_label}' at {bp}. Run: snap {before_label}")
        sys.exit(1)
    before = pickle.loads(bp.read_bytes())
    if after_label:
        ap = _path(after_label)
        if not ap.exists():
            print(f"no snapshot '{after_label}' at {ap}. Run: snap {after_label}")
            sys.exit(1)
        after, asrc = pickle.loads(ap.read_bytes()), f"snapshot '{after_label}'"
    else:
        after, asrc = _read_live(), "LIVE memory"

    print(f"=== formation diff: '{before_label}' -> {asrc} ===")
    print(f"  sentinels before: {_sentinel_str(before['meta'])}")
    print(f"  sentinels after : {_sentinel_str(after['meta'])}\n")
    for reg in REGIONS:
        bslots = before["regions"].get(reg["name"], [])
        aslots = after["regions"].get(reg["name"], [])
        hits = []
        for s in range(max(len(bslots), len(aslots))):
            b = bslots[s] if s < len(bslots) else None
            a = aslots[s] if s < len(aslots) else None
            d = _diff_slot(reg, b, a)
            if d:
                hits.append((s, d))
        if not hits:
            print(f"[{reg['name']}] no slot changed.")
            continue
        print(f"[{reg['name']}] {len(hits)} slot(s) changed:")
        for s, (kind, b, a, changed) in hits:
            if kind == "APPEARED":
                tag = _summ(reg, a) if _occupied(reg, a) else "(empty/garbage)"
                print(f"  slot {s:>2}  APPEARED in memory  {tag}")
                continue
            if kind == "UNMAPPED":
                print(f"  slot {s:>2}  UNMAPPED after (region shrank/unreadable)")
                continue
            star = "  <<< SPAWN: empty -> live unit (the instantiation recipe)" if kind == "SPAWN" else ""
            print(f"  slot {s:>2}  {kind}{star}")
            if kind in ("SPAWN", "VANISH"):
                print(f"      before: {_summ(reg, b) if _occupied(reg, b) else '(empty)'}")
                print(f"      after : {_summ(reg, a) if _occupied(reg, a) else '(empty)'}")
            _annot_changes(reg, b, a, changed)
        print()


def main():
    mode = sys.argv[1] if len(sys.argv) > 1 else ""
    if mode == "regions":
        cmd_regions()
    elif mode == "snap" and len(sys.argv) >= 3:
        cmd_snap(sys.argv[2])
    elif mode == "diff" and len(sys.argv) >= 3:
        cmd_diff(sys.argv[2], sys.argv[3] if len(sys.argv) >= 4 else None)
    else:
        print(__doc__)


if __name__ == "__main__":
    main()
