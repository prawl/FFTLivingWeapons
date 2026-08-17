#!/usr/bin/env python
"""
Twin-less construction probe (LW-193 premise P-B). THE decisive question for the consent
rewrite's menu-evaporation design: if a battle is CONSTRUCTED while the roster off-hand is
EMPTY, does the wielder still dual-fire once the mod's in-battle re-assert stamps the twin
back into the roster row? The 2026-07-04 history says the in-battle re-assert is what made
the twin hold through combat at all (so roster reads feed the dual-fire), but the from-empty
construction case has never been isolated: construction may latch a per-unit weapon copy
into the combat struct that a later roster stamp cannot reach.

Pre-registered outcomes (owner counts Attack shots in the drill battle):
  TWO shots  -> P-B TRUE: post-construction roster stamps drive the dual-fire; the menu
                evaporation design is safe EVERYWHERE, formation screen included.
  ONE shot   -> P-B FALSE: the dual-fire is construction-bound; the fix design must keep
                the twin present at formation/battle construction and evaporate only in
                world-map menus.

Verb `hold`: fights the mod's ~1s re-stamp with a 50ms clear so the roster off-hand reads
EMPTY at construction. WRITE DISCIPLINE (the probe's only write): u16 EMPTY sentinel 255
into Ramza's roster off-hand (nameId 1), written ONLY when the slot currently reads a
gun-slinger twin id (71 or 79), verified by read-back; nothing else is ever written, and
nothing needs restoring (the mod re-stamps its own twin, and the pre-hold off-hand was the
EMPTY sentinel by construction of the drill). The hold releases itself the moment
battleMode goes nonzero (battle loading), then watches read-only for 90s so the tape shows
the mod's re-stamp landing plus the menu-signal byte 0x140d508d0 through formation/battle.

Usage:  python -u tools/probes/twinless_probe.py hold
Addresses come from Offsets.cs via tools/lib/offsets.py; the menu-signal candidate byte is
this session's wide-solve winner (not yet an Offsets constant; promoted with the fix arc).
"""
import ctypes
import ctypes.wintypes as W
import datetime
import os
import pathlib
import struct
import sys
import time

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1]))
from lib import offsets as _offsets

O = _offsets.load()
(ROSTER_BASE, ROSTER_STRIDE, ROSTER_SLOTS, R_OFFHAND, R_RHAND, R_SUPPORT, R_LEVEL,
 R_NAMEID, BATTLE_MODE) = _offsets.require(
    ["RosterBase", "RosterStride", "RosterSlots", "ROffHand", "RRHand", "RSupport",
     "RLevel", "RNameId", "BattleMode"], O)

TWIN_IDS = (71, 79)          # Outrider Pistol / Arbalest (gunSlinger-flagged)
EMPTY = 255                  # roster off-hand EMPTY sentinel (GunSlingerPolicy)
MENU_BYTE = 0x140D508D0      # menu-open candidate, this session's wide-solve winner
RAMZA_NAMEID = 1

PROCESS_ALL = 0x0010 | 0x0020 | 0x0008 | 0x0400   # VM_READ | VM_WRITE | VM_OPERATION | QUERY
k32 = ctypes.windll.kernel32
psapi = ctypes.windll.psapi


def open_game():
    arr = (W.DWORD * 4096)()
    needed = W.DWORD()
    psapi.EnumProcesses(ctypes.byref(arr), ctypes.sizeof(arr), ctypes.byref(needed))
    for i in range(needed.value // ctypes.sizeof(W.DWORD)):
        h = k32.OpenProcess(PROCESS_ALL, False, arr[i])
        if not h:
            continue
        buf = ctypes.create_unicode_buffer(260)
        if psapi.GetModuleBaseNameW(h, None, buf, 260) and buf.value.lower() == "fft_enhanced.exe":
            return h
        k32.CloseHandle(h)
    return None


H = None


def rpm(addr, n):
    buf = ctypes.create_string_buffer(n)
    got = ctypes.c_size_t()
    if not k32.ReadProcessMemory(H, ctypes.c_void_p(addr), buf, n, ctypes.byref(got)) or got.value != n:
        return None
    return buf.raw


def wpm_u16(addr, value):
    buf = struct.pack("<H", value)
    done = ctypes.c_size_t()
    return bool(k32.WriteProcessMemory(H, ctypes.c_void_p(addr), buf, 2, ctypes.byref(done))) and done.value == 2


def u16(data, off=0):
    return struct.unpack_from("<H", data, off)[0]


def now():
    return datetime.datetime.now().strftime("%H:%M:%S.%f")[:-3]


def find_ramza():
    for s in range(ROSTER_SLOTS):
        b = ROSTER_BASE + s * ROSTER_STRIDE
        d = rpm(b, R_NAMEID + 2)
        if d is None:
            continue
        if 1 <= d[R_LEVEL] <= 99 and u16(d, R_NAMEID) == RAMZA_NAMEID:
            return s, b
    return None, None


def verb_hold():
    global H
    H = open_game()
    if not H:
        print("game not running")
        sys.exit(1)
    slot, b = find_ramza()
    if b is None:
        print("Ramza (nameId 1) not found in the roster")
        sys.exit(1)
    tapes = pathlib.Path(__file__).resolve().parent / "tapes"
    tapes.mkdir(exist_ok=True)
    path = tapes / f"lw193_twinless_{time.strftime('%Y%m%d_%H%M%S')}.log"
    tape = open(path, "w", encoding="utf-8")

    def emit(line):
        print(line, flush=True)
        tape.write(line + "\n")
        tape.flush()

    rh = u16(rpm(b + R_RHAND, 2))
    emit(f"# twinless hold: Ramza roster slot {slot}, rh={rh}, holding off-hand EMPTY until battleMode != 0")
    if rh not in TWIN_IDS:
        emit(f"# WARNING: main hand reads {rh}, not a twin weapon (71/79); the drill wants the gun equipped FIRST")
    clears = 0
    last_oh = None
    last_menu = None
    try:
        while True:
            mode = rpm(BATTLE_MODE, 1)[0]
            oh = u16(rpm(b + R_OFFHAND, 2))
            menu = rpm(MENU_BYTE, 1)[0]
            if menu != last_menu:
                emit(f"{now()} menu-byte 0x140d508d0: {last_menu} -> {menu}")
                last_menu = menu
            if oh != last_oh:
                emit(f"{now()} off-hand: {last_oh} -> {oh} (mode={mode})")
                last_oh = oh
            if mode != 0:
                emit(f"{now()} battleMode -> {mode}: HOLD RELEASED after {clears} clears; watching read-only")
                break
            if oh in TWIN_IDS:
                if wpm_u16(b + R_OFFHAND, EMPTY):
                    back = u16(rpm(b + R_OFFHAND, 2))
                    clears += 1
                    if back != EMPTY:
                        emit(f"{now()} clear did not stick (read-back {back})")
                else:
                    emit(f"{now()} WRITE FAILED on off-hand clear")
            time.sleep(0.05)

        deadline = time.time() + 90
        while time.time() < deadline:
            mode = rpm(BATTLE_MODE, 1)[0]
            oh = u16(rpm(b + R_OFFHAND, 2))
            supp = u16(rpm(b + R_SUPPORT, 2))
            menu = rpm(MENU_BYTE, 1)[0]
            if menu != last_menu:
                emit(f"{now()} menu-byte 0x140d508d0: {last_menu} -> {menu} (mode={mode})")
                last_menu = menu
            if oh != last_oh:
                emit(f"{now()} off-hand: {last_oh} -> {oh} (mode={mode}, supp={supp})")
                last_oh = oh
            time.sleep(0.05)
        emit(f"{now()} watch window over")
    except KeyboardInterrupt:
        emit(f"{now()} stopped by hand after {clears} clears")
    finally:
        tape.close()
        print(f"tape: {path}")


def verb_hold2():
    """Corrected release trigger (run 1 was confounded): battleMode flips at the FORMATION
    phase, ~40s before units construct, so releasing there let the mod stamp the twin long
    before construction. hold2 keeps clearing until CONSTRUCTION ITSELF is visible: two or
    more plausible unit seats in the battle band (hp/level sane), the same plausibility rule
    nameid_unique_probe's battle verb uses. Only then does it release and watch."""
    global H
    import struct as _s
    (COMBAT_ANCHOR, BAND_ENTRY, STRIDE, BAND_SLOTS, A_LEVEL, A_HP, A_MAXHP) = _offsets.require(
        ["CombatAnchor", "BandEntry", "CombatStride", "BandSlots", "ALevel", "AHp", "AMaxHp"], O)
    band_base = COMBAT_ANCHOR + BAND_ENTRY - 24 * STRIDE   # == Offsets.BandReadBase

    def plausible_seats():
        n = 0
        for s in range(BAND_SLOTS):
            d = rpm(band_base + s * STRIDE, A_MAXHP + 2)
            if d is None:
                continue
            lvl = d[A_LEVEL]
            mhp = _s.unpack_from("<H", d, A_MAXHP)[0]
            if 1 <= lvl <= 99 and 0 < mhp < 2000:
                n += 1
        return n

    H = open_game()
    if not H:
        print("game not running")
        sys.exit(1)
    slot, b = find_ramza()
    if b is None:
        print("Ramza (nameId 1) not found in the roster")
        sys.exit(1)
    tapes = pathlib.Path(__file__).resolve().parent / "tapes"
    tapes.mkdir(exist_ok=True)
    path = tapes / f"lw193_twinless2_{time.strftime('%Y%m%d_%H%M%S')}.log"
    tape = open(path, "w", encoding="utf-8")

    def emit(line):
        print(line, flush=True)
        tape.write(line + "\n")
        tape.flush()

    rh = u16(rpm(b + R_RHAND, 2))
    emit(f"# twinless hold2: Ramza roster slot {slot}, rh={rh}; clearing until >=2 plausible band seats")
    clears = 0
    last_oh = None
    last_mode = None
    last_seats = -1
    try:
        while True:
            mode = rpm(BATTLE_MODE, 1)[0]
            oh = u16(rpm(b + R_OFFHAND, 2))
            if mode != last_mode:
                emit(f"{now()} battleMode: {last_mode} -> {mode}")
                last_mode = mode
            if oh != last_oh:
                emit(f"{now()} off-hand: {last_oh} -> {oh} (mode={mode})")
                last_oh = oh
            seats = plausible_seats()
            if seats != last_seats:
                emit(f"{now()} plausible band seats: {last_seats} -> {seats}")
                last_seats = seats
            if seats >= 2:
                emit(f"{now()} CONSTRUCTION SEEN ({seats} seats): HOLD RELEASED after {clears} clears; watching read-only")
                break
            if oh in TWIN_IDS:
                if wpm_u16(b + R_OFFHAND, EMPTY):
                    clears += 1
                else:
                    emit(f"{now()} WRITE FAILED on off-hand clear")
            time.sleep(0.05)

        deadline = time.time() + 120
        while time.time() < deadline:
            mode = rpm(BATTLE_MODE, 1)[0]
            oh = u16(rpm(b + R_OFFHAND, 2))
            supp = u16(rpm(b + R_SUPPORT, 2))
            if oh != last_oh:
                emit(f"{now()} off-hand: {last_oh} -> {oh} (mode={mode}, supp={supp})")
                last_oh = oh
            time.sleep(0.05)
        emit(f"{now()} watch window over")
    except KeyboardInterrupt:
        emit(f"{now()} stopped by hand after {clears} clears")
    finally:
        tape.close()
        print(f"tape: {path}")


def verb_hold3():
    """Third trigger, correct by construction: from the world map the battle band is FROZEN
    residue of the last battle (run 2's trap: 17 stale seats read as 'constructed' at t=0),
    so construction is detectable as the band being REWRITTEN wholesale. Baseline the whole
    band region at start; release when >5 percent of its bytes differ from that baseline."""
    global H
    (COMBAT_ANCHOR, BAND_ENTRY, STRIDE, BAND_SLOTS) = _offsets.require(
        ["CombatAnchor", "BandEntry", "CombatStride", "BandSlots"], O)
    band_base = COMBAT_ANCHOR + BAND_ENTRY - 24 * STRIDE
    band_size = BAND_SLOTS * STRIDE

    H = open_game()
    if not H:
        print("game not running")
        sys.exit(1)
    slot, b = find_ramza()
    if b is None:
        print("Ramza (nameId 1) not found in the roster")
        sys.exit(1)
    tapes = pathlib.Path(__file__).resolve().parent / "tapes"
    tapes.mkdir(exist_ok=True)
    path = tapes / f"lw193_twinless3_{time.strftime('%Y%m%d_%H%M%S')}.log"
    tape = open(path, "w", encoding="utf-8")

    def emit(line):
        print(line, flush=True)
        tape.write(line + "\n")
        tape.flush()

    baseline = rpm(band_base, band_size)
    if baseline is None:
        emit("band region unreadable; cannot baseline")
        sys.exit(1)
    threshold = band_size // 20   # 5 percent
    rh = u16(rpm(b + R_RHAND, 2))
    emit(f"# twinless hold3: Ramza roster slot {slot}, rh={rh}; band baseline {band_size} bytes, "
         f"release when >{threshold} bytes change (construction rewrite)")
    clears = 0
    last_oh = None
    last_mode = None
    try:
        while True:
            mode = rpm(BATTLE_MODE, 1)[0]
            oh = u16(rpm(b + R_OFFHAND, 2))
            if mode != last_mode:
                emit(f"{now()} battleMode: {last_mode} -> {mode}")
                last_mode = mode
            if oh != last_oh:
                emit(f"{now()} off-hand: {last_oh} -> {oh} (mode={mode}, clears={clears})")
                last_oh = oh
            cur = rpm(band_base, band_size)
            if cur is not None:
                diff = sum(1 for x, y in zip(baseline, cur) if x != y)
                if diff > threshold:
                    emit(f"{now()} CONSTRUCTION REWRITE SEEN ({diff} band bytes changed, mode={mode}): "
                         f"HOLD RELEASED after {clears} clears; watching read-only")
                    break
            if oh in TWIN_IDS:
                if wpm_u16(b + R_OFFHAND, EMPTY):
                    clears += 1
                else:
                    emit(f"{now()} WRITE FAILED on off-hand clear")
            time.sleep(0.05)

        deadline = time.time() + 150
        while time.time() < deadline:
            mode = rpm(BATTLE_MODE, 1)[0]
            oh = u16(rpm(b + R_OFFHAND, 2))
            supp = u16(rpm(b + R_SUPPORT, 2))
            if oh != last_oh:
                emit(f"{now()} off-hand: {last_oh} -> {oh} (mode={mode}, supp={supp})")
                last_oh = oh
            time.sleep(0.05)
        emit(f"{now()} watch window over")
    except KeyboardInterrupt:
        emit(f"{now()} stopped by hand after {clears} clears")
    finally:
        tape.close()
        print(f"tape: {path}")


def verb_hold4():
    """Fourth trigger, human-released and therefore unconfoundable: runs 1-3 proved battle
    construction is multi-stage (formation-phase mode flip, staged band rewrites), so every
    automatic release fired before the PLAYER units actually built and the mod's stamp won
    the race each time. hold4 keeps the off-hand empty until the OWNER, looking at the
    visible battlefield, confirms the units exist; the orchestrator then creates the release
    marker file and the hold lets go. Whatever the mod stamps after that lands on a unit
    that was constructed twin-less by direct observation."""
    global H
    marker = pathlib.Path(os.environ.get("TEMP", ".")) / "lw193_release_hold"
    if marker.exists():
        marker.unlink()
    H = open_game()
    if not H:
        print("game not running")
        sys.exit(1)
    slot, b = find_ramza()
    if b is None:
        print("Ramza (nameId 1) not found in the roster")
        sys.exit(1)
    tapes = pathlib.Path(__file__).resolve().parent / "tapes"
    tapes.mkdir(exist_ok=True)
    path = tapes / f"lw193_twinless4_{time.strftime('%Y%m%d_%H%M%S')}.log"
    tape = open(path, "w", encoding="utf-8")

    def emit(line):
        print(line, flush=True)
        tape.write(line + "\n")
        tape.flush()

    rh = u16(rpm(b + R_RHAND, 2))
    emit(f"# twinless hold4: Ramza roster slot {slot}, rh={rh}; clearing until release marker {marker}")
    clears = 0
    last_oh = None
    last_mode = None
    try:
        while not marker.exists():
            mode = rpm(BATTLE_MODE, 1)[0]
            oh = u16(rpm(b + R_OFFHAND, 2))
            if mode != last_mode:
                emit(f"{now()} battleMode: {last_mode} -> {mode}")
                last_mode = mode
            if oh != last_oh:
                emit(f"{now()} off-hand: {last_oh} -> {oh} (mode={mode}, clears={clears})")
                last_oh = oh
            if oh in TWIN_IDS:
                if wpm_u16(b + R_OFFHAND, EMPTY):
                    clears += 1
                else:
                    emit(f"{now()} WRITE FAILED on off-hand clear")
            time.sleep(0.05)
        emit(f"{now()} RELEASE MARKER SEEN: hold released after {clears} clears; watching read-only")

        deadline = time.time() + 180
        while time.time() < deadline:
            mode = rpm(BATTLE_MODE, 1)[0]
            oh = u16(rpm(b + R_OFFHAND, 2))
            supp = u16(rpm(b + R_SUPPORT, 2))
            if oh != last_oh:
                emit(f"{now()} off-hand: {last_oh} -> {oh} (mode={mode}, supp={supp})")
                last_oh = oh
            time.sleep(0.05)
        emit(f"{now()} watch window over")
    except KeyboardInterrupt:
        emit(f"{now()} stopped by hand after {clears} clears")
    finally:
        tape.close()
        print(f"tape: {path}")


def main():
    if len(sys.argv) > 1 and sys.argv[1] == "hold":
        verb_hold()
    elif len(sys.argv) > 1 and sys.argv[1] == "hold2":
        verb_hold2()
    elif len(sys.argv) > 1 and sys.argv[1] == "hold3":
        verb_hold3()
    elif len(sys.argv) > 1 and sys.argv[1] == "hold4":
        verb_hold4()
    else:
        print(__doc__)
        sys.exit(2)


if __name__ == "__main__":
    main()
