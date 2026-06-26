#!/usr/bin/env python
"""
Actor-attribution probe (READ-ONLY). Diagnose Bug A: unique units (Cloud/Reis/Meliadoul, ...)
score zero kills and their kills leak to the last resolvable actor (Ramza / a generic).

The live mod credits a kill to the LAST acting player whose weapon set RESOLVED. The resolver
(LivingWeapon/ActorResolver.TryResolveActingPlayer) reads the turn-queue active-unit struct,
matches it to a live BAND entry by (maxHp, hp, level), then fingerprints (level, brave, faith)
against the ROSTER to read the equipped hands. If ANY gate fails it returns "unresolved", and
KillTracker leaves the PREVIOUS actor's latch sticky -- so the unresolved unit's kill is paid
out to whoever last resolved. That is exactly the reported symptom.

This probe replays every gate for the CURRENTLY ACTIVE unit and prints which one fails, so we
fix the real gate instead of guessing. PRIME SUSPECT (visible in the source): both the resolver
sanity bail AND Band.IsValid reject maxHp >= 2000. NG+ uniques are tanky enough to cross 2000;
generics + Ramza stay under it. If the active unit's maxHp prints >= 2000, that is the bug.

GROUND TRUTH (LivingWeapon/Offsets.cs, all 1.5-CONFIRMED -- this probe owns its own constants):
  TURN QUEUE (condensed active-unit struct) @0x1407832A0:
    +0x00 u16 level   +0x02 u16 team(0=player/1=enemy)   +0x0C u16 hp   +0x10 u16 maxHp
  BAND (live unit data; the static array freezes on restart, the band stays live):
    BandReadBase = CombatAnchor(0x141855CE0) + BandEntry(0x1C) - 24*CombatStride(0x200); 49 slots.
    within an entry: +0x0D u8 level  +0x0E u8 brave  +0x10 u8 faith  +0x14 u16 hp
                     +0x16 u16 maxHp  +0x33 u8 gx  +0x34 u8 gy
  ROSTER (equip blueprint) @0x1411A7D10 stride 0x258, 20 slots:
    +0x14 u16 rHand  +0x16 u16 lHand  +0x18 u16 offHand  +0x1D u8 level
    +0x1E u8 brave  +0x1F u8 faith  +0x230 u16 nameId
  Level-drift rule (Band.LevelMatchesRoster): live level may EXCEED roster level by up to 9.

GATE PARITY: this mirrors ActorResolver.TryResolveActingPlayer + FingerprintPlayer +
Band.IsValid exactly (sanity bounds, the twin filter preferring a real-position gx/gy!=0,0
entry, the armed-vs-empty roster match). Read the verdict block at the end of `active`.

USAGE (game running, IN battle; run it WHILE the unit you care about is the ACTIVE unit --
its turn / its action menu up, so the turn-queue holds its fingerprint):
  python actor_attrib_probe.py active     # full gate replay for the current active unit (THE one)
  python actor_attrib_probe.py roster      # dump every occupied roster slot (eyeball the uniques)
  python actor_attrib_probe.py band        # dump every band entry incl. WHY each fails IsValid

NOTE on weapon ids: the equip slot stores the IC-NATIVE item id, which is NOT always the
items.json id (live 2026-06-21: equipped Stoneshooter reads 67, but items.json 67 = Warbrand).
So a "not a tracked weapon" tag here is advisory -- the FALSE/sticky-latch symptom comes from a
FINGERPRINT miss (level/brave/faith/maxHp), not from weapon-set membership. We load meta.json
only to annotate; the verdict never hinges on it.
"""
import ctypes as C
from ctypes import wintypes as W
import json
import os
import sys
import time

PROC = "FFT_enhanced"

# --- 1.5-CONFIRMED constants (LivingWeapon/Offsets.cs) ---
TURN_QUEUE = 0x1407832A0
TQ_LEVEL = 0x00   # u16
TQ_TEAM = 0x02    # u16  0 = player, 1 = enemy
TQ_HP = 0x0C      # u16
TQ_MAXHP = 0x10   # u16

ACTED = 0x140782A8C   # u8  0->1 when an action completes (the latch trigger)

# FFTHandsFree's robust resolver source (BattleTracker.cs): a SEPARATE condensed struct at
# 0x14077D2A0 whose +0x04 IS the roster nameId (joined directly to roster +0x230), unlike OUR
# 0x1407832A0 +0x04 which is a sequential battle-index trap. No band-HP match -> survives mid-action.
# +0x6000-corrected: BattleTracker.cs's 0x14077D2A0 is stale pre-1.5 (reads garbage); the live
# 1.5 struct is at 0x1407832A0 (== our TURN_QUEUE). The question this probe settles: is +0x04 the
# roster nameId (FFTHandsFree's claim) or a sequential battle-index (our Offsets.cs trap warning)?
HF_TQ_BASE = 0x1407832A0
HF_TQ_LEVEL = 0x00     # u16
HF_TQ_TEAM = 0x02      # u16
HF_TQ_HP = 0x0C        # u16
HF_TQ_MAXHP = 0x10     # u16
HF_COND_NAMEID = 0x1407832A4   # u16 +0x04: roster nameId? or battle-index trap?

COMBAT_ANCHOR = 0x141855CE0
COMBAT_STRIDE = 0x200
BAND_ENTRY = 0x1C
BAND_READ_BASE = COMBAT_ANCHOR + BAND_ENTRY - 24 * COMBAT_STRIDE
BAND_SLOTS = 49
A_LEVEL = 0x0D    # u8
A_BRAVE = 0x0E    # u8
A_FAITH = 0x10    # u8
A_HP = 0x14       # u16
A_MAXHP = 0x16    # u16
A_GX = 0x33       # u8
A_GY = 0x34       # u8

ROSTER_BASE = 0x1411A7D10
ROSTER_STRIDE = 0x258
ROSTER_SLOTS = 20
R_RHAND = 0x14    # u16
R_LHAND = 0x16    # u16
R_OFFHAND = 0x18  # u16
R_LEVEL = 0x1D    # u8
R_BRAVE = 0x1E    # u8
R_FAITH = 0x1F    # u8
R_NAMEID = 0x230  # u16

MAX_LEVEL_DRIFT = 9
EMPTY = (0x00FF, 0xFFFF)

# --- kernel32 / psapi RPM (read-only; largest-working-set PID like dualgun_probe) ---
k32 = C.WinDLL("kernel32", use_last_error=True)
psapi = C.WinDLL("psapi", use_last_error=True)
PV = 0x0010 | 0x0400   # VM_READ | QUERY_INFORMATION


class _PMC(C.Structure):
    _fields_ = [("cb", W.DWORD), ("PageFaultCount", W.DWORD),
                ("PeakWorkingSetSize", C.c_size_t), ("WorkingSetSize", C.c_size_t),
                ("QuotaPeakPagedPoolUsage", C.c_size_t), ("QuotaPagedPoolUsage", C.c_size_t),
                ("QuotaPeakNonPagedPoolUsage", C.c_size_t), ("QuotaNonPagedPoolUsage", C.c_size_t),
                ("PagefileUsage", C.c_size_t), ("PeakPagefileUsage", C.c_size_t)]


def _working_set(pid):
    h = k32.OpenProcess(PV, False, pid)
    if not h:
        return 0
    pmc = _PMC(); pmc.cb = C.sizeof(pmc)
    ok = psapi.GetProcessMemoryInfo(h, C.byref(pmc), pmc.cb)
    k32.CloseHandle(h)
    return pmc.WorkingSetSize if ok else 0


def find_pid(name):
    # Largest working set = the real rendered game (a stale 2nd FFT_enhanced ate every write
    # for ~10 turns on 2026-06-21; first-match is the bug, max-working-set is the fix).
    class PE32(C.Structure):
        _fields_ = [("dwSize", W.DWORD), ("cntUsage", W.DWORD), ("th32ProcessID", W.DWORD),
                    ("th32DefaultHeapID", C.POINTER(C.c_ulong)), ("th32ModuleID", W.DWORD),
                    ("cntThreads", W.DWORD), ("th32ParentProcessID", W.DWORD),
                    ("pcPriClassBase", C.c_long), ("dwFlags", W.DWORD), ("szExeFile", C.c_char * 260)]
    snap = k32.CreateToolhelp32Snapshot(0x2, 0)
    e = PE32(); e.dwSize = C.sizeof(e); matches = []
    if k32.Process32First(snap, C.byref(e)):
        while True:
            if name.lower() in e.szExeFile.decode(errors="ignore").lower():
                matches.append(e.th32ProcessID)
            if not k32.Process32Next(snap, C.byref(e)):
                break
    k32.CloseHandle(snap)
    return max(matches, key=_working_set) if matches else None


def rd(h, a, n):
    b = (C.c_ubyte * n)(); g = C.c_size_t(0)
    if k32.ReadProcessMemory(h, C.c_void_p(a), b, n, C.byref(g)) and g.value == n:
        return bytes(b)
    return None


def u8(h, a):
    b = rd(h, a, 1); return b[0] if b else None


def u16(h, a):
    b = rd(h, a, 2); return (b[0] | (b[1] << 8)) if b else None


# --- meta.json weapon ids (advisory annotation only) ---
def load_weapon_ids():
    here = os.path.dirname(os.path.abspath(__file__))
    meta = os.path.normpath(os.path.join(here, "..", "..", "LivingWeapon", "meta.json"))
    try:
        with open(meta) as f:
            d = json.load(f)
        ids = set()
        for k in d.keys():
            try:
                ids.add(int(k))
            except (ValueError, TypeError):
                pass
        return ids
    except (OSError, ValueError):
        return set()


WEAPON_IDS = load_weapon_ids()


def fmt_item(v):
    if v is None:
        return "??"
    if v in EMPTY:
        return "(empty)"
    tag = ""
    if WEAPON_IDS:
        tag = " [tracked]" if v in WEAPON_IDS else " [untracked-id]"
    return f"{v}{tag}"


def band_valid(h, addr):
    """Mirror Band.IsValid; return (ok, reason)."""
    lvl = u8(h, addr + A_LEVEL)
    if lvl is None:
        return False, "unreadable"
    if lvl < 1 or lvl > 99:
        return False, f"level {lvl} out of 1..99"
    br = u8(h, addr + A_BRAVE)
    if br < 1 or br > 100:
        return False, f"brave {br} out of 1..100"
    fa = u8(h, addr + A_FAITH)
    if fa < 1 or fa > 100:
        return False, f"faith {fa} out of 1..100"
    mhp = u16(h, addr + A_MAXHP)
    if mhp < 1 or mhp >= 2000:
        return False, f"maxHp {mhp} >= 2000 (THE SUSPECT)" if mhp and mhp >= 2000 else f"maxHp {mhp} < 1"
    gx = u8(h, addr + A_GX); gy = u8(h, addr + A_GY)
    if gx > 30 or gy > 30:
        return False, f"pos ({gx},{gy}) out of 0..30"
    return True, "ok"


def iter_roster(h):
    for s in range(ROSTER_SLOTS):
        base = ROSTER_BASE + s * ROSTER_STRIDE
        lvl = u8(h, base + R_LEVEL)
        if lvl is None or lvl < 1 or lvl > 99:
            continue
        yield s, base, lvl, u8(h, base + R_BRAVE), u8(h, base + R_FAITH), u16(h, base + R_NAMEID)


def roster_hands(h, base):
    out = []
    for off in (R_RHAND, R_LHAND, R_OFFHAND):
        v = u16(h, base + off)
        if v is not None and v not in EMPTY:
            out.append(v)
    return out


def fingerprint_roster(h, level, brave, faith):
    """Mirror FingerprintPlayer: roster slots matching (level w/ drift, brave, faith). Returns
    list of (slot, nameId, hands)."""
    hits = []
    for s, base, rlvl, rbr, rfa, nid in iter_roster(h):
        if not (level >= rlvl and level - rlvl <= MAX_LEVEL_DRIFT):
            continue
        if rbr != brave or rfa != faith:
            continue
        hits.append((s, nid, roster_hands(h, base)))
    return hits


def cmd_active(h):
    level = u16(h, TURN_QUEUE + TQ_LEVEL)
    team = u16(h, TURN_QUEUE + TQ_TEAM)
    hp = u16(h, TURN_QUEUE + TQ_HP)
    maxhp = u16(h, TURN_QUEUE + TQ_MAXHP)
    print(f"=== ACTIVE UNIT (turn-queue @{TURN_QUEUE:012X}) ===")
    print(f"  level={level}  team={team}({'player' if team == 0 else 'enemy' if team == 1 else '?'})"
          f"  hp={hp}  maxHp={maxhp}")

    # Gate 0: resolver sanity bail.
    bail = []
    if maxhp == 0:
        bail.append("maxHp == 0")
    if maxhp is not None and maxhp >= 2000:
        bail.append(f"maxHp {maxhp} >= 2000  <<< SMOKING GUN if this fires for a unique")
    if level is None or level < 1 or level > 99:
        bail.append(f"level {level} out of 1..99")
    if bail:
        print("\n  VERDICT: resolver returns FALSE at the sanity bail:")
        for b in bail:
            print(f"    - {b}")
        print("  -> latch stays sticky on the previous actor -> THIS unit's kill is mis-credited.")
        return

    # Gate 1+2: band entries matching (maxHp, hp, level), with twin filter.
    print(f"\n=== BAND match (maxHp={maxhp}, hp={hp}, level={level}) ===")
    matches = []          # (slot, addr, brave, faith, realPos)
    near_miss = []        # entries matching hp+level but failing IsValid (esp. maxHp>=2000)
    for s in range(BAND_SLOTS):
        addr = BAND_READ_BASE + s * COMBAT_STRIDE
        a_hp = u16(h, addr + A_HP)
        a_lvl = u8(h, addr + A_LEVEL)
        if a_hp != hp or a_lvl != level:
            # surface entries that share hp+level but were rejected -- the maxHp>=2000 path
            if a_hp == hp and a_lvl is not None and a_lvl == level:
                pass
            continue
        ok, reason = band_valid(h, addr)
        a_mhp = u16(h, addr + A_MAXHP)
        if not ok:
            near_miss.append((s, addr, a_mhp, reason))
            continue
        if a_mhp != maxhp:
            continue
        br = u8(h, addr + A_BRAVE); fa = u8(h, addr + A_FAITH)
        gx = u8(h, addr + A_GX); gy = u8(h, addr + A_GY)
        real = gx != 0 or gy != 0
        matches.append((s, addr, br, fa, real))
        print(f"  band slot {s:>2} @{addr:012X}: brave={br} faith={fa} pos=({gx},{gy}) "
              f"{'REAL-POS' if real else '(0,0) twin'}  -> VALID match")

    for s, addr, mhp, reason in near_miss:
        print(f"  band slot {s:>2} @{addr:012X}: hp+level match but REJECTED -- {reason}")

    if not matches:
        print("\n  VERDICT: resolver returns FALSE -- NO valid band entry matched (maxHp/hp/level).")
        if near_miss:
            print("  The near-miss rows above show WHY (most likely maxHp >= 2000 for an NG+ unique).")
        print("  -> sticky latch -> mis-credit. THIS is the bug for this unit.")
        return

    # twin filter: prefer real-position entries
    if any(r for *_, r in matches):
        matches = [m for m in matches if m[4]]

    # Gate 3: roster fingerprint for each surviving band match.
    print(f"\n=== ROSTER fingerprint (level w/ drift {MAX_LEVEL_DRIFT}, brave, faith) ===")
    resolved_sets = []
    for s, addr, br, fa, real in matches:
        hits = fingerprint_roster(h, level, br, fa)
        if not hits:
            print(f"  band slot {s}: fp(lvl{level}/br{br}/fa{fa}) -> NO roster slot matched "
                  f"(this band entry resolves to an enemy / unresolved)")
            continue
        for rslot, nid, hands in hits:
            tag = "armed" if any(WEAPON_IDS and x in WEAPON_IDS for x in hands) or hands else "EMPTY-handed"
            print(f"  band slot {s}: fp(lvl{level}/br{br}/fa{fa}) -> roster slot {rslot} "
                  f"nameId={nid} hands={[fmt_item(x) for x in hands]} ({tag})")
            resolved_sets.append(hands)

    print("\n=== VERDICT ===")
    if not resolved_sets:
        print("  resolver returns FALSE -- band matched but NO roster fingerprint matched.")
        print("  -> sticky latch -> mis-credit. Gate that fails: ROSTER (level/brave/faith).")
        print("  Run `roster` and compare this unit's live brave/faith to its stored roster row.")
    else:
        print("  resolver RESOLVES this unit (a roster fingerprint matched).")
        print("  If kills still mis-credit, the failure is intermittent -- re-run AT the kill moment,")
        print("  or the weapon set is empty (untracked id) so the kill drops rather than steals.")


def cmd_roster(h):
    print(f"roster @{ROSTER_BASE:012X} stride 0x{ROSTER_STRIDE:X}\n")
    print(f"{'slot':>4} {'nameId':>6} {'lvl':>3} {'br':>3} {'fa':>3}  hands")
    for s, base, lvl, br, fa, nid in iter_roster(h):
        hands = [fmt_item(x) for x in roster_hands(h, base)]
        print(f"{s:>4} {nid:>6} {lvl:>3} {br:>3} {fa:>3}  {hands}")


def cmd_band(h):
    print(f"band base @{BAND_READ_BASE:012X} stride 0x{COMBAT_STRIDE:X}, {BAND_SLOTS} slots\n")
    for s in range(BAND_SLOTS):
        addr = BAND_READ_BASE + s * COMBAT_STRIDE
        lvl = u8(h, addr + A_LEVEL); br = u8(h, addr + A_BRAVE); fa = u8(h, addr + A_FAITH)
        hp = u16(h, addr + A_HP); mhp = u16(h, addr + A_MAXHP)
        gx = u8(h, addr + A_GX); gy = u8(h, addr + A_GY)
        ok, reason = band_valid(h, addr)
        if lvl in (None, 0) and (mhp in (None, 0)):
            continue   # empty slot, skip noise
        flag = "VALID" if ok else f"reject: {reason}"
        print(f"  slot {s:>2} @{addr:012X}: lvl={lvl} br={br} fa={fa} hp={hp} maxHp={mhp} "
              f"pos=({gx},{gy})  {flag}")


def resolve_active(h):
    """Compact one-line resolution of the current active unit, mirroring the resolver gates.
    Returns (summary, fp) where fp is (level, brave, faith) of the chosen band entry or None."""
    level = u16(h, TURN_QUEUE + TQ_LEVEL)
    team = u16(h, TURN_QUEUE + TQ_TEAM)
    hp = u16(h, TURN_QUEUE + TQ_HP)
    maxhp = u16(h, TURN_QUEUE + TQ_MAXHP)
    if maxhp in (None, 0) or maxhp >= 2000 or level is None or level < 1 or level > 99:
        return f"tq(lvl={level} hp={hp} mhp={maxhp}) -> SANITY-BAIL (resolver FALSE)", None

    # band match (maxHp,hp,level) with twin filter
    cand = []
    for s in range(BAND_SLOTS):
        addr = BAND_READ_BASE + s * COMBAT_STRIDE
        if u16(h, addr + A_HP) != hp or u8(h, addr + A_LEVEL) != level:
            continue
        ok, _ = band_valid(h, addr)
        if not ok or u16(h, addr + A_MAXHP) != maxhp:
            continue
        gx = u8(h, addr + A_GX); gy = u8(h, addr + A_GY)
        cand.append((s, u8(h, addr + A_BRAVE), u8(h, addr + A_FAITH), gx, gy, gx != 0 or gy != 0))
    if not cand:
        return f"tq(lvl={level} hp={hp} mhp={maxhp} team={team}) -> NO band match (resolver FALSE)", None
    if any(c[5] for c in cand):
        cand = [c for c in cand if c[5]]
    s, br, fa, gx, gy, real = cand[0]
    hits = fingerprint_roster(h, level, br, fa)
    if not hits:
        return (f"tq(lvl{level} br{br} fa{fa} pos({gx},{gy})) -> band slot {s} but NO roster fp "
                f"(resolver FALSE)"), (level, br, fa)
    hands = hits[0][2]
    tracked = [x for x in hands if x in WEAPON_IDS] if WEAPON_IDS else hands
    verb = f"wields {tracked}" if tracked else f"NO tracked weapon (raw hands {hands})"
    return (f"tq(lvl{level} br{br} fa{fa} pos({gx},{gy})) -> roster {hits[0][1]} {verb}"), (level, br, fa)


def resolve_active_byname(h):
    """FFTHandsFree-style resolution: read the condensed nameId (0x14077D2A4) and join it to the
    roster nameId (+0x230); read the weapon off that slot's R-hand (+0x14). No band-HP match."""
    team = u16(h, HF_TQ_BASE + HF_TQ_TEAM)
    nid = u16(h, HF_COND_NAMEID)
    lvl = u16(h, HF_TQ_BASE + HF_TQ_LEVEL)
    hp = u16(h, HF_TQ_BASE + HF_TQ_HP)
    if nid in (None, 0) or nid > 4000:
        return f"HF[nid={nid} lvl={lvl} hp={hp} team={team}] -> no/invalid nameId"
    for s in range(ROSTER_SLOTS):
        base = ROSTER_BASE + s * ROSTER_STRIDE
        if u16(h, base + R_NAMEID) != nid:
            continue
        w = u16(h, base + R_RHAND)
        if w is None or w in EMPTY:
            return f"HF[nid={nid} team={team}] -> roster slot {s} UNARMED"
        tracked = (w in WEAPON_IDS) if WEAPON_IDS else None
        tag = " [tracked]" if tracked else (" [untracked]" if tracked is False else "")
        return f"HF[nid={nid} team={team}] -> roster slot {s} weapon {w}{tag}"
    return f"HF[nid={nid} team={team}] -> NO roster nameId match"


def cmd_watchweapon(h, weapon_id, seconds):
    """Lock onto the unit holding `weapon_id` (resolve its fp from the roster) and dump its band
    state every tick -- through a Jump. The point: find the DELAYED-ACTION signal. When the unit
    goes airborne does its band entry VANISH (absent = pending jump) or does a status/CT byte flag
    it? Prints on change: pos / hp / status(+0x45..+0x49) / CT(+0x09,+0x25), or ABSENT."""
    fp = None
    for s in range(ROSTER_SLOTS):
        base = ROSTER_BASE + s * ROSTER_STRIDE
        if u16(h, base + R_RHAND) == weapon_id:
            fp = (u8(h, base + R_LEVEL), u8(h, base + R_BRAVE), u8(h, base + R_FAITH))
            break
    if fp is None:
        print(f"no roster slot holds weapon {weapon_id} (run `roster`)."); return
    lvl, br, fa = fp
    print(f"watching weapon {weapon_id} fp(lvl{lvl}/br{br}/fa{fa}) for {seconds:.0f}s -- Jump now.\n")
    last = None
    t0 = time.time()
    while time.time() - t0 < seconds:
        # locate this unit's band entry by fp (level-drift tolerant, like the resolver)
        found = None
        for s in range(BAND_SLOTS):
            addr = BAND_READ_BASE + s * COMBAT_STRIDE
            albl = u8(h, addr + A_LEVEL)
            if albl is None or not (albl >= lvl and albl - lvl <= MAX_LEVEL_DRIFT):
                continue
            if u8(h, addr + A_BRAVE) != br or u8(h, addr + A_FAITH) != fa:
                continue
            found = (s, addr)
            break
        if found is None:
            summary = "ABSENT (no band entry -- airborne / pending action?)"
        else:
            s, addr = found
            gx = u8(h, addr + A_GX); gy = u8(h, addr + A_GY)
            hp = u16(h, addr + A_HP)
            st = [u8(h, addr + 0x45 + i) for i in range(5)]   # 0x45..0x49 status field
            ctt = u8(h, addr + 0x09); cts = u8(h, addr + 0x25)
            summary = (f"slot {s} pos=({gx},{gy}) hp={hp} "
                       f"status[45..49]={' '.join(f'{(x if x is not None else 0):02X}' for x in st)} "
                       f"ct(+09)={ctt} ct(+25)={cts}")
        acted = u8(h, ACTED)
        key = (acted, summary)
        if key != last:
            last = key
            print(f"  t={time.time() - t0:6.2f}s  acted={acted}  {summary}")
        time.sleep(0.08)
    print("\nwatch done.")


def cmd_trace(h, seconds):
    """Continuously print the Acted byte + active-unit resolution, ONE line per change. Run this,
    then Jump with the unit under test: we capture whether Jump sets Acted, and what the active
    unit / band position looks like through the ascend-land window (the Jump-latch diagnosis)."""
    print(f"tracing {seconds:.0f}s -- Jump now. (acted | resolution); prints on change.\n")
    last = None
    t0 = time.time()
    while time.time() - t0 < seconds:
        acted = u8(h, ACTED)
        ours, _ = resolve_active(h)
        hf = resolve_active_byname(h)
        key = (acted, ours, hf)
        if key != last:
            last = key
            print(f"  t={time.time() - t0:6.2f}s  acted={acted}")
            print(f"        OURS: {ours}")
            print(f"        {hf}")
        time.sleep(0.08)
    print("\ntrace done.")


def main():
    op = sys.argv[1] if len(sys.argv) > 1 else "active"
    if op not in ("active", "roster", "band", "trace", "watchweapon"):
        print(__doc__); return
    pid = find_pid(PROC)
    if not pid:
        print(f"{PROC} not running"); return
    h = k32.OpenProcess(PV, False, pid)
    if not h:
        print(f"OpenProcess failed err={C.get_last_error()}"); return
    try:
        if WEAPON_IDS:
            print(f"(loaded {len(WEAPON_IDS)} tracked weapon ids from meta.json)\n")
        else:
            print("(meta.json not found -- weapon-set tags omitted; verdict does not depend on them)\n")
        if op == "trace":
            cmd_trace(h, float(sys.argv[2]) if len(sys.argv) > 2 else 60)
        elif op == "watchweapon":
            cmd_watchweapon(h, int(sys.argv[2]), float(sys.argv[3]) if len(sys.argv) > 3 else 120)
        else:
            {"active": cmd_active, "roster": cmd_roster, "band": cmd_band}[op](h)
    finally:
        k32.CloseHandle(h)


if __name__ == "__main__":
    main()
