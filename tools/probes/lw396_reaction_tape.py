"""LW-396 Phase 0: how does the game mark a Counter or other reaction at the moment it kills?

A kill landed by your unit's Counter happens during the ENEMY's turn, and the mod refuses every kill
on an enemy turn so that enemies killing enemies never pay your weapon. To pay a real Counter kill the
mod has to tell the two apart. This probe tapes what the game itself records, read only, one line
per change:

  python tools/probes/lw396_reaction_tape.py [seconds=300]

Watched:
  turn      the turn queue's team value (0 player, 1 enemy, 2 ally or guest)
  per seat  hp, the friend or foe bit (band +0x1D2 bit 0x10, E for enemy), nameId (+0x1E0) and the
            unit's action record at band +0x184: ability id (+0x2, u16), kind (+0xA, 5 performing,
            6 receiving) and the cross reference byte (+0xB)

Owner protocol: give a sturdy party unit Counter (or another reaction) and a Living Weapon, let an
enemy hit it until the Counter kills that enemy, then end the battle. Start this probe before the
enemy's turn. Send the tape (%TEMP%/lw396_tape_<time>.log) and livingweapon.log. What to look for:
the lines around the enemy's hp reaching 0 show which seat reads kind 5 (performing) and which
ability id it carries while the turn value reads 1. A bait first, if time allows: a plain enemy on
enemy hit (Confuse or a monster AoE) taped the same way, so the two shapes can be compared.
"""
import os
import sys
import tempfile
import time
from datetime import datetime

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import battle_cheats as bc  # noqa: E402

TURN_QUEUE = 0x1407832A0
TQ_TEAM = 0x02
A_HP = 0x14
A_FRIEND_FOE = 0x1D2
A_ENEMY_BIT = 0x10
A_NAMEID = 0x1E0
A_AREC = 0x184


def stamp():
    return datetime.now().strftime("%H:%M:%S.%f")[:-3]


def main():
    seconds = int(sys.argv[1]) if len(sys.argv) > 1 else 300
    old_anchor = bc.COMBAT_ANCHOR
    bc._rebase_pins()
    turn_queue = TURN_QUEUE + (bc.COMBAT_ANCHOR - old_anchor)
    out_path = os.path.join(tempfile.gettempdir(), f"lw396_tape_{datetime.now():%Y%m%d_%H%M%S}.log")
    out = open(out_path, "w", encoding="utf-8")

    def emit(line):
        text = f"[{stamp()}] {line}"
        print(text)
        out.write(text + "\n")
        out.flush()

    def seat(s):
        e = bc._band_entry_addr(s)
        if not bc._is_valid_entry(e):
            return None
        arec = bc.rpm(e + A_AREC, 12) or b"\0" * 12
        ff = bc.ru8(e + A_FRIEND_FOE) or 0
        return (bc.ru16(e + A_HP), "E" if ff & A_ENEMY_BIT else "P", bc.ru16(e + A_NAMEID),
                int.from_bytes(arec[2:4], "little"), arec[10], arec[11])

    emit(f"start, tape {out_path}")
    last_turn, last = None, {}
    end = time.time() + seconds
    try:
        while time.time() < end:
            turn = bc.ru16(turn_queue + TQ_TEAM)
            if turn != last_turn:
                emit(f"turn team={turn}")
                last_turn = turn
            for s in range(bc.BAND_SLOTS):
                cur = seat(s)
                if cur != last.get(s):
                    if cur is None:
                        emit(f"s{s} gone")
                    else:
                        hp, side, nid, abil, kind, xref = cur
                        emit(f"s{s} hp={hp} side={side} n{nid} abil={abil} kind={kind} xref={xref}")
                    last[s] = cur
            time.sleep(0.033)
    except KeyboardInterrupt:
        pass
    emit("stop")
    out.close()
    return 0


if __name__ == "__main__":
    sys.exit(main())
