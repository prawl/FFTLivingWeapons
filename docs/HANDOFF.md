# Session Handoff — Living Weapon state of the world (2026-06-10, post rod pass)

Everything below is COMMITTED and PUSHED on `living-weapon` and deployed as a DEV build
(LwDev=true: thresholds {1,2,3}, every weapon seeded to P3 on load, verbose `ev:` log). The arc
of the day: the ROD PASS shipped end to end — data layer (all eight rods turned into ranged
artillery) plus two player-verified +3 signatures — while the debugging burned through four
trigger designs, exposed a Denuvo probing wall, and uncovered (and fixed) a family of
EquipBonus-row corruptions that had been silently rewriting unrelated items.

## What ships now (player-verified unless noted)

- **Rod data layer**: all 8 rods attack at RANGE 4 with the `Direct` flag — bare `<Range>` is
  IGNORED on `Striking`-class weapons; the projectile flag (`Direct` gun-style / `Arc` bow-style)
  is what unlocks ranged targeting (`Lunging` is the separate 2-tile spear reach). The
  Spark/Ember/Frost trio also regained Strengthens-element +25% via vanilla EquipBonus rows
  6/7/8. Dragon Rod's card states its Silence proc and "Begins battle with Reraise." again (the
  MECHANIC never left — row 40 always shipped it; the desc-rewrite commit had eaten the card
  line). Rod of Faith now points at the REAL innate-Faith row (73; it had been pointing at a
  squatted row). Materia Blade stays melee — the legendary map-length swing came from the
  (disabled) WotL equipment-replacer mod's id-31 entry (Range 8 + Direct), kept as the recipe.
- **Rod of Faith "Rapture" (+3) — VERIFIED ("works flawlessly")**: below 30% HP, Master
  Teleportation (movement 243 — marked cut content, but the engine HONORS it) replaces the
  wielder's movement and HOLDS UNTIL RECOVERY to/above the threshold. The 3-turn cap is RETIRED
  (its clock never ticked live; recovery-release was player-verified the same session, and
  recovery doubles as the re-arm hysteresis). If parked-low perma-teleport ever proves
  degenerate, the nerf lever is ONCE-PER-BATTLE ARMING — never a turn timer (see trap ledger).
- **Umbral Rod "Spiritual Font" (+3) — VERIFIED ("twerks like a charm")**: MOVED from the
  Wellspring (rule: no T1 weapon carries a signature anywhere in the mod). Trigger = direct
  POSITION POLLING (`MoveWatch`: a new tile must hold 3 consecutive ticks, then ~3s rate cap);
  on a settled move, +10% maxHP and +10% maxMP are written through EVERY located twin
  (`Wielder.LocateAll` + idempotent `ReplenishAll` — covers the (0,0)-corner twin tie). The MP
  pair (band +0x18/+0x1A) is LIVE-VERIFIED on screen; the per-battle layout validation stays as
  a guard. Move-only turns and knockback PAY (intended; it's the Lifefont fantasy).
- **Shelved on branch `shelved-rod-signatures` (@1c3e9c0, pushed)** — configs removed, runtime
  code dormant and tested on living-weapon; revival = a config block + a working trigger:
  Hushward **Unbroken Chant** (Swiftspell 226 grant; live-bit question still open), Umbral
  **Life Sap** (kill → 25% heal; BLOCKED on the kill-attribution flake below), Dragon Rod
  **Wyrmblood** (turn-edge splash regen; its TurnTracker edge has the same disease as below).
- Housekeeping: dev seed floor is now 3 (every signature live on equip); `FontDevPulse`
  (Tuning, LWDEV) force-fires the font every ~10s for on-screen write-path proof — retired to
  `false`, flip it for debugging. The rods working sheet was folded into
  `docs/living_weapon_grid.csv` (rows 51–58; 56/58 carry `Verified Live? = Yes`) and deleted.

## EquipBonus row corruptions (found live, all fixed)

Redefining a vanilla EquipBonus row via `_equipBonus` rewrites EVERY item still pointing at it —
on BOTH sides: vanilla items not repointed by our ItemData (row 3 had corrupted Excalibur's
Haste/AbsorbHoly kit, row 83 the Akademy Tunic's Shell) AND our own items.json reusers of the
row's vanilla contents (claiming row 10 turned five MA+1 items — Arcanist Cap, Zeus Mace,
Mage's Wrap, Acolyte Robe, Runecloak — innately ATHEIST; row 5 turned four Regen items into
Holy-boost). The audit rule + verified-free row list now live in items.json `_meta.equipBonus`
(claimed: 9/17/22/40/56/74-79). OPEN: Riposte (id 21) carries an undocumented innate Atheist
(row 17) — feature or fossil? Patrick's call.

## The trap ledger (today's tuition — read before building any trigger)

1. **Wielder-side triggers: poll the unit's OWN observable state.** Four trigger corpses in one
   day: TurnTracker (the condensed struct follows the CURSOR), band entry+0x25 (`ACtSlam` — the
   scheduler-CT WRITE target ExtraTurn slams; never ticks for reads), band entry+0x09
   (`ACtTurn` — Maim's victim-turn byte, fine for ENEMIES, never reads ≥90 for player units),
   and the actor latch (the global Acted byte 0x14077CA8C SKIPS player actions — the same flake
   that eats kill credits). The position-poll MoveWatch is the survivor; copy it.
2. **The (0,0) twin tie**: a unit standing at the origin tile is indistinguishable from its
   frozen band twin; `Wielder.Locate` now tie-breaks identical twins (same weapon/brave/faith =
   one unit), and `LocateAll` + idempotent write-through is the robust write pattern.
3. **External RPM probes are Denuvo-walled** — python probes read zero units while the
   in-process runtime sees everything. Instrument the DLL instead (the dev-pulse +
   `Wielder.DumpCandidates` pattern); `%TEMP%\fft_probes\*` still work for module-static data,
   not unit structs.
4. **The engine honors exactly ONE movement passive** — both font bits held perfectly on +0x9C
   and only Lifefont applied. Multi-font = runtime writes, not bit grants.
5. **HandsFree cross-reference recipe**: its kill cheat re-discovers the unit table by scanning
   0x141800000–0x141900000 for (hp,maxHp) pairs each run — the relocation-tolerant locate
   pattern if fixed anchors ever go stale for real.

## Open threads (none blocking)

1. **Kill-attribution flake** — a magic kill credited nobody this morning (the
   "corpse slot 24 not a captured enemy" line was a red herring: that was the player's own dead
   archer, correctly refused). Root cause family: the Acted byte skips player actions. Gates
   Life Sap's revival; consider attribution via per-unit observable state (the font-pivot
   lesson) instead of the acted latch.
2. **Staff pass NEXT**: Triage (revive an ally → +33% max HP back) is designed,
   feasibility-cleared, and waiting — revival-edge detection already exists in Corpses.cs. The
   TODO's healing-amplifier ideas live there too.
3. Grid hygiene: bow row 90 sigNote still says "Fan-Splitter PROPOSAL" (Barrage shipped
   instead); a stale-sigNote sweep is cheap.
4. Dual-wield range bleed: pairing a melee blade with a ranged Direct weapon makes Attack
   target at the ranged hand's reach — observed live, accepted as vanilla-legal for now.
5. Pre-release checklist unchanged: `Publish.ps1` (prod thresholds {5,20,50}, no seeding, no
   FontDevPulse), TODO.md's 2.0 items (DLC restore, icon colors, changelog, P3 descs...).

## Dev harness facts

- Deploy = kill FFT_enhanced.exe, `.\BuildLinked.ps1`. Tables/nxd apply on game RESTART; the
  DLL on next launch. Suite: 464 green; both gates enforced by BuildLinked/Publish/CI.
- Diagnostics in `livingweapon.log` (mod folder): `font:` (baseline/moved/mp readback),
  `rapture:` (armed/released + SET/MISS), `locate-miss` + `cand slot` dumps on pulse misses,
  `ev:` timeline (dev). The log is wiped on each deploy.
- items.json `_meta.equipBonus` = the row-claim rule + audited free rows. The vanilla table
  dumps live at `...\Reloaded\Mods\FFTIVC_Mod_Loader\TableData\*.xml`.
