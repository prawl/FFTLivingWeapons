# The ramp recolour engine (banked 2026-08-16, NOT yet wired into the pipeline)

This folder is the REAL source of the icon art currently deployed to the live install. It is
banked here so the work cannot be lost with a temp folder or a Downloads sweep. It is a PROBE
bank, not a pipeline: nothing in `BuildLinked.ps1` or `Publish.ps1` calls any of it yet. Wiring
it in, and regenerating all 300 textures through the real pipeline, is LW-247.

**Do not run `tools/recolor_icons.py` for a deploy until LW-247 lands.** That file still holds the
engine players rejected; running it would clobber every deployed texture back to the old look.

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
