# Treasure Master — prototype notes (2026-06-11)

Auto-mark trap/treasure tiles in battle, using the game's OWN native tile mark.

## Status

- **OUTPUT mechanism — SOLVED (proven live).** The hover-+-2 mark is a per-tile FLAG
  (bit `0x80` of a status byte), held in ~3 frame/buffer copies. Write+holding `0x80`
  onto an unmarked tile makes the engine render the mark itself — no input, no render
  hacking; release lets the engine clear it (so the DLL holds, like stat growth). Found
  via a differential toggle scan (`probes/mark_probe.py togglefind`). See `LIVE_LEDGER.md`.
- **OUTPUT addressing — OPEN.** The flags found are one tile's copies at heap addresses
  that rebase per battle. Need tile `(x,y)` → flag-address mapping (mark two known tiles,
  toggle-scan each, derive base+stride; then resolve the base via a pointer chain or AoB,
  not a hardcoded address).
- **INPUT data — OPEN, and the static guide is NOT trustworthy.** The PSX "Move-Find Item
  Guide" (`treasure.txt`) does not match The Ivalice Chronicles: Sledge Weald panel 1
  (Leather Hat) has no item in IC. So the DB must come from the game's own hidden-item
  table in memory, NOT the guide. The terrain grid (`0x140C65000`, 7 bytes/tile) is
  height/surface only — no treasure flag there. Source structure not yet located.
- **Cursor (x,y) read — validated.** `0x140C64A54` (x) / `0x140C6496C` (y), confirmed
  against the game's own on-screen coordinate display. NOTE `0x140C64E7C` is a
  list-position, NOT an absolute tile index (1 for (0,1), 3 for (1,9)).
- **Hover does NOT reveal treasure (tested 2026-06-11).** Diffed an unclaimed treasure
  tile (0,1) vs a blank (0,2); after excluding cursor + render the only changes are
  ordinary per-tile terrain (height/surface) display + animation counters — no
  treasure flag. Matches FFT design (items stay hidden until a Move-Find unit STEPS on
  the tile). So treasure is static map data; the way in is the claim-toggle below.

## Sledge Weald (Sweegy Woods) — confirmed treasure tiles (in-game, IC)

| (x,y) | Item | Source |
|---|---|---|
| (0,1) | Bow Gun | hover-confirmed, matches game's on-screen coord |
| (1,9) | Escutcheon | hover-confirmed |
| — | Leather Hat (PSX panel 1) | ABSENT in IC — guide wrong |
| ? | (panel 2 Leather Helmet) | unchecked |

## Next session

1. Locate the game's hidden-item table. CAVEAT: claiming a Move-Find item clears that
   tile for the current battle, so a CLAIMED treasure tile reads like a blank — compare
   only UNCLAIMED tiles (fresh battle). Better: claiming-clears-the-flag is itself a
   TOGGLE — toggle-scan a treasure tile by claiming it (snap before/after the grab) and
   keep the byte that clears. Same technique that cracked the mark. Reading this table =
   INPUT solved, guide retired, auto-detection on every map.
2. Per-tile flag addressing: derive tile→flag-address mapping; resolve the heap base each
   battle via pointer chain / AoB rather than hardcoding.
3. Then: DLL holds `0x80` on each treasure tile's flag every tick → tiles auto-mark.
