# Mod Compatibility

STATUS: CONTRACT (the player-facing compatibility grid the Nexus page links to; update it
whenever a mod is checked or a conflict class changes)

This page answers one question: **can I run FFT Living Weapons alongside mod X?** It comes
from a hands-on survey of every mod published on the Ivalice Chronicles Nexus (all 97 as of
2026-08-12) and gets updated as new mods appear or get checked. If a mod misbehaves alongside
Living Weapons, please open a GitHub issue on this repository.

## The short version

- **Six mods cannot run with Living Weapons.** All six ship their own item tables, and the
  mod loader keeps only one side's rows. This is structural: **no load order fixes it.**
- **Two caveats cover most of the rest:** custom-job mods can cost you the three
  weapon-granted commands (Living Weapons detects this itself and tells you in game), and a
  non-English game language hides the kill counter on weapon cards.
- **Everything else coexists cleanly** (roughly two-thirds of the catalog surveyed clean
  with no caveat at all).
- **Mod-making tools are always safe**, because a tool is never loaded by the game. Only the
  mods people build with one can conflict, and those land in the classes above.

## Incompatible (do not run together)

Both mods rewrite whole rows of the game's item tables. Whichever loads last wins each row,
so gear from the losing mod silently reverts. There is no safe ordering; pick one.

| Mod | Nexus # | Scope |
|---|---|---|
| [CustomJOB_ITEM-1](https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/64) | 64 | Full item rebalance; every gear row fights |
| [FFT Regabond's Rebalance](https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/72) | 72 | Full item rebalance; every gear row fights |
| [Antidote - FFT Rebalanced](https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/75) | 75 | Full item rebalance; every gear row fights |
| [The War of the Lions Equipment Replacer](https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/84) | 84 | Full item rebalance; every gear row fights |
| [All Items in Shops](https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/107) | 107 | Item table rows; the rows it ships fight ours |
| [Materia Blade Plus Replacement](https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/115) | 115 | Item table rows; the rows it ships fight ours |
| [Cloud Re-Work](https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/46) | 46 | Limited: ships ItemData rows for the gear it edits, so those specific items follow one mod only (its ability and encounter files are fine); pairing breaks either Cloud's kit or our rebalance on those items |

## Works, with a known caveat

These classes coexist with Living Weapons at one specific, known cost:

| If the other mod... | What happens | What you keep |
|---|---|---|
| Adds or rewrites jobs and touches the job-command records we use | Living Weapons detects the rewrite at startup, stays ON, and switches off only the three weapon-granted commands (Barrage, Shadow Blade, Provoke) with an in-game notice saying exactly that | Kill counting, growth, and every other weapon signature |
| Overwrites the text cells our custom names and descriptions live in | Those specific names/descriptions may display the other mod's text | Everything mechanical |
| ...or you play in a language other than English | The painted "Kills:" counter on weapon cards does not render (a non-English game loads different item text, so our anchor is not there). Switching the game to English and restarting shows it again | Counting itself continues untouched; nothing is lost, only hidden |

### Specific mods checked into these classes

| Mod | Nexus # | Verdict |
|---|---|---|
| [Ivalice Retranslation Project](https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/9) | 9 | Text lane: it rewrites the game's English text including item and ability tables (its standalone version replaces the whole English text archive). Our custom item names/descriptions fight its translation, and the painted kill counter can lose its anchor on affected cards |
| [Dual Subtitles JP-EN with Kana Assist](https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/90) | 90 | Text lane, milder: it switches item/job/ability names to Japanese while keeping descriptions in English (the author's stated design), so our custom names may fight it but the kill counter's description anchor likely survives. Not yet watched in game |
| [Ramza Overhaul](https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/110) | 110 | Payload read row by row: its command records (25-28, Ramza and Delita) and job text are clear of everything we ship or guard, and no item tables. Exactly one row overlaps ours (a Ramza chapter job where it grants Knight Sword access and we raise evasion): depending on load order one of those two small edits can lose, so worst case is Ramza missing 3 points of evasion or the overhaul missing Knight Sword access on that chapter. Everything else coexists |

## Verified compatible

| Mod | Nexus # | Note |
|---|---|---|
| [Generic Jobs](https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/34) | 34 | The site's most-downloaded mod; source-audited, and its one residual write pattern was checked in-game against our job-command region: clean |
| [Super and Ultrawide Resolutions](https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/11) | 11 | Ships only resolution configuration tables; nothing shared with this mod |
| [Deep Brave Story (base + add-on files)](https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/83) | 83 | Payload-verified on three of its eight files: the base ships encounter data and character/place names (none of the files we touch), and the Sage Elder and Dark Gaffgarion add-ons edit only story-job rows (17, 19, 23) and story command records (32, 39, 82), none of the rows or records this mod ships or guards. The other boss add-ons (Dark Witch, Witch Assassin, Divine Alma, Elmdor Enhanced, Delita QoL) ship the same file classes aimed at other story jobs and are expected clean the same way; in the worst case one rewrites the two command records we guard, and then the mod says so in game and costs only the three weapon-granted commands. No item tables anywhere in the family |

## Mod-making tools

Not every mod page is a mod. A tool is a program you run on your own computer to build a
mod; it never gets installed into the game, so it cannot fight with Living Weapons at all.
What it can do is make it much easier for other people to build mods that do.

| Tool | Nexus # | Verdict |
|---|---|---|
| [The Ivalice Chronicles Mod Studio](https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/111) | 111 | Compatible, because it is a mod editor rather than a mod: nothing from it is ever loaded by the game. Mods somebody builds with it land in the classes already on this page. Read the note below before pairing one with Living Weapons |

**Mods built with it.** Mod Studio can edit every kind of file this mod ships, so anything
made with it falls into a class already described above, and there is no new kind of
conflict to learn:

| If the mod was built to edit... | Class it lands in |
|---|---|
| Item stats, shops, or equip bonuses | Incompatible. Both sides ship whole item rows and one side's rows are dropped |
| Item or ability names and descriptions | Text caveat. Those files are replaced whole rather than merged, so whichever mod loads last supplies all of that text, and the painted kill counter can lose its anchor |
| Equipment menu icons | Whichever mod loads last supplies the picture. Nothing mechanical breaks |
| Jobs or job commands | Job caveat. Worst case is the three weapon-granted commands switching off, with an in-game notice |
| Encounters, poaching, traps, sounds, other textures | Coexists cleanly. Living Weapons ships nothing in those lanes |

The practical advice is unchanged: load Living Weapons after a text mod to keep our item
names and the kill counter, and do not pair it with an item rebalance at all. What is new
is how ordinary those pairings are about to get, because building one no longer takes any
modding knowledge.

## Not yet checked

Nothing at the moment: every mod flagged by the survey now has a verdict above. New mods
get checked as they appear; open a GitHub issue to request one.

## Everything else

Every other mod published as of the survey date falls into "coexists cleanly" or one of the
caveat classes above. New mods appear all the time, and the classes are the durable truth:
an item-table rebalance will always be incompatible, a job mod costs at most the three
weapon commands, and a text mod costs at most some display text. If you are unsure which
class a new mod falls into, open an issue and it will be checked and added.

---

Survey provenance: hands-on review of all 97 published Ivalice Chronicles Nexus mods,
2026-08-12, against this mod's shipped tables and runtime guards. Same-day follow-up: the
formerly unchecked mods were classified by enumerating their archive contents through the
Nexus file-preview metadata (no downloads), and Deep Brave Story's verdict was upgraded by
reading three of its downloaded payloads row by row against this mod's shipped tables, and
Ramza Overhaul was settled last the same way from a downloaded payload. Every survey-flagged
mod now carries a verdict. Updated 2026-08-21: The Ivalice Chronicles Mod Studio (111)
added after reading its shipped source in full; it emits mod folders in the same formats
this mod ships, which is where the classes in its section come from. Page last updated:
2026-08-21.
