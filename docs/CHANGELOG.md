# Changelog (work-ledger exits)

STATUS: CONTRACT (machine-checked by TodoContractTests)

Where docs/TODO.md items land when they ship, die, or retract; newest first within a cycle.
Entry first line: `- [LW-<n>] SHIPPED <hash> YYYY-MM-DD: <summary>`, or WONTFIX / RETRACTED
with a date and no hash.

## 2.3.0 cycle

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
