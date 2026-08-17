#!/usr/bin/env python
"""
Off-hand / shield gear-loss probe (READ-ONLY). Premise check for LW-193 (a Crossfire or
Gun Slinger twin grant permanently deletes the wielder's off-hand item, a shield included;
owner hit it live 2026-08-13) and its sibling LW-194 (the re-assert overwrites whatever the
player equips). Nothing here writes game memory.

Background fact this probe exists to exploit: the roster row has FOUR hand-ish equip fields
(Offsets.cs): RRHand +0x14 (main weapon), RLHand +0x16 (live-unused), ROffHand +0x18 (the
dual-wield second WEAPON -- the only field the twin grant snapshots and writes), and
RShield +0x1A (a SEPARATE shield slot the mod never reads). The live gunslinger.json on this
box (2026-08-14) holds origOff 255 (EMPTY) for every unit that ever received a twin, so no
shield id was ever captured -- consistent with the shield living at +0x1A, invisible to the
snapshot, and being destroyed by the game itself when it normalizes the illegal
two-weapons-plus-shield hand. That is hypothesis H1. H2 is the older reading: the shield sat
at +0x18 and the re-assert Write branch stamps the twin over a player-re-equipped item
without ever snapshotting it.

Falsifiable premises, one drill:
  P1 "A shield equipped alongside a flagged gun lands at +0x1A, not +0x18."
     -> watch which field changes when the owner equips the shield.
  P2 "After the twin stamp, some GAME edge (equip-menu open, battle construction, or the
      battle-end gear commit) clears +0x1A WITHOUT incrementing the shield's inventory
      count." PASS = the vaporize edge is caught on tape with its timestamp; the loss is
      the game's normalize, not a mod write (the mod never touches +0x1A).
     -> FAIL shape A: +0x1A survives everything and the sack count is intact after release;
        then the reported loss needs the H2 wrestle -- step 6 of the drill provokes it.
     -> FAIL shape B: the shield was at +0x18 all along (P1 false): H2 confirmed directly;
        the tape shows which write ate it (a mod re-assert stamps 71/79 over it within ~1s;
        a game normalize clears it to 255/0).
  P3 "No copy of the lost item survives anywhere": the sack count for the shield id never
     rises across the whole drill. Any +1 anywhere is a recovery lane worth naming.

Output: every change is printed AND appended to tools/probes/tapes/lw193_watch_<ts>.log so
the evidence survives the console (QuickEdit trap: never click the console window).

Addresses come from LivingWeapon/Offsets.cs via tools/lib/offsets.py (never hardcoded).
Usage:  python -u tools/probes/offhand_shield_probe.py snap  [itemId ...]
        python -u tools/probes/offhand_shield_probe.py watch [itemId ...]
  itemId args add sack-count columns to `snap`; `watch` reports EVERY sack-count change
  (all ids 0..315) regardless, flagging watched ids. Twin ids 71 and 79 are always watched.
"""
import ctypes
import ctypes.wintypes as W
import datetime
import pathlib
import struct
import sys
import time

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1]))
from lib import offsets as _offsets

O = _offsets.load()
(ROSTER_BASE, ROSTER_STRIDE, ROSTER_SLOTS, R_RHAND, R_LHAND, R_OFFHAND, R_SHIELD,
 R_ACCESSORY, R_SUPPORT, R_LEVEL, R_NAMEID, INV_BASE) = _offsets.require(
    ["RosterBase", "RosterStride", "RosterSlots", "RRHand", "RLHand", "ROffHand", "RShield",
     "RAccessory", "RSupport", "RLevel", "RNameId", "InventoryCountBase"], O)

MAX_ITEM_ID = 315          # GunSlingerPolicy.MaxItemId; sack array read as 0..315 inclusive
ALWAYS_WATCH = {71, 79}    # Outrider Pistol (Gun Slinger) / Arbalest (Crossfire) twin ids
FIELDS = [("rh", R_RHAND), ("lh", R_LHAND), ("oh", R_OFFHAND), ("sh", R_SHIELD),
          ("acc", R_ACCESSORY), ("supp", R_SUPPORT)]

PROCESS_VM_READ = 0x0010
PROCESS_QUERY_INFORMATION = 0x0400
k32 = ctypes.windll.kernel32
psapi = ctypes.windll.psapi


def open_game():
    arr = (W.DWORD * 4096)()
    needed = W.DWORD()
    psapi.EnumProcesses(ctypes.byref(arr), ctypes.sizeof(arr), ctypes.byref(needed))
    for i in range(needed.value // ctypes.sizeof(W.DWORD)):
        h = k32.OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, False, arr[i])
        if not h:
            continue
        buf = ctypes.create_unicode_buffer(260)
        if psapi.GetModuleBaseNameW(h, None, buf, 260) and buf.value.lower() == "fft_enhanced.exe":
            return h
        k32.CloseHandle(h)
    return None


H = open_game()
if not H:
    print("game not running (fft_enhanced.exe not found)")
    sys.exit(1)

TAPE = None  # opened by watch()


def emit(line):
    print(line)
    if TAPE:
        TAPE.write(line + "\n")
        TAPE.flush()


def rpm(addr, n):
    buf = ctypes.create_string_buffer(n)
    got = ctypes.c_size_t()
    if not k32.ReadProcessMemory(H, ctypes.c_void_p(addr), buf, n, ctypes.byref(got)) or got.value != n:
        return None
    return buf.raw


def u16(data, off):
    return struct.unpack_from("<H", data, off)[0]


def now():
    return datetime.datetime.now().strftime("%H:%M:%S.%f")[:-3]


def read_rows():
    """{slot: (nameId, lvl, {field: value})} for rows with level 1..99."""
    rows = {}
    for s in range(ROSTER_SLOTS):
        d = rpm(ROSTER_BASE + s * ROSTER_STRIDE, R_NAMEID + 2)
        if d is None:
            continue
        lvl = d[R_LEVEL]
        if not (1 <= lvl <= 99):
            continue
        rows[s] = (u16(d, R_NAMEID), lvl, {name: u16(d, off) for name, off in FIELDS})
    return rows


def read_sack():
    """[count per item id 0..MAX_ITEM_ID] or None on a failed read."""
    d = rpm(INV_BASE, MAX_ITEM_ID + 1)
    return None if d is None else list(d)


def fmt_row(s, nameId, lvl, f):
    cols = " ".join(f"{k}={v}" for k, v in f.items())
    return f"slot {s:>2} nameId {nameId:>4} lvl {lvl:>3}  {cols}"


def verb_snap(watch_ids):
    rows = read_rows()
    for s in sorted(rows):
        nameId, lvl, f = rows[s]
        print(fmt_row(s, nameId, lvl, f))
    print(f"\noccupied rows: {len(rows)}")
    sack = read_sack()
    if sack is None:
        print("sack read FAILED (InventoryCountBase unreadable)")
        return
    ids = sorted(ALWAYS_WATCH | set(watch_ids))
    print("sack counts: " + "  ".join(f"id {i}={sack[i]}" for i in ids))


def verb_watch(watch_ids):
    global TAPE
    tapes = pathlib.Path(__file__).resolve().parent / "tapes"
    tapes.mkdir(exist_ok=True)
    path = tapes / f"lw193_watch_{datetime.datetime.now():%Y%m%d_%H%M%S}.log"
    TAPE = open(path, "w", encoding="utf-8")
    ids = sorted(ALWAYS_WATCH | set(watch_ids))
    emit(f"# lw193 off-hand/shield watch, started {datetime.datetime.now():%Y-%m-%d %H:%M:%S}")
    emit(f"# watched sack ids (flagged with *): {ids}; every other id 0..{MAX_ITEM_ID} reported too")

    rows = read_rows()
    sack = read_sack()
    for s in sorted(rows):
        nameId, lvl, f = rows[s]
        emit(f"{now()} BASE  {fmt_row(s, nameId, lvl, f)}")
    if sack:
        emit(f"{now()} BASE  sack " + "  ".join(f"id {i}={sack[i]}" for i in ids))
    emit("# watching... (Ctrl+C to stop; do NOT click/select in this console)")

    try:
        while True:
            time.sleep(0.05)
            new_rows = read_rows()
            for s in sorted(set(rows) | set(new_rows)):
                old, new = rows.get(s), new_rows.get(s)
                if old is None:
                    emit(f"{now()} ROW+  {fmt_row(s, *new)}")
                elif new is None:
                    emit(f"{now()} ROW-  slot {s} (row no longer occupied)")
                else:
                    changes = [f"{k}: {old[2][k]} -> {new[2][k]}" for k in old[2] if old[2][k] != new[2][k]]
                    if old[0] != new[0]:
                        changes.insert(0, f"nameId: {old[0]} -> {new[0]}")
                    if changes:
                        emit(f"{now()} EQUIP slot {s:>2} nameId {new[0]:>4}  " + "; ".join(changes))
            rows = new_rows

            new_sack = read_sack()
            if sack is not None and new_sack is not None:
                for i in range(MAX_ITEM_ID + 1):
                    if sack[i] != new_sack[i]:
                        star = "*" if i in ids else " "
                        emit(f"{now()} SACK{star} id {i:>3}: {sack[i]} -> {new_sack[i]}")
            if new_sack is not None:
                sack = new_sack
    except KeyboardInterrupt:
        emit(f"{now()} # watch stopped")
    finally:
        TAPE.close()
        print(f"tape: {path}")


def main():
    verb = sys.argv[1] if len(sys.argv) > 1 else ""
    watch_ids = {int(a) for a in sys.argv[2:] if a.isdigit()}
    if verb == "snap":
        verb_snap(watch_ids)
    elif verb == "watch":
        verb_watch(watch_ids)
    else:
        print(__doc__)
        sys.exit(2)


if __name__ == "__main__":
    main()
