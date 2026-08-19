# Release Scope -- next release (consolidation)

STATUS: CONTRACT (locked scope for the 2.3.0 consolidation release, the 2.3.1 patch cut, the
2.3.2 game-compatibility cut, the 2.3.3 feature cut, and the 2.4.0 art cut)

Locked 2026-07-04. Current shipped version 2.2.2; proposed next **2.3.0** (owner confirms the bump).
2.3.0 shipped 2026-07-16 (tag v2.3.0). The **2.3.1** patch cut (section at the bottom) followed on
2026-07-21 with the two post-release bug clusters; the last 2.3.0 box (LW-80, the upstream
modloader report) closed 2026-07-21: the owner delivered it to the author by direct contact
instead of a public issue (exited RETRACTED in CHANGELOG.md).
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
(docs/archive/SMOKE_TEST_2.3.0.md row 8.8).
- [x] **Round-7 recon (STRETCH):** instrument-only build reading the AREC kind byte (band +0x184
      +0xA) + naming-span durations + puppet gx/gy on the puppet's OWN seat; ONE cleanly-ended
      battle. If it yields a reliable per-puppet-turn release -> land it (verify live first).
      (Ticked in the 8.8 owner sweep 2026-07-16: the stretch goal landed via a different
      mechanism, the TurnQueue own-turn release, LW-5 e882799, owner live-verified 2026-07-07
      and re-confirmed in smoke rows 3.1-3.3 on 2026-07-15; the AREC recon was never needed.)
- [x] **Fallback (GUARANTEED):** keep the wielder-clock release; reword the card to match. NEVER
      commit an expiry change without a live release observed in-game.
      (Ticked in the 8.8 owner sweep 2026-07-16: moot, the own-turn release shipped, was
      live-verified, and held through the whole smoke pass; the fallback was never invoked.)
- [x] Fix the card text regardless: p3Desc promises "no Lucavi" (IsDominatable allows every job) and
      "for its full turn" (does not hold) -- reword to shippable semantics.
      (Ticked in the 8.8 owner sweep 2026-07-16: shipped as LW-46, the false no-Lucavi clause
      dropped and the full-turn wording made accurate by the own-turn release.)
- [x] **Iai ReleaseSignal harden** -- same ActorPtr-dwell trap (Ame-no-Murakumo is a katana too);
      fix the bare-arrival false-release ONCE, alongside this turn-credit work.
      (Ticked in the 8.8 owner sweep 2026-07-16: shipped as LW-71, c2965ce, owner live-verified
      2026-07-11; the release names the turn flags, re-confirmed in smoke rows 2.1 and 2.2.)

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
- [x] analyze.py exit 0 (no dominated item). (Final ship run 2026-07-16: exit 0, standalone and
      inside Publish.)
- [x] dotnet test green. (Final ship run 2026-07-16: 2565 passed, 0 failed.)
- [x] Publish.ps1 clean, PROD thresholds {5,25,50}, no LWDEV / no seeding. (2026-07-16: package
      verify PASS on every required entry, FFTLivingWeapons-2.3.0.zip, 5.18 MB.)
- [x] Bump ModVersion (-> 2.3.0) + cut the matching tag. (2026-07-16: version bumped and the
      disarmed Treasure Master clause dropped from the description; tag v2.3.0 cut at the
      pass-close commit.)
- [x] **ReleaseScopeContractTests gate (LW-84, owner-added 2026-07-14)**:
      this scope file itself goes under test (the TodoContractTests enforcer pattern):
      an IN box naming an id that already exited to CHANGELOG.md must be ticked, a ticked box
      whose id is still open in TODO.md goes red, ticks cite a commit hash or date, and every
      LW-id cited here or in docs/archive/SMOKE_TEST_2.3.0.md must exist in docs/TODO.md or
      docs/CHANGELOG.md. Lands with the one-time annotation pass ticking the already-shipped
      2.3.0 boxes with their hashes, so the gate is born green and smoke row 8.8 becomes
      re-verification. Shipped 008dd35 2026-07-14, ahead of the 8.8 sweep.

### 7. Save-integrity + patch-safety hardening (BLOCKER)
- [x] **Startup fingerprint guard (LW-50)**: verify three DATA-ONLY landmarks at launch (the PE build
      key, the JobCommand table's rec 8/rec 9 ability-byte signature, and Ramza's roster row shape);
      on a debounced mismatch disarm every write and log loudly. Turns a future game patch from
      silent save corruption into a clean "needs updating." RPM/WPM guard crashes, not semantic
      corruption at a valid-but-wrong address. Shipped 0152cf9, 2026-07-07. (Detail superseded
      2026-07-28, the mod-conflict guard split: the JobCommand landmark moved to a kit-lane guard
      that disables only the weapon-granted commands when the PE key still matches; the PE key and
      roster landmarks keep the full stand-down. The in-flight ticket in docs/TODO.md carries the
      id.)
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
- [x] **Production Scholar's Ring grant killed (LW-86, owner-scoped in 2026-07-14; SHIPPED
      fe30e1f, owner live-verified 2026-07-14 via smoke row 7.22)**: ScholarRing.Grant
      compiles out of production builds (LWDEV-only), so 2.3.0 stops writing a free ring into player
      saves for the disarmed, removal-slated Treasure Master module (the 2026-07-11 fresh-save grant
      incident); live evidence rides smoke row 7.22.

### 8. Equip-card fast paint (SHOULD, pulled in by the owner 2026-07-07)
- [x] **Fast Kills meter (LW-37)**: retire the slow whole-heap Display sweep for the equip-card
      Kills meter. The LW-31 catalog-record REDIRECT is walled here (live recon 2026-07-07,
      tools/probes/item_text_census.py: the card re-materializes its description from a stable string
      pool each open; the FString descriptors are transient). Working alternative (owner-observed
      live, no LIVE_LEDGER row): overwrite the "Kills:" field IN PLACE in that pool (same-length, within its padded
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
- [x] **Job-mod collision prune (LW-77; SHIPPED 2a4c325, owner live-verified 2026-07-14 via
      smoke row 7.29)**: validation done 2026-07-14 (the row-57 differential
      ladder; the guard read armed throughout). JobData.xml now lists only rows carrying a real
      behavioral payload (the 28-id keep set); mod JobCommandData.xml is deleted, its sole
      payload (zeroing the dead-JP Equip Axes RSM slot) replaced by one ability.en.nxd
      Description cell on key 460. Riders kept: the Nexus known-issues pin, marking the Old
      Files 1.x zips superseded, and the upgrade note (in-place upgraders must delete the old
      mod folder so a stale JobCommandData.xml from 2.2.2 does not silently retain the
      collision); the riders travel with the ship notes (owner at ship time).
- [x] **Full-table nxd re-diff vs 1.5 vanilla (LW-78)**: re-diff the pre-1.5 item.en.nxd and
      ability.en.nxd bakes against current vanilla; count unintended cell edits and check
      row-count parity (rows missing vs vanilla apply as RemovedRows). Done 91b230b + b9777d6
      2026-07-14: 111 stale cells found (the whole 1.5.x ability-text delta plus the item menu
      re-sorts and the Leather Helm recategorization; premise owner-observed live on the Padded
      Coif card) and rebased away; tools/audit_nxd_bakes.py stays red on any future unintended
      cell and reruns per game patch.
- [x] **DESIGN.md compose-claim correction (LW-79; SHIPPED 2a4c325, exited edc117f
      2026-07-14)**: replace the stale "no interaction with
      Blue/Red Mages" claim (written before JobData.xml existed) with the pinned whole-row
      writeback mechanism; lands with LW-77's resolution.
- [x] **File the upstream modloader issue (LW-80)**: the whole-row-writeback report with the
      dirty-field-writeback proposal (draft banked in the 2026-07-13 handoff action pack); the
      owner files it under his account. Fixes the LW-77 class ecosystem-wide once adopted.
      (Closed 2026-07-21: the owner delivered the report to the modloader author by direct
      contact instead of a public issue; exited RETRACTED in CHANGELOG.md the same day.)

---

## 2.3.1 patch cut (2026-07-21) -- the two post-2.3.0 bug clusters, no new features

- [x] **Roster window (LW-96)**: soldiers past number 20 on the party list earn kills and growth
      like everyone else (the "only Ramza and generics benefit" report). Shipped 18b983f,
      owner live-verified 2026-07-21 (slot 46 credit on tape flight_20260721_044145).
- [x] **Stale menu text cluster (LW-91 + LW-98 + LW-88)**: battle menus no longer wear the
      previous unit's weapon name, bare fists never show someone else's weapon, the kill count
      no longer freezes, and the equip card's Kills meter updates on kills mid-battle. Shipped
      5136f2e + 10320b2, ledger exits 519204d; owner live pass 2026-07-21 (07:24 battle) clean.
- [x] Gates re-run at the cut: dotnet test 2584 green, analyze.py exit 0 (2026-07-21).

## 2.3.2 compat cut (2026-07-25) -- game 1.5.2 compatibility ONLY, no new features

The game updated to 1.5.2 and the mod switched itself off to protect saves, which is what the
startup guard is for. This cut exists to make the mod work again on the updated game, and to do
nothing else. Owner scope call 2026-07-25, in-session.

- [x] **1.5.2 re-anchor (LW-132)**: the mod recognises the new game build and arms again. Almost
      nothing moved; the audit was done offline by diffing the two executables. Shipped 9959821.
      Damage report: docs/research/PORT_1.5.2_OFFSETS.md.
- [x] **Provoke ships DISARMED (LW-133)**: shipped 6f1e21a, 2026-07-25. The shout is held back
      because nobody has played it yet and its acceptance pass (docs/PROVOKE_AC.md) has never been
      run, so a compat cut must not carry it to players. Three parts, all required together: the
      Defender's items.json signature block is removed (no granted command), item.en.nxd is rebaked
      so the equip card stops advertising Provoke, and Tuning.ProvokeEnabled gates the hold, which
      reads the mark bit rather than the signature and would otherwise stay live. The ability/status
      text rows stay baked: both are unreachable in vanilla (ability 189 is a cut ability,
      UIStatusEffect Key 1 ships blank), so they are invisible with the grant gone. The feature's
      own ticket stays open and BUILDING; nothing was reverted, only gated.
- [x] **Owner live pass** (2026-07-25, 01:52 launch / 01:54 battle exit): armed with no stand-down,
      two kills credited with victim identity, clean battle exit, no Provoke command and no hide
      lines. The moved hook is confirmed by its own canary intercepting the session's first prompt
      and reading real text. scan_logs --require-battle --flight exit 0, zero warnings. The equip
      card was owner-confirmed by eye (it leaves no log line the scanner reads).
- [x] Gates re-run at the cut: dotnet test 2752 green, analyze.py exit 0, audit_nxd_bakes.py exit 0
      (2026-07-25).

## 2.3.3 feature cut (2026-08-12) -- Living Poach, Crossfire, and the softened growth curve

The cycle's theme: weapons that do more than grow. Owner scope call 2026-08-12, in-session.
The cut was preceded the same evening by an owner regression night covering the two riskiest
shipped mechanics plus a random signature sample, and by a prod-flavor eyeball of the new
kill curve.

- [x] **Living Poach (LW-167)**: weapons rebuilt on the game's dormant damage formulas can
      Poach again, end to end (job map, credit seam, corpse despawn, basic-Attack
      discriminator, arming). Shipped across five stages ending ac43327; owner live pass
      completed 2026-08-12; exited 323b393. Rider shipped with it: the LW-166 "No
      Poaching." notice and its later reversal to clean cards (8aa4d8e). The alias-row map
      fix (35d998c) also ships in this cut; its ticket stays open in Now, see the prose
      below.
- [x] **Crossfire (LW-171)**: the Arbalest loads a twin and fires twice at +3, on the proven
      Gun Slinger lane made N-weapon and data-driven. Shipped 4bd256b, owner live pass the
      same evening (twin rendered, double Attack, kill credited), exited 506360a.
- [x] **Kill curve softened (LW-161)**: production tier thresholds {5,25,50} became
      {5,10,15}; the prod flavor was eyeballed live at the cut (a 13-kill Defender read
      "Kills: 13/15 to +3" on screen, 2026-08-12).
- [x] **Support-slot Key model (LW-168)**: the roster picked-support is a u16 ability Key
      end to end; a bare unit receives Dual Wield correctly and unequipping restores a true
      empty. Shipped fd7274b, owner live pass 2026-08-12.
- [x] **Card counters survive quick reloads (LW-163)**: the equip-card paint latch re-arms
      after mid-session save loads. Shipped 303ddf6, owner-passed.
- [x] **Compatibility grid (LW-169)**: docs/COMPATIBILITY.md public with every verdict
      settled and the Nexus description linking it. Exited e8f90ce citing dd36845.
- [x] **Owner regression night (2026-08-12, pre-tag)**: Provoke full acceptance pass (three
      hold cycles, re-cast on a released enemy, player-side mark self-scrub, ETA-driven
      hides throughout); Bulwark V1, V5, and the V6 path-around observed live (an AI unit
      routed around the barred tile); three randomly drawn signatures spot-checked green
      (Attack Boost, Kobu, Choir); eleven LIVE_LEDGER rows flipped Proven on the night;
      closing session scan CLEAN with every warning dispositioned to a ledger row.

Known and accepted at the cut, deliberately shipped as-is (open rows, prose on purpose):
the first equip-card paint lands about 11 seconds after arming, so a party menu opened
immediately after a save load briefly shows the baked zero counts (LW-165 stays open for
the Steam Deck reading; the README carries the player note); the kill-credit census can
warn about a vanished enemy seat in undead fights with no credit loss observed (LW-180);
a Provoke hold under held fast-forward can outlive its enemy turn and fall to the
90-second watchdog (LW-143, bounded, one confirmed instance); the poach despawn watchdog
carries a bounded delayed double-payout residual awaiting its fix-shape call (LW-175); and
the alias-row map fix ships while its ticket LW-174 stays open in Now for the one
unverified link, the staged-encounter premise beat (a live band job byte reading 169-173),
or the owner's call to accept it as a standing watch.

- [x] Gates at the cut (2026-08-12): analyze exit 0 and the full test suite green inside
      Publish.ps1; package verify PASS on every required entry
      (FFTLivingWeapons-2.3.3.zip); ModVersion bumped to 2.3.3; no parked artifacts
      packaged.
- [x] Tag v2.3.3 cut at the release commit b7104c0 and pushed (2026-08-12, owner word in
      session).

## 2.4.0 art cut (scoped 2026-08-19) -- every icon reworked

Owner scope call 2026-08-19, in session: every icon reworked for the next deploy. Plain version
of what that means: today a player opening the equip menu sees two different eras of artwork
sitting side by side. Roughly two thirds of the items wear the new colouring and look like a set;
the rest still wear a flat one colour stamp applied back in May, and six items were never
coloured at all. This cut finishes the job so the whole catalogue reads as one wardrobe, and it
hardens the checks that prove a rebake of that size did not quietly damage anything.

Grounded 2026-08-19 by a full inventory of all 240 item rows against the shipped art and the
engine each one routes to: 150 on the current ramp engine, 12 on the superseded three zone
engine, 72 on the legacy stamp, 6 with no art at all. The numbers below come from that inventory
rather than from the ticket prose, which had drifted in several places.

**Identity: finish the wardrobe, and make the machine prove it.** 90 items get new art, the four
already built families get their owner look, and the checks that catch collateral damage stop
being things a person has to remember.

### 1. The 90 remaining items (BLOCKER, the release's whole point)
- [ ] **The 12 hats (LW-288)**: the only family still on the superseded three zone engine. The
      owner did pass them in game, but under the older method, so they will drift visibly from
      their neighbours once the rest of the wardrobe is redone. This ticket did not exist before
      2026-08-19; the inventory found the gap between LW-248's own sentence naming hats as
      remaining colour work and the ledger.
- [ ] **The 72 legacy one hue items (LW-217, LW-218, LW-219, LW-220, LW-221, LW-222, LW-223,
      LW-224, LW-225, LW-226)**: hair adornments 3, armour 14, clothing 14, robes 8, shoes 7,
      armguards 4, rings 6, armlets 5, cloaks 7, perfumes 4. Art untouched since ac756d1 on
      2026-05-30.
- [ ] **The 6 never coloured throwing weapons and bombs (LW-214)**: a first colouring, not a re
      pass, and the only item in this cut that ADDS files. The deploy ships 480 .tex, not 468, so
      every count based check and the required file manifest must be updated in the same commit,
      or they go red for the right reason at the worst possible moment.

### 2. Gate hardening BEFORE the bake (BLOCKER, and the reason this is not just art work)
The audit that scoped this cut found the weakest link is not the art, it is the proof. Only one
of the four icon gates runs automatically, and both deploy verifiers accept a SINGLE icon file as
evidence the icon tree shipped, so a deploy that silently dropped 400 of 468 icons would go
green. On a cut that rebakes everything, that stops being tidy up and becomes the safety net.
- [ ] **Wire the three hand run gates (LW-241, LW-245)**: compare with an expected movers list,
      the reserved name anchors check, and the shared silhouette check. The expected movers list
      is the piece that makes automation possible: during a pass the family being worked moves ON
      PURPOSE, so the gate needs a declared list of who is allowed to move.
- [ ] **Replace the one file deploy proof (LW-241)**: BuildLinked and Publish must verify the
      whole icon manifest, by count and by identity, not one representative file.
- [ ] **Run the full arc gate on the final bake (LW-245)**: full bake, then a repo versus install
      comparison over every file, run twice from the already rebaked tree so a hidden read of the
      mod tree cannot hide inside a passing run.

### 3. The four already built families need the owner's eye, not new art (SHOULD)
One sitting clears all four. Their art is already in the live install and already survived the
2026-08-16 whole catalogue look; what is missing is the formal gallery verdict.
- [ ] **Knives (LW-198), ninja blades (LW-205), books (LW-210), bags (LW-212)**: 28 items. Three
      of these hold Now seats purely on a pending owner pass, so clearing them also unblocks the
      in flight ledger.

### 4. Four items that render off their own artwork (SHOULD, owner rulings)
Each needs a keep or re tint call, and a keep the art verdict means a re tint baked before the cut.
- [ ] **Venetian Shield and Fallingstar Bag (LW-277), Ragnarok (LW-244), Whale Whisker
      (LW-238)**: measured 161, 60 and 115 degrees off their own art respectively, and cyan over
      red for the last.

### 5. The system that makes it repeatable (SHOULD)
- [ ] **Icon art framework (LW-278)**: the style bible and verdict corpus, then the judge harness
      calibrated against held out owner verdicts BEFORE it prefilters anything, then the per
      family loop. The owner gallery stays the final gate no matter how the judge scores. This is
      the top of Now and is what carries sections 1 and 3 without burning the owner's time on
      rounds a machine could have pruned.

### 6. Deploy riders (BLOCKER, they only get one chance)
- [ ] **The rider checklist itself (LW-286)**: three checks have been waiting for whichever
      deploy happens next, recorded only as scattered prose. They get a home in
      docs/VERIFY_LIVE.md before the deploy, not after it.
- [ ] **No drift check, restated (LW-286, confirms LW-247)**: the original pre registration said
      the installed icon bytes must not move at all, 468 of 468. That was written before this cut
      existed and it collides with a deploy whose entire purpose is to move icon bytes. The
      honest restatement, which preserves the intent: the 150 already final ramp items must not
      move, and only the newly reworked items plus the 12 new files may. Deferring glow, see OUT
      below, is what keeps that half of the check meaningful, because the ramp art then has no
      reason to move at all.
- [ ] **Flight recorder eviction fix (LW-286, confirms LW-259)**: the one undeployed runtime
      change in the tree; needs a deploy plus a tape read after a long battle.
- [ ] **Cache partition combined pass (LW-286, confirms LW-262)**.
- [ ] **Deploy flavour**: the install is DEV flavoured, which seeds every weapon's kill tally.
      Fine for judging art, wrong for anything read as release ready, so any pass meant to stand
      as a release check runs a prod flavoured build.

### 7. Shop front (NICE)
- [ ] **Re render the download page banner (LW-281)**: it is drawn from the installed icon cache
      and still shows pre ramp colours. It must be regenerated AFTER the final bake, and the
      rerun becomes a step of the icon pass rather than a separate chore anyone can forget.

### 8. Release gates (existing GO/NO-GO)
- [ ] analyze.py exit 0, no dominated item.
- [ ] dotnet test green.
- [ ] Publish.ps1 clean, PROD thresholds, no LWDEV and no seeding.
- [ ] Bump ModVersion to 2.4.0 and cut the matching tag.

---

## 2.4.0 OUT of scope (explicit, so nothing drifts back in)

- **The glow rim (LW-287). Owner call 2026-08-19: hold off, there are other ideas for it.**
  Carved out of LW-248 into its own story and deliberately outside this cut. It costs nothing to
  defer, because the rim is a separate layer that adds or removes without touching a body pixel.
  Note for whoever picks it up: the deployed install DOES currently carry a rim on shields,
  helms, weapons and bags, so the shipped state is glow present, and any later decision moves
  from that baseline in one direction or the other.
- **Battle sprite colours (LW-251)**: a live reverse engineering arc on its own clock, currently
  waiting on a one shot boot race probe. It shares the word colour with this cut and nothing else.
- **Three rows whose premises are stale and must be re measured rather than trusted (LW-232,
  LW-237, LW-242)**: all three describe recipes that LW-247 deleted from the recolour tool, so
  they no longer describe the shipped art.

---

## DEFERRED (post-release backlog)
- **Remove Treasure Master** -- L, works + tested, no user benefit this cycle; do as a dedicated
  cleanup. OBVIATES the Scholar's Ring idle-nag bug (do NOT fix that doomed code). On removal:
  de-list treasure.json from pipeline.ps1 + release.yml + csproj together; BattleState.BattleDisplayed
  and its TickContext property must survive the cut (Treasure Master is its last pre-gate consumer,
  but the predicate is core BattleState); also drop Treasure Master from the ModConfig
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
