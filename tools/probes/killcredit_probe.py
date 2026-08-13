r"""LW-137 probe: does the kill-credit death-edge bury read a TURN or a CURSOR?

Read-only, passive. Run it alongside any normal play session; it needs no setup and changes
nothing. Every ~33ms (Engine.cs's own PollMs -- the actual engine tick rate) it walks the band
and, on each death EDGE (a seat's alive-debounced dead-streak going 0->1, the same edge
KillTracker.Corpses.cs fires on), records TWO columns side by side:

  Column A (SHIPPED): the exact read KillTracker.Corpses.cs:232 performs at that edge -- a
    FRESH TqTeam (TurnQueue+0x02, Readable-guarded else 0) read taken right at the edge, verdict
    nonPlayerTurn = team in (1, 2).
  Column B (PROPOSED): Band.FlagOwner's verbatim answer (LivingWeapon/Battle/Band.cs:171-204) -- the
    single real-position seat holding the per-unit turn flag (band +0x19C == 1), with FlagOwner's
    own identity-dedup (same nameId when both are nonzero, else the same (lvl,br,fa)) and its own
    ambiguity refusal (two DIFFERENT identities both holding the flag this tick). THEN,
    separately from FlagOwner itself, the winning seat's side (player/AI) is read off its
    friend/foe byte (band +0x1D2 bit 0x10).

Why: the 2026-07-27 Provoke pass produced a clean observation that TqTeam reads PLAYER for a
whole enemy turn when the AI targets a player unit, which is what a cursor-tracking field does
and what a turn-owner field cannot do (docs/PROVOKE_AC.md premise table; TODO LW-137). If that
holds at death edges too, the shipped bury can miss enemy-turn deaths (mis-credit) and can bury
a player kill scored while the cursor rests on an enemy. Nobody has MEASURED how often the two
columns disagree at real death edges; this probe is that measurement. The 20 banked flight
tapes were mined first (2026-07-28) and carry ZERO death edges (recent sessions were feature
drills), so the evidence has to come from live play.

Per-edge verdict classes, pre-registered:
  AGREE-PLAYER      both columns say a player turn was in progress (normal player kill shape).
  AGREE-AI          both say AI turn (normal enemy-turn death shape).
  A-AI-B-PLAYER     shipped says AI, flags say player: the shipped bury would EAT a player kill
                    (the cursor-resting-on-an-enemy direction).
  A-PLAYER-B-AI     shipped says player, flags say AI: the shipped bury would MISS an AI-turn
                    death (the Provoke-observed direction; a mis-credit risk).
  B-NO-OWNER        FlagOwner found no real-position seat holding the turn flag at all. This is
                    the general "nobody has it" state, NOT specifically the LW-138
                    action-resolution gap -- Band.cs's own doc calls a zero-owner read a real,
                    tape-verified case in its own right (the battle-opening edge), so this class
                    has more than one cause and the resolution gap is only one of them.
  B-AMBIGUOUS       FlagOwner refused because two DIFFERENT identities both held the flag this
                    tick (Band.FlagOwner's own miss-beats-mis-credit refusal). Counted separately
                    from B-NO-OWNER: a FlagOwner-based fix would need a different fallback for
                    "refused on conflict" than for "nobody has it".
Disagreement RATE = (A-AI-B-PLAYER + A-PLAYER-B-AI) / (AGREE-PLAYER + AGREE-AI + A-AI-B-PLAYER +
A-PLAYER-B-AI) -- i.e. only over edges where FlagOwner actually answered. B-NO-OWNER and
B-AMBIGUOUS ticks have no B side to agree or disagree with A, so they are excluded from this
rate's denominator, not folded into it. The discriminating verdict the ledger row wants is the
owner's to run (LIVE_LEDGER flip rules).

Alive/dead debounce and identity-drift handling mirror KillTracker.Corpses.cs's own state
machine: AliveNeeded=3 consecutive valid alive on-field ticks before a seat is edge-eligible
(:40, consumed :182-183); an IsValid failure resets only the streak counters, not the
remembered identity, so a seat can re-fire after a transient bad read instead of staying
silently latched (:132); an identity drift while alive resets a recycled seat's tracking so a
new occupant starts a fresh debounce, never inheriting an old edge (:172-180); the identity
guard on the DEAD side just bails without resetting, same as shipped (:214). onField is
approximated as battleMode in {2,3,4} (Offsets.BattleMode) -- the shipped OnField also requires
the engine's own debounced in/out BattleState (Engine.cs:373), which this passive external
reader does not reconstruct, so this probe's onField can read true a few seconds LATER than the
shipped value during the exit-debounce window. Documented gap, not a silent one.

Base: cursor_resolve_probe.py's constants + guarded-read pattern (the post-1.5.1 probe base;
the ct_probe-family slot-marker filters are dead, LW-93) -- its flag_owner is replayed verbatim
here. Constants mirror LivingWeapon/Offsets.cs; re-verify there after any game patch.

Usage: python tools\probes\killcredit_probe.py [seconds=600]
"""
import datetime
import sys
import time

sys.path.insert(0, str(__import__("pathlib").Path(__file__).resolve().parent))
from treasure_flags import rpm, _require_game

# --- Offsets.cs constants (single source: LivingWeapon/Offsets.cs; 1.5.2-current) ---
TURN_QUEUE = 0x1407832A0
TQ_TEAM = 0x02                      # u16: 0 player, 1 enemy, 2 ally/guest
COMBAT_ANCHOR = 0x141855CE0
STRIDE = 0x200
BAND_ENTRY = 0x1C
FRAME_READ_BASE = COMBAT_ANCHOR - 24 * STRIDE   # frame = combat struct; band = frame + 0x1C
BAND_SLOTS = 49
BATTLE_MODE = 0x1409069A0           # Offsets.BattleMode: u8, 2/3/4 = on the live battlefield
ALIVE_NEEDED = 3                    # Corpses.cs:40 AliveNeeded

# Frame-relative = 0x1C + band-relative (whole 0x200 frame read per seat). Deliberately no
# combat+0x01 gate byte here: the reviewer confirmed AGateByte appears nowhere in KillTracker's
# credit path (it's a Provoke-hold-only concern, Offsets.cs's AGateByte doc), so an earlier
# version of this probe skipping seats on that byte had no shipped-code counterpart and is gone.
F_LEVEL, F_BRAVE, F_FAITH = 0x1C + 0x0D, 0x1C + 0x0E, 0x1C + 0x10
F_HP, F_MAXHP = 0x1C + 0x14, 0x1C + 0x16
F_GX, F_GY = 0x1C + 0x33, 0x1C + 0x34
F_DEAD = 0x1C + 0x45                # ADeadStatus; bit 0x20 = Dead (Offsets.cs:218-219)
DEAD_BIT = 0x20
F_TFLAG = 0x1C + 0x19C              # per-unit turn flag (ATurnFlag; PSX turn flags row)
F_FOE = 0x1C + 0x1D2                # AFriendFoe; bit 0x10 set = AI side, clear = player side
FOE_BIT = 0x10
F_NAMEID = 0x1C + 0x1E0


def u16(b, off):
    return b[off] | (b[off + 1] << 8)


def read_seats():
    """One walk: raw fields for every band slot (index == slot), or None at slots that are
    unreadable or fail Band.IsValid's shape check. `dead` is Corpses.cs:143's own disjunction
    (hp==0 OR the Dead status bit), with the two disjuncts also kept separately so callers can
    report which one fired."""
    seats = [None] * BAND_SLOTS
    for s in range(BAND_SLOTS):
        base = FRAME_READ_BASE + s * STRIDE
        b = rpm(base, 0x200)
        if not b:
            continue
        lvl, br, fa = b[F_LEVEL], b[F_BRAVE], b[F_FAITH]
        mhp = u16(b, F_MAXHP)
        gx, gy = b[F_GX], b[F_GY]
        # Band.IsValid, verbatim shape (cursor_resolve_probe precedent)
        if not (1 <= lvl <= 99 and 1 <= br <= 100 and 1 <= fa <= 100
                and 1 <= mhp < 2000 and gx <= 30 and gy <= 30):
            continue
        hp = u16(b, F_HP)
        hp_zero = hp == 0
        dead_bit = bool(b[F_DEAD] & DEAD_BIT)
        seats[s] = dict(slot=s, lvl=lvl, br=br, fa=fa, mhp=mhp, gx=gx, gy=gy, hp=hp,
                         hp_zero=hp_zero, dead_bit=dead_bit,
                         dead=hp_zero or dead_bit,   # Corpses.cs:143 verbatim: isDead = hp==0 || deadBit
                         tflag=b[F_TFLAG], foe=bool(b[F_FOE] & FOE_BIT), nameId=u16(b, F_NAMEID))
    return seats


def shipped_team():
    """Column A: KillTracker.Corpses.cs:232 verbatim -- Readable-guarded TqTeam else 0. Called
    fresh at each detected edge (see main()), not once before the 49-seat walk: the walk itself
    takes measurable wall-clock time, so a once-per-sweep read could be stale relative to an edge
    found late in slot order."""
    b = rpm(TURN_QUEUE + TQ_TEAM, 2)
    return u16(b, 0) if b else 0


def flag_owner(seats):
    """Band.FlagOwner (LivingWeapon/Battle/Band.cs:171-204), replayed verbatim -- the same replay
    cursor_resolve_probe.py's flag_owner already exercises against this game build. Walks every
    non-None seat with ATurnFlag==1 AND a real (nonzero) position; the first such seat is the
    candidate, and every later candidate must share its identity (nameId when BOTH are nonzero,
    else the (lvl,br,fa) fingerprint) or the read refuses (miss beats mis-credit).

    Returns (winner_or_None, err): err == "ambiguous" means two DIFFERENT identities both held
    the flag this tick (B-AMBIGUOUS); err is None and winner is None means no real-position
    seat holds the flag at all (B-NO-OWNER); otherwise winner is the seat FlagOwner names."""
    found = None
    for rec in seats:
        if rec is None or rec["tflag"] != 1:
            continue
        if rec["gx"] == 0 and rec["gy"] == 0:
            continue   # D2b: a frozen (0,0) mirror is never a candidate, win or ambiguity
        if found is None:
            found = rec
            continue
        if found["nameId"] == 0 and rec["nameId"] == 0:
            same = (found["lvl"], found["br"], found["fa"]) == (rec["lvl"], rec["br"], rec["fa"])
        else:
            same = found["nameId"] == rec["nameId"]
        if not same:
            return None, "ambiguous"
    return found, None


def new_state():
    return dict(alive_streak=0, dead_streak=0, seen_alive=False, slot_id=None)


def step_seats(seats, states, on_field):
    """Mirrors KillTracker.Corpses.cs's ScanCorpses per-slot state machine for the fields this
    probe needs: the AliveNeeded debounce (:40, consumed :182-183), the identity guard that
    resets a recycled seat's tracking on an alive-side (lvl,br,fa) drift (:172-180), the
    IsValid-failure reset that zeroes ONLY the streak counters so a later valid dead read can
    still re-fire (:132), the dead-side identity bail with no reset (:214), and the onField-gated
    dead-streak edge (:145, :167-220). Mutates `states` in place. Returns the list of slots
    whose dead_streak just went 0->1 this tick (a fresh death EDGE)."""
    edges = []
    for s in range(BAND_SLOTS):
        st = states[s]
        rec = seats[s]
        if rec is None:
            # Corpses.cs:132 -- IsValid failure zeroes ONLY aliveStreak/deadStreak; seenAlive and
            # the remembered identity survive, so a transient bad read can never permanently
            # silence a seat -- it just re-earns its debounce on the next valid dead tick.
            st["alive_streak"] = 0
            st["dead_streak"] = 0
            continue

        identity = (rec["lvl"], rec["br"], rec["fa"])
        if not rec["dead"]:
            st["dead_streak"] = 0
            if not on_field:
                continue
            if st["seen_alive"] and st["slot_id"] != identity:
                # Corpses.cs:172-180 -- identity drifted while alive: a recycled seat (a new unit
                # took a vacated slot). Reset so the OLD identity's debounce can never manufacture
                # an edge for the NEW occupant; the new occupant starts its own AliveNeeded count.
                st["seen_alive"] = False
                st["alive_streak"] = 0
                st["slot_id"] = None
                continue
            st["alive_streak"] += 1
            if st["alive_streak"] >= ALIVE_NEEDED and not st["seen_alive"]:
                st["seen_alive"] = True
                st["slot_id"] = identity
            continue

        # dead path (hp==0 or Dead bit set)
        if not st["seen_alive"]:
            continue
        if st["slot_id"] != identity:
            continue   # Corpses.cs:214 -- identity mismatch on a dead read just bails, no reset
        if not on_field:
            continue
        st["dead_streak"] += 1
        if st["dead_streak"] == 1:
            edges.append(s)
    return edges


def main():
    seconds = int(sys.argv[1]) if len(sys.argv) > 1 else 600
    _require_game()
    print(f"killcredit_probe: watching {BAND_SLOTS} seats for death edges, {seconds}s. Ctrl+C safe (read-only).")
    states = [new_state() for _ in range(BAND_SLOTS)]
    census = {}
    disjunct_census = {}
    t_end = time.time() + seconds
    while time.time() < t_end:
        seats = read_seats()
        bm = rpm(BATTLE_MODE, 1)
        battle_mode = bm[0] if bm else -1
        on_field = battle_mode in (2, 3, 4)   # BattleState.OnField approximation -- see docstring
        edges = step_seats(seats, states, on_field)
        for s in edges:
            rec = seats[s]
            # Column A: fresh read taken right here at the edge, not before the walk (LW-137 fix).
            a_team = shipped_team()
            a_says_ai = a_team in (1, 2)              # Corpses.cs:233 verbatim
            # Column B: Band.FlagOwner verbatim, over this tick's already-complete seat snapshot.
            found, err = flag_owner(seats)
            if err == "ambiguous":
                verdict = "B-AMBIGUOUS"
                b_desc = "ambiguous"
                b_side = None
            elif found is None:
                verdict = "B-NO-OWNER"
                b_desc = "no-owner"
                b_side = None
            else:
                b_side = "ai" if found["foe"] else "player"
                b_desc = f"s{found['slot']}"
                if a_says_ai and b_side == "ai":
                    verdict = "AGREE-AI"
                elif not a_says_ai and b_side == "player":
                    verdict = "AGREE-PLAYER"
                elif a_says_ai:
                    verdict = "A-AI-B-PLAYER"           # shipped would EAT a player kill
                else:
                    verdict = "A-PLAYER-B-AI"           # shipped would MISS an AI-turn death
            census[verdict] = census.get(verdict, 0) + 1

            if rec["hp_zero"] and rec["dead_bit"]:
                disjunct = "both"
            elif rec["hp_zero"]:
                disjunct = "hp-only"
            else:
                disjunct = "bit-only"
            disjunct_census[disjunct] = disjunct_census.get(disjunct, 0) + 1

            now = datetime.datetime.now().strftime("%H:%M:%S.%f")[:-3]
            print(f"[{now}] DEATH s{s} nameId={rec['nameId']} side={'ai' if rec['foe'] else 'player'} "
                  f"disjunct={disjunct} | A: TqTeam={a_team} nonPlayer={a_says_ai} | "
                  f"B: {b_side or '?'} ({b_desc}) | {verdict}")
        time.sleep(0.033)
    print("census:", census if census else "no death edges observed")
    print("disjunct census:", disjunct_census if disjunct_census else "no death edges observed")


if __name__ == "__main__":
    main()
