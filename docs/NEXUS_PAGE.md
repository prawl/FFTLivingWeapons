# FFT Item Overhaul

Vanilla FFT gear is a shopping treadmill. Buy the new sword, sell the old one, never look back. I got tired of that, so I sat down and reworked every single piece of equipment in the game so your old stuff doesn't turn into vendor trash the second the next tier shows up.

All 234 equippable items got touched. Weapons, shields, helmets, armor, accessories. New stats, recolored icons, rewritten descriptions, and fresh names for most of them.

It's pure data. No DLL, no config window, nothing to fiddle with. Drop it in, restart, play.

### So what actually changes?

Weapon Power and HP still climb over the chapters, just slower and steadier than vanilla. The interesting part of a weapon lives somewhere other than its damage number now: how much it dodges, what element it carries, a status it lands on hit, a stat bonus, how far it reaches, or just how early you can get your hands on it. Two weapons with the same WP can play completely differently.

### Why bother keeping the old gear around?

Because nothing in here is a flat upgrade over something else. That chapter-1 dagger might dodge better, or hit at range, or inflict a status the shiny new sword can't touch. Every item had to pass one question before it shipped: "okay, when would I actually grab this one?" If I couldn't answer that, I kept tweaking until I could.

### How do I know what an item does?

I rewrote all the descriptions to actually tell you. Element, on-hit status, equip bonus, reach, it's right there in the tooltip. No more digging through a wiki to figure out what a weapon's gimmick is.

### Did you rename everything?

Most of it. Shop gear got fresh names and recolored icons across the board. The legendaries keep theirs though. I wasn't about to rename Excalibur or the Genji set, and the samurai katanas stay as they are too. So if you're hunting for a specific named treasure, it's still called what you remember.

### Is the game harder or easier now?

A bit of both, honestly. Enemies pull from the same item tables you do, so they get all of these changes right along with you. The more your other mods crank up enemy power, the more this one earns its keep, since picking gear becomes an actual decision instead of "grab whatever has the biggest number."

### Do I need to set anything up?

Nope. No menu, no sliders, no hotkeys. It's just data, so it kicks in the next time you start the game.

## Installation

You'll need Reloaded-II and the FFT:TIC Mod Loader. If you install through Vortex it should pull the dependencies in for you. New to modding FFT? There's a setup video below.

(Vortex setup video)

(Reloaded-II setup video)

## Load Order

Never had to touch mine. The only real rule is don't run another item mod at the same time as this one (more on that in the FAQ). Anything that isn't editing items can sit wherever it likes.

## FAQ

**Will it fight with my other mods?**
Only other item mods. This is meant to be the one item mod you run. If you've got something like Regabond's Rebalance or the WotL Equipment Replacer / Treasure Hunt, switch those off, or they'll wrestle over the same tables and you'll end up with items neither mod actually designed. Everything else (level scaling, job mods, skill and spell mods) is totally fine alongside it.

**Does it change enemy equipment too?**
Yep, on purpose. Enemies wear gear out of the same tables you buy from, so they get the reworked stats too.

**What about potions and throwables?**
Left alone. This is equipment only. Potions, ethers, shuriken, bombs, all still vanilla.

**Does it add new items?**
No. The item table is a fixed size, so instead of bolting on new slots I reworked the 234 that are already there. Same count, all different.

**Can I add or remove it mid-playthrough?**
Go for it. It only changes item data and never writes anything to your save, so you can drop it in whenever, and if you pull it back out everything goes right back to vanilla.

**Does it run on Steam Deck?**
Should be fine. There's no interface to draw, it's just data, so it works anywhere Reloaded-II and the mod loader do.
