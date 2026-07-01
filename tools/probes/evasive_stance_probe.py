#!/usr/bin/env python
"""
Evasive Stance / Vigilance "Defending" status WATCHSPAN probe (FFT:IC 1.5).

QUESTION: IC's Evasive Stance (ability.en Key 479, a command == the PSX "Defend"
command -- see memory menu-command-injection, support id 223 -> Defend) and
Vigilance (Key 426, a reaction) both "greatly increase physical and magickal parry
and evasion rates UNTIL ITS NEXT TURN". That "until next turn" lifetime is the exact
signature of a held STATUS bit (the engine reads it at hit-resolution, clears it on
the owner's next turn) -- the same shape as Charm / Doom / Poison / Reraise. If the
evade boost is a status bit, the runtime can WRITE+HOLD it (like CharmLock/Plague)
and grant the buff without ever casting the command.

PREDICTION (decode-map, NOT yet live-confirmed): FFTHandsFree's StatusDecoder.StatusMap
-- the PSX 5-byte current-status field, byte N = band +0x45+N, already cross-checked
live for Dead/Undead (+0x45/0x20,0x10), Reraise/Transparent (+0x47), Poison (+0x48/0x80),
Doom (+0x49/0x01) -- enumerates "Defending" at band +0x45 bit 0x02 (and "Performing"
at +0x45/0x01). So the LIKELY Defending bit is band +0x45 mask 0x02. +0x45 itself is a
PROVEN status byte (Dead/Undead live in it), so only the SPECIFIC bit + the evade-honoring
need the live diff below.

This is a RESEARCH probe, not a build. RPM is read-only and cannot crash the game; the
ONLY writes happen behind the explicit `hold` verb (a guarded OR-set + revert). The live
diff (cast the stance, watch the bit) and the evade confirmation (watch attackers whiff on
the held unit) are the USER'S EYES -- the probe only locates the byte and pins a candidate.

THE WATCHSPAN RECIPE (per memory poison-status-bytes / the poison_probe playbook):
  1. snapshot  -- baseline the located unit's status bytes 0x44..0x5A (decoded).
  2. watch     -- poll those bytes; you cast Evasive Stance (or get Vigilance to fire) and
                  READ which bit flips ON. Confirm it clears on the unit's NEXT turn.
                  That names the real Defending bit (predicted +0x45/0x02).
  3. hold      -- write+HOLD that candidate bit every ~30ms on a unit that did NOT cast the
                  stance, then WATCH IN-GAME whether attackers start missing it (and whether
                  the status icon / "Defending" tag shows). Reverts the bit on exit. If held
                  attackers visibly whiff -> the evade boost is calc-gated and honored from the
                  bit -> the status-hold path is GO. If nothing changes -> the boost is NOT a
                  held-status effect (action-state, or applied at cast-time only) -> fall back
                  to the reaction-grant (Vigilance via the +0x94/+0x78 reaction bitfield).

FINGERPRINT: locate by (brave, faith) [+ optional level to disambiguate twins], read off
the LIVE auth band (0x14184xxxx family) -- the static array freezes on restart. Use `survey`
first to read every unit's brave/faith if you don't know the target's.

USAGE (game running, live battle):
  python tools\\probes\\evasive_stance_probe.py survey
        # dump every live band unit: fp (brave/faith/lvl), HP, status bytes 0x44..0x5A decoded.
  python tools\\probes\\evasive_stance_probe.py snapshot <brave> <faith> [level]
        # one located unit: full 0x44..0x5A dump + per-bit decode + the Defending highlight.
  python tools\\probes\\evasive_stance_probe.py watch <brave> <faith> [secs=180] [level] [hz=15]
        # poll 0x44..0x5A; prints every byte/bit change. Cast Evasive Stance on this unit and
        # SEE which bit sets; let its next turn pass and SEE it clear. (read-only)
  python tools\\probes\\evasive_stance_probe.py hold <brave> <faith> <off> <mask> [secs=120] [level]
        #   off hex ok (0x45), mask hex ok (0x02). THE SPIKE: OR-hold the candidate bit @30ms,
        #   counts engine clears (each clear = an expiry/turn we overrode), reverts on exit.
        #   e.g. hold the predicted Defending bit:  ... hold <brave> <faith> 0x45 0x02
        #   Then ATTACK the held unit in-game and watch the hit/miss rate. (WRITE -- gated here)

DON'T restart the battle mid-probe (static array freezes; band relocates) -- start a fresh
battle and relaunch. Addresses are the 1.5-confirmed CombatAnchor band (LivingWeapon/Offsets.cs).
"""
import ctypes as C
from ctypes import wintypes as W
import sys
import time

PROC = "FFT_enhanced"

# --- 1.5-confirmed auth band (LivingWeapon/Offsets.cs: CombatAnchor / CombatStride / BandEntry) ---
COMBAT_ANCHOR  = 0x141855CE0   # Offsets.CombatAnchor (1.5 CONFIRMED)
BAND_ENTRY     = 0x1C          # Offsets.BandEntry
COMBAT_STRIDE  = 0x200         # Offsets.CombatStride
BAND_SLOTS     = 49            # n = -24..+24 around the anchor (Offsets.BandSlots)
BAND_READ_BASE = COMBAT_ANCHOR + BAND_ENTRY - 24 * COMBAT_STRIDE   # Offsets.BandReadBase

# band-relative fingerprint fields (static layout; matches charm_probe.py / poison_probe.py)
A_LEVEL, A_BRAVE, A_FAITH, A_HP, A_MAXHP = 0x0D, 0x0E, 0x10, 0x14, 0x16

# status window to dump: 0x44 (leading byte) .. 0x5A inclusive (covers the 5-byte current-status
# field +0x45..+0x49, the poison timer +0x4A, and the doom countdown +0x59).
WIN_LO, WIN_HI = 0x44, 0x5B    # [lo, hi)

# band-relative status byte -> [(mask, name)], from FFTHandsFree StatusDecoder.StatusMap
# (byte N == band +0x45+N). The (*) bits are the Defend-related candidates this probe hunts.
STATUS = {
    0x45: [(0x40, "Crystal"), (0x20, "Dead"), (0x10, "Undead"), (0x08, "Charging"),
           (0x04, "Jump"), (0x02, "Defending*"), (0x01, "Performing*")],
    0x46: [(0x80, "Petrify"), (0x40, "Invite"), (0x20, "Blind"), (0x10, "Confuse"),
           (0x08, "Silence"), (0x04, "Vampire"), (0x02, "Cursed"), (0x01, "Treasure")],
    0x47: [(0x80, "Oil"), (0x40, "Float"), (0x20, "Reraise"), (0x10, "Transparent"),
           (0x08, "Berserk"), (0x04, "Chicken"), (0x02, "Frog"), (0x01, "Critical")],
    0x48: [(0x80, "Poison"), (0x40, "Regen"), (0x20, "Protect"), (0x10, "Shell"),
           (0x08, "Haste"), (0x04, "Slow"), (0x02, "Stop"), (0x01, "Wall")],
    0x49: [(0x80, "Faith"), (0x40, "Innocent"), (0x20, "Charm"), (0x10, "Sleep"),
           (0x08, "DontMove"), (0x04, "DontAct"), (0x02, "Reflect"), (0x01, "DeathSentence")],
}
# annotations for non-bitfield bytes in the window (timers / countdowns)
NOTE = {0x4A: "<poison timer>", 0x59: "<doom countdown>"}
DEFENDING_OFF, DEFENDING_MASK = 0x45, 0x02   # the prediction this probe is built to test

k32 = C.WinDLL("kernel32", use_last_error=True)
PV   = 0x0010 | 0x0400          # VM_READ | QUERY_INFORMATION  (read-only)
PV_W = PV | 0x0008 | 0x0020     # + VM_OPERATION | VM_WRITE     (hold verb only)


def find_pid(name):
    """Return the pid of the LARGEST-working-set match (trap: 2 FFT_enhanced procs exist;
    the live game is the big one -- see memory dual-gun-equip-write-proven)."""
    class PE32(C.Structure):
        _fields_ = [("dwSize", W.DWORD), ("cntUsage", W.DWORD), ("th32ProcessID", W.DWORD),
                    ("th32DefaultHeapID", C.POINTER(C.c_ulong)), ("th32ModuleID", W.DWORD),
                    ("cntThreads", W.DWORD), ("th32ParentProcessID", W.DWORD),
                    ("pcPriClassBase", C.c_long), ("dwFlags", W.DWORD), ("szExeFile", C.c_char * 260)]
    snap = k32.CreateToolhelp32Snapshot(0x2, 0)
    e = PE32(); e.dwSize = C.sizeof(e)
    pids = []
    if k32.Process32First(snap, C.byref(e)):
        while True:
            if name.lower() in e.szExeFile.decode(errors="ignore").lower():
                pids.append(e.th32ProcessID)
            if not k32.Process32Next(snap, C.byref(e)):
                break
    k32.CloseHandle(snap)
    if not pids:
        return None
    if len(pids) == 1:
        return pids[0]
    # pick the largest working set (the live game, not a helper/stub process)
    psapi = C.WinDLL("psapi")

    class PMC(C.Structure):
        _fields_ = [("cb", W.DWORD), ("PageFaultCount", W.DWORD),
                    ("PeakWorkingSetSize", C.c_size_t), ("WorkingSetSize", C.c_size_t),
                    ("QuotaPeakPagedPoolUsage", C.c_size_t), ("QuotaPagedPoolUsage", C.c_size_t),
                    ("QuotaPeakNonPagedPoolUsage", C.c_size_t), ("QuotaNonPagedPoolUsage", C.c_size_t),
                    ("PagefileUsage", C.c_size_t), ("PeakPagefileUsage", C.c_size_t)]
    best, best_ws = pids[0], -1
    for p in pids:
        hh = k32.OpenProcess(PV, False, p)
        if not hh:
            continue
        m = PMC(); m.cb = C.sizeof(m)
        if psapi.GetProcessMemoryInfo(hh, C.byref(m), m.cb) and m.WorkingSetSize > best_ws:
            best, best_ws = p, m.WorkingSetSize
        k32.CloseHandle(hh)
    return best


def rd(h, a, n):
    buf = (C.c_ubyte * n)(); g = C.c_size_t(0)
    if k32.ReadProcessMemory(h, C.c_void_p(a), buf, n, C.byref(g)) and g.value == n:
        return bytes(buf)
    return None


def wr(h, a, data):
    g = C.c_size_t(0)
    buf = (C.c_ubyte * len(data))(*data)
    return bool(k32.WriteProcessMemory(h, C.c_void_p(a), buf, len(data), C.byref(g)))


def u16(b, o):
    return b[o] | (b[o + 1] << 8)


def band_entry(s):
    return BAND_READ_BASE + s * COMBAT_STRIDE


def valid(b):
    """unit-shaped band entry guard (matches the runtime's Band validity bounds)."""
    if not b:
        return False
    mhp, lvl, br, fa, hp = u16(b, A_MAXHP), b[A_LEVEL], b[A_BRAVE], b[A_FAITH], u16(b, A_HP)
    return 0 < mhp < 2000 and hp <= mhp and 1 <= lvl <= 99 and 1 <= br <= 100 and 1 <= fa <= 100


def scan_units(h):
    """Return [(addr, lvl, br, fa, hp, mhp)] for every valid live band entry."""
    out = []
    for s in range(BAND_SLOTS):
        a = band_entry(s)
        b = rd(h, a, 0x60)
        if not valid(b):
            continue
        out.append((a, b[A_LEVEL], b[A_BRAVE], b[A_FAITH], u16(b, A_HP), u16(b, A_MAXHP)))
    return out


def locate(h, br, fa, lvl=None):
    """Find band entries matching (brave, faith) [and level]. Returns a list of addrs.
    De-dupes by fingerprint (the band carries frozen (0,0) twins -- same fp, two addrs)."""
    hits = []
    seen = set()
    for a, l, b, f, hp, mhp in scan_units(h):
        if b != br or f != fa:
            continue
        if lvl is not None and l != lvl:
            continue
        key = (mhp, l, b, f)
        if key in seen:
            continue
        seen.add(key)
        hits.append(a)
    return hits


def decode(b, lo):
    """List active status names from a status window buffer b starting at offset lo."""
    names = []
    for off, bits in STATUS.items():
        if off < lo or off - lo >= len(b):
            continue
        v = b[off - lo]
        for mask, name in bits:
            if v & mask:
                names.append(name)
    return names


def dump_window(h, addr, label=""):
    b = rd(h, addr + WIN_LO, WIN_HI - WIN_LO)
    if not b:
        print(f"  {label}<window @{addr + WIN_LO:012X} unreadable>")
        return
    cells = " ".join(f"{(WIN_LO + i):02X}:{v:02X}" for i, v in enumerate(b))
    print(f"  {label}[{addr + WIN_LO:012X}] {cells}")
    for off, note in NOTE.items():
        if lo_in(off):
            print(f"     +0x{off:02X} = {b[off - WIN_LO]:>3}   {note}")
    active = decode(b, WIN_LO)
    print(f"     active: {', '.join(active) if active else '(none)'}")
    dv = b[DEFENDING_OFF - WIN_LO]
    print(f"     PREDICTED Defending bit +0x{DEFENDING_OFF:02X}/0x{DEFENDING_MASK:02X}: "
          f"{'SET' if dv & DEFENDING_MASK else 'clear'}  (byte=0x{dv:02X})")


def lo_in(off):
    return WIN_LO <= off < WIN_HI


def cmd_survey(h):
    units = scan_units(h)
    print(f"{len(units)} live band units. status bytes 0x{WIN_LO:02X}..0x{WIN_HI - 1:02X}:\n")
    for a, lvl, br, fa, hp, mhp in sorted(units, key=lambda u: (u[2], u[3])):
        b = rd(h, a + WIN_LO, WIN_HI - WIN_LO)
        hx = " ".join(f"{x:02X}" for x in b) if b else "??"
        active = ", ".join(decode(b, WIN_LO)) if b else "?"
        print(f"@{a:012X} br{br:>3}/fa{fa:<3} L{lvl:<2} hp={hp:>4}/{mhp:<4}  {hx}"
              + (f"   -> {active}" if active else ""))
    print("\nPick a target's br/fa, then `snapshot`/`watch`/`hold` it.")


def resolve_one(h, br, fa, lvl, verb):
    hits = locate(h, br, fa, lvl)
    if not hits:
        print(f"no live band unit with brave={br} faith={fa}"
              + (f" level={lvl}" if lvl is not None else "")
              + " (battle live? unit on field? try `survey`).")
        return None
    if len(hits) > 1:
        print(f"{len(hits)} units match brave={br} faith={fa}"
              + (f" level={lvl}" if lvl is not None else "")
              + f"; using the first (@{hits[0]:012X}). Pass a level to disambiguate.")
    return hits[0]


def cmd_snapshot(h, br, fa, lvl):
    addr = resolve_one(h, br, fa, lvl, "snapshot")
    if addr is None:
        return
    print(f"snapshot of br{br}/fa{fa}{'' if lvl is None else f'/L{lvl}'} @{addr:012X}:")
    dump_window(h, addr)
    print("\nNow `watch` it and cast Evasive Stance -- the bit that flips ON is the real one.")


def cmd_watch(h, br, fa, secs, lvl, hz):
    addr = resolve_one(h, br, fa, lvl, "watch")
    if addr is None:
        return
    base = rd(h, addr + WIN_LO, WIN_HI - WIN_LO)
    if base is None:
        print("can't read the status window")
        return
    print(f"watching br{br}/fa{fa}{'' if lvl is None else f'/L{lvl}'} @{addr:012X} "
          f"bytes 0x{WIN_LO:02X}..0x{WIN_HI - 1:02X} for {secs:.0f}s @ {hz:.0f}Hz (read-only).")
    print("IN-GAME: cast Evasive Stance on this unit (or get Vigilance to fire), then let its")
    print("NEXT TURN pass. Read which bit sets, then clears.\n")
    prev = base
    fp = (u16(rd(h, addr, 0x18), A_MAXHP), rd(h, addr, 0x18)[A_LEVEL])
    t0 = time.time()
    dt = 1.0 / hz
    try:
        while time.time() - t0 < secs:
            head = rd(h, addr, 0x18)
            if not head or u16(head, A_MAXHP) != fp[0] or head[A_LEVEL] != fp[1]:
                nb = locate(h, br, fa, lvl)
                if nb:
                    if nb[0] != addr:
                        print(f"  [reloc] {addr:012X} -> {nb[0]:012X}")
                    addr = nb[0]
                    prev = rd(h, addr + WIN_LO, WIN_HI - WIN_LO) or prev
                    fp = (u16(rd(h, addr, 0x18), A_MAXHP), rd(h, addr, 0x18)[A_LEVEL])
                time.sleep(dt)
                continue
            cur = rd(h, addr + WIN_LO, WIN_HI - WIN_LO)
            t = time.time() - t0
            if cur and cur != prev:
                for i in range(len(cur)):
                    if cur[i] == prev[i]:
                        continue
                    off = WIN_LO + i
                    flipped = []
                    for mask, name in STATUS.get(off, []):
                        was, now = prev[i] & mask, cur[i] & mask
                        if was != now:
                            flipped.append(f"{name}{'+' if now else '-'}")
                    extra = f"  {NOTE[off]}" if off in NOTE else ""
                    tag = "  <-- *** DEFENDING ***" if (off == DEFENDING_OFF
                          and (cur[i] ^ prev[i]) & DEFENDING_MASK) else ""
                    print(f"t={t:6.1f}s  +0x{off:02X}: {prev[i]:02X} -> {cur[i]:02X}"
                          + (f"  [{', '.join(flipped)}]" if flipped else "") + extra + tag)
                prev = cur
            time.sleep(dt)
    except KeyboardInterrupt:
        print("(interrupted)")
    print("\nREAD ME: a bit that sets ON the cast and clears on the unit's NEXT turn is the")
    print("'until next turn' Defending status. Predicted +0x45/0x02. Then confirm with `hold`.")


def cmd_hold(h, br, fa, off, mask, secs, lvl):
    addr = resolve_one(h, br, fa, lvl, "hold")
    if addr is None:
        return
    head = rd(h, addr, 0x18)
    fp = (u16(head, A_MAXHP), head[A_LEVEL])
    print(f"HOLD band+0x{off:02X} |= 0x{mask:02X} on br{br}/fa{fa}"
          f"{'' if lvl is None else f'/L{lvl}'} (mhp={fp[0]} L{fp[1]}) @{addr:012X} for {secs:.0f}s.")
    print("THE SPIKE: this unit did NOT cast the stance. Now ATTACK it in-game and watch the")
    print("hit/miss rate -- if attackers start WHIFFING, the evade boost is honored from the held")
    print("bit (status-hold path GO). The bit is REVERTED on exit.\n")
    t0 = time.time()
    asserts = 0          # engine cleared the bit -> we re-set it (each = an expiry/turn we overrode)
    last_s = -1
    try:
        while time.time() - t0 < secs:
            cur = rd(h, addr, 0x18)
            if not cur or u16(cur, A_MAXHP) != fp[0] or cur[A_LEVEL] != fp[1]:
                nb = locate(h, br, fa, lvl)   # relocated -> re-find
                if nb:
                    addr = nb[0]
                    head = rd(h, addr, 0x18)
                    fp = (u16(head, A_MAXHP), head[A_LEVEL])
                time.sleep(0.03)
                continue
            b = rd(h, addr + off, 1)
            if b is not None and (b[0] & mask) != mask:
                wr(h, addr + off, bytes([b[0] | mask]))
                asserts += 1
            s = int(time.time() - t0)
            if s != last_s:
                last_s = s
                blk = rd(h, addr + WIN_LO, WIN_HI - WIN_LO)
                hx = " ".join(f"{x:02X}" for x in blk) if blk else "??"
                print(f"  t={s:>3}s  byte+0x{off:02X}={b[0] if b else -1:02X}  "
                      f"re-asserts={asserts}  [{WIN_LO:02X}..]={hx}")
            time.sleep(0.03)
    except KeyboardInterrupt:
        print("(interrupted)")
    # revert: clear the bit we held (leave the byte otherwise untouched)
    b = rd(h, addr + off, 1)
    if b is not None and (b[0] & mask):
        wr(h, addr + off, bytes([b[0] & ~mask]))
    print(f"hold ended + bit reverted. engine cleared it {asserts}x while held "
          f"(each = an expiry/turn edge we overrode).")


def main():
    mode = sys.argv[1] if len(sys.argv) > 1 else ""
    if mode not in ("survey", "snapshot", "watch", "hold"):
        print(__doc__)
        return
    pid = find_pid(PROC)
    if not pid:
        print(f"{PROC} not running")
        return
    h = k32.OpenProcess(PV_W if mode == "hold" else PV, False, pid)
    if not h:
        print(f"OpenProcess failed err={C.get_last_error()}")
        return
    try:
        a = sys.argv
        if mode == "survey":
            cmd_survey(h)
        elif mode == "snapshot":
            cmd_snapshot(h, int(a[2]), int(a[3]), int(a[4]) if len(a) > 4 else None)
        elif mode == "watch":
            cmd_watch(h, int(a[2]), int(a[3]),
                      float(a[4]) if len(a) > 4 else 180,
                      int(a[5]) if len(a) > 5 else None,
                      float(a[6]) if len(a) > 6 else 15)
        elif mode == "hold":
            cmd_hold(h, int(a[2]), int(a[3]), int(a[4], 0), int(a[5], 0),
                     float(a[6]) if len(a) > 6 else 120,
                     int(a[7]) if len(a) > 7 else None)
    finally:
        k32.CloseHandle(h)


if __name__ == "__main__":
    main()
