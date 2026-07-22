"""THE BATTLE TOOLBAG (LW-117): one verb per proven mechanic, so a design conversation can say
"what if the weapon benched them for a turn" and we can just DO it in ten seconds.

Nothing here is new reverse engineering. Every write is a mechanism the LIVE_LEDGER already
carries in its Proven section, wrapped in a guard and a plain-English name. Constants were
re-read out of the tree (not recalled) on 2026-07-22: CT combat +0x41 (Offsets.ACtSlam 0x25
band-relative, "matches combat base+0x41"), gate combat +0x01 (0xFF hides, the MODEL ID
reveals), present combat +0x1B5, node world Z +0x4E with Z = -12 * height, node reached via
list head 0x140D3A410 with +0x148 the combat backref (swap_units.py, proven live).

    python tools\\probes\\battle_toolbag.py state              # every unit, every field we touch
    python tools\\probes\\battle_toolbag.py quick <slot> [ct]  # slam CT: act NOW (default 100)
    python tools\\probes\\battle_toolbag.py bench <slot> <s>   # hold CT 0: deny turns for s seconds
    python tools\\probes\\battle_toolbag.py hide <slot>        # gate FF: untargetable, no turns
    python tools\\probes\\battle_toolbag.py show <slot>        # restore the saved model id
    python tools\\probes\\battle_toolbag.py float <slot> <n>   # hover n height units (0 = ground)
    python tools\\probes\\battle_toolbag.py reserve <slot>     # hide + sink below the floor
    python tools\\probes\\battle_toolbag.py deploy <slot>      # the reverse, with a sky descent

HAZARDS, all ledger-documented, all stated because they bite:
  - A HIDDEN unit gets no scheduler turns, so it cannot un-hide itself: `show` is the only way
    back. Never hide the last unit you control.
  - A mid-hide AUTOSAVE persists the hidden state into the resume (proven live). Do not save
    while anything is hidden or reserved.
  - `show`/`deploy` need the model id saved by `hide`/`reserve`; it is kept in a state file
    under %TEMP% (per-process invocations cannot share memory). If that file is lost, the model
    id is recoverable from any same-sprite unit, or by ending the battle.
  - `show` REFUSES to reveal onto an occupied tile: co-tiling causes target shadowing plus a
    movement soft-lock (both proven live), and the same ledger row records the engine displacing
    a hidden unit by a tile on its own, so a parked unit's tile is not stable while it waits.
    Move the intruder (swap_units.py) and re-run.
  - The state file is stamped with the game's PID and `show` refuses a slot whose gate is not
    0xFF, because slot numbers are not identity: slot 5 is a different unit every battle, and
    %TEMP% outlives reboots. Both guards exist so a stale record cannot stamp a stranger.
  - Never `hide` or `bench` the CURRENT ACTOR; both refuse a unit whose turn flag is open, and
    the flag check FAILS CLOSED (an unreadable flag refuses rather than writing blind).
"""
import json
import os
import pathlib
import struct
import sys
import time

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import ctypes
from battle_cheats import rpm, ru8, ru16, wu8, wu16, _handle, _require_game

UNITS = 0x141853CE0
HEAD = 0x140D3A410
CT = 0x41          # combat CT byte (band +0x25); write side proven by the Zwill extra-turn slam
GATE = 0x01        # 0xFF = hidden; otherwise the unit's MODEL ID (restore value)
PRESENT = 0x1B5    # 1 = on the field; 0x80 = removed
HP = 0x30          # u16 current HP, for the state dump's sanity column
TURNFLAG = 0x19C   # band-relative per-unit turn flag; combat-relative via the band offset below
BAND_FROM_COMBAT = 0x1C   # band entry sits at combat base + 0x1C (Offsets/probe convention)
NODE_Z = 0x4E      # u16 signed world Z; Z = -12 * height (+1 height unit renders as FLOAT hover)
STATE = pathlib.Path(os.environ.get("TEMP", ".")) / "lw_toolbag_state.json"


def u64(a):
    b = rpm(a, 8)
    return None if b is None else struct.unpack("<Q", b)[0]


def combat_of(slot):
    return UNITS + slot * 0x200


def node_of(slot):
    cur = u64(HEAD)
    for _ in range(64):
        if not cur:
            return None
        if u64(cur + 0x148) == combat_of(slot):
            return cur
        cur = u64(cur)
    return None


def game_pid():
    """Session identity for the state file. Slot numbers alone are NOT identity: slot 5 is a
    different unit in every battle, and %TEMP% survives reboots, so an entry banked in one
    launch could otherwise be stamped into a stranger."""
    return int(ctypes.windll.kernel32.GetProcessId(_handle()))


def load_state():
    """Entries from another game launch are dropped, not trusted."""
    try:
        d = json.loads(STATE.read_text(encoding="utf-8"))
    except Exception:
        return {}
    if d.get("pid") != game_pid():
        return {}
    return d.get("slots", {})


def save_state(slots):
    STATE.write_text(json.dumps({"pid": game_pid(), "slots": slots}), encoding="utf-8")


def acting(slot):
    """True if this unit's turn is structurally open (the LW-63 per-unit turn flag). FAILS
    CLOSED: an unreadable flag reports acting, so a refusal beats a blind write."""
    v = ru8(combat_of(slot) + BAND_FROM_COMBAT + TURNFLAG)
    return v is None or v == 1


def require_fielded(slot):
    """peek-before-poke: every write verb refuses a slot that is not a live fielded unit."""
    live = dict(live_slots())
    if slot not in live:
        print(f"slot {slot} is not a fielded unit right now; run `state` to see what is.")
        sys.exit(1)
    return live[slot]


def occupant_of(tile, ignore_slot):
    for slot, _node in live_slots():
        if slot == ignore_slot:
            continue
        c = combat_of(slot)
        if (ru8(c + 0x4F), ru8(c + 0x50)) == tile:
            return slot
    return None


def live_slots():
    out = []
    cur = u64(HEAD)
    for _ in range(64):
        if not cur:
            break
        c = u64(cur + 0x148)
        off = (c or 0) - UNITS
        if c and 0 <= off < 21 * 0x200 and off % 0x200 == 0:
            out.append((off // 0x200, cur))
        cur = u64(cur)
    return out


def cmd_state():
    st = load_state()
    print(f"{'slot':>4} {'tile':>8} {'hp':>5} {'ct':>4} {'gate':>5} {'pres':>5} {'worldZ':>7} {'turn':>5}  saved")
    for slot, node in live_slots():
        c = combat_of(slot)
        z = ru16(node + NODE_Z)
        z = z - 65536 if z > 32767 else z
        gate = ru8(c + GATE)
        print(f"{slot:>4} ({ru8(c + 0x4F):>2},{ru8(c + 0x50):>2}) {ru16(c + HP):>5} {ru8(c + CT):>4} "
              f"{gate:>#5x} {ru8(c + PRESENT):>5} {z:>7} {'ACT' if acting(slot) else '':>5}  "
              f"{st.get(str(slot), {}).get('model', '')}")
    if st:
        print(f"\nstate file {STATE}: {st}")


def cmd_quick(slot, ct):
    if not 0 <= ct <= 255:
        print("CT must be 0..255 (100 is a full charge); refusing rather than truncating.")
        sys.exit(1)
    require_fielded(slot)
    c = combat_of(slot)
    before = ru8(c + CT)
    wu8(c + CT, ct)
    print(f"slot {slot}: CT {before} -> {ru8(c + CT)}. EYEBALL: do they jump the AT queue and act next?")


def cmd_bench(slot, secs):
    require_fielded(slot)
    if acting(slot):
        print("that unit's turn is OPEN right now; benching the current actor is refused.")
        sys.exit(1)
    c = combat_of(slot)
    before = ru8(c + CT)
    print(f"slot {slot}: CT {before}, holding at 0 for {secs:.0f}s (Ctrl+C releases)")
    end = time.time() + secs
    try:
        while time.time() < end:
            wu8(c + CT, 0)
            time.sleep(0.1)
    except KeyboardInterrupt:
        print("released early.")
    print("released; CT re-accrues from 0 (the accrued charge is authentically lost).")
    print("EYEBALL: did their turn never come while others acted? That is the AT-list read.")


def cmd_hide(slot):
    require_fielded(slot)
    if acting(slot):
        print("that unit's turn is OPEN right now; hiding the current actor is refused.")
        sys.exit(1)
    c = combat_of(slot)
    gate = ru8(c + GATE)
    if gate == 0xFF:
        print("already hidden (gate reads 0xFF); refusing so the real model id is not lost.")
        sys.exit(1)
    st = load_state()
    st[str(slot)] = {"model": gate}
    save_state(st)
    wu8(c + GATE, 0xFF)
    print(f"slot {slot} hidden; model id {gate:#04x} saved to {STATE}.")
    print("REMEMBER: hidden units get no turns and cannot un-hide themselves. Do not autosave.")


def cmd_show(slot):
    st = load_state()
    rec = st.get(str(slot))
    if not rec:
        print(f"no saved model id for slot {slot} in this game launch; check {STATE}, or end the "
              f"battle (the combat struct is rebuilt) to reset.")
        sys.exit(1)
    node = require_fielded(slot)
    c = combat_of(slot)
    if ru8(c + GATE) != 0xFF:
        print(f"slot {slot} is not hidden (gate reads {ru8(c + GATE):#04x}, not 0xFF). Refusing: "
              f"this record is stale and writing it would stamp a stranger's model id.")
        sys.exit(1)
    # The PROVEN hide/reveal row names this hazard first: restoring onto an OCCUPIED tile
    # co-tiles into target shadowing plus the movement soft-lock, and the same row records the
    # engine displacing a hidden unit by a tile on its own, so a parked unit's tile is not even
    # stable while it waits.
    tile = (ru8(c + 0x4F), ru8(c + 0x50))
    other = occupant_of(tile, slot)
    if other is not None:
        print(f"tile {tile} is now occupied by slot {other}; revealing here would co-tile them "
              f"(target shadowing + movement soft-lock, both proven live). Move that unit first "
              f"(swap_units.py) and re-run.")
        sys.exit(1)
    if "z" in rec:                          # a reserve, not a plain hide: un-sink before showing
        wu16(node + NODE_Z, rec["z"] & 0xFFFF)
    wu8(c + PRESENT, 1)
    wu8(c + GATE, rec["model"] & 0xFF)      # gate LAST, per the resurrect recipe's ordering
    st.pop(str(slot), None)
    save_state(st)
    print(f"slot {slot} revealed at {tile} (model {rec['model']:#04x} restored, present then gate).")


def cmd_float(slot, heights):
    node = node_of(slot)
    if not node:
        print("unit not noded; aborting.")
        sys.exit(1)
    z = ru16(node + NODE_Z)
    z = z - 65536 if z > 32767 else z
    wu16(node + NODE_Z, (z - 12 * heights) & 0xFFFF)
    back = ru16(node + NODE_Z)
    print(f"slot {slot}: world Z {z} -> {back - 65536 if back > 32767 else back} ({heights} height unit(s) up). "
          f"Pure render data; the engine re-stamps at their next move or turn-open.")


def cmd_reserve(slot):
    """Park-and-summon: hide the logic AND sink the render below the floor, so nothing shows
    even if some later pass re-opens the gate. The despawn arc's cheaper cousin: no engine
    sweeper involved, so no re-enrollment is needed to bring them back."""
    node = node_of(slot)
    if not node:
        print("unit not noded; aborting.")
        sys.exit(1)
    cmd_hide(slot)
    z = ru16(node + NODE_Z)
    z = z - 65536 if z > 32767 else z
    st = load_state()
    st[str(slot)]["z"] = z
    save_state(st)
    wu16(node + NODE_Z, (z + 600) & 0xFFFF)     # +Z is downward: sink well under the map
    print(f"slot {slot} reserved (hidden and sunk from Z {z}).")


def cmd_deploy(slot):
    """The reverse, with the sky-descent flourish the resurrect arc used: appear high, settle."""
    node = node_of(slot)
    st = load_state()
    rec = st.get(str(slot))
    if not node or not rec or "z" not in rec:
        print(f"slot {slot} was not reserved by this toolbag; use show instead.")
        sys.exit(1)
    z = rec["z"]
    # Take the Z out of the record first: cmd_show restores a reserve's Z on the owner's behalf,
    # which would fight the descent below.
    rec.pop("z", None)
    st[str(slot)] = rec
    save_state(st)
    wu16(node + NODE_Z, (z - 600) & 0xFFFF)     # start above the map
    cmd_show(slot)                              # occupancy-checked; exits if the tile is taken
    for step in range(10, -1, -1):              # descend over ~0.6s
        wu16(node + NODE_Z, (z - 60 * step) & 0xFFFF)
        time.sleep(0.05)
    wu16(node + NODE_Z, z & 0xFFFF)
    print(f"slot {slot} deployed from the sky to Z {z}.")


def main():
    _require_game()
    a = sys.argv[1:]
    if not a:
        print(__doc__); return
    cmd = a[0]
    if cmd == "state":
        cmd_state()
    elif cmd == "quick" and len(a) >= 2:
        cmd_quick(int(a[1]), int(a[2]) if len(a) > 2 else 100)
    elif cmd == "bench" and len(a) >= 3:
        cmd_bench(int(a[1]), float(a[2]))
    elif cmd == "hide" and len(a) >= 2:
        cmd_hide(int(a[1]))
    elif cmd == "show" and len(a) >= 2:
        cmd_show(int(a[1]))
    elif cmd == "float" and len(a) >= 3:
        cmd_float(int(a[1]), int(a[2]))
    elif cmd == "reserve" and len(a) >= 2:
        cmd_reserve(int(a[1]))
    elif cmd == "deploy" and len(a) >= 2:
        cmd_deploy(int(a[1]))
    else:
        print(__doc__)


if __name__ == "__main__":
    main()
