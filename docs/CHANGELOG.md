# Changelog (work-ledger exits)

STATUS: CONTRACT (machine-checked by TodoContractTests)

Where docs/TODO.md items land when they ship, die, or retract; newest first within a cycle.
Entry first line: `- [LW-<n>] SHIPPED <hash> YYYY-MM-DD: <summary>`, or WONTFIX / RETRACTED
with a date and no hash.

## 2.3.0 cycle

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
