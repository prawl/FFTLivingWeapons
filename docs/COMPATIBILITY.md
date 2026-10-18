# Mod Compatibility

STATUS: CONTRACT (the player-facing compatibility grid the Nexus page links to. Update it
whenever a mod is checked or a conflict class changes)

This page says which mods run next to FFT Living Weapons and which do not. It comes from a
hands-on check of every mod on the Ivalice Chronicles Nexus (all 97 on 2026-08-12, all 143
on 2026-10-17) and gets updated as new mods appear. If a mod misbehaves next to Living
Weapons, open a GitHub issue on this repository.

## Quick answer

- Thirteen mods will not work with Living Weapons. They all ship their own item table rows
  and the mod loader only keeps one side's rows, so no load order fixes it. Five of the
  thirteen are small (a fix to one gun, two cheat items) and only the rows they ship are
  affected, but on those rows it is still one mod or the other.
- The ones with a catch have one of two. A custom job mod can cost you the three
  weapon-granted commands, and the mod tells you in game when that happens. A non-English
  game language or an English text mod hides the kill counter on weapon cards.
- Everything else works fine. About two thirds of the catalog had no catch at all.
- Modding tools are fine. The game never loads a tool, only what people make with it.
- Since version 2.4.0 the mod checks each of its parts on its own and prints one "Parts report:"
  line in the log. It lists each part as ON, OFF or WAITING with a reason. When another mod gets
  in the way you see it there as one part OFF with the reason next to it, for example
  "weapon commands OFF (another mod rewrote the job command data)", and everything else keeps
  running; once every part has passed the line reads "Living Weapons: all parts armed." You also
  get a box, "Living Weapons: some features are paused", that lists what still works and what is
  paused, says the game update or another mod may be the cause, and ends with what to do and the
  mod version plus game build. When this page says the mod will tell you, that line and that box
  are what it means.

## Will not work together

Both mods rewrite whole rows of the game's item tables. Whichever loads last wins each row,
so gear from the losing mod quietly reverts. Load order does not help. Pick one.

| Mod | Nexus # | What overlaps |
|---|---|---|
| [CustomJOB_ITEM-1](https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/64) | 64 | Full item rebalance, every gear row |
| [FFT Regabond's Rebalance](https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/72) | 72 | Full item rebalance, every gear row |
| [Antidote - FFT Rebalanced](https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/75) | 75 | Full item rebalance, every gear row |
| [The War of the Lions Equipment Replacer](https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/84) | 84 | Full item rebalance, every gear row |
| [FFTIVC - Difficulty Enhanced](https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/125) | 125 | Full rebalance. Ships every item table we do, plus item text and ability tables |
| [All Items in Shops](https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/107) | 107 | Item table rows across the catalog |
| [Lucifeh All Items in Shops](https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/137) | 137 | Same shape as 107, an item table and a shop table across the catalog |
| [Materia Blade Plus Replacement](https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/115) | 115 | The item rows it ships |
| [Lucifeh Cheating Gear](https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/136) | 136 | Two cheat items added through item, armor, equip bonus and shop rows plus item text. The rows it reuses take one mod's values |
| [Dual Wield Guns - A Gun in Each Hand](https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/148) | 148 | Only the gun rows, where it sets the two-handing flag. Those guns get its flag or our rebalance |
| [Stoneshooter Fix](https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/149) | 149 | The Stoneshooter's weapon row and one equip bonus row, plus a small code hook. One gun, one mod's values |
| [Taming Gun](https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/150) | 150 | The Blaze Gun's weapon row plus a job row. Same deal, one gun |
| [Cloud Re-Work](https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/46) | 46 | ItemData rows for the gear it edits. Those items take one mod's values; its ability and encounter files are fine. Pairing breaks either Cloud's kit or our rebalance on those items |

## Works, with a catch

These work, but each one costs you something.

| If the other mod... | What happens | What you keep |
|---|---|---|
| Adds or rewrites jobs and touches the job-command records we use | Living Weapons notices at startup, stays ON, and switches off only the three weapon-granted commands (Barrage, Shadow Blade, Provoke). It tells you in game | Kill counting, growth, and every other weapon signature |
| Overwrites the text cells our custom names and descriptions live in | Those names and descriptions may show the other mod's text | Everything mechanical |
| ...or you play in a language other than English | The painted "Kills:" counter on weapon cards does not render. A non-English game loads different item text, so our anchor is not there. Switch to English and restart and it is back | Kills still count, you just cannot see the number until you switch back |
| Replaces the equipment menu icons | Whichever mod loads last supplies the pictures. Load Living Weapons last to keep ours | Everything mechanical either way |
| ...or you REMOVE Living Weapons while a save holds one of its brand-new weapons (the extended inventory, ids past the game's own 261: today the Terrastaff, Ravager, Sunderer, Warbrand, Bloodlash, Climhazzard and Sasori) | The game only knows those items while the mod is running. Without it the save still loads. The unit's hand ends up empty and the item is gone from the bag. No crash. Unequip or sell the new weapons before you uninstall. If a list then looks short (an entry seems to be missing after the spot where the new weapon sat), sort that list once and it repairs itself | Everything else in the save. The new weapon comes back the moment the mod is reinstalled, with its saved bag count if you did not save while the mod was off; a save written without the mod starts the new weapons over at zero |

### Specific mods checked into these classes

| Mod | Nexus # | Class | Verdict |
|---|---|---|---|
| [Ivalice Retranslation Project](https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/9) | 9 | Text mod | Rewrites the game's English text including item and ability tables, and its standalone version replaces the whole English text archive. Our item names and descriptions lose to its translation and the kill counter can lose its anchor on affected cards. Checked again 2026-08-18, nothing changed |
| [PS1 English Localization](https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/123) | 123 | Text mod | Restores the PS1 names for items, abilities and jobs through the same text tables we ship. Our item names and descriptions lose on every gear row and the kill counter can lose its anchor. Load Living Weapons last to keep ours |
| [Thai Localization](https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/122) | 122 | Text mod | Replaces the game's text packs outright, so our English item names and the kill counter are not there. Same as playing in a non-English language |
| [Dual Subtitles JP-EN with Kana Assist](https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/90) | 90 | Text mod | This one is milder. It switches item, job and ability names to Japanese and keeps descriptions in English (the author's stated design), so our custom names may lose but the kill counter's description anchor should survive. Nobody has run both together yet |
| [Mobile Equipment Icons](https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/116) | 116 | Icon mod | 260 equipment icon textures from the mobile release. Whichever mod loads last supplies the icons. Nothing mechanical changes |
| [Ramza Overhaul](https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/110) | 110 | Job table mod | Read its files row by row. Its command records (25 to 28, Ramza and Delita) and job text are clear of everything we ship or watch, and it has no item tables. One row overlaps ours, a Ramza chapter job where it grants Knight Sword and we raise evasion. Whichever loads last wins that row, so worst case is Ramza down 3 evasion or missing Knight Sword that chapter |
| [True Knight Ramza](https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/89) | 89 | Job table mod | Same shape as Ramza Overhaul. One job table editing Ramza's Squire rows, which we also ship (rows 3 and 4, where we raise evasion). Whichever loads last keeps its edit on those rows. Worst case is 3 evasion or its innate abilities on those two jobs |
| [Ultima and Zodiark Learnable with JP](https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/117) | 117 | Job table mod | An ability table we do not ship and a job table whose rows are Ramza's, the same rows 3 and 4 we edit. Same worst case as above |
| [New Jobs - Blue Mage - Red Mage](https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/26) | 26 | Job mod | Ships job-command and job tables plus its own code (updated 2026-10-15 with a job table and a monster command table). If its records reach the two we watch, the mod tells you in game and only the three weapon commands switch off |
| [Knight Overhaul](https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/113) | 113 | Job mod | This one slips past our check. It rewrites the Knight's command record, which is where Shadow Blade's command is injected, and we do not watch that record. Barrage and Provoke are watched. Shadow Blade can break with no notice when both are installed. Load Living Weapons last if you want Shadow Blade |
| [The Way of Blue](https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/120) | 120 | Job mod | A Blue Mage kit for Luso through job-command, job and ability tables (needs WotL Restoration for Luso himself, see below). Story job rows, clear of ours. Job catch above at worst |
| [Teleport Never Fails](https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/133) | 133 | Job mod | Ability and job-command tables for the Time Mage. Job catch above at worst |
| [Holy Knight Enhancement](https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/139) | 139 | Job mod | One job-command table for the Holy and White Knight story jobs. Job catch above at worst |
| [Onion Knight as Omega Knight](https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/140) | 140 | Job mod | Job-command, job and ability tables plus its own code for the Onion Knight. Job catch above at worst |
| [Warlord's Hero Ramza](https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/160) | 160 | Job mod | A job-command table for Ramza's own command (records 25 to 28, the story records Ramza Overhaul also edits). Clear of the two records we watch. Should be fine |
| [Ladd the Heaven Knight](https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/164) | 164 | Job mod | Job-command, job and ability tables plus encounter data and code for one story unit. Job catch above at worst |
| [Ramza - The Numinous Knight](https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/167) and [Lore-Accurate Party](https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/171) | 167, 171 | Job mod | Both ship job-command and job tables and two code files, one of them named as a weapon compatibility layer. Our job rows are the generic jobs and Ramza's, theirs are story jobs. Should be fine. Nobody has run their weapon code next to ours yet |
| [Lance Saint](https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/170) | 170 | Job mod | Replaces the Mime through job-command and ability tables and UI text (none of the cells we use). Job catch above at worst |
| [Boco the Titan](https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/172) | 172 | Job mod | Job-command and job tables, encounter data and code for one monster unit. Job catch above at worst |

### WotL Restoration

[WotL Restoration](https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/33)
(Nexus 33, version 2.0.7, read file by file on 2026-10-17) restores the PSP characters, events
and items. The characters and events work next to Living Weapons. The items are the catch.

Both mods teach the game that items past 261 exist, and both patch the same spots in the
game's code to do it. Only one of those patches can hold at a time. Living Weapons patches
first, while it loads. WotL Restoration patches later, after every mod has loaded, sees the
spots already changed, and backs off with a log line saying the sites differ. Its events then
hand out the vanilla item they were written to fall back to (Agrias's birthday gives a
Septième Sens instead of a Tynar Rouge). So with both installed you get Living Weapons' new
weapons and WotL Restoration's characters, events and battles, and you do not get its 40
WotL items (the Onion Knight gear, Balthier's guns, the fell swords and the rest).

Two things to set up. Load Living Weapons after WotL Restoration, because its item text names
ids 261 to 267 for its own items and ours live at those same ids. If it loads last, the
Terrastaff reads as "Moonblade" on the card and the kill counter loses its anchor on those
seven weapons. And expect the line "Item expansion NOT armed" in WotL Restoration's log. That
is normal with Living Weapons installed.

Nothing else overlaps. Its job edits are the WotL story jobs (rows 162 to 168), ours are the
generic jobs and Ramza. It has no job-command table. It writes the same Barrage name we do.
I have read the files but not run both mods together yet.

## Checked and fine

| Mod | Nexus # | Note |
|---|---|---|
| [Generic Jobs](https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/34) | 34 | The site's most-downloaded mod. Read its source and checked its one memory write against our job command region in game. Clean. Version 0.0.12 (2026-09-27) ships the same files |
| [Super and Ultrawide Resolutions](https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/11) | 11 | Resolution configuration tables only. Nothing shared with this mod |
| [Deep Brave Story (base + add-on files)](https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/83) | 83 | Checked three of its files row by row. The base is encounter data and names. The Sage Elder and Dark Gaffgarion add-ons touch story job rows (17, 19, 23) and story command records (32, 39, 82), none of them ours. The other add-ons, including the two added since (Inquisitor Zalmour, FK DK QoL), ship the same kinds of files for other story jobs, so they should be fine too. No item tables anywhere in the family. Job catch above at worst |
| [Remove 'Nothing Equipped'](https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/131) | 131 | Edits one cell of the item text table (the empty-slot label), a cell we never write. Text tables merge per cell, so both edits land |
| [Auto Battle Word Indicator Removal](https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/144) | 144 | One cell of the UI text table. We ship no UI text |
| [Better stat growth for Agrias Holy Knight](https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/168) and [Better stat growth for Cloud Soldier](https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/169) | 168, 169 | One story-job row each in the job table. Our rows are the generic jobs and Ramza's |
| [Unit Control](https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/132) | 132 | Code only, toggles manual or AI control of units. Different memory than ours. Nobody has run both together yet |
| [Equip Crossbow also Allows Bows](https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/129), [Special Characters Can Go on Errands](https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/147), [Quick Errands](https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/155), [Wait this is not Ramza this is ME](https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/154) | 129, 147, 155, 154 | Code-only tweaks to equip rules, errands and the hero's name. None of the tables or memory we use |
| [FFT Deeds of Arms](https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/138) and [FFT Combined Arms](https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/165) | 138, 165 | Code only (a gear proficiency system and a combo system). No shared files. Deeds of Arms is the closest idea to ours on the site. If you run both, I would like to hear how it goes |
| [Arithmeticks -ja Spells](https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/151), [Arithmeticks Summons](https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/152), [Instant Summons](https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/161), [Never-Fail Revive](https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/163), [Cloud Limits - Any Sword No Charge Time](https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/153) | 151, 152, 161, 163, 153 | Ability action data edits, a table we do not ship. Clean |
| [Classic Death Sounds](https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/130), [Free Cam](https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/127), [Midlight's Deep - Easy Exit](https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/166), [Better Looking Agrias](https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/156), [New Battle Sprite for Reis](https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/157), [Sprite replacement texture packs](https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/14), [Unique Job Icons](https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/32), [Level Scaling](https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/30), [Enhanced Encounters for Ch4](https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/54), [Final Fantasy Tactics Subtitle Indonesia](https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/126), [All Items Beginner Save](https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/124) | 130, 127, 166, 156, 157, 14, 32, 30, 54, 126, 124 | Sounds, camera, maps, sprites, job icons, encounter data, story dialogue, a save file. None of the files or memory we use |
| [Dark Knight Crash Fix - Auto Inject](https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/141) | 141 | A loader shim (a dinput8 auto-inject for Reloaded), not a game mod. Nothing to conflict with |

## Modding tools

Tools are not mods. You run them on your PC to make a mod and the game never loads them, so
there is nothing to conflict with. The mods people build with them can, and those land in
the classes above.

| Tool | Nexus # | Verdict |
|---|---|---|
| [The Ivalice Chronicles Mod Studio](https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/111) | 111 | Fine. It is an editor and the game never loads it. See the note below for what people make with it |
| [FFT Sprite Porter](https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/159), [Solo Ramza Manager](https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/146), [Austronesian Patcher](https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/134) | 159, 146, 134 | A sprite converter, a challenge-run editor, a translation patcher. What they produce lands in the sprite, save or text classes above |

Mod Studio can edit every file we ship, so anything built with it lands in one of the classes
above.

| If the mod was built to edit... | Where it lands |
|---|---|
| Item stats, shops, or equip bonuses | Will not work together. Both sides ship whole item rows and one side's rows are dropped |
| Item or ability names and descriptions | Text catch. Those files are replaced whole rather than merged, so whichever mod loads last supplies all of that text, and the painted kill counter can lose its anchor |
| Equipment menu icons | Whichever mod loads last supplies the picture. Nothing mechanical breaks |
| Jobs or job commands | Job catch. Worst case is the three weapon-granted commands switching off, and the mod tells you |
| Encounters, poaching, traps, sounds, other textures | Fine. Living Weapons ships nothing there |

Same advice as everywhere else on this page. Load Living Weapons after a text mod, never pair
it with an item rebalance. Expect more of both now that Mod Studio exists. Of the 48 mods
since August, six ship item rows and thirteen ship job tables.

## Not yet checked

None right now. Everything published as of 2026-10-17 is covered above or falls under the
rules below.

## Everything else

New mods show up all the time, but the rules do not change. Item rebalances will not work,
job mods can cost the three weapon commands, and text mods can cost some display text. If a
new one is not on this page, open an issue and I will check it and add it.

---

How this was checked. 2026-08-12, all 97 mods on Nexus, against our tables and runtime
guards, with Deep Brave Story and Ramza Overhaul downloaded and read row by row. 2026-08-21,
added Mod Studio after reading its source. 2026-10-17, catalog at 143 (48 new, 12 updated).
Every current upload's file list was read from the Nexus file manifest and sorted by the
files it ships, and WotL Restoration was downloaded and read file by file including its code.
One mod is left off this page while I sort out compatibility with its author directly. Page
last updated 2026-10-17.
