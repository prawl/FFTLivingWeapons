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

## The 10/10 swing (post-release headline bet)
**Full vision + proven levers + open probes: `docs/RELIQUARY_DESIGN.md`.**

**Slayer's Reliquary -- the weapon remembers WHO it killed, not just how many.** When a weapon lands
the killing blow on a named Ivalice antagonist (Lucavi, Zodiac Brave, story boss), etch that foe onto
the blade as a growing roll that promotes to an earned canonical card epithet ("Demonsbane -- felled
Queklain, Velius, Hashmal"), and announce the deed in the moment on the game's own center-screen
callout banner (PromptSwap fallback). Turns the kill tally from a scoreboard into a trophy wall built
from FFT's own rogues' gallery -- the deepest, wall-free instantiation of the attachment thesis.
- **PROBE FIRST (the one research bet):** does the enemy ANameId (already read at the attribution
  edge, ActorRegister) stably distinguish a named boss from a job-sharing grunt at hp==0? The ledger
  flags the enemy/player nameId pools "not proven disjoint" -- may need composite job+sprite+level
  keys. Also confirm a killing-blow edge actually FIRES on marquee bosses (some end the battle /
  crystallize by cutscene without a normal corpse-death edge). Bounded probe on an already-read field.
- **Reuses proven levers only:** CreditKill death edge, enemy nameId read, kills.json-style atomic
  persist (a parallel legends.json), SuffixRotation card paint (DLL-live -- the French wall does NOT
  bite), and the big-banner callout (ShowSpike, proven live). No weapon art, no new ability, no crit.
- **Stages, each green-gated:** probe -> curated legends table (Lucavi/Zodiac core + unique-sprite
  human bosses) + atomic persist -> evolving card epithet -> moment-of-kill PromptSwap toast ->
  big-banner delivery upgrade. Grafts in "The Awakening" (route the once-ever +3 crossing to the same
  big banner instead of the whisper-y facing-prompt slot).
- **Design constraints to respect:** legends are RARE (many weapons earn none -- the durable card
  epithet, not the rare toast, must carry the everyday payoff; scarcity = meaning); epithets are pure
  fiction with ZERO stat bonus (the moment carries the feeling, not a number); hard-gate the loud
  banner to genuine marquee kills so it never becomes noise; keep the PromptSwap fallback so a Denuvo
  dead-hook launch never silently eats a once-per-campaign boss kill.

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
