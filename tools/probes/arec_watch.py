r"""LW-167 stage 4 probe: does a basic Attack kill stamp the victim's ACTION RECORD the way an
ability kill does?

Read-only. Watches every battle unit's 12-byte per-unit action record (Offsets.AArec, band-entry
relative 0x184) plus each unit's HP (Offsets.AHp, band-entry relative 0x14) plus the engine actor
pointer (Offsets.ActorPtr), all at a configurable poll rate, and prints ONE line per CHANGE
(the house probe style: a printed line marks the start of a state, not a per-tick heartbeat).

Why this exists (plain language first): LW-167 wants a Living Poach that fires on a basic-Attack
kill but NEVER on an ability kill (the game already poaches those itself -- see docs/TODO.md
LW-167 and docs/CHANGELOG.md's LW-166 entry). The one open premise blocking that stage is whether
the corpse's action record reliably tells the two kill kinds apart. This tape lets the owner run
real battles and eyeball the answer instead of guessing from a doc comment.

Pre-registered beats (the owner runs these battles; each is one A/B data point)
--------------------------------------------------------------------------------
  1. Non-fatal Attack (a basic Attack that damages but does not kill).
  2. Fatal Attack (a basic Attack that lands the kill).
  3. Non-fatal Rend Weapon (a weapon-ability strike that damages but does not kill).
  4. FATAL Rend Weapon (a weapon-ability strike that lands the kill).
  5. A charged-spell kill (a Black Magic / White Magic cast that lands the kill).
  6. A may-cast weapon proc kill (a weapon's on-hit ability fires and lands the kill).
  7. A reaction (Counter / Counter Magic / etc. triggering off an incoming hit).

Hypothesis under test (LIVE_LEDGER's Uncertain AREC row + KillTracker.LogKillDiag's D4 note):
  - ATTACK (beats 1-2): the basic-Attack case stamps NOTHING distinctive on the victim's action
    record in the fatal tick window -- no kind=6, no ability id, no attacker idx appearing that
    was not already there before the strike.
  - ABILITY (beats 3-7): an ability-driven strike stamps kind=6 ("receiving", per Offsets.cs's
    PROBABLE decode) on the victim's record, carrying the ability id (+0x2, u16) and the
    attacker's seat index (+0x0, u8, == attacker's seat - 8 per the AArec doc comment), and that
    stamp is still present (survives) at the hp->0 death edge, not just transiently in an
    earlier tick.

If both sides show the SAME shape (or the "attack" side also stamps kind=6), the discriminator
premise is refuted and LW-167 stage 4 needs a different signal. Bytes +0x4..+0x9 (6 bytes,
printed as raw hex, UNDECODED) are captured every tick alongside the decoded fields because one
of them may turn out to be a freshness/generation counter that would settle the "is this stamp
fresh or a stale echo of an earlier action" question the kind/xref bytes alone cannot answer.

What is watched (source: LivingWeapon/Offsets.cs, cited per field below)
--------------------------------------------------------------------------------
  - Per seat, per tick: the full 12 bytes at bandEntry + Offsets.AArec (Offsets.cs:363,
    AArec = 0x184). Decoded per Offsets.cs:354-362 / KillTracker.cs's LogKillDiag (the only other
    reader of this field, D4 diagnostic-only): +0x0 idx (u8), +0x2 abil (u16), +0x4..+0x9 unk
    (6 raw bytes), +0xA kind (u8, PROBABLE 5=performing/6=receiving), +0xB xref (u8, UNPROVEN
    victim<->attacker cross-reference candidate).
  - Per seat, per tick: HP (u16 at bandEntry + Offsets.AHp, Offsets.cs:101 AHp = 0x14 -- the same
    band-entry-relative field cursor_resolve_probe.py's read_band() reads as A_HP). hp->0 is the
    death edge this probe correlates the AREC stamp against.
  - The engine actor pointer (Offsets.cs:65 ActorPtr = 0x14186AF68): a raw pointer to the acting
    unit's combat frame base. Printed on every value change, decoded to a seat index via
    FrameReadBase (== CombatAnchor - 24*CombatStride, Offsets.cs:197) the same way
    cursor_resolve_probe.py's actor_slot() does.

Band walking reuses cursor_resolve_probe.py's approach verbatim (that probe is the sanctioned
post-1.5 probe base per the repo's conventions; the older ct_probe family is stale): same
COMBAT_ANCHOR/STRIDE/BAND_ENTRY/BAND_READ_BASE/BAND_SLOTS constants, same guarded per-seat
0x200-byte read, same Band.IsValid shape filter (level/brave/faith/maxhp/gx/gy sanity ranges --
NOT a stale slot-marker filter; slot0/slot9 battle-phase markers are not consulted here). A dead
seat (hp==0) still passes IsValid (IsValid never looks at current hp), which is exactly what lets
this probe watch a unit through its own death.

Correlate this tape's timestamps against livingweapon.log's kill-diagnostic lines
(KillTracker.LogKillDiag, "kill-diagnostic: corpse action record index=... ability=... kind=...
cross-reference?=...") by wall-clock ms, the same habit cursor_resolve_probe.py uses for its own
correlation against the Attack-card paint/revert lines.

Usage: python tools\probes\arec_watch.py [--hz N]   (default N=20; Ctrl+C for a clean summary)
"""
import datetime
import sys
import time

sys.path.insert(0, str(__import__("pathlib").Path(__file__).resolve().parent))
from treasure_flags import rpm, _handle

# --- Offsets.cs constants (band-walking core copied from cursor_resolve_probe.py; AArec/AHp
#     citations above). Single source: LivingWeapon/Offsets.cs. ---
ACTOR_PTR = 0x14186AF68          # Offsets.ActorPtr (Offsets.cs:65)
COMBAT_ANCHOR = 0x141855CE0      # Offsets.CombatAnchor (Offsets.cs:189)
STRIDE = 0x200                   # Offsets.CombatStride (Offsets.cs:190)
BAND_ENTRY = 0x1C                # Offsets.BandEntry (Offsets.cs:295)
FRAME_READ_BASE = COMBAT_ANCHOR - 24 * STRIDE     # Offsets.FrameReadBase (Offsets.cs:197)
BAND_READ_BASE = COMBAT_ANCHOR + BAND_ENTRY - 24 * STRIDE   # Offsets.BandReadBase (Offsets.cs:351)
BAND_SLOTS = 49                  # Offsets.BandSlots (Offsets.cs:352)

# Band-entry-relative field offsets (Band.IsValid shape filter, verbatim from
# cursor_resolve_probe.py's read_band()/Band.IsValid; Offsets.cs:90-120).
A_LEVEL, A_BRAVE, A_FAITH = 0x0D, 0x0E, 0x10
A_HP, A_MAXHP, A_GX, A_GY = 0x14, 0x16, 0x33, 0x34   # Offsets.AHp/AMaxHp/AGx/AGy

# The action record this probe exists to watch.
A_AREC = 0x184                   # Offsets.AArec (Offsets.cs:363)
AREC_LEN = 0xC                   # 12 bytes

SEAT_READ_LEN = 0x200             # one guarded read per seat covers both HP and AREC


def u16(b, off):
    return b[off] | (b[off + 1] << 8)


def is_valid(lvl, br, fa, mhp, gx, gy):
    """Band.IsValid, verbatim shape (cursor_resolve_probe.py). Never looks at current HP, so a
    freshly-dead corpse (hp==0) still passes -- deliberate, this probe watches units THROUGH
    death."""
    return (1 <= lvl <= 99 and 1 <= br <= 100 and 1 <= fa <= 100
            and 1 <= mhp < 2000 and gx <= 30 and gy <= 30)


def decode_arec(b):
    """b is the 12 raw bytes at bandEntry + AArec. Returns (idx, abil, unk_hex, kind, xref)."""
    idx = b[0]
    abil = u16(b, 0x2)
    unk = b[0x4:0xA].hex()
    kind = b[0xA]
    xref = b[0xB]
    return idx, abil, unk, kind, xref


def kind_label(kind):
    # Offsets.cs:359 -- PROBABLE, not proven.
    if kind == 5:
        return "5(performing?)"
    if kind == 6:
        return "6(receiving?)"
    return str(kind)


def read_seat(s):
    """One guarded read of a band seat; returns the raw 0x200 buffer or None."""
    addr = BAND_READ_BASE + s * STRIDE
    return rpm(addr, SEAT_READ_LEN)


def actor_slot(ptr):
    if ptr == 0 or ptr < FRAME_READ_BASE:
        return None
    d = ptr - FRAME_READ_BASE
    if d % STRIDE:
        return None
    s = d // STRIDE
    return s if s < BAND_SLOTS else None


def read_actor_ptr():
    b = rpm(ACTOR_PTR, 8)
    if b is None:
        return None
    return int.from_bytes(b, "little")


def main():
    hz = 20.0
    argv = sys.argv[1:]
    i = 0
    while i < len(argv):
        a = argv[i]
        if a == "--hz" and i + 1 < len(argv):
            hz = float(argv[i + 1])
            i += 2
            continue
        if a.startswith("--hz="):
            hz = float(a.split("=", 1)[1])
            i += 1
            continue
        i += 1
    if hz <= 0:
        hz = 20.0
    period = 1.0 / hz

    if not _handle():
        print("game not running")
        sys.exit(1)

    print(f"arec_watch: polling at {hz:g}Hz (read-only). Ctrl+C for a clean summary.")
    print("Run the pre-registered beats one at a time (see the module docstring); correlate "
          "timestamps against livingweapon.log's kill-diagnostic lines.")

    last_hp = {}
    last_arec = {}
    last_valid = {}
    change_counts = {}
    actor_ptr_changes = 0
    last_ptr = None
    last_ptr_seat = None

    def bump(s):
        change_counts[s] = change_counts.get(s, 0) + 1

    try:
        while True:
            now = datetime.datetime.now().strftime("%H:%M:%S.%f")[:-3]

            # --- band seats: HP + action record ---
            for s in range(BAND_SLOTS):
                buf = read_seat(s)
                if buf is None:
                    continue   # guarded read: skip silently when unreadable

                lvl, br, fa = buf[A_LEVEL], buf[A_BRAVE], buf[A_FAITH]
                mhp = u16(buf, A_MAXHP)
                gx, gy = buf[A_GX], buf[A_GY]
                if not is_valid(lvl, br, fa, mhp, gx, gy):
                    last_valid[s] = False
                    continue
                last_valid[s] = True

                hp = u16(buf, A_HP)
                arec = bytes(buf[A_AREC:A_AREC + AREC_LEN])

                if s in last_hp and last_hp[s] != hp:
                    print(f"{now} [hp]   seat={s:02d} hp {last_hp[s]}->{hp}", flush=True)
                    bump(s)
                last_hp[s] = hp

                if s in last_arec and last_arec[s] != arec:
                    oidx, oabil, ounk, okind, oxref = decode_arec(last_arec[s])
                    nidx, nabil, nunk, nkind, nxref = decode_arec(arec)
                    print(f"{now} [arec] seat={s:02d} idx {oidx}->{nidx}  abil {oabil}->{nabil}  "
                          f"kind {kind_label(okind)}->{kind_label(nkind)}  xref {oxref}->{nxref}  "
                          f"unk {ounk}->{nunk}  raw {last_arec[s].hex()}->{arec.hex()}",
                          flush=True)
                    bump(s)
                last_arec[s] = arec

            # --- engine actor pointer ---
            ptr = read_actor_ptr()
            if ptr is not None and ptr != last_ptr:
                new_seat = actor_slot(ptr)
                print(f"{now} [actor] ptr {(hex(last_ptr) if last_ptr is not None else 'none')}"
                      f"->{hex(ptr)}  seat {last_ptr_seat}->{new_seat}", flush=True)
                actor_ptr_changes += 1
                last_ptr = ptr
                last_ptr_seat = new_seat

            time.sleep(period)
    except KeyboardInterrupt:
        pass

    print("\n--- summary ---")
    if not change_counts:
        print("  no changes observed on any seat")
    for s in sorted(change_counts):
        print(f"  seat {s:02d}: {change_counts[s]} change(s)")
    print(f"  actor pointer transitions: {actor_ptr_changes}")


if __name__ == "__main__":
    main()
