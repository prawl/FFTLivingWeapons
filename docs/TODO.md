# TODO

STATUS: CONTRACT (machine-checked by TodoContractTests; format grammar at the bottom of this file)

The work ledger. "Now" holds what is actively being worked for the current release (hard cap 5,
each entry carries Done means + Verify). "Backlog" captures everything else at the cheapest
possible entry cost. Items EXIT this file only through docs/CHANGELOG.md, moved there in the
commit that ships or kills them. The full release ship gate stays in docs/RELEASE_SCOPE.md; Now
is the in-flight subset, not a mirror of that checklist.

## Now (release: 2.3.0)

- **[LW-1] Unarmed stale latch eats an armed player's kill (Boco/Phoenix Down)** (opened 2026-07-05) [QUEUED]
  - Done means: the bury branch (KillTracker.Corpses.cs, _latchResolvedEmpty && _latched) consults
    KillerStamp.Decide; a fresh differing ARMED hypothesis becomes a Register override, everything
    else still buries (a dancer/summoner is her own empty hypothesis, so designed no-credits are
    unaffected). Recovers both the kill tally and the Reliquary deed (this ate the first undead
    Requiem test kill).
  - Verify: KillTracker bury-branch unit tests green; live check is VERIFY_LIVE.md row 8 (the
    undead Requiem path).
  - Notes: owner-verified 2026-07-05 13:40, log on file. Boco the chocobo acted, the actor
    pointer stayed parked on him when Ramza's acted period opened (the known pointer-at-edge
    lag), so the latch resolved "acting player, no living weapon" and froze; Ramza's Phoenix
    Down skeleton kill was then buried without consulting the actor register, which by then
    named Ramza (armed, fresh). The armed-latch sibling of this race is already fixed (the
    KillerStamp death-edge stamp, f4bf5df).

- **[LW-2] Deploy the shipped batch and run the live verification script** (opened 2026-07-05) [BLOCKED(owner live session)]
  - Done means: kill fft_enhanced.exe, run BuildLinked.ps1, then docs/VERIFY_LIVE.md rows 6-12
    pass, including the row-11 log-facelift protocol (armed battle 8-14 console lines, unarmed
    battle exactly 2 bookends, file cross-check for evidence thinning, fast-forward soak for
    the console-sink lock).
  - Verify: VERIFY_LIVE.md checkboxes (owner-only flips). Row 8 waits on LW-1.

- **[LW-3] Docs three-tier reorg** (opened 2026-07-05) [BUILDING]
  - Done means: docs/ top level holds living contracts only; closed journals move to
    docs/research/; shipped or dead one-shot plans move to docs/archive/; every doc opens with
    a status line (CONTRACT, JOURNAL, or ARCHIVED with its successor named); plan docs archive
    in the commit that ships them; executed with git mv plus a sweep of references (code
    comments, tools, memories cite old paths).
  - Verify: the docs-map lockstep test lands green; git log with follow shows history preserved
    on the moved docs.

- **[LW-4] Samurai Sword signatures: Murasame + Kiku-ichimonji** (opened 2026-07-04) [QUEUED]
  - Done means: Murasame id41 ships Masamune's Mercy (brave-gated heal, proven lever; AVOID
    Mushin, the parked wait-detection byte hunt); Kiku-ichimonji id45 ships Onryo (Undead brand)
    or Shura (controllable Berserk on 2nd kill, bit +0x47/0x08). Release blocker 1 of 2
    (RELEASE_SCOPE.md section 1).
  - Verify: items.json block, then gen_living_weapon_meta.py, then xUnit green, then deploy and
    VERIFY LIVE, then commit and LIVE_LEDGER flip. Clean DEV redeploy before ANY katana live
    test (an orphaned Zanshin DLL may still be deployed).

- **[LW-5] Galewind / Puppeteer expiry, ship with fallback** (opened 2026-07-04) [QUEUED]
  - Done means: round-7 AREC recon attempted as a stretch; if a per-puppet-turn release does not
    crack, keep the committed wielder-clock behavior and reword the card to match. The p3Desc
    card promises ("no Lucavi", "for its full turn") are fixed either way. The Iai ReleaseSignal
    bare-arrival false-release is hardened in the same touch (same ActorPtr-dwell trap).
    Release blocker 2 of 2 (RELEASE_SCOPE.md section 2).
  - Verify: never commit an expiry change without a live release observed in-game; xUnit green.

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
  the cut; drop Treasure Master from the ModConfig description.
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
- [LW-20] 2026-07-05: LoggerTests flake: Two_different_verbs_sharing_the_same_Info_sentence_both_reach_the_console
  (LoggerTests.cs:148) compares two rendered console lines that embed wall-clock ms timestamps;
  a millisecond-boundary straddle fails it (observed once in a clean tree, green on rerun).
  Fix: freeze or strip the timestamp in the assertion. Until then a red suite can be this flake
  and a green suite can be luck; check here first before blaming a real change.
- [LW-21] 2026-07-05: Harden TodoContractTests' changelog scan: the grammar check only inspects
  lines starting with "- [", so a mangled exit line (say "- LW-22 ...") dodges both the grammar
  and the id-uniqueness gates. Scan every top-level "- " line in CHANGELOG.md the way the
  Backlog check does, and tighten NowEntryRegex's greedy title match while in there.
- [LW-22] 2026-07-05: The launch header (Engine.cs:81) does not pluralize its Marks count, so
  "1 Marks" prints when TotalMarks is 1, and the LOGGING.md launch-header example faithfully
  showcases the bad grammar. BattleSummary.Compose pluralizes correctly; only the header
  emitter was missed. One string plus the doc example; fold into the next round that touches
  Engine.cs or LOGGING.md.

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
- Items exit ONLY by moving to docs/CHANGELOG.md, in the very commit that ships or kills them.
- No em dashes and no double-dash separators anywhere in this file or the changelog.
- AWAITING-LIVE flips and VERIFY_LIVE checkboxes are owner-only.
