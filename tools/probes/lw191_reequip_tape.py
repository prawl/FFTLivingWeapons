"""LW-191 Phase 0: does a mid battle Re-equip or a steal write the party roster, or only the battle copy?

The mod grants Shadow Blade, Barrage and Provoke by looking for the weapon in the party ROSTER. If a
mid battle Re-equip or a steal only changes the BATTLE copy of the unit, the roster still shows the
old hand and the mod takes the command away while the wielder stands right there. This probe tapes
both copies side by side, read only, one line per change.

  python tools/probes/lw191_reequip_tape.py [seconds=300]

Watched, per occupied roster row: right hand (+0x14) and off hand (+0x18), keyed by nameId (+0x230).
Per valid band seat: the battle weapon (+0x04) and nameId (+0x1E0). A party member's band seat and
roster row share a nameId, so the two can be compared directly.

Owner protocol: start a battle with a Knight or Squire who does NOT hold the Sanguine Sword, start the
probe, then either steal the sword from an enemy who carries it or Re-equip it from the bag
mid battle. Send the tape (%TEMP%/lw191_tape_<time>.log) and that session's livingweapon.log. PASS for
the "roster is written" premise: the roster row's hand changes to the sword (id 23) within a second
of the band seat's weapon changing. FAIL: only the band seat changes.
"""
import os
import sys
import tempfile
import time
from datetime import datetime

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import battle_cheats as bc  # noqa: E402

ROSTER_BASE = 0x1411A7D10
ROSTER_STRIDE = 0x258
ROSTER_SLOTS = 50
R_LEVEL = 0x1D
R_RHAND = 0x14
R_OFFHAND = 0x18
R_NAMEID = 0x230
A_WEAPON = 0x04
A_NAMEID = 0x1E0


def stamp():
    return datetime.now().strftime("%H:%M:%S.%f")[:-3]


def main():
    seconds = int(sys.argv[1]) if len(sys.argv) > 1 else 300
    old_anchor = bc.COMBAT_ANCHOR
    bc._rebase_pins()
    roster_base = ROSTER_BASE + (bc.COMBAT_ANCHOR - old_anchor)
    out_path = os.path.join(tempfile.gettempdir(), f"lw191_tape_{datetime.now():%Y%m%d_%H%M%S}.log")
    out = open(out_path, "w", encoding="utf-8")

    def emit(line):
        text = f"[{stamp()}] {line}"
        print(text)
        out.write(text + "\n")
        out.flush()

    def roster():
        rows = {}
        for r in range(ROSTER_SLOTS):
            b = roster_base + r * ROSTER_STRIDE
            lvl = bc.ru8(b + R_LEVEL)
            if lvl is None or not 1 <= lvl <= 99:
                continue
            rows[bc.ru16(b + R_NAMEID)] = (r, bc.ru16(b + R_RHAND), bc.ru16(b + R_OFFHAND))
        return rows

    def band():
        seats = {}
        for s in range(bc.BAND_SLOTS):
            e = bc._band_entry_addr(s)
            if bc._is_valid_entry(e):
                seats[s] = (bc.ru16(e + A_NAMEID), bc.ru16(e + A_WEAPON))
        return seats

    emit(f"start, tape {out_path}")
    last_r, last_b = None, None
    end = time.time() + seconds
    try:
        while time.time() < end:
            r, b = roster(), band()
            if r != last_r:
                emit("roster " + " ".join(f"n{n}:row{v[0]} rh={v[1]} oh={v[2]}" for n, v in sorted(r.items(), key=lambda kv: kv[1][0])))
                last_r = r
            if b != last_b:
                emit("band " + " ".join(f"s{s}:n{v[0]} w={v[1]}" for s, v in sorted(b.items())))
                last_b = b
            time.sleep(0.1)
    except KeyboardInterrupt:
        pass
    emit("stop")
    out.close()
    return 0


if __name__ == "__main__":
    sys.exit(main())
