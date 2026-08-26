#!/usr/bin/env python
"""DEV HARNESS CHEAT -- writes the live install's kills.json. Owner test loop only.

Set EVERY living weapon's kill tally to one value (docs/DEV_TEST_RECIPES.md, "Glow tier
bounce"). This script ONLY writes kills.json -- LW-336 retired the manual glow-tex deploy
step it used to shell out to. The RUNTIME re-tiers every deployed icon file itself, out
of battle, a few seconds after a save loads; but equip-icon textures cache at first draw
for the whole game process, so a file the runtime rewrites mid-session never shows THAT
session. The rhythm is TWO launches: launch once so the runtime re-tiers the files
(tier 0 included, restored from its plain-base snapshot), quit, then launch again to
actually SEE the change.

  python tools/glow_bounce.py 0     # tier 0: every icon plain (after the SECOND launch)
  python tools/glow_bounce.py 7     # tier 1: faint rims     (prod thresholds {5,10,15},
  python tools/glow_bounce.py 12    # tier 2: full rims       LivingWeapon/Tuning.cs)
  python tools/glow_bounce.py 15    # tier 3: max pop

Also sets what the cards READ ("Kills: 7") and what growth/signatures arm at next
launch, which is exactly what a tier tour wants. Refuses while the game runs.
NEVER touches any kills.json.*.bak (the real-tally backups, e.g. glowtest.bak, stay
untouched; restore one by hand when the tour is over).
"""
import json
import os
import pathlib
import subprocess
import sys

MOD_ID = "prawl.fft.livingweapons"
PROD_THRESHOLDS = (5, 10, 15)  # Tuning.cs production KillThresholds (LW-161)


def tier_for(kills):
    t1, t2, t3 = PROD_THRESHOLDS
    return 3 if kills >= t3 else 2 if kills >= t2 else 1 if kills >= t1 else 0


def game_running():
    out = subprocess.run(["tasklist"], capture_output=True, text=True).stdout.lower()
    return "fft_enhanced.exe" in out  # .lower() first: the image is FFT_enhanced.exe


def main():
    if len(sys.argv) != 2 or not sys.argv[1].isdigit():
        sys.exit(__doc__)
    kills = int(sys.argv[1])
    if game_running():
        sys.exit("REFUSED: fft_enhanced.exe is running; quit the game first")
    mods = os.environ.get("RELOADEDIIMODS")
    if not mods:
        sys.exit("REFUSED: RELOADEDIIMODS is not set")
    # RELOADEDIIMODS is <root>\Reloaded\Mods, so the Reloaded root is ONE parent up.
    save_dir = pathlib.Path(mods).parent / "User" / "Mods" / MOD_ID
    kills_path = save_dir / "kills.json"
    here = pathlib.Path(__file__).resolve().parent
    meta = json.loads((here.parent / "LivingWeapon" / "meta.json").read_text(encoding="utf8"))
    weapon_ids = sorted(int(k) for k, v in meta.items() if isinstance(v, dict) and "lane" in v)
    kills_path.write_text(json.dumps({str(i): kills for i in weapon_ids},
                                     separators=(",", ":")), encoding="utf8")
    print(f"kills.json: all {len(weapon_ids)} living weapons set to {kills} "
          f"(tier {tier_for(kills)}) -- launch the game to see it")


if __name__ == "__main__":
    main()
