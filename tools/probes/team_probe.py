"""Find a clean ALLY/ENEMY ("team") byte on the LIVE band entry (0x14184xxxx family).

Why: the adjacency-aura staff signatures (Communion = share your support with neighbours,
Wellspring = Regen aura) walk band slots next to the wielder and OR-set a buff bit on each.
That is only safe if we can tell friend from foe -- OR-setting onto an adjacent ENEMY hands the
buff to the wrong side. The only team byte we know (TqTeam +0x02) lives on the CONDENSED struct,
which the runtime distrusts (it follows the cursor, not the turn owner). The BAND -- the live copy
we trust for positions/status -- has NO mapped team byte. This probe finds one.

Method: the roster (brave,faith) fingerprint is GROUND TRUTH -- a band unit whose (brave,faith)
matches a player roster slot is an ally; one that matches none is an enemy (the same split
give_enemy_buffs.py already uses). With each band unit labelled, we auto-scan EVERY band-entry
offset for the byte whose ally-values and enemy-values are DISJOINT -- ideally a tidy 0=friend /
1=foe split like the condensed +0x02. Read-only; cannot hurt the game.

USAGE (game running, in a live battle):
  python team_probe.py               # discover: ranked candidate offsets + per-slot table
  python team_probe.py watch <off> [sec=15] [hz=6]
        # watch ONE band offset across all slots over time -- confirms a candidate doesn't
        # PULSE/drift (the inb-flag trap: some bytes flip 0/1 per unit mid-battle). A real team
        # byte holds steady per unit across its turns; a pulsing one is a false lead.

CAVEATS (read before trusting the output):
  - GUEST units (AI-controlled allies not in the roster) fingerprint as ENEMY here and will
    pollute the split -- run a guest-free battle for the cleanest read, or eyeball the table.
  - (0,0) frozen twins (a roster unit's band mirror) are correctly labelled ally; they just
    duplicate a real-position ally -- harmless for the split.
  - A coincidental brave/faith collision (an enemy sharing a player's pair) mislabels one unit;
    the per-slot table lets you spot it against the on-screen battle.
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ct_probe import PROC, PV_W, find_pid, k32, rd

# --- roster (ground-truth player set) ---
ROSTER, RSTRIDE, RSLOTS = 0x1411A18D0, 0x258, 20
R_LVL, R_BRAVE, R_FAITH = 0x1D, 0x1E, 0x1F

# --- live band ---
ANCHOR, STRIDE, ENTRY = 0x14184F890, 0x200, 0x1C
BBASE, BSLOTS = ANCHOR + ENTRY - 24 * STRIDE, 49
WIN = 0x1F0   # bytes dumped per band entry (full 0x200 struct minus a hair, so a high team byte shows)

# band-entry validity fingerprint fields (== static-array A* layout)
A_LVL, A_OBRAVE, A_OFAITH, A_HP, A_MAXHP, A_GX, A_GY = 0x0D, 0x0E, 0x10, 0x14, 0x16, 0x33, 0x34

# Known fields in 0x00..0xBF -- a "candidate" landing here is identity/coincidence, not team.
KNOWN = {
    0x0C: "exp", 0x0D: "level", 0x0E: "origBrave", 0x0F: "brave", 0x10: "origFaith",
    0x11: "faith", 0x12: "inBattle.lo", 0x13: "inBattle.hi", 0x14: "HP.lo", 0x15: "HP.hi",
    0x16: "MaxHP.lo", 0x17: "MaxHP.hi", 0x18: "MP.lo", 0x19: "MP.hi", 0x1A: "MaxMP.lo",
    0x1B: "MaxMP.hi", 0x22: "PA", 0x23: "MA", 0x24: "Speed", 0x25: "CT", 0x26: "PAraw",
    0x27: "MAraw", 0x28: "WP", 0x2E: "C-EV", 0x32: "S-EV", 0x33: "gx", 0x34: "gy",
    0x45: "status0", 0x46: "status1", 0x47: "status2", 0x48: "status3", 0x49: "status4",
    0x78: "reaction0", 0x79: "reaction1", 0x7A: "reaction2", 0x7B: "reaction3",
    0x80: "move0", 0x81: "move1", 0x82: "move2",
}


def u16(b, o):
    return b[o] | (b[o + 1] << 8)


def player_set(h):
    """(brave,faith) of every populated roster slot -- the ground-truth ally set."""
    players = set()
    for r in range(RSLOTS):
        b = rd(h, ROSTER + r * RSTRIDE, 0x20)
        if b and 1 <= b[R_LVL] <= 99:
            players.add((b[R_BRAVE], b[R_FAITH]))
    return players


def scan_units(h, players):
    """Valid band units -> list of {slot, addr, ally, hp, mhp, lvl, gx, gy, raw}."""
    units = []
    for s in range(BSLOTS):
        addr = BBASE + s * STRIDE
        b = rd(h, addr, WIN)
        if not b:
            continue
        mhp, lvl, br, fa = u16(b, A_MAXHP), b[A_LVL], b[A_OBRAVE], b[A_OFAITH]
        if not (1 <= mhp < 2000 and 1 <= lvl <= 99 and 1 <= br <= 100 and 1 <= fa <= 100):
            continue
        units.append({
            "slot": s, "addr": addr, "ally": (br, fa) in players,
            "hp": u16(b, A_HP), "mhp": mhp, "lvl": lvl,
            "gx": b[A_GX], "gy": b[A_GY], "raw": b,
        })
    return units


def partition(units):
    """For each offset, the (ally_values, enemy_values) sets. Disjoint+both-nonempty = candidate."""
    cands = []
    for off in range(WIN):
        av = {u["raw"][off] for u in units if u["ally"]}
        ev = {u["raw"][off] for u in units if not u["ally"]}
        if av and ev and av.isdisjoint(ev):
            binary = len(av) == 1 and len(ev) == 1
            cands.append((off, av, ev, binary))
    # gold first: binary clean & not a known identity field; then by union size, then offset.
    cands.sort(key=lambda c: (not (c[3] and c[0] not in KNOWN), len(c[1]) + len(c[2]), c[0]))
    return cands


def fmt_vals(vals):
    return "{" + ",".join(f"0x{v:02X}" for v in sorted(vals)) + "}"


def slot_sign_report(units):
    """Is the team split STRUCTURAL -- allies all on one side of a slot boundary, enemies the
    other? That mirrors the static array (players n>=0, enemies n<0) and needs no team byte."""
    a = sorted(u["slot"] for u in units if u["ally"])
    e = sorted(u["slot"] for u in units if not u["ally"])
    print(f"slot-sign check: allies@{a}  enemies@{e}")
    if a and e and (max(e) < min(a) or max(a) < min(e)):
        lo, hi = (max(e), min(a)) if max(e) < min(a) else (max(a), min(e))
        side = "BELOW" if max(e) < min(a) else "ABOVE"
        print(f"  CLEAN boundary: every enemy slot is {side} every ally slot "
              f"(gap {lo}..{hi}). Team == which side of the anchor -> no team byte needed,\n"
              f"  IF this holds across battles/restarts. Confirm in 1-2 more battles "
              f"(varied party size, after a restart).")
    else:
        print("  NOT cleanly separated by slot index this frame -- a team BYTE is needed.")
    print()


def do_find(h):
    players = player_set(h)
    units = scan_units(h, players)
    allies = [u for u in units if u["ally"]]
    enemies = [u for u in units if not u["ally"]]
    print(f"roster players: {len(players)} fingerprints | band units: {len(units)} "
          f"({len(allies)} ally, {len(enemies)} enemy)\n")
    if not allies or not enemies:
        print("need BOTH an ally and an enemy on the band to partition -- "
              "open this in a live battle with enemies present.")
        return

    slot_sign_report(units)
    cands = partition(units)
    if not cands:
        print("NO offset cleanly splits ally vs enemy in 0x00..0x%02X.\n"
              "  -> the band may carry no team byte; fall back to the (level,brave,faith) roster\n"
              "     fingerprint (already robust in GrowthEngine.MatchesEntry) for ally detection." % (WIN - 1))
    else:
        print(f"candidate team offsets (ally-values disjoint from enemy-values):")
        for off, av, ev, binary in cands[:12]:
            tag = f"  [known: {KNOWN[off]}]" if off in KNOWN else ""
            star = " <== CLEAN BINARY" if binary and off not in KNOWN else ""
            print(f"  +0x{off:02X}  ally={fmt_vals(av):<14} enemy={fmt_vals(ev):<14}{tag}{star}")
        gold = [c for c in cands if c[3] and c[0] not in KNOWN]
        if gold:
            off = gold[0][0]
            print(f"\nbest bet: +0x{off:02X}. Confirm it holds steady (no pulse) with:\n"
                  f"  python team_probe.py watch 0x{off:02X}")
        print()

    # per-slot table for eyeball sanity-check against the on-screen battle
    print("per-slot (sanity-check team labels against the screen):")
    print("  slot  team   hp/mhp     lvl  pos      +05 +45  candidate-offset bytes")
    top = [c[0] for c in cands[:4]] if cands else []
    hdr = " ".join(f"+{o:02X}" for o in top)
    print(f"  {'':38}{hdr}")
    for u in sorted(units, key=lambda x: (not x["ally"], x["slot"])):
        b = u["raw"]
        cols = " ".join(f" {b[o]:02X}" for o in top)
        team = "ALLY " if u["ally"] else "enemy"
        print(f"  {u['slot']:4}  {team}  {u['hp']:4}/{u['mhp']:<4}  {u['lvl']:2}  "
              f"({u['gx']:2},{u['gy']:2})  {b[0x05]:02X}  {b[0x45]:02X}  {cols}")


TQ_BASE = 0x14077D2A0   # turn-queue / condensed list; each record has team @ +0x02
TQ_ANCHOR = 24          # band scan index of the anchor (BBASE is n=-24); n = s - 24


def tq_teams(h):
    """Walk the turn-queue records (FFTHandsFree ScanTurnQueue) -> {(level,maxHp): team}.
    team: 0=player, 1=enemy, 2=ally/NPC. Includes the active unit (slot 0, read directly)."""
    raw = rd(h, TQ_BASE, 0x4000)
    teams = {}
    if not raw:
        return teams
    # active unit sits at the head (no preceding FFFF marker) -- read it directly.
    a_lvl, a_team, a_mhp = u16(raw, 0x00), u16(raw, 0x02), u16(raw, 0x10)
    if 1 <= a_lvl <= 99 and 0 <= a_team <= 3 and 0 < a_mhp < 10000:
        teams[(a_lvl, a_mhp)] = a_team
    pos = 0
    while pos < len(raw) - 20:
        if raw[pos] == 0xFF and raw[pos + 1] == 0xFF:
            pos += 2
            if pos < len(raw) - 1 and raw[pos] == 0x00 and raw[pos + 1] == 0x00:
                pos += 2
            if pos < len(raw) - 24:
                lvl, team, mhp = u16(raw, pos), u16(raw, pos + 2), u16(raw, pos + 0x10)
                if 1 <= lvl <= 99 and 0 <= team <= 3 and 0 < mhp < 10000:
                    teams[(lvl, mhp)] = team
                pos += 0x18
                continue
        pos += 1
    return teams


def do_xcheck(h):
    """Triangulate the three team sources unit-for-unit: turn-queue +0x02 (FFTHandsFree's
    per-unit field), band slot-sign (slot >= anchor = player), roster fingerprint."""
    players = player_set(h)
    units = scan_units(h, players)
    tq = tq_teams(h)
    name = {0: "PLAYER", 1: "enemy", 2: "ALLY/NPC", 3: "team3"}
    print(f"turn-queue records parsed: {len(tq)} | band units: {len(units)}\n")
    print("  slot  pos      hp/mhp     | slot-sign | fingerprint | turnQ+0x02   agree?")
    disagree = 0
    for u in sorted(units, key=lambda x: x["slot"]):
        sign = "PLAYER" if u["slot"] >= TQ_ANCHOR else "enemy"
        fp = "PLAYER" if u["ally"] else "enemy"
        t = tq.get((u["lvl"], u["mhp"]))
        tqn = name.get(t, "?(absent)")
        # normalize for agreement: treat ALLY/NPC(2) as friendly, player(0) friendly, enemy(1) foe
        norm = {"PLAYER": "F", "enemy": "E"}
        tq_f = "F" if t in (0, 2) else "E" if t == 1 else "?"
        ok = norm[sign] == norm[fp] == tq_f
        if not ok:
            disagree += 1
        print(f"  {u['slot']:4}  ({u['gx']:2},{u['gy']:2})  {u['hp']:4}/{u['mhp']:<4}  | "
              f"{sign:6}    | {fp:6}      | {tqn:11} {'OK' if ok else '<-- MISMATCH'}")
    print(f"\n{len(units) - disagree}/{len(units)} units: all three sources agree." if not disagree
          else f"\n{disagree} unit(s) disagree -- inspect above.")
    print("If they agree across this + a couple more battles, the team-ID question is closed:\n"
          "  - turn-queue +0x02 = the robust per-unit field (keyed by level+maxHp to a band unit),\n"
          "  - band slot-sign = the cheap O(1) check (FFTHandsFree uses it for kill attribution),\n"
          "  - roster fingerprint = already in the runtime, safe-by-construction for an aura.")


def do_watch(h, off, sec, hz):
    import time
    players = player_set(h)
    print(f"watching band +0x{off:02X} for {sec}s @ {hz}Hz -- a real team byte holds steady per\n"
          f"unit; a value that flips 0/1 mid-battle is the inb-pulse trap (false lead).\n")
    history = {}   # slot -> set of values seen
    t_end = time.time() + sec
    while time.time() < t_end:
        units = scan_units(h, players)
        line = []
        for u in sorted(units, key=lambda x: (not x["ally"], x["slot"])):
            v = u["raw"][off]
            history.setdefault(u["slot"], set()).add(v)
            tag = "A" if u["ally"] else "e"
            line.append(f"{u['slot']}{tag}:{v:02X}")
        print("  " + " ".join(line))
        time.sleep(1.0 / hz)
    pulsing = {s: vs for s, vs in history.items() if len(vs) > 1}
    if pulsing:
        print(f"\nPULSED (value changed) on slots: " +
              ", ".join(f"{s}={fmt_vals(vs)}" for s, vs in sorted(pulsing.items())) +
              "\n  -> +0x%02X is NOT a stable team byte." % off)
    else:
        print(f"\nstable: every unit held one value at +0x%02X across the window. "
              f"Good team-byte candidate." % off)


def main():
    pid = find_pid(PROC)
    if not pid:
        sys.exit(f"{PROC} not running")
    h = k32.OpenProcess(PV_W, False, pid)
    try:
        if len(sys.argv) >= 2 and sys.argv[1] == "watch":
            off = int(sys.argv[2], 0)
            sec = int(sys.argv[3]) if len(sys.argv) > 3 else 15
            hz = int(sys.argv[4]) if len(sys.argv) > 4 else 6
            do_watch(h, off, sec, hz)
        elif len(sys.argv) >= 2 and sys.argv[1] == "xcheck":
            do_xcheck(h)
        else:
            do_find(h)
    finally:
        k32.CloseHandle(h)


if __name__ == "__main__":
    main()
