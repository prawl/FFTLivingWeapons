# Treasure Master — HANDOFF (2026-06-11)

**Goal:** auto-mark trap/treasure tiles in battle using the game's OWN native tile mark
(the hover-+-2 marker from the screenshot), driven by the Living Weapon DLL.

This was a long live-RE session. The hard "can we even do this?" question is **answered: yes.**
What remains is well-defined build work, not unknowns. Resume from here.

---

> **READ THIS TOP SECTION FIRST.** Everything below the `=== HISTORICAL JOURNEY ===` marker is the
> day's exploration and contains conclusions that were later **overturned** (we wrongly declared the
> mark "render-cache, not addressable" mid-day). The top section is the authoritative, proven result.

## SOLVED — native treasure marks work (2026-06-11, proven live)

**The feature is achievable and the hard RE is done.** Mechanism + stability are proven live; what
remains is engineering (per-map capture + map detection + the DLL signature).

### The mechanism (proven live, repeatedly)
Each tile's native mark is **bit `0x80` of render-flag bytes** at **module-static addresses**, in
**3 buffer families** (`0x140de_xxxx`, `0x140f95_xxxx`, `0x14116x_xxxx` for Sledge Weald), ~**6 bytes
per tile** (3 copies × a 2-byte pair, clean `01`→`81`). **Hold `0x80` on a tile's bytes every frame →
the engine paints its own native mark on that tile.** Proven live: **cursor-independent** (marked
`(0,1)` with cursor at `(0,5)`), **multi-tile** (held `(0,1)`+`(2,1)`, both painted), works while the
tile is **on-screen** (off-screen tiles aren't rendered, so the hold is a no-op there — fine, no mark
needed off-screen). Holding requires the tile to be in the render — but it does NOT require the byte to
be pre-"active"; forcing `00`/`01`→`81` and holding paints it.

### Stability (the architecture-defining facts)
- **Stable per map**: Sledge Weald `(0,1)` = `0x140de1ea7` survived a *fresh battle* (exit to world map,
  new fight) — identical addresses. So **no per-battle resolver is needed.**
- **Differs across maps**: Zeklaus Desert `(0,1)` = `0x140e7fb13 / 0x14103327b / 0x141180807` — the buffer
  base shifts per map. So the address is **NOT** a universal function of `(x,y)`; it's per-map.
- Within a map the `(x,y)→addr` fit is **nonlinear/packed** (don't chase a formula).

### Therefore the feature = a DLL ISignature that, per map, holds `0x80` on that map's treasure-flag bytes
every tick. The treasure *coordinates* for all 128 maps are already solved (see "INPUT" below); we just
need each map's treasure-tile *flag addresses*, captured once.

### Captured addresses so far (the DLL's first dataset)
- **Sledge Weald** (trap table Id 74) `(0,1)` Bow Gun: `0x140de1ea7 0x140de1f37 0x140f9560f 0x140f9569f 0x141166117 0x14116612f`
- Sledge Weald `(2,1)` (non-treasure, multi-tile proof): `0x140de0dc7 0x140de0e57 0x140f9452f 0x140f945bf 0x141165e47 0x141165e5f`
- **Zeklaus Desert** `(0,1)`: `0x140e7fb13 0x14103327b 0x141180807` (only 3 clean shown that run — re-capture; pairs may not have toggled).
- Still to capture for the Sledge prototype: `(1,9)` Escutcheon, `(5,11)` rare 144, `(6,6)` trap.

### NEXT-SESSION BUILD PLAN
1. **Capture pipeline**: extend `findflag` (or a new verb) to append a tile's clean `01→81` flag
   addresses into a per-map DB (`data/trap_treasure_tiles.json` already exists — add an `addrs` field
   per tile). Capture Sledge Weald's 4 treasure tiles first (we have `(0,1)`).
2. **Prototype hold**: hold all 4 Sledge flags at once → verify all treasure paints (visceral proof).
3. **Map detection** in the DLL: identify the current map to pick the right addresses. Options — port
   FFTHandsFree `DetectMap()` fingerprinting, OR use the **buffer base** (differs per map = a natural map
   key), OR terrain-grid fingerprint (`0x140C65000`).
4. **`TreasureMaster` ISignature** (`LivingWeapon/`): TDD per house rules — `Tick()` holds `0x80` on the
   current map's treasure-flag bytes via `Mem` (guarded WPM). One ctor line + one array entry (see the
   contract in `LivingWeapon/ISignature.cs`). Build-flavored if needed.
5. **Scale capture** to all maps with trap-table entries (≤4 tiles each; capture as you naturally play).

### Tooling built today — `tools/probes/treasure_addr.py`
`findflag` (cursor-still toggle scan → a tile's clean flag bytes; THE capture verb) · `survive` ·
`vpmatrix`/`solve` · `region`/`ptrscan` · `read`/`poke`/`hold`/`holdmany` (hold = the paint primitive) ·
`findmarks`/`pair`/`mapscan`/`layout`/`multimark`/`neighbor` (**contaminated multi-tile attempts — do not
trust**). Captures in `%TEMP%\fft_treasure\`. `mark_probe.py` has `churn`/`snap`/`diff`/`find` (the
camera-invariant intersection that cracked it used `snap`/`diff` + a custom 3-snap script).

### Two real alternatives (kept as fallbacks)
- **Randomizer** (`tools/parse_treasure_guide.py` era + the table): `MapTrapFormationData.xml` in the
  modloader `TableData` is the full, moddable treasure table (128 maps × ≤4 items: `X,Y,TrapFlags,
  RareItemId,CommonItemId`). Validated vs our hovers. "Randomized treasure runs" = a static data mod.
- **Overlay** (`tools/treasure_overlay.py`): WORKS — external transparent window, projects tiles via the
  camera **view matrix at static `0x1407D61EC`** (orthonormal 2:1 iso, found via a rotation differential),
  draws markers. Needs only screen-scale calibration (F-key tuned, live cursor crosshair as the anchor).

### DEAD ENDS (do not repeat)
Native chest = a crystallized unit (`deathCtr` at unit +0x07), position-write desyncs the renderer.
Map textures = a shared packed atlas (no per-tile color). Terrain shading = darken-only. The persistent
"marked list" / `0x142e` store / `0x14080b` counter-list = all render-coupled or unrelated UI — there is
**no separate logical source**; the render-flag bytes above ARE the writable state.

### INPUT — which tiles have treasure (SOLVED via the table)
Every map's treasure is in `MapTrapFormationData.xml` (per-map `Id`, ≤4 items, each `X,Y`(0-15),
`TrapFlags`, `RareItemId`, `CommonItemId`). `(x,y)` = grid-cursor coords (`0x140C64A54`/`0x140C6496C`,
validated). No more manual hovering needed for coords; `data/trap_treasure_tiles.json` + the parser are
validators only now.

=== HISTORICAL JOURNEY (superseded conclusions below — kept for the record) ===

---

## NEXT STEPS (the plan)

**1. Build the treasure-tile coordinate database (`data/trap_treasure_tiles.json`).**
   - Source: the saved PSX Move-Find Item Guide (`treasure.txt`) tells you WHICH maps have
     treasure, the items, and the rough panel layout.
   - **Caveat (load-bearing):** the guide is PSX, NOT The Ivalice Chronicles. Sledge Weald
     panel 1 (Leather Hat) has NO item in IC — confirmed. And the guide's ASCII grid
     orientation does NOT match the game's internal (x,y) (a panel drawn bottom-right read
     as (0,1) in-game). So the guide is a *checklist of where to look*, not coords.
   - **Real workflow:** for each map, with a unit on the field, hover each treasure panel
     and read the cursor (x,y) — the game's on-screen coord display IS the ground truth (we
     verified it matches memory exactly). Record `{x, y, item}` per tile. This is manual but
     certain and IC-accurate. FFT has a finite battle-map count.
   - Schema seeded in `data/trap_treasure_tiles.json` (Sledge Weald already has 2 tiles).

**2. Solve per-tile flag ADDRESSING (the gate for auto-marking).**
   - Coords alone aren't enough — to mark tile (x,y) the DLL needs that tile's flag address.
   - We found ONE tile's flag (3 buffer copies) via toggle scan, but those are HEAP addresses
     that **rebase every battle**, so they can't be hardcoded.
   - Approach: mark two KNOWN tiles in one battle, toggle-scan each to get their flag
     addresses, derive the (x,y) → address mapping (base + per-tile stride). Then find how to
     resolve the base each battle — a pointer chain from a stable module address, or an AoB
     signature scan for the flag region. This is the same kind of work as the kill-tracker's
     band locator.

**3. Wire the DLL.** Detect the current map (FFTHandsFree `DetectMap()` fingerprinting is
   done), look up its treasure tiles from the DB, and hold bit `0x80` on each tile's flag
   every tick (exactly like the runtime holds stat growth / charm). Out-of-battle: nothing.

---

## PROVEN: the mark mechanism (how to reproduce)

The native mark is **bit `0x80` of a per-tile status byte** (NOT a coordinate list — that's
why an earlier 3.8 GB array-of-bytes scan found nothing). Held in ~3 frame/buffer copies.

**How it was found — differential toggle scan** (`tools/probes/mark_probe.py`):
1. Park cursor on a tile, unmarked. `snap off_0`.
2. Mark (press 2). `snap on_0`. Unmark. `snap off_1`. Mark. `snap on_1`. Unmark. `snap off_2`.
   (Mark/unmark the SAME tile, cursor still, every cycle.)
3. `togglefind off:off_0,off_1,off_2 on:on_0,on_1` → bytes that flip in lockstep.
4. Exclude the UI render arena (`~0x140c69000`–`0x140cc5000`, multi-buffered, survives the
   toggle because the billboard is deterministic per state). Survivors in real game-data
   regions (e.g. `0x14187xxxx` auth-band) are the store.
5. Confirm by live re-read: read each candidate OFF, mark, read ON, unmark, read OFF. The
   real ones flip `01→81→01` (bit 0x80); coincidences don't.

**How to MARK a tile ourselves (proven):** write `0x80` onto the flag byte(s) and HOLD
(`holdmany`). Engine renders the mark with no input. Release → engine clears it. So the DLL
holds continuously. Live-proven 2026-06-11: held `0x80` on an unmarked tile, the mark appeared.

---

## Key addresses (this build; module-static ones are stable, heap ones rebase)

- Cursor X = `0x140C64A54`, cursor Y = `0x140C6496C` (u8). VALIDATED vs on-screen display.
- `0x140C64E7C` = a list-position, NOT an absolute tile index (was 1 for (0,1), 3 for (1,9)).
- Terrain grid `0x140C65000`, 7 bytes/tile = `[height, slope, 00, 1f 1f 1f, X]`. Height/
  surface only — NO treasure flag here.
- Inventory count: `count[id] = u8 @ 0x1411A17C0 + id` (used to confirm the claimed item id).
- Mark flag for one tile this session: `0x140e7c3bb` / `0x14102fb23` / `0x14117fe67` (bit
  0x80) — EXAMPLES; heap, rebase per battle. Do not hardcode.

## Dead ends (don't repeat)

- **Hover does NOT reveal treasure** — diff of a treasure tile vs blank tile = only terrain +
  cursor, no item flag. Items stay hidden until a Move-Find unit STEPS on the tile.
- **Claim does NOT change the item table** — claiming the Scoutbolt (gained inventory id 77)
  cleared zero bytes holding 77; the table is static, FFT tracks "claimed" separately. So a
  claim-toggle can't find the table, and scanning for the id+coords is too noisy (0x4d common).
- The move-range highlight system: the count byte `0x140c64c68` gates the highlight (holding
  it keeps it rendered out of move), but the highlight's tile source isn't the path list at
  `0x140C66315` and writing there changed nothing. Not the path forward; use the mark instead.

## Probe reference — `tools/probes/mark_probe.py`

`regions` · `churn [s]` (build self-change mask) · `snap <name>` · `diff <a> <b>` ·
`togglefind off:a,b,c on:x,y` (lockstep differential) · `find <hexbytes>` (AoB, C-speed) ·
`read <addr> <n>` · `poke <addr> <hex>` (one-shot) · `hold <addr> <hex> [s]` (write+hold) ·
`holdmany <s> <addr> <hex> ...` (hold several). Session snaps live in `%TEMP%\fft_mark_probe\`.

## Confirmed data — Sledge Weald (Sweegy Woods), in IC

| (x,y) | Item (mod name) | Note |
|---|---|---|
| (0,1) | Scoutbolt (vanilla Bow Gun, id 77) | hover-confirmed; claimed as the probe |
| (1,9) | Escutcheon | hover-confirmed, unclaimed |
| — | (PSX panel 1 Leather Hat) | ABSENT in IC |
| ? | (PSX panel 2 Leather Helmet) | unchecked |

---

## 2026-06-11 PM — the addressing session (findings + a reframed plan)

A long live-RE session on the per-tile flag ADDRESSING. It did NOT close the formula, but it
ruled out the approach we were on and found the structure that should crack it next time.
**Net: the mark is not addressable by an `(x,y)` formula; anchor on the static terrain grid.**

### What we learned (the load-bearing facts)

1. **There are two classes of flag copy: volatile render buffers and a cursor-STABLE store.**
   `findflag` (toggle a tile on/off with the cursor held still, lockstep filter) cleanly returns
   ~6 bytes per tile: **3 buffer copies × a 2-byte pair** (pair gap `0x90` in the big buffers,
   `0x18`/`0x14` in the compact one). Those copies sit in render buffers (`0x140de…`,
   `0x140f95…`, `0x141166…`, `0x141579…`, `0x142fea…`) that **relocate the instant the cursor
   moves / the camera scrolls**. The `survive` test (mark, then move the cursor far, keep only
   bytes still set at the same address) proved a **separate copy survives the move at
   `~0x142exxxxx`** — the authoritative, cursor-independent store. That `0x142e` copy is what the
   DLL should ultimately write.

2. **Everything rebases; the only clean capture is single-tile, cursor-still.** The volatile
   buffers move on every cursor step (this is what made `multimark`/`mapscan`/`pair` contaminate —
   a moved buffer's old marks reappear at new addresses and get misattributed; camera-scroll also
   re-renders enough that the move-filter leaks coincidental `0x81`s). Even the `0x142e` store
   rebases between separate probe runs / large state changes. **`findflag` with the cursor dead
   still is the only capture that stays clean.** Multi-tile marking across the map does not work.

3. **The tile index is NONLINEAR in the cursor `(x,y)`.** Tiles A(0,1), B(2,0), C(1,9) were
   captured in ONE tight array (`0x141166027 / …117 / …957`, a 2 KB span — unambiguously a single
   allocation), and the linear fit `addr = base + a·x + b·y` came out fractional (determinant 17).
   So there is **no `(x,y)` formula**. The map is almost certainly **non-rectangular / packed**
   (invalid tiles — trees, like the un-markable `(1,1)` — are skipped), so the array index is the
   engine's internal packed tile order, not `y·W + x`.

4. **The terrain grid `0x140C65000` is a clean, STATIC, 7-byte/tile array.** Confirmed this
   session: the `1f` marker repeats exactly every 7 bytes for 200+ records. It cannot rebase, and
   it is the engine's per-tile structure — the right Rosetta stone for the packed index. (Also on
   file from the prior session: `0x140C64E7C` holds a per-tile "list-position" — 1 at (0,1), 3 at
   (1,9) — a candidate packed-index read, but its deltas did NOT match the mark-array index, so it
   is a different list; reconcile next time.)

### The reframed plan (next session, clear-headed)

- **Stop multi-tile marking.** Use only `findflag` (cursor still) for clean single-tile bytes,
  and `survive` (mark → far move) to confirm the stable `0x142e` copy.
- **Anchor on the static terrain grid.** Hover known tiles and read both the cursor `(x,y)` and
  the terrain-grid record they land on to learn `(x,y) → engine tile index` (row-major over the
  full bounding box, or packed — the grid tells us). The grid is static, so this is reliable and
  repeatable with no volatility.
- **Read the mark array directly, don't multi-mark it.** With ONE tile marked and its `0x142e`
  byte known (from `survive`), read a window of the array to measure its stride and confirm it is
  indexed by the same tile order as the terrain grid. Then `mark_addr = stable_base + index·stride`.
- **Then the resolver:** how to find `stable_base` each battle — pointer chain from a module-static
  address, AoB, or (cleanest) a fixed offset from the static terrain grid / a struct the runtime
  already locates. The inter-buffer stride between the first two render copies was a **stable
  constant `0x1b3768`** across runs — a possible anchor if it generalizes.

### Tooling built this session — `tools/probes/treasure_addr.py`

RPM/WPM, UI-render-arena excluded from snapshots. Verbs:
`findflag` (cursor-still toggle scan → clean per-tile bytes, saves `flag_<x>_<y>.json`) ·
`survive` (mark → far move → the cursor-stable survivors) · `region <addr>` (VirtualQueryEx) ·
`ptrscan <addr>` (LE8 pointer hunt, resolver seed) · `read`/`poke`/`hold`/`holdmany` ·
`solve` (cluster saved captures by buffer family, fit `base + xs·x + yp·y`) ·
`pair`/`mapscan`/`layout`/`multimark` (multi-tile attempts — **contaminated by buffer relocation;
kept only as cautionary one-shots, do not trust their output**) · `findmarks` (mark several tiles,
unmark them, diff → the per-tile store wherever it currently lives). Captures live in
`%TEMP%\fft_treasure\`. Also: `tools/probes/capture_treasure.py` (hover→cursor→DB) and
`tools/parse_treasure_guide.py` (clean-grid guide → draft coords) from the input-pipeline work.

### Update: the per-tile store measured, and why it doesn't close

Pushed past the render buffers and characterized the **cursor-stable per-tile store** (the
`~0x142e_xxxx` family). It is a genuine per-tile array — resting byte `0x00`, marked `0x80`, two
bytes per tile `0x14` apart — found via `findmarks` (mark a run of tiles, unmark, diff). Measured
strides, each from a clean within-one-capture comb:

- **y-neighbors (same x, consecutive y): `0x1b0` = 432 bytes apart** (cleanest signal of the day;
  a `0x360` = 2×432 gap appears exactly where a tile was skipped).
- **x-neighbors (same y, consecutive x): `0x5298` = 21144 bytes apart** (reproduced across two
  independent rows — real, not noise).

**These do NOT reconcile with the 208-tile terrain grid.** `21144 / 432 ≈ 48.9` (not integer), and
no 16×13 / 13×16 indexing fits both. So `0x142e` is a **large (~432 B/tile) per-tile render/detail
structure over the full bounding box, not the compact logic array** — and it **relocates between
captures** (it was at `0x142eca…`, `0x142ec4…`, `0x142ebed…` on successive runs). Conclusion:

> **The native tile mark is render-side state. It cannot be addressed by an `(x,y)→address`
> formula and held at a fixed address — the store both uses a non-grid render layout and rebases
> constantly.** Writing it reliably would require per-frame re-location AND decoding the render
> layout — a large lift.

**Two honest paths for next session (decide before more probing):**
1. **Hunt for a compact logic mark-array** — a ~208-byte array indexed like the terrain grid that
   the renderer reads from. The toggle/diff scans so far only ever surfaced render buffers + the
   `0x142e` detail store; if no compact array exists, the native mark is render-only and path 2 wins.
2. **Pivot off the native mark** — have the Living Weapon DLL render its OWN treasure indicator
   (it already locates units and paints the equip card). Sidesteps the addressing wall entirely.
   This is likely the pragmatic route given the above.

The terrain grid (`0x140C65000`, **208 records × 7 bytes**, static) remains the clean anchor for
*input* — `(x,y)→engine index` for the DB — regardless of which output path we take.

---

## 2026-06-11 — DECISION: custom DLL-rendered overlay (output path)

The native mark is a dead mechanism (above). **Chosen path: the Living Weapon DLL renders its OWN
treasure markers**, so the engine's render-cache is irrelevant. This is a NEW capability for the DLL
(today it only does RPM/WPM; an overlay means a graphics hook). Graphics API confirmed: **D3D12**
(`dxgi.dll` + `d3d12.dll` loaded; no D3D11/Vulkan/GL).

**What we already have (no RE needed):**
- Tile `(x,y)` → world is an **identity** map to map-tile coords (FFTHandsFree `docs/BATTLE_COORDINATES.md`,
  independently re-validated): grid X `0x140C64A54`, grid Y `0x140C6496C` (u8).
- Tile **height** (z) = `tile.height + tile.slope/2`, read from the static terrain grid (`0x140C65000`,
  7 B/tile = `[height, slope, …]`).
- **Camera rotation** = `0x14077C970 % 4` (0–3 isometric angles; Q/E increment it).
- The treasure-tile DB + the hover-capture probe (input pipeline) — done.

**What FFTHandsFree does NOT have:** any world→screen projection. It navigates by arrow-key cursor
moves, never projecting to pixels. So the **view-projection matrix is the one piece we must RE.**

**Overlay build plan (next session — a graphics project, multi-step, Denuvo = extra crash care):**
1. **D3D12 present hook** (MinHook on `IDXGISwapChain3::Present` / the command-queue) — scaffold that
   can draw a test quad each frame. The big new piece; more involved than a D3D11 hook.
2. **Find the camera view-projection matrix** — a 4×4 float matrix in memory near the render/camera
   state (or derive an iso projection from camera pan+zoom+the known rotation). Calibrate by checking
   that a known tile's projected corners land where the tile is on screen.
3. **Project tile (x,y,height) → screen**, draw a marker: a translucent ground-conforming quad
   (native look) or a floating sprite/diamond (cheaper) — either is fine per Patrick.
4. **Drive from the DB**: on battle enter, look up the map's treasure tiles and draw each.

Fallback if the D3D12 hook proves too fragile on Denuvo: the "DLL text treasure-radar" (card/log
line listing treasure tiles) ships the *information* with zero graphics risk.

---

## 2026-06-11 — BREAKTHROUGH: the treasure data is a moddable table (INPUT solved)

The whole render-cache hunt was the wrong layer for the DATA. `MapTrapFormationData.xml` in the
modloader's `TableData` (path = `lib.paths.TABLE_DATA`) is the complete move-find/trap table:
**128 map entries** (`Id` = map id, ≤127), up to **4 items each** —
`X`,`Y` (0–15), `TrapFlags` (`None`/`DisableTrap`/…), `RareItemId`, `CommonItemId` (ItemData ids).
Moddable exactly like the item tables. Override filename per the template comment = `MapTrapData.xml`
under `FFTIVC/tables/<flavor>/` — **VERIFY the exact accepted filename before shipping.**

VALIDATED against our hand-hovered ground truth — Id 74 "The Siedge Weald":
`(0,1)` rare 77 (Bow Gun) ✓, `(1,9)` rare 128 (Escutcheon) ✓ — exact; plus `(5,11)` rare 144 and
`(6,6)` rare 157 (flags `None` = the live trap) for free. So `(x,y)` = the same map-tile coords we
read at the cursor; the table is authoritative for every map. **No more hovering needed for coords.**

What this gives / doesn't:
- ✅ **WHERE every treasure/trap is, all maps** — and a clean **randomizer** feature (rewrite the
  table's `X`/`Y`/items; static data mod, zero runtime risk). Reachability caveat: a random `X,Y`
  must be a STANDABLE tile (need the per-map walkable set, or shuffle among known-valid positions).
- ❌ **The on-tile VISUAL marker is still unsolved.** FFT hides move-find items by design (no native
  reveal flag), the render-cache mark is unaddressable, input automation is off-limits, and Patrick
  ruled out HUD/panel/minimap UI. So a visual must be **rendered by us at the tile's screen position**
  → still gated on the **view-projection matrix** (not found near the camera struct; needs a
  camera-rotation differential or a cursor-screen-pos calibration hunt). That is the open next step.
