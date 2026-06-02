# Untapped weapon-design knobs (sweep 2026-06-02)

Pure-data levers we have NOT shipped yet, found by sweeping the FFHacktics formula table, the
weapon/EquipBonus fields, and the ItemOptionsData proc pool against our confirmed/dead list.
Viability rule: a formula works on a weapon only if it scales by WP or a unit stat (PA/Speed/HP/MP/
Brave/Faith) — anything keyed off an ability X/Y param is dead (weapon has no X/Y).

## New damage formulas (new archetypes)

| formula | archetype | what it does | viability |
|---------|-----------|--------------|-----------|
| **0x45 / 69** | **Executioner** | dmg = TARGET's missing HP — useless on full-HP, lethal as a finisher. Mirror of confirmed 0x43 berserker. (We have Climhazzard rigged but never live-confirmed.) | needs-test (same class as 0x43) |
| **0x44 / 68** | **Mana-reaver** | dmg = target's CURRENT MP, and drains it — trivial vs fighters, devastating vs casters (+ denies their next spell) | needs-test |
| **0x2E / 46** | **Sunder** | full PA*WP strike **+ destroys a random piece of enemy gear** (innate to the formula, no proc needed). Whiffs on unequipped/monsters. | needs-test |
| **0x3C / 60** | **Medic** | the Attack HEALS the struck unit 40% of their MaxHP, costs wielder 20% — aim at an ally = MP-free heal-on-touch | risky (ally-targeting unconfirmed) |
| **0x52 / 82** | **Kamikaze** | wielder-missing-HP dmg (berserker) **+ 100% status** + self-recoil. The only missing-HP formula that bundles a guaranteed status. | risky (self-recoil leg) |
| 0x47/71, 0x17/23, 0x27/39 | (ruled out / niche) | Blood Suck lifesteal is Y-gated → dead (use 0x2D); Gravi2 near-kill is MA-gated hit; gil-steal is novelty | risky/skip |

**Ship the matched pair:** 0x43 Berserker ("reward dying") + 0x45 Executioner ("reward focus-fire") — two legible archetypes from one param-free mechanic; 0x43 already confirmed.

## New passive identities (EquipBonus riders — never put on our weapons)

| rider | archetype | double-edge / cost |
|-------|-----------|--------------------|
| **Always: Reflect** | spellbreaker (enemy single-target magick bounces) | also reflects your own single-target cures |
| **Always: Regen** | sustain bruiser; pairs w/ 0x43 berserker (stay alive low) | lower WP tax |
| **Always: Reraise** | undying / self-insurance blade | — |
| **Initial: Haste** | fast-starter opener | — |
| **Initial: Invisible** | first-strike assassin (guaranteed turn-1 hit) | gone after first action |
| **Always: Faith** | zealot caster-weapon (max magick; pairs w/ formula-4 gun) | wielder eats more enemy magick |
| **Always: Innocent** | magick-immune bruiser (can't be nuked/magic-statused) | own magick is dead too |
| **ImmuneStatus: X** on a weapon | WARD blade (anti-Charm/Sleep/Stop = partial Ribbon in a weapon slot) | opportunity cost vs offense |
| **Move/Jump +1** on a weapon | skirmisher (mobility as a weapon axis; pairs w/ speed-weapon 0x63) | trades WP for board control |
| **BoostJP** | trainer weapon (low WP, double JP — a build-economy pick) | weak in combat |
| **element X + StrongElements:X** | elemental specialist (Fire blade that +25%s its own Fire) | low risk, both halves vanilla-proven |
| **WeakElements as a power-tax** | cursed top-tier (strongest blade makes you 2x-weak to an element) | gives the best gear a real downside |
| Multi-element (2 bits) | coverage blade | RISKY — must verify engine resolves attacker-favorable, not defender |
| add/remove **2Swords** flag | dual-wield a non-knife class (status-stacking Ninja) | no shield |

## New proc effects (ItemOptionsData — the on-hit pool)

- **Row 55 = Dispel-all-buffs** (strip Haste/Protect/Shell/Regen/Reraise/Reflect/Float/Invisible). Barely used → make it a signature **anti-boss Dispel Blade**. Pair with formula 45 = strip every buff *every* hit.
- **CUSTOM rows in free slots (0, 29–31, 122–127)** — author bespoke procs (it's plain XML we already emit). Unlocks a whole sub-family: **partial dispel** Cancel{Haste,Reraise}, **corrosion** Separate{Poison,Oil,Blind}, **lockdown-lottery** Random{Sleep,Charm,Stop}, **armor-crack** Cancel{Protect,Shell}. ⚠ feasibility unconfirmed — does the modloader honor a hand-authored row? **Test this early, it unlocks the most.**
- **Row 95 = Random{Stop,Stone,KO}** executioner-lottery; **105/106 = Separate plague** (8–9 ailments each roll) cursed bomb; singles **96 Crystal** (no-revive, no-loot), **63 Traitor** (mind-control), **85 Vampire**.
- **formula 45 + any of the above = the guaranteed (100%) version** — 19%-vs-100% is now a design axis across the *whole* proc set.

## Global balance dials

- **StatusEffectData `Counter` = duration** (global per status, in CT ticks). Trim brutal ones (Charm=60→40) or lengthen Poison for DoT builds. Whole-status, not per-weapon.
- **Zodiac multiplies formula-1 proc rate** (×0.5–1.5) but **NOT formula 45**. Selection rule: must-land control → formula 45 (Zodiac-proof); flavor/variance proc → formula 1 (Zodiac-swingy). Effective f1 rate = 19% × Zodiac, not flat 19%.

Source: Sweep of FormulaTable.md / GameDataStructs.md / ItemWeaponData.xml / ItemOptionsData.xml / ItemEquipBonusData.xml.
