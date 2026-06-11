# Treasure Master — HANDOFF (2026-06-11)

**Goal:** auto-mark trap/treasure tiles in battle using the game's OWN native tile mark
(the hover-+-2 marker from the screenshot), driven by the Living Weapon DLL.

This was a long live-RE session. The hard "can we even do this?" question is **answered: yes.**
What remains is well-defined build work, not unknowns. Resume from here.

---

## TL;DR status

| Piece | State |
|---|---|
| **Mark a tile from memory (output mechanism)** | **SOLVED, proven live.** Per-tile flag, bit `0x80`. Write+hold renders it. |
| Per-tile flag ADDRESSING (tile (x,y) → its flag address) | OPEN. Tractable (derive base+stride from 2 toggled tiles; heap rebases per battle → resolve base via pointer/AoB). |
| INPUT: which tiles have treasure | OPEN. PSX guide unreliable for IC; game's table is static (not toggle/scan-findable easily). Two paths below. |
| Cursor (x,y) read | VALIDATED against the game's on-screen coord display. |

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
