# FFT Living Weapons: Design Doc (living draft)

**Status:** shipped (v2.x); living draft. Started life as a pure-data item rebalance ("Item Overhaul");
the project's center of gravity is now the **Living Weapon**, with the rebalance as its foundation.
**Date:** 2026-05-29 (thesis reframed 2026-06-17)
**Goal:** Make each weapon a character that **grows as it kills** -- and rebalance **all** items so the
weapon you invest in never becomes vendor trash. Every item should have a reason to exist; new gear
should not auto-obsolete old gear.
**Mechanism:** two layers shipped as one Reloaded mod -- an in-process C# DLL (`LivingWeapon/`) for the
growth + per-weapon signature runtime, layered over a pure-data item rebalance on Nenkai's
`fftivc.utility.modloader` (same channel as "WotL Equipment Replacer / Treasure Hunt" and
`FFT.Regabonds.Rebalance`).

---

## 1. The thesis (the spine of the whole mod)

> **A weapon is a character: it grows as it kills, and the iconic weapons awaken signature abilities.
> For that to mean anything, the gear around it can never become vendor trash -- so the item rebalance
> below is the foundation, not a side feature.**

The growth runtime is the headline; the rest of this section is the rebalance that keeps a grown weapon
worth keeping. The rebalance rule:

> **Let Weapon Power (and HP) climb gently and predictably across chapters, but load every item's
> *longevity and interest* into its non-WP dimensions** (evasion, element, on-hit status, stat bonus,
> movement, status immunity, element resist).

One move, three problems solved:

- **Old gear stays relevant** (no throwing items away): a higher-WP weapon never power-creeps the
  Ancient Sword's "inflicts Old" or Nagnarok's 50% evade. Raw power creeps; *identity* doesn't.
- **Gear matters under enemy-buffing mods** (Level Scaling, Strong Monsters): defensive/utility profiles
  (evade, immunity, resist) outvalue raw HP when enemies hit harder.
- **Gear competes with free abilities** (All-Skills-Cost-0, Spell Overhaul): evasion, status immunity,
  innate status are things passive *abilities can't replicate*; raw stats are boring next to free skills.

**The acceptance test for every item:** *"When would I pick THIS over its neighbors?"* Only three valid
answers: **Niche** (best for a build), **Tradeoff** (power for evade/element/status/stat), or
**Availability** (a fine budget pick, not strictly dominated forever). Vanilla FFT fails all three for
most non-top-tier gear, which is why the roster is full of vendor trash.

### Sidegrades, not strict tiers
Vanilla swords are a strict ladder (Broadsword 4 → Longsword 5 → … → Runeblade 14). We re-expand the
~5-dimensional design space (WP + Evade% + Element + on-hit Status + flags) into **overlapping profiles**:

| Weapon | WP | Evade | Extra | Pick when… |
|---|---|---|---|---|
| Broadsword | 9 | 0% | cheap | you just want raw damage |
| Longsword | 6 | 15% | none | front-line duelist who dodges |
| Coral Sword | 7 | 5% | Lightning | exploit Ice-shield enemies |
| Ancient Sword | 6 | 5% | inflicts Old | debuff utility |
| Runeblade | 7 | 10% | +2 MA, Dark | magic-knight hybrid |
| Nagnarok | 5 | 50% | none | evasion tank |

(Illustrative numbers, not final.) Nothing strictly dominates; you choose a *profile*. Legendaries
(Chaos Blade, Excalibur) stay iconic-strong but pay a real cost so they're a *choice*, not an auto-include.

---

## 2. How items work (the system we're editing)

### Data model: every item points at secondary tables
```
Item Data (0x0C/entry, ≤261 ids) ──AdditionalDataId──▶ Weapon / Shield / Head-Body / Accessory secondary
                                  ──EquipBonusId──────▶ Item Attributes (PA/MA/Spd/Move/Jump, innate status, element)
   fields: Palette, SpriteID, RequiredLevel, TypeFlags, ItemCategory, Price, ShopAvailability
Name + description ───────────────▶ item.en.nxd (separate binary NXD)
Menu icon ────────────────────────▶ ei_<id>_uitx.tex (+ ei_s_<id> small)   [FF16 "TEX " format]
```
The modloader exposes all of this as **sparse XML** (include only `<Id>` + changed fields; rest inherits).
Tables and hardcoded size caps: ItemData ≤261 ("256 + 5 extended"), Weapon ≤127, Armor ≤63, Accessory
≤31, Shield ≤15, EquipBonus ≤84, Shops ≤255, MapTrapFormation ≤127.

### Combat model: damage is formula-by-weapon-type
| Type | Formula | Design consequence |
|---|---|---|
| Sword/Spear/Rod/Crossbow | `PA × WP` | linear → top WP always wins, no tradeoffs |
| Knight Sword/Katana | `PA × Br/100 × WP` | Brave-scaling; Chaos Blade outlier |
| Knife/Bow/Ninja Blade | `(PA+Sp)/2 × WP` | speed-scaling; good for Dual Wield |
| Staff/Pole | `MA × WP` | caster melee |
| Book/Instrument/Cloth | `(PA+MA)/2 × WP` | hybrid |
| **Gun** | `WP × WP` | **quadratic, ignores PA & evasion**, the broken one |
| Axe/Flail/Bag | `Rand(1..PA) × WP` | swingy; players avoid |

**Lever confirmed:** the per-weapon `<Formula>` field is editable (Regabonds' Mage Masher uses Formula 47
= Blood Suck; the Treasure Hunt Orochi uses Formula 6 = absorb-HP). So we can re-curve individual weapons
(e.g. take a gun off `WP²`). `OptionsAbilityId` is the on-hit status/spell hook.
**Open:** whether `WP²` is the gun's *Formula value* (overridable) or hardcoded by item type, pending the
reference-mod teardown + an in-game test.

(Source: FFTHandsFree `docs/Wiki/{Equipment,DamageFormulas,FormulaTable,GameDataStructs}.md`; the Treasure
Hunt + Regabonds XML teardown.)

---

## 3. Mod-coexistence strategy (user assumption: "people use ALL the mods")

Audit of the user's installed stack:

| Bucket | Mods | Interaction |
|---|---|---|
| Item-table editors | **Regabonds.Rebalance** (93 weapons, 64 armors, +abilities/jobs/spawns), **Treasure Hunt** (11 items) | direct field conflict; load order decides per field |
| Combat-context shifters | Level Scaling, Strong Monsters, Spell Overhaul, All-Skills-Cost-0 | no field conflict, but change the math our gear lives in |
| Clean compose | GenericJobs, WotL Characters, Innate Skills, Blue/Red Mages, color mod | no interaction |

**DECISION (2026-05-30):** the user will **disable conflicting item mods** (Regabonds, Equipment Replacer)
rather than coexist with them. So we are simply *the* authoritative item mod; no load-order fighting
needed. `scan_conflicts.py` becomes a "here's your disable list" advisor. We still compose cleanly with all
non-item mods (Level Scaling, jobs, skills, spells). The hardening points below are kept as good practice
(complete-per-item, ratio balancing) but are no longer load-bearing.

**Blunt truth:** you cannot *coherently* stack two full item overhauls. If we and Regabonds both set item
20's WP, last-loaded wins that field; if we win WP but they win evade, the item is a frankenstein neither
designed. So "harden for stackers" means:

1. **Be authoritative + complete-per-item.** Set *every* field on every item we touch, so load order
   yields a clean winner (us-or-them per item), never a hybrid. Document "load after other item mods."
2. **Ship a conflict scanner.** Tool reads other installed mods' item XMLs and reports the exact item IDs
   that collide. The honest way to support stackers.
3. **Compose with the context-shifters and clean-compose buckets**, that's where the real "all the mods" value lives, not item-vs-item.
4. **Balance on ratios, not absolute breakpoints**, so enemy-stat-scaling mods don't break us.

**Positioning vs Regabonds:** Regabonds is a *smoothed vertical curve* (knife line WP 3→4→4→5→5, flat 15%
evade, still strict-dominant within a category). Our *sidegrade/identity* philosophy is a different,
more ambitious design. Not redundant; mutually exclusive on items. Users pick one item mod, ours composes
with everything non-item.

---

## 4. Architecture (mirror the color-mod registry pattern)

Do **not** hand-edit ~256 items across 8 XML tables. Single source of truth → generated outputs:

```
items.json   (one row per item: id, type, every stat, name, icon ref, design-intent tag)
   ├─ generator ──▶ the 8 sparse modloader XMLs (only fields we set) + item.en name/desc patches
   ├─ analyzer  ──▶ DOMINANCE CHECK: flags any item strictly dominated by a same-category item;
   │                plots the WP/HP power curve per chapter band; lists "dead" (no-identity) items
   └─ scanner   ──▶ CONFLICT CHECK: reads other installed mods' item XMLs, reports colliding ids
```

The **analyzer is the secret weapon**: it lets us *prove* "no item is strictly dominated" across the whole
roster instead of eyeballing 256 of them, and keeps the build-diversity invariant enforceable as we tune.

---

## 5. Per-category design levers

- **Weapons:** WP (gentle climb) · Evade% · Element · on-hit Status (`OptionsAbilityId`) · Formula (re-curve
  outliers) · flags (2H/dual/throwable). Give every weapon a profile from these.
- **Shields:** Phys Ev / Mag Ev split + element interactions. Make the mid-tier shields distinct
  (anti-magic vs anti-physical vs element-niche) instead of a strict evade ladder.
- **Head/Body armor:** HP vs MP vs (via EquipBonus) innate status / element resist / stat. Kill the
  identity-less "pure HP" armors by giving each a small hook.
- **Accessories:** the richest identity slot already (stat / movement / status-immunity / element). Problem
  is opportunity cost: most lose to a flat stat booster. Fix by making protections *also* carry a minor
  stat or by tightening the stat boosters.
- **Consumables:** lower priority; mostly fine. Possible: smooth the Potion → Hi → X curve, fix Phoenix
  Down's ~1 HP glitchiness if reachable.

---

## 6. Enemy-side consideration (do NOT forget)

Enemies equip from the **same item tables** (gated by `RequiredLevel`). Restatting gear changes enemy power
too; buffing swords buffs every enemy Knight. The overhaul is really a **combat rebalance wearing an item
hat.** Helpfully, our thesis (value in defensive/utility dims) aligns: when enemies are tougher (Level
Scaling/Strong Monsters), evade/immunity/resist gear gets *more* valuable, exactly what we're emphasizing.
Design player and enemy loadouts together; use `RequiredLevel` to control when stronger gear enters the
enemy pool.

---

## Gotchas (learned in the knife pilot; these apply to the WHOLE overhaul)

1. **Formula IDs in the XML are DECIMAL; every FFT reference (FFHacktics, our docs) lists them in HEX.**
   `<Formula>47</Formula>` = decimal 47 = `0x2F` = **Absorb MP** (Dark Sword), NOT `0x47` Blood Suck.
   Confirmed in-game (Sanguine Gauche with 47 stole MP). HP-absorb = decimal **48** (`0x30` Night Sword).
   The absorb formulas also switch damage to `PA*WP` (off knife speed-scaling), a deliberate tradeoff.
   ALWAYS hex→decimal convert formula/effect ids. (Small ids where hex==decimal, like Blind 9, Doom 11,
   Sleep 12, happen to be safe.)

2. **The description's "Special Effect" badge is a SEPARATE nxd field: `UiStatusEffectId` in item.en.nxd**,
   not auto-derived from the weapon row. The IVC item table uses only `1001` (="Absorbs HP") and `1002`
   (healing staff). Status procs (Blind/Silence/Doom/Sleep) and elements are conveyed by the DESCRIPTION
   TEXT (UiSE=0), not a badge. So: set `UiSE=1001` on HP-absorb items; write the status into the
   description prose for everything else. Renaming/restatting an item means re-syncing its `UiSE`.

3. **NXD overrides are FULL-TABLE replace** (item.en = all 261 rows) and base game pacs are ENCRYPTED
   (FF16Tools needs `-g fft`; list-files on locale pacs was still flaky). Pilot uses the Treasure Hunt
   item.en.nxd as a stand-in vanilla base (vanilla except their 11 renames; only id3 overlaps a knife).
   TODO: extract the true vanilla item.en.nxd for the full build. NXD round-trip = `nxd-to-sqlite` →
   edit `Item-en` table → `sqlite-to-nxd`, both with `-g fft`.

## 7. Open questions / next steps

- [ ] **Formula override scope**: can the `<Formula>` field re-curve guns off `WP²`, or is `WP²` hardcoded
      by item *type*? (reference-mod teardown + in-game test)
- [ ] **Add vs replace**: are the 5 "extended" item slots (256–260) usable for net-new items, or are we
      strictly restatting the existing roster? (reference-mod teardown). Not blocking; a rebalance restats.
- [ ] **In-hand graphic**: `SpriteID`/`Palette` select the wielded-weapon look; what to change for a new
      battle appearance (vs just the menu icon). (reference-mod teardown, may be UNCONFIRMED)
- [ ] **Pilot category**: prototype one category (proposal: **weapons**) end-to-end: items.json schema →
      generator → analyzer (prove no strict dominance) → deploy → in-game check.
- [ ] **Naming**: mod name + working folder name.

### Immediate next step
Build the **vertical slice**: `items.json` schema + generator + dominance analyzer for **one weapon
category** (e.g. swords or knives), deploy, and confirm in-game it loads and recolors the curve. If the
pipeline holds, scale to all categories.
