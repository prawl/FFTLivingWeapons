# FFT Item Overhaul

**Every item in Final Fantasy Tactics: The Ivalice Chronicles, rebalanced so old gear never becomes vendor trash.**

A pure-data rebalance of all **234** equippable items (every weapon, shield, head/body armor, and accessory), aimed at one thing: build diversity. Weapon Power and HP climb gently across the chapters, and each item earns its own identity in the stats that actually shape a build: evasion, element, on-hit status, stat and equip riders, range, and how early you can get it. A newer weapon is rarely a flat upgrade. It's a sidegrade with a different reason to run it.

Pure-data Reloaded-II mod (no DLL) over Nenkai's `fftivc.utility.modloader`.

See [`docs/DESIGN.md`](docs/DESIGN.md) for the full design rationale.

## Thesis

Vanilla loot is a strict tier ladder. Each shop tier beats the last, so old gear is dead weight the moment you can afford the next thing. This mod flattens the power curve and moves the interesting differences out of Weapon Power and into everything else. Every item has to answer one question: "when would I pick this?" The answer is always a niche, a tradeoff, or an availability window.

This pays off harder the more the rest of your install ramps up. Under enemy-buffing mods (Level Scaling, Strong Monsters) or free-ability mods (All-Skills-Cost-0), strict-tier vanilla gear collapses into "just equip the newest thing". Sidegrades stay relevant because the reason to run them was never raw power. Enemies equip from these same tables too, so it applies to both sides.

## Features

- **All 234 equippable items reworked** across every category:
  - **Weapons — 121** (Sword 14, Knife 10, Katana 10, Bow 9, Ninja Blade 8, Rod 8, Staff 8, Polearm 8, Pole 8, Gun 6, Crossbow 6, Knight Sword 5, Flail 4, Book 4, Bag 4, Axe 3, Instrument 3, Cloth 3)
  - **Shields — 16**
  - **Head — 28** (Helmet 13, Hat 12, Hair Adornment 3)
  - **Body — 36** (Clothing 14, Armor 14, Robe 8)
  - **Accessories — 33** (Shoes 7, Cloak 7, Ring 6, Armlet 5, Perfume 4, Armguard 4)
- **Recolored menu icons and rewritten descriptions for the full roster**, plus fresh names for all but the iconic legendaries and samurai katanas — every item ships an `ei_<id>` / `ei_s_<id>` icon pair (468 `.tex` files total).
- **A programmatic no-strict-domination gate.** `tools/analyze.py` fails the build if *any* item is strictly dominated by another — and it even models **equip-slot access**, so an item isn't called "dominated" by something its slot can't actually use.
- **Range as a real lever.** Reach is a first-class tradeoff via the **Lunging** flag, not just a number, so short-range and extended-range weapons stay distinct choices instead of one obsoleting the other.
- **Vanilla spell and status casts preserved.** On-equip and on-use spell/status effects keep their original behavior — the rebalance touches stats and identity, not the game's existing magic.

## Requirements

- **[Reloaded-II](https://reloaded-project.github.io/Reloaded-II/)**
- **Nenkai's `fftivc.utility.modloader`** — auto-fetched as a dependency, or grab it from [Nenkai/fftivc.utility.modloader](https://github.com/Nenkai/fftivc.utility.modloader).

## Install

1. Install Reloaded-II and the FFT:IVC modloader (above).
2. Download the latest `FFTItemOverhaul-*.zip` from [Releases](../../releases).
3. In Reloaded-II, drag the zip onto the mod list (or extract it into your `Reloaded/Mods` folder).
4. Enable **FFT Item Overhaul** in the mod list. The modloader dependency will be fetched automatically if missing.
5. **Restart the game.** This is a data-only mod — changes take effect on launch, not live.

## ⚠️ Compatibility — disable conflicting item mods

**This is the authoritative item mod.** It rewrites the full item tables, and enemies equip from those same tables, so it must own them outright. **Disable any other item-rebalance / equipment mod**, including:

- **Regabonds Rebalance**
- **WotL Equipment Replacer** (and Treasure Hunt variants)

It composes cleanly with **non-item** mods — Level Scaling, Strong Monsters, Spell Overhaul, All-Skills-Cost-0, GenericJobs, and color mods are all fine. Run `python tools/scan_conflicts.py` to list any installed mods that edit the same item IDs.

## How it works / build from source

The design lives in one source of truth and is compiled into the modloader package:

```
data/items.json             # SOURCE OF TRUTH — every item's stats, name, and identity
  └─ tools/generate.py       # → FFTIVC/tables/enhanced/*.xml + item.en.nxd (names + descriptions)
  └─ tools/analyze.py        # build-diversity GATE: exit 1 if ANY item is strictly dominated
  └─ tools/scan_conflicts.py # lists installed mods that touch the same item IDs
```

```bash
python tools/generate.py        # emit the modloader tables from items.json
python tools/analyze.py         # prove no item is strictly dominated (gates deploy/release)
.\deploy.ps1                    # generate + gate + copy into your Reloaded Mods folder
.\Publish.ps1                   # generate + gate + build the release zip
```

The package contains 6 sparse modloader XMLs (`ItemData`, `ItemWeaponData`, `ItemArmorData`, `ItemShieldData`, `ItemAccessoryData`, `ItemEquipBonusData`), the full-table `item.en.nxd` (names + descriptions), and the 234 menu-icon `.tex` pairs.

> **Editing note:** formula and effect IDs in the XML are **decimal, not hex**. The "Special Effect" badge in an item's description is the separate `UiStatusEffectId` nxd field; status/element procs are described in the item's prose, not flagged by a badge.

## Credits

- **Nenkai** — [`fftivc.utility.modloader`](https://github.com/Nenkai/fftivc.utility.modloader), without which none of this loads.
- **FFHacktics** and **Game8** — item/formula data and reference.
