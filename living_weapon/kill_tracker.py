#!/usr/bin/env python
"""
Living Weapon -- kill tracker (FFTItemOverhaul runtime, v1).

The Living Weapon grows as it kills, so the foundation is counting kills
accurately and attributing each one to the *weapon that swung*. This module
watches FFT:IVC and maintains a per-weapon kill tally; growth + display read
that tally and are built on top of it.

WHY POLLING (not hooks): on this Denuvo build, in-process hooks and hardware
breakpoints crash the game (verified -- see docs/ITEM_CAP_261_BREAK_JOURNEY.md).
External ReadProcessMemory is 100% safe, so we poll from a separate process.

EFFICIENCY: the proven detector (FFTHandsFree BattleTracker) issues ~180 small
reads per tick. This does THREE bulk reads -- battle flags, turn-queue head, and
the entire static unit array in one shot -- plus the roster only on the tick a
player acts. ~3 syscalls/tick at 100 ms = trivial load, and parsing is done in a
single in-memory buffer.

DETECTION (ported verbatim from the battle-tested BattleTracker; offsets verified
in docs/BATTLE_MEMORY_MAP.md):
  * In battle when slot0 (0x14077CA30) == 0xFF AND slot9 (0x14077CA54) ==
    0xFFFFFFFF. Sticky: stay in battle until slot9 changes (slot0 flickers
    during attack animations).
  * Static unit array @0x140893C00, stride 0x200. Enemy slots at array offsets
    <= 0, player slots at >= 1. Per slot: inBattleFlag +0x12, HP +0x14,
    MaxHP +0x16, gridX/Y +0x33/+0x34.
  * STATE-BASED death: a KO'd corpse persists at HP 0 for several turns. Credit
    each corpse exactly ONCE (DeadCredited flag, reset when the slot is seen
    alive). Pure transition detection -- catching the single tick HP crosses 0 --
    is defeated when the victim moves or its MaxHP flickers on the death hit,
    which re-inits the slot and swallows the crossing.
  * ATTRIBUTE BY CORPSE TEAM: an enemy slot dying means a PLAYER killed it, so
    credit the last player who acted (resolved when they swung). Crediting
    "who's active at corpse time" reliably mis-credits the NEXT enemy -- the
    game rotates the active pointer the instant an action ends. Player corpses
    (an enemy's kill) are ignored: the Living Weapon only cares about player kills.
  * The acting player's weapon = roster R-hand (+0x14) of the slot whose nameId
    (+0x230) matches the condensed turn-queue nameId (+0x04). Weapon ids are the
    FFTPatcher-canonical encoding -- same ids as data/items.json.

TALLY: per-weapon counts in living_weapon_kills.json next to this script
(durable -- survives reboots), written atomically with a .bak of the last-good.
"""
import json, os, time, shutil
import ctypes as C
from ctypes import wintypes as W
from pathlib import Path

PROC = "FFT_enhanced"
POLL_SECONDS = 0.10          # matches the proven detector cadence
REATTACH_CHECK = 2.0         # seconds between "is the game still alive?" checks

# ---- battle-flag / turn region (read together as REGION_A) ----------------
REGION_A_BASE = 0x14077CA30
REGION_A_LEN  = 0x60
A_SLOT0 = 0x00               # u32 == 0xFF        -> in battle
A_SLOT9 = 0x24               # u32 == 0xFFFFFFFF  -> in battle
A_ACTED = 0x5C               # u8  active unit has acted this turn

# ---- condensed turn-queue head (current/acting unit) ----------------------
TQ_BASE   = 0x14077D2A0
TQ_LEN    = 0x18
TQ_LEVEL  = 0x00             # u16
TQ_TEAM   = 0x02             # u16  0 = player, 1 = enemy
TQ_NAMEID = 0x04             # u16  matches roster +0x230
TQ_MAXHP  = 0x10            # u16

# ---- static unit array -----------------------------------------------------
ARRAY_BASE   = 0x140893C00
ARRAY_STRIDE = 0x200
SLOTS_BACK   = 20            # enemy slots, at array offsets <= 0
SLOTS_FWD    = 10            # player slots, at array offsets >= 1
N_SLOTS      = SLOTS_BACK + SLOTS_FWD
# slot s sits at ARRAY_BASE + (s - (SLOTS_BACK - 1)) * stride, so the contiguous
# block starts (SLOTS_BACK - 1) strides below the base. Enemy slots are s <= 19.
ARRAY_READ_BASE = ARRAY_BASE - (SLOTS_BACK - 1) * ARRAY_STRIDE
ENEMY_SLOT_MAX  = SLOTS_BACK - 1     # slots 0..19 are enemy-side
A_INBATTLE = 0x12           # u16
A_HP       = 0x14           # u16  (0 == KO'd)
A_MAXHP    = 0x16           # u16
A_GX       = 0x33           # u8
A_GY       = 0x34           # u8

# ---- roster (nameId -> R-hand weapon lookup) ------------------------------
ROSTER_BASE   = 0x1411A18D0
ROSTER_STRIDE = 0x258
ROSTER_SLOTS  = 20
R_RHAND  = 0x14             # u16 right-hand weapon id (equip slot 3)
R_NAMEID = 0x230           # u16

STATE = Path(__file__).resolve().parent / "living_weapon_kills.json"

# ============================ win32 plumbing ===============================
PROCESS_VM_READ = 0x0010
PROCESS_QUERY_INFORMATION = 0x0400
k32 = C.WinDLL("kernel32", use_last_error=True)


def find_pid(name):
    TH32CS_SNAPPROCESS = 0x2

    class PE32(C.Structure):
        _fields_ = [("dwSize", W.DWORD), ("cntUsage", W.DWORD), ("th32ProcessID", W.DWORD),
                    ("th32DefaultHeapID", C.POINTER(C.c_ulong)), ("th32ModuleID", W.DWORD),
                    ("cntThreads", W.DWORD), ("th32ParentProcessID", W.DWORD),
                    ("pcPriClassBase", C.c_long), ("dwFlags", W.DWORD), ("szExeFile", C.c_char * 260)]

    snap = k32.CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0)
    e = PE32(); e.dwSize = C.sizeof(e); pid = None
    if k32.Process32First(snap, C.byref(e)):
        while True:
            if name.lower() in e.szExeFile.decode(errors="ignore").lower():
                pid = e.th32ProcessID; break
            if not k32.Process32Next(snap, C.byref(e)):
                break
    k32.CloseHandle(snap)
    return pid


def open_proc():
    pid = find_pid(PROC)
    if not pid:
        return None, None
    h = k32.OpenProcess(PROCESS_VM_READ | PROCESS_QUERY_INFORMATION, False, pid)
    return (h, pid) if h else (None, None)


def rd(h, addr, n):
    """One ReadProcessMemory -> bytes, or None on failure."""
    buf = (C.c_ubyte * n)(); got = C.c_size_t(0)
    if k32.ReadProcessMemory(h, C.c_void_p(addr), buf, n, C.byref(got)) and got.value == n:
        return bytes(buf)
    return None


def u16(b, o):
    return b[o] | (b[o + 1] << 8)


def u32(b, o):
    return b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24)


# ============================ tally persistence ============================
def load_kills():
    for src in (STATE, STATE.with_suffix(".bak")):
        try:
            if src.exists():
                return {int(k): int(v) for k, v in json.loads(src.read_text()).get("weapon_kills", {}).items()}
        except Exception:
            continue
    return {}


def save_kills(d):
    # atomic write + keep the last-good as .bak, so a crash mid-write can't lose the tally.
    payload = json.dumps({"weapon_kills": {str(k): v for k, v in d.items()}})
    tmp = STATE.with_suffix(".tmp")
    tmp.write_text(payload)
    if STATE.exists():
        try:
            shutil.copy2(STATE, STATE.with_suffix(".bak"))
        except Exception:
            pass
    os.replace(tmp, STATE)


# ============================ attribution ==================================
def resolve_player_weapon(h, name_id):
    """R-hand weapon id of the roster unit whose nameId matches the acting unit.
    One bulk read of the 20-slot roster; returns None if not found / read fails."""
    buf = rd(h, ROSTER_BASE, ROSTER_SLOTS * ROSTER_STRIDE)
    if buf is None:
        return None
    for slot in range(ROSTER_SLOTS):
        o = slot * ROSTER_STRIDE
        if u16(buf, o + R_NAMEID) == name_id:
            return u16(buf, o + R_RHAND)
    return None


# ============================ main loop ====================================
def main():
    print(f"[killtracker] start. tally -> {STATE}", flush=True)
    kills = load_kills()
    h = pid = None
    in_battle = False
    dead = bytearray(N_SLOTS)     # DeadCredited flag per array slot
    last_weapon = -1              # last player weapon that completed an action
    last_check = 0.0

    while True:
        try:
            now = time.monotonic()

            # (re)attach -- survives game restarts
            if h is None:
                h, pid = open_proc()
                if h is None:
                    time.sleep(0.5); continue
                in_battle = False; last_weapon = -1
                for i in range(N_SLOTS):
                    dead[i] = 0
                print(f"[killtracker] attached pid={pid}; {len(kills)} weapons, "
                      f"{sum(kills.values())} kills loaded.", flush=True)
            elif now - last_check > REATTACH_CHECK:
                last_check = now
                if find_pid(PROC) != pid:
                    try:
                        k32.CloseHandle(h)
                    except Exception:
                        pass
                    h = None
                    print("[killtracker] game closed; waiting to reattach...", flush=True)
                    continue

            # --- battle flags (1 read) ---
            a = rd(h, REGION_A_BASE, REGION_A_LEN)
            if a is None:
                time.sleep(POLL_SECONDS); continue
            slot0 = u32(a, A_SLOT0)
            slot9 = u32(a, A_SLOT9)
            entering = slot0 == 0xFF and slot9 == 0xFFFFFFFF
            now_in = entering or (in_battle and slot9 == 0xFFFFFFFF)
            if not now_in:
                if in_battle:                       # battle just ended -> reset state
                    in_battle = False; last_weapon = -1
                    for i in range(N_SLOTS):
                        dead[i] = 0
                time.sleep(POLL_SECONDS); continue
            in_battle = True
            acted = a[A_ACTED]

            # --- resolve the acting player's weapon on the tick they act (1 read, only then) ---
            tq = rd(h, TQ_BASE, TQ_LEN)
            if tq is not None:
                team = u16(tq, TQ_TEAM)
                name_id = u16(tq, TQ_NAMEID)
                level = u16(tq, TQ_LEVEL)
                maxhp = u16(tq, TQ_MAXHP)
                if acted == 1 and team == 0 and name_id > 0 and 1 <= level <= 99 and maxhp > 0:
                    w = resolve_player_weapon(h, name_id)
                    if w is not None and 0 <= w < 0xFFFF:
                        last_weapon = w

            # --- scan the unit array for fresh corpses (1 read for all 30 slots) ---
            arr = rd(h, ARRAY_READ_BASE, N_SLOTS * ARRAY_STRIDE)
            if arr is None:
                time.sleep(POLL_SECONDS); continue
            changed = False
            for s in range(N_SLOTS):
                o = s * ARRAY_STRIDE
                inb = u16(arr, o + A_INBATTLE)
                maxhp = u16(arr, o + A_MAXHP)
                gx = arr[o + A_GX]
                gy = arr[o + A_GY]
                if inb == 0 or maxhp == 0 or maxhp >= 2000 or gx > 30 or gy > 30:
                    continue                        # empty / stale / garbage slot
                hp = u16(arr, o + A_HP)
                if hp == 0:                          # KO'd
                    if not dead[s]:
                        dead[s] = 1
                        if s <= ENEMY_SLOT_MAX and last_weapon >= 0:
                            kills[last_weapon] = kills.get(last_weapon, 0) + 1
                            changed = True
                            print(f"[killtracker] KILL -> weapon {last_weapon} "
                                  f"now {kills[last_weapon]} kills (enemy at {gx},{gy})", flush=True)
                    continue
                dead[s] = 0                           # alive -> a revive+rekill recounts

            if changed:
                save_kills(kills)

        except Exception as ex:
            print(f"[killtracker] loop error: {ex}", flush=True)

        time.sleep(POLL_SECONDS)


if __name__ == "__main__":
    main()
