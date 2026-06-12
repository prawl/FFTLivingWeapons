# FFTItemOverhaul 2.x Release Verification Checklist
*Work top to bottom; blockers surface first. Check each box live.*

**Gating model (read first):** Treasure Master is enabled per-battle when **the Scholar's Ring (id 260) is
equipped on any party member** (the normal gate, `RingGate.ScholarRingEquipped`) **OR** the Reloaded config
`Treasure Master Always On` is true (a force-on override that skips the ring check). Config **defaults OFF**,
so out of the box the feature is **ring-gated**, not off. Idle log when neither is satisfied:
`treasure: no Scholar's Ring equipped -- module idle`.

---

## 1. Smoke Test (5 minutes)

> Goal: confirm a clean prod deploy, startup, the ring gate, and marks in one battle.

- [ ] **Deploy prod build.** `.\BuildLinked.ps1 -Prod`. All steps complete (generate → gate → meta → treasure-bake → 1002 tests → DLL publish → deploy). No red lines.
  - Fail: any `REFUSING TO DEPLOY` or non-zero exit. **[BLOCKER]**
- [ ] **Launch, check startup log** (`livingweapon.log`).
  - Expected: `Living Weapon starting up`, `config: TreasureAlwaysOn=False`.
  - Fail: `startup failed` / exception trace = Engine ctor threw. **[BLOCKER]**
- [ ] **Equip the Scholar's Ring on a party member**, enter a captured battle (e.g. The Siedge Weald, map 74).
  - Expected: tiles light up within ~1 s. Log: `treasure: map 74 The Siedge Weald armed -- 4 tile(s)`.
  - Fail: `no Scholar's Ring equipped -- module idle` (ring not detected) / `fingerprint mismatch` (L2) / `waiting to arm` (L3, pan camera) / no log (L0 PE key). **[BLOCKER]**
- [ ] **Unequip the ring from everyone**, re-enter a treasure battle.
  - Expected: no marks. Log: `treasure: no Scholar's Ring equipped -- module idle` (once per battle).
  - Fail: marks despite no ring → `RingGate` / per-battle gate broken. **[BLOCKER]**
- [ ] **Check kills counter.** Equip menu → a weapon with kills.
  - Expected: counter on equip card, matches `kills.json`. Fail: no counter/suffix → display sweep broken. **[BLOCKER]**

---

## 2. Treasure Master Feature

### 2a. Ring gate + Config override + Scholar's Ring item

- [ ] **Ring equipped → arms; ring removed → idles (the gate).** Equip ring, enter battle → marks + `armed`. Remove ring, re-enter → `no Scholar's Ring equipped -- module idle`, no marks. Re-equip between battles re-enables (gate is re-checked each battle). **[BLOCKER]**
- [ ] **Config force-on override works.** Launcher → Configure Mod → `Treasure Master Always On` = True → Save → relaunch. With NO ring equipped, marks still appear (override bypasses the ring). Log: `config: TreasureAlwaysOn=True (from ...\User\Mods\prawl.fft.itemoverhaul\Config.json)`. Fail: still requires ring → `_alwaysOn` not honored. **[MAJOR]**
- [ ] **Launcher property name + description.** Game NOT running, PROD build → Configure Mod shows `Treasure Master Always On`, description mentions the ring is the normal gate + this force-enables without it, default False. Fail: no Configure button → `Configurator.cs` not in DLL. **[BLOCKER]**
- [ ] **Config written to User/Mods/, not the deploy folder.** Toggle True → `<Reloaded>/User/Mods/prawl.fft.itemoverhaul/Config.json` has `"TreasureAlwaysOn": true`. **[MAJOR]**
- [ ] **Corrupt Config.json falls back gracefully.** Overwrite with garbage → game runs, log `config load failed, using default TreasureAlwaysOn=False`, no crash. **[BLOCKER]**
- [ ] **Scholar's Ring auto-granted when count 0** (out of battle, ~1 s on world map). Ring appears (count 1), log `scholar-ring: granted` once per session. **[MAJOR]**
- [ ] **Grant is idempotent.** Count ≥ 1 → no further `scholar-ring:` lines, count unchanged. **[MINOR]**
- [ ] **Ring card shows the right text.** Equip menu → Scholar's Ring: name `Scholar's Ring`, desc "Treasure Master: a scholar's eye marks where treasure lies hidden on the field." + "Boosts JP earned." Fail: name `Ring of Aptitude` → nxd not rebuilt. **[MAJOR]**
- [ ] **JP-doubling rider works.** Equip ring, earn JP vs baseline → doubled (BoostJP, equipBonusId 80). **[MAJOR]**

### 2b. Treasure Master Core (with ring equipped or override on)

- [ ] **Fingerprinted map arms** (fpVer 2/3, e.g. map 74) — all tiles within ~1 s, `armed` log. **[BLOCKER]**
- [ ] **Marks persist through enemy turns / cast animations** (the `BattleDisplayed` gate fix — no flicker, no re-arm cycling). Fail: marks vanish on enemy turns → gate regressed to `InLiveBattle`. **[BLOCKER]**
- [ ] **No marks on the world map** (mode 0) with the feature on. Fail: marks on world map → `battleMode != 0` veto broken. **[BLOCKER]**
- [ ] **Formation/placement screen shows marks** (INFERRED — needs field-confirm; mode 1 + slot9 stuck → `BattleDisplayed` true). Fail: marks only from first turn → slot9 not yet stuck during placement. **[MINOR]**
- [ ] **Map-id-only map arms** (fpVer 0, nofp'd water/lava, e.g. Zeirchele Falls 83) — `armed -- N tile(s) (map-id-only)`, no fingerprint-mismatch lines. **[MAJOR]**
- [ ] **Running-water marks stay solid** (FastHold thread, ~8 ms re-stamp out-paces the ~16 ms wipe). Fail: flicker synced to water → FastHold not running. **[MAJOR]**
- [ ] **Fingerprint flap re-proves, doesn't permanently disarm** — transient mismatch logs `fingerprint flap -- re-proving`, marks resume within ~1 s. **[MAJOR]**
- [ ] **Hot-reload: fresh capture paints on battle retry, no relaunch.** Capture session auto-pushlives → `dataset reloaded -- N map(s)` → armed on the new map. **[MAJOR]**
- [ ] **Uncaptured populated map nags once per battle** (`has N treasure tile(s), not captured`), not per tick. **[MINOR]**
- [ ] **Midlight's Deep trapped-treasure tiles ARE marked** (maps 105-114 — confirms `is_treasure = rareItemId > 0`, commit f80a94a). **[MAJOR]**
- [ ] **Game-patch build-key safety** — wrong `timeDateStamp` (OS-temp copy, not repo) → `dataset built for game ... -- disarmed`, no crash, no writes. **[BLOCKER]**
- [ ] **Absent map is silent** (mapId with no entry → no marks, no nag). **[MINOR]**

---

## 3. Regression Pass — existing Living Weapon features

*(All exercised after the Engine pre-gate-tick / BattleState refactor.)*

- [ ] **Kill tally increments + persists** across a session restart (`kill:` lines, `battle: ended -- saving kill tally`). **[BLOCKER]**
- [ ] **Equip-card kills counter + suffix paint** out of battle (`+ `/`+2`/`+3`, `display: memory sweep ... maintaining M card-text spots`). **[BLOCKER]**
- [ ] **Equip card paints inside the in-battle Status card** (mode 3 + paused + submenu). **[MAJOR]**
- [ ] **Battle enter/exit cycle clean** — `started` → `ended` (4 s debounce) → fresh `started`, no double-exit. **[BLOCKER]**
- [ ] **Stat growth holds** (≥ 5 kills = tier 1 = `round(natural × 1.10)`, `growth: found combat struct`). **[MAJOR]**
- [ ] **Charm-Lock** (Galewind +3) holds charm through hits. **[MAJOR]**
- [ ] **Plague** (Venombolt +3) — poison never cures. **[MAJOR]**
- [ ] **Barrage** (Yoichi Bow +3 on Thief job 83) appears in the command list. **[MAJOR]**
- [ ] **Zwill extra-turn** fires on a kill (CT slam → extra turn). **[MAJOR]**
- [ ] **SpiritualFont silent with NO Umbral Rod equipped** (the fix — zero `font:` lines); fires + restores HP/MP with one at +3. **[MAJOR]**
- [ ] **Integration smoke** — charm-lock + plague + stat growth all active in one battle, no `tick:` error lines, one `battle: ended`. **[BLOCKER]**

---

## 4. Build / Release Gates

- [ ] **Dominance gate green** — `python tools\analyze.py` exit 0, all 5 sub-checks PASS. **[BLOCKER]**
- [ ] **Treasure-bake gate green** — `python tools\gen_treasure_db.py` exit 0, FNV self-test vectors OK, `coverage: 71 shippable / 15 stub / 0 dropped`. **[BLOCKER]**
- [ ] **Unit tests: 1002, 0 failed** — `dotnet test LivingWeapon.Tests\LivingWeapon.Tests.csproj` (TreasureSchemaTests deserializes the committed treasure.json with `MissingMemberHandling.Error` — schema drift fails here, not silently at runtime). **[BLOCKER]**
- [ ] **Publish.ps1 clean, PROD thresholds** — no `-p:LwDev=true`, Verify-Package `[OK]` for every entry incl. `treasure.json` + `meta.json`, exit 0. **[BLOCKER]**
- [ ] **treasure.json ships in the zip** (non-zero size; absent → entire module silent at runtime). **[BLOCKER]**
- [ ] **PROD DLL: KillThresholds = {5,25,50}, DevSeedAllKills = false** (no dev build shipped as release). **[BLOCKER]**
- [ ] **kills.json preserved across -Prod deploy** (`-> Restored player's kills.json tally`). **[BLOCKER]**
- [ ] **Build-flavor guard rejects plain dev deploy over prod** (exit 1, no files modified). **[BLOCKER]**
- [ ] **No dev-seeded phantom tallies** — `kills.json` not a flat sea of exact-3 entries. **[MAJOR]**
- [ ] **release.yml sentinels** include both `treasure.json` and `meta.json`; `$RequiredModFiles` (pipeline.ps1) too. **[MAJOR]**
- [ ] **Version/tag decision** — move `v2.0.0` to tip or cut `2.0.1`/`2.1.0`; bump `mod/ModConfig.json` `ModVersion` to match. **[MAJOR]**

---

## GO / NO-GO (any fail → do not release)
1. `analyze.py` exit 0, no DOMINATED/FAIL.
2. `gen_treasure_db.py` exit 0, FNV self-test OK, 71 shippable.
3. `dotnet test` — **1002 passed, 0 failed**.
4. Publish.ps1 clean; Verify-Package `[OK]` incl. treasure.json; zip has treasure.json (non-zero).
5. PROD DLL: thresholds {5,25,50}, no LWDEV, no seeding.
6. **Ring gate works**: ring equipped → marks; ring removed → `no Scholar's Ring equipped` idle, zero marks.
7. Config default OFF; toggle ON = force-on override; corrupt config = fail-soft, no crash.
8. Marks off on world map (mode 0); marks persist through enemy turns.
9. Build-key safety: patched exe → global disarm, no crash, no writes.
10. Kill tally increments + persists; equip-card counter paints; integration smoke clean.
11. build_flavor guard blocks dev-over-prod; kills.json preserved across deploy.

---

## Open risks to watch
- **Formation coverage is inferred** — slot9 may not be stuck at 0xFFFFFFFF during unit placement; if so, marks appear only from the first turn. Confirm live (run `battle_cheats.py sentinels` on the placement screen).
- **Two-phase boss / chained battles** (e.g. Eagrose Castle Keep, Dycedarg→Adramelk) — the phase swap likely rebuilds render buffers, orphaning the capture; marks die in phase 2 and re-scanning would overwrite phase 1. Known edge case, deprioritized.
- **Animated-terrain maps** beyond water (action-camera, torches) can flap the fingerprint; the fix is `nofp <id>` (map-id-only) — a one-time per-map action, already done for the known offenders.
- **Phantom kill tallies** from prior dev deploys (HANDOFF notes Scoutbolt +2, Wellspring Rod) — cosmetic wrong-tier suffix; surgery offer open.
- **Dev cheat probes** (`tools/probes/battle_cheats.py`: godmode/pa99/myturn/kill_all/give_move/sentinels) are dev tooling — they do NOT ship in the mod artifact. Verify only that they aren't referenced by the shipped pipeline.
