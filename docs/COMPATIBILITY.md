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

| Mod | Nexus # |
|---|---|
| CustomJOB_ITEM-1 | 64 |
| FFT Regabond's Rebalance | 72 |
| Antidote - FFT Rebalanced | 75 |
| The War of the Lions Equipment Replacer | 84 |
| All Items in Shops | 107 |
| Materia Blade Plus Replacement | 115 |

## Works, with a known caveat

These classes coexist with Living Weapons at one specific, known cost:

| If the other mod... | What happens | What you keep |
|---|---|---|
| Adds or rewrites jobs and touches the job-command records we use | Living Weapons detects the rewrite at startup, stays ON, and switches off only the three weapon-granted commands (Barrage, Shadow Blade, Provoke) with an in-game notice saying exactly that | Kill counting, growth, and every other weapon signature |
| Overwrites the text cells our custom names and descriptions live in | Those specific names/descriptions may display the other mod's text | Everything mechanical |
| ...or you play in a language other than English | The painted "Kills:" counter on weapon cards does not render (a non-English game loads different item text, so our anchor is not there). Switching the game to English and restarting shows it again | Counting itself continues untouched; nothing is lost, only hidden |

## Verified compatible

| Mod | Nexus # | Note |
|---|---|---|
| Generic Jobs | 34 | The site's most-downloaded mod; source-audited, and its one residual write pattern was checked in-game against our job-command region: clean |

## Not yet checked

Flagged during the survey as needing a closer look at their actual payloads; treat as
unknown until a verdict lands here:

| Mod | Nexus # |
|---|---|
| Ivalice Retranslation Project | 9 |
| Super and Ultrawide Resolutions | 11 |
| Cloud Re-Work | 46 |
| Dual Subtitles JP-EN Text with Kana Assist | 90 |
| Ramza Overhaul | 110 |

## Everything else

Every other mod published as of the survey date falls into "coexists cleanly" or one of the
caveat classes above. New mods appear all the time, and the classes are the durable truth:
an item-table rebalance will always be incompatible, a job mod costs at most the three
weapon commands, and a text mod costs at most some display text. If you are unsure which
class a new mod falls into, open an issue and it will be checked and added.

---

Survey provenance: hands-on review of all 97 published Ivalice Chronicles Nexus mods,
2026-08-12, against this mod's shipped tables and runtime guards. Page last updated:
2026-08-12.
