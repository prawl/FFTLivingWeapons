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
- **BUG: auto-battle kill attribution is dead wrong** (owner-reported 2026-07-05; ROOT CAUSE
  CONFIRMED same day from flight archive flight_20260705_075603_battle-exit.jsonl). Auto-battle
  chains player actions without the Acted byte resting low for UnfreezeTicks (~400ms), so the
  acted-period NEVER closes: the once-per-period actor latch (KillTracker.cs Poll) goes stale and
  every later kill inherits it. Archive proof: after the battle's last latch ([60] Warlock's Staff,
  T-45s), zero acted edges for the rest of the battle; three kills (T-39.8/-26.1/-17.6) all credited
  60 while the actor POINTER named Ramza (nameId=1, Chaos Blade, bridge=Player) at each kill instant.
  Mis-credits cascaded into toasts (staff "first blood" + Choir unlock) and kills.json.
  **FIX (needs /build + live-verify, attribution core):** close the acted-period on ActorRegister
  OWNER CHANGE (a new Player-bridged owner = a new turn) in addition to the byte-fall debounce, so
  the latch re-resolves per real actor; the register's bridge classification already filters the
  struck-victim pointer dwell. Respect the ledger caveat: the pointer may name the REACTOR during
  a reaction (unverified) -- the fix must not regress reaction-kill credit. TurnTracker's turn
  counting collapses the same way under auto-battle (turns #2-#6 all credited one fingerprint,
  log 07:58) -- the same owner-change edge likely repairs both. Until fixed: living-weapon probe
  battles (Reliquary P1-P3) must be fought MANUALLY -- auto-battle poisons killer-side evidence
  (victim-side capture is unaffected).
  **NOT auto-battle-exclusive (owner-reported 2026-07-05 09:19, MANUAL Zirekile battle):** Ember
  Rod (id 53) credited the slot-13 enemy Knight kill (nameId 450) landed by another unit. Log
  chain: Ember latch 09:19:20, then turn-credits #8-#11 ALL collapsed onto the wielder's
  fingerprint (22/52/71) for 23s with no fresh player latch; the 25s-stale latch took the 09:19:45
  credit. ADJUDICATED same day (flight_20260705_092548): the CLAYMORE wielder (nameId 451)
  landed the blow -- the acted rising edge fired ~500ms BEFORE the pointer left the previous
  actor (588), so turn #34 + the latch resolved 588/[53] and 451's blow 100ms later paid the
  stale latch (the ledger ActorPtr pointer-at-edge caveat, on tape). FIX IMPLICATION:
  owner-change period-close alone can RACE a sub-100ms gap; the culprit stamp must consult the
  ActorRegister at death-edge/credit time (the register held 451 unambiguously by then). Raises
  the fix priority: regular manual play mis-credits, not just auto-battle.
  P3 side-finding (same archive): Zirekile Gafgarion WITHDRAWS at his defeat threshold -- no
  death edge, no victim record, nothing creditable. Withdrawal-style bosses cannot produce a
  Legend credit; the legends table must exclude or special-case them.
- **Console QuickEdit blocks the engine loop** (observed 2026-07-05: selecting text in the Reloaded
  console suspended the mod thread ~3 min mid-battle -- kills/growth/toasts all stall; the census
  "hang" was this). With VerboseLog on, the loop does console I/O constantly, so a stray click can
  stall it. Hardening candidate: async/queued console sink in FileConsoleLogger (file sink stays
  synchronous -- it is the evidence chain). Until then: read livingweapon.log, not the console.
- Remove Treasure Master (OBVIATES the Scholar's Ring idle-nag bug -- do not fix that doomed code).
- Alter Axes and Flails (only cheap slice: Squire/Geomancer equip access on existing sword-typed items).
- Migrate the remaining lossy-detection siblings (Maim/Larceny/Ricochet) to cache + rearm.
- Kill-tally milestones on the equip card beyond the counter (gated on a glyph-render probe).
- Replace the Stormbrand (do AFTER the Samurai signatures lock, to avoid a Slow/element dupe).
- Enemies actually USE living-weapon benefits (XL undesigned feature; static rebalance already lands the real want).

## Walled (blocked by engine / Denuvo / modloader)
- Fix the sword swing-art (art welded to weapon id; the same render node also drives damage).
- Make item TEXT display in French (game + modloader parser walls; DLL live-paint is the only path).
