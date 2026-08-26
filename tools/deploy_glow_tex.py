#!/usr/bin/env python
"""DEPLOY STEP -- writes into the LIVE Reloaded Mods folder. Not a pipeline script.

LW-334 interim display path (launch-time tier selection). The runtime's pac splice
provably lands its bytes but the game never draws them (icon textures cache at first
draw / the pac is read before the background splice finishes indexing). Until that
display path is cracked, this step bakes the glow INTO the deployed loose .tex files
AFTER BuildLinked runs: the modloader merges loose files into modded.pac at launch,
which is the channel every shipped icon already provably rides. Cost: a weapon's rim
updates on game RESTART instead of live; the splice stays deployed and harmless.

For every icon in the DEPLOYED glow_icons/manifest.json: tier = the weapon's current
kills.json tally against the PRODUCTION thresholds {5,10,15} (LivingWeapon/Tuning.cs,
LW-161 curve; this script targets prod-flavored installs and refuses dev flavor).
Tier 0 keeps the plain base; tier 1..3 copies the baked variant over the deployed
base tex (same fixed byte length, guarded).

Run AFTER BuildLinked (each deploy restores plain bases, so re-run this after every
deploy). Refuses to run while the game is up (the merge reads these files at launch;
mid-session writes only feed the broken splice path anyway).

  python tools/deploy_glow_tex.py apply
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
    if len(sys.argv) < 2 or sys.argv[1] != "apply":
        sys.exit(__doc__)
    if game_running():
        sys.exit("REFUSED: fft_enhanced.exe is running; kill the game first")
    mods = os.environ.get("RELOADEDIIMODS")
    if not mods:
        sys.exit("REFUSED: RELOADEDIIMODS is not set")
    mod_dir = pathlib.Path(mods) / MOD_ID
    flavor = (mod_dir / "build_flavor.txt").read_text(encoding="utf8").strip()
    if flavor != "prod":
        sys.exit(f"REFUSED: deployed flavor is {flavor!r}, not prod (dev builds seed "
                 "tallies; this step keys tiers off REAL kills)")
    root = mod_dir.parent.parent
    kills = json.loads((root / "User" / "Mods" / MOD_ID / "kills.json")
                       .read_text(encoding="utf8"))
    manifest = json.loads((mod_dir / "glow_icons" / "manifest.json")
                          .read_text(encoding="utf8"))
    applied = skipped = 0
    for entry in manifest["icons"]:
        tier = tier_for(int(kills.get(str(entry["id"]), 0)))
        if tier == 0:
            skipped += 1
            continue
        src = mod_dir / "glow_icons" / entry["variants"][str(tier)]
        dst = mod_dir / "FFTIVC" / entry["baseRel"]
        data = src.read_bytes()
        if len(data) != entry["length"]:
            sys.exit(f"REFUSED at {src.name}: {len(data)} bytes != manifest length "
                     f"{entry['length']} (never write a wrong-size tex)")
        dst.write_bytes(data)
        applied += 1
    print(f"glow tex applied over {applied} deployed icons ({skipped} at tier 0 kept "
          f"plain); restart the game to see them")


if __name__ == "__main__":
    main()
