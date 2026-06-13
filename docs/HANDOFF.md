# Session Handoff — Treasure Master shipped end-to-end (2026-06-12)

Everything below is COMMITTED on branch **`treasure-master`** (~50 commits ahead of `main`, **1006 tests
green**, both gates green, **NOT merged**). This session built the entire **Treasure Master** feature from
the plan in `docs/TREASURE_MASTER_PLAN.md` to a deployed, ring-gated, fully-captured release candidate — plus
a dev cheat kit that grew out of one brutal boss fight. The release runbook is `docs/2.0_RELEASE_CHECKLIST.md`;
the next decision is **verify → tag → merge → Publish**.

## The live install (IMPORTANT — current state)

The Reloaded mod folder holds the **latest PROD build** (`BuildLinked.ps1 -Prod`, thresholds {5,25,50}, no
seeding, kills.json preserved). The Reloaded **config `Treasure Master Always On` defaults OFF**, so the
feature is **ring-gated**, not off — equip the **Scholar's Ring (id 260)** on any party member and marks
turn on. The user's `…/Reloaded/User/Mods/prawl.fft.itemoverhaul/Config.json` is set to `false` (ring is the
gate). A `BuildLinked.ps1` (no `-Prod`) over this install REFUSES without `-Prod`/`-Force` (dev-seed guard).

## What shipped (Treasure Master — complete)

- **Mechanism**: a `TreasureMaster` ISignature holds bit `0x80` on each treasure tile's per-map
  module-static render-flag bytes every tick → the engine paints its own native tile mark. Read path
  `ArmAudit.cs`, write path `TileHolder.cs`, decisions `TreasureMaster.Policy.cs`, fail-soft loader
  `TreasureDb.cs`, hot-reload + fast-hold below.
- **Four-layer write containment** (no write until all pass): L0 PE build key (game-patch self-disable),
  L1 map-id byte `0x14077D83C`, L2 terrain fingerprint, L3 per-address resting audit. OR-only, no clear path.
- **Fingerprint saga (load-bearing)**: the terrain grid `0x140C65000` (7 B/record) is partially DYNAMIC —
  fields 0(height)/1(slope)/6(flow) animate on water/lava. v1=raw (failed), v2=field-0 (failed on water),
  **v3=fields {2,3,4,5}** (current). Some maps animate even those / differ across battle instances →
  **map-id-only mode** (`nofp <id>`: fpVer 0, no fingerprint, arms on map-id + addr quorum). `refp <id>`
  upgrades a stale fingerprint to v3. **Both are one-time per-map fixes** stored in the DB.
- **`BattleDisplayed` gate** (`slot9==0xFFFFFFFF && battleMode!=0`): treasure ticks PRE-GATE (like Barrage),
  so marks survive enemy turns / cast animations (mode-1 frames the old `InLiveBattle` flickered off) and
  show on the formation screen; dark only on the world map (mode 0). **Formation coverage is INFERRED** —
  confirm with `battle_cheats.py sentinels` on a placement screen.
- **FastHold** (`FastHold.cs`): a dedicated ~8ms background thread re-stamps armed addresses so running-water
  animation (~60fps wipe) can't flicker the marks.
- **Hot-reload**: the DLL watches `treasure.json`'s write stamp (~1s) and reloads; a capture session
  auto-`pushlive`s → marks appear on battle retry with **no relaunch**.
- **Scholar's Ring gate** (`RingGate.cs`): the equipped accessory id lives at **roster `+0x12` (u16)**
  (probe-confirmed: RosterBase 0x1411A18D0 + slot*0x258 + 0x12; ring=260, siblings 218/224/226/232).
  Gate = `config alwaysOn || a roster ring-bearer is DEPLOYED in the battle`. **Battle-only** (2026-06-12): a
  ring-bearer found in the roster only counts if that unit is present in the live battle band — matched by
  (brave,faith) + level-drift (the band stores no accessory id). A **benched** ring-bearer is ignored, like
  any equipped effect on a unit not on the field (`BandHasUnit`). **Checked once per battle** (read when the
  module first arms, cached; mid-battle unequip doesn't drop marks; re-read next battle via ResetBattle). The
  live ~1s re-check was rolled back the same day. `alwaysOn` is a force-on override that never reads roster/band.
- **Scholar's Ring item (id 260)**: description leads with "Treasure Master: a scholar's eye marks where
  treasure lies hidden on the field." + "Boosts JP earned." (nxd rebuilt); **auto-granted** when the player
  has zero (`ScholarRing.cs`, out-of-battle, idempotent). The JP-doubling rider is unchanged.
- **Reloaded config**: `Config.cs`/`Configurator.cs`/`Configurable.cs` (mirrors FFTColorCustomizer). Mod.cs
  reads the USER config (`…/User/Mods/<id>/Config.json`) with a modDir fallback; fail-soft (corrupt → default).
- **SpiritualFont fix**: the "MP field layout verified" log/sampling now only runs when an Umbral Rod is
  actually wielded (no spurious log when none equipped).

## Capture campaign — DONE

**71 shippable maps** (every reachable campaign treasure map, including all 10 Midlight's Deep floors). The
~15 stubs are cutscene/town/test maps (Eagrose Gate, Warjilis, 116–125 Checkerboard/Unused) that never
battle — not capturable, leave them. **`is_treasure` = `rareItemId > 0`**: every Move-Find tile has a rare
item (344/344, zero pure-trap tiles), so the trap is a *property* (`is_trapped`), not a separate class —
this is why Midlight's Deep (trapped-treasure tiles) is marked. The native mark can't distinguish trapped
from safe; a mark there means "loot here, expect a trap."

## Dev cheat kit (tools/probes — does NOT ship in the mod)

`tools/probes/battle_cheats.py` + `fft.ps1`/`fft.sh` (`source`/dot-source then call): **godmode** (hold
party HP — start before the battle), **pa99** (party Physical Attack=99), **myturn** (party Speed 99 /
enemies 1 + CT reset = take ~every turn), **kill_all** (KO enemies; `x <slot>` to spare guests — there is NO
team byte, guests share enemy slots), **revive**, **give_move** (243=Master Teleport, hover-pick),
**sentinels** (battle-state dump for the formation gate). Capture tooling: `treasure_flags.py`
(session/mapid/status/verify/refp/**nofp**/pushlive) + `gen_treasure_db.py` (the bake gate).

## Release checklist — `docs/2.0_RELEASE_CHECKLIST.md`

Live verification + 11 GO/NO-GO blockers + open risks. Next steps:
- Work the smoke test + per-feature + regression + build/release sections (the ring gate, fingerprinted +
  map-id-only maps, and Siedge Weald v3 are already confirmed live).
- **Build/release gates** (section 4) can be dry-run without the game — offer stands to run them.
- **Version/tag**: `v2.0.0` sits behind; decide retag vs 2.x; bump `mod/ModConfig.json` ModVersion; then
  `Publish.ps1` (clean compile forced; byte-verify thresholds {5,25,50}).
- **Merge** `treasure-master` → `main` after review (one commit per green-gated stage already on the branch).

## Open risks / watches

1. **Formation marks are INFERRED** (slot9 may not be stuck during unit placement) — confirm with
   `battle_cheats.py sentinels` on a placement screen; if it arms only on the first turn, that's why.
2. **Two-phase boss maps** (Dycedarg→Adramelk, Eagrose Castle Keep 10): the Lucavi swap rebuilds the render
   buffers → the phase-1 capture is orphaned in phase 2; re-scanning would overwrite phase 1. Known edge
   case, deprioritized.
3. **Terrain fingerprint is now ADVISORY ONLY — FIXED 2026-06-12 (LIVE INCIDENTS #4 + #5).** The v1→v2→v3
   fingerprint kept drifting: first mid-battle (#4, Siedge Weald), then traced to **weather** (#5) — rain
   perturbs the hashed fields, so a map captured clear fails the *arm-time* gate in a rainy instance (found on
   74/76/79/81, all raining; no weather metadata exists to enumerate them, so per-map `nofp` is unwinnable —
   a clear-captured map silently shows no tiles in rain). Fix: the fingerprint **never gates arming** (arm-time
   + mid-battle both advisory; logs `fingerprint mismatch -- arming ... anyway`). Containment = build-key (L0)
   + per-tick map-id (L1, unique per map) + per-tile resting quorum (L3); `BattleDisarmed` removed; `nofp`
   obsolete for weather. Maps 74 + 76 were nofp'd before the runtime fix (harmless; left map-id-only).
4. **Story bosses** ignore band HP=0 writes (scripted death) — kill_all KOs grunts, not story bosses; drop
   the boss to 1 HP and land a real hit, or fight it with godmode+pa99.
5. **Phantom kill tallies** (HANDOFF history: Scoutbolt +2, Wellspring Rod) — cosmetic wrong-tier suffix;
   surgery offer open.

## Dev harness facts

Deploy = kill `FFT_enhanced.exe`, `.\BuildLinked.ps1 -Prod` (the install is prod-flavored — a plain run
refuses). Tables/nxd apply on game RESTART; the DLL on next launch; `treasure.json` hot-reloads live. Both
gates (`analyze.py` + `dotnet test`, 1006) enforced by BuildLinked/Publish/CI. Diagnostics in
`livingweapon.log` (`treasure:`, `font:`, `kill:`, `battle:` prefixes; the ring-idle and arm/disarm lines
name their reason). The session memory store carries the per-mechanism detail
(treasure-master-architecture-plan is the index for this arc).
