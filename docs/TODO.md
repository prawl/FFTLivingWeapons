# TODO

**The next release scope is LOCKED in `docs/RELEASE_SCOPE.md`** (consolidation release,
"Finish the Samurai Swords + a focused balance pass"). Work that file's IN checklist to ship.
This file is the BACKLOG: what is deferred past that release, and what is walled. Keep it that
way -- new ideas land here as backlog, not as release scope, until they are pulled into a scope doc.

## In the next release (see docs/RELEASE_SCOPE.md)
- Finish the Samurai Swords -- 4 signatures (Iai + Kobu done; Murasame id41 + Kiku id45 new). BLOCKER.
- Fix Galewind / Puppeteer expiry -- ship-with-fallback (wielder-clock + card reword). BLOCKER.
- Item-balance tuning pass -- Rod nerf + added-Move nerf + early-armor rider smell + Claymore card reword.
- Remove Offensive Chemist (independent of Treasure Master; cheap).
- Doc + hygiene -- French release-note, USER_FEEDBACK enemy correction, delete falsified
  pointer-presence turn code, drop dead spriteIdOverride on Warbrand id67.

## Deferred (post-release backlog)
- Remove Treasure Master (OBVIATES the Scholar's Ring idle-nag bug -- do not fix that doomed code).
- Alter Axes and Flails (only cheap slice: Squire/Geomancer equip access on existing sword-typed items).
- Migrate the remaining lossy-detection siblings (Maim/Larceny/Ricochet) to cache + rearm.
- Kill-tally milestones on the equip card beyond the counter (gated on a glyph-render probe).
- Replace the Stormbrand (do AFTER the Samurai signatures lock, to avoid a Slow/element dupe).
- Enemies actually USE living-weapon benefits (XL undesigned feature; static rebalance already lands the real want).

## Walled (blocked by engine / Denuvo / modloader)
- Fix the sword swing-art (art welded to weapon id; the same render node also drives damage).
- Make item TEXT display in French (game + modloader parser walls; DLL live-paint is the only path).
