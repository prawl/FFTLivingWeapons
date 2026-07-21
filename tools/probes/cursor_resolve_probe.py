r"""LW-87 Phase 0 probe: WHY does the Attack card's cursor resolve go silent for whole battles?

Read-only. Replays ActorResolver.TryResolveCursorPlayer's exact algorithm from outside the
process every ~60ms and prints a verdict CHANGE line each time the outcome shifts, so a live
battle produces a tick-accurate tape of what the resolve would do and exactly why it refuses.

Background (the 2026-07-14 11:57 PROD blackout, log + flight tape in the LW-28 capture):
the row painted once at 11:57:17, reverted at 11:57:19 with "no cursor answer", and never
painted again across 3 more of Ramza's turns (3 kills, 3.5 minutes). The LW-55 tripwire's
per-battle dedup key (kind, rosterHand, bandWeapon) had already been burned by a routine
pre-battle hover, so the log cannot distinguish the three candidate mechanisms. This probe
exists to separate them.

Pre-registered hypotheses (H) and disconfirmers (D), per the build pipeline's Phase 0:
  H1 ambiguity-bail: the revolving mirror seat clones the turn owner WITH a real position,
     the twin filter keeps both, matched != addr bails. Signature: verdict AMBIGUOUS while
     TqTeam==0, all matched entries sharing one nameId.
  H2 mirror-only match: the TurnQueue tuple stops matching the REAL entry (hp divergence)
     and matches ONLY the frozen clone. Signature: verdict OK-or-gated with matchedSlot ==
     a dup-pair (0,0) member, or NOMATCH while the real seat's hp differs from TqHp.
  H3 bridge failure: band match fine, roster bridge (nameId + level drift + brave/faith)
     returns 0 or >1. Signature: verdict BRIDGE-FAIL.
  D1 (kills nameId dedup): AMBIGUOUS ticks where matched entries carry DISTINCT nameIds.
  D2 (kills the turn-flag pick): a dup-pair MIRROR member reading ATurnFlag==1 on a tick
     where the real member also reads 1 (Band.FlagOwner's doc already warns a frozen (0,0)
     mirror can carry a stale t=1 -- this measures the real-position mirror case).
  D3 (kills the actor-ptr pick): ActorPtr naming NEITHER dup member during the owner's turn.

Correlate with livingweapon.log's paint/revert lines by wall-clock ms (both sides stamp it).

Usage: python tools\probes\cursor_resolve_probe.py [seconds=300]
"""
import datetime
import sys
import time

sys.path.insert(0, str(__import__("pathlib").Path(__file__).resolve().parent))
from treasure_flags import rpm, _require_game

# --- Offsets.cs constants (1.5.1-confirmed; single source: LivingWeapon/Offsets.cs) ---
TURN_QUEUE   = 0x1407832A0
TQ_LEVEL, TQ_TEAM, TQ_HP, TQ_MAXHP = 0x00, 0x02, 0x0C, 0x10
ACTOR_PTR    = 0x14186AF68
COMBAT_ANCHOR = 0x141855CE0
STRIDE       = 0x200
BAND_ENTRY   = 0x1C
FRAME_READ_BASE = COMBAT_ANCHOR - 24 * STRIDE
BAND_READ_BASE  = COMBAT_ANCHOR + BAND_ENTRY - 24 * STRIDE
BAND_SLOTS   = 49
A_WEAPON, A_LEVEL, A_BRAVE, A_FAITH = 0x04, 0x0D, 0x0E, 0x10
A_HP, A_MAXHP, A_GX, A_GY = 0x14, 0x16, 0x33, 0x34
A_TURNFLAG, A_NAMEID = 0x19C, 0x1E0
ROSTER_BASE, ROSTER_STRIDE, ROSTER_SLOTS = 0x1411A7D10, 0x258, 50
R_RHAND, R_LEVEL, R_BRAVE, R_FAITH, R_NAMEID = 0x14, 0x1D, 0x1E, 0x1F, 0x230
MAX_LEVEL_DRIFT = 9
SENTINELS = {0, 0xFF, 0xFFFF}


def u16(b, off):
    return b[off] | (b[off + 1] << 8)


def read_band():
    """One walk: list of dicts for every band slot that passes Band.IsValid."""
    units = []
    for s in range(BAND_SLOTS):
        e = BAND_READ_BASE + s * STRIDE
        b = rpm(e, 0x200)
        if not b:
            continue
        lvl, br, fa = b[A_LEVEL], b[A_BRAVE], b[A_FAITH]
        mhp = u16(b, A_MAXHP)
        gx, gy = b[A_GX], b[A_GY]
        # Band.IsValid, verbatim
        if not (1 <= lvl <= 99 and 1 <= br <= 100 and 1 <= fa <= 100
                and 1 <= mhp < 2000 and gx <= 30 and gy <= 30):
            continue
        units.append(dict(slot=s, addr=e, lvl=lvl, br=br, fa=fa, hp=u16(b, A_HP),
                          mhp=mhp, gx=gx, gy=gy, weapon=u16(b, A_WEAPON),
                          tflag=b[A_TURNFLAG], nameId=u16(b, A_NAMEID)))
    return units


def bridge_count(name_id, level, br, fa):
    """TryBridgeCursorToRoster, verbatim: exactly-one agreeing roster slot required."""
    n = 0
    hand = -1
    for s in range(ROSTER_SLOTS):
        base = ROSTER_BASE + s * ROSTER_STRIDE
        b = rpm(base, 0x240)
        if not b:
            continue
        rlvl = b[R_LEVEL]
        if not (1 <= rlvl <= 99):
            continue
        if u16(b, R_NAMEID) != name_id:
            continue
        if not (level >= rlvl and level - rlvl <= MAX_LEVEL_DRIFT):
            continue
        if b[R_BRAVE] != br or b[R_FAITH] != fa:
            continue
        n += 1
        hand = u16(b, R_RHAND)
    return n, hand


def resolve(units, tq):
    """Replay TryResolveCursorPlayer + CursorGate. Returns (verdictClass, detail)."""
    if tq is None:
        return "TQ-UNREADABLE", ""
    level, team, hp, mhp = tq
    if team != 0:
        return "NOT-PLAYER-TURN", f"team={team}"
    if mhp == 0 or mhp >= 2000 or not (1 <= level <= 99):
        return "TQ-GARBAGE", f"lvl={level} hp={hp}/{mhp}"

    fits = [u for u in units if u["mhp"] == mhp and u["hp"] == hp and u["lvl"] == level]
    # twin filter, verbatim shape (real-position preferred, (0,0) dropped when a real exists)
    matched = None
    found_real = False
    for u in fits:
        real = u["gx"] != 0 or u["gy"] != 0
        if found_real and not real:
            continue
        if real and not found_real and matched is not None:
            matched = None
            found_real = True
        if real:
            found_real = True
        if matched is None:
            matched = u
        elif matched["slot"] != u["slot"]:
            return "AMBIGUOUS", fmt_fits(fits)
    if matched is None:
        return "NO-MATCH", f"tq=({level},{hp}/{mhp}) " + fmt_fits(fits)
    if matched["nameId"] == 0:
        return "NAMEID-ZERO", f"slot={matched['slot']}"
    n, hand = bridge_count(matched["nameId"], level, matched["br"], matched["fa"])
    if n != 1:
        return "BRIDGE-FAIL", f"slot={matched['slot']} nameId={matched['nameId']} rosterMatches={n}"
    if matched["tflag"] != 1:
        return "GATE-NotTurnOwner", f"slot={matched['slot']} hand={hand} bandW={matched['weapon']} tflag={matched['tflag']}"
    rs, bs = hand in SENTINELS, matched["weapon"] in SENTINELS
    if (rs != bs) or (not rs and hand != matched["weapon"]):
        return "GATE-WeaponMismatch", f"slot={matched['slot']} hand={hand} bandW={matched['weapon']}"
    return "OK", f"slot={matched['slot']} weapon={hand}"


def fmt_fits(fits):
    return "fits=[" + ", ".join(
        f"s{u['slot']} id{u['nameId']} hp{u['hp']} ({u['gx']},{u['gy']}) t{u['tflag']} w{u['weapon']}"
        for u in fits) + "]"


def flag_owner(units):
    """Band.FlagOwner, verbatim replay: IsValid (done in read_band), t==1, real position
    mandatory, same-identity dedup (nameId when either is nonzero, else the fp triple),
    two disagreeing identities -> ambiguous."""
    found = None
    for u in units:
        if u["tflag"] != 1:
            continue
        if u["gx"] == 0 and u["gy"] == 0:
            continue
        if found is None:
            found = u
            continue
        if found["nameId"] == 0 and u["nameId"] == 0:
            same = (found["lvl"], found["br"], found["fa"]) == (u["lvl"], u["br"], u["fa"])
        else:
            same = found["nameId"] == u["nameId"]
        if not same:
            return None, "ambiguous"
    return found, None


def flag_resolve(units):
    """Phase 0b (review blocker B1): replay the PLANNED resolve (Band.FlagOwner + the roster
    bridge + gate A) so every tick carries both the shipped verdict and the would-be verdict.
    The decisive question: does FLAG-OK hold through the owner's T-status detour while the
    shipped verdict flips to GATE-NotTurnOwner?"""
    owner, err = flag_owner(units)
    if err:
        return "FLAG-AMBIG", ""
    if owner is None:
        return "FLAG-NO-OWNER", ""
    if owner["nameId"] == 0:
        return "FLAG-NAMEID-0", f"slot={owner['slot']}"
    n, hand = bridge_count(owner["nameId"], owner["lvl"], owner["br"], owner["fa"])
    if n != 1:
        return "FLAG-BRIDGE-FAIL", f"slot={owner['slot']} nameId={owner['nameId']} rosterMatches={n}"
    rs, bs = hand in SENTINELS, owner["weapon"] in SENTINELS
    if (rs != bs) or (not rs and hand != owner["weapon"]):
        return "FLAG-GATE-WeaponMismatch", f"slot={owner['slot']} hand={hand} bandW={owner['weapon']}"
    return "FLAG-OK", f"slot={owner['slot']} weapon={hand}"


def actor_slot():
    b = rpm(ACTOR_PTR, 8)
    if not b:
        return None
    ptr = int.from_bytes(b, "little")
    if ptr == 0 or ptr < FRAME_READ_BASE:
        return None
    d = ptr - FRAME_READ_BASE
    if d % STRIDE:
        return None
    s = d // STRIDE
    return s if s < BAND_SLOTS else None


def dup_pairs(units):
    """All (nameId -> slots) groups with >1 member: the mirror observatory."""
    by_id = {}
    for u in units:
        by_id.setdefault(u["nameId"], []).append(u)
    return {i: us for i, us in by_id.items() if len(us) > 1 and i != 0}


def main():
    secs = int(sys.argv[1]) if len(sys.argv) > 1 else 300
    _require_game()
    print(f"cursor_resolve_probe: watching {secs}s (read-only). Play a battle; open unit "
          "menus; the money ticks are refusals during YOUR OWN unit's open menu.")
    last_sig = None
    last_dups = None
    counts = {}
    fcounts = {}
    fix_wins = 0
    fix_regressions = 0
    ambiguous_nameid_diff = 0
    mirror_t1 = 0
    t0 = time.time()
    while time.time() - t0 < secs:
        now = datetime.datetime.now().strftime("%H:%M:%S.%f")[:-3]
        tqb = rpm(TURN_QUEUE, 0x12)
        tq = (u16(tqb, TQ_LEVEL), u16(tqb, TQ_TEAM), u16(tqb, TQ_HP), u16(tqb, TQ_MAXHP)) if tqb else None
        units = read_band()
        verdict, detail = resolve(units, tq)
        counts[verdict] = counts.get(verdict, 0) + 1
        fverdict, fdetail = flag_resolve(units)
        fcounts[fverdict] = fcounts.get(fverdict, 0) + 1
        if verdict != "OK" and fverdict == "FLAG-OK":
            fix_wins += 1
        if verdict == "OK" and fverdict != "FLAG-OK":
            fix_regressions += 1
        act = actor_slot()

        dups = dup_pairs(units)
        dup_sig = tuple(sorted((i, tuple(sorted(u["slot"] for u in us))) for i, us in dups.items()))
        if dup_sig != last_dups:
            for i, us in dups.items():
                line = " | ".join(
                    f"s{u['slot']} hp{u['hp']}/{u['mhp']} ({u['gx']},{u['gy']}) t{u['tflag']} w{u['weapon']}"
                    for u in us)
                print(f"{now} [dup] nameId={i}: {line}  actorSlot={act}", flush=True)
                if sum(1 for u in us if u["tflag"] == 1) > 1:
                    mirror_t1 += 1
                    print(f"{now} [dup] *** D2 SIGNAL: multiple t=1 in one dup group ***", flush=True)
            last_dups = dup_sig

        sig = (verdict, detail, fverdict, fdetail)
        if sig != last_sig:
            print(f"{now} [verdict] {verdict} {detail} | {fverdict} {fdetail}  actorSlot={act}", flush=True)
            if verdict == "AMBIGUOUS":
                ids = {u["nameId"] for u in units if u["mhp"] == tq[3] and u["hp"] == tq[2] and u["lvl"] == tq[0]}
                if len(ids) > 1:
                    ambiguous_nameid_diff += 1
                    print(f"{now} [verdict] *** D1 SIGNAL: ambiguous with DISTINCT nameIds {ids} ***", flush=True)
            last_sig = sig
        time.sleep(0.06)

    print("--- summary ---")
    print("  shipped resolve:")
    for k in sorted(counts, key=counts.get, reverse=True):
        print(f"    {k}: {counts[k]} ticks")
    print("  planned flag resolve:")
    for k in sorted(fcounts, key=fcounts.get, reverse=True):
        print(f"    {k}: {fcounts[k]} ticks")
    print(f"  fix wins (shipped refused, flag OK): {fix_wins} ticks")
    print(f"  fix REGRESSIONS (shipped OK, flag refused): {fix_regressions} ticks")
    print(f"  D1 ambiguous-with-distinct-nameIds events: {ambiguous_nameid_diff}")
    print(f"  D2 multiple-t1-in-dup-group events: {mirror_t1}")


if __name__ == "__main__":
    main()
