"""THE BATTLE TOOLBAG (LW-117): one verb per proven mechanic, so a design conversation can say
"what if the weapon benched them for a turn" and we can just DO it in ten seconds.

Nothing here is new reverse engineering. Every write is a mechanism the LIVE_LEDGER already
carries in its Proven section, wrapped in a guard and a plain-English name. Constants were
re-read out of the tree (not recalled) on 2026-07-22: CT combat +0x41 (Offsets.ACtSlam 0x25
band-relative, "matches combat base+0x41"), gate combat +0x01 (0xFF hides, the MODEL ID
reveals), present combat +0x1B5, node world Z +0x4E with Z = -12 * height, node reached via
list head 0x140D3A410 with +0x148 the combat backref (swap_units.py, proven live).

    python tools\\probes\\battle_toolbag.py state                    # every unit, every field
    python tools\\probes\\battle_toolbag.py quick <slot> [ct]        # slam CT: act NOW (default 100)
    python tools\\probes\\battle_toolbag.py bench <slot> <secs>      # hold CT 0: deny turns
    python tools\\probes\\battle_toolbag.py hide <slots> [--vanish]  # gate FF; --vanish hides the SPRITE too
    python tools\\probes\\battle_toolbag.py show <slots>             # restore gate (and pose, and Z)
    python tools\\probes\\battle_toolbag.py float <slot> <n>         # hover n height units
    python tools\\probes\\battle_toolbag.py reserve <slot>           # hide + remember ground Z
    python tools\\probes\\battle_toolbag.py deploy <slot>            # reveal with a sky descent
    python tools\\probes\\battle_toolbag.py warp <slot> <x> <y> [--force]      # arbitrary tile
    python tools\\probes\\battle_toolbag.py status <slot> <name> on|off [--all]
    python tools\\probes\\battle_toolbag.py rsm <slot> [movement|support|reaction]
    python tools\\probes\\battle_toolbag.py sweep <slot> [secs]   # catalog all 23 untested statuses

`hide`/`show` take a comma list (17 or 6,16,17). `--all` on status reaches the innate layer.
`rsm <slot> <field>` clears a whole RSM field and prints its own restore command.

HAZARDS, all ledger-documented, all stated because they bite:
  - A HIDDEN unit gets no scheduler turns, so it cannot un-hide itself: `show` is the only way
    back. Never hide the last unit you control.
  - HIDING EVERY ENEMY WINS THE BATTLE (owner live 2026-07-22). The engine's living-enemy count
    walks the same logic list the gate byte removes a unit from, so taking the last one ends the
    fight in a victory. Fine in a throwaway, ruinous mid-playthrough, and a guard any shipped
    Vanish effect will need.
  - `hide` alone removes the unit from LOGIC only; the render weld leaves its SPRITE STANDING
    (the ledger's ghost-statue toggle). Add `--vanish` to also request an invisibility page and
    get a true disappearing act. The pairing is self-holding: an animation page is normally
    re-stamped at the unit's next event, and a hidden unit has no events. `show` restores both.
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
import status_map

UNITS = 0x141853CE0
HEAD = 0x140D3A410
CT = 0x41          # combat CT byte (band +0x25); write side proven by the Zwill extra-turn slam
GATE = 0x01        # 0xFF = hidden; otherwise the unit's MODEL ID (restore value)
PRESENT = 0x1B5    # 1 = on the field; 0x80 = removed
HP = 0x30          # u16 current HP, for the state dump's sanity column
TURNFLAG = 0x19C   # band-relative per-unit turn flag; combat-relative via the band offset below
BAND_FROM_COMBAT = 0x1C   # band entry sits at combat base + 0x1C (Offsets/probe convention)
NODE_Z = 0x4E      # u16 signed world Z; Z = -12 * height (+1 height unit renders as FLOAT hover)
ANIM_REQ = 0x10    # animation request register (Proven 2026-07-21): write u16 page + 1
VANISH_PAGE = 0x32 # owner catalog: 'unit turned completely invisible' (0x3b is its sibling)
IDLE_PAGE = 0x03   # 'started walking in place again', the resting page
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


BAND = 0x1C   # band entry = combat base + 0x1C; every status offset is band-relative
# RSM (Reaction / Support / Movement) ability fields, combat-relative, read out of Offsets.cs
# 2026-07-22: CReaction 0x94 (4 bytes, Maim zeroes it to suppress Counter), CSupport 0x98
# (4 bytes, base id 198), CMovement 0x9C (3 bytes, base id 230, where Rapture parks its teleport
# image, so this field is known-writable). Bits are MSB-first per byte, ability id = base + index.
RSM = {"reaction": (0x94, 4, None), "support": (0x98, 4, 198), "movement": (0x9C, 3, 230)}


def cmd_rsm(slot, clear=None):
    """Show a unit's Reaction/Support/Movement ability bits, and optionally clear a whole field.

    The question that prompted it: a Bomb keeps floating after its Float STATUS is cleared,
    because the hover comes from the job's innate Levitate MOVEMENT ability. This is where that
    ability lives. Clearing the movement field removes every movement ability the unit has, which
    is a blunt instrument on purpose: we do not know Levitate's bit index, and zeroing three bytes
    answers "does the engine re-derive hover from this field" without needing to.

    Height is re-stamped on move-end, turn-open and being HIT (all observed), so if the Bomb does
    not drop immediately, give it a turn or hit it before concluding anything."""
    require_fielded(slot)
    c = combat_of(slot)
    for name, (off, size, base) in RSM.items():
        raw = rpm(c + off, size)
        bits = []
        if raw:
            for bi, byte in enumerate(raw):
                for k in range(8):
                    if byte & (0x80 >> k):
                        bits.append(bi * 8 + k + (base or 0))
        print(f"  {name:>8} @combat+{off:#04x}: {raw.hex(' ') if raw else '??'}"
              f"{'  ids ' + str(bits) if bits and base else ''}")
    if clear:
        off, size, _ = RSM[clear]
        before = rpm(c + off, size)
        print(f"clearing {clear} ({size} bytes at combat+{off:#04x}), was {before.hex(' ')}")
        print(f"restore with: rsm {slot} --restore {clear}:{before.hex()}")
        if input("CLEAR? (y/n) ").strip().lower() != "y":
            print("aborted."); return
        for i in range(size):
            wu8(c + off + i, 0)
        after = rpm(c + off, size)
        print(f"cleared -> {after.hex(' ')}")
        print("EYEBALL: did it drop? If not, give it a turn or hit it (both re-stamp height).")


def cmd_rsm_restore(slot, field, hexbytes):
    """Put a cleared RSM field back from the hex the clear printed."""
    require_fielded(slot)
    off, size, _ = RSM[field]
    data = bytes.fromhex(hexbytes)
    if len(data) != size:
        print(f"{field} needs {size} bytes, got {len(data)}"); sys.exit(1)
    c = combat_of(slot)
    for i, b in enumerate(data):
        wu8(c + off + i, b)
    print(f"{field} restored to {rpm(c + off, size).hex(' ')}")


def cmd_status(slot, name, on, layers="both"):
    """Set or clear a status bit (LW-119, map in status_map.py).

    Layers, and why it matters: the model says composed (band +0x45..) is rebuilt every frame as
    inflicted (band +0x1D3..) OR innate (band +0x3B..). So `composed` alone is the wasted-write
    trap; `both` writes composed and inflicted, which is the normal choice; `all` also clears the
    INNATE layer, which is the only way to remove a job-derived status like a Bomb's Float, and
    is also a live test of the layer model itself (if clearing composed alone sticks on an innate
    status, the model is wrong).

    Refuses the two ids with crash tapes and asks before the engine-owned ones."""
    e = status_map.lookup(name)
    if not e:
        print(f"unknown status '{name}'. Known: {', '.join(sorted(status_map.STATUSES))}")
        sys.exit(1)
    if e["name"] in status_map.REFUSE:
        print(f"{e['name']} is REFUSED: {e['hazard']}")
        sys.exit(1)
    node_ok = require_fielded(slot)
    band = combat_of(slot) + BAND
    if e["hazard"]:
        print(f"HAZARD: {e['hazard']}")
    if e["name"] in status_map.CONFIRM and input("proceed? (y/n) ").strip().lower() != "y":
        print("aborted."); return
    targets = [("composed", band + e["composed"])]
    if layers in ("both", "all"):
        targets.append(("inflicted", band + e["inflicted"]))
    if layers == "all":
        targets.append(("innate", band + status_map.INNATE_BASE + e["byte"]))
    print(f"slot {slot} {e['name']} (id {e['id']}, tier {e['tier']}) -> {'ON' if on else 'OFF'}")
    for label, addr in targets:
        before = ru8(addr)
        if before is None:
            print(f"  {label:>9}: unreadable, skipped"); continue
        after = (before | e["mask"]) if on else (before & ~e["mask"] & 0xFF)
        wu8(addr, after)
        print(f"  {label:>9} @band+{addr - band:#05x}: {before:#04x} -> {ru8(addr):#04x} "
              f"(wanted {after:#04x})")
    time.sleep(0.5)
    print("  after 0.5s:", ", ".join(
        f"{label}={ru8(addr):#04x}" for label, addr in targets if ru8(addr) is not None))
    print("EYEBALL: did the effect change in game? A composed bit that snaps back means the layer "
          "below it is re-ORing, which is the model working.")


def cmd_status_sweep(slot, secs=6.0):
    """THE STATUS SWEEP: the animation catalog treatment for statuses.

    Applies each never-exercised bit one at a time, waits, asks what happened, clears it, moves
    on. Appends one JSON line per status to status_catalog.jsonl next to this probe (append per
    entry, so a crash or a freeze loses nothing already labeled).

    SKIPPED, deliberately: the two ids with crash tapes (crystal is permanent unit loss, treasure
    crashed the game outright), the engine-owned action states (charging, jump) whose write
    desyncs an action, dead (it crashed the engine when set away from the unit's turn), and the
    contested/walled pair (invite, charm) whose companion bytes three sources disagree about.
    Those are not sweep material; they are individually-designed experiments.

    PROTOCOL, learned from the animation sweep: sit on your own unit's OPEN MENU so the clock is
    frozen and the guinea pig cannot act mid-question, and sweep an ENEMY. Enter = nothing
    visible, s = skip, q = quit (the resume hint names the status you stopped on, not the next)."""
    import json
    require_fielded(slot)
    band = combat_of(slot) + BAND
    who = input(f"sweeping slot {slot}: what unit is this (job/monster)? ").strip()
    if not who:
        print("a unit description is required; statuses may well behave per job. Aborting.")
        return
    skip = status_map.REFUSE | status_map.CONFIRM
    todo = [n for n, (sid, tier, hz) in status_map.STATUSES.items()
            if n not in skip and tier in ("M", "O")]
    out_path = pathlib.Path(__file__).resolve().parent / "status_catalog.jsonl"
    run_id = time.strftime("%Y%m%d_%H%M%S")
    print(f"{len(todo)} statuses to sweep -> {out_path}")
    print("Enter = nothing visible, s = skip, q = quit")
    for name in todo:
        e = status_map.lookup(name)
        for label_, addr in (("c", band + e["composed"]), ("i", band + e["inflicted"])):
            v = ru8(addr)
            if v is not None:
                wu8(addr, v | e["mask"])
        time.sleep(secs)
        label = input(f"  {name} (id {e['id']}) -> what happened? ").strip()
        for label_, addr in (("c", band + e["composed"]), ("i", band + e["inflicted"])):
            v = ru8(addr)
            if v is not None:
                wu8(addr, v & ~e["mask"] & 0xFF)
        if label.lower() == "q":
            print(f"stopped; resume by sweeping again and skipping past {name}.")
            break
        if label.lower() == "s":
            continue
        with open(out_path, "a", encoding="utf-8") as f:
            f.write(json.dumps({"unit": who, "run": run_id, "slot": slot, "status": name,
                                "id": e["id"], "label": label or "none"}) + chr(10))
    print("sweep done; every bit set by this sweep was cleared after its question.")


def cmd_warp(slot, x, y, force=False):
    """Teleport to an ARBITRARY tile, including one the guards would normally refuse. Exists to
    answer the question every other verb dodges: what does the engine do when a unit is placed
    somewhere it should not be? Uses the live-proven triple-write (combat logic tile, node AI
    tile key, node world X/Y) and leaves Z alone, so a height difference renders floated or sunk
    until the engine re-stamps.

    Guards kept even under --force, because both are known to ruin a battle rather than teach us
    anything: never the current actor, never a tile another unit already holds (co-tiling causes
    target shadowing plus the movement soft-lock, proven live). Everything else is allowed:
    off-map coordinates, walls, void, whatever the map does not actually contain.

    PRE-REGISTERED OUTCOMES: renders and acts normally = tiles are just coordinates and placement
    is unconstrained; renders but cannot move or be pathed to = placed and stranded (a reserve
    trick with a cost); renders at a silly height = the known Z gap; hard freeze or crash = a
    hazard worth a ledger row and a guard everywhere else. THROWAWAY BATTLE."""
    node = require_fielded(slot)
    if acting(slot):
        print("that unit's turn is OPEN; warping the current actor is refused.")
        sys.exit(1)
    c = combat_of(slot)
    here = (ru8(c + 0x4F), ru8(c + 0x50))
    other = occupant_of((x, y), slot)
    if other is not None:
        print(f"({x},{y}) is held by slot {other}; refused even under --force (co-tile soft-lock).")
        sys.exit(1)
    if not force and not (0 <= x <= 30 and 0 <= y <= 30):
        print(f"({x},{y}) is outside the sane tile range; pass --force to do it anyway.")
        sys.exit(1)
    print(f"slot {slot}: {here} -> ({x},{y}). Z untouched, so expect a height error if the "
          f"ground differs. Restore afterwards with: warp {slot} {here[0]} {here[1]}")
    if input("WARP? (y/n) ").strip().lower() != "y":
        print("aborted.")
        return
    wu8(c + 0x4F, x & 0xFF); wu8(c + 0x50, y & 0xFF)
    wu8(node + 0x88, x & 0xFF); wu8(node + 0x89, y & 0xFF)
    wu16(node + 0x4C, (28 * x + 14) & 0xFFFF)
    wu16(node + 0x50, (28 * y + 14) & 0xFFFF)
    print(f"warped. logic=({ru8(c + 0x4F)},{ru8(c + 0x50)}) "
          f"world=({ru16(node + 0x4C)},{ru16(node + 0x50)})")
    print("EYEBALL: does it render there? can the cursor reach it? does it still take turns?")


def cmd_hide(slot, vanish=False, page=VANISH_PAGE):
    """--vanish adds the missing half of a real disappearing act (owner's idea, 2026-07-22).
    The gate byte removes a unit from LOGIC only: untargetable, unhoverable, no turns, AI blind
    to it, but the render weld leaves its SPRITE STANDING, which is why the ledger calls this
    the ghost-statue toggle. Requesting an invisibility page (the owner's sweep found 0x32 "unit
    turned completely invisible" and 0x3b as its sibling) removes the sprite too.

    The two halves hold each other up: an animation page is normally re-stamped at the unit's
    next event, but a hidden unit GETS no events, so nothing overwrites it. Reveal restores both."""
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
    wu8(c + GATE, 0xFF)
    if vanish:
        node = node_of(slot)
        if node:
            wu16(node + ANIM_REQ, (page + 1) & 0xFFFF)
            st[str(slot)]["vanished"] = True
            print(f"slot {slot} hidden AND sprite vanished (page {page:#04x}).")
        else:
            print(f"slot {slot} hidden, but the node was unreadable so the sprite still stands.")
    save_state(st)
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
    if rec.get("vanished"):
        wu16(node + ANIM_REQ, (IDLE_PAGE + 1) & 0xFFFF)   # sprite back before the logic returns
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


def cmd_reserve(slot, vanish=False):
    """Park-and-summon: hide the logic and REMEMBER the ground Z so deploy can drop them back in
    from the sky. The despawn arc's cheaper cousin: no engine sweeper involved, so no
    re-enrollment is needed to bring them back.

    v2 (owner live 2026-07-22): the original version also sank the render below the floor as
    belt-and-braces. That does not work and is removed. World Z is -12 * height, so POSITIVE is
    DOWNWARD, and a downward write collides with the terrain: the unit will not go under the
    map. Upward still works, which is why the resurrect arc's sky descent (negative Z) does.
    The gate hide alone already removes the unit completely, so nothing is lost."""
    node = node_of(slot)
    if not node:
        print("unit not noded; aborting.")
        sys.exit(1)
    cmd_hide(slot, vanish=vanish)
    z = ru16(node + NODE_Z)
    z = z - 65536 if z > 32767 else z
    st = load_state()
    st[str(slot)]["z"] = z
    save_state(st)
    print(f"slot {slot} reserved (hidden; ground Z {z} remembered for the deploy descent).")


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
    vanish = "--vanish" in a
    force = "--force" in a
    layers = "all" if "--all" in a else ("composed" if "--composed" in a else "both")
    a = [x for x in a if x not in ("--all", "--composed")]
    a = [x for x in a if x != "--force"]
    page = VANISH_PAGE
    if "--page" in a:
        i = a.index("--page")
        if i + 1 >= len(a):
            print("--page needs a hex page id, e.g. --page 3b"); sys.exit(1)
        page = int(a[i + 1], 16); del a[i:i + 2]
    a = [x for x in a if x != "--vanish"]
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
        for sl in [int(x) for x in a[1].split(",")]:
            cmd_hide(sl, vanish, page)
    elif cmd == "show" and len(a) >= 2:
        for sl in [int(x) for x in a[1].split(",")]:
            cmd_show(sl)
    elif cmd == "rsm" and len(a) >= 2:
        if len(a) >= 4 and a[2] == "--restore":
            f, hx = a[3].split(":"); cmd_rsm_restore(int(a[1]), f, hx)
        else:
            cmd_rsm(int(a[1]), a[2] if len(a) > 2 else None)
    elif cmd == "sweep" and len(a) >= 2:
        cmd_status_sweep(int(a[1]), float(a[2]) if len(a) > 2 else 6.0)
    elif cmd == "status" and len(a) >= 4:
        cmd_status(int(a[1]), a[2], a[3].lower() in ("on", "1", "true"), layers)
    elif cmd == "warp" and len(a) >= 4:
        cmd_warp(int(a[1]), int(a[2]), int(a[3]), force)
    elif cmd == "float" and len(a) >= 3:
        cmd_float(int(a[1]), int(a[2]))
    elif cmd == "reserve" and len(a) >= 2:
        cmd_reserve(int(a[1]), vanish)
    elif cmd == "deploy" and len(a) >= 2:
        cmd_deploy(int(a[1]))
    else:
        print(__doc__)


if __name__ == "__main__":
    main()
