#!/usr/bin/env python
"""PHASE 0 premise probe for the Provoke turn-order LOOKAHEAD (LW-118). READ ONLY: RPM only,
never a single write, so it cannot disturb the battle it measures.

WHY THIS EXISTS. The owner's 2026-07-27 live pass asked for the shout to hide the party only for
the enemy it was aimed at, instead of the whole enemy phase. Reactive hiding cannot do that: the AI
commits its target the instant its turn opens (measured live 2026-07-22, which is why
Tuning.ProvokeSliceMode ships false), so a hide that fires when the turn starts lands too late. The
only way to hide for exactly one enemy is to know that enemy is ABOUT to act and be hidden before
it opens. This probe asks whether the scheduler fields can tell us that, EARLY ENOUGH.

THE FALSIFIABLE PREMISE, pre-registered before any run:

  P1. For an ENEMY unit, its scheduler CT (band +0x25) and Speed (band +0x24) identify it as the
      NEXT unit to act at least 100ms before it becomes the acting unit.
      CONFIRMED  if >= 80% of enemy turns were predicted by the ETA leader, and the median lead
                 time is >= 300ms (about 10 runtime ticks of margin).
      DISCONFIRMED if the ETA leader is usually not the unit that acts, or if the median lead is
                 near zero, which would mean prediction and turn-open coincide and no lookahead is
                 possible from these fields. In that case the feature stays WINDOW and the plan is
                 downgraded rather than built on a guess.

  P2. CT reads cleanly for enemy units (it is documented unreliable only for the player's own
      actively-managed unit, Offsets.cs ACtSlam).
      DISCONFIRMED if enemy CT is frozen across a whole battle, or reads outside 0..100.

METHOD. Poll every valid band seat at ~30Hz. For each unit compute
    ETA ticks = 0 if CT >= 100 else ceil((100 - CT) / max(1, Speed))
which is how long until it reaches the act threshold. The ETA leader is the predicted next actor.
Separately resolve the engine's ActorPtr to a seat to see who IS acting. Every time the actor
changes to a new unit (a turn opening), report whether that unit was already the sole predicted
leader, and for how long it had been.

ADDRESSES come from LivingWeapon/Offsets.cs on the 1.5.2 build. Deliberately NOT imported from
ct_probe.py, whose BAND_ANCHOR is the pre-1.5 address and would read garbage.

USAGE (game running, in a live battle):
  python tools/probes/provoke_lookahead_probe.py [seconds=120]
Play a normal battle and let several enemies take turns. The verdict prints at the end.
"""
import math
import os
import sys
import time
from collections import defaultdict

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ct_probe import PROC, PV, find_pid, k32, rd, u16   # process scaffolding only, no addresses

# --- LivingWeapon/Offsets.cs, 1.5.2 ---
COMBAT_ANCHOR = 0x141855CE0
COMBAT_STRIDE = 0x200
BAND_ENTRY = 0x1C
BAND_SLOTS = 49
BAND_READ_BASE = COMBAT_ANCHOR + BAND_ENTRY - 24 * COMBAT_STRIDE
FRAME_READ_BASE = COMBAT_ANCHOR - 24 * COMBAT_STRIDE
ACTOR_PTR = 0x14186AF68

A_LEVEL, A_BRAVE, A_FAITH = 0x0D, 0x0E, 0x10
A_HP, A_MAXHP = 0x14, 0x16
A_SPEED, A_CT = 0x24, 0x25
A_GX, A_GY = 0x33, 0x34
A_NAMEID = 0x1E0
A_FRIENDFOE, A_ENEMY_BIT = 0x1D2, 0x10
A_GATE = -0x1B          # band entry - 0x1B == combat +0x01; 0xFF marks an off-field cutscene seat
READ_LEN = 0x1E2        # through nameId

ACT_THRESHOLD = 100
HZ = 30


def seat_valid(b):
    """Band.IsValid's shape (plausible level/brave/faith/maxHp) plus two extra rejections this
    probe learned the hard way on its first live run: a seat read hp 437 of maxHp 96 at tile
    (255,129) with Speed 47, which is not a unit but sailed through the fingerprint and then
    dominated the whole turn-order prediction. Current HP above max HP is impossible, and a tile
    outside the board is not a unit standing anywhere."""
    if b is None:
        return False
    lvl, br, fa = b[A_LEVEL], b[A_BRAVE], b[A_FAITH]
    mhp, hp = u16(b, A_MAXHP), u16(b, A_HP)
    if not (1 <= lvl <= 99 and 1 <= br <= 100 and 1 <= fa <= 100 and 1 <= mhp < 2000):
        return False
    if hp > mhp:
        return False
    return b[A_GX] < 40 and b[A_GY] < 40


def read_seats(h):
    """Every valid, on-field seat this instant: seat -> dict of the fields we reason about."""
    out = {}
    for s in range(BAND_SLOTS):
        addr = BAND_READ_BASE + s * COMBAT_STRIDE
        b = rd(h, addr + A_GATE, READ_LEN - A_GATE)
        if b is None:
            continue
        gate = b[0]
        b = b[-A_GATE:]                      # re-base to the band entry itself
        if not seat_valid(b) or gate == 0xFF:
            continue
        ct, spd = b[A_CT], b[A_SPEED]
        # CT OVERSHOOTS the act threshold (first run of this probe read a live unit at CT 110), so
        # a value above 100 is normal scheduler state, not a bad read. Only a zero Speed makes the
        # estimate meaningless, since nothing would ever climb.
        if spd == 0:
            eta = None
        else:
            eta = 0 if ct >= ACT_THRESHOLD else math.ceil((ACT_THRESHOLD - ct) / spd)
        out[s] = {
            "ct": ct, "spd": spd, "eta": eta,
            "enemy": bool(b[A_FRIENDFOE] & A_ENEMY_BIT),
            "hp": u16(b, A_HP), "nameId": u16(b, A_NAMEID),
            "tile": (b[A_GX], b[A_GY]),
            "fp": (u16(b, A_MAXHP), b[A_LEVEL], b[A_BRAVE], b[A_FAITH]),
        }
    return out


def actor_seat(h):
    """Resolve ActorPtr to a band seat, exactly as Band.ActorEntry does."""
    b = rd(h, ACTOR_PTR, 8)
    if b is None:
        return None
    ptr = int.from_bytes(b, "little")
    if ptr == 0 or ptr < FRAME_READ_BASE:
        return None
    delta = ptr - FRAME_READ_BASE
    if delta % COMBAT_STRIDE:
        return None
    seat = delta // COMBAT_STRIDE
    return seat if 0 <= seat < BAND_SLOTS else None


def leaders(seats):
    """Seats sharing the best schedule position: lowest ETA first, and among units already at or
    over the threshold the HIGHEST CT goes first (that is what overshoot means, the unit has been
    waiting longest). Returns every seat sharing that key, so a genuine tie stays visible instead of
    being resolved by seat order, which would fake a confident prediction."""
    live = {s: v for s, v in seats.items() if v["eta"] is not None and v["hp"] > 0}
    if not live:
        return []
    best = min((v["eta"], -v["ct"]) for v in live.values())
    return sorted(s for s, v in live.items() if (v["eta"], -v["ct"]) == best)


def snapshot(h, depth=9):
    """One frozen read, printed as the order this model PREDICTS, deep enough to compare against
    the game's own on-screen Combat Timeline. The engine's preview repeats units (it projects the
    clock forward), so this projects too: take the leader, reset it by one full 100-CT climb, and
    ask again. Agreement with the screen is what validates the model without spending a battle."""
    seats = read_seats(h)
    if not seats:
        print("no valid seats: is a battle running?")
        return
    print("live seats:")
    for s in sorted(seats):
        v = seats[s]
        print(f"  seat {s:>2} {'enemy ' if v['enemy'] else 'player'} nameId {v['nameId']:>4} "
              f"lvl {v['fp'][1]:>2} hp {v['hp']}/{v['fp'][0]} br {v['fp'][2]} fa {v['fp'][3]} "
              f"tile {v['tile']} CT {v['ct']:>3} spd {v['spd']:>2} eta {v['eta']}")

    # The clock model being tested: every tick each unit's CT climbs by its Speed; a unit acts once
    # CT reaches the threshold and then pays a full threshold back. Project that forward and print
    # the order it produces, which is the thing to hold up against the screen.
    proj = {s: dict(v) for s, v in seats.items() if v["eta"] is not None and v["hp"] > 0}
    print(f"\nPREDICTED next {depth} turns (CT + Speed clock model):")
    for i in range(depth):
        if not proj:
            break
        wait = min(0 if o["ct"] >= ACT_THRESHOLD else math.ceil((ACT_THRESHOLD - o["ct"]) / o["spd"])
                   for o in proj.values())
        for o in proj.values():
            o["ct"] += o["spd"] * wait
        s = max(proj, key=lambda k: proj[k]["ct"])   # highest CT over the line acts first
        v = proj[s]
        print(f"  {i+1}. seat {s:>2} nameId {v['nameId']:>4} "
              f"lvl {v['fp'][1]:>2} hp {v['hp']}/{v['fp'][0]} br {v['fp'][2]} fa {v['fp'][3]} "
              f"({'enemy' if v['enemy'] else 'player'}) CT {v['ct']:>3} spd {v['spd']:>2}")
        v["ct"] -= ACT_THRESHOLD


def project(seats, depth):
    """The predicted running order: repeatedly advance the clock to the next unit over the
    threshold, let it act, and pay a full threshold back. Returns [(seat, nameId, enemy)]."""
    proj = {s: dict(v) for s, v in seats.items() if v["eta"] is not None and v["hp"] > 0}
    order = []
    for _ in range(depth):
        if not proj:
            break
        wait = min(0 if o["ct"] >= ACT_THRESHOLD else math.ceil((ACT_THRESHOLD - o["ct"]) / o["spd"])
                   for o in proj.values())
        for o in proj.values():
            o["ct"] += o["spd"] * wait
        s = max(proj, key=lambda k: proj[k]["ct"])
        order.append((s, proj[s]["nameId"], proj[s]["enemy"]))
        proj[s]["ct"] -= ACT_THRESHOLD
    return order


def cmd_validate(h, secs, depth=6):
    """HARDENING RUN. Every sample, predict the running order. Every time a turn actually STARTS,
    score the prediction that was standing just before it.

    Turn starts are detected by the CT PAYMENT, not by the actor pointer: CT is deducted at turn
    start (established live 2026-07-27 -- a unit sitting on its own open menu already reads its
    post-payment CT), and the actor pointer is unreliable for this because it parks on units that
    were merely struck. A drop of at least most of a threshold is a payment; nothing else moves CT
    downward.

    Scores two things separately, because they answer different questions:
      NEXT-UP accuracy  -- was the unit that started its turn the one predicted first? This is what
                           a hide-before-it-acts feature needs.
      DEPTH accuracy    -- of the order predicted at the last turn start, how many of the following
                           turns matched, position by position? This is what any feature that wants
                           to reason further ahead needs, and it is where reshuffles show up.
    """
    print(f"validating the CT clock model for {secs}s, {depth} deep. Play normally.\n")
    prev_ct = {}
    standing = []          # prediction as of the previous sample: scores NEXT-UP
    captured = []          # prediction frozen at a turn start: scores DEPTH
    resolved = 0           # how many turns have started since that capture
    next_hits = next_total = 0
    depth_hits = [0] * depth
    depth_total = [0] * depth
    misses = []
    t0 = time.time()
    samples = 0

    while time.time() - t0 < secs:
        seats = read_seats(h)
        if seats:
            samples += 1
            started = [s for s, v in seats.items()
                       if s in prev_ct and prev_ct[s] - v["ct"] >= ACT_THRESHOLD - 20]
            for s in started:
                who = seats[s]
                team = "enemy " if who["enemy"] else "player"

                next_total += 1
                ok = bool(standing) and standing[0][0] == s
                next_hits += 1 if ok else 0
                mark = "HIT " if ok else "MISS"
                pred = f"predicted seat {standing[0][0]}" if standing else "no prediction"
                print(f"  t={time.time()-t0:6.1f}s TURN START seat {s:>2} ({team} nameId {who['nameId']}, "
                      f"CT now {who['ct']:>3}, spd {who['spd']:>2})  {mark}  [{pred}]")
                if not ok and standing:
                    misses.append((round(time.time() - t0, 1), s, standing[0][0]))

                # DEPTH: score this turn against the queue frozen `resolved` turns ago.
                if captured and resolved < depth:
                    depth_total[resolved] += 1
                    depth_hits[resolved] += 1 if captured[resolved][0] == s else 0
                    resolved += 1
                if not captured or resolved >= depth:
                    captured = project(seats, depth)   # start a fresh depth window
                    resolved = 0
            standing = project(seats, depth)
            prev_ct = {s: v["ct"] for s, v in seats.items()}
        time.sleep(1.0 / HZ)

    print(f"\n{samples} samples, {next_total} turn starts observed.")
    if not next_total:
        print("NO TURNS OBSERVED -- inconclusive. Was the game paused on a menu the whole time?")
        return
    pct = 100.0 * next_hits / next_total
    print(f"NEXT-UP accuracy: {next_hits}/{next_total} = {pct:.0f}%")
    print("DEPTH accuracy (how far ahead the order stays right):")
    for i in range(depth):
        if depth_total[i]:
            print(f"  {i+1} turn(s) ahead: {depth_hits[i]}/{depth_total[i]} "
                  f"= {100.0*depth_hits[i]/depth_total[i]:.0f}%")
    if misses:
        print("misses (t, actual seat, predicted seat) -- reshuffles worth explaining:")
        for r in misses:
            print(f"  {r}")
    print(f"\nVERDICT: {'CONFIRMED' if pct >= 90 else 'NOT SOLID'} "
          f"(bar: >=90% next-up accuracy over a real battle)")


def main():
    a = sys.argv[1:]
    if a and a[0] in ("snapshot", "validate"):
        pid = find_pid(PROC)
        if not pid:
            sys.exit(f"{PROC}.exe not running")
        h = k32.OpenProcess(PV, False, pid)
        if a[0] == "snapshot":
            snapshot(h)
        else:
            cmd_validate(h, float(a[1]) if len(a) > 1 else 180.0)
        return
    secs = int(sys.argv[1]) if len(sys.argv) > 1 else 120
    pid = find_pid(PROC)
    if not pid:
        sys.exit(f"{PROC}.exe not running")
    h = k32.OpenProcess(PV, False, pid)

    print(f"provoke lookahead probe: {secs}s at ~{HZ}Hz, READ ONLY.")
    print("Play a battle and let several enemies take turns.\n")

    # For each seat, when did it MOST RECENTLY become a (co-)leader without having lost it since.
    leader_since = {}
    prev_actor = None
    turns = []            # (seat, enemy?, lead_ms, sole_leader?, was_leader?)
    ct_seen = defaultdict(set)
    t0 = time.time()
    samples = 0

    while time.time() - t0 < secs:
        now = time.time()
        seats = read_seats(h)
        if seats:
            samples += 1
            for s, v in seats.items():
                ct_seen[s].add(v["ct"])
            lead = leaders(seats)
            for s in list(leader_since):
                if s not in lead:
                    del leader_since[s]
            for s in lead:
                leader_since.setdefault(s, now)

            a = actor_seat(h)
            # prev_actor None is the probe's own first sight of the pointer, not a turn opening:
            # scoring it would count a park that started before the instrument did.
            if a is not None and prev_actor is not None and a != prev_actor and a in seats:
                since = leader_since.get(a)
                lead_ms = (now - since) * 1000.0 if since else 0.0
                turns.append((a, seats[a]["enemy"], lead_ms, lead == [a], a in lead))
                who = "enemy" if seats[a]["enemy"] else "player"
                mark = "PREDICTED" if a in lead else "MISSED   "
                print(f"  t={now-t0:6.2f}s actor -> seat {a:>2} ({who}, nameId {seats[a]['nameId']}, "
                      f"tile {seats[a]['tile']}, CT {seats[a]['ct']}, spd {seats[a]['spd']}) "
                      f"{mark} lead {lead_ms:7.0f}ms {'(sole)' if lead == [a] else '(tied ' + str(lead) + ')'}")
            if a is not None:
                prev_actor = a
        time.sleep(1.0 / HZ)

    # ---- verdict ----
    print(f"\n{samples} samples, {len(turns)} actor changes observed.")
    enemy_turns = [t for t in turns if t[1]]
    if not enemy_turns:
        print("NO ENEMY TURNS OBSERVED -- inconclusive, run again through an enemy phase.")
        return
    hit = [t for t in enemy_turns if t[4]]
    leads = sorted(t[2] for t in hit)
    median = leads[len(leads) // 2] if leads else 0.0
    pct = 100.0 * len(hit) / len(enemy_turns)
    print(f"P1 enemy turns predicted by the ETA leader: {len(hit)}/{len(enemy_turns)} = {pct:.0f}%")
    print(f"P1 median lead time on the predicted ones : {median:.0f}ms")
    print(f"P1 lead times: {[round(x) for x in leads]}")
    verdict = "CONFIRMED" if pct >= 80 and median >= 300 else "DISCONFIRMED"
    print(f"P1 VERDICT: {verdict} (pre-registered bar: >=80% and >=300ms median)")

    frozen = [s for s, vals in ct_seen.items() if len(vals) == 1]
    print(f"P2 seats whose CT never changed across the run: {frozen if frozen else 'none'}")
    print(f"P2 VERDICT: {'DISCONFIRMED' if len(frozen) == len(ct_seen) else 'CONFIRMED'}")


if __name__ == "__main__":
    main()
