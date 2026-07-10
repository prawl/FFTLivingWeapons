# LW-51 notes: save-file relocation + non-destructive migration

STATUS: JOURNAL (unit tests green, SaveLocationTests.cs, 9 tests, and wired into Engine.cs; NOT
live-verified, the owner is away and this build deliberately never deploys or runs the game).
This doc is the handoff for that live-verify pass, plus the two probes LW-51 deliberately
deferred.

## What shipped

kills.json, legends.json, and gunslinger.json now live in the update-safe
`Reloaded/User/Mods/prawl.fft.livingweapons/` directory instead of the deploy mod dir
(`Reloaded/Mods/prawl.fft.livingweapons/`). A Reloaded mod-package update replaces the deploy
dir wholesale, so anything living there was wiped on every update; User/Mods is never touched by
an update (the same directory Config.json already lives in).

On first launch after this ships, `SaveLocation.Migrate` copies each legacy file (and its `.bak`,
on an independent guard) from the old deploy-dir location into the new save dir, once, only if
the new location doesn't already have a file there. It never deletes or overwrites anything.
Default behavior is unchanged otherwise: one global save per install, same filenames, same JSON
schema, same by-reference `Kills` dictionary every subsystem shares.

## Live-test script (owner-gated)

Two scenarios matter; neither is a `BuildLinked.ps1` deploy or a Reloaded-loader self-update,
both are a real player-style update of the packaged mod:

1. **Steady-state update survives.** Place a known `kills.json` directly in
   `Reloaded/User/Mods/prawl.fft.livingweapons/`. Perform a real update of
   `Reloaded/Mods/prawl.fft.livingweapons` the way a player would (Vortex/Nexus reinstall, or a
   manual delete-and-extract-over of the deploy folder). Launch the game and confirm the tally
   read back in-game still matches the known value, and that the User-dir file on disk is
   byte-unchanged by the update.
2. **Fresh install migrates a legacy file.** Simulate an extract-over-merge update: start from a
   deploy dir that still has a legacy `kills.json` sitting in `Mods/prawl.fft.livingweapons/`
   (the pre-LW-51 layout) and nothing yet in `User/Mods/prawl.fft.livingweapons/`. Launch once.
   Confirm the file appears in the User dir with the same content, the deploy-dir copy is left in
   place untouched, and the in-game tally reflects the migrated counts.

## Migration-timing caveat (not a bug, a hard boundary)

The 2.2.2 -> 2.3.0 transition (the version LW-51 ships in) is only recoverable for players whose
update mechanism does an extract-over-merge, leaving the old deploy-dir files readable when the
new DLL first runs and migrates them. A mod-package update that deletes the whole deploy folder
*before* the LW-51 build's first launch (a clean folder-replace) has already destroyed the only
copy of a save that predates this fix; there is nothing left for the code to migrate. This is a
one-time, one-directional gap tied to this exact version boundary. Every update after 2.3.0 has
landed is safe, because by then the authoritative copy already lives in `User/Mods/`, which no
update ever touches.

## Per-playthrough scoping: the deferred half (grounded via FFHacktics recon, 2026-07-08)

LW-51 ships one GLOBAL save per install (no playthrough identity). Two findings from the FFT
save/story internals shape how to close it. All addresses below are PSX reference (FFHacktics);
TIC has a different save format (the FF16 `.png` blob) and has renumbered ids before (see the
`ic-job-id-remap` memory), so every id needs a TIC live-confirm. The SIGNALS transfer even though
the addresses do not.

FINDING 1: FFT has NO native per-playthrough identity. The PSX "Load Game" routine (`0x80130338`)
validates a format byte (save `+0x117` must be 4) and a slot-used marker (`+0x100`), copies
party/inventory/variables, and reads a play-timer (`+0x120`). There is no save GUID, seed, or
creation id anywhere. A stable "which playthrough" key does not exist to read; it can only be
CONSTRUCTED (Tier 2).

FINDING 2: a clean NEW-GAME EDGE is reachable, from three redundant LIVE world-state signals (in
the WORLD.BIN Player Data block the game holds on the world map, the same family as the already-read
`Offsets.LiveBattleMapId = 0x140784478`):
- Storyline Progression / Scenario Order (PSX `~0x800578d4`): a MONOTONIC story counter. A new game
  sits at scenario `0x001` (Orbonne Prayer); `0x000` is an unused sentinel; it only climbs. New game
  reads the low opening value, a continue reads the saved higher value.
- ENTD Data ID (PSX `~0x800577e4`): the current encounter/event id; the opening event is ENTD
  `0x100` ("Beginning of game inside Orbonne"). ENCOUNTER-granular (unlike the map-only
  LiveBattleMapId), so map reuse never confuses it.
- Playtime (save `+0x120`, copied to a live H:M:S timer near `0x800459c4`): about 0 on a fresh game.
- Named story SCRIPT VARIABLES (read via the game's Get/Set Script Variable, PSX WORLD.BIN): Var
  `0x27` = Current Event (a story-event counter, `0x12C` = the final vanilla event) is another
  monotonic progress value; Var `0x64` = "Next Scenario? / New Game?" is a flag toggled around the
  save-screen logic (uncertain semantics, a candidate new-game flag). Extra sampling targets for the
  probe alongside Scenario Order.
These are a PROGRESS clock, not an identity: two playthroughs pass through scenario `0x001` identically.

Corroboration (OPEN.BIN title flow): there is a discrete "new game selected?" title-menu path
(`~0x0006b358`) and a Birthday character-creation menu, so a New Game is a real distinct event, not
an inference. But the title screen is the intro executable (a different context from where the mod
reads gameplay memory), so the practical lever stays the story-progress value a new game PRODUCES,
not the title-menu selection. Ramza's birthday/zodiac is a weak per-playthrough candidate (limited
values, not unique), noted for completeness only.

### Tier 1 (ships "a new game starts fresh", the primary want): reset-on-new-game
Read the live Storyline Progression; when it reads the opening value (corroborated by ENTD id == the
opening event and/or playtime near 0), ARCHIVE the current tally and start a fresh one. This does NOT
isolate two concurrent playthroughs on different save slots (no identity to switch on), but for a
play-one-at-a-time player it is effectively per-playthrough. Lightest path, no new persistence.

### Tier 2 (full per-playthrough incl. concurrent-save isolation): mod-stamped id
At the new-game edge, generate a random id and STAMP it into an unused/free save variable (the save
has "free halfwords", Variables `0xA00-0xAA8`, plus other unused fields). It then persists per save
slot; on Continue the mod reads it back and keys the tally file to that id. Bigger (needs a proven
truly-unused writable save variable in TIC), but it is the only path that stops two playthroughs
cross-contaminating.

### The TIC signal: RESOLVED LIVE 2026-07-08 (probe run, owner started a new game)
The Tier-1 lever is the live EVENT ID at `Offsets.EventId = 0x140782A94` (u16), already in Offsets.cs
(1.5-predicted `+0x6000` off FFTHandsFree's pre-1.5 `0x14077CA94`, anchored by the CONFIRMED neighbor
`Acted = 0x140782A8C`). `tools/probes/read_state.py` confirmed it live: `0xFFFF` at the menu, then it
climbed `2 -> 4 -> 5` through the Orbonne prologue (`2` = Orbonne Prayer opening dialogue, `4` = Orbonne
Battle, `5` = Gafgarion chat), EXACTLY matching the PSX Scenario Order table -- so 1.5 kept the PSX
event numbering. It climbs monotonically into the hundreds across the game.

TIER 1 IS NOW BUILDABLE, fully grounded:
- Trigger: `Offsets.EventId` in the low opening range (`== 1` / `2`, the Orbonne Prayer, which lingers
  for minutes and only ever plays at a brand-new game's start), edge-detected (fire once on entry).
- HARD GATE: only read `EventId` when `Offsets.BattleMode == 0`. In battle it holds a stale event or
  aliases as the acting unit's nameId (`400+`) -- confirmed live (it read `5` at `BattleMode==3`).
- Action on the edge: archive the current tally (non-destructive) and start a fresh one.

TIER 2 (concurrent-save isolation) is still open: it needs a genuinely-unused writable TIC save
variable to stamp a mod-generated id into (the save has "free halfwords" Variables `0xA00-0xAA8`), not
yet found. 1.5 New Game+ carries over levels and items, so a "did the roster reset" heuristic is NOT a
safe substitute for the event-id signal.

### Superseded fallbacks
The earlier filesystem-snapshot probe and blind memory-scan are demoted to fallbacks: the
story-progress signals above are more direct and already in the mod's reachable live-world-state
family. Filesystem note for reference: the game's saves are opaque `.png` blobs at
`OneDrive\Documents\My Games\...\Steam\<id64>\`, where `<id64>` is the per-USER Steam id, not a
per-playthrough value.

## $PreservedSaveFiles bonus effect

`tools/pipeline.ps1`'s `$PreservedSaveFiles` round-trip exists because `BuildLinked.ps1`'s
`[3/5]` clean step used to wipe `kills.json`/`legends.json`/`gunslinger.json` (and their `.bak`
files) straight out of the deploy dir before restaging. Now that these files live outside
`$dest` entirely, that clean step can no longer touch them (a bonus LW-28 side-fix). The
round-trip mechanism itself now only shuffles files that are frozen fossils in the deploy dir
(nothing writes there anymore) and is safe to trim later; left as-is for this build since it's
harmless and out of LW-51's scope.
