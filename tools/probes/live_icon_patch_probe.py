"""Live icon-repaint probe: can an equip icon change WITHOUT restarting the game?

WHY THIS EXISTS (the tiered-glow premise, 2026-08-16). The plan "glow intensity = weapon
tier" only works per-save if icons can change while the game runs. All day we assumed
"textures are restart-only". That assumption was never probed -- it was inferred from
deploys not showing up. This probe tests the real mechanism instead.

WHAT WE ESTABLISHED OFFLINE (this is the probe's foundation, all verified 2026-08-16):
  * The modloader MERGES every mod's FFTIVC/data/enhanced/... file into
    <game>/data/enhanced/modded.pac at launch, and the GAME READS THAT PAC, not our loose
    .tex. That is why a mid-session file swap did nothing: we were editing the source of a
    merge that already happened, not the file being read.
  * Inside modded.pac each icon sits UNCOMPRESSED, VERBATIM, EXACTLY ONCE, at the same
    byte length as the .tex on disk (cards 0x C860, smalls 0x3060). Verified by locating
    our deployed bytes with a content search: found once each, no second copy.
  * SAFETY CORRECTION 2026-08-26: the launch merge is INCREMENTAL, not a full rebuild.
    A patch made here SURVIVED a relaunch (modded_files.txt untouched, marker art still
    in the pac in the new session), so "worst case, relaunch restores it" is FALSE.
    A patch persists until the matching loose file changes (a deploy) or you restore.
    ALWAYS run restore explicitly; never rely on a relaunch to clean up.

2026-08-26 VERDICTS (owner-run, ledger rows [live-icon-repaint] and
[icon-refresh-unreliable] carry the evidence): stage 1 (first draw) still shows the
patch; stage 2 (already-drawn icon) refused EVERY eviction tried in a long session AND
a fresh session: tab round-trip, close/reopen equipment, world map, save+load,
title-screen reload, and a full battle enter/exit. The 2026-08-16 stage 2 pass remains
the only time a drawn icon ever refreshed; the condition is unidentified. Treat drawn
icons as restart-only.

THEREFORE a live repaint is physically expressible: overwrite an icon's bytes in place
inside modded.pac. What remains genuinely UNKNOWN, and is the whole point of the probe:
  (a) can the file even be written while the game holds it open, and
  (b) does the engine re-read an icon when a menu draws it (lazy), or does it cache every
      texture at startup (in which case only a restart can ever show a change)?
The sibling FFTColorCustomizer mod live-applies sprite changes for MOST story characters
but not all ("engine cache, not a bug"), which is real evidence that this engine loads
assets lazily and caches after first use -- so the answer to (b) is likely "lazy, with a
cache", and the cache is what the two stages below tease apart.

RUN IT (game RUNNING; ~2 minutes):
  Stage 1 -- cold cache. Launch the game and go to the title screen / world map WITHOUT
  opening any equipment list, then:
      python tools/probes/live_icon_patch_probe.py patch
  Now open a character's equipment and look at TIDEWARD (the first shield).
      Shows EMBERWARD's red art -> LIVE REPAINT WORKS on a cold cache.
      Shows its own blue art    -> either the write failed (the probe says so) or icons
                                   are loaded before the menu opens.

  Stage 2 -- warm cache (only if stage 1 worked). Close the equipment list, then:
      python tools/probes/live_icon_patch_probe.py patch --target 129
  Reopen the equipment list and look at GALEWALL (second shield).
      Changed  -> the engine re-reads on every menu open. Full per-save tier glow is
                  buildable with no restart at all.
      Unchanged -> icons cache on first draw. Tier changes would then show at the next
                  menu-scene load or next launch: still buildable, just with that caveat.

  Then put it back (any time, game running or not):
      python tools/probes/live_icon_patch_probe.py restore

The state file records exact offsets so restore is byte-exact rather than another search.
"""
import hashlib, json, os, subprocess, sys

GAME = (r"c:\program files (x86)\steam\steamapps\common"
        r"\FINAL FANTASY TACTICS - The Ivalice Chronicles")
PAC = os.path.join(GAME, "data", "enhanced", "modded.pac")
INSTALL_ICON = os.path.join(GAME, "Reloaded", "Mods", "prawl.fft.livingweapons",
                            "FFTIVC", "data", "enhanced", "ui", "ffto", "icon")
STATE = os.path.join(os.environ.get("TEMP", "."), "live_icon_patch_probe_state.json")
MARKER_SRC = 135          # Emberward: red, unmistakable against any blue/other shield
DEFAULT_TARGET = 128      # Tideward


def tex_path(icon_id, surface):
    sub = "equip_item" if surface == "card" else "equip_item_s"
    stem = f"ei_{icon_id:03d}_uitx" if surface == "card" else f"ei_s_{icon_id:03d}_uitx"
    return os.path.join(INSTALL_ICON, sub, "texture", f"{stem}.tex")


def game_running():
    # CASE-INSENSITIVE ON PURPOSE: the running image is "FFT_enhanced.exe" while every
    # path and doc writes "fft_enhanced.exe". A case-sensitive match here silently reports
    # "not running" for a running game, which makes any guard built on it vacuous.
    out = subprocess.run(["tasklist", "/FI", "IMAGENAME eq fft_enhanced.exe"],
                         capture_output=True, text=True).stdout
    return "fft_enhanced.exe" in out.lower()


def find_once(pac_bytes, needle, label):
    at = pac_bytes.find(needle)
    assert at >= 0, (f"{label}: not found in modded.pac. The pac is stale relative to the "
                     f"deployed .tex -- launch the game once so the modloader re-merges, "
                     f"then re-run.")
    assert pac_bytes.find(needle, at + 1) < 0, f"{label}: found more than once; refusing to guess"
    return at


def patch(target):
    if not game_running():
        print("NOTE: the game is not running. The patch will still be written, but the "
              "point of this probe is to watch a RUNNING game change.", flush=True)
    pac = open(PAC, "rb").read()
    state = {"target": target, "writes": []}
    for surface in ("small", "card"):
        own = open(tex_path(target, surface), "rb").read()
        marker = open(tex_path(MARKER_SRC, surface), "rb").read()
        assert len(own) == len(marker), "size mismatch between target and marker art"
        off = find_once(pac, own, f"item {target} {surface}")
        state["writes"].append({"surface": surface, "offset": off, "size": len(own),
                                "own_md5": hashlib.md5(own).hexdigest(),
                                "marker_md5": hashlib.md5(marker).hexdigest()})
        try:
            with open(PAC, "r+b") as f:
                f.seek(off)
                f.write(marker)
                f.flush()
                os.fsync(f.fileno())
        except PermissionError as e:
            print(f"WRITE BLOCKED at {off:#x} ({surface}): {e}\n"
                  f"  -> the game holds modded.pac with an exclusive handle. That answers "
                  f"question (a): an in-place live patch is impossible while running, and "
                  f"the tier glow would have to be selected at launch instead.")
            return
        with open(PAC, "rb") as f:
            f.seek(off)
            assert f.read(len(marker)) == marker, f"verify failed at {off:#x}"
        print(f"patched {surface:5s} at {off:#x} ({len(marker)} bytes) -- "
              f"item {target} now carries item {MARKER_SRC}'s art inside modded.pac")
    json.dump(state, open(STATE, "w"), indent=1)
    print(f"\nstate saved to {STATE}\n"
          f"NOW: open a character's equipment list in game and look at item {target}. "
          f"If it shows the RED shield, the engine re-read the pac live.")


def restore():
    if not os.path.exists(STATE):
        print("nothing to restore (no state file). WARNING: a relaunch does NOT rebuild "
              "modded.pac (the merge is incremental, proven 2026-08-26), so if a patch was "
              "made without state, restore by hand: content-search the marker bytes and "
              "write the target's own .tex bytes back over them.")
        return
    state = json.load(open(STATE))
    target = state["target"]
    for w in state["writes"]:
        own = open(tex_path(target, w["surface"]), "rb").read()
        assert hashlib.md5(own).hexdigest() == w["own_md5"], \
            "the deployed .tex changed since the patch; relaunch instead (a changed source " \
            "file is what triggers the incremental re-merge; VERIFY the pac afterwards)"
        with open(PAC, "r+b") as f:
            f.seek(w["offset"])
            f.write(own)
            f.flush()
            os.fsync(f.fileno())
        with open(PAC, "rb") as f:
            f.seek(w["offset"])
            assert f.read(len(own)) == own, "restore verify failed"
        print(f"restored {w['surface']:5s} at {w['offset']:#x}")
    os.remove(STATE)
    print(f"item {target} is back to its own art in modded.pac (verified)")


if __name__ == "__main__":
    mode = sys.argv[1] if len(sys.argv) > 1 else ""
    tgt = DEFAULT_TARGET
    if "--target" in sys.argv:
        tgt = int(sys.argv[sys.argv.index("--target") + 1])
    if mode == "patch":
        patch(tgt)
    elif mode == "restore":
        restore()
    else:
        print(__doc__)
        sys.exit(2)
