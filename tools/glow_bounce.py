#!/usr/bin/env python
"""DEV HARNESS CHEAT -- writes the live install's kills.json. Owner test loop only.

Set EVERY living weapon's kill tally to one value and re-apply the glow overlay, so a
game relaunch shows the whole arsenal at one chosen tier (docs/DEV_TEST_RECIPES.md,
"Glow tier bounce"). The loop: run this, launch, look, quit, run with the next value.

  python tools/glow_bounce.py 0     # tier 0: every icon plain
  python tools/glow_bounce.py 7     # tier 1: faint rims     (prod thresholds {5,10,15},
  python tools/glow_bounce.py 12    # tier 2: full rims       LivingWeapon/Tuning.cs)
  python tools/glow_bounce.py 15    # tier 3: max pop

Also sets what the cards READ ("Kills: 7") and what growth/signatures arm at next
launch, which is exactly what a tier tour wants. Refuses while the game runs.
NEVER touches any kills.json.*.bak (the real-tally backups, e.g. glowtest.bak, stay
untouched; restore one by hand when the tour is over).
"""
import json
import pathlib
import subprocess
import sys

HERE = pathlib.Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
from deploy_glow_tex import MOD_ID, game_running, tier_for  # noqa: E402


def main():
    if len(sys.argv) != 2 or not sys.argv[1].isdigit():
        sys.exit(__doc__)
    kills = int(sys.argv[1])
    if game_running():
        sys.exit("REFUSED: fft_enhanced.exe is running; quit the game first")
    import os
    mods = os.environ.get("RELOADEDIIMODS")
    if not mods:
        sys.exit("REFUSED: RELOADEDIIMODS is not set")
    # RELOADEDIIMODS is <root>\Reloaded\Mods, so the Reloaded root is ONE parent up
    # (the adversarial review caught the original two-parent form resolving outside
    # the Reloaded tree entirely; deploy_glow_tex.py computes the same root from
    # mod_dir.parent.parent, which is equivalent to this).
    save_dir = pathlib.Path(mods).parent / "User" / "Mods" / MOD_ID
    kills_path = save_dir / "kills.json"
    meta = json.loads((HERE.parent / "LivingWeapon" / "meta.json").read_text(encoding="utf8"))
    weapon_ids = sorted(int(k) for k, v in meta.items() if isinstance(v, dict) and "lane" in v)
    kills_path.write_text(json.dumps({str(i): kills for i in weapon_ids},
                                     separators=(",", ":")), encoding="utf8")
    print(f"kills.json: all {len(weapon_ids)} living weapons set to {kills} "
          f"(tier {tier_for(kills)})")
    r = subprocess.run([sys.executable, str(HERE / "deploy_glow_tex.py"), "apply"])
    sys.exit(r.returncode)


if __name__ == "__main__":
    main()
