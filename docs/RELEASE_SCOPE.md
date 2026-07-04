# Release Scope -- next release (consolidation)

Locked 2026-07-04. Current shipped version 2.2.2; proposed next **2.3.0** (owner confirms the bump).

**Identity: "Finish the Samurai Swords + a focused item-balance tuning pass."** A consolidation
release -- land the two committed blockers, absorb ONE cheap high-value tuning batch, and DEFER every
removal and research/walled item. Scope grounded by a repo-wide triage of every open TODO + user-
feedback item (2026-07-04): each was ground-truthed against the actual code/data before landing here.

Two owner decisions set the stopping line:
- **Galewind expiry: SHIP WITH FALLBACK.** Try the round-7 AREC recon as a stretch; if a per-puppet-
  turn release does not crack, keep the committed wielder-clock behavior and reword the card to match.
  The release is never held hostage to an RE breakthrough.
- **Samurai "finished" = 4 signatures:** Iai + Kobu (done) + 2 new (Murasame id41, Kiku id45).
  Capstones (Masamune id46 / Chirijiraden id47 / Sasori id70) stay pure-growth.

---

## IN -- ship gate (every box green = ship)

### 1. Samurai Swords (BLOCKER) -- finish = 4 signatures
Tuning is DONE and analyze.py-green; the work is the two open signature slots.
- [ ] **Murasame id41** signature -- **Masamune's Mercy** (brave-gated heal, proven lever). AVOID
      Mushin (parked +0x1BB wait-detection byte hunt).
- [ ] **Kiku-ichimonji id45** signature -- **Onryo** (Undead brand) OR **Shura** (controllable
      Berserk on 2nd kill, bit +0x47/0x08). If Kusanagi's Reach is ever picked instead, run a
      memory Doom-reap probe FIRST.
- [ ] Each signature: items.json block -> gen_living_weapon_meta.py -> xUnit tests -> deploy ->
      **VERIFY LIVE** -> commit -> LIVE_LEDGER flip. Live-verify is non-negotiable (Zanshin
      graveyard: built green, LIVE-FAILED on the damage-intercept wall, reverted).
- [ ] Clean DEV redeploy before ANY katana live test (orphaned Zanshin DLL may still be deployed).

### 2. Galewind / Puppeteer expiry (BLOCKER) -- ship with fallback
Committed behavior (verified 2026-07-04, Puppeteer.Hold.cs): puppet releases on the GALEWIND
WIELDER's next turn (TurnTracker wielder-clock; wielderless fallback = 12 GlobalTurns; 4-turn
cooldown; NO wall-clock cap). Wrong clock -- between dominate and the wielder's next turn a fast
enemy puppet can act more than once. The approved fallback is therefore ~already the shipped
behavior; the guaranteed-shippable path is a card match.
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
- [ ] **Rod nerf** (rods are over-tuned).
- [ ] **Added-Move nerf** (Trailwarden Jerkin + Wayfarer Boots -- too mobile too early). Give every
      de-Moved item a replacement dimension or analyze.py flags a dominated husk.
- [ ] **Early-armor rider smell** -- retune riders that are dead weight at their tier (incl.
      Sanctguard id133 StrongElements=Holy, dead until T5-T6 Holy weapons).
- [ ] **Claymore = CARD REWORD only** (working-as-designed per the ForcedTwoHands commit; a buff
      would WORSEN the off-class two-hander over-tuning feedback).
- [ ] Any name/desc change -> patch_names.py item.en.nxd re-bake (full-table replace, restart-only).

### 4. Remove Offensive Chemist (NICE) -- S, INDEPENDENT of Treasure Master
- [ ] Grenades (246-250) already out of items.json; scrub any residual refs; two nxd re-bakes via
      patch_names.py + patch_ability_names.py (never hand-edit cells -- Barrage shares ability key 358).

### 5. Doc + hygiene (free)
- [ ] Release note: non-English players get FULL gameplay (rebalance + growth + signatures); item
      TEXT stays vanilla-language and the Kills/+3 card counter is English-only.
- [ ] Correct docs/USER_FEEDBACK.md: enemies inherit only the STATIC global rebalance, NOT the
      living-weapon runtime (growth/signatures/tally).
- [ ] Delete the falsified pointer-presence turn-detection code (rides the Galewind rework).
- [ ] Drop the dead `spriteIdOverride:1` on id67 Warbrand (items.json:2163).

### 6. Release gates (existing GO/NO-GO)
- [ ] analyze.py exit 0 (no dominated item).  - [ ] dotnet test green.
- [ ] Publish.ps1 clean, PROD thresholds {5,25,50}, no LWDEV / no seeding.
- [ ] Bump ModVersion (-> 2.3.0) + cut the matching tag.

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
