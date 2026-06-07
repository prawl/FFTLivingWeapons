# Weapon "May Cast: X" for abilities >255 — relocate the effect into a ≤255 host slot

**Question:** A weapon's "May Cast: X" references the ability by id. Monster/boss/cut abilities live at
ids ≥256. The id slot is one byte (≤255). Can we get a weapon to cast a >255 ability?

**Answer:** Not by id — the field is a hard byte. But you can **relocate the ability's *effect*** into a
free ≤255 ability slot via the `OverrideAbilityActionData` Nex table, then point a Formula-2 weapon at
that slot. **Confirmed live 2026-06-03** (Vagabond → "May Cast: Dispose", Dispose's id 353 relocated to
host slot 219).

---

## Why the id field can't just hold >255 (the byte wall)

`ItemWeaponData.xml` `<OptionsAbilityId>` (the "May Cast" ability, used when `<Formula>2`) is a **single
byte, 0–255**. Confirmed three independent ways:

1. On-disk weapon struct is **8 bytes**, cast-id at the terminal offset `0x07`, size 1 — a `u16` would
   spill into the next weapon's Range byte. (FFTHandsFree `docs/Wiki/GameDataStructs.md`.)
2. The modloader's editable model types it **`System.Nullable<System.Byte>`** (reflected from
   `fftivc.utility.modloader.Interfaces.dll`).
3. `tools/generate.py:68-72` hard-rejects `onHitAbilityId > 255`.

Storing >255 anyway **wraps mod-256** (e.g. 282 → 26, casts the wrong ability) **or** throws
`YAXBadlyFormedInput` and **silently drops the entire `ItemWeaponData` table back to vanilla**. So the
generator's hard error is load-bearing — keep it.

### Don't confuse this with the skillset fold

There are **two different "ability id" fields**, and only one is byte-limited:

| Field | Width | >255? | Use |
|-------|-------|-------|-----|
| `ItemWeaponData` `OptionsAbilityId` (weapon May-Cast proc) | **8-bit byte** | ❌ no | this doc |
| `JobCommandData` `AbilityIdN` (command-menu fold) | **16-bit** (+ `ExtendAbilityIdFlagBits` "+256") | ✅ yes | `docs/FOLDABLE_ABILITIES.md` |

The earlier "we made a >255 ability work" proof was the **menu fold** (Bad Breath 328, Mighty Guard 339,
Vengeance 256 cast from Ramza's Mettle) — a different code path. It does **not** transfer to the weapon
proc. Widening the weapon byte itself = a native engine patch, Denuvo-walled (`docs/PROC_RATE_RESEARCH.md`).

---

## The workaround: `OverrideAbilityActionData`

Ability *effects* are not in the XML tables (`AbilityActionData.xml` is an empty stub). They live in the
Nex table **`OverrideAbilityActionData`** — a **sparse patch layer keyed by ability id**
(`use_base_row_id=true`), 368 rows (ids 0–367). Layout:
`…/FFTIVC_Mod_Loader/Nex/Layouts/ffto/OverrideAbilityActionData.layout`.

| Column | Meaning |
|--------|---------|
| `Key` | ability id being patched |
| `Flags12` / `Flags34` | bit-index arrays patching Flags1–4 |
| `Range` `EffectArea` `Vertical` `Element` `Formula` `X` `Y` `InflictStatus` `CT` `MPCost` | effect fields |

**Rule:** a column value **≥0 is cast to byte and overrides** that field; **`-1` = leave the base value.**
Default rows are all `-1`.

**Recipe (generalized):**
1. Pick a **free ≤255 host Key** — a cut/unused ability, ideally one whose *animation* suits the effect
   (the override has **no animation field**, so the visual stays the host slot's).
2. Author the host's override row to fully define the effect (set every field that matters; don't rely on
   the host's base).
3. Re-encode the nxd and deploy.
4. Point a weapon at it: `items.json` → `proposed.formula: 2`, `proposed.onHitAbilityId: <host Key>`.
5. (Optional, for the card) rename the host ability in `ability.en.nxd` so "May Cast: X" reads right.

Tooling: `FF16Tools.CLI.exe` `nxd-to-sqlite` / `sqlite-to-nxd`, both with `-g fft`. Other shipped mods
(Regabonds, spell-overhaul, Blue/Red Mages) already use this table — decode one for value templates.

---

## Worked example — Vagabond casts Dispose

- **Ability:** Dispose = id **353** (Construct 8 / Worker 8; Formula `0x42`, `PA*Y` + self-damage `PA*Y/X`).
- **Host slot:** **219** — vanilla "Crushing Blow", a cut `Normal`-type sword attack
  (`AIBehaviorFlags = TargetEnemies, HP, PhysicalAttack`). Real melee animation, nothing references it.
- **Weapon:** **Vagabond** (item 19, the Broadsword) — early, cheap, no prior proc.

Override row written at `Key=219`:

```
Range=1  EffectArea=0  Vertical=2  Element=-1  Formula=45  X=100  Y=20  InflictStatus=-1  CT=0  MPCost=0
```

> ⚠ We used **Formula 45** (a live-confirmed physical-nuke; `Y`=power) for a clean, reliable first test —
> NOT Dispose's true **`0x42`** self-damaging formula. To make it the *exact* Dispose (recoils on the
> wielder), change `Formula 45 → 66`. `0x42` is less-tested in the override path.

Then: `items.json` Vagabond `proposed.formula=2 / onHitAbilityId=219`; `ability.en.nxd` Key 219 renamed
`Crushing Blow → Dispose`; `tools/generate.py` rebuilds the weapon table; deploy + restart. Result: ~19%
on-hit physical "Dispose" strike on the struck enemy. **Live-confirmed.**

---

## Gotchas (what bit us, what to remember)

- **Build the override from the CURRENT deployed nxd, not `working/action.sqlite`.** That cached copy was
  **stale — missing live rows 16 and 223** (an existing relocated proc). Starting from it would have
  silently wiped them. Always decode the deployed `overrideabilityactiondata.nxd` first, edit, re-encode,
  and diff the non-default Key set before deploying.
- **Round-trip every encode.** `sqlite-to-nxd` then `nxd-to-sqlite` back, and confirm `row[host]` plus that
  all other rows survived. A malformed nxd makes the modloader silently fall back to vanilla.
- **Weapon procs are single-target** (they ignore `EffectArea`) and fire on the **struck enemy** at ~19%
  (50/256). AoE monster spells lose their AoE; offense/debuff = good, heals/buffs would benefit your victim.
- **Animation stays the host's** — pick the host for its look, not just a free slot.
- **`generate.py` reads the `proposed` block** of each `items.json` weapon and emits `<Formula>` +
  `<OptionsAbilityId>`. `BuildLinked.ps1` (dev) / `Publish.ps1` (prod) = generate → diversity-gate
  (`analyze.py`) → bake `meta.json` → build the DLL → copy `mod/FFTIVC` to the live Reloaded mod dir (or
  package it). Data-only at the table level → **restart the game** to apply.
- **Env:** use `python` (real CPython); `python3` is the broken MS-Store stub. No `sqlite3` CLI — use the
  python module. The real ability list is the vanilla `AbilityData.xml` (512 rows, ids 0–511) or a decode
  of `ability.en.nxd` — there used to be a 0-byte `ability.sqlite` stub in the repo root; it was removed.

---

## Status

- ✅ Byte wall confirmed three ways; relocation workaround confirmed **live** (Dispose → host 219 → Vagabond).
- ✅ Reusable: any >255 ability's *effect* can be relocated to a free ≤255 slot (mind the animation caveat).
- ⛔ True id-level >255 in the weapon field still needs a native engine patch (Denuvo-walled, see
  `docs/PROC_RATE_RESEARCH.md`).

Related: `docs/FOLDABLE_ABILITIES.md` (the 16-bit menu fold), `docs/PROC_RATE_RESEARCH.md` (the 19% lever +
Denuvo wall), `docs/UNTAPPED_WEAPON_KNOBS.md`. Origin recipe: FFTColorCustomizer memory
`project_fft_weapon_cast_abilities`.
