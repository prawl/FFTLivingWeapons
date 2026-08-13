"""TILE-CAST DETECTION (LW-181 Upheaval's last wall risk): where does the engine record the
PANEL a player aims an ability at?

WHY: Provoke's mark rides the target UNIT's memory; an empty tile has no unit, so a
ground-aimed Upheaval has nothing to piggyback on. The mod needs a readable signal that says
"the cast was aimed at tile (x, y)". The engine unquestionably has one: AoE resolution,
the targeting cursor, and the action preview all consume the aimed panel. This probe is the
differential tape that hunts it. READ-ONLY: it never writes game memory.

PRE-REGISTERED HUNT (call the shot before the tape rolls, playbook rule): at CAST-CONFIRM
(the moment the player locks the panel, before the charge/animation), some watched byte pair
flips to the aimed tile's (x, y). Candidates, most likely first:
  1. The CASTER's combat-struct action scratch (the briefed decision block is +0x180..+0x1D7;
     the window pads to +0x1E0, the extra 8 bytes are speculative coverage, not established
     fact): churns on actions; pointers inside crash on WRITES, reads are safe. NOTE the
     band frame target block (+0x170..+0x18C band-relative, the ENTD Target uid mirror from
     the LIVE_LEDGER taunt rows) lives INSIDE this window at combat +0x18C..+0x1A8, so a hit
     there is ONE field, not two corroborating lanes.
  2. The battle cursor globals (FFTHandsFree docs/BATTLE_COORDINATES.md, PRE-1.5 addresses
     0x140C64A54 / 0x140C6496C, UNVERIFIED on current patch): during targeting the hovered
     panel; at confirm the aimed panel. Even if the fixed globals moved, a cursor-shaped
     (x, y) pair inside the scratch window serves the same role.
A hit = knockback_probe's dest-tile moment (+0x8C/8D/8E) but for casts: the input the engine
wrote BEFORE anything visible happened.

RUN RECIPE (owner night, ~5 minutes in any throwaway battle). The game never shows tile
coordinates, so anchor every aim on the unit map: run
`python tools\\probes\\knockback_probe.py table` first; it prints each unit's (x, y), and an
aim "two tiles east of the archer at (3,5)" is tile (5,5). The DELTA protocol below works
even if the absolute guess is off by one:
  1. python tools\\probes\\tilecast_probe.py cursor 20
     Hover the battle cursor around DURING those seconds; if the pre-1.5 globals still track,
     their values march with the cursor and lane 2 is alive on this patch.
  2. python tools\\probes\\tilecast_probe.py watch <casterSlot> 45
     (<casterSlot> from the knockback table; watch warns if the slot looks unseated.)
     In-game: pick a mage, choose a tile-aimed ability (any Black Magick panel aim), aim at
     an empty tile anchored off the unit map, confirm, let it resolve.
  3. Repeat aiming exactly TWO TILES AWAY along one axis. The decisive test is the DELTA:
     a real panel field differs between the two tapes by exactly 2 on one byte and 0 on its
     partner; anything else is coincidence. (Absolute (x, y) matching is the bonus check.)
  4. Contrast tape: one unit-aimed action from the same caster (does the panel field zero,
     or carry the victim's tile?).
Tape lands in %TEMP% as tilecast_<verb>_<stamp>.jsonl; every change also prints live.

    python tools\\probes\\tilecast_probe.py cursor [secs]
    python tools\\probes\\tilecast_probe.py watch <slot> [secs]
    python tools\\probes\\tilecast_probe.py sweep [secs]      # all seated slots' windows
"""
import json
import os
import pathlib
import sys
import time

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
from battle_cheats import rpm, _require_game

UNITS = 0x141853CE0
STRIDE = 0x200
BAND_OFF = 0x1C              # band entry = combat struct + 0x1C (offset rebase rule)
SCRATCH = (0x180, 0x1E0)     # briefed decision block +0x180..+0x1D7, padded to 0x1E0; the
                             # band target mirror (combat +0x18C..+0x1A8) sits INSIDE this
                             # window, so it is deliberately NOT a second window (one field
                             # must not tape twice under two names)
CURSOR_CANDIDATES = [        # pre-1.5 FFTHandsFree cursor grid globals; verify before trusting
    ("cursorA", 0x140C64A54),
    ("cursorB", 0x140C6496C),
]
HZ = 0.008                   # target ~125 samples/sec; Windows sleep granularity makes the
                             # REAL rate lower, so run_tape reports the achieved Hz at exit


def tape_path(verb):
    stamp = time.strftime("%Y%m%d_%H%M%S")
    return pathlib.Path(os.environ.get("TEMP", ".")) / f"tilecast_{verb}_{stamp}.jsonl"


def seated_slots():
    """Slots whose struct looks alive (level byte sane); read-only walk of all 21."""
    out = []
    for slot in range(21):
        b = rpm(UNITS + slot * STRIDE + 0x29, 1)
        if b and 1 <= b[0] <= 99:
            out.append(slot)
    return out


def windows_for(slot):
    base = UNITS + slot * STRIDE
    return [(f"s{slot}+scratch", base + SCRATCH[0], SCRATCH[1] - SCRATCH[0])]


def cursor_windows():
    return [(name, addr, 8) for name, addr in CURSOR_CANDIDATES]


def run_tape(windows, secs, verb):
    _require_game()
    out = tape_path(verb)
    last = {}
    t0 = time.time()
    n = sweeps = 0
    with out.open("w", encoding="utf-8") as fh:
        fh.write(json.dumps({"verb": verb, "windows": [(w[0], hex(w[1]), w[2]) for w in windows],
                             "t0": t0}) + "\n")
        print(f"taping {len(windows)} windows for {secs}s -> {out}")
        print("changes only; columns: t(s) window offset old->new   (Ctrl+C ends the tape early)")
        try:
            while time.time() - t0 < secs:
                burst = False
                for name, addr, size in windows:
                    cur = rpm(addr, size)
                    if cur is None:
                        continue
                    prev = last.get(name)
                    if prev is not None and cur != prev:
                        t = round(time.time() - t0, 3)
                        for i, (a, b) in enumerate(zip(prev, cur)):
                            if a != b:
                                rec = {"t": t, "w": name, "off": i, "old": a, "new": b}
                                fh.write(json.dumps(rec) + "\n")
                                print(f"{t:8.3f}  {name:<14} +0x{i:02X}  0x{a:02X}->0x{b:02X}")
                                n += 1
                        burst = True
                    last[name] = cur
                if burst:
                    fh.flush()   # a Ctrl+C or a game crash must not eat the decisive lines
                sweeps += 1
                time.sleep(HZ)
        except KeyboardInterrupt:
            print("(tape ended early by Ctrl+C)")
    elapsed = max(time.time() - t0, 1e-6)
    print(f"done: {n} byte-changes on tape ({out.name}); {sweeps} sweeps ~{sweeps / elapsed:.0f}Hz achieved")
    print("ANALYSIS: the decisive test is the DELTA between the two aim runs (exactly the tile")
    print("distance on one byte, 0 on its partner); absolute (x, y) match is the bonus check.")


def main(argv):
    if len(argv) < 1:
        print(__doc__)
        return 2
    verb = argv[0]
    if verb == "cursor":
        secs = int(argv[1]) if len(argv) > 1 else 20
        run_tape(cursor_windows(), secs, "cursor")
    elif verb == "watch":
        if len(argv) < 2:
            print("watch needs <slot>")
            return 2
        slot = int(argv[1])
        secs = int(argv[2]) if len(argv) > 2 else 45
        seated = seated_slots()
        if slot not in seated:
            print(f"WARNING: slot {slot} does not look seated (seated now: {seated}); "
                  "taping anyway, but a typo'd slot records plausible junk.")
        run_tape(windows_for(slot) + cursor_windows(), secs, "watch")
    elif verb == "sweep":
        secs = int(argv[1]) if len(argv) > 1 else 45
        seated = seated_slots()
        if not seated:
            print("no seated slots found; refusing a cursor-only sweep (is a battle up?)")
            return 2
        print(f"sweeping seated slots: {seated}")
        wins = []
        for slot in seated:
            wins += windows_for(slot)
        run_tape(wins + cursor_windows(), secs, "sweep")
    else:
        print(__doc__)
        return 2
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
