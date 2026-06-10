# Session Handoff — Living Weapon state of the world (2026-06-10, post bow-pass marathon)

Everything below is COMMITTED on `living-weapon` and deployed as a DEV build (LwDev=true: kill
thresholds {1,2,3}, all weapons seeded to P2, verbose `ev:` log). THIS file is the uncommitted
working doc and carries only what a fresh session needs. The all-nighter's arc: bow pass shipped
(Chain Lightning / Maim / Barrage all LIVE-VERIFIED), the ability-name wall fell, and the
"4th battle command row" hunt hit a definitive Denuvo wall after producing a working inline-hook
capability and a complete battle-menu architecture map.

## What ships now (all live-verified by Patrick unless noted)

- **The bow pass is DONE** (`docs/living_weapon_bows.csv` = shipped truth, ready to merge into
  the grid like knives/crossbows):
  - **Stormarc "Chain Lightning"** (Ricochet.cs): +3, wielder's damage chips the nearest OTHER
    enemy within 3 tiles for 50% (floor 1, never kills, no chains). Test config reverted
    (wp5/range7/radius3). Card: `+3 Ability — Chain Lightning` / "Normal attacks also hit an
    additional unit within 3 tiles at 50% damage." BALANCE OPEN: Aim-only gating still wants
    the action-id probe.
  - **Huntress "Maim"** (Maim.cs): +3, struck enemies lose their REACTIONS for 3 of their turns
    (save once → hold zeros → restore; combat +0x94 = band +0x78, 4 bytes; re-hit refreshes;
    allies never latched). Base edit shipped: proc 214 Arm Shot → 213 Leg Shot.
  - **Yoichi Bow "Barrage" — FINAL: THIEF-ONLY** (Barrage.cs): at +3 a THIEF wielder (job 83)
    gains the Barrage command (ability 358, 4-arrow volley) inside Steal — injected into the
    JobCommand table (page 14) + the learned bit HELD per tick (menu purchases stale-writeback-
    wipe external bits — proven by purchase diff). Card: `+3 Ability — Barrage (Thief Only)` /
    "A Thief wielder gains the Barrage command: a volley of four swift arrows."
    `IsEligibleWielder(83)` enforces it; all other jobs skip (the why is the menu-architecture
    section below). Restores on unequip; learned-bit residue is inert; table rebuilds at boot.
  - Perseus: signature DROPPED (pure growth). Skirmisher "Loosed and Gone" + the Phase-1 items
    were NOT shipped this pass (see open threads).
- **ability.en.nxd is SHIPPED AGAIN (text-only) — the Bloodpact parking is partially superseded**:
  ability 358 has Name "Barrage" / Patrick's desc / icon 32 via `tools/patch_ability_names.py`,
  which builds from the vanilla decode (`working/nxd_ability/ability.sqlite`) and SELF-VERIFIES
  exactly the intended cells differ before deploying (anti-Bloodpact gate). KEY INSIGHT: the
  modloader merges nxd tables CELL-level against vanilla, so it coexists with GenericJobs' file.
  `overrideabilityactiondata.nxd` (mechanics) stays PARKED.
- **Barrage debugging yielded live-proven plumbing** (memory `barrage-jobcommand-injection`):
  JobCommand table = 176 rows × 25B (NOT ~200 — rec 199 reads past the end), extend bits
  MSB-first per byte (byte0=slots 1-8, byte1=slots 9-16; the probe's old whole-u16 'msb' was
  WRONG and now fixed); free pages 77-102 (103 occupied); roster job band 74-92 = PSX wheel
  order (rec = job−69, learned jobIdx = job−74, Dance shares 17); roster +0x07 = the secondary
  command's REC ID (game re-derives it at menu refreshes — one-shot writes lose, holds win);
  per-job JP = u16 array at roster +0x80 + jobIdx*2.
- **NEW CAPABILITY: Denuvo-safe inline hooks from our DLL, proven live.** A Reloaded.Hooks
  detour on the battle-unit constructor at **0x140280884** armed and fired 40×/battle-load with
  zero crashes (scaffolding REVERTED from the shipping build; recipe = add
  reloaded.sharedlib.hooks + Reloaded.Memory.SigScan.ReloadedII ModDependencies,
  Reloaded.Hooks.Definitions + Reloaded.SharedLib.Hooks NuGets, resolve IReloadedHooks in
  Mod.StartEx via Reloaded.Mod.Interfaces.Internal, CreateHook(...).Activate()). The
  constructor: rdx = 0x280-byte battle unit struct (memset then filled), r8 = roster record.
  **Struct map: primary command PAGE u16 at +0x5E, secondary at +0x60, reaction/support/
  movement ids at +0x62/+0x64/+0x66.** HW breakpoints/page guards still crash; function-entry
  detours in plain code are fine; anything in the 0x14D... region is Denuvo-encrypted — do not hook.
- Everything from before stands: Zwill extra turn v8, tracker/band capture-hybrid, BattleState,
  first-kill credit, cards/counter painter, grant verification, crossbow signatures
  (Eagle Eye runtime; Venombolt Plague still card-only), Thief has Bows.

## The battle-menu architecture (mapped 2026-06-10 — read before ANY command-row idea)

A unit's battle command rows come from exactly four sources, all assembled at BATTLE-INIT:
1. Attack/Move/Wait — fixed.
2. PRIMARY = the job's command page (JobData.JobCommandId; vanilla job 77 Archer → page 8).
3. SECONDARY = roster +0x07 (one byte, one slot — the entire extra-command budget).
4. Support-granted rows (Reequip 480 → page 3, Evasive Stance 479 → page 2): the row RENDERS
   from the page (re-aliased page 3 listed Barrage!) but EXECUTION is welded to the hardcoded
   handler by page id — both Reequip rows ran reequip regardless of contents. Innate supports
   (JobData.InnateAbilityId) do NOT mint rows.
- **CommandTypeData** (modloader `CommandTypeData.xml`, FFTPatcher "Action Menus"): 1 byte per
  page selects the menu EXECUTOR (Default/Aim/Jump/Items/Throw/Math/...). Pages 77-102 are
  Default — why an injected free page renders+casts perfectly as a secondary. Page 8 = Aim:
  its executor id-whitelists 406-413 IN CODE (foreign ids render a label + positional
  basic-attack preview, then silently drop at confirm — proven with 358/102/146/16 across
  slots 1/9/10/11 on two units). Re-aliasing page 8 → Default would kill real Aim (tiers
  bounce under generic executors — proven both directions).
- **The 4th-row wall (definitive)**: the battle struct has only TWO command-page fields
  (+0x5E/+0x60); every readable accessor of them is plumbing (constructor, normalizer at
  +0x28162E region, slot-memcpy at 0x14028572F, UI layout math); the only LOGIC read of the
  command page lives at **0x14D359159 — inside Denuvo-encrypted memory**, unhookable. A true
  standalone row needs that decision code = off the table without breaking Denuvo. The
  page-claim tech (fill free page 100 + hold +0x07 + jobcommand.en rename) WORKS and is in the
  back pocket if a "replace your secondary with a named Barrage command" mode is ever wanted;
  JobData.xml JobCommandId repoint (e.g. Archer's Aim → custom page) also works data-only but
  is job-wide. Curator chose the clean Thief-only ship instead.

## Open threads (none blocking)

1. **Category rollout — NEXT: RODS** (`docs/living_weapon_rods.csv`, split from the grid like
   bows; staffs after). Grid rows carry OLD multi-grant drafts — re-curate to the knife model
   (growth everywhere, ONE P3 signature on iconic picks, additive-only). Candidates begging:
   Wellspring MP-siphon, the Spark/Ember/Frost identity trio, Umbral dark-amp, Dragon Rod's
   Silence+Reraise kit. New tech available: status pins (poison recipe), ability.en renames.
2. **Venombolt "Plague" runtime** (card ships, runtime TODO): never-expire + 1.75× tick via
   the acted-latch + hold; prototype in poison_probe.py `venom`.
3. **Skirmisher "Loosed and Gone"** (timed Speed +2, 3 turns) — first HoldTimedStat user, was
   deferred out of the bow pass; ship with a later pass.
4. **Chain Lightning Aim-gate** — needs the action-id probe (what ability is the actor using);
   candidates: watch 406-413 during Aim. Fallbacks: once-per-acted-period, lower pct.
5. **SlamCt 105 queue-jump probe**; **generalize the extra turn** (hardcoded ZwillId).
6. On-crit detection; status-bit mapping continuation; Doom/DoT kill credit; revert-on-loss;
   cosmetic seams; KillTrackerTests split (200-line debt).
7. **Adversarial verification re-run** — the bow-pass verifier workflow was stopped mid-run
   during live debugging and never re-run against the final state (commits through dbc90e5).
8. **Tidecaller/Frostarc prose-stale fixes** + Tidecaller identity decision (pre-release).

## Dev harness facts

- Deploy = kill FFT_enhanced.exe, `.\BuildLinked.ps1`. Tables/nxd on game RESTART.
- items.json → patch_names.py → item.en.nxd (cards); ability text → patch_ability_names.py →
  ability.en.nxd (cell-merge safe, self-verifying). jobcommand.en.nxd rename = same recipe if
  the page-claim mode is ever shipped.
- Probes in `%TEMP%\fft_probes\`: barrage_probe.py (FIXED bit order; dump/scan/inject/restore),
  learned_probe.py, purchase_diff.py, hold_secondary.py, menu_diff/cmdlist/jobpage scans,
  roster_head.py, ct_probe.py, poison_probe.py. All RPM/WPM crash-safe.
- Vanilla table dumps (schemas for every modloader hardcoded table incl. JobData/
  JobCommandData/CommandTypeData): `...\Reloaded\Mods\FFTIVC_Mod_Loader\TableData\*.xml`.
- 315 unit tests; both gates enforced by BuildLinked/Publish/CI. Before RELEASE: `Publish.ps1`
  (prod thresholds {5,20,50}, no seeding).
