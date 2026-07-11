# Changelog (work-ledger exits)

STATUS: CONTRACT (machine-checked by TodoContractTests)

Where docs/TODO.md items land when they ship, die, or retract; newest first within a cycle.
Entry first line: `- [LW-<n>] SHIPPED <hash> YYYY-MM-DD: <summary>`, or WONTFIX / RETRACTED
with a date and no hash.

## 2.3.0 cycle

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
