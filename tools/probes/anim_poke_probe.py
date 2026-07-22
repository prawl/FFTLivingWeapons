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


def main():
    _require_game()
    argv = sys.argv[1:]
    force = "--force" in argv
    argv = [a for a in argv if a != "--force"]
    if argv and argv[0] == "table":
        cmd_table()
    elif len(argv) >= 2 and argv[0] == "watch":
        cmd_watch(int(argv[1]), float(argv[2]) if len(argv) > 2 else 10.0)
    elif len(argv) >= 3 and argv[0] == "poke":
        cmd_poke(int(argv[1]), int(argv[2], 16), force)
    else:
        print(__doc__)


if __name__ == "__main__":
    main()
