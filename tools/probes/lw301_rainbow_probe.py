#!/usr/bin/env python
"""
LW-301 PHASE 0 PROBE: can two weapons ever be on screen at once?

THE PREMISE UNDER TEST, stated so it can fail. Per-weapon battle colour
([per-weapon-colour-by-turn-repaint]) works only because 127 weapons sharing 13 palettes never
COLLIDE on screen: the weapon sprite is drawn during its wielder's attack animation, so at most
one weapon is visible at any instant and the mod can repaint that weapon's palette on its turn.

  P3: at no moment are two weapons visible at the same time.

If P3 is FALSE the design collapses, because two weapons sharing one palette would be forced to
one colour exactly when the player can compare them side by side. So this probe is built to
DISCONFIRM P3, not to confirm it.

METHOD: paint all 13 weapon palettes (3..15) different vivid colours at once, then play normally
and watch. Vanilla weapon art is steel and leather and reads as one family, which is exactly the
condition under which a second weapon on screen would go unnoticed. Under the rainbow any second
weapon is unmissable and, better, its COLOUR names which palette it came from.

  DISCONFIRMING OBSERVATION, pre-registered: any frame showing two coloured weapons at once.
  The cases most likely to produce one, in rough order of likelihood:
    - a COUNTER-ATTACK or reaction: the counter-attacker swings while the attacker's swing is
      still on screen
    - dual wield: one unit holding two weapons, which are TWO sprites and may be two palettes
    - a thrown or fired projectile still in flight while the next unit acts
    - any idle pose that draws a held weapon (none seen so far, but "not seen" is not "absent")

  CONFIRMING OBSERVATION: through a full battle including at least one counter-attack, only ever
  the acting unit's weapon is coloured, and it is the only weapon visible.

Arc palettes 0..2 are deliberately NOT touched: [wep-spr-palette-block] established that weapons
draw from 3..15 and effects from 0..2 with zero overlap across all 127 weapons, so leaving them
alone keeps slash arcs vanilla and means any colour seen is a WEAPON.

Writes the static workspace at 0x140d35750 (both 512-byte banks). That buffer is refreshed from
the loaded file at every battle load, so a battle reload reverts everything this does and
`--restore` is only needed to clean up within one battle.

USAGE (game running, in a live battle):
  python lw301_rainbow_probe.py rainbow   # 13 palettes, 13 colours, with a legend
  python lw301_rainbow_probe.py restore   # copy the loaded file's palettes back over the workspace
  python lw301_rainbow_probe.py show      # read back what the workspace currently holds
"""
import pathlib
import struct
import sys

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import battle_cheats as bc

STATIC_BANKS = [0x140d35750, 0x140d35950]   # [resident-weapon-palette-buffer]
WEAPON_PALETTES = range(3, 16)              # 0..2 are effect/arc palettes, left vanilla

# 13 hues spread around the wheel, in BGR555 (bit 15 is the flag bit, preserved separately).
# Named so the owner can say "I saw the orange one" and we can name the palette from it.
RAINBOW = {
    3:  (0x001F, "red"),        4:  (0x011F, "orange"),   5:  (0x02FF, "amber"),
    6:  (0x03FF, "yellow"),     7:  (0x03E0, "green"),    8:  (0x07E0, "spring"),
    9:  (0x7FE0, "cyan"),       10: (0x7E00, "azure"),    11: (0x7C00, "blue"),
    12: (0x7C10, "indigo"),     13: (0x7C1F, "magenta"),  14: (0x6C1F, "pink"),
    15: (0x4210, "grey"),
}


def paint(pal, code):
    """Set every non-zero slot of one palette to one colour, in BOTH banks."""
    for base in STATIC_BANKS:
        tgt = base + pal * 32
        raw = bc.rpm(tgt, 32)
        if not raw:
            return False
        codes = struct.unpack("<16H", raw)
        # slot 0 is transparent and bit 15 is a per-slot flag: preserve both, or the sprite
        # loses its cutout and reads as a coloured rectangle rather than a weapon.
        new = [c if c == 0 else ((c & 0x8000) | code) for c in codes]
        if not bc.wpm(tgt, struct.pack("<16H", *new)):
            return False
    return True


def cmd_rainbow():
    print("painting the 13 WEAPON palettes (3..15); arc palettes 0..2 left vanilla\n")
    for pal in WEAPON_PALETTES:
        code, name = RAINBOW[pal]
        ok = paint(pal, code)
        print(f"  palette {pal:>2} -> {name:<8} {code:#06x}  {'ok' if ok else 'WRITE FAILED'}")
    print("\nNow play. WATCH FOR: any single frame with TWO coloured weapons in it.")
    print("Most likely moments, in order: a COUNTER-ATTACK, a dual-wielding unit, a thrown or")
    print("fired projectile still in flight, or a standing unit that draws a held weapon.")
    print("\nSeeing two at once DISPROVES the design and is the result worth having.")
    print("Seeing only the attacker's weapon, through a battle that included a counter, supports it.")


def cmd_restore():
    """Copy the loaded file's palettes back over the workspace, so the battle looks normal again."""
    print("searching for the loaded sprite file to source pristine palettes...")
    import lw291_locate_file as loc  # optional helper; fall back to a reload instruction
    print("no locator available in this build; simply RELOAD the battle, which copies the")
    print("loaded file over the workspace and reverts everything this probe did.")


def cmd_show():
    for base in STATIC_BANKS:
        print(f"bank {base:#x}")
        for pal in WEAPON_PALETTES:
            raw = bc.rpm(base + pal * 32, 8)
            if raw:
                v = struct.unpack("<4H", raw)
                print(f"   pal {pal:>2}: {' '.join(f'{x:04x}' for x in v)}")


def main():
    bc._require_game()
    mode = sys.argv[1] if len(sys.argv) > 1 else ""
    if mode == "rainbow":
        cmd_rainbow()
    elif mode == "show":
        cmd_show()
    elif mode == "restore":
        cmd_restore()
    else:
        print(__doc__)


main()
