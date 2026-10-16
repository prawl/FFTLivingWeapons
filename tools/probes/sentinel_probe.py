"""Dump the battle-state sentinels Engine reads every tick, from the live game.

LW-41: addresses come from LivingWeapon/Offsets.cs via tools/lib/offsets.py. The original
hardcoded the pre-1.5 copies and fed garbage sentinels (battleMode=0, slot9=0x1) into the
LW-40 live incident, nearly misdirecting the diagnosis.

LW-389 / LW-395 (2026-10-16): the Denuvo-free build is ASLR-relocated, so every pin is
rebased onto the live module base through tools/lib/livebase.py. The new --watch mode samples
the six sentinels at 20 Hz and logs one line per CHANGE (elapsed seconds plus every value) to
stdout and to tools/probes/tapes/sentinel_watch_<stamp>.log; it is the LW-395 premise
instrument for the out-of-battle value sets (title screen, world map, Orbonne opener).

Usage:
  python tools/probes/sentinel_probe.py                 # game running: dump the sentinels once
  python tools/probes/sentinel_probe.py --watch [secs]  # log every change for secs (default 900)
  python tools/probes/sentinel_probe.py --selftest      # no game: parser + Offsets.cs shape check
"""
import struct
import sys
import time
from datetime import datetime
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from lib import offsets

if "--selftest" in sys.argv:
    offsets.selftest()
    sys.exit(0)

from lib.livebase import open_game, rebase
import ctypes

NAMES = ["Slot0", "Slot9", "BattleMode", "EventId", "PauseFlag", "SubmenuFlag"]
PREFERRED = offsets.require(NAMES)

def _open_waiting(timeout_s):
    """--watch may be started before the game: poll for it so the tape catches the title
    screen from the very first readable tick."""
    t_end = time.time() + timeout_s
    while True:
        try:
            return open_game()
        except SystemExit:
            if time.time() >= t_end:
                raise
            time.sleep(2.0)


pid, h, base = _open_waiting(600.0 if "--watch" in sys.argv else 0.0)
k32 = ctypes.windll.kernel32
SLOT0, SLOT9, BATTLE_MODE, EVENT_ID, PAUSE, SUBMENU = [rebase(a, base) for a in PREFERRED]


def rpm(addr, n):
    buf = ctypes.create_string_buffer(n)
    got = ctypes.c_size_t()
    if not k32.ReadProcessMemory(h, ctypes.c_void_p(addr), buf, n, ctypes.byref(got)) or got.value != n:
        return None
    return buf.raw


def u32(a):
    b = rpm(a, 4); return struct.unpack('<I', b)[0] if b else None
def u16(a):
    b = rpm(a, 2); return struct.unpack('<H', b)[0] if b else None
def u8(a):
    b = rpm(a, 1); return b[0] if b else None


def fmt(v, hexed=False):
    if v is None: return "unreadable"
    return f"{v:#x}" if hexed else str(v)


def sample():
    return (u32(SLOT0), u32(SLOT9), u8(BATTLE_MODE), u16(EVENT_ID), u8(PAUSE), u8(SUBMENU))


def line(t0, s):
    slot0, slot9, mode, ev, pause, sub = s
    return (f"t={time.time() - t0:8.2f}s slot0={fmt(slot0, True)} slot9={fmt(slot9, True)} "
            f"mode={fmt(mode)} event={fmt(ev)} pause={fmt(pause)} submenu={fmt(sub)}")


print(f"pid={pid} base={base:#x} (addresses from Offsets.cs, rebased onto the live image)")

if "--watch" in sys.argv:
    i = sys.argv.index("--watch")
    secs = float(sys.argv[i + 1]) if len(sys.argv) > i + 1 and not sys.argv[i + 1].startswith("-") else 900.0
    tapes = Path(__file__).resolve().parent / "tapes"
    tapes.mkdir(exist_ok=True)
    out = tapes / f"sentinel_watch_{datetime.now():%Y%m%d_%H%M%S}.log"
    t0 = time.time()
    last = None
    with out.open("w", encoding="utf-8") as f:
        hdr = f"# sentinel watch pid={pid} base={base:#x} started {datetime.now():%Y-%m-%d %H:%M:%S} for {secs:.0f}s"
        print(hdr); f.write(hdr + "\n")
        while time.time() - t0 < secs:
            s = sample()
            if s != last:
                l = line(t0, s)
                print(l, flush=True); f.write(l + "\n"); f.flush()
                last = s
            time.sleep(0.05)
    print(f"tape: {out}")
    sys.exit(0)

s = sample()
print(f"slot0      = {fmt(s[0], hexed=True)}   @ {SLOT0:#x}")
print(f"slot9      = {fmt(s[1], hexed=True)}   @ {SLOT9:#x}")
print(f"battleMode = {fmt(s[2])}   @ {BATTLE_MODE:#x}")
print(f"eventId    = {fmt(s[3])}   @ {EVENT_ID:#x}")
print(f"pauseFlag  = {fmt(s[4])}   @ {PAUSE:#x}")
print(f"submenu    = {fmt(s[5])}   @ {SUBMENU:#x}")
