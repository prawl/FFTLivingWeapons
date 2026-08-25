#!/usr/bin/env python
"""LW-251: watch the band's turn flags through a battle's OPENING minutes, live.

WHY. The WeaponPalette runtime resolves the acting unit via Band.FlagOwner (exclusive
t==1, real grid position mandatory), and on the FIRST turn of a battle that resolve has
been seen to land before the swing (Adamant Fang), seconds after the swing (Sage's Pole),
or never during the whole turn (the dual-wield Materia Blade turn, 94s with no paint).
This probe answers WHICH precondition fails and WHEN: t never rising, the position still
(0,0), or two disagreeing t==1 owners. Fix from the tape, not from a guess.

READ ONLY: rpm only, no writes, safe to leave running.

USAGE:
  cd tools\\probes
  python lw251_openturn_probe.py            # start BEFORE entering the battle
  python lw251_openturn_probe.py 300        # optional: seconds to watch (default 180)

Then enter a battle and take the first turn normally. The probe prints a line each time
the t==1 picture CHANGES, with the resolve verdict the runtime would reach. A tape lands
beside this file as lw251_openturn_tape.jsonl (append; timestamped runs).
"""
import json
import pathlib
import struct
import sys
import time

HERE = pathlib.Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
sys.path.insert(0, str(HERE.parents[1] / "tools" / "lib"))

import battle_cheats as bc

# Band-relative flag/name offsets, parsed from the pinned Offsets.cs so a re-anchor
# cannot silently stale this probe (offsets.require raises if the constant is missing).
import offsets as offsets_lib
_OFF = offsets_lib.load()
A_TURNFLAG = _OFF["ATurnFlag"]
A_NAMEID = _OFF["ANameId"]

TAPE = HERE / "lw251_openturn_tape.jsonl"


def sane(entry_bytes):
    lvl = entry_bytes[bc.A_LEVEL]
    br = entry_bytes[bc.A_BRAVE]
    fa = entry_bytes[bc.A_FAITH]
    mhp = struct.unpack_from("<H", entry_bytes, bc.A_MAXHP)[0]
    return 1 <= lvl <= 99 and 1 <= br <= 100 and 1 <= fa <= 100 and 1 <= mhp < 2000


def snapshot():
    """One pass over the 49 band slots: every SANE entry's t-flag picture."""
    rows = []
    for s in range(bc.BAND_SLOTS):
        base = bc.BAND_READ_BASE + s * bc.COMBAT_STRIDE
        raw = bc.rpm(base, 0x200)
        if not raw or not sane(raw):
            continue
        t = raw[A_TURNFLAG]
        if t == 0:
            continue
        rows.append({
            "slot": s,
            "t": t,
            "gx": raw[bc.A_GX],
            "gy": raw[bc.A_GY],
            "nameId": struct.unpack_from("<H", raw, A_NAMEID)[0],
            "lvl": raw[bc.A_LEVEL],
            "weapon": struct.unpack_from("<H", raw, 0x04)[0],   # AWeapon: CWeapon - BandEntry
        })
    return rows


def verdict(rows):
    """What Band.FlagOwner (D2b rules) would decide on this picture."""
    cands = [r for r in rows if r["t"] == 1 and (r["gx"] != 0 or r["gy"] != 0)]
    if not cands:
        zero_pos = [r for r in rows if r["t"] == 1]
        return "REFUSE: no t=1" if not zero_pos else "REFUSE: t=1 only at (0,0)"
    ids = {(r["nameId"] or (r["lvl"],)) for r in cands}
    if len(ids) > 1:
        return "REFUSE: ambiguous (%d identities)" % len(ids)
    w = cands[0]["weapon"]
    return f"RESOLVE slot {cands[0]['slot']} nameId {cands[0]['nameId']} weapon {w}"


def main(seconds):
    bc._require_game()
    started = time.time()
    last_key = None
    print(f"watching the band for {seconds}s; enter a battle and take the first turn.")
    with TAPE.open("a", encoding="utf8") as fh:
        fh.write(json.dumps({"run": time.strftime("%Y-%m-%d %H:%M:%S")}) + "\n")
        while time.time() - started < seconds:
            rows = snapshot()
            key = json.dumps(rows, sort_keys=True)
            if key != last_key:
                last_key = key
                t = round(time.time() - started, 2)
                v = verdict(rows)
                line = {"t": t, "verdict": v, "flags": rows}
                fh.write(json.dumps(line) + "\n")
                fh.flush()
                brief = ", ".join(
                    f"s{r['slot']} t={r['t']} pos=({r['gx']},{r['gy']}) name={r['nameId']} wep={r['weapon']}"
                    for r in rows) or "(no t!=0 entries)"
                print(f"[{t:>7.2f}s] {v}   {brief}")
            time.sleep(0.1)
    print(f"done. tape: {TAPE}")


if __name__ == "__main__":
    main(int(sys.argv[1]) if len(sys.argv) > 1 else 180)
