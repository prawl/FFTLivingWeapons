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

- **[LW-36] Re-bake every card description to the locked release grammar** (opened 2026-07-06) [QUEUED]
  - Done means: (owner direction 2026-07-06, update before release) BOTH card-text deliverables
    land in one voice. (1) The +3 ability block moves to the new grammar on every card: header
    "{Name} (+3)" replacing "+3 Ability - {Name}", body "{Verb} {effect}. {Condition, if
    any.}"; worked example: "Gun Slinger (+3)" then "Loads a second pistol into the off-hand,
    granting dual-wield. Must equip outside of battle." (2) The equipment card body LEADS
    with the Kills tier meter (SHIPPED cd6599e: meter as the first line, verbatim-identical
    to the Attack card, then a blank line, then flavor + mechanics and the ability block).
    The count lives in the BODY, not a header: the header arc (LW-27) is CUT (owner
    2026-07-06), so no "Kills: N" header stamp is built on any surface. Data-layer re-bake: items.json prose assembled by generate.py +
    patch_names.py, restart-only; confirm item.en.nxd descriptions render an embedded blank
    line at pickup; the attack-card tail (LW-31) adopts the same ability wording. (3) PINNED
    (owner 2026-07-06): analyze.py grows a gate check that every +3 ability desc MATCHES the
    master CSV (docs/living_weapon_grid.csv, the design source of truth); any drift between
    the baked prose and the CSV goes red and refuses the deploy.
  - Verify: analyze.py budgets green (DESC_MAX 266, P3DESC_MAX 90, uniqueness) plus the new
    CSV-match check; xUnit suite green; owner eyeballs the re-baked cards live before release.

- **[LW-31] The battle Abilities menu becomes the weapon funnel** (opened 2026-07-05) [BUILDING]
  - Done means: (owner-consolidated 2026-07-06) in battle, the Attack command row renders the
    acting unit's living weapon name with its growth suffix ("Save the Queen+" / Mettle /
    Items; the row text and the hover card's title share one string, so both update together);
    ANY resolve failure falls back to the vanilla "Attack" (explicit owner rule). The hover
    card becomes a mini equipment card: its brown header reads "Kills: XXXX" (the LW-27
    treatment; view detection here is the cursor resolve, already shipped), and its
    description body shows EXACTLY the equipment card's info (flavor line + the "+3 Ability"
    signature prose), NO Marks (hidden this release, LW-35) and no Kills line in the body (the
    count lives in the header). CONSTRAINT: the desc's in-place footprint is 73 chars vs equip
    descriptions up to 259, so the body-mirror needs either a trim policy or the stage-3
    pointer redirect (the record block above the strings), which is now REQUIRED rather than
    optional. OWNER ANSWERS 2026-07-06: row naming covers ALL weapons, not just living ones
    (bake the full weapon name table into meta from the data source); the "Kills: XXXX" header
    ships on the ATTACK CARD NOW (view detection = the shipped cursor resolve) and on the
    party-menu cards only after the hovered-item probe (LW-27); Marks hide on EVERY surface
    (LW-35). SQUEEZE RULE (owner answer 2026-07-06): ability-first: if a footprint forces a
    cut, the "+3 Ability" info survives and the flavor prose drops. UNIFIED CARD GRAMMAR
    (owner, with screenshot): the SAME design applies to the party Equipment and Abilities
    card AND the battle Attack card: header = "Kills: XXXX", body = flavor + mechanics + the
    ability block, no Marks (LW-35), and the body's old Kills line RETIRES on both (the count
    moves to the header; the equip-card side means a baked-description re-bake via
    patch_names.py, restart-only, and frees DESC_MAX budget). EMPTY HANDS (owner rule 2026-07-05):
    a unit with no weapon equipped (Monks, barehanded anyone) shows "Fist" in the row, no
    suffix, no tally, vanilla desc. A non-living weapon keeps vanilla text in v1 (naming every
    item would need the full name table baked into meta; noted as a cheap later extension).
    DUAL WIELD (owner rule 2026-07-05): the row and hover title show the MAIN HAND only; when
    the off-hand differs and has its own story, the desc gains an off-hand clause ("Koga Blade
    off-hand: 8.") under the normal clause-drop budget rules; same-weapon pairs are one id and
    need nothing special. Staged: (1) AttackCardSpike census (HeaderSpike blueprint, F6 co-fire): dump the
    packed "Attack" tables (canonical desc text, copy count, encodings, rebuild cadence), and
    live-test a footprint-safe desc write with a revert watch; (2) ship the desc painter (the
    kills home); (3) crack the row rename, since "Attack" is a 6-char in-place prison:
    candidates are the SetTextString-family swap (PromptSwap precedent) or the battle-menu
    ctor inline hook (documented recipe; Denuvo dead-hook canary required); read the
    battle-menu architecture notes before stage 3.
  - Verify: pure halves unit-tested per stage; owner eyeballs each stage live before commit;
    LIVE_LEDGER rows for the new mechanics (menu-table desc write holds; row-rename mechanism).
  - Notes: owner discovery + CE adjudication 2026-07-05 (screenshots on file): the two CE addrs
    are ONE packed buffer, "Attack" plus NUL then the desc (delta exactly 7); an overlong row
    write ate the NUL and a desc write then truncated it to "Save th4", proving adjacency; the
    row and card title render from the same string; multiple copies exist. Owner: highest
    priority. LAYOUT VERIFIED 2026-07-05 (owner screenshot): the worst-case name
    "Zwill Straightblade+3" (21 chars) renders clean and unclipped in both the row and the
    hover title, so stage 3 needs no truncation policy: full name always. STAGE 2 LIVE PASS
    2026-07-05 22:4x: census/compose/paint/tally/Marks all worked on screen (owner screenshot
    of the full dossier), but FAILED on attribution timing: the dossier shows the PREVIOUS
    actor's weapon at menu time, because the actor register only arrives on a unit when it
    ACTS and the menu is hovered before acting (each unit's card wears the last actor's
    weapon). Stage 2 stays UNCOMMITTED until fixed. Fix needs a LEADING turn-owner signal at
    menu time: probe candidates are the scheduler CT ceiling (proven offset, one player at the
    ceiling during their menu?) or the cursor-follower struct (sits on the actor while their
    own menu is open?); both need a live probe before trusting. The painter's conservative
    null default stays for pre-first-action. STAGE 3 MECHANISM CRACKED 2026-07-06 (owner
    eyewitness, screenshot on file): the row and hover title rename via ONE u32 offset
    redirect in the JobCommand text catalog record (id 1, base = label minus 0x1FC1, nameOff
    and descOff sibling fields); arbitrary length renders (row shrinks to fit, title
    scrolls); the menu caches at build so writes land on the next menu open (turn-open
    timing suffices); restore is the same u32. LIVE_LEDGER row added, awaiting the owner
    flip. The desc body mirror rides the sibling descOff. Instruments:
    tools/probes/attack_table_scan.py and tools/probes/attack_row_redirect.py. OWNER
    WORDING LOCKS 2026-07-06 (stage-3 live pass): barehanded row text is "Fists"; the kills
    clause is a tier-progress meter ("Kills: 1/5 to +", "Kills: 6/25 to +2",
    "Kills: 34/50 to +3", then "Kills: 55" at max) plus a signature tease while locked
    ("Unlocks Gun Slinger.") that flips to the armed clause when earned. HEADER ARC CUT
    2026-07-06 (owner): the "Kills: XXXX" header stamp is dropped. The Kills count lives in
    the card BODY as the tier meter, NOT the header (the equip card leads its body with the
    meter, SHIPPED cd6599e; the attack card already carries the meter in its body). This
    SUPERSEDES every "brown header reads Kills: XXXX", "count lives in the header", and
    "body's old Kills line retires" phrasing above; the body keeps its meter plus the
    signature state ("Unlocks Gun Slinger." locked, "Gun Slinger armed." earned). Monsters keep vanilla
    text on every surface.

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
- [LW-35] 2026-07-06: HIDE the Marks feature for this release (owner direction: "we'll finish
  implementing that in a future release"): one flag suppresses every Mark surface: the
  equip-card story narration (Reliquary Phase 1 lines), the Mark deed toasts, and the
  attack-card Mark clause. Kill/legends COLLECTION keeps running (the chronicle keeps
  recording; only the display hides), so the two-wave build (LW-32) re-enables display over
  history that never stopped accumulating. Release-scoped: pull into Now when a slot frees.
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
- [LW-37] 2026-07-06: FAST-paint the equip-card BODY Kills meter. The count now ships in the
  body first line (cd6599e), painted by the slow heap sweep; first-open browse latency is the
  only enemy, and kills change only in battle so a battle-exit repaint is never stale. The
  header-stamp candidate is MOOT now the count lives in the body (header arc cut, LW-27
  retracted). Remaining option: generalize the 2026-07-06 catalog-record discovery to the
  ITEM-text records and redirect the desc offset at a mod-owned buffer (instant updates,
  retires the sweep; same tech the attack-card mirror uses). Low priority: the slow sweep
  works and the owner accepted out-of-battle paint latency.
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
