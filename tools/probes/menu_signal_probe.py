#!/usr/bin/env python
"""
World-map menu-open signal hunt (READ-ONLY). Premise P-A of the LW-193 consent rewrite:
the fix wants the twin grant to stand down whenever the player is inside ANY world-map menu
(party, equip, shop, save), and no existing Offsets constant covers that state: PauseFlag and
SubmenuFlag are battle-scope (status card) signals per their own doc comments. Known lead:
0x140D40554 was REJECTED as the status-card byte in the 1.5.1 re-anchor because it fired for
generic panels; firing for any panel is exactly the shape wanted here.

Method: the consistency-sampled multi-state solve that re-found SubmenuFlag on 1.5.1
(docs/research/PORT_1.5.1_OFFSETS.md): capture N samples per labeled game state, keep bytes
whose value is CONSTANT within every state, and report those whose menu-state constant
differs from their free-state constant.

Usage (one state capture per game state, then solve):
  python tools/probes/menu_signal_probe.py state free     # world map, no menu, hands off
  python tools/probes/menu_signal_probe.py state party    # party menu root open
  python tools/probes/menu_signal_probe.py state equip    # a unit's equip screen open
  python tools/probes/menu_signal_probe.py state save     # save menu open
  python tools/probes/menu_signal_probe.py state free2    # everything closed again
  python tools/probes/menu_signal_probe.py solve
Labels starting with "free" count as FREE; every other label counts as MENU.
Samples persist to %TEMP% between runs; `solve` reads them all.

Regions swept (candidate byte families, provenance in Offsets.cs comments):
  0x140D40000..0x140D41000  SubmenuFlag family incl. the 0x140D40554 generic-panel byte
  0x140C6B000..0x140C6B400  PauseFlag family (two synced copies live here)
  0x1407FC000..0x1407FD000  MenuCursor family
Named scalars (BattleMode, Slot0, Slot9, EventId, PauseFlag, SubmenuFlag) print with each
capture for context.
"""
import ctypes
import ctypes.wintypes as W
import json
import os
import pathlib
import struct
import sys
import time

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1]))
from lib import offsets as _offsets

O = _offsets.load()
(BATTLE_MODE, SLOT0, SLOT9, EVENT_ID, PAUSE_FLAG, SUBMENU_FLAG) = _offsets.require(
    ["BattleMode", "Slot0", "Slot9", "EventId", "PauseFlag", "SubmenuFlag"], O)

REGIONS = [
    (0x140D40000, 0x1000, "submenu-family"),
    (0x140C6B000, 0x0400, "pause-family"),
    (0x1407FC000, 0x1000, "menucursor-family"),
]
SAMPLES = 12
INTERVAL = 0.4
STORE = pathlib.Path(os.environ.get("TEMP", ".")) / "lw193_menu_signal_states"

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


def rpm(h, addr, n):
    buf = ctypes.create_string_buffer(n)
    got = ctypes.c_size_t()
    if not k32.ReadProcessMemory(h, ctypes.c_void_p(addr), buf, n, ctypes.byref(got)) or got.value != n:
        return None
    return buf.raw


def verb_state(label):
    h = open_game()
    if not h:
        print("game not running")
        sys.exit(1)
    STORE.mkdir(exist_ok=True)
    caps = []
    for s in range(SAMPLES):
        cap = {}
        for base, size, name in REGIONS:
            d = rpm(h, base, size)
            cap[name] = list(d) if d else None
        caps.append(cap)
        time.sleep(INTERVAL)
    ctx = {
        "battleMode": rpm(h, BATTLE_MODE, 1)[0],
        "slot0": struct.unpack("<I", rpm(h, SLOT0, 4))[0],
        "slot9": struct.unpack("<I", rpm(h, SLOT9, 4))[0],
        "eventId": struct.unpack("<H", rpm(h, EVENT_ID, 2))[0],
        "pauseFlag": rpm(h, PAUSE_FLAG, 1)[0],
        "submenuFlag": rpm(h, SUBMENU_FLAG, 1)[0],
    }
    (STORE / f"{label}.json").write_text(json.dumps({"label": label, "caps": caps, "ctx": ctx}))
    print(f"captured {SAMPLES} samples for state '{label}'; context: {ctx}")


def verb_solve():
    states = {}
    for f in STORE.glob("*.json"):
        d = json.loads(f.read_text())
        states[d["label"]] = d
    if len(states) < 2:
        print(f"need at least 2 captured states in {STORE}, found {len(states)}")
        sys.exit(1)
    free = [l for l in states if l.startswith("free")]
    menu = [l for l in states if not l.startswith("free")]
    print(f"states: FREE={free} MENU={menu}")

    hits = []
    for base, size, name in REGIONS:
        for off in range(size):
            per_state = {}
            ok = True
            for label, d in states.items():
                vals = {c[name][off] for c in d["caps"] if c[name]}
                if len(vals) != 1:
                    ok = False
                    break
                per_state[label] = vals.pop()
            if not ok:
                continue
            fvals = {per_state[l] for l in free}
            mvals = {per_state[l] for l in menu}
            if len(fvals) == 1 and len(mvals) == 1 and fvals != mvals:
                hits.append((base + off, name, per_state))
    if not hits:
        print("no byte separates MENU from FREE cleanly; capture more states or widen regions")
        return
    print(f"{len(hits)} candidate byte(s): constant per state, MENU value != FREE value")
    for addr, name, per_state in hits:
        vals = "  ".join(f"{l}={v}" for l, v in sorted(per_state.items()))
        star = " <-- generic-panel lead" if addr == 0x140D40554 else ""
        print(f"  {addr:#x} ({name})  {vals}{star}")


# --- wide sweep: the narrow candidate regions came up dry 2026-08-17, so sweep the whole
# --- static-data span with a numpy streaming constant-intersection (same method, bigger net).
WIDE_BASE = 0x140700000
WIDE_END = 0x141880000     # covers 0x1407xx cursor statics, 0x140C6x battle statics,
PAGE = 0x10000             # 0x140D3A/D40 UI families, 0x1411A7 roster/sack, 0x14185+ combat


def _wide_sample(h):
    import numpy as np
    size = WIDE_END - WIDE_BASE
    arr = np.zeros(size, dtype=np.uint8)
    readable = np.zeros(size, dtype=bool)
    for off in range(0, size, PAGE):
        d = rpm(h, WIDE_BASE + off, min(PAGE, size - off))
        if d is not None:
            arr[off:off + len(d)] = np.frombuffer(d, dtype=np.uint8)
            readable[off:off + len(d)] = True
    return arr, readable


def verb_wstate(label):
    import numpy as np
    h = open_game()
    if not h:
        print("game not running")
        sys.exit(1)
    STORE.mkdir(exist_ok=True)
    value, const = _wide_sample(h)
    const = const.copy()
    for _ in range(SAMPLES - 1):
        time.sleep(INTERVAL)
        arr, readable = _wide_sample(h)
        const &= readable & (arr == value)
    np.savez_compressed(STORE / f"wide_{label}.npz", value=value, const=const)
    print(f"wide state '{label}': {int(const.sum())} bytes constant across {SAMPLES} samples")


def verb_wsolve():
    import numpy as np
    states = {}
    for f in STORE.glob("wide_*.npz"):
        label = f.stem[len("wide_"):]
        d = np.load(f)
        states[label] = (d["value"], d["const"])
    free = [l for l in states if l.startswith("free")]
    menu = [l for l in states if not l.startswith("free")]
    print(f"wide states: FREE={free} MENU={menu}")
    if not free or not menu:
        print("need at least one free* and one menu state")
        sys.exit(1)
    mask = None
    for value, const in states.values():
        mask = const if mask is None else (mask & const)
    fv = [states[l][0] for l in free]
    mv = [states[l][0] for l in menu]
    for v in fv[1:]:
        mask &= (v == fv[0])
    for v in mv[1:]:
        mask &= (v == mv[0])
    mask &= (fv[0] != mv[0])
    idx = np.flatnonzero(mask)
    print(f"{len(idx)} byte(s) constant per state, equal within FREE, equal within MENU, and different between")
    binary = [i for i in idx if {int(fv[0][i]), int(mv[0][i])} <= {0, 1}]
    if binary:
        print(f"  {len(binary)} of them are clean 0/1 flags (best candidates):")
        for i in binary[:40]:
            print(f"    {WIDE_BASE + i:#x}  free={int(fv[0][i])} menu={int(mv[0][i])}")
    for i in idx[:60]:
        if i in binary:
            continue
        print(f"  {WIDE_BASE + i:#x}  free={int(fv[0][i])} menu={int(mv[0][i])}")


def verb_verify(addrs):
    """20Hz on-change watch of candidate bytes, self-taping (verification tour: every menu
    open/close must flip a surviving candidate; map travel and dialogs must not)."""
    import datetime
    h = open_game()
    if not h:
        print("game not running")
        sys.exit(1)
    tapes = pathlib.Path(__file__).resolve().parent / "tapes"
    tapes.mkdir(exist_ok=True)
    path = tapes / f"lw193_menusig_{time.strftime('%Y%m%d_%H%M%S')}.log"
    tape = open(path, "w", encoding="utf-8")

    def emit(line):
        print(line, flush=True)
        tape.write(line + "\n")
        tape.flush()

    last = {}
    emit(f"# candidate verify watch: {[hex(a) for a in addrs]}")
    try:
        while True:
            now = datetime.datetime.now().strftime("%H:%M:%S.%f")[:-3]
            for a in addrs:
                d = rpm(h, a, 1)
                v = d[0] if d else None
                if a in last and last[a] != v:
                    emit(f"{now} {a:#x}: {last[a]} -> {v}")
                elif a not in last:
                    emit(f"{now} {a:#x}: BASE {v}")
                last[a] = v
            time.sleep(0.05)
    except KeyboardInterrupt:
        pass
    finally:
        tape.close()
        print(f"tape: {path}")


def main():
    verb = sys.argv[1] if len(sys.argv) > 1 else ""
    if verb == "state" and len(sys.argv) > 2:
        verb_state(sys.argv[2])
    elif verb == "solve":
        verb_solve()
    elif verb == "wstate" and len(sys.argv) > 2:
        verb_wstate(sys.argv[2])
    elif verb == "wsolve":
        verb_wsolve()
    elif verb == "verify" and len(sys.argv) > 2:
        verb_verify([int(a, 0) for a in sys.argv[2:]])
    else:
        print(__doc__)
        sys.exit(2)


if __name__ == "__main__":
    main()
