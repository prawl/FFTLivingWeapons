# Changelog (work-ledger exits)

STATUS: CONTRACT (machine-checked by TodoContractTests)

Where docs/TODO.md items land when they ship, die, or retract; newest first within a cycle.
Entry first line: `- [LW-<n>] SHIPPED <hash> YYYY-MM-DD: <summary>`, or WONTFIX / RETRACTED
with a date and no hash. New entries are written ELI5-first (a plain-language opening anyone
can follow, technical detail after), per the Format rules in docs/TODO.md; rows written
before 2026-07-21 keep their original prose.

## 2.3.0 cycle

- [LW-90] SHIPPED d780d13 2026-07-21: a battle restart no longer bakes a held stat boost in
  as a unit's natural, and the Iai opening Speed boost now truly ends after the wielder's
  first turn (that second half shipped as the follow-up prong, 4ca396d). The mod remembers
  every boost value it writes (NaturalLedger: per unit and stat lane, across battles,
  level-keyed so an earned level-up point is never eaten by a value collision) and refuses
  to adopt its own leftovers at a fresh battle's first sight; and because the game
  re-paints its boosted baseline every turn (live-proven this day, LIVE_LEDGER row),
  released holds keep re-correcting their own written values for the rest of the battle.
  All six capture-natural holds are guarded (Iai plus the five growth lanes, which
  compounded multiplicatively across restarts); the backlog's roster-clamp candidate died
  in recon (no mapped roster or raw Speed byte exists). Owner live pass 2026-07-21 11:46:
  Ramza went first against faster enemies while his card read natural 11 throughout, the
  release fired at his turn open, and the corrective caught the game's re-paint (14 to 11)
  on tape; the restart leg was proven the same morning on the prior build ("restart
  residue corrected at capture", the 11:03 battle), whose correction machinery the prong
  did not touch. Owner flip 2026-07-21. The roster stayed untouched throughout (the owner's
  out-of-battle card read natural 11 after the residue battles). Residuals banked as
  LW-100 (the mounted lane); rigor trail: a 3-critic adversarial plan review killed the
  naive design, an implementation review found the level-key collision, worktree sabotage
  bit exactly the predicted tests three times, and the owner's first live pass caught the
  fresh-battle gap every desk round missed.
- [LW-42] SHIPPED ef747d1 2026-07-21: the mod can no longer believe a battle ended in the
  middle of a long spell cast or enemy turn (which would silently reset its kill
  bookkeeping mid-fight). Two checks still asked the old game version's question: the
  pre-1.5 battle marker 0xFF never appears on 1.5, so the battle-enter sentinel pair and
  the in-battle excuse covering cast-targeting and enemy-turn frames were both silently
  dead, and the morning log caught the consequence live (a false battle-exit during a
  battle intro, 7.6 seconds after the enter). Both checks now read the 1.5 marker
  (Offsets.Slot0InBattleMarker 0x10, tripwire-pinned; red-first TDD, worktree sabotage
  reproduced exactly the predicted red set). Owner live pass 2026-07-21 (09:04 battle on
  the deployed build): zero mid-battle exits across 138 mode flips, credits normal, no
  warnings, and the victory exit proved the stuck-marker guard live (slot0 held 0x10 six
  seconds past battle end, the debounced exit fired on schedule; banked 1edc124). Owner
  signed off 2026-07-21. The greater-than-4s cast stretch and the post-QUIT value stay
  tracked on the LIVE_LEDGER 1.5 slot0 battle-phases row; both fail safe (a wrong premise
  leaves the excuse dead, the old behavior, never wrongly live).
- [LW-97] WONTFIX 2026-07-21: the player seeing Squires able to learn Equip Axes again on
  2.3.0 is intended behavior, not a broken install (owner call, 2026-07-21). 2.3.0
  deliberately stopped suppressing that ability: the off switch lived in the mod's
  JobCommandData.xml, whose whole-row writeback also erased other job mods' changes (the
  LW-77 collision prune), so the file was deleted and the vanilla learnable Equip Axes
  returned with it. It is harmless dead JP under this mod: every axe is reforged into
  another weapon type, so there is nothing left to equip, and the ability's in-game
  description says exactly that (the ability.en.nxd Description cell on key 460, LW-77,
  2a4c325).
- [LW-80] RETRACTED 2026-07-21: the modloader bug report reached its author another way, so
  the plan to file it as a public GitHub issue is withdrawn. The report itself (one mod's
  table file can silently erase another mod's runtime changes because the loader writes back
  every field of a row, not just the edited ones, with dirty-field writeback as the proposed
  fix) was delivered by the owner to the modloader author through direct contact on
  2026-07-21; no public issue URL exists (the repo's issue list was checked the same day).
  The durable technical record of the mechanism lives in docs/DESIGN.md (the whole-row
  writeback section, LW-79) and this file's LW-77 row.
- [LW-91] SHIPPED 10320b2 2026-07-21: battle menus no longer wear the previous unit's
  weapon name or an old kill count. The mod paints weapon names and kill tallies over the
  game's own menu text; when a routine recheck of a painted spot failed even once, the mod
  instantly forgot the spot and spent up to half a minute re-finding it, and during that
  blind window the old paint stayed on screen (the reported wrong-name menus and frozen
  counts; the 2026-07-21 04:35 log caught a battle ending with two such orphaned spots). Now
  a spot that fails its recheck is kept under watch with nothing written while unsure: spots
  that come back (the common case, the log showed the same spot count re-found after every
  blind window) are corrected within about a second, and truly dead spots are dropped after
  a few seconds with zero writes and the replacement search already running. The equip
  card's Kills meter also updates on kills mid-battle now (this hash), instead of waiting
  for a pause-menu visit. (Tech: per-Hit FirstFailMs strike retention in
  AttackCard.RepaintAll, evict at Tuning.AttackCardEvictAfterMs, early census arm on the
  episode edge gated on !_scanning, eviction rearm unconditional, eviction taped on the
  flight "card" record with the label address; restores remain gated on a verified label
  anchor so the lost-buffer zero-writes rule stands (the plan review killed a
  restore-under-failed-label variant as a heap hazard); Display.PaintCountsIfChanged
  mirrors the count-change edge incl. RecomposeChanged and RequestRescan, wired as the else
  of Engine's ShouldPaintCard gate; retention core in 5136f2e. Adversarial verify SHIP 9/10,
  both sabotages bit the predicted tests. Owner live pass 2026-07-21 07:24 battle: renamed
  row, Fists compose, clean turn-change reverts, mid-battle count update, zero stale
  sightings; the stochastic eviction trigger did not fire in that battle, so the retention
  path itself is sabotage-proven logic that fails safe to the old behavior and
  self-documents in normal play via the "retained pending recovery" line and the taped
  eviction addresses.)
- [LW-98] SHIPPED 5136f2e 2026-07-21: a bare-handed unit's Attack menu no longer shows a
  weapon name left over from another unit's turn. Same root and same fix as LW-91: the
  leftover name lived on a painted menu spot the mod had lost track of; keeping lost spots
  under watch means the unarmed menu's own text (Fists for a human, plain vanilla
  otherwise) reaches every spot again. (Tech: rides the LW-91 strike retention; the Fists
  compose and the fail-closed vanilla doctrine are unchanged. Owner live pass 2026-07-21:
  the unarmed human composed Fists correctly right after tracked units' turns.)
- [LW-88] SHIPPED 5136f2e 2026-07-21: the attack-card kill count no longer freezes
  mid-battle. Subsumed by LW-91 (same root): the count was composed live all along; it
  froze only on painted spots the mod had lost, which the strike retention now keeps
  reachable. Kept its own row for the evidence trail (the owner-witnessed 19 held across a
  battle that credited 7 kills live on the 07-14 tapes).
- [LW-96] SHIPPED 18b983f 2026-07-21: soldiers past number 20 on the party list now earn
  kills, growth, and card text like everyone else. The mod only ever read the first 20
  roster rows while the game allows 50, so a full roster's late recruits (usually the story
  characters) silently got nothing: the exact shape of the 2.3.0 player report "only Ramza
  and generics benefit". The window now covers all 50 rows and deliberately stops there,
  because the rows past 50 hold stale guest copies of real units that would confuse unit
  matching (a cloned Beowulf with identical stats sat there on the owner's save). (Tech:
  Offsets.RosterSlots 20 to 50; bank proven live by tools/probes/roster_span_probe.py;
  boundary tests pin slot 49 seen, slot 50 never scanned, plus a constant tripwire; built
  via build-lite, adversarial verify SHIP 9/10 with both sabotages biting the predicted
  tests. Owner verified live 2026-07-21: a roster slot 46 unit's Sanguine Sword kill
  credited on tape flight_20260721_044145 while veterans credited normally.)
- [LW-99] WONTFIX 2026-07-21: a player reported Nagrarok missing from Beowulf, "turned into
  another sword". Not a bug: this mod deliberately turned that sword into a new one
  (Lightbringer, the sword line's only Holy blade), so Beowulf's famous frog-sword really is
  a different weapon now; the player just could not know. (Tech: item id 31, data/items.json,
  "Repurposed from the retired Living Blade base".) The real gap is communication: renames
  are invisible to players, so "my item vanished" reports will recur; the candidate follow-up
  if they do is a rename table in the Nexus description.
- [LW-60] SHIPPED 19000b1 2026-07-16: the 2.3.0 pre-ship smoke pass, authored 2026-07-11 and
  run to completion by the owner across 2026-07-14 to 2026-07-16. Every row resolved: the pass
  caught four live regressions and shipped their fixes before release (toast delivery, the
  Plague drift hold, the turn-credit lane, the Eagle Eye scope, each exited above), flipped
  the pending LIVE_LEDGER rows to PROVEN on owner evidence, and closed with scan_logs
  --require-battle --flight exit 0 on the final session, the real saves restored over the dev
  lane, the PROD 2.3.0 deploy verified, and the release package cut and content-verified
  (FFTLivingWeapons-2.3.0.zip). The one deliberately open item rides the handoff action pack:
  the backlog LW-80 upstream-issue filing, an owner-account action.
- [LW-95] SHIPPED 8c67ca5 2026-07-16: Eagle Eye no longer hastens Dooms it did not inflict.
  Smoke row 7.10 caught it live the same night (a Mortal Coil Doom proc snapped 3 to 1, tape
  flight_20260716_001721): the tier-3 aura armed on fielded-at-tier alone and the hasten rule
  never saw the inflictor, while the design (living_weapon_grid.csv id 78) scopes the
  shortening to the bow's own May-inflict-Doom procs. The fix tracks a per-enemy doomed
  baseline and hastens only a rising edge observed while the acting main hand is the
  Eclipsebolt during its acted period (the Larceny/Benediction last-actor lane); an
  unattributed edge logs once and is left alone, and the guarded write-down path is unchanged.
  Built via build-lite on the cited proven rows (Doom bytes, the LastPlayerMainHand latch),
  adversarially verified 9/10 SHIP with both sabotages biting the predicted tests
  (hash-verified restores). Owner live re-tested the same night on the 01:06:57 tape: the
  foreign Doom was left alone with exactly one skip line and the bow's own proc forced to 1.
  Accepted residuals, documented in the class doc: attribution is action-level (a non-wielder
  Doom landing during the wielder's own acted period would attribute falsely, charm-tier
  obscurity), and a mid-battle tier-up over a pre-existing foreign Doom could false-hasten
  once (impossible on DEV builds, which seed every tally past tier).
- [LW-94] SHIPPED 5dd5003 2026-07-16: turn credits now resolve flags-first. TurnTracker rode the
  parked actor pointer, which sits on the action TARGET at a caster's acted edge, so a healer's
  own turns never counted (smoke rows 7.14 and 7.27 caught it live: the Mending wielder's three
  acting turns all credited other fingerprints including the heal target on the 17:10:41 tape,
  and the Swiftedge wielder took zero credits on the 17:47:22 tape, so Renewal's turn-edge aura
  and the Afterimage ramp both starved silently); a mid-battle level-up also re-keyed the
  fingerprint and restarted the count. The fix resolves the acting unit from the per-unit turn
  flags (the LW-63/LW-71 Band.FlagOwner pattern), demotes the pointer to fall-through, and
  settles credits into the pre-level-up roster key. Built via build-lite, adversarially
  verified 9/10 SHIP with both sabotages biting as predicted. Owner live re-tested 2026-07-16
  across three post-fix battles: every credit on the 23:57:49, 00:01:35, and 00:06:55 tapes
  rode src=turn-flags, the Mending wielder accrued its own counts with Renewal mending at its
  exact turn ends (row 7.14), the Afterimage ramp stepped +1 per acted turn to its cap (row
  7.27), and Larceny's steal landed with its counter lane advancing (row 7.11). The backlog
  LW-7 auto-battle collapse is this same counting lane and stays open, un-retested under
  auto-battle.
- [LW-92] SHIPPED d75b39f 2026-07-14: the Plague hold now survives mid-battle level drift. Smoke
  row 7.5 failed live (the latch dropped when the victim leveled 95/449 to 96/453 while the pin
  defeated three cures on tape, so the loss was identity-only), and the same-day fix replaced
  the exact-match victim fingerprint with drift-tolerant identity (exact orig brave/faith,
  Band.LevelMatchesRoster up-only level drift, bounded maxHp growth) re-anchored on every
  accepted step; the first adversarial verify caught the re-anchor as test-vacuous and the fix
  round pinned it with a two-step drift-chain test before the 9/10 SHIP. Owner live-verified
  22:10:51 on the identical drift shape: the re-anchor line printed exactly once, the hold
  survived the level-up, and a cure was still defeated after it. Friendly-fire poison stays
  vanilla and curable by design.
- [LW-89] SHIPPED be7e989 2026-07-14: tier-up toast delivery restored (dead the whole post-1.5.1
  era). Smoke row 7.25's positive control caught it: the Chaos Blade tier-2 toast enqueued at
  the exact 25th-kill credit with zero deliver records in any retained tape. Diagnosis in three
  builds the same day: a bounded prompt-head sampler (ed3ce16) showed the hooked entry's rdx was
  garbage; a struct sampler (22611a4) plus live disassembly proved the 1.5.1 re-anchor had
  landed on a dispatch wrapper whose rdx is a flag byte, with the text resolved from a string
  object at holder+0x20 and every branch converging on the true setter at 0x1403F1098 with the
  resolved char* in rdx; the fix (be7e989) re-anchors the hook there, landmark-guarded, with
  the proven pre-1.5 swap semantics and the unchanged "Select a facing" prefix (the 1.5.1
  wording "Select a facing direction and press F to confirm." still matches). Owner
  live-verified 15:55:36: kill number 5 credited to Kiku-ichimonji at 15:55:34 and the banner
  "Kiku-ichimonji has gained its 5th kill and has grown to Kiku-ichimonji+" delivered on the
  facing prompt two seconds later, the first banner since the game patched. The samplers stay
  shipped as permanent bounded observability.
- [LW-77] SHIPPED 2a4c325 2026-07-14: the job-mod collision surface prune. The loader applies
  every listed table-XML row as a whole-row writeback at OnAllModsLoaded (proven live by the
  owner's row-57 ladder with Blue And Red Mages 2.0.2; LIVE_LEDGER Proven row added), so
  JobData.xml now lists only the 28 live-payload rows (contract-test pinned) and
  JobCommandData.xml is deleted outright, its dead-JP Equip Axes protection replaced by one
  ability.en.nxd Description cell on key 460. Owner live-verified 2026-07-14 (smoke row 7.29):
  Red and Blue Mage compose intact on the pruned PROD deploy with zero hand edits, Archer keeps
  its widened Gun access, and the reforged-note description renders in the learn screen
  (screenshot). The Nexus riders (known-issues pin, Old Files supersede, the delete-old-folder
  upgrade note) travel with the ship notes as owner-at-ship work.
- [LW-57] SHIPPED 9d347c9 2026-07-14: the Attack command's first-open readiness after a session
  load. Cause was census cold-start latency, not actor resolution: the sweep could arm and never
  complete across a whole battle while starving RepaintDriver; the fix alternates repaint/scan
  ticks and re-arms aborted sweeps with hit preservation. Owner live-verified 2026-07-14 (smoke
  row 5.3): the weapon name rendered on the very first turn of the session's first battle, and a
  mid-battle weapon swap re-resolved; the LIVE_LEDGER Attack-row rename row flipped PROVEN on
  the same evidence.
- [LW-86] SHIPPED fe30e1f 2026-07-14: the production Scholar's Ring auto-grant killed (finding
  F5 from the LW-10 recon). ScholarRing.Grant compiles to a no-op outside LWDEV (the Tuning
  compile-out pattern), so shipped builds never write items into player saves for the disarmed,
  removal-slated Treasure Master module; dev builds keep the disarm-oracle convenience. Owner
  live-verified 2026-07-14 (smoke row 7.22): ring equipped on a deployed unit produced no marks,
  the disarm line logged, zero grant lines all session, id 260 inventory untouched.
- [LW-69] SHIPPED 9ae454f 2026-07-14: the census-evict log flood silenced. The two attack-card
  "evicting the cached copy" DBG lines were 98.4% of the 2026-07-11 session log (9,024 lines in
  2.5s), caused by per-candidate census rejection logging, not evict thrash; SyncHit/SyncHitEnc2
  return a SyncOutcome and rejections aggregate into the census-finished line's rejected count.
  Owner live-verified 2026-07-14 on the first PROD session (smoke row 4.4): census-finished line
  carried "8191 candidates rejected", zero evicting lines, 224-line session log with no
  dominating line class.
- [LW-75] SHIPPED f91e0d2 2026-07-14: the demoted coverage line now reaches the console once on
  the armed rise. Owner live-verified 2026-07-14 on the first PROD session (smoke row 4.3): the
  battle-1 "All 4 enemies are accounted for" line went out at INFO tier exactly once and matched
  the field, while the no-tracked-weapon battle's line correctly stayed file-only (the designed
  demotion).
- [LW-79] SHIPPED 2a4c325 2026-07-14: the stale DESIGN.md compose claim. Section 3 claimed clean
  compose with Blue/Red Mages ("no interaction", written 2026-05-30, two days before JobData.xml
  existed); three player reports and the loader's whole-row writeback (pinned from source, proven
  live by the 2026-07-14 owner ladder) contradicted it. The claim now states the mechanism and
  that jobs-mod compose holds only since the LW-77 minimal-table prune, which shipped in the same
  commit (2a4c325); LW-77 itself stays in Now awaiting its smoke row 7.29 compose re-check.
- [LW-78] SHIPPED b9777d6 2026-07-14: the stale-nxd re-diff and rebase. The loader applies an
  nxd override per-cell against the RUNNING game's vanilla, so the pre-1.5 full-table bakes
  silently reverted every text cell the 1.5.x patches changed: 111 cells measured (61 ability,
  the whole 1.5.x ability-text delta including the Mighty Guard to Thunder Breath dragon fix;
  50 item: the shield and armor menu re-sorts, the Leather Helm hat-to-helmet
  recategorization, the deleted Moonblade dupe row 254 resurrected). Premise proven live by
  the owner BEFORE the rebake: the stale Padded Coif equip card read Hat where 1.5.1 vanilla
  reads Helmet (shop screenshot 2026-07-14). Shipped as tools/audit_nxd_bakes.py (91b230b: an
  intent-classified audit against a fresh pac extract, red on any UNINTENDED or DRIFT cell,
  reruns after every future game patch; now cited in PATCH_REANCHOR.md) plus
  tools/rebase_nxd_pristine.py and the rebaked bakes (b9777d6): all 111 cells adopt current
  vanilla, designed cells survive byte-for-byte (DRIFT 0), the ability bake is vanilla plus
  exactly the three Barrage cells, and the closure was independently re-derived twice from
  primary sources. Deliberate hand edits now live in tools/lib/bake_intent.py with cited
  reasons (the Sanguine Gauche 1001 badge, Warbrand non-random, three known-good display
  flags on repurposed rows, the cap-break row 261). The one-time working/nxd_th snapshot
  proved to be an old bake of this very mod, not vanilla, so intent is derived from
  items.json, never snapshot-compared. In-game text eyeball folds into the
  SMOKE_TEST_2.3.0.md text rows. Suite 2524 green; analyze exit 0.
- [LW-84] SHIPPED 008dd35 2026-07-14: the ReleaseScopeContractTests gate. docs/RELEASE_SCOPE.md
  and docs/archive/SMOKE_TEST_2.3.0.md go under contract test (the TodoContractTests enforcer pattern):
  an IN box whose cited ids all shipped (none still open) must be ticked; a ticked box in
  either doc citing only still-open work goes red; every tick cites a commit hash or ISO date;
  every cited LW-id must exist in TODO.md or CHANGELOG.md; checkbox lines are shape-checked
  (exactly one well-formed box per line); parser sanity floors keep the rules non-vacuous.
  A "backlog LW-n" citation is a deferral pointer exempt from tick logic (the ticked Murasame
  deferral box stays truthful) and WONTFIX/RETRACTED ids never force a tick; the must-tick rule
  deliberately skips the smoke doc, whose boxes are owner live re-verifications. Landed with
  the one-time annotation pass ticking 14 already-shipped scope boxes with git-verified hashes
  (sections 3-5 and 7-9; section 2 stays for the owner's 8.8 sweep per its own prose), so the
  gate was born green on a truthful file; from now on the commit that ships a scope item ticks
  its box in that same commit and smoke row 8.8 becomes re-verification. Build-lite pipeline:
  red-first drift inventory (4 shipped-but-unticked boxes, 3 provenance-free ticks, 1 merged
  checkbox line), independent verifier SHIP 9/10 with two hash-proven doc-sabotage non-vacuity
  checks. Suite 2524 green; docs-only gate, no runtime surface, live pass skipped by design.
- [LW-82] SHIPPED e77b9d7 2026-07-14: the AnchorScan verifier scout (the v1 slice; merge f701795).
  A dependency-free single-file AnchorScan core (chunked pin-neighborhood signature scan,
  overlap-safe boundary math, alignment-before-Confirm filtering, fail-closed verdicts: found at
  pin / found elsewhere / ambiguous / not found) plus the AnchorScout adapter: after any
  LaunchGuard stand-down the mod re-finds the JobCommand table (rec8/rec9 pair signature,
  file-baked image data, needs no save) and the roster base (nameId shape + 0x258 stride
  structure + %8 alignment; calibrated live 2026-07-14: 11,869 raw hits, 766 shape candidates,
  exactly the pin survives) and logs a re-find inventory plus the inventory-count sibling
  prediction: the starting map for docs/PATCH_REANCHOR.md Phase B. Verifier scout only
  (owner-locked): zero writes, no arming, no self-heal; consumers keep the Offsets pins. Premise
  probe tools/probes/anchorscan_feasibility_probe.py; two LIVE_LEDGER rows await the PROVEN
  flip on the drill evidence. Full /build pipeline: 4-reviewer plan panel, TDD implementer,
  independent verifier SHIP 9/10 with a SHA256-verified sabotage proof and forced rebuild;
  suite 2489 green (+20 tests incl. the source-scan portability contract). Owner live drill
  2026-07-14: drill-tagged title-screen stand-down, jobcommand found at pin pre-save, honest
  roster not-found, post-save upgrade with the sibling line and the summary (2 at pin, 0
  elsewhere, 0 ambiguous, 0 not found), then a marker-free relaunch armed with zero scout lines
  and scan_logs CLEAN. Later tiers exit to backlog LW-85.
- [LW-83] SHIPPED 656a832 2026-07-14: guard stand-down artifacts self-diagnose. Landmark probes
  return a LandmarkReading (verdict plus mismatch detail), so the flight "guard" record and the
  startup Error line carry observed-vs-expected values for every landmark mismatching on the
  deciding tick (PE build key: both u32 fields in hex; byte signatures: both hex windows;
  roster row: the observed nameId/sprite/brave/faith; JobCommand: only the mismatching recs).
  A drill-forced stand-down self-identifies by naming LW_FORCE_FINGERPRINT_MISMATCH in both
  artifacts; production compiles the trigger to const false. The drill gained a marker-file
  lane (DrillTrigger.cs, always compiled): env variables set at launch never reach
  fft_enhanced through this box's launch chain, so a file named after the flag in the mod dir
  also triggers, and BuildLinked's clean step wipes it on every deploy. The stand-down notice
  carries owner-authored copy. FingerprintGuard.cs stays a one-file zero-dependency portable
  core; LaunchGuard split into lifecycle plus Landmarks partials. Owner live pass 2026-07-14:
  drill A stood down with the drill tag and both value pairs in the log line and the standdown
  flight archive (parse_flight verified, message box clean); drill B happy path armed with
  zero drill traces and scan_logs CLEAN exit 0. Build-lite pipeline, independent verifier SHIP
  9/10 with a sabotage non-vacuity proof; suite 2469 green.
- [LW-81] SHIPPED 1289fa1 2026-07-14: the mod is re-anchored for game 1.5.1 (with 0cd2f11, the
  companion data-layer commit; Steam
  buildid 23901820, exe 2026-07-13; the fingerprint guard's first real-world catch fired
  pe-build-key on the owner's first post-patch launch, save untouched). The live layout audit
  (docs/research/PORT_1.5.1_OFFSETS.md, method docs/PATCH_REANCHOR.md, banked as a living
  contract during this arc) found the entire 1.5 address layout survived except TWO movers:
  SubmenuFlag (data, moved -0x52, found by a consistency-sampled 3-state solve) and the
  FnSetTextString prompt-hook entry (code region slid -0x4C, leaving the 1.5 entry a
  mid-function branch target; the detour corrupted the function and crashed the game twice on
  engaging auto-battle). Fixes: SubmenuFlag and the LaunchGuard PE key flipped in one commit
  (0cd2f11); the hook entry corrected to 0x14028F750 and every detour now landmark-guarded
  (HookLandmark.cs, dependency-free portable core; PromptSwapHook.ShouldArm fail-closed,
  sabotage-proven) so a future code shift refuses with one Warn instead of crashing (1289fa1).
  PauseFlag kept its address but narrowed to card-only semantics on 1.5.1. Owner live pass
  2026-07-14: forced-mismatch drill stood down cleanly, then a normal launch armed, the hook
  installed (landmark passed), auto-battle ran crash-free, one battle credited 3 kills with
  victim identity (ArrayBase proven), and scan_logs --require-battle --flight exited 0. The
  toast SWAP payload proof rides SMOKE_TEST_2.3.0.md row 7.25 (dev seeding leaves no tier-up
  to fire); Treasure Master stays auto-disarmed on 1.5.1 pending the LW-10 removal decision.
- [LW-41] SHIPPED 77010b0 2026-07-11: probe sentinel addresses come from Offsets.cs instead of
  hardcoded pre-1.5 copies (sentinel_probe fed garbage sentinels into the LW-40 live incident).
  tools/lib/offsets.py extracts the named constants textually (pure selftest + a shape check of
  the real file, 88 constants); sentinel_probe gained --selftest and address-annotated output;
  the six sibling probes carrying the stale sentinel set (clone, crystal_counter, feign,
  formation_diff, turnteam, roster_loss_trace) now resolve through the helper, and turnteam
  warns loudly that its remaining COND_BASE/ACTED/MENU_CURSOR anchors are still pre-1.5.
  Verified by selftest, address equality with Offsets.cs, compile checks, and a no-game run
  resolving cleanly; the next real probe use doubles as the live sanity read.
- [LW-62] SHIPPED 474d494 2026-07-11: Wielder.Roster.cs's six hand-rolled occupied-slot roster
  walks now ride one shared seam. TryOccupiedSlot centralizes the slot base arithmetic and the
  occupancy filter (level read first, sequencing preserved) so the addressing and the occupancy
  rule cannot drift apart per caller; CollectHands centralizes the sentinel-filtered hands
  collection TryResolve and HasLiveWielder duplicated verbatim (and drops its per-slot temp-array
  allocation). Pure refactor, zero signature or contract changes, no test edits; verified
  equivalent loop by loop against the prior revision, non-vacuity by double sabotage (occupancy
  inversion fails 22 WielderTests; off-hand-append neutering fails the off-hand resolve pin).
  Build-lite verify SHIP 9/10. Suite 2434 green.
- [LW-70] SHIPPED 97549cc 2026-07-11: a dev build's first post-reset kill no longer swallows its
  first-blood toast. The out-of-battle tally clear (LW-51's PlaythroughReset) left BannerToast's
  construction baselines stale, so the first crossing after a new game read as a rollback; the
  constructor's prime is now an explicit Rebaseline (pure snapshot refresh, never enqueues,
  loop-thread-only maps) called on the reset detection edge beside the LW-59 Display.Invalidate.
  Production behavior unchanged in practice (no seeding; the prod curve cannot be jumped in one
  change). Three failing-first tests incl. a kept contrast pin of the pre-fix swallow;
  build-lite verify SHIP 9/10 with a no-op-sabotage non-vacuity proof. Suite 2434 green. The
  in-game dev-smoke observation (first kill after an in-session New Game toasts) folds into the
  next dev smoke.
- [LW-46] SHIPPED a1c643b 2026-07-11: the Galewind card no longer promises "No Lucavi"
  (IsDominatable is allow-everyone by design, owner request 2026-06-18, so the card
  overpromised). Of the two open candidates the reword shipped, the path RELEASE_SCOPE
  section 2 mandated regardless; the gameplay-changing Lucavi carve-out stays unbuilt (the
  items.json note keeps the restore recipe). The "3-turn cooldown" wording is accurate
  (PuppeteerCooldownTurns=4 global turns = the dominate turn plus 3 blocked turns). p3Desc and
  the grid CSV moved in lockstep, item.en.nxd rebaked (old clause absent from the baked bytes,
  new line present), the items.json note shed its stale wielder-clock expiry and CARD/CODE
  MISMATCH claims, and RELEASE_SCOPE section 2's lock-time paragraph was corrected to the
  shipped LW-5 own-turn release. The in-game card eyeball rides SMOKE_TEST_2.3.0.md row 3.2.
  Suite 2431 green, analyze exit 0.
- [LW-22] SHIPPED c7104b9 2026-07-11: the launch header's save lines pluralize their counts (no
  more "1 Marks"; the kills/weapons counts in the same two lines got the same treatment). The
  two lines moved into a pure LaunchHeader composer riding BattleSummary.Plural so five
  failing-first tests pin the singular, plural, and zero forms, and the LOGGING.md launch-header
  example that faithfully showcased the bad grammar reads "1 Mark" now. Suite 2431 green.
- [LW-74] SHIPPED c494faa 2026-07-11: PORT_1.5.md's Appendix E inventory reconciled with the
  post-Offensive-Chemist table set: the grenade ItemData rows 246-252, the removed
  ItemConsumableData.xml, and the ability.en.nxd grenade learn-names 374-378 (all gone since
  a5ea61e) no longer appear as shipped artifacts; a dated note records the reconciliation.
- [LW-34] SHIPPED 91593d0 2026-07-11: the "All N enemies are accounted for" line counts only the
  enemies actually fielded, closing the systematic over-count (owner repro "All 8" in a 4-enemy
  battle). Root cause: encounters define conditional-spawn variant rows whose phantom seats carry
  sane stats, full hp, and real tiles, so EnemyOracle's array capture counted them; only
  scheduler participation discriminates them (tape-evidenced: phantom seats read band CT slam
  +0x25 frozen 0 and turn flag +0x19C never exactly 1, never move, never die). Fix: two additive
  evidence sets fed from the existing ScanCorpses band walk: MarkFielded (slam nonzero or turn
  flag ==1, real position, 3 consecutive ticks) and MarkDead at the dead-edge stamp (a died id
  counts as found without band visibility, the crystallize/chest case); CheckCoverage counts only
  evidenced identities, defers silently on an empty count, and latches only on two consecutive
  checks agreeing on the total (evidence comes from the same band the check reads, so a
  first-pass latch would freeze a partial count). The `_enemyIds` kill-credit gate and the
  CoverageDone/BattleCensus trigger are untouched by construction. Live pass 2026-07-11 (14:24
  battle, owner eyeball): 11 identities captured, 5 excluded as never-scheduled, "All 6" reported
  with 6 visible, zero unseen-enemy warnings; probe data agreed (exactly the 6 counted seats ever
  showed slam movement). LW-75 opened for the pre-existing facelift race that keeps the line off
  the console. Suite 2426 green (17 new EnemyOracleTests).
- [LW-72] SHIPPED ba5e0fc 2026-07-11: the three section-5 doc-and-hygiene leftovers from the
  2026-07-11 release-remainder audit are closed. The README gained a player-facing Language
  support section (non-English players get the full gameplay: rebalance, growth, signatures;
  item text, the equip-card Kills counter, and the in-battle toasts are English-only readouts,
  the toast bullet added after an adversarial review pass flagged the PromptSwap
  English-prompt dependency as a third undisclosed surface). data/items.json id67 Warbrand no
  longer carries the dead spriteIdOverride:1 (VERIFY_LIVE row 3 marks the override DEAD;
  ItemData.xml regenerated with only the SpriteID line gone, analyze exit 0). docs/LOGGING.md
  no longer calls the removed chemist grenades slated for eventual removal (they left the repo
  with the Offensive Chemist removal, a5ea61e; only Treasure Master remains slated, LW-10).
  Owner eyeball of the release note rides the ship gate. Suite 2406 green.
- [LW-71] SHIPPED c2965ce 2026-07-11: the Iai opening-turn Speed hold no longer false-releases
  when the engine actor pointer parks on the struck wielder before its opening turn (the
  ActorPtr-dwell trap: a parked arrival read as the S1 release signal, and the striker's acted
  edge as S2). Every release is corroborated against Band.FlagOwner (the LW-63 per-unit PSX
  turn-flag primitive): the flag owner being the wielder confirms the release regardless of the
  pointer (also closing the old stale-equal starvation corner), a flag owner verifiably naming
  another unit refuses it even when the legacy pointer signal fires, and an indeterminate read
  (the tape-verified zero-t battle-opening record) falls through to the legacy signal unchanged
  so release is never starved; the wall-clock cap stays the backstop. A flags-confirmed release
  restores Speed to the flag owner's entry (the old acting-entry restore would write the
  parked-on unit's Speed byte when the pointer is elsewhere), and the release log line names its
  source (turn flags / actor pointer / cap). Closes the RELEASE_SCOPE section-2 Iai harden box
  and the surviving half of the section-5 falsified pointer-presence deletion. Owner
  live-verified 2026-07-11: the opening-turn release fired "released by the turn flags" with a
  clean session log scan; the struck-pre-turn repro rides the LW-60 smoke pass. Suite 2406
  green.
- [LW-63] SHIPPED be0e4cc 2026-07-11: a kill no longer credits whichever living weapon the engine
  actor pointer happens to be parked on (the 2026-07-10 repro: Ramza killed with the Chaos Blade
  while Wilham's fielded Warbrand claimed it, the pointer parked on the wrong player). All three
  credit sources (the live latch, the death-edge stamp, and the global delayed-culprit arm) now
  key the acted-period resolve on the per-unit PSX turn flags (band +0x19C/D/E): Band.FlagOwner
  walks real-position candidates for exactly one turn-open flag reading 1 and refuses on ambiguity
  (mirror-seat twins stay harmless under the real-position guard); ActorResolver gained
  flags-first preambles (the latch keys on t==1, the stamp on t==1 && a==1, matching the live
  observation that the per-unit a byte lags the global acted edge); KillerStamp gained a
  flags-first hypothesis lane whose bury stamps read UntrackedReason.TurnFlags. The flag bytes
  are not boolean (moved reads raw 3), so every key tests ==1; battle-opening acted edges can
  read all-zero flags, so every low-confidence outcome falls through to the register/turn-queue
  chain unchanged (that fall-through is load-bearing), and the delayed-culprit arm was fixed
  transitively (test-pinned, no code change). Owner live-verified 2026-07-11 on two tapes: a
  manual two-unit battle with the pointer parked on an enemy frame for 3.3 minutes credited the
  true killer (latch src=turn-flags), and an auto-battle credited all five kills correctly,
  proving the flags rise under auto-battle (direct LW-7 fuel). Merged 23429c9; suite 2394 green.
  The exit commit removes the temporary TurnTracker.EmitTurnFlags flight tap, its test pin, and
  its LOGGING.md passage.
- [LW-59] SHIPPED fbf59ce 2026-07-11: a stale +N name suffix no longer survives the in-session
  new-game tally reset on the equip card (owner read "Claymore+3" over a provably empty tally
  while the same card's Kills meter correctly read "0/1 to +"). Root cause was a coverage hole,
  not a paint hole: the kills meter has guaranteed total pool coverage (CoversAllMeta refuses
  to retire the sweep until every id has a kills site) but suffix sites were registered only
  for the two mirror targets plus an 8-id SuffixRotation slice whose covered set persists
  across Display.Invalidate, so post-reset pool rescans re-registered almost no suffix sites
  while the painted "+3" bytes persisted in the very pool text the card materializes from (the
  painter was always downgrade-capable: the tier-0 suffix is the baked vanilla two-space
  state). Fix: pool-path OnChunk searches suffixes for every tracked id in chunks that carry
  kills hits (the whole-heap sweep keeps the rotation slice, pinned by test),
  CardSites.MaxSites grew 768 to 2048 so full suffix coverage is never refused at the cap
  (~701 live kills sites pre-fix), and Engine invalidates the display on the new-game
  detection edge (a main-menu New Game fires no battle-exit edge, so it previously kept the
  stale pool text). Full plan-review-implement-verify cycle; non-vacuity by break-and-restore
  (forcing the pool path back to the rotation slice fails the three new coverage tests). Owner
  live-verified 2026-07-11: the post-reset opener card shows the plain name beside a fresh
  meter, coverage re-latched 15s after the reset with no cap refusals and no engine stall
  (4 pool regions), and the suffix climbed again at the first real battle (Vagabond+ then
  Vagabond+2). Suite 2370 green.
- [LW-56] SHIPPED b6b234f 2026-07-10: the new-game opener crediting arc. Fault 1 (the mis-credit:
  a stale identity bridging an in-session new game credited a weapon no fielded unit wields) shipped
  earlier as the forced new-game exit edge plus the no-live-wielder credit gate (a4d6e33). Fault 2
  (credit the scripted Orbonne opener kills) was found STRUCTURALLY UNBRIDGEABLE and accepted as
  uncredited: the opener fields scripted stand-in units whose live identity (canonical nameIds like
  2/23/52, pre-leveled brave/faith, ENTD weapons) diverges from the fresh level-1 roster on every
  dimension, so no nameId, fingerprint, or weapon match can connect them to a roster row (owner
  live-confirmed 2026-07-10, tape flight_20260710_201535). The canonical fingerprint-and-weapon
  rescue built for it (ActorRegister.RescueCanonical) ships anyway as SAFE: it lives strictly
  behind the zero-roster-match gate, so a real recruited unit bridges Player directly and never
  enters it, making the rescue strictly credit-additive (it can add a credit, never suppress or
  redirect a real one). A four-analyst audit confirmed only scripted stand-ins and guests reach the
  rescue, neither of which can hold a player-chosen living weapon; the one new surface is a narrow
  guest weapon-key over-credit, tape-visible and largely blocked by the live-wielder gate.
  Suite 2369 green.
- [LW-68] SHIPPED b6b234f 2026-07-10: a real player kill was silently blocked as a duplicate when
  the victim's maxHp shifted within its life (the 3-tuple swap detector does not track maxHp, so
  the alive-edge belt was stamped under the old maxHp and the death tuple read as an absent entry,
  which the block misreported as "already credited" in a battle that credited nothing; owner live,
  tape flight_20260710_064433). The alive-edge block now splits absent from false: an
  oracle-confirmed, seen-alive enemy whose edge was orphaned by a maxHp shift credits (reason
  orphan-alive-edge), while a genuinely resolved identity still blocks with honest wording
  (reason=identity-already-resolved). The absent-rescue arm is self-contained (it does not consume
  the global delayed-culprit latch, so it cannot steal a charged action's credit) and the shared
  alive-edge is cleared only on an actual credit, so a fully refused credit no longer blocks a
  later same-tuple wielder-backed kill. Full plan-review-implement-verify cycle, non-vacuity by
  break-and-restore. Suite 2369 green.
- [LW-55] SHIPPED e774405 2026-07-10: the in-battle Attack card no longer shows another weapon's
  kill count. Root cause: the cursor resolve named a roster row and read its formation main hand
  with no cross-check against battle truth, so a wrong or stale row (scripted opener loadouts,
  the hover-following turn-queue struct) keyed the shared tally with a different weapon id; the
  observed "Kills: 100" was that other weapon's real count in the then-global kills.json. The
  resolve now returns raw facts (CursorAnswer) and AttackCard applies CursorGate before
  composing: the matched band entry's PSX turn flag must read 1, then the roster main hand must
  agree with the band entry's own equipped weapon, sentinel-normalized; any refusal composes
  vanilla and writes one "card" flight record per key per battle (a weapon mismatch also warns
  once; a not-turn-owner refusal stays at Debug because cursor hover is routine). Both gates are
  narrowing-only: they can turn a composed row into vanilla, never invent a dossier. The PSX
  turn-flag trio moved to Offsets with provenance (3a8bf6d). Owner live-verified 2026-07-10:
  attack and equip cards agree on a manual turn, hover targeting never swaps the dossier, and
  the new-game opener shows the true weapon; the auto-battle premise check stays open (worst
  case is a vanilla card during auto-turns). Suite 2311 green.
- [LW-53] SHIPPED c906d60 2026-07-10: a fingerprint-guard stand-down now leaves a durable
  black-box archive instead of flushing an empty ring (the 2026-07-07 drill observation: every
  tapped subsystem is gated off pre-arm, so the FlushOnce error flush drained nothing).
  LaunchGuard records the guard lifecycle into the flight ring through recorder/requestFlush tap
  delegates: the armed edge records one guard entry (it rides the next battle flush), and
  StandDown records the failing landmark diag then requests a dedicated standdown flush as its
  last step. The dedicated trigger bypasses the error FlushOnce latch, which an earlier unrelated
  error can burn while the ring is still empty (battle-edge flushes never fire pre-arm, so the
  guard record would strand forever), and it names the archive flight_*_standdown.jsonl. No
  game-memory write path is touched; writes stay disarmed through a stand-down. Live-verified
  2026-07-10: the forced mismatch produced the loud line, the OS notice, and a one-record
  standdown archive naming pe-build-key; a clean relaunch armed normally and the battle-start
  flush carried the armed record. Suite 2284 green.
- [LW-4] SHIPPED b8f6741 2026-07-09: Kiku-ichimonji id45 ships Mushin, the one-shot stillness
  charge: a full WAIT turn (no move, no act) arms one PA-boosted hit (PA held at
  round(natural x 2.05) at tier 3, about 1.6x a normal +3 swing), spent on the wielder's next own
  action. The trigger reads the engine's own per-unit turn bookkeeping, mapped from the FFHacktics
  PSX struct and probe-confirmed live the same day (band +0x19C menu-open flag falling edge,
  +0x19D moved, +0x19E acted, both engine-reset at next turn open; tools/probes/
  mushin_wait_probe.py). Four earlier same-day designs (CT state machine, TurnTracker round clock,
  enemy-CT median, action-record-confirmed latch) each failed live on attribution noise and are
  retired; their forensics live in the Mushin.cs provenance doc and the memory ledger. Owner
  live-verified (BANK on a still wait, SPENT on the strike); card text rebaked into item.en.nxd
  (052bb12); suite 2277 green.
- [LW-51] SHIPPED bf351db 2026-07-09: kill-tally scoping and mod-update survival. The save files
  (kills.json, legends.json, gunslinger.json) moved out of the deploy mod dir into the update-safe
  Reloaded User/Mods/[ModId] folder (the directory Config.json already lives in, which a mod
  update never touches) via SaveLocation's one-time copy-only migration of each legacy file (never
  delete, never overwrite, fail-soft). A NEW GAME now resets the tally: PlaythroughReset detects
  the Orbonne opening dialogue held for a sustained tick window (a one-frame EventId dip from a
  Continue load can never trip it), archives the current kills.json, and clears the shared
  KillTally instance in place, so a fresh playthrough no longer starts pre-maxed. Owner waived the
  formal live pass (relocation proven incidentally across a dozen deploys); the reset then proved
  itself on the 2026-07-10 opener tape (kills.2.json archived, the battle's first credit logged as
  kill number 1). A real cold-launch New Game eyeball rides the LW-60 smoke test; Tier-2
  per-save-identity isolation (two ALTERNATING playthroughs still share one tally) is deliberately
  deferred to LW-61.
- [LW-29] SHIPPED bf351db 2026-07-09: the release question is answered by removal: player save
  files no longer live in the mod folder at all, so a Reloaded mod UPDATE (2.2.2 to 2.3.0)
  replacing that folder cannot wipe the tally. The relocation ships with a one-time
  non-destructive migration read of the old location, exactly this entry's ask (mechanism detail
  on the LW-51 row above).
- [LW-37] SHIPPED 7830def 2026-07-08: the equip-card Kills meter is painted by a pool-anchored
  in-place write instead of the whole-heap Display sweep. The card re-materializes its description
  from stable UE string-pool regions, so PoolLocator finds every writable region holding a baked
  entry (a "Kills:" hit with the owner weapon's name adjacent) and PoolPaint writes the live count
  in place through the existing OnChunk/CardSites path, then skips the sweep once every tracked
  weapon is covered. Each write is name-gated, foreign-refused, and Writable-checked (the
  transient render copies are excluded; painting a non-source baked copy is harmless), gated by
  Tuning.PoolPaintEnabled; CardScanner, ChunkReader, and CardSites are reused verbatim. Merged as
  4afce70; live-verified 2026-07-08 in a DEV build, and the 2026-07-10 opener tape shows the
  repaint running post-reset (701 sites). The stale-count question on this surface (a painted 3
  outliving the LW-51 reset) is tracked as LW-59.
- [LW-52] SHIPPED 50ae6b3 2026-07-07: removed the player-facing config toggles. The Reloaded
  launcher now exposes only Treasure Master Always On; BannerToasts, DevSeedKills, and VerboseLog
  were deleted from Config.cs so players cannot switch off designed behavior. Their runtime keeps
  its compiled defaults: toasts always on (Engine falls back to Tuning.BannerToasts), dev-seeding
  governed by the LWDEV compile flag, and the console pinned to Info (the log FILE still records
  every line; a dev raises ModLogger.LogLevel in Mod.cs for Debug on the console). A reflection
  guard (ConfigSurface_IsExactlyTreasureAlwaysOn_LW52) fails if any removed toggle reappears. Owner
  spared TreasureAlwaysOn per direction; live-verified 2026-07-07 (the launcher shows the single
  toggle). Suite 2213 green, both build flavors clean.
- [LW-54] SHIPPED 2d8f2b9 2026-07-07: the verify-time log scanner (tools/scan_logs.py). Reads the
  newest livingweapon.log from the deployed mod folder (resolved like BuildLinked) and exits
  nonzero on runtime trouble: any [ERROR] line, a fingerprint-guard stand-down, or a
  played-a-battle-but-never-armed state; WARN never fails it. Flags: --mod-dir, --flight,
  --require-battle, --allow, --quiet, --selftest (36 self-test cases, the repo idiom since there is
  no pytest). NOT a build gate (the build never runs the game): BuildLinked runs it before each
  deploy as a non-blocking report on the outgoing session's log, captured before the clean wipe and
  printed from the finally block so a dirty session is the last thing on screen; VERIFY_LIVE.md
  documents the manual run as a live-verify session's closing hard-fail gate. Hardened by a
  five-lens adversarial pass (empty --allow no longer blanket-suppresses, --quiet is silent on a
  clean scan, a line-one UTF-8 BOM no longer hides a first-line error).
- [LW-50] SHIPPED 0152cf9 2026-07-07: the startup fingerprint guard. Before any game-memory write
  arms, the runtime verifies three data-only landmarks (the PE build key, the JobCommand table's
  rec 8/rec 9 ability-byte signature gated on a populated roster, and Ramza's roster-row shape at
  RosterBase slot 0) with retry-until-decidable arming and a 30-tick consecutive-mismatch
  debounce. A confirmed mismatch permanently disarms every write path for the session (a volatile
  Mem.WritesEnabled gate inside the WriteBytes/W8/W16 funnel, an Engine tick gate, and a deferred
  lock-protected PromptSwapHook arm handshake), logs one loud stand-down line, and raises a
  once-per-session OS message box (StandDownNotice.cs) with plain-language guidance and the
  support email. FingerprintGuard.cs is the dependency-free portable core (copy the file to adopt
  in sibling mods). The player-facing force-mismatch config knob was removed in 81fcb79; the dev
  drill is the LW_FORCE_FINGERPRINT_MISMATCH environment variable in DEV builds. Live-verified by
  the owner 2026-07-07: a normal launch arms after save load, the forced mismatch stands down
  with zero writes through a full battle, and the box renders exactly once.
- [LW-25] SHIPPED c842ba1 2026-07-07: the DEV-only ShowSpike research instrument still armed its
  commit-tap in dev builds, spamming a "show-spike: commit-tap ..." line on every text commit when
  its F5 window was tripped (owner hit it mid-testing). Its tap mechanism already graduated into the
  shipped PromptSwap (facing-prompt toast delivery, its own independent hook), so ShowSpike was pure
  redundant noise. Unwired it from Engine (field, construction, Arm, Tick); PromptSwap's production
  delivery is untouched. ShowSpike.cs retained unreferenced (still on the LogContractTests dev-spike
  file list).
- [LW-31] SHIPPED 2b2f5b4 2026-07-07: the battle Abilities menu is the weapon funnel. In battle the
  "Attack" command row renames to the acting unit's living weapon (name + trimmed tier suffix, or
  "Fists" for an unarmed human), and its hover card becomes a mini equip card (flavor + the "+3
  ability" prose + the "Kills: N/T to +" tier meter, no Marks). Row text and hover title share one
  string, driven by a JobCommand text-catalog record: the rename never touches the "Attack" label
  bytes (kept as the race-guard anchor), it writes a split image into the desc footprint after the
  label and repoints nameOff/descOff into its two halves, restore is the mirror. A budgeted heap
  census finds the table copies; a three-way anchor (vanilla / current / previous image) leaves
  foreign records untouched. Turn-owner resolve is cursor-only (Offsets.TurnQueue, snaps at turn
  open); any resolve failure restores vanilla (a wrong dossier is worse than none). Delivered
  incrementally through cdfcc60 (dossier painter) and 2b2f5b4 (row rename) plus its already-exited
  sub-ids LW-33/LW-36/LW-38/LW-40/LW-44 (LW-27 retracted). RESIDUAL: fingerprint-twin units
  (identical level+hp+maxHp) fail closed to vanilla by design, carried as backlog LW-39; the
  row-rename LIVE_LEDGER row stays owner-flip-only.
- [LW-2] SHIPPED 10161db 2026-07-07: deploy-and-live-verify pass for the 2026-07-05 shipped batch.
  Rows 10 (desc budget trims), 11 (log facelift, full row-11 protocol), and 12 (Boco unarmed
  stale-latch fix) all verified live by the owner 2026-07-07, closing the release-verify scope. The
  Reliquary Phase 1 rows (6-9: Mark toasts, card story line, undead/Requiem classifier,
  legends.json persistence) are deferred past 2.3.0 and now ride backlog LW-6; VERIFY_LIVE.md keeps
  their revival instructions in a dedicated deferred section.
- [LW-45] SHIPPED c132edd 2026-07-07: equip-card descriptions ran off the bottom of the screen. The
  real constraint is the box's wrapped-LINE count, not char count, so the old 266-char budget was far
  too loose (a third of the catalog passed it yet clipped); living weapons with a +3 signature block
  stacked Kills line + flavor + mechanics + the +3 block past the box height. Fixed by three
  owner-eyeballed levers: compressed the generated mechanics prose across every card ("Deals X
  damage", "May cast Y on hit", "Reaches N tiles"), collapsing the fattest wrapped block and fixing
  the elemental/ranged weapons; trimmed 30 over-long flavor lines (each only as much as needed); and
  trimmed Umbral Rod's +3 prose (content-dense enough that flavor alone could not fit it) in lockstep
  with the grid CSV. DESC_MAX tightened 266 -> 205 as a rough char guard (the true constraint is
  wrapped lines; recalibrate on the card UI). Owner confirmed the cards fit.
- [LW-26] SHIPPED c132edd 2026-07-07: the Outrider Pistol's over-long card, folded into the LW-45
  catalog-wide desc-fit pass (trimmed in the same batch, and the Marks-line margin concern is moot
  now that Marks are release-hidden, LW-35).
- [LW-5] SHIPPED e882799 2026-07-07: Galewind Puppeteer releases the puppet after IT takes its own
  turn, not on the wielder's clock. The shipped wielder-clock rode TurnTracker.Turns(wielderFp),
  which LW-7 collapses onto the wielder, so the puppet released on the next turn after dominate
  regardless of whose it was (premature when the puppet was not the next actor, late when it was
  fast, per the 2026-07-07 tapes). Release now fires when the engine turn-owner queue
  (Offsets.TurnQueue, the struct TurnTracker.TryActiveFingerprint matches) names the puppet across an
  acted rising..falling edge, read directly so it is immune to the LW-7 credit collapse (the CT byte
  and actor pointer both read dead for a human-driven puppet). A GlobalTurns cap backstops the case
  where the queue signal never fires, bounding a puppet to at most N global turns, never to battle
  exit. Live-verified: held through three other units' turns, then released reason=own-turn on its own
  turn, corroborated by the queue, AREC performing stamp, and actor pointer all naming the puppet at
  once. The card's "for its full turn" is now accurate; the unimplemented "No Lucavi" clause spins out
  to LW-46. The recon instrument that cracked the signal was retired; the dominate/release flight taps
  are kept.
- [LW-35] SHIPPED 672e8f4 2026-07-07: release-hide the Marks feature on every card surface (owner
  direction; the display returns with the two-wave Chronicle build, LW-32). The equip-card story
  narration (Display legends:null, 65f7f77) and the attack-card Mark clause (AttackCard
  markLabel=null) were already dark; this closes the last surface by passing null for Reliquary's
  toast, so an earned Mark never enqueues a deed toast even when BannerToasts is enabled. Milestone
  and unlock toasts on the shared BannerToast are unaffected. Collection is untouched: the
  LegendStore still records every deed and Mark (proven inert by
  ReliquaryTests.Disabled_toasts_stay_fully_inert), so re-enabling paints over unbroken history.
- [LW-36] SHIPPED 5bf180d 2026-07-07: reworded every +3 ability card block to the locked grammar
  (header "{Name} (+{tier})", a verb-first "{Verb} {effect}. {Condition}" body for all 25
  signatures within the 90-char budget, job gates moved into the body), and added the
  check_p3_grid_lockstep gate that makes the grid CSV's "+3 ability" column the design source of
  truth and refuses any drift from items.json's p3Desc. The equip-card body meter (part 2) shipped
  earlier in cd6599e; the attack-card tail no longer carries the ability line (superseded by LW-44).
  Owner live-verified the baked cards.
- [LW-44] SHIPPED 8d145bf 2026-07-07: removed the battle Attack card's signature tease ("Unlocks
  {ability}" / "{ability} armed") for now (owner request). ComposeTail composes the Kills meter
  only; the sigLabel/sigEarned params and the caller are retained so re-enabling is a one-line
  revert. Owner live-verified: no tease on the Attack card.
- [LW-40] SHIPPED 08980f2 2026-07-07: re-entering a battle from the world map silently failed to
  register as a battle, so the Attack row (and growth, and kill-tracking) stayed dormant and the
  Abilities menu read the game's vanilla "Attack" (owner repro: leave to the world map, restart the
  battle). Root cause: the 1.5 re-enter presents battleMode=3 with the slot0 marker reading 0x10,
  but EnterSignal gated mode 3 behind the 1.0-era slot0==0xFF. EnterSignal now enters on any live
  battle mode (2/3/4), matching InLiveBattle; battleMode reads 0 on the world map so it cannot
  false-enter. Live-verified by the owner the same day.
- [LW-38] SHIPPED 3bcdadc 2026-07-07: the Attack-row rename missed the battle's first turn
  (owner gripe: the whole-heap census took dozens of ticks per battle, so the first Abilities
  menu open beat the first paint). ResetBattle now keeps the cached table copies warm across
  the battle edge; the next battle's first RepaintAll re-verifies each copy (label bytes plus
  footprint image) and evicts anything stale, re-arming a full census only when the cache is
  empty. Owner live-verified: the weapon loads in place of "Attack" on the first turn of the
  second battle, no rescan wait.
- [LW-27] RETRACTED 2026-07-06: the party-menu equip-card "Kills: N" header, superseded by the body-first-line Kills meter (cd6599e); the count lives in the card body on every surface, so no header stamp is built.
- [LW-33] SHIPPED 18d640d 2026-07-06: the residual footprint-poisoning path in the attack-card
  painter. SyncHit re-pins the footprint to the vanilla 73 chars on every known-line read
  (repairing an already-poisoned cache entry instead of only avoiding fresh poisoning), with a
  test hook proving the repair, and the two overselling test comments were corrected in the
  same commit. Ledger exit recorded late: the fix itself shipped inside 18d640d's round.
- [LW-20] SHIPPED 0bf9d65 2026-07-05: the LoggerTests millisecond-timestamp flake (two rendered
  console lines compared with embedded wall-clock stamps could straddle a boundary and fail a
  clean tree). A pure StripTimestamp helper normalizes both lines; a dedup-key sabotage run
  proved the test still bites.
- [LW-21] SHIPPED 0bf9d65 2026-07-05: TodoContractTests hardening: the changelog scan now
  inspects every top-level list line (a bracketless exit line goes red instead of invisible)
  and the Now-entry title capture excludes asterisks so a rogue second bold marker cannot be
  swallowed.
- [LW-1] SHIPPED 1a157f2 2026-07-05: the unarmed stale-latch bury branch ate armed players'
  kills (Boco/Phoenix Down; two burials taped in one battle the same day). Fixed by consulting
  the KillerStamp register at the empty-latch bury: only a strictly fresher, disagreeing,
  ARMED hypothesis converts the bury into a credit; designed no-credits and closed periods
  stay byte-identical. Owner verified crediting live on the 2026-07-05 deploy.
- [LW-3] SHIPPED 02eff93 2026-07-05: docs three-tier reorg. Living contracts stay at the docs
  top level, closed journals moved to docs/research/, shipped or dead one-shots to
  docs/archive/, every doc stamped with an opening STATUS line, references swept repo-wide
  (code comments, probes, tools, data, gitignore), history preserved via git renames.
  DocsContractTests gates the top-level allow-list, the per-tier stamps, and repo-wide
  doc-link integrity.
- [LW-16] SHIPPED 58d5c7b 2026-07-05: long item descriptions pushed the equip card off the
  screen (Sanguine Sword id 23, owner screenshot). Fixed with the analyze.py total-description
  budget (DESC_MAX=259, live-calibrated) plus three owner-approved prose trims (Sanguine Sword,
  Wrathblade, Stormarc).
- [LW-17] SHIPPED f4bf5df 2026-07-05: stale-latch kill mis-credit under auto-battle AND manual
  play (root-caused from flight archives; the Ember Rod / Claymore mis-credit adjudicated on
  tape). Fixed with the KillerStamp death-edge culprit stamp; live-verified the same day (4
  correct stamp overrides on tape, including the battle-ending Queklain credit under
  auto-battle). The residual turn-count half is tracked as LW-7.
- [LW-18] SHIPPED a3106d0 2026-07-05: BuildLinked deploys wiped the flight/ archives (PowerShell
  Remove-Item with -Exclude filtering is unreliable and erased the auto-battle attribution
  tape). Fixed with the named temp-dir preservation round-trip ($PreservedSaveFiles in
  tools/pipeline.ps1); all three manual verifications passed live.
- [LW-19] RETRACTED 2026-07-05: "battle-ENDING kills vanish" was a false alarm (the suspect
  tape was a manual RETRY of Lionel Gate, not a victory; the completed re-run credited all
  seven deaths cleanly). Kept findings live in LIVE_LEDGER and the Reliquary docs:
  per-encounter canonical boss keys, retry re-earns tally kills, and the Queklain
  battle-ending credit through its cutscene.
