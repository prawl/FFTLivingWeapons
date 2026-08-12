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

## Incompatible (do not run together)

Both mods rewrite whole rows of the game's item tables. Whichever loads last wins each row,
so gear from the losing mod silently reverts. There is no safe ordering; pick one.

| Mod | Nexus # | Scope |
|---|---|---|
| CustomJOB_ITEM-1 | 64 | Full item rebalance; every gear row fights |
| FFT Regabond's Rebalance | 72 | Full item rebalance; every gear row fights |
| Antidote - FFT Rebalanced | 75 | Full item rebalance; every gear row fights |
| The War of the Lions Equipment Replacer | 84 | Full item rebalance; every gear row fights |
| All Items in Shops | 107 | Item table rows; the rows it ships fight ours |
| Materia Blade Plus Replacement | 115 | Item table rows; the rows it ships fight ours |
| Cloud Re-Work | 46 | Limited: ships ItemData rows for the gear it edits, so those specific items follow one mod only (its ability and encounter files are fine); pairing breaks either Cloud's kit or our rebalance on those items |

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
| Ivalice Retranslation Project | 9 | Text lane: it rewrites the game's English text including item and ability tables (its standalone version replaces the whole English text archive). Our custom item names/descriptions fight its translation, and the painted kill counter can lose its anchor on affected cards |
| Dual Subtitles JP-EN with Kana Assist | 90 | Text lane, milder: it switches item/job/ability names to Japanese while keeping descriptions in English (the author's stated design), so our custom names may fight it but the kill counter's description anchor likely survives. Not yet watched in game |

## Verified compatible

| Mod | Nexus # | Note |
|---|---|---|
| Generic Jobs | 34 | The site's most-downloaded mod; source-audited, and its one residual write pattern was checked in-game against our job-command region: clean |
| Super and Ultrawide Resolutions | 11 | Ships only resolution configuration tables; nothing shared with this mod |
| Deep Brave Story (base + add-on files) | 83 | Payload-verified on three of its eight files: the base ships encounter data and character/place names (none of the files we touch), and the Sage Elder and Dark Gaffgarion add-ons edit only story-job rows (17, 19, 23) and story command records (32, 39, 82), none of the rows or records this mod ships or guards. The other boss add-ons (Dark Witch, Witch Assassin, Divine Alma, Elmdor Enhanced, Delita QoL) ship the same file classes aimed at other story jobs and are expected clean the same way; in the worst case one rewrites the two command records we guard, and then the mod says so in game and costs only the three weapon-granted commands. No item tables anywhere in the family |

## Not yet checked

Treat as unknown until a verdict lands here:

| Mod | Nexus # | Why still unchecked |
|---|---|---|
| Ramza Overhaul | 110 | Its upload storage format offers no archive preview, and the page does not say which tables it ships; needs a payload check |

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
reading three of its downloaded payloads row by row against this mod's shipped tables. One
mod (Ramza Overhaul) resists both routes and stays unchecked. Page last updated: 2026-08-12.
