# TODO

STATUS: CONTRACT (machine-checked by TodoContractTests; format grammar at the bottom of this file)

The work ledger. "Now" holds what is actively being worked for the current release (hard cap 5,
each entry carries Done means + Verify). "Backlog" captures everything else at the cheapest
possible entry cost. Items EXIT this file only through docs/CHANGELOG.md, moved there in the
commit that ships or kills them. The full release ship gate stays in docs/RELEASE_SCOPE.md; Now
is the in-flight subset, not a mirror of that checklist.

## Now (release: 2.3.0)

- **[LW-2] Deploy the shipped batch and run the live verification script** (opened 2026-07-05) [AWAITING-LIVE]
  - Done means: kill fft_enhanced.exe, run BuildLinked.ps1, then docs/VERIFY_LIVE.md rows 6-12
    pass, including the row-11 log-facelift protocol (armed battle 8-14 console lines, unarmed
    battle exactly 2 bookends, file cross-check for evidence thinning, fast-forward soak for
    the console-sink lock).
  - Verify: VERIFY_LIVE.md checkboxes (owner-only flips). Row 8 waits on LW-1.
  - Notes: owner verified 2026-07-05 during this pass: dual-pistol off-hand equip works, the
    second Outrider Pistol equipped and fired.

- **[LW-27] Marks move to the equipment card's header bar** (opened 2026-07-05) [BUILDING]
  - Done means: the equipment card's brown "Description" header paints per-weapon (target
    wording shape: "Kills: 100 - Mageslayer") through the card painter with paint-time
    ownership verification, in both text encodings, without bleeding onto ability cards or
    other items' cards; the description body drops the Mark prose (which also relieves LW-26).
    First stage is a HeaderSpike dev instrument (ShowSpike/FlavorSpike pattern) proving the
    header spots are locatable, paintable, and hold across card refreshes.
  - Verify: pure pattern/compose halves unit-tested; owner eyeballs the spike live; the
    header-spot mechanic gets a LIVE_LEDGER row before the production painter ships.
  - Notes: owner CE prototype 2026-07-05: plain-string edits take on the ability card but
    never the equipment card (per-widget copies, encodings, and card-refresh re-sets; see the
    backlog history in docs/CHANGELOG.md when this exits).

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
- [LW-25] 2026-07-05: The show-spike F5 dev hotkey is still live in DEV builds (owner tripped
  it while testing). Spike research shipped (callout spawn cracked); disable or gate the hotkey
  and its console chatter so a test pass cannot trigger it by accident.
- [LW-26] 2026-07-05: The Outrider Pistol assembled card sits at the visual edge of too long
  (owner eyeball, within the DESC_MAX=259 budget). Trim its prose (items.json flavor or
  signature line), and consider whether the budget needs a safety margin when a Marks line
  is present.
- [LW-28] 2026-07-05: A BuildLinked deploy LOST kills.json and legends.json despite the
  preservation round-trip (the 17:54 launch logged "No kill tally was found on disk"; the 82
  kill tally and the Beastbane Mark were gone; the %TEMP% livingweapon_preserve dir no longer
  exists). The 17:0x deploy preserved the same files fine, so the failure is intermittent.
  Second anomaly on the same evidence: the 17:1x session flushed exit tapes at 17:37/17:41 but
  kills.json kept its 13:45 timestamp, so exit-edge tally saves may not have written that
  session. Investigate both; add a loud post-restore existence check to BuildLinked (a deploy
  that loses a preserved file must fail red, not print success). Owner declined tally
  reconstruction for now (tapes and prev.log carry the counts if ever wanted).
- [LW-30] 2026-07-05: Weapon reputation in the ATTACK-TARGETING prompt (owner discovery, screenshot
  on file): the "Select a target and press F to confirm" pill during Attack targeting is writable
  text in the same prompt family as the Wait-state facing prompt, and the owner's CE edit rendered
  in it live ("Kills: 100" shown mid-targeting). Feature: PromptSwap gains a "Select a target"
  prefix match and swaps in the acting unit's weapon line ("Windrunner, Mageslayer: 103 kills"),
  composed from the turn owner's equipped living weapon; swap ONLY when a living weapon has a
  story (kills or a Mark), vanilla text otherwise. Renders at the moment of aiming, no timing or
  hold problems (the game triggers the render, we swap content). Candidate FLAGSHIP placement for
  the kills+trait surface, ahead of the header bar for in-battle presence; the header bar (LW-27)
  stays the menu-side surface. Verify per prompt-swap house rules before shipping.
- [LW-29] 2026-07-05: RELEASE QUESTION: do player save files (kills.json, legends.json,
  gunslinger.json) survive a Reloaded mod UPDATE (2.2.2 to 2.3.0)? If a mod update replaces
  the mod folder, every player loses their tally on upgrade, which is the worst possible bug
  for an attachment mod. Check Reloaded's update behavior; if unsafe, relocate the save files
  outside the mod directory (with a one-time migration read of the old location) BEFORE
  shipping 2.3.0.

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
