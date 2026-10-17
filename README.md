# FFT Living Weapons

**Your weapon is a character. It grows as it kills, and the gear around it is rebalanced so it never becomes vendor trash.**

> **Which version is this?** This README describes the development version on GitHub. The current release is **2.3.4**. It has the item rebalance and the living weapons but not the new extended inventory items, and it still carries the dormant Treasure Master module, which does nothing on the current game build. Where the two differ, both numbers are given below.

A Reloaded-II mod for Final Fantasy Tactics: The Ivalice Chronicles, with two layers that build and ship together over Nenkai's `fftivc.utility.modloader`:

- **Living Weapons (DLL).** Every weapon counts its own kills and lifts the wielder's stats as the tally climbs; the iconic weapons awaken unique signature abilities. The running count is painted right onto the equip card.
- **The item rebalance (data).** All 234 equippable items reworked into sidegrades instead of a tier ladder (227 on the development branch, see Features), with a programmatic gate that proves no item is ever strictly dominated.

See [`docs/DESIGN.md`](docs/DESIGN.md) for the full design rationale.

## Thesis

A weapon you pour fifteen kills into should matter, and it can't, if the next shop tier turns it into vendor trash. So the two layers are one idea: **the living weapon is what grows; the rebalance is the soil that keeps it worth growing.**

Vanilla loot is a strict tier ladder. Each shop tier beats the last, so old gear is dead weight the moment you can afford the next thing. This mod flattens the power curve and moves the interesting differences out of Weapon Power and into everything else. Every item has to answer one question: "when would I pick this?" The answer is always a niche, a tradeoff, or an availability window, never "it's just bigger." That is what lets a weapon you've invested kills into stay a real choice all game.

This pays off harder the more the rest of your install ramps up. Under enemy-buffing mods or free-ability mods, strict-tier vanilla gear collapses into "just equip the newest thing". Sidegrades, and a weapon that has grown with you, stay relevant because the reason to run them was never raw power. Enemies equip from these same tables too, so it applies to both sides.

## Features

- **Living weapons that grow as they kill.** Each weapon tracks its own kills, lifts the wielder's PA, MA or Speed at 5, 10 and 15 kills, and shows the count on the equip card. Iconic weapons wake up signature abilities, Charm, Barrage, extra turns, doom, life steal, healing auras and more, so the weapon you commit to becomes yours. In 2.3.4 every weapon grows, including the seven designs that sit in the axe and flail slots (Terrastaff, Ravager, Sunderer, Warbrand, Bloodlash, Climhazzard, Sasori). On the development branch those seven moved to ids 261 to 267 and the vanilla axes and flails came back at their own numbers, with no growth.
- **An extended inventory: brand-new weapons past the game's 261-item limit** (development branch only, not in the 2.3.4 release). The game only knows
  261 items; the mod adds more (seven today, ids 261 to 267), listed in every menu, sold in the
  towns its data names, equipped and swung in battle, growing and glowing like the rest, and remembered
  per save. Everything about a new item is one row in `data/items.json`
  ([`docs/EXTENDED_INVENTORY_GUIDE.md`](docs/EXTENDED_INVENTORY_GUIDE.md) is the authoring guide).
- **All 234 equippable items reworked** across every category (227 on the development branch, except those seven):
  - **Weapons: 121 vanilla-range**; the development branch adds 7 extended ids on top
  - **Shields: 16**
  - **Head: 28** (Helmet 13, Hat 12, Hair Adornment 3)
  - **Body: 36** (Clothing 14, Armor 14, Robe 8)
  - **Accessories: 33** (Shoes 7, Cloak 7, Ring 6, Armlet 5, Perfume 4, Armguard 4)
- **Rewritten descriptions for the full roster, and recolored menu icons** for all of it in 2.3.4 (on the development branch the seven restored axes and flails keep the game's own art). 64 items keep their vanilla names (57 in 2.3.4), the samurai katanas and the iconic legendaries among them; the rest are renamed. Every equippable item ships an `ei_<id>` / `ei_s_<id>` icon pair: 234 pairs (468 `.tex` files) in the 2.3.4 release, 241 pairs (482 files) on the development branch with the seven new weapons.
- **A programmatic no-strict-domination gate.** `tools/analyze.py` fails the build if *any* item is strictly dominated by another. It also models **equip-slot access**, so an item isn't called "dominated" by something its slot can't actually use.
- **Range as a real lever.** Reach is a first-class tradeoff via the **Lunging** flag, not just a number, so short-range and extended-range weapons stay distinct choices instead of one obsoleting the other.
- **Vanilla spell and status casts preserved.** On-equip and on-use spell/status effects keep their original behavior. The rebalance touches stats and identity, not the game's existing magic.

## Requirements

- **The current Steam build of the game.** The September 2026 game update removed Denuvo, and the unprotected game now loads at a random address every launch. **Mod 2.3.4 is the first version that works on it.** Versions 2.3.3 and older still load on the current game but never switch on: kill counts stop moving, with no error and no harm to your save. If your kill counts froze after the September update, update the mod.
- **[Reloaded-II](https://reloaded-project.github.io/Reloaded-II/)**
- **Nenkai's `fftivc.utility.modloader`**: fetched automatically as a dependency, or grab it from [Nenkai/fftivc.utility.modloader](https://github.com/Nenkai/fftivc.utility.modloader).
- **`reloaded.sharedlib.hooks`** (Reloaded.SharedLib.Hooks): a Reloaded-II library mod. Reloaded-II normally fetches it from its own library feed when you enable Living Weapons; if the mod does not start, install it from Reloaded's download list.
- **SteamOS / Linux:** works through Proton; the author has run it there. Set up Reloaded-II the usual Proton way first, then install the mod as below.

## Install

1. Install Reloaded-II and the FFT:IVC modloader (above).
2. Download the latest `FFTLivingWeapons-*.zip` from [Releases](../../releases).
3. In Reloaded-II, drag the zip onto the mod list (or extract it into your `Reloaded/Mods` folder).
4. Enable **FFT Living Weapons** in the mod list. Both dependencies are normally fetched automatically if missing.
5. **Restart the game.** The item rebalance (tables, text, icons) applies on launch, not live; the Living Weapon runtime (kill tracking, growth, signatures, the equip-card counter) runs in-process while you play.

## Compatibility: disable conflicting item mods

**This is the authoritative item mod.** It rewrites the full item tables, and enemies equip from those same tables, so it must own them outright. **Disable any other item-rebalance / equipment mod**, including Regabond's Rebalance and the War of the Lions Equipment Replacer; no load order fixes an item-table clash.

Non-item mods coexist with it in general: a job mod costs at most the three weapon-granted commands, a text mod at most some display text, and everything else runs alongside untouched (Generic Jobs is verified clean). [`docs/COMPATIBILITY.md`](docs/COMPATIBILITY.md) holds the compatibility notes, with verdicts for the mods checked so far (surveyed as of August 2026). Run `python tools/scan_conflicts.py` to list any installed mods that edit the same item IDs.

## Language support

The gameplay is language-neutral by construction: the item tables carry no text, and kill-growth and the signature abilities run at the memory level, so the item rebalance (stats, elements, procs, shop availability), the growth and the signatures do not depend on the game's language. Play in other languages is not separately tested; one Chinese-language player confirmed the English-switch workaround for the kill counter below. The readouts are English-only:

- **Item text.** The rewritten names, descriptions, and flavor ship only for English. On any other game language, items keep their vanilla names and descriptions (so the prose may not reflect an item's reworked stats), while the rebalanced numbers still apply.
- **The equip-card Kills counter.** The painted "Kills: N" tally and the "+N" name suffix anchor to the English item text, so they display only when the game language is English. Kills are still counted and stats still grow in every language; only the readout is absent.
- **In-battle toasts.** The tier-up and milestone banners deliver by swapping an English battle prompt, so they too appear only in English. The growth they announce still happens in every language.
- **Granted command names and the Provoked mark.** The Barrage and Provoke command names, the Equip Axes wording and the "Provoked" status label ship in the English text files only. In other languages the commands work but show their vanilla names, and the Provoked mark appears as an unlabeled icon.

## Good to know

- **Kill counts appear about ten seconds after loading a save.** The mod switches on the moment
  a save finishes loading; the card counter paints about ten seconds later on 2.3.4, longer on
  the development branch (one session measured 34 seconds). If you open the party menu sooner,
  the cards briefly show zeroed "Kills" text; back out and re-open the menu and your numbers are
  there. Nothing is ever lost; only the first paint lags.
- **The mod checks the game before it writes anything, one part at a time.** Each of its fourteen
  parts (kill counting, stat growth, the weapon powers in battle, weapon commands, banners, the
  extended inventory and the rest) checks the spot in the game's memory it depends on, at startup,
  when a save loads, or when your first battle starts. A part whose check passes runs. A part whose
  check fails pauses for the session, along with any other part that leans on the same spot, and
  everything else keeps running. The log prints one "Parts report:" line naming every part as ON,
  OFF or WAITING with the reason, and the first time every part has passed it prints
  "Living Weapons: all parts armed." instead. When a check fails you also get a
  box, "Living Weapons: some features are paused", listing what still works, what is paused, and
  what to do (check the mod page for a newer version, try without a mod you installed recently, or
  email me with the log). If nothing is still working the box says the mod has paused itself.
  Every such message says the game update or another mod may be the cause, and every box ends
  with the mod version and the game build, so a screenshot of it is a usable bug report. A game
  build the mod has
  never seen is only reported; the mod runs normally if its checks pass. If it cannot read the game
  at all it says so after about a minute and keeps trying. Your save is never touched in any of
  these cases. A custom job mod that rewrites the job command data pauses only the three weapon
  granted commands, Barrage, Shadow Blade and Provoke.
- **For bug reports, send the log.** It is `livingweapon.log` in the mod's folder under `Reloaded/Mods`;
  each launch rotates the previous one to `livingweapon.prev.log`, so grab it right after the session
  where things went wrong. [`docs/LOGGING.md`](docs/LOGGING.md) explains what is in it.
- **Removing the mod loses the extended-inventory weapons cleanly** (development branch only). The game cannot know an item past
  its own 261 without the mod running: a save that holds one still loads fine, the unit's hand is
  simply emptied and the item is gone from the bag. Unequip or sell them first if you plan to
  uninstall; if an inventory list then looks short, sort it once and it repairs itself. Reinstall and
  they come back with their saved counts.
- **Dual-wielding doubles the growth.** A kill while wielding two weapons credits both of them,
  not just one, so pairing two Living Weapons together is the fastest way to grow a pair at once.
  A shield equipped in either hand never earns credit; only a real weapon does.

## How it works / build from source

The design lives in one source of truth and is compiled into the modloader package:

```
data/items.json             # the only hand-edited source: every item's stats, name, and identity
  └─ tools/generate.py       # → FFTIVC/tables/enhanced/*.xml + extended_inventory/*.xml + out/names.json
  │                          #   (item.en.nxd is baked separately by tools/patch_names.py from out/names.json)
  └─ tools/analyze.py        # build-diversity GATE: exit 1 if ANY item is strictly dominated
  └─ tools/gen_living_weapon_meta.py  # → LivingWeapon/meta.json, the runtime's per-weapon facts
  └─ tools/scan_conflicts.py # lists installed mods that touch the same item IDs
```

```bash
python tools/generate.py        # emit the modloader tables from items.json
python tools/analyze.py         # prove no item is strictly dominated (gates deploy/release)
dotnet test LivingWeapon.Tests\LivingWeapon.Tests.csproj   # the runtime's xUnit suite
.\BuildLinked.ps1               # DEV: generate + gate + meta + self-tests + format check + xUnit + build the DLL + deploy into your Reloaded Mods folder (refuses on any red step)
.\Publish.ps1                   # PROD: the same steps, then package the release zip instead of deploying (never touches the game)
```

Both scripts refuse to deploy or package on any red step. Beyond the item gate and the unit tests, two
style gates run every time. `dotnet format` must have nothing to change, and the comment budget test
keeps comments short, one comment line per three code lines for a new file, no block over eight lines,
no colon or dash inside a comment sentence, and no comment that argues instead of informs.

What the package carries.

- The Reloaded manifest, `ModConfig.json`.
- The runtime and its data. `LivingWeapon.dll`, `LivingWeapon.deps.json`, `Newtonsoft.Json.dll`, `meta.json` with every growing weapon's facts, `poach.json` for Living Poach, and in 2.3.4 only `treasure.json` for Treasure Master.
- Seven sparse modloader XMLs. `ItemData`, `ItemWeaponData`, `ItemArmorData`, `ItemShieldData`, `ItemAccessoryData`, `ItemEquipBonusData`, plus `JobData` for the widened generic job equip lists and the class evade floor.
- Three nxd text files. The full table `item.en.nxd` for names and descriptions, the cell merged `ability.en.nxd` for the Barrage, Provoke and Equip Axes text, and `uistatuseffect.en.nxd` for the Provoked mark's name.
- The menu icon `.tex` pairs, 234 in 2.3.4 and 241 on the development branch.
- Development branch only. The extended inventory's three tables under `extended_inventory/` (read by the DLL at boot, not by the modloader), 14 `.utexpt` icon parts files for the new ids, and 726 glow rim icon variants with their `manifest.json` under `glow_icons/`.

The game's own code no longer sits at one fixed address (ASLR since the Denuvo-free build), so every game address the DLL knows is written as its documented number and rebased against the live image at startup; [`docs/PATCH_REANCHOR.md`](docs/PATCH_REANCHOR.md) is the runbook for the next game patch.

> **Editing note:** formula and effect IDs in the XML are **decimal, not hex**. The "Special Effect" badge in an item's description is the separate `UiStatusEffectId` nxd field; status/element procs are described in the item's prose, not flagged by a badge.

## Credits

- **Nenkai**: [`fftivc.utility.modloader`](https://github.com/Nenkai/fftivc.utility.modloader), without which none of this loads.
- **FFHacktics** and **Game8**: item/formula data and reference.
