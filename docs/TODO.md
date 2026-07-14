# TODO

STATUS: CONTRACT (machine-checked by TodoContractTests; format grammar at the bottom of this file)

The work ledger. "Now" holds what is actively being worked for the current release (hard cap 5,
each entry carries Done means + Verify). "Backlog" captures everything else at the cheapest
possible entry cost. Items EXIT this file only through docs/CHANGELOG.md, moved there in the
commit that ships or kills them. The full release ship gate stays in docs/RELEASE_SCOPE.md; Now
is the in-flight subset, not a mirror of that checklist.

## Now (release: 2.3.0)

- **[LW-75] Promote the demoted coverage line to the console on the armed edge** (opened 2026-07-11) [AWAITING-LIVE]
  - Done means: when the once-per-battle coverage line latches while no tracked weapon has acted
    yet (ScopedLogger demotes it to file-only, the race the LW-34 live pass measured at 97
    seconds), EnemyOracle remembers the demoted line and re-emits it to the console exactly once
    when the armed latch rises later in the battle; per-battle reset; the file evidence chain is
    unchanged.
  - Verify: failing-first EnemyOracleTests (demoted line promoted once on the armed rise;
    armed-at-latch never re-emits; battle reset clears the pending line); suite green; the
    console eyeball folds into SMOKE_TEST_2.3.0.md row 4.3 (owner).
- **[LW-57] Fix the Attack command's first-open readiness after a battle load** (opened 2026-07-09) [AWAITING-LIVE]
  - Done means: on the first turn of the first battle after a session load, the Attack command
    row already shows the wielder's weapon name; later battles keep the LW-38 warm-cache
    behavior (that fix shipped 3bcdadc and holds; it deliberately left this cold-cache first
    battle open). Verified 2026-07-11: the mechanism is census cold-start latency, NOT the
    actor-resolve guess the original entry made; the rename cannot land until the census finds
    the table copies, the census steps only on in-battle ticks, and the 2026-07-11 tapes show a
    sweep arming and never completing across a whole battle (14:23 and 08:31 sessions) despite
    the handful-of-ticks budget design, which also starves RepaintDriver while _scanning holds.
    Step 1 is therefore the live sweep diagnosis (shared with LW-69's pending census-finished
    observation); the fix follows the diagnosis. Owner re-scoped this into 2.3.0 on 2026-07-11.
  - Verify: suite green; owner live pass: cold-load a save, enter the first battle, open the
    command list on the very first turn and see the weapon name, with the census-finished line
    in the file (folds into SMOKE_TEST_2.3.0.md row 5.3 and the LW-69 check).
  - Fix SHIPPED 9d347c9 2026-07-11 (repaint/scan tick alternation + aborted-sweep re-arm with
    hit preservation; full build cycle, verifier SHIP 9/10, suite 2439 green); the owner live
    pass above is what remains.
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
- **[LW-83] Guard observability: observed-vs-expected in the stand-down record, self-identifying drill** (opened 2026-07-14) [AWAITING-LIVE]
  - Done means: a guard stand-down self-diagnoses from its own artifacts: the flight "guard"
    record and the startup Error line carry each mismatching landmark's observed and expected
    values (PE build key: both u32 fields in hex; byte-signature landmarks: the observed vs
    expected byte windows; the roster-row probe: the observed field values), and a stand-down
    forced by the dev drill (forceMismatch, wired from LW_FORCE_FINGERPRINT_MISMATCH in
    Mod.StartEngine) self-identifies by printing the flag's name in both artifacts. Production
    passes forceMismatch=false, so the drill marker is unreachable for players.
    FingerprintGuard.cs keeps its zero-dependency copy-file portability contract.
  - Verify: failing-first tests (core mismatch detail surfaces in the stand-down diag; the
    LaunchGuard recorder record carries observed plus expected PE key values; a drill stand-down
    names the flag while a real mismatch does not); suite green; owner drill run on a dev build
    (trigger via the LW_FORCE_FINGERPRINT_MISMATCH marker file in the mod dir; the env-var lane
    does not reach fft_enhanced on this box) reads the self-identified line and the value pairs
    in livingweapon.log and the standdown flight archive.
  - Fix SHIPPED 2026-07-14 (LandmarkReading detail plumbing in the portable core, drill
    self-identify in the adapter, LaunchGuard split into lifecycle + Landmarks partials;
    build-lite pipeline, verifier SHIP 9/10 with a sabotage non-vacuity proof, suite 2463
    green); the owner drill run above is what remains, scripted in the session handoff.
- **[LW-69] Silence the unnecessary log output (census-evict flood + audit findings)** (opened 2026-07-11) [AWAITING-LIVE]
  - Done means: the 2026-07-11 owner-directed log audit (livingweapon.log + the flight tapes) is
    run and its unnecessary-output findings are silenced. Audit verdict: the two attack-card
    "evicting the cached copy" DBG lines were 98.4% of the session log (9,024 lines in 2.5s), and
    the cause is NOT evict/re-census thrash: the census logs one line per rejected never-cached
    heap candidate during a single sweep. Fix: SyncHit/SyncHitEnc2 return a SyncOutcome instead of
    logging internally; census rejections aggregate into the census-finished line's rejected
    count; real RepaintAll evictions keep one DBG line each naming the reason. No other line
    class flags as noise (next largest: 40 lines); flight tapes are healthy (bounded ring,
    on-change records) and untouched.
  - Verify: suite green; a fresh live session log shows zero per-candidate evicting lines, a
    census-finished line carrying the rejected count, and no single line class dominating the
    file (owner live pass).

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
- [LW-10] 2026-07-04: Remove Treasure Master (OBVIATES the Scholar's Ring idle-nag bug; do not
  fix that doomed code). On removal: de-list treasure.json from pipeline.ps1, release.yml, and
  the csproj together; BattleState.BattleDisplayed is shared with CharmLock and must survive
  the cut; drop Treasure Master from the ModConfig description. More fuel 2026-07-11: it
  granted a Scholar's Ring into a freshly reset new-game save 16s after the LW-59 smoke's
  tally reset, with TreasureAlwaysOn=False (log 02:12:03); confirm at removal time that no
  such inventory write is reachable in the production flavor.
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

- [LW-77] 2026-07-13: Shrink the third-party job-mod collision surface: prune JobData.xml's
  unknown-id rows (0, 28, 53-57, 142-143, 145-149, 151, 153, 155-161, 163-164, emitted by
  make_jobequip.py's MonsterGraphic==0 sweep) and audit JobCommandData.xml's record list (5,
  25-76, 155-160) for records no shipped feature needs. Mechanism (pinned from modloader
  source, Nenkai fftivc.utility.modloader master): FFTOJobDataManager.ApplyTablePatch does a
  WHOLE-ROW writeback at OnAllModsLoaded for any row a mod's XML lists (model.X ?? previous.X
  across all ~40 fields, incl. JobCommandId since loader 1.7.1 and InnateAbilityId1-4), so a
  CharacterEvasion-only row reverts every post-snapshot runtime write another mod made to that
  row; load order cannot fix it. Live casualties: DanaCrysalis Blue/Red Mages (Red Mage = job
  57, ours since v1.1.0; Blue Mage = job 33, never ours: the reported Red-dies-Blue-survives
  asymmetry), latent for GenericJobs DK/OK (jobs 160/161, both ours). JobCommandData has the
  same writeback shape (all 16 AbilityIds + 6 RSM slots), so pruning JobData alone does not
  close it. Confirmation before building: the reporter-runnable ladder (delete the row-57
  block, then JobCommandData.xml, then ability.en.nxd, restart between each) or the loader's
  own yellow per-field conflict console lines. Rider: Nexus hygiene (mark the Old Files
  1.0.0/1.1.1 Item Overhaul zips superseded so users stop running both generations; pin a
  known-issues post: Warbrand welded-art cosmetic, the row-57 interaction, game-1.5 blast
  radius). Owner answered 2026-07-13: Red Mage was NEVER re-verified after the Bloodpact park
  (no compose with these mods was ever verified), so no counter-evidence exists against the
  writeback mechanism and the June "Red Mage lost abilities" sighting may have been this bug
  misattributed to Bloodpact. Remaining validation before building: the delete-row-57 ladder
  above. working/dir_bluered/ holds their decoded action table from the June dev install.
  2026-07-14 compose-test methodology (owner directive, rides LW-83): every compose test with a
  suspected-conflicting mod (Blue And Red Mages, GenericJobs) starts by reading the guard
  verdict in livingweapon.log BEFORE any manual diffing: armed means the other mod's writes
  miss our watched regions entirely; a jobcommand-table stand-down while pe-build-key matches
  fingerprints their table writes, and the LW-83 observed-vs-expected bytes in that line name
  exactly which bytes they changed, ruling a game patch in or out from one log read.
- [LW-80] 2026-07-13: File the upstream modloader issue (Nenkai/fftivc.utility.modloader):
  table-XML row edits apply as whole-row writebacks (ApplyTablePatch assigns every field via
  model.X ?? previous.X at OnAllModsLoaded), clobbering other mods' post-snapshot runtime row
  writes; propose dirty-field-only writeback. Draft body in handoff.md (2026-07-13 action
  pack); owner files it under his account. Fixes the LW-77 class ecosystem-wide once adopted.
- [LW-82] 2026-07-14: The anti-game-update hardening arc: turn pinned addresses into things the
  mod re-finds itself at boot. Shape per the owner directive (memory hardening-must-be-portable):
  a dependency-free single-file AnchorScan core (byte signatures and struct fingerprints in,
  verified addresses out, FAIL CLOSED on zero or multiple hits) plus a thin per-mod adapter, the
  FingerprintGuard/HookLandmark pattern, copy-reusable in the sibling FFT mods. Tiers from the
  1.5.1 data: content-signaturable tables (the JobCommand find, generalize it), struct bases via
  cold fingerprint scans (GrowthEngine.Locate already owns the scan idiom, today it only scans
  NEAR a pinned base), region siblings by measured delta plus verify, and the residue with no
  content to sign (the SubmenuFlag class of UI flags: boot-time state-solve or anchoring
  relative to signed neighbors; 1.5.1's only data casualty was exactly this class). Code hooks
  are covered by HookLandmark (shipped in the LW-81 arc): refusal, not self-relocation.
- [LW-78] 2026-07-13: Re-diff the pre-1.5 full-table nxd bakes (item.en.nxd and ability.en.nxd)
  against 1.5 vanilla: the loader diffs each mod's nxd against the CURRENT vanilla table at
  load, so any text cell the 1.5 game patch changed silently converts our stale bake into an
  unintended table-wide edit. Verifiable offline (decode 1.5 vanilla, re-diff, count
  unintended cells); also check row-count parity (rows missing vs vanilla are applied as
  RemovedRows).
- [LW-79] 2026-07-13: docs/DESIGN.md line ~107 still claims clean compose with Blue/Red Mages
  ("no interaction", written 2026-05-30, two days before JobData.xml existed); three player
  reports and the pinned loader writeback contradict it. Correct the claim (cite LW-77's
  mechanism) when LW-77 resolves.

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
