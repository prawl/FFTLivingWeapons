# Porting FFTLivingWeapons to FFT:IC 1.5 (New Game+)

STATUS: JOURNAL (closed research log; verify claims against LIVE_LEDGER.md before building on them)

> This is the resume-from-cold guide for porting `prawl.fft.livingweapons` to the FFT:IC 1.5
> content patch. It assumes you are picking this up weeks after the patch dropped and have
> forgotten the details. Read sections 1-2 first, then work sections 4-5. Everything you need
> to start cleanly is here; the appendices are the concrete checklists.

---

## 1. SITUATION

FFT:IC **1.5 dropped 2026-06-16** -- the New Game+ patch (NG+ carryover, and more content; exact
item additions TBD until decoded). This is a **content patch that recompiled `fft_enhanced.exe`**,
which is the part that hurts us.

The mod has two halves that fail in completely different ways:

- **Living Weapon (DLL)** -- in-process C#. It is locked to a wall of **absolute addresses** in
  `LivingWeapon/Offsets.cs`, valid only because the 1.x exe had a fixed image base `0x140000000`
  with **no ASLR**. A recompiled 1.5 exe relinks/relayouts code and static data, so **every
  absolute address in `Offsets.cs` is presumed moved until re-verified.** Struct-internal field
  offsets (`+0xNN` within a unit struct) are far more likely to survive than the base addresses
  (layout is stickier than link order), but "likely" is not "verified" -- a patch that adds a
  field reflows them. They must be spot-checked, not assumed.
- **Item Overhaul (data)** -- table XML + nxd + tex, applied by Nenkai's modloader on restart.
  Status is **TBD pending a schema/ID check** against the 1.5 base tables. It does not depend on
  the exe layout, but it depends on item IDs keeping their meaning and on the modloader + FF16Tools
  both supporting 1.5.

### 1.5.0 patch notes -- CONFIRMED (good news, narrows the port)

Patch notes (nintendoeverything, 2026-06-16) confirm 1.5.0 is **New Game+** (carries over unit
levels + item data), a **Zodiac compatibility** status-screen feature, QoL/UI (status-while-targeting,
a "Remove All Equipment" button, clearer job-unlock text, persistent camera), new settings, **added
Simplified Chinese / Traditional Chinese / Korean localization**, minor text/sound, and stability. It
**explicitly does NOT change item / weapon / equipment / job stats or balance, and adds NO new items
or equipment.** This reframes both halves:

- **DATA side: very likely applies AS-IS.** No item/job table schema or ID changes and no new items
  means our sparse table overrides still hit the right rows and the **261-item display wall holds**.
  Two things to verify (not assume): (1) the **CJK + Korean localization + "minor text adjustments"**
  touched the text container -- re-decode the 1.5 base `item.en.nxd` and confirm the ENGLISH table's
  row count/layout is unchanged before shipping our full-table replace (they ADDED languages, so the
  English layout should be intact, but the container may now hold more language tables); (2) the patch
  repacked the base pacs -- confirm FF16Tools still decodes them with `-g fft` (format almost certainly
  unchanged). If both hold, the data half ships with no edits.
- **DLL side: this is a RE-ANCHOR, not a rewrite.** Because **no unit/item/job data structures
  changed**, the **relative struct offsets** in `Offsets.cs` (band `+0x14` HP, `+0x16` MaxHp, `+0x7C`
  support, `+0x48` poison, `-0x15` crystal, the combat-frame `+0x3E/0x3F/0x40` stats, etc.) almost
  certainly **carry over unchanged** -- a field reflow needs a struct change, and there wasn't one.
  Only the **absolute base anchors** (Appendix A: BattleMode, the band anchor `0x14184F890`,
  RosterBase, TurnQueue, ArrayBase, the JobCommand table, the slot sentinels, the display/mirror
  addresses) move with the recompile. Re-find THOSE (FFTHandsFree's 1.5 map + the static exe diff +
  a probe spot-check), keep the relative offsets, and the DLL is back. Spot-check a couple of relative
  offsets anyway -- "almost certainly" is not "verified" -- but expect them to hold.

Net: realistic best case is the **data half boots clean and the DLL needs only ~8-10 base-address
updates.** Treat that as the working hypothesis when you start, and let the first-boot capture
(section 3) confirm or refute it.

### The dangerous failure mode

`Mem` accesses go through RPM/WPM with `Readable`/`Writable` pre-filters, so **a wrong address
will not crash the game** -- reads fail safe, writes are pre-filtered. Instead the DLL **silently
does nothing**: no kills counted, no growth, no card paint, no signatures firing. It looks "fine,"
not broken. The one genuinely dangerous case is a **stale-but-valid** address that now points at
unrelated live data -- a write there is silent corruption. The highest-risk such write is the
JobCommand injection (`Barrage.AbilityBase`, see Appendix A section 7).

### The gates are a TRAP for 1.5 verification

`analyze.py` (dominance) and `LivingWeapon.Tests` (xUnit) are **pure logic over `data/items.json`
and `meta.json`**. They do NOT touch the running game or the exe. **They will stay green across 1.5
regardless of total offset breakage.** A fully green `BuildLinked`/`Publish` proves the data design
and runtime *logic* are sound; it proves **nothing** about whether the DLL's addresses still point
at live game state. Per the house rule: gates prove logic, not engine behavior. **1.5 sign-off is a
live-deploy-and-watch task, not a gate pass.**

### Known-good pre-1.5 backup (your rollback + your diff source)

Captured 2026-06-16 immediately before taking the patch, with Steam auto-update blocked first, then
the patch taken deliberately:

```
Location: C:\Users\ptyRa\FFT_IC_backup_pre1.5\
  FFT_enhanced.exe   359,214,920 bytes   built 2025-11-19 05:41:02
                     SHA256 6DEDDA923D2D834404C4D513DD21AE77F0A09C35E05C59D612A79BFF65EB9C28
  FFT_classic.exe    373,146,440 bytes   (PSX mode, preserved for completeness)
  README_BACKUP.txt
```

This is the **exact build every `Offsets.cs` address was found against.** Notes:

- **PE FileVersion reads `1.0.0.0` on both exes** (engine stamp; "1.5" is the content-patch number).
  File-version diffing will NOT tell you which build you have -- **the SHA256 is the real identity
  check.**
- **Confirm which build you are on before trusting anything.** SHA256 the current install's
  `fft_enhanced.exe` against `6DEDDA92...EB9C28`. Match = still pre-1.5 (offsets valid). Mismatch =
  already patched (offsets presumed moved).
- **Rollback:** the backup exe can be dropped back into the Steam install (update blocked) to keep
  the 1.x build playable/testable while the port is in progress -- the dev environment does not have
  to break the day you start.
- **Static-diff source:** with both exes side by side, a binary/symbol-anchored compare locates
  where each known structure moved. For each static address in `Offsets.cs`: find a stable
  byte-pattern landmark *around* the old address in the backup exe, locate that pattern in the 1.5
  exe, and the delta gives the new base. Faster and safer than blind live scanning for the
  static-module addresses (no running battle needed, no heap noise). Live probes then confirm.

The repo holds the rest of the pre-1.5 snapshot: the offset map (`LivingWeapon/Offsets.cs`) and the
decoded 1.x base tables under `working/` (`working/ref/*.json`, `working/pilot_item.sqlite`,
`working/th_item.sqlite`, `working/nxd_ability/ability.sqlite`). Between the exe backup and the
repo, the full pre-1.5 state exists -- the port can be done deliberately, with a known-good
rollback, not under fire.

---

## 2. WHAT'S PARKED (where exactly we stopped)

So future-us knows precisely where the work was when 1.5 landed mid-stream:

### Shadow Blade enemy-leak turn-gate -- PLANNED + REVIEWED + PROBE-VALIDATED, one implement step away

The Shadow Blade signature (id 23, Sanguine) injects a JobCommand record so the granted command is
castable -- but the inject leaks the command to **enemy Knight units** on their turns (the bug
logged in commit 58c6233's predecessor and `docs/LIVE_LEDGER.md`). The fix is fully designed,
adversarially reviewed, and probe-validated; it was **not yet implemented** when 1.5 dropped.

- **Signal:** the turn-queue TEAM field. Pre-1.5 address `Offsets.TurnQueue + TqTeam = 0x14077D2A0
  + 0x02 = 0x14077D2A2`, a u16 where **0 = player, 1 = enemy, 2 = ally.**
- **Validation:** confirmed live by `tools/probes/turnteam_probe.py` and recorded in
  `docs/LIVE_LEDGER.md` ('Turn-owner TEAM field' row, dated 2026-06-16). Only Patrick flips PROVEN.
- **Fix (the full implement spec):** gate the JobCommand record inject on a fail-safe decision so
  Shadow Blade is present on player turns / out of battle and removed on enemy/ally turns:

  ```
  RecordShouldHoldGrant(battleMode, battleTeam)
    => battleMode == 0 || (battleTeam != 1 && battleTeam != 2)
  ```

  i.e. hold the grant out of battle (mode 0) or when the turn owner is neither enemy (1) nor ally
  (2). It is one Sonnet-implement step (TDD: failing test first, then the gate). The full
  implement spec exists alongside this work.
- **1.5 CAVEAT:** `0x14077D2A2` is a **PRE-1.5 address and must be re-found** before implementing.
  Do not wire the gate to the literal until `turnteam_probe.py` re-confirms the TEAM field on the
  1.5 exe (it imports `ct_probe`'s `COND_BASE`, so re-find that first -- see Appendix C).

### Other committed-this-session work (all addresses in notes are PRE-1.5)

- **Chain Lightning multi-hop rework + bow-range trims** -- commit `7d7664d`.
- **Static test-plan notes** -- commit `1f380d5` (the staff + sword live test plan, plus
  `1f380d5`-era notes). **Any addresses referenced in these notes are PRE-1.5 and must be
  re-found.**
- Recent landed signature work for context: Choir instant-cast aura (Warlock's Staff), Sanctuary
  anti-crystallization (Staff of the Magi, crystal counter `band -0x15`), Non-charge support-grant
  probe. All of these key on offsets that move on 1.5.

---

## 3. BREAKAGE-CAPTURE TEMPLATE (fill this on the FIRST 1.5 + mod boot)

Leave the checkboxes blank; fill them the first time you boot 1.5 with the mod installed. This is
the triage snapshot that tells you which track (data vs DLL) is on fire and how badly.

**Build identity**
- [ ] SHA256 of current `fft_enhanced.exe`: `________________________________________________________________`
- [ ] Confirmed != `6DEDDA92...EB9C28` (i.e. we are actually on 1.5): ____
- [ ] 1.5 content-patch confirmed in-game (NG+ menu present): ____

**Does it boot**
- [ ] Game launches with the mod enabled (no boot crash): ____
- [ ] Game launches with the mod DISABLED as a control (isolates mod vs patch): ____

**Data side applied (restart-only)**
- [ ] Item NAMES changed by the mod appear correct: ____
- [ ] Item DESCRIPTIONS (flavor + Kills scaffold) appear correct: ____
- [ ] Menu ICONS (recolored) appear on the right items: ____
- [ ] Item STATS (WP/HP/evade/element) match the overhaul: ____
- [ ] Wrong item shows mod data (sign of ID renumbering): ____  which items: __________
- [ ] New 1.5 items appear, and are vanilla (untouched by us) vs corrupted: ____

**Modloader**
- [ ] `fftivc.utility.modloader` dependency satisfied (mod not auto-disabled): ____
- [ ] Modloader log warnings / table-rejection messages: ____  text: __________
- [ ] Any table silently reverted to vanilla (a dropped XML): ____  which: __________

**DLL (Living Weapon) -- live**
- [ ] DLL loads (Reloaded shows it; log file written): ____
- [ ] Log looks sane on boot (no exception spew): ____  notes: __________
- [ ] Battle ENTER detected (enter fires once): ____
- [ ] Battle EXIT detected (debounced, no metronome): ____
- [ ] Kills counted at all: ____
- [ ] Kills credited to the RIGHT weapon: ____
- [ ] Growth (PA/MA/Speed) applies to a known wielder: ____
- [ ] Card paint: Kills line + suffix appear on the equip card: ____
- [ ] Card paint targets the RIGHT weapon (not all-same-count): ____
- [ ] Any signature fires (note which): __________
- [ ] Tick errors / exceptions in the log: ____  text: __________

**Crash**
- [ ] CRASH observed: Y / N
- [ ] If Y -- repro (what action): __________
- [ ] If Y -- mod enabled vs disabled (isolate): __________

**NG+ / item count vs the 261 wall**
- [ ] Total item count in 1.5 (vs old 261): __________
- [ ] Items past id 260 exist: ____
- [ ] 261-display-wall behavior: truncates / crashes / raised / unchanged: __________
- [ ] HandsFree cap-break interaction reintroduced ("+" items corrupted): ____

---

## 4. PORT PLAN

Two independent tracks. They can proceed in parallel once you know which one is unblocked (the data
side is blocked on modloader+FF16Tools 1.5 support; the DLL side is blocked on the offset re-find).

### TRACK A -- DLL offset re-find

**Re-validating `Offsets.cs` against a 1.5 dump is ~80% of the DLL port.** Do it in this order.

#### A0. Get the new offset map cheaply (do not blind-scan everything)

1. **SHA-verify** the current install vs the backup so you know you are actually on 1.5 (Appendix B).
2. **Wait for / crib FFTHandsFree's 1.5 `BATTLE_MEMORY_MAP.md`.** That doc is the canonical upstream
   offset map; `Offsets.cs` already cites it as source (the IC roster `0x1411A18D0` stride `0x258`,
   static array `0x140893C00` stride `0x200`, condensed `0x14077D2A0`, passive bitfields
   `+0x74/+0x78/+0x7D` all originate there). **Critical constraint: FFTHandsFree is ALSO offset-locked
   to the 1.x exe**, so its current map is just as invalid against 1.5 as ours. The recipe is
   sequenced, not "go look it up":
   - Let FFTHandsFree ship its own 1.5 update first -- it is the in-house oracle (live screen reads,
     BFS, class fingerprints) and will absorb the discovery cost, the same way it originally found the
     turn-team field. Do not pay the re-find cost twice.
   - Crib the updated base addresses + reflowed field offsets into `Offsets.cs`. Re-cite as before.
   - **Spot-validate with our own probes -- never trust the cribbed map blind.**
3. **Or static-diff** the backup exe vs the 1.5 exe for the static-module bases (Appendix B) if
   FFTHandsFree lags and you must move first. The probes can also do a full unaided re-find
   (`sentinel_probe` -> `ct_probe` -> the rest), but that is the expensive path.

#### A1. Re-run the probes in dependency order

All probes are external RPM/WPM (Python, fail-safe, cannot crash the game) and live in
`tools/probes/` (tracked, not `%TEMP%`). They share scaffolding: `sentinel_probe.py` is standalone;
`ct_probe.py` is the base module most others import (`PROC`, `find_pid`, `rd`, `wr`, `scan_static`,
`scan_auth`). **Order matters** -- the status/feature probes import `ct_probe`'s constants and will
read garbage until those are corrected. Re-run in this sequence and correct `Offsets.cs` as you go:

1. `sentinel_probe.py` -- the battle in/out sentinels (slot0/slot9/battleMode/eventId/pause/submenu).
2. `ct_probe.py` -- **the keystone re-find.** The three battle struct families + scheduler CT
   (`combat base+0x41`). `dump` is orientation; `watch`/`hold` re-validate which CT field is
   authoritative. Most other probes depend on its constants.
3. `turnteam_probe.py` -- the turn-queue TEAM field (the parked Shadow Blade gate signal). Imports
   `ct_probe`.
4. `crystal_counter_probe.py` -- the crystallization "3 hearts" countdown byte (`band -0x15` /
   `combat +0x07`) and that pinning it stops crystallization (Sanctuary's newest, least-tested
   offset, found 2026-06-16).
5. `poison_probe.py` -- the status-byte layout (Poison `+0x48` bit `0x80`, timer `+0x4A`) and the
   whole **watchspan diff recipe** that maps any status field. Imports `ct_probe`.
6. `barrage_probe.py` -- the JobCommand record table base + 25-byte record layout (ability injection).
   Its `ABILITY_DB` sqlite path points at `working/nxd_ability/ability.sqlite` (also re-derived for
   1.5). Imports `ct_probe`.
7. `noncharge_probe.py` -- live support-bitfield grant honored at charge-time (Choir). Imports
   `ct_probe` + `poison_probe`.
8. `cripple_probe.py` -- reaction-field suppression (`combat +0x94` hold-zero), Maim's primitive.
9. `oracle_probe.py` -- static-array enemy-identity capture (the EnemyOracle filter).
10. `knockback_probe.py` -- gx/gy position-write renderer desync (why guaranteed Knockback is parked).

The "session one-shot" probes in the same folder (`set_learned*`, `support_poke`, `roster_dump`,
the menu/cmdlist scans, etc.) encode day-specific addresses and **several write live memory** -- per
the probes README, read before running, never run blind. They re-find on demand when their feature
is revisited, not as part of the core sweep.

#### A2. Update `Offsets.cs` (and the two un-centralized address sites)

`Offsets.cs` is the single source for nearly every hard address (full inventory in Appendix A).
**Centralization gap -- a sweep of `Offsets.cs` alone will MISS the single highest-risk write
anchor:**

- **`Barrage.AbilityBase` (`0x140679436 - 27*25`)** + its roster-relative learned-bitfield consts
  (`RJobId 0x02`, `RSecondary 0x07`, `RLearnedBase 0x32`, `LearnedStride 3`) are **local consts in
  `Barrage.cs`**, reused verbatim by `ShadowBlade.cs`. Re-find these explicitly.
- The `TreasureDb`/`ArmAudit` module-bound consts are local to their files too.

#### A3. Re-validate live, foundations-first (do NOT trust any signature until its foundations are green)

Validate strictly bottom-up; everything above assumes the layer below:

1. **Mem/MemBits fail-safe contract** -- confirm RPM/WPM + VirtualQuery still degrade a bad address
   to a no-op (not a crash) on the 1.5 process. Everything else assumes this.
2. **BattleState in/out detection** -- re-find `Slot0/Slot9/BattleMode/PauseFlag/SubmenuFlag/EventId`;
   confirm the `battleMode 2/3/4` / `slot0==0xFF` / `slot9==0xFFFFFFFF` **encodings** still hold;
   enter fires once, exit debounces, no metronome. EVERY signature gates on these. **Validate first.**
   (Watch the slot0 quit-stick trap: slot0 sticks `0xFF` after a battle QUIT; victory clears to 0x66.)
3. **Band locate (Band + Wielder + GrowthEngine.Locate)** -- re-anchor `CombatAnchor 0x14184F890` +
   `RosterBase 0x1411A18D0`, re-verify the fingerprint offsets (`ALevel/ABrave/AFaith/AMaxHp/AHp/
   AGx/AGy/CWeapon` + roster hands). Confirm `Locate`/`TryResolveMainHand` find a known wielder.
   Self-mapping by weapon id contains *corruption* but not *blindness* -- the search must still start
   near the right place. This gates growth + every weapon-keyed signature. **Validate second.**
4. **KillTracker attribution (KillTracker + Corpses + ActorResolver + EnemyOracle + TurnTracker)** --
   re-verify `Acted 0x14077CA8C`, the turn-queue (`0x14077D2A0` + `TqHp/TqMaxHp/TqLevel`), the
   static-array base/stride, and the Dead bit (`+0x45/0x20`). Confirm one kill credits exactly the
   right weapon and that `TqNameId +0x04` is **still a trap** (a colliding battle index, never resolve
   by it). Gates LifeSap/ExtraTurn/Ricochet/Maim/Larceny/Benediction. **Validate third.**
5. **TurnTracker** -- once Acted + band fingerprint are confirmed, verify per-unit and global turn
   counts (gates Larceny/Renewal expiry + timed grants).
6. **Display sweep** -- re-find `MirrorWeapon 0x141870854` / `MirrorOffHand 0x141870856` /
   `WpScratch 0x141870836`; confirm the string scan still locates cards using the shipped nxd
   strings. The heap sweep itself is dynamic (`VirtualQueryEx`, no hardcoded heap address), so only
   those four anchors are build-pinned. Cosmetic -- safe to defer if combat work is the priority.
7. **GrowthEngine stat hold + Afterimage + Ultima** -- with locate + the combat-struct field map
   confirmed, verify PA/MA/Speed/Support holds land on the right bytes for a known weapon.
8. **Then individual status/positional signatures, riskiest last** (full per-signature break analysis
   in Appendix D):
   - Low-risk reads-of-shared-state first: EagleEye, Ricochet, Maim, Renewal, Benediction, Sanctuary,
     Choir (spot-check each one's status/support/HP offset).
   - Then multi-byte holds: CharmLock, Plague, Rapture, SpiritualFont.
   - **Barrage + ShadowBlade together** -- re-verify the JobCommand table base (`0x140679436` family)
     AND the IC job-id remap before enabling; a wrong base corrupts real command lists.
   - **TreasureMaster** -- expect its L0 build-key check (`TimeDateStamp`/`SizeOfImage`) to
     **auto-disarm on 1.5 by design** (the new exe won't match). Needs a full `treasure.json`
     re-capture + map-id/terrain re-find, not just an offset bump.
   - **FeignDeath LAST** -- it force-kills and depends on engine auto-revive timing (documented
     crash-on-mistime). Validate only after CT (`+0x25`), Dead/Reraise/Invisible bits
     (`+0x45/+0x47`), and turn-queue match are all independently confirmed.

#### A4. Implement the parked Shadow Blade gate

Once the TEAM field (`turnteam_probe.py`) is re-confirmed on 1.5, implement the
`RecordShouldHoldGrant` gate from section 2 (TDD: failing test first). Re-run `dotnet test`.

### TRACK B -- Data side

**Prereq: an FF16Tools build that supports the 1.5 pacs.** Base pacs are encrypted; every FF16Tools
call passes `-g fft`. If 1.5 rotates the pac key or bumps the nxd schema, the pinned
`FF16Tools.CLI-1.13.2` may fail to decode. Mitigation already in place: `FF16TOOLS_CLI` is an env
override (`tools/lib/paths.py`), so a tool bump is one env edit, not a script hunt. Until FF16Tools
handles 1.5, nothing below that touches nxd/tex can run. Also install 1.5 + the modloader so
`TableData/` and the encrypted base pacs are the 1.5 versions.

**Order matters; diff-against-the-1.5-base is the spine of the port.**

1. **Re-dump the 1.5 modloader template tables.** Ensure
   `Steam/.../Reloaded/Mods/FFTIVC_Mod_Loader/TableData/` holds the 1.5 `ItemData.xml`,
   `ItemEquipBonusData.xml`, `ItemArmorData.xml`, `ItemAccessoryData.xml`, `JobData.xml`,
   `JobCommandData.xml` (that path = `lib.paths.TABLE_DATA`).
2. **Re-decode the reference snapshot** -- `python tools\decode_tables.py`. Rewrites
   `working/ref/{itemdata,equipbonus,armor,accessory}.json`. **Git-diff against the committed
   `working/ref/*.json`** to surface: new/renumbered item ids, changed `AdditionalDataId` per item,
   new EquipBonus references (which of {9,17,22,25,40,56} are now non-free), new armor/accessory rows.
   This diff is the authoritative "what 1.5 changed" report.
3. **Regenerate `data/additional_data_ids.json`** from the fresh `working/ref/itemdata.json` (it
   falls back to that ref's `additionalDataId` when the committed map is absent -- temporarily
   move/delete the committed file and run `generate.py` to repopulate, or script the extract).
   Validate every items.json id still maps to the intended item; reconcile any renumbering in
   `items.json` **before** emitting tables.
4. **Re-decode the 1.5 base `item.en.nxd` and `ability.en.nxd` to sqlite** (FF16Tools
   `nxd-to-sqlite -g fft`). Refresh `working/pilot_item.sqlite` (and `working/th_item.sqlite`, the
   unchanged-name vanilla-desc source) and `working/nxd_ability/ability.sqlite`. **Diff row counts
   and schema** (`PRAGMA table_info`) vs the old sqlite -- confirm row count, the `Item-en` columns
   (`Name`/`Description`/`UiItemCategoryId`/`SortOrder`/`UiStatusEffectId`), and the `Ability-en`
   schema are unchanged. Resolve any count/schema delta here.
5. **Re-run the generators in pipeline order:**
   - `python tools\generate.py` -- tables + `out/names.json`; now raises if any `AdditionalDataId`
     or `OptionsAbilityId` is invalid against 1.5.
   - `python tools\analyze.py` and `python tools\analyze.py --baseline` -- dominance gate.
   - `python tools\gen_living_weapon_meta.py` -- `meta.json`.
   - `python tools\patch_names.py` -- rebuilds `item.en.nxd` from the refreshed sqlite.
   - `python tools\patch_ability_names.py` -- rebuilds + self-verifies `ability.en.nxd`; its
     `set(vanilla) != set(rebuilt)` check catches any 1.5 row-set change (good: it refuses to deploy).
   - `python tools\make_jobequip.py` and `python tools\make_jobcommand.py` -- rebuild from the 1.5
     TableData; confirm Equip Axes is still id 460 and job ids still mean the same generics
     (`make_jobcommand.py` requires the flag element BEFORE the RSM elements or the table is dropped).
6. **Re-decode + re-recolor icons** -- re-dump the 1.5 vanilla `ui/ffto/icon` `.tex` set to the
   `recolor_icons.py` `VANILLA` path, then `python tools\recolor_icons.py`. Confirm the
   `ei_NNN`/`ei_s_NNN` id range and tex format are unchanged; regenerate all 478 (239x2). Add tints
   for any new 1.5 items kept in `items.json`.
7. **Run the full gates** -- `.\BuildLinked.ps1` (dev) then verify the `$RequiredModFiles` manifest
   in `tools/pipeline.ps1`; `.\Publish.ps1` for the release zip. Both refuse to ship on a red
   dominance gate or failed unit tests. **(Reminder: green here proves logic only.)**
8. **Live-verify** -- boot 1.5, confirm renamed items/descriptions/icons land on the correct items,
   new 1.5 items render, and the 261-wall (or its 1.5 successor) does not truncate or crash. The
   committed full-table nxds must be **rebuilt from the 1.5-decoded base** so 1.5's new content is
   not reverted to 1.x text.

---

## 5. RISKS / UNKNOWNS

- **Modloader may not support 1.5 yet.** `mod/ModConfig.json` hard-depends on
  `fftivc.utility.modloader` (also its `ReleaseMetadataName`). Until Nenkai ships a 1.5-compatible
  build, the **entire data half won't load** (dependency unsatisfied -> mod disabled). Track A (DLL)
  is independent of this, but Track B is fully blocked on it.
- **FF16Tools may not handle the 1.5 pacs yet.** If 1.5 re-encrypts/rotates the pac key or bumps the
  nxd schema, the pinned 1.13.2 can't decode/encode and `patch_names.py`/`patch_ability_names.py`/
  `recolor_icons.py` cannot rebuild. Wait for an FF16Tools release that handles 1.5; point
  `FF16TOOLS_CLI` at it.
- **NG+ item additions may shift IDs and the 261 wall (the single biggest data risk).** Every
  id-keyed artifact (table XML rows, `item.en.nxd`'s 261 rows, every `.tex` filename) is correct
  ONLY while ids 0-260 keep their vanilla meaning. The frozen 261-entry `additional_data_ids.json`
  and the 261-row `item.en.nxd` are both hard-built to that exact layout and will **silently
  mistarget (not error)** if NG+ inserts/renumbers items. The `AdditionalDataId` indirection for
  armor/accessories is the worst offender (`generate.py` raises only if an id is *missing*, never if
  the mapping is *wrong*). Shield base `SHIELD_DATA_BASE = 128` (id-128 = row) breaks on any
  reshuffle of the 128-143 block. NXD full-table replace means if 1.5 grows the table past 261, our
  file either truncates new items or is size-rejected. The 261 display wall is a boot-built
  registry/page cache (8+ `cmp ...,261` sites; page-guard crashes) -- if 1.5 raises/moves it, our
  row count + icon id range + additional-data map are all invalidated at once. See
  `docs/research/ITEM_CAP_261_BREAK_JOURNEY.md`. Also confirm the HandsFree cap-break auto-arm interaction
  (which corrupted IC "+" items) is not reintroduced.
- **FFTHandsFree may take time to update.** It is the in-house oracle and the cheapest path to the
  new offset map, but it is offset-locked to the same 1.x exe and must do its own re-find first. If
  it lags, our probes can do the full unaided re-find (`sentinel_probe` -> `ct_probe` -> the rest) --
  the expensive path.
- **Offsets may not move uniformly -- DO NOT assume a constant delta.** A recompile relinks code and
  relayouts static data section-by-section; different structures move by different amounts. The
  static-diff landmark technique (Appendix B) finds each base independently for that reason. And a
  patch that *adds a field* to a unit struct reflows the `+0xNN` field offsets too -- so even the
  "sturdier" struct-internal offsets must be spot-checked (status bits, the crystal counter, CT),
  not assumed to survive.
- **Stale-but-valid silent corruption.** The fail-safe RPM/WPM path turns a *moved* address into a
  no-op, but a *stale-but-valid* address (now pointing at unrelated live data) makes a write silent
  corruption. Highest risk: `Barrage.AbilityBase` (writes near the code section, and it's a local
  const not in `Offsets.cs`) and any status-bit write whose offset reflowed.
- **The gates lie about 1.5.** Restating because it is the easiest mistake to make weeks from now: a
  green `BuildLinked`/`Publish`/CI proves the data design and runtime logic, NOT that any DLL address
  points at live state. Live deploy-and-watch is the only real 1.5 sign-off.

---

## Appendix A -- `LivingWeapon/Offsets.cs` absolute-address inventory (PRE-1.5, all presumed moved)

Image base `0x140000000`, no ASLR. Risk class: **CRITICAL-GATE** = a whole family silently dies or
mis-targets; **HIGH** = contained corruption/mis-credit, or a write target where staleness clobbers
unrelated memory; **MED** = guarded no-op/failed read, feature silently off; **LOW** = cosmetic /
read-only fingerprint. Band-/roster-relative `+0xNN` offsets are sturdier than absolute bases but
not guaranteed.

### 1. Battle sentinels & frame-class discriminators
| Name | Address | Meaning | Risk |
|---|---|---|---|
| `Slot0` | `0x14077CA30` | u32 == 0xFF in battle (sticks 0xFF after QUIT) | CRITICAL-GATE |
| `Slot9` | `0x14077CA54` | u32 == 0xFFFFFFFF in battle (sticky) | CRITICAL-GATE |
| `BattleMode` | `0x140900650` | u8 0 = out, 2/3/4 = live battlefield | CRITICAL-GATE |
| `Acted` | `0x14077CA8C` | u8 acting unit acted this turn (global turn-edge) | HIGH |
| `EventId` | `0x14077CA94` | u16 cutscene event id; aliases active nameId in anims | MED |

### 2. Condensed turn-queue / active-unit struct (`TurnQueue 0x14077D2A0`)
TRAP: follows the CURSOR, not always the turn owner. Resolve by HP+MaxHp+level fingerprint.
| Field | Off | Meaning | Note |
|---|---|---|---|
| `TqLevel` | `+0x00` u16 | active level (fingerprint key) | CRITICAL-GATE (base) |
| `TqTeam` | `+0x02` u16 | 0 player / 1 enemy / 2 ally | the parked Shadow Blade gate signal |
| `TqNameId` | `+0x04` u16 | sequential battle index | TRAP -- never resolve by it |
| `TqHp` | `+0x0C` u16 | active HP (fingerprint key) | |
| `TqMaxHp` | `+0x10` u16 | active MaxHP (fingerprint key) | |

### 3. Static unit array (`ArrayBase 0x140893C00`, stride `0x200`; freezes on restart)
20 enemy back + 10 player fwd; `ArrayReadBase = 0x140893C00 - 19*0x200`. Note: `0x140893C00` is a
"cosmetic decoy" for status/charm writes -- the **band is authoritative**. Read-only enemy-side.
| Field | Off | Meaning | Risk |
|---|---|---|---|
| `ALevel/ABrave/AFaith` | `0x0D/0x0E/0x10` | universal unit-identity fingerprint triple | HIGH |
| `AInBattle` | `0x12` u16 | in-battle flag -- TRAP: pulses 0/1, never filter by it | n/a |
| `AHp/AMaxHp` | `0x14/0x16` u16 | current/max HP (corpse detect + all scans) | HIGH |
| `AMp/AMaxMp` | `0x18/0x1A` u16 | MP/MaxMP (SpiritualFont write, self read-back guarded) | HIGH |
| `ACtTurn` | `0x09` u8 | READ for completed-turn counting (>=90 then <70 = one turn) | MED |
| `ACtSlam` | `0x25` u8 | WRITE -- slam to 100 to inject a scheduler turn; do NOT read-trust | HIGH |
| `AGx/AGy` | `0x33/0x34` u8 | grid pos + liveness probe | HIGH |

### 4. Roster (pre-battle equipped gear; `RosterBase 0x1411A18D0`, stride `0x258`, 20 slots)
The weapon->wielder map -- biggest anchor after BattleMode/band.
| Field | Off | Meaning | Risk |
|---|---|---|---|
| `RSupport` | `0x0A` u8 | chosen support ability id | HIGH |
| `RAccessory` | `0x12` u16 | equipped accessory id (Scholar's Ring) | MED |
| `RRHand` | `0x14` u16 | right-hand weapon id (== items.json id) | CRITICAL |
| `RLHand` | `0x16` u16 | left-hand (stays empty live) | LOW |
| `ROffHand` | `0x18` u16 | dual-wield off-hand (real 2nd weapon) | HIGH |
| `RLevel` | `0x1D` u8 | empty-slot guard + drift check | HIGH |
| `RBrave/RFaith` | `0x1E/0x1F` u8 | fingerprint -> combat struct (roster->band bridge) | HIGH |
| `RNameId` | `0x230` u16 | nameId (slot-populated guard) | HIGH |

### 5. Combat-struct band (LIVE unit data; `CombatAnchor 0x14184F890` = Ramza, stride `0x200`)
`BandReadBase = CombatAnchor + 0x1C - 24*0x200`; `BandEntry 0x1C` frames band-vs-combat (every `A*`
band offset = the `C*` combat offset minus `BandEntry`). Self-mapped by `CWeapon`.
| Field | Off | Meaning | Risk |
|---|---|---|---|
| `CWeapon` | `0x20` u16 | equipped weapon id (self-map key) | CRITICAL |
| `CLevel` | `0x29` u8 | level in combat frame (== BandEntry+ALevel) | HIGH |
| `CBrave/CFaith` | `0x2A/0x2C` u8 | fingerprint | HIGH |
| `CHp` | `0x30` u16 | current HP (== band +0x14) | HIGH |
| `CPa` | `0x3E` u8 | physical attack -- WRITE (growth target, headline feature) | HIGH |
| `CMa` | `0x3F` u8 | magic attack -- WRITE | HIGH |
| `CSpeed` | `0x40` u8 | speed -- WRITE | HIGH |
| `CReaction` | `0x94` 4B | reaction passive bitfield (base id 166) | HIGH |
| `CSupport` | `0x98` 4B | support passive bitfield (base id 198, MSB-first) -- WRITE | HIGH |
| `CMovement` | `0x9C` 3B | movement passive bitfield (base id 230) | HIGH |

Band-relative passive views (derived): `AReaction 0x78` (Maim suppress), `ASupport 0x7C` (Choir
Non-charge OR-set), `AMovement 0x80` (Rapture hold). All HIGH (writes).

### 6. Status / death / crystal bytes (band-entry relative bitfields -- offset+mask sturdier, but a
shifted layout silently sets the WRONG status on a write = HIGH silent corruption)
| Name | Off / bit | Meaning |
|---|---|---|
| `ACrystalHearts` | `-0x15` (== combat +0x07) | crystallization countdown 3->0; WRITE-hold at 3 (Sanctuary; newest find 2026-06-16) |
| `ADeadStatus/ADeadBit/AUndeadBit` | `0x45` / `0x20` / `0x10` | dead/undead (corpse detect + FeignDeath) |
| `AReraise/AReraiseBit` | `0x47` / `0x20` | auto-revive; WRITE-hold (FeignDeath, Larceny) |
| `AInvisible/AInvisibleBit` | `0x47` / `0x10` | transparent; WRITE-hold (FeignDeath) |
| `APoison/APoisonBit/APoisonTimer` | `0x48` / `0x80` / `0x4A` (init 36) | poison flag + timer; WRITE (Plague) |
| `ARegen` | `0x48` / `0x40` | regen; WRITE (Larceny steal) |
| `AProtect/AShell/AHaste` | `0x48` / `0x20`/`0x10`/`0x08` | buff bits; WRITE (Larceny steal) |
| `AReflect` | `0x49` / `0x02` | reflect; WRITE (Larceny steal) |
| (Charm) | `0x49` / `0x20` + allegiance `0x54` / `0x20` | CharmLock hold |
| (Doom) | `0x49` / `0x01` + countdown `0x59` (init 3) | EagleEye |
| `AFloat` | `0x47` / `0x40` | reference/probe only (Larceny lists, does not steal) -- LOW |

### 7. JobCommand table -- NOT in `Offsets.cs` (the centralization gap)
`Barrage.AbilityBase = 0x140679436 - 27*25` (record 0's AbilityId1; 25-byte records; flags at base +
rec*25 - 3). Local const in `Barrage.cs`, **reused verbatim by `ShadowBlade.cs`.** The most dangerous
WRITE anchor: a stale-but-valid address writes ability ids into the code-section vicinity = silent
corruption/crash. Paired roster-relative learned-bitfield consts (also local, off `RosterBase`):
`RJobId 0x02`, `RSecondary 0x07`, `RLearnedBase 0x32`, `LearnedStride 3`. DATA: THIEF job 83 / rec
14; `BarrageAbilityId 358` -- IC job-id remap must be re-verified.

### 8. Display / card / menu / equip-mirror
The heap sweep is **dynamic** (`VirtualQueryEx`, no hardcoded heap addr; pattern-scans card text).
Only these four are build-pinned:
| Name | Address | Meaning | Risk |
|---|---|---|---|
| `MirrorWeapon` | `0x141870854` | u16 viewed unit's on-card weapon id (which Kills to show) | CRITICAL-GATE (display) |
| `MirrorOffHand` | `0x141870856` | u16 viewed off-hand (dual-wield card target) | HIGH |
| `WpScratch` | `0x141870836` | u8 equipped-weapon menu WP scratch (Ramza); WRITE | MED |
| `PauseFlag` | `0x140C64A5C` | u8 pause (==1); card-open gate + KillTracker latch | HIGH |
| `MenuCursor` | `0x1407FC620` | menu cursor slot (==3 = Status) | MED |
| `SubmenuFlag` | `0x140D3A10C` | u8 submenu flag (==1) | MED |

### 9. Inventory
| Name | Address | Meaning | Risk |
|---|---|---|---|
| `InventoryCountBase` | `0x1411A17C0` | u8 array, `count[itemId] = base + itemId`; WRITE (ring grant, out-of-battle gated) | HIGH |
| `ScholarRingItemId` | `260` (id, not addr) | item id constant | LOW |

### 10. Treasure Master (full re-capture, not just an offset bump)
| Name | Address | Meaning | Risk |
|---|---|---|---|
| `LiveBattleMapId` | `0x14077D83C` | u8 current battle map id (1..127); stale out of battle | MED |
| `TerrainGrid` | `0x140C65000` | static per-map terrain, 7 bytes/tile; FNV-1a64 fingerprint source | LOW-MED |
| `TreasureDb` bounds | `UiArenaLo 0x140C63000`, `UiArenaHi 0x140CC5000`, `ModuleBase 0x140000000`, `ModuleEnd 0x143000000` | guarded sweep containment window | MED |
| `ArmAudit.ModuleBase` | `0x140000000` (+ PE `ELfanew`/`TimeDateStamp`/`SizeOfImage`) | reads PE header to fingerprint the running build -- the recompile detector that auto-disarms on 1.5 | LOW |

### Load-bearing anchors, ranked by blast radius
1. `BattleMode 0x140900650` + `Slot0 0x14077CA30` / `Slot9 0x14077CA54` -- the in-battle gate.
2. `RosterBase 0x1411A18D0` (stride `0x258`) -- weapon->wielder map.
3. `CombatAnchor 0x14184F890` (stride `0x200`) -> `BandReadBase` -- live-band scan center.
4. `TurnQueue 0x14077D2A0` + `Acted 0x14077CA8C` -- actor identity + turn edge.
5. `MirrorWeapon 0x141870854` -- which-weapon-on-card.
6. `ArrayBase 0x140893C00` -> `ArrayReadBase` -- enemy-side fingerprints + EnemyOracle.
7. `Barrage.AbilityBase ~0x140679436` -- JobCommand inject; most dangerous WRITE, not centralized.

---

## Appendix B -- Build identity + static-diff procedure

```bash
# Confirm which build you are on (Git Bash):
sha256sum "/c/Program Files (x86)/Steam/steamapps/common/FINAL FANTASY TACTICS - The Ivalice Chronicles/fft_enhanced.exe"
# Backup reference: 6DEDDA923D2D834404C4D513DD21AE77F0A09C35E05C59D612A79BFF65EB9C28
#   match    -> still pre-1.5, offsets valid
#   mismatch -> on 1.5, all Offsets.cs addresses presumed moved
```

Static-diff to locate each moved base (no running game needed, no heap noise):
1. Open `C:\Users\ptyRa\FFT_IC_backup_pre1.5\FFT_enhanced.exe` (1.x) and the 1.5 exe side by side.
2. For each static address in `Offsets.cs`: find a stable byte-pattern landmark *around* the old
   address in the backup exe, locate that pattern in the 1.5 exe, take the delta = new base.
3. Do NOT assume one global delta -- sections move by different amounts; find each independently.
4. Live-probe the diffed addresses against the running 1.5 game to confirm.

---

## Appendix C -- Probe re-run order (Track A1, condensed)

`tools/probes/`. `sentinel_probe.py` standalone; `ct_probe.py` is the base import for the rest.

1. `sentinel_probe.py` -> slot0/slot9/battleMode/eventId/pause/submenu
2. `ct_probe.py` -> **keystone**: `ARRAY_BASE 0x140893C00`, `BAND_ANCHOR 0x14184F890`,
   `COND_BASE/TQ_BASE 0x14077D2A0`, scheduler CT (`base+0x41`)
3. `turnteam_probe.py` -> TEAM field `COND_BASE+0x02` (Shadow Blade gate; `ACTED 0x14077CA8C`,
   `MENU_CURSOR 0x1407FC620`)
4. `crystal_counter_probe.py` -> crystal counter `band -0x15` / `combat +0x07`
5. `poison_probe.py` -> Poison `+0x48`/`0x80`, timer `+0x4A`; the watchspan diff recipe
6. `barrage_probe.py` -> JobCommand base `0x140679436 - 27*25`, 25-byte records
7. `noncharge_probe.py` -> support bitfield `band+0x7C`, base id 198
8. `cripple_probe.py` -> reaction suppression `combat +0x94`
9. `oracle_probe.py` -> static-array enemy identity
10. `knockback_probe.py` -> gx/gy desync (parked-feature confirm)

Session one-shots (`set_learned*`, `support_poke`, `roster_dump`, menu/cmdlist scans) write live
memory -- read headers before running, never run blind; re-find on demand only.

---

## Appendix D -- Per-signature break notes (validate riskiest LAST)

Every signature is OFFSET-dependent (its struct/status offsets) and most are also DATA-dependent
(its `meta.json`/`items.json` id must still map to a live item, and any ability id it injects can be
renumbered by 1.5). Foundations gate all of them -- do not trust any row until A3 steps 1-4 are green.

| Signature (id) | Specific 1.5 break |
|---|---|
| GrowthEngine core | combat `CWeapon 0x20/CLevel 0x29/CBrave 0x2A/CFaith 0x2C/CPa 0x3E/CMa 0x3F/CSpeed 0x40/CSupport 0x98`. Shared by ALL growth/support sigs -- a shifted combat field map lands every hold on the wrong byte. |
| Ultima / Materia Blade | PA `+0x3E` + HP/MaxHp band fingerprint; per-tick HP-scaled PA write. |
| Afterimage / Swiftedge | Speed `+0x40`; owns the Speed lane. |
| CharmLock / Galewind (9) | Charm `+0x49`/`0x20` + allegiance `+0x54`/`0x20`; CT `+0x25`. Wrong offset writes a harmful status bit or never holds. |
| ExtraTurn / Zwill | CT slam `+0x25` (read-unreliable on own unit); HP `+0x14`. A CT relocation slams a non-CT byte. |
| EagleEye / Eclipsebolt (78) | Doom `+0x49`/`0x01`, countdown `+0x59`. |
| Ricochet / Stormarc (86) | HP `+0x14`, pos `+0x33/0x34`; consumes `LastPlayerMainHand` + `Acted`. |
| Maim / Huntress (89) | reaction band `+0x78` (4 bytes); a moved field zeroes the wrong 4 bytes (destructive). |
| Larceny / Arcanum (30) | buff bits `+0x47/0x48/0x49`; rides TurnTracker for expiry. |
| Plague / Venombolt (80) | poison `+0x48`/`0x80`, timer `+0x4A` (init 36), CT `+0x09`; mhp*3/32 augment. |
| Barrage / Yoichi (90) | JobCommand `AbilityBase 0x140679436` family + IC job-id remap (THIEF 83 / rec 14, ability 358). Wrong base corrupts a real command list. |
| ShadowBlade / Sanguine (23) | reuses ALL Barrage table primitives -> inherits every Barrage break, wider radius (multiple records). The parked turn-gate fix attaches here. |
| Renewal / Mending Staff (61) | HP `+0x14`, pos `+0x33/0x34`, ally oracle, TurnTracker. |
| Rapture / Rod of Faith (58) | movement band `+0x80` (3 bytes), HP/MaxHp `+0x14/0x16`; move-id 243 can renumber. |
| SpiritualFont / Umbral (56) | pos `+0x33/0x34`, HP `+0x14/0x16`, MP pair `+0x18/0x1A` (own SET/MISS guard; MP half self-disables if moved, HP half still fires). |
| FeignDeath / Wrathblade (27) | **HIGHEST RISK** -- Dead `+0x45/0x20`, Reraise `+0x47/0x20`, Invisible `+0x47/0x10`, HP `+0x14`, CT `+0x25`, turn-queue match. Force-kills + relies on engine auto-revive timing; a CT/status relayout can leave a unit dead-and-scheduled = documented CRASH. Validate LAST. |
| Benediction / Sanctus Staff (64) | HP `+0x14`, ally oracle; gated on `LastPlayerMainHand == 64`. |
| Sanctuary / Staff of the Magi (66) | crystal counter `-0x15` (newest/least-tested), Dead `+0x45/0x20`, HP `+0x14`. Per-tick write -> a mistarget is high-frequency. |
| Choir / Warlock's Staff (60) | support band `+0x7C` for Non-charge id 227 (base 198), pos `+0x33/0x34`, HP `+0x14`, roster `RSupport +0x0A`. Renumbered support base OR-sets the wrong passive bit. |
| TreasureMaster | `LiveBattleMapId 0x14077D83C`, `TerrainGrid 0x140C65000`, `InventoryCountBase 0x1411A17C0`, per-tile flags baked into `treasure.json`. L0 build-key check **auto-disarms on 1.5 by design** -- needs full re-capture + map-id/terrain re-find. |
| ScholarRing / RingGate | `InventoryCountBase 0x1411A17C0`, `RAccessory +0x12`, `RNameId 0x230`; id 260. A moved inventory base corrupts item counts. |

DORMANT (no activating signature on their host item): Wyrmblood (Dragon Rod 57), LifeSap (Umbral
56). But the LifeSap *helper* statics (`NewHp`/`WriteHp`/`HealAmount`) are LIVE -- consumed by
Renewal/Benediction/SpiritualFont -- so the HP-write path at band `+0x14` still needs validation.

---

## Appendix E -- Shipped data artifacts (Track B re-decode/regenerate targets)

Single hand-edited source: `data/items.json` (240 items; `_equipBonus` rows {9,17,22,25,40,56,74-79}).
All artifacts ship from committed copies under `mod/FFTIVC/`.

### Table XMLs -- `mod/FFTIVC/tables/enhanced/` (sparse cell-level merges, load AFTER other item mods)
| File | Generator | Keyed by |
|---|---|---|
| `ItemWeaponData.xml` | `generate.py` | global item id (WP/evade/element/`OptionsAbilityId` BYTE 0-255/formula decimal/range/AttackFlags) |
| `ItemShieldData.xml` | `generate.py` | id - 128 (`SHIELD_DATA_BASE`) |
| `ItemArmorData.xml` | `generate.py` | `AdditionalDataId` (from `additional_data_ids.json`, 261 entries) |
| `ItemAccessoryData.xml` | `generate.py` | `AdditionalDataId` |
| `ItemData.xml` | `generate.py` | global item id (`EquipBonusId` ptr, category/flags/shop) |
| `ItemEquipBonusData.xml` | `generate.py` | EB row id (the new `_equipBonus` rows) |
| `JobData.xml` | `make_jobequip.py` (reads live `TableData/JobData.xml`) | job id (cross-equips + evasion floor; strips Axe/Flail) |
| `JobCommandData.xml` | `make_jobcommand.py` (reads live `TableData/JobCommandData.xml`) | rec id (zeros Equip Axes id 460) |

`generate.py` does NOT emit `JobData.xml`/`JobCommandData.xml` -- the two `make_job*` scripts produce
them from the modloader vanilla templates at `lib.paths.TABLE_DATA`; they are manual regen steps, not
in BuildLinked/Publish.

### NXD full-table replaces -- `mod/FFTIVC/data/enhanced/nxd/` (full-table replace, base pacs encrypted, `-g fft`)
| File | Generator | Rows |
|---|---|---|
| `item.en.nxd` | `patch_names.py` -> `working/pilot_item.sqlite` -> FF16Tools | 261 (ids 0-260); names/descs/category/sort/`UiStatusEffectId` + baked Kills scaffold. NOT from generate.py |
| `ability.en.nxd` | `patch_ability_names.py` -> `working/nxd_ability/ability.sqlite` -> FF16Tools (self-verifying) | full; only Barrage 358 differs |
| `*.bloodpact_parked` | parked, NOT shipped | corrupted abilities -- do not revive |

Reconciled 2026-07-11 (LW-74): the Offensive Chemist removal (a5ea61e) dropped the ItemData
grenade rows 246-252, the whole `ItemConsumableData.xml`, and the ability.en.nxd grenade
learn-names 374-378; the tables above reflect the post-removal set.

### Icons -- `mod/FFTIVC/data/enhanced/ui/ffto/icon/` (478 BC7 .tex = 239 items x 2)
`equip_item/texture/ei_NNN_uitx.tex` (100x100) + `equip_item_s/texture/ei_s_NNN_uitx.tex` (48x48),
keyed by 3-digit global item id. Generated by `recolor_icons.py` (vanilla .tex -> tex-conv -> DDS ->
Pillow HSV recolor -> img-conv --no-chunk-compression -> .tex); tints from `ICON_TINTS` + per-item
`iconTint`/`iconSource`.

### Refs to diff against the 1.5 base
`working/ref/{itemdata,equipbonus,armor,accessory}.json`, `working/pilot_item.sqlite`,
`working/th_item.sqlite`, `working/nxd_ability/ability.sqlite`.

### Key files (absolute paths)
- Source of truth: `data\items.json`, `data\additional_data_ids.json`
- Generators: `tools\{generate,gen_living_weapon_meta,patch_names,patch_ability_names,decode_tables,recolor_icons,make_jobequip,make_jobcommand}.py`
- Shared lib: `tools\lib\{paths,nxd,items,categories,flavor}.py` (`FF16TOOLS_CLI` env override + `STEAM_FFT`/`TABLE_DATA` in `paths.py`; `-g fft` encode in `nxd.py`)
- Pipeline: `tools\pipeline.ps1`, `BuildLinked.ps1`, `Publish.ps1`
- Offsets: `LivingWeapon\Offsets.cs` (single source of every hard address except the JobCommand + TreasureDb local consts)
- Tuning/data ids: `LivingWeapon\Tuning.cs` + `meta.json`
- Probes: `tools\probes\` (`ct_probe.py` + `sentinel_probe.py` are keystones)
- Modloader dep: `mod\ModConfig.json`
- Upstream offset map to re-crib: `c:\Users\ptyRa\Dev\FFTHandsFree\docs\BATTLE_MEMORY_MAP.md`
- Known-good 1.x exe: `C:\Users\ptyRa\FFT_IC_backup_pre1.5\FFT_enhanced.exe` (SHA256 `6DEDDA923D2D834404C4D513DD21AE77F0A09C35E05C59D612A79BFF65EB9C28`)
- Wall context: `docs\research\ITEM_CAP_261_BREAK_JOURNEY.md`
