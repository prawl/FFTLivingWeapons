# TODO

STATUS: CONTRACT (machine-checked by TodoContractTests; format grammar at the bottom of this file)

The work ledger. "Now" holds what is actively being worked for the current release (hard cap 5,
each entry carries Done means + Verify). "Backlog" captures everything else at the cheapest
possible entry cost. Items EXIT this file only through docs/CHANGELOG.md, moved there in the
commit that ships or kills them. The full release ship gate stays in docs/RELEASE_SCOPE.md; Now
is the in-flight subset, not a mirror of that checklist.

Entries are written ELI5-first: the opening sentence is plain language anyone can follow, and
the technical detail lives in the indented lines under it.

## Now (release: 2.4.0)

- **[LW-465] A Provoke hold and a planted Bulwark let go the moment a battle rewinds** (opened 2026-10-19) [QUEUED]
  - Quitting to the title and choosing Continue mid battle, or picking Retry, rewinds the fight in place without the battle end signal the Provoke hold and the Bulwark listen for, so a hold can hide the party until its watchdog lets go (box 9.2) and a barred tile stays barred until the wielder's next turn (box 9.4, LW-466). The first fix was dropped on release night because nobody has measured when the game rebuilds the battlefield after the restore. Probe that moment first, then release both on it.
  - Done means: a tape of a Retry and a quit and Continue with a hold up and a blade planted shows, per tick, the restore count rise, the live gate, battleMode, the band's invisible bits and the barred tile; the release then lands on a proven moment, nobody is left invisible, no tile stays barred, and no watchdog line fires.
  - Verify: the probe tape banked under tools/probes/tapes; suite green with tests built from that tape; the owner's live pass on boxes 9.2 and 9.4 step 3.
## Backlog

- [LW-517] 2026-10-23: A player on Nexus asked for the three Dancer cloths to grow Physical Power instead of Magic Power, since PA does more for a Dancer. Weigh the swap and, if it holds up, make it.
  (Tech: Tarsilk Veil 119, Bindsilk Veil 120, Gravesilk Veil 121 carry `"grows": "MA"` in data/items.json; change the field, regenerate, rebake meta, and check analyze.py plus the growth lane tests. A Nexus comment, 2026-10-23.)
- [LW-506] 2026-10-23: About a dozen of the mod's hooks switch on a moment before they save the handle they need to call the game's own routine, so a game call landing in that sliver would crash the game. Nobody has seen it happen, and the gap is microseconds at startup, but the fix is cheap.
  (Tech: `_hook = hooks.CreateHook(...).Activate()` assigns after Activate returns while the detour reads `_hook!.OriginalFunction`. Create, assign, then Activate. Sites: CategoryGetterHook, InventoryResetHook, ListBuilderHook, OrderRebuildHook, RewindRestoreHook (two), SaveEdgeHooks (three), PromptSwapHook, IconCacheHeal. Covers LW-468 and the same note on LW-500.)
- [LW-507] 2026-10-23: Three old branches on GitHub still carry commit trailers that credit an outside tool, which this repo forbids. Their work is already on main under clean commits, so the branches can simply be deleted once the owner says go.
  (Tech: origin/docs/extended-code-map, origin/tooling/health-report, origin/cold-read-extended-sweep. The sweep's own commits are clean, but its work is merged, so it can go too.)
- [LW-508] 2026-10-23: The cold read log says the item category hook was changed to refuse installing before the catalog, but that change never reached main. It sits uncommitted in a side worktree, so either land it properly or note in the log that it was dropped.
  (Tech: .claude/worktrees/extended-readability, LivingWeapon/Extended/CategoryGetterHook.cs, three code lines; log entry "Seeded trial build 3" records hash 17437a8ea40e9f3c, main's file differs.)
- [LW-509] 2026-10-23: Thirty five findings from the cold read sweep sit untriaged, so real bugs may be hiding among style notes. Read each once and promote it, merge it into a real row, or close it.
  (Tech: the "Cold read findings, untriaged" section below, LW-468 to LW-503.)
- [LW-510] 2026-10-23: The release health record has three rough edges. The tag's own CI run warns that the record is missing because it is committed after the tag, the reminder names the current version as the next one, and the wiring test does not check that a failing selftest stops the build.
  (Tech: document Publish, commit docs/health, then tag, or have the tag run accept an uncommitted record; health.py status message; HealthReportWiringContractTests should pin the throw after `--selftest`.)
- [LW-511] 2026-10-23: The number of places where the mod quietly swallows an error doubled this release, from 21 to 44, so failures can happen with no trace in the log. Audit them and give each one that matters a log line or a counter.
  (Tech: docs/HEALTH.md v2.4.0 silent catches; several cold read rows such as LW-471 and LW-481 name specific ones.)
- [LW-512] 2026-10-23: The README does not tell players that Counter and other reaction kills do not count toward a weapon's tally, which is by design this release, so "my Counter kills do not count" will arrive as a bug report.
  (Tech: add a known issue bullet beside the Provoke and Bulwark ones; design per LW-396 and smoke open risks.)
- [LW-513] 2026-10-23: Players who installed Reloaded somewhere other than the game folder lose the glow rings on icons until a restart, because the mod looks for the game's art file a fixed three folders up. Find it a sturdier way.
  (Tech: IconGlow warns "IconGlow cannot locate modded.pac this launch"; smoke open risks, box 7.2.)
- [LW-514] 2026-10-23: Nobody has tested buying a new extended weapon in a brand new game that was never reloaded, the one path where only the game's own menu rebuild seats it. A reload fixes it if it fails, but a test save that reaches the first shop would close the gap.
  (Tech: OrderRebuildHook.Seat.cs; SMOKE_TEST_2.4.0 Appendix A, the Lesalia Terrastaff lane.)
- [LW-515] 2026-10-23: There is no repo tool that fills the bag with every item, so the long inventory list past 149 rows could not be retested this release. Build a small probe that writes a full bag on a test save.
  (Tech: HandsFree crashes the copy protection free build; smoke box 4.3 read NOT REACHED for its second half.)
- [LW-516] 2026-10-23: Add a ruler that stops methods from growing too deep or too long, as ruled before the tag, since long methods rose from 13 to 17 this release.
  (Tech: MethodBudgetContractTests beside CommentBudget, nesting depth cap 4 first, body lines cap 80 as backstop, constructor parameters cap 6 with a group them note, frozen allowlist that only shrinks; owner ruling 2026-10-18.)

- [LW-467] 2026-10-19: The twin weapon does not appear on the Status page when you back out of Equipment and Abilities; it only appears after the menu is closed on the world map for a few seconds. The party browse flag the mod trusts to say "the Status page is up" reads 0 on the Status page on the Denuvo free build (live read 2026-10-19 23:03, menu byte 246 and browse 0 on three samples), so the only path left open is the closed menu. Re map the browse flag on this build, the way LW-439 re mapped the menu byte, then let the Status page stamp again. Belongs with LW-450. (Tech: Offsets.PartyBrowseFlag 0x140D408E2, GunSlinger.MenuGate.cs ReadBrowseStable; ledger row [party-browse-screen-byte] was proven on the old fixed base.)
- [LW-466] 2026-10-19: After a Retry with the Sunderer's Bulwark planted, the barred tile behind the wielder stays barred until the wielder's next turn opens, and the plant toast replays once on the rewound battle. Seen live on the dev build of 29632f46 (smoke box 9.4 step 3): plant at 22:48:37 barring (11,5), Retry at 22:49:04, a stale `The Sunderer plants its blade` toast delivered at 22:49:05, release only at 22:49:25 when the wielder's turn opened. It heals itself within one turn and only blocks one tile, so it ships as known for 2.4.0. Fix it with LW-465 in 2.4.1 on the same measured rewind moment: release the Bulwark and drop any queued signature toast when the checkpoint restore lands. (Tech: Bulwark.cs and Bulwark.Terrain.cs release on the wielder's turn edge or a battle edge only; the LW-458 restore count ExtendedInventory.RetryRewinds is the signal; BannerToast queue survived the rewind.)
- [LW-463] 2026-10-19: The readability quiz becomes one four stage pipeline, in a fixed order, after the 2.4.0 tag. First the cold read as today, with one label change (a why that lives only in a comment counts as a miss, and "bad question" means only that the answer needs code outside the packet). Then one plain words comment pass, comments only, with the stripped code proven identical and comment lines not allowed to rise. Then every warning comment ("must run before", "changing one means changing the other") goes back to the code and is fixed there or kept with a logged reason. Last, a second toolless reader gets the files with comments and reports any comment the code does not back up. One log entry per change with a result per stage. Values in the order they win, accuracy, then understanding, then readability. (Tech: owner review doc "Corpses readability review", 2026-10-19; the four versions on branch compare/corpses-readability, A 12ac7ea7 plain words only scored 6, B 2e0fd3ad cold read only 6.5, C a8072bc8 cold read then plain words 7, D 14708265 the warnings turned into code; the gate and its skill live in the home setup repo.)
- [LW-462] 2026-10-19: The corpse scan keeps about fifteen side by side lists, one entry per battle seat, so forgetting a death means clearing every list by hand and missing one is easy. After the 2.4.0 release, give each seat one object holding what we watch (seen alive, streaks, recorded identity, last dead hp), the open death case (verdict, waiting, killer stamp, victim snapshot) and the paid kill, so forgetting a death is two assignments. One identity type with one same unit check replaces the three tuples. Then turn the dead edge priority chain into a small pure function that returns a verdict, tested with a table of cases, name the team values, and return what changed instead of threading a ref flag. One group per commit, paid kill first, replayed battles green before and after each step, seats made once per battle. (Tech: LivingWeapon/Kills/KillTracker.Corpses.cs; owner review doc steps 7.5 to 9; Seat, Watch, DeathCase, PaidKill, UnitIdentity.SameUnit are the review's names.)
- [LW-461] 2026-10-19: The check that decides a different unit now sits in a seat compares level, brave and faith but leaves out max hp, while the alive record that decides whether a death can pay is keyed with max hp included. A max hp change alone can therefore send a real first death down the orphan path instead of the normal one. Decide whether max hp belongs in the seat check, and test the case. Separately, the seat clear leaves the fallback flag behind; harmless today because every reader checks the killer stamp first, but it should clear with the rest. (Tech: RecordedIdentityChanged vs _identityAlive's (lvl,br,fa,maxHp) key; ForgetDeathAndCredit skips _lethalViaFallback; found by the 2026-10-19 cold reads and the blind review.)
- [LW-460] 2026-10-19: The two places that leave a kill unpaid because the killer had no Living Weapon disagree on what they leave behind. One marks the enemy dead on the record, the other leaves it marked alive, so a second death of the same enemy is blocked after one and allowed after the other. Make them agree or make the difference a named rule with a test. A player would only see it as a second kill on one unit paying or not paying depending on which path buried the first. (Tech: ResolveCredit's untracked bury writes _identityAlive false; HandleOrphanAliveEdge's writes true first and returns; flagged by every reader in the 2026-10-19 readability runs.)
- [LW-459] 2026-10-19: A retry can pay a kill twice when the dead enemy comes back with changed brave or faith. The branch that handles a unit whose stats changed while it stood back up compares the old credited identity against the new stats, which can never match there, so the retry check leans only on the battle age grace and an early retry slips through. Check this against the LW-458 retry failure first, since both end in a phantom kill after a Retry. (Tech: ScanAlivePath's identity swap branch, UncreditIfRestartRevive with _creditedIdentity stamped from the tuple that seeded _slotId; the old comment called it the Finding 6(ii) residual; RestartSentinelPolicy.GraceTicks.)
- [LW-457] 2026-10-19: When the mod's DLL is missing but its item tables still load (a half broken install, or the DLL renamed for smoke box 4.5 leg 3), every living weapon card shows the raw placeholder `Kills: -/- to +` and nothing ever fills it, since the dashes are baked into the text tables and only the running DLL paints the numbers over them. Harmless and no crash, seen by the owner on 2026-10-19 at 03:10 with the DLL renamed to .off. Add one sentence to README.md's troubleshooting and docs/COMPATIBILITY.md so a player who sees dashes forever knows the DLL did not load and checks Reloaded's log for `DLL Not Found! LivingWeapon.dll`. Docs only, no code. (Tech: tools/lib/flavor.py KILLS_SCAFFOLD; Reloaded writes the line to %APPDATA%\Reloaded-Mod-Loader-II\Logs.)
- [LW-456] 2026-10-19: A Ninja with Throw Swords learned cannot Throw a Warbrand, because the game's Throw list stops at the base game's items and never offers one of the seven new weapons (owner saw Claymore and Materia Blade listed and no Warbrand, 2026-10-19 02:17). Hook the Throw list builder the way the weapon menus were hooked so the new weapons can be thrown, and check the thrown item leaves the bag count. Owner ruling 2026-10-19: out of 2.4.0, recorded as a known limit in docs/EXTENDED_INVENTORY_GUIDE.md; a 2.5.0 arc. (Tech: the Throw list is one of the optional extra reads LW-346 never opened; the research journal lists it as unchecked since 2026-08-27.)
- [LW-455] 2026-10-19: When a battle is retried and the mod cannot read what the party's hands hold at that instant, the log still says the new weapons' bag counts were put back, with empty parentheses, so a no op reads like a success. Say plainly that nothing was written because the hands could not be read, and count it. Found by the third cold read of LW-452; never seen live, the roster block is static memory.
- [LW-454] 2026-10-19: On the first battle of a launch the game put a checkpoint back that this mod never saw being saved, so the once per launch Warn fired at 01:27:12 on 2026-10-19 while nothing was wrong (nothing was written, Warbrand held at 5). The checkpoint was stored before this launch, most likely in the save file. Decide whether that first restore should log at Debug instead, or whether the mod should capture a total at that moment. (Tech: RewindRestoreHook.RestoreCounts not found branch, CheckpointRestoresUnrecorded.)
- [LW-451] 2026-10-18: The world map Inventory list loses the three books when it is sorted by Type, and shows them again sorted by Acquired. Plain version, the owner pressed Sort until the weapons were grouped by type and the Glarebound Tome, Knell Codex and Binding Codex were nowhere in the list, while the Equipment picker on an Arithmetician listed all three and the Acquired sort showed them between the Blaster and the Greenwood Pole. Seen 2026-10-18 at 14:04 on prod with a bag holding nearly every weapon kind (most rows read 0 of 99). A player with a full bag sees it, so it is a candidate for R5 under the freeze rule once the cause is known; the owner's first guess is that the item cap break (LW-346, LW-371, LW-372) leaves the Type sort's list too short for a bag this size. Smoke box 4.3 names exactly this shape, a list stopping short of an owned kind, and this is its first live sighting.
  (Tech, offline read 2026-10-18 14:30 to 15:00 on the live prod process with the menu closed, nothing written: the list path is dispatcher 0x14036C3EC, builder 0x140288B94 into the static buffer 0x141811470, then 0x14036B18C reads the menu sort mode (0x14019879C) and either walks the order template through 0x140285DF0 (mode -1, the default order) or calls the sorter 0x140286228(mode, list, -1). The sorter compacts the list through the validity accessor 0x1402B8EBC first in every mode, then mode 8 (Acquired) rebuilds from the acquired record 0x141874726 while every other mode runs a swap only bubble sort with comparator 0x14028B180 over three keys, 0xB (is this the highlighted id, always 0 here), the mode itself, and 0xA (the id). Mode 0 is the Type sort and its key reads the item record's category byte at +5 through 0x1402B8C44, category 19 mapped to 100 so shields sort last. ELIMINATED: both relocated weapon templates (tab 0 of 0x14067F498 and 0x14067FA90, live pages at preferred 0x143FB0000 and 0x143FB0400) hold 131 entries with 95, 96, 97 at positions 71 to 73 and no duplicates; the shipped item.en.nxd keeps the books at category 14 and SortOrder 901 to 904, the same as vanilla; the shipped ItemData.xml ships no row for 95 to 98 so their category byte is vanilla 14; the compaction cannot be the drop because the Acquired path runs it too and shows the books; a swap only sort cannot lose a word; the last built buffer holds 115 weapon kinds, under the 149 cap, with no flag bits. LIVE READ 2026-10-18 15:00 to 15:15, owner at the screen, tools/probes/lw451_inventory_snapshot.py (read only, rebased through lib.livebase) run after every step: the Sort button opens a chooser with four modes, Acquired, Type, Name and Strength, and the menu remembers the last one across opens. All four held every one of the 115 weapon kinds in the buffer with the three books present, Type at rows 72 to 74 (the chart order, which is the default walk), Acquired at 22 to 24, Name at 9, 38 and 54, Strength at 78, 79 and 105, no duplicates and no flag bits; the owner saw them on screen under Type and Acquired. The owner's recipe (Equipment and Abilities on an Arithmetician, back to Inventory, Sort) was replayed and did not reproduce; the picker build left nine words in the buffer (the books and six poles) and the next Inventory open rebuilt all 115. The only logged event near the 14:04 sighting is a save written at 13:53:45. The panel pointer chain the August probe used reads NULL on the world map Inventory, so the sort mode field was not read; the buffer order identifies the mode instead. NOT reproduced, kept as a watch row on the owner's word; if it shows again, run the probe before touching Sort, then once per mode.)
- [LW-450] 2026-10-18: The twin pistol and twin crossbow feature needs a real design and repair pass; owner ruling 2026-10-18 after the smoke box 9.6 walk, "it works sorta" from repeated observations, so one blanket row carries it instead of a fix per symptom. Seen that sitting on prod with a tier 3 Outrider Pistol and no support picked: the twin stamped on the Status visit as designed, then putting the player's own second gun (Ironclad Repeater, id 72) in the off hand made the game unequip the main hand pistol too and refund the conjured twin into the bag as a phantom copy (the LW-254 watch line fired, nothing was corrected), and the LW-426 steps aside line never printed because the main hand was no longer a twin weapon. The design questions to settle before code: what the player should see when they replace the twin by hand, whether the lent Dual Wield should stay while gear is edited, whether the phantom refund gets corrected (LW-254) or the grant stops writing the off hand at all, and what the Status page shows after Equipment (LW-439). Folds the open live reads of LW-426 and LW-439 and the LW-254 watch mode into one pass. Not for 2.4.0 unless the owner says so; the feature ships as it is with its known edges.
  (Tech: GunSlinger.Apply.cs DesiredOffHand and DesiredSupport, GunSlinger.Reconcile.cs twin-refund, GunSlinger.MenuGate.cs, LaunchGuard.Twin.cs; LIVE_LEDGER [twin-grant-inventory-desync] and [twin-dualfire-construction-bound]; smoke box 9.6 stays open, its 2026-10-18 partial reads are the stamp line at 13:32:29 and the refund line at 13:33:34.)
- [LW-449] 2026-10-18: The live tally poke tool can write into the wrong map when the weapon has never scored a kill. Plain version, the tool finds the mod's kill tally in the running game by its shape and writes one number; when the tally has no entry for that weapon (a weapon at zero kills is simply absent from the file), the only match it finds is one of the mod's other per weapon maps that hold every id, and it writes there, so the card still reads 0 and the mod sees nothing. Seen 2026-10-18 at 13:25 on prod with the Outrider Pistol (id 71, 1 of 1 copies, 12 corroborating neighbours, no re judge pass after). Dev tooling only, nothing a player sees, outside the 2.4.0 scope. Fix idea, refuse to write unless the matched entry sits in a dictionary whose key set equals the kills.json key set, and say "stage the weapon in kills.json first" instead.
  (Tech: tools/probes/lw381_tally_poke.py, the per process address cache made the second run a cache hit on the same wrong address; candidates it can hit are BannerToast._counts, Display._lastCounts and the AttackCard snapshot, each a Dictionary<int, int> over the meta ids; the restore poke to 0 was run the same minute.)
- [LW-448] 2026-10-18: Enemy Knights and Squires can cast Shadow Blade, and enemy Thieves can cast Barrage, whenever a player in the party holds the matching weapon at its top tier, because the mod grants those commands by writing them into the job's shared command list and every unit of that job reads the same list. The owner saw an enemy Knight named Dyana cast Shadow Blade on 2026-06-16; the bug was deferred on 2026-06-27 with two fix options and then fell out of the ledger when the LW ids arrived, surviving only in docs/archive/STAFF_SWORD_TEST_PLAN.md and git history (commits 63d4c183 and 110bd72a). Re surfaced 2026-10-18 by an owner question; nothing was ever wired, the code still grants on records 5 and 7 and carries no turn team gate. A player sees it, so under the freeze rule it may join R5 when a Now seat frees; the owner picks the fix. Options as analysed 2026-06-27: (A) a turn team gate that pulls the command out of the record on enemy and ally turns and re injects on the player's turn, relying on the 33 ms loop beating the AI's ability enumeration, must be live verified; (B) Ramza only, restrict Shadow Blade to the Mettle records 25 to 28 that no enemy uses, zero leak by construction but a generic Knight loses the signature; (C) drop the command grant for a per unit write and hold signature. Barrage has the same shape on the Thief record 14. Scope is unchanged until a wielder reaches +3 (15 kills on prod), so new players never meet it in the first chapter.
  (Tech: ShadowBladePolicy.AllowedRecords is still { 5, 7 } plus RamzaSwordJobs; no TurnQueue read exists in ShadowBlade.cs or Barrage.cs; LIVE_LEDGER [zero-value-command-ai-silent] proves only that a ZERO value command is not chosen by the AI, which is why Provoke does not leak in use while these two do; the UsableByAI byte at 0x140787F80 +7 mask 0x80 per ability is the mapped suppressor, never shipped, and clearing it for 165 and 358 would likely also stop a player unit on auto battle from using them, unverified.)
  Recommended fix, 2026-10-18 offline analysis: hold the ability's UsableByAI bit CLEAR while the grant is active (option E), the same mask scoped read, modify, write hold every other grant already uses, restored on release. One bit per ability, no turn timing race, covers Barrage and Shadow Blade alike, and the enemy still sees the command in its list but never picks it. The cost is that a player unit under AI control (auto battle, Charm, Confuse, Berserk) cannot cast it either, which reads as acceptable or even welcome. The premise is unproven, the bit was mapped on 2026-07-23 and never tested as a suppressor, so step one is the owner's probe, `python tools\probes\ability_grant_probe.py aiflag 165 clear` held through one battle with a +3 Sanguine Sword Knight in the party and enemy Knights on the field, following the probe's own protocol in verb_aiflag. PASS means the player's Shadow Blade still casts and no enemy Knight casts it across the battle; FAIL means an enemy cast with the bit clear, and the fallback is option A, the turn team gate at TurnQueue +0x02, which needs its own live run for the race. Option B does not cover Barrage and option C is a rewrite.
- [LW-447] 2026-10-18: Redesign the battle weapon colors for 2.5.0 so the weapon a unit swings looks as close to its equip icon and card art as the sprite's fifteen color slots allow, then bring the feature back. Carries LW-412 (exited 2026-10-18). Lessons from the 2026-10-18 sitting that the redesign must settle before code: the kill tier glow is decoupled from the sprite (the icon's art never changes with tier, only its ring does, so the sprite has one target); the palette map is verified against the game's own table for all 127 weapons, not trusted from the generated file plus two overrides (LW-305); thrown weapons, the Move Find staff and the ex flails (LW-312) draw from sources the palette path does not reach and need their own path or a stated exclusion; the parry clash and the counter in steel (LW-310) get a ruling; the edge pass (LW-339) runs before the feature ships; and a build gate compares each sprite's body hue to its icon so a mismatch cannot pass by eye again. The design doc sentence lives in docs/DESIGN.md section 8.
- [LW-408] 2026-10-17: Find out whether Living Weapons runs alongside WotL Restoration and write the answer down. Demoted from Now 2026-10-18 to make the seat for LW-446 (the weapon color cut); it is an owner run with no code and goes back to Now the moment a seat frees. Its Done means and Verify as written:
  - Done means: a player reading docs/COMPATIBILITY.md knows what happens with both mods installed, which order to load them in, and what each mod's own log will say. Payload half done 2026-10-17: WotL Restoration 2.0.7 was downloaded and read file by file (its text tables diffed against vanilla and ours, its job table, and its decompiled code), and the verdict is written in the doc's "WotL Restoration" section: characters and events coexist, its 40 new items switch off because both mods break the 261 item cap at the same code sites and ours arms first, and Living Weapons must load last to keep its item names. (Tech: her nine accessor hooks are our ten entries; 52 of her 63 relocations and 30 of her 44 caps sit on our sites; item.en.nxd keys 261 to 267 are written by both; her JobData rows 162 to 168 are clear of ours; ability 358 Barrage is named by both.)
  - Verify: owner runs the game once with both mods enabled, WotL Restoration loading before Living Weapons. Expected: Living Weapons' parts report reads all ON (extended inventory included), WotL Restoration's log prints its "Item expansion NOT armed" line, Luso and Balthier's events still fire, the Terrastaff card reads Terrastaff with its kill counter, and Agrias's birthday hands out a Septième Sens. Owner flips this row to AWAITING-LIVE when ready and PROVEN or not after the run.
- [LW-443] 2026-10-18: (carries LW-441, exited 2026-10-18) Hide an icon at the moment the game next draws it, so the new ring shows with no search of memory at all. Plain version, today the mod hunts through the game's memory for the old icon, which is slow and was the source of the memory growth. The game already tells the mod every time a screen asks for an icon, so when that ask returns an icon the mod wants replaced, the mod can hide it right there and answer "not found", and the reload fires on the same call. (Tech: when the draw thread's lookup returns an entry for a name waiting to be hidden, hide it inside the hook and return a miss so the heal fires on the same call; the entry is alive by construction, so there is no sweep for parts entries and no tick thread write, and per cache quiet falls out; the texture record keeps its first sweep. A 2.5.0 row that replaces the proven sweep path, the second reader's design, 2026-10-18.)
- [LW-444] 2026-10-18: The plus one, plus two, plus three on a weapon's name in the list lags and sometimes reads wrong until the player unequips and reequips it. Plain version, the mod paints the suffix onto the name after the game has already drawn it, so it can miss or be stale; it should add the suffix as the game writes the name, the same lesson as the icons. (Tech: append the suffix inside the text setter hook the toasts already own, PromptSwapHook and FnSetTextString; the after the fact painter stays for the cards; 2.5.0.)
  (Tech: IconCacheHide.ClassifyAndWrite, HideOutcome.TextureMissing when texHidden is 0; the pointer sweep looks for pointers to the path string start, a hidden record points at start+1; the heal only fires on a miss, so an intact stale parts entry never heals.)
- [LW-438] 2026-10-17: On the first launch after a prod deploy, with every weapon at +3, no weapon name in the lists showed its +3, and unequipping and re equipping did not bring it back; on later launches every name read +3 again. Reproduce on a deploy with a few weapons at a tier, and decide whether a player updating the mod would see bare names for a launch. If a player would see it, it joins R5 under the freeze rule.
  (Tech: seen 2026-10-17 about 22:20 on prod of the tree later committed as ea841c1c; all 121 tallies were at 15 from a direct kills.json edit, so the name suffix room (capped, SuffixRotation) may simply have been full; the 23:45 owner screenshot shows Outrider Pistol+3 and the gun names correct.)
  Checked offline on 2026-10-18 and the full room idea does not hold up. The painter has room for eight copies of every one of the 121 names at once, and the last live count (2026-08-18) was about four copies per name, so 121 weapons at +3 fit with half the room to spare; a test now paints all 121 at +3 on a cold launch and on a warm start alike. The room only overflows at nine copies per name, and then the last found copies stay bare whatever the tier count, so it is the headroom that rules the room out, not the tier count. The tests model the room's arithmetic only, not the launch window (the memory sweep runs before the pool is found, and every Continue load makes the painter look again). The warm versus cold split is not keyed to a deploy either, because the region file lives in the Reloaded user folder the deploy script never touches. The one piece of state a deploy does reset is the icon glow's first apply, which is icons, not names. Only the owner's live reproduce can pin the first launch difference.
  (Tech: tests in LivingWeapon.Tests/DisplayAllTieredSuffixTests.cs; CardSites.MaxSuffixSites 1024 and SuffixCopiesPerId 12 give 121 x 8 = 968 inside the room and 121 x 9 = 1089 over it; which copies overflow depends on region scan order, PoolLocator.WarmStart.cs compares region lists as sets for that reason; the sweep alone paints 9 names on its first pass and all 121 after 14 hot visits at RotationSlice 8 and HotRescanMs 250, about four seconds; the pool path registers every name tiered or not, so a re equip never frees the room; pool_regions.json sits under Reloaded/User/Mods/<id>. What decides it live is the suffix site count from the bare launch. The first Info line "The kill counters are live on the equip cards" reports only the first latch, which on a warm start is the sidecar seeded subset, so read the LAST "pool coverage re-established" Debug line in that launch's livingweapon.log (total slots minus paint spots is the suffix count), or the flight archive's coverage record, which carries it as suffix=N and survives the one launch log rotation; 1024 means the room was full, 0 means no name copy was found, anything between means the names were painted somewhere the lists do not read.)
- [LW-436] 2026-10-17: On the small list icon a +1 green weapon (speed) is hard to tell from a +3 one, and the yellow guns' +1 is very faint. The owner judged both acceptable for 2.4.0. After the release, try spreading the tier steps so +1 and +3 look different on the light colors, and try strength 2.0 on the yellow lane. Owner judges by eye in game.
  (Tech: light hues saturate early against the cream panel #FFFAE8, so the dark green rim reads solid by +1; the lever is per tier width or a per lane strength override in tools/bake_glow_icons.py, not a darker color, which merges with the icon outline.)
  Second sighting 2026-10-18 15:13, owner on the Cleaver card at +1: the PA ring read orange to him. Measured from the shipped ring pixels (tex decoded through FF16Tools, ring = pixels outside the plain art): the PA ring is hue 0 at every tier and the HP ring hue 24, so the hue is right; the +1 ring ships at mean alpha 91 of 255, which over the cream panel lands PA at hue 7 and 39 percent saturation (a washed salmon) and HP at hue 27 and 41 percent, twenty degrees apart, while +3 (alpha 183) lands PA at hue 2 and 72 percent, plain red. So the +1 step is where the red and red orange lanes can be mistaken for each other, the same lever as above.
- [LW-437] 2026-10-17: Rename the LivingWeapon/glow_icons folder to GlowIcons so it matches every other folder's ProperCase naming. Players never see it, and the mod looks the folder up by name, so it waits until after the 2.4.0 release and stays out of the release scope. After the rename the glow smoke check runs again.
  (Tech: about 55 references in 19 files, including IconGlow.Store, IconGlow.StoreFile and IconGlow.Manifest, LivingWeapon.csproj, tools/pipeline.ps1, bake_glow_icons.py, bake_extended_icon_parts.py, recolor_icons.py, two probes and the docs; BuildLinked's clean step removes the old deployed folder.)
- [LW-435] 2026-10-17: The axes and flails restored into the game do not grow. They never count toward a growth lane, so they earn no kill tiers, no stat growth and no glow ring. Give each one a growth lane so they become living weapons like every other weapon. This is a new feature, so it waits until after the 2.4.0 release and stays out of the release scope.
  (Tech: they are noGrowth by LW-351, so bake_glow_icons.py weapon_ids and the runtime skip them; scope covers picking each weapon's lane in data/items.json, growth tiers, the glow bake, and whether signatures apply.)
- [LW-428] 2026-10-17: A weapon icon sometimes fails to save into the mod folder and the mod quietly retries, so a player never sees a blank icon, only a WARN in the log; until now the log could not say why. The failing step is the loose icon file swap in the mod folder (temp file, then replace), not a pac lock, and its exception used to be thrown away. Sightings on 2026-10-17: icon 19 at 10:44:56, icon 79 at 12:48:50, and a burst of 12 icons at 17:12:10 on the first launch after a deploy; every one retried fine (109 then 11 then 1 of 121). The log now names the exception type and the Win32 code, once per icon file per launch; 32 means another program holds the file and 5 means access denied. First live code 2026-10-17 at 18:32:33 on prod: icons 14 and 39 (card) failed with IOException, Win32 error 1175 (Windows could not move the old file aside during the swap, so some program held it open without delete sharing), and both landed on the next pass about a second later. That is neither 32 nor 5, so the holder still has to be named before a fix is picked, and any change to the swap itself goes through the full pipeline. (Tech: IconGlowWriteError via IIconGlowStore.LastWriteError, FileIconGlowStore.AtomicWrite, the (detail: ...) tail on the write and snapshot warns.)
- [LW-429] 2026-10-17: During the sitting 2 recheck a Gariland battle logged the WARN `A fielded enemy is no longer visible in the battle band; its kills may go uncredited.` at 12:40:25; the battle went on to credit the next kill normally and no kill was lost that the owner saw. The launch's log rotated away before it was saved. Reproduce on a New Game Gariland with the log kept, and decide whether the warning is a real band coverage hole or a false alarm on the story's mid battle unit change. Owner ruling 2026-10-18, this warning has shown up for a long time with no kill ever lost to it, so it is out of the 2.4.0 scope and waits for 2.5.0.
- [LW-417] 2026-10-17: The HP growth code file has grown past the size where a reader can hold it in their head (253 lines). Split the Max HP lane into its own file under the new file comment cap, keeping or rewriting the LW-327 edge prose at the old lines 112 to 138, never dropping it. Owed since LW-327, grew again with LW-416.
- [LW-418] 2026-10-17: During an opening cutscene the battle tape goes blind, because the event watcher only samples units on field frames; that blind spot made the LW-416 read a two hour job instead of a five minute one. Log one baseline line at the first field frame saying how long the watcher was blind, or add a max HP only change event.
- [LW-422] 2026-10-17: Latent shape seen on the LW-416 tape: if the Max HP hold ever captures the 400 of 400 placeholder body during a cutscene, the later real fill matches neither its natural nor its target and the hold goes quiet for that battle. Not seen live (the locate waited for the real body); worth a test and a log line.
Rows are ordered by priority, highest first (full re-sort 2026-08-24, owner directed).
A new row still lands here in the session it surfaces; slot it where its urgency
belongs rather than at the bottom.

- [LW-405] 2026-10-16: The card paint cache file is stamped with the build the mod was made for, not the build the game actually is, so since the mod can now run on a build it does not know the stamp no longer tells builds apart.
  Found in the LW-397 review. PoolRegionSidecar.Save writes the expected constants and Load compares
  against them (PoolRegionSidecar.cs 45, 72), never the observed header; the per-region reverify in
  PoolLocator.WarmStart (51 to 54) plus the forced full restart are the net today. Fix: plumb
  LaunchGuard's latched observed pair into Save and Load (Display is built before tick 1 latches it,
  so a Func seam, not a value).
- [LW-409] 2026-10-17: A player mentioned "the double cast thing" as if it were a known bug, and nobody knows what they meant; find the comment, ask, and either file the bug or close this.
  Owner recalled the comment 2026-10-17 ("figured there'd be updates coming just didn't know if
  you'd spotted the double cast thing"). No feedback row, no ledger row, no changelog entry
  mentions a double cast. Thread found 2026-10-17 (Discord, 2026-04-02, Tal, EpicBrownie, Nyzer):
  other modders explain that the game's dual wield hit count routine, the one that sets two
  attacks when a unit dual wields, is hardcoded by weapon formula id, so a custom formula does
  not get the second strike and a cast formula fired through it casts twice. Why it touches us:
  the rebalance moved many weapons onto formulas that routine may not list (45, 47, 48, 67, 69,
  99, and the formula 4 magick guns and rods), and the twin weapon grant hands out Dual Wield.
  So a living weapon under Dual Wield may swing once instead of twice, or a formula 4 gun or rod
  may fire its spell twice. Live check first: Ninja with Dual Wield and two Blaze Guns, then two
  Hushblades, count strikes and casts; then read the routine Tal names.
- [LW-415] 2026-10-17: Every write the mod makes into the game should say which of three buckets it belongs to, battle, process or save, so a part that dies mid action can never leave the save changed without someone knowing who puts it back.
  Owner design 2026-10-17: a required WriteScope argument on the Mem write funnel (Battle,
  Process, Save, no default, so a new write site that names no bucket does not compile); the
  Save scope takes a recovery reference (a sidecar, a re assert edge, HeldByDesign with a
  reason, or NoRecovery) and a contract test lists every NoRecovery and HeldByDesign; a range
  floor in Mem refuses a Battle scoped write into a save backed range (roster, the persisted
  static tables) and logs once, with a test driving each module through the fake memory and
  asserting no refusal fired; a DocsContractTest holds the per part bucket table in
  docs/MECHANICS.md in lockstep with the Save scoped sites in code; one line in
  docs/PATCH_REANCHOR.md for the case where a Process region becomes save backed after a game
  patch. Fold rulings: the orchestrator folds docs/research/WRITE_SITE_AUDIT_2026-10.md's 129
  site rows into the per part table inline; a skeptic verifies bucket 3 and every "no
  recovery" row against the code; buckets 1 and 2 stand on the audit's word. Lands after
  LW-406.
- [LW-394] 2026-10-16: Three weapon commands mark an ability as learned in the player's save and never unmark it, so no safety switch can undo them if they ever land in the wrong place; decide whether they should clean up after themselves.
  Barrage, Shadow Blade and Provoke each set one learned bit in the roster row (Barrage.cs 247,
  ShadowBlade.cs 199, Provoke.cs 194) and hold it by design so the command stays usable. The audit
  found these are the only save-persistent writes with no restore path; the Ramza row landmark is
  their only protection under any guard design. Options: clear the bit when the grant ends, or keep
  the hold and accept the risk in writing. Surfaced by the 2026-10-16 write-site audit.
- [LW-396] 2026-10-16: A Warbrand kill on the enemy's own turn went uncredited in the owner's live pass; look at whether the enemy-turn rule is too strict for weapon damage that lands during the enemy phase.
  Live pass 2026-10-16 11:02 (LW-388 port): the first Warbrand kill was refused by the existing
  enemy-turn rule (KillTracker.Corpses, UntrackedReason.EnemyTurn) while the second was credited.
  Pre-existing behavior, not a regression; the rule exists to stop enemy-on-enemy deaths from
  paying a player weapon. Worth a flight-tape read to see whether a counter or reaction kill is
  being thrown away.
- [LW-389] 2026-10-15: The live memory probes in tools/probes still talk to the game at the old fixed address; teach the shared harness the real module base so probes work again on the unprotected game.
  battle_cheats.py and friends read pins from tools/lib/offsets.py (preferred base) and RPM the
  game externally; with ASLR on they must add the live base (EnumProcessModules or psutil on
  fft_enhanced.exe) in one shared place, not per probe.
- [LW-390] 2026-10-15: With the copy protection gone the game's code is plain and debuggable for the first time; re-examine every wall the ledger blamed on Denuvo and decide which to reopen.
  Candidates: crit-hit engine byte, external code probes, the ENTD blueprint hunt, the callout
  orchestrator's VM-frame callers, the may-cast relocation past 255 and proc rates. Engine
  design walls (art welded to id, 4th menu row, entice pool relocation) stay walled. Every
  0x15xxxxxxx address (the old protector blob) is dead forever. Owner decides the order.
- [LW-402] 2026-10-16: New weapon signatures still have to borrow an existing ability's number, because the game only knows 512 abilities; another modder has shown on this exact game build that brand-new ability numbers past 512 can be taught to it, so find out what that would buy us.
  Not for 2.4.0. Scouted 2026-10-16 from ZeroDS's FFTModLoader source (github.com/andersongz0/
  FFTModLoader, ContentExpansionRuntime): abilities 513 to 523 served from a mod-owned pointer
  table by a stub in front of the ability getter (RVA 0x275A78) and its secondary-record getter
  (0x2BB060), the action-bound immediates at 0x309B85 and 0x309CFB lifted 368 to 1024, the
  ability data table 0x787F80 relocated 4096 to 8192 bytes by rewriting 31 operands, and a
  private formula dispatch. Same exe SHA as ours. Independent work, not a copy of ours, and the
  consumable path only; whether a weapon command (the Barrage and Shadow Blade lane) can ride
  the same route is the question. Walled items that would reopen: the hijack recipe's
  rename-and-re-effect limit, the parked Bloodpact channel, every "needs its own ability" idea
  in docs/MECHANICS.md. Starts as a probe, not a build.
- [LW-403] 2026-10-16: The file that remembers how many of each new weapon the player owns lives beside the mod, so moving saves to another computer or losing the mod folder loses the counts; find out whether the counts can live inside the save file itself.
  Not for 2.4.0. Corrected 2026-10-17: the sidecar is ALREADY keyed per save (LW-353, a hash of
  the save header taken when the game serializes), so slots never share counts and an older save
  of the same slot gets its own; the first version of this row said otherwise and was wrong. What
  remains is where the file lives. Lead from WotL Restoration 2.0.7 (DanaCrysalis, read 2026-10-17):
  her mod writes its 55 extra counts INTO the save struct, at the block the game-variables pointer
  0x14078DAE8 points to plus 0x2FC, 59 bytes behind a four byte magic header, mirrored every 50 ms
  from a background thread and once more on the serializer hook 0x140218F78 (the same entry our
  SaveEdgeHooks use). If that region is truly spare and survives the load sanitizer, our counts
  could ride the save file and the sidecar becomes a backup. Verify first that vanilla saves hold
  zeros there and that no routine clears it; her ItemSites.cs and ItemExpansion.cs are the map.
- [LW-404] 2026-10-16: Every time the mod needs to step into the middle of one of the game's routines it gets a hand-built, one-of-a-kind patch, which is slow to write and easy to get wrong; adopt one general "stop here, hand me the registers, let me change them, carry on" tool for new sites, and keep the hand-built fast paths where they are.
  Not for 2.4.0. Today a new code-site hook means a bespoke byte layout in ThunkStub.cs or a
  one-off like SwingIdFallbackHook, each with its own register contract pinned by tests.
  gibbed's WhatSpriteLimit (github.com/gibbed/WhatSpriteLimit, MidHook.cs, a port of
  safetyhook's mid-hook) shows the cleaner shape: one fixed stub saves the full register set
  and flags into a context struct, calls a C# handler marked UnmanagedCallersOnly, the handler
  reads and rewrites registers and may set a new rip, and the stub restores and resumes or
  redirects. Eleven of his hooks share that one stub. Scope: add it as a SECOND tool in Memory/
  beside ThunkStub, not a replacement; the ten accessor leaf stubs and the list builder stay
  hand-laid because they run per list entry and a full context save there is a measurable tax.
  Decide whether to emit the stub as fixed bytes (no new dependency) or take Iced as he does.
  First customers: the LW-390 reopen candidates and the LW-402 probe. Done means: one new site
  hooked through it with a unit test on the context round-trip and an owner live pass showing
  no stutter.

- [LW-411] 2026-10-17: WotL Restoration and Living Weapons both add items past the game's 261 and both claim ids 261 to 267, so a player can only ever have one mod's new items; work out with its author how the two can share the space.
  Not for 2.4.0. Read 2026-10-17 from WotL Restoration 2.0.7's code: her ItemExpansion hooks the
  same nine accessor entries (0x1402B8BCC through 0x1402B8EBC), re-points the same bag array
  0x1411A7C00 and the same three order-chart slots, and widens 44 caps, 30 of them ours. Her
  verify step refuses when our bytes are already there (ours arms in StartEx, hers after all mods
  load), so today ours wins and her 40 items vanish with a log line. Options to put to Dana:
  an id range split (her 268 to 313 never collide; only 261 to 267 do), one side chaining to the
  other's accessor stubs instead of refusing, or a shared cap-break library both load. Also tell
  her the two list caps her bounds table skips (0x140288CC3 and 0x14028631A, the 149 and 150
  stack-buffer limits) are the past-149-kinds crash Tal described. Related: LW-403 (her in-save
  count tail), docs/COMPATIBILITY.md WotL Restoration section.
- [LW-386] 2026-09-01: The Poacher's Den is handing players items the mod never meant it to, and one
  player saw four katanas where two dragons' carcasses belong. Plain language: a Discord player on
  the public 2.3.3 build poached Blue and Red Dragons and the Den listed and produced Asura, Kotetsu,
  Bizen Osafune and Murasame instead of the dragons' rewards, while the regular Dragon and the pig
  line behaved. That exact symptom is NOT reproduced yet and its cause is unknown; what the same-day
  audit DID prove wrong in 2.3.3 is listed below, and it is player-visible on its own.
  - PROVEN wrong in the public build (data audit 2026-09-01, docs/USER_FEEDBACK.md 2026-09-01):
    five carcasses hand out a weapon of a different TYPE than the carcass card promises, because
    2.3.3 recategorized the seven axes and flails in place: Grenade rare gives Bloodlash (a knife;
    the card says Flail of Flame), Wisenkin common and rare give Terrastaff (a pole) and Ravager (a
    knight sword; the card says Battle Axe and Giant's Axe), Minotaur rare gives Sunderer (a knight
    sword; the card says Slasher), Hydra rare gives Sasori (a katana; the card says Scorpion Tail).
    HEAD already restored those seven ids (LW-351), so 2.4.0 cures this by shipping; the row exists
    so the 2.3.3 player-facing fact is on record and the release notes can say so.
  - PROVEN wrong in every build: 39 of the 96 carcass descriptions name the VANILLA reward ("Used
    to produce a length of cashmere") while the Den hands out the mod's renamed item (Bindsilk
    Veil). The mod never patches poachitem.en.nxd. Decide: patch the 39 descriptions the way
    item.en.nxd is patched, or leave and FAQ it.
  - ELIMINATED for the katana symptom: vanilla poach data is correct (keys 87 to 90 produce items
    120, 57, 239, 105); the mod ships no poach or job text table; JobData.xml touches no monster
    row; the nxd bake audit is green; the owner's running modded game holds the vanilla produced
    ids in all three in-memory copies of the poach sheet
    (tools/probes/lw386_den_produced_probe.py table, read-only). The tag's ItemData.xml,
    ItemWeaponData.xml, JobData.xml and item.en.nxd all differ from HEAD, so a 2.3.3-tag deploy
    is the honest repro bed if the owner's HEAD build does not reproduce.
  - NEXT (owner-driven, two minutes): poke Den store keys 87 and 89 to 1 with the probe, open the
    Den, read what the Blue and Red Dragon lines show and produce, restore. Then ask the player
    for mod version, other mods, a Den screenshot and livingweapon.log. Related to [LW-358]
    (the earlier no-poach report) only by subject; different symptom, different gate.
  - (Tech: carcass store u8[96] at Offsets.PoachStoreBase 0x1411A7A1B; PoachItem sheet rows are
    0x38 bytes with the produced item id at +0x2C, three live copies found 2026-09-01 at
    0x15c62xxxx and two 0x4c9bxxxxxx heap mirrors; the tag build is v2.3.3 = cb14be3b; the
    recategorized rows are ItemData.xml ids 48, 49, 50, 67, 68, 69, 70 at that tag.)

- [LW-384] 2026-09-01: The Eclipsebolt's death mark lands on the slow three turn countdown instead
  of the one turn its tier three signature promises, and once it does there is no second chance.
  Owner-observed 2026-09-01 07:10 in a live battle, and the log names the refusal outright:
  "a level 27 enemy's Doom appeared outside the Eclipsebolt wielder's own action (lastActed
  mainHand id=22); its countdown (3) is left alone". The hastening was armed at the time (the
  tier three line fired at 07:10:09), so the signature was awake and simply decided the mark was
  not its own.
  - Why it is a real defect and not just bad luck, from the code rather than the tape: EagleEye
    records its own baseline BEFORE it checks attribution (EagleEye.Hasten writes
    _wasDoomed[addr] = doomed, then tests the gate). A mark is only ever hastened on a RISING
    edge, so the moment one sweep refuses it, that edge is spent and the mark can never be
    hastened again, even if the wielder's acted period is still open a millisecond later. There
    is no retry anywhere in the path.
  - The gate is also a race by construction. It reads KillTracker.LastPlayerMainHand and the
    global Acted byte at SWEEP time, not at the moment the mark appeared, and the sweep only runs
    on every sixth engine tick (about 200ms). Anything that closes the acted period or lets
    another player unit act inside that window makes the mark look foreign forever.
  - Honest limit on the evidence: the log does not independently prove this particular Doom came
    from the Eclipsebolt, because no Eclipsebolt damage event sits next to it (the surrounding
    traffic is all movement). The owner saw the shot. Whether or not this one instance was the
    bow, the spent-rising-edge flaw above is real on its own reading.
  - Beware the opposite failure when fixing. This gate exists because of LW-95: without it, Mortal
    Coil (id 8) procced Doom while a tier three Eclipsebolt merely sat fielded and EagleEye
    hastened it anyway. A fix must stop under firing without bringing that over firing back, so
    the answer is probably to keep re-testing an unhastened mark for a few sweeps while its
    countdown is still above target rather than to widen who counts as the actor.
  - (Tech: LivingWeapon/Signatures/EagleEye.cs, the Hasten sweep. Doom status at base+0x49 mask
    0x01, countdown at base+0x59, per LIVE_LEDGER doom-status-bytes. Same family as the known
    last actor attribution traps, memories actorptr-dwell-semantics and stale-latch-stamp-fix.)
- [LW-320] 2026-08-25: Every weapon's power audited against how it is obtained. Owner verdict 2026-10-19 on release day: accepted as it stands for 2.4.0, the Warbrand and Ravager keep their shop power, revisit after the tag. Demoted from Now on 2026-08-27 late to seat
  LW-353 (per-save bag counts); status unchanged, AWAITING-LIVE, the owner's sign-off on the
  grid rulings is still the only thing outstanding; every line below is the Now row's text verbatim.
  - A weapon's price should include the effort of getting it. The grid's obtain column
    splits the catalog 88 shop weapons versus 33 earned ones (Midlight's Deep treasure
    hunts, rare poaches, story steals, character joins), and the first look already found
    the disease: of the twelve highest-WP weapons, ten are earned and TWO are shop stock,
    the Warbrand (WP 15, tier 5) and the Ravager (WP 15, tier 4), a shop shelf selling
    treasure-grade power, exactly the owner's "steady then boom" playthrough moment.
    Feeds LW-318's fairness formula directly and sits before it on purpose. Owner moved
    it ahead of LW-322 in the working order, 2026-08-25.
  - (Tech: authority is docs/living_weapon_grid.csv's obtain column; cross with WP, tier,
    riders and the new baked grows lanes; candidate output is an obtain-vs-power chart
    plus per-weapon rulings recorded in the grid, enforced the way the grows column is,
    an analyze.py lockstep check once the rulings are locked.)
  - Audit built and adversarially verified 2026-08-25; the chart is in front of the owner
    and every ruling is still his. The full 121-weapon cross confirmed both named spikes:
    the Warbrand is the ONLY shop weapon that strictly outguns every hunt in its class,
    and three more shelves tie their best hunt. One new violation surfaced (the shop
    Graviton beats the Materia Blade on every gate axis; its Limit role and MA lane are
    the counterweight) plus three hygiene piles: nine stale identity claims, seven ghost
    on-hit ability ids no card text mentions, and grid onHit cells missing the may-casts
    the cards advertise. (Tech: instrument tools/probes/lw320_obtain_power.py reuses
    analyze.py's dominance vocabulary and mirrors lib/flavor.mechanics() for on-hit
    labels; dataset tools/probes/lw320_obtain_power.json; the grid rulings column and
    its lockstep gate land after the owner rules.)
  - First owner ruling executed 2026-08-25: the seven ghost procs are re-advertised on
    the cards now rather than riding LW-322. Git archaeology proved them deliberate
    (thematic may-cast pass 9433f22, 2026-06-02) and silently un-advertised five days
    later by the generated-description rewrite (65b2a88) whose proc-name map never
    learned the ids. Five of seven cards now say their cast (Riposte, Claymore,
    Scoutbolt, Huntress, Greenwood Pole); Kiku-ichimonji and Dragon Whisker cannot
    without breaking the kept-vanilla-name text rule, spun off as LW-328. Restart-only;
    the owner still owes one live read confirming a proc fires (a Riposte swing).
  - All remaining rulings executed 2026-08-25 (owner agreed to the chart's suggestions
    in chat): the Warbrand keeps its crown but gets it LAST (tier 6, shelf moved to the
    final shop unlock; the owner picked this variant after the WP cut to 14 proved
    impossible, the gate showed Arcanum would strictly dominate a riderless zero-evade
    14); the Ravager's shelf moves off its Chapter 2 debut to Chapter 4 (tier 5, power
    kept); the Materia Blade, the three shelf-matchers, and the two riderless capstone
    hunts are accepted with reasons; nine stale identity claims fixed; the grid's onHit
    column synced to every real proc (18 cells, including the Huntress cell that said
    Arm Shot while the data ships Leg Shot) and a new ruling column records every call.
    (Tech: items.json shopOverride Chapter4_KillElmdor / Chapter4_Start; grid tier
    cells moved in lockstep; dominance + grid-sync gates green; the rulings lockstep
    gate waits until rulings lock after LW-318. Owner still owes: the live spot-reads,
    both shelves stocking at their new chapters, and the Riposte proc firing.)
  - Done means: every earned weapon is worth its hunt (a rare poach beats its shop peer
    on some axis), no shop weapon spikes into treasure territory, each violation gets an
    owner ruling recorded in the grid, and any resulting number changes pass the
    dominance gate.
  - Verify: the obtain-vs-power chart reviewed by the owner, every ruling signed off by
    the owner in the grid, analyze.py green after any retunes, and the changed weapons
    spot-read live if stats moved.
- [LW-357] 2026-08-27: Reconfirm whether an equip icon can change WHILE the game is running,
  owner-ordered after tonight's rim read: the mod's working rule says a drawn icon never
  refreshes mid-session (rims show at the next launch, LW-340's physics), but the owner is
  fairly positive it was proven the other way, and the ledger agrees the refresh DID happen
  once. The record: on 2026-08-16 one tab round-trip refreshed a drawn icon; on 2026-08-25
  and 2026-08-26 every eviction tried (tab round-trip, equipment reopen, world map, save and
  load, title reload, a full battle) kept the first-draw art on the same game binary; the
  file write and the first draw stayed dependable throughout ([live-icon-repaint] and its
  contradiction row). So the honest state is "it works under a condition nobody has named",
  not "it never works". Outside the extended-inventory arc. Shape: one scripted session
  that replays the 2026-08-16 recipe EXACTLY (a fresh launch, the patch minutes after boot,
  one tab round-trip) against the 2026-08-26 shape, changing one thing at a time (time since
  boot, whether the tab was drawn before the patch, VRAM pressure), until the discriminator
  shows; the win is a rule the runtime can drive (LW-336's sync would then refresh the rim
  the moment a tier is crossed). Cite the exact probe (tools/probes/live_icon_patch_probe.py)
  and restore the pac explicitly after (it persists across relaunches).
- [LW-335] 2026-08-26: A handful of chosen weapons get their OWN inner ring color so
  they stand out from the rest of their lane. Owner intent stated at the Atlas sitting
  ("I fully intend on changing the inner color of a handful of weapons to help them
  stand out; that's another task though"): the lane color stays the language, a few
  special weapons speak louder. Which weapons and which colors are the owner's picks.
  (Tech: mechanism is a per-id override map consulted before LANE_GLOW in
  bake_glow_icons._glow_variant, the same precedence shape rims.json already uses for
  per-weapon alphas; the mesh finding from the same sitting applies, a ring reads best
  when its hue sits far from the weapon's own art hue. Audit candidates from the same
  night, rings that dissolve into their own art: Frost Kodachi 13 green on green the
  worst, Save the Queen 34 orange on gold, Terrastaff 48 violet on violet, Blaze Gun
  75 gold on gold, Gloomfang 3 mildly; Excalibur 35 proves the lane hue itself is fine
  on contrasting art. Render: tools/probes/lw319_suspects.png.)
- [LW-334] 2026-08-25: Weapon icons never show their glow rims in game even though the
  splice itself works. First real live read of the LW-295 icon half: the pac on disk
  provably holds the tier 3 icon bytes (checked directly this session) while the equip
  list keeps drawing plain art, and closing and reopening the menu does not refresh it.
  Likely the game caches icon textures at first draw, or reads the pac only at boot
  before the background splice lands (the splice needs a minute or two after launch to
  index 242 icons against a 64MB pac, and the pac is rebuilt plain at every launch).
  As things stand the LW-295 icon mechanism live pass FAILS; rethink the display path
  before LW-319 rebakes the rims in lane hues. Evidence: this session's livingweapon.log
  (manifest load 20:01:21, zero warns) plus the direct pac byte check.
  ANSWERED 2026-08-26 by re-running the icon patch probe by hand with the owner watching,
  then CORRECTED the same hour on the owner's skepticism: the refresh of an icon the game
  has already shown is UNRELIABLE, and nothing may be built on it. Tonight menu round
  trips, save and load, even backing out to the title screen all kept showing stale art
  while the file on disk said otherwise; on 2026-08-16 the very same recipe worked after
  one tab hop, and the exe's build fingerprint proves both sessions ran the SAME game
  binary, so the first "the game patch changed it" conclusion was wrong and is retracted.
  Something about session state decides whether the game ever rechecks a drawn icon, and
  that something is not yet identified. Writing the pac still works and an icon never yet
  shown this session picks up the new art on its first draw, so the splice was never
  buggy, it is simply too late and too optional: by the time it lands the player has
  usually seen the plain icons. Correct-at-boot via the launch merge is the display path;
  treat drawn icons as restart-only. (Tech: probe live_icon_patch_probe.py stages 1 and
  2, owner eviction ladder all stale; PE key 0x6A5EA53C unchanged since the 1.5.2
  re-anchor 2026-07-24, commit 9959821, so all three sessions share one binary; ledger
  rows [icon-refresh-unreliable] and the updated [live-icon-repaint] carry the evidence
  and the eviction-condition hunt's next probes; successor design captured as LW-336.)
  SAME NIGHT, TWO MORE READS, then the owner PAUSED the hunt: a full battle enter and
  exit also failed to refresh a drawn icon (the heaviest scene load in the game), and
  the launch merge turned out to be INCREMENTAL, meaning a pac patch survives
  relaunches until a deploy changes the matching loose file, so probe patches must
  always be restored by hand and never trusted to a relaunch. Silver lining recorded
  in the ledger: splice work persists across restarts too in a deploy-free install.
- [LW-337] 2026-08-26: Torso gear gets the recolor treatment so body armor reads as
  distinct items the way the helms already do; owner queued 2026-08-26 while defining
  the next release. This is the owner's restatement of the EXISTING legacy re-pass
  seats, not new work: armour is LW-218, clothing LW-219, robes LW-220; this row sets
  the quality bar (the helm standard) and the sequencing, those rows hold the items.
  The LW-313 seat (the owner's new icon path, still unwritten) gets spelled out first
  so the family rides the right pipeline.
- [LW-338] 2026-08-26: Accessories get the recolor treatment to the same helm-quality
  standard; owner queued 2026-08-26 alongside LW-337 and the same shape applies: it
  restates the existing seats (shoes LW-221, armguards LW-222, rings LW-223, armlets
  LW-224, cloaks LW-225, perfumes LW-226, hair adornments LW-217) rather than opening
  new ones, with LW-313's path spelled out first.
- [LW-339] 2026-08-26: The in-battle weapon sprite recolor gets one extensive live test
  pass hunting edge cases before the release calls it done; owner queued 2026-08-26.
  Known suspects to script into the pass rather than rediscover live: counter attacks
  still swing vanilla (LW-310), the four ex flails draw broken garbage (LW-312), two
  weapons sharing one of the 13 palettes can clash during a parry (roughly 8 percent of
  parry exchanges per ledger row [per-weapon-colour-by-turn-repaint]), the palette map
  carries at least one proven-wrong entry (LW-305), and shields stay vanilla entirely
  (LW-302, its own hunt).

- [LW-342] 2026-08-26: The test suite silently means "production flavor only": building
  the tests with the DEV knob turned on fails 89 of them at today's HEAD, and nothing
  anywhere runs that combination, so the dev flavored DLL ships gated by tests compiled
  for the other flavor's thresholds. Found as bycatch during the LW-336 verify rounds
  (the implementer proved the 89 are pre existing via a throwaway worktree at HEAD).
  Decide the honest posture: either the dev test build becomes a gate somewhere
  (BuildLinked dev runs dotnet test with the LwDev property), or the suite documents
  that tests are prod threshold only ON PURPOSE and a tripwire keeps dev only logic
  out of tested paths. (Tech: dotnet test -p:LwDev=true, 95 failures, 89 pre existing,
  6 the new IconGlow tests written to the file's own prod threshold convention.)
- [LW-349] 2026-08-27: Weapon battle colors may not be walled after all: the probes that
  declared "you cannot pick which palette a weapon uses" read the two-byte sprite/palette
  table one item off (base 0x140785CF2 instead of 0x140785CF0), so every write and every
  watch landed on the neighbouring weapon. The live code shows two real readers of that
  table, the sprite composer for the drawing and a copy-protected color loader for the
  palette nibbles, both through the accessor thunk 0x1402B8E60. One live poke on a vanilla
  sword (tools/probes/lw346_sprite_pair_poke.py, undo included) settles it; if the swing
  follows the record, LW-289's wall row is overturned and per-weapon color becomes a data
  fix. Surfaced while chasing the Moonblade's punch animation (LW-346 item 4).
  RESULT 2026-08-27 00:50-00:56, owner eyes, mid-battle, no relaunch: BOTH bytes are live.
  Save the Queen rewritten to Warbrand's drawing with Chaos Blade's palette swung as a purple
  Warbrand on the very next swing; rewritten to Warbrand's own record it swung in Warbrand's
  steel; restored after (screenshots tools/probes/lw349_sprite_pair_*_34.png, ledger row
  [weapon-sprite-pair-drives-swing-art], owner PROVEN flip pending). What this buys: which
  of the 16 palettes a weapon draws from is one byte per weapon in a plain writable table,
  so the per-turn repaint the WeaponPalette runtime does today can become "assign each
  weapon its own palette slot at boot" plus one repaint per slot, and the 13-palettes-for-127-
  weapons grouping stops being the game's. Shape of the build: a guarded boot write of byte 0
  per living weapon (from a new items.json field), the LW-305 map re-derived from
  0x140785CF0 (the old map read the neighbor), and a LaunchGuard landmark on the table.
- [LW-350] 2026-08-27: Enemies cannot be handed a new weapon id through the encounter table,
  so the mod itself must dress them after they are built. The classic encounter record keeps
  item ids as single bytes, and a Knight given id 261 there spawned empty-handed (journal
  2026-08-27 02:45); this blocks steal, Rend Weapon and battle spoils of new ids until a
  runtime loadout write exists. Shape: a per-encounter loadout list in the same data layer as
  LW-346 item 7, applied once per unit right after enemy construction (the construction edge
  the Body Double arc already reads), verified by the unit's status screen and one steal.
  Surfaced by the LW-346 enemy test; needs the port (LW-346 item 8) first.
- [LW-361] 2026-08-30: The restored axes and flails get a real redesign of their own.
  LW-351 brings the seven vanilla weapons back exactly as the base game had them, which
  leaves them the only items without the mod's build-diversity treatment; the owner wants
  them redesigned as their own task AFTER the restore ships. Until then LW-351's spec must
  decide how the vanilla rows pass the no-domination gate (an owner-ruled exemption or
  interim tiers, decided there); this row is where the real designs land, through the
  usual grid csv first, then items.json, then the gates.
- [LW-364] 2026-08-30: Find out where the game really reads an extended weapon's reach from,
  so a range-1 pole (or any reach its donor lacks) becomes possible. Plain language: an
  extended weapon strikes as far as the vanilla weapon it borrows its engine from, no matter
  what its own row says; the Terrastaff proved it on 2026-08-30 (row said 1, it hit two
  tiles away, its donor is a range-2 pole). Two explanations fit and the one sighting cannot
  separate them: the game reads the donor's own row through the sibling accessors, or the
  Lunging delivery class fixes the reach by itself. Done looks like: a live probe that tells
  them apart (a donor swap or a flags swap on one item, owner-observed), the answer recorded
  in LIVE_LEDGER, and either a per-item reach stub or a guide rule saying reach is
  donor-bound for good. Until then generate.py's check_extended_range keeps every row honest.
  (Tech: the weapon-stat thunk 0x1402B8C74 already serves the mod's own 8-byte row whose
  byte 0 is the Range the game ignored; suspects are the two-byte sibling rows keyed by
  (id minus range base) behind 0x1402B8CD4 / 0x1402B8D3C / 0x1402B8DA0, which answer for
  the clone donor today.)
- [LW-366] 2026-08-31: The Sunderer's Bulwark has not been seen firing at its new id 263 (264
  before the Moonblade's removal later that day slid every extended id down one). The
  owner swung the Sunderer on the stage-2 pass and saw no Bulwark, but that weapon held zero
  kills (the tally was zeroed for the pass and the move carried zero across), and Bulwark only
  wakes at the third growth tier (15 kills on a production build), so at tier 0 staying dark is
  the correct behavior, not a fault. What is owed is the real check: a Sunderer at tier 3 on
  the 263 build fires Bulwark exactly as the id-50 one did. (Tech: Bulwark.SundererId = 263
  since the Moonblade's removal, 264 from 0e612454 before; the signature gates on tier >=
  Signature.AtTier (3); the kill tally migration moved id 50's count to the Sunderer's id
  (TallyMigration), which was 0 on the owner's rig;
  a dev-flavored BuildLinked seeds every tally to max tier and is the quickest way to see it.)
  Note 2026-08-31: the Moonblade removal slid every hand word down one, so the unit that wore
  the Sunderer now holds a Warbrand and the owner's rig owns no Sunderer at all; one must be
  bought again (Chapter4_Bethla window) before this check can run.
- [LW-369] 2026-08-31: Brand-new weapons should be able to carry equip bonuses of their own
  (PA+1, Haste and kin) even though the game's bonus table is full. Plain language: every item
  points at a row in the game's ItemEquipBonusData table for what wearing it gives you beyond
  its base numbers (stat riders, Move and Jump, innate statuses and abilities); that table caps
  at 84 rows and vanilla wastes most of them on duplicates (three items with the same PA+1 get
  three rows), so a new weapon can reuse an existing row today but cannot get a row of its own,
  and consolidating vanilla rows is unsafe because redefining a shared row changes every item
  that points at it. Asked by the LW-344 partner author (Jade Knightblazer, 2026-08-31) for his
  eight planned weapons. Shape: relocate the table into a buffer the DLL owns and re-point the
  game's reader at it, the way LW-346 relocated the item catalog, so rows past 84 exist for
  extended items; the alternative he floated, applying bonuses from meta.json at runtime, only
  reaches PA, MA and Speed (the growth engine already holds those) and cannot reach Move, Jump,
  innate statuses or innate abilities, which the game reads from the table at equip time, so it
  is the fallback, not the plan. Done looks like: one extended weapon with a NEW bonus row (a
  stat rider plus an innate status) showing both on its card and in battle, the loader
  untouched, the guide's rider paragraph updated. (Tech: the resident bonus table starts at
  0x14080FEA0, right after the 128-row item-stats table (Offsets.ItemStatsRows); an extended
  item's catalog record already carries an EquipBonusId (ExtendedRecords.EncodeCatalogRecord),
  and generate.py places new rows only in the free slots 74-79 plus six audited vanilla rows
  (memory equipbonus-row-override-audit); the game's readers of the table are unfound, so the
  first step is the xref sweep that found the shop-flags table, lw346_xref_scan.py.)
- [LW-370] 2026-08-31: A partner mod's brand-new weapons should get their names and card text
  from a plain file, with this mod's card painter working on them, and without the partner ever
  building an item.en.nxd. Plain language: the painter (the live Kills counter and the "+2" name
  suffix on the equip card) only overwrites a fixed-width slot that this mod's name bake reserves
  inside each item's text row, so an item whose text row lacks the slot gets no painting; and a
  brand-new item has no text row at all until someone adds one. Asked by the LW-344 partner
  author (2026-08-31): "the Painter, 100 percent import; descriptions in a txt or the meta file,
  as long as the item.en.nxd formatting is left vanilla". Shape: the LW-344 whitelist integration
  reads the partner's item names and descriptions from a simple file (JSON in the items.json row
  shape, or plain text) and bakes them through patch_names.py, which already seeds a text row per
  extended id and lays the painter's scaffold; the loader merges nxd overrides per cell against
  vanilla with cross-mod union and inserts new rows, so the partner ships no nxd, vanilla rows he
  never touches stay untouched, and the painter works on his cards with no new code. A free-text
  description option (his prose instead of this mod's two-line house format, scaffold still laid)
  is part of the shape. The runtime alternative, the DLL serving text by hooking the game's text
  lookup, is the walled French-text path and stays out of scope. Done looks like: one partner
  item authored from the file, listed with its own name and card, Kills line painting live.
  (Tech: patch_names.seed_extended_rows + item_intent; lib/bake_intent.ALLOWED_EXTRA_ROWS derives
  from the data; KILLS_SCAFFOLD is 11 chars and the suffix slot 2; memory
  modloader-merge-semantics pins the per-cell nxd union.)
- [LW-359] 2026-08-30: A player reports the twin-weapon consent rule leaking two ways: with a shield in the off hand, backing out of the equip menu removed the shield and stamped the second weapon anyway (the shield only stuck when placed in the MAIN hand), and swapping the main hand from a twin weapon to daggers left the granted Dual Wield support behind for a while, letting daggers dual-wield free.
  Reported by Darkrapid on Discord 2026-08-30 (docs/USER_FEEDBACK.md); the owner told the
  player the intended rule and promised a look. The design (LW-193/LW-194, ad2240a): an
  empty off hand invites the twin, anything player-equipped in the off-hand or shield slot
  declines it, and the mod never writes the shield slot. The shield-eaten symptom is the
  shape of the [twin-grant-inventory-desync] lane those rows closed, so first establish
  WHICH lane stamps over the back-out transition (suspect: the browse-screen re-stamp that
  fires within one pass of leaving Equip and Abilities, racing the game's own normalize
  while the off hand reads transiently empty), and whether Dual Wield support Key 477 is
  ever restored when a main-hand swap disqualifies the grant. GunSlinger.cs family
  (Policy/Apply/Reconcile/Store/MenuGate); re-read GunSlingerPolicy's consent checks and
  the LIVE_LEDGER twin rows before trusting any hypothesis. The first "cannot single-wield
  without occupying the other hand" half of the report is the consent design working as
  built, a UX perception to answer in the FAQ (LW-358's entry can cover both).

- [LW-360] 2026-08-30: The kill counters on the equip cards stop populating for most of the
  menu when the new warm start is running: the owner flipped cards live and the counts stayed
  dashes indefinitely. Cause read live the same day: the warm start (the uncommitted LW-324
  work) latches paint coverage 0.3s after arming on the one persisted region it could seed
  (122 sites, one per weapon), and MaybePoolPaint's covered short-circuit returns before ever
  consulting ShouldRunFullPoolScan, so the two regions the full locate publishes 20s later are
  never scanned at all; their 244 scaffold copies (one full card set per encoding, probed live,
  none painted in 45s of watching) stay dashes until something drains coverage. Before the warm
  start, coverage could not latch until after an all-region scan, which is why this never
  showed. Fix shape: the covered short-circuit must also break on a fresh region publish (read
  PoolLocator.PublishGeneration there, or clear the coverage latch when a publish adds regions),
  so a newly published region gets one scan even while covered. Blocks the paint half of the
  LW-332 live pass; the defect lives in the uncommitted LW-323/324 pile, so it lands there, not
  on a shipped build.
- [LW-344] 2026-08-26: Partner mods want their items to be living weapons too: the
  Cloud mod author (Xeoshades) asked for growth on their Buster Sword line, and today
  growth already bleeds onto any item occupying our ids, wearing the WRONG name in
  toasts and losing its counters (their descriptions replace our baked anchors). The
  right shape is a partner manifest: our runtime merges a small declaration file a
  partner mod ships (id, name, growth lane), giving their items first-class growth
  with correct names; counters additionally need the partner to bake our Kills
  scaffold and flavor anchor format into their descriptions, which is documentable.
  Turns the mod into a platform. Surfaced via the 2026-08-26 Nexus PM and Discord
  exchange (docs/USER_FEEDBACK.md same date).
  Owner agreement 2026-08-30, the settled design: the runtime SCANS the Reloaded Mods folder
  for a WHITELISTED partner mod's folder (keyed on its ModConfig.json ModId, first entry
  fftivc.re.cloud.complete, the FF7 RE Trilogy Cloud Conversion by Jade Knightblazer,
  https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/114) and, when
  present, USES THAT MOD'S OWN FILES to author new weapons as extended-inventory items.
  Facts from the 1.0.1 archive (read 2026-08-30, the owner's download): it ships eight
  whole-table nxds (item.en, ability.en, job, jobcommand, poachitem, uijobabilityhelp,
  overrideentrydata, and the overrideabilityactiondata table this repo parked as corrupting),
  ten table XMLs, four sharing this mod's own filenames (ItemData, ItemWeaponData,
  ItemEquipBonusData whose rows 74 to 78 collide with this mod's 74 to 79, and JobData), effect bins, g2d tex_161, and equip icons (both
  sizes) for ids 032, 049, 067, 068, 069, 070 and 256. Ids 32/49/67-70 sit exactly on slots
  this mod occupies today, 49/67-70 being the axe and flail slots LW-351 restores; id 256 is
  the partner's OWN new item (Materia Blade Plus) on a vanilla extended row, proof they are
  already squeezing the game's own 256-260 extended catalog range, the one this mod relocates
  (this mod's new ids start at 261). Re-homing
  their weapons onto extended ids resolves the ITEM-SLOT collisions; the whole-table nxds and
  the EquipBonus row overlap collide regardless and need their own answer in the build. The
  author is the same person as LW-344's original reporter: docs/USER_FEEDBACK.md records the
  Xeoshades PM signed "Jade" (2026-08-14). Folder recognition in practice means reading the
  ModConfig.json ModId, an implementation choice the build may revisit.
- [LW-345] 2026-08-26: Softening the card cache wipe on menu edges is its own arc, on
  purpose: today Display.Invalidate clears every found counter site on the status-card
  edge, forcing a re-find even when nothing moved, and the LW-324 plan review ruled
  softening OUT of that build after finding the wipe load-bearing on four axes
  (coupled pending-state reset, the coverage latch's shrink detection, a post-LW-257
  three-beat verify storm across up to 3072 retained sites, and SuffixRotation's
  survives-Invalidate invariant). Any future attempt starts from that review's
  evidence list, not from the June research doc's optimistic Option A.
- [LW-343] 2026-08-26: The mod's own Nexus page teaches the OLD kill thresholds and
  never says where the knob lives, and players notice: two users independently hunted
  for the threshold setting and failed, and the page still says 50 kills while the
  shipped curve has been 5/10/15 since the 2026-08-12 softening (LW-161). Fix is
  content, not code: update the Nexus description (thresholds, the kills.json tweak
  path with its real location under Reloaded/User/Mods, and the current feature list),
  and mirror the same facts in README.md so the page can be regenerated from the repo
  instead of drifting again. Surfaced by the 2026-08-26 Nexus posts sweep
  (docs/USER_FEEDBACK.md, same date).
- [LW-341] 2026-08-26: Teach the deploy audit that glow-tiered icons are healthy, not
  drift. Once LW-336 ships, the mod itself rewrites its deployed weapon icon tex files
  to each weapon's current tier, so BuildLinked -VerifyOnly comparing the install
  against the repo will report up to 242 "mismatched" icons that are actually the
  feature working, burying any real drift in noise. The audit should treat a managed
  glow icon as current when its hash matches the manifest's base OR any baked tier
  variant for that icon, and only then report it. Surfaced while speccing LW-336;
  the post-deploy [5/5] parity check is unaffected (it runs on fresh plain bases).
- [LW-319] 2026-08-25: The weapon glow in lane hues, PAUSED by the owner the same day
  ("let's pause on the glow for a moment") to re-promote LW-332; state banked and ready
  to resume: the eight lane text colors are MEASURED off the live card
  (tools/probes/lw319_text_rgb_map.json, crops committed), the Glow Ladder gallery
  renders A (verbatim) against B (same hue at glow strength, the owner called verbatim
  underwhelming) per lane with the full 121-weapon roster on both icon surfaces, WP's
  glow hue still needs re-measuring from the live teal 93 text (one owner screenshot of
  any gun's card), and the icon splice display failure is its own row (LW-334). Full
  seat contract in this file's history (f43135a). A third rung now exists (2026-08-25
  late, owner call: icons only, one family judged before the recipe rolls forward): C,
  a two-tone pop rim that keeps the lane hue but paints the inner ring full-chroma
  held deep so it punches on the white list ground, with a bright full-chroma falloff
  outside. The 11 Speed knives are rendered in A/B/C on both icon surfaces and sit in
  the gallery for the ruling (Tech: lw319_glow_ladder.py knives mode, commit 11f43be;
  Speed inner-ring panel dE moves 30.3 to 87.4 in the same hue family). Same sitting,
  the owner rejected Speed's GREEN outright, so Speed needs a new color before its lane
  can be ruled, and the color menu is gated by what the card text can wear: the vibrancy
  sweep steps the untested markup forms on a live card (Tech: lw307_card_markup_probe.py
  sweep mode; hex first, then the 8X/9X slot gaps, then three-digit; reads merge into
  lw307_card_colors.json). SWEEP RAN 2026-08-25/26 (reads banked): hex does not parse
  and one bad tag un-parses the WHOLE description string, values past 99 parse but only
  into washes, slot 82 pink is the one new family, slot 90 red is preferred over 30.
  The owner then delegated the palette ("paint the weapons to their respective colors,
  make the glow POP", 2026-08-26): a pink Speed round was cut as too close to PA red,
  Speed settled on the EMERALD already in the baked icon art (Warbrand screenshot),
  PA red 90, and the full C-rung lane bake SHIPPED to the install (commits bd3a68e,
  19f2c10, 68670f6, 53d5887): 726 variants re-baked, PA Grows text slot 90 through
  flavor/meta/nxd in lockstep, and deploy_glow_tex.py as the LW-334 interim display
  path (tier rims copied into the deployed loose tex after BuildLinked, riding the
  launch merge; restart-only updates until LW-334 cracks live refresh). The owner then
  tuned the look live across five passes the same night (2026-08-26): tier 3 softened
  1.5 to 1.3, the falloff band halved and the third band dropped, the whole ladder
  dimmed to 0.7 after a 0.85 pass measured real but read invisible, and a border
  quality pass (rim paints exterior-only and composes under art) after the owner
  photographed stray rim pixels on a gun. A final adversarial review returned SHIP
  WITH NITS; its two real catches (glow_bounce.py wrote one directory too high and
  had never worked, and the exterior fill was 4-connected against an 8-connected
  silhouette, costing Perseus Bow its inner rim) are fixed with a new pin3d guarding
  the border behavior. AWAITING the owner's look at the final border-fixed state, its
  overlay pending one game-quit window.

- [LW-329] 2026-08-25: The Grows line on every weapon card gets painted in its weapon's
  glow color, whole line, one hue per weapon (owner ruling 2026-08-25: single-lane cards
  wear the lane hue, PA red and MA blue and Speed green per LW-319's vocabulary, and
  multi-lane cards wear their blend hue like the poles' purple; the words carry the
  decomposition, the color carries the identity; the Kills line stays first and plain).
  The full color dictionary was RULED 2026-08-25 in chat: Speed green, PA red, MA blue,
  HP orange, WP cyan (the three physical guns), WP+Faith gold outright (Faith never
  stands alone, and gold dodges the white-on-white card trap), PA+MA purple (poles),
  PA+MA+Brave magenta with shared purple as the fallback (katanas). Color follows the
  LANE, not the family: Wrathblade and Swiftedge glow green among red swords, Arcanum
  blue, and that is the dictionary working. The line text is also final: "Grows:" label,
  short stat names, ampersand lists ("PA, MA & Brave", baked 50c2505). The palette sitting
  RAN 2026-08-25 (owner-read, 32 slots banked in tools/probes/lw329_palette_map.json)
  and every ruled hue found a real readable slot: Speed green 40, PA red 30, MA blue 50,
  HP orange 81, WP cyan 83, poles purple 95; the magic guns' gold is mustard 60 (the
  table's only gold, semi-readable, upgradeable if a later fine pass finds better); the
  katana blend awaits one owner pick, periwinkle 94 (readable, distinct) versus sharing
  purple 95. Sitting lessons for the bake arc: the heap copy population GROWS mid-sitting
  and a killed probe strands tagged copies (recovered live by scanning for the tag prefix;
  harden the cycler with a stuck-state check before its next run). COLOR SHIPPED
  2026-08-25 late (katanas periwinkle 94 by owner default): every Grows line wears its
  lane hue via an inline color tag, baked at the BODY BOTTOM. The owner's requested move
  (Grows directly under the Kills line) is deliberately NOT in this bake: it stretches
  the runtime's Kills-to-flavor anchor gap from 2 fixed bytes to about 50, and a
  short-tailed neighbor card in the heap pool could then out-near the card's own flavor
  and mispaint the counter; the move ships only with a CardScanner anchor extension, row
  LW-332. Owner live read owed: colored lines on restart, hues matching the sitting map. (Tech: wrap each baked Grows line in <color=NN>...</color> keyed
  by meta.json's lane; the desc-budget gate must strip tags before counting since tags
  are bytes, not glyphs; ships as its own bake after the LW-322 live pass.)

- [LW-330] 2026-08-25: The enemy AI cures Venombolt's poison, so the always-Poison
  crossbow's identity needs a look: the owner watched an enemy NPC spend its turn using a
  Remedy to cleanse the poison a Venombolt bolt had just applied (the bolt came from an
  owner-side unit under auto-control). Two readings and the row exists to decide between
  them with more observation: the poison is still paying (it taxed the enemy a full turn
  and an item, which is action denial even when it never ticks), or the DoT identity is
  hollow against item-carrying humans and the weapon only truly bites monsters and
  item-less foes. Watch a few more fights before any tuning; if the turn-tax reading
  wins, the card text and identity prose could even advertise it. (Tech: Venombolt id 80,
  formula 45 always-Poison; observed 2026-08-25 in a live battle.)

- [LW-318] 2026-08-25: The weapon power curve gets a fairness pass: every weapon strong
  enough to matter, none so strong the rest become vendor trash. The owner replayed the mod
  a few weeks before 2026-08-25 and the balance felt steady until the Warbrand appeared and
  dominated everything after. The likely shape: the rebalance raised Warbrand to WP 15, the
  highest sword WP in the game, priced only in zero evade and zero utility, and the
  build-diversity gate is structurally blind to this (analyze.py forbids strict dominance
  but cannot see a power SPIKE: a weapon can dominate the game while dominating no single
  item row). Revisit the pricing formula so tiers climb without a boom moment, and consider
  a curve check the gate can actually enforce. Owner-placed DIRECTLY before the glow task,
  so the colors land on a curve worth showing.

  Balance proof re-run for the owner 2026-08-26 with a new blind-spot instrument
  (tools/probes/lw318_thin_niche.py, the gate's own dominance law reused verbatim):
  zero strictly dominated items stands, vanilla's same-law count is 73 of 234, and the
  thin-niche hunt over all 836 single-shield pairs found exactly ONE true catch: the
  Hushward Mail (id 175) and the Wardsilk Vest (id 191) are STAT TWINS, identical on
  every gate axis, tier and rider, in the same body slot, where the vest's universal
  access makes the armored-only mail redundant in practice; the gate cannot see it
  because domination requires a strict edge. Suggested fix when this row runs: armor
  out-HPs clothing at tier parity. Watch item, thinner but defensible: the Argent Dirk
  (id 5) escapes three same-or-earlier siblings by single points, its riderless plain
  knife identity is the thinnest in the catalog. The Warbrand's one-point WP crown over
  Arcanum and Lightbringer is the owner's own LW-320 ruling, by design.
  RULINGS EXECUTED 2026-08-26 in session, gates hardened the same hour: the Hushward
  Mail takes hp 46 (armor out-HPs clothing at tier parity, the owner's pick), the
  Padded Coif takes hp 14 (the same disease one shelf up, same medicine), and the
  Argent Dirk goes HOLY (owner: "Holy-guacamole, I love it"; the name and its own
  silversmith flavor line always said silver, the element makes it mechanical and
  erases all three of its thin matchups without touching a number). Two NEW permanent
  analyze.py stanzas enforce the audit forever: STAT TWINS (no two slot-sharing items
  with one identical stat line) and THIN NICHE with the SPLIT LINE (2-point margin
  same-tier, 1-point cross-tier; the owner delegated the line call 2026-08-26 and the
  diversity reasoning is written into analyze.py at the constants), both
  mutation-proven red then green. The exception list holds exactly three owner-ruled
  entries (Warbrand twice, Iga Blade). item.en.nxd rebaked for the Dirk's new "Deals
  Holy damage." card line, verify PASS, restart-only, rides the next deploy.
- [LW-321] 2026-08-25: Weak classes get a design pass through the weapon lens: Thief,
  Archer, Orator and Mystic sit low on community tier lists, and this mod's proven lever
  for that is their TOOLS, not their job tables. The owner already ran the template on the
  Thief (knives made powerful with great abilities, plus bow and crossbow access), and the
  growth-lane arc quietly extends it: Speed-growing knives and bows serve Thief and Archer,
  the resurrected guns serve the Orator, and the pole, rod, staff and book MA lanes serve
  the Mystic. This row is IDEATION, may produce no single actionable: audit each weak
  class's full kit (weapon access, whether a signature weapon exists worth growing,
  whether any growth lane feeds the class's own skillset scaling, e.g. verify whether held
  Faith moves Orator talk success), and propose per-class packages using only weapon-side
  levers. Boundary, on purpose: no job-table or skillset edits; job internals are
  Denuvo-walled, other mods' legitimate turf (the kit-lane guard's whole premise), and
  outside this mod's identity. Owner proposed 2026-08-25 from tier-video observations.
  The same lens must look DOWN as well as up, owner FYI 2026-08-25: the Monk is considered
  overpowered in every regard, which puts the planned monks-get-poles access grant (the
  entire justification for the poles' dormant PA half in LW-250's table) under scrutiny
  here rather than assumed; handing the strongest class a PA and MA growing weapon family
  needs a deliberate ruling, and the pole lane itself loses nothing while that waits.

- [LW-325] 2026-08-25: Regression test every one of the 30 signature abilities, owner
  directed after Puppeteer needed retries during demo recording. The natural vehicle is
  the owner's gif tour (media/signatures/): every ability gets demonstrated on camera
  anyway, so each recording doubles as a live check, and any ability that needs a retry,
  a specific target kind, or special timing gets a note in this row as the tour reaches
  it. Runs AFTER the current arc queue per the owner's slotting call. (Tech: the list is
  the 30 signature blocks in data/items.json; verdicts land here as sub-notes and any
  real defect gets its own row, LW-326 being the first.)

- [LW-326] 2026-08-25: Puppeteer works but is unreliable: quick attacks fail to dominate
  and the owner needed retries to record a clean demo. The log tape shows the arm's two
  signal design running on one leg: the actor-pointer half read pointerMatch=False on
  every line of the whole session, and the main-hand latch it falls back on flickers off
  right at the strike edge, so a hit confirmed within about half a second of arming
  misses while a slow three-count before confirming lands every time. Also on the tape:
  a dominated MONSTER (job 100) shows nothing visible, reading as a silent failure.
  (Tech: livingweapon.log 14:38-14:42 2026-08-25; failures show latchMainHand=False
  actedByte=1 at the hit tick, both successes had 4+ seconds between ACTIVE and the
  strike. Diagnose why Band.ActorEntry pointerMatch never fires (ActorPtr semantics vs
  the D1 OR gate, Puppeteer.Policy.cs) and harden the latch across the strike edge;
  consider a target-kind note or refusal for monsters whose domination has no visible
  menu effect. Owner workaround documented in-session: wait a beat before confirming.)

- [LW-324] 2026-08-25: The kill counters take too long to appear on the equip cards after a
  cold boot, and the owner dislikes the wait (his words, "expressing my displeasure"). The
  counters live in heap string pools the mod has to re-find every launch, and the search is
  deliberately budgeted so it never hitches a frame, which is why the numbers dribble in.
  Candidate lever: persist the located pool sites across launches and re-verify instead of
  re-hunting from scratch. (Tech: Display's budgeted DisplaySweep/PoolPaint census; the
  pool addresses are heap allocations so a blind replay is unsafe, but a verify-then-adopt
  warm-start against the LW-257 anchor checks could cut the cold-boot window. Owner filed
  it as a someday row, not urgent.)
  BUILT and adversarially verified 2026-08-26 (full pipeline: premise from four
  launches of flight tapes, plan, adversarial plan review that REDESIGNED the lever
  from per-site to per-region warm start, TDD implement whose mandated trap test
  caught the plan's own suppression bug, fresh verify: code 9 of 10, zero code
  changes required, suite 3343 green). Awaiting the owner's stopwatch read: cold
  boot 1 writes pool_regions.json at locate completion, cold boot 2 logs "seeded
  N of M" and the counters appear in one to two seconds. The N of M numbers close
  the [kills-pool-region-recurrence] ledger row.

  2026-08-27 18:15, owner report on the deployed prod build (Aug 26, without this row's
  warm start): the Chaos Blade crossed to +3 mid-battle (kill 15 at 18:12:39, toast the same
  second) but the card's +3 text arrived long after. The log names the wait: after the battle
  the cached text pools tested stale (18:15:05 revalidate stillPool=False), so a fresh
  budgeted sweep ran (locate-complete 18:15:26, 20 s; in-battle passes took 60 to 88 s) before
  the paint could land. Post-battle re-find is a second face of this row, separate from the
  cold boot the warm start covers: candidate lever = keep painting into still-valid regions
  while only the invalidated ones are re-found, instead of one all-or-nothing revalidate.
- [LW-305] 2026-08-22: The colour bench can now paint a weapon in the running game, but the list
  saying which weapon owns which colour set has at least one wrong entry, so some weapons would be
  painted in someone else's colours. Audit that list weapon by weapon and correct it. Materia Blade
  is the known liar: the list says it uses colour set 8, the screen says it does not, and a second
  weapon on set 8 changed colour in the same battle while Materia Blade did not. (Tech:
  tools/probes/lw305_bench_paint.py keychart paints all 16 palettes one distinct hue each in both
  resident banks at 0x140d35750/0x140d35950, so a swing names its own palette; `saw <weapon>
  <colour>` banks the observation into lw305_observed_palettes.json and diffs it against
  lw289_weapon_palette_map.json's X nibble. Confirmed agreeing so far: Javelin/Wyrmpike X=8,
  Gokuu's/Sage's Pole X=6 Y=0, Broadsword/Vagabond X=14 Y=0, Romandan/Outrider Pistol Y=1.
  Materia Blade reads palette 0 against a mapped X=8 Y=1. Note every probe before this one skipped
  palettes 0-2 as effects-only, which is how the miss survived.)

- [LW-310] 2026-08-24: Counter attacks still swing in vanilla colours, and the owner wants them
  painted too. Confirmed live with a staged test: an unauthored attacker swung at Ramza, his
  Materia Blade counter came out vanilla, exactly the accepted limit the turn based painter
  ships with. The candidate mechanism is already proven for a different purpose: the engine's
  actor pointer parks on struck victims (documented as a kill credit trap), and the struck
  victim is exactly the unit about to counter, so a paint keyed on that parking lands in the
  window between the hit and the counter swing. Design sketch: a second painted slot for the
  reaction unit, painted when the pointer parks on an authored wielder who is not the turn
  owner, restored on the next turn edge. Attacker and victim sharing one palette stays a
  residual. Needs a live probe of the parking to counter swing timing before any build.
  (Tech: ActorPtr dwell semantics per [actorptr-dwell-semantics]; WeaponPalette.Policy gains a
  reaction lane; the ~170ms effective tick must beat the counter animation start, measure
  first.)

- [LW-307] 2026-08-24: The game can colour its own text. The owner edited the world map's
  Camera Controls help text and proved the strings carry colour tags the renderer obeys: a
  well formed tag recoloured his inserted "Modded by prawl" text, a broken one printed the
  tag characters on screen. We have wanted coloured text on the weapon description cards
  since the Kills counter first landed and never knew how. Try the same tags on the card
  text the runtime already paints. (Tech: inline markup, observed form color=80 in angle
  brackets with a bare closing color tag; ledger row [inline-color-markup-in-ui-text],
  PROVEN on the world map surface only at capture time; the card's draw path was the
  untested half.)
  CARD PROVEN 2026-08-25, owner screenshot: the description card consumed a well formed
  color 80 tag poked into Warbrand's flavor line and rendered the span bright yellow,
  with the Kills counter surviving beside it. Probe tools/probes/lw307_card_markup_probe.py
  (scan/poke/restore, length neutral pool rewrite). This row is now the WIRING seat:
  ship colored card text through the runtime's existing paints, with LW-322's Grows text
  and LW-319's tier colors as the first customers.

- [LW-312] 2026-08-24: Warbrand and the other three ex flails still draw broken garbage
  chunks when they swing, the last visible scar of retyping flails into swords, and two cheap
  fixes were tried live tonight and both died honestly. The battle frame geometry files
  (FFTPack 63/64) were decoded end to end, the game provably read our patched copies both
  rounds (the loader log said modded file 63/64 while the garbage persisted unchanged), so
  the community's PSX model of how a weapon picks its frames does not hold in the remaster:
  neither the type indexed nor the graphic indexed zero frame edit changed a single pixel.
  This rejoins the June finding that the swing model resolves through an unlocated in
  process resolver. Next instruments, in order of cheapness: match the garbage chunks'
  geometry offline against all 871 decoded frames to reverse the actual frame selection
  arithmetic (no game time needed); then single step the June resolver chain on a swing.
  (Tech: probe tools/probes/lw251_wep_shape_probe.py rounds 1 and 2, screenshots
  lw251_warbrand_probe1/2.png, decoded contact sheets lw251_wep1/wep2_zeroframes.png,
  zero frame parser cross checked against TacticsTemplateG's Shp.gd; resolver chain notes
  in the weapon-blade-art-walled memory, prototype branch ledger only.)
  - OFFLINE GEOMETRY ROUND 2026-08-24, and its verdict REFRAMES the hunt: the garbage is not
    classic sprite data at all. All 871 frames of both shape files were decoded and rendered
    (parser validated by the frame count matching exactly), all three pixel pages of the
    sprite file were dumped and searched, and the garbage chunks appear in none of them. The
    chunks themselves, cropped and enlarged, are SMOOTH high fidelity art (a golden quill
    feather with a metal nib, and a glossy cyan sliver that closely resembles the swing arc
    art in HD form) while the unit beside them renders as chunky classic pixels, so the
    garbage is drawn by the HD layer, not the classic frame path. The gold also matches no
    entry of Warbrand's authored palette, so the draw is not even wearing the acting weapon's
    paint. Stop editing the classic shape files; the next instruments are the HD equipment
    sheet rendered with a REAL colour table (the garbage may be its art sampled at overrun
    coordinates; tex_161 is the classic page's HD twin at the same 256x256 coordinates) and
    the June in process resolver chain. (Tech: renderer tools/probes/lw312_frame_atlas.py,
    format from TacticsTemplateG Shp.gd; crops lw312_crop_plume/sliver in the session
    scratchpad; silhouette IoU over all frames peaked at 0.64 on generic blobs, no true
    match; g2d BGRA scan found no gold feather blob but is blind to paletted entries.)

- [LW-302] 2026-08-21: Shields are visible in battle too and they stay their original colour while
  the weapons change, which will look odd once weapons match their icons. Owner spotted it during
  the LW-301 testing. They are not part of the weapon work and cannot be reached the same way: the
  table that says which colour set a weapon uses covers only the 127 weapons, ids 1 to 127, and
  holds no entry for any of the 16 shields, so a shield draws from some other source entirely.
  Confirmed by the same test that coloured every weapon on the field: with all 13 weapon colour
  sets painted, shields stayed ordinary. Finding that source is its own hunt and should reuse the
  method that worked for weapons, which was to take the untouched art from the game files and
  search the running game's memory for it. Deliberately deferred so the weapon work can land
  first. (Tech: lw289_weapon_palette_map.json is ids 1..127 with zero Shield category entries;
  shields are items 128..143. Ledger [per-weapon-colour-by-turn-repaint] covers weapons only.)

- [LW-313] 2026-08-24: The owner has found a different path to professional looking icon
  art and cancelled the three piece system that was being built for it (LW-278, see the
  changelog). What the path actually is has not been written down yet, so this row holds
  the seat: capture the method here the moment the owner spells it out, and only then
  decide what happens to the queued per family art rows further down.

- [LW-314] 2026-08-24: The last planned feature: units earn TITLES from how they behave
  (kill many mages and the tag over your head reads Mage Slayer), delivered on the game's
  own overhead plate. Tonight's CE session cracked the display path: every unit has one
  auto battle byte that starts auto battle AND shows the Auto tag, and writing it forces
  the tag to re render, the missing trigger for swapping the text. The tag's text is
  COPIED from a source string into a per widget holder at show time and rendered to glyphs
  once, so poking the holder alone shows nothing until the next re set; finding the source
  is the next step, and the unbounded lane is the mod's existing render time text swap
  hook. The plate also carries sibling text nodes (name, guest, special), so per unit text
  is structurally supported. (Tech: auto byte = combat +0x1EC per unit, owner driven both
  directions at FFT_enhanced.exe+1855ECC, band slot 0; 0 = manual, 12 decimal = 0x0C =
  auto on, instruction mode encoding unchecked; manual control flag = combat +0x05 bit
  0x08 with roster mirror partyUnit +0x04, from decompiling Dicene's fftivc.handsfree,
  which hooks CopyUnitToBattleUnit, CopyJobEffectsToUnit and set_status_all to keep it
  cleared; tag anim routine UpdateTagAnimFromBattleUnit reads the unit via anim +0x148;
  widget states ShowAuto and HideAuto, text nodes TextAuto, TextName, TextGuest,
  TextSpecial; probe tools/probes/auto_text_probe.py; ledger row [auto-battle-mode-byte].)
  - HUNT PARKED 2026-08-24, owner call (juice not worth the squeeze): the tag's TEXT source
    was not found, and the negatives are the valuable part. It is NOT any live string: the
    owner rewrote every ASCII and wide "Auto" in memory and the tag still rendered, and a
    poke of the one standalone message table entry (beside an <if template, the likely
    earlier crash cause) changed nothing after a battle restart. The plate board
    (ffto_battle_main_fieldunit.uib) holds only node and state names, no words. Best
    remaining hypotheses: the word is baked ART in an undecoded battle atlas (ui_battle_05
    held the command icons, not words), or glyphs are baked at boot from a source that
    only reads once. Instruments: auto_text_probe.py verbs scan/classify/pokeall/check/
    hold/autoflag/snap/flagdiff; FF16Tools unpack recipe proven on 0008.pac.
  - SOURCE FOUND 2026-10-16 (offline, from another modder's data-only mod): the word over the
    unit's head is an ordinary table string after all, and the hunt poked the wrong one of two
    identical "Auto" rows. Jaylo's "Auto battle word indicator removal" (Nexus mod 144) ships
    one file, ui.en.nxd, with exactly one cell changed against vanilla 0004.en.pac: key 2806
    "Auto" set to empty. Key 2806 sits in the overhead plate's own cluster (2802 Enemy, 2803
    Guest, 2804 Special, 2806 Auto, 2807 ??, 2809 Lv. ??), which are the plate's TextGuest,
    TextSpecial and TextAuto nodes. The row the parked hunt poked was key 2566, the other
    "Auto" (the save-list "Saved Data (Auto)" family beside the 2567 <if template), which is
    why nothing changed. Consequences: a static rename is a data-only change (full-table
    ui.en.nxd override, takes effect on restart, same per-language caveat as item.en.nxd); a
    per-unit title still needs the render-time swap, but the swap now has a known source
    string to match on. Not re-tested live; ledger row [auto-battle-mode-byte] unchanged.

- [LW-172] 2026-08-12: The mod's human versus monster job boundary is off by two, and every
  consumer of it needs an audit: monsters start at job 94 (Chocobo; Black Chocobo 95), not 96.
  OWNER SIGHTING 2026-08-17, the player-visible cost is now on record: during the LW-233 retry
  drill the mod announced "Stoneshooter claims kill number 25, felling a human" twice over a
  chocobo, once at 18:08:16 and once at 18:09:07 after the bird was raised and killed again.
  The victim probe read job 94 both times (nameId 861, battle slot 12), and job 94 sits inside
  the generic human band because Puppeteer.Policy.cs:25 sets GenericJobHi to 95 while
  Puppeteer.Policy.cs:31 sets MonsterJobLo to 96, so every chocobo in the game is announced as a
  person. The toast is the harmless half; VictimClass feeds Reliquary victim classing, so fix the
  boundary rather than the wording.
  Falsified live during the LW-167 pass (a Black Chocobo victim's job byte read 95 at the
  credit edge) and confirmed by the game's own Job sheet (Key 94 Chocobo, 95 Black Chocobo,
  96 Red Chocobo; the sheet also carries each monster's poach keys). Known consumers of the
  wrong floor: Puppeteer.Policy.cs (GenericJobHi 95, MonsterJobLo 96), VictimClass.cs
  (IsHumanJob 74 to 95; job 94 labeled "Dark Knight", which per the sheet is the Chocobo, so
  the July "Dark Knight 94 live" reading and any Reliquary victim classing built on it are
  suspect), and any band walk leaning on the 96 floor. Living Poach itself no longer cares
  (its gate is Job sheet map membership since the fix). Also unresolved and captured here so
  it is not smoothed over: a 2026-06-08 live ROSTER read recorded job 96 as "Chocobo", which
  either means that unit was actually a Red Chocobo or the roster +0x02 space differs from
  the band job byte at the boundary; one deliberate re-read settles it.

- [LW-234] 2026-08-14: The mod files the player's own guests as enemies, so a guest dying could
  hand the player's weapon a kill it did not earn, complete with a growth toast. Found in the
  same user's tape. Two player-side units (nameId 4 and nameId 7, band seats 8 and 9) were
  bridged Enemy fourteen times with rescue=OracleEnemy, meaning the enemy oracle had captured
  them. That they are on the player's side is visible in the tape rather than assumed: seat 8
  takes 18 damage at t=12782890 and never dies, while every unit that does die is one of the
  job 169 to 172 monsters. Nothing was mis-credited this session because neither guest died, so
  this is a live exposure and not an observed loss. What makes it sharp is that oracle
  membership is the ONLY team gate on kill credit, and the class doc for EnemyOracle states that
  guests are "structurally excluded", which this tape falsifies. (Tech:
  Downloads/flight_20260812_023631_battle-exit.jsonl; the gate is _oracle.Contains(slot.Id) in
  KillTracker.Corpses.cs. Either add a real side-membership read, which the Provoke arc proved
  must come from the band's own bit and never from seat position, or stop claiming exclusion in
  the doc and gate credit on something that actually holds. Rides naturally with LW-233 since
  both are corpse-credit correctness.)

- [LW-195] 2026-08-13: Equip a weapon in the OFF hand and a shield in the MAIN hand and the
  battle menu's Attack row reads as bare fists, as if the unit were unarmed. Owner hit it live
  2026-08-13. First question when picked up, before any fix: is the fists text the GAME's own
  behaviour for that hand arrangement or this mod's paint? One battle with the mod disabled (or
  an untracked vanilla weapon in the same arrangement) settles it. If it is ours, the likely
  lane is that everything in the runtime treats the roster MAIN hand slot as "the weapon":
  the attack-row painter resolves the acting unit's weapon from that slot, so a shield there
  reads as no weapon even though a real weapon sits in the off hand. If that read is right, the
  same blind spot silently breaks more than text: kill credit and every signature also key on
  the main hand, so an off-hand-weapon build would earn no kills, no growth, and no granted
  commands, making this a coverage hole for an entire legal equipment arrangement, with the
  fists text just its visible corner. (Tech: unverified beyond the sighting; the main-hand
  reads to audit are the RRHand roster reads in Wielder/ActorResolver/Display and the
  mainHandWeapon field the kobu/credit log lines already print. The flight tape from the
  sighting battle, if one flushed, shows what the credit lane resolved for that unit.)

- [LW-191] 2026-08-13: Equipping a living weapon in the middle of a battle can take its granted
  command AWAY instead of giving it, and the mod then says there is no eligible wielder while that
  wielder is standing on the field. Seen live 2026-08-13: Patrick stole the Sanguine Sword from
  Gaffgarion and used Re-equip during the same battle. Shadow Blade had been granted to Ramza at
  11:51:49 (party slot 0, job 2, record 26, the Squire story variant) and was released at 11:55:45
  with the line "no eligible Sanguine Sword wielder remains", so the command disappeared from the
  menu mid fight. One second after that release the battle side event lines still read weapon 23,
  meaning the battle still held the sword while the roster scan no longer found it. (Tech:
  ShadowBlade.cs walks RosterBase and requires the roster right hand to read id 23; Barrage and
  Provoke are the same roster keyed shape, so all three granted commands share the exposure. This
  is LW-103's roster versus band disagreement reaching a surface where it costs the player an
  ability instead of being harmless. Cheapest first question, one probe: does a mid battle Re-equip
  write the roster row at all, or only the band? If only the band, the grant lane wants a band
  fallback keyed the way kill credit already bridges a battle actor to its roster slot. Second,
  independent of the fix: the release message should not assert there is no eligible wielder when
  the truth is that the mod lost sight of the weapon.)

- [LW-164] 2026-08-12: An enemy carrying the same weapon type as a player's living weapon can be
  briefly mistaken for a player unit, which could someday hand an enemy's kill to the player's
  weapon.
  Seen once on the 2026-08-12 player tape (flight_20260812_023631): a band enemy wielding weapon
  id 19 was bridged Player with rescue=WeaponUnique even though FOUR roster rows also carry id
  19 and the enemy's level/brave/faith fingerprint matched no roster row; no kill landed on that
  hit, so nothing mis-credited, but the WeaponUnique rescue demonstrably fired on a non-unique
  id. Check ActorResolver's WeaponUnique lane: it should refuse when the weapon id is not unique
  across the roster, and probably verify the fingerprint against the roster row it names. The
  same tape shows vanilla chapter 1 human enemies routinely carrying tracked ids 19 and 20, so
  the collision population is normal play, not an exotic setup.

- [LW-108] 2026-07-21: Restarting a battle quickly is completely invisible to the mod, so it
  keeps believing the old battle never ended; the worst case is a kill being counted twice.
  Found while checking LW-100 (flight tape flight_20260721_211423): battleMode fell to 0 for
  3.469 seconds and the mod's exit debounce is 4.0 seconds (BattleState.ExitDebounceSeconds), so
  no battle-end and no battle-start fired, the turn counter ran straight through (the closing
  line reported 8 turns across BOTH attempts), and Engine.ResetBattleState never ran. Everything
  per battle survives into the replayed battle: kill tracker corpse latches and coverage, growth
  captures, the struct location cache, the LW-42 marker arm, and every signature's state. The
  sharp edge is credit: enemies killed before the restart are alive again while the mod still
  holds their corpses, so re-killing them can credit twice. Seen twice now (3.469s here, 2.860s
  in the 14:04 tape), so a fast reload is the normal case, not an outlier. Candidates: detect the
  board snapping back to spawn tiles (five units moved in one 4ms tick) as a restart edge, or key
  the reset on a battle identity rather than a wall clock gap. Do NOT just shorten the debounce
  without re-checking the LW-42 post battle marker stick that the 4 seconds exists to absorb.

- [LW-7] 2026-07-05: Turn counting breaks under auto-battle: several different units' turns
  all get counted as one unit's.
  Observed turns #2-#6 credited to one fingerprint (log 07:58). The kill-credit half already
  shipped (KillerStamp death-edge stamp, f4bf5df); the turn-count half is still live.
  Candidate: close the acted period on ActorRegister OWNER CHANGE in addition to the
  byte-fall debounce. Must not regress reaction-kill credit (the pointer may name the
  REACTOR during a reaction, unverified per the ledger caveat).

- [LW-296] 2026-08-21: When another mod replaces the game's item text, the kill counter quietly
  stops appearing on weapon cards and the player is never told why, so tell them. The counter
  is not broken and counting never stops; the number simply has nowhere to be drawn, and today
  that looks identical to the mod being dead. The fix is the calm notice we already use when a
  job mod takes the weapon commands away: notice it, stay on, say exactly what is hidden and
  exactly what still works.
  This got more likely on 2026-08-21, when a full graphical mod editor shipped on the Nexus
  (The Ivalice Chronicles Mod Studio, mod 111) that puts item and ability text editing behind a
  checkbox. Text mods used to require running conversion tools by hand; now they do not, so the
  collision goes from rare to ordinary. See docs/COMPATIBILITY.md.
  (Tech: our painted counter anchors to the flavor line of an item's description, and an
  item.en.nxd override is a full-table replace, so a winning text mod removes every anchor.
  LivingWeapon/Display/ currently has no detection for a session-long zero-hit sweep and nothing
  user-facing fires. Model the notice on LaunchGuard.KitLane.cs: a truthful WARN plus a
  once-per-session calm box, mod stays armed, nothing else disabled.)

- [LW-101] 2026-07-21: Players whose game language is not English see no kill counts at all,
  and the only way to get them back is switching the game to English and restarting (player
  report 2026-07-21, native language Chinese; the same player confirmed the switch works).
  The failure is silent and graceful: growth, signatures, and tallies all keep working, only
  the on card text is missing, so a player has no way to tell the mod is fine. What is NEW in
  this report: the wall is not French specific (it reaches Chinese too), and the English plus
  restart workaround is player confirmed. Known cause and wall: the card painter anchors on
  the literal English "Kills: " string baked into our English item descriptions
  (CardPatterns.cs), and a non English game loads its own language item table, so no anchor
  exists to paint into; shipping our text into a per language slot was WALLED live 2026-06-30
  on two independent counts (the game resolves the item table once under English at boot and
  never reloads it when another language activates, and FF16Tools cannot parse the real non
  English tables anyway). Cheap candidates in ascending cost: (a) say it plainly in README
  and on the Nexus page, since the workaround is real and free; (b) detect a non English item
  table at startup and log one clear line (and consider the same message box lane the
  fingerprint guard already owns) so the player learns the mod is healthy and how to see the
  counter; (c) the real cure, DLL live painting of the counter into the loaded language table
  (a genuine RE arc, the painter would need a language agnostic anchor), or upstream
  modloader support for per language text overrides (ask Nenkai; the parser bug report is
  worth sending either way). See the walled French investigation in the memory ledger and
  docs/MECHANICS.md before reopening the data lane; do not retry the item.<lang>.nxd approach
  without new information.

- [LW-283] 2026-08-19: Several finished features passed the owner's own in game checks,
  but the official record still lists them as unproven because the paperwork was deferred
  every time and now survives only in scattered session notes. One sitting with the owner
  clears the whole backlog. (Tech: at least four arcs carry deferred LIVE_LEDGER flips,
  namely Provoke (LW-123, passed 2026-07-28), Bulwark (d4c3744), the twin grant consent
  rewrite (LW-193 and LW-194, three flips), and the restart rewind pair (LW-233, two
  flips); collect every pending claim into one list carrying its mechanism, evidence and
  date so the owner can flip them in a single pass. Flips stay owner only.)

- [LW-284] 2026-08-19: One of the mod's running behaviours is trusted every day but was
  never written into the official record of proven behaviours, so a future session could
  rebuild around it with nothing to check against. (Tech: the Puppeteer release rule, that
  a puppet is freed after taking its OWN turn, has no docs/LIVE_LEDGER.md row despite
  being live verified during 2.3.0 as LW-5, commit e882799, and re confirmed in that
  release's smoke pass rows 3.1 to 3.3; write the claim, mechanism, evidence and date as a
  row, then the owner flips it.)

- [LW-162] 2026-08-12: The build script's warning about leftover dev kill tallies points at the
  wrong folder, so anyone following it looks where the tally no longer lives.
  Found during the LW-161 live pass: BuildLinked's DEV-flavored-install warning tells the owner
  to delete kills.json from the deploy folder (Reloaded\Mods\prawl.fft.livingweapons), but since
  the LW-134 save-dir split the tally lives in the User save folder
  (Reloaded\User\Mods\prawl.fft.livingweapons), and the deploy-folder path misdirected this
  session's live-pass staging on the first try. Re-point the warning text at the real save dir
  and check whether the preserve/restore list still names the old location anywhere else in
  tools/pipeline.ps1.

- [LW-282] 2026-08-19: A file everyone was told to treat as disposable scratch has quietly
  become load bearing, so deleting it would make the next probe slower and dumber instead
  of failing loudly. (Tech: tools/probes/lw251_clut_hits.json holds the 64 known in image
  palette row addresses that the new lw251_boot_clut_race.py polls on its fast discovery
  path; commit it alongside that probe, correct the handoff line calling it safe to delete,
  and have the probe log a loud line when the file is absent rather than silently falling
  back to span hunts only.)

- [LW-285] 2026-08-19: An old leftover copy of the game's art container sits in the game
  folder looking exactly like the real one, and it already cost one session a detour down
  the wrong path before anyone spotted the difference. (Tech: the loose
  data/enhanced/system/ffto/g2d.dat is a December 2025 leftover, 11.2MB and 1426 entries,
  whose offset table carries a 16 per index drift; the file the game actually reads is
  0007.pac, 13.59MB and 2450 entries, with no drift. It is a game owned file, so the fix
  is a loud warning at the top of every probe that touches the container plus an owner
  decision about renaming it locally; never silently change a file the game installer
  owns.)

- [LW-280] 2026-08-19: A typo in one of our hand edited .json data files fails silently
  today: nothing checks the files are even well formed, let alone the right shape, so a
  bad edit only surfaces later as a confusing build error or a wrong table. Add a
  validation gate that reads every hand edited .json and fails loudly at the file and
  line when something is off. (Tech: candidates = data/items.json,
  data/additional_data_ids.json, data/vanilla_equipbonus.json, data/vanilla_shop.json;
  generate.py already implies a schema for items.json, so extract its expectations into
  an explicit check that runs in the shared pipeline prefix alongside analyze.py;
  generated jsons like meta.json are covered by their generators; probe scratch jsons in
  tools/probes/ stay exempt.)

- [LW-315] 2026-08-24: The release scope gate reads more of the scope file than it should, so a
  bullet in the OUT of scope list can be policed as if it were a ship gate checkbox. Found when
  LW-251's ship turned an OUT bullet red in the R1 check. The IN region is defined as everything
  between the "## IN" header and the next "## DEFERRED" header, and the per cycle cut
  sections plus the OUT list all sit between those two, so they are silently swallowed into the
  IN scan. The 2026-08-24 fix reworded the bullet honestly rather than narrowing the parser;
  the parser half is this row. (Tech: ReleaseScopeContractTests.ExtractInSectionLines; either
  bound the region at the next "## " header of any kind, or give each cut its own IN header,
  and add a pin proving an OUT bullet is not an IN box.)

- [LW-309] 2026-08-24: The ability data channel we parked after it corrupted the game in June
  is safe to reopen, and a stranger proved it. The Knight Overhaul mod on Nexus ships the exact
  same override file we abandoned, and inspection shows why theirs works: their table is the
  current vanilla table with exactly five rows changed, nothing stale. Ours corrupted because
  our build drifted from vanilla in cells we never meant to touch, which is a build discipline
  problem we already solved for ability names (patch_ability_names.py rebuilds from pristine
  vanilla and refuses to deploy unless exactly the intended cells differ). Port that discipline
  to overrideabilityactiondata.nxd and the parked ideas wake up: MP costs on weapon granted
  commands, custom damage formulas, self inflicted statuses as costs, new abilities minted in
  unused slots instead of hijacked. (Tech: their file = base + rows 138-142 only, verified by
  cell diff against working/base_action.sqlite; formulas 67/69/80/104 and per-ability MPCost/
  CT/InflictStatus all set through the override table; unused ability slots 219/220/357 and
  RSM slots 484/485 filled with new named abilities; see bloodpact-ability-corruption memory
  for the June failure this supersedes.)

- [LW-299] 2026-08-21: The Staff of the Magi keeps a fallen ally out of a crystal by pushing the
  death timer back to three hearts over and over, every fraction of a second, for as long as the
  body lies there. The game itself has a far simpler way of saying "this one never crystallizes",
  and we found it: the first battle switches the timer OFF entirely rather than holding it high,
  and the switch is one value written once. Rebuild the staff's power on that instead. It is the
  same promise to the player and a much quieter mechanism, and it stops us wrestling the game
  every tick for something the game already knows how to do. OBSERVED live 2026-08-21 with the
  owner reading the screen, and the ledger row is AWAITING HIS FLIP rather than proven: hearts
  vanish the instant the off value is written, the game does not put it back, and writing a real
  number returns the hearts exactly as they were, which is what lets the protection lift the
  moment the bearer dies or unequips. Two things stay untested and both could still sink it,
  whether the suppression survives to the moment a body would actually crystallize, and how a
  suppressed unit behaves through revival and battle exit. (Tech: the countdown is combat slot
  base +0x07, band entry -0x15. 0xFF is the off state, observed on EVERY unit of the no-crystal
  first battle against 3 on every unit of a normal one, and confirmed by write both directions on
  a guest mid-countdown 255 -> hearts gone -> 2 -> two hearts back. Replaces Sanctuary's per-tick
  re-pin, and with it the dead-streak guard, the write budget and the fight with the countdown;
  the ally filter and the lift-on-bearer-loss rule stay. Petrify never arms the counter at all, so
  stone units need no handling. Two open questions before building: whether 0xFF is a true
  sentinel or merely greater than 3, testable with one write of 0x7F, and revive plus battle exit
  behaviour on a unit that was suppressed then restored. Instrument:
  tools/probes/crystal_counter_probe.py, verbs dump/diff/suppress/set. Ledger row
  [crystal-countdown-off-switch].)

- [LW-273] 2026-08-18: When a unit falls in the game's very first battle, the game draws NO
  three hearts countdown at all, the corpse just lies there with no permadeath clock, which
  proves the engine has a native no hearts mode (owner observation 2026-08-18). Our Staff of
  the Magi's Sanctuary signature wants exactly that look: today it protects the fallen by
  HOLDING the heart counter at 3 forever, so the player stares at a countdown that never counts,
  which reads as a bug rather than a blessing. Research how the first battle suppresses the
  hearts render entirely (a battle flag, a per unit flag, or the counter field holding some
  sentinel the renderer treats as no clock) and, if the lever is per unit and safe, switch
  Sanctuary from pinning 3 to removing the hearts outright. Probe first: find the field diff
  between a first battle corpse and a normal battle corpse at the same offsets. (Tech:
  Sanctuary.cs pins Offsets.ACrystalHearts, band entry -0x15 == combat base +0x07, guarded W8
  of 3, idempotent; LivingPoach.Despawn.cs shares the same field; candidate levers are a
  sentinel value in that byte, a nearby render gate byte, or an ENTD per unit flag, none
  verified; check LIVE_LEDGER.md before building on any of them.)

- [LW-6] 2026-07-04: Slayer's Reliquary, the post-release headline bet: weapons remember WHO
  they killed.
  Design: docs/RELIQUARY_DESIGN.md; acceptance: docs/RELIQUARY_AC.md. Phase 0 probes COMPLETE
  2026-07-05 (boss key = per-encounter canonical nameId; same-form minions collide; withdrawal
  bosses like Zirekile Gafgarion produce no death edge, exclude or special-case; a retried
  boss kill must dedup by key). Phase 1 (Marks + card story) SHIPPED 061e36c, awaiting live.

- [LW-32] 2026-07-05: Rebuild Marks in two waves: first a chronicle that RECORDS what every
  weapon does, then an interpreter that turns those records into titles, so new titles can
  be awarded retroactively from history already collected (owner architecture direction).
  Wave 1 = aggregate counters per weapon and victim class plus a notable-events log (first
  blood, first of each class, boss keys, battle-enders, milestones); the victim snapshot
  already captured at every credit edge makes collection nearly free; KillTally-pattern
  persistence, deploy-preserved, raises LW-29's stakes. Wave 2 = a pure interpreter (policy
  only, fully unit-testable, no live risk). The killer property is RETROACTIVITY:
  interpretation can iterate forever without wronging a save. Owner is on the fence about
  Mark titles doubling the +N system; record-first defers that question (candidate rule: a
  Mark requires PLURALITY of kills, not raw count). Supersedes/absorbs the Phase 1
  legends.json shape when picked up; ties to LW-6.

- [LW-139] 2026-07-27: The mod can now know who acts NEXT, and several features that currently
  guess at turn state from present tense signals could be rebuilt on it.
  Why it matters: every turn related mechanic in the runtime today asks "who is acting right now"
  and answers with either the engine's actor pointer (which parks on units that were merely struck,
  the LW-138 bug) or the per unit menu open flag (which is blank during action resolution, the hole
  LW-138 fell through). Neither can answer "who is up after this one", so anything wanting to act
  BEFORE a turn opens has been impossible. The CT and Speed clock model proved live on 2026-07-27
  reproduces the game's own Combat Timeline, so that answer is now available for the cost of a band
  walk, with no new write surface and no new address.
  Candidate consumers, each its own card when picked up: Provoke's hide (LW-118, the reason this was
  proven at all), Iai and Kobu and Mushin's turn detection, the Attack card's turn owner resolve
  (LW-87 anchors it on the menu open flag), and ExtraTurn, which already WRITES CT and could read
  the same field it slams.
  Not free, and the limit is sharper than the headline: only the NEXT unit to act is trustworthy.
  That part scored 15 of 15 across two sessions and a game restart. The DEEP forecast is not
  trustworthy and the two runs disagree about how far it holds, one staying exact for six turns and
  the other going wrong at three, so any consumer wanting more than one turn of lookahead has to
  earn it with its own measurement rather than inheriting this row. Shared code would want one turn
  order helper rather than five copies. (Tech: band +0x25 CT and
  +0x24 Speed; ledger rows dated 2026-07-27; harness tools/probes/provoke_lookahead_probe.py.)

- [LW-179] 2026-08-12: The battle menu's Attack row should wear the weapon's name from the
  very first menu of a battle; today the first open (and the odd mid-battle blip) shows the
  plain Attack text because the mod refuses to paint when it cannot prove whose menu is
  open, the fail-closed rule that cured the 2.3.0 wrong-name bug. Owner ideal stated
  2026-08-12: never see the plain text. Fix shape agreed in session: ride the LW-139
  turn-order helper (the proven next-actor clock) as a fallback owner at battle open, with
  fail-closed kept as the outer gate (prediction and flags disagreeing composes vanilla,
  same as today), and note the BridgeFail lane (owner resolved but the roster match failed)
  is untouched by that helper and needs its own look before "never" is honest. The owner
  signalled this may be picked up very soon; the three sightings and their log lines are in
  the LW-171 session log (two NoOwner at battle load, one BridgeFail mid-battle). Make the living poach look and sound closer to the game's own poach:
  the owner watched one live and called it eerily close but not identical, the removal
  timing is slightly off and the game's poach sound never plays. Step one when picked up is
  a read-only tape of a VANILLA poach with the existing text-hook head sampler running, to
  inventory exactly what the engine fires (banner call, timing, whatever sits next to the
  sound trigger); that ten minute tape decides whether the mimicry is a weekend or a wall.
  Sound triggering from in-process has no proven lane today, and calling engine UI routines
  cold has walled before (the numeral popup), so this is a real RE arc, not a knob. Open
  design question for the owner first: perfect mimicry may be undesirable, since a slightly
  distinct presentation is how a player and a debugger tell a mod poach from a vanilla
  poach at a glance.

- [LW-173] 2026-08-12: The game has a native message box on the WORLD MAP (clean modal, story
  text styling, F/Enter Close), and owning it would give the mod an in game voice outside of
  battle, replacing the raw OS message box the fingerprint guard uses today for stand down
  and conflict notices, and opening a home for things like the non English counter warning.
  Owner discovered a reproducible trigger 2026-08-12: traverse to a story node, start the
  battle, FLEE back to the world map (the unit now stands ON the story node), then select the
  next, not yet accessible story node; the game answers with the modal "You cannot travel to
  the selected location at this time..." (screenshot banked in the owner's Monosnap). RE
  leads when picked up: the dialog text almost certainly flows through the FnSetTextString
  family the PromptSwap hook already taps (watch the hook's head sampler while triggering
  it); the RTTI scan of the live image names candidate window classes (FFTOItemConfirmWindow
  and siblings); a show routine plus text holder pair would make this a callable surface, the
  same shape as the battle callout bubble arc.

- [LW-306] 2026-08-22: Write one guide, shared with the ColorCustomizer mod, covering how a
  weapon's colours get changed end to end: the menu icon and the battle sprite, offline bake and
  live repaint, and which knobs belong to which repo. Owner asked for it to be written jointly with
  that repo's session rather than assembled from one side. Start it only once the battle repaint is
  actually working in game. (Tech: LivingWeapons owns the battle_wep_spr palette path, the FFTPack
  file 71 block, the two resident banks and the per swing repaint plan; ColorCustomizer owns the
  icon ramp engine and RelativeShadeGenerator Preserve mode, which this repo's transform was
  derived from. Peer session at the time of writing: fftcolorcustomizer-8c.)

- [LW-178] 2026-08-12: Settle whether the mod loader merges table XML rows per PROPERTY or
  per ROW, because compatibility verdicts hinge on it when two mods edit different fields of
  the same row. The loader's own template comment claims per property tracking ("only
  properties actually included will be tracked as edited"), while the LW-77 Proven ledger
  row showed load order cannot fix the item table conflicts we tested; both can be true if
  the tested conflicts shipped the same properties, so the grain question is genuinely open.
  Concrete test case sitting ready: Ramza Overhaul (Nexus 110) and this mod both ship
  JobData row 4 with DISJOINT properties (their EquippableItems grant vs our
  CharacterEvasion 8); install both, restart, and read Ramza's chapter job in game: both
  effects landing proves per property, one missing proves per row. The answer sharpens the
  compatibility grid's caveat wording and could soften the fatal six's phrasing.

- [LW-177] 2026-08-12: Teach the mod to detect the conflict classes the compatibility
  survey exposed and say the truth in game, the guard extension half of what LW-169 carried
  before the grid narrowed it. Candidates from the survey session: an item lane guard (we
  know every row we ship; detect foreign item rows at arm time and name the loss instead of
  silently fighting, covering the only FATAL class), a text anchor census message when
  another mod's cells cover ours (pool coverage already counts sites), and a grant time
  record verify for command rewrites beyond recs 8 and 9 (Knight Overhaul rewrites rec 7,
  exactly where ShadowBlade injects for a Knight wielder). Same guard family and born
  disarmed discipline as LW-112's kit lane. Survey provenance: all 97 published mods, owner
  session 2026-08-12: 6 fatal item table mods (ids 115, 107, 84, 75, 72, 64), roughly 25
  with potential conflicts (job row overlaps, command record rewrites, text cell overlaps,
  non English counters per LW-101), 66 clean; GenericJobs (id 34) source audited clean; the
  per mod verdicts for the middle bucket live only as classes, the durable record is
  docs/COMPATIBILITY.md.
  2026-10-17 survey refresh: Knight Overhaul (Nexus 113) rewrites the Knight command record, the
  record Shadow Blade injects into, and the kit lane watches only records 8 and 9, so that pairing
  degrades Shadow Blade with no in-game notice. docs/COMPATIBILITY.md says so plainly; a third
  watched record is the fix this row already names.
- [LW-235] 2026-08-14: The mod has no way to learn which weapons players actually use, and the
  one time real user logs arrived they answered nothing. Owner question 2026-08-14: which
  weapons are used and which are ignored. The six submitted Reloaded console logs contain ZERO
  lines from this mod, across two different users and including two logs that run more than
  twenty minutes into play, while five other mods log to that console freely, so support
  requests built on those logs have never carried our evidence. The only weapon data that
  existed came from flight tapes one user happened to send, and it is a chapter 1 party holding
  the gear the game gives you (four Vagabonds, two Cutpurses, one Quicksilver), which is not a
  preference signal at all. Worth noting the mod already persists exactly the right data:
  kills.json in the save dir is a per-weapon tally across a whole playthrough. The question is
  whether an opt-in way exists for a player to send it, what it must not contain, and whether
  the console silence is itself a bug worth fixing first since it costs support time today.

- [LW-8] 2026-07-05: Clicking inside the console window can freeze the whole mod for minutes
  (Windows QuickEdit suspends the thread).
  About 3 minutes observed mid-battle; kills, growth, and toasts all stall (the census
  "hang" was this). Candidate: async/queued console sink in FileConsoleLogger (the FILE sink
  stays synchronous, it is the evidence chain). Until then read livingweapon.log, not the
  console.

- [LW-28] 2026-07-05: One deploy LOST the kill tally and legends files even though the
  deploy script preserves them; it is intermittent, a loud failure check now ships, but the
  two underlying causes are still unfound.
  The 17:54 launch logged "No kill tally was found on disk"; the 82-kill tally and the
  Beastbane Mark were gone; the %TEMP% livingweapon_preserve dir no longer existed; the
  17:0x deploy preserved the same files fine. Second anomaly on the same evidence: the
  17:1x session flushed exit tapes at 17:37/17:41 but kills.json kept its 13:45 timestamp,
  so exit-edge tally saves may not have written that session. Investigate both. The loud
  post-restore existence check SHIPPED 2026-07-11 (Get-LostPreservedItems in
  tools/pipeline.ps1; BuildLinked fails red before deleting the backup dir, and the catch
  path re-restores from it). Owner declined tally reconstruction for now (tapes and
  prev.log carry the counts if ever wanted).

- [LW-255] 2026-08-17: The black box records what the mod SAW but not what it DECIDED, so any
  check that quietly says no leaves no trace at all and the next person has to re-derive the
  refusal by hand. Proven the expensive way on the LW-233 live drill: the retry detector declined
  to fire, and because it only ever writes a record on SUCCESS, settling which of its gates
  refused took a code read plus stopwatch arithmetic against the tape instead of a single line
  saying so. Three gaps, worst first. One, a detector that can decline records nothing when it
  does; it should name the gate that refused. Two, values the code DERIVES are absent, so a
  reader has to recompute them from the raw inputs and can get it wrong. Three, records are
  written only on change, which makes a quiet stretch and a stalled tick loop look identical on
  the tape, and the LW-233 drill carried an 8.6 second hole of exactly that kind across the
  game-over screen. The generalisation worth keeping is that a silent refusal is the expensive
  kind of silence, because the moment anyone cares about it the evidence is already gone.
  (Tech: RestartSentinel.LogOpenEdge is the only recorder call in the class, so the miss paths
  in Tick / PresentRevive / ProcessStash and the ShouldOpenLatch grace refusal are all invisible;
  onField, the derived gate input feeding the sentinel's inLiveish, is absent from the tape and
  has to be re-derived from the mode records through BattleState.OnField; a sparse heartbeat
  record would separate a starved tick loop from a quiet one. Evidence tape
  tools/probes/tapes/lw233_death_retry_live_20260817.jsonl. Vocabulary is FROZEN per the
  log-facelift note, so new record types need that constraint respected.)

- [LW-125] 2026-07-22: Three weapons now grant a command to their wielder, and all three do it with
  the same hundred and thirty lines copied out three times. The next one makes it four.
  Why it matters: the duplicated part is not the interesting part. It is the roster walk that finds
  the wielder, the release path, and the learned bit hold, and every copy is a place a fix has to
  be applied again and can be forgotten. ShadowBlade.cs has carried a FOLLOW UP SEAM comment since
  it shipped saying the shared core should be extracted once it is live verified, and Provoke has
  now made it a third copy rather than a second.
  Deliberately deferred, not overlooked: extracting the core means editing two shipped modules that
  players are running, and doing that inside a commit that also adds a feature is the exact diff a
  reviewer should distrust. It is its own stage. The pure decisions were already shared rather than
  copied (ProvokePolicy delegates to ShadowBladePolicy for record resolution), so what is left is
  the stateful half only.
  Watch for when it happens: Barrage.Policy.InjectSlot and ReleaseSlot call
  ShadowBladePolicy.NeedsInject, so the dependency between those two files already points the wrong
  way, and the extraction is the natural moment to straighten it. Do not fold the table write half
  of Provoke into any shared core: it is keyed on a different arming condition on purpose, and
  fusing the two lifecycles is the bug LW-123's plan review was written to prevent.

- [LW-124] 2026-07-22: Audit every band walk in the runtime for staged cutscene units. The engine
  parks them in real seats with sane stats and real map positions, so a walk that only checks for
  sane values counts them as party members.
  Why it matters: five of them sailed through a position-based filter on 2026-07-22 and would have
  been treated as live party members. Band.IsValid does not test the engine's own hide gate, so
  every existing caller is exposed, not just the new one.
  State: LW-123 adds a Band.IsOnField predicate (IsValid plus combat +0x01 not equal to 0xFF) and
  uses it in the new code only. Folding it into IsValid touches about ten call sites and each one
  needs a think about whether excluding an off-field seat changes its answer, so it is deliberately
  a separate pass.

- [LW-126] 2026-07-22: When an enemy you shouted at with Provoke gets mind-controlled onto your
  side by Galewind's Puppeteer, the shout does not notice the takeover and hangs on until a
  failsafe fires; it should recognise the takeover and let go right away.
  Why it matters: a Puppeteered enemy is now driven by the player, so it never takes the AI turn
  Provoke is waiting on. RE-READ 2026-07-27, because this row's reasoning was written against a
  shipped behaviour that no longer exists and its conclusion flipped: it used to argue the gap was
  harmless under the then-hypothetical single enemy mode and would only become REQUIRED if the
  whole phase fallback shipped. LW-127 shipped the single enemy rule, so the harmless case is the
  live one: a dominated enemy is not the acting AI unit, so the ranking stops naming it as next and
  the party is revealed rather than held hidden. What remains is a stranded ARMED hold that sits
  there until the ninety second watchdog clears it, which logs a WARN meaning "a real bug" and so
  produces a false bug report rather than a broken battle. A direct release is still the right fix.
  State: LW-123's disabling-status release catches engine Charm (status id 34), but Puppeteer
  dominates through the agency bits (combat +0x05 and its shadow +0x1EE), not the Charm status, so
  a domination slips past. The agency read mask is a LIVE_LEDGER Uncertain row (inferred from
  scouting Dicene's fftivc.unitcontrol, never live-proven as a readable signal), so this is gated
  on proving that mask first; once proven, add the agency bit to Provoke's disabling check (Tuning
  holds the id/mask set). The same case is a watch item on the LW-123 live pass.

- [LW-128] 2026-07-22: Provoke pops an empty speech bubble over the caster (Ramza's portrait, no
  words) on cast; fill it with a taunt, since Provoke is literally a taunt.
  Why it matters: that bubble is the game's own callout for the ability, Embrace's vanilla quote
  slot gone blank after we renamed and repointed the ability, and it fires exactly on cast, so it is
  free thematic flavour going to waste. Owner asked for a jeer in there 2026-07-22.
  State, CORRECTED 2026-07-23: this ticket previously said the mechanism rests on a Proven ledger
  row. IT DOES NOT, and the correction matters more than the feature, because acting on the old
  wording would have meant building on a premise nobody has signed off. Both callout rows in
  docs/LIVE_LEDGER.md (the show flag hijack, and the injected call with piggyback timing) sit in the
  UNCERTAIN section, dated 2026-07-02. They are strong: the owner eyewitnessed custom text rendering
  on screen twice, and roughly ten in process call runs went by without a crash. They are still not
  a Proven row, and only the owner moves one.
  What that costs: this is a full build arc with its own live premise proof, not a small change
  riding an established mechanism. The evidence is also OLDER THAN THE 1.5.1 RE-ANCHOR, and the
  addresses involved (a 0x436B07D058 holder, an in process call to 0x14028F720) were never
  re-verified after it, so step one is simply reading whether that holder still validates on this
  build. The spike that proved it (ShowSpike) was DELETED from the tree by LW-67, so the code has to
  be written again rather than revived, and commit b6a1ffe is where the original lives.
  Candidate lines drafted (Eyes on me curs / Come break upon my shield / Your mother swung truer);
  consider rotating a few at random. Polish; rides after the core arc and the usable by AI fix.

- [LW-23] 2026-07-05: When one kill earns two popups (a deed and a tier-up), only the deed
  shows and the tier-up popup is lost (owner observed).
  Ramza's gun earned Beastbane and the deed toast delivered, but the same blow was kill 2
  (tier-up to +2) and no tier-up toast appeared. Investigate contention on the single
  delivery slot (queued and dropped? overwritten?) and make both deliver in order.

- [LW-24] 2026-07-05: The tier-up banner can appear a turn late, while the NEXT unit is
  already acting; the locked policy is deliver on the earner's own turn or not at all.
  Owner screenshot: the Stormbrand wielder's 3rd-kill banner appeared during White Mage
  Collys's turn. POLICY LOCKED (owner, 2026-07-05): fire the UI text only during the earning
  unit's own wait turn; if credit resolves after that window, SWALLOW the message (the card
  and tally still record the growth, so nothing durable is lost). Implementation: compare
  the toast's earner to the current turn owner at delivery time and drop on mismatch;
  turn-owner detection has known traps (the hover-follower struct is NOT the turn owner), so
  use the durable turn/register state. Interacts with LW-23: within the correct window,
  deed and tier-up toasts still need ordered delivery, not mutual starvation.

- [LW-39] 2026-07-06: Two party units with identical stats (twins) look the same to the
  mod, so it refuses to guess and their card shows plain vanilla text; give it more
  identifying fields so twins tell apart.
  Owner hit it live: two units at identical level and hp/maxHp made the resolve refuse, and
  the register fallback then dressed Ramza's Attack row in the Spark Rod wielder's dossier;
  the fallback is now removed, so twins simply show vanilla (fail closed by design). Fix
  direction: extend the condensed turn-queue fingerprint with more struct fields; the probe
  dump shows brave/faith-like u16 candidates in the cursor struct needing offset
  verification (turn-owner-probe lines, livingweapon.log 04:0x).
  LW-87's flag-owner resolve (2026-07-21) already gives this surface partial relief: the
  nameId bridge tells identical-stat twins apart on the Attack card now, though the growth
  and locate surfaces still need the fingerprint extension planned here.

- [LW-43] 2026-07-07: The Outrider pistol's twin-gun perk is slow to kick in for a SECOND
  wielder when someone else already has it running (it does apply eventually; owner saw
  the lag live 2026-07-07, not a correctness bug). (Tech: Gun Slinger, Outrider Pistol
  id 71.)
  Suspect the per-wielder locate/write cadence serializes or throttles with multiple
  carriers: check the Gun Slinger signature's tick loop and whether its locate stops at
  the first wielder per tick.

- [LW-253] 2026-08-17: The twin weapon takes up to ten seconds to visibly appear on the status
  page when the player bounces in and out of menus, and the owner asked for about two if it
  comes cheap. The wait is polling arithmetic, not a bug: the twin pass runs once a second and
  the safety rule wants two consecutive safe-screen readings before writing, so every dip back
  into the equip screen resets the count. (Tech: raise the gunslinger phase cadence from 30
  ticks toward 10 with an in-battle modulo guard so the in-battle re-assert keeps its
  established one second rhythm, or sample PartyBrowseFlag per tick and decide per pass; the owner accepted
  the current feel for the shipped cut, so this rides a normal round with its own verify.)

- [LW-254] 2026-08-17: The phantom-pistol refund detector shipped watching but not acting: it
  names every refund the game issues for the conjured twin and records the full evidence, and
  the correction that would take the phantom back out of the inventory stays unbuilt until
  those recordings prove the detector fires on phantoms and nothing else. The adversarial
  review rejected arming it because the inventory count is one global number per item, so a
  teammate's lawful pistol return inside the same second could be blamed on the twin and a REAL
  pistol destroyed. (Tech: watch-mode lane in GunSlinger.Reconcile.cs, flight record type
  twin-refund; arming needs the hardened rule from the plan-v3 review verdict: menu context
  required, rise exactly one, both sack reads valid, no other row's gear transition touching
  the id in the window, plus one round of watch tapes showing the fire-set equals the phantom
  set. The interim cost is accepted phantom inflation, benign direction only.)

- [LW-12] 2026-07-04: Three weapon abilities (Maim, Larceny, Ricochet) watch the battle for
  their trigger moment in an older way that can blink and miss it; upgrade them to the newer,
  reliable watching style when those files are next touched.
  (Tech: migrate the lossy-detection siblings to the cache-plus-rearm pattern, the same
  upgrade the Kobu raise detection already got.)

- [LW-180] 2026-08-12: The kill-credit census warned twice in one battle that a fielded enemy
  vanished from the battle band ("its kills may go uncredited"), both times in an undead
  fight, and nobody knows which seat or why; the one kill actually taken that battle
  credited fine, so this is a caution light, not an observed loss. Suspect: undead final
  death or dissolution removing or hiding the seat, which would make this a false-alarm
  class; a real coverage hole if not. Evidence banked from the 2026-08-12 Provoke
  regression session: tapes flight_20260812_211943_battle-exit.jsonl (first WARN 21:20:42)
  and flight_20260812_213229_battle-exit.jsonl (second WARN 21:32:13; the flush census
  still shows the killed undead 851 seated at s8, so corpses staying seated is not the
  vanish). Fold the verdict into LW-76's healthy-session WARN triage if it proves benign.

- [LW-175] 2026-08-12: If a poached corpse's removal stays blocked for about 30 seconds the
  mod gives up, and the corpse can then still crystallize on top of the already-banked
  carcass, the exact double payout the fix batch set out to remove, just delayed; the code
  comments claim this cannot happen, and the give-up clock even burns while the game is
  paused. Confirmed (minor, four independent confirmations) by the ac43327 adversarial
  verify round: at PendingTickCap (~900 ticks) LivingPoach.Despawn.cs drops the pending
  entry, the crystal pin stops at the vanilla start value 3, and the engine's countdown
  simply resumes; the class doc at lines 29-31 ("never the double-payout") and the give-up
  Warn both oversell the guarantee; and the cap counts raw 33ms Engine ticks that keep
  running through pause, menus, and mid-battle dialogue while every Transient blocker is
  frozen game state, so a longer-than-30s pause defeats the watchdog outright
  (KillTracker.Corpses gates its own pending age on onField polls for exactly this reason).
  Fix shape is an owner call: gate the cap on progress-capable frames, or keep pinning with
  retries stopped until a Permanent read or battle end, or accept the bounded residual and
  make the doc and the Warn tell the truth. Rides with it, from the same round's notes: the
  new refusal log claims crystal conversion is caught by the Dead-bit check while the doc it
  replaced said crystal also sets composed bit 0x01, both cannot be right, and no ledger row
  distinguishes them; the owed live pass should watch one corpse crystallize while pending
  to settle what +0x46 and the Dead bit actually do.

- [LW-170] 2026-08-12: Units wearing the rebalance's Float granting gear render in a strange
  half floating state: standing flush on solid ground as if not floating at all, hovering
  slightly only over water, and the float look overrides the critical kneel (the owner saw a
  critical Agrias over a water tile in an upright standing pose players never normally see,
  hovering above the water).
  Owner observed live 2026-08-12 on enemies and on Agrias. The items were the mod's own innate
  Float pieces: Sunsteel Helm (id 149, vanilla Golden Helm) and Empyrean Robe (id 206, vanilla
  Luminous Robe, EquipBonus row 46); enemies picked them up through level lists, so the state
  was common in late fights. UPDATE 2026-08-13 (LW-188): both named pieces lost Float in the
  cutback, so they no longer reproduce this; the remaining repro gear is the vanilla Float pair
  (Featherfoot Boots row 46, Envoutement row 66) plus the Cursed Ring unique (row 56), making
  exposure rare instead of common. The render question itself is unchanged and this row stays
  open on it. Cosmetic only so far; nobody has verified whether the gameplay half
  of the innate Float (Earth/Quake immunity, trap immunity, terrain walk) actually works on
  these pieces, and that check should ride the diagnosis, since a broken gameplay half would
  reclassify this from cosmetic oddity to broken rider. Diagnosis leads when picked up:
  compare against a vanilla native Float source (status layer Float via the proven band bytes,
  or any vanilla float gear if one exists) to learn whether this is the game's own rendering
  of equipment granted float or something about the EquipBonus lane; the render side hover is
  pure node data (node Z = negative 12 times height, one extra height unit with FLOAT, per the
  teleport/float ledger row), so a probe can read whether the game ever re stamps Z for these
  units on land. Prior that may bite: a redefined EquipBonus row rewrites every user of that
  row (see the row override audit memory).

- [LW-111] 2026-07-21: Stepping off a chocobo mid turn keeps the Speed bonus until the turn ends,
  and the item text promises it drops immediately.
  Owner observed it live 2026-07-21 (step 5 of the LW-100 pass): dismounting and moving away held
  Speed at 15 for about 23 seconds, until the turn was committed. Most likely game side rather
  than ours: the mod re-reads the ride bit about ten times a second and reverts on the first tick
  it sees clear (the same session showed an instant revert when the move was undone), so a hold
  that long means the game itself keeps combat +0x1B4 bit 0x80 set until turn commit. Bounded to
  one turn, capped at natural plus 3, self healing, non compounding. Two cheap outcomes: a
  LIVE_LEDGER Uncertain row for when the game clears the ride bit, and a wording fix in
  data/items.json, which currently claims the bonus reverts on dismount.

- [LW-103] 2026-07-21: After a battle ends, the party list and the leftover battle data disagree
  about which weapon a unit is holding, and they stay disagreeing until the next battle; nothing
  visible breaks, but nobody has explained it yet.
  Seen post-deploy 2026-07-21 (LW-87 live pass): from the 14:06:31 battle end to the end of a
  10 minute recording, the roster read weapon 42 for Ramza while his frozen band entry still
  read 37, about 5.4 minutes of steady disagreement (5439 probe ticks, not a blip; the tape
  prints only changes, so the single line at battle end was the ONSET). Harmless today on every
  known surface: the Attack card never composes out of battle (Engine.Tick returns early), so
  the mod logged zero warnings all session, and CursorGate would refuse the mismatch anyway.
  Worth an explanation before anything new trusts a roster read taken out of battle: the likely
  cause is the post-battle equipment reconcile moving the roster while the band stays frozen
  (the LIVE_LEDGER row on broken and stolen gear committing at battleMode 0 is the neighbouring
  mechanism), and the owner may simply have re-equipped in the menu. Check GunSlinger.PrepRoster
  first if it is ever picked up: that lane DOES read roster hands out of battle, though it reads
  the roster (the live side) rather than the stale band. Instrument already in tree:
  tools/probes/cursor_resolve_probe.py.

- [LW-181] 2026-08-12: Redesign Bulwark as "Upheaval" (pivoted 2026-08-13, supersedes the
  earlier enemy-turns-only toggle sketch): instead of a wait stance that quietly bars the
  tile behind the knight, the player aims Bulwark at the ground like Provoke's sibling,
  anyone standing in the zone is visibly thrown clear, and the vacated ground then refuses
  entry. The wall stays always-on and side-blind (vanilla tree semantics), so the game's
  own red no-entry cursor and move previews keep telling the truth and the old turn-toggle
  premise beat is not needed. Built the Living Poach way: recreate the game's knockback
  from pieces already proven one by one (flinch-with-displacement animation page plus the
  Proven teleport triple-write), delivered through the granted-command lane (the Provoke
  hijack recipe), whose donor resolution also supplies the flash and sound poach had to
  skip. Owner-directed due diligence before any build: one last shove probe, the
  acceptance run of knockback_probe.py shove, judged poach-style for convincingness, and
  it must settle four unknowns: which flinch page (0x37/0x38) maps to which push
  direction, whether those page ids transfer to a second sprite class (the anim catalog is
  time mage male only, LW-114), whether the post-flinch freeze needs an idle re-request,
  and how the stale-Z float transient reads on a height-changing shove. Stretch probe if
  the imitation reads cheap: the global differential diff during a real engine move
  hunting the mover's trigger (the LW-58 one-byte enrollment shape); from rest the mover's
  block is settled dead across three rounds, in flight the destination rewrite is honored.
  The other unproven premise is tile-cast detection (how the mod learns which tile the
  player aimed at; Provoke's mark rides the target unit, an empty tile has none), which
  needs its own probe. The two comms fixes from the original capture (the silent plant
  refusal and the "full wait" card wording) stay owed only if this redesign walls and the
  shipped full-wait Bulwark survives; until the arc runs, the shipped Bulwark stays as-is.
  SHOVE ACCEPTANCE RUN 2026-08-13, owner live, three of the four unknowns settled GREEN in
  one battle (the fourth still owed): the victim was a Red Panther, so the second sprite
  class question answered itself, both pages 0x37 and 0x38 played as a plausible flinch on
  a monster class ("works like a charm"); the direction map question resolved as MOOT, the
  flinch is too brief to read a direction at game speed so page choice is not load bearing;
  the freeze tail self-heals exactly as documented, the pose locked after each shove and
  the victim's own turn-open re-stamp fixed it, the panther then took a normal turn with NO
  rubberbanding, meaning the engine fully adopted the shoved tile as the real position.
  Still owed before the build arc: one shove onto a height-changing tile (the stale-Z float
  transient read) and the tile-cast detection probe, which remains the one wall risk.

- [LW-114] 2026-07-21: Finish mapping the animation flipbooks: one sweep per sprite class so
  every signature can pick its pages by fact instead of folklore.
  The time mage sweep (tools/probes/anim_catalog.jsonl, all 128 pages owner labeled) proved the
  ids are per sprite class and killed the old decode labels (its "crouch 0x34" is the full
  death animation). Wanted next, about ten minutes each with anim_poke_probe.py sweep: a KNIGHT
  or other weapon carrier (the same-as-previous runs at 0x4b-0x55 and 0x5d-0x63 are suspected
  per weapon category swing variants that a staff collapses; a sword should fan them out), a
  FEMALE sprite, and a MONSTER (chocobo first, since summons and mounts care). Protocol notes
  that earned their keep: sit on your own unit's open menu so CT freezes and the guinea pig
  stays idle; labels append per entry so a freeze loses nothing; the book ends near 0x79.

- [LW-120] 2026-07-22: Play an animation at a dramatic moment, so a weapon coming alive looks
  like something instead of only printing text.
  Now legal: the animation register is Proven (owner flip 2026-07-21). Candidate first moment is
  a tier up, which the mod already detects (BannerToast) and which the owner's catalog already
  has a page for (0x1c, the level up leap). Work needed: the render node walk currently lives
  ONLY in BodyDoubleSpike.cs behind #if LWDEV, so production has never touched a node; moving it
  into shipped code through the guarded Mem layer and the IGameMemory seam is the actual task,
  after which the write is one guarded u16 (page + 1 into node +0x10). Theater only, so the risk
  is a wrong pose for a few seconds and the engine re-stamps at the unit's next event. THE REAL
  GATE: page ids are per sprite class and only one class is swept, so either finish LW-114 first
  and use pages that agree across classes, or fire only for mapped classes and skip the rest
  (fail closed, house style). Rides /build-lite plus an owner live pass.

- [LW-121] 2026-07-22: A weapon that plants its wielder somewhere nothing can reach: proven to
  work, and degenerate unless it costs something.
  Found by deliberately breaking a guard (battle_toolbag.py warp onto a treetop). The engine
  accepts placement on terrain it would never path a unit onto: correct perched render, real
  height readout, turn marker, health bar, valid selection diamond. The unit is then STRANDED,
  since the pathfinder refuses to route out of a tile it would not route into ("At present, can't
  move to any other tiles"), but it acts normally and its ranged attacks connect, while the enemy
  AI degrades gracefully, backing off and milling about without engaging or crashing. So the
  mechanic writes itself: trade all mobility for an unassailable firing position, which is a
  genuine tactical bargain rather than a cheat, PROVIDED it is bounded. Unbounded it is a
  degenerate strategy, since melee simply cannot answer it. Design levers to price it: a duration
  after which the wielder is returned to the ground, a one per battle limit, an accuracy or damage
  penalty while perched, or making the descent the wielder's whole next turn. Height matters
  mechanically in this game (damage, accuracy, range), so the perch is worth more than the
  novelty suggests. Open before any build: whether melee genuinely cannot reach (the AI's retreat
  implies it but was not directly tested), and whether an ability that moves a target could shove
  the wielder off.

- [LW-116] 2026-07-22: Knockback: we can already teleport a unit, but a real shove (the Rush
  effect) is three lanes of work and none has run yet; the probe is armed for two of them.
  Lane 1, in the bag pending an eyeball: shove = the owner's cataloged flinch-with-displacement
  page (0x37/0x38) plus the proven teleport triple-write, occupancy refused, Z left to the
  engine's own re-stamp (knockback_probe.py v2; v1 from June predates the render crack and rode
  the dead ct_probe harness). Lane 2, a pure table experiment nobody has tried: assign a Dash
  family formula id to a test weapon and see whether hits push natively; v1's durable note
  applies, proc rates are Denuvo locked so the native rate is the rate. Lane 3, the discovery:
  run knockback_probe.py watch on a victim while the owner Rushes them for real, hunting any
  field that changes BEFORE the world coords start marching; that would be the engine's own
  shove ORDER, the animation register shape again, and lane 1 retires. Wanted on the same tape:
  one plain walk and one non knockback hit for contrast.
  LANE 3 SUCCEEDED FIRST RUN 2026-07-22 and outgrew the ticket: the tape found the engine
  ordering the move 18ms before anything visibly moved, and the same machinery drives ordinary
  walking, so this is the MOVEMENT api and knockback is one mode of it. Read banked as a
  LIVE_LEDGER Uncertain row, tape preserved at tools/probes/tapes_knockback_20260722.jsonl.
  THE WRITE LANE IS DEAD by this method, settled the same night in THREE rounds (one field, then
  three, then the engine's whole fourteen field burst with the is-moving flag last; every value
  stuck untouched for two seconds and nothing moved, and the engine did not even reset them, so
  nothing reads that block at rest). Earlier note, kept because the correction matters: the destination
  alone is inert but sticky, and replaying the engine's entire order in its own sequence
  (destination, counter, then mode last) also stuck in every byte and still moved nothing. These
  fields are the mover's bookkeeping, not its inputs, the same wall shape as the LW-58 pending
  field. So lane 1's composed imitation STANDS as the shipping path for a knockback effect, and
  the read half keeps its value: we can now watch any move and tell a shove from a walk by the
  mode byte. Whatever actually drives the mover is in-process territory (a call, not a poke), so
  it belongs with the deep levers rather than here. Lane 2, the Dash formula table experiment, is
  untouched and still worth ten minutes.

- [LW-117] 2026-07-22: The battle toolbag: one plain verb per already-proven mechanic, so a
  design conversation can say "what if the weapon benched them a turn" and we can just do it.
  tools/probes/battle_toolbag.py wraps Proven-section mechanisms only, no new reverse
  engineering: quick (CT slam, act now), bench (CT held at zero, turn denial), hide and show
  (the gate byte, with the model id saved to a temp state file because each probe run is its
  own process), float (render Z hover), reserve and deploy (park below the floor, return with
  the sky descent), and state (every field the bag touches, for every unit). Constants were
  re-read out of the tree rather than recalled. Hazards are printed by the commands that carry
  them: a hidden unit gets no turns and cannot un-hide itself, a mid-hide autosave persists the
  hidden state into the resume, and hide and bench both refuse the current actor. Owner eyeball
  wanted per verb before any of it informs a signature design; quick and bench together also
  settle the LW-115 AT-list question in one battle.

- [LW-119] 2026-07-22: The status map is extracted and the hazards are known, but the probe
  verb that would use it is not written yet, and two thirds of the map has never been exercised
  in this game.
  tools/probes/status_map.py holds all 40 ids with their band offsets, the three layer model
  (write inflicted AND composed, re-assert on a loop; composed alone is the same wasted-write
  mistake the animation output block taught us), the two proven timers (poison band +0x4A init
  36, doom band +0x59 init 3), and an evidence tier per status. Only 13 bits are anchored by
  shipped code or a Proven row; the other 27 are map-only, meaning the ported decode table plus
  id arithmetic that checks out, which is good evidence for the TABLE and no evidence for any
  individual bit. Two ids are refused outright because the repo has crash tapes for them:
  crystal is permanent unit loss and treasure crashed the game when an enemy pathed onto the
  tile. Six more need an explicit yes. Three open disagreements are recorded in the file rather
  than smoothed over: charm's companion byte (band +0x54 versus +0x38 versus a third source
  calling +0x38 a node pool index, so write the status bit only), composed-rebuilt-every-frame
  versus the proven composed-only poison hold, and Larceny's one-shot strip which has no ledger
  row and should be undone by the next compose. Next step is the verb plus a live pass that
  promotes map-only bits to observed, cheapest first: haste, regen, protect, shell.

- [LW-122] 2026-07-22: Make the game apply a status for us. The door is found, resolvable and
  safe to knock on; we are not yet speaking its dialect.
  Why it matters: three independent systems said the same thing this session (the mover ignores
  written state, the animation register wanted an input not an output, a raw status bit sets a
  flag while the engine does the work), so "ask the engine" is the general key, and this is the
  first tool that turns it. It also unlocks two walls directly, the enemy model rebuild and the
  Guest/Traitor allegiance flip, both of which were declared dead for reasons that only apply to
  writing data ourselves.
  State: the pinned v1 apply engine is dead on this build. The fixed image thunk at 0x1401FB064
  resolves the live routine every launch and its prologue is verified before every call, which is
  a permanent fix rather than a re-pin. Eleven cold calls landed safely and applied nothing,
  covering all three modes against all three candidate subjects at the decoded argument order.
  Next, cheapest first: sweep the four remaining argument permutations (one loop, no deploy, the
  knob already ships); read the global flag the routine tests early, since a wrong global sends it
  down a different path before it touches anything; disassemble past the bail branches; and
  reconsider the premise, because the claim that this dispatch means id, mode and slot came from
  a v1 header note that was never verified and the function may not be what we think it is.
  Instruments in tree: tools/probes/apply_engine_find.py (peek, spring, dump, scan) and
  battle_toolbag.py engine with its order and subj knobs.

- [LW-64] 2026-07-10: Mirror Image ability concept (owner): briefly phase a unit out so a
  locked-on spell whiffs while its sprite stays standing; the decisive test PASSED live.
  Mechanism: flip the hide gate (combat +0x01 to 0xFF); every primitive live-proven in the
  LW-58 gate-toggle session. THE DECISIVE TEST (2026-07-10, owner live): a mid-cast Slow
  whiffed entirely when the target was gate-hidden during the cast animation, so
  hide-at-resolution defeats locked-on actions and the core fantasy is proven. Known
  hazards to guard: restoring onto an occupied tile co-tiles into the movement soft-lock
  (proven live); a mid-hide autosave persists the hidden state into resumes (proven live,
  needs a battle-enter un-strand sweep); hidden units get no scheduler turns, so the
  restore trigger must be external (other units' acted edges, or the dodged action
  resolving). Castable wrapper when built: JobCommand injection plus an action-record
  watch (the Barrage lane). New side effect to chase before any build: the whiffed
  resolution DISPLACED the hidden unit one tile (unexplained; possibly target-snap
  bookkeeping applying to a unit the effect could not find).

- [LW-65] 2026-07-10: Unit TELEPORT is proven live (real units moved, two units swapped
  mid-battle, both acted normally after); it needs a tile-occupancy check and a ledger row
  before it can ship as a mechanic.
  The missing layer was render position: render node +0x4C/+0x50 u16 world X/Y = 28*tile
  + 14 (node via list head 0x140D3A410, +0x148 combat backref). A coherent triple-write
  (combat +0x4F/+0x50 logic, node +0x88/+0x89 AI tile key, node world) moved a real enemy
  who then hovered correctly and took a normal AI turn from the new tile, after which the
  engine re-stamps every layer itself. The Z formula is solved (node +0x4E = -12 x height,
  +1 height unit with FLOAT: the hover offset is pure node data, owner-witnessed granted
  and stripped by Z pokes alone). Un-parks the Knockback family (position-write-desync
  memory updated) and gives Mirror Image its restore-displacement primitive. Open before
  any shipped mechanic: the tile-occupancy check (co-tile = target shadowing + movement
  lock) and a LIVE_LEDGER row (owner flip).

- [LW-66] 2026-07-10: Mid-battle unit REMOVE and RESTORE are both proven live with pure
  data writes (sky-descent flourish included); this unlocks the summon/reinforcement
  mechanic family.
  Despawn = one mode-2 byte on the render node (the engine sweeper tears down unit +
  sprite, byte-perfect); resurrect = AI-registry re-enroll (clone + re-key a living
  object) + node revival (in-use flag, done-mark clear, list re-splice) + present/gate
  reopen. The removal drops AI enrollment, so re-enroll MUST precede visibility (else the
  LW-58 freeze). Full byte recipe in the unit-despawn-resurrect memory; MECHANICS.md has
  the summary. The park-and-summon variant needs no despawn at all (gate FF + render Z
  below floor = invisible reserve). Open: victory-check sanity after a removal; whether a
  legitimate registry rebuild evicts the hand-cloned object; the Ctrl+F5 despawn spike fix
  (hover-marker refusal removed) awaits its next deploy.

- [LW-192] 2026-08-13: Signature idea from Patrick, "Scholar": the weapon teaches its wielder any
  spell an enemy casts on them, so getting hit with something new is how you learn it. A book or
  rod fits the fantasy. The appeal is that it turns being targeted into progress, which no current
  signature does, and it rewards the player for walking into a caster's range instead of avoiding
  it. Design questions before any build, none answered yet: does it learn only spells the
  wielder's CURRENT job could legally know, or bank them for whenever that job is worn; does it
  fire on a resisted or missed cast; is it capped per battle so a long fight does not hand over a
  whole spell list; and does it announce itself, since a silent learn is the invisible-feedback
  failure the signature audit already flagged on Chain Lightning and Font. (Tech: the WRITE half
  is likely cheap and already proven in tree, since Barrage and Shadow Blade both set the roster
  learned flag for an ability slot, see HoldLearnedBit in Barrage.cs and ShadowBlade.cs. The
  unproven half is the READ: learning which ability was just cast AT the wielder. The mod
  currently infers actions from turn flags and damage events, not from an ability id on an
  incoming cast, so this needs its own probe to find where the engine names the ability being
  resolved against a target. Check whether the game already ships a vanilla learn on being hit
  support ability before inventing a mechanism, since poaching a working one is the pattern that
  made Living Poach and the granted commands cheap.)

- [LW-228] 2026-08-13: Harp signature ideas from the owner, banked for when the instruments
  arc opens. One harp grants Ramza's Tailwind so the wielder can hand a unit extra Speed. The
  blood-string harp keeps its long range HP steal, which is unique, and its awakened tier
  could cast Elmdore's Blood Suck, turning the bitten enemy into a vampire. A third harp
  takes the MP restoring ability the tree monsters use and widens it into an area effect.
  (Tech: the granted-command trio (Barrage, Shadow Blade, Provoke via JobCommand injection)
  is the proven candidate mechanism for the Tailwind grant; the Blood Suck vampire conversion
  and the AOE widening are both unverified and need their own live RE before any of this is
  promised in a design doc.)

- [LW-229] 2026-08-13: Dancers should be able to equip harps, owner call, banked alongside the
  LW-228 harp signature ideas. Vanilla locks instruments to Bards, so a Dancer today cannot
  touch any of the harp designs at all; opening the slot makes the harp arc serve both of the
  performance jobs instead of half of them. (Tech: which job may wear which equipment category
  is job table data, not item table data, and whether the modloader reaches that table has not
  been checked; find the equip category mask in the job data first, and if the modloader cannot
  write it this becomes a runtime write behind the guarded Mem layer like every other live
  mechanism.)

- [LW-47] 2026-07-07: Murasame (id 41) has no living-weapon signature: it was cut from
  2.3.0 when Kiku-ichimonji took the one samurai slot (Mushin); design a new one when
  revived, built on a mechanism already proven live.

- [LW-14] 2026-07-04: Replace the Stormbrand: its on-hit effects are too rare to feel, and
  the real cure is a custom living-weapon ability (a runtime signature).
  Pick the theme AFTER the Samurai signatures lock, to avoid a Slow/element dupe.

- [LW-15] 2026-07-04: Make enemies actually USE living-weapon growth (an extra-large
  undesigned feature; the static rebalance already lands most of the real player want).

- [LW-13] 2026-07-04: Show milestone marks on the weapon card beyond the kill counter.
  Gated on an untested glyph-render probe; largely redundant with the shipped milestone
  toasts.

- [LW-30] 2026-07-05: Show the weapon's name and title in the attack-targeting text, e.g.
  "Select the target for Beastbane Longsword +2." (demoted from Now when LW-31 took the
  slot; the Abilities-menu funnel covers the in-battle identity job).
  If revived, the locked wording is "Select the target for {Mark}{Name}{suffix}." via a
  PromptSwap prefix match on "Select a target"; unstoried weapons keep vanilla text. Every
  technical unknown was answered live 2026-07-05: writable, render-call-time swap
  (fragment-length unbound), pill auto-sizes to viewport width, markup tokens supported
  ("<keyicon=ok>").

- [LW-48] 2026-07-07: Vanity touch: make the in-battle "View Battlefield" label read
  "View Battlefield - Modded by prawl".
  Likely mechanism: a SetTextString-family tap/prefix-match swap (PromptSwap precedent) or
  the text-catalog offset redirect (AttackCard/AttackRow precedent); find the "View
  Battlefield" string source first.

- [LW-9] 2026-07-05: The Warbrand (id 67) shows up too early for how strong it is
  (owner-noted).
  Candidates when picked up: later availability tier, price bump, or stat trim (re-run the
  analyze.py dominance gate after any change). Independent of the release-scope
  spriteIdOverride cleanup.

- [LW-11] 2026-07-04: Give Squires and Geomancers their axe-style weapons back, the cheap
  way only (equip access on existing sword-typed items).
  The rest is walled research: type-welded formula, id-welded art, no known flail formula id.

- [LW-61] 2026-07-10: Two ALTERNATING playthroughs still share one kill-tally file; key the
  tally to a save identity if cross-contamination proves a real problem in play.
  The shipped Tier-1 reset only archives on a detected NEW GAME (bf351db); this Tier-2
  isolation was deliberately deferred out of LW-51.

- [LW-85] 2026-07-14: Finish the after-a-game-patch self-rescue (AnchorScan): teach it to
  re-find the remaining addresses it cannot yet recover on its own, then copy the pattern
  into the sibling mods.
  This is the rest of the LW-82 arc; the v1 slice shipped e77b9d7. Remaining: battle-state
  anchors (CombatAnchor/TurnQueue
  via chained fingerprint scans seeded from the found roster base; needs a live battle,
  and on a patched build the scout cannot trust any pinned battle-state flag to know one
  is running) and the no-content residue (the SubmenuFlag class: boot-time state-solve or
  anchoring relative to signed neighbors; 1.5.1's only data casualty). Sibling-mod
  adoption (copy AnchorScan.cs, the FingerprintGuard pattern, into
  FFTHandsFree/FFTColorCustomizer/FFTMultiplayer) rides this row too
  (hardening-must-be-portable).

- [LW-93] 2026-07-14: The external probe scripts can no longer find battle units on game
  version 1.5.1 (the mod itself can); fix the scripts' outdated assumptions, or rebuild
  them on the pattern that still works, before the next probe is needed.
  During the LW-92 diagnosis, poison_probe.py watch spun on "unit not located" and survey
  reported "0 units, 0 band-located" while the DLL census read the same battle fine: the
  ct_probe-family slot-marker filters (the pre-1.5 slot0==0xFF semantics, the LW-42
  class) filter every unit out. The workaround that worked: an ad-hoc watcher on the
  DLL's own Offsets constants (scratchpad plague_watch.py pattern: BandReadBase =
  CombatAnchor + BandEntry - 24 * stride, guarded rpm, fingerprint scan). Lift that into
  tools/probes as the new base.
  2026-07-21: tools/probes/cursor_resolve_probe.py (shipped with LW-87) is that base, now
  tracked and live-exercised across four sessions: it walks the band from the DLL's own
  constants with guarded reads, replays Band.IsValid and the roster bridge faithfully (a
  plan reviewer re-verified it field by field against the C#), and carries no slot-marker
  filter at all. Copy its read_band plus bridge_count helpers when rebuilding the
  ct_probe family, and keep its two column habit (replay the SHIPPED logic beside the
  PROPOSED one) whenever a probe exists to judge a change.

- [LW-73] 2026-07-11: The flight recorder's unit snapshots do not include health, position,
  or turn charge, so a recording alone cannot prove whether a seat held a real unit or a
  ghost; add those fields next time the recording format is touched.
  (Tech: widen the census band record with hp/position/CT; the LW-34 over-count mining
  needed the raw file log alongside the tapes for exactly this reason.)

- [LW-268] 2026-08-18: The search re-reads the whole 2.8GB of game memory every time, even though at most 145MB of it has ever held what it is looking for, and the regions it found last time were still there next time. An index that remembers which regions were already checked and re-reads only new or resized ones could cut a rescan from tens of seconds to a few. The catch that must be probed FIRST: if the game pre-commits big arenas and writes card text into them later, a region can become interesting without changing its size, and the index would skip it forever with no error and no log line. Step one is a probe that logs the full region list diff alongside each locate across one battle and checks whether newly interesting regions are freshly committed or pre-existing. Sequence this after LW-262, because fixing coverage latching should make rescans rare enough to re-price the whole idea. (Tech: candidate design diffs (base,size) against the last completed PoolScan snapshot and carries not-pool verdicts for unchanged regions; hazard is that VirtualQueryEx sees commit granularity, not content.)

- [LW-258] 2026-08-17: Stage 3 of the card-disagreement fix, parked deliberately until the new tapes
  say whether it is needed. If the LW-257 commits land and a card copy is STILL going dark (rather
  than merely being drawn before the paint), the answer already designed is a standing re-offer: walk
  one chunk of one already-located pool region per maintenance beat, round-robin, through the existing
  OnChunk. Never a whole-process region walk, never the retired whole-heap sweep. What makes it
  affordable is a second piece worth having on its own: split CardScanner.FindKills so the literal hit
  and the meter-slot check happen BEFORE the 121-flavor attribution search, and skip attribution
  entirely for a slot address CardSites already owns, which cuts the dominant per-chunk cost by
  roughly ten times in steady state. Promote this only on evidence from the LW-257 taps (a site that
  keeps getting evicted, or a region whose count keeps draining), never on suspicion. (Tech: full
  design in the LW-257 round 1 proposal, section 4 items 10 and 11; the new `card` flight records
  named `site-evicted` and `coverage` are the evidence to read.)

- [LW-274] 2026-08-18: Two soft spots in the card heartbeat, observed by the LW-257 verify and
  carried out of LW-260's exit so they do not die in the changelog archive. Neither is a known
  bug, both are places the design leans permissive. One: after a battle starts, the settled
  check falls back to its permissive answer for the whole on field stretch, because only an out
  of battle tick refreshes the located region list. Two: the drain re latch walks the OLD key
  set, so a region found later never enters the latch until the next full coverage latch. Each
  wants either a pin proving the permissiveness is bounded or a live tape showing it never
  matters in practice. (Tech: the settled predicate and drain re latch live in
  Display.Heartbeat.cs and Display.PoolDrain.cs; the LW-260 changelog row holds the provenance.)

- [LW-271] 2026-08-18: The progress heartbeat of a long memory search is pinned to fire at tick
  300 and to stay silent before it, but nothing proves it fires AGAIN at 600 and 900, so a slip
  that degrades the every-300 rhythm to a one-shot would leave a real 650 tick scan logging once
  and then going quiet, with every gate green. Low stakes, it is a debug diagnostic line only,
  and the dangerous regressions (wrong constant, log spam, never firing) are all pinned by
  LW-267's test. Found by the LW-267 verify round's own extra mutation, which the suite did not
  catch. (Tech: `result.Ticks % ProgressLogEveryTicks == 0` in PoolLocator.Restart.cs Step; the
  uncaught mutation was `%` degraded to `==`; the fix is one more assertion driving the scan to
  tick 600 in PoolLocatorGuardTests.)

- [LW-270] 2026-08-18: One small piece of the cleanup rate limiter has no test holding it down:
  when a big cleanup pass earns an immediate repeat, the refusal counter is also supposed to
  reset to zero, and deliberately deleting that reset leaves all 3159 tests green. The effect of
  losing it is mild, the every-32-refusals cleanup rhythm would start from a stale phase after a
  later low-yield pass, but it is the same unpinned-guard class as LW-269 and was found by the
  LW-269 verify round's own extra mutations. (Tech: the `_refusalsAtCap = 0` half of the rearm
  block in CardSites.Admission.cs's PruneDeadSites; the `_pruneImmediately = true` half IS pinned
  by Prune_evicting_exactly_the_floor_rearms_immediate_retry.)

- [LW-187] 2026-08-13: The build pipeline's test gate never exercises the dev-only code paths,
  so a broken dev-only phase row (or any future dev-only logic) would sail through the gate
  that guards deploys and only fail in a hand-typed dev test run. Surfaced by the LW-186
  adversarial verify: tools/pipeline.ps1 runs plain dotnet test with no dev define, while the
  five dev spike rows in the engine's phase table only compile under it. Blocker to fixing it
  cheaply: 77 tests fail under the dev define today because their tier-threshold expectations
  are written against the production numbers {5,10,15}, not the dev numbers {1,2,3}, so a
  dev-defined gate leg needs those expectations parameterized by flavor first.

- [LW-140] 2026-07-27: Silent effects like Renewal's heal still show no number. A probe day
  found the engine routine that builds the floating number popup and a dev trigger fired it
  four times live (paused, unpaused, with and without the unit pointer pre-set): it returns
  cleanly and draws nothing, so the routine is only the last step of a pipeline and the
  caller's preparation work is what actually spawns the number. Verdict recorded in the
  LIVE_LEDGER numeral wall row with the two next levers named (call one level up, or detour
  the natural call site). Reopen only with one of those levers; the easy paths are spent.

- [LW-142] 2026-07-27: The blue move-range tiles render from a record buffer that was decoded
  this session and survived the 1.5 update at its old address. The first held write CRASHED
  THE GAME (owner-run, same day): the renderer consumes this buffer live and tolerates no
  incoherent value, which proves the buffer is real and raises the bar. Any paint attempt
  must write whole coherent records, paused, likely with the record count; treat it as a
  small arc now, not a one-poke test.

- [LW-277] 2026-08-18: Two items that kept their vanilla name now render noticeably off their
  own artwork, and only the owner can say whether that is fine or needs a re-tint. The LW-247
  ramp arc's pre-commit-3 anchors run measured both for the first time (neither had been
  checked under any engine before): the Venetian Shield reads a warm gilt hue on its own icon
  but renders icy platinum, a 161-degree move, and the Fallingstar Bag renders 60 degrees from
  its own icon hue. Both are recorded as OPEN rows in recolor_icons.ANCHOR_RULINGS so the
  anchors gate reports them instead of failing; this ticket collects the two owner calls in one
  place rather than losing them in the gate's own printout. (Tech: ids 142 and 116; measured via
  `python tools/icon_preview.py anchors`; art hue/chroma and rendered hue are printed per row.)

- [LW-244] 2026-08-14: Ragnarok kept its original name and renders lilac over artwork that is
  warm orange, 115 degrees away, and nobody ever measured it. It is the same shape of problem as
  the Whale Whisker (LW-238): a knight sword you passed by eye, whose violet was chosen as "the
  dark arriving as fire" on its fuller, which is a good reason for a colour and not a reason
  that survives the rule that an item keeping its name keeps its look. Found by the new
  reserved-name gate the moment that gate existed, which is the point of building it. Owner
  call: keep the violet and let the reason be written down, or bring it back toward its own
  amber. (Tech: id 36, icon chroma 0.138 at hue 18 degrees against a rendered 263;
  `python tools/icon_preview.py anchors` lists it, and it sits in recolor_icons.ANCHOR_RULINGS
  as OPEN so the gate reports rather than blocks.)

- [LW-238] 2026-08-14: The Whale Whisker kept its vanilla name, which by the owner's own rule
  means its colour should start from its own picture, and it was painted cyan over art that is
  visibly red. Its list icon reads warm red at a chroma HIGHER than the Perseus Bow's, and that
  bow got a measurement, a written note and an owner ruling one commit earlier; this pole got
  none of the three, and its hue barely moved from the pre-pass value. The counter-argument is
  real (it is the family's only Water pole, and cyan says water), but it is unrecorded, so the
  next pass will read the file and learn the wrong lesson. Owner call: re-anchor to the art, or
  write the exception down the way the Perseus Bow's is. (Tech: id 114. Icon 1.7 deg at chroma
  0.148 against the tint's 196.2 deg; the shipped SILVER zone takes the blue end caps, so the
  body the tint actually paints reads 6.9 deg at chroma 0.177.)

- [LW-239] 2026-08-14: Three pairs of items sit close enough in colour to be mistaken for each
  other in the same equip list, and two of them clear the look-alike tripwire by a hundredth.
  The tripwire only ever reads the three colour numbers, so it cannot know that a pair also
  shares one silhouette, which is when colour is the ONLY thing telling them apart. Pole 109
  against 113 is byte-identical in outline and 0.010 of hue from a red gate; 108 against 113 is
  inside the saturation floor by a float hair; rods 55 and 57 are the family's two green rods on
  a 99.5 percent identical outline. Found by the LW-206/LW-208 audit. (Tech: RACK_MIN_HUE_GAP
  0.05, dhue 0.060 on both pole pairs; mean-Lab dE 6.45 for 109/113 against 16.04 for the
  runner-up. Fix is a design call plus, separately, LW-240.)

- [LW-242] 2026-08-14: Nine weapons wear a second colour so dark that the measurement says it is
  barely there, and the file's own rule predicted exactly that. The rule, written during the
  sword pass, is that a dark tone laid on the art's dark share is invisible by construction,
  because the body already renders those pixels dark. The file then said the one dark metal was
  used on one sword as a deliberate exception. It is on nine items, and re-measuring on the
  pixels each zone actually claims puts every one of them at 27 to 44 out of 255 from the same
  render without its second colour, where every bright metal on the same key sits at 109 to 159.
  The owner passed all nine by eye and that is the higher authority, which is why nothing was
  re-tinted; this row exists so the choice is a choice. Affected: Flamberge, Swiftedge,
  Lightbringer, Defender, Excalibur, Chaos Blade, Wellspring Rod, Ember Rod, Rod of Faith.
  (Tech: BLACK_IRON (0.615, 0.10, 0.45) on a shade-keyed zone, ids 26/28/31/33/35/37/51/53/58.
  Measure with zone_recolor over one zone against the same recipe with zones removed, median
  max-channel delta over pixels at mask weight >= 0.5. A p90 over the whole sprite, which is how
  the original rule was stated, reads 0 for any zone under a tenth of the art and is the reason
  this went unnoticed.)

- [LW-237] 2026-08-14: The Wellspring Rod's orb, the one part of it that is supposed to glow
  green, never gets painted on the big equip card. Its recipe asks for the brightest slice of
  the picture and on that particular sprite the slice survives smoothing as a SINGLE pixel, so
  the card ships the rod's body colour over the artist's green orb and reads as one flat colour.
  The single surviving pixel is not even on the orb: it sits halfway down the shaft.
  The same recipe finds a 30 pixel orb on the small list icon, so the item looks right in the
  list and wrong on the card. Confirmed by the LW-208 audit; no gate can see it, because the
  no-single-colour pin measures a fixture, not the real art. (Tech: id 51, card. cover mask at
  pct 18 is 104 raw pixels, 14 after two smoothing passes and 1 solid pixel after the whole
  chain, sitting at 0.68 along the sprite's long axis where the other seven rods' orb zones sit
  at 0.03 to 0.28; min_blob makes no difference, pct 26 recovers 33. Total second-material share
  9.5% against a rod median of 32.2%, the minimum of every reviewed weapon card. Fixing it changes art the owner already passed, so it
  needs his eye on a before-and-after.)

- [LW-298] 2026-08-21: Change an icon's colour, forget to re-bake, and every gate still passes,
  because nothing a build runs compares the committed pictures against the colours they were
  supposed to be made from. LW-297 closed the gap between the repo and the installed game, so a
  stale install is now caught; this is the gap one step earlier, between the colour chosen in the
  source and the picture actually committed. The fix is mostly WIRING, not new tooling, and the
  first draft of this row said otherwise: tools/icon_preview.py already carries four checks
  (verify, anchors, silhouettes, and compare --expect, the engine-drift gate), and NONE of them is
  referenced by tools/pipeline.ps1, BuildLinked.ps1, Publish.ps1 or the CI workflow, so all four
  run only when a human remembers. Read that list before building anything new here. The one that
  looks closest, verify, still cannot answer this question as written: its reference side is
  working/icons, which is gitignored with zero files committed, so it compares a fresh render
  against a local cache that a clean checkout does not have and CI can never have. So the shape is
  wire the existing checks into a gate and give verify a committed reference, rather than write a
  fifth check. This matters most for the LW-278 design framework, whose loop is choose tints,
  re-bake, judge: a silent stale bake there means the owner reviews a picture that no longer
  matches the numbers under it. Known constraint before promoting: twelve twin-group shields
  derive their small surface from a real vanilla decode, which a machine without the game files
  cannot do, so a full render-and-compare cannot run in CI as-is and must either skip those ids
  through the announced skip path or stay a local-only gate.

- [LW-241] 2026-08-14: Nothing runs the check that proves an icon pass left every other item
  alone; a person has to remember. The build pipeline runs the recolor selftest and refuses a
  red one, but the compare gate is invoked by hand, which is how two holes in it survived three
  passes. It cannot simply be added to the pipeline as-is, because during a pass the family
  being worked moves on purpose, so it needs an expected-movers list that lives somewhere the
  build can read. (Tech: tools/pipeline.ps1 runs recolor_icons.py --selftest and throws on
  failure; grep finds no icon_preview call in any script or workflow.)

- [LW-245] 2026-08-14: The two gates that judge artwork have to be run by hand, and one of them
  now matters enough that forgetting it is a real risk. The reserved-name check and the
  shared-picture check both need the game files and the texture tool, which the automated build
  on the server does not have, so they live beside compare as things a person runs. That is the
  same gap LW-241 describes for compare itself, and the answer is probably the same one: a
  single local pre-commit or pipeline step that runs all three when the icon tools or the item
  colours change. (Tech: tools/icon_preview.py anchors / silhouettes / compare --expect; the
  recolor selftest is the only one wired into tools/pipeline.ps1 today.)

- [LW-232] 2026-08-14: On a lot of weapon CARDS the identity colour was never on the weapon at
  all, it was on the glow around it, so those items have been shipping as vanilla art wearing a
  coloured haze. Found while extending the LW-230 halo ramp: the card engine splits a sprite
  into groups by brightness and paints the brightest one, which is right for a chunky item and
  wrong for line art, because on a thin staff or bow the brightest population IS the sprite's
  own semi-transparent haze. Measured over all 115 weapons: 104 of the card masks are more than
  half haze, 13 have ZERO solid pixels in the tint zone, and once the haze is left alone the
  median card keeps 57 percent of its identity colour with 37 items under 30 percent and 14
  under 10 percent. The list icons are fine (median 97 percent, worst 91) because they take a
  whole glyph ramp with no mask in it, so this is a card-only defect. SCOPED by the owner
  2026-08-14 after the review gallery: weapons keep their current look until each family's own
  queued re-pass comes up, and that pass is where the engine gets chosen from the art. Nothing
  bakes for weapons before then, so this row is a standing brief for those passes rather than a
  decision waiting to be made. The fix is already proven in this
  repo: it is exactly what LW-202 found on the crossbows ("bright-v2 splits a picture into two
  clusters and a crossbow is line art with no second cluster in it"), and the answer there was
  to route the family to the zone engine and pick its materials by saturation. Swords are the
  first family through this brief (LW-199, 2026-08-14) and they add the piece the crossbows
  could not show: which mask finds a family's second material is a property of the ART, not of
  the engine, and it flips between families. Saturation found the crossbow's stock; on a sword
  the same key lands on the blade and it is DARKNESS that finds the hilt, on 15 of 15 sprites.
  Measure all three keys on the real art before choosing, and prefer a bright second tone,
  since a dark one on a darkness-keyed mask is invisible by construction. The queued per
  family re-passes (LW-198 daggers, LW-199 swords, LW-201 bows, LW-206 poles, LW-207 polearms,
  LW-209 staves and the rest) are where that judgement belongs, one family and one owner gallery
  at a time. Two alternatives were offered and declined: restricting the card engine's
  clustering to solid pixels, which puts the tint on the object but re-renders all 115 cards
  (28.1 percent of solid pixels change zone, on 115 of 115 sprites) and would need one very
  large review round, or baking the honest de-tint now and letting each family's pass restore
  the colour later, which is the only option that looks worse before it looks better. (Tech: reproduce the whole
  measurement with `python tools/icon_preview.py compare`; the bake now prints a WARN naming any
  item whose tint reaches under 2 percent of its solid art, so this can never ship silently
  again.)

- [LW-246] 2026-08-16: Players did not like the recoloured icons, and the reason is now known
  and measured instead of guessed. A spriter player named it and a five lens diagnostic confirmed
  it: the old engine threw away the artist's own colour choices pixel by pixel, so highlights
  stopped describing light and every sprite became one colour made lighter or darker. A
  replacement approach was prototyped and iterated live with the owner across twelve review
  rounds until all sixteen shields passed at game size: keep every brightness the artist chose,
  move only which colour family each material wears, leave outlines, glints and gold fittings
  alone, and add an identity rim glow. The finished shields went straight into the live install
  on 2026-08-16 for the owner's in game look; the repo mod tree is deliberately untouched until
  LW-247 makes the pipeline able to reproduce them. (Tech: prototype is
  tools/probes/ramp_engine_prototype.py; diagnostic evidence: legacy hue entropy 0.129 bits vs
  vanilla 2.569 on shield cards; all 224 invented brightness edges sat on the zone percentile
  seam; outline ring saturation was repainted 0.29 to 0.60. Prototype metrics on all 32 renders:
  zero invented edges, ring delta 0.000, mean saturation within about 1.2x of vanilla. The
  earlier palette census in this row's history reproduces via
  `python tools/probes/vanilla_palette_sample.py <out.json>`.)

- [LW-300] 2026-08-21: The seventy two items nobody has re-passed yet, the armour, clothing, robes,
  shoes, rings and the rest, are all still painted by the oldest and worst method we have, and it
  does exactly one thing: it floods the whole picture with a single colour. Measured, not guessed.
  Every one of those items ends up with zero colour variety left in it, so a steel cuff and a
  leather glove come out as the same flat purple. The weapons were rescued from a different defect,
  where the colour never reached the art at all; this is the opposite, the colour reaches
  everything and erases the material. A second measurement found the likely reason the results
  look loud: the colours we authored for these items are far stronger than the art they sit on,
  a median of 0.45 against the art's own 0.16, and thirty one of the seventy one are more than
  three times their own artwork, one of them nearly twelve times. A first attempt on the four
  gauntlets is banked and NOT shipped: turning the colour down recovers the cuff and the fingers
  on two of them and turns the other two into grey shapes with a white blob where the metal should
  be, and flattening every item to the same strength makes two of the four look like each other.
  That is the owner's other standing complaint, so the honest state is that turning the colour down
  is necessary and nowhere near sufficient. (Tech: 72 ids on `legacy` per engine_for; circular hue
  spread over solid pixels falls to 0.000 for all ten families, worst average loss Armguard 0.221,
  worst single id 217 Runecast Gloves 0.778 -> 0.000. solid_tint_share is the WRONG instrument here
  and reads 96 to 100 percent, because a whole-icon stamp is full coverage by construction. Tint
  saturation vs own-art median saturation: 0.45 vs 0.16, 31 of 71 above 3x, 210 Roamer's Boots
  11.7x. Zone route via ZONE_OVERRIDES per the LW-202 crossbow precedent gets the desat mask
  landing as blobs on 216 and 218. Needs a per-item mask key chosen from the art per the bible's
  Part 4, not one recipe for the family, and a saturation rule relative to each item's own art
  rather than a flat cap. Renders in the session scratchpad.)
  - OWNER DIRECTIVE 2026-08-21, and it found the actual rule: do the big picture and the little
    picture together. They are NOT the same drawing. The 100px card is a detailed near grey object;
    the 48px list icon is a chunky simplified shape the artist already coloured. Measured over all
    71: the card art sits at 0.164 saturation, the list art at 0.333, and we paint BOTH with one
    authored 0.450. So the same number is nearly three times too strong on the card and about right
    on the list, and 60 of the 71 have a more colourful list icon than card icon. That is why one
    flat cap fixed a surface and broke the other, and it is a rule rather than a hand tune: keep
    ONE identity hue per item so the two surfaces agree, and scale the STRENGTH per surface
    against the art actually underneath it. Also confirms the corpus line that an invented split
    reads muddy at 48px, since the desat zone that recovered a cuff on the card put white blobs on
    the mitten.)

- [LW-288] 2026-08-19: The twelve hats are the one family everybody thinks is finished but is
  not, and until now no row said so. The owner did look at them in game and pass them, so they
  are not broken, but that pass judged art made by an older recolouring method that every other
  family has since moved off. They are the last family still wearing it, which means a set that
  reads as done today will visibly drift from its neighbours the moment the rest of the
  wardrobe is redone. Re-pass them on the current method and put them back in front of the
  owner. Found 2026-08-19 by an inventory of every family against the shipped art. (Tech: hats
  are ids 157 to 168 and the ONLY family still routed to the three zone engine, per engine_for
  in tools/recolor_icons.py; LW-247's ramp port rebaked exactly 300 surfaces, the 150 ramp ids
  times two, and moved zero pixels on the 24 hat surfaces, per section 3 of
  tools/probes/lw247_arc_gate_result.txt. Their last art commit is fb02f80. The owner's own
  scope line in LW-248 already names hats as remaining colour work, so this row closes a gap
  between that sentence and the ledger. Process per LW-198.)

- [LW-214] 2026-08-13: Throwing weapons and Bombs first pass: the 6 shuriken and bomb icons were never tinted at all (they sit outside the 121-weapon set), so this is a first coloring, not a re-pass; process per LW-198.

- [LW-217] 2026-08-13: Hair Adornments re-pass: the 3 hair adornment icons still wear the legacy one-hue stamp; process per LW-198.

- [LW-218] 2026-08-13: Heavy Armor re-pass: the 14 armor icons still wear the legacy one-hue stamp; process per LW-198.

- [LW-219] 2026-08-13: Clothing re-pass: the 14 clothing icons still wear the legacy one-hue stamp; process per LW-198.

- [LW-220] 2026-08-13: Robes re-pass: the 8 robe icons still wear the legacy one-hue stamp; process per LW-198.

- [LW-221] 2026-08-13: Shoes re-pass: the 7 shoe icons still wear the legacy one-hue stamp; process per LW-198.

- [LW-222] 2026-08-13: Armguards re-pass: the 4 armguard icons still wear the legacy one-hue stamp; process per LW-198.

- [LW-223] 2026-08-13: Rings re-pass: the 6 ring icons still wear the legacy one-hue stamp; process per LW-198.

- [LW-224] 2026-08-13: Armlets re-pass: the 5 armlet icons still wear the legacy one-hue stamp; process per LW-198.

- [LW-225] 2026-08-13: Cloaks re-pass: the 7 cloak icons still wear the legacy one-hue stamp; process per LW-198.

- [LW-226] 2026-08-13: Perfumes re-pass: the 4 perfume icons still wear the legacy one-hue stamp; process per LW-198.

- [LW-196] 2026-08-13: The item icon shown when picking up a Move-Find Treasure appears NOT to
  carry the mod's recolor; it looks like the vanilla icon even for items whose menu icons are
  recolored. Owner sighting 2026-08-13 (softly held, "pretty sure"), logged with a concrete
  suspect already in hand: the vanilla icon tree has 26 icon FAMILIES and the recolor pipeline
  ships exactly two of them (equip_item, the 100px card art, and equip_item_s, the 48px list
  icon), so any UI surface drawing from a third family shows vanilla art. The treasure pickup
  popup is exactly such a surface, and a "treasure" family exists right beside the two we
  cover (Pac Files 0008 ui/ffto/icon/treasure, 90 files as t_NNN plus t_NNN_l pairs, so about
  45 ids, which is NOT one per item and means an id mapping question, not just a recolor
  question). First probe when picked up: pick up one recolored item via Move-Find, screenshot
  the popup, and match its art against equip_item versus treasure to learn which family that
  popup reads and how its t-ids map to item ids; then the fix is the settled recolor assembly
  line pointed at one more family, plus the same check for other uncovered surfaces (shop,
  battle spoils, Poachers Den) which may each read yet another family.

- [LW-279] 2026-08-18: When a unit steps on a Move-Find tile and finds an item, the game
  briefly shows that item held above their head, and that held-up art still wears vanilla
  colours while the item's own icon wears ours. Owner noticed 2026-08-18 during the LW-251
  battle art rounds. The held-up graphic almost certainly lives in the same battle art
  container the LW-251 probe cracked, so finding and recolouring it rides whatever
  mechanism that arc lands. (Tech: candidate sheets include g2d tex_160, the
  crystal/treasure-chest sheet, and tex_161's unidentified tiles; probe =
  tools/probes/lw251_g2d_extract.py; confirm by finding the item-hold frame's source
  entry, then route it through the same recolour pass.)

- [LW-248] 2026-08-16: The mod's shipping look is DECIDED for colour: every item gets the new
  recolour. The owner settled that half on 2026-08-16 after seeing the whole catalogue in game.
  Remaining colour work is hats, armour and accessories, on the same set by set rhythm the
  weapons and shields used. UPDATED 2026-08-19, owner call: the glow half is no longer part of
  this row and is no longer part of the next release. It moved to its own story, LW-287,
  because the owner has other ideas for it that deserve their own thinking rather than a yes or
  no tacked onto the colour pass. This row is therefore now purely the colour decision and it
  blocks nothing. (Tech: glow is a separate layer that adds or removes without touching a body
  pixel, proven by re-rimming the sixteen shields four times in seconds on 2026-08-16, which is
  exactly why deferring it costs nothing; the four candidates and their prices are in the
  decision brief artifact f9835932, now inherited by LW-287.)

- [LW-100] 2026-07-21: A rider who restarts a battle and opens it on foot can keep the previous
  run's leftover mounted Speed until they climb back on a chocobo.
  Demoted from Now on 2026-07-27 to make room for LW-127, which is being actively worked. Nothing
  about it changed: it has been BLOCKED since 2026-07-21 on a live pass nobody can currently run,
  and parking it in Now while five other things move is how a ledger starts lying about what is
  being worked.
  State: the 2026-07-21 live pass came back INCONCLUSIVE rather than clean. The reload took 3.469
  seconds and the mod only counts a battle as ended after 4.0 seconds out of battle, so it never saw
  an end or a start, never cleared its notes, and wrote the natural it still remembered, which is
  what it would produce in BOTH worlds. That seat cannot tell them apart. The premise itself is no
  longer unverified: the same session caught the mod's own boosted value surviving a battle rebuild
  on the PA lane (read 27 against natural 21, exactly the 1.30 hold target) and an earlier log caught
  it on the Speed byte itself (18 against natural 11).
  RE TEST RECIPE, in order: (1) DONE 2026-07-23, commit 3dccbf7, the mount lane now logs its
  capture, boost, re-apply and revert, so a line appearing means a write really happened and the
  absence of one is finally evidence; this step used to be the blocker and no longer is. (2) Make
  the restart cross the 4 second debounce, or lower ExitDebounceSeconds in a dev build, and CONFIRM
  a real battle-end plus battle-start pair in livingweapon.log before trusting the read. (3) Then
  read Speed at the restarted open while on foot, BEFORE remounting. Only then the unit tests: a
  recorded leftover target is refused as a natural even with no active hold, plus the remount
  sentinel. (Tech: the code hole is confirmed, GrowthEngine.TimedStat.cs gates the only
  FilterCapture call on active first, so a dismounted open misses all three arms; the 2026-07-21
  pass never entered it. Rides with it: a clean remount capture currently drops the post revert
  corrective sentinel for the rest of the battle.)

- [LW-143] 2026-07-27: While holding the fast-forward button, the owner may have seen the Defender's
  shout keep going past the one enemy turn it is supposed to last; parked at the owner's request
  ("keep that in your back pocket, don't act on it yet"), a single possible sighting, not a
  confirmed bug.
  Why it is plausible rather than dismissed: the mod samples the battle every 33 milliseconds of
  real time, but fast-forward speeds the GAME clock, not ours. The release needs to SEE the marked
  enemy become the acting unit and then stop being it (a rise then a fall, LW-138's fresh-rise
  rule). Under fast-forward a short enemy turn could open and close entirely between two of our
  samples, so the rise is never observed, the turn is never counted, and the hold lingers until the
  90 second watchdog force-releases it with a WARN. That WARN line, or an EnemyTurnDone arriving
  only after a SECOND turn of the marked enemy, is exactly what the tape would show if this is
  real; the flight tapes survive deploys, so the evidence keeps.
  If confirmed, the likely fix direction already exists in this session's findings: detect the
  marked enemy's turn by its CT PAYMENT (a drop of about 100, readable long after the fact) instead
  of, or as well as, the sub-sample actor-pointer dwell. The payment persists between samples; the
  pointer does not.
  Probable corroborating tape, banked same day without acting on it:
  flight_20260727_101904_battle-exit.jsonl cast 3 (mark nameId 795, second cast on that enemy)
  held 43 seconds between its why=next hide (-110.312s) and its EnemyTurnDone release (-67.703s),
  where the battle's other holds released within about 14 seconds. Matches the predicted
  "EnemyTurnDone only after a later turn" signature; the release reason was NOT Watchdog, so the
  fall was eventually observed rather than timed out.
  CONFIRMED SIGNATURE 2026-08-12, one instance, from the Provoke regression night: the owner's
  deliberate fast-forward hold produced the exact predicted shape, a markedEta=0 arm at
  21:24:14 with no turn ever observed and the watchdog WARN release at 21:25:44 (tape
  flight_20260812_213229_battle-exit.jsonl); the same enemy then accepted a re-cast without
  fast-forward and released cleanly on EnemyTurnDone. Honesty rider: the markedEta=0 arm means
  a cast landing while the enemy's turn was already open (the fresh-rise refusal) cannot be
  fully excluded as the cause of this one instance, so the confirmation is strong but not
  airtight. Still parked for the 2.3.3 release at the owner's original direction; the CT
  payment fix direction stands.

- [LW-76] 2026-07-11: A console-noise audit left a triage list of log lines that repeat,
  over-warn, or fire in healthy sessions; walk it with the owner and demote, dedup, or
  keep each. None are urgent (console dedup masks most). The audit yardstick was
  LOGGING.md's match-report contract.
  (a) Repeat-spam risks with no per-event dedup beyond the console's per-battle key:
  Sanctuary.cs:116 re-fires per crystal-counter dip on the same ally,
  GrowthEngine.Ultima.cs:66 re-logs on HP-percent flap, SpiritualFont.cs:164's per-copy
  WARN loop, the Barrage/ShadowBlade grant/release pairs on equip flapping. (b) Info lines
  stretching the match-report definition: SpiritualFont.cs:167 narrates EVERY wielder
  move, PromptSwap.cs:161 doubles every on-screen toast, EagleEye.cs:93 prints per enemy,
  BattleCensus.cs:144 is a WARNING under the [trace] verb (tier/verb mismatch). (c) WARNs
  that fire in healthy sessions: the one-tick locate and readback misses in
  LifeSap/Renewal/Wyrmblood/Rapture/GrowthEngine.Signatures, the revive-and-rekill
  repeat-credit WARN, TreasureMaster.cs:305's self-described-benign weather mismatch,
  AttackCard.Resolve.cs:87 on known stale-cursor hovers.

- [LW-165] 2026-08-12: Kill counts are slow to appear in the status menu after a cold boot on
  the Steam Deck; demoted from Now 2026-08-14 to make room for LW-230 and LW-231, which are
  being actively worked. Nothing about it changed and nothing is blocked on it: the owner
  ACCEPTED it for the 2.3.3 cut on 2026-08-12, the README carries the player note, and it has
  been waiting since then on a Deck cold boot that has not been run, so holding a Now seat for
  it is how the ledger starts lying about what is being worked (the same argument LW-100 and
  LW-112 were demoted under). State, preserved in full: the mod prints one plain line the first
  time the kill counters come alive each launch, saying how many card text spots it maintains
  and how many seconds after arming that happened, so the next Deck cold boot turns the
  complaint into a stopwatch reading. Desktop halves already measured 2026-08-12: two launches
  read 10.5s and 10.9s between arming and the first paint, and the owner reproduced the felt
  symptom on desktop (a party menu opened right after a save load shows the baked zero counts
  until the menu is re-entered). Promote it back the moment a Deck cold boot log exists; the
  likely tune is an unthrottled first pool locate at arming. (Tech: the Info line fires on the
  false to true edge of the pool coverage latch in Display.PoolPaint.cs, once per launch, timed
  from Display's own first Tick, which Engine only starts after the guard arms. The deeper lever
  is read only pre locating before arming, which touches the born disarmed principle and would
  need its own arc.)

- [LW-184] 2026-08-13: Rewrite Engine.Tick's hand-written recipe as a declarative phase
  table (owner directed). BUILT AND COMMITTED 27eb98f, owner regression watch OWED before
  this row exits: each subsystem tick is now a data row with a name, a named gate, a
  cadence, and an "after" annotation naming its ordering reason, so the tick order is a
  value tests inspect instead of physical line positions. Tests pin the exact table, every
  gate and cadence, and every ordering reason, each sabotage-proven non-vacuous; a regime
  test proves the in-battle rows run in battle and stay silent outside it. The prologue and
  both battle-edge transactions stayed sequential (bodies moved verbatim, mechanically
  diffed), the kit-lane trio's slot-claim order and the locked second PauseFlag read are
  preserved, and the independent verify rated it SHIP 9/10 on suite 3099. Exit gate, owner
  only: the seven-step DEV live regression watch (guard arms; twin equips out of battle;
  battle enter line; kill plus toast; growth on the status card; exit summary plus flight
  flush; post-battle equip-card paint), one battle, all signatures read from the log.

- [LW-146] 2026-07-28: A batch of comments and docs that lied about the code they sit on is fixed;
  one item is left, and it needs the owner's own sign-off.
  Fixed this commit: docs/LOGGING.md's claim that Puppeteer is not flight-tapped (it is, and the
  "pup" record shapes are now documented), StatusApply.cs's dead v1 apply-engine address,
  NumeralSpike.cs/BodyDoubleSpike.cs/Engine.cs's stale F6-spike comments (LW-67 deleted those
  spikes; the current map is F2/F4 StatusSpike, F5/Shift+F5 BodyDoubleSpike, F6 ProvokeSpike, F8
  NumeralSpike), the TurnOwnerSpike/TurnOwnerProbe/TurnOwnerProbeTests headers (LW-31 shipped
  2026-07-07, docs/CHANGELOG.md:932; the recorder stays wired only as a passive correlation
  instrument for LW-139), the dead Log.cs shim (deleted, its test allowlist updated), and
  Puppeteer.Policy.cs's dangling reference to a class TODO that no longer exists. tools/probes/
  tile_cal.py's stale terrain grid base was already fixed earlier the same day, commit df8b779, so
  there was nothing left to do there.
  Still open, owner territory: docs/LIVE_LEDGER.md line 138's "TILE SYSTEM SOLVED" paragraph still
  names the wrong terrain grid base two lines under its own correction banner and wants a
  supersession stamp; that flip is owner sign-off only, same as every LIVE_LEDGER row.

- [LW-112] 2026-07-21: Stop blaming a game update when another mod rewrote the same game data;
  demoted from Now 2026-08-13 to make room for LW-190, which is being actively worked.
  Nothing about it changed and nothing is blocked on this repo: it has been AWAITING-LIVE since
  2026-07-28 on a two leg owner drill that has not been run in over two weeks, and holding a Now
  seat for a row nobody is moving is how the ledger starts lying about what is being worked (the
  same argument LW-100 was demoted under). The build, the tests and the adversarial verify all
  passed at the time; only the live drill is owed. Full done means, the unexplained player residue,
  and the drill's pre registered failure signatures are preserved verbatim in the CHANGELOG entry
  this row will cite when it exits, and the drill recipe itself lives in docs/DEV_TEST_RECIPES.md
  (LW-112 section). Promote it back the moment the owner is ready to run the two legs.

- [LW-174] 2026-08-12: Five story-battle monster jobs are invisible to Living Poach because the
  map skips their alias rows. Demoted from Now 2026-08-14 to make room for the icon re-pass
  program the owner is actively working; nothing about it changed and nothing is blocked on me.
  The build and the adversarial verify both finished on 2026-08-12 (six pinned tests written red
  first, the regenerated map byte identical on a re-run, verdict SHIP), and the only step left is
  a live premise beat that is OWNER-ONLY: alias jobs appear in exactly three battles in the whole
  game, all story battles a late save cannot reach, so settling it needs a staged encounter with
  one MainJob 169 panther injected into a reachable random battle through the moddable encounter
  table. Promote it again when that drill is on the table. (Tech: full detail in the entry this
  replaces, commit ac43327 and the rows around it; PoachMap.TryGetJob membership at
  LivingPoach.cs:108 is the mod's entire monster gate, so an unmapped alias job is a silent
  refusal.)

- [LW-304] 2026-08-21: The colours we were about to put on weapons in battle were wrong for most
  of them, and looking at eight at a time is what hid it. Put all 118 side by side with their own
  icons and the picture is plain: orange icons were arriving blue. Measured properly, the old
  recolour missed the icon's colour by 76 degrees in the middle of the pack, and only 17 of 118
  weapons landed anywhere near their icon. The cause was not the colour maths, it was how the
  weapon was carved into parts: it split each weapon by BRIGHTNESS into "blade" and "everything
  else", which cuts across the artist's own shading, so the icon's main colour often landed on
  the few pale pixels while its accent colour took the whole body. Two smaller faults rode along:
  a single stray pixel in an icon could be promoted to "second material" and painted over half
  the weapon, and colour was applied by multiplying what was already there, so anything drawn in
  grey steel (books, guns, silver blades) could never take colour at all. Rewritten to carve the
  weapon along the artist's own shading runs instead, hand each run one of the icon's real
  materials, and give each material a share of the weapon matching its share of the icon. The
  same measurement now reads 5 degrees in the middle, 110 of 118 within 20, nothing worse than 64,
  and the light to dark shading is untouched so blades still read as lit metal rather than
  plastic. (Tech: lw301_palette_transform.py, two_zone deleted in favour of palette_ramps plus
  assign_ramps plus paint_by_ramp; the measuring instrument is lw303_grade.py, which compares the
  icon's chroma weighted hue against the hue delivered over the pixels that weapon's own tile
  inks. The diagnosis was funded by a natural experiment inside the failing run: the handful of
  weapons whose icons offered a single material skipped the zone code and every one scored within
  3 degrees.)
  - Left open for the owner: forty eight weapons outside the four named families are still single
    toned, because only those four were asked for; the same part rules would extend to them and
    that is his call, not an assumption. A handful still sit over 25 degrees from their icon, led
    by Flamberge at 54, whose icon genuinely carries two strong colours. The number cannot see mud,
    plastic or a lost outline, so it is a floor and not a verdict.
  - ADVERSARIAL REVIEW the same day, and it found four things worth fixing rather than arguing
    with. FIRST and worst: the measurement was blind to the very failure this work exists to
    prevent. Deliberately flattening every weapon to one brightness, which is the painted plastic
    look, SCORED BETTER than the real thing, because the score only ever read colour and never
    shading. It now checks both, and re-running that same sabotage makes it fail loudly instead of
    passing. SECOND: the code claimed it never repaints a drawing's outline, and that was simply
    untrue; the safety line it used sits below the darkest colour any of the thirteen weapon sets
    contains, so it has never once fired and every edge does get repainted. That is actually what
    we want, since a blue sword should have a dark blue edge and not the old brown one, so the
    behaviour stays and the false claim goes, with the cost of it now measured and printed.
    THIRD: one sentence of the write up blamed a pixel count for a bug, and counting the pixels
    disproves it; the real cause was a single stray pixel being promoted to a whole material. The
    wrong sentence is struck in place rather than quietly deleted. FOURTH: the old recolour had
    been deleted, which made the before and after impossible for anyone else to re-run, so it now
    lives inside the measuring tool where it can never paint a real pixel, and re-running it
    reproduces the 76 degrees exactly. Also fixed: the picture sheet the owner actually looks at
    was hiding ninja blades, which is where the single worst scoring weapon lives.
    (Tech: `python lw303_grade.py --baseline` prints both transforms over the same 118 weapons,
    76 median against 5, improved on 107 of them; the structure section asserts HSV value is
    carried per slot, worst change 0.0000, and reports the worst perceived edge shift, 0.266 on
    Yoichi Bow. Ablation recorded in assign_ramps: replacing the proportional budget with "biggest
    ramp takes the dominant material" costs only 1 degree of median but drops within-20 from 110
    to 98, so the bulk of the repair is the hue grouping and the saturation rebase, not the budget
    rule that the first draft of the docstring took credit for.)
  - OWNER LOOKED AGAIN and said to note the two toned areas, and he was right a second time. The
    colours were landing on the wrong PARTS of the weapon. A sword came back striped down its
    blade instead of a coloured blade with a differently coloured grip, which is the exact thing
    he asked for in the first place. The cause is that the artist shades ONE object with more than
    one family of colour, so grouping by colour splits a blade from its own shading and then hands
    the two halves different paint. Measuring where each group actually sits on the drawing says
    it plainly: on the sword the two halves of the blade sit 2 percent of the picture apart and
    the two halves of the grip sit 14 percent apart, while blade and grip sit 49 percent apart.
    So groups that cover the same place are now merged back into one part before any colour is
    chosen, and the biggest part is pinned to the colour a person would actually name the weapon.
    Every weapon now reads as two or three solid regions a person can name rather than stripes.
    (Tech: weapon_parts merges palette_ramps by centroid distance under PART_MERGE_FRACTION of the
    tile diagonal, then assign_ramps pins the largest part to the material nearest the rendered
    icon hue. Costs 7 weapons at the 20 degree line, 110 to 103, and improves the worst case from
    64 to 60, a trade taken deliberately because the score was always a floor and right colours in
    wrong places still look wrong. New instrument lw303_zonemap.py paints one flat colour per
    material so the split can be READ; the score cannot see this failure at all, which is why it
    passed a striped sword.)
  - OWNER PASS THREE, and this one named the families: bows, crossbows, knives and knight swords
    still were not two toned, bow strings should be white, and a blade and its grip should never be
    the same colour. Checking first showed why no amount of cleverness with the pictures would have
    worked: the icons themselves are single colour objects. Measured along each icon's own long
    axis, its two ends sit within ten degrees of each other for almost every weapon in those four
    families, so there is no second colour in the picture to find. A second tone therefore has to
    be a deliberate rule about the WEAPON, and that is what was built. Bows and crossbows now get a
    white string, knives and knight swords a grip that is forced away from the blade colour, and
    all thirty three of them now read as two toned where none reliably did before. Which slots draw
    which part was measured rather than guessed: every frame of every one of the four families was
    dumped slot by slot, and the same slots draw the same part in all of them, eight bow frames,
    twelve crossbow frames and every knife and knight sword frame.
    (Tech: PART_ROLES in lw301_palette_transform.py, applied by apply_part_roles after the normal
    paint so no other weapon is touched. Bow and crossbow strings are slots 2, 3, 4; knife and
    knight sword grips are 5, 6, 7. The string is the ONE place brightness is rewritten, because
    vanilla draws strings dark grey and draining the colour alone gives a grey string, so its ramp
    is lifted with its order preserved and lw303_grade.py checks that order separately rather than
    just excusing it. Grip colour prefers a genuine second material from the icon and otherwise
    follows the vanilla artist's convention, steel for a warm blade and leather for a cool one.)
  - BOW ROUND, owner call 2026-08-21: the vanilla bow is TRI toned and ours was not. Vanilla
    draws a grey string, a blue grip wrap and a wood limb, and our recolour was flooding the grip
    with the limb colour. The grip is now simply LEFT ALONE, so the artist's own third colour
    survives, and Skirmisher reads as the owner asked: blue handle, purple sides, white string.
    Two bows are now excluded from recolouring entirely by owner call and stay exactly vanilla,
    Yoichi Bow and Perseus Bow. Honest limit on the word blue: that grip wrap is only blue in four
    of the thirteen weapon colour sets, so of the seven bows still recoloured, three get blue and
    four keep their own earth tone. It is a third TONE everywhere, a blue one only sometimes, and
    which the owner wants is his call.
  - The icon half of that request is NOT done and needs him, because the icon is a different
    drawing from the battle sprite and has no blue grip to carry over. Marking every blue pixel in
    all nine vanilla bow icons shows what the blue actually is: outline and shading on Skirmisher
    and Stormarc, and on Huntress, Perseus and Frostarc the whole limb. The icons do carry a
    distinct grip blob in the middle, but its colour is gold, not blue, and it is not reliably
    separable on all nine. Giving the icon a blue handle therefore means DECIDING which pixels are
    the handle, which is an art call and not a measurement.
    (Tech: PART_ROLES gains a Bow handle role on slots 5, 6, 7 which restores vanilla; KEEP_VANILLA
    holds ids 90 and 91. Only four of the eight bow frames use those slots, the other four are a
    second bow drawing with no separate grip. Evidence render lw303_bow_icon_blue.png.)

  - The score had to be rescoped in the same pass, and the reason is worth keeping. Scoring the
    WHOLE weapon against its icon turns the owner's own request into an error: a deliberately
    contrasting grip drags the average off the icon, and Hushblade went from 6 degrees to 76 by
    doing exactly what was asked. Fidelity is now read on the weapon's MAIN part, and being two
    toned is counted separately, because either number alone can be satisfied by something ugly.
    Reads 6 degrees median, 102 of 118 within 20, and 70 of 118 two toned overall.

- [LW-303] 2026-08-21: We now know which drawing in the game's art sheet each kind of weapon
  actually uses in battle, all twenty kinds, which we did not know before. This matters because
  the review sheet that shows a weapon's new colour was printing a made up shape next to five of
  them, and a wrong picture invites the owner to pass judgement on something that is not the
  weapon. The method that worked was to paint the fifteen shades of every weapon colour set
  fifteen loud different colours, so a weapon on screen wears a stripe pattern that names its
  drawing, and then let the owner match that against a numbered chart of all 150 drawings. Two
  earlier attempts at matching by outline alone failed, twice, because at twenty pixels a
  crossbow limb and a bow limb are the same curve. Left open on purpose: three kinds still have
  fewer drawings than the byte table says they need (Katana, Pole, NinjaBlade), thirteen drawings
  are still unidentified including six that are people rather than weapons, and the arrow and
  scroll piles hold far more art than their categories call for. (Tech: labels and full revision
  history in tools/probes/lw301_sprite_labels.json; instruments are lw301_sprite_chart.py and
  lw301_slotmap_probe.py. Cross checking the owner's labels against the per category graphic byte
  at 0x02D3E6 + (id-1)*2 gives exactly 2.0 tiles per shape class for Knife, Sword, KnightSword,
  Polearm and Staff, from two independent sources, which is the first real evidence FOR the
  category relative reading of the byte that [weapon-graphic-byte-not-sprite] retracted in its
  absolute form. That row deserves a follow up, owner flip only.)
  PROGRESS 2026-08-21: the map is now WIRED, so the review sheet draws each weapon's real
  drawing instead of a hand typed guess, and there is a page that puts all 121 weapons' menu
  icons beside the battle sprites they will actually swing, which nobody had ever seen side by
  side. The tool reads the labels file rather than holding its own copy, so an owner correction
  reaches the pictures the moment it is made. Picking which of a kind's several drawings to show
  needed one rule beyond confidence: an edge on view inks MORE pixels than the same weapon seen
  across, so a pole seen end on used to win and showed nothing recognisable. (Tech: labels drive
  CATEGORY_LABEL_ALIAS plus tiles_for_category in lw301_palette_transform.py; the only category
  with no identified tile is Cloth, named in CATEGORY_NO_SPRITE with the reason, because the
  nine tile Scroll pile is a candidate that was never confirmed and a wrong silhouette is worse
  than none. Page builder is lw303_icon_vs_sprite_page.py with its template beside it.)

- [LW-301] 2026-08-21: Give every weapon its own colour when it is swung in battle, which we spent
  two days believing was impossible. The game only has thirteen colour sets for a hundred and
  twenty seven weapons and it decides which weapon uses which, and that decision really is
  unchangeable. It also turns out not to matter. The colours themselves can be changed while the
  game is running and the change shows up immediately, and only one weapon is ever on screen at a
  time because a weapon is only drawn while its owner is swinging it. So the mod can set the
  colours for whoever is about to attack, and every weapon can look like its own icon. Shown live
  twice with the owner watching: one knife turned cyan while another kept its steel, then the two
  were driven to yellow and magenta at the same time in the same battle. (Tech: write the static
  1024 byte workspace at 0x140d35750, both 512 byte banks, palette N at +N*32, BGR555, preserve
  slot 0 and bit 15. Ledger [per-weapon-colour-by-turn-repaint]. Reads per draw, so no reload is
  needed and no hook is involved; this routes around [weapon-palette-assignment-walled] rather
  than breaking it. Three things to settle before building: whether the palette is latched at
  animation start or sampled continuously, which decides how early the repaint must land; what
  happens when two weapons sharing one palette are drawn together, as in a counter-attack; and
  re-applying after a battle load, since the load copies the file over the workspace. Colour
  source is items.json iconTint, which makes this the first surface where the icon and the battle
  sprite can actually agree.)

- [LW-289] 2026-08-19: Give every weapon a battle colour taken from its own menu icon, now
  that we know exactly which colour set each weapon uses and have proved we can repaint those
  sets. The promise has to shrink to match what the game allows: there are thirteen usable
  colour sets and a hundred and twenty seven weapons, so weapons share, and the honest wording
  is that every weapon GROUP wears a chosen identity colour rather than every weapon matching
  its own card. Reassigning a weapon to a different set is walled (see the ledger row), so the
  grouping is the game's, not ours. Two things make this better than it sounds: weapons and
  effects never share a set, so nothing can accidentally retint a slash arc, and the thirteen
  living weapons land on only six sets. Four of them collide on one set though (Zwill
  Straightblade, Lightbringer, Materia Blade and Defender all share it), so those four must
  agree on a colour or the design has to accept one of them driving it. Expect some weapons to
  look deliberate rather than right: the best single colour for a group still leaves its worst
  member about a hundred and ten degrees of hue away. (Tech: bake all sixteen palettes of
  FFTPack file 71 unit/battle_wep_spr.bin from data/items.json plus the shipped ei_NNN_uitx.tex
  icons; the file is THREE (palette, page) pairs, palA+page1 rows 0-255 weapons, palB+page2 rows
  256-511 arcs, palC+page3 rows 512-655 impacts, total exactly 85504 bytes and it must stay
  exactly that length; weapon palettes are 3-15 and effect palettes 0-2 with zero overlap; the
  map is tools/probes/lw289_weapon_palette_map.json; the existing icon_ramp painter in
  lw251_wep_spr_forge.py is structurally wrong because it sorts all fifteen slots into one
  luminance ramp and shuffles colour across the four independent zone ramps, so the bake needs a
  zone aware painter; the baked sheet becomes a GENERATED artifact and must never be hand edited.)

- [LW-290] 2026-08-19: Once weapons are painted from their icons, nothing stops the two
  drifting apart again, because a recoloured icon and a stale weapon sheet both look fine on
  their own and only disagree in a battle nobody runs before shipping. Add a gate that fails
  the build when a weapon's battle colours no longer match the icon it is supposed to wear,
  the same way the item gate already refuses to ship a dominated item. Be honest about what
  such a gate can and cannot see: it can prove the shipped sheet contains the colours taken
  from the icons, in the right slots, with no two graphics writing over each other's slice,
  and that every weapon's picture data stays inside the slice it was given, all of which is
  cheap and repeatable. It cannot prove the game DISPLAYS the result, because that half is
  the game choosing a colour set, which only the owner can confirm in a battle, once, after
  which it stays put. Depends on [LW-289]. (Tech: CI has no FF16Tools so icon decode cannot
  run there; use the pattern already in this repo and commit a derived manifest of item to
  ramp colours built on a dev box, then have CI compare manifest against the baked sheet
  while the dev box additionally compares manifest against the icons and skips loudly when
  the game tree is absent, as the ramp engine already does; wire it into tools/pipeline.ps1
  so BuildLinked and Publish both refuse on red, matching analyze.py; the gate must also
  cover the slice partition and the effect rows that share the sets.)

- [LW-291] 2026-08-19: Find where the game actually decides which colour set a weapon uses, so
  that weapons can be moved between sets instead of being stuck with the grouping the original
  designers picked. Everything cheaper has been tried and failed: the two obvious fields in the
  item table, shipping our own copy of the old data file, and writing the copy of that data
  sitting in the running game's memory. The sibling mod searched every file the game ships, 14.35
  GB of it, and found only one copy of the table anywhere. So the game works the answer out once
  when it starts and keeps it somewhere else in a different shape. The only instrument left is to
  watch the drawing code use the value rather than guess where it is stored. This is a research
  arc with a real chance of walling, and it must not hold up the shipping work in [LW-289].
  (Tech: hook the weapon sprite draw in process and read the palette index at the point of use;
  the resident vanilla battle_bin image sits at heap base 0x416DC768C0 and is loaded BEFORE the
  modloader installs its FFTPack hook, which is why a file override never reaches it; ledger row
  [weapon-palette-assignment-walled] carries the four negatives and their controls.)

- [LW-67] 2026-07-10: Strip every service bound to the F6 test key (owner directive, about
  six F6 users); this repo is DONE, the sibling FFTHandsFree repo still needs its sweep.
  Done here: the four dev spikes (AttackCardSpike, HeaderSpike, FlavorSpike, ShowSpike
  deleted whole) plus their Engine wiring, the spike-only feeders (HeaderProbeText,
  FlavorProbeText) and their tests. AttackCardProbeText and ScanCursor/RegionCursor were
  KEPT: the production Attack-card painter (AttackCard / AttackCard.Census) consumes them.

- [LW-58] 2026-07-09: Research arc, RESOLVED: a mid-battle summoned COPY of a live unit is
  fully possible (drawn, named, controllable, AI-fighting, descends from the sky); the
  shippable slices are tracked as LW-64/LW-65/LW-66.
  The road there, kept for provenance: the raw-flag activation path is CONFIRMED DEAD (the
  chest-revert test re-enrolled timeline/hearts/revival but the model stayed a chest and
  the unit's turn soft-locked; treasure-pop signature decoded, +0x45/+0x46 plus the +0x18E
  mirrors). The status system was decoded (three 5-byte layers; apply engine 0x150BF66DC;
  dispatch 0x1401FB064; treasure = status id 15). External pending-field writes are
  consumed but ignored (3 tapes), closing ALL external-write spawn/model lanes. The
  breakthrough: node builder 0x14026EBEC + a data-only AI enroll whose one-byte key is the
  AI-roster index 0x141873038[slot] (0xFF = un-enrolled leads to the null AI-subject
  crash). Battle-scoped (temporary summon; a permanent recruit = a save-roster entry,
  unbuilt). Also cracked in this arc: full unit TELEPORT/SWAP, visual FLOAT, DESPAWN (node
  +0x12C mode-2 + engine sweeper), RESURRECT, and the animation request register (node
  +0x10). Full records: MECHANICS.md breakthrough block, five LIVE_LEDGER Uncertain rows
  plus two overturned walls, memories body-double-spawn-arc / position-write-desync /
  unit-despawn-resurrect-recipe / anim-request-register. BodyDoubleSpike Canary 1-9
  (dev-only, worktree feature/body-double-spawn). Open polish: AI-passivity (behavior
  row), decoy-hold default. Dead-branch extras for the record: the original probe plan
  (plan.md) and tools/probes/spawn_probe.py (built on battle_cheats.py), the band
  +0x17A..0x181 presence-byte candidate, the SpriteSet +0x00 model swap being
  scene-graph-side, the then-next CE step (what-writes on band +0x46 at a pop), and the
  untested frog-cast-in-the-revert-window variant.
- [LW-373] 2026-08-31: A partner who wants to add weapons should get a two-page recipe, not
  a framework and not a Python lesson. Plain language: Jade tried to add his weapons by
  hand-editing the game's text table and found the mod "renaming everything back" and no
  kill counter on his cards; the painter finds a card by the exact name and flavor text baked
  from data/items.json, so his hand edits could never match. Everything he needs is already
  one items.json row (name, flavorOverride, identity, stats, riders, shops, availability,
  donors, growth lane, tier, seedCount) plus the whitelist step; what is missing is a readable
  guide. Done looks like: a partner recipe page in the docs folder (the row schema in plain words, the whitelist
  step, `generate.py` and `analyze.py` as the two commands, the refusals he will meet and what
  each means, the 90-character flavor budget, tier 1 at 5 kills, the EquipBonus row budget)
  and a DocsContractTests entry for it; his rows then arrive as a normal items.json PR. Owner
  ruling 2026-08-31: no JSON extraction of the gate's tables (that would be the open
  declaration format the 2026-08-30 ruling rejected).
- [LW-374] 2026-08-31: The build should refuse, in plain words, when the new weapons ask for
  more equip-bonus combinations than the game has free rows. Plain language: a weapon's
  stat bonuses (PA, MA, Speed, Move, Jump, innate or immune statuses) live in a separate game
  table with only a handful of free rows (74 to 79) plus vanilla rows the generator reuses
  when the combination already exists; ours and a partner's designs share that budget. Today
  running out is a generator error nobody has read yet. Done looks like: `generate.py`
  counts the distinct new combinations, names the free-row budget and the rows already taken,
  and refuses with one sentence a partner can act on (reuse an existing combination or drop
  a bonus); a test pins the count.
- [LW-362] 2026-08-30: The game's own two DLC blades (ids 256 and 257, Materia Blade+ and
  the Akademy Blade) could become living weapons too, counting kills and growing like
  everything else; today their kills are deliberately left uncredited as untracked items,
  which the owner noticed mid-battle. (Corrected 2026-08-31: this row first said four blades,
  ids 256 to 259, but 258 to 260 are DLC gear, a hat, a vest and a ring, per the byte dump in
  docs/research/ITEM_CAP_261_BREAK_JOURNEY.md; the LW-375 ceiling arithmetic leans on exactly
  two DLC blades existing.) Owner priority ruling: LOW, not important right now,
  parked at the bottom of the backlog on purpose. Shape when picked up: meta rows for the
  two (growth lanes, tiers), the grid csv rows, and a check that their stock SortOrder
  reservations (the check_stock_rows tripwire) and the LW-344 partner whitelist stay
  consistent with them becoming tracked.

### Cold read findings, untriaged

Rows the cold read's stage 1 reader raised without being asked, one row per file, one indented line per finding, tagged with the trial that found them. Nobody has judged them yet; a row leaves here by being promoted, merged into a real row, or closed in the changelog.

- [LW-481] 2026-10-20: Cold read of Extended/InventoryResetHook.cs found 6 things worth a look. (cold-read, Rerun trial 2 build 3)
  - Process swallows a Restore exception with an empty catch and no log, and Restores does not reflect it.
  - A failed read before the reset, or the second read inside Restore failing, is silent, with no log and no counter.
  - _canary is a plain bool while the counters go through Interlocked, so the first time Event could fire more than once if the detour ran on several threads.
  - kept counts the nonzero saved bytes, which may not be the ones the game zeroed.
  - Release only disables the hook and never nulls it, so a later Install re enables it without checking the prologue.
  - The bag is read as one byte per item, and Same and kept rest on that.
- [LW-479] 2026-10-20: Cold read of Extended/RewindRecomputeStep.cs found 1 thing worth a look. (cold-read, Rerun trial 1 build 3)
  - A source counter that drops, after a reset say, hides every later rise up to the old high water mark, because _last only moves on a rise.
- [LW-480] 2026-10-20: Cold read of Extended/RewindTotals.cs found 2 things worth a look. (cold-read, Rerun trial 1 build 3)
  - PutBack publishes Latest and counts a capture before it knows the hands read succeeded.
  - wrote rests on a name not ending in "refused" instead of a flag.
- [LW-478] 2026-10-20: Cold read of Extended/CategoryGetterHook.cs found 3 things worth a look. (cold-read, Seeded trial build 3)
  - Release swallows a failed Disable in an empty catch, so IsActive is the only sign of the failure.
  - Install returns null on a fresh install and on the already hooked path alike, so the caller cannot tell the two apart.
  - A second Install after a failure overwrites _keepalive, so an earlier hook still live could lose its delegate to the collector.
- [LW-471] 2026-10-20: Step and Recompute swallow exceptions with no log or counter. (cold-read, Rerun trial 1)
- [LW-472] 2026-10-20: A null Latest makes Recompute return false with no trace while the due pass is spent. (cold-read, Rerun trial 1)
- [LW-473] 2026-10-20: Step reads each counter twice per tick. (cold-read, Rerun trial 1)
- [LW-474] 2026-10-20: LastReason is written outside the lock. (cold-read, Rerun trial 1)
- [LW-475] 2026-10-20: ReadHands counts only RRHand and ROffHand and never RLHand or RShield. (cold-read, Rerun trial 1)
- [LW-476] 2026-10-20: Capture stores unclamped totals while BagFor clamps to 0..255. (cold-read, Rerun trial 1)
- [LW-477] 2026-10-20: Offsets.InventoryCountBase and Offsets.BagCountArray are two names for 0x1411A7C00. (cold-read, Rerun trial 1)
- [LW-468] 2026-10-20: In Extended/CategoryGetterHook.cs, Detour dereferences _hook with a bang, and Activate can enable the hook before the assignment lands, so an early call would throw inside the game. (cold-read, Seeded trial)
- [LW-470] 2026-10-20: In Extended/CategoryGetterHook.cs, Release never clears _hook, so IsActive cannot tell a released hook from one never installed. (cold-read, Seeded trial)
- [LW-502] 2026-10-20: Cold read of Extended/ExtendedInventory.cs found 4 things worth a look. (cold-read, Extended sweep inventory-root)
  - The armed log line hardcodes six hooks, the swing fallback and the list builder whatever a custom install step actually installed.
  - The repair and refusal notes are volatile but their join updates are not atomic, so a tick side read and clear between the read and the write could lose or resurrect a note.
  - The tripwire runs every tick and its warning could repeat each tick.
  - Refuse serves both the not shipped and the failed paths while the door readings differ.
- [LW-503] 2026-10-20: Cold read of Extended/ExtendedInventory.Hooks.cs found 2 things worth a look. (cold-read, Extended sweep inventory-root)
  - RollBack calls Restore on parts whose Install may have failed or never run.
  - The detour plan is overwritten when ReplayOnLoad runs twice before a tick, and a key mismatch discards it with no log.
- [LW-500] 2026-10-20: Cold read of Extended/SaveEdgeHooks.cs found 3 things worth a look. (cold-read, Extended sweep save-edges)
  - Install has a retry trap, since a throw after the first hook is assigned leaves it set, so a later Install returns success with the hooks disabled.
  - The hook fields are assigned only after Activate returns, so a game call in between would hit a null hook outside any try.
  - The tracker holds one pending slot each for save and load, and a new edge overwrites an unconsumed one.
- [LW-501] 2026-10-20: Cold read of Extended/SaveEdgeHooks.Detours.cs found 2 things worth a look. (cold-read, Extended sweep save-edges)
  - AfterApplyCore calls Adopt outside its own try, so a throw there skips the replay, the edge and the callback, and the canary never sets.
  - AfterApply returns silently when the header read fails on a load.
- [LW-499] 2026-10-20: Cold read of Extended/RewindRestoreHook.cs found 7 things worth a look. (cold-read, Extended sweep rewind-restore)
  - A repeat Install only re enables the hooks and never checks the prologues again, and a null checkpoint hook leaves IsActive false.
  - The install catch releases before it nulls the hooks, so a half created pair could leave the first one active.
  - The catches in both detours swallow every error, so a callback failure is counted as handed on and never logged.
  - The two once per launch warn flags are plain bools, not interlocked.
  - PutBack publishes Latest and counts a capture before it knows the hands read succeeded, so a failed restore logs as a success with an empty description.
  - PutBack and Recompute cast to a byte, so a value above 255 wraps unless BagFor clamps it.
  - CheckpointTotalShadow is not thread safe on its own, and the TotalsByStruct accessor exposes it outside the lock.
- [LW-497] 2026-10-20: Cold read of Extended/ListBuilderHook.Pool.cs found 1 thing worth a look. (cold-read, Extended sweep list-builder)
  - TryWrite results are ignored in CopyBackRefused, PoolExhausted and the zero count branch of CopyBack, so a failed write leaves the game's bytes in the buffer while the log says an empty list was given.
- [LW-498] 2026-10-20: Cold read of Extended/ListBuilderHook.cs found 6 things worth a look. (cold-read, Extended sweep list-builder)
  - The two once per session warn flags are plain bools, so two first failures at once could warn twice.
  - The static buffer passthrough runs the original with no pool, no cap and no truncation.
  - CopyBack clamps to 511 words but nothing limits how much the original writes into a slot.
  - An exception from the original propagates out of Process with the slot still released.
  - Release only disables the hook and never frees the pool page or clears the keepalive.
  - The pool is keyed by comparing the list address with the static buffer, not by detecting a stack address.
- [LW-495] 2026-10-20: Cold read of Extended/OrderRebuildHook.Seat.cs found 1 thing worth a look. (cold-read, Extended sweep order-rebuild)
  - The first refusal flag in Refused is a plain bool with no lock, so a detour on two threads could warn twice, while the repair log set is locked.
- [LW-496] 2026-10-20: Cold read of Extended/OrderRebuildHook.cs found 6 things worth a look. (cold-read, Extended sweep order-rebuild)
  - Both catches in Process are silent, so a failure in the seating or the re-append leaves no counter and no log.
  - Reappend returns the game's count quietly when the second read fails or the write is refused, apart from the warn lines.
  - A list with no 0xFFFF terminator within MaxWords makes ParseList return null and the re-append is skipped silently.
  - The footprint check does not count the terminator word TailBytes appends, which may be one short against the before length.
  - The template end marker is 0x00FF while the live list terminator is 0xFFFF, two different sentinels.
  - Install returns null both for success and for the already hooked case.
- [LW-493] 2026-10-20: Cold read of Extended/TemplateRelocation.cs found 6 things worth a look. (cold-read, Extended sweep template)
  - RipFields go through ImageBase.At with hardcoded addresses while Slots and CapSites use Offsets values directly.
  - Restore keeps going on a failed write with ok folded in, so the state can end half restored with Installed still true, and PageAddr and the page stay.
  - Slots, CapSites, Charts and WeaponRegions allocate a fresh array on every access.
  - Regions covers two of the three charts, and the all items chart is relocated but never seated.
  - Plan masks the listed words and the checked id with 0x3FF but Contains and Encode use the unmasked id.
  - The widened builder cap 0xFF equals the end marker 0x00FF numerically.
- [LW-494] 2026-10-20: Cold read of Extended/TemplateRelocation.Sites.cs found 1 thing worth a look. (cold-read, Extended sweep template)
  - Chart.Capacity, Chart.RegionBytes and Chart.PageOffset duplicate the constants, and the Slot and RipField labels are unused in the messages.
- [LW-491] 2026-10-20: Cold read of Extended/ListRelocation.cs found 8 things worth a look. (cold-read, Extended sweep list-relocation)
  - Every failed Install after the allocation leaves the page allocated and filled, and a retry allocates another.
  - A failed rollback write leaves game code pointed at the new page while Installed is false, and Restore then returns true at once and cannot repair it.
  - SnapshotOldBlocks and CheckOldBlocks pass Offsets.BagCountArray and Offsets.SiblingListArray straight to TryRead while the sites go through ImageBase.At.
  - ImageBase.Base can be set again, and Live is recomputed on every call, so a base change between Install and Restore would make Restore write to other addresses.
  - Once the field writes start the game uses the new page while CountBase still reports the old block and Installed is false, until the final stores.
  - Install, Restore and CheckOldBlocks take no lock, and only the installed flag is volatile.
  - A failed Restore leaves some sites restored with Installed still true, and PageAddr is never cleared.
  - The hidden writer warning fires at most once per instance and is never reset, even across a reinstall.
- [LW-492] 2026-10-20: Cold read of Extended/ListRelocation.Fields.cs found 1 thing worth a look. (cold-read, Extended sweep list-relocation)
  - Vanilla is a raw four byte value, not an address, and for the trailing byte sites Trail and Off only enter Distance.
- [LW-488] 2026-10-20: Cold read of Extended/ExtendedCatalog.cs found 7 things worth a look. (cold-read, Extended sweep catalog)
  - ExtendedCatalog does not rebase Offsets.ExtCatalogDisp32, Offsets.ExtCatalogBase or the CopyPlan sources through ImageBase.At while ExtendedSites rebases every address.
  - A failed Install leaves the buffer and partial writes behind, harmless while the disp32 is untouched.
  - Restore rewrites the disp32 but keeps the buffer and BufferAddr, and a failed Restore write leaves Installed true.
  - Install returns null when already installed, so new records passed then are silently ignored.
  - SlotOf is id minus 261 but records are placed at id times the record size, and SlotOf is unused in these files.
  - Install does not check for duplicate ids, so a later record overwrites an earlier one.
  - ImageBase.Set must run before Build or Install and nothing here enforces it.
- [LW-489] 2026-10-20: Cold read of Extended/ExtendedSites.cs found 2 things worth a look. (cold-read, Extended sweep catalog)
  - Build does not check n against MaxExtendedCount, and the byte casts wrap silently for a large n.
  - PostLoadSites is empty, so PostLoadPatches always returns an empty list.
- [LW-490] 2026-10-20: Cold read of Extended/ExtendedRecords.cs found 1 thing worth a look. (cold-read, Extended sweep catalog)
  - The Shops mask is parsed but never encoded by EncodeCatalogRecord, whose bytes 6 and 11 are always 0.
- [LW-486] 2026-10-20: Cold read of Extended/ThunkStub.cs found 6 things worth a look. (cold-read, Extended sweep thunks)
  - The swing stub never sets rsi, so its value comes from the caller.
  - A non zero standing word leaves the stub with eax holding it unchecked, and only a zero word reaches the hands lookup.
  - EmitDonorStub and EmitRowStub do not check lo and hi against IdMask, so a hi above 0x3FF can never be reached after the mask.
  - Both stubs overwrite eax and the flags before the range check and jump to the original with eax changed on the out of range path.
  - The donor stub's in range path jumps on to the original with ecx replaced while the row stub's ends in a ret with a pointer.
  - The trampoline relocates only one E8 call, so a rip relative instruction in the stolen span would break.
- [LW-487] 2026-10-20: Cold read of Extended/ExtendedDonorThunks.cs found 3 things worth a look. (cold-read, Extended sweep thunks)
  - SubRspMovzxR9 is one mutable static byte array shared by five entries.
  - AReraise, AInvisible and AFloat share byte 0x47 and APoison, ARegen, AProtect, AShell and AHaste share byte 0x48 in Offsets.
  - WeaponStat is defined in ExtendedDonorThunks but is not in HookedDonorEntries.
- [LW-484] 2026-10-20: Cold read of Extended/BytePatchSet.cs found 3 things worth a look. (cold-read, Extended sweep patches-shop)
  - BytePatchSet.Apply returns null without doing anything when something is already applied, so a second call with a different list looks like success.
  - BytePatchSet.Rollback ignores every write result and clears the list regardless, so a failed restore leaves a patched byte with no record.
  - PendingPatch.Step stays Waiting on an unreadable site, so a permanently unreadable address polls forever.
- [LW-485] 2026-10-20: Cold read of Extended/ShopFlagsMirror.cs found 3 things worth a look. (cold-read, Extended sweep patches-shop)
  - ShopFlagsMirror.Install leaves the page allocated on every failure after the allocation, and the returns after the saved bytes are set leave stale saved values.
  - The low byte rollback inside Install is unchecked, so a failed rollback would leave the builder half redirected while Installed is false.
  - Sync writes the 512 vanilla bytes from offset 0 of the mirror, and whether that reaches the rows written at id times two depends on the first extended id.
- [LW-482] 2026-10-20: Cold read of Extended/AccessorEntryHook.cs found 4 things worth a look. (cold-read, Extended sweep small-hooks)
  - Neither Install frees the page after a failed write and neither Restore frees it after a restore, so pages leak unless the allocator reclaims them.
  - AccessorEntryHook.Restore writes only the original bytes, and TrampolineAddr and StubAddr keep their old values after a restore.
  - Neither hook checks that the allocator returned a page of the size asked for.
  - AccessorEntryHook.Install copies the jump into entryBytes without checking the jump fits in the expected length, and that copy sits outside any try.
- [LW-483] 2026-10-20: Cold read of Extended/SwingIdFallbackHook.cs found 1 thing worth a look. (cold-read, Extended sweep small-hooks)
  - ShouldArm is called with a constant true in SwingIdFallbackHook.Install, so its readOk parameter is redundant there.

## Walled (blocked by engine / Denuvo / modloader)

- Swords cannot get new swing visuals: the art is welded to the weapon id inside the
  engine, and the same render node also drives DAMAGE, so touching it breaks combat.
- Item text cannot ship in French: game + modloader parser walls; the only path is the
  DLL painting text live (or upstream modloader support).

## Format (enforced by TodoContractTests)

- Sections, in this order and no others: Now (with the release name in the header), Backlog,
  Walled, Format.
- Now: at most 5 entries. Entry first line: `- **[LW-<n>] <title>** (opened YYYY-MM-DD) [STATUS]`
  where STATUS is QUEUED, BUILDING, AWAITING-LIVE, or BLOCKED(reason). Every entry carries a
  `- Done means:` and a `- Verify:` sub-bullet. Promote from Backlog by filling those in; if Now
  is at cap, demote something first.
- Backlog: entry first line `- [LW-<n>] YYYY-MM-DD: <one sentence>`; indented continuation lines
  are free. Capture new items here in the session they surface.
- ELI5-first prose (owner rule, 2026-07-21): the first sentence of every entry, and the opening
  of every Done means / Verify, is plain language a non-programmer follows: what is broken or
  wanted, for whom, what done looks like. Technical detail (offsets, hashes, file and memory
  names) comes AFTER that opening, in continuation lines or a "(Tech: ...)" tail, never
  instead of it.
- IDs are unique across this file and docs/CHANGELOG.md; never reuse a retired ID.
- Items exit ONLY by moving to docs/CHANGELOG.md when they ship or die: in the shipping commit
  itself, or in the immediately following commit when the exit row cites that commit's own hash.
- No em dashes and no double-dash separators anywhere in this file or the changelog.
- AWAITING-LIVE flips and VERIFY_LIVE checkboxes are owner-only.
