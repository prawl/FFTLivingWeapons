"""SPAWN PROBE (Body Double feasibility): can a pre-loaded but DORMANT unit slot be activated
mid-battle so the ENGINE constructs its sprite? (plan.md, LW-58)

WHY: the battlefield sprite render is welded at construction (LIVE_LEDGER: the write-and-hold
spawn row is WALLED: a hand-written band slot enrolls in the CT scheduler but renders BLANK,
and a byte-identical clone AVs the timeline-detail view via a null sprite deref). despawn_probe.py
beat the same wall for REMOVAL by triggering the engine's own crystallization event instead of
writing "despawned". This probe tests the reverse: not fabricating a unit (known dead), but
flipping the engine's own presence state on a slot the engine ALREADY loaded, so that any sprite
construction is engine-driven.

RECON FACTS (2026-07-09 fan-out; sources per line):
  * PSX ENTD record byte 0x01 flag field (MSB-first): 0x80 Male, 0x40 Female, 0x20 Monster,
    0x10 Join After Event ("JoinA"), 0x08 Load Formation, 0x04 HideStats, 0x01 Save Formation.
    JoinA lives at byte 0x01, NOT 0x18 (plan.md placed it at 0x18; corrected here).
    Byte 0x18: 0x80 Always Present, 0x40 Randomly Present, 0x30 TeamColor, 0x08 Control,
    0x04 Immortal. (FFTPatcher EventUnit.cs; archived ffhacktics.com/wiki/ENTD.)
  * The engine's activation primitives are EVENT INSTRUCTIONS: {45} AddUnit loads an unloaded
    ENTD unit mid-event (draw now, or hold in memory); {44} Draw reveals a LOADED unit that is
    not displayed, flipping a documented per-unit "Unit Present" byte 0 to 1. That routine pair
    is the crystallization-equivalent for spawning. (ffhacktics.com/wiki/AddUnit, /wiki/Draw.)
  * Vanilla marks loaded-but-hidden units by setting AlwaysP|RandomlyP together (0x18 = 0xC0 or
    0xD0) and reveals them later with a bare Draw: Limberry Ultima Demons (revealed on Celia and
    Lede deaths), Riovanes hidden spares. Earliest TRUE mid-battle join: Chapter 3 Inside of
    Riovanes Castle (Wiegraf at 25% HP: AddUnit Velius, 3 Archaic Demons, and the player party).
    Chapters 1 and 2 have ZERO mid-battle combat adds. (Vanilla ENTD + battle-conditional
    decompile, github.com/Glain/FFTPatcher data.)
  * Random encounters: RandomlyP candidates that fail the init roll are (strong inference) never
    generated at all, so random battles are probably NOT the dormant-slot hunting ground; story
    battles with scripted reveals are.
  * Owner find (FFHacktics PSX Battle Stats struct, 2026-07-09): a unit-existence byte at 0x0183 with the enum
    0x00 = does not exist but can, 0x01 = exists, 0x02 = being removed (treasure/crystal),
    0x80 = was active now disabled (crystallized/dismissed), 0xFF = cannot exist (loading
    screens/ChangeMap). Mapped through the tape-corroborated PSX-to-frame delta of +0x32 (the
    turn-flags mapping: PSX 0x186..0x189 = frame +0x1B8..0x1BB, mushin_wait_probe tape) that
    lands at frame +0x1B5 = band entry +0x199: candidate P1 below.

CANDIDATE presence bytes (band-entry-relative; band entry = combat/frame base + 0x1C):
    P1   +0x199  owner's 0x0183 existence enum read as a PSX offset, +0x32 delta (LEAD candidate)
    P2   +0x167  the same 0x0183 read as an IC frame-relative offset (fallback interpretation)
    INB  +0x12   combat +0x2E "inBattle" u16 low byte: a KNOWN PULSING TRAP, listed for
                 reference only, never trusted and never written (deployed_signal_probe.py)
    X18  +0x172  linear extrapolation of ENTD 0x18 off the +0x17A action-echo block (delta
                 +0x15A). LOW confidence: that block was reframed as current-action echo, and
                 the live struct is provably not a linear ENTD copy. Read-only here.
    LOAD +0x15B  same extrapolation for ENTD 0x01. Same low confidence. Read-only here.

POSITIVE CONTROL (run before any dormant write): run `watch` while a unit crystallizes (drive it
with despawn_probe.py remove, or play a kill out). If a candidate is the real existence byte it
should step 0x01 -> 0x02 or 0x80 on the crystallize edge. RESULT 2026-07-09 (owner tape, first
watch session): P1/P2/X18/LOAD all stayed FLAT across 4 deaths, an undead die-and-rise cycle,
and 2 crystallizes; the only in-slot lifecycle byte was dead +0x45 (0x08 charging pulses, 0x20
death, 0x10/0x30 undead cycle, crystal edges 0x20->0x40 and 0x20->0x00). The +0x32-delta
placement of the owner's 0x0183 enum is therefore falsified for the lifecycle; `deathdiff`
(below) is the follow-up instrument: full 0x200-slot byte diff on one dying unit, to find the
real in-slot presence byte or prove existence is tracked engine-externally (which would match
the render-weld precedent and make the AddUnit/Draw routine hunt the only path).

The three questions, staged:
  Q1  list: classify every band slot ACTIVE / DORMANT-POP / GARBAGE / ZEROED and cross-tab the
      candidate bytes per class. DORMANT-POP (addressable, invalid, nonzero, AND every identity
      field zero-or-in-range) is the activation target. GARBAGE (nonzero but implausible
      fields) is adjacent non-unit memory: the outer band slots run past the real unit array
      into engine data (bytecode/offset tables in the first live census) and are NEVER
      activatable. ZEROED slots are never written.
  Q2  watch: edge-log candidate-byte and classification transitions across all slots (the
      crystallize positive control, or any scripted reveal if a story battle provides one).
  Q3  activate: on a chosen DORMANT-POP slot (interactive y/n confirm; THROWAWAY SAVE ONLY),
      flip P1/P2 to 0x01, fill only invalid identity fields, set position/team, and watch for
      (a) sprite [operator eyeball], (b) scheduler adoption: turn-count CT movement at band
      +0x09 (combat +0x25, Offsets.ACtTurn, the proven turn-count READ; the band +0x25 slam
      byte is sampled too but its reads are documented unreliable, so a flat +0x25 is
      INCONCLUSIVE, never a "no"), (c) write persistence: the fill writes satisfy
      _is_valid_entry by construction, so the informative signal is whether the engine lets
      them HOLD or reverts them during the watch. A slot with no identity anchor (no valid
      level or maxHP) is REFUSED: filling every field would be fabrication (the known-dead
      write-and-hold path), not activation. Expect the render weld to hold: a clean "engine
      did not construct a sprite" verdict is a SUCCESSFUL probe outcome and points the
      follow-up at the engine's AddUnit/Draw event routines instead.

Reuses the PROVEN battle_cheats harness (guarded RPM/WPM, band addressing, _is_valid_entry).
Writes happen ONLY in `activate`, ONLY on a slot classified DORMANT-POP this run, after an
identity-anchor check (a valid level or maxHP must already be present) and an identity-twin
check against every ACTIVE slot, and behind an interactive y/n confirm. No restore after
activate (restoring bytes the engine may have adopted is worse than leaving them; run on a
THROWAWAY save and quarantine the autosave first). Do NOT open the timeline/unit-detail view
on a half-activated unit (null-sprite AV precedent, clone_probe).

    python tools\\probes\\spawn_probe.py list
        # classify all 49 band slots, cross-tab candidate bytes per class, hex windows
    python tools\\probes\\spawn_probe.py watch [seconds=120]
        # edge-log classification/candidate transitions (positive control: crystallize a unit)
    python tools\\probes\\spawn_probe.py deathdiff <slot> [seconds=240]
        # full 0x200 combat-slot byte diff on ONE unit: kill it MID-BATTLE and let it
        # crystallize while the battle continues; every changed offset logs (noisy offsets mute
        # after 6 edges); the end summary ranks offsets by edge count, rare edges = lifecycle
        # candidates. Read-only. CONTROLLED RECIPE: start deathdiff FIRST, then drive the
        # crystallize on demand with despawn_probe.py remove <brave> <faith> (kills + sets the
        # crystal counter, so the unit crystallizes on its next turn); the proven hold-at-3
        # counter pin (Sanctuary mechanism, combat +0x07) can PARK a corpse until the recorder
        # is rolling. Combat +0x07 sits inside the diffed window, so the 3-2-1-0 countdown
        # itself will show in the tape and timestamp the crystal edge exactly.
    python tools\\probes\\spawn_probe.py revert <slot> [seconds=90]
        # the REVERSE flip on a freshly-popped chest/crystal: restore the pre-pop corpse bytes
        # (marker+mirror to 0, dead bit+mirror set, counter parked at 3) and watch whether the
        # engine re-materializes the unit model. y/n confirm; refuses any slot not matching the
        # conversion signature. THE decisive render-weld test this probe can run today.
    python tools\\probes\\spawn_probe.py activate <slot> <gx> <gy> [seconds=120]
        # Q3 on a DORMANT-POP slot: presence flip + sanity fill + position, then watch.
        # Interactive y/n confirm; THROWAWAY SAVE ONLY. Pick an EMPTY on-map tile for gx,gy
        # by eye. End turns in game to advance the scheduler.
    python tools\\probes\\spawn_probe.py addr <slot>
        # print the absolute addresses of the slot's signature bytes (combat base, band entry,
        # status +0x45, conversion marker +0x46, mirrors, counter) for CE what-writes work
    python tools\\probes\\spawn_probe.py findrender <slot>
        # whole-VA pointer sweep for the slot's RENDER struct: the PSX "Misc Unit Data" twin
        # struct (owner-supplied wiki dump, data_tables.txt in the repo root) is the render
        # side of a unit (spritesheet id +0x006, SHP/SEQ animation pointers +0x1F4+, a
        # transparent-sprite bit +0x012&0x01, the frog/crystal/TREASURE graphic offset +0x07A,
        # render-side status flags +0x144 with Treasure 0x08 / Frog 0x04 / Crystal 0x01) and
        # carries a BACK-POINTER to the unit's battle data at +0x134. This verb scans every
        # committed region for pointers to the slot's combat base / band entry: any IC
        # equivalent struct should hold one. Read-only. Run on a LIVE unit first (control),
        # then on the chest slot; diff the two candidates' surroundings.
    python tools\\probes\\spawn_probe.py --selftest
        # offline: constants arithmetic + classifier/fill-plan logic. No game required.

LIVE RESULTS (owner-run sessions 2026-07-09):
  Census: ACTIVE units read P1 mostly 0x01 with 0x00/0xFF exceptions on frozen/mirror-suspect
  seats; outer player-region slots (n >= +5) held residue or bytecode/offset-table data from
  past the unit array (hence the GARBAGE class).
  Q1: non-scripted battles carry NO engine-loaded dormant units (two battles: nothing past the
  active seats but residue/garbage), matching the ENTD recon prediction that failed-roll
  RandomlyP units are never generated; scripted-reveal story fights remain the only candidate
  hunting ground.
  Q2: the extrapolated 0x0183 placements (P1/P2) and the ENTD-echo extrapolations are
  FALSIFIED: flat across deaths, an undead die-and-rise cycle, and treasure pops. The real
  in-slot lifecycle pair is the +0x45 status byte and the +0x46 conversion marker (mirrors at
  +0x1D3/+0x1D4, delta +0x18E), decoded from the s15 chest-pop deathdiff.
  Q3 (superseded by a STRONGER test, the revert): restoring the pre-pop bytes on the s15 chest
  was ACCEPTED by the engine logic-side: hearts re-rendered over the chest, the monk's portrait
  re-entered the Combat Timeline (no AV: real identity object), and a Phoenix Down on the chest
  REVIVED the unit (health bar over the chest). The FIELD MODEL stayed a chest through multiple
  turns, and when the reverted unit's own turn arrived the battle SOFT-LOCKED (a chest has no
  actor to execute a turn).
  CONCLUSION: the raw-flag spawn path is DEAD for rendering. In-slot state flips drive
  scheduler, timeline, targeting, and revival BOTH ways, but the field model is welded at
  conversion time (consistent with the Frog-rebuild and write-and-hold-spawn walls). Combat
  +0x00 (SpriteSet) NEVER edged at the pop: the chest model is scene-graph-side and there is
  nothing in-slot to swap back (PSX corroboration: changing a spritesheet, battle stat 0x015F,
  requires a RemoveUnit+AddUnit cycle even on PSX; sprite bytes are construction-read). A
  dormant never-constructed slot has strictly less than the chest had, so flag-flip activation
  cannot render a Body Double. NEXT STEP: the treasure pop is a REPEATABLE mid-battle engine
  model-construction event (unit model torn down, chest model built) with a known trigger
  byte; CE what-writes on combat +0x62 (band +0x46, `addr` prints the absolute address) at a
  pop lands inside the conversion routine, and the model-construction call it makes is the
  crystallization-equivalent primitive for spawning (the AddUnit/Draw hunt, now with a cheap
  trigger). UNTESTED VARIANT: frog-cast on the reverted+revived unit inside the window before
  its own turn (the Frog rebuild is a proven model-rebuild event; a frog can act, which may
  dodge the soft-lock). TRAP: a reverted unit that gets a TURN soft-locks the battle.
  Request-layer test (inflict, 3 owner tapes 2026-07-09): pending-field writes are CONSUMED on
  the engine's schedule (5 to 17s, battle-phase-linked) but NEVER applied: poison id 24 on a
  living unit, treasure id 15 on a living unit AND on a fresh corpse all flushed with no
  effect (the corpse later popped NATURALLY, dead 0x20 -> 0x40: the crystal variant). The
  field is engine scratch/output, not a free-standing request queue. FINAL PICTURE: every
  EXTERNAL write lane is exhausted; the remaining levers are in-process cold-calls of the
  apply engine 0x150BF66DC(slot, mode) or the dispatch 0x1401FB064(id+1, ...) on the right
  thread (callout-banner precedent), or the event-script AddUnit/Draw layer.
"""
import ctypes
import ctypes.wintypes as wt
import pathlib
import struct
import sys
import time
from collections import Counter

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
from battle_cheats import (
    ru8, ru16, wu8, wu16, rpm, _require_game,
    _band_entry_addr, _is_valid_entry, BAND_SLOTS, PLAYER_SLOT_THRESHOLD,
    A_LEVEL, A_BRAVE, A_FAITH, A_HP, A_MAXHP, A_GX, A_GY,
    A_DEAD_STATUS, A_DEAD_BIT, A_ALLEG, A_CT,
)

# Candidate presence offsets, band-entry-relative. Provenance in the module docstring.
A_PRESENT = 0x199        # P1: owner's 0x0183 existence enum via the +0x32 PSX-to-frame delta
A_PRESENT_ALT = 0x167    # P2: 0x0183 read as frame-relative (0x183 - 0x1C)
A_INBATTLE = 0x12        # INB: combat +0x2E low byte, pulsing trap, read-only reference
A_X18_ECHO = 0x172       # X18: ENTD 0x18 linear extrapolation, low confidence, read-only
A_LOAD_ECHO = 0x15B      # LOAD: ENTD 0x01 linear extrapolation, low confidence, read-only
A_CT_TURN = 0x09         # band +0x09 = combat +0x25 (Offsets.ACtTurn): the proven turn-count
                         # READ. The harness A_CT (band +0x25) is the ExtraTurn slam WRITE
                         # byte; its reads are documented unreliable (Offsets.ACtSlam), so
                         # activate samples it only as a secondary signal.

# Unit-to-treasure conversion signature, decoded from the owner's deathdiff tape 2026-07-09
# (s15 corpse -> chest pop, all edges in one 100ms frame): dead +0x45 bit 0x20 CLEARS (0x40
# observed on the crystal-suspect s9 in the earlier watch tape), band +0x46 flips 0 -> 1, the
# crystal counter (combat +0x07) resets to 3, scheduler CT (+0x25 slam byte) zeroes, and a
# mirror pair at +0x1D3/+0x1D4 (combat +0x1EF/+0x1F0, exactly +0x18E above the primaries)
# tracks +0x45/+0x46. Transients +0x1C0=1 / +0x1C9=8 self-cleared ~26s later.
A_CONVERT = 0x46         # the conversion marker byte (0 unit, 1 treasure/crystal entity)
A_DEAD_MIRROR = 0x1D3    # mirror of the +0x45 status byte
A_CONVERT_MIRROR = 0x1D4 # mirror of the +0x46 conversion marker
A_COUNTER = -0x15        # crystal/heart counter at combat +0x07 (despawn_probe's byte)

# STATUS SYSTEM DECODE (owner CE captures + disasm, 2026-07-09). The engine keeps 40 statuses
# (ids 0..39) in 5-byte MSB-first bitfields, THREE layers per unit:
#   innate/base  band +0x3B..+0x3F
#   inflicted    band +0x1D3..+0x1D7 (the "mirrors": actually the persistent OR-source)
#   composed     band +0x45..+0x49 (current = inflicted | innate, re-derived per frame by the
#                compose loop at 0x150BD0990/0x150BF6936: dest[+0x0A] = src[+0x198] | src[0])
# The apply engine (prologue 0x150BF66DC; per-status loop entry 0x150BF6767) consumes a
# PENDING-ADD bitfield at combat +0x1DB (= image 0x141853E9E + slot*0x200 + 0x1D), runs the
# conflict scan + timer-table copy, ORs the bit into the inflicted layer, and dispatches the
# status event via call 0x1401FB064(statusId+1, mode, unit) (checker call 0x140278D68). The
# treasure pop queued id 15 here (deathdiff transient band +0x1C0 = 0x01, self-cleared).
# Known ids from the proven composed-layer bits: 2 Dead (+0x45/0x20), 3 Undead (+0x45/0x10),
# 4 Charging (+0x45/0x08), 9 team-flip/invite-family (special-cased), 15 Treasure/Crystal
# (+0x46/0x01), 18 Reraise (+0x47/0x20), 24 Poison (+0x48/0x80), 25 Regen, 26 Protect,
# 27 Shell, 28 Haste, 34 Charm (+0x49/0x20, team-color special case), 38 Reflect, 39 Doom.
A_PENDING_ADD = 0x1BF    # band-relative base of the 5-byte pending status-ADD bitfield
A_INFLICTED = 0x1D3      # band-relative base of the 5-byte inflicted/persistent layer
A_COMPOSED = 0x45        # band-relative base of the 5-byte composed/current layer
CANDS = (("P1", A_PRESENT), ("P2", A_PRESENT_ALT), ("INB", A_INBATTLE),
         ("X18", A_X18_ECHO), ("LOAD", A_LOAD_ECHO))

CORE_LEN = 0x60          # identity/status core sampled for the DORMANT-vs-ZEROED call
DORMANT_MIN_NONZERO = 4  # fewer nonzero core bytes than this = ZEROED (nothing to activate)
COMBAT_SLOT_LEN = 0x200  # full combat-slot stride, diffed whole by deathdiff
DIFF_MUTE_AFTER = 6      # per-offset edge prints before deathdiff mutes that offset as noisy

# Hex windows printed for non-ZEROED slots (band-relative start, length):
# window A covers P1 + the turn-flag block (+0x19C..0x19F); window B covers LOAD/P2/X18 and
# runs through +0x187 so the full +0x17A..0x181 action-echo block is captured.
HEX_WINDOWS = ((0x190, 0x18), (0x158, 0x30))


def _u16(core, off):
    return struct.unpack_from("<H", core, off)[0]


def _classify(valid, core):
    """Pure classifier over one core snapshot: ACTIVE (engine-valid), DORMANT-POP (invalid but
    holds data whose identity fields are all zero-or-in-range: a plausible dormant unit),
    GARBAGE (nonzero data with implausible fields: adjacent non-unit memory, never
    activatable), ZEROED (nothing to activate), UNREADABLE (core read failed)."""
    if valid:
        return "ACTIVE"
    if core is None:
        return "UNREADABLE"
    nonzero = sum(1 for b in core if b)
    if nonzero < DORMANT_MIN_NONZERO:
        return "ZEROED"
    plausible = (core[A_LEVEL] <= 99 and core[A_BRAVE] <= 100 and core[A_FAITH] <= 100
                 and _u16(core, A_HP) < 2000 and _u16(core, A_MAXHP) < 2000
                 and core[A_GX] <= 30 and core[A_GY] <= 30)
    return "DORMANT-POP" if plausible else "GARBAGE"


def _fill_plan(lvl, br, fa, hp, mhp):
    """Pure Q3 sanity-fill plan: touch ONLY fields _is_valid_entry would reject, so plausible
    dormant data (a benched unit's real stats) is never stomped. Returns (label, off, width, val)."""
    plan = []
    if lvl is None or not (1 <= lvl <= 99):
        plan.append(("level", A_LEVEL, 1, 5))
    if br is None or not (1 <= br <= 100):
        plan.append(("brave", A_BRAVE, 1, 60))
    if fa is None or not (1 <= fa <= 100):
        plan.append(("faith", A_FAITH, 1, 60))
    new_mhp = mhp if (mhp is not None and 1 <= mhp < 2000) else 50
    if new_mhp != mhp:
        plan.append(("maxhp", A_MAXHP, 2, new_mhp))
    if hp is None or hp < 1 or hp > new_mhp:
        plan.append(("hp", A_HP, 2, new_mhp))
    return plan


def _hx(v):
    return "??" if v is None else "%02X" % v


def _diff_bytes(prev, cur):
    """Pure byte-level diff of two snapshots: [(offset, old, new)] over the common length."""
    return [(i, prev[i], cur[i]) for i in range(min(len(prev), len(cur))) if prev[i] != cur[i]]


def _n(v):
    """None-safe cell for width-formatted table fields (format(None, '>3') raises)."""
    return "?" if v is None else v


def _is_converted(dead, marker, hp):
    """Treasure/crystal-conversion signature (owner deathdiff tape 2026-07-09): the pop clears
    the dead bit (chest reads 0x00; 0x40 seen on a crystal-suspect), sets the +0x46 marker to 1,
    and the slot keeps hp 0 with identity intact. A live unit never matches (hp > 0, marker 0)."""
    return (marker == 1 and hp == 0
            and dead is not None and not (dead & A_DEAD_BIT))


def _has_identity_anchor(lvl, mhp):
    """Q3 refusal predicate: a genuinely engine-loaded dormant unit should already carry a
    plausible level or maxHP. Neither valid means there is nothing to activate; filling every
    field by hand would FABRICATE a unit (the known-dead write-and-hold path)."""
    return (lvl is not None and 1 <= lvl <= 99) or (mhp is not None and 1 <= mhp < 2000)


def _state(s):
    e = _band_entry_addr(s)
    core = rpm(e, CORE_LEN)   # one snapshot; all sub-0x60 fields parse from it for consistency
    valid = _is_valid_entry(e)
    if core is None:
        f = dict(nz=None, lvl=None, br=None, fa=None, hp=None, mhp=None,
                 gx=None, gy=None, dead=None, ct=None, alleg=None)
    else:
        f = dict(nz=sum(1 for b in core if b),
                 lvl=core[A_LEVEL], br=core[A_BRAVE], fa=core[A_FAITH],
                 hp=_u16(core, A_HP), mhp=_u16(core, A_MAXHP),
                 gx=core[A_GX], gy=core[A_GY],
                 dead=core[A_DEAD_STATUS], ct=core[A_CT], alleg=core[A_ALLEG])
    return dict(
        slot=s, n=s - 24, entry=e,
        side="PLAYER" if s >= PLAYER_SLOT_THRESHOLD else "ENEMY",
        valid=valid, cls=_classify(valid, core),
        cands={name: ru8(e + off) for name, off in CANDS},
        **f,
    )


def _hexwin(e, start, length):
    b = rpm(e + start, length)
    if b is None:
        return [f"    +0x{start:03X}: <unreadable>"]
    lines = []
    for row in range(0, length, 8):
        chunk = b[row:row + 8]
        lines.append(f"    +0x{start + row:03X}: " + " ".join("%02X" % x for x in chunk))
    return lines


def _print_table(states):
    print(f"{'slot':>4} {'n':>4} {'side':<6} {'class':<12} {'nz':>3} {'lvl':>3} {'br':>3} "
          f"{'fa':>3} {'hp':>9} {'pos':>7} {'ct':>3} {'dead':>4} {'al':>3}  "
          + " ".join(f"{name:>4}" for name, _ in CANDS))
    print("-" * 110)
    for u in states:
        hp = f"{_n(u['hp'])}/{_n(u['mhp'])}"
        pos = f"({_n(u['gx'])},{_n(u['gy'])})"
        print(f"{u['slot']:>4} {u['n']:>+4} {u['side']:<6} {u['cls']:<12} "
              f"{_n(u['nz']):>3} {_n(u['lvl']):>3} {_n(u['br']):>3} {_n(u['fa']):>3} "
              f"{hp:>9} {pos:>7} {_n(u['ct']):>3} {_hx(u['dead']):>4} {_hx(u['alleg']):>3}  "
              + " ".join(f"{_hx(u['cands'][name]):>4}" for name, _ in CANDS))


def cmd_list():
    _require_game()
    states = [_state(s) for s in range(BAND_SLOTS)]
    _print_table(states)

    print("\nQ2 cross-tab (candidate byte values per class; the real presence byte should split")
    print("cleanly, e.g. ACTIVE all 01 while DORMANT-POP reads 00):")
    for name, off in CANDS:
        parts = []
        for cls in ("ACTIVE", "DORMANT-POP", "GARBAGE", "ZEROED", "UNREADABLE"):
            vals = Counter(_hx(u["cands"][name]) for u in states if u["cls"] == cls)
            if vals:
                parts.append(cls + " {" + ", ".join(f"{v}:{c}" for v, c in vals.most_common()) + "}")
        print(f"  {name:>4} (band +0x{off:03X}): " + " | ".join(parts))

    interesting = [u for u in states if u["cls"] in ("ACTIVE", "DORMANT-POP")]
    print("\nhex windows (non-ZEROED slots):")
    for u in interesting:
        print(f"  s{u['slot']} (n={u['n']:+d}, {u['cls']}):")
        for start, length in HEX_WINDOWS:
            for line in _hexwin(u["entry"], start, length):
                print(line)

    garbage = [u for u in states if u["cls"] == "GARBAGE"]
    if garbage:
        print(f"\n{len(garbage)} GARBAGE slot(s) (implausible fields, adjacent non-unit memory,"
              " never activatable): slots " + ", ".join(str(u["slot"]) for u in garbage))
    dormant = [u for u in states if u["cls"] == "DORMANT-POP"]
    anchored = [u for u in dormant if _has_identity_anchor(u["lvl"], u["mhp"])]
    if anchored:
        print(f"\n{len(anchored)} anchored DORMANT-POP candidate(s): slots "
              + ", ".join(str(u["slot"]) for u in anchored)
              + "  (activate takes one of these)")
    elif dormant:
        print(f"\n{len(dormant)} DORMANT-POP slot(s) but none carries an identity anchor"
              " (valid level or maxHP): residue, not a benched unit; activate will refuse them.")
    else:
        print("\nno DORMANT-POP slots in this battle: nothing to activate here. Story battles"
              "\nwith scripted reveals (Limberry demons, Riovanes) are the likely hunting ground;"
              "\nrandom encounters probably never generate their failed-roll units.")


def _watch_tuple(s):
    """The edge-watched subset: classification, stable candidates, dead byte. INB is excluded
    (it pulses by design and would flood the log)."""
    e = _band_entry_addr(s)
    core = rpm(e, CORE_LEN)
    valid = _is_valid_entry(e)
    return (
        _classify(valid, core),
        ru8(e + A_PRESENT), ru8(e + A_PRESENT_ALT),
        ru8(e + A_X18_ECHO), ru8(e + A_LOAD_ECHO),
        None if core is None else core[A_DEAD_STATUS],
    )


def cmd_watch(seconds=120.0):
    _require_game()
    print("initial census:")
    _print_table([_state(s) for s in range(BAND_SLOTS)])
    print(f"\nwatching all {BAND_SLOTS} slots for {seconds:.0f}s "
          "(class / P1 / P2 / X18 / LOAD / dead edges; Ctrl+C to stop).")
    print("positive control: crystallize a unit now (despawn_probe.py remove, or play it out)"
          "\nand watch its P1: the existence enum predicts 01 -> 02 or 80 on the crystal edge.\n")
    names = ("class", "P1", "P2", "X18", "LOAD", "dead")
    prev = {s: _watch_tuple(s) for s in range(BAND_SLOTS)}
    start = time.monotonic()
    end = start + seconds
    try:
        while time.monotonic() < end:
            time.sleep(0.1)
            for s in range(BAND_SLOTS):
                cur = _watch_tuple(s)
                if cur == prev[s]:
                    continue
                t = time.monotonic() - start
                for i, name in enumerate(names):
                    if cur[i] == prev[s][i]:
                        continue
                    old, new = prev[s][i], cur[i]
                    if name == "class":
                        print(f">>> [+{t:5.1f}s] s{s}: class {old} -> {new}")
                    else:
                        print(f"    [+{t:5.1f}s] s{s}: {name} {_hx(old)} -> {_hx(new)}")
                prev[s] = cur
    except KeyboardInterrupt:
        print("\nstopped by operator.")
    print("watch over. If a crystallize happened, the P1 edge above is the Q2 verdict evidence.")


def cmd_deathdiff(slot, seconds=240.0):
    _require_game()
    if not (0 <= slot < BAND_SLOTS):
        print(f"slot must be 0..{BAND_SLOTS - 1}")
        sys.exit(2)
    e = _band_entry_addr(slot)
    base = e - 0x1C   # combat slot base, so the crystal counter (+0x07) is inside the window
    snap = rpm(base, COMBAT_SLOT_LEN)
    if snap is None:
        print(f"slot {slot} combat block unreadable; is a battle loaded?")
        sys.exit(2)
    u = _state(slot)
    print(f"=== DEATHDIFF s{slot} (n={u['n']:+d}) {u['side']} {u['cls']} lvl={u['lvl']} "
          f"hp={u['hp']}/{u['mhp']} pos=({u['gx']},{u['gy']}) dead={_hx(u['dead'])} ===")
    print(f"diffing the full 0x{COMBAT_SLOT_LEN:X}-byte combat slot every 100ms for "
          f"{seconds:.0f}s.")
    print("Kill this unit MID-BATTLE and let it crystallize while the battle continues")
    print("(despawn_probe.py remove drives the crystal counter on demand; hold-at-3 parks the")
    print(f"corpse until you are ready). Noisy offsets mute after {DIFF_MUTE_AFTER} edges."
          " Ctrl+C to stop.\n")
    counts = Counter()
    start = time.monotonic()
    end = start + seconds
    try:
        while time.monotonic() < end:
            time.sleep(0.1)
            cur = rpm(base, COMBAT_SLOT_LEN)
            if cur is None:
                continue
            t = time.monotonic() - start
            for off, old, new in _diff_bytes(snap, cur):
                counts[off] += 1
                if counts[off] <= DIFF_MUTE_AFTER:
                    mute = "  (muting: noisy)" if counts[off] == DIFF_MUTE_AFTER else ""
                    print(f"[+{t:6.1f}s] combat+0x{off:03X} (band {off - 0x1C:+#06x}): "
                          f"{old:02X} -> {new:02X}{mute}")
            snap = cur
    except KeyboardInterrupt:
        print("\nstopped by operator.")
    if not counts:
        print("\nno byte in the slot changed during the watch.")
        return
    rare = sorted((off, c) for off, c in counts.items() if c <= 3)
    print("\n=== edge summary (rare edges = lifecycle candidates) ===")
    print("rare (<=3 edges): " + (", ".join(f"+0x{o:03X}:{c}" for o, c in rare) or "none"))
    print("all offsets: " + ", ".join(f"+0x{o:03X}:{c}" for o, c in sorted(counts.items())))


# ---- whole-VA pointer sweep (findrender), cribbed from attack_table_scan.py ----
_MEM_COMMIT, _PAGE_GUARD, _PAGE_NOACCESS = 0x1000, 0x100, 0x01
_READABLE = {0x02, 0x04, 0x08, 0x20, 0x40, 0x80}
_SWEEP_CHUNK, _SWEEP_OVERLAP = 4 * 1024 * 1024, 16
_REGION_SKIP = 2 * 1024 * 1024 * 1024  # skip pathological mappings (>2 GB)
_k32 = ctypes.WinDLL("kernel32", use_last_error=True)


class _MBI(ctypes.Structure):
    _fields_ = [("BaseAddress", ctypes.c_ulonglong), ("AllocationBase", ctypes.c_ulonglong),
                ("AllocationProtect", wt.DWORD), ("PartitionId", wt.WORD),
                ("RegionSize", ctypes.c_ulonglong), ("State", wt.DWORD),
                ("Protect", wt.DWORD), ("Type", wt.DWORD)]


def _regions(h):
    addr = 0
    mbi = _MBI()
    while _k32.VirtualQueryEx(h, ctypes.c_void_p(addr), ctypes.byref(mbi), ctypes.sizeof(mbi)):
        if (mbi.State == _MEM_COMMIT and not (mbi.Protect & (_PAGE_GUARD | _PAGE_NOACCESS))
                and (mbi.Protect & 0xFF) in _READABLE):
            yield mbi.BaseAddress, mbi.RegionSize
        nxt = mbi.BaseAddress + mbi.RegionSize
        if nxt <= addr:
            break
        addr = nxt


def _find_all(buf, pat, base):
    """Pure: absolute addresses of every occurrence of pat in buf mapped at base."""
    out = []
    i = buf.find(pat)
    while i != -1:
        out.append(base + i)
        i = buf.find(pat, i + 1)
    return out


# Any little-endian pointer into the combat array region (0x14185xxxx) carries this rare byte
# tag at offsets 2..7, so one find() pass catches pointers to ANY interior slot offset; the
# u64 range check then narrows to the target slot.
_PTR_TAG = b"\x85\x41\x01\x00\x00\x00"


def _interior_hits(buf, base, combat):
    """Pure: (absolute address, interior offset) for every pointer in buf that lands anywhere
    inside [combat, combat+0x200)."""
    out = []
    i = buf.find(_PTR_TAG)
    while i != -1:
        if i >= 2 and i + 6 <= len(buf):
            v = struct.unpack_from("<Q", buf, i - 2)[0]
            if combat <= v < combat + 0x200:
                out.append((base + i - 2, v - combat))
        i = buf.find(_PTR_TAG, i + 1)
    return out


def cmd_findrender(slot):
    h = _require_game()
    if not (0 <= slot < BAND_SLOTS):
        print(f"slot must be 0..{BAND_SLOTS - 1}")
        sys.exit(2)
    e = _band_entry_addr(slot)
    pats = {"combat-base": struct.pack("<Q", e - 0x1C), "band-entry": struct.pack("<Q", e)}
    print(f"sweeping every committed region for pointers to s{slot} "
          f"(combat base 0x{e - 0x1C:X}, band entry 0x{e:X})...")
    hits = {}   # addr -> pattern name (dedupe across chunk overlap)
    scanned = 0
    skipped = 0
    for base, size in _regions(h):
        if size > _REGION_SKIP:
            skipped += 1
            continue
        pos, end, carry, carry_base = base, base + size, b"", base
        while pos < end:
            data = rpm(pos, min(_SWEEP_CHUNK, end - pos))
            pos += min(_SWEEP_CHUNK, end - pos)
            if data is None:
                carry, carry_base = b"", pos
                continue
            buf = carry + data
            for name, pat in pats.items():
                for a in _find_all(buf, pat, carry_base):
                    hits.setdefault(a, name)
            for a, off in _interior_hits(buf, carry_base, e - 0x1C):
                hits.setdefault(a, f"interior+0x{off:03X}")
            carry = buf[-_SWEEP_OVERLAP:]
            carry_base = pos - len(carry)
        scanned += size
    print(f"scanned {scanned // (1024 * 1024)} MB ({skipped} oversized regions skipped), "
          f"{len(hits)} pointer hit(s).\n")
    band_lo, band_hi = _band_entry_addr(0) - 0x1C, _band_entry_addr(BAND_SLOTS - 1) + 0x1E4
    per_bucket = Counter()   # context dumps are rationed PER 16MB REGION, so one huge
    for a in sorted(hits):   # repetitive arena cannot starve the lonely hits elsewhere
        kind = "static" if 0x140000000 <= a < 0x150000000 else "heap"
        inband = "  (inside the band array itself)" if band_lo <= a < band_hi else ""
        note = "  (= Offsets.ActorPtr)" if a == 0x14186AF68 else ""
        print(f"0x{a:012X}  [{hits[a]}] {kind}{inband}{note}")
        bucket = a >> 24
        per_bucket[bucket] += 1
        if per_bucket[bucket] <= 6:
            ctx = rpm(a - 0x20, 0x60)
            if ctx is not None:
                for row in range(0, 0x60, 0x10):
                    print(f"    {a - 0x20 + row:012X}: "
                          + " ".join("%02X" % b for b in ctx[row:row + 0x10]))
    if not hits:
        print("no pointers found: an IC render struct either keys the unit differently"
              "\n(index, not pointer) or lives behind an indirection this sweep cannot see.")
    else:
        print("\nnext: run this on a LIVE unit as a control, then on the chest slot; a hit"
              "\nwhose surrounding struct looks per-unit (and differs unit-to-unit) is the"
              "\nrender-struct candidate; watch it during that unit's animation to confirm.")


def cmd_peek(addrs, length=0x100):
    """Hexdump arbitrary absolute addresses (read-only; for chasing findrender hits)."""
    _require_game()
    for addr in addrs:
        print(f"\n=== 0x{addr:012X} ===")
        start = addr - 0x40
        buf = rpm(start, length)
        if buf is None:
            print("unreadable")
            continue
        for row in range(0, len(buf), 0x10):
            mark = " <-- hit" if start + row <= addr < start + row + 0x10 else ""
            print(f"{start + row:012X}: "
                  + " ".join("%02X" % b for b in buf[row:row + 0x10]) + mark)


def cmd_addr(slot):
    """Print absolute addresses for CE what-writes/what-accesses work on one slot."""
    if not (0 <= slot < BAND_SLOTS):
        print(f"slot must be 0..{BAND_SLOTS - 1}")
        sys.exit(2)
    e = _band_entry_addr(slot)
    print(f"s{slot} (n={slot - 24:+d}):")
    for label, addr in (
        ("combat base", e - 0x1C),
        ("band entry", e),
        ("crystal counter (combat +0x07)", e + A_COUNTER),
        ("status byte (band +0x45)", e + A_DEAD_STATUS),
        ("conversion marker (band +0x46)", e + A_CONVERT),
        ("status mirror (band +0x1D3)", e + A_DEAD_MIRROR),
        ("marker mirror (band +0x1D4)", e + A_CONVERT_MIRROR),
    ):
        print(f"  {label:<34} 0x{addr:012X}")


def _status_bit(sid):
    """Pure: (byte offset within a 5-byte status bitfield, MSB-first mask) for status id 0..39."""
    return sid >> 3, 0x80 >> (sid & 7)


def cmd_inflict(slot, sid, seconds=60.0):
    """Queue a status id in the unit's PENDING-ADD bitfield so the ENGINE's own apply pipeline
    inflicts it (conflict checks, timers, event dispatch, and, for form statuses like treasure,
    the model swap). This is the request-layer injection: the despawn principle at the status
    system. Read the STATUS SYSTEM DECODE comment for the id table."""
    _require_game()
    if not (0 <= slot < BAND_SLOTS):
        print(f"slot must be 0..{BAND_SLOTS - 1}")
        sys.exit(2)
    if not (0 <= sid < 40):
        print("status id must be 0..39")
        sys.exit(2)
    u = _state(slot)
    if u["cls"] != "ACTIVE":
        print(f"REFUSED: slot {slot} classifies {u['cls']}; inflict targets a real unit.")
        sys.exit(2)
    e = u["entry"]
    off, mask = _status_bit(sid)
    pend, infl, comp = A_PENDING_ADD + off, A_INFLICTED + off, A_COMPOSED + off

    print(f"=== INFLICT status {sid} on s{slot} (n={u['n']:+d}) {u['side']} lvl={u['lvl']} "
          f"hp={u['hp']}/{u['mhp']} pos=({u['gx']},{u['gy']}) ===")
    print(f"  pending byte band+0x{pend:03X} mask 0x{mask:02X}; watching inflicted "
          f"band+0x{infl:03X} and composed band+0x{comp:03X}")
    print("THROWAWAY SAVE ONLY: the engine runs its own apply path; outcomes may include a"
          "\nmodel swap, a rejected (conflict-scanned) request, or unknown side effects.")
    if input("queue it? [y/n] ").strip().lower() != "y":
        print("aborted, nothing written")
        return

    old = ru8(e + pend)
    if old is None:
        print("pending byte unreadable; aborting")
        sys.exit(1)
    ok = wu8(e + pend, old | mask)
    print(f"  pending band+0x{pend:03X}: {_hx(old)} -> {_hx(old | mask)} "
          f"write={'OK' if ok else 'FAIL'} readback={_hx(ru8(e + pend))}")

    print(f"\nwatching for {seconds:.0f}s: the engine consuming the pending bit and the"
          "\ninflicted/composed layers gaining it. EYES ON THE UNIT for any model/overlay"
          "\nchange. Ctrl+C to stop.\n")
    start_t = time.monotonic()
    end_t = start_t + seconds
    last_log = 0.0
    consumed = applied = False
    try:
        while time.monotonic() < end_t:
            time.sleep(0.1)
            now = time.monotonic()
            p, i2, c = ru8(e + pend), ru8(e + infl), ru8(e + comp)
            if not consumed and p is not None and not (p & mask):
                consumed = True
                print(f">>> [+{now - start_t:5.1f}s] pending bit CONSUMED by the engine")
            if not applied and i2 is not None and (i2 & mask):
                applied = True
                print(f">>> [+{now - start_t:5.1f}s] inflicted layer GAINED the bit "
                      f"(engine accepted the status)")
            if now - last_log >= 1.0:
                last_log = now
                print(f"[+{now - start_t:5.1f}s] pend={_hx(p)} inflicted={_hx(i2)} "
                      f"composed={_hx(c)} dead={_hx(ru8(e + A_DEAD_STATUS))} "
                      f"hp={ru16(e + A_HP)}/{ru16(e + A_MAXHP)}")
    except KeyboardInterrupt:
        print("\nstopped by operator.")
    print("\n=== inflict verdict ===")
    print(f"  pending consumed: {'YES' if consumed else 'no (engine never picked it up)'}")
    print(f"  status applied:   {'YES' if applied else 'no (conflict-scan reject, or not seen)'}")
    print("  visible effect:   OPERATOR CALL (model swap / overlay / tick damage?)")


def cmd_revert(slot, seconds=90.0):
    """The reverse flip: restore a freshly-converted treasure/crystal slot to its pre-pop
    corpse state and watch whether the ENGINE re-materializes the unit model. The pop proved
    the engine swaps the field model on an in-slot state flip; this tests the direction we
    actually need for Body Double (state flip -> model CONSTRUCTED)."""
    _require_game()
    if not (0 <= slot < BAND_SLOTS):
        print(f"slot must be 0..{BAND_SLOTS - 1}")
        sys.exit(2)
    e = _band_entry_addr(slot)
    u = _state(slot)
    marker = ru8(e + A_CONVERT)
    if not u["valid"] or not _is_converted(u["dead"], marker, u["hp"]):
        print(f"REFUSED: slot {slot} does not match the treasure-conversion signature"
              f" (valid={u['valid']} dead={_hx(u['dead'])} marker={_hx(marker)} hp={u['hp']}).")
        print("  revert only touches a slot that just popped to a chest/crystal (marker 1,"
              "\n  dead bit clear, hp 0, identity intact). Run deathdiff/list to confirm.")
        sys.exit(2)

    print(f"=== REVERT target s{slot} (n={u['n']:+d}) {u['side']} lvl={u['lvl']} "
          f"hp={u['hp']}/{u['mhp']} pos=({u['gx']},{u['gy']}) dead={_hx(u['dead'])} "
          f"marker={_hx(marker)} ctr={_hx(ru8(e + A_COUNTER))} ===")
    print("\nrestores the pre-pop corpse bytes: marker 1->0 (+ its mirror), dead bit set"
          "\n(+ its mirror), crystal counter -> 3 (parked hearts). THROWAWAY SAVE ONLY, no"
          "\nrestore; do NOT open unit detail views on this slot afterwards."
          "\nKNOWN OUTCOME (owner run 2026-07-09): logic re-enrolls (hearts, timeline,"
          "\nPhoenix-Down-revivable) but the MODEL stays a chest, and the unit's own turn"
          "\nSOFT-LOCKS the battle. Run this only to reproduce/instrument the state machine.")
    if input("proceed with revert writes? [y/n] ").strip().lower() != "y":
        print("aborted, nothing written")
        return

    def w8(label, off, val):
        old = ru8(e + off)
        ok = wu8(e + off, val)
        back = ru8(e + off)
        print(f"  {label} band{off:+#06x}: {_hx(old)} -> {_hx(val)} "
              f"write={'OK' if ok else 'FAIL'} readback={_hx(back)}")

    print("\napplying revert writes:")
    w8("marker", A_CONVERT, 0x00)
    w8("marker mirror", A_CONVERT_MIRROR, 0x00)
    dead = ru8(e + A_DEAD_STATUS)
    w8("dead bit set", A_DEAD_STATUS, (dead or 0) | A_DEAD_BIT)
    mirror = ru8(e + A_DEAD_MIRROR)
    w8("dead mirror set", A_DEAD_MIRROR, (mirror or 0) | A_DEAD_BIT)
    w8("counter -> 3 (parked)", A_COUNTER, 3)

    print(f"\nwatching s{slot} for {seconds:.0f}s. EYES ON THE TILE: does the chest/crystal"
          "\nmodel change (corpse reappears, model vanishes, or nothing)? End a turn or two to"
          "\ngive the engine a redraw opportunity. Ctrl+C to stop.\n")
    start_t = time.monotonic()
    end_t = start_t + seconds
    last_log = 0.0
    try:
        while time.monotonic() < end_t:
            time.sleep(0.1)
            now = time.monotonic()
            if now - last_log >= 1.0:
                last_log = now
                print(f"[+{now - start_t:5.1f}s] dead={_hx(ru8(e + A_DEAD_STATUS))} "
                      f"marker={_hx(ru8(e + A_CONVERT))} "
                      f"mirrors={_hx(ru8(e + A_DEAD_MIRROR))}/{_hx(ru8(e + A_CONVERT_MIRROR))} "
                      f"ctr={_hx(ru8(e + A_COUNTER))} ctT={_hx(ru8(e + A_CT_TURN))} "
                      f"hp={ru16(e + A_HP)}/{ru16(e + A_MAXHP)}")
    except KeyboardInterrupt:
        print("\nstopped by operator.")
    print("\n=== revert verdict ===")
    print("  bytes held / engine re-flipped them: read the tail of the log above")
    print("  model change on screen: OPERATOR CALL")
    print("If the corpse re-materialized, the conversion state machine is drivable BOTH ways:")
    print("an engine-driven model-construction trigger exists (huge for Body Double). If the")
    print("chest stayed a chest, the visual is welded at conversion time (Frog precedent) and")
    print("the AddUnit/Draw routine hunt stays the path.")


def cmd_activate(slot, gx, gy, seconds=120.0):
    _require_game()
    if not (0 <= slot < BAND_SLOTS):
        print(f"slot must be 0..{BAND_SLOTS - 1}")
        sys.exit(2)
    if not (0 <= gx <= 30 and 0 <= gy <= 30):
        print("gx/gy must be 0..30 (harness position sanity bound)")
        sys.exit(2)

    u = _state(slot)
    e = u["entry"]
    if u["cls"] != "DORMANT-POP":
        print(f"REFUSED: slot {slot} classifies {u['cls']}, not DORMANT-POP.")
        print("  ACTIVE slots are real units; GARBAGE is adjacent non-unit memory (writing it"
              "\n  corrupts engine data, not a unit); ZEROED slots hold nothing to activate"
              "\n  (writing them blind is the known-dead fabrication path); UNREADABLE = no data.")
        sys.exit(2)

    # Identity-anchor guard: refuse a residue slot whose every identity field is invalid.
    # "Activating" such a slot degenerates into hand-fabricating a whole unit, the exact
    # known-dead path (sprite-less scheduler unit, timeline-detail AV) this probe must avoid.
    if not _has_identity_anchor(u["lvl"], u["mhp"]):
        print(f"REFUSED: slot {slot} holds no identity anchor (level {u['lvl']} and maxHP"
              f" {u['mhp']} are both invalid). Filling every field by hand would FABRICATE a"
              "\n  unit, not activate an engine-loaded one; that path is walled (renders blank,"
              "\n  AVs the timeline-detail view). Pick a slot with plausible dormant data.")
        sys.exit(2)

    # Identity-twin guard: a dormant-looking copy of a LIVE unit (mirror/bench clone) must not
    # be touched; flipping presence on a clone could corrupt the real unit's state.
    for s in range(BAND_SLOTS):
        if s == slot:
            continue
        o = _state(s)
        if o["valid"] and (o["lvl"], o["br"], o["fa"], o["mhp"]) == \
                          (u["lvl"], u["br"], u["fa"], u["mhp"]):
            print(f"REFUSED: slot {slot} identity (lvl {u['lvl']}, br {u['br']}, fa {u['fa']},"
                  f" mhp {u['mhp']}) twins ACTIVE slot {s}: likely a mirror/bench clone of a"
                  f" live unit.")
            sys.exit(2)

    print(f"=== ACTIVATE target s{slot} (n={u['n']:+d}) {u['side']}-region "
          f"lvl={u['lvl']} br={u['br']} fa={u['fa']} hp={u['hp']}/{u['mhp']} "
          f"nz={u['nz']} P1={_hx(u['cands']['P1'])} P2={_hx(u['cands']['P2'])} ===")
    for start, length in HEX_WINDOWS:
        for line in _hexwin(e, start, length):
            print(line)

    def w8(label, off, val):
        old = ru8(e + off)
        ok = wu8(e + off, val)
        back = ru8(e + off)
        print(f"  {label} band+0x{off:03X}: {_hx(old)} -> {_hx(val)} "
              f"write={'OK' if ok else 'FAIL'} readback={_hx(back)}")

    def w16(label, off, val):
        old = ru16(e + off)
        ok = wu16(e + off, val)
        back = ru16(e + off)
        print(f"  {label} band+0x{off:03X}: {old} -> {val} "
              f"write={'OK' if ok else 'FAIL'} readback={back}")

    print("\nTHROWAWAY SAVE ONLY: these writes have NO restore (restoring bytes the engine may")
    print("have adopted is worse than leaving them), and a poked session can corrupt the")
    print("autosave (quarantine autoenhanced.png first if this playthrough matters).")
    if input("proceed with activation writes? [y/n] ").strip().lower() != "y":
        print("aborted, nothing written")
        return

    print("\napplying activation writes (P1/P2 presence flip, fill-if-invalid, position, team):")
    for name, off in (("P1", A_PRESENT), ("P2", A_PRESENT_ALT)):
        old = u["cands"][name]
        if old == 0x01:
            print(f"  {name} band+0x{off:03X}: already 01, skipped")
        else:
            w8(name, off, 0x01)

    for label, off, width, val in _fill_plan(u["lvl"], u["br"], u["fa"], u["hp"], u["mhp"]):
        (w8 if width == 1 else w16)(label, off, val)

    dead = ru8(e + A_DEAD_STATUS)
    if dead is not None and dead & A_DEAD_BIT:
        w8("dead-bit clear", A_DEAD_STATUS, dead & ~A_DEAD_BIT)

    w8("gx", A_GX, gx)
    w8("gy", A_GY, gy)

    donor = next((s for s in range(PLAYER_SLOT_THRESHOLD, BAND_SLOTS)
                  if _is_valid_entry(_band_entry_addr(s))), None)
    if donor is None:
        print("  alleg: no valid player slot to copy from, left untouched")
    else:
        dv = ru8(_band_entry_addr(donor) + A_ALLEG)
        if dv is None:
            print(f"  alleg: donor s{donor} unreadable, left untouched")
        else:
            w8(f"alleg (copied from player s{donor}; NOTE side may be governed by slot index,"
               f" not this byte)", A_ALLEG, dv)

    print(f"\nwatching s{slot} for {seconds:.0f}s. END TURNS in game to advance the scheduler.")
    print("watch the tile ({},{}) for a sprite. Do NOT open the timeline/unit-detail view on"
          " the new unit (null-sprite AV precedent). Ctrl+C to stop.\n".format(gx, gy))

    start_t = time.monotonic()
    end_t = start_t + seconds
    last_log = 0.0
    ct_turn_vals = set()
    ct_slam_vals = set()
    prev_valid = None
    reverts = 0
    try:
        while time.monotonic() < end_t:
            time.sleep(0.1)
            now = time.monotonic()
            valid = _is_valid_entry(e)
            ct_t = ru8(e + A_CT_TURN)
            ct_s = ru8(e + A_CT)
            if ct_t is not None:
                ct_turn_vals.add(ct_t)
            if ct_s is not None:
                ct_slam_vals.add(ct_s)
            if prev_valid is not None and valid != prev_valid:
                if valid:
                    print(f">>> [+{now - start_t:5.1f}s] s{slot} back to engine-plausible data")
                else:
                    reverts += 1
                    print(f">>> [+{now - start_t:5.1f}s] s{slot} REVERTED to invalid (the"
                          f" engine overwrote our bytes: evidence of engine-side management)")
            prev_valid = valid
            if now - last_log >= 1.0:
                last_log = now
                print(f"[+{now - start_t:5.1f}s] valid={valid} "
                      f"P1={_hx(ru8(e + A_PRESENT))} P2={_hx(ru8(e + A_PRESENT_ALT))} "
                      f"hp={ru16(e + A_HP)}/{ru16(e + A_MAXHP)} "
                      f"pos=({_n(ru8(e + A_GX))},{_n(ru8(e + A_GY))}) "
                      f"ctT={_hx(ct_t)} ctS={_hx(ct_s)} "
                      f"dead={_hx(ru8(e + A_DEAD_STATUS))}")
    except KeyboardInterrupt:
        print("\nstopped by operator.")

    print("\n=== Q3 data-side verdict ===")
    print(f"  writes held (never reverted to invalid): "
          f"{'no, engine reverted x' + str(reverts) if reverts else 'YES'}")
    print(f"  turn-count CT band+0x09 movement: "
          + (f"YES ({len(ct_turn_vals)} distinct values: scheduler-adoption evidence)"
             if len(ct_turn_vals) >= 3 else f"flat ({len(ct_turn_vals)} value(s))"))
    print(f"  slam CT band+0x25 movement: "
          + (f"YES ({len(ct_slam_vals)} distinct values)"
             if len(ct_slam_vals) >= 3 else "flat")
          + "  (a flat +0x25 is INCONCLUSIVE: its reads are documented unreliable)")
    print("  sprite on screen: OPERATOR CALL (did a unit appear at the target tile?)")
    print("Flat CT on both bytes is weak evidence, not proof of non-adoption; the decisive")
    print("signals are a sprite or the unit's turn coming up (do NOT open the timeline detail).")
    print("If no sprite rendered, the raw-flag path is dead as expected: record the verdict in")
    print("the docstring and point the follow-up at the engine's AddUnit/Draw event routines.")


def _selftest():
    ok = True

    def check(name, got, want):
        nonlocal ok
        if got == want:
            print(f"  {name}  OK")
        else:
            print(f"  {name}  FAIL (want {want!r}, got {got!r})")
            ok = False

    # Constants arithmetic (provenance in the docstring).
    check("P1 = PSX 0x183 + 0x32 delta - band 0x1C", A_PRESENT, 0x183 + 0x32 - 0x1C)
    check("P2 = frame 0x183 - band 0x1C", A_PRESENT_ALT, 0x183 - 0x1C)
    check("INB = combat 0x2E - band 0x1C", A_INBATTLE, 0x2E - 0x1C)
    check("X18 = ENTD 0x18 + echo delta 0x15A", A_X18_ECHO, 0x18 + 0x15A)
    check("LOAD = ENTD 0x01 + echo delta 0x15A", A_LOAD_ECHO, 0x01 + 0x15A)
    check("CT-turn read = combat 0x25 - band 0x1C", A_CT_TURN, 0x25 - 0x1C)
    check("band stride", _band_entry_addr(24) - _band_entry_addr(23), 0x200)
    check("player threshold", PLAYER_SLOT_THRESHOLD, 24)

    # Classifier logic.
    check("classify valid", _classify(True, None), "ACTIVE")
    check("classify unreadable", _classify(False, None), "UNREADABLE")
    check("classify zeroed", _classify(False, bytes(CORE_LEN)), "ZEROED")
    sparse = bytearray(CORE_LEN)
    sparse[0] = 1
    check("classify near-zero is ZEROED", _classify(False, bytes(sparse)), "ZEROED")
    populated = bytearray(CORE_LEN)
    populated[0:8] = b"\x05\x01\x02\x03\x04\x05\x06\x07"
    check("classify populated in-range is DORMANT-POP",
          _classify(False, bytes(populated)), "DORMANT-POP")
    garbage = bytearray(populated)
    garbage[A_BRAVE] = 199
    check("classify implausible brave is GARBAGE", _classify(False, bytes(garbage)), "GARBAGE")
    garbage2 = bytearray(populated)
    struct.pack_into("<H", garbage2, A_MAXHP, 25613)
    check("classify implausible maxhp is GARBAGE", _classify(False, bytes(garbage2)), "GARBAGE")

    # Fill plan: only invalid fields are touched.
    check("fill: all-sane touches nothing", _fill_plan(10, 70, 70, 40, 80), [])
    plan = dict((p[0], p[3]) for p in _fill_plan(0, 0, 0, 0, 0))
    check("fill: empty slot gets full sanity set",
          sorted(plan), ["brave", "faith", "hp", "level", "maxhp"])
    check("fill: hp fills to new maxhp", plan["hp"], plan["maxhp"])
    check("fill: hp above maxhp refilled",
          [p[0] for p in _fill_plan(10, 70, 70, 500, 80)], ["hp"])
    check("fill: None reads treated as invalid",
          sorted(p[0] for p in _fill_plan(None, None, None, None, None)),
          ["brave", "faith", "hp", "level", "maxhp"])

    # Status-bitfield math (cross-checked against every proven composed-layer bit).
    check("status bit: dead id 2 = +0x45 byte0 mask 0x20",
          (_status_bit(2), A_COMPOSED + 0), ((0, 0x20), 0x45))
    check("status bit: treasure id 15 = byte1 mask 0x01", _status_bit(15), (1, 0x01))
    check("status bit: poison id 24 = byte3 mask 0x80", _status_bit(24), (3, 0x80))
    check("status bit: charm id 34 = byte4 mask 0x20", _status_bit(34), (4, 0x20))
    check("status bit: doom id 39 = byte4 mask 0x01", _status_bit(39), (4, 0x01))
    check("pending pop transient: id 15 lands at band +0x1C0 as 0x01",
          (A_PENDING_ADD + _status_bit(15)[0], _status_bit(15)[1]), (0x1C0, 0x01))

    # Conversion signature constants and predicate (owner deathdiff tape 2026-07-09).
    check("convert marker = combat 0x62 - band 0x1C", A_CONVERT, 0x62 - 0x1C)
    check("mirror pair sits +0x18E above the primaries",
          (A_DEAD_MIRROR - A_DEAD_STATUS, A_CONVERT_MIRROR - A_CONVERT), (0x18E, 0x18E))
    check("counter = combat 0x07 - band 0x1C", A_COUNTER, 0x07 - 0x1C)
    check("converted: chest (dead 00, marker 1, hp 0)", _is_converted(0x00, 1, 0), True)
    check("converted: crystal variant (dead 40)", _is_converted(0x40, 1, 0), True)
    check("converted: live unit refused", _is_converted(0x00, 0, 350), False)
    check("converted: plain corpse refused (dead bit set)", _is_converted(0x20, 1, 0), False)
    check("converted: None dead refused", _is_converted(None, 1, 0), False)

    # findrender's pure scan helpers.
    check("find_all: none", _find_all(b"\x00\x01\x02", b"\xFF", 0), [])
    check("find_all: maps to absolute base",
          _find_all(b"\x00AB\x00AB", b"AB", 100), [101, 104])
    check("find_all: overlapping occurrences",
          _find_all(b"AAA", b"AA", 0), [0, 1])
    combat = 0x141853EE0
    inbuf = b"XXXX" + struct.pack("<Q", combat + 0x40) + b"YY"
    check("interior: catches an inside-slot pointer with its offset",
          _interior_hits(inbuf, 1000, combat), [(1004, 0x40)])
    outbuf = b"XXXX" + struct.pack("<Q", combat + 0x200) + b"YY"
    check("interior: next slot's base is out of range",
          _interior_hits(outbuf, 1000, combat), [])

    # deathdiff's pure diff.
    check("diff: identical is empty", _diff_bytes(b"\x00\x01", b"\x00\x01"), [])
    check("diff: reports offset old new",
          _diff_bytes(b"\x00\x01\x02", b"\x00\xFF\x02"), [(1, 0x01, 0xFF)])
    check("diff: truncates to common length", _diff_bytes(b"\x00", b"\x01\x02"), [(0, 0x00, 0x01)])

    # Identity-anchor refusal predicate (fabrication guard).
    check("anchor: valid level suffices", _has_identity_anchor(5, 0), True)
    check("anchor: valid maxhp suffices", _has_identity_anchor(0, 50), True)
    check("anchor: neither is a refusal", _has_identity_anchor(0, 0), False)
    check("anchor: None reads are a refusal", _has_identity_anchor(None, None), False)
    check("anchor: absurd maxhp rejected", _has_identity_anchor(0, 2500), False)
    return ok


def main():
    args = sys.argv[1:]
    if not args or args[0] in ("-h", "--help", "help"):
        print(__doc__)
        return
    if args[0] == "--selftest":
        sys.exit(0 if _selftest() else 1)
    if args[0] == "list":
        cmd_list()
        return
    if args[0] == "watch":
        try:
            seconds = float(args[1]) if len(args) > 1 else 120.0
        except ValueError:
            print("usage: watch [seconds=120]")
            sys.exit(2)
        cmd_watch(seconds)
        return
    if args[0] == "peek":
        try:
            targets = [int(a, 16) for a in args[1:]]
        except ValueError:
            targets = []
        if not targets:
            print("usage: peek <hex-addr> [more hex-addrs...]")
            sys.exit(2)
        cmd_peek(targets)
        return
    if args[0] == "findrender":
        if len(args) < 2 or not args[1].lstrip("-").isdigit():
            print("usage: findrender <slot>")
            sys.exit(2)
        cmd_findrender(int(args[1]))
        return
    if args[0] == "addr":
        if len(args) < 2 or not args[1].lstrip("-").isdigit():
            print("usage: addr <slot>")
            sys.exit(2)
        cmd_addr(int(args[1]))
        return
    if args[0] == "inflict":
        nums = [x for x in args[1:3] if x.lstrip("-").isdigit()]
        if len(nums) < 2:
            print("usage: inflict <slot> <status-id 0..39> [seconds=60]")
            sys.exit(2)
        seconds = 60.0
        if len(args) > 3:
            try:
                seconds = float(args[3])
            except ValueError:
                print("usage: inflict <slot> <status-id 0..39> [seconds=60]")
                sys.exit(2)
        cmd_inflict(int(nums[0]), int(nums[1]), seconds)
        return
    if args[0] == "revert":
        if len(args) < 2 or not args[1].lstrip("-").isdigit():
            print("usage: revert <slot> [seconds=90]")
            sys.exit(2)
        seconds = 90.0
        if len(args) > 2:
            try:
                seconds = float(args[2])
            except ValueError:
                print("usage: revert <slot> [seconds=90]")
                sys.exit(2)
        cmd_revert(int(args[1]), seconds)
        return
    if args[0] == "deathdiff":
        if len(args) < 2 or not args[1].lstrip("-").isdigit():
            print("usage: deathdiff <slot> [seconds=240]")
            sys.exit(2)
        seconds = 240.0
        if len(args) > 2:
            try:
                seconds = float(args[2])
            except ValueError:
                print("usage: deathdiff <slot> [seconds=240]")
                sys.exit(2)
        cmd_deathdiff(int(args[1]), seconds)
        return
    if args[0] == "activate":
        pos = args[1:5]
        if len(pos) < 3 or any(not x.lstrip("-").isdigit() for x in pos[:3]):
            print("usage: activate <slot> <gx> <gy> [seconds=120]")
            sys.exit(2)
        seconds = 120.0
        if len(pos) > 3:
            try:
                seconds = float(pos[3])
            except ValueError:
                print("usage: activate <slot> <gx> <gy> [seconds=120]")
                sys.exit(2)
        cmd_activate(int(pos[0]), int(pos[1]), int(pos[2]), seconds)
        return
    print(f"unknown verb: {args[0]!r}")
    print(__doc__)
    sys.exit(2)


if __name__ == "__main__":
    main()
