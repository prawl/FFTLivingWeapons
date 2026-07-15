# FFT Living Weapons

**Your weapon is a character. It grows as it kills -- and the gear around it is rebalanced so it never becomes vendor trash.**

A Reloaded-II mod for Final Fantasy Tactics: The Ivalice Chronicles, with two layers that build and ship together over Nenkai's `fftivc.utility.modloader`:

- **Living Weapons (DLL).** Every weapon counts its own kills and lifts the wielder's stats as the tally climbs; the iconic weapons awaken unique signature abilities -- Charm-on-hit, Barrage, extra turns, doom, life-steal, healing auras, and more. The running count is painted right onto the equip card.
- **The item rebalance (data).** All 234 equippable items reworked into sidegrades instead of a tier ladder, with a programmatic gate that proves no item is ever strictly dominated.

See [`docs/DESIGN.md`](docs/DESIGN.md) for the full design rationale.

## Thesis

A weapon you pour dozens of kills into should matter -- and it can't, if the next shop tier turns it into vendor trash. So the two layers are one idea: **the living weapon is what grows; the rebalance is the soil that keeps it worth growing.**

Vanilla loot is a strict tier ladder. Each shop tier beats the last, so old gear is dead weight the moment you can afford the next thing. This mod flattens the power curve and moves the interesting differences out of Weapon Power and into everything else. Every item has to answer one question: "when would I pick this?" The answer is always a niche, a tradeoff, or an availability window -- never "it's just bigger." That is what lets a weapon you've invested kills into stay a real choice all game.

This pays off harder the more the rest of your install ramps up. Under enemy-buffing mods (Level Scaling, Strong Monsters) or free-ability mods (All-Skills-Cost-0), strict-tier vanilla gear collapses into "just equip the newest thing". Sidegrades -- and a weapon that has grown with you -- stay relevant because the reason to run them was never raw power. Enemies equip from these same tables too, so it applies to both sides.

## Features

- **Living weapons that grow as they kill.** Each weapon tracks its own kills, lifts the wielder's PA/MA/Speed as the tally climbs, and shows the count on the equip card. Iconic weapons gain signature on-hit and aura effects (Charm, Barrage, extra turns, doom, life-steal, healing auras, and more) -- so the weapon you commit to becomes uniquely yours.
- **Treasure Master.** Equip the Scholar's Ring to reveal hidden treasure tiles on the battlefield.
- **All 234 equippable items reworked** across every category:
  - **Weapons: 121** (Sword 14, Knife 10, Katana 10, Bow 9, Ninja Blade 8, Rod 8, Staff 8, Polearm 8, Pole 8, Gun 6, Crossbow 6, Knight Sword 5, Flail 4, Book 4, Bag 4, Axe 3, Instrument 3, Cloth 3)
  - **Shields: 16**
  - **Head: 28** (Helmet 13, Hat 12, Hair Adornment 3)
  - **Body: 36** (Clothing 14, Armor 14, Robe 8)
  - **Accessories: 33** (Shoes 7, Cloak 7, Ring 6, Armlet 5, Perfume 4, Armguard 4)
- **Recolored menu icons and rewritten descriptions for the full roster**, plus fresh names for all but the iconic legendaries and samurai katanas. Every item ships an `ei_<id>` / `ei_s_<id>` icon pair (468 `.tex` files total).
- **A programmatic no-strict-domination gate.** `tools/analyze.py` fails the build if *any* item is strictly dominated by another. It also models **equip-slot access**, so an item isn't called "dominated" by something its slot can't actually use.
- **Range as a real lever.** Reach is a first-class tradeoff via the **Lunging** flag, not just a number, so short-range and extended-range weapons stay distinct choices instead of one obsoleting the other.
- **Vanilla spell and status casts preserved.** On-equip and on-use spell/status effects keep their original behavior. The rebalance touches stats and identity, not the game's existing magic.

## Requirements

- **[Reloaded-II](https://reloaded-project.github.io/Reloaded-II/)**
- **Nenkai's `fftivc.utility.modloader`**: auto-fetched as a dependency, or grab it from [Nenkai/fftivc.utility.modloader](https://github.com/Nenkai/fftivc.utility.modloader).

## Install

1. Install Reloaded-II and the FFT:IVC modloader (above).
2. Download the latest `FFTLivingWeapons-*.zip` from [Releases](../../releases).
3. In Reloaded-II, drag the zip onto the mod list (or extract it into your `Reloaded/Mods` folder).
4. Enable **FFT Living Weapons** in the mod list. The modloader dependency will be fetched automatically if missing.
5. **Restart the game.** The item rebalance (tables, text, icons) applies on launch, not live; the Living Weapon runtime (kill tracking, growth, signatures, the equip-card counter) runs in-process while you play.

## Compatibility: disable conflicting item mods

**This is the authoritative item mod.** It rewrites the full item tables, and enemies equip from those same tables, so it must own them outright. **Disable any other item-rebalance / equipment mod**, including:

- **Regabonds Rebalance**
- **WotL Equipment Replacer** (and Treasure Hunt variants)

It composes cleanly with **non-item** mods: Level Scaling, Strong Monsters, Spell Overhaul, All-Skills-Cost-0, GenericJobs, and color mods are all fine. Run `python tools/scan_conflicts.py` to list any installed mods that edit the same item IDs.

## Language support

Everything the mod *does* works in every game language: the item rebalance (stats, elements, procs, shop availability), weapon kill-growth, and the signature abilities all apply at the data and runtime level, so non-English players get the full gameplay. The readouts are English-only:

- **Item text.** The rewritten names, descriptions, and flavor ship only for English. On any other game language, items keep their vanilla names and descriptions (so the prose may not reflect an item's reworked stats), while the rebalanced numbers still apply.
- **The equip-card Kills counter.** The painted "Kills: N" tally and the "+N" name suffix anchor to the English item text, so they display only when the game language is English. Kills are still counted and stats still grow in every language; only the readout is absent.
- **In-battle toasts.** The tier-up and milestone banners deliver by swapping an English battle prompt, so they too appear only in English. The growth they announce still happens in every language.

## How it works / build from source

The design lives in one source of truth and is compiled into the modloader package:

```
data/items.json             # the only hand-edited source: every item's stats, name, and identity
  └─ tools/generate.py       # → FFTIVC/tables/enhanced/*.xml (item.en.nxd is baked separately by tools/patch_names.py)
  └─ tools/analyze.py        # build-diversity GATE: exit 1 if ANY item is strictly dominated
  └─ tools/scan_conflicts.py # lists installed mods that touch the same item IDs
```

```bash
python tools/generate.py        # emit the modloader tables from items.json
python tools/analyze.py         # prove no item is strictly dominated (gates deploy/release)
.\BuildLinked.ps1               # DEV: generate + gate + build the DLL + deploy into your Reloaded Mods folder
.\Publish.ps1                   # PROD: generate + gate + build the DLL + package the release zip
```

The package contains 7 sparse modloader XMLs (`ItemData`, `ItemWeaponData`, `ItemArmorData`, `ItemShieldData`, `ItemAccessoryData`, `ItemEquipBonusData`, plus `JobData` for the widened generic-job equip lists and class-evade floor), the full-table `item.en.nxd` (names + descriptions), the cell-merged `ability.en.nxd` (the Barrage text and the retired Equip Axes note), and the 234 menu-icon `.tex` pairs.

> **Editing note:** formula and effect IDs in the XML are **decimal, not hex**. The "Special Effect" badge in an item's description is the separate `UiStatusEffectId` nxd field; status/element procs are described in the item's prose, not flagged by a badge.

## Credits

- **Nenkai**: [`fftivc.utility.modloader`](https://github.com/Nenkai/fftivc.utility.modloader), without which none of this loads.
- **FFHacktics** and **Game8**: item/formula data and reference.
