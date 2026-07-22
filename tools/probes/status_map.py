"""THE STATUS MAP (LW-119): all 40 per-unit status ids with their evidence tier and hazards.

Extracted 2026-07-22 from this repo's own sources (Offsets.cs, LIVE_LEDGER.md, MECHANICS.md,
the shipped modules that read/write these bytes, and the existing probes) rather than from
recall. Consumed by battle_toolbag.py's `status` verb. Kept as data, separate from any verb,
so the provenance and the hazards travel with the numbers.

FRAMING (get this wrong and every offset is off by 0x1C): every offset here is BAND-ENTRY
relative. Band entry = combat base + 0x1C. Cross-checked independently: the animation decode
names "combat statuses +0x61..+0x65, Critical +0x63 bit 0x01", which is exactly band
+0x45..+0x49 with Critical at band +0x47 bit 0x01.

THE THREE LAYERS (LIVE_LEDGER's LW-58 row, Uncertain, 2026-07-09):
  innate    band +0x3B..+0x3F   job/equipment derived; this repo neither reads nor writes it
  inflicted band +0x1D3..+0x1D7 the persistent layer the engine ORs accepted bits into
  composed  band +0x45..+0x49   the displayed layer, re-derived from inflicted OR innate
Write BOTH inflicted and composed, and re-assert on a loop for anything the engine fights over.
Composed-only is the "wasted write to a rebuilt block" mistake, the same class as the animation
OUTPUT block that cost the anim decode three months.

UNRESOLVED TENSION, stated rather than smoothed over: "composed is rebuilt every frame" is an
Uncertain row, while the poison hold (composed-only re-OR at 33ms, beating healer cures) is a
Proven one. Both cannot be literally true at 60Hz. Practical reading: the compose pass is not a
per-render-frame wipe, so a fast hold on composed does stick. Writing both layers is the safe
posture, not a nicety.

CLOSED DOOR: the pending-ADD field at band +0x1BF is consumed but IGNORED for external writes
(three live tapes). The apply-engine cold call 0x150BF66DC is in-process only, so a python probe
cannot use it. Bit math: byte = id >> 3, mask = 0x80 >> (id & 7).

TIERS: S = shipped production code writes it AND it was live-verified; P = Proven ledger row;
U = Uncertain/observed; O = map-only bit since OBSERVED working live in this game; M = map-only (the ported decode table plus id arithmetic that checks out
against 13 independently anchored bits, but this specific bit has never been set or cleared and
watched in this game). M is not a guess, but it is not evidence either.
"""

COMPOSED_BASE = 0x45     # band-relative, 5 bytes
INFLICTED_BASE = 0x1D3   # band-relative, 5 bytes
INNATE_BASE = 0x3B       # band-relative, 5 bytes; documented, never written by this repo

POISON_TIMER = 0x4A      # band-relative u8, engine inits 36 (0x24), ticks per CT unit (Proven)
DOOM_COUNTDOWN = 0x59    # band-relative u8, engine inits 3, decrements per victim turn (Proven)

# DO NOT extrapolate a timer table: +0x4A (id 24) and +0x59 (id 39) both fit
# timer = 0x32 + id, which is seductive and wrong-looking, because that span runs straight
# through the innate layer at +0x3B..+0x3F. Two points, one collision: unknown until probed.

# name: (id, tier, hazard or None). Hazard text is printed before any write.
STATUSES = {
    # ---- band +0x45, ids 0-7 ----
    "crystal":    (1,  "U", "PERMANENT UNIT LOSS: crystallization removes the unit for good."),
    "dead":       (2,  "S", "CRASHED the engine when set far from the unit's own turn; set mid-turn "
                            "it left a stuck dead-but-active turn. Only 'up next' was ever safe. "
                            "Killing the last standing ally wipes the party."),
    "undead":     (3,  "S", "Shipped code READS this and never writes it; write side unexercised."),
    "charging":   (4,  "P", "Engine-owned action state, not a buff; writing it desyncs the action."),
    "jump":       (5,  "P", "Engine-owned action state (set at jump commit, cleared at landing)."),
    "defending":  (6,  "M", None),
    "performing": (7,  "M", None),
    # ---- band +0x46, ids 8-15 ----
    "petrify":    (8,  "M", None),
    "invite":     (9,  "M", "The apply engine special-cases this id to merge team-colour bits into "
                            "combat +0x05/+0x1EE; allegiance flips are separately WALLED."),
    "blind":      (10, "M", None),
    "confuse":    (11, "M", None),
    "silence":    (12, "M", None),
    "vampire":    (13, "M", None),
    "cursed":     (14, "M", None),
    "treasure":   (15, "U", "CRASHES THE GAME: the bit alone is a corrupt half-state (no chest model, "
                            "hearts lost, status card bounced to another unit) and an enemy pathing "
                            "onto the tile crashed the game outright."),
    # ---- band +0x47, ids 16-23 ----
    "oil":        (16, "M", None),
    "float":      (17, "U", "COSMETIC ONLY, and 2026-07-22 explained WHY: floating comes from a "
                            "job's innate Levitate ability and/or the render node Z, not this "
                            "bit. Clearing it on a Bomb removed the icon and left the unit "
                            "airborne; grounding a flier needs the job ability set."),
    "reraise":    (18, "S", None),
    "invisible":  (19, "S", "Breaks the instant the unit acts, so it needs re-stamping every tick "
                            "(--hold). AoE splash still reaches the unit."),
    "berserk":    (20, "O", "OBSERVED WORKING 2026-07-22 (owner live, the first map-only bit promoted): "
                            "the unit charges mindlessly at the nearest target and uses no abilities. "
                            "IT DOES NOT CAUSE FRIENDLY FIRE: targeting still respects sides, so a "
                            "berserk enemy still only attacks the player. Prior note, now answered: a "
                            "write spike existed with no recorded result."),
    "chicken":    (21, "M", None),
    "frog":       (22, "O", "TESTED 2026-07-22 (owner live) and it splits the difference: a raw bit "
                            "write gives the ICON and the status-menu representation (the menu shows a "
                            "frog) while the BATTLE MAP MODEL stays unchanged. So the bit is the flag "
                            "and the model rebuild is separate work the engine's apply routine does. "
                            "Corrects the earlier note, which called frog a real model-rebuild event "
                            "without distinguishing the game applying it from us setting the bit. This "
                            "is the concrete case FOR the apply-engine cold call 0x150BF66DC: same "
                            "shape as everything else here, drive the engine rather than write data."),
    "critical":   (23, "M", None),
    # ---- band +0x48, ids 24-31 ----
    "poison":     (24, "S", "Rides a timer at band +0x4A (engine inits 36). A cure clears the bit, so "
                            "ON wants --hold."),
    "regen":      (25, "U", None),
    "protect":    (26, "M", "Wired in Larceny but recorded as 'under test', never confirmed."),
    "shell":      (27, "M", "Wired in Larceny but recorded as 'under test', never confirmed."),
    "haste":      (28, "P", None),
    "slow":       (29, "M", None),
    "stop":       (30, "M", None),
    "wall":       (31, "M", None),
    # ---- band +0x49, ids 32-39 ----
    "faithstatus":(32, "M", "The status, not the Faith stat (that is band +0x10 orig)."),
    "innocent":   (33, "M", None),
    "charm":      (34, "P", "CONTESTED COMPANION BYTE: the status bit at +0x49/0x20 is confirmed, but "
                            "sources disagree whether the allegiance twin is band +0x54 or +0x38, and "
                            "a third calls +0x38 a node-pool index. Write the STATUS BIT ONLY."),
    "sleep":      (35, "M", None),
    "dontmove":   (36, "M", None),
    "dontact":    (37, "M", None),
    "reflect":    (38, "M", "Wired in Larceny but recorded as 'under test', never confirmed."),
    "doom":       (39, "S", "Rides a countdown at band +0x59 (engine inits 3). Undead no-ops its expiry."),
}

# Ids the probe must refuse outright: the harm is unit loss or a hard crash, and neither is
# something a design conversation needs badly enough to risk a save.
REFUSE = {"crystal", "treasure"}

# Ids that need an explicit opt-in flag: real but engine-owned, or destructive in the ordinary
# course of play rather than catastrophic.
CONFIRM = {"dead", "charging", "jump", "invite", "charm", "frog"}


def bit_of(status_id):
    """(byte index within the 5-byte layer, mask). byte = id >> 3, mask = 0x80 >> (id & 7)."""
    return status_id >> 3, 0x80 >> (status_id & 7)


def lookup(name):
    key = name.lower()
    if key not in STATUSES:
        return None
    sid, tier, hazard = STATUSES[key]
    byte, mask = bit_of(sid)
    return {"name": key, "id": sid, "tier": tier, "hazard": hazard, "byte": byte, "mask": mask,
            "composed": COMPOSED_BASE + byte, "inflicted": INFLICTED_BASE + byte}


if __name__ == "__main__":
    print(f"{'status':<13}{'id':>4}{'tier':>6}{'composed':>10}{'inflicted':>11}{'mask':>6}  gate")
    for name in STATUSES:
        e = lookup(name)
        gate = "REFUSED" if name in REFUSE else ("--yes" if name in CONFIRM else "")
        print(f"{e['name']:<13}{e['id']:>4}{e['tier']:>6}{'band+' + hex(e['composed']):>10}"
              f"{'band+' + hex(e['inflicted']):>11}{hex(e['mask']):>6}  {gate}")
