#!/usr/bin/env python
r"""DEV HARNESS CHEAT. Stages a glow ring tier change on about thirty weapons (LW-445).

Writes the install's kills.json with the game CLOSED so the next launch has to repaint about
thirty icons into the game's merged art file (modded.pac). That many mirror writes in one pass
is what makes a momentary file lock likely, which is the thing the LW-445 retry exists for.
After the launch, open and close the inventory quickly for twenty seconds and read the log for
a "retrying on a later pass" line followed by an "on retry" line for the same icon (the smoke
pass and docs/TODO.md LW-445 hold the PASS and FAIL signatures).

  python tools\probes\lw445_tally_stage.py                  # dry run: prints the plan, writes nothing
  python tools\probes\lw445_tally_stage.py --write          # 30 weapons to 5 kills (tier 1 on prod)
  python tools\probes\lw445_tally_stage.py --write --count 10 --weapons 40
  python tools\probes\lw445_tally_stage.py --restore <file> # put a saved copy back over kills.json

Before writing, the current kills.json is copied to %TEMP%\kills.lw445.before_<n>.json and the
restore command is printed; --restore saves the file it replaces the same way. Refuses while the
game runs (the runtime reads the file once at launch and rewrites it whenever a kill lands, so an
edit under a live game is lost). If the primary file is unreadable the merge base is its .bak,
the same fallback the runtime takes; rows whose value is not a whole number are skipped like the
runtime skips them. Prod thresholds only: a dev build floors every tally to 3 on load and the
staged change vanishes.
"""
import argparse
import json
import os
import pathlib
import shutil
import subprocess
import sys

MOD_ID = "prawl.fft.livingweapons"
PROD_THRESHOLDS = (5, 10, 15)  # LivingWeapon/Tuning.cs ProdThresholds (LW-161)


def tier_for(kills):
    t1, t2, t3 = PROD_THRESHOLDS
    return 3 if kills >= t3 else 2 if kills >= t2 else 1 if kills >= t1 else 0


def game_running():
    out = subprocess.run(["tasklist"], capture_output=True, text=True).stdout.lower()
    return "fft_enhanced.exe" in out


def save_dir():
    mods = os.environ.get("RELOADEDIIMODS")
    if not mods:
        sys.exit("REFUSED: RELOADEDIIMODS is not set")
    # RELOADEDIIMODS is <root>\Reloaded\Mods; the save folder is <root>\Reloaded\User\Mods\<id>.
    return pathlib.Path(mods).parent / "User" / "Mods" / MOD_ID


def weapon_ids():
    here = pathlib.Path(__file__).resolve().parent
    meta = json.loads((here.parent.parent / "LivingWeapon" / "meta.json").read_text(encoding="utf8"))
    return sorted(int(k) for k, v in meta.items() if isinstance(v, dict) and "lane" in v)


def parse_tally(path):
    if not path.exists():
        return None, "absent"
    try:
        data = json.loads(path.read_text(encoding="utf8"))
    except ValueError:
        return None, "unparsable"
    if not isinstance(data, dict):
        return None, "not a JSON object"
    kept = {str(k): v for k, v in data.items() if isinstance(v, int) and not isinstance(v, bool)}
    skipped = len(data) - len(kept)
    return kept, "loaded" + (f", {skipped} row(s) without a whole number skipped" if skipped else "")


def load_tally(path):
    tally, state = parse_tally(path)
    if tally is not None:
        return tally, state
    backup = path.with_name(path.name + ".bak")
    from_bak, bak_state = parse_tally(backup)
    if from_bak is not None:
        return from_bak, f"{state}, merge base taken from the .bak ({bak_state})"
    return {}, f"{state}, .bak {bak_state}; treated as empty"


def next_free(template):
    n = 1
    while template(n).exists():
        n += 1
    return template(n)


def save_aside(kills_path):
    temp = pathlib.Path(os.environ["TEMP"])
    backup = next_free(lambda n: temp / f"kills.lw445.before_{n}.json")
    shutil.copyfile(kills_path, backup)
    return backup


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--write", action="store_true", help="write kills.json (default is a dry run)")
    ap.add_argument("--count", type=int, default=5, help="kills to set on each chosen weapon (default 5)")
    ap.add_argument("--weapons", type=int, default=30, help="how many weapons to change (default 30)")
    ap.add_argument("--restore", metavar="FILE", help="copy FILE back over kills.json and exit")
    args = ap.parse_args()

    if game_running():
        sys.exit("REFUSED: fft_enhanced.exe is running; quit the game first")
    kills_path = save_dir() / "kills.json"

    if args.restore:
        src = pathlib.Path(args.restore)
        if not src.exists():
            sys.exit(f"REFUSED: {src} does not exist")
        if parse_tally(src)[0] is None:
            sys.exit(f"REFUSED: {src} is not a JSON object of counts")
        if kills_path.exists():
            kept = save_aside(kills_path)
            print(f"saved the file being replaced to {kept}")
        shutil.copyfile(src, kills_path)
        print(f"restored {kills_path} from {src}")
        return

    ids = weapon_ids()
    current, state = load_tally(kills_path)
    step = max(1, len(ids) // max(1, args.weapons))
    chosen = ids[::step][: args.weapons]
    changes = 0
    print(f"kills.json: {kills_path} ({state}, {len(current)} weapons on file)")
    print(f"plan: {len(chosen)} of {len(ids)} living weapons to {args.count} kills (tier {tier_for(args.count)})")
    for wid in chosen:
        old = current.get(str(wid), 0)
        moved = tier_for(old) != tier_for(args.count)
        changes += moved
        print(f"  id {wid:>3}: {old:>3} -> {args.count:>3}  tier {tier_for(old)} -> {tier_for(args.count)}"
              f"{'' if moved else '  (no tier change, no repaint)'}")
    print(f"{changes} icon(s) change tier, each is one card and one list surface to mirror")

    if not args.write:
        print("dry run; add --write to stage it")
        return
    if kills_path.exists():
        backup = save_aside(kills_path)
        print(f"saved the current file to {backup}")
        print(f"restore: python tools\\probes\\lw445_tally_stage.py --restore \"{backup}\"")
    merged = dict(current)
    for wid in chosen:
        merged[str(wid)] = args.count
    kills_path.parent.mkdir(parents=True, exist_ok=True)
    kills_path.write_text(json.dumps(merged, separators=(",", ":")), encoding="utf8")
    print(f"wrote {kills_path}; launch the game, then open and close the inventory for twenty seconds")


if __name__ == "__main__":
    main()
