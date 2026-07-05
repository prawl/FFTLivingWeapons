#!/usr/bin/env python
"""
Display-card re-anchor probe for FFT:IC 1.5 (read-only, cannot crash the game).

Finishes the three display offsets the 1.5 re-anchor left open (docs/research/PORT_1.5_OFFSETS.md):
  PauseFlag    candidate 0x140C80199  -- WRONG (did not 0->1; pause is held through the menu)
  SubmenuFlag  found     0x140D4085E  -- CONFIRMED 0->1 on the Status card, twice
  MirrorWeapon UNKNOWN   -- the u16 holding the viewed unit's weapon id while a card is open

The in-battle "Status" card repaints only when battleMode==3 && pause && submenu all read true
(BattleState.StatusCardOpen). Pause is held across the WHOLE player turn (even cursor-on-map
mode 1), and is 0 only on enemy turns / animations -- so the unpaused baseline must be a live
enemy turn, NOT your own turn. THREE states:

  live : an ENEMY turn or an action animating -- no player menu, game actively running (UNPAUSED)
  menu : your command menu up (Move/Act/Wait), but NOT viewing a Status card (paused)
  card : a unit's Status card open (paused; a known weapon on screen)

Then:
  pause        = u8 0 in {live}, 1 in {menu, card}
  submenu      = u8 0 in {live, menu}, 1 in {card}
  MirrorWeapon = (round 2) the u16 that == weapon X in cardA and == weapon Y in cardB

USAGE (game running; you drive the states):
  python display_probe.py read
        # one-shot read of the candidates + battleMode right now.
  python display_probe.py snap live       # ENEMY turn / animation, no menu (unpaused)
  python display_probe.py snap menu        # your command menu up, no Status card
  python display_probe.py snap card        # Status card open, known weapon
  python display_probe.py solve            # pause: 0 live / 1 menu&card; submenu: 1 card only.

Snapshots -> %TEMP% (throwaway). Read-only RPM only.
"""
import ctypes, ctypes.wintypes as w, struct, sys, os, json

PROCESS_VM_READ = 0x0010
PROCESS_QUERY_INFORMATION = 0x0400
k32 = ctypes.windll.kernel32
psapi = ctypes.windll.psapi

BATTLE_MODE  = 0x1409069A0   # 1.5 confirmed
PAUSE_CAND   = 0x140C80199   # last-session guess (now disproven)
SUBMENU_CAND = 0x140D4085E   # confirmed

# regions to capture. (base, length). Wide enough to contain the real flags + the mirror.
REGIONS = {
    "pause":   (0x140C64000, 0x1E000),   # old pause 0x140C64A5C through the 0x140C8x noise zone
    "submenu": (0x140D3F000, 0x3000),    # around SUBMENU 0x140D4085E
    "mirror":  (0x141860000, 0x40000),   # wide 0x14186x-0x14189x window for MirrorWeapon
}
TMP = os.environ.get("TEMP", ".")


def find_pid(name):
    arr = (w.DWORD * 4096)(); needed = w.DWORD()
    psapi.EnumProcesses(ctypes.byref(arr), ctypes.sizeof(arr), ctypes.byref(needed))
    for i in range(needed.value // ctypes.sizeof(w.DWORD)):
        hh = k32.OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, False, arr[i])
        if not hh:
            continue
        buf = ctypes.create_unicode_buffer(260)
        if psapi.GetModuleBaseNameW(hh, None, buf, 260) and buf.value.lower() == name.lower():
            return arr[i], hh
        k32.CloseHandle(hh)
    return None, None


pid, h = find_pid("fft_enhanced.exe")
if not h:
    print("game not running"); sys.exit(1)


def rpm(addr, n):
    buf = ctypes.create_string_buffer(n); got = ctypes.c_size_t()
    if not k32.ReadProcessMemory(h, ctypes.c_void_p(addr), buf, n, ctypes.byref(got)) or got.value != n:
        return None
    return buf.raw


def u8(a):
    b = rpm(a, 1); return b[0] if b else None


def cmd_read():
    print(f"pid={pid}")
    print(f"battleMode (0x1409069A0) = {u8(BATTLE_MODE)}")
    print(f"pause  cand 0x140C80199  = {u8(PAUSE_CAND)}")
    print(f"submenu     0x140D4085E  = {u8(SUBMENU_CAND)}")


def cmd_snap(label):
    out = {"battleMode": u8(BATTLE_MODE), "regions": {}}
    for name, (base, ln) in REGIONS.items():
        raw = rpm(base, ln)
        out["regions"][name] = {"base": base, "len": ln, "hex": raw.hex() if raw else None}
    path = os.path.join(TMP, f"fft_disp_{label}.json")
    json.dump(out, open(path, "w"))
    miss = [n for n in REGIONS if out["regions"][n]["hex"] is None]
    print(f"snap '{label}' -> {path}  battleMode={out['battleMode']}"
          + (f"  MISSED: {miss}" if miss else "  (all captured)"))


def _load(label):
    p = os.path.join(TMP, f"fft_disp_{label}.json")
    return json.load(open(p)) if os.path.exists(p) else None


def _region(snap, name):
    hx = snap["regions"][name]["hex"]
    return bytes.fromhex(hx) if hx else None


def cmd_solve(weapon_ids):
    live, menu, card = _load("live"), _load("menu"), _load("card")
    if not (live and menu and card):
        have = [l for l in ("live", "menu", "card") if _load(l)]
        print(f"need snaps live/menu/card; have {have}"); return
    print(f"battleMode live={live['battleMode']} menu={menu['battleMode']} card={card['battleMode']}")

    # pause: byte == 0 in live, == 1 in menu AND card.
    lb, mb, cb = _region(live, "pause"), _region(menu, "pause"), _region(card, "pause")
    base = REGIONS["pause"][0]
    if lb and mb and cb:
        hits = [base + i for i in range(len(lb))
                if lb[i] == 0 and mb[i] == 1 and cb[i] == 1]
        print(f"[pause] ==0 in live, ==1 in menu&card: {len(hits)}")
        for a in hits[:80]:
            print(f"    {a:#x}" + ("  <- old guess" if a == PAUSE_CAND else ""))

    # submenu: byte == 1 in card only, == 0 in live AND menu.
    lb, mb, cb = _region(live, "submenu"), _region(menu, "submenu"), _region(card, "submenu")
    base = REGIONS["submenu"][0]
    if lb and mb and cb:
        hits = [base + i for i in range(len(lb))
                if lb[i] == 0 and mb[i] == 0 and cb[i] == 1]
        print(f"[submenu] ==1 in card only: {len(hits)}")
        for a in hits[:60]:
            print(f"    {a:#x}" + ("  <- confirmed cand" if a == SUBMENU_CAND else ""))


import time

# top isolated pause candidates from the solve (near the old addr / region-delta match).
PAUSE_WATCH = [0x140C6B311, 0x140C6CFFB, 0x140C7E415, 0x140C7FD0D]


def cmd_sample(label, secs):
    """Poll the pause region at ~10Hz for <secs>; record, per byte, whether it stayed
    CONSTANT and at what value. Animated UI bytes vary and get dropped; a true flag holds.
    Hold ONE game state steady for the whole window (e.g. a menu open, or an enemy turn)."""
    base, ln = REGIONS["pause"]
    first = rpm(base, ln)
    if first is None:
        print("could not read pause region"); return
    first = bytearray(first)
    constant = bytearray([1]) * ln   # 1 = still constant
    n = 1
    end = time.time() + secs
    while time.time() < end:
        cur = rpm(base, ln)
        if cur:
            n += 1
            for i in range(ln):
                if constant[i] and cur[i] != first[i]:
                    constant[i] = 0
        time.sleep(0.1)
    # keep only bytes that stayed constant at 0 or 1 (flag-like)
    held = {base + i: first[i] for i in range(ln) if constant[i] and first[i] in (0, 1)}
    path = os.path.join(TMP, f"fft_pause_{label}.json")
    json.dump({str(k): v for k, v in held.items()}, open(path, "w"))
    print(f"sample '{label}': {n} reads, {len(held)} bytes held constant at 0/1 -> {path}")


def cmd_mirrorfind(wx, wy):
    """MirrorWeapon = the u16 that == weapon X in the 'card' snap and == weapon Y in 'card2'.
    Snap two Status cards on units with DIFFERENT weapons: 'snap card' (weapon X) then
    'snap card2' (weapon Y), then 'mirrorfind X Y'."""
    a, b = _load("card"), _load("card2")
    if not (a and b):
        have = [l for l in ("card", "card2") if _load(l)]
        print(f"need 'snap card' (weapon {wx}) and 'snap card2' (weapon {wy}); have {have}"); return
    ca, cb = _region(a, "mirror"), _region(b, "mirror")
    base = REGIONS["mirror"][0]
    if not (ca and cb):
        print("mirror region missing in a snap"); return
    hits = []
    for off in range(0, len(ca) - 1):
        va = ca[off] | (ca[off + 1] << 8)
        vb = cb[off] | (cb[off + 1] << 8)
        if va == wx and vb == wy:
            hits.append(base + off)
    print(f"[mirror] u16 == {wx} in card AND == {wy} in card2: {len(hits)}")
    for x in hits[:60]:
        print(f"    {x:#x}  (offhand sibling +2 = {x + 2:#x})")


def cmd_pausefind():
    """pause flag = constant 1 across the 'paused' sample AND constant 0 across 'running'."""
    pp = os.path.join(TMP, "fft_pause_paused.json")
    pr = os.path.join(TMP, "fft_pause_running.json")
    if not (os.path.exists(pp) and os.path.exists(pr)):
        print("need samples: 'sample paused 6' (menu/card held open), 'sample running 6' (enemy turn)"); return
    paused = {int(k): v for k, v in json.load(open(pp)).items()}
    running = {int(k): v for k, v in json.load(open(pr)).items()}
    hits = [a for a, v in paused.items() if v == 1 and running.get(a) == 0]
    print(f"[pause] constant 1 while paused AND constant 0 while running: {len(hits)}")
    for a in sorted(hits)[:80]:
        print(f"    {a:#x}" + ("  <- old guess" if a == PAUSE_CAND else ""))


def cmd_watch(addrs, secs):
    addrs = addrs or PAUSE_WATCH
    print("battleMode + candidate pause bytes. Expect: enemy turn -> 0, your turn/menu/card -> 1.")
    print("t     bMode  " + "  ".join(f"{a:#x}" for a in addrs))
    end = time.time() + secs
    while time.time() < end:
        bm = u8(BATTLE_MODE)
        vals = "  ".join(f"{str(u8(a)):>9}" for a in addrs)
        print(f"{time.strftime('%H:%M:%S')}  {str(bm):>4}  {vals}")
        time.sleep(0.5)


if __name__ == "__main__":
    if len(sys.argv) < 2:
        print(__doc__); sys.exit(0)
    cmd = sys.argv[1]
    if cmd == "read":
        cmd_read()
    elif cmd == "snap":
        cmd_snap(sys.argv[2] if len(sys.argv) > 2 else "snap")
    elif cmd == "solve":
        cmd_solve({int(x) for x in sys.argv[2:]})
    elif cmd == "sample":
        cmd_sample(sys.argv[2], int(sys.argv[3]) if len(sys.argv) > 3 else 6)
    elif cmd == "pausefind":
        cmd_pausefind()
    elif cmd == "mirrorfind":
        cmd_mirrorfind(int(sys.argv[2]), int(sys.argv[3]))
    elif cmd == "watch":
        # optional trailing int = seconds; any 0x.. args = addresses to watch
        secs = 30
        addrs = []
        for a in sys.argv[2:]:
            if a.lower().startswith("0x"):
                addrs.append(int(a, 16))
            else:
                secs = int(a)
        cmd_watch(addrs, secs)
    else:
        print(__doc__)
