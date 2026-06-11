#!/usr/bin/env python
"""
Scheduler-CT probe for the Zwill "extra turn on kill" signature.

The feature reduces to ONE unknown: where the AUTHORITATIVE charge-time (CT) lives.
The static-array CT (+0x25) is a known decoy (pinning it did nothing). This probe
watches THREE candidates live and finds the real one empirically:

  static  : static battle array 0x140893C00, byte  +0x25   (the known decoy)
  combat  : authoritative combat band 0x14184xxxx, byte +0x41  (= static 0x25 + 0x1C
            shift that holds for PA/MA/Speed; sits right after Speed @ +0x40)
  cond    : the scheduler turn-queue 0x14077D2A0, u16 +0x0A  (the queue the engine
            actually orders turns from -- the strongest a-priori candidate)

USAGE (game must be running, in a live battle):
  python ct_probe.py watch [seconds=20] [hz=8]   # live table; WATCH for the sawtooth
  python ct_probe.py dump                         # one-shot labeled struct dump (orientation)
  python ct_probe.py hold <which> <val> <maxhp> <lvl> [seconds=12]
        # which in {static,combat,cond}. Re-finds the unit each tick (structs relocate),
        # holds its CT field at <val> (use 100), logs read-back. Watch the AT queue in-game:
        # if the unit jumps to act next, THAT candidate is the authoritative CT. Done.

Cribs all RPM/WPM + scanning scaffolding from entice_diff.py (proven, can't crash the game).
"""
import ctypes as C
from ctypes import wintypes as W
import sys, time

PROC = "FFT_enhanced"

# --- static battle array ---
ARRAY_BASE = 0x140893C00
STRIDE = 0x200
SLOT_LO, SLOT_HI = -20, 11

# --- authoritative combat band ---
BAND_ANCHOR = 0x14184F890
BAND_RADIUS = 0x100000
BAND_CHUNK = 0x40000

# --- scheduler turn-queue (condensed) ---
COND_BASE = 0x14077D2A0
COND_SPAN = 0x8000
C_LVL, C_TEAM, C_CT, C_HP, C_MAXHP = 0x00, 0x02, 0x0A, 0x0C, 0x10

# fingerprint fields (static array layout)
A_LVL, A_OBRAVE, A_OFAITH, A_INB, A_HP, A_MAXHP = 0x0D, 0x0E, 0x10, 0x12, 0x14, 0x16
A_STATIC_CT = 0x25
# auth-band copy uses the STATIC layout (scan_auth locates by MaxHP@0x16), so its stat
# block is PA@0x22 MA@0x23 Speed@0x24 CT@0x25 -- NOT GrowthEngine's combat frame (which
# is anchored 0x1C lower, Speed@0x40). This copy is where charm/allegiance writes STUCK,
# so it is the authoritative copy; its +0x25 CT is the untested candidate.
B_PA, B_MA, B_SPEED, B_CT = 0x22, 0x23, 0x24, 0x25

k32 = C.WinDLL("kernel32", use_last_error=True)
PV = 0x0010 | 0x0008 | 0x0400          # VM_READ | VM_OPERATION | QUERY_INFORMATION
PV_W = PV | 0x0020                      # + VM_WRITE


def find_pid(name):
    class PE32(C.Structure):
        _fields_ = [("dwSize", W.DWORD), ("cntUsage", W.DWORD), ("th32ProcessID", W.DWORD),
                    ("th32DefaultHeapID", C.POINTER(C.c_ulong)), ("th32ModuleID", W.DWORD),
                    ("cntThreads", W.DWORD), ("th32ParentProcessID", W.DWORD),
                    ("pcPriClassBase", C.c_long), ("dwFlags", W.DWORD), ("szExeFile", C.c_char * 260)]
    snap = k32.CreateToolhelp32Snapshot(0x2, 0)
    e = PE32(); e.dwSize = C.sizeof(e); pid = None
    if k32.Process32First(snap, C.byref(e)):
        while True:
            if name.lower() in e.szExeFile.decode(errors="ignore").lower():
                pid = e.th32ProcessID; break
            if not k32.Process32Next(snap, C.byref(e)): break
    k32.CloseHandle(snap)
    return pid


def rd(h, a, n):
    b = (C.c_ubyte * n)(); g = C.c_size_t(0)
    if k32.ReadProcessMemory(h, C.c_void_p(a), b, n, C.byref(g)) and g.value == n:
        return bytes(b)
    return None


def wr(h, a, data):
    buf = (C.c_ubyte * len(data))(*data)
    return bool(k32.WriteProcessMemory(h, C.c_void_p(a), buf, len(data), C.byref(C.c_size_t(0))))


def u16(b, o): return b[o] | (b[o + 1] << 8)


def is_unit(b):
    if b is None: return False
    mhp = u16(b, A_MAXHP); lvl = b[A_LVL]
    return u16(b, A_INB) == 1 and 1 <= mhp <= 2000 and 1 <= lvl <= 99 \
        and b[A_OBRAVE] <= 100 and b[A_OFAITH] <= 100


def fp_of(b): return (u16(b, A_MAXHP), b[A_LVL], b[A_OBRAVE], b[A_OFAITH])


def scan_static(h):
    """All active units. {key: {slot, addr, hp, mhp, lvl}} keyed by mhp/lvl/br/fa."""
    out = {}
    for n in range(SLOT_LO, SLOT_HI):
        addr = ARRAY_BASE + n * STRIDE
        b = rd(h, addr, STRIDE)
        if not is_unit(b): continue
        key = "%d/%d/%d/%d" % fp_of(b)
        if key in out: continue
        out[key] = {"slot": n, "addr": addr, "hp": u16(b, A_HP),
                    "mhp": u16(b, A_MAXHP), "lvl": b[A_LVL]}
    return out


def scan_auth(h, fps):
    """Band-scan 0x14184xxxx for each fingerprint. {key: addr} (combat-struct base)."""
    want = {}
    for (mhp, lvl, br, fa) in fps:
        want.setdefault(mhp, []).append((lvl, br, fa))
    found = {}
    lo = BAND_ANCHOR - BAND_RADIUS
    total = BAND_RADIUS * 2
    off = 0
    while off < total:
        n = min(BAND_CHUNK + 0x200, total - off)
        buf = rd(h, lo + off, n)
        if buf:
            lim = min(BAND_CHUNK, len(buf) - 0x200)
            i = A_MAXHP
            while i < lim:
                mhp = buf[i] | (buf[i + 1] << 8)
                cands = want.get(mhp)
                if cands:
                    base = i - A_MAXHP
                    for (lvl, br, fa) in cands:
                        if buf[base + A_LVL] == lvl and buf[base + A_OBRAVE] == br \
                                and buf[base + A_OFAITH] == fa:
                            key = "%d/%d/%d/%d" % (mhp, lvl, br, fa)
                            found.setdefault(key, lo + off + base)
                            break
                i += 1
        off += BAND_CHUNK
    return found


def find_cond_entry(buf, mhp, lvl):
    for i in range(0, len(buf) - 0x12, 2):
        if u16(buf, i + C_MAXHP) == mhp and buf[i + C_LVL] == lvl and buf[i + C_LVL + 1] == 0:
            return i
    return None


# ---- candidate readers: (read_value, write_bytes(val), addr) per candidate ----
def candidates(h, key, static, auth_addr):
    """Return {name: (addr, width, value_or_None)} for this unit's three CT candidates."""
    out = {}
    s = static[key]
    # static decoy
    sb = rd(h, s["addr"], STRIDE)
    out["static"] = (s["addr"] + A_STATIC_CT, 1, sb[A_STATIC_CT] if sb else None)
    # combat band
    if auth_addr is not None:
        cb = rd(h, auth_addr, STRIDE)
        ct = cb[B_CT] if cb else None
        out["combat"] = (auth_addr + B_CT, 1, ct)
        out["_combat_spd"] = (auth_addr + B_SPEED, 1, cb[B_SPEED] if cb else None)
    else:
        out["combat"] = (None, 1, None); out["_combat_spd"] = (None, 1, None)
    # scheduler queue
    cbuf = rd(h, COND_BASE, COND_SPAN)
    if cbuf:
        i = find_cond_entry(cbuf, s["mhp"], s["lvl"])
        out["cond"] = (COND_BASE + i + C_CT if i is not None else None, 2,
                       u16(cbuf, i + C_CT) if i is not None else None)
    else:
        out["cond"] = (None, 2, None)
    return out


def locate(h):
    static = scan_static(h)
    fps = set(tuple(int(x) for x in k.split("/")) for k in static)
    auth = scan_auth(h, fps)
    return static, auth


def cmd_dump(h):
    static, auth = locate(h)
    print(f"{len(static)} units; {len(auth)} located in combat band.\n")
    for key, s in sorted(static.items()):
        side = "PLAYER" if s["slot"] >= 1 else "enemy "
        a = auth.get(key)
        astr = f"{a:012X}" if a else "--MISS--"
        c = candidates(h, key, static, a)
        print(f"{side} fp={key:16} slot={s['slot']:>3} hp={s['hp']}/{s['mhp']} "
              f"combat@{astr}")
        print(f"    staticCT(+25)={c['static'][2]}  combatSpd(+40)={c['_combat_spd'][2]} "
              f"combatCT(+41)={c['combat'][2]}  condCT(+0A)={c['cond'][2]}")


def cmd_watch(h, seconds, hz):
    """Live table of all three CT candidates per unit. WATCH the value that climbs 0->~100
    and resets the instant that unit acts -- that is the CT. Re-locates only on a miss."""
    static, auth = locate(h)
    keys = sorted(static, key=lambda k: -static[k]["slot"])   # players first
    print(f"watching {len(keys)} units for {seconds}s @ {hz}Hz. "
          f"cols: side fp | staticCT | combatSpd combatCT | condCT\n")
    dt = 1.0 / hz
    end = int(seconds * hz)
    for t in range(end):
        line_units = []
        miss = False
        for key in keys:
            s = static.get(key)
            if not s: continue
            a = auth.get(key)
            c = candidates(h, key, static, a)
            if a is None: miss = True
            side = "P" if s["slot"] >= 1 else "e"
            line_units.append(f"{side}{key.split('/')[1]:>2}L:"
                              f"s{_v(c['static'][2])} "
                              f"c[sp{_v(c['_combat_spd'][2])} ct{_v(c['combat'][2])}] "
                              f"q{_v(c['cond'][2])}")
        print(f"t{t/hz:5.1f}  " + "  ".join(line_units))
        if miss:                                   # a struct relocated -> re-find the band
            static, auth = locate(h)
            keys = sorted(static, key=lambda k: -static[k]["slot"])
        time.sleep(dt)


def _v(x): return "--" if x is None else f"{x:>3}"


def _locate_ct(h, which, mhp, lvl):
    """Resolve (addr, width, auth_base) of the chosen CT field for the (mhp,lvl) unit.
    auth_base is the combat-band struct base (for cheap re-validation), or None."""
    static = scan_static(h)
    key = next((k for k in static if static[k]["mhp"] == mhp and static[k]["lvl"] == lvl), None)
    if key is None:
        return None, None, None
    if which == "static":
        return static[key]["addr"] + A_STATIC_CT, 1, None
    if which == "cond":
        cbuf = rd(h, COND_BASE, COND_SPAN)
        i = find_cond_entry(cbuf, mhp, lvl) if cbuf else None
        return (COND_BASE + i + C_CT, 2, None) if i is not None else (None, None, None)
    auth = scan_auth(h, {tuple(int(x) for x in key.split("/"))})   # combat = auth band
    a = auth.get(key)
    return (a + B_CT, 1, a) if a is not None else (None, None, None)


def _fp_ok(h, auth_base, mhp, lvl):
    """Cheap check that the cached combat-band base still holds this unit (it can relocate)."""
    b = rd(h, auth_base, 0x18)
    return bool(b) and u16(b, A_MAXHP) == mhp and b[A_LVL] == lvl


def cmd_hold(h, which, val, mhp, lvl, seconds):
    """FAST hold of one CT candidate at <val> on the (mhp,lvl) unit. Locates once (the 1MB
    band scan is slow), then pins at ~30ms with a cheap fingerprint re-validation, re-scanning
    ONLY when the unit relocates. Watch the AT queue: if the unit acts (repeatedly), this field
    is the authoritative scheduler CT."""
    print(f"FAST HOLD {which}CT={val} on unit mhp={mhp} lvl={lvl} for {seconds}s. "
          f"Watch the turn order...")
    addr, width, auth_base = _locate_ct(h, which, mhp, lvl)
    if addr is None:
        print("  unit/field not located -- is the battle live and the unit on the field?"); return
    print(f"  pinning @{addr:012X} (width {width})")
    tick = 0.03
    end = int(seconds / tick)
    last = -1
    acts = 0                                   # count engine resets we overrode (= turns stolen)
    for t in range(end):
        if auth_base is not None and not _fp_ok(h, auth_base, mhp, lvl):
            addr, width, auth_base = _locate_ct(h, which, mhp, lvl)   # relocated -> re-scan
            if addr is None:
                time.sleep(tick); continue
        cur = rd(h, addr, width)
        cv = int.from_bytes(cur, "little") if cur else -1
        if cv != val:
            if 0 <= cv < val:
                acts += 1                       # engine pulled it below our pin = it consumed/reset CT
            wr(h, addr, val.to_bytes(width, "little"))
        if int(t * tick) != last:
            last = int(t * tick)
            print(f"  t={last:>2}s  @{addr:012X}  pre-write={cv:>3}  overrides={acts}")
        time.sleep(tick)
    print(f"hold ended. engine pulled CT below our pin {acts}x (each = a turn taken / CT reset).")


TQ_BASE = 0x14077D2A0   # turn-queue active-unit header (Offsets.TurnQueue)
TQ_LVL, TQ_TEAM, TQ_HP, TQ_MAXHP = 0x00, 0x02, 0x0C, 0x10


def cmd_queue(h):
    """Read the turn-queue ACTIVE unit header (who the engine thinks is acting) + dump CTs.
    Distinguishes 'engine isn't scheduling our pinned unit' from 'our active-unit read is broken'."""
    lvl = u16(rd(h, TQ_BASE + TQ_LVL, 2) or b"\0\0", 0)
    team = u16(rd(h, TQ_BASE + TQ_TEAM, 2) or b"\0\0", 0)
    hp = u16(rd(h, TQ_BASE + TQ_HP, 2) or b"\0\0", 0)
    mhp = u16(rd(h, TQ_BASE + TQ_MAXHP, 2) or b"\0\0", 0)
    print(f"ACTIVE UNIT (turn queue @ {TQ_BASE:012X}): lvl={lvl} team={team} hp={hp}/{mhp}\n")
    cmd_dump(h)


def main():
    mode = sys.argv[1] if len(sys.argv) > 1 else ""
    if mode not in ("watch", "dump", "hold", "queue"):
        print(__doc__); return
    if mode == "queue":
        pid = find_pid(PROC)
        if not pid: print(f"{PROC} not running"); return
        h = k32.OpenProcess(PV, False, pid)
        try: cmd_queue(h)
        finally: k32.CloseHandle(h)
        return
    pid = find_pid(PROC)
    if not pid:
        print(f"{PROC} not running"); return
    h = k32.OpenProcess(PV_W if mode == "hold" else PV, False, pid)
    if not h:
        print(f"OpenProcess failed err={C.get_last_error()}"); return
    try:
        if mode == "dump":
            cmd_dump(h)
        elif mode == "watch":
            secs = float(sys.argv[2]) if len(sys.argv) > 2 else 20
            hz = float(sys.argv[3]) if len(sys.argv) > 3 else 8
            cmd_watch(h, secs, hz)
        else:
            which = sys.argv[2]; val = int(sys.argv[3])
            mhp = int(sys.argv[4]); lvl = int(sys.argv[5])
            secs = float(sys.argv[6]) if len(sys.argv) > 6 else 12
            cmd_hold(h, which, val, mhp, lvl, secs)
    finally:
        k32.CloseHandle(h)


if __name__ == "__main__":
    main()
