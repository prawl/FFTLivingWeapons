"""THE ONE OWED POKE: the animation request register (LW-113; ledger row 'Play ANY animation',
Uncertain since 2026-07-10 because every earlier poke hit the OUTPUT block, never the input).

PREMISE (falsifiable): writing u16 = logicalId+1 into render node +0x10 makes the engine play
that animation on that unit. Provenance for the encoding: the game's own RequestAnim stub
(0x140268E7C) stores logicalId+1; the decoded force-stand recipe writes 0x0004 (idle logical 3,
plus 1) and the crouch recipe 0x0035 (crouch logical 0x34, plus 1). The per-node tick
(0x14026CB1C) consumes the latch; the SEQ walker (0x14026B388) plays it. Full decode:
anim-request-register memory + LIVE_LEDGER's Uncertain row.

PRE-REGISTERED OUTCOMES (decide BEFORE poking; do not rationalize after):
  PASS........ +0x10 consumed (reads back 0 within ~0.5s) AND the unit visibly plays the anim.
  CONTRADICTED +0x10 consumed but the unit never changes: input-consumed-but-ignored, the same
               wall shape as the LW-58 pending-field writes. Ledger the negative.
  RE-CHECK.... +0x10 never consumed: wrong width/address/tick gating, NOT a verdict either way.

VOCABULARY (logical ids; the probe applies the +1 itself): idle 3, flinch 0x19, walk 0x24,
crouch 0x34, stand 0x35, weapon swings 0x3D-0x57, die 0x75/0x76. Round 1 sticks to crouch and
flinch on an IDLE ENEMY (zero-cost guinea pig, never the current actor, never mid-walk: the
mover owns the node during a walk). Round 2 only if round 1 passes: --force sets node +0x8F
bit 0x20 (the decoded force bit for overriding a critical unit's selector). Restore = poke 3
(idle); the engine also re-stamps on the unit's next turn.

    python tools\\probes\\anim_poke_probe.py table              # units + register/output reads
    python tools\\probes\\anim_poke_probe.py watch <slot> [s]   # healthy-baseline watch, no writes
    python tools\\probes\\anim_poke_probe.py poke <slot> <logicalHex> [--force]
    python tools\\probes\\anim_poke_probe.py sweep <slot> [--start H] [--stop H]   # catalog pages
    python tools\\probes\\anim_poke_probe.py face <slot> <0..7>    # +0x7C write (meaning UNKNOWN, latches)
    python tools\\probes\\anim_poke_probe.py turn <slot> <count>   # request turn PAGE 0x01 count times
    python tools\\probes\\anim_poke_probe.py stop <slot> [secs]    # page 0x00 + CT-pin: the Stop combo

LIVE RESULT 2026-07-21 (owner, first input poke ever fired): PASS by the pre-registered bar,
twice. Poke 1 consumed before the +0.10s sample, output block parked at phase 1 (the frozen
pose). Poke 2 caught the latch red-handed: req held 53 through +0.25s, consumed by +0.50s. The
unit visibly played the page both times and a later real move event re-stamped it (the director
overwrite, as decoded). Vocabulary caveat proven the same minute: logical 0x34 played PRONE on
the owner's knight, not the decode session's crouch, so page ids are per sprite class and the
sweep mode below exists to map each class's flipbook.
"""
import pathlib
import struct
import sys
import time

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
from battle_cheats import rpm, ru8, ru16, wu8, wu16, _require_game

UNITS = 0x141853CE0
HEAD = 0x140D3A410
REQ = 0x10      # u16 request register (INPUT; write logicalId+1)
FORCE = 0x8F    # |= 0x20 = force bit (round 2 only)
OUT_BLOCK = 0x420  # per-frame OUTPUT state block (re-stamps; writes here proved nothing)
OUT_PHASE = 0x423  # output phase byte


def u64(a):
    b = rpm(a, 8)
    return None if b is None else struct.unpack("<Q", b)[0]


def node_of(slot):
    cur = u64(HEAD)
    for _ in range(64):
        if not cur:
            return None
        if u64(cur + 0x148) == UNITS + slot * 0x200:
            return cur
        cur = u64(cur)
    return None


def reg_snap(node):
    out = rpm(node + OUT_BLOCK, 4)
    return dict(req=ru16(node + REQ), f8f=ru8(node + FORCE),
                out=out.hex() if out else "??", phase=ru8(node + OUT_PHASE))


def cmd_table():
    print(f"{'slot':>4} {'tile':>7} {'node':>12} {'+0x10 req':>9} {'+0x8F':>5} {'+0x420 out':>10} {'phase':>5}")
    cur = u64(HEAD)
    for _ in range(64):
        if not cur:
            break
        c = u64(cur + 0x148)
        off = (c or 0) - UNITS
        if c and 0 <= off < 21 * 0x200 and off % 0x200 == 0:
            s = reg_snap(cur)
            print(f"{off // 0x200:>4} ({ru8(cur + 0x88):>2},{ru8(cur + 0x89):>2}) 0x{cur:>10X} "
                  f"{s['req']:>9} {s['f8f']:>5} {s['out']:>10} {s['phase']:>5}")
        cur = u64(cur)


def cmd_watch(slot, secs):
    node = node_of(slot)
    if not node:
        print("unit not noded; aborting.")
        sys.exit(1)
    print(f"slot {slot} node 0x{node:X}: watching req/out for {secs}s (healthy baseline, no writes)")
    prev = None
    end = time.time() + secs
    while time.time() < end:
        s = reg_snap(node)
        if s != prev:
            print(f"  +{secs - (end - time.time()):6.2f}s req={s['req']} f8f={s['f8f']} out={s['out']} phase={s['phase']}")
            prev = s
        time.sleep(0.1)
    print("baseline done. Expected on an idle unit: req stays 0, out block cycles.")


def cmd_poke(slot, logical, force):
    node = node_of(slot)
    if not node:
        print("unit not noded; aborting.")
        sys.exit(1)
    encoded = (logical + 1) & 0xFFFF
    before = reg_snap(node)
    print(f"slot {slot} node 0x{node:X}  BEFORE: {before}")
    if before["req"] != 0:
        print("register not idle (req != 0); refusing this tick, retry when idle.")
        sys.exit(1)
    print(f"write plan: u16 {encoded:#06x} (logical {logical:#x} + 1) -> node+0x10"
          + ("; node+0x8F |= 0x20 (FORCE, round 2)" if force else "; no force bit (round 1)"))
    if input("POKE? (y/n) ").strip().lower() != "y":
        print("aborted.")
        return
    if force:
        wu8(node + FORCE, (before["f8f"] | 0x20) & 0xFF)
    wu16(node + REQ, encoded)
    last = 0.0
    for dt in (0.1, 0.25, 0.5, 1.0, 2.0):   # cumulative sample points after the write
        time.sleep(dt - last)
        last = dt
        s = reg_snap(node)
        print(f"  +{dt:4.2f}s req={s['req']} f8f={s['f8f']} out={s['out']} phase={s['phase']}")
    print("EYEBALL VERDICT NEEDED: did the unit visibly play it?")
    print("  consumed + visible  = PASS (the premise holds; owner flips the ledger row)")
    print("  consumed + no change = CONTRADICTED (consumed-but-ignored; ledger the negative)")
    print("  req never consumed   = RE-CHECK (no verdict; wrong width/gating)")
    print("restore: rerun with logical 3 (idle), or let the unit's next turn re-stamp.")


def cmd_sweep(slot, start, stop):
    """Flipbook cataloging (LW-113 round 2): walk logical ids [start, stop] on one unit and
    record what each page plays, one JSON line per id appended to anim_catalog.jsonl next to
    this probe (append-per-entry: a crash or a game freeze loses nothing already labeled).
    PROTOCOL: sit on YOUR OWN unit's open menu (CT is frozen while a player menu is up) and
    sweep an ENEMY, so the guinea pig stays idle indefinitely. Labels are the owner's words at
    the keyboard: what did the unit DO? Enter = 'none' (no visible change); s = skip (could not
    see); q = quit (resume later with --start). Ids are per sprite class (0x34 was labeled
    crouch on the decode unit and plays PRONE on a knight), so the catalog records the unit
    description the sweep opened with, and one sweep per sprite class is the goal."""
    import json
    node = node_of(slot)
    if not node:
        print("unit not noded; aborting.")
        sys.exit(1)
    who = input(f"sweeping slot {slot} node 0x{node:X}: what unit is this (job/sex/monster)? ").strip()
    if not who:
        print("a unit description is required: page ids are per sprite class, so an unlabeled "
              "sweep cannot be used. Aborting before any write.")
        return
    run_id = time.strftime("%Y%m%d_%H%M%S")
    out_path = pathlib.Path(__file__).resolve().parent / "anim_catalog.jsonl"
    print(f"catalog -> {out_path} (append); Enter=none, s=skip, q=quit+resume-later")
    for logical in range(start, stop + 1):
        if reg_snap(node)["req"] != 0:
            time.sleep(0.3)                        # a prior slip is still latched; let it consume
            if reg_snap(node)["req"] != 0:
                print(f"  id {logical:#04x}: register stuck, stopping (resume with --start {logical:#x})")
                break
        wu16(node + REQ, (logical + 1) & 0xFFFF)
        time.sleep(1.5)                            # let the page play far enough to read
        label = input(f"  id {logical:#04x} -> what played? ").strip()
        if label.lower() == "q":
            # Resume AT this id, not after it: quitting means this one was never labeled, and
            # an off-by-one here silently drops a page from the catalog forever.
            print(f"stopped; resume with: sweep {slot} --start {logical:#x}")
            break
        if label.lower() == "s":
            continue
        with open(out_path, "a", encoding="utf-8") as f:
            # run stamp: repeated or interleaved sweeps of the same class stay distinguishable
            f.write(json.dumps({"unit": who, "run": run_id, "slot": slot, "logical": logical,
                                "label": label or "none"}) + "\n")
    wu16(node + REQ, 3 + 1)                        # leave the guinea pig standing (idle)
    print("sweep done; guinea pig restored to idle.")


FACING = 0x7C   # u8 facing stored by the RequestAnim stub alongside the page id (decoded, unpoked)
CT = 0x41       # combat-struct CT byte (band +0x25); write side proven live (the Zwill extra-turn slam)


def cmd_face(slot, value):
    """Facing poke (decoded as the RequestAnim stub's second store, never fired): write u8 to
    node +0x7C and eyeball which way the unit turns. Map the value space by poking 0..7; the
    pre-registered outcomes mirror the register's: re-stamped-and-ignored is a verdict too."""
    node = node_of(slot)
    if not node:
        print("unit not noded; aborting.")
        sys.exit(1)
    before = ru8(node + FACING)
    print(f"slot {slot} node 0x{node:X}  facing before: {before}")
    wu8(node + FACING, value & 0xFF)
    time.sleep(0.5)
    print(f"facing after 0.5s: {ru8(node + FACING)} (write was {value})")
    print("EYEBALL: did the unit turn? value kept = input; value re-stamped = output, try pairing with a page poke.")


def cmd_turn(slot, count):
    """RETRACTED THEORY, corrected mechanism (2026-07-21, same evening). v1 of this command
    treated +0x7C as a request-time facing parameter; the owner falsified it live (facing +
    idle page, several values, zero turns) and the stop combo's camera-stare reproduced with
    DIFFERENT leftover facing state, so that stare is just what page 0x00 looks like. +0x7C's
    meaning is back on the unknown pile; the decoder's 'facing' label was a guess.

    The REAL turn mechanism was already in the owner's sweep catalog: turning is done by PAGES
    (0x01 'turn to the right/east', 0x02 'turn north-east'). This command requests page 0x01
    <count> times, 0.6s apart, to settle absolute-vs-relative: units that keep rotating right
    prove RELATIVE (four requests walk the compass); a single snap east that repeats prove
    ABSOLUTE (the other directions hide elsewhere)."""
    node = node_of(slot)
    if not node:
        print("unit not noded; aborting.")
        sys.exit(1)
    for i in range(count):
        wu16(node + REQ, 0x01 + 1)
        time.sleep(0.6)
        print(f"  request {i + 1}/{count} sent")
    print("EYEBALL: kept rotating right = RELATIVE pages; snapped east once and held = ABSOLUTE.")


def cmd_stop(slot, seconds):
    """THE STOP COMBO (first composition of the new family): page 0x00 (the owner's own sweep
    label: 'like the stop spell was casted') + the CT byte held at 0 so the scheduler never
    gives the unit a turn. Both writes are decoded/proven individually; this tests the PAIR.
    Release = stop holding (CT re-accrues from 0, which is authentic Stop economics: victims
    lose accrued CT) + page 3 (idle). Ctrl+C also releases."""
    node = node_of(slot)
    if not node:
        print("unit not noded; aborting.")
        sys.exit(1)
    c = UNITS + slot * 0x200
    ct0 = ru8(c + CT)
    print(f"slot {slot}: CT reads {ct0}; STOPPING for {seconds:.0f}s (page 0x00 + CT held 0)")
    wu16(node + REQ, 0x00 + 1)
    end = time.time() + seconds
    try:
        while time.time() < end:
            wu8(c + CT, 0)
            time.sleep(0.1)
    except KeyboardInterrupt:
        print("released early by Ctrl+C.")
    wu16(node + REQ, 3 + 1)
    print(f"released: CT resumes from 0 (was {ct0}, authentically lost), page back to idle.")
    print("EYEBALL: frozen mid-pose the whole hold, skipped in the turn order, then normal after?")


def main():
    _require_game()
    argv = sys.argv[1:]
    force = "--force" in argv
    start, stop = 0x00, 0x7F
    if "--start" in argv:
        i = argv.index("--start"); start = int(argv[i + 1], 16); del argv[i:i + 2]
    if "--stop" in argv:
        i = argv.index("--stop"); stop = int(argv[i + 1], 16); del argv[i:i + 2]
    argv = [a for a in argv if a != "--force"]
    if argv and argv[0] == "table":
        cmd_table()
    elif len(argv) >= 2 and argv[0] == "watch":
        cmd_watch(int(argv[1]), float(argv[2]) if len(argv) > 2 else 10.0)
    elif len(argv) >= 3 and argv[0] == "poke":
        cmd_poke(int(argv[1]), int(argv[2], 16), force)
    elif len(argv) >= 2 and argv[0] == "sweep":
        cmd_sweep(int(argv[1]), start, stop)
    elif len(argv) >= 3 and argv[0] == "face":
        cmd_face(int(argv[1]), int(argv[2]))
    elif len(argv) >= 3 and argv[0] == "turn":
        cmd_turn(int(argv[1]), int(argv[2]))
    elif len(argv) >= 2 and argv[0] == "stop":
        cmd_stop(int(argv[1]), float(argv[2]) if len(argv) > 2 else 15.0)
    else:
        print(__doc__)


if __name__ == "__main__":
    main()
