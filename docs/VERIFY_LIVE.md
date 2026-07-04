# Verify Live -- committed but not yet watched in-game

These changes pass both gates (analyze.py + LivingWeapon.Tests) and are committed, but the
gates prove logic, not engine behavior. Each row needs an in-game confirmation before it counts
as proven. Deploy with `.\BuildLinked.ps1`, watch it fire, then check the box (and flip the
relevant LIVE_LEDGER row if it has one -- only Patrick flips PROVEN).

| # | Commit | Change | How to verify live | Done? |
|---|--------|--------|--------------------|-------|
| 1 | e98c2f2 | **Choir multi-bearer** -- two deployed Warlock's Staff +3 bearers each project a duet (bearer + nearest ally, radius 1); winners union. | Deploy two units each with a +3 Warlock's Staff (id 60) in the MAIN hand on the same battle. Confirm BOTH project (up to four instant-cast allies get the Non-charge bit, magick casts resolve instantly) instead of the old "two bearers -> signature goes dark." Also confirm a benched 3rd copy neither projects nor blocks. | [ ] |
| 2 | b861806 | **KillTracker lethal-actor stamp** -- credit the weapon set latched at the alive->dead edge, not a later re-latch. | Land a lethal blow with weapon A, then have player B act before the enemy's dead-streak matures (DeadNeeded ticks). Confirm the kill credits A, not B. Watch the log line `kill: crediting whoever landed the finishing blow, not whoever acted most recently [lethal=[...] vs live-latch=[...]]` when the two differ. | [ ] |
| 3 | 279e7b8 | **SpriteID override** -- Warbrand (id 67, category-crossed to Sword) draws the right weapon graphic. | DATA change: takes effect on game **restart**, not live. Restart, equip Warbrand, confirm the held/swung graphic is a sword (not the offset flail art mid-swing). | [ ] |

## Notes
- Rows 1-2 are runtime DLL behavior (live on deploy). Row 3 is a table edit (restart-gated).
- If a verification FAILS, do not silently revert -- capture what you saw and reopen here.
