# Slayer's Reliquary -- scope + acceptance criteria

STATUS: CONTRACT (Reliquary acceptance criteria)

Approved 2026-07-05. Design source: docs/RELIQUARY_DESIGN.md. Post-release feature: it does not
gate the 2.3.0 consolidation release (docs/RELEASE_SCOPE.md); Phase 0 probe work may run ahead of
the release since it is instrumentation, not ship-gate scope.

## One line
The weapon remembers WHO it killed: per-victim capture at the existing kill-credit edge -> persisted
per-weapon deeds (Marks + Legends) -> the card's flavor line becomes the weapon's own story, with
toast announcements. Zero stat effect; pure fiction.

## Non-goals (this scope)
- NO unit-title apex ("Ramza, the Demonsbane" / command-menu header hook) -- design-only, later scope.
- **Card-only v1:** design surfaces 2-10 (sprite label, status sheet full-roll, formation, turn-order,
  targeting, shop, victory screen, roster, +0xDC retitle) are DEFERRED to a later scope. Consequence,
  stated plainly: the full legend roll persists in legends.json but has no visible in-game home in v1
  -- the card shows only the proudest line. (Decision 6.)
- NO new game-memory WRITES beyond the two existing proven surfaces (card painter slots, PromptSwap
  facing-prompt swap). Everything else is read-only against game memory.
- NO enemy-side identity/name write (WALLED). NO reliance on AREC +0xB (REFUTED). NO stat bonus, ever.
- The weapon NAME field is never modified beyond the existing +N suffix -- no epithet in the name.

## Phase 0 -- probes (instruments land tracked in tools/probes/; results land as LIVE_LEDGER rows;
## only the owner flips PROVEN)

**P1 -- victim snapshot integrity.** Instrumented dev build captures victim {nameId (band +0x1E0),
job byte (combat +0x03 = band-entry -0x19, Puppeteer.Policy JobOff, live-confirmed 2026-06-18),
undead bit (+0x45/0x10)} at THREE points: (a) alive-path refresh beside _slotId, (b) deadStreak==1
edge, (c) CreditKill tick.
- PASS: across >=2 battles / >=6 kills, at least one capture point yields sane values (nameId != 0,
  job classifiable incl. the expected out-of-band story ids) on EVERY kill; that point is adopted as
  authoritative and recorded as a ledger row. Probe also measures the out-of-band job-id rate and
  demonstrates the mirror-seat clone is filtered.
- FAIL (blocks Phase 1 capture): any kill where ALL three points read garbage (nameId==0 or
  unclassifiable-and-implausible job) -- then the capture design is wrong, back to RE.
- Also on the verify list: Dark Knight 94-vs-160 conflict, Machinist 43.

**P2 -- named-boss key: discrimination AND stability.** Dump victim/roster nameIds across >=3
battles incl. human story enemies (Gafgarion at Zirekile = ENTD 405) + the first Lucavi (Queklain).
- PASS: a key (nameId alone, or an enumerated composite of at most nameId+job+level+maxHP) that (i)
  uniquely separates the named boss from every same-battle grunt, (ii) has zero collisions with the
  player-roster nameId pool in the sample, and (iii) is STABLE: the same boss re-read across >=2
  separately-loaded instances of the same battle (ideally +1 different save) yields the same key.
- FAIL (blocks Phase 2): no such key exists in the sampled data.

**P3 -- credit fires on battle-ending boss kills.** Kill >=2 battle-ending bosses (one Lucavi + one
human story boss, named in the probe log). First confirm the flight battle-exit flush fires AT ALL on
a cutscene-terminated battle (else "no records" is ambiguous).
- PASS: flight jsonl for each battle contains the kill latch + CreditKill for the boss death. A pass
  admits ONLY the probed battle-end styles into the initial legends table; each additional style gets
  a spot-check before its entries are trusted.
- FAIL: either record absent -> Phase 2 BLOCKED until a supplemental credit path for battle-ending
  deaths is designed and probed (its own ledger row).

**P4 -- flavor-line overwrite renders (RENDER-ONLY).** Dev build paints a same-length ASCII test
string over ONE weapon's card flavor line. Explicitly accepted: the probe weapon's Kills site dies
for that session (the painter's anchor is the very text overwritten -- the Phase 1 painter change is
what fixes that; P4 only proves the render).
- PASS: renders on the equip card in BOTH text encodings the painter already handles (8-bit and
  UTF-16LE copies); bounded interaction set = open/close the card >=5x, scroll >=3 weapons, one
  battle enter/exit; no crash; other weapons' Kills slots still correct.
- FAIL: fallback = baked fixed-width deed-slot line via patch_names.py. NOTE: the fallback is a
  data-layer change that hits analyze.py's uniqueness/length gates, re-bakes item.en.nxd, moves the
  Kills anchor, and ADDS a line (breaks zero-net-text-growth) -- it needs its own sign-off before use.

## Phase 1 -- Marks + last-victim narration (gated on P1; card display additionally on P4)

Capture:
- [ ] Victim snapshot {nameId, job, undeadBit} captured at the P1-adopted point, stored per-slot
      beside _slotId. Test: edge_snapshot_stored_per_slot.
- [ ] Snapshot consumed exactly once at CreditKill; deed recording invoked SOLELY from the existing
      CreditKill call site; a dual-credit kill records the deed on every credited weapon.
      Tests: snapshot_consumed_exactly_once_at_credit, dual_credit_kill_records_deed_on_both_weapons.
- [ ] Snapshot cleared on the same three reset paths as _lethalActor (identity-change, revive,
      ResetBattleCorpses). Test per path.
- [ ] All new reads threaded through IGameMemory, Readable-guarded; no static Mem calls in new files.
- [ ] **Missing-snapshot failure mode:** a CreditKill with no captured snapshot increments the kill
      tally exactly as today, records NO deed, never throws, logs one DBG line + one flight record
      (deed-miss). Test: CreditKill_without_snapshot_records_no_deed_and_does_not_throw.

Classify:
- [ ] Pure classifier returns exactly ONE archetype per kill; precedence: undead bit > caster band
      (79-82, 85, 90) > human band (74-95) > monster band (96-144) > unknown/special. One test per
      precedence pair (e.g. undead_caster_classifies_undead).
- [ ] Out-of-band ids (story bosses read e.g. 37; Ramza forms 0-3; Machinist 43) land in
      unknown/special, never a Mark -- EXPECTED on story battles, documented as such (human/caster
      Marks undercount there by design).
- [ ] Dark Knight: classifier accepts both 94 and 160 until the P1 verify resolves it (resolution is
      a named P1 output, not open-ended).
- [ ] Dragon/species archetypes: NOT in Phase 1. Add-on gated on a monster job-table dump with its
      own done-condition (id set committed with the dump cited).

Persist:
- [ ] New LegendStore (legends.json), sibling of KillTally, using KillTally's prior-copy-to-.bak
      ordering (legends are permanent facts; the prior-generation fallback is the right trade).
      Load: [path, .bak, empty] fallback chain, never throws; corrupt-load logs one warning + flight
      record. Tests: CorruptLoad_falls_back_and_logs, LegendStore_roundtrip.
- [ ] A failed SAVE never throws (Engine tick thread), leaves the previous on-disk primary+.bak
      intact, retries next save edge. Test: Save_failure_leaves_previous_file_intact.
- [ ] Schema per weapon id: lastVictim {nameId, job, cls}, counts per archetype, marks earned,
      legends list, pendingAnnounce. kills.json untouched (additive sibling file).
- [ ] Save on battle-exit edge + on change (mirror kills.json timing).
- [ ] BuildLinked.ps1: add legends.json, legends.json.bak, gunslinger.json, gunslinger.json.bak to
      the line-101 -Exclude list (ONE mechanism, named; kills.json keeps its historical %TEMP%
      round-trip). Two checkboxes, two manual verifications: sentinel value survives two consecutive
      deploys AND one simulated failed deploy.
      (gunslinger.json is wiped by every deploy TODAY -- pre-existing bug, fixed in the same touch.)

Announce:
- [ ] Mark-earned toast rides the existing BannerToast queue. Event-key space pinned: Marks =
      1000 + archetypeIndex, Legends = 2000 + tableIndex (tiers own 1..3, Chronicle owns negatives).
      Property test: union of all tier/milestone/mark/legend keys is pairwise distinct.
- [ ] Threshold PLUMBING: dev + prod threshold arrays differ under #if LWDEV (mirroring Tuning.cs
      tiers); archetype count <= 6 enforced by a unit test. Prod values = deferred balance-pass item.

Display:
- [ ] Card flavor line REPLACED at paint time (DLL-live) by the proudest story, with a TOTAL ORDER:
      most-recent Legend > Mark with highest count (ties by fixed archetype-list order) >
      last-victim > baked flavor. ComposeCardLine is pure over store state. Tests per boundary and
      per tie-break (Compose_prefers_newest_legend_over_older, Compose_breaks_mark_tie_by_order, ...).
- [ ] **Uniqueness invariant:** every composable earned line is per-weapon unique AND
      non-prefix-colliding across the whole padded pattern set (likely mechanism: lead with the
      weapon's own name). Enforced by a unit test mirroring analyze.py's check_unique_flavor over the
      full archetype/legend x weapon cross-product at padded lengths. (Without this, identical earned
      lines across weapons resurrect the pre-Display-v2 shared-kills cross-attribution bug.)
- [ ] Composer: exact same char count as the baked flavor line (pad/truncate), pure ASCII (non-ASCII
      desyncs dual-encoding byte lengths), " -- " never em dash. Validator unit-tested.
- [ ] **Three-way anchor + paint-through:** site discovery/verify accepts original baked flavor OR
      current earned line OR the per-weapon LAST-PAINTED line (persisted in LegendStore so it
      survives relaunch). A site verifying via a stale-but-known anchor is REPAINTED to current,
      never evicted. (Two-way is insufficient: the earned line changes on every kill via the
      last-victim tier; a site painted with the previous line would match neither anchor, get
      evicted, and freeze that buffer's Kills counter.) Tests:
      site_rediscovered_after_earned_line_changes,
      kill_that_changes_line_migrates_site_and_kills_slot_keeps_updating.
- [ ] Weapon NAME field untouched beyond the existing +N suffix.
- [ ] FitsLookback still passes with earned patterns registered.

## Phase 2 -- Legends (gated on P2 + P3)
- [ ] Curated legends table: P2-key -> canonical name + epithet fragment. Storage (meta.json vs
      sibling data file) DECIDED and recorded before the first Phase 2 commit; the entry list is
      enumerated in the data file with a count in the AC.
- [ ] Rarity preserved: the table stays Lucavi-scale small; a weapon with zero legends is a normal,
      fully-narrating weapon (Mark/last-victim line), never padded with a synthetic legend.
- [ ] Killing blow on a table entry appends a permanent legend record (permanent = survives store
      round-trip + battle exit; test: legend_survives_reload); card line becomes the Legend
      narration, outranking all Marks.
- [ ] **Announce honesty:** the legend RECORD is guaranteed (persisted at earn time). The
      ANNOUNCEMENT is best-effort with named loss modes (process exit before delivery, queue
      overflow, dead-hook launch). Mitigation: undelivered Legend announcements persist
      (pendingAnnounce in legends.json) and re-enqueue on next launch.
      Test: pending_legend_toast_survives_store_roundtrip.
- [ ] Big center-screen banner: STRETCH, never blocks Phase 2. Honest ledger status: the callout
      renders are UNCERTAIN rows (piggyback-only; arbitrary-time fire still OPEN; ShowSpike is
      #if LWDEV dev-only). Productionizing it is its own arc with its own ledger row. If it lands:
      hard-gated to Legends only; Marks never fire it; the toast remains the delivery fallback.

## Cross-cutting (all phases)
- [ ] TDD; both gates green per stage commit. analyze.py untouched on the PRIMARY path (the P4
      fallback is the one exception and needs its own sign-off).
- [ ] Zero new unguarded writes; only writes remain the painter slots + PromptSwap buffer.
- [ ] No new heap sweeps (reuse budgeted DisplaySweep). 200-line trigger respected; policy halves pure.
- [ ] Flight record kinds: deed-miss, mark-earned, legend-earned, legend-delivered -- visible in a
      captured battle jsonl via parse_flight.py.
- [ ] EN-only caveat added to docs/RELIQUARY_DESIGN.md display section (toast + card text EN-only;
      gameplay-neutral in FR).
- [ ] Live-verify before commit for every runtime-visible stage; only the owner flips ledger rows.

## Decisions (owner-approved 2026-07-05)
1. Phase order + gates: P1 gates Phase 1; P4 gates its card display; P2+P3 gate Phase 2. APPROVED.
2. Display: in-place flavor replacement w/ three-way anchor (primary); baked deed-slot line only as
   a separately signed-off fallback if P4 fails. APPROVED.
3. Mark archetypes: caster / human / monster / undead (+dragon later, gated on the table dump).
   Naming + final list = owner creative pass before Phase 1 ships.
4. Legends curation breadth + epithet naming = owner creative pass before Phase 2 lands.
5. gunslinger.json deploy-wipe: fix in the same BuildLinked touch. APPROVED.
6. Card-only v1 (surfaces 2-10 deferred; full legend roll save-file-only in v1). ACCEPTED.
7. Banner demoted to stretch; toast is the primary v1 Legend announce with pending-replay. ACCEPTED.
8. Dual-credit boss kill: legend recorded on BOTH credited weapons (mirrors CreditKill semantics).
