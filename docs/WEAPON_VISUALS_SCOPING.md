# In-battle weapon visuals — scoping & test results (TABLED)

**Date:** 2026-06-01 · **Status:** TABLED. Ask B is BLOCKED (tested in-game). Ask A is UNTESTED (deferred).

Goal: make the weapon a unit holds/swings in battle match the mod's recolored menu icons —
**(A) shape/size** (players report the two-handed Knight Swords / "greatswords" render oversized
and funky) and **(B) color** (recolor the battle weapon to match the recolored icon).

## Key finding
The in-battle weapon is the **classic 2D FFT weapon sprite, not a 3D model.** Assets live in
pac `0002` under `fftpack/unit/`: `battle_wep_spr.bin` (256-color sprite atlas, **FFTPack file 71**)
+ `battle_wep1/2_shp.bin` (frame geometry) + `_seq.bin` (animation). The HD layer upscales the 2D
graphic. No scale field and no 3D mesh in the item tables.

## Ask A — size / shape (UNTESTED, deferred)
- **Lever (identified, not yet tested):** per-item `<SpriteID>` byte in `ItemData`. Normal Swords
  use SpriteID **12–21**; Knight Swords use **22–25** (the oversized greatsword art).
- **The fix would be:** swap the 5 Knight Swords (item Id 33–37) SpriteID to a 12–21 value so they
  render at normal-sword size. You can't truly *scale* the existing art (that's SHP-frame-geometry
  reverse-engineering — multi-day, shelved); you swap the graphic.
- ⚠ **Confidence lowered by the Ask-B results:** `<SpriteID>` may be nex-overridden like
  `<Palette>` was, and/or the HD model may come from a separate asset SpriteID doesn't drive.
  Promising but **unproven**. Also waiting on a player clarification of the exact request.
- Cheap test if revisited: set Excalibur (Id 35) `<SpriteID>` 24→14, deploy, look at one battle.

## Ask B — color (TESTED → BLOCKED)
Two levers, both failed in-game:
1. **Pure-data `<Palette>` byte (ItemData):** Materia Blade palette 1→15, then 1→3; Vagabond 0→13.
   **No change in battle.** → `<Palette>` is **nex-overridden** by the base nex 'Item' table,
   exactly like the item `<Price>` field. DEAD.
2. **Asset — edit the bin palette:** `battle_wep_spr.bin` first 512 bytes = 16 palettes × 16 BGR555
   colors (the *exact* format/channel FFTColorCustomizer uses to recolor chocobos). Magenta-filled
   palettes 0 & 1, deployed the bin at `FFTIVC/data/enhanced/fftpack/unit/battle_wep_spr.bin`.
   **No change in battle** (basic swords stayed steel). → **the HD weapon render does NOT index this
   2D bin palette** (unlike chocobos, which do). The chocobo recolor pipeline does **not** transfer
   to weapons.

**Conclusion:** battle-weapon color is unreachable via the `<Palette>` data field (nex-overridden)
or the 2D bin palette (HD weapons don't read it). The magenta test implies a **separate HD weapon
texture/material** drives the color, which was never located. Recoloring is NOT the cheap
chocobo-pipeline win the scoping hoped — it's uncharted. **Shelved.**

## If revisited
- Locate the actual HD weapon color asset (a different pac / the g2d runtime-mapping channel rather
  than the fftpack bin).
- Sanity-check: re-confirm the chocobo bin still recolors via the same override path, to rule out a
  deploy/channel fault vs a genuine "HD weapons don't read the bin."
- Ask A's `<SpriteID>` model/size swap is independent of color and still a cheap one-item test.

See also memory: `project_fft_weapon_visuals`, `project_chocobo_color_pipeline`.
