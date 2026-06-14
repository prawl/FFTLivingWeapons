#!/usr/bin/env python
"""
FEIGN DEATH probe -- deliberately reproduce the "corpse that still takes turns".

Premise (discovered by accident 2026-06-12): godmode held a self-killed ally's HP
high and CLEARED the dead bit every ~10ms; the ally played the death animation, lay
down as a corpse, yet kept taking its turns -- stand, act, flop back to the dead pose.
That accidental behavior IS the Feign Death feature. This probe reproduces it on purpose.

FEIGN DEATH = GODMODE INVERTED. Godmode held the dead bit OFF (fighting the engine,
which wants it ON). Feign Death holds the dead bit ON (band-entry +0x45 |= 0x20) while
holding HP > 0 (band-entry +0x14, u16). It AGREES with the engine on the bit, so the
only thing held against engine pressure is HP > 0. The turn scheduler keys PURELY on CT
(band-entry +0x09 read / +0x25 write) and never reads HP or the dead bit -- so a unit
can be a visual corpse AND still be scheduled. We do NOT touch CT; we only LOG it (+0x09)
so the user can see whether the down unit is still charging toward its turn.

The engine re-commits the death at the TURN BOUNDARY, so a ~10ms hold loop is what makes
the held state stick. Every write goes through the guarded wpm helper; we address the
AUTHORITATIVE band family (0x14184xxxx, BAND base 0x14184C8AC), NEVER the cosmetic
static-array decoy at 0x140893C00. On exit (Ctrl-C, fingerprint migration, or a real
death) the unit is REVIVED -- the dead bit WE set is cleared and HP confirmed > 0 -- so we
never strand a zombie corpse.

THE OPEN QUESTIONS THIS PROBE LETS US ANSWER LIVE (the user is the visual oracle):
  Q1 (animation): does OR-setting the dead bit on a LIVE full-HP unit play the death
     animation + lay it down, or must the unit take a real lethal hit first? Use `feign`
     bit-only (default, on a healthy unit) vs `feign --already-dead` (let it die first,
     then start holding -- restores HP, holds the bit).
  Q2 (scheduler): with the dead bit HELD ON and HP HELD > 0, does the unit still get turns
     (CT crosses threshold)? The loop logs CT (+0x09), HP, and the dead-bit state each pass.
  Q3 (aggro -- make or break): do enemies stop pathing to / attacking a unit whose dead bit
     is held ON while HP > 0? `aggrotest` holds ONLY the dead bit (single variable) and
     does NOT restore HP, so a genuinely lethal overkill is allowed to be a REAL death (the
     "NOT invincible" requirement) -- you can tell "dropped aggro" from "couldn't kill it".

WHY A TARGET MUST BE NAMED EXPLICITLY (safety): a band unit is identified by the 4-tuple
fingerprint (brave, faith, level, maxHP) PLUS its captured side (player/enemy) and original
slot. FFT generics share brave=70/faith=50 + level + maxHP all the time, so the 4-tuple
alone is NOT unique. Every write verb REQUIRES an explicit (brave, faith); if more than one
band entry matches at lock time, the probe ABORTS ("ambiguous target") rather than guessing.
During the hold, relocation re-finds the unit by fingerprint AND original side AND (as a
tiebreaker) the original (gx, gy); it refuses to migrate to a unit on a different side, so
the dead bit can never latch onto an enemy.

USAGE (game must be running, in a live battle):
  python feign_probe.py status
      # read-only: dump every sane band unit (side,slot,br,fa,lvl,mhp,gx,gy,hp,ct,deadbit).
      # Pick a (brave,faith) target from the PLAYER rows.

  python feign_probe.py feign <br> <fa> [--hp N] [--flag-dead] [seconds=60]
      # THE lie-down flop = `battle_cheats godmode` scoped to ONE unit. Let the chosen PLAYER
      # unit REALLY die first (death animation + prone pose), THEN run this: it CLEARS the dead
      # bit (+0x45 &= ~0x20) and holds HP every ~10ms (default 999; --hp 1 holds at 1), CT
      # untouched. The unit stays data-alive without a real revive, so it stays PRONE yet takes
      # turns (springs up to act, flops back). A real Phoenix Down stands it up for good.
      #   --flag-dead   STUDY the opposite: SET the dead bit + hold HP>0. LIVE-FINDING: the unit
      #                 STANDS with KO hearts, is immune, and enemies IGNORE it -- but it counts
      #                 as dead for game-over and shows NO lie-down. (The 'untargetable' half.)

  python feign_probe.py aggrotest <br> <fa> [--hp0] [--team] [seconds=60]
      # Single-variable Q3 on the PLAYER unit with that fingerprint: hold ONLY the dead bit
      # ON, position untouched, write NOTHING else, and do NOT restore HP. Watch several
      # enemy turns and report whether enemies still path to / attack the unit. Because HP is
      # not held, a genuinely lethal overkill produces a REAL death (it is NOT invincible).
      # CONTROL flags:
      #   --hp0    hold HP==0 with the dead bit CLEAR (does HP-keying drop aggro?). RISKIER:
      #            it WILL read as a real-death state; printed warning; revives on exit.
      #   --team   REPORT-ONLY in v1: dump +0x38 across player & enemy units so we can see
      #            whether it discriminates side BEFORE anyone writes it. (No write: the
      #            offset is live-proven but the VALUE is not a clean team id -- 0x06 was a
      #            guest once and a killed enemy another. Writing a guessed team value every
      #            10ms is the single riskiest write, so it is gated out of the first run.)

  python feign_probe.py pdiff <br> <fa>
      # READ-ONLY. KO the unit first, then run this and apply a PHOENIX DOWN in-game. It
      # snapshots the unit's whole combat slot before/after, ignores naturally-drifting bytes,
      # and prints what the REAL revive wrote -- the stand-up / pose trigger godmode's HP-restore
      # never touched. Writes NOTHING; you drive the Phoenix Down.

  python feign_probe.py standup <br> <fa> [--only 0xAA,0xBB] [--hold] [seconds]
      # Replay the Phoenix Down's captured bytes onto a PRONE-but-alive unit to see which makes
      # him stand up. --only restricts to a subset (binary-search); --hold re-writes each tick.

  python feign_probe.py watch <br> <fa> [seconds=8]
      # READ-ONLY high-rate poll during a Phoenix Down: catches TRANSIENT bytes the before/after
      # pdiff misses (a byte set for a few frames to command the stand-up animation).

  python feign_probe.py            # prints this help.

Writes only the dead bit (+ a bounded HP floor in feign); reverts on exit. pdiff writes
NOTHING. Run in a battle.
"""
import pathlib
import struct
import sys
import tempfile
import time

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
from treasure_flags import rpm, wpm, ru8, _require_game

# --- Live battle band (static base, no ASLR): CombatAnchor 0x14184F890 + BandEntry 0x1C
#     - 24*CombatStride(0x200) = 0x14184C8AC.  This is the AUTHORITATIVE family.
#     NEVER write the static-array cosmetic decoy at 0x140893C00 (Offsets.ArrayBase).
BAND, BSTRIDE, BSLOTS = 0x14184C8AC, 0x200, 49
BAND_ENTRY_OFF = 0x1C               # band entry sits 0x1C into each 0x200 combat slot (combat->band gap)
PLAYER_SLOT_THRESHOLD = 24          # band slot s >= 24 is player-side (n = s-24 >= 0), anchor=Ramza

# --- band-entry-relative offsets (verified vs LivingWeapon/Offsets.cs + battle_cheats.py) ---
ALVL, ABR, AFA = 0x0D, 0x0E, 0x10   # fingerprint: level, brave, faith
AHP, AMHP = 0x14, 0x16              # u16 current HP / max HP
AGX, AGY = 0x33, 0x34              # u8 grid x / y
ADEAD = 0x45                       # u8 status byte holding the Dead + Undead flags
DEAD_BIT = 0x20                    # mask: Dead flag (bit 5)
UNDEAD_BIT = 0x10                  # mask: Undead flag (bit 4) -- preserve, never clobber
ACT_TURN = 0x09                    # u8 CT, READ for turn counting (>=90 then <70 = one turn)
ACT_SLAM = 0x25                    # u8 CT WRITE/slam byte (== combat +0x41); ExtraTurn slams this to 100
# Reraise (auto-revive) status bit. FFTHandsFree: "the Reraise bit (0x20 at battle-array +0x47)".
# Sits right in the band status cluster (dead 0x45, poison 0x48, doom/charm 0x49). Reraise revives
# a DEAD unit at ~10% HP when its CT hits 100 -- so SLAM CT to fire it on demand.
RERAISE_OFF, RERAISE_BIT = 0x47, 0x20
SLAM_CT = 100

# --- allegiance/team byte: combat base +0x54 -> band +0x38 (A_ALLEG, battle_cheats.py).
#     Live-proven 2026-06-11 as an ADDRESS, but it is NOT a clean team discriminator: the
#     SAME value (0x06) read as a guest in one battle and a killed enemy in another. We
#     therefore only READ it (the --team control reports; v1 never writes it).
ATEAM = 0x38                       # u8 -- READ-ONLY in this probe (report-only --team)

# --- battle gate sentinels ---
BATTLE_MODE = 0x140900650          # u32: 0 = out of battle, 2/3/4 = live battlefield
SLOT9 = 0x14077CA54                # u32 == 0xFFFFFFFF when a battle is displayed

HOLD_TICK = 0.01                   # ~10ms: the engine re-commits death at the turn boundary
LOG_EVERY = 0.5                    # throttle the per-loop print to ~2 Hz
HP_FLOOR = 1                       # minimum HP we restore to if HP reads 0 / drops (feign only)
MAX_RESTORES = 200                 # feign: cap HP restores so an unbounded resurrection is bounded
GODMODE_FLOOR = 999                # feign default: HP value we hold each tick (godmode-faithful)


def u16(addr):
    b = rpm(addr, 2)
    return struct.unpack("<H", b)[0] if b else None


def _entry_addr(s):
    return BAND + s * BSTRIDE


def _read_unit(a):
    """Read the full sane-unit picture at band entry address `a`, or None if not a unit.
    Mirrors battle_cheats._is_valid_entry bounds; HP may legitimately be 0 (KO)."""
    lvl, br, fa = ru8(a + ALVL), ru8(a + ABR), ru8(a + AFA)
    mhp, gx, gy = u16(a + AMHP), ru8(a + AGX), ru8(a + AGY)
    hp, dead, ct = u16(a + AHP), ru8(a + ADEAD), ru8(a + ACT_TURN)
    if None in (lvl, br, fa, mhp, gx, gy, hp, dead, ct):
        return None
    if not (1 <= lvl <= 99 and 1 <= br <= 100 and 1 <= fa <= 100
            and 1 <= mhp < 2000 and gx <= 30 and gy <= 30):
        return None
    # Recover slot/side from the (aligned) entry address so a bare read is self-contained:
    # tgt.matches() needs "player", and the hold loop reads single entries directly.
    slot = (a - BAND) // BSTRIDE
    return {"lvl": lvl, "br": br, "fa": fa, "mhp": mhp, "gx": gx, "gy": gy,
            "hp": hp, "dead": dead, "ct": ct,
            "slot": slot, "addr": a, "player": slot >= PLAYER_SLOT_THRESHOLD}


def band_units():
    """List (slot, addr, fields) for every sane band entry, player-side first, slot order."""
    out = []
    for s in range(BSLOTS):
        a = _entry_addr(s)
        u = _read_unit(a)
        if u is None:
            continue
        out.append(u)   # _read_unit already filled slot/addr/player
    out.sort(key=lambda u: (not u["player"], u["slot"]))
    return out


def _gate():
    """Abort unless attached AND a battle is displayed. Matches battle_cheats.py's sentinels
    predicate: slot9 == 0xFFFFFFFF and battleMode != 0. Modes 1-5 are all live battle states
    (5 is an 'excused' marker the DLL's BattleState also treats as in-battle); only 0 (out of
    battle) is rejected."""
    _require_game()
    bm = rpm(BATTLE_MODE, 4)
    s9 = rpm(SLOT9, 4)
    bm = struct.unpack("<I", bm)[0] if bm else 0
    s9 = struct.unpack("<I", s9)[0] if s9 else 0
    if s9 != 0xFFFFFFFF or bm == 0:
        print(f"not on a live battlefield (battleMode={bm}, slot9={s9:#x}). "
              f"Load a battle first.")
        sys.exit(1)


# ---------------------------------------------------------------------------
# Target identity: fingerprint = (br, fa, lvl, mhp) + captured side + original slot/pos.
# The 4-tuple alone is NOT unique (FFT generics collide), so SIDE is part of identity and a
# migration may NEVER cross sides; (gx,gy) breaks a same-side tie, original slot wins first.
# ---------------------------------------------------------------------------
class Target:
    __slots__ = ("fp", "player", "slot0", "gx0", "gy0", "mhp")

    def __init__(self, u):
        self.fp = (u["br"], u["fa"], u["lvl"], u["mhp"])
        self.player = u["player"]
        self.slot0 = u["slot"]
        self.gx0, self.gy0 = u["gx"], u["gy"]
        self.mhp = u["mhp"]

    def matches(self, u):
        """Same fingerprint AND same side (player/enemy). Position is NOT required (units
        move); it is used only as a tiebreaker when several entries match this predicate."""
        return (u["br"], u["fa"], u["lvl"], u["mhp"]) == self.fp and u["player"] == self.player


def _matches_in_band(tgt):
    """Every current band entry that matches the target's fingerprint + side."""
    return [u for u in band_units() if tgt.matches(u)]


def _prefer_real_position(cands):
    """Drop the frozen (0,0) twin copy when at least one real-position entry exists.
    A unit appears twice in the band -- once at its live (gx,gy), once as a frozen (0,0)
    duplicate. battle_cheats.give_move filters the identical way (real position beats the
    (0,0) twin). If every candidate sits at (0,0) -- or several are at real positions --
    nothing is dropped, so a genuine two-generics collision still aborts."""
    real = [u for u in cands if u["gx"] != 0 or u["gy"] != 0]
    return real if real else cands


def _resolve_unique(br, fa, require_player=True):
    """Find the live unit with (br, fa). REQUIRE exactly one same-side match or abort.
    Returns (Target, unit) on success, or (None, reason_string) on failure."""
    units = band_units()
    cands = [u for u in units if u["br"] == br and u["fa"] == fa]
    if require_player:
        players = [u for u in cands if u["player"]]
        if not players:
            return None, (f"no PLAYER-side unit with brave={br} faith={fa}. "
                          f"Run `status` and pick a PLAYER row.")
        cands = players
    if not cands:
        return None, f"no band unit with brave={br} faith={fa}. Run `status`."
    if len(cands) > 1:
        cands = _prefer_real_position(cands)   # the frozen (0,0) twin is not a real collision
    if len(cands) > 1:
        where = ", ".join(f"s{u['slot']} lvl={u['lvl']} mhp={u['mhp']} @({u['gx']},{u['gy']})"
                          for u in cands)
        return None, (f"AMBIGUOUS target: {len(cands)} units share brave={br} faith={fa} "
                      f"[{where}]. This fingerprint is not unique -- refusing to guess. Pick a "
                      f"battle where it is unique (or extend the probe to take a slot index).")
    return Target(cands[0]), cands[0]


def _relocate(tgt):
    """Re-find the entry holding this target (same fp + same side). Returns the unit dict,
    or None if it cannot be uniquely re-found (gone, or an unbreakable same-side collision)."""
    cands = _matches_in_band(tgt)
    if not cands:
        return None
    if len(cands) > 1:
        cands = _prefer_real_position(cands)   # ignore the frozen (0,0) twin
    if len(cands) == 1:
        return cands[0]
    # Ambiguous after relocation: prefer the original slot, else nearest to original pos.
    same_slot = [u for u in cands if u["slot"] == tgt.slot0]
    if len(same_slot) == 1:
        return same_slot[0]
    cands.sort(key=lambda u: abs(u["gx"] - tgt.gx0) + abs(u["gy"] - tgt.gy0))
    # If the two closest are equidistant we genuinely cannot tell them apart -> refuse.
    if len(cands) >= 2 and (abs(cands[0]["gx"] - tgt.gx0) + abs(cands[0]["gy"] - tgt.gy0)
                            == abs(cands[1]["gx"] - tgt.gx0) + abs(cands[1]["gy"] - tgt.gy0)):
        return None
    return cands[0]


# ---------------------------------------------------------------------------
# guarded single-bit RMW + bounded HP hold (every write through wpm)
# ---------------------------------------------------------------------------
def _set_dead_bit(a):
    """OR the Dead bit (0x20) into +0x45, preserving Undead (0x10) and other status bits."""
    cur = ru8(a + ADEAD)
    if cur is None:
        return False
    if cur & DEAD_BIT:
        return True
    return wpm(a + ADEAD, bytes([cur | DEAD_BIT]))


def _clear_dead_bit(a):
    """AND ~0x20 out of +0x45, preserving every other bit (Undead etc.)."""
    cur = ru8(a + ADEAD)
    if cur is None:
        return False
    return wpm(a + ADEAD, bytes([cur & ~DEAD_BIT]))


def _restore_hp(a, mhp):
    """Restore HP to MaxHP (or a floor) and return True if the guarded write fired."""
    target = mhp if (mhp and 1 <= mhp < 2000) else HP_FLOOR
    return wpm(a + AHP, struct.pack("<H", target))


# ---------------------------------------------------------------------------
# verbs
# ---------------------------------------------------------------------------
def cmd_status():
    """Read-only baseline: dump every sane band unit so the user can pick a target."""
    _gate()
    units = band_units()
    print(f"=== live band: {len(units)} sane unit(s) "
          f"(side slot br fa | lvl mhp | gx gy | hp ct deadbit team) ===")
    for u in units:
        side = "PLAYER" if u["player"] else "enemy "
        dead = "DEAD" if (u["dead"] & DEAD_BIT) else "----"
        undead = " UND" if (u["dead"] & UNDEAD_BIT) else ""
        team = ru8(u["addr"] + ATEAM)
        tstr = f"{team:#04x}" if team is not None else "??"
        print(f"  {side} s{u['slot']:>2}  br={u['br']:>3} fa={u['fa']:>3} | "
              f"lvl={u['lvl']:>2} mhp={u['mhp']:>4} | ({u['gx']:>2},{u['gy']:>2}) | "
              f"hp={u['hp']:>4} ct={u['ct']:>3} {dead}{undead} "
              f"(+0x45={u['dead']:#04x} +0x38={tstr})")
    print("\nPick a PLAYER (brave,faith) for `feign` / `aggrotest`, e.g.:")
    print("  python feign_probe.py feign <br> <fa>")


def _hold_loop(tgt, addr, mhp, seconds, *, set_dead=False, clear_dead=False,
               heal_to=0, keep_hp_pos=False, hp0=False, extra=None, banner=""):
    """Interruptible hold loop. Re-validates target identity each tick (structs relocate),
    refuses to cross sides, always revives in the finally block, logs throttled. Per tick it
    applies the requested writes on the live target entry:

      heal_to>0   : godmode-style -- hold HP at this value every tick (bumping MaxHP up to it
                    if needed). After a REAL death this keeps the unit data-alive (it stands to
                    act) while the leftover death pose persists -- the Feign Death flop.
      clear_dead  : AND ~0x20 out of +0x45 (stop the engine re-committing the death).
      set_dead    : OR 0x20 into +0x45 (the 'flag-dead' study: untargetable + KO hearts).
      keep_hp_pos : restore HP only when it reads 0 (BOUNDED: lethal-edge respect + ceiling).
      hp0         : force HP to 0 each tick (a real-death control).

    CT (+0x09 read / +0x25 write) is NEVER written.
    """
    if banner:
        print(banner)
    print(f"holding @ band entry {addr:#014x} for up to {seconds:.0f}s "
          f"(Ctrl-C to stop early). Cadence {int(HOLD_TICK*1000)}ms.")
    end = time.monotonic() + seconds
    last_log = 0.0
    last_hp = None              # HP observed on the previous tick (lethal-edge detection)
    restores = 0
    orig_mhp = mhp if (mhp and 1 <= mhp < 2000) else None   # real MaxHP, restored after a heal
    stop_reason = "duration elapsed"
    try:
        while time.monotonic() < end:
            cur = _read_unit(addr)
            if cur is None or not tgt.matches(cur):
                u = _relocate(tgt)
                if u is None:
                    stop_reason = "target migrated / left the band (stopping, no zombie)"
                    break
                addr, cur = u["addr"], u
                last_hp = None   # address moved; don't trust the cross-address HP delta
                if orig_mhp is None and cur["mhp"] and 1 <= cur["mhp"] < 2000:
                    orig_mhp = cur["mhp"]

            hp = cur["hp"]

            # REAL-DEATH RESPECT: only when we promised to keep HP positive but are NOT
            # god-healing. If HP collapsed straight to 0 in one tick, a real lethal hit beat
            # the hold -- let it die.
            if keep_hp_pos and not heal_to and hp == 0 and last_hp not in (None, 0):
                stop_reason = (f"real lethal hit beat the hold (HP {last_hp} -> 0 in one tick) "
                               f"-- letting it die, not resurrecting")
                break

            # --- the writes ---
            if heal_to:
                if heal_to > cur["mhp"]:
                    wpm(addr + AMHP, struct.pack("<H", heal_to))
                wpm(addr + AHP, struct.pack("<H", heal_to))       # hold HP at heal_to (godmode)
            elif keep_hp_pos and hp == 0:
                if restores >= MAX_RESTORES:
                    stop_reason = (f"HP-restore ceiling ({MAX_RESTORES}) hit -- something keeps "
                                   f"re-zeroing HP; stopping to avoid an unbounded loop")
                    break
                if _restore_hp(addr, orig_mhp):
                    restores += 1
            if hp0:
                wpm(addr + AHP, struct.pack("<H", 0))
            if clear_dead:
                _clear_dead_bit(addr)
            if set_dead:
                _set_dead_bit(addr)
            if extra:
                for _off, _val in extra.items():
                    wpm(addr + _off, bytes([_val & 0xFF]))

            now = time.monotonic()
            if now - last_log >= LOG_EVERY:
                last_log = now
                u = _read_unit(addr) or cur
                dead = "DEAD" if (u["dead"] & DEAD_BIT) else "alive"
                extra = f" restores={restores}" if (keep_hp_pos and not heal_to) else ""
                print(f"  hp={u['hp']:>4}/{u['mhp']:<4} ct={u['ct']:>3} "
                      f"+0x45={u['dead']:#04x} ({dead})  [+{seconds-(end-now):5.1f}s]{extra}")

            # remember HP for the next tick's lethal-edge check (read AFTER our writes)
            post = _read_unit(addr)
            last_hp = post["hp"] if post else hp
            time.sleep(HOLD_TICK)
    except KeyboardInterrupt:
        stop_reason = "Ctrl-C"
    finally:
        _revive(tgt, addr, orig_mhp)
        print(f"stopped ({stop_reason}).")


def _revive(tgt, last_addr, orig_mhp=None):
    """Clear any dead bit WE set, undo a heal-mode MaxHP bump, and confirm HP > 0 -- but ONLY
    on an entry whose identity still matches the target. Never write a stale address whose
    occupant is unconfirmed."""
    u = _relocate(tgt)
    if u is None:
        # Last chance: does last_addr STILL hold our target? If not, write NOTHING.
        cur = _read_unit(last_addr)
        if cur is None or not tgt.matches(cur):
            print("REVIVE: target gone / identity no longer matches -- writing NOTHING "
                  "(refusing to poke whoever now occupies that slot).")
            return
        u = {"addr": last_addr}
    a = u["addr"]
    if orig_mhp and 1 <= orig_mhp < 2000 and (u16(a + AMHP) or 0) != orig_mhp:
        wpm(a + AMHP, struct.pack("<H", orig_mhp))   # undo the heal-mode MaxHP bump
    ok_bit = _clear_dead_bit(a)
    mhp_now = u16(a + AMHP) or orig_mhp or 0
    hp = u16(a + AHP)
    if hp == 0:
        _restore_hp(a, mhp_now)
    elif mhp_now and hp and hp > mhp_now:
        wpm(a + AHP, struct.pack("<H", mhp_now))     # clamp god-healed HP back to the real max
    hp = u16(a + AHP)
    db = ru8(a + ADEAD)
    print(f"REVIVED @ {a:#014x}: dead-bit cleared={ok_bit} +0x45={db:#04x} hp={hp}")


def _parse_target_args(args):
    """Pull (br, fa) -- both REQUIRED -- and an optional trailing seconds from positionals."""
    pos = [a for a in args if not a.startswith("--")]
    if len(pos) < 2:
        return None, None, None
    br, fa = int(pos[0]), int(pos[1])
    seconds = float(pos[2]) if len(pos) >= 3 else 60.0
    return br, fa, seconds


def cmd_feign(args):
    """Reproduce the lie-down-and-act flop = `battle_cheats godmode` scoped to ONE unit: CLEAR
    the dead bit + hold HP each tick, CT untouched. Run it AFTER the unit really died (death
    pose showing). REQUIRES an explicit PLAYER (br, fa)."""
    _gate()
    pos_args = list(args)
    heal_to = GODMODE_FLOOR
    if "--hp" in pos_args:
        i = pos_args.index("--hp")
        if i + 1 < len(pos_args):
            heal_to = max(1, min(60000, int(pos_args[i + 1])))
            del pos_args[i:i + 2]          # drop "--hp" + its value so it can't leak into seconds
        else:
            del pos_args[i]
    flag_dead = "--flag-dead" in pos_args
    br, fa, seconds = _parse_target_args(pos_args)
    if br is None:
        print("feign REQUIRES an explicit PLAYER (brave, faith). Run `status`, then:\n"
              "  python feign_probe.py feign <br> <fa> [--hp N] [--flag-dead] [seconds]")
        sys.exit(1)

    tgt, found = _resolve_unique(br, fa, require_player=True)
    if tgt is None:
        print(found)   # the reason string
        sys.exit(1)
    u = found

    if flag_dead:
        # STUDY MODE: SET the dead bit + keep HP>0. LIVE-FINDING: the unit STANDS with KO
        # hearts, is immune, and enemies IGNORE it -- but it counts as dead for game-over and
        # shows NO lie-down. The 'untargetable' half, NOT the corpse half.
        banner = (f"=== FEIGN (flag-dead study) on PLAYER br={u['br']} fa={u['fa']} "
                  f"slot={u['slot']} at ({u['gx']},{u['gy']}) ===\n"
                  f"SET dead bit (+0x45 |= 0x20) + keep HP>0. Expect: STANDS with KO hearts,\n"
                  f"immune, enemies IGNORE it, counts as dead for game-over. No lie-down pose.")
        _hold_loop(tgt, u["addr"], u["mhp"], seconds,
                   set_dead=True, keep_hp_pos=True, banner=banner)
        return

    # DEFAULT = the known-good lie-down flop (godmode-on-ONE unit). Let the unit REALLY die
    # first (death animation + prone pose), THEN run this: it CLEARS the dead bit + holds HP,
    # keeping it data-alive without a real revive, so it stays prone yet takes turns.
    if u["hp"] != 0 and not (u["dead"] & DEAD_BIT):
        print(f"NOTE: this unit reads hp={u['hp']} and is not flagged dead. For the lie-down\n"
              f"      flop, let it ACTUALLY DIE first (death animation + prone pose), THEN run\n"
              f"      this -- otherwise there is no death pose to preserve.")
    banner = (f"=== FEIGN DEATH (godmode-on-one) on PLAYER br={u['br']} fa={u['fa']} lvl={u['lvl']} "
              f"slot={u['slot']} at ({u['gx']},{u['gy']}) ===\n"
              f"CLEAR dead bit (+0x45 &= ~0x20) + hold HP at {heal_to} every "
              f"{int(HOLD_TICK*1000)}ms; CT (+0x09) untouched, logged.\n"
              f"= `battle_cheats godmode` on ONE unit. WATCH: after its real death, does it stay\n"
              f"lying down yet spring up to act and flop back? (--hp 1 holds it at 1 HP instead of\n"
              f"{GODMODE_FLOOR}; a real Phoenix Down stands it up for good.)")
    _hold_loop(tgt, u["addr"], u["mhp"], seconds,
               clear_dead=True, heal_to=heal_to, banner=banner)


def cmd_aggrotest(args):
    """Single-variable Q3: hold ONLY the dead bit (no HP restore), watch enemy turns.
    REQUIRES an explicit PLAYER (br, fa)."""
    _gate()
    hp0 = "--hp0" in args
    team_report = "--team" in args
    br, fa, seconds = _parse_target_args(args)
    if br is None:
        print("aggrotest REQUIRES an explicit PLAYER (brave, faith). Run `status`, then:\n"
              "  python feign_probe.py aggrotest <br> <fa> [--hp0] [--team] [seconds]")
        sys.exit(1)

    tgt, found = _resolve_unique(br, fa, require_player=True)
    if tgt is None:
        print(found)
        sys.exit(1)
    u = found

    if team_report:
        # REPORT-ONLY: dump +0x38 across player & enemy units. NO write -- the value is not a
        # clean team id; writing a guessed team every 10ms is the riskiest write (gated out).
        print("=== --team REPORT (read-only) -- +0x38 allegiance byte across the band ===")
        for x in band_units():
            t = ru8(x["addr"] + ATEAM)
            side = "PLAYER" if x["player"] else "enemy "
            tval = f"{t:#04x}" if t is not None else "(unreadable)"
            print(f"  {side} s{x['slot']:>2} br={x['br']:>3} fa={x['fa']:>3} +0x38={tval}")
        print("(+0x38 is live-proven as the allegiance ADDRESS but NOT a clean team id -- the\n"
              " same value was a guest once and a killed enemy another. v1 never WRITES it.)\n")

    warn = ""
    if hp0:
        warn += ("\n  ! --hp0: holding HP==0 with the dead bit CLEAR. This is a REAL-death\n"
                 "    state -- the engine may commit the kill. Riskier; revives on exit.")

    banner = (f"=== AGGRO TEST on PLAYER br={u['br']} fa={u['fa']} lvl={u['lvl']} "
              f"slot={u['slot']} at ({u['gx']},{u['gy']}) ===\n"
              f"SINGLE VARIABLE: hold ONLY the dead bit ON, position untouched, nothing else\n"
              f"written. HP is NOT held -- a genuinely lethal overkill is allowed to be a REAL\n"
              f"death (the unit is NOT invincible), so you can tell 'dropped aggro' from\n"
              f"'attacked but couldn't kill it'.{warn}\n"
              f"WATCH SEVERAL ENEMY TURNS: do enemies stop pathing to / attacking this unit?")
    # Default aggrotest: dead bit ON, HP NOT held. --hp0 control: dead bit CLEAR, HP forced 0.
    _hold_loop(tgt, u["addr"], u["mhp"], seconds,
               set_dead=(not hp0), hp0=hp0, banner=banner)


# ---------------------------------------------------------------------------
# Phoenix Down diff: what does a REAL revive write that godmode's HP-restore did NOT?
# ---------------------------------------------------------------------------
_KNOWN_BAND_OFF = {
    ALVL: "level", ABR: "brave", AFA: "faith",
    AHP: "HP.lo", AHP + 1: "HP.hi", AMHP: "MaxHP.lo", AMHP + 1: "MaxHP.hi",
    AGX: "gx", AGY: "gy", ADEAD: "dead/undead status",
    ACT_TURN: "CT (read)", 0x25: "CT (slam)",
}


def _fmt_off(slot_i):
    """Format a slot offset as band-entry-relative (+0xNN); 'hdr+' for the 0x1C combat header."""
    br = slot_i - BAND_ENTRY_OFF
    return f"+{br:#04x}" if br >= 0 else f"hdr+{slot_i:#04x}"


def _annot(slot_i):
    return _KNOWN_BAND_OFF.get(slot_i - BAND_ENTRY_OFF, "?? candidate stand-up / pose trigger")


def cmd_pdiff(args):
    """Capture what a Phoenix Down (a REAL revive event) writes to a unit's combat slot -- the
    stand-up trigger godmode's HP-restore never hits. READ-ONLY: the USER applies the Phoenix
    Down in-game; the probe only snapshots + diffs. REQUIRES a PLAYER (br, fa) on a KO'd unit.

    Method: snapshot the whole 0x200 combat slot, learn the naturally-drifting bytes from a
    second 'before' sample, then diff against an 'after' snapshot taken once the unit stands
    up. Known fields (HP/MaxHP/dead/CT) are annotated; anything ELSE is a candidate trigger."""
    _gate()
    br, fa, seconds = _parse_target_args(args)
    if br is None:
        print("pdiff REQUIRES an explicit PLAYER (brave, faith). KO the unit first, then:\n"
              "  python feign_probe.py status   # read its (br,fa) from the KO'd PLAYER row\n"
              "  python feign_probe.py pdiff <br> <fa>")
        sys.exit(1)
    tgt, found = _resolve_unique(br, fa, require_player=True)
    if tgt is None:
        print(found)
        sys.exit(1)
    u = found
    addr = u["addr"]
    slot_base = addr - BAND_ENTRY_OFF
    WIN = 0x200

    if u["hp"] != 0 and not (u["dead"] & DEAD_BIT):
        print(f"NOTE: this unit reads hp={u['hp']} and is not flagged dead. A Phoenix Down only\n"
              f"      revives a KO'd unit -- KO it first (let it die), then re-run pdiff.")

    print(f"=== Phoenix Down diff on PLAYER br={u['br']} fa={u['fa']} slot={u['slot']} ===")
    print(f"snapshotting the {WIN:#x}-byte combat slot @ {slot_base:#014x} "
          f"(band entry {addr:#014x}).")

    a1 = rpm(slot_base, WIN)
    if a1 is None:
        print("could not read the unit slot. Are you in a battle?")
        sys.exit(1)
    time.sleep(1.0)
    a2 = rpm(slot_base, WIN)
    drift = {i for i in range(WIN) if a2 is not None and a1[i] != a2[i]}
    if drift:
        print(f"(ignoring {len(drift)} naturally-drifting byte(s): "
              + ", ".join(_fmt_off(i) for i in sorted(drift)) + ")")
    base = a2 if a2 is not None else a1

    try:
        input("\nNow use a PHOENIX DOWN on this unit in-game, watch him STAND UP, then Enter ...")
    except (EOFError, KeyboardInterrupt):
        print("\naborted.")
        return

    v = _relocate(tgt)
    if v is not None and v["addr"] != addr:
        addr = v["addr"]
        slot_base = addr - BAND_ENTRY_OFF
        print(f"(unit re-located to {addr:#014x})")
    after = rpm(slot_base, WIN)
    if after is None:
        print("could not read the unit slot after the revive (it may have moved).")
        return

    changed = [i for i in range(WIN) if base[i] != after[i] and i not in drift]
    if not changed:
        print("\nNo non-drift changes inside the per-unit slot. The stand-up trigger likely\n"
              "lives OUTSIDE this struct (a global animation queue / pose table) -- we'd escalate\n"
              "to a writable-region-wide diff like treasure_flags' snapshot scan.")
        return
    print(f"\n{len(changed)} byte(s) changed by the revive:")
    for i in sorted(changed):
        print(f"  {_fmt_off(i):>10}  {base[i]:#04x} -> {after[i]:#04x}   {_annot(i)}")
    print("\nKnown revive writes: HP +0x14, MaxHP +0x16, dead +0x45. Anything tagged '?? candidate'\n"
          "is what godmode's HP-restore NEVER wrote -- the lead for forcing the stand-up.")


# ---------------------------------------------------------------------------
# Standup test: replay the Phoenix Down's non-HP writes onto a prone unit to find the trigger.
# Captured 2026-06-13 via pdiff on a real Phoenix Down (band-relative offset -> revived value).
# ---------------------------------------------------------------------------
REVIVE_BYTES = {
    0x19c: 0x00, 0x19f: 0x01, 0x1aa: 0x09, 0x1bc: 0x01,
    0x1c4: 0x20, 0x1c9: 0x48, 0x1d3: 0x00, 0x1d9: 0xff,
}


def cmd_standup(args):
    """Replay the Phoenix Down's non-HP byte writes (REVIVE_BYTES, captured by pdiff) onto a
    PRONE-but-alive unit to find which one makes him STAND UP without a real revive. It writes
    the selected candidate bytes, then godmode-holds (clear dead bit + HP) so he stays alive
    while you watch. Binary-search with --only:

      python feign_probe.py standup <br> <fa>                 # all 8 candidates at once
      python feign_probe.py standup <br> <fa> --only 0x1d3    # just the prone-flag suspect
      python feign_probe.py standup <br> <fa> --only 0x1aa,0x1d3
      python feign_probe.py standup <br> <fa> --hold          # re-write each tick (held)

    REQUIRES a PLAYER (br, fa). Run it on the lie-down unit (just died, or under `feign`)."""
    _gate()
    pos_args = list(args)
    only = None
    custom = None
    if "--set" in pos_args:                      # --set 0x1c4=0,0x1c9=0  (write arbitrary values)
        i = pos_args.index("--set")
        if i + 1 < len(pos_args):
            custom = {}
            for pair in pos_args[i + 1].split(","):
                k, _, val = pair.partition("=")
                custom[int(k, 0)] = int(val, 0) & 0xFF
            del pos_args[i:i + 2]
        else:
            del pos_args[i]
    if "--only" in pos_args:
        i = pos_args.index("--only")
        if i + 1 < len(pos_args):
            only = {int(x, 0) for x in pos_args[i + 1].split(",")}
            del pos_args[i:i + 2]
        else:
            del pos_args[i]
    hold_bytes = "--hold" in pos_args
    br, fa, seconds = _parse_target_args(pos_args)
    if br is None:
        print("standup REQUIRES a PLAYER (brave, faith). Run it on the lie-down unit:\n"
              "  python feign_probe.py standup <br> <fa> [--only 0x1d3,0x1aa] [--hold] [seconds]")
        sys.exit(1)
    tgt, found = _resolve_unique(br, fa, require_player=True)
    if tgt is None:
        print(found)
        sys.exit(1)
    u = found
    addr = u["addr"]
    if custom is not None:
        writes = custom
    else:
        writes = {o: v for o, v in REVIVE_BYTES.items() if (only is None or o in only)}
    if not writes:
        print("no candidate offsets selected. Valid: "
              + ", ".join(f"{o:#05x}" for o in sorted(REVIVE_BYTES)) + "  or use --set off=val,...")
        sys.exit(1)

    print(f"=== STANDUP test on PLAYER br={u['br']} fa={u['fa']} slot={u['slot']} "
          f"at ({u['gx']},{u['gy']}) ===")
    print(f"replaying {len(writes)} Phoenix-Down byte(s) (held={hold_bytes}), then godmode-holding "
          f"alive. WATCH: does he STAND UP?")
    for o, v in sorted(writes.items()):
        cur = ru8(addr + o)
        ok = wpm(addr + o, bytes([v & 0xFF]))
        print(f"  +{o:#05x}  {cur!s:>4} -> {v:#04x}  ({'ok' if ok else 'FAIL'})")

    _hold_loop(tgt, addr, u["mhp"], seconds, clear_dead=True, heal_to=GODMODE_FLOOR,
               extra=(writes if hold_bytes else None),
               banner="(holding him alive -- did he stand up? Ctrl-C once you have your answer.)")


# ---------------------------------------------------------------------------
# Watch: poll the slot AS FAST AS POSSIBLE during a Phoenix Down to catch a TRANSIENT trigger
# (a byte set for a few frames to command the stand-up animation) that before/after pdiff misses.
# ---------------------------------------------------------------------------
def cmd_watch(args):
    """READ-ONLY high-rate capture. KO the unit, run this, then apply a PHOENIX DOWN: it polls
    the unit's whole combat slot as fast as it can for ~8s and reports every byte that changed
    at ANY point -- flagging TRANSIENT ones (set then reverted) that the before/after pdiff
    could not see. A transient byte during the stand-up is the likely 'play animation' command.
    REQUIRES a PLAYER (br, fa) on a KO'd unit. Writes NOTHING."""
    _gate()
    br, fa, seconds = _parse_target_args(args)
    if br is None:
        print("watch REQUIRES a PLAYER (brave, faith). KO the unit first, then:\n"
              "  python feign_probe.py watch <br> <fa> [seconds=8]")
        sys.exit(1)
    if seconds == 60.0:        # this verb's natural default is short
        seconds = 8.0
    tgt, found = _resolve_unique(br, fa, require_player=True)
    if tgt is None:
        print(found)
        sys.exit(1)
    u = found
    addr = u["addr"]
    slot_base = addr - BAND_ENTRY_OFF
    WIN = 0x200
    base = rpm(slot_base, WIN)
    if base is None:
        print("could not read the unit slot. Are you in a battle?")
        sys.exit(1)

    print(f"=== WATCH on PLAYER br={u['br']} fa={u['fa']} slot={u['slot']} ===")
    print(f"polling the {WIN:#x}-byte slot @ {slot_base:#014x} as fast as possible for {seconds:.0f}s.")
    print(">>> USE THE PHOENIX DOWN NOW <<<  capturing transient changes ...")
    seen = {}                  # offset -> set of differing values observed
    samples = 0
    end = time.monotonic() + seconds
    while time.monotonic() < end:
        snap = rpm(slot_base, WIN)
        samples += 1
        if snap is None or snap == base:
            continue
        for i in range(WIN):
            if snap[i] != base[i]:
                seen.setdefault(i, set()).add(snap[i])
    final = rpm(slot_base, WIN) or base

    if not seen:
        print(f"\n{samples} samples, NO byte in this slot EVER differed from baseline. The stand-up\n"
              "trigger is not in the per-unit struct at all -- it lives in a global animation system\n"
              "(a much bigger hunt). (Or the revive didn't land -- confirm he stood up.)")
        return
    print(f"\n{samples} samples. Bytes that changed at any point during the revive:")
    for i in sorted(seen):
        vals = ", ".join(f"{v:#04x}" for v in sorted(seen[i]))
        kind = "DURABLE  " if final[i] != base[i] else "TRANSIENT"
        print(f"  {_fmt_off(i):>10}  base={base[i]:#04x} seen[{vals}] final={final[i]:#04x} "
              f"[{kind}]  {_annot(i)}")
    print("\nTRANSIENT rows (final == base) are what the before/after pdiff MISSED -- a byte briefly\n"
          "set during the stand-up is the prime 'play animation' command to try writing.")


# ---------------------------------------------------------------------------
# Snapshot / restore: copy a unit's whole combat slot while STANDING, then write it back when
# PRONE. Tests the "recreate the struct -> recreate the unit" idea. Snapshots live in OS temp.
# ---------------------------------------------------------------------------
def _snap_path(br, fa):
    return pathlib.Path(tempfile.gettempdir()) / f"fft_feign_snap_{br}_{fa}.bin"


def cmd_snapshot(args):
    """Save a unit's full 0x200 combat slot to OS temp. Run at BATTLE START while STANDING."""
    _gate()
    br, fa, _ = _parse_target_args(args)
    if br is None:
        print("snapshot REQUIRES a PLAYER (brave, faith). Run `status`, then:\n"
              "  python feign_probe.py snapshot <br> <fa>   # while he's standing/full-HP")
        sys.exit(1)
    tgt, found = _resolve_unique(br, fa, require_player=True)
    if tgt is None:
        print(found)
        sys.exit(1)
    u = found
    slot_base = u["addr"] - BAND_ENTRY_OFF
    blob = rpm(slot_base, 0x200)
    if blob is None:
        print("could not read the slot.")
        sys.exit(1)
    p = _snap_path(br, fa)
    p.write_bytes(blob)
    print(f"snapshot saved: {p}")
    print(f"  unit br={br} fa={fa} hp={u['hp']} at ({u['gx']},{u['gy']}), 0x200 bytes from {slot_base:#014x}")
    print("Now let him die + feign, then:  python feign_probe.py restore "
          f"{br} {fa} [--full]")


def cmd_restore(args):
    """Write a saved STANDING snapshot back onto the (now prone) unit, to test whether copying
    the struct forces a stand-up. Default restores the band-entry DATA region (slot 0x1C onward
    -- skips the combat-header pointer zone; this is ~ a thorough `standup`, expected to fail).
    --full restores ALL 0x200 incl. the header (CRASH RISK from stale pointers -- your literal
    'copy the exact struct' idea). REQUIRES a PLAYER (br, fa) with a prior snapshot."""
    _gate()
    full = "--full" in args
    br, fa, _ = _parse_target_args(args)
    if br is None:
        print("restore REQUIRES a PLAYER (brave, faith) with a prior snapshot.")
        sys.exit(1)
    p = _snap_path(br, fa)
    if not p.exists():
        print(f"no snapshot at {p}. Run `snapshot {br} {fa}` at battle start (standing) first.")
        sys.exit(1)
    blob = p.read_bytes()
    if len(blob) < 0x200:
        print(f"snapshot too small ({len(blob)} bytes).")
        sys.exit(1)
    tgt, found = _resolve_unique(br, fa, require_player=True)
    if tgt is None:
        print(found)
        sys.exit(1)
    u = found
    slot_base = u["addr"] - BAND_ENTRY_OFF
    if full:
        print("--full: restoring ALL 0x200 bytes incl. the combat header. CRASH RISK if the header\n"
              "holds stale pointers. This is the literal 'copy the exact struct' test.")
        start, data = 0, blob
    else:
        print("restoring the band-entry DATA region (slot 0x1C onward; skips the pointer header).\n"
              "NOTE: this is ~ a thorough `standup` -- expected to NOT stand him up. Add --full for\n"
              "the complete copy.")
        start, data = BAND_ENTRY_OFF, blob[BAND_ENTRY_OFF:]
    ok = wpm(slot_base + start, data)
    print(f"wrote {len(data)} bytes @ {slot_base + start:#014x}  ({'ok' if ok else 'FAIL'}).")
    print("DID HE STAND UP? (HP / position / CT were also reset to the snapshot, so expect some\n"
          "teleport / turn-order weirdness regardless of the pose.)")


# ---------------------------------------------------------------------------
# Reraise: set the auto-revive status bit and (optionally) slam CT to fire it on demand.
# The engine's OWN Reraise runs the real stand-up animation -- the revive we can't call.
# ---------------------------------------------------------------------------
def cmd_reraise(args):
    """Grant Reraise (auto-revive) by setting its status bit (band +0x47 bit 0x20), and
    optionally SLAM CT to 100 (--fire) to trigger the revive NOW. Reraise revives a DEAD unit
    (hp=0) at ~10% HP when its CT hits 100, so --fire only does something visible on a KO'd
    unit. REQUIRES a PLAYER (br, fa).

      --fire   also slam CT (band +0x25) to 100 to fire the revive immediately.
      --hold   re-assert the bit (+ CT if --fire) each tick for <seconds> (default 15) so the
               death sequence sees Reraise even if you kill the unit mid-run.
    """
    _gate()
    fire = "--fire" in args
    hold = "--hold" in args
    br, fa, seconds = _parse_target_args(args)
    if br is None:
        print("reraise REQUIRES a PLAYER (brave, faith). Run `status`, then:\n"
              "  python feign_probe.py reraise <br> <fa> [--fire] [--hold] [seconds]")
        sys.exit(1)
    tgt, found = _resolve_unique(br, fa, require_player=True)
    if tgt is None:
        print(found)
        sys.exit(1)
    u = found
    addr = u["addr"]
    state = "DEAD -- Reraise can fire" if u["hp"] == 0 else \
            f"ALIVE hp={u['hp']} -- Reraise sits dormant until it dies"
    print(f"=== RERAISE on PLAYER br={u['br']} fa={u['fa']} slot={u['slot']} "
          f"at ({u['gx']},{u['gy']})  [{state}] ===")

    def set_bit(a):
        cur = ru8(a + RERAISE_OFF)
        if cur is None:
            return False, None
        return wpm(a + RERAISE_OFF, bytes([cur | RERAISE_BIT])), cur

    ok, cur = set_bit(addr)
    print(f"set Reraise band +0x{RERAISE_OFF:02x} |= 0x{RERAISE_BIT:02x}  "
          f"(was {'?' if cur is None else hex(cur)}) -> {'ok' if ok else 'FAIL'}")
    if fire:
        ok2 = wpm(addr + ACT_SLAM, bytes([SLAM_CT]))
        print(f"slammed CT band +0x{ACT_SLAM:02x} = {SLAM_CT} -> {'ok' if ok2 else 'FAIL'}  "
              f"(Reraise fires at CT 100)")
    if hold:
        secs = seconds or 15.0
        print(f"holding Reraise{' + CT slam' if fire else ''} for {secs:.0f}s "
              f"(kill the unit now if it is still alive). Ctrl-C to stop.")
        try:
            end = time.monotonic() + secs
            while time.monotonic() < end:
                v = _relocate(tgt)
                a = v["addr"] if v else addr
                set_bit(a)
                if fire:
                    wpm(a + ACT_SLAM, bytes([SLAM_CT]))
                time.sleep(HOLD_TICK)
        except KeyboardInterrupt:
            print()
    print("\nWATCH: did he AUTO-REVIVE with the real stand-up animation? (If he's alive, kill him")
    print("first, then re-run with --fire. If band +0x47 does nothing, the bit may live in the")
    print("static-array mirror instead -- tell me and I'll add a --static fallback.)")


def main():
    mode = sys.argv[1] if len(sys.argv) > 1 else ""
    rest = sys.argv[2:]
    if mode == "status":
        cmd_status()
    elif mode == "feign":
        cmd_feign(rest)
    elif mode == "aggrotest":
        cmd_aggrotest(rest)
    elif mode == "pdiff":
        cmd_pdiff(rest)
    elif mode == "standup":
        cmd_standup(rest)
    elif mode == "watch":
        cmd_watch(rest)
    elif mode == "snapshot":
        cmd_snapshot(rest)
    elif mode == "restore":
        cmd_restore(rest)
    elif mode == "reraise":
        cmd_reraise(rest)
    else:
        print(__doc__)


if __name__ == "__main__":
    main()