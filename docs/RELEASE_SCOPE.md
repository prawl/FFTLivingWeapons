# Release Scope -- next release (consolidation)

STATUS: CONTRACT (locked scope for the 2.3.0 consolidation release)

Locked 2026-07-04. Current shipped version 2.2.2; proposed next **2.3.0** (owner confirms the bump).
Re-scoped 2026-07-14 (owner, in-session): the AnchorScan verifier scout (LW-82 v1, section 7) and
the 1.5.1-aftermath compat batch (section 10) are IN and required.

**Identity: "Finish the Samurai Swords + a focused item-balance tuning pass."** A consolidation
release -- land the two committed blockers, absorb ONE cheap high-value tuning batch, and DEFER every
removal and research/walled item. Scope grounded by a repo-wide triage of every open TODO + user-
feedback item (2026-07-04): each was ground-truthed against the actual code/data before landing here.

Two owner decisions set the stopping line:
- **Galewind expiry: SHIP WITH FALLBACK.** Try the round-7 AREC recon as a stretch; if a per-puppet-
  turn release does not crack, keep the committed wielder-clock behavior and reword the card to match.
  The release is never held hostage to an RE breakthrough.
- **Samurai "finished" = 3 signatures:** Iai + Kobu (done) + Mushin (Kiku id45). Murasame id41's
  signature is deferred out of this release. Capstones (Masamune id46 / Chirijiraden id47 / Sasori
  id70) stay pure-growth.

---

## IN -- ship gate (every box green = ship)

### 1. Samurai Swords (BLOCKER): finish = 3 signatures
Tuning is DONE and analyze.py-green; the work is the one open signature slot (Kiku's Mushin).
- [x] **Kiku-ichimonji id45** signature = **Mushin**: a full WAIT turn (no move, no act) arms one
      PA-boosted hit, spent on the wielder's next own action. Buff-hold is proven (StatHold, Iai's
      sibling pattern); the OPEN piece (detecting a full wait live) is CLOSED by the 2026-07-09
      mapping (tools/probes/mushin_wait_probe.py, scratchpad/psxflags_watch.log): the engine's own
      per-unit turn-open flag (band +0x19C) and its moved/acted latches (band +0x19D/+0x19E, both
      PSX-struct-derived) give a direct read of the wait, no aggregation over other units needed.
      Earlier same-day designs built on other units' CT cycling and KillTracker's action-latch
      machinery are retired in favor of this literal read. Ships as LW-4 (b8f6741, 2026-07-09).
- [x] **Murasame id41** signature is DEFERRED out of 2.3.0 (backlog LW-47); its capstone stays
      pure-growth for now. Owner scope call at lock, 2026-07-04.
- [x] The signature: items.json block -> gen_living_weapon_meta.py -> xUnit tests -> deploy ->
      **VERIFY LIVE** -> commit -> LIVE_LEDGER flip. Live-verify is non-negotiable (Zanshin
      graveyard: built green, LIVE-FAILED on the damage-intercept wall, reverted). Followed for
      Mushin, closed 2026-07-09.
- [x] Clean DEV redeploy before ANY katana live test (orphaned Zanshin DLL may still be deployed);
      done ahead of the Mushin live pass, 2026-07-09.

### 2. Galewind / Puppeteer expiry (BLOCKER) -- ship with fallback
Shipped behavior (LW-5, e882799, owner live-verified 2026-07-07; supersedes the wielder-clock
paragraph this section carried at lock time): the puppet releases after taking ITS OWN turn
(TurnQueue acted rising-falling edge on the puppet's seat; GlobalTurns cap backstop; 4-global-turn
cooldown), so the stretch goal below LANDED, via a different mechanism than the AREC recon it
proposed. The card reword shipped as LW-46 (the false "No Lucavi" clause dropped; "for its full
turn" became accurate with the own-turn release). Boxes stay for the owner sweep
(docs/SMOKE_TEST_2.3.0.md row 8.8).
- [ ] **Round-7 recon (STRETCH):** instrument-only build reading the AREC kind byte (band +0x184
      +0xA) + naming-span durations + puppet gx/gy on the puppet's OWN seat; ONE cleanly-ended
      battle. If it yields a reliable per-puppet-turn release -> land it (verify live first).
- [ ] **Fallback (GUARANTEED):** keep the wielder-clock release; reword the card to match. NEVER
      commit an expiry change without a live release observed in-game.
- [ ] Fix the card text regardless: p3Desc promises "no Lucavi" (IsDominatable allows every job) and
      "for its full turn" (does not hold) -- reword to shippable semantics.
- [ ] **Iai ReleaseSignal harden** -- same ActorPtr-dwell trap (Ame-no-Murakumo is a katana too);
      fix the bare-arrival false-release ONCE, alongside this turn-credit work.

### 3. Item-balance tuning pass (SHOULD) -- ONE analyze.py / patch_names.py batch, restart-only
- [x] **Rod nerf** (rods are over-tuned). Shipped dd45229, 2026-07-05.
- [x] **Added-Move nerf** (Trailwarden Jerkin + Wayfarer Boots -- too mobile too early). Give every
      de-Moved item a replacement dimension or analyze.py flags a dominated husk. Shipped dd45229,
      2026-07-05.
- [x] **Early-armor rider smell** -- retune riders that are dead weight at their tier (incl.
      Sanctguard id133 StrongElements=Holy, dead until T5-T6 Holy weapons). Shipped dd45229,
      2026-07-05.
- [x] **Claymore = CARD REWORD only** (working-as-designed per the ForcedTwoHands commit; a buff
      would WORSEN the off-class two-hander over-tuning feedback). Shipped dd45229, 2026-07-05.
- [x] Any name/desc change -> patch_names.py item.en.nxd re-bake (full-table replace, restart-only);
      this batch's own re-bake shipped dd45229, 2026-07-05.

### 4. Remove Offensive Chemist (NICE) -- S, INDEPENDENT of Treasure Master
- [x] Grenades (246-250) already out of items.json; scrub any residual refs; two nxd re-bakes via
      patch_names.py + patch_ability_names.py (never hand-edit cells -- Barrage shares ability key 358).
      Shipped a5ea61e, 2026-07-05.

### 5. Doc + hygiene (free)
- [x] Release note: non-English players get FULL gameplay (rebalance + growth + signatures); item
      TEXT stays vanilla-language and the Kills/+3 card counter is English-only. Shipped as LW-72
      (ba5e0fc, 2026-07-11).
- [x] Correct docs/USER_FEEDBACK.md: enemies inherit only the STATIC global rebalance, NOT the
      living-weapon runtime (growth/signatures/tally). Shipped c83700c, 2026-07-04.
- [x] Delete the falsified pointer-presence turn-detection code (rides the Galewind rework);
      closed by LW-71 (c2965ce, 2026-07-11).
- [x] Drop the dead `spriteIdOverride:1` on id67 Warbrand (items.json:2163). Shipped as LW-72
      (ba5e0fc, 2026-07-11).

### 6. Release gates (existing GO/NO-GO)
- [ ] analyze.py exit 0 (no dominated item).
- [ ] dotnet test green.
- [ ] Publish.ps1 clean, PROD thresholds {5,25,50}, no LWDEV / no seeding.
- [ ] Bump ModVersion (-> 2.3.0) + cut the matching tag.
- [ ] **ReleaseScopeContractTests gate (LW-84, owner-added 2026-07-14, next up in the queue)**:
      this scope file itself goes under test (the TodoContractTests enforcer pattern):
      an IN box naming an id that already exited to CHANGELOG.md must be ticked, a ticked box
      whose id is still open in TODO.md goes red, ticks cite a commit hash or date, and every
      LW-id cited here or in docs/SMOKE_TEST_2.3.0.md must exist in docs/TODO.md or
      docs/CHANGELOG.md. Lands with the one-time annotation pass ticking the already-shipped
      2.3.0 boxes with their hashes, so the gate is born green and smoke row 8.8 becomes
      re-verification. Land before the 8.8 sweep.

### 7. Save-integrity + patch-safety hardening (BLOCKER)
- [x] **Startup fingerprint guard (LW-50)**: verify three DATA-ONLY landmarks at launch (the PE build
      key, the JobCommand table's rec 8/rec 9 ability-byte signature, and Ramza's roster row shape);
      on a debounced mismatch disarm every write and log loudly. Turns a future game patch from
      silent save corruption into a clean "needs updating." RPM/WPM guard crashes, not semantic
      corruption at a valid-but-wrong address. Shipped 0152cf9, 2026-07-07.
- [x] **Kill-tally scoping (LW-51, covers LW-29)**: decide global-forever vs per-playthrough; if
      per-playthrough, key the save files to a save identity (one-time migration) so a new game is not
      pre-maxed and playthroughs do not cross-contaminate; ensure a Reloaded mod UPDATE does not wipe
      the tally. Shipped bf351db, 2026-07-09.
- [x] **AnchorScan verifier scout (LW-82 v1, owner-scoped in 2026-07-14; SHIPPED e77b9d7, merge
      f701795, owner live drill passed 2026-07-14)**: the dependency-free
      AnchorScan core plus the AnchorScout adapter. After any LaunchGuard stand-down, re-find the
      JobCommand table and the roster base by pin-neighborhood scan and log the re-find inventory
      (found at pin / elsewhere with delta / ambiguous / not found): the starting map for
      docs/PATCH_REANCHOR.md Phase B. Verifier scout only: no writes, no arming, no self-heal;
      consumers keep the Offsets pins. The live drill (marker-file stand-down on a dev build,
      smoke row 6.5) doubles as the eyewitness for the two 2026-07-14 LIVE_LEDGER premise rows.

### 8. Equip-card fast paint (SHOULD, pulled in by the owner 2026-07-07)
- [x] **Fast Kills meter (LW-37)**: retire the slow whole-heap Display sweep for the equip-card
      Kills meter. The LW-31 catalog-record REDIRECT is walled here (live recon 2026-07-07,
      tools/probes/item_text_census.py: the card re-materializes its description from a stable string
      pool each open; the FString descriptors are transient). PROVEN alternative (owner-verified
      live): overwrite the "Kills:" field IN PLACE in that pool (same-length, within its padded
      width) and the card re-materializes our bytes on open. Build the pool-anchored write: a cheap
      stable-substring anchor to the viewed weapon's pool entry, locate the Kills field, compose
      "Kills: N/T to +", overwrite. Unit tests for the pure halves plus a live first-open latency check.
      Shipped 7830def, 2026-07-08.

### 9. Configuration surface removal (SHOULD, pulled in by the owner 2026-07-07)
- [x] **Remove the remaining config options (LW-52)**: strip TreasureAlwaysOn, BannerToasts,
      DevSeedKills, and VerboseLog from the Reloaded config surface so players cannot toggle away
      designed behavior (the LW-50 force-mismatch knob removal set the precedent; dev levers move
      to environment variables). Owner may spare individual options during the build. Shipped
      50ae6b3, 2026-07-07.

### 10. Game-1.5.1 aftermath + ecosystem compat (owner-scoped in 2026-07-14)
- [ ] **Job-mod collision prune (LW-77)**: run the row-57 differential validation first (the
      handoff ladder; read the guard verdict in livingweapon.log before any manual diffing, the
      LW-83 methodology), then prune JobData.xml's unknown-id rows and audit JobCommandData.xml's
      record list so a single-field XML row stops erasing other mods' runtime job edits. Riders:
      the Nexus known-issues pin and marking the Old Files 1.x zips superseded.
- [ ] **Full-table nxd re-diff vs 1.5 vanilla (LW-78)**: re-diff the pre-1.5 item.en.nxd bake
      (and the parked ability bakes) against current vanilla; count unintended cell edits and
      check row-count parity (rows missing vs vanilla apply as RemovedRows). Offline work, no
      game session needed.
- [ ] **DESIGN.md compose-claim correction (LW-79)**: replace the stale "no interaction with
      Blue/Red Mages" claim (written before JobData.xml existed) with the pinned whole-row
      writeback mechanism; lands with LW-77's resolution.
- [ ] **File the upstream modloader issue (LW-80)**: the whole-row-writeback report with the
      dirty-field-writeback proposal (draft banked in the 2026-07-13 handoff action pack); the
      owner files it under his account. Fixes the LW-77 class ecosystem-wide once adopted.

---

## DEFERRED (post-release backlog)
- **Remove Treasure Master** -- L, works + tested, no user benefit this cycle; do as a dedicated
  cleanup. OBVIATES the Scholar's Ring idle-nag bug (do NOT fix that doomed code). On removal:
  de-list treasure.json from pipeline.ps1 + release.yml + csproj together; BattleState.BattleDisplayed
  is shared with CharmLock and must survive the cut; also drop Treasure Master from the ModConfig
  description.
- **Alter Axes and Flails** -- scope trap. Only cheap slice = Squire/Geomancer equip access on
  existing sword-typed items; PA*WP axe collides with the type-welded formula + id-welded art, flail
  band has no known formula id -- the rest is research.
- **Migrate lossy-detection siblings** (Maim / Larceny / Ricochet) to cache + rearm -- invisible
  tech-debt; do opportunistically when those files are next touched.
- **Kill-tally card milestones** beyond the counter -- redundant with the shipped milestone toasts;
  gated on an untested glyph-render probe.
- **Replace the Stormbrand** -- marginal (status procs are low-%); the real cure is an L runtime
  signature. Pick the theme AFTER the Samurai signatures lock (avoid a Slow/element dupe).
- **Enemies USE living-weapon benefits** -- XL undesigned feature; the player's real want is already
  delivered by the static rebalance.

## WALLED (not release work)
- **Sword swing-art** -- art is welded to the weapon id and the same render node drives DAMAGE
  (Warbrand computed as a Broadsword when swapped). No art-only lever; recat needs item-id relocation.
- **French item TEXT display** -- two independent live-confirmed walls (game loads item.nxd once under
  English; modloader nex parser crashes on the real French table). Only a DLL live-paint path remains.
