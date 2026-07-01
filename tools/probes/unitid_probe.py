#!/usr/bin/env python
"""
Unit-identity probe (READ-ONLY). The question: how do we attribute a COMPLETED TURN to a
SPECIFIC unit without a stat fingerprint? Both (maxHp,hp,level) and (level,brave,faith) COLLIDE
(two same-stat units are indistinguishable), which burned two Iai fixes. Two sibling repos agree
there is NO magic unique-id byte -- the answer is POSITIONAL identity. This probe proves the two
positional signals we need:

  WATCH mode (THE priority -- validates the Iai/TurnTracker rebuild):
    The engine keeps a POINTER to the acting unit's combat frame at G_ACTOR = 0x14186AF68
    (FFTMultiplayer action_record_probe + our own doaction-target-redirect memory:
     acting slot = ([0x14186AF68] - 0x141853CE0) / 0x200). On each rising edge of the global
    Acted flag (0x140782A8C, TurnTracker's proven "a turn completed" trigger) -- and whenever the
    pointer changes -- we read the pointer, resolve it to a band seat by ADDRESS (no stats), and
    print who acted + which weapon they hold. Run the 2x-Ame-no-Murakumo(id 42) repro and confirm:
    each katana wielder's turn yields an actor-pointer hit at ITS OWN seat, where the turn-queue
    stat fingerprint (what TryActiveFingerprint uses today) is AMBIGUOUS. That is the whole fix.

  FIND mode (bonus -- the recon's speculative upside):
    Dumps the combat-FRAME HEADER +0x00..+0x03 (spriteSet/?/?/job) that NO existing probe dumps
    (combat_struct_diff starts at the band base, 0x1C too late), for every seat, plus:
      SCAN-A: u16 offsets in the 0x200 frame whose value == that unit's roster nameId, intersected
              across all player seats -> a struct-resident nameId back-reference, if one exists.
      SCAN-B: byte offsets whose value is DISTINCT across all occupied seats this frame -> a
              candidate per-unit instance id (flags +0x01/+0x02, the PSX ENTD/formation slots).
    A hit at +0x01/+0x02 that holds through a level-up and reads on enemies would be an even
    cleaner both-teams id; a miss confirms positional identity is the answer.

GROUND TRUTH (LivingWeapon/Offsets.cs, 1.5-confirmed):
  ACTED flag   0x140782A8C (u8, ==1 after an action; stays 1 through the turn, drifts to 0)
  G_ACTOR      0x14186AF68 (u64 pointer -> acting unit's combat FRAME base, i.e. band_base - 0x1C)
  G_SEQ        0x14186AFF4 (u32 action/turn sequence counter -- prints to show turn progression)
  TURN QUEUE   0x1407832A0: +0x00 level u16, +0x02 team u16, +0x04 nameId(seq-index TRAP) u16,
                            +0x0C hp u16, +0x10 maxhp u16   (what the COLLIDING fingerprint reads)
  COMBAT FRAME base(i) = 0x141852CE0 + i*0x200  (= band_base - 0x1C); band_base(i) = frame + 0x1C
    band-relative: +0x04 weapon u16, +0x0D level, +0x0E brave, +0x10 faith, +0x14 hp u16,
                   +0x16 maxhp u16, +0x33 gx, +0x34 gy
  ROSTER @0x1411A7D10 stride 0x258: +0x14 rHand u16, +0x1D level, +0x1E brave, +0x1F faith, +0x230 nameId

USAGE (deploy TWO units holding Ame-no-Murakumo id 42 + a faster non-katana unit, in one battle):
  python unitid_probe.py watch            # actor-pointer + Acted watcher (run through several turns)
  python unitid_probe.py watch 120 30     # watch for 120s at 30 Hz
  python unitid_probe.py find             # one-shot header dump + SCAN-A/SCAN-B
  python unitid_probe.py find watch <off> # after find flags an offset, watch it for pulse/drift
"""
import ctypes as C
from ctypes import wintypes as W
import sys
import time

PROC = "FFT_enhanced"

# --- global engine signals ---
ACTED = 0x140782A8C
G_ACTOR = 0x14186AF68
G_SEQ = 0x14186AFF4

# --- turn queue (the colliding fingerprint the runtime uses today) ---
TQ = 0x1407832A0
TQ_LEVEL, TQ_TEAM, TQ_NAMEID, TQ_HP, TQ_MAXHP = 0x00, 0x02, 0x04, 0x0C, 0x10

# --- combat frame / band ---
COMBAT_ANCHOR = 0x141855CE0
STRIDE = 0x200
BAND_ENTRY = 0x1C
BAND_BASE = COMBAT_ANCHOR + BAND_ENTRY - 24 * STRIDE     # band index 0 (n = -24)
FRAME_BASE0 = BAND_BASE - BAND_ENTRY                     # 0x141852CE0; frame(i) = FRAME_BASE0 + i*STRIDE
BAND_SLOTS = 49
A_WEAPON, A_LEVEL, A_BRAVE, A_FAITH, A_HP, A_MAXHP, A_GX, A_GY = 0x04, 0x0D, 0x0E, 0x10, 0x14, 0x16, 0x33, 0x34
A_SPEED = 0x24            # band-relative Speed (frame +0x40) -- watch the Iai hold/release live
FR_AREC = 0x1A0           # frame-relative per-unit ACTION RECORD (FFTMultiplayer reaction-builder find):
FR_AREC_LEN = 12          #   +0x1A2 u16 ability id, +0x1AA kind tag, +0x1AB seq counter. HYPOTHESIS under
                          #   test: this fires on the unit's OWN frame when IT acts -- a per-unit turn
                          #   signal that works even for units the global actor pointer skips (seat 25).
FR_NAMEID = 0x1FC         # frame-relative roster-nameId back-reference (SCAN-A hit 2026-07-01, all player seats)

# --- roster (ground truth for SCAN-A) ---
ROSTER_BASE, ROSTER_STRIDE, ROSTER_SLOTS = 0x1411A7D10, 0x258, 20
R_RHAND, R_LEVEL, R_BRAVE, R_FAITH, R_NAMEID = 0x14, 0x1D, 0x1E, 0x1F, 0x230
EMPTY = (0x00FF, 0xFFFF)
MAX_DRIFT = 9

k32 = C.WinDLL("kernel32", use_last_error=True)
psapi = C.WinDLL("psapi", use_last_error=True)
PV = 0x0010 | 0x0400


class _PMC(C.Structure):
    _fields_ = [("cb", W.DWORD), ("PageFaultCount", W.DWORD),
                ("PeakWorkingSetSize", C.c_size_t), ("WorkingSetSize", C.c_size_t),
                ("QuotaPeakPagedPoolUsage", C.c_size_t), ("QuotaPagedPoolUsage", C.c_size_t),
                ("QuotaPeakNonPagedPoolUsage", C.c_size_t), ("QuotaNonPagedPoolUsage", C.c_size_t),
                ("PagefileUsage", C.c_size_t), ("PeakPagefileUsage", C.c_size_t)]


def _ws(pid):
    h = k32.OpenProcess(PV, False, pid)
    if not h:
        return 0
    p = _PMC(); p.cb = C.sizeof(p)
    ok = psapi.GetProcessMemoryInfo(h, C.byref(p), p.cb)
    k32.CloseHandle(h)
    return p.WorkingSetSize if ok else 0


def find_pid(name):
    class PE32(C.Structure):
        _fields_ = [("dwSize", W.DWORD), ("cntUsage", W.DWORD), ("th32ProcessID", W.DWORD),
                    ("th32DefaultHeapID", C.POINTER(C.c_ulong)), ("th32ModuleID", W.DWORD),
                    ("cntThreads", W.DWORD), ("th32ParentProcessID", W.DWORD),
                    ("pcPriClassBase", C.c_long), ("dwFlags", W.DWORD), ("szExeFile", C.c_char * 260)]
    snap = k32.CreateToolhelp32Snapshot(0x2, 0)
    e = PE32(); e.dwSize = C.sizeof(e); m = []
    if k32.Process32First(snap, C.byref(e)):
        while True:
            if name.lower() in e.szExeFile.decode(errors="ignore").lower():
                m.append(e.th32ProcessID)
            if not k32.Process32Next(snap, C.byref(e)):
                break
    k32.CloseHandle(snap)
    return max(m, key=_ws) if m else None   # MAX working-set dodges the stale second fft_enhanced


def rd(h, a, n):
    b = (C.c_ubyte * n)(); g = C.c_size_t(0)
    if k32.ReadProcessMemory(h, C.c_void_p(a), b, n, C.byref(g)) and g.value == n:
        return bytes(b)
    return None


def u8(h, a):
    b = rd(h, a, 1); return b[0] if b else None


def u16(h, a):
    b = rd(h, a, 2); return (b[0] | (b[1] << 8)) if b else None


def u32(h, a):
    b = rd(h, a, 4); return int.from_bytes(b, "little") if b else None


def u64(h, a):
    b = rd(h, a, 8); return int.from_bytes(b, "little") if b else None


def band_valid(h, base):
    lvl, mhp, hp = u8(h, base + A_LEVEL), u16(h, base + A_MAXHP), u16(h, base + A_HP)
    if lvl is None or mhp is None or hp is None:
        return None
    if not (1 <= lvl <= 99) or not (1 <= mhp <= 9999) or hp > mhp:
        return None
    return {"lvl": lvl, "brv": u8(h, base + A_BRAVE), "fa": u8(h, base + A_FAITH),
            "hp": hp, "mhp": mhp, "gx": u8(h, base + A_GX), "gy": u8(h, base + A_GY),
            "wpn": u16(h, base + A_WEAPON)}


def ptr_to_seat(ptr):
    """Resolve the actor pointer (a combat FRAME base) to a band index i and side n=i-24, or None."""
    if ptr is None or ptr < FRAME_BASE0:
        return None
    off = ptr - FRAME_BASE0
    if off % STRIDE != 0:
        return None
    i = off // STRIDE
    if not (0 <= i < BAND_SLOTS):
        return None
    return i


def describe_actor(h, ptr):
    i = ptr_to_seat(ptr)
    if i is None:
        return f"ptr=0x{ptr:X} (NOT a frame base -- off-array or stale)"
    band_base = FRAME_BASE0 + i * STRIDE + BAND_ENTRY
    u = band_valid(h, band_base)
    n = i - 24
    side = "ENEMY" if n < 0 else "player"
    if not u:
        return f"seat i={i} (n={n:+d} {side}) band@0x{band_base:X} -- stats unreadable/invalid"
    return (f"seat i={i} (n={n:+d} {side}) weapon={u['wpn']:>3}  "
            f"lvl {u['lvl']} brv {u['brv']} fa {u['fa']}  hp {u['hp']}/{u['mhp']}  pos ({u['gx']},{u['gy']})")


def tq_fingerprint(h):
    lvl, hp, mhp = u16(h, TQ + TQ_LEVEL), u16(h, TQ + TQ_HP), u16(h, TQ + TQ_MAXHP)
    team, nid = u16(h, TQ + TQ_TEAM), u16(h, TQ + TQ_NAMEID)
    return lvl, team, nid, hp, mhp


def tq_ambiguous(h, mhp, hp, lvl):
    """How many DISTINCT (brave,faith) band entries match the turn-queue (maxhp,hp,level)?
    >1 => the fingerprint TryActiveFingerprint bails on (the collision that stuck Iai)."""
    if mhp is None:
        return 0
    seen = set()
    for i in range(BAND_SLOTS):
        base = FRAME_BASE0 + i * STRIDE + BAND_ENTRY
        u = band_valid(h, base)
        if u and u["mhp"] == mhp and u["hp"] == hp and u["lvl"] == lvl:
            seen.add((u["brv"], u["fa"]))
    return len(seen)


def snapshot_players(h):
    """Every valid PLAYER seat (n >= 0): (i, speed, action-record bytes, nameId@+0x1FC, hp)."""
    out = {}
    for i in range(24, BAND_SLOTS):
        frame = FRAME_BASE0 + i * STRIDE
        band = frame + BAND_ENTRY
        u = band_valid(h, band)
        if not u:
            continue
        spd = u8(h, band + A_SPEED)
        rec = rd(h, frame + FR_AREC, FR_AREC_LEN)
        nid = u16(h, frame + FR_NAMEID)
        out[i] = (spd, rec, nid, u["hp"], u["brv"], u["fa"])
    return out


def fmt_rec(rec):
    return "-" if rec is None else " ".join(f"{b:02X}" for b in rec)


def watch(h, seconds, hz):
    print(f"WATCH actor pointer 0x{G_ACTOR:X} + Acted 0x{ACTED:X} + PER-SEAT action records for {seconds}s at {hz}Hz.")
    print("Start this on the FORMATION screen, then play the opening turns. Events fire on: pointer")
    print("move, Acted edge, any player seat's Speed change (Iai hold/release), or any player seat's")
    print("action-record (+0x1A0) change -- the per-unit turn-signal hypothesis for pointer-skipped units.\n")
    period = 1.0 / hz
    end = time.time() + seconds
    prev_acted, prev_ptr = None, None
    prev_seats = {}
    while time.time() < end:
        acted = u8(h, ACTED)
        ptr = u64(h, G_ACTOR)
        rising = acted == 1 and prev_acted == 0
        moved = ptr != prev_ptr
        seats = snapshot_players(h)
        changes = []
        for i, cur in seats.items():
            old = prev_seats.get(i)
            if old is None:
                changes.append((i, "seen", cur))
            else:
                if cur[0] != old[0]:
                    changes.append((i, f"SPEED {old[0]}->{cur[0]}", cur))
                if cur[1] != old[1]:
                    changes.append((i, f"AREC {fmt_rec(old[1])} -> {fmt_rec(cur[1])}", cur))
        if rising or moved or changes:
            t = time.strftime("%H:%M:%S")
            if rising or moved:
                tag = "ACTED-EDGE" if rising else "ptr-move  "
                print(f"{t} {tag}  ACTOR-> {describe_actor(h, ptr)}")
            for i, what, cur in changes:
                spd, rec, nid, hp, brv, fa = cur
                print(f"{t}   seat {i} (nameId {nid}, brv{brv} fa{fa}, hp{hp}, spd{spd}): {what}")
        prev_acted, prev_ptr, prev_seats = acted, ptr, seats
        time.sleep(period)
    print("\nwatch done.")


def roster_slots(h):
    out = []
    for s in range(ROSTER_SLOTS):
        base = ROSTER_BASE + s * ROSTER_STRIDE
        rh = u16(h, base + R_RHAND)
        if rh is None or rh in EMPTY:
            continue
        out.append({"slot": s, "rhand": rh, "nameId": u16(h, base + R_NAMEID),
                    "lvl": u8(h, base + R_LEVEL), "brv": u8(h, base + R_BRAVE), "fa": u8(h, base + R_FAITH)})
    return out


def band_frames(h):
    """Every occupied seat with its raw 0x200 FRAME (starting at frame base, so +0x00..+0x03 IS included)."""
    out = []
    for i in range(BAND_SLOTS):
        frame_base = FRAME_BASE0 + i * STRIDE
        band_base = frame_base + BAND_ENTRY
        u = band_valid(h, band_base)
        if not u:
            continue
        raw = rd(h, frame_base, STRIDE)
        if not raw:
            continue
        u["i"] = i; u["n"] = i - 24; u["frame"] = frame_base; u["raw"] = raw
        out.append(u)
    return out


def match_band(rs, frames):
    best = None
    for b in frames:
        if b["brv"] == rs["brv"] and b["fa"] == rs["fa"] and 0 <= (b["lvl"] - rs["lvl"]) <= MAX_DRIFT:
            # twin filter: prefer a real-position entry over the (0,0) frozen mirror
            real = b["gx"] != 0 or b["gy"] != 0
            if best is None or (real and not (best["gx"] != 0 or best["gy"] != 0)) or b["lvl"] < best["lvl"]:
                best = b
    return best


def find(h):
    rs = roster_slots(h)
    frames = band_frames(h)
    if not frames:
        print("No valid band entries -- are you IN a battle with units deployed?")
        return
    # attach roster nameId ground truth to player seats
    for r in rs:
        b = match_band(r, frames)
        if b:
            b["nameId"] = r["nameId"]

    print("SEAT TABLE (frame header +0x00..+0x03 is the unmapped region; raw1FC = frame+0x1FC u16,")
    print("the SCAN-A nameId back-reference candidate -- printed for BOTH teams to test both-teams-ness):")
    print("  i   n   side   +00 +01 +02 +03(job)  weapon  lvl brv fa   hp/maxhp   pos    spd  raw1FC  rosterNid")
    for b in sorted(frames, key=lambda x: x["n"]):
        h0, h1, h2, h3 = b["raw"][0], b["raw"][1], b["raw"][2], b["raw"][3]
        side = "ENEMY" if b["n"] < 0 else "plyr"
        nid = b.get("nameId")
        spd = b["raw"][BAND_ENTRY + A_SPEED]
        raw1fc = b["raw"][FR_NAMEID] | (b["raw"][FR_NAMEID + 1] << 8)
        print(f"  {b['i']:>2} {b['n']:+3d}  {side:<5}  "
              f"{h0:3d} {h1:3d} {h2:3d} {h3:3d}       {b['wpn']:>4}   "
              f"{b['lvl']:>3} {b['brv']:>3} {b['fa']:>2}  {b['hp']:>4}/{b['mhp']:<4} "
              f"({b['gx']:>2},{b['gy']:>2})  {spd:>3}  {raw1fc:>5}   {nid if nid is not None else '-'}")

    # SCAN-A: a struct-resident copy of the roster nameId (battle->roster back-reference)
    players = [b for b in frames if b.get("nameId") is not None]
    if len(players) >= 2:
        hits = None
        for b in players:
            offs = {o for o in range(0, STRIDE - 1)
                    if (b["raw"][o] | (b["raw"][o + 1] << 8)) == b["nameId"]}
            hits = offs if hits is None else (hits & offs)
        print(f"\nSCAN-A (nameId back-reference): offsets where u16==roster nameId on ALL {len(players)} "
              f"player seats: {sorted('0x%X' % o for o in hits) if hits else 'NONE'}")
        if hits:
            print("  ^ a non-trivial hit here is the in-battle nameId copy -- the both-teams id prize. "
                  "Re-run across a RESTART to confirm it is not a scratch pointer.")
    else:
        print("\nSCAN-A skipped (need >=2 player seats resolved to roster nameId).")

    # SCAN-B: byte offsets that are DISTINCT across every occupied seat (candidate instance id)
    known = {A_LEVEL, A_BRAVE, A_FAITH, A_HP, A_HP + 1, A_MAXHP, A_MAXHP + 1, A_GX, A_GY,
             A_WEAPON, A_WEAPON + 1, 0x2A, 0x2B, 0x2C, 0x2D, 0x3E, 0x3F, 0x40, 0x41}
    distinct = []
    for o in range(STRIDE):
        vals = [b["raw"][o] for b in frames]
        if len(set(vals)) == len(vals):   # all-distinct across seats this frame
            distinct.append(o)
    interesting = [o for o in distinct if o not in known]
    print(f"\nSCAN-B (per-seat DISTINCT bytes, candidate instance id): "
          f"{sorted('0x%X' % o for o in interesting) if interesting else 'NONE'}")
    print("  ^ watch any header hit (+0x01/+0x02) across a LEVEL-UP: `find watch 0x1` -- a byte that")
    print("    holds while lvl/maxHp drift AND reads on enemies is the true fix; one that pulses is a trap.")


def watch_offset(h, off, seconds=30, hz=6):
    print(f"WATCH frame offset +0x{off:X} across all seats for {seconds}s at {hz}Hz "
          f"(flagging any seat whose byte CHANGES = pulse/drift = reject):")
    period = 1.0 / hz
    end = time.time() + seconds
    base = {}
    while time.time() < end:
        for i in range(BAND_SLOTS):
            frame_base = FRAME_BASE0 + i * STRIDE
            band_base = frame_base + BAND_ENTRY
            if not band_valid(h, band_base):
                continue
            v = u8(h, frame_base + off)
            if i not in base:
                base[i] = v
                print(f"  seat i={i} (n={i-24:+d}) +0x{off:X} = {v}")
            elif v != base[i]:
                print(f"  !! seat i={i} +0x{off:X} CHANGED {base[i]} -> {v}  (DRIFT/PULSE)")
                base[i] = v
        time.sleep(period)
    print("watch-offset done.")


def main():
    argv = sys.argv[1:]
    pid = find_pid(PROC)
    if not pid:
        print(f"{PROC} not running."); return
    h = k32.OpenProcess(PV, False, pid)
    if not h:
        print(f"OpenProcess failed (err {C.get_last_error()})."); return
    print(f"pid {pid}")

    if argv and argv[0] == "watch":
        seconds = int(argv[1]) if len(argv) > 1 else 90
        hz = int(argv[2]) if len(argv) > 2 else 30
        watch(h, seconds, hz)
    elif argv and argv[0] == "find" and "watch" in argv:
        off = int(argv[argv.index("watch") + 1], 16)
        watch_offset(h, off)
    elif argv and argv[0] == "find":
        find(h)
    else:
        print("Usage: unitid_probe.py watch [sec] [hz] | find | find watch <hexoff>")
    k32.CloseHandle(h)


if __name__ == "__main__":
    main()
