# In-battle weapon visuals — scoping & test results (TABLED)

STATUS: JOURNAL (closed research log; verify claims against LIVE_LEDGER.md before building on them)

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

---

## Ask C -- recategorized weapon SWING ART (TABLED 2026-06-26)

The rebalance retypes axe/flail items into sword-family types via `categoryOverride` (ItemData byte
0x05 Item Type). In battle these weapons SWING WITH THE WRONG BLADE ART -- a sword that animates with
the old flail/axe graphic (Bug B/D in `handoff.md`). Full live RE this session settled it: **the blade
model is bound to the item id and is WALLED.** Chain of proof (probes in `tools/probes/`):

1. **Item-type override LANDS (functional half FIXED).** `item_type_probe.py` reads the live ItemData
   table (located by AOB; byte 0x05 = Item Type, `03=Sword 04=KnightSword 06=Axe 09=Flail`). Warbrand
   id 67 reads `Sword`, Ravager/Sunderer read `KnightSword`, etc. -> equip-class, weapon-skill access,
   and swing MOTION are correct (confirmed live: Orlandeau uses sword skills with Warbrand).
2. **ItemData byte 0x01 `SpriteID` is INERT for the swing.** Set Warbrand's live SpriteID to a sword
   value (15); the swing stayed busted. It is the menu icon, not the battle blade. (So the session's
   `spriteIdOverride` edits were reverted as confirmed no-ops -- do NOT re-add them.)
3. **The legacy FFHacktics "weapon battle sprite + palette" 2-byte table is VESTIGIAL in IC.** Located
   at VA `0x140785CF2` (`weapon_sprite_probe.py`), indexed by item id, byte0 palette / byte1 Graphic
   ID; its section map decodes perfectly (Sword 0/2/4/6, KnightSword 12/14, Katana 16/18, Axe/Flail
   22/24, ...). But writing BOTH copies (the .rdata original AND a heap copy) changed NOTHING, and CE
   "find what accesses" the heap copy fired ZERO times during a swing/re-equip. IC's HD renderer never
   reads it. (`weapon_sprite_writetest.py` does the guarded VirtualProtect write.)
4. **The blade model DOES follow the live combat-struct `CWeapon` (+0x20) field -- but so do the
   stats.** Freezing CWeapon at a sword id rendered a clean sword swing (`combat_struct_diff.py` area).
   BUT the same field drives the combat math: Warbrand attack 304 -> 121 when CWeapon held at Cleaver.
   So CWeapon binds model AND weapon together; there is NO render-only lever -- holding it neuters the
   weapon. The engine also re-asserts CWeapon from the roster every tick.
5. **No data/asset redirect exists** (parallel research, two workflows): the whole install has two
   `.mdl` files (neither a weapon); the battle art is the shared 2D atlas (`fftpack/unit/
   battle_wep_spr.bin` + `_shp`/`_seq`), index-addressed per visual-class with no per-item slot; the
   `ffto` nex layout set (245 tables) has no weapon-model/skin column; Nenkai's modloader exposes no
   weapon-graphic struct. This also CORRECTS Ask B's theory: the nex `Item` table has no palette column
   at all -- the HD render simply never reads `ItemData.Palette`.

**Verdict:** the swing art is item-id-asset-bound through an unlocated construction-time model resolver.
A real fix needs EITHER (a) relocating each retyped item onto a vanilla item id whose asset already
matches the new type (data rework -- how Sanguine Sword id 23 was moved off the Giant's Axe slot; limited
by available matching ids), OR (b) an RE spike to find a writable item-id->graphic indirection or a
construction hook that rewrites the resolved model handle independent of CWeapon. Not worth it for a
cosmetic. **TABLED.** The functional half (type/equip/skills/animation) is fixed and ships; the 7
retyped weapons (48 Terrastaff / 49 Ravager / 50 Sunderer / 67 Warbrand / 68 Bloodlash / 69 Climhazzard
/ 70 Sasori) swing their old axe/flail/morning-star art, harmlessly.
