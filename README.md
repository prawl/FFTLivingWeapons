# FFT Living Weapons

Your weapon grows as it kills, and the gear around it is rebalanced so nothing turns into vendor
trash.

A Reloaded-II mod for Final Fantasy Tactics: The Ivalice Chronicles. This is mod version 2.4.0.

## What it does

In vanilla, the sword you carried through chapter 1 is junk by chapter 2. This mod makes it worth
keeping.

Every weapon counts its own kills, and the count shows on its card in the menu and on the Attack
command in battle. At 5, 10 and 15 kills the name picks up a +, +2 and +3 and the weapon gets
stronger. Which stat grows depends on the weapon's lane, and the card says which lane it's on.
Most weapons grow PA, MA, HP or Speed. Poles and katanas grow PA and MA together and katanas add
Brave on top. Guns grow weapon power, and the magic guns add Faith.

At +3 some weapons wake up a signature ability. Bloodthirst gives you another turn on a kill,
Puppeteer lets you run an enemy you hit for its next turn. Feign Death cheats death once a
battle. Provoke drags a foe onto you and off the rest of your party. There are about thirty of
these, and the card tells you which one you're working toward.

That only works if old gear stays worth using, so 227 of the game's 234 equippable items are
reworked into sidegrades. Nothing is a straight upgrade over the item before it. Weapon Power
only creeps up from item to item, and what actually changes is evade, elements, procs, range and
when you can buy them. A script checks every item against every other and refuses to build if
one is worse on every count, so every item is worth picking somewhere. Enemies draw from the
same tables, so they get the new gear too.

There are also seven brand new weapons past the game's 261 item limit (Terrastaff, Ravager,
Sunderer, Warbrand, Bloodlash, Climhazzard, Sasori), sold in shops as the story unlocks them.
The seven vanilla axes and flails are back as themselves and don't grow.

If you want the reasoning behind all this, it's in [`docs/DESIGN.md`](docs/DESIGN.md).

## Requirements

- The current Steam build. The September 2026 update removed Denuvo, and mod 2.3.4 is the first
  version that works on it. Older versions load but never switch on, so if your kill counts
  froze, update.
- [Reloaded-II](https://reloaded-project.github.io/Reloaded-II/).
- Nenkai's `fftivc.utility.modloader` and `reloaded.sharedlib.hooks`. Reloaded fetches both when
  you enable the mod. If the mod doesn't start, install them from Reloaded's download list.
- Works on SteamOS through Proton. Set up Reloaded the usual Proton way first.

## Install

1. Download the latest `FFTLivingWeapons-*.zip` from [Releases](../../releases).
2. Drag the zip onto Reloaded's mod list, or extract it into `Reloaded/Mods`.
3. Enable FFT Living Weapons.
4. Restart the game. The item changes apply on launch, the kill tracking runs while you play.

Coming from 2.3.x? Your axes and flails go back to vanilla, no Kills line. The seven custom
weapons that used to replace them are their own items now, sold in shops. The kills you racked up
on them carry over on the first launch, and the log says so.

## Compatibility

Run this as your only item mod. Disable any other item rebalance or equipment mod, Regabond's
Rebalance and the War of the Lions Equipment Replacer included. Load order won't help, two item
mods just clash. Job mods and text mods are fine. Worst case a job mod knocks out the three
commands that weapons grant. [`docs/COMPATIBILITY.md`](docs/COMPATIBILITY.md) has verdicts for
the 143 mods checked so far, and `python tools/scan_conflicts.py` lists installed mods that edit
the same item ids.

The new text is English only. In any other language you get the vanilla names and descriptions
for items and for the granted commands, and the Kills counter and battle toasts don't show. Kills
still count and stats still grow.

## Good to know

- Kills are counted per weapon type, not per unit. Two units on the same weapon share one count
  and hit +3 about twice as fast. Enemies show the same +2 or +3 label but get none of the
  growth.
- Nothing is written to your save. Kill counts live in `kills.json` in the mod folder.
- The Kills counter paints a few seconds after a save loads. If the card says "Kills: -/- to +",
  back out of the menu and open it again.
- If the dashes never fill in, the DLL didn't load. Nothing is counting and your save is fine.
  Look for `DLL Not Found! LivingWeapon.dll` in Reloaded's newest log and reinstall from the zip.
- The mod checks the game before it writes anything, one part at a time. If a check fails, that
  part pauses itself and pops up a box with the mod version and game build on it. Screenshot the
  box for a bug report, and send `livingweapon.log` from the mod folder with it
  ([`docs/LOGGING.md`](docs/LOGGING.md) explains what's in it).
- Uninstalling drops the seven new weapons from your save, since the game can't know them
  without the mod. Unequip or sell them first. Reinstall and they come back with their kills,
  unless you saved while the mod was off.
- Quitting to the title or picking Retry during a Provoke hold can leave your party hidden from
  enemies for up to about 90 seconds. It clears on its own. Retry with the Sunderer's Bulwark
  planted keeps one tile blocked until that unit's next turn.
- Dual wielding counts the kill for both weapons. Shields don't count.

## Build from source

`data/items.json` is the only hand edited item source. `tools/generate.py` turns it into the
modloader tables, `tools/analyze.py` fails the build if any item is dominated, and
`tools/gen_living_weapon_meta.py` builds the per weapon data the DLL reads.

```
python tools/generate.py
python tools/analyze.py
dotnet test LivingWeapon.Tests\LivingWeapon.Tests.csproj
.\Check.ps1            # every gate with one exit code, deploys nothing
.\BuildLinked.ps1      # dev deploy into your Reloaded Mods folder, refuses on any red step
.\Publish.ps1          # the same gates, then the release zip
```

The code gates come from a shared rulebook vendored under `gates/`
([prawl/FFTModGates](https://github.com/prawl/FFTModGates)). New items past the 261 limit are one
row each in `items.json`. [`docs/EXTENDED_INVENTORY_GUIDE.md`](docs/EXTENDED_INVENTORY_GUIDE.md)
shows how. Game addresses are rebased at startup since the Denuvo free build loads at a random
address. [`docs/PATCH_REANCHOR.md`](docs/PATCH_REANCHOR.md) is the runbook for the next game patch.

Heads up if you edit the XML by hand, formula and effect ids in there are decimal, not hex.

## How it's made

Living Weapons is built with AI assistance and I want that clear up front.

The design is mine. I set every item's stats and picked which stat each weapon grows, and the
signature abilities are mine too. Same for the reverse engineering, done by hand with a
disassembler and live testing. JadeKnightblazer and I broke the 261 item limit together. The
evidence for each address is in [`docs/LIVE_LEDGER.md`](docs/LIVE_LEDGER.md).

Claude Code wrote much of the C#, with me driving. I don't take its word for anything. Every
change has to pass 4,500+ tests plus the balance gate and build checks above. Bigger changes also
get a cold read, where a reader with no access to the project has to explain the code from just
the code ([`docs/COLD_READ_LOG.md`](docs/COLD_READ_LOG.md)). And I play every release before it
goes out.

Questions? Find me in the FFT modding Discord, I'm Pax (prawl) there.

## Credits

- Nenkai for [`fftivc.utility.modloader`](https://github.com/Nenkai/fftivc.utility.modloader).
  Nothing here loads without it.
- JadeKnightblazer, my partner in breaking the 261 item limit.
- FFHacktics and Game8 for item and formula data.
