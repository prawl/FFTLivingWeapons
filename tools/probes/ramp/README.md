# The ramp recolour engine (banked 2026-08-16, PORTED by LW-247 2026-08-18)

This folder is the ORIGINAL source of the icon art deployed to the live install on 2026-08-16.
It is banked here for provenance so the work cannot be lost with a temp folder or a Downloads
sweep. LW-247 (2026-08-18) ported this engine into `tools/recolor_icons.py` (function names
prefixed `ramp_`/`_ramp_`) and wired it into the real pipeline: `python tools/recolor_icons.py`
now rebakes the mod tree to the SAME 468/468 bytes as the live install, arc-gate verified twice
(the double run catches any hidden read of the mod tree, the failure class LW-247's B1 fix
closed). This folder is now READ-ONLY history: nothing in `BuildLinked.ps1`, `Publish.ps1`, or
`tools/recolor_icons.py` imports or runs anything in this folder any more.

**Re-running the census probes (`tools/probes/lw247_repro_census.py` / `lw247_repro_census2.py`)
requires the PRE-PORT revision** of this file: they import `ramp_engine.py` and self-parse
shield tints out of `tools/recolor_icons.py`'s old `ICON_TINTS` table, which commit 3 of LW-247
deleted (those tints now live in `data/items.json`). Check out the repo as of LW-247's commit 2
(the port, not yet routed) to re-run them; they will not import cleanly against the current
`tools/recolor_icons.py`.

## What each file is

- `ramp_engine.py` the engine itself, the newest state (supersedes
  `tools/probes/ramp_engine_prototype.py`, which is the stale midday version kept for history).
  It never invents colour: it keeps the artist's brightness on every pixel and moves only which
  colour family each material wears.
- `weapon_assignments.json` per weapon: tint, provenance, and the review verdict that chose it.
- `weapon_treatments.json` per weapon: punch / rotate / forceb / swapped plus the final delta-E.
- `reserved_weapons.json` the 37 vanilla-name weapons that keep the artist's own colours.
- `weapon_manifest.json` per category strips, ids and names (drives the review page).
- `wow_render.py` renders and bakes the whole weapon catalogue from those tables.
- `deploy_shields.py` bakes and deploys shields and helms.
- `shield_flashy2.py` the glow rounds: reconstructs the deployed BODY from the installed art and
  re-rims it. This is the file that proves bodies and rims are independent layers.
- `bag_v2.py` the bag rounds, including the regression proof that the v2 knobs are pure additions.
- `build_review_page.py` builds the owner review page.
- `escalate_weapons.py` the distinctness ladder (historical; its rules are baked into the tables).
- `rollback.py` restores the shields to the pre-glow deploy.

## Two external dependencies that are NOT in this repo

1. **The vanilla texture cache**, `%TEMP%\vanilla_cache` (466 DDS files). Every render reads it.
   It is in the OS temp directory, so treat it as disposable and re-extractable, not as a source.
2. **FF16Tools CLI**, at `C:\Users\ptyRa\Downloads\FF16Tools.CLI-1.13.2-win-x64\win-x64\`.
   Used to convert PNG to the game's `.tex`.

## The rounds themselves

Every baked round (the deployed textures, the rollback copies, and each rejected iteration) is in
`C:\Users\ptyRa\Downloads\fft_ref\`, under `session_bank_2026-08-16\`,
`shield_flashy_2026-08-16\` and `bag_round_2026-08-16\`. Those bytes are NOT tracked here: the
engine plus these tables reproduces them exactly, which was verified bit-for-bit during the
session. Getting baked output into the repo properly is part of LW-247.
