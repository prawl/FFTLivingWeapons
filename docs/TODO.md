# TODO

STATUS: CONTRACT (machine-checked by TodoContractTests; format grammar at the bottom of this file)

The work ledger. "Now" holds what is actively being worked for the current release (hard cap 5,
each entry carries Done means + Verify). "Backlog" captures everything else at the cheapest
possible entry cost. Items EXIT this file only through docs/CHANGELOG.md, moved there in the
commit that ships or kills them. The full release ship gate stays in docs/RELEASE_SCOPE.md; Now
is the in-flight subset, not a mirror of that checklist.

## Now (release: 2.3.0)

- **[LW-92] Plague drops its victim on a mid-battle level-up (Venombolt hold fails)** (opened 2026-07-14) [AWAITING-LIVE]
  - Done means: Plague's victim identity survives mid-battle stat drift. The exact-match
    fingerprint (mhp, lvl, brave, faith) at Plague.cs:108 and Drive's mismatch drop releases
    the latch when the victim levels mid-battle (live capture 2026-07-14 dev lane: Aitne
    latched at 95/449, leveled to 96/453 with orig brave/faith stable at 67/51, hold dropped;
    at her CURRENT stats the pin defeated three cures on tape with the timer re-stamped to 36
    each time, so the write machinery is healthy and the loss is identity-only). Fix:
    drift-tolerant victim matching in the pure policy half (Band.LevelMatchesRoster's up-only
    level drift, bounded maxHp GROWTH, exact orig brave/faith), applied at BOTH check sites,
    with the stored fingerprint re-anchored to the current values on every accepted drift so
    the budget never accumulates stale.
  - Verify: failing-first policy tests (accepts level+maxHp up-drift, rejects level drop, maxHp
    shrink, brave/faith change, or beyond-bound growth; stored fp re-anchors on accept); suite
    green; owner live re-run of the repro (latch, victim levels, hold persists, cure defeated);
    smoke row 7.5 ticks on that pass.
- **[LW-60] Author the 2.3.0 release Smoke Test Plan** (opened 2026-07-10) [AWAITING-LIVE]
  - Done means: docs/SMOKE_TEST_2.3.0.md exists at the docs/ top level (allow-listed in
    DocsContractTests), modeled on the archived 2.0 checklist, and gathers every deferred live
    check in one owner pass: the LW-55 auto-battle gate premise, the LW-71 struck-pre-turn
    repro, the LW-51 Tier-1 reset eyeball on a real cold-launch New Game, the LW-69 census
    line and LW-34 coverage confirmations, VERIFY_LIVE's open Choir row, the 2.3.0 feature
    and regression rows from RELEASE_SCOPE's IN list, and the ship-gate checklist (version
    bump, tag, Publish).
  - Verify: suite green (DocsContractTests allow-list + link scan, TodoContractTests); the
    owner then RUNS the pass, flips its checkboxes, and closes with a green
    `python tools/scan_logs.py`.
## Backlog

- [LW-6] 2026-07-04: Slayer's Reliquary, the post-release headline bet (the weapon remembers WHO
  it killed). Full design and staged plan: docs/RELIQUARY_DESIGN.md; acceptance:
  docs/RELIQUARY_AC.md. Phase 0 probes COMPLETE 2026-07-05 (boss key = per-encounter canonical
  nameId; same-form minions collide; withdrawal bosses like Zirekile Gafgarion produce no death
  edge and must be excluded or special-cased; a retried boss kill must dedup by key). Phase 1
  (Marks + card story) SHIPPED 061e36c, awaiting its live pass.
- [LW-7] 2026-07-05: TurnTracker turn counting collapses under auto-battle (turns #2-#6 all
  credited one fingerprint, log 07:58). The kill-credit half of the stale-latch bug shipped as
  the KillerStamp death-edge stamp (f4bf5df); the turn-count half is still live. Candidate fix:
  close the acted period on ActorRegister OWNER CHANGE in addition to the byte-fall debounce.
  Must not regress reaction-kill credit (the pointer may name the REACTOR during a reaction,
  unverified per the ledger caveat).
- [LW-8] 2026-07-05: Console QuickEdit selection suspends the mod thread (about 3 minutes
  observed mid-battle; kills, growth, and toasts all stall; the census "hang" was this).
  Hardening candidate: async/queued console sink in FileConsoleLogger (the file sink stays
  synchronous, it is the evidence chain). Until then read livingweapon.log, not the console.
- [LW-9] 2026-07-05: Warbrand (id 67) arrives too early for its power (owner-noted): available
  from early on, overtuned for that acquisition point. Candidates when picked up: later
  availability tier, price bump, or stat trim (re-run the analyze.py dominance gate after any
  change). Independent of the release-scope spriteIdOverride cleanup.
- [LW-10] 2026-07-04: Remove Treasure Master (owner-paused 2026-07-14 until after the 2.3.0 tag;
  2.3.0 ships the module disarmed on 1.5.1, smoke row 7.22).
  Stage 1 committed 0f842f5 on branch feature/lw10-remove-treasure-master (worktree
  C:\Users\ptyRa\Dev\FFTLivingWeapons-lw10, plan at the worktree root lw10_plan.md, Stage 2
  first half uncommitted there); merges to main only after the 2.3.0 tag; the production
  Scholar's Ring grant was killed separately in 2.3.0 (LW-86); demoted from Now 2026-07-14 to
  make room for LW-86.
- [LW-11] 2026-07-04: Alter Axes and Flails, cheap slice only (Squire/Geomancer equip access on
  existing sword-typed items). The rest is walled research (type-welded formula, id-welded art,
  no known flail formula id).
- [LW-12] 2026-07-04: Migrate the lossy-detection siblings (Maim, Larceny, Ricochet) to cache
  plus rearm, opportunistically when those files are next touched.
- [LW-13] 2026-07-04: Kill-tally milestones on the equip card beyond the counter. Gated on an
  untested glyph-render probe; largely redundant with the shipped milestone toasts.
- [LW-14] 2026-07-04: Replace the Stormbrand (status procs are low-percent; the real cure is a
  runtime signature). Pick the theme AFTER the Samurai signatures lock to avoid a Slow/element
  dupe.
- [LW-15] 2026-07-04: Enemies actually USE living-weapon benefits (XL undesigned feature; the
  static rebalance already lands the real player want).
- [LW-23] 2026-07-05: A Mark deed toast starves the tier-up toast on the same kill (owner
  observed): Ramza's gun earned Beastbane and the deed toast delivered, but the same blow was
  kill 2 (tier-up to +2) and no tier-up toast ever appeared. Investigate contention on the
  single delivery slot (queued and dropped? overwritten?) and make both deliver in order.
- [LW-24] 2026-07-05: The tier-up banner delivers a turn late (owner screenshot): the Stormbrand
  wielder's 3rd-kill banner appeared while the NEXT unit (White Mage Collys) was already active.
  POLICY LOCKED (owner, 2026-07-05): fire the UI text only during the earning unit's own wait
  turn; if the credit resolves after that wait window has passed, SWALLOW the message entirely
  (never deliver on a later unit's turn; the card and tally still record the growth, so a
  swallowed toast loses nothing durable). Implementation: at delivery time compare the toast's
  earner to the current turn owner and drop on mismatch; turn-owner detection has known traps
  (the hover-follower struct is NOT the turn owner), so use the durable turn/register state.
  Interacts with LW-23: within the correct window, deed and tier-up toasts still need ordered
  delivery, not mutual starvation.
- [LW-32] 2026-07-05: Marks in two waves (owner architecture direction): wave 1 = a weapon
  CHRONICLE store collecting metrics as play happens (aggregate counters per weapon and victim
  class for scale, plus a notable-events log: first blood, first of each class, boss keys,
  battle-enders, milestones; the victim snapshot already captured at every credit edge makes
  collection nearly free; KillTally-pattern persistence, deploy-preserved, raises LW-29's
  stakes); wave 2 = a pure INTERPRETER turning metrics into Marks/tiers/deeds/card lines
  (policy only, fully unit-testable, no live risk). The killer property is RETROACTIVITY: new
  Mark vocabulary or threshold changes award from history already collected, so interpretation
  can iterate forever without wronging a save. Owner is on the fence about Mark titles
  doubling the +N system; the record-first architecture defers that question (candidate
  anti-doubling rule on the table: a Mark requires PLURALITY of kills, not raw count).
  Supersedes/absorbs the Phase 1 legends.json shape when picked up; ties to LW-6.
- [LW-28] 2026-07-05: A BuildLinked deploy LOST kills.json and legends.json despite the
  preservation round-trip (the 17:54 launch logged "No kill tally was found on disk"; the 82
  kill tally and the Beastbane Mark were gone; the %TEMP% livingweapon_preserve dir no longer
  exists). The 17:0x deploy preserved the same files fine, so the failure is intermittent.
  Second anomaly on the same evidence: the 17:1x session flushed exit tapes at 17:37/17:41 but
  kills.json kept its 13:45 timestamp, so exit-edge tally saves may not have written that
  session. Investigate both. The loud post-restore existence check SHIPPED 2026-07-11
  (Get-LostPreservedItems in tools/pipeline.ps1; BuildLinked fails red before deleting the
  backup dir, and the catch path re-restores from it), so the next occurrence fails loud with
  the copies recoverable instead of printing success; the two investigations remain this row's
  open work. Owner declined tally reconstruction for now (tapes and prev.log carry the counts
  if ever wanted).
- [LW-30] 2026-07-05: Weapon reputation in the attack-targeting pill (demoted from Now when
  LW-31 took the slot; the Abilities-menu funnel covers the in-battle identity job). If
  revived, the locked wording is "Select the target for {Mark}{Name}{suffix}." via a PromptSwap
  prefix match on "Select a target"; unstoried weapons keep vanilla text. Every technical
  unknown was answered live 2026-07-05: writable, render-call-time swap (fragment-length
  unbound), pill auto-sizes to viewport width, markup tokens supported ("<keyicon=ok>").
- [LW-39] 2026-07-06: Recover fingerprint-TWIN units for the cursor resolve (owner hit it live:
  two party units at identical level and hp/maxHp made the resolve refuse, and the register
  fallback then dressed Ramza's Attack row in the Spark Rod wielder's dossier; the fallback
  is now removed, so twins simply show vanilla). Fix direction: extend the condensed
  turn-queue fingerprint with more struct fields; the probe dump shows brave/faith-like u16
  candidates in the cursor struct needing offset verification (turn-owner-probe lines,
  livingweapon.log 04:0x). Until then twins fail closed to vanilla by design.
- [LW-42] 2026-07-07: Audit the remaining slot0==0xFF marker checks for 1.5, where the in-battle
  marker reads 0x10 (Offsets.Slot0 note; live probe 2026-07-07 read slot0=0x10 on a mode-3 turn).
  InLiveBattle's cast-targeting / paused / event excuse (modes 1 and 5) and PairArmed both test
  0xFF and are therefore dead in 1.5, so a long cast or animation at mode 1/5 could accumulate the
  exit debounce and false-exit mid-battle (resetting the kill tracker). Verify live with a slow
  cast, then re-anchor the marker value.
- [LW-43] 2026-07-07: Gun Slinger (Outrider Pistol id 71) dual-wield off-hand equip is SLOW to
  apply to a SECOND wielder when it is already in effect on a first (it DOES eventually equip; owner
  saw the lag live 2026-07-07, not a correctness bug). Suspect the per-wielder locate/write cadence
  serializes or throttles when more than one unit carries the pistol: check the Gun Slinger
  signature's tick loop and whether its locate stops at the first wielder per tick.
- [LW-47] 2026-07-07: Murasame id41's living-weapon signature is deferred out of 2.3.0 (Kiku-ichimonji
  took the one samurai signature slot with Mushin); pick a proven lever and build it when revived.
- [LW-48] 2026-07-07: Append "Modded by prawl" to the in-battle "View Battlefield" UI label so it
  reads "View Battlefield - Modded by prawl" during a battle (a subtle mod-attribution touch). Likely
  mechanism: a SetTextString-family tap/prefix-match swap (PromptSwap precedent) or the text-catalog
  offset redirect (AttackCard/AttackRow precedent); find the "View Battlefield" string source first.
- [LW-58] 2026-07-09: Body Double feasibility probe (plan.md): can a pre-loaded dormant unit slot be
  activated mid-battle so the ENGINE constructs its sprite (the despawn-probe principle in reverse)?
  Build tools/probes/spawn_probe.py (list / activate / selftest) on battle_cheats.py; classify
  dormant-populated slots, hunt the live presence byte (taunt research's band ENTD mirror at
  +0x17A..0x181 is the first candidate region), then the raw-flag flip test on a throwaway save.
  Expected outcome per LIVE_LEDGER (the walled write-and-hold spawn row): flag-flip fails to render
  and the real primitive is the engine's mid-battle-join activation routine; a clean negative is a
  successful probe. Research probe only, nothing wired into the DLL.
  2026-07-09 live: raw-flag path CONFIRMED DEAD by a stronger test: the chest revert re-enrolled
  timeline/hearts/revival but the model stayed a chest (SpriteSet +0x00 never changed at the pop,
  the model swap is scene-graph-side) and the unit's own turn soft-locked. Treasure-pop signature
  decoded (+0x45/+0x46 plus the +0x18E mirrors); see the new LIVE_LEDGER row. Next: CE what-writes
  on band +0x46 at a pop (spawn_probe addr prints addresses); untested variant: frog-cast in the
  post-revert turn window.
  2026-07-09 later: status system decoded (three 5-byte layers; apply engine 0x150BF66DC;
  dispatch 0x1401FB064; treasure = status id 15; see the LIVE_LEDGER status-system row).
  External pending-field writes are consumed but ignored (3 tapes), so ALL external-write
  spawn/model lanes are closed; the next lever is an in-process cold-call spike (DLL, LWDEV)
  or the event-script AddUnit/Draw layer.
  RESOLVED 2026-07-10 (dev-spike proven, not shipped): a mid-battle DUPLICATE of a live donor is a
  real, drawn, named, controllable, AI-FIGHTING unit that descends from the heavens; the render weld
  is beaten via the node builder 0x14026EBEC + a data-only AI enroll whose one-byte key is the
  AI-roster index 0x141873038[slot]. Battle-scoped (temporary summon; permanent recruit = a
  save-roster entry, unbuilt). Also cracked this arc: full unit TELEPORT/SWAP + visual FLOAT (render
  node world transform), DESPAWN (node +0x12C mode-2 + engine sweeper), RESURRECT, and the animation
  request register (node +0x10). Full records: MECHANICS.md breakthrough block, five LIVE_LEDGER
  Uncertain rows + two overturned walls, memories body-double-spawn-arc / position-write-desync /
  unit-despawn-resurrect-recipe / anim-request-register. BodyDoubleSpike Canary 1-9 (dev-only,
  worktree feature/body-double-spawn). Open polish: AI-passivity (behavior row), decoy-hold default,
  and shipping any of it as a real player mechanic (LW-64 Mirror Image / LW-65 teleport / LW-66
  remove-restore track the shippable slices).
- [LW-61] 2026-07-10: LW-51 Tier-2, per-save-identity tally isolation: two ALTERNATING
  playthroughs still share one kills.json (the shipped Tier-1 reset only archives on a detected
  NEW GAME, bf351db), so key the tally files to a save identity if cross-contamination proves a
  real problem in play; deliberately deferred out of LW-51.
- [LW-64] 2026-07-10: Mirror Image ability concept (owner): flip a unit's hide gate (combat +0x01
  to 0xFF) to phase it out of logic while the render weld leaves its sprite standing, dodging
  locked-on spells for a turn; every primitive live-proven in the LW-58 gate-toggle session.
  DECISIVE UNKNOWN first: does a CHARGED spell whiff when its target is hidden at resolution
  (cheap CE test: bait a charge, gate-FF the target pre-resolve, watch)? Known hazards to guard:
  restoring onto an occupied tile co-tiles into the movement soft-lock (proven live); a mid-hide
  autosave persists the hidden state into resumes (proven live, needs a battle-enter un-strand
  sweep); hidden units get no scheduler turns, so the restore trigger must be external (other
  units' acted edges, or the dodged action resolving). Castable wrapper when built: JobCommand
  injection plus an action-record watch (the Barrage lane).
  2026-07-10 later: THE DECISIVE TEST PASSED (owner live): a mid-cast Slow whiffed entirely when
  the target was gate-hidden during the cast animation, so hide-at-resolution defeats locked-on
  actions and the core fantasy is proven. New side effect to chase before any build: the whiffed
  resolution DISPLACED the hidden unit one tile (unexplained; possibly target-snap bookkeeping
  applying to a unit the effect could not find).

- [LW-65] 2026-07-10: Unit TELEPORT is proven live (LW-58 session): the render
  position was the missing layer (render node +0x4C/+0x50 u16 world X/Y = 28*tile + 14; node via
  list head 0x140D3A410, +0x148 combat backref), and a coherent triple-write (combat +0x4F/+0x50
  logic, node +0x88/+0x89 AI tile key, node +0x4C/+0x50 world) moved a real enemy who then
  hovered correctly and took a normal AI turn from the new tile, after which the engine re-stamps
  every layer itself. Un-parks the Knockback family (position-write-desync memory updated) and
  gives Mirror Image its restore-displacement primitive. Same night: the Z formula was solved
  (node +0x4E = -12 x height, +1 height unit when the unit has FLOAT: the hover offset is pure
  node data, owner-witnessed granted to a non-Float unit and stripped from it by Z pokes alone;
  full set X=28x+14, Y=28y+14, Z=-12h with the Float rider) and a complete TWO-UNIT
  POSITION SWAP (Ramza and a live enemy, all layers, own facing kept) executed flawlessly with
  both units acting normally after. Open before any shipped mechanic: a tile-occupancy check
  (co-tile = target shadowing + movement lock) and a LIVE_LEDGER row (owner flip).

- [LW-66] 2026-07-10: Mid-battle unit REMOVE + RESTORE are both proven live and DATA-ONLY (the
  LW-58 session finale): despawn = one mode-2 byte on the render node (engine sweeper tears down
  unit + sprite, byte-perfect); resurrect = AI-registry re-enroll (clone + re-key a living
  object) + node revival (in-use flag, done-mark clear, list re-splice) + present/gate reopen,
  with a sky-descent flourish; the removal drops AI enrollment, so re-enroll MUST precede
  visibility (else the LW-58 freeze). Full byte recipe in the unit-despawn-resurrect memory;
  MECHANICS.md breakthrough block has the summary. This unlocks the summon/reinforcement
  mechanic family (park-and-summon variant needs no despawn at all: gate FF + render Z below
  floor = invisible reserve). Open: victory-check sanity after a removal; whether a legitimate
  registry rebuild evicts the hand-cloned object; Ctrl+F5 despawn spike fix (hover-marker
  refusal removed) awaits its next deploy.
- [LW-67] 2026-07-10: Remove every service bound to the F6 test key (owner directive, about six F6
  users). DONE in this repo: the four dev spikes (AttackCardSpike, HeaderSpike, FlavorSpike, and
  ShowSpike deleted whole) plus their Engine wiring, the spike-only feeders (HeaderProbeText,
  FlavorProbeText) and their tests are gone; AttackCardProbeText and ScanCursor/RegionCursor were
  KEPT because the production Attack-card painter (AttackCard / AttackCard.Census) consumes them.
  REMAINING: sweep the sibling FFTHandsFree repo for its own F6 bindings (the other roughly two of
  the six).

- [LW-73] 2026-07-11: The flight census band records carry no hp/position/CT, so tapes cannot
  self-diagnose phantom-seat classes on their own (the LW-34 over-count mining needed the raw
  file log alongside the flight tapes to tell a phantom seat from a real one). Widen the census
  band record with those fields when the census format is next touched.
- [LW-76] 2026-07-11: The owner-directed console audit (every Event/Warn/Error and ScopedLogger
  call site justified against LOGGING.md's match-report contract) left a candidate list:
  (a) repeat-spam risks with no per-event dedup beyond the console's per-battle key
  (Sanctuary.cs:116 re-fires per crystal-counter dip on the same ally, GrowthEngine.Ultima.cs:66
  re-logs on HP-percent flap, SpiritualFont.cs:164's per-copy WARN loop, the Barrage/ShadowBlade
  grant/release pairs on equip flapping); (b) Info lines stretching the match-report definition
  (SpiritualFont.cs:167 narrates EVERY wielder move, PromptSwap.cs:161 doubles every on-screen
  toast, EagleEye.cs:93 prints per enemy, BattleCensus.cs:144 is a WARNING under the [trace]
  verb, a tier/verb mismatch); (c) WARNs that fire in healthy sessions (the one-tick locate and
  readback misses in LifeSap/Renewal/Wyrmblood/Rapture/GrowthEngine.Signatures, the
  revive-and-rekill repeat-credit WARN, TreasureMaster.cs:305's self-described-benign weather
  mismatch, AttackCard.Resolve.cs:87 on known stale-cursor hovers). None urgent (console dedup
  masks most); triage with the owner which get demoted, deduped, or left.

- [LW-80] 2026-07-13: File the upstream modloader issue (Nenkai/fftivc.utility.modloader):
  table-XML row edits apply as whole-row writebacks (ApplyTablePatch assigns every field via
  model.X ?? previous.X at OnAllModsLoaded), clobbering other mods' post-snapshot runtime row
  writes; propose dirty-field-only writeback. Draft body in handoff.md (2026-07-13 action
  pack); owner files it under his account. Fixes the LW-77 class ecosystem-wide once adopted.
- [LW-85] 2026-07-14: AnchorScan later tiers (the rest of the LW-82 arc; the v1 slice shipped
  e77b9d7): battle-state anchors (CombatAnchor/TurnQueue via chained fingerprint scans seeded
  from the found roster base; needs a live battle, and on a patched build the scout cannot
  trust any pinned battle-state flag to know one is running) and the no-content residue (the
  SubmenuFlag class: boot-time state-solve or anchoring relative to signed neighbors; 1.5.1's
  only data casualty). Sibling-mod adoption (copy AnchorScan.cs, the FingerprintGuard pattern,
  into FFTHandsFree/FFTColorCustomizer/FFTMultiplayer) rides this row too
  (hardening-must-be-portable).
- [LW-87] 2026-07-14: A whole battle can compose the vanilla Attack row when the cursor's
  condensed struct parks on a band MIRROR seat (owner watched it live, first PROD session, log
  12:14:00: credit-side resolve found Vagabond id 19 via the turn-queue fallback, but the card's
  cursor unit sat at slot 25, not the turn owner, so the CursorGate correctly composed vanilla
  for the entire battle; the next battle resolved on turn 1 and a mid-battle weapon swap
  followed correctly). Fail-closed by design (LW-55 gates, LW-39 family), but a full-battle
  rename blackout is a UX miss; candidate: a mirror-seat-aware cursor resolve (frame nameId
  dedup, the Band mirror rule).
- [LW-88] 2026-07-14: The attack-card dossier's kill count goes stale mid-battle: the owner
  watched it hold 19 across a battle that credited 7 Chaos Blade kills live (the 14:08 flight
  tape shows counts 20 to 26 crediting in real time with victims attached), then read 26 in the
  next battle. The tally map is live; the composed dossier line is not recomposed on count
  change within a battle. Cosmetic; candidate: invalidate or recompose the cached dossier for
  the resolved weapon when its tally entry changes.

- [LW-91] 2026-07-14: With MULTIPLE tracked wielders fielded, the Attack row and hover card can
  wear the PREVIOUS wielder's dossier on another unit's turn (owner screenshot 20:36:32, dev
  lane: Wilham's menu read "Kiku-ichimonji+3, Kills: 5", Ramza's weapon; visiting the status
  page and returning corrected it). The log shows the paint lifecycle mid-churn: label-gone
  evictions plus ~29s mid-battle re-census windows during which the painter can neither repaint
  nor REVERT copies it lost, so bytes painted for the prior turn stay visible until a menu
  rebuild; the cursor gate itself still refuses correctly when it has a cache (20:37:36
  NotTurnOwner revert). Display-only, self-correcting, credit unaffected (the same window's
  credit lines resolved Warlock's Staff). Subsumes LW-88 (same lifecycle root, the stale-count
  variant). Fix direction: make paint/revert transactional across cache loss (revert-on-evict,
  or refuse-to-paint while the census is mid-sweep); ship 2.3.0 with a known-issue note.
  Second witness same day (owner): the Attack card and the equip card disagreed on Venombolt's
  kill count mid-battle, converging a turn later, and the owner read the EQUIP card as the
  laggard (Engine gates Display.Tick on ShouldPaintCard's off-field settle, so mid-battle
  equip-card views can serve stale pool paint), meaning the staleness is cross-surface with
  distinct cadences. Owner directive: address this one when picked up, not just note it.
- [LW-90] 2026-07-14: An in-battle RESTART after the Iai opening hold leaves the wielder's
  boosted Speed in place for the restarted battle (owner live, dev lane, 17:31-17:35 logs): the
  mod's own bookkeeping reads healthy (hold at battle-start, "released by the turn flags" in
  every instance), so the leading theory is the game's restart snapshot capturing the held
  Speed as the unit's baseline before the release lands, making the mod's captured "natural"
  the boosted value on the restarted run. Battle-scoped (combat struct only, roster untouched,
  gone at battle end); candidate fix: cross-check the captured natural against the ROSTER
  speed at hold time and clamp, which also caps the restart ratchet. Same hazard likely
  applies to every capture-natural-then-hold signature (Afterimage, Ultima, Cavalier's
  Charge) on restarted battles; audit when picked up.

## Walled (blocked by engine / Denuvo / modloader)

- Fix the sword swing-art (art welded to weapon id; the same render node also drives damage).
- Make item TEXT display in French (game + modloader parser walls; DLL live-paint is the only path).

## Format (enforced by TodoContractTests)

- Sections, in this order and no others: Now (with the release name in the header), Backlog,
  Walled, Format.
- Now: at most 5 entries. Entry first line: `- **[LW-<n>] <title>** (opened YYYY-MM-DD) [STATUS]`
  where STATUS is QUEUED, BUILDING, AWAITING-LIVE, or BLOCKED(reason). Every entry carries a
  `- Done means:` and a `- Verify:` sub-bullet. Promote from Backlog by filling those in; if Now
  is at cap, demote something first.
- Backlog: entry first line `- [LW-<n>] YYYY-MM-DD: <one sentence>`; indented continuation lines
  are free. Capture new items here in the session they surface.
- IDs are unique across this file and docs/CHANGELOG.md; never reuse a retired ID.
- Items exit ONLY by moving to docs/CHANGELOG.md when they ship or die: in the shipping commit
  itself, or in the immediately following commit when the exit row cites that commit's own hash.
- No em dashes and no double-dash separators anywhere in this file or the changelog.
- AWAITING-LIVE flips and VERIFY_LIVE checkboxes are owner-only.
