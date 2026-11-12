# Self test plan: Living Weapons proves its own changes in the game

STATUS: JOURNAL (plan, written 2026-11-11 from a read only pass; owner ruled its four decisions the same day; nothing built)

## The goal

Every change is played in the real game by a script before a player gets it, and nobody has to
watch. Each run leaves a proof pack. The release script refuses to package a build that has no
green pack for that exact build.

## The quality rule (owner, 2026-11-11)

Code quality must not slip. Every change, the self test scripts included, passes the full
pipeline and every gate, including Check.ps1 on every commit, docs only commits too. Never
loosen, skip or disable a gate or a test to get something green. If a gate blocks the work,
stop and tell the owner why.

One gap to close first. This repo has no Check.ps1 today. Its gates run inside BuildLinked,
Publish and CI, and BuildLinked also deploys into the game's folder, which is off limits while
Battle Brain has the game. So build order step 1 is a Check.ps1 for this repo, like Battle
Brain's: every gate, one exit code, touches nothing.

## Checked against the goal

Every step was held against "proven in the game, before players get it, without the owner
watching". What changed:

- Dropped: checking the simulator's hit model against the dodge report. It is an offline model
  and proves nothing in the game.
- Dropped: the tie to the replay test card LW-519. Only the stamp on a tape's first line is
  kept, inside the proof pack. Replaying old bugs offline is a different job.
- Narrowed: the old unproven list. Shipped features become standing fights that every later
  build runs. Ledger rows about research that never shipped are out of scope here.
- Flagged: the glow step. Rings sit on menu screens a self test cannot open, so glow cannot
  meet the goal until there is a keyless way into those menus.
- Flagged: LW-561's own live check is a hand read of the Attack preview. Under the goal the
  dodge count fight replaces it.
- Flagged: the release zip can be built on GitHub, where no game runs. So the proof record has
  to be a small committed file keyed to the commit, checked by Publish wherever it runs.
- Flagged: any change in a "cannot yet" area below cannot ship under the goal. It is called
  out when the change is planned, not when it is ready.

## The rules this plan obeys

1. No UI driving. No key presses, no menu walking, nothing from FFTHandsFree. Battle Brain's
   boot, formation and leave tools are allowed, because they call the game's own routines and
   send no keys (owner ruling below).
2. Fights are driven the Battle Brain way. Its hook sits on the game's own AI decision routine
   and hands each unit its action.
3. Setup is memory writes only, each one read back.
4. One game, two users. Living Weapons (LW) and Battle Brain (BB) take turns, and LW never
   touches the game while BB has it.

## How one self test runs

1. Ask "is the game free?" (the check below). Busy or unsure means do nothing and log why.
2. Take the turn: write the lease, save copies of everything LW is about to change, switch the
   mod list to LW's test set, start the game. LW only ever starts from a closed game.
3. Boot to the save and start a named battle with BB's keyless tools.
4. Set the stage. Gear goes into the party roster before the battle is built, because the game
   fixes some things at that moment (twin weapons, swing art). Stats, HP, tiles, statuses and
   who acts next are written into the battle afterwards.
5. Fight. BB's hook gives each owned unit its action from a recipe file. No model, no cost.
6. Judge against lines written down before the run: LW's log, LW's black box tape, memory
   reads, BB's session file. A video clip is kept for anything a person may want to see.
7. Bait run. The same fight with the feature switched off must fail the same check. A test that
   cannot fail proves nothing.
8. Hand back: leave the battle without saving, close the game, restore every saved copy byte
   for byte, release the lease.

The build under test is the production build, the same file players get. That is the main reason
for the sharing choice below.

## What to share with Battle Brain, what to copy, what to skip

Ruled by the owner: share by running, copy almost nothing. In an LW session the game loads LW, BB's
mod and the fast forward mod. BB's mod is the fight engine, and LW's test scripts call BB's
tools where they sit in the sibling repo. LW's own code gets no test hook, so what is proven is
what ships.

| BB piece | What it does | For LW |
|---|---|---|
| The hook (BrainHook, TurnDriver, plan.json) | Answers the game's "what does this unit do" with a written plan. Plays the player's side, the enemy's or both | Share by running BB's mod |
| Hand mode driver (`driver.py --hand`) | Follows recipe files, no model | Share. LW writes its own recipes |
| `stage.py`, `force.py`, `spawn.py` | Build a battle from one file with memory writes, each read back | Share. LW adds a roster writer for gear before the battle |
| `cast.py` and fight files (pinned fights) | Start a fight, check the game matches the file, void the run if not | Share the tool, keep LW's fight files in this repo |
| `boot.py`, `battle.py`, `game_life.py` | Closed game to a running battle and back, no keys. Includes Retry | Share |
| `arena.py` | Whole batches for win rates | Skip. Copy two ideas: void rules and the freeze watch |
| Tapes (`tape_trim.py`, the tape spec) | A kept session with the game, build and layout stamped on its first line | Copy only the first line stamp, into the proof pack. No code |
| `stamp.py` | Refuses a run when the exe, mods or settings changed | Copy the idea into the proof pack |
| The simulator (`tools/sim`) | Plays battles offline | Skip. It proves nothing in the game |
| Video (`fft_rec`, `fft_sheet`) | Rolling film of the game, read back as frames | Share. It is already outside both repos |
| `fft.py ready` | BB's own "is the game free" | Read its rules, do not call it. It reads the game |

Two things must be proven before anything is built on this:

- Both mods in one game. They have never run together. From the addresses I read they hook
  different routines, and BB finds some of its targets by scanning, so only a launch settles it.
- BB's fight tools under LW's item tables. BB has only ever run on the vanilla item set.

If the first one fails, the sharing ruling goes back to the owner, because the only other road
is a test hook inside LW, which he ruled out.

## The "game busy" check

Three questions. Any yes, or any answer it cannot read, means busy.

1. Is there a lease held by someone else that has not run out? The lease is one small file in a
   folder both repos already use, saying who has the game, since when and until when.
2. Is the game running? LW starts only from a closed game and touches only a game it launched.
   It never closes a game it did not start.
3. Is any Battle Brain runner alive (arena, driver, stage, cast, boot)?

The check reads a file and the process list. It never attaches to the game.

BB does not write a lease today, so between two BB batches with the game closed the PC looks
free when it is not. BB gets one small card for it: take the lease when a session starts using
the game, renew it during a run, release it and close the game when done. Battle Brain's own
session builds that card after its current run ends, and LW sessions do not touch that repo.
Until it ships, LW runs only on the owner's go.

What LW saves on take and restores on release, with the game closed: the mod list, both mods'
settings, the kill tally files and the save folder. The game autosaves inside battles, so
without the last one LW's test gear would be waiting in BB's next Continue.

## What a self test can and cannot prove

Can: the mod starts and every part arms; all 234 item rows in memory match items.json; kill
credit, tiers and growth; each weapon's trick, read from memory and the log; Retry and leave
routes; long battle soak; toasts and banners in battle, from the video.

Cannot yet: anything on a menu screen (the equip card count, glow rings, shop and bag lists),
because opening a menu is UI driving. Hand played turns, where LW has logic that follows the
cursor. Save and load round trips. Other mods installed alongside. A player's machine layout.
These stay with the owner until someone finds a keyless way in, one screen at a time.

## Build order

1. Check.ps1 for this repo: every gate, one exit code, touches nothing. Nothing else in this
   plan can be committed by the quality rule until it exists.
2. No game needed: the lease tool and the busy check with a fake world selftest, the snapshot
   and restore step, the first three fight files with their expected lines.
3. Canary: both mods loaded, one stock battle, nothing scripted. Pass is a clean LW log scan and
   a normal BB session file.
4. First proof: a pinned duel where a unit holding a Living Weapon kills a 1 HP enemy on an
   ordered attack. Expect the kill line and the tally up by one. Bait: a plain weapon, no line.
5. The proof pack and the Publish gate. The pack's record is a small committed file keyed to
   the commit, and Publish refuses without a green one. It never launches the game itself.
6. Turn the shipped but unproven features below into standing fights, most player harm first.
   First targets: LW-465, LW-506, LW-513 and LW-172.

## Owner rulings, 2026-11-11

1. Sharing: "run Battle Brain's mod inside LW tests, with no test hook in LW."
2. "BB's boot, formation and leave tools are allowed. They aren't UI driving."
3. "Add a separate SELF PROVEN mark that carries its proof pack. PROVEN stays my flip, for
   whatever needs eyes."
4. "BB gets a lease card." Battle Brain's own session builds it after its run ends. LW sessions
   do not touch that repo.

## The two marks

- SELF PROVEN means a script watched it. The row or card carries its proof pack: the fight file,
  the lines expected before the run, the log, the tapes, the clip, the bait run and the build's
  hash.
- PROVEN means the owner watched it. It stays his flip alone, for whatever needs eyes.

A SELF PROVEN mark never becomes PROVEN by itself.

## The memory gate (owner, 2026-11-11)

Memory for this repo changes only on the owner's explicit yes. The repo's .claude/settings.json
carries an ask rule on the memory folder, so a write there stops and asks him first, and it
turns background memory tidying off. Proven the same day: the owner saw the permission prompt
on a throwaway test write, which was then deleted.

## Roadmap (owner, 2026-11-11)

Order: balance first as its own small release, then signature batches, then glow.

1. Balance, a small release of its own, version 2.5.0 by owner ruling, since balance changes
   how the game plays. The list parked earlier moves to 2.6.0. LW-561's reading first, then the cards that wait on it
   (Appendix B). Its proof is every changed table row read back from the running game plus the
   dodge count fight, so build order steps 1 to 5 land with this release. The harness is card
   LW-562 on the balance list.
2. Signature batches. The goal is at least three +3 signatures for every weapon type, with no
   consumables (bombs and throwing are out). That is 24 new signatures across ten types, shipped
   in batches of a few types each, with Cloth in the last batch. Every new signature ships with its fight file, its expected
   lines and a bait run, and carries SELF PROVEN before players get it.
3. Glow. The icon rings stay. The battle sprite recolor stays parked.

| Type | +3 signatures today | New ones needed |
|---|---|---|
| Bag | 0 | 3 |
| Book | 0 | 3 |
| Instrument | 0 | 3 |
| Ninja Blade | 0 | 3 |
| Pole | 0 | 3 |
| Gun | 1 | 2 |
| Polearm | 1 | 2 |
| Knight Sword | 2 | 1 |
| Rod | 2 | 1 |
| Cloth | 0 | 3 |
| Total | 6 | 24 |

The counts come from the +3 ability column of docs/living_weapon_grid.csv. The owner added
Cloth on 2026-11-11, which took his first count of 21 across nine types to 24 across ten. One
open point for him: the icon rings live on menu screens, which a self test cannot open yet, so
the glow step needs his eyes or a keyless way into the menu.

## Appendix A: written as done or proven, not confirmed in the game

Sources read: docs/VERIFY_LIVE.md, docs/LIVE_LEDGER.md (all 178 rows, opening text only, folds
not opened), docs/SMOKE_TEST_2.4.0.md boxes, docs/TODO.md. The changelog was only searched for
awaiting markers and had none. It was not read entry by entry.

### Shipped in 2.4.0 with its smoke box never run (20 boxes, by owner ruling on release day)

3.2 each weapon class. 3.4 riding a chocobo. 3.5 a Retry must not bake in a held value. 4.4 the
indirect roads in and out of the bag. 5.4 two fingerprint twins. 5.7 the long battle tape. 5.9 a
guest like Delita. 7.2 glow rings. 7.5 Siren's Lyre. 9.5 Poach on a basic Attack kill. 9.6 the
tier 3 Outrider. 9.7 three katanas. 9.8 each blade's trick. 9.9 each caster weapon's trick. 9.10
each ranged weapon's trick. 9.11 the +3 hit. 10.1 WotL Restoration alongside. 10.2 the game
moving each launch. 10.3 the helper mod missing. 10.4 text for ids 261 to 267.

Two more boxes failed and were shipped as known: 9.2 and 9.4, both now under LW-465.

### Deploy riders (docs/VERIFY_LIVE.md)

| Rider | State |
|---|---|
| LW-259 eviction reserve | Not proven. Its box 5.7 was never run |
| LW-262 cache partition | Not proven. Same box |
| LW-247 icon bytes | Looks satisfied (boxes 1.1 and B13 passed 2026-10-17). Row never removed |
| LW-413 release hotkeys | Looks satisfied (box 2.5 passed 2026-10-17). Row never removed |
| LW-323 stale toast | Changelog records an owner pass 2026-10-18. Row never removed and points at a TODO entry that is gone |

### The older verify rows (docs/VERIFY_LIVE.md)

- Rows 6 to 9, Reliquary phase 1, shipped in 061e36c: Mark toasts, the card story line, the
  undead path (its classifier has never fired) and legends.json. Deferred, never passed.
- Row 3, the Warbrand sprite override, is ticked, but its own text says it never took effect.

### TODO cards marked AWAITING-LIVE

| Card | What is still missing |
|---|---|
| LW-561 dodge report | The model has never been checked against the game. See Appendix B |
| LW-465 rewind release | Committed in ccbc590f. The ten step live test has not been run, and the Retry with a planted Bulwark was never taped |
| LW-513 glow rings anywhere | Pass A passed. Pass B, an install outside the game folder, is the point of the card and was not run |
| LW-506 hook order | Both clean launches are on record 2026-10-24. Only the owner's flip is missing |
| LW-172 chocobos | Owner pass on record 2026-10-24. Only the flip is missing |
| LW-511 swallowed errors (parked) | One normal battle on record. Only the flip is missing |
| LW-459, LW-195, LW-234, LW-326, LW-296, LW-360 (parked) | Built, no live result written on the card |
| LW-550 default address (parked) | Shipped as 2.4.1. Proof can only come from a player's log |
| LW-112 another mod's rewrite | Waiting on a two leg drill since 2026-07-28 |
| LW-320 weapon power audit | Waiting on the owner's sign off on the grid rulings |

### Live ledger rows filed under Proven that are not

- [icon-cache-scoped-hide] says UNCERTAIN, owner flip pending.
- [crystal-countdown-off-switch] says awaiting owner flip.
- [twin-menu-flags-zero-at-map-time] has its menu byte half overturned on 2026-10-18.
- [slot0-quit-stick] is proven for the game before 1.5 only.
- [terrain-grid-cursor-source] has its pathfinding half contradicted.
- 36 rows from June and early July say Proven with no owner flip named in the opening line, and
  they were watched three game patches ago. [charm-survives-break] through
  [flavor-line-overwrite-displays]. Most back shipped tricks, so the 9.x fights re-prove them.

### Rows honestly marked Uncertain that shipped code leans on, by their own words

[card-materializes-from-named-pool] (the whole card paint design). [slot0-battle-phase-values]
(battle start and end). [pe-key-match-kitlane-mismatch-discriminator] (the guard split).
[retry-preserves-credited-identity] and [battle-retry-rewind-fingerprint] (Retry handling).
[turn-flag-holds-through-status-detour], [attack-card-reverts-hover-follower] and
[stale-paint-orphaned-copies] (the Attack card). [per-turn-normalize-repaints-boosted-baseline].
[worldmap-menu-open-byte], [party-browse-screen-byte] and [twin-dualfire-construction-bound]
(twin weapons). [new-game-no-battle-exit-edge]. [mapsave-load-card-sites-verify-dead].
[empty-tile-hand-read-bounds] (split, half applied).

Ready for a flip and never flipped: the three id261 rows, [ct-hold-zero-turn-denial],
[weapon-sprite-pair-drives-swing-art], [inventory-default-order-drops-unknown-ids], and the two
overturn candidates under Walled, [bag-list-261-cap] and [weapon-palette-assignment-walled].

## Appendix B: where the balancing patch stands

Plain version: the ruler is built, nothing in the game has changed yet, and one unrun check
holds up everything behind it.

- LW-561, the dodge report, is built in three stages (23fea2b7, 81f6af0d, 360125a4). One command
  prints how hard each job is to hit, vanilla against the mod. The job table is unchanged, byte
  for byte.
- Its model of the game's rules has not been confirmed in a battle, and the first reading is
  not pasted. The check on the card reads the Attack preview by hand: a Thief in Cutpurse and
  Galewall should read 57 from the front. Near 51 means the model is wrong.
- LW-553 (take shields off seven jobs) and LW-552 (bring enemy dodge down) both wait on that
  reading. The review already suspects LW-553 names the wrong seven.
- LW-554 (hats and robes back to within about 10 percent of vanilla HP) is in the backlog and
  waits on nothing.
- The balance release is version 2.5.0. The list parked on 2026-11-02 for the quality pause
  moves to 2.6.0.

A self test can run the LW-561 check without the preview: order a few hundred front attacks on
the pinned Thief and count hits. About 600 swings tells 57 from 51. That makes it a good second
fight after the first proof.
