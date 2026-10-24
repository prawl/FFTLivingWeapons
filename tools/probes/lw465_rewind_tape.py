"""LW-465 Phase 0: what does the game do, tick by tick, when a battle is rewound?

A Provoke hold hides the party (the Invisible bit on every player unit but one) and a planted
Bulwark bars a tile (the path grid's veto bit). Both are meant to let go when the battle ends, but a
Retry, or quitting to the title and choosing Continue mid battle, rewinds the fight in place with no
battle end. Before the mod can release them on the rewind it needs to know when the game rebuilds the
battlefield. This probe tapes that, read only.

  python tools/probes/lw465_rewind_tape.py [seconds=300] [tiles=512]

It prints one line per change, stamped with wall clock time to the millisecond, so the lines can be
laid beside livingweapon.log, whose RewindRestoreHook lines ("retry hook seen", restart records) mark
the moment the mod saw the checkpoint restore. Watched:

  mode     the battle mode byte (3 on the battlefield, 0 on the map)
  slot0/9  the two battle phase words the mod's live gate reads
  inv      which band seats have the Invisible bit (band +0x47 bit 0x10) set
  veto     which path grid tiles have the veto bit (+6 bit 0x02) set beyond the map's own at start

The tape also goes to %TEMP%/lw465_tape_<time>.log. Owner protocol for the card's two runs:
  1. In a battle, put a Provoke hold up and plant the Sunderer, then start this probe.
  2. Pick Retry. Let the rewound battle run ten seconds.
  3. Again with a hold and a plant: quit to the title, Continue, let it run ten seconds.
  4. Ctrl+C, send the tape file and that session's livingweapon.log.
"""
import os
import sys
import tempfile
import time
from datetime import datetime

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import battle_cheats as bc  # noqa: E402

PREFERRED = {
    "SLOT0": 0x140782A30,
    "SLOT9": 0x140782A54,
    "BATTLE_MODE": 0x1409069A0,
    "PATH_GRID": 0x140D8DCB0,
}
PATH_STRIDE = 8
PATH_VETO_FIELD = 6
PATH_VETO_BIT = 0x02
A_INVISIBLE = 0x47
A_INVISIBLE_BIT = 0x10


def u32(addr):
    raw = bc.rpm(addr, 4)
    return int.from_bytes(raw, "little") if raw and len(raw) == 4 else None


def stamp():
    return datetime.now().strftime("%H:%M:%S.%f")[:-3]


def main():
    seconds = int(sys.argv[1]) if len(sys.argv) > 1 else 300
    tiles = int(sys.argv[2]) if len(sys.argv) > 2 else 512
    old_anchor = bc.COMBAT_ANCHOR
    bc._rebase_pins()
    delta = bc.COMBAT_ANCHOR - old_anchor
    pin = {k: v + delta for k, v in PREFERRED.items()}

    out_path = os.path.join(tempfile.gettempdir(), f"lw465_tape_{datetime.now():%Y%m%d_%H%M%S}.log")
    out = open(out_path, "w", encoding="utf-8")

    def emit(line):
        text = f"[{stamp()}] {line}"
        print(text)
        out.write(text + "\n")
        out.flush()

    def veto_set():
        raw = bc.rpm(pin["PATH_GRID"], tiles * PATH_STRIDE)
        if not raw:
            return None
        return {i for i in range(len(raw) // PATH_STRIDE)
                if raw[i * PATH_STRIDE + PATH_VETO_FIELD] & PATH_VETO_BIT}

    def invisible_seats():
        seats = []
        for s in range(bc.BAND_SLOTS):
            e = bc._band_entry_addr(s)
            if not bc._is_valid_entry(e):
                continue
            b = bc.ru8(e + A_INVISIBLE)
            if b is not None and b & A_INVISIBLE_BIT:
                seats.append(s)
        return seats

    baseline_veto = veto_set() or set()
    emit(f"start, {len(baseline_veto)} tiles already vetoed by the map (ignored from here), tape {out_path}")
    last = None
    end = time.time() + seconds
    try:
        while time.time() < end:
            mode = bc.ru8(pin["BATTLE_MODE"])
            s0, s9 = u32(pin["SLOT0"]), u32(pin["SLOT9"])
            inv = invisible_seats()
            vs = veto_set()
            extra = sorted(vs - baseline_veto) if vs is not None else None
            cur = (mode, s0, s9, tuple(inv), tuple(extra) if extra is not None else None)
            if cur != last:
                emit(f"mode={mode} slot0={s0 if s0 is None else hex(s0)} slot9={s9 if s9 is None else hex(s9)} "
                     f"inv={inv} veto={'unreadable' if extra is None else extra}")
                last = cur
            time.sleep(0.033)
    except KeyboardInterrupt:
        pass
    emit("stop")
    out.close()
    return 0


if __name__ == "__main__":
    sys.exit(main())
