#!/usr/bin/env python
"""
Kit-stamp probe (WRITE -- reversible). Overwrite ONE JobCommand record so it holds a single
distinctive out-of-class ability, to answer the gating multiplayer question:

    Does a PUPPETED ENEMY render + cast a command from a JobCommand record overwrite ALONE,
    with NO roster learned-bit? (Enemies have no roster entry, so this probe writes ZERO learned
    bits -- if the enemy can still see + cast the injected ability, the record overwrite suffices
    and the enemy kit-stamp is unblocked.)

Base CONFIRMED live by jobcommand_find_probe.py = 0x14067E213 (1.5, delta +0x5080). The record is
per-JOB and GLOBAL (BOTH teams' units of that job read it) -- so this ALSO measures the global
side effect: a party unit of the same job will see the injected ability too (sub-probe #2).

REVERSIBLE: saves the original 25 bytes to %TEMP% and restores on `restore`; a game restart reverts
regardless. Only the 16 ability bytes + the 2 ExtAb bytes are touched; ExtRSM + the 6 RSM bytes
(reactions/supports) are left alone. Guarded by the OS (RPM/WPM on our handle) -- a bad rec id reads
back nothing and aborts before writing.

Record map (from the locator dump, normal executors only):
    7 Knight (Arts of War) | 9 Monk (Martial Arts) | 11 Black Mage (Black Magick)
    14 Thief (Steal -- the Barrage-PROVEN host) | 16 Mystic | 19 Samurai (Iaido) | 22 Bard (Sing)
AVOID special-executor jobs (they swallow foreign ids): 6 Items, 8 Aim, 18 Jump, 20 Throw, 21 Arith.

USAGE (game running, in a battle, with a puppetable enemy of the target job):
    python kit_stamp_probe.py show    <rec>            # read-only dump of a record
    python kit_stamp_probe.py inject  <rec> [abilId]   # save + overwrite to one ability (default 148 Throw Stone)
    python kit_stamp_probe.py restore <rec>            # restore the saved original
"""
import os
import sys
import tempfile

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ct_probe import PROC, PV, PV_W, find_pid, k32, rd, wr

BASE = 0x14067E213          # CONFIRMED 1.5 JobCommand table base (rec0 ability1)
REC = 25                    # bytes per record: [ExtAb b0, b1][ExtRSM][ab x16][rsm x6]
DEFAULT_ABILITY = 148       # Throw Stone -- no MP, simple ranged physical, unmistakable out of class


def flag_addr(rec):
    return BASE + rec * REC - 3        # start of the 3-byte flag prefix


def ab_addr(rec):
    return BASE + rec * REC            # start of the 16 ability bytes


def save_path(rec):
    return os.path.join(tempfile.gettempdir(), f"kit_stamp_rec{rec}.bin")


def decode(buf):
    """(extAb, [ability ids]) from a 25-byte record buffer."""
    ext = buf[0] | (buf[1] << 8)
    ids = []
    for i in range(16):
        bit = (7 - i % 8) + 8 * (i // 8)          # MSB-first per byte (proven layout)
        b = buf[3 + i]
        if b or (ext & (1 << bit)):
            ids.append(b + (256 if ext & (1 << bit) else 0))
    return ext, ids


def cmd_show(h, rec):
    buf = rd(h, flag_addr(rec), REC)
    if not buf:
        sys.exit("record unreadable -- wrong rec, or the table is not built yet (past the title?)")
    ext, ids = decode(buf)
    print(f"rec {rec} extAb={ext:04X} abilities: {ids}")


def cmd_inject(h, rec, abil):
    orig = rd(h, flag_addr(rec), REC)
    if not orig:
        sys.exit("record unreadable -- aborting before any write")
    with open(save_path(rec), "wb") as f:
        f.write(orig)
    print("saved original:", " ".join(f"{v:02X}" for v in orig))

    slots = bytearray(16)
    slots[0] = abil & 0xFF                                       # slot 1 = the test ability
    wr(h, ab_addr(rec), bytes(slots))
    ext = bytes([0x80, 0x00]) if abil >= 256 else bytes([0x00, 0x00])   # slot 1 extend bit only if id>=256
    wr(h, flag_addr(rec), ext)                                   # ExtAb (2 bytes) only; ExtRSM + RSM untouched

    after = rd(h, flag_addr(rec), REC)
    print("after          :", " ".join(f"{v:02X}" for v in after))
    print(f"rec {rec} is now a single-ability command: {abil}.")
    print("-> Puppet an enemy of this job, open its command on its turn, and look for the ability.")
    print(f"-> Restore with: python kit_stamp_probe.py restore {rec}")


def cmd_restore(h, rec):
    path = save_path(rec)
    if not os.path.exists(path):
        sys.exit(f"no saved original at {path} (already restored, or never injected this rec)")
    with open(path, "rb") as f:
        saved = f.read()
    wr(h, flag_addr(rec), saved)
    print(f"rec {rec} restored from {path}:", " ".join(f"{v:02X}" for v in saved))


def main():
    if len(sys.argv) < 3:
        sys.exit(__doc__)
    op = sys.argv[1]
    rec = int(sys.argv[2])
    pid = find_pid(PROC)
    if not pid:
        sys.exit(f"{PROC} not running")
    perm = PV if op == "show" else PV_W
    h = k32.OpenProcess(perm, False, pid)
    try:
        if op == "show":
            cmd_show(h, rec)
        elif op == "inject":
            abil = int(sys.argv[3]) if len(sys.argv) > 3 else DEFAULT_ABILITY
            cmd_inject(h, rec, abil)
        elif op == "restore":
            cmd_restore(h, rec)
        else:
            sys.exit("op must be one of: show | inject | restore")
    finally:
        k32.CloseHandle(h)


if __name__ == "__main__":
    main()
