# TODO

STATUS: CONTRACT (machine-checked by TodoContractTests; format grammar at the bottom of this file)

The work ledger. "Now" holds what is actively being worked for the current release (hard cap 5,
each entry carries Done means + Verify). "Backlog" captures everything else at the cheapest
possible entry cost. Items EXIT this file only through docs/CHANGELOG.md, moved there in the
commit that ships or kills them. The full release ship gate stays in docs/RELEASE_SCOPE.md; Now
is the in-flight subset, not a mirror of that checklist.

## Now (release: 2.3.0)

- **[LW-4] Samurai Sword signature: Kiku-ichimonji Mushin** (opened 2026-07-04) [QUEUED]
  - Done means: Kiku-ichimonji id45 ships Mushin: a full WAIT (no move, no act) banks a buff so the
    wielder's next hit lands harder. Buff-hold is proven (StatHold, Iai's sibling pattern); the open
    risk is detecting a full wait live (tools/probes/acted_moved_watch.py, candidate combat +0x1BB),
    which gets a live probe BEFORE the build. Murasame id41's signature is deferred out of 2.3.0
    (LW-47). Release blocker (RELEASE_SCOPE.md section 1).
  - Verify: probe the wait signal live first, then items.json block, gen_living_weapon_meta.py, xUnit
    green, deploy and VERIFY LIVE, commit and LIVE_LEDGER flip. Clean DEV redeploy before ANY katana
    live test (an orphaned Zanshin DLL may still be deployed).

- **[LW-51] Kill-tally scoping and mod-update survival** (opened 2026-07-07) [QUEUED]
  - Done means: DECIDE global-forever vs per-playthrough (owner call; recommend per-playthrough for
    the growth fantasy). If per-playthrough, key kills.json / legends.json / gunslinger.json to a save
    identity with a one-time migration of the existing global file, so a NEW GAME no longer starts
    pre-maxed and two playthroughs never cross-contaminate. Same pass covers the LW-29 question: a
    Reloaded mod UPDATE (2.2.2 to 2.3.0) must not wipe the tally (relocate the save files outside the
    mod dir if an update replaces the folder).
  - Verify: unit-test the save-identity keying + migration; live, a new game starts fresh, a second
    playthrough keeps its own tally, and a simulated mod-folder replace preserves the tally.

- **[LW-37] Fast-paint the equip-card Kills meter via the item-text catalog redirect** (opened 2026-07-06) [QUEUED]
  - Done means: generalize the LW-31 catalog-record redirect (the attack-card mirror tech) to the
    ITEM-text records: census the item catalog, locate the viewed weapon's desc record, compose the
    body (the Kills meter first line) into a mod-owned buffer image, and repoint descOff at it under
    the same three-way anchor discipline (vanilla/current/previous, rotation only on a compose-change
    edge, foreign records refused). The equip card then updates instantly on first open and the slow
    heap sweep stops being that surface's paint path. Pulled into 2.3.0 by the owner 2026-07-07
    (RELEASE_SCOPE.md section 8).
  - Verify: unit-test the record decode and compose policy halves; live, first-open browse shows the
    correct Kills line with no latency, a battle-exit kill shows updated on re-open instantly,
    another item's vanilla desc stays untouched, and the in-battle Attack card surface is unaffected.

- **[LW-53] Flight archive for a fingerprint-guard stand-down** (opened 2026-07-07) [QUEUED]
  - Done means: a stand-down leaves a durable black-box archive, not just the livingweapon.log line.
    Observed 2026-07-07 (forced-mismatch launch): the loud ERROR armed FlushOnce but no
    flight_*_error.jsonl landed, because nothing is recorded into the ring while the guard holds the
    mod disarmed (every tapped subsystem is gated off pre-arm), so DrainPending flushes an empty ring
    (Flush no-ops on count 0). Record the guard lifecycle (arm and stand-down verdicts, with the
    landmark diag) into the flight ring so the FlushOnce archive is non-empty and self-contained, and
    confirm the drain path fires on the stand-down. Must NOT re-enable any game-memory write. Low
    severity: the stand-down is already fully evidenced in livingweapon.log; this is diagnostic
    completeness.
  - Verify: unit-test that a stand-down records a ring entry and a pending flush drains it to a file;
    live, force a mismatch (LW_FORCE_FINGERPRINT_MISMATCH=1) and confirm a flight_*_error.jsonl
    archive appears beside the loud line, with zero game-memory writes through a battle.

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
- [LW-34] 2026-07-05: The coverage line over-counts enemies ("All 8 enemies are accounted for"
  in a 4-enemy battle; owner report, log 22:43). Attribution itself was flawless that battle
  (all 4 kills credited cleanly, correct battle-end summary), so likely the EnemyOracle is
  counting band MIRROR-seat clones as distinct identities (or hidden/reinforcement units).
  Investigate: dump the oracle's identity set against the census in one battle; cosmetic
  unless the count gates something.
- [LW-28] 2026-07-05: A BuildLinked deploy LOST kills.json and legends.json despite the
  preservation round-trip (the 17:54 launch logged "No kill tally was found on disk"; the 82
  kill tally and the Beastbane Mark were gone; the %TEMP% livingweapon_preserve dir no longer
  exists). The 17:0x deploy preserved the same files fine, so the failure is intermittent.
  Second anomaly on the same evidence: the 17:1x session flushed exit tapes at 17:37/17:41 but
  kills.json kept its 13:45 timestamp, so exit-edge tally saves may not have written that
  session. Investigate both; add a loud post-restore existence check to BuildLinked (a deploy
  that loses a preserved file must fail red, not print success). Owner declined tally
  reconstruction for now (tapes and prev.log carry the counts if ever wanted).
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
- [LW-29] 2026-07-05: RELEASE QUESTION: do player save files (kills.json, legends.json,
  gunslinger.json) survive a Reloaded mod UPDATE (2.2.2 to 2.3.0)? If a mod update replaces
  the mod folder, every player loses their tally on upgrade, which is the worst possible bug
  for an attachment mod. Check Reloaded's update behavior; if unsafe, relocate the save files
  outside the mod directory (with a one-time migration read of the old location) BEFORE
  shipping 2.3.0.
- [LW-41] 2026-07-07: Re-anchor tools/probes/sentinel_probe.py (and audit sibling probes) to the
  1.5 offsets; it still reads the pre-1.5 addresses and fed garbage sentinels (battleMode=0,
  slot9=0x1) during the LW-40 live incident, nearly misdirecting the diagnosis. Source the
  addresses from LivingWeapon/Offsets.cs.
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
- [LW-46] 2026-07-07: Galewind's p3Desc still claims "No Lucavi" but IsDominatable is allow-everyone
  (no carve-out), so the card overpromises. Spun out of LW-5 (the expiry, shipped e882799, made the
  "for its full turn" clause accurate). Either drop the "No Lucavi" clause from items.json p3Desc and
  re-bake, or implement a Lucavi job carve-out in IsDominatable. Owner decision open.
- [LW-47] 2026-07-07: Murasame id41's living-weapon signature is deferred out of 2.3.0 (Kiku-ichimonji
  took the one samurai signature slot with Mushin); pick a proven lever and build it when revived.
- [LW-48] 2026-07-07: Append "Modded by prawl" to the in-battle "View Battlefield" UI label so it
  reads "View Battlefield - Modded by prawl" during a battle (a subtle mod-attribution touch). Likely
  mechanism: a SetTextString-family tap/prefix-match swap (PromptSwap precedent) or the text-catalog
  offset redirect (AttackCard/AttackRow precedent); find the "View Battlefield" string source first.

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
