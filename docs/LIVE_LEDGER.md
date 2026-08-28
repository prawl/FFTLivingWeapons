# Live ledger — runtime memory-mechanic claims

STATUS: CONTRACT (runtime mechanism claims; owner-only PROVEN flips)

The single home for "is this actually true in the live game?". Every load-bearing claim the
Living Weapon runtime rests on gets an entry. Mirrors the grid CSV's convention: **only
Patrick flips an entry to PROVEN** (he is the live-verification authority); anyone may add
UNCERTAIN entries or attach contradicting evidence. A "proven live" claim in a doc comment
without an entry here is a rumor with good posture.

Entry format (LW-183, 2026-08-13): each entry opens with `### [slug] title` and one or two
sentences stating what is true RIGHT NOW (current claim, current status, latest date). The
full history — original wording, mechanism, evidence, corrections — sits below it inside a
collapsible fold, verbatim from the ledger's table era. Cite entries from code or docs by
their `[slug]`. Compress the top; never trim the fold.

Statuses: **PROVEN** (live-verified, evidence linked) · **UNCERTAIN** (works-in-code, never
isolated live) · **CONTRADICTED** (evidence points both ways — probe before building on it)
· **WALLED** (investigated, blocked; do not retry without new information).

## Proven

### [shop-buy-list-flags-mirror] A town's Buy list can stock an extended-inventory id once the builder's loop bound is widened and its two reads of the 256-row town-flags table are re-pointed at a mirror page carrying rows past 255

Not yet observed (built 2026-08-27 evening from a static read of the live 1.5.2 process, docs/research/ITEM_CAP_261_BREAK_JOURNEY.md "shops"). What the read established: the builder is plain code at 0x140288E54, loops ids 0..0xFF (cmp ebx,0x100 at 0x140288FD9), reads the 16 town bits from 0x14067F890 + id*2 through a rip-relative high-byte walker (0x140288F01) and an image-relative low-byte read (0x140288F3B), tests 0x8000 >> townIndex, then gates on the catalog record's +0x0A chapter byte. What the build assumes and the live pass must show: that widening the loop and re-pointing those two references (LivingWeapon/Extended/ShopFlagsMirror.cs) is sufficient, i.e. no other reader between the list build and the purchase re-derives the flags from the vanilla table, and that the "+" ids 256-260 (now candidates too, with zero rows in the mirror) stay unlisted. The owner's shop visit on the next deploy moves this row.

PROVEN 2026-08-27 19:50-19:53, owner live pass on the deployed prod build (07b9cb2 tree): the boot line reported the mirror, Dorter's Outfitter listed the Moonblade at 10 gil and a purchase raised the bag count 1 to 2 (sidecar x2), Gariland's Outfitter did not list it, no "+" item appeared; the live builder sites read the mirror page (0x114AB0000, vanilla half byte-identical to the game's table, row 261 = Dorter, loop bound 0x106).

<details><summary>How we got here</summary>

**Claim (original wording):** the buy-list builder's loop bound and its two table references are the whole shop gate for a new id.

**Mechanism:** the modloader's own data signature located the table; a raw disp32 search of .code found the two readers where rip-relative and imm64 sweeps found none; capstone over RPM read the builder.

**Evidence:** ShopFlagsMirrorTests and ExtendedInventoryTests over a fake vanilla image; the disk and live bytes of all three sites matched on 2026-08-27; no game run.

**Date:** 2026-08-27

</details>

### [extended-inventory-boot-arm] The ported extended inventory (LivingWeapon/Extended, 2026-08-27) arms the whole cap-break set from Mod.StartEx and the Moonblade behaves as it did under the research rig

Built 2026-08-27 (commits 4a4c3e3, 10ba024, 4e1cfae, 9406599, 128b28a) and NOT yet run in the game: the FFTHandsFree rig's boot-arm marker v2, the two post-load pokes and the count re-seed, replayed as one transaction inside this mod (19 cap patches with old-byte verification, the relocated catalog, ten accessor thunk stubs, the category-getter and order-rebuild hooks, the LW-348 bag sidecar). Every piece is a port of something the owner observed on 1.5.2 on 2026-08-26/27 under the rig; what is new and unobserved is the composition (arming from this DLL's StartEx behind the PE build-key landmark, the two copy-protected caps landing from the tick loop, the sidecar replay) and the two stub shapes below. The owner's live pass on the ported build (docs/TODO.md LW-346 Verify) is what moves this row.

PROVEN 2026-08-27 18:34-18:43, owner live pass on the ported build (commit c4afde5, prod flavor, research marker off): the arm line at boot, both post-load caps landing on their own after the load, the Moonblade listed in Inventory and the equip picker, a shield kept in the off-hand, a blade swing in a fresh battle, one kill credited to its tally (kills.json 261: 1, the first-blood toast, the exit-edge summary), and a save / quit / cold boot / load with the hand still 261 and the bag count replayed from the sidecar.

<details><summary>How we got here</summary>

**Claim (original wording):** everything the rig did can be done by the mod itself at boot, with the same result.

**Mechanism:** docs/research/ITEM_CAP_261_BREAK_JOURNEY.md, the 2026-08-27 03:20 port blueprint; ExtendedInventory.BootArm (LivingWeapon/Extended/ExtendedInventory.cs).

**Evidence:** unit suites only (ExtendedInventoryTests over a fake vanilla image; ThunkStubTests executing the stubs in-process). No game run.

**Date:** 2026-08-27

</details>

### [weapon-stat-row-stub] The weapon-stat accessor thunk answering an extended id with a pointer to an 8-byte ITEM_WEAPON_DATA row the mod authored gives that item those stats

Not yet observed. The rig always answered as a DONOR id (row 67, Warbrand) and the owner-read WP flip (67 to 37 made the card read 28, journal 2026-08-26 "STAT FUNNEL") shows every stats consumer resolves through this thunk; the port returns the mod's own row instead (ThunkStub.EmitRowStub, executed in-process by ThunkStubTests). The premise that nothing downstream needs the row to live inside the game's 128-row table (Offsets.ItemStatsBase) is the untested step. If the card or the damage read wrong on the ported build, this row is the first suspect: fall back to a donor stub for the weapon-stat thunk and keep the custom row for later.

PROVEN 2026-08-27, same owner pass: the card read the authored row's stats, the swing did weapon damage and the kill credited, all with the weapon-stat thunk answering a pointer into the mod's stub page (row 01 8E 01 FF 0F 00 00 00 read back live).

<details><summary>How we got here</summary>

**Claim (original wording):** the weapon-stat thunk's callers use only the pointer it returns.

**Mechanism:** journal 2026-08-26 "STAT FUNNEL" (WP 15 to 28 on donor swap) and 2026-08-27 00:00 (the 8-byte row copied to the damage staging).

**Evidence:** ThunkStubTests.Row_stub_executes_returning_our_row_pointer_for_extended_ids_only (the stub itself); no game run.

**Date:** 2026-08-27

</details>

### [donor-table-thunk-stub] A per-id donor table behind each accessor thunk behaves like the rig's constant-donor stub for one id and lets every extended id name its own clone and art donors

Not yet observed. The rig's stub remapped every id 261..511 to ONE donor; the port's stub (ThunkStub.EmitDonorStub) looks the donor up in a table indexed by id minus 261 and passes ids past the table through untouched. Register contract is the same (only rax and rcx touched, rdx and r8..r11 untouched, the June r11 lesson). ThunkStubTests executes the stub in the test process through a function pointer (donor lookup, mask, passthrough); the game has not run it.

PROVEN 2026-08-27, same owner pass: every per-category answer (type, validity, range, sprite pair, the four siblings) came through the table stubs (all nine read back live as donor 37) with the same outcome the constant-donor rig gave on 2026-08-26/27.

<details><summary>How we got here</summary>

**Claim (original wording):** a table lookup in the stub is register-safe and behaves as the constant stub did.

**Mechanism:** LivingWeapon/Extended/ThunkStub.cs, byte layout pinned in ThunkStubTests.

**Evidence:** in-process execution tests only.

**Date:** 2026-08-27

</details>

### [capbreak-uninstall-is-clean-loss] Loading a save that holds the new weapon on a game WITHOUT the rig loads fine: the hand is emptied, the item is simply gone, nothing crashes

Observed 2026-08-27 02:25 by the owner on 1.5.2: with the boot marker disarmed (vanilla game plus the HandsFree bridge without the cap-break rig), the save that carried the Moonblade in Ramza's right hand and one spare in the bag loaded normally; the hand read 0x00FF, the bag count 0, the Acquired list had been rebuilt without 261, and the Inventory showed no Moonblade. Both persisted display-order tables still carried 261 as their LAST entry before the end marker; the vanilla rebuild stops scanning at the first word >= 261, so nothing after it could be lost in this save, but a save where a sort had moved 261 into the middle of a table would hide every later entry from that list's default order until the player sorts once (the sort path drops the id and rewrites the table). Not yet built on; the removal note for LW-346 item 10 should say "sell or unequip new items first; if a list looks short after uninstalling, sort it once".

PROVEN 2026-08-27 (owner flip): the 02:25 observation stands and the removal note now ships in docs/COMPATIBILITY.md.

<details><summary>How we got here</summary>

**Claim (original wording):** without the rig the load sanitizer (the 5-slot validity loop behind thunk 0x1402B8EBC) zeroes hand ids past 260 and the count array past 260 is never read, so removal is a clean loss.

**Mechanism:** marker renamed, cold relaunch, tools/probes/lw346_saveload_check.py plus a table-index read after the load.

**Evidence:** the probe readout (hand 255/255, count 0, tables has-261 at index 127 with the marker at 128, Acquired 261 absent); the owner's menu read.

**Date:** 2026-08-27

</details>

### [capbreak-save-roundtrip-1-5-2] A save carries the new weapon in the hand, in both menu order tables and in the Acquired list, but NOT its bag count

Observed 2026-08-27 01:50-02:00 by the owner on 1.5.2 with the research rig boot-armed (marker v2): roster slot 0 rHand 261 survived save, quit to title, load (the load sanitizer passes it with the validity thunk cloned); both weapons display-order tables and the Acquired list held 261 after the load; a bag count of 2 written before the save read 2 after a same-process reload but 1 after a cold boot plus load, which is the rig's boot seed, so the count is not in the save file and the load neither restores nor clears it. Matches the static read of the serializer (bag copied as exactly 261 bytes at save+0x83A8). Design consequence: LW-348, a mod-owned sidecar re-seeds count[261+] after every load. Probe: tools/probes/lw346_saveload_check.py.

PROVEN 2026-08-27 (owner flip): re-observed on the ported build's cold boot at 18:43 (hand 261, both order tables and the Acquired list carrying 261, bag count absent from the save and replayed from extended_inventory.json).

<details><summary>How we got here</summary>

**Claim (original wording):** count[261] cannot ride in the save because the save struct packs the bag as 0x105 bytes against the next 261-byte array; the roster hand ids are u16 in the unit block and do ride.

**Mechanism:** serializer 0x14021926C / restores 0x14021B1D5 and 0x14021E1D1 read live (journal 2026-08-27 00:00-00:50); the three-step owner test above.

**Evidence:** the probe's three readouts (post-load: hand 261, count 0; after seeding 2 and a same-process reload: 2; after a cold boot and load: 1).

**Date:** 2026-08-27

</details>

### [capbreak-swing-art-via-accessor-clones] With the range-index and sprite-pair accessor thunks cloned to the Chaos Blade, the new weapon (id 261) swings a visible knight-sword blade and damages as a knight sword

Observed 2026-08-27 01:30-01:35 by the owner on 1.5.2 (screenshots tools/probes/lw346_moonblade_swing_blade_1.png and _2.png): with marker v2 boot-armed (seven extra cathook lines: 0x1402B8BCC range index, 0x1402B8E60 sprite/palette pair, 0x1402B8C0C range base, 0x1402B8CD4 / 0x1402B8D3C / 0x1402B8DA0 / 0x1402B8E04 sibling per-item accessors, all ids 261-511 -> 37) plus the two post-load damage caps, Ramza's Moonblade swings drew a blade on every attack, 272 damage on a chocobo at 125 percent compatibility = PA 21 x WP 15 x Brave 69 percent, i.e. the knight-sword Brave formula on the Warbrand-clone WP (the earlier 396 / 317 reading was the same PA x WP without the Brave factor). Two loose ends keep this Uncertain: the first several swings of that battle were still bare-fisted before the blade began showing on every swing (nothing was changed in between; a lazily loaded sprite sheet or CLUT is the working guess), and the action block's weapon word reads 242 for the Moonblade during the swing (combat +0x1A8, published to 0x1407B077A) in both the fist and the blade swings, so that value is not what decides the drawing.

PROVEN 2026-08-27 (owner flip): re-observed on the ported build at 18:37-18:42, a blade swing and a credited kill in a fresh battle; the first-swings-fist loose end did not recur this pass.

<details><summary>How we got here</summary>

**Claim (original wording):** the punch was the attack-animation setup 0x1403099B0 publishing "no weapon" because the range-index thunk 0x1402B8BCC answers -1 for id 261, and the hand art comes from the sprite/palette pair accessor 0x1402B8E60, which answers NULL for 261; cloning both to 37 gives the Moonblade the Chaos Blade's drawing.

**Mechanism:** static read of the live process (journal 2026-08-27 00:00-00:50), the M0 poke proof on Save the Queen ([weapon-sprite-pair-drives-swing-art]), then one relaunch with tools/probes/lw346_capbreak_bootarm.marker.v2.txt. The 242 came from a 0.5 ms watch (tools/probes/lw346_render_cluster_watch.py) on the render cluster 0x1407B0760.. and Ramza's combat action block 0x141855CE0+0x1A0 during two swings; the CE "find what writes" on that word (owner) resolved to the CRT memcpy from a stack local built by the action-commit routine 0x140281488, whose class-8 path copies its input unchanged, and the plain attack builder 0x14030D2D4 takes that input from the static template 0x14186AFAC filled by copy-protected code (0x1508B2DC0).

**Evidence:** the two screenshots above; the watch logs in the session scratchpad; docs/research/ITEM_CAP_261_BREAK_JOURNEY.md (2026-08-27 01:00-01:40 section).

**Date:** 2026-08-27

</details>

### [per-weapon-colour-by-turn-repaint] Per-weapon battle colour IS achievable: the palette is read PER DRAW and can be repainted live

Writing the static 1024-byte workspace at `0x140d35750` changes a weapon's colour IMMEDIATELY, mid-battle, with no reload, and two palettes can be driven to different colours in the same battle at the same time. Because the game is turn-based and a weapon sprite only renders during its own attack animation, repainting that weapon's palette on its turn gives every weapon its own colour. This ROUTES AROUND [weapon-palette-assignment-walled] rather than breaking it. PROVEN: owner live passed the shipped WeaponPalette runtime built on this row, 2026-08-24 (the LW-251 pass; runtime commit b38160a).

<details><summary>How we got here</summary>

**Claim:** the 127-weapons-share-13-palettes limit does NOT prevent per-weapon battle colour.

**Why this was missed for two days.** [resident-weapon-palette-buffer] established that the static workspace is the DESTINATION of a copy performed at battle load, and a test writing it came back "normal steel", which was recorded as the workspace being unusable. That test wrote the workspace and then RELOADED the battle, so the reload's copy destroyed the write before anything was drawn. The reload was the confound, not the mechanism. Writing the workspace and drawing WITHOUT a reload was never tried until now.

**Evidence, two rounds, each with a same-frame control.**

Round 1: palette 14 set to cyan `0x7FE0` in both workspace banks mid-battle, loaded file left pristine so it could not be the source of anything seen. No reload. The Cutpurse (palette 14) swung CYAN; the Galewind (palette 4) swung normal steel in the same battle. Control holds and the loaded file was vanilla throughout, so the workspace alone drove the render.

Round 2, to show independent simultaneous control rather than a single global tint: palette 14 to yellow `0x03FF` and palette 4 to magenta `0x7C1F`, written together, still no reload. Cutpurse rendered YELLOW (owner screenshot, bright yellow blade mid-swing with a "Parried!" callout) and Galewind rendered MAGENTA. Two palettes, two colours, one battle.

**So the palette is sampled per draw, not uploaded once at battle load.** That is the fact everything else rests on.

**Why this yields PER-WEAPON colour.** The game is turn-based and the weapon sprite is drawn during its wielder's attack animation; idle units on the field show no weapon, visible in both screenshots. So at most one weapon is on screen at a time, and the mod already knows whose turn it is and what they are holding. Repaint that weapon's palette at its turn, and the shared-palette collision never materialises because the other members of that palette are not being drawn.

**CORRECTED 2026-08-21 by a deliberate disconfirming test. The "one weapon at a time" claim above is FALSE.**

The original wording said a weapon renders only during its wielder's attack animation, so at most one is on screen at any instant. A PARRY falsifies it: with all 13 weapon palettes painted distinct colours, an owner screenshot shows the attacking ninja's blade and Ramza's raised parrying blade in the SAME frame, in two different colours, with the "Weapon parry!" callout between them. Both read vanilla steel immediately before the repaint, so the colours are demonstrably ours. Credit where due: the owner proposed the parry case himself after the first pass found nothing, and it is the one scenario the earlier session never provoked.

**The design survives, with a bounded and now-measured caveat.** The fix is to repaint the ATTACKER's and the TARGET's palettes rather than only the acting unit's, which works because simultaneous multi-palette control is already proven (round 2 above). It fails only when those two weapons share one of the 13 palettes. Measured over the 121 tinted weapons: 9.1 percent of attacker/defender pairs share a palette, and of those pairs 87.5 percent carry icon hues more than 20 degrees apart and would therefore look visibly wrong, so roughly 8 percent of PARRY exchanges would show a clash. Parries are themselves a fraction of exchanges, so the visible-defect rate across normal play is well under that. The three largest groups (palettes 14, 13 and 15, holding 19, 18 and 17 weapons) account for over half the collision mass.

**Two further scope limits found in the same pass, both outside the mechanism:** a THROWN weapon (ninja throw, Gloomfang) rendered normal steel with all 13 palettes painted, and the Move-Find treasure staff likewise, so both draw from a source these palettes do not reach. Samurai ability animations DO render the weapon with its palette, so coverage extends past basic attacks.

**Still untested:** whether the palette is sampled continuously or latched at animation start. This is deliberately NOT load-bearing, because repainting at turn start is correct under either answer.

**Also proven this pass:** the workspace address survives a relaunch. Across two separate game restarts, `0x140d35750` and `0x140d35950` were both readable at the same addresses holding byte-identical pristine palettes, which is expected for a MEM_IMAGE region with no ASLR but is now observed rather than inferred.

**Date:** 2026-08-21

</details>

### [resident-weapon-palette-buffer] The weapon palettes are writable in MEMORY and take effect on the next battle load

The 16 palettes of FFTPack file 71 sit in process memory in four copies, two heap and two inside the game image; writing one of them repaints every weapon drawn from that palette from the NEXT battle load onward, with no file override, no relaunch, and no draw hook. PROVEN: owner live passed the shipped WeaponPalette runtime built on this row, 2026-08-24 (the LW-251 pass; runtime commit b38160a).

<details><summary>How we got here</summary>

**Claim:** weapon battle colour is controllable by a guarded memory write, not only by shipping a modified FFTPack file.

**Mechanism:** the pristine 512-byte palette block (palA, `0x00000..0x001FF` of `unit/battle_wep_spr.bin`, 16 palettes x 16 BGR555) was extracted from `0002.pac` under the existing md5 gate and used as a search needle. A scan of committed readable memory (1983 MB) found it at four addresses. Palette 14's 15 non-zero slots were flattened to BGR555 `0x7C1F` in the two heap addresses only, preserving slot 0 and bit 15.

**CORRECTED 2026-08-21, same session: those are NOT four copies.** The first wording said "four copies, two heap and two in-image" and that is wrong in a way that changes what a fix would target. Region classification and a landmark check show two distinct objects:

- **The LOADED FILE**, one copy, MEM_PRIVATE, inside a single large arena (allocation base `0x4158d80000`, the file at `+0x160ee0c0` on this launch). The two "heap hits" were `palA` at file `+0x00000` and `palB` at file `+0x08200`, which are byte-identical in vanilla, so one needle matched twice inside ONE file. Confirmed by reading all three format landmarks at that base: palA matches vanilla, palB matches palA, and palC at `+0x10400` is a visibly different bank (`9c83 9863 9463 ...`), exactly as `tools/probes/lw251_wep_spr_forge.py` documents the format. Its address is per-launch.
- **A STATIC 1024-byte WORKING BUFFER** at `0x140d35750`, MEM_IMAGE, therefore at a FIXED address with no ASLR. The two "in-image hits" are its two 512-byte banks, `0x200` apart. `[palette-code-sites]` finds a `memset(0x140d35750, 0, 0x400)` that clears exactly this whole region, which is what confirms the two banks are one object.

**Independence proven, not assumed.** Writing palette 3 green at the loaded-file address alone left the other three addresses untouched in the same instant, so these are genuinely separate memory and not one physical page aliased at several virtual addresses. That test was run specifically to try to break this row's original claim, because aliasing would have produced the identical all-four-changed observation by a completely different mechanism.

**Evidence.** Immediately after the write, in the battle already running, a swung Dagger (palette 14) was UNCHANGED, which places the GPU upload before the write. The owner then reloaded the battle from the world map; the Cutpurse (palette 14, art 0) rendered a vivid magenta blade in the owner's screenshot, while Galewind (palette 4) in the same frame was unchanged, which is the control. Re-reading all four addresses afterwards showed magenta in ALL FOUR, including the static working buffer never written by us, so the game copies the LOADED FILE into that working buffer at battle load rather than re-reading the file from disk into it.

**What this does and does NOT buy.** It buys per-palette colour from an in-process guarded write, which is the mechanism a runtime signature can use, and it removes the file-staging timing race entirely. It does NOT buy per-weapon colour: WHICH palette a weapon draws from is still [weapon-palette-assignment-walled], so 127 weapons still share 13 palettes. Per-weapon needs that wall broken or a draw-path hook.

**Not yet established:** whether writing the STATIC working buffer alone is enough, which is the question that decides whether this needs any per-launch search at all; where the loaded file sits relative to a stable anchor, since nothing in 2 GB holds a pointer to it or to anything within 128 KB before it, so the game reaches it by a computed offset or a register-held pointer rather than a stored address; whether the buffers survive a fresh launch or a save load; and the interaction with a shipped FFTPack override, ABSENT during this test since BuildLinked had wiped the deployed fftpack tree.

**Shipping target SETTLED 2026-08-21 by direct test: write the LOADED FILE, not the static buffer.**

The test was built so the two candidates could not be confused. The loaded file was first restored to pristine on both banks, so it could not be the cause of anything seen; then palette 14 was set to a NEW colour (cyan `0x7FE0`, deliberately different from the magenta already in play) in the static buffer's bank A ONLY, leaving bank B holding the older magenta as a second control. The owner reloaded the battle and swung two palette-14 knives: both rendered NORMAL STEEL, neither cyan nor magenta.

The follow-up read is what makes this conclusive rather than merely negative, because "the write was overwritten" and "that buffer is not the render source" look identical on screen. Both static banks read VANILLA afterwards, so our cyan was demonstrably destroyed rather than ignored: the game performs a file to buffer copy at battle load, and the static buffer is its DESTINATION.

So the two candidates resolve as: the static buffer at `0x140d35750` is refreshed every battle load and a write there is transient, surviving only if it lands between the copy and the GPU upload; the loaded file persists across a battle load and is where a durable write belongs, which is consistent with the original magenta observation where the file was what got written.

**The remaining engineering problem is therefore locating the loaded file each launch**, since its address moves and nothing in 2 GB points to it or to within 128 KB before it. The workable route is a signature scan for content the file uniquely contains: palC at file `+0x10400` is a visibly distinct bank (`9c83 9863 9463 9042 ...`) and unlike palA it is not duplicated within the file, which makes it a better needle than the palette block that found this in the first place. Scoping such a scan to MEM_PRIVATE regions is the obvious cost reduction.

**Date:** 2026-08-21

</details>

### [inline-color-markup-in-ui-text] The UI text renderer parses inline color markup, so text can recolour itself

UI strings carry inline `<color=NN>` tags as DATA, and the renderer consumes them at draw
time: a well-formed tag changes the colour of the text that follows, and a malformed or
orphaned tag renders as literal characters on screen. PROVEN, owner live-verified, reported
2026-08-24, on the world-map Camera Controls help text.
CARD SURFACE PROVEN 2026-08-25 (the LW-307 follow-up, owner screenshot): the weapon
description card's text path runs the SAME parser. tools/probes/lw307_card_markup_probe.py
rewrote Warbrand's flavor line in the heap pool at identical byte length with
<color=80>...</color>; the card rendered the tagged span in bright yellow with zero
literal tag characters, and the runtime's own Kills counter paint ("Kills: 9/10 to +2")
survived beside it, so the flavor-anchor tolerated a 24-character intact prefix. Color 80
reads YELLOW on the card surface. This clears colored card text for the Kills meter, the
"Grows:" lane text (LW-322) and the name suffix.

<details><summary>How we got here</summary>

**Claim:** in-game text colour is controllable from inside the string itself, no draw-code
hook needed.

**Mechanism:** inline markup of the form `<color=NN>` (observed value 80) embedded in the
UI string, with a bare `<color>` observed acting as the boundary/reset in the working edit.
Parsed at draw time by the text renderer, so a live string edit is enough to change colour.

**Evidence (owner, two edits on the world-map Camera Controls help text):** first edit
inserted the plain text "Modded by prawl" into the string and the screen came back showing
the literal characters `<color=80>` before "Modded by Prawl", proving the tag lives in the
string as data and stops being consumed when the insertion breaks its form. Second edit
placed the text with the tag well-formed (`Modded by prawl<color>`) and the game consumed
the markup and rendered the inserted text in a changed colour.

**Why it matters here:** the runtime already paints text into the weapon description cards
(the Kills counter and name suffixes) and colour was never achievable on that surface
because the markup grammar was unknown. This row supplies the grammar to try there. The
card surface itself stays untested until probed: its copies may be consumed by a different
draw path than the world-map help text.

**Corroboration (2026-08-24, offline):** the third-party Knight Overhaul mod
(fftivc.jepoy.jobs.knight 1.0.1) ships uijobabilityhelp.en.nxd strings full of the same
markup in richer forms: NAMED colour tokens (`<color=abilityhint_cap_01>`) and inline
icons (`<icon=104>`), closed with `</color>`. So the grammar supports named palette
tokens and icon glyphs, not just numeric colours, in at least the help-panel surface.

</details>

### [g2d-equipment-sheet-override] The in-battle HD equipment art is g2d tex_161 and a mod override repaints it

The weapon a unit swings in battle draws from g2d container entry 161, a 512x512 sixteen-colour
indexed sheet (the PSX WEP art at exactly 2x), and a mod-shipped `system/ffto/g2d/tex_161.bin`
holding raw decompressed bytes replaces it: restart-gated, shared per graphic (every weapon
using a tile changes together). PROVEN, owner live-verified 2026-08-18.

<details><summary>How we got here</summary>

**Claim:** battle weapon art can be repainted by a mod, which the 2026-06-01 levers could not do.

**Mechanism:** `g2d.dat` (magic YOX, 1426 entries, offset table at 0xaa4800 whose offsets
drift +16 per index; per-entry YOX header + zlib) holds the HD 2D art. The modloader's G2D
hook serves loose `tex_N.bin` files INSTEAD of container entries for enabled mods, at launch,
cached per process. Entry 161 is the equipment sheet: bows, guns, shields, helmets, harps,
maces, blades, spears, shuriken, cloths; four bits per pixel, palette applied at display
time (the PSX CLUT model, per FFHacktics' load routine), index 0 transparent, layout
identical to the classic `battle_wep_spr.bin` sheet's first 256 rows at double size.

**Evidence:** whole-sheet index derangement (every visible pixel's palette index shifted,
transparency preserved) shipped as tex_161.bin on 2026-08-18. The owner observed an Archer's
bow render deep blue with a WHITE EDGE (no vanilla CLUT white-edges a bow; outlines are dark
in every palette) while the nocked ARROW in the same frame rendered normal vanilla art (the
arrow lives on untouched tex_158): a same-frame internal control ruling out global rendering
causes. The crossbow was ALSO unaffected, and the owner identified that the crossbow is not
on tex_161 at all (its sheet is still unlocated). Probe: tools/probes/lw251_g2d_extract.py
(container format, findings, and the owner observation in its docstring). Owner flip:
in-session, 2026-08-18.

**Limits:** restart-gated (per-process cache, same constraint the scoping predicted); the
sheet carries palette INDICES, not colours, so an index remap can only shuffle a weapon's
existing sixteen colours: true hue control lives in the palette asset, which is NOT
`battle_wep_spr.bin`'s palette block (2026-06-01 magenta test) and is still unlocated.

**Rounds 2-6 (2026-08-18, all owner-observed negatives, banked so nobody re-walks them):**
the colour source resists every post-boot lever. (2) The six palette-shaped container
entries beside the sheets: file overrides mapped by the hook, no effect anywhere. (3) Live
nudges of every classic-palette copy findable in the running game, including the loaded
`battle_wep_spr.bin` block and the binary's own tables: no effect; the binary tables
self-heal, and a poked bin block was even observed being copied into a working table
without the weapons caring. (4) All 1008 palette entries in the container tail overridden
per-file: mapped by the hook, never consumed; those tail palettes turn out to be ONE
duplicated five-colour placeholder anyway, vestigial like the dummy sheets. (5) A full
13.6MB container with hue-coded palettes shipped as a mod file: ignored; the modloader has
no merge channel for system files (only fftpack.txt exists) and the game reads the pac
directly. (6) The ItemData per-item palette byte written live (the same table where byte
0x05 provably lands): no effect on battle art even across battle reloads, vestigial like
SpriteID. Conclusion: colours are resolved ONCE at process start from binary-baked data;
any true hue mechanism must act at launch, in-process (the DLL boards before the game
wakes), or repaint indices within the sheet (the proven lever above).

**CORRECTION 2026-08-19 (adversarial review before the round-9 retry; leave the bullets
above as written, they record what was believed that night, but do not build on rounds 2
or 5):** two of the negatives are weaker than recorded. Round 5's "full container with
hue-coded palettes" was in fact byte-identical to the vanilla copy (sha256 match across
all 2450 entries of the shipped g2d_huecoded.dat), so that round tested NOTHING, and the
recorded mechanism ("no merge channel for system files; the game reads the pac directly")
is contradicted by that same launch's own Reloaded log, which shows
prawl.fft.livingweapons adding system/ffto/g2d.dat, the loader hooking CFILE_DAT::Load and
CFILE_DAT::Decode for it, and 0xCF6120 bytes served from the mod file. A mod-supplied
FULL-CONTAINER route exists and has never been validly tested. Round 2 ran with NO
positive control in the same frame (that launch mapped only the palette entries, no
tex_161), and the loader's "mapping" log lines are registration-side, not serve-side, with
a known dead-detour-launch precedent; so round 2's negative is real but uncontrolled.
Round 9 (2026-08-19, tools/probes/lw251_g2d_clut_forge.py) re-runs the entry-156 file
override, forged FLAT per-row from the modded.pac copy, with the proven deranged tex_161
in frame as the control; its three pre-registered readings are in the probe header.
Sibling finds from the same review: the entry table's third word is 6 (not 0) on every
entry, each payload carries 16 unknown trailing bytes inside payload_len (load-bearing
only if the container is ever repacked), and entries 154-162 except 158 are byte-identical
between the loose leftover container and the modded.pac copy.

</details>

### [wep-spr-palette-block] Battle weapon colour lives in the classic sprite sheet's palette block

The colour a weapon wears when a unit swings it in battle comes from the 512-byte palette block
at the head of FFTPack file 71, `unit/battle_wep_spr.bin` (16 palettes x 16 BGR555, colour 0
transparent), and a mod-shipped copy of that file at
`<mod>/FFTIVC/data/enhanced/fftpack/unit/battle_wep_spr.bin` repaints it. TRUE HUE control:
colour values, not an index shuffle. PROVEN, owner live-verified 2026-08-19. The same day settled
WHICH palette each weapon uses: it is the X nibble of the classic BATTLE.BIN item-graphics record
at `0x02D3E6 + (itemId - 1) * 2`, confirmed four for four against owner captures, giving the full
127-weapon map offline with no census. Weapons draw from palettes 3-15 and effects from 0-2, with
ZERO overlap, so repainting a weapon palette can never retint a swing arc. Which palette a weapon
uses cannot be CHANGED by any known data channel: see [weapon-palette-assignment-walled].
A colour change needs NO game restart: swapping the shipped file between two battles of one
session repaints the next battle, owner live-verified 2026-08-21.

<details><summary>How we got here</summary>

**Claim:** the classic 2D weapon sheet's own palette block, not any g2d asset, colours the
in-battle weapon, and the FFTPack override channel reaches it.

**Mechanism:** round 12 shipped that file with ALL SIXTEEN palettes flattened (every non-zero
slot 1-15 of palette N set to one vivid colour unique to N, 0x0000 slots and slot 0 untouched,
bit 15 preserved) and the 4bpp pixel block left BYTE-IDENTICAL. One variable: colour, sourced
only from that block. Probe: tools/probes/lw251_wep_spr_forge.py.

**Evidence, machine-verified before it was eyeballed.** The loader distinguishes serving the
game's own copy (`[FFTPack] Accessing file 71 -> unit/battle_wep_spr.bin`) from serving a mod's
(`Accessing MODDED file 71`). The launch log reports 2 modded reads and 0 game-copy reads, so
the game demonstrably consumed our bytes; this pre-registered check is what makes the visual
read admissible at all. On screen, Ramza's swung sword rendered FLAT MAGENTA. Measured on the
capture: 738 magenta-family pixels in the whole frame, forming one diagonal blade band at
x 230-291, y 282-315, hue median 300.0 degrees with a 10-90 spread of 1.7 degrees. The forged
palette-15 code is hue 300.0 exactly. Same-frame vanilla controls calibrate the instrument:
grass spreads 14 degrees, the unit's armour 37, the tree 120. Nothing else in the frame is
magenta. Owner flip: in-session, 2026-08-19 ("That was it").

**Round 14, 2026-08-21: the restart question, answered NO.** The open half of this claim was
whether the GAME holds a decoded copy across battles, which would make any change relaunch-only.
The modloader half was already settled from source (`FFTPackFileOverrideStrategy.OnRequestRead`
does `File.OpenRead` per call and caches nothing). Sequence, run by the owner in one session:
the launch log first showed file 71 never read, which matters because the FIRST battle of a
launch reads from disk either way and would have proved nothing; battle 1 with the flat census
sheet deployed rendered a FLAT MAGENTA sword; out to the world map with the game still running,
the deployed file was swapped on disk to a pristine vanilla-content copy (md5
`78fa510234f9d5214527e958fc79f6dc` -> `cf6ad45e04fef2b1795dfff5b8e54c21`, both 85504 bytes,
the replacement md5-gated out of `0002.en.pac`); battle 2 in the SAME session, no relaunch,
rendered a normal steel sword. Final log state for the launch: 3 reads, all `Accessing MODDED
file 71`, 0 from the game's own copy, at 07:59:35, 08:02:04 and 08:02:41. So the game re-reads
per battle load AND re-uses what it read; there is no session-long VRAM copy. This retires the
"the art channel is cached for the whole session" belief that opened LW-251, for this file.
Scope: file 71 only. Menu art comes from the g2d container, read once per launch, and is
untested here. Probe: tools/probes/lw289_palette_selector.py --checklog. Owner flip: in-session,
2026-08-21 ("Back to vanilla colors").

**What this also settles.** The 2026-06-01 Ask B negative (docs/research/WEAPON_VISUALS_SCOPING.md,
"the HD weapon render does NOT index this 2D bin palette") is WRONG and is retired. Its two
holes: it painted only palettes 0 and 1 of 16, so any weapon indexing 2..15 rendered vanilla and
read as a negative (the sword proven here uses palette 15, which that test never touched), and it
never established that its file was served, the same gap that produced the retracted
[g2d-clut-bank-override]. The HD layer upscales the classic 2D art and does index this palette.

**Limits and opens.** The weapon-to-palette mapping is UNMEASURED: the ItemData `<Palette>` byte
is a poor candidate since the values seen there run 0-7 while the proven sword uses 15, and that
byte was separately shown inert in 2026-06. The flat forge is the instrument for mapping it, one
weapon per look. Sharing grain unmeasured (which items share a palette). Not yet known whether a
swap needs a restart: the file is read once per BATTLE LOAD (2 reads in one launch), not once per
process like the g2d channel, so a mid-session change may take on the next battle; UNTESTED.
Palettes 1 and 2 hold only 5 non-zero colours, at the BRIGHT slots 11-15, with 1-10 fully
transparent: they are effect palettes for the slash arcs and sparkles, so a weapon assigned to
one would render as a half-invisible ghost, not a recolour. Slot-0 and bit-15 semantics
untested (both preserved by the forge). AND THE SHEET IS NOT ONLY WEAPONS: about 60% of its
664 rows are weapon art, the rest are swing/slash arcs, sparkles and smoke puffs which draw
through these same 16 palettes. So repainting a weapon's palette may also retint any effect
that shares it, which has to be checked before this ships as a feature. That shared use also
gives a free second signal during a census: if the slash ARCS go flat but the BLADE stays
vanilla, the sheet is rendering and the weapon simply does not index that palette, which is a
far sharper negative than "nothing happened".

**2026-08-19, THE MAP, and two corrections to the text above.** The weapon-to-palette map is no
longer unmeasured. It is the classic PSX BATTLE.BIN item-graphics record, published by FFHacktics
and unmoved by the remaster: `offset(itemId) = 0x02D3E6 + (itemId - 1) * 2`, byte 0 high nibble X
= the weapon's palette, low nibble Y = the swing-arc palette, byte 1 = the graphic, interpreted
relative to the item's category. `battle_bin` is not in any `data/enhanced` pac; it lives at
`data/classic/0002.en.pac` as `fftpack/battle_bin.en.bin`, 1397096 bytes, FFTPack file index 0.
Probe: `tools/probes/lw289_battle_bin_palette_map.py`, map dumped to
`tools/probes/lw289_weapon_palette_map.json`.

Confirmed twice, by methods sharing no assumptions. LIVE: with all sixteen palettes forged to
distinct generated label hues and the serve proven from the loader log, the owner swung four
swords and every measured palette matched the file (Broadsword 14, Iron Sword 3, Mythril Sword 15,
Sleep Blade 15; all four Y=0 and all four frames carried a palette-0 red arc). OFFLINE: a
connected-component pass had already found that palettes 1 and 2 hold only five non-zero colours
each, at slots 11-15, so no weapon tile on the sheet can be drawn with them; BATTLE.BIN agrees,
with palettes 1 and 2 appearing as an effect palette Y twenty times and as a weapon palette X
zero times. Fifteen published PSX records match the shipped file byte for byte, which is the gate
the probe re-runs every time.

Distribution, which is the product constraint: X in use is 3(8) 4(9) 5(6) 6(7) 7(6) 8(10) 9(5)
10(9) 11(7) 12(5) 13(18) 14(20) 15(17); Y in use is 0(107) 1(10) 2(10). Thirteen palettes for one
hundred and twenty seven weapons.

CORRECTION 1, the file is THREE pages, not one image. palA at 0x0000 (512 B), page 1 at 0x0200
(32768 B, rows 0-255, weapons), palB at 0x8200 (512 B, byte-identical to palA), page 2 at 0x8400
(32768 B, rows 256-511, arcs and sparkles), palC at 0x10400 (512 B, a different bank of near-black
additive fades), page 3 at 0x10600 (18432 B, rows 512-655, impacts). Sums to exactly 85504.
Reading the pixel block as one 256x664 image is WRONG: it splices palB and palC into the picture
as 4-row junk bands at y 256-259 and y 516-519 and mislocates every row above 255. Page 2 uses
slots 11-15 and nothing else, 0 ink in slots 1-10, which is exactly why palettes 1 and 2 serve it.

CORRECTION 2, index-zone addressing does not exist in vanilla. An earlier reading of this sheet
claimed the artists packed several weapons into disjoint index zones of one palette; that was the
three-page layout being misread as repeated art. The zone structure itself is real (every full
palette is four short ramps plus a shared specular at slot 15, derived independently from the
palette bytes and from the pixel data), but of the 681 within-palette pairs of distinct weapon
graphics, ZERO are index-disjoint: minimum overlap 3 indices, median 7, and all 681 collide inside
the base zone {1,2,3,4}. So the grain today is one palette equals one colour. Re-indexing the
pixel block remains an untested escape hatch, worth roughly a factor of two, not ten.

CORRECTION 3, the effect-overlap worry above is DEAD, and this is the best news in the row. The
"Limits and opens" paragraph warned that "repainting a weapon's palette may also retint any effect
that shares it, which has to be checked before this ships as a feature". It has now been checked
and it CANNOT happen, for any weapon, ever. BATTLE.BIN's own distribution settles it: weapons take
X from 3 to 15 only, effects take Y from 0 to 2 only, and across all 127 weapons the two sets never
intersect. Palettes 1 and 2 additionally hold ink at slots 11-15 only, so no weapon tile could be
drawn with them even if one were assigned. The same paragraph's estimate that "about 60% of its 664
rows are weapon art" is superseded by CORRECTION 1: weapon art is page 1, rows 0-255, a clean third
of the file rather than a 60% share of a mis-measured 664. The free-second-signal trick that
paragraph describes (arcs go flat while the blade stays vanilla) still works and is still worth
using, since arcs and weapons draw from disjoint palettes by construction.

</details>

### [g2d-clut-bank-override] RETRACTED: g2d entry 156 is not a weapon palette bank and the game never reads it

RETRACTED 2026-08-19, hours after it was written, by four independent adversarial checks.
The claim was that battle weapon colours live in g2d entry 156 and that a shipped
`tex_156.bin` repaints them. Every load-bearing part of it is false: the game has NEVER read
entry 156 (zero serve lines across all 18 Reloaded logs on disk, while its same-sized
neighbours 154 and 155 are served routinely), the two live screenshots show the SAME shaded
weapon under two structurally different flat forges, and entry 156 is not a palette bank at
all. NOTHING may be built on this row. It is kept, struck through, as the record of how the
mistake was made.

<details><summary>How we got here</summary>

**How the false PROVEN happened (the lesson, worth more than the claim):** a 4608-byte blob
divides evenly into 144 rows of 16 u16 values, so it *can* be read as a palette bank; that
arithmetic works on any 4608-byte blob. Rendering the weapon sheet under those "palettes"
produced recognisable coloured weapons, which felt like confirmation, but the recognisable
part was the SHAPE, which comes from the sheet alone; the colours were arbitrary bytes read
as BGR555. Then a single owner sentence about a NIGHT screenshot ("gold for the base") was
taken as the live proof and written up as an owner flip. Measurement afterwards found ZERO
gold pixels anywhere in that frame, and that at night the engine rotates hue by ~135 degrees,
so no colour word spoken about a night capture is evidence at all. The unit was very likely
not even mid-attack, and battle weapons are only drawn during an attack animation.

**What the evidence actually says (all four lenses independently NOT_CONSUMED):**
- LOG (decisive): the modloader emits two distinct messages, `mapping G2D file N` at mod
  load (registration) and `[G2D] Accessing file N` from the CFILE_DAT hook (a true serve
  trace, not mapping-gated). Entry 156 is mapped every launch and `Accessing file 156` never
  appears in ANY log. Entry 161 is served exactly once per launch. Entries 154 and 155, the
  same class and same 4608-byte size, are served repeatedly, which kills the "this entry
  class is not logged" escape.
- CROSS-SHOT: the 03:00 and 03:29 launches shipped structurally different flat forges (a
  full-saturation hue wheel, then the coded forge). A single per-channel lighting transform
  maps one shot's weapon onto the other with ~14 RGB units of residual across a 93-unit
  ramp. Same weapon, same colours, twice.
- FLATNESS: the on-screen bow is a sculpted 5-step ramp (dark edge, mid, bright core), 22
  distinct colours, saturation 0.13-0.47. Every forged colour is saturation 0.5 or 1.0 and
  flat by construction. Calibration in the same frame: a genuinely flat patch of grass gives
  1 distinct colour, vanilla armour gives 20, the bow sits with the vanilla controls.
- ASSET IDENTITY: entry 156 is a 96x96 4bpp SPRITE, not colour data (68.5% of adjacent
  nibbles are identical, the locality signature of art). Its same-sized neighbours 154, 159,
  162 and 163 all render as an intact chocobo egg at 96x96 and 155 as the cracked egg; 156 is
  the hatch swirl in that animation family. The deployed forge merely smeared that swirl.

**Where battle weapon colour actually lives (found while retracting this, 2026-08-19):** the
game reads `unit/battle_wep_spr.bin` as FFTPack file 71 during battle, alongside file 63/64
(`battle_wep1/2_shp.bin`) and 65/66 (the seq files), and the unit sprites as
`unit/battle_*_spr.bin`. That is the CLASSIC sprite pipeline, and the modloader demonstrably
serves modded files through it (`[FFTPack] Accessing modded file 159 -> unit/battle_ramuza2_spr.bin`).
The g2d equipment sheet (entry 161) is served ONCE per launch during the menu/formation
screen, which fits menu/UI equipment art rather than battle art, so
[g2d-equipment-sheet-override] deserves its own re-examination before anything is built on it
(see the caveat added there).

**Superseded claim:** the palette bank the weapon sheet consumes offline can be replaced
through the same per-entry file channel that repaints the sheet itself.

**Mechanism:** round 9 (2026-08-19): tools/probes/lw251_g2d_clut_forge.py extracted entry
156 from the modded.pac container (4608 bytes, 144 rows of 16 BGR555 colours), forged every
colour except slot 0 and 0x0000 slots to ONE flat saturated colour per row (hue spread
across the bank so every row is self-identifying by colour; zeros and bit 15 preserved),
and deployed it beside the proven deranged tex_161 as the in-frame control.

**Evidence, ALL OF IT WITHDRAWN:** the "flat gold bow" was an unmeasured reading of a night
screenshot containing zero gold pixels. The "arrow changed colour" claim is worse than
unsupported, it is self-contradictory: entry 158 was never overridden in either round, and
the same arrow is cited in OPPOSITE directions by the two rows here, as the untouched control
that proves the sheet lever fired, and as the changed art that proves the bank lever fired.
At night 1222 of 1253 saturated pixels in the whole frame are blue, and a night capture from
2026-08-18 23:28, before any palette forge existed, shows the same blue dominance. Separately,
entry 158 is not the arrow sheet at all: rendered as 4bpp it is a second 512x512 equipment
sheet holding swords, shields and helmets.

**Live re-test, for completeness:** round 11 (2026-08-19 03:55 launch) shipped the coded
tex_156 with the bow's own sheet left VANILLA and entry 158 deranged as a same-frame control.
The owner swung two swords (Warbrand, Flamberge) and both looked normal. The log explains
why without ambiguity: that launch mapped 156 and 158 and the game accessed NEITHER. So the
control could not fire either, and the g2d file channel reaches only entries the game
actually reads.

</details>

### [live-icon-repaint] Equip icon pac patch: the WRITE still lands live and first draw reads it, but the already-drawn-icon refresh is UNRELIABLE (fired 2026-08-16, refused 2026-08-25 and 2026-08-26, same game binary)

Split verdict as of 2026-08-26 (owner-run re-probe, the LW-334 bisect). STILL TRUE: `modded.pac` accepts an in-place overwrite while the game runs (no lock, byte-exact verify), and an icon drawn for the FIRST time in the process afterwards shows the patched art on both surfaces (owner-witnessed: Tideward wore Emberward's red, screenshot 03:51). NO LONGER TRUSTWORTHY: the 2026-08-16 "leave the list and come back" refresh of an already-drawn icon. In tonight's session nothing evicted a drawn icon: with Galewall patched and Tideward restored in the pac, a tab round-trip, closing and reopening equipment, leaving to the world map, save plus load, and a title-screen reload ALL kept the stale art; only restart refreshes. The same recipe DID work on 2026-08-16, and the PE build key proves all three sessions ran the SAME exe (0x6A5EA53C / 0x18D78000, the 1.5.2 build taught 2026-07-24), so this is a conditional eviction with an unidentified condition, not a game change; a full battle enter/exit in a FRESH session also failed to refresh (owner-run, same night). Treat already-drawn icons as restart-only for design purposes. SAFETY CORRECTION 2026-08-26: the launch merge is INCREMENTAL, a pac patch SURVIVES relaunches until the matching loose file changes, so "relaunch repairs it" is FALSE; always restore explicitly. See [icon-refresh-unreliable] in the Contradicted section.

<details><summary>How we got here</summary>

**Claim:** the mod can change an equip icon's art in a running game.

**Mechanism:** the modloader (`fftivc.utility.modloader`) merges every enabled mod's
`FFTIVC/data/enhanced/...` files into `<game>/data/enhanced/modded.pac` AT LAUNCH, and the
game reads that pac, never our loose files. That is why mid-session `.tex` deploys appeared
to do nothing, and why the belief "textures are restart-only" survived so long: we were
editing the merge's INPUT after the merge had already run. Inside `modded.pac` each icon is
stored UNCOMPRESSED, VERBATIM and EXACTLY ONCE, at the same byte length as the `.tex` on
disk (cards 0x C860, smalls 0x3060), so an in-place same-length overwrite is well defined.
The listed `dataOffset` in `modded_files.txt` is section-relative, not a file offset, so the
probe locates each icon by CONTENT SEARCH (and asserts a single occurrence) rather than
trusting the index. The file is writable while the game holds it open (no exclusive lock).

**Refresh granularity (the load-bearing detail):** the engine does NOT re-read on every
frame. An icon already drawn on the open list does not change under you; navigating to
another inventory tab and back re-loads the list and shows the new art. So the refresh unit
is the list/tab load, not the frame and not the process.

**Evidence:** `tools/probes/live_icon_patch_probe.py` (stage 1 cold, stage 2 warm). Owner
live-verified 2026-08-16 with the game running: Tideward's slot showed Emberward's red art
in BOTH surfaces (screenshot), Galewall likewise after a tab round-trip, and both restored
cleanly. Safety: `modded.pac` is regenerated from the loose files at every launch, so any
patch is transient and a relaunch is a guaranteed repair.

**Corroborated by decompiling the modloader** (same day, `fftivc.utility.modloader.dll`),
which also explains the refresh granularity. The modloader has THREE override channels and
our icons sit in the third:
  * `fftpack/` (`unit/battle_*_spr.bin`): a read-request detour serves each request with a
    fresh `File.OpenRead` of the mod's file, NO cache -- genuinely live per read. This is
    the channel FFTColorCustomizer's live palette recolours ride.
  * `system/ffto/g2d/tex_N.bin`: served from a `_cachedFileBuffers` dictionary, read once
    per file index per process -- restart-only, and FFTColorCustomizer's own docs say so.
  * everything else, `nxd/` and `ui/**.tex` INCLUDING our icons: merged into `modded.pac`
    by `FFTOModPackManager.Apply()` once at startup. So writing our loose `.tex` mid-session
    can never show up, which is the whole origin of the wrong "restart-only" belief -- but
    the merged pac is just a file on disk, and patching THAT is what this row proves works.
The engine then re-reads through `faith::Resource::ResourceManager::OpenFileAndCache` and
holds textures in DX12 VRAM, which is exactly why an icon already drawn does not change
under you and a tab round-trip does: the round-trip is what re-loads the resource.
NOTE the modloader also exposes an unused `IFFTOModPackManager.AddModdedFile` API; no mod
in this install calls it, and it is registration-time, so it is not an alternative here.

**Date:** 2026-08-16

**RE-RUN 2026-08-26 (game 1.5.2, the LW-334 bisect), owner live:** stage 1 cold on Tideward
reproduced the 2026-08-16 result exactly: red art on both surfaces at the session's first
equipment draw (screenshot 03:51, game up since 03:26). Stage 2 warm FAILED where 2026-08-16
passed: with Galewall patched and Tideward restored in the pac, the owner climbed the full
eviction ladder (tab round-trip, close and reopen equipment, back to the world map, save
then load, title screen then reload the save) and EVERY rung kept drawing the first-draw
art on both shields. All restores verified byte-exact; the pac was clean at session end.
First conclusion that night, RETRACTED WITHIN THE HOUR by the owner's skepticism: "game
patch 1.5.2 coarsened the cache". REFUTED by the PE build key: the exe on disk reads
TimeDateStamp 0x6A5EA53C / SizeOfImage 0x18D78000, exactly the 1.5.2 key the guard
learned 2026-07-24 (commit 9959821), which predates the 2026-08-16 PASS by three weeks;
and the mod armed normally on 8-16, 8-25 and 8-26, which a build change would have
prevented. Same binary, opposite outcomes. Corrected conclusion: the pac stays writable
and is read at an icon's first draw, but the eviction that re-reads an ALREADY-drawn
icon is CONDITIONAL and the condition is unidentified; it fired on 2026-08-16 (fresh
launch made for the probe) and refused on 2026-08-25 and 2026-08-26 (tonight's session
hours old, heavy play behind it). Suspects: session age, VRAM/cache pressure, scene
history. BATTLE RUNG SETTLED LATER THE SAME NIGHT (owner-run, ~04:30, in a FRESH
session): with Galewall drawn pink and the pac then patched red, a full battle enter
and exit did NOT refresh it either, so every candidate eviction rung has now failed at
least once, in both a stale session and a fresh one. Whatever the
condition, LW-334's operational conclusion holds: the LW-295 splice cannot rely on
mid-session refresh (it lands minutes after boot, after the plain art is drawn). Bonus
context from the same session's log: the shipped splice currently stands itself down
anyway ("deployed base tex does not match the manifest's baked hash" for every icon),
because deploy_glow_tex.py overwrites the loose bases with tier variants; the interim
launch-merge path and the splice fight over the same bases and the guard picks the safe
loser. Probe: `tools/probes/live_icon_patch_probe.py` run by hand, patch/restore both
verified in the tool output.

**SECOND SAFETY CORRECTION, same night (~04:15): the launch merge is INCREMENTAL, so
this row's "regenerated from the loose files at every launch, relaunch is a guaranteed
repair" sentence above is FALSE.** The owner relaunched mid-probe and the fresh
session's pac still held the patch: Emberward's art counted TWICE (its own slot plus
Galewall's), Galewall's own bytes were nowhere, and `modded_files.txt` had not been
touched since the last deploy (Aug 19) while the pac's mtime was bumped with its
content preserved. The patch had to be restored BY HAND from the loose bytes (verified:
every icon back to exactly one occurrence). Rule: a pac patch persists until the
matching loose file changes; ALWAYS restore explicitly. Upside of the same fact: bytes
spliced into the pac (the LW-295 design) persist across restarts in a deploy-free
install, so splice work would legitimately show at the NEXT session's first draw; only
dev-time deploys kept erasing it. FINAL LADDER RUNG, owner-run in the fresh session:
with Galewall drawn pink on screen and the pac then patched red, a full battle enter
and exit did NOT refresh the icon. Every candidate eviction has now failed at least
once, in both a stale session and a fresh one.

</details>

### [crystal-countdown-off-switch] 0xFF in the crystal countdown byte switches crystallization OFF

Writing 0xFF to combat-slot base +0x07 removes a KO'd unit's death/crystal countdown entirely rather than setting it to 255, the game does not overwrite it, and writing a small value back restores the countdown unchanged; AWAITING OWNER FLIP, observed live 2026-08-21 with the owner reading the screen.

<details><summary>How we got here</summary>

**Claim (original wording):** 0xFF in the crystal countdown byte switches crystallization OFF

**Mechanism:** combat-slot base `+0x07` (== band entry `-0x15`) is the 3-hearts countdown, and it carries a distinguished OFF value. The game uses it itself: a battle where a KO'd unit never crystallizes reads `255` on EVERY unit on the field, where a normal battle reads `3` on every unit. The byte tracks the on-screen hearts exactly (3 hearts = 3, 2 hearts = 2). Writing `0xFF` to a unit mid-countdown makes the hearts VANISH, not read as 255, and the engine does not re-write the byte afterwards. Writing `2` back restores two hearts.

**Evidence:** owner live-verified 2026-08-21 on a dead guest unit mid-countdown, both directions, screen read by the owner each time. Dumps `crystal_dump_normal` / `crystal_dump_firstbattle` (all-3 versus all-255 across the whole field), `crystal_dump_guest_2hearts` (byte 2 while screen showed 2), `crystal_dump_guest_after_write` (255 held, no engine rewrite). Probe `tools/probes/crystal_counter_probe.py` verbs `dump` / `suppress` / `set`. Same session, separately: petrify does NOT arm the counter (a petrified unit held full HP, dead bit clear, counter 3, and zero bytes changed across combat `+0x00..0x17`).

**Not yet established:** whether `0xFF` is a true sentinel or merely any value greater than 3 (one write of `0x7F` settles it), and revive / battle-exit behaviour on a unit suppressed then restored. Supersedes the per-tick counter-pin for the purpose of DISABLING; the pin itself remains proven and shipped. Consumer: docs/TODO.md LW-299.

**Date:** 2026-08-21

</details>

### [turn-moved-acted-flags] Per-unit turn/moved/acted flags (the full-wait read)

The game exposes each unit's menu-open, moved, and acted flags at band +0x19C/+0x19D/+0x19E, with the falling edge of +0x19C as the turn-end decision point; Proven, owner live-verified 2026-07-09.

<details><summary>How we got here</summary>

**Claim (original wording):** Per-unit turn/moved/acted flags (the full-wait read)

**Mechanism:** band `+0x19C` menu-open flag (1 while the unit's move/act/wait menu is up), `+0x19D` moved, `+0x19E` acted; both latches engine-reset at next turn open; falling edge of `+0x19C` is the turn-end decision point

**Evidence:** owner live-verified 2026-07-09 (Mushin BANK on a still wait, SPENT on the strike); FFHacktics PSX struct 0x186-0x189 + probes/mushin_wait_probe.py tape; `+0x19F` = PSX Ability Outcome (0x02 hit, 0x01 turn ended)

**Date:** 2026-07-09

</details>

### [charm-survives-break] Charm survives its on-hit break

Charm can be made to survive its normal on-hit break by writing and holding both +0x49/0x20 and +0x54/0x20; Proven live (held through a hit) as of 2026-06-08.

<details><summary>How we got here</summary>

**Claim (original wording):** Charm survives its on-hit break

**Mechanism:** write+HOLD auth-copy `+0x49/0x20` AND `+0x54/0x20`

**Evidence:** held through a hit live; `0x140893C00` is a cosmetic decoy

**Date:** 2026-06-08

</details>

### [poison-pin-uncurable] Poison can be pinned + made uncurable

Poison stays pinned and uncurable by re-ORing band +0x48/0x80 and re-pinning the +0x4A timer (init 36); Proven live against a two-healer battle, 2026-06-09.

<details><summary>How we got here</summary>

**Claim (original wording):** Poison can be pinned + made uncurable

**Mechanism:** band `+0x48/0x80` re-OR + timer `+0x4A` re-pin (init 36)

**Evidence:** two-healer battle at 1.75x rate; `probes/poison_probe.py`

**Date:** 2026-06-09

</details>

### [doom-bytes-hasten] Doom bytes + hasten

Doom lives at bit +0x49/0x01 with its countdown at +0x59 (init 3), and writing the countdown down renders live on the head counter; Proven live 2026-06-09.

<details><summary>How we got here</summary>

**Claim (original wording):** Doom bytes + hasten

**Mechanism:** bit `+0x49/0x01`, countdown `+0x59` (init 3); write-down renders LIVE on the head counter

**Evidence:** Eclipsebolt +3 in play; doom research session

**Date:** 2026-06-09

</details>

### [ct-slam-extra-turn] Extra turn via CT slam

An extra turn is granted by slamming the scheduler CT byte (combat base+0x41, band entry +0x25) to 100; Proven live via Zwill v8, 2026-06-09.

<details><summary>How we got here</summary>

**Claim (original wording):** Extra turn via CT slam

**Mechanism:** scheduler CT = combat `base+0x41` (band entry `+0x25`) slammed to 100

**Evidence:** Zwill v8 played live; `probes/ct_probe.py` found the byte

**Date:** 2026-06-09

</details>

### [victim-turn-counting] Victim turn counting

A victim's turns can be counted by reading band entry +0x09: a value at or above 90 then below 70 marks one turn; Proven live in Maim and CharmLock victim windows, 2026-06-09.

<details><summary>How we got here</summary>

**Claim (original wording):** Victim turn counting

**Mechanism:** band entry `+0x09` read, ≥90 then <70 = one turn

**Evidence:** Maim + CharmLock victim windows in play

**Date:** 2026-06-09

</details>

### [reaction-suppression] Reaction suppression

A unit's reaction is suppressed by hold-zeroing 4 bytes at combat +0x94 with a one-shot restore; Proven live with Counter suppressed through 5 hits, 2026-06-09.

<details><summary>How we got here</summary>

**Claim (original wording):** Reaction suppression

**Mechanism:** hold-zero combat `+0x94` (4 bytes), one-shot restore

**Evidence:** Counter suppressed through 5 hits; `probes/cripple_probe.py`

**Date:** 2026-06-09

</details>

### [jobcommand-inject-grant] Command grant via JobCommand inject

A command is granted live by injecting the ability id into a JobCommand record slot plus the extend bit and a held learned bit; Proven with Barrage cast end-to-end, 2026-06-10.

<details><summary>How we got here</summary>

**Claim (original wording):** Command grant via JobCommand inject

**Mechanism:** ability id into record slot + extend bit + held learned bit

**Evidence:** Barrage cast end-to-end (Thief primary AND Steal-as-secondary)

**Date:** 2026-06-10

</details>

### [support-bits-grantable] Support bits grantable live

Calculation-gated support abilities take effect when OR-set at combat +0x98, while HP/MP Boost and Doublehand/Dual Wield are build-time-only (read-back fine, no effect); Proven live 2026-06-08.

<details><summary>How we got here</summary>

**Claim (original wording):** Support bits grantable live

**Mechanism:** OR-set combat `+0x98`; calculation-gated supports take effect

**Evidence:** Concentration etc. in play; HP/MP Boost + Doublehand/Dual Wield are build-time-only (read-back fine, no effect)

**Date:** 2026-06-08

</details>

### [movement-exactly-one] Movement is exactly-ONE-effective

Only one movement ability is ever effective: with both Font bits held perfectly, the engine honored only Lifefont; Proven via the retired Spiritual Font bit-grant attempt, 2026-06-10.

<details><summary>How we got here</summary>

**Claim (original wording):** Movement is exactly-ONE-effective

**Mechanism:** both Font bits held perfectly; engine honored only Lifefont

**Evidence:** the retired Spiritual Font bit-grant attempt

**Date:** 2026-06-10

</details>

### [master-teleportation-grant] Master Teleportation grantable

Proven: Master Teleportation can be granted live by holding the movement image for id 243; a player teleported in session (2026-06-10).

<details><summary>How we got here</summary>

**Claim (original wording):** Master Teleportation grantable

**Mechanism:** movement image for id 243 held

**Evidence:** player teleported in session

**Date:** 2026-06-10

</details>

### [mp-pair-on-band] MP pair on the band (+0x18/+0x1A)

Proven: the unit's MP lives on the band as a u16 pair at +0x18/+0x1A; MP was visibly restored on screen, gated per battle by MpLayoutOk (2026-06-10).

<details><summary>How we got here</summary>

**Claim (original wording):** MP pair on the band

**Mechanism:** `+0x18/+0x1A` u16 pair

**Evidence:** MP visibly restored on screen (gated per battle by MpLayoutOk)

**Date:** 2026-06-10

</details>

### [mid-battle-level-drift] Mid-battle level-up drift

Proven: live structs update on a mid-battle level-up while the roster keeps the pre-battle level until battle end; the Phoenix Down mis-credit chain was fixed via LevelMatchesRoster (2026-06-10).

<details><summary>How we got here</summary>

**Claim (original wording):** Mid-battle level-up drift

**Mechanism:** live structs update; roster keeps pre-battle level until battle end

**Evidence:** Phoenix Down mis-credit chain, fixed via `LevelMatchesRoster`

**Date:** 2026-06-10

</details>

### [slot0-quit-stick] slot0 quit-stick (pre-1.5 values)

Proven for pre-1.5 values: QUIT leaves slot0 at 0xFF on the world map and victory clears it to 0x66. The 2026-07-21 note updates 1.5: the in-battle marker is 0x10, a victory exit sampled 0x11, and the 1.5 post-QUIT value is unverified.

<details><summary>How we got here</summary>

**Claim (original wording):** slot0 quit-stick (pre-1.5 values)

**Mechanism:** QUIT leaves slot0 at 0xFF on the world map; victory clears to 0x66. 1.5 note (2026-07-21): the in-battle marker is 0x10 and a victory exit sampled 0x11 (see the 1.5 slot0 battle-phases Uncertain row); the 1.5 post-QUIT value is unverified

**Evidence:** `probes/sentinel_probe.py`

**Date:** 2026-06-10

</details>

### [static-array-freezes-band-live] Static array freezes on battle restart; band stays live

Proven: the static array freezes on battle restart while the band stays live, so tracker reads must use the 0x14184xxxx band (2026-06-09).

<details><summary>How we got here</summary>

**Claim (original wording):** Static array freezes on battle restart; band stays live

**Mechanism:** tracker reads must use the `0x14184xxxx` band

**Evidence:** fresh corpse 0/539 visible only in band; Ramza's real position only there

**Date:** 2026-06-09

</details>

### [frozen-zero-zero-twins] Frozen (0,0) twins in the band

Proven: the band can hold frozen (0,0) duplicate entries, and a real-position match outranks the (0,0) duplicate; seen live with slots 25+28 sharing an identity (2026-06-10).

<details><summary>How we got here</summary>

**Claim (original wording):** Frozen (0,0) twins in the band

**Mechanism:** real-position match outranks the (0,0) duplicate

**Evidence:** slots 25+28 same identity live; Zwill locate v7→v8

**Date:** 2026-06-10

</details>

### [plague-grace-window] Plague needs a grace window

Proven: the poison-bit edge and the acted window can land up to about 2 seconds apart in either order, so Plague needs a grace window (2026-06-10).

<details><summary>How we got here</summary>

**Claim (original wording):** Plague needs a grace window

**Mechanism:** poison-bit edge and acted window land up to ~2s apart, either order

**Evidence:** four open windows, zero latches, chocobo cleansed the "permanent" poison

**Date:** 2026-06-10

</details>

### [inb-flag-pulses] inb flag (+0x12) pulses mid-battle

Proven: the inb flag at +0x12 pulses mid-battle, so filtering must use sane field bounds plus slot sign, never the flag itself (2026-06-09).

<details><summary>How we got here</summary>

**Claim (original wording):** inb flag (`+0x12`) pulses mid-battle

**Mechanism:** filter by sane field bounds + slot sign, never the flag

**Evidence:** half the live enemies read 0 at any instant

**Date:** 2026-06-09

</details>

### [cursor-tile-addresses] Cursor tile (x,y) addresses

Proven: the cursor tile reads live at cursor X 0x140C64A54, cursor Y 0x140C6496C, and linear index 0x140C64E7C, all u8 (2026-06-11).

<details><summary>How we got here</summary>

**Claim (original wording):** Cursor tile (x,y) addresses

**Mechanism:** cursor X = `0x140C64A54`, cursor Y = `0x140C6496C`, linear idx = `0x140C64E7C` (all u8)

**Evidence:** live diff (Y tracked 5→11 on hover) + FFTHandsFree CommandWatcher reads the same

**Date:** 2026-06-11

</details>

### [treasure-master-tile-mark-writable] Treasure Master: the native tile mark IS writable

Proven: the native tile mark is a per-tile flag (bit 0x80 of a status byte) and write-holding it makes the engine render the mark itself, solving Treasure Master's output half; per-tile addressing for arbitrary tiles is still open (2026-06-11).

<details><summary>How we got here</summary>

**Claim (original wording):** **Treasure Master: the native tile mark IS writable**

**Mechanism:** The hover-+-2 mark is a per-tile FLAG (bit `0x80` of a status byte), NOT a coordinate list (that's why the earlier AoB scan missed it). Found via a differential toggle scan (mark on/off/on/off ×N, keep only addresses that flip in lockstep AND survive a live re-read): one marked tile flips bit `0x80` at THREE copies `0x140e7c3bb` / `0x14102fb23` / `0x14117fe67` (frame/buffer copies). WRITE+HOLDING `0x80` onto them with the tile UNMARKED made the engine RENDER the mark itself — no input, no button. Release → engine clears it (so the DLL holds, like stat growth). Output half of Treasure Master SOLVED. `probes/mark_probe.py togglefind/holdmany`. OPEN: per-tile addressing (these 3 are THIS tile's copies; need the stride/layout to mark an ARBITRARY tile — mark a 2nd known tile, toggle-scan, derive the base+stride). Heap addresses may rebase per battle → resolve via a pointer chain or AoB, not hardcode.

**Evidence:** 2026-06-11

**Date:** 

</details>

### [battle-tile-cluster] Battle tile structures cluster at 0x140C6xxxx

Battle tile structures cluster at 0x140C6xxxx: the terrain grid at 0x140C65000 (static map data) and the move-tile list at 0x140C66315. Proven as of 2026-06-11.

<details><summary>How we got here</summary>

**Claim (original wording):** Battle tile structures cluster at `0x140C6xxxx`

**Mechanism:** terrain grid `0x140C65000` (7 bytes/tile, 9×8 window, STATIC map data — marking never wrote here); move-tile list `0x140C66315`

**Evidence:** FFTHandsFree CommandWatcher.cs + mark_probe diffs

**Date:** 2026-06-11

</details>

### [afterimage-speed-ramp] Swiftedge "Afterimage": Speed ramps damage per ACTED turn

Swiftedge's Afterimage signature ramps Speed (and thus damage) by one stack per acted turn, resetting on a hit; proven live with owner confirmation on 2026-06-14.

<details><summary>How we got here</summary>

**Claim (original wording):** Swiftedge "Afterimage": Speed ramps damage per ACTED turn

**Mechanism:** `GrowthEngine.HoldAfterimage` owns CSpeed `+0x24` = round(natural×(1+SpeedFactor)) + stacks; +1 stack per acted turn (TurnTracker), reset on a hit; one writer (Route yields the speed lane to it). Speed×WP (f99) = the damage, so the ramp accelerates damage. NOTE the stack counts ACTED turns (the global acted flag), not move/wait.

**Evidence:** Patrick confirmed: Swiftedge damage climbed per acted turn; log `afterimage: Swiftedge wielder Speed 13 -> 16 (stacks 1/5)`

**Date:** 2026-06-14

</details>

### [command-grant-any-job] Command grant resolves for ANY normal-executor job (not just Thief) + renders/casts

The command-grant inject works for any normal-executor job, not just Thief, and the granted command renders and casts. Proven 2026-06-14 via Night Sword's Shadowblade on a Knight.

<details><summary>How we got here</summary>

**Claim (original wording):** Command grant resolves for ANY normal-executor job (not just Thief) + renders/casts

**Mechanism:** `NightSword.cs` reuses the Barrage inject with OPEN eligibility (`Barrage.TryResolveGrant`); Shadowblade (165) injected into the wielder's JobCommand record. Generalizes the 2026-06-10 Thief-only command-grant row.

**Evidence:** Patrick confirmed Night Sword works on a Knight (job 76, record 7); log `night sword: ACTIVE ... Shadowblade (165) added to record 7`

**Date:** 2026-06-14

</details>

### [renewal-regen-aura] Renewal: turn-edge regen AURA heals adjacent allies

The Mending Staff's Renewal signature heals the wielder and all adjacent allies (diagonals included) for 10 percent max HP on the wielder's completed-turn edge; enemies and far allies are excluded. Proven live 2026-06-16.

<details><summary>How we got here</summary>

**Claim (original wording):** Renewal: turn-edge regen AURA heals adjacent allies

**Mechanism:** Mending Staff (id 61) +3: on the wielder's completed-turn edge (TurnTracker), every ALLY within Chebyshev 1 (8 surrounding tiles, diagonals included) + the wielder is healed round(maxHP×0.10) via the guarded band `+0x14` write (`Renewal.cs`); ally-filter `Band.AllyFingerprints`, twin-dedupe, never revives. Silent (no numeral -- see the walled row). Additive clone of the dormant Wyrmblood, differing by metric (Chebyshev) + amount.

**Evidence:** Patrick confirmed live ("It works"): wielder + adjacent allies (incl. diagonal) mended each turn; enemies/far allies excluded; log `renewal: turn-edge aura -- ally at (x,y) mended N HP`

**Date:** 2026-06-16

</details>

### [kill-attribution-low-entropy] Kill attribution survives a low-entropy roster (collapsed brave/faith)

When roster fingerprints collapse (units sharing level/brave/faith), kill attribution now disambiguates by the acting unit's own weapon byte at band +0x04, falling back to the prior ambiguous-bail otherwise. Proven live 2026-06-26.

<details><summary>How we got here</summary>

**Claim (original wording):** Kill attribution survives a low-entropy roster (collapsed brave/faith)

**Mechanism:** the actor->roster fingerprint `(level,brave,faith)` collapses when units share stats (brave pinned, levels within the ±9 drift); `ActorResolver.FingerprintPlayer`/`MainHandFromRoster` now disambiguate by the acting unit's OWN weapon read at band `+0x04` (== `CWeapon`, GrowthEngine's self-map byte): full-scan the roster, prefer the slot whose hands hold that weapon, else reproduce the prior ambiguous-bail exactly

**Evidence:** brave/faith forced to 65/65 on two units with different weapons live: Reis (Hexweave Bag 118, via a Jump -- delayed-action path) and Cloud (Perseus Bow 91) BOTH credited to their own weapon, zero "could not determine"; the override log showed the collision twin actively losing (`delayed=[118] stamp=[91] live=[91]`). `probes/collapse_stats.py`, `livingweapon.log`, commit `b42f77a`

**Date:** 2026-06-26

</details>

### [crystal-counter-pin] Crystal "3 hearts" counter pinnable to STOP crystallization

The crystal countdown is the combat-slot byte at base +0x07; holding it at 3 stops crystallization while still allowing revival and post-battle recovery, and Ramza follows the same rule. Proven live 2026-06-16.

<details><summary>How we got here</summary>

**Claim (original wording):** Crystal "3 hearts" counter pinnable to STOP crystallization

**Mechanism:** the death/crystal countdown is the combat-slot byte at base `+0x07` (== band entry `-0x15`); a KO'd unit steps it 3-2-1-0 once per its turn, 0 = crystallize (permanent loss). Holding it at 3 stops crystallization; a held ally still revives via Phoenix Down/Raise, and a held KO'd ally is recovered to the party post-battle. RAMZA is not special in IC (same counter, no instant game-over) so the pin covers him.

**Evidence:** found + pin-gating + Phoenix-Down-while-pinned + post-battle recovery ALL confirmed live (Patrick, "works flawlessly") via `probes/crystal_counter_probe.py` and the shipped Sanctuary signature

**Date:** 2026-06-16

</details>

### [sanctuary-anti-crystallize] Sanctuary: battlefield-wide anti-crystallize while the bearer lives

The Staff of the Magi's Sanctuary signature re-pins every fallen ally's crystal counter to 3 while a +3 bearer is alive and fielded, lifting the instant the bearer dies or unequips. Proven live 2026-06-16.

<details><summary>How we got here</summary>

**Claim (original wording):** Sanctuary: battlefield-wide anti-crystallize while the bearer lives

**Mechanism:** Staff of the Magi (id 66) +3: each in-battle tick, while a +3 bearer is alive and fielded, every fallen ALLY's crystal counter (`Band.Entry(s) - 0x15`) is re-pinned to 3 (guarded W8) so it never crystallizes; dead-streak guarded (3 consecutive fallen ticks before the first write, so a phantom band-load never touches a live unit), ally-filtered (`Band.AllyFingerprints`), lifts the instant the bearer dies/unequips. `Sanctuary.cs`.

**Evidence:** Patrick: "works flawlessly" -- hearts froze at 3, bearer-death lifted it, enemies still crystallized, Phoenix-Down + post-battle recovery both fine

**Date:** 2026-06-16

</details>

### [noncharge-instant-cast] Non-charge support grant casts magick INSTANTLY (calc-gated, honored live)

OR-setting the Non-charge support bit (band +0x7F mask 0x04) live makes charge spells resolve instantly the same turn, because the engine consults the support field at charge-resolution time. Proven live 2026-06-16.

<details><summary>How we got here</summary>

**Claim (original wording):** Non-charge support grant casts magick INSTANTLY (calc-gated, honored live)

**Mechanism:** OR-set the Non-charge support bit in the `+0x98` support bitfield -- ability.en Key 483 -> live support id 227 -> band `+0x7F` mask `0x04` (== combat `+0x9B`), MSB-first. The engine consults the support field at charge-resolution time, so a LIVE grant makes the spell resolve the same turn with NO charging state. This is the calc-gated class (like Concentration), NOT build-time-only (Doublehand/Dual Wield read back fine but do nothing). No `overrideabilityactiondata.nxd` edit -- we grant the EXISTING ability. Foundation for a 4th-staff "instant cast for N turns" signature: hold the bit via TurnTracker, clear after. Swiftspell (Key 482 -> id 226, band `+0x7F` mask `0x08`) is the half-charge sibling.

**Evidence:** Patrick confirmed live: granted to a Black Mage (fp 365/99, supports were only 224/Reequip) via `probes/noncharge_probe.py grant`, then a charge spell cast instantly -- "NEW MECHANICS UNLOCKED"

**Date:** 2026-06-16

</details>

### [turn-owner-team-field] Turn-owner TEAM field is reliable for TURN-LEVEL gating (NOT per-action identity)

The condensed turn-queue team field at 0x14077D2A2 (0 player, 1 enemy, 2 ally) is stable across a whole turn and reliable for coarse whose-turn gating, but is garbage on save-load entry and wrong for per-action player identity, so reads must fail safe. Proven live 2026-06-16.

<details><summary>How we got here</summary>

**Claim (original wording):** Turn-owner TEAM field is reliable for TURN-LEVEL gating (NOT per-action identity)

**Mechanism:** condensed turn-queue `+0x02` (`0x14077D2A2`, u16): 0 = player turn, 1 = enemy, 2 = ally. STABLE across a whole turn -- held `1` through multiple enemy units (L48-51) over full menu/move/target/cast sequences and even survived a `battleMode`-0 cast blip (team is steadier than battleMode); held `0` through the player's own turn and did NOT follow cursor hover. RECONCILES the KillTracker "unreliable team field" note: that field is garbage (reads 3) on a SAVE-LOAD entry and is wrong for PER-ACTION player identity, but is reliable for the coarse "whose turn is it" question DURING an actual turn. Use a fail-safe read (`team != 1 && team != 2` => treat as safe/player) so an entry-garbage frame never strips a player grant. Matches FFTHandsFree's BattleEnemiesTurn signal.

**Evidence:** `probes/turnteam_probe.py` run live 2026-06-16 (player turns team=0 incl. hover; enemy turns team=1 across L48-51 whole turns; no in-battle team=3; the `-1` tail was the process quit, not a signal). Used by the Shadow Blade turn-gate.

**Date:** 2026-06-16

</details>

### [full-kit-transplant] Full-command kit transplant (live, castable)

Overwriting a JobCommand record's 16 ability slots plus holding the per-unit learned bit swaps a unit's whole command set to any composed loadout live, castable, per-record; the full PvP draft loop (control an enemy and draft its kit) is proven. Proven live 2026-06-18.

<details><summary>How we got here</summary>

**Claim (original wording):** Full-command kit transplant (live, castable)

**Mechanism:** overwrite a JobCommand record's 16 ability slots at `0x14067E213 + rec*25` (flag prefix at -3, extend bits MSB-first per byte) + hold the per-unit learned bit at roster `+0x32 + jobIdx*3`; the engine reads the record live, so a unit's WHOLE command becomes ANY composed loadout -- not just another class, but an arbitrary mix of ability ids from any classes (extends the single-ability Barrage grant). Per-record (job-global), not per-unit.

**Evidence:** Patrick cast it live: rec 14 (Steal 108-115) -> rec 7 (Knight Rend 138-145); then a hand-picked Thief+Knight+Monk mix (108/138/100/140/102/110/104/145) both rendered + executed, no crash. END-TO-END on a HUMAN ENEMY: puppeted an AI Wizard (slot 9, dual-write agency +0x05 AND +0x1EE) + drafted Knight Rends into rec 11 (Black Magic) -> Patrick drove the enemy mage, her command listed Rend Helm/Rend MP. The full PvP draft loop (control enemy + draft its kit) is proven. Base verified vs rec 8 Aim / rec 9 Martial Arts / rec 14 Steal

**Date:** 2026-06-18

</details>

### [post-construction-sprite-preview-only] POST-construction live +0x00 write re-skins only the STATUS-PAGE preview, texture-gated (field welded; construction-time write does re-skin the field)

Proven as of 2026-06-18: writing the combat-struct sprite byte +0x00 after a unit is built only changes its status-page model preview (textured only if the sprite is resident in the encounter), while the battlefield model stays unchanged; a construction-time write is what actually re-skins the field (next entry).

<details><summary>How we got here</summary>

**Claim (original wording):** POST-construction live `+0x00` write re-skins only the STATUS-PAGE preview, texture-gated (field welded -- but a CONSTRUCTION-time write DOES re-skin the field, see next row)

**Mechanism:** opening a unit's status/jobs page rebuilds its MODEL PREVIEW from the live combat-struct `+0x00` (SpriteSet) at `BattleUnitsBase 0x141853CE0 + slot*0x200` -- so the model-SELECT path reads `+0x00` live, NOT only a build-time blueprint. GATE = **texture residency**: a sprite resident in the encounter (a unit that actually SPAWNED) renders fully textured; a non-resident sprite renders a BLACK SILHOUETTE (geometry loads, no skin/material). **The BATTLEFIELD model does NOT follow a live `+0x00` write** (built once at construction) -- so this is NOT yet a live field re-skin; it maps the mechanism + the render gate, which is the map for cracking the field render. Works on an AI enemy.

**Evidence:** Patrick live 2026-06-18: enemy goblin slot 0 +0x00=`0x1E` (Agrias, NOT spawned) -> black silhouette in the status preview; +0x00=`0x03` (Ramza, on his team = resident) -> textured Ramza in the status preview; BUT the on-field unit stayed a goblin and took its turn as a goblin (confirmed again with an Archer `0x66`: preview-only, field unchanged). RESIDENCY NUANCE: the preview's borrowable texture set is populated by a unit's NATIVE construction sprite (Archer `0x66` from a real archer unit = borrowable, textured) but NOT by a roster spriteSet-OVERRIDE (a generic re-skinned to Agrias `0x1E` rendered the BLM herself fine, yet an enemy set to `0x1E` stayed BLACK) -- i.e. the texture loader keys off the construction sprite. `tools/probes/combat_sprite.py` (NB static array `0x141853CE0` has ghost/stale slots; match by on-screen HP/level or read the `0x14184xxxx` band)

**Date:** 2026-06-18

</details>

### [construction-time-enemy-field-reskin] CONSTRUCTION-time +0x00 write re-skins the ENEMY BATTLEFIELD model, textured

Proven as of 2026-06-19: hooking CopyJobEffectsToUnit during unit construction and writing combat +0x00 re-skins the enemy's on-grid battlefield model, textured when the target sprite is resident, with job, kit, and class label untouched.

<details><summary>How we got here</summary>

**Claim (original wording):** CONSTRUCTION-time `+0x00` write re-skins the ENEMY BATTLEFIELD model, textured

**Mechanism:** hooking `CopyJobEffectsToUnit @0x14EFD2F20` (fires per unit DURING construction, before the field model is built from `+0x00`) and writing combat `+0x00` re-skins the on-grid BATTLEFIELD model -- not just the status preview. Textured for a RESIDENT target sprite; job/class + kit untouched (only the sprite byte changes); the equipment-graphic overlay does NOT follow. This is the enemy-side equivalent of the roster lever, and it is what the POST-construction raw write (prev row) could not do -- the difference is purely WHEN the write lands vs the field build. Texture residency for an off-roster (non-resident) body is the open variable (likely black silhouette; make it resident).

**Evidence:** Patrick confirmed live 2026-06-19 (screenshot): the `prawl.fft.skinspike` mod re-skinned every AI enemy to a TEXTURED Agrias on the tactical grid (Agrias resident via a deployed party Agrias); class label stayed e.g. Skeleton, no equipment overlay. `Dev/FFTMultiplayer/SkinSpike/` (`CopyJobEffectsToUnit` hook + `ReskinPolicy` enemy `+0x06 & 0x20`)

**Date:** 2026-06-19

</details>

### [roster-two-byte-identity-swap] Player FULL identity swap via two roster bytes (body + portrait/name, kit intact)

Proven as of 2026-06-18: two independent roster writes before construction swap a player unit's full identity, SpriteSet +0x00 for the 3D body and the id at +0x230 for portrait, name, and voice, while the job and command kit stay unchanged.

<details><summary>How we got here</summary>

**Claim (original wording):** Player FULL identity swap via two roster bytes (body + portrait/name, kit intact)

**Mechanism:** the unit is built from the roster blueprint (base `0x1411A7D10`, stride `0x258`) at battle-entry / Organize refresh, so pre-construction writes re-skin it. TWO independent levers: (1) BODY = `SpriteSet` `+0x00` (u8) -- the battle 3D model; (2) PORTRAIT + NAME + VOICE = the identity id at `+0x230` (Dicene's mislabeled "VoiceID", written as a u32). Both are INDEPENDENT of Job (`+0x02`), so body+face swap while the command/kit stays. Uniques carry `voiceID == canonical char id` (Agrias `0x1E`, Cloud `0x32`, Orlandeau `0x0D`, Ramza `0x01`); generics carry a high name-pool id (`0x12A`/`0x23F`/`0x3xx`). The COMBAT-struct `+0x00` write is the opposite (label-only -- see the Uncertain row); the roster is the pre-construction lever that actually re-skins. Valid bodies = Dicene `SpriteSetID` enum (story `0x01`-`0x34` + `0xA2`-`0xA8`, generics `0x80`/`0x81`/`0x82`).

**Evidence:** Patrick confirmed live 2026-06-18: slot 3 generic male Black Mage (job `0x50`) -> `SpriteSet 0x1E` flipped the BODY to Agrias (kit stayed Black Magic, portrait stayed generic); then `+0x230 0x10F->0x1E` flipped the PORTRAIT + name to Agrias too -- a complete identity swap from two writes. `tools/probes/roster_sprite.py set 3 0x1E` + `setraw 3 0x230 0x1E 4`. (Natural corroboration: slot 8 already carried Cloud body `0x32` on a Thief job `0x53`.)

**Date:** 2026-06-18

</details>

### [engine-actor-pointer] Engine actor pointer 0x14186AF68 names the acting unit's combat frame, both teams

Proven as of 2026-07-01: the engine global at 0x14186AF68 holds a pointer to the acting unit's real combat frame for both teams, updating near turn start, with known caveats that the Acted-flag edge can lag and it may name a reactor during reactions.

<details><summary>How we got here</summary>

**Claim (original wording):** Engine actor pointer `0x14186AF68` names the acting unit's combat frame, both teams

**Mechanism:** `Offsets.ActorPtr`: engine global holding a POINTER to the acting unit's combat FRAME base (`FrameReadBase + seat*CombatStride`; frame `+0x1C` = that seat's band entry). Updates at/near turn start and visits EVERY acting unit's REAL seat (never a mirror copy). Reads 0x0 at battle-open idle. CAVEATS that stand: the Acted-flag EDGE can lag the action by many seconds, so pointer-at-edge attribution can credit the NEXT actor (observed live -- see the frame+0x1A0 action-record note for the future fix); may name the REACTOR during a reaction (FFTMultiplayer caveat, unverified here). `TurnTracker` resolves pointer-first (TQ-fingerprint fallback); `Iai`'s release rides it.

**Evidence:** `tools/probes/unitid_probe.py watch` x3 sessions 2026-07-01 (named both id-42 wielders' own seats while the TQ tuple was ambiguous x2; enemy seats on enemy turns; visited seat 25 that fp-Locate was ambiguity-bailing on). **Patrick live-verified 17:44 battle: both wielders released `[actor pointer]` at their OWN turns (incl. the previously cap-starved wielder), zero `[wall-clock cap]`.** Related find, unexplored: per-unit action record at frame+0x1A0 (global action counter stamped on the actor's own frame, 0x10/0x11/0x12).

**Date:** 2026-07-01

</details>

### [frame-1fc-nameid-mirror] Frame +0x1FC (u16) mirrors the unit's roster nameId (Offsets.ANameId, band-entry-relative +0x1E0)

Proven as of 2026-07-01: the combat frame field at +0x1FC back-references the unit's roster nameId, exact for player seats and distinct per enemy unit; combined with the actor pointer it gives Iai an identity-match release, live-verified by Patrick.

<details><summary>How we got here</summary>

**Claim (original wording):** Frame `+0x1FC` (u16) mirrors the unit's roster nameId (`Offsets.ANameId`, band-entry-relative `+0x1E0`)

**Mechanism:** Back-reference to the roster's nameId (`RNameId`, roster `+0x230`). Player seats exact-match their roster nameId; enemy seats read distinct sane per-unit values (both-teams unique id). A revolving engine MIRROR frame (band seat 28 observed, cloning different real units' identities over time WITH real positions) carries the MIRRORED unit's nameId too -- it cannot distinguish original from copy by itself, but combined with the actor pointer (always the real frame) it gives `Iai` an identity-match release that survives `Wielder.Locate` ambiguity-bailing on the mirror.

**Evidence:** `unitid_probe.py find` SCAN-A, two separately-loaded battles 2026-07-01 (players exact: Ramza 1 / Samurai 298 / Ninja 271; enemies distinct: 918, 992, 1008, ...; the mirror read 298 then 271 in one session). Consumed by `Iai` v2 (`Wielder.RosterNameId` + `Iai.Policy.ReleaseSignalById`). **Patrick live-verified 17:44: the identity-match release fired for the wielder the mirror had been starving.**

**Date:** 2026-07-01

</details>

### [charged-summon-no-credit] Cross-turn charged-summon kill -> no credit (untracked-delayed arm)

Proven as of 2026-06-26: a charged summon cast by a unit with no living weapon is snapshotted at cast and armed on the landing edge for a 45-tick window, so a kill maturing in that window is stamped no-credit instead of leaking to the next armed unit.

<details><summary>How we got here</summary>

**Claim (original wording):** Cross-turn charged-summon kill -> no credit (untracked-delayed arm)

**Mechanism:** a summoner (no living weapon) casts a CHARGED summon: snapshot at cast (band `+0x45` Charging `0x08`, bit 0->1 while the untracked actor is latched -> `_chargeUntracked[s]`) + arm on the landing edge (1->0) for `Tuning.UntrackedDelayedWindow`=45 ticks; a kill maturing in the window stamps `_lethalUntracked` -> no credit, so the cross-turn summon kill no longer leaks to the next armed unit to latch. Composes with the in-period `_lethalUntracked` stamp + the `ActorResolver.actorUnarmed` empty-resolve guard; tracked-delayed (Jump) still wins via the `delayed == null` guard. `KillTracker.Delayed.cs`/`Corpses.cs`, `tools/probes/charging_probe.py`.

**Evidence:** Patrick confirmed live 2026-06-26: summon kills NO-credited on band slots 10 (x2) and 8 -- the slot-8 killing blow carried the `[w:37]` Chaos Blade sticky-latch tag yet was correctly buried (`killed by a unit holding no tracked weapon -- no credit`); Chaos Blade's own Phoenix-Down kill #10 still credited (no over-suppression); the 45-tick window held on every observed summon. Charging-bit SET observed via `charging_probe.py`.

**Date:** 2026-06-26

</details>

### [current-brave-write-sticks] One-shot CURRENT-brave write on a player unit STICKS (no re-normalize)

Proven as of 2026-07-02: writing a player unit's current brave once at combat +0x2B (band +0x0F), leaving orig +0x2A untouched, sticks with no engine re-normalization.

<details><summary>How we got here</summary>

**Claim (original wording):** One-shot CURRENT-brave write on a player unit STICKS (no re-normalize)

**Mechanism:** write combat current-brave +0x2B (band +0x0F) once; orig +0x2A untouched

**Evidence:** probes/brave_oneshot_probe.py: Ramza 97->92 held across several rounds incl. his own turns; Patrick live-verified in-session (Kobu rework premise)

**Date:** 2026-07-02

</details>

### [victim-identity-sane-at-credit] Victim identity fields read SANE at the kill-credit edge (Reliquary P1)

Proven as of 2026-07-05: a victim's identity fields (nameId, job byte, undead bit) read sane and identical at all three capture points through the credit tick, and the dead-edge capture is the adopted Phase 1 point; the undead bit remains unexercised.

<details><summary>How we got here</summary>

**Claim (original wording):** Victim identity fields read SANE at the kill-credit edge (Reliquary P1)

**Mechanism:** `VictimProbe.cs` captures victim {nameId band `+0x1E0`, job byte combat `+0x03` (band `-0x19`, Puppeteer's JobOff), undead bit `+0x45/0x10`} at THREE points: alive-path refresh beside `_slotId`, deadStreak==1 edge, fresh read at CreditKill. All three points read sane AND identical on every observed kill -- the corpse's identity fields survive to the credit tick (unlike the AREC record, which cleared). Adopt the DEAD-EDGE capture for Phase 1 (earliest identity-consistent point; the AREC-clear precedent says do not lean on credit-time reads). Field bonus: an enemy read job=94 in a random battle (evidence for the Dark-Knight-94-live reading). OPEN: undead bit never exercised (no undead killed yet); mirror-seat dup never observed (belt held).

**Evidence:** 8+ kills across 3 battles 2026-07-05 (nameIds 935/799/792/808/789/928, jobs 94/98/104/130, has=1 throughout): `livingweapon.log` 07:55-07:58 victim-probe triplets + `flight_20260705_075603_battle-exit.jsonl` [victim] records; Patrick confirmed "everything verified and works"

**Date:** 2026-07-05

</details>

### [boss-canonical-nameid-stable] Named-boss discriminator = the CANONICAL nameId: story units stable across reloads; generic nameIds RE-ROLL per load (Reliquary P2)

Proven as of 2026-07-05: story units read a nameId equal to their canonical character id and it stays stable across battle reloads, while generic enemies' nameIds re-roll every load, so the Legends table keys on curated canonical ids only.

<details><summary>How we got here</summary>

**Claim (original wording):** Named-boss discriminator = the CANONICAL nameId: story units read nameId == canonical char id (== their low job byte), STABLE across battle reloads; generic nameIds RE-ROLL per load (Reliquary P2)

**Mechanism:** BattleCensus dumps, Zirekile fought twice from fresh loads: Gafgarion read nameId=5 job=5 BOTH loads (named units 12/23/52 likewise stable); the seven generic enemy Knights re-rolled 453/433/267/450/475/347/371 -> 354/330/267/455/486/375/308; player roster pool (1/451/118/329/608/714/588) stable and disjoint from canonical boss ids (unique players carry their OWN canon ids, never enemy-only bosses'). Legends table keys on curated exact canonical ids; generic victim identity is per-load only (fine -- Marks/last-victim never needed it to persist). Corollary: band nameId survived a between-loads job change on a player unit (s25 nameId=451, job 76->80).

**Evidence:** census blocks 09:10:26 + 09:34:26 livingweapon.log 2026-07-05; flight_20260705_092548 census record; owner ran both loads

**Date:** 2026-07-05

</details>

### [flavor-line-overwrite-displays] Equip-card DESCRIPTION renders from paintable heap buffers: in-place same-length flavor-line overwrite DISPLAYS on the card (Reliquary P4)

Proven as of 2026-07-05: overwriting a weapon's flavor anchor bytes in-place with a same-length string renders on the equip card and survives reopen, scroll, and battle stress, clearing the gate for the Reliquary earned-line display; the probed weapon's own sites evict for the session as the accepted cost.

<details><summary>How we got here</summary>

**Claim (original wording):** Equip-card DESCRIPTION text renders from paintable heap buffers -- an in-place same-length flavor-line overwrite DISPLAYS on the card (Reliquary P4)

**Mechanism:** `FlavorSpike.cs` (#if LWDEV, F6): overwrite a weapon's kills-site flavor ANCHOR bytes (`CardSites.Site.AnchorAddr`) with a same-char-count ASCII string via the painter's own guarded path (verify-before-overwrite vs `pat.Flavor`, `Writable` gate, WPM). The card rendered the probe text ("P4 FLAVOR PROBE -- THE BLADE REMEMBERS"), renderer WRAPS the single logical line dynamically, the "Kills: 0" slot below was untouched, and the render survived the stress set (card reopen x5, weapon scroll, battle enter/exit) with other weapons' counters unaffected. Accepted cost confirmed: the probed weapon's own sites evict for the session (anchor destroyed) -- the Phase 1 multi-anchor painter change is what lifts that. GATE CLEARED for the Reliquary earned-line card display (primary path; deed-slot fallback not needed).

**Evidence:** Patrick eyewitness + screenshot 2026-07-05 08:15 (Cutpurse card, weapon id 1, 3 sites written enc1+enc2, 0 skipped); stress set verified same session ("everything verified and works"); log `flavor-spike: 3 written / 0 skipped`

**Date:** 2026-07-05

</details>

### [anchorscan-tier1-jobcommand-pair] AnchorScan tier-1 (LW-82): JobCommand rec8+rec9 pair signature unique in its +/-4MB pin window on 1.5.1, file-baked in the exe image

Proven: the JobCommand rec8+rec9 pair signature is unique in its pin +/-4MB window on 1.5.1 and the pair content is file-baked in the exe (present before any save loads); the shipped scout found the anchor at its pin from the title screen in the 2026-07-14 owner live drill.

<details><summary>How we got here</summary>

**Claim (original wording):** AnchorScan tier-1 premise (LW-82): the JobCommand rec8+rec9 pair signature is UNIQUE in its pin +/-4MB window on 1.5.1, at the pin; the pair content is FILE-BAKED in the exe image (static data, present from module map time, before any save loads)

**Mechanism:** scan window `0x14027E213..0x140A7E213` for rec8 bytes `0x96..0x9D` with rec9 `0x64..0x6B` exactly 25 bytes later, base = hit - 8*25; the 1.5.1 exe backup carries the pair at file offset `0x67D6DB` (live VA offset differs by section alignment), so the table content is baked, not save-built; window half-width vs the 1.5 recompile's max observed static delta +0x6C3C is ~151x margin

**Evidence:** `tools/probes/anchorscan_feasibility_probe.py` live 2026-07-14 (exactly 1 pair hit at the pin, 0.01s per 8MB window; independently re-run by a plan-review agent, identical; `exe` mode on the 1.5.1 backup). OWNER LIVE DRILL 2026-07-14: the shipped scout (e77b9d7) found the anchor AT ITS PIN from the TITLE SCREEN, pre-save, during the drill stand-down; drill log banked (lw82-drill-livingweapon.log; smoke row 6.5)

**Date:** 2026-07-14

</details>

### [anchorscan-tier2-roster-structural-confirm] AnchorScan tier-2 (LW-82): roster-base region scan viable ONLY with the structural confirm; shape rules alone drown

Proven: a roster-base scan is viable only with the structural confirm (shape rules alone leave 766 candidates; stride structure plus %8 alignment narrows to exactly the pin 0x1411A7D10); the shipped scout found the base at its pin in the 2026-07-14 owner live drill. Solo-roster / early-prologue saves remain unmeasured.

<details><summary>How we got here</summary>

**Claim (original wording):** AnchorScan tier-2 premise (LW-82): a roster-base region scan is viable ONLY with the structural confirm; the shape rules alone drown

**Mechanism:** scan for `{01,00}` (nameId==1) with a +0x230 lookback in RosterBase +/-4MB: the LaunchGuard shape rules alone leave 766 candidates; adding stride structure (slots +1..+3 row-like at 0x258, at least one a populated non-Ramza row) leaves 2; adding base %8 alignment leaves EXACTLY the pin `0x1411A7D10` (both known bases 16-aligned: pre-1.5 `0x1411A18D0`, 1.5/1.5.1 `0x1411A7D10`; the stride-riding decoy at pin-0x342 reads %8==6). Raw `{01,00}` hits 11,869 in the window (worst 1MB chunk 3,250): the free alignment-first rejection order is load-bearing for scan cost. Solo-roster / early-prologue saves remain unmeasured (the relaxed any-of-slots rule exists for them)

**Evidence:** probe runs x2 2026-07-14 (identical results; relaxed rule recalibrated same day, still exactly the pin). OWNER LIVE DRILL 2026-07-14: the shipped scout read honest not-found pre-save, then found the base AT ITS PIN with the sibling prediction within one rescan cycle of the save loading; summary `2 at pin, 0 elsewhere, 0 ambiguous, 0 not found`; drill log banked (smoke row 6.5)

**Date:** 2026-07-14

</details>

### [table-xml-whole-row-writeback] Table-XML rows apply as WHOLE-ROW writebacks (LW-77)

Proven: the modloader applies any XML-listed table row as a whole-row writeback against its Init-time snapshot, so a single-field edit reverts every runtime write other mods made to that row; load order cannot fix it, only listing fewer rows can. Owner ladder test confirmed 2026-07-14.

<details><summary>How we got here</summary>

**Claim (original wording):** Table-XML rows apply as WHOLE-ROW writebacks (LW-77)

**Mechanism:** `FFTOJobDataManager.ApplyTablePatch` (Nenkai `fftivc.utility.modloader`) assigns every field via `model.X ?? previous.X` against the Init-time snapshot at `OnAllModsLoaded`, for any row a mod's XML lists, including `JobCommandId` since loader 1.7.1; a row from a CharacterEvasion-only edit therefore reverts every post-snapshot runtime write another mod made to that SAME row, and load order cannot fix it, only listing fewer rows can

**Evidence:** owner ladder 2026-07-14 (Blue And Red Mages 2.0.2 compose test): deleting our row-57 block from JobData.xml resurrected Red Mage's abilities on relaunch; the guard stayed armed throughout, ruling out a guard/fingerprint explanation

**Date:** 2026-07-14

</details>

### [attack-row-catalog-offset-redirect] Battle Attack ROW + hover TITLE rename via catalog offset redirect (LW-31 stage 3), arbitrary length, no hooks

Proven: repointing the JobCommand text catalog's nameOff u32 (one write per copy) renames the battle Attack row and its hover-card title together, arbitrary length, no hooks; the sibling descOff mirror is also proven. Flipped Proven by the owner 2026-07-14 after the shipped rename rendered on the first battle turn and followed a mid-battle weapon swap.

<details><summary>How we got here</summary>

**Claim (original wording):** **Battle Attack ROW + hover TITLE rename via catalog offset redirect (LW-31 stage 3), arbitrary length, no hooks**

**Mechanism:** The census's packed "Attack"+desc buffer is the string POOL of the JobCommand TEXT CATALOG. Above it: a 0x24-stride record table, nine u32s per record `{nameOff, descOff, poolHeadOff, id, poolHead8Off, 0, 0, 0, ordinal}`, every offset RECORD-BASE-relative. Attack is record id 1 at exactly `label - 0x1FC1` (nameOff==0x1FC1, descOff==0x1FC8: serialized-layout constants, identical on 3/3 copies across two launches; a whole-VA sweep found exactly ONE reference to the label: that nameOff field). Repointing nameOff (ONE u32 write per copy, verify-before-write, restore = write 0x1FC1 back) renames the row AND the hover-card title together. The menu caches at BUILD: the change renders on the next menu open (turn-open writes suffice; the stage-2 painter's cadence already covers it). Overflow behavior: the row shrinks-to-fit, the title marquee-scrolls: length-unbound. The desc body mirror is the sibling descOff u32, ISOLATED AND PROVEN in the same session (probe `descsplit` mode): footprint composed as "name NUL tail" with nameOff at the footprint start and descOff past the NUL rendered the name in the row/title and the tail in the hover desc, independently (owner eyewitness: "Loki Approves. Works"). Render side also materializes per-copy `{namePtr, descPtr}` 8-byte pairs in widget structs (secondary lever, untested). Per-launch locate: catalog copies found by desc anchors (vanilla 73-char desc, or the never-painted Squire desc at `label + 0x7B` when a composed line is in place).

**Evidence:** Owner eyewitness 2026-07-06 ~01:5x in a live battle: "Evasive Stance" (14 chars) rendered in the Attack row, then the 58-char composed dossier line rendered in row + title (screenshot on file, Monosnap 01.51.32); poke and restore OK on all 3 copies. Instruments: `probes/attack_table_scan.py` (read-only locate/decode) + `probes/attack_row_redirect.py` (the u32 poke). PROVEN flip 2026-07-14 (owner, smoke row 5.3): the shipped rename rendered the wielder's weapon name on the very first turn of the session's FIRST battle (cold census) and followed a mid-battle weapon swap.

**Date:** 2026-07-14

</details>

### [kobu-brave-climb-to-match] Brave climb-to-match: a katana +3 raises the wielder's Brave to a braver struck foe's (Kobu)

Proven: Kobu (Kiyomori) writes the wielder's current Brave one shot up to a braver struck foe's value (the earlier ceiling/hold was reworked away 2026-07-02, so external Brave Break sticks between strikes). Flipped Proven by the owner 2026-07-14 when a Kiyomori strike raised the wielder 57 to 71 on screen.

<details><summary>How we got here</summary>

**Claim (original wording):** Brave climb-to-match: a katana +3 raises the wielder's Brave to a braver struck foe's

**Mechanism:** `Kobu` (Kiyomori id 43): on a main-hand hit (acting gate + Maim HP-diff scan), if the struck foe's CURRENT brave (band `+0x0F` = combat `+0x2B`) exceeds the wielder's accumulated ceiling, hold the wielder's current brave at that value (climb-only, cap `Tuning.KobuBraveCap`=97, battle-scoped). The foe is matched by ORIG brave (band `+0x0E`) so it stays recognizable while current is raised; wielder via `Wielder.ResolveDeployedMainHand`. NEVER writes orig brave (the locate fingerprint/decoy). **REWORKED (2026-07-02):** the ceiling/hold is GONE -- a qualifying strike now writes the matched value ONE SHOT (premise: the one-shot current-brave row above) and between strikes brave is a normal stat (an external Brave Break sticks; the hold-era clamp was the reported bug). Detection consumption is now NON-LOSSY for detectably-transient reads (`RicochetState.Rearm` retry; a one-tick transient ate a gate-open strike at 10:33:36.601) and the enemy filter reads a per-battle additive fingerprint cache (`EnemyFingerprintCache`). One-shot raise fired live 11:02:03 same day (37 -> 47).

**Evidence:** LIVE 2026-06-27 (DEV): `kobu: struck enemy (orig brave 46, current brave 99) -- wielder brave ceiling raised 85 -> 97`, the 97 rendered on the wielder's card. Current-brave write per `brave-faith-current-vs-orig-offsets` (FFTMultiplayer StatHold). `tools/probes/brave_probe.py`. analyze.py exit 0 + 1346 xUnit. PROVEN flip 2026-07-14 (owner, smoke row 2.3, dev lane): Kiyomori strike on a braver foe raised the wielder 57 to 71 on screen; the file logged the one-shot raise and verdict detail (struckBrave=71 wielderBrave=57 raisedTo=71), with earlier inactive verdicts proving the braver-foe gate refuses correctly.

**Date:** 2026-07-14

</details>

### [cavaliers-charge-mounted-speed-grant] Mounted flat stat grant: a spear +3 grants Speed +3 while riding a chocobo (Cavalier's Charge)

Proven: Cavalier's Charge (Holy Lance) holds combat Speed at natural+3 while the wielder's mount bit (combat +0x1B4 bit 0x80) is set, reverting on dismount or battle exit. Re-verified Proven by the owner 2026-07-15 after the growth-locate and roster-walk reworks.

<details><summary>How we got here</summary>

**Claim (original wording):** Mounted flat stat grant: a spear +3 grants Speed +3 while riding a chocobo

**Mechanism:** `Cavalier's Charge` (Holy Lance id 104): `GrowthEngine.HoldTimedStat` holds combat `CSpeed` (`+0x40`) at natural+3 while the wielder's Mount Info byte (combat `+0x1B4`) has bit `0x80` set (riding); reuses the Afterimage Speed-lane hold with the `forTurns` gate swapped for the mount bit. A Polearm is PA-formula so growth never claims CSpeed -- no Route conflict. Reverts on dismount / battle-exit. `mounted:true` signature flag flows items.json -> meta.json -> WeaponMeta.

**Evidence:** LIVE 2026-06-27 (DEV): Speed 11 -> 14 on mounting a chocobo, reverts to 11 on dismount. Both halves were independently proven (Afterimage Speed-hold 2026-06-14; mount bit `+0x1B4 & 0x80` via `tools/probes/mount_probe.py` 2026-06-26). PROVEN flip 2026-07-15 (owner, smoke row 7.26, dev lane): re-verified AFTER the growth-locate and roster-walk reworks (eb76fe5, 474d494) that rebuilt the machinery it rides: Speed read natural+3 on the card while riding, reverted on dismount, no other unit's Speed moved. The hold is silent by design (no log lines); evidence is the owner card read plus clean battle edges 16:48-16:49.

**Date:** 2026-07-15

</details>

### [gunslinger-roster-offhand-support-writes] Gun Slinger roster off-hand + support slot writes

Proven: GunSlinger writes a twin pistol into the roster off-hand slot and Dual Wield into the support slot for qualifying Outrider Pistol wielders (the row's original "Blaster id 76" wording was corrected to id 71 at flip time), re-asserting in battle. Flipped Proven by the owner 2026-07-15 with the twin fill and double Attack fire observed live.

<details><summary>How we got here</summary>

**Claim (original wording):** Gun Slinger roster off-hand + support slot writes

**Mechanism:** `GunSlinger.cs` (Outrider Pistol id 71 +3; this row's original "Blaster id 76" wording predated the move to id 71 and was corrected at flip time): `PrepRoster()` scans roster slots 0..19 (RosterBase `0x1411A7D10`, stride `0x258`); for each slot where main-hand reads the pistol id (71, meta-flag resolved) and tier >= 3, writes a twin pistol into `ROffHand +0x18` (u16 via W16) and Dual Wield (221) into `RSupport +0x0A` (u8 via W8). Originals snapshot+restored when main-hand changes. Runs every ~1 s on Engine, world map AND in battle (in-battle pass is re-assert-only; the between-battles-only version did not hold into combat, fixed 2026-07-04). Write path: `IGameMemory.W16` -> `Mem.W16` (WPM little-endian 2-byte, same safety contract as W8).

**Evidence:** Twin-fires-twice first observed live 2026-07-04 (in-battle re-assert fix). PROVEN flip 2026-07-15 (owner, smoke row 7.19, dev lane): equip line 16:53:07 on equipping the pistol, off-hand filled with the twin plus Dual Wield on the unit, Attack fired twice in the 16:54 battle; clean edges, zero errors. Roster write path also unit/integration tested (`GunSlingerTests.cs`).

**Date:** 2026-07-15

</details>

### [sanctus-benediction-charged-heal-boost] Sanctus "Benediction" +30% heal boost fires on CHARGED spells via the last-actor latch

Proven: Benediction's +30% heal boost gates on the sticky last-actor latch (KillTracker.LastPlayerMainHand == 64) rather than a timing window, so it fires on charged spells. Flipped Proven by the owner 2026-07-15 with correct latch handoff, a correct near-max refusal, and a real boosted heal on screen.

<details><summary>How we got here</summary>

**Claim (original wording):** Sanctus "Benediction" +30% heal boost fires on CHARGED spells via the last-actor latch

**Mechanism:** gate = `KillTracker.LastPlayerMainHand == 64` (sticky across enemy turns AND the ~7s charged-Cure resolve gap; resets to 0 on battle enter/exit) — NOT a timing window (the old acted-edge + 2s grace closed ~3s before the heal landed). Boost = quiet `+0x14` band HP write of `floor(rise×30%)` on any ally HP rise; `Band.AllyFingerprints` ally filter, `LifeSap.NewHp` clamp + never-revive, `HealState.Consume` blocks re-boost of our own write.

**Evidence:** live (DEV) 2026-06-16: Cure 88 → `benediction: ally heal +88 boosted by 26 (30% of 88)` → HP 359→473/572; FFTHandsFree BattleTracker independently reported `114 healed → 473`; no second boost on our +26 write (Consume held). PROVEN flip 2026-07-15 (owner, smoke row 7.15, dev lane): the latch rose exactly when the Sanctus wielder was last to act and handed off when other weapons (ids 56/58/80) took the latch, a near-max heal was correctly refused (+22 unboosted at 399/399), and a real boost applied at 17:03:53 (band slot 24); owner verified the boosted heal on screen.

**Date:** 2026-07-15

</details>

### [teleport-swap-float-render-node-transform] Unit TELEPORT, two-unit SWAP, and visual FLOAT via the render-node world transform (OVERTURNS the Knockback + render-position walls)

Proven: a coherent triple-write across the combat logic tile, the render node's AI tile key, and the node's world transform teleports, swaps, or floats a unit completely, including onto unreachable terrain; render Z is cosmetic for targeting and being hit re-stamps it. Owner-flipped 2026-07-21 confirming the 2026-07-10 session (row date 2026-07-10).

<details><summary>How we got here</summary>

**Claim (original wording):** **Unit TELEPORT, two-unit SWAP, and visual FLOAT via the render-node world transform (OVERTURNS the Knockback + render-position walls)**

**Mechanism:** Position is THREE live-writable data layers: combat logic tile (`+0x4F` x / `+0x50` y, `+0x51` bit7 layer), the render node's AI tile key (`+0x88/89/8A`), and the node's WORLD transform (node `+0x4C` u16 X, `+0x4E` u16 signed Z, `+0x50` u16 Y; node via list head `0x140D3A410`, `+0x148` = combat backref). Formulas: X = 28·x + 14, Y = 28·y + 14, Z = -12·(tileHeight, +1 if the unit has FLOAT). A coherent triple-write moves a unit COMPLETELY (it hovers, paths, and acts from the new tile; the engine re-adopts every layer after its first real move, re-stamp also at turn-open). ARBITRARY TILE PLACEMENT ACCEPTED, INCLUDING UNREACHABLE TERRAIN (owner live 2026-07-22): a unit warped onto a TREETOP rendered perched correctly, kept its turn marker, showed a health bar, and took a valid selection diamond, with the game's own Height readout reporting 17 for the new spot. The engine RE-DERIVED the height rather than honouring the stale Z we left behind, which is the same re-adoption this row already claims for the other layers. Consequence worth a mechanic: terrain the game would never path a unit onto (treetops, roofs, cliffs) is freely placeable, and FFT height drives damage, accuracy and range. BOTH OPEN QUESTIONS ANSWERED THE SAME SESSION: the unit is STRANDED (the game refuses movement with "At present, can't move to any other tiles", so the pathfinder will not route out of a tile it would never have routed into) but otherwise FULLY FUNCTIONAL: it acts normally and a thrown stone connected with an enemy, so ranged offence works from up there. The enemy AI degrades GRACEFULLY rather than breaking: it backed off and milled around without engaging, and nothing crashed or froze. Net shape: a self-costing mechanic (trade all mobility for an unassailable ranged platform) and, unbounded, a degenerate one. DOWNWARD Z IS FLOOR-COLLIDED (owner live 2026-07-22): world Z is -12 * height so POSITIVE is downward, and a downward write does not sink a unit under the terrain, it clamps at the floor. Upward (negative) is free, which is what makes the resurrect arc's sky descent work. Writing node `+0x4E` alone grants/strips the FLOAT hover — BEING HIT ALSO RE-STAMPS Z (owner live 2026-07-22): a unit warped sideways kept the previous tile's height and floated visibly above its new tile, an enemy attacked it with no difficulty, and the hit snapped it down to correct ground level. Two consequences: render Z is COSMETIC for targeting, since the AI reads the LOGIC tile (so a floating unit over a normal tile is attacked normally, and the treetop perch works because of WHERE the unit stands, not how high it looks); and combat contact joins move-end and turn-open as a re-stamp trigger. The hover is transform DATA, not an animation; the renderer never re-derives Z per frame (the mover owns the transform during a walk, else it is unowned). Guard: co-tiled units = slot-order target shadowing + a movement-confirm soft-lock, so a shipped teleport must pick a FREE tile.

**Evidence:** owner live 2026-07-10: a ghost-Knight slid tile-by-tile on `+0x4C` pokes; a full Ramza↔enemy SWAP (all layers, own facing kept) executed flawlessly, both acting normally after; Float granted to a non-Float unit and stripped by Z pokes. Discovery instruments `node_move_watch.py` (idle-noise-masked byte watch during a one-tile walk) + `node_world_fit.py` (cross-unit linear fit) + `swap_units.py`. OWNER FLIP 2026-07-21 (LW-107): the owner confirmed the 2026-07-10 session and promoted this row.

**Date:** 2026-07-10

</details>

### [spawn-ai-unit-donor-duplication] SPAWN a real AI-fighting unit mid-battle by DUPLICATING a live donor (OVERTURNS "spawn a rendered unit is walled")

Proven: a data-only enroll chain (combat-struct copy, AI-registry clone, render-node builder call, plus the one-byte AI-roster index key) spawns a battle-scoped clone that draws, is named, is controllable, and fights as a real AI unit. Owner-flipped 2026-07-21 confirming the 2026-07-10 session (row date 2026-07-10).

<details><summary>How we got here</summary>

**Claim (original wording):** **SPAWN a real AI-fighting unit mid-battle by DUPLICATING a live donor (OVERTURNS "spawn a rendered unit is walled")**

**Mechanism:** Data-only enroll chain, all live-proven: (1) copy the donor's 0x200 combat struct into a vacant same-region slot at a FREE tile (`+0x1BC`=host slot; gate/membership 0xFF; CT 0); (2) clone the donor's battle-keyed AI registry object (pool `0x1437415A0` stride 0x280, key word `+0x2C`), re-key `+0x2C`=host slot, append its ptr at table `0x141800F50`[count], bump count word `0x1437414A8` + byte `0x140D407BB`; (3) cold-call the render-node BUILDER `0x14026EBEC` with the DESTINATION TILE as a1/a2/a3 (they land in node tile key `+0x88/89/8A`, the AI's subject-lookup; cribbing the donor's tile here was the freeze bug) + arg8=host, then the inlined scene-bind + own anim objects (`0x14E8CBF78`/`0x14E8F2CA0`) + donor identity stamps (`+0x191/2` = name + control); (4) THE ONE-BYTE COEXISTENCE KEY: the per-slot AI-ROSTER INDEX `0x141873038`[hostSlot] = next free index. Real units hold 0..7; an un-indexed clone reads 0xFF, and the AI-turn orchestrator `0x151086A53` early-gate (`cmp [0x141873038+slot],0x10; jae skip 0x151086DA0`) refuses to snapshot ANY subject while ANY occupied slot is un-enrolled — the subject ptr `0x141872EA0` stays null and the facing code `0x150E74A5D` (`movzx ecx,[rax+0x51]`) null-derefs = the auto-battle crash. Result: draws + named + controllable + DESCENDS FROM THE HEAVENS (node Z stepped -600→ground) + FIGHTS as a real AI unit. Battle-scoped (does NOT persist = the temporary-summon semantic; a permanent recruit needs a save-roster entry, unbuilt). Two polish items: AI-PASSIVE (its behavior row `0x1411A7D10`+idx·0x258 is the donor's shadow — needs a real AI-data row for aggression) and the decoy CT-hold must be released for turns.

**Evidence:** owner live 2026-07-10: "KENRICK HAS GRACED US WITH A CLONE FROM THE HEAVENS"; the clone took a real AI turn with the roster-index byte set 0xFF→0x08 (membership `+0x02` and node id proven red herrings), game alive, no crash, fought a full battle. `BodyDoubleSpike` Canary 8/9 (dev-only F5); three decode workflows byte-cited the chain (the crashes = null AI-subject `0x141872EA0`; the freeze = tile-keyed subject resolve). OWNER FLIP 2026-07-21 (LW-107): the owner confirmed the 2026-07-10 session and promoted this row.

**Date:** 2026-07-10

</details>

### [resurrect-despawned-unit] RESURRECT a despawned unit mid-battle (data-only)

A despawned unit can be resurrected mid-battle data-only by re-enrolling the AI registry, reviving the render node, and restoring presence and model gates. Proven, owner-flipped 2026-07-21 confirming the 2026-07-10 live session.

<details><summary>How we got here</summary>

**Claim (original wording):** **RESURRECT a despawned unit mid-battle (data-only)**

**Mechanism:** Combat struct + node bytes survive a despawn (static memory); re-add by (1) re-enrolling the AI registry (clone a living same-team object, re-key `+0x2C` to the slot, append + bump counts) BEFORE visibility, (2) reviving the node (pool element in-use dword = 1, clear the node `+0x12C` done-mark, re-splice at list head `0x140D3A410`), (3) present `+0x1B5`=1 then gate `+0x01`=model LAST, plus the AI-roster index `0x141873038`[slot].

**Evidence:** owner live 2026-07-10: a despawned enemy Knight was brought back and could be walked-to and punched (real damage, battle flowing). `unit-despawn-resurrect` recipe memo. OWNER FLIP 2026-07-21 (LW-107): the owner confirmed the 2026-07-10 session and promoted this row.

**Date:** 2026-07-10

</details>

### [despawn-unit-mode2] DESPAWN any unit mid-battle, sprite and all (the engine's own declarative removal)

Writing mode 2 into the render node's flag word (node +0x12C) lets the engine's own sweeper remove any unit completely, the same primitive vanilla crystallization uses; it is one-way and must never target the current actor or an open turn. Proven, owner-flipped 2026-07-21 confirming the 2026-07-10 live session.

<details><summary>How we got here</summary>

**Claim (original wording):** **DESPAWN any unit mid-battle, sprite and all (the engine's own declarative removal)**

**Mechanism:** Write mode 2 into the render node's flag word: node `+0x12C = (old & ~0x30) | 0x20`; the engine's per-frame node sweeper `0x14026E20C` completes the whole removal on its next UNPAUSED frame (combat `+0x01`=0xFF, present `+0x1B5`=0x80, node done-mark `+0x30`, pool element in-use cleared, node UNLINKED from `0x140D3A410`, leaf teardown `0x14026E16C`) — the same primitive vanilla crystallization uses. The ledger's old "cold-call `0x14023BFB0`" was WRONG (mid-instruction inside the event interpreter `0x14023A8E0`). Guards: never the current actor (node id == dword `0x140CF873C`) or an open turn (`+0x1B8`); the per-id byte `0x140C6CFE0+id*9` is the hover/menu-engagement marker, NOT a busy gate. ONE-WAY.

**Evidence:** owner live 2026-07-10: a hovered enemy Knight vanished (unit + sprite + list entry) on unpause; every predicted side-effect byte read exactly. `BodyDoubleSpike` Ctrl+F5. OWNER FLIP 2026-07-21 (LW-107): the owner confirmed the 2026-07-10 session and promoted this row.

**Date:** 2026-07-10

</details>

### [hide-reveal-ghost-statue] HIDE / REVEAL a unit's logic live (ghost-statue toggle); locked-on actions WHIFF against hidden targets

Writing 0xFF to combat +0x01 hides a unit's logic (no targeting, no turns, sprite gone) and restoring the model id reveals it; a locked-on action whiffs against a hidden target, and hiding every enemy at once wins the battle. Proven, owner-flipped 2026-07-21, with the victory-check consequence owner-verified live 2026-07-22.

<details><summary>How we got here</summary>

**Claim (original wording):** **HIDE / REVEAL a unit's logic live (the ghost-statue toggle); a locked-on action WHIFFS against a hidden target**

**Mechanism:** the model/logic gate is combat `+0x01`: write 0xFF to hide (the unit stops being a valid target or actor, sprite gone), restore the model id to reveal. Hidden units get NO scheduler turns, so the restore trigger must be external. Guards: restoring onto an OCCUPIED tile co-tiles into the movement soft-lock, and a mid-hide autosave persists the hidden state into a resume (both observed 2026-07-10), so a battle-enter un-strand sweep is owed by anything that ships this. VICTORY CHECK READS THE SAME LIST (owner live 2026-07-22): hiding EVERY enemy at once ended the battle in a WIN. So the engine's living-enemy count walks the same logic list the gate byte removes a unit from, which closes the question the despawn arc left open (victory-check sanity after a removal) for the hide lane at least. TWO CONSEQUENCES. Hazard: anything that hides or despawns enemies must never take the last one, or it silently ends the battle; a shipped Vanish signature needs that guard. Capability: hiding the whole enemy team is an instant win, which is worth knowing exists and worth never shipping.

**Evidence:** owner live 2026-07-10, the decisive test: a mid-cast Slow whiffed ENTIRELY when its target was gate-hidden during the cast animation, so hide-at-resolution defeats an already-locked-on action. Toggled repeatedly that session with no crash. Still open: the whiffed resolution DISPLACED the hidden unit one tile, unexplained. OWNER FLIP 2026-07-21 (LW-107): promoted alongside the teleport/spawn/despawn/resurrect family it was found with; it previously had NO row of its own and rode inside the despawn row.

**Date:** 2026-07-10

</details>

### [anim-request-register] Play ANY animation on any unit via the request register (node +0x10 u16)

Node +0x10 u16 is the game's own animation-request API: write logicalId+1 to play any animation page, which latches with no hold; page ids are per sprite class and the time mage male map lives in tools/probes/anim_catalog.jsonl. Proven, owner-flipped 2026-07-21 after the input poke passed its pre-registered bar twice.

<details><summary>How we got here</summary>

**Claim (original wording):** **Play ANY animation on any unit via the request register**

**Mechanism:** node `+0x10` u16 is the game's own animation-request API (RequestAnim stub `0x140268E7C` stores logicalId+1 + facing `+0x7C` + force bit `+0x8F|0x20`); the per-node tick `0x14026CB1C` consumes it, the SEQ bytecode walker `0x14026B388` plays (frameId,duration) byte pairs, and it LATCHES with no hold (duration 0 = frozen frame). Force crouch on a healthy unit = write u16 0x0035; force stand on a critical unit = 0x0004 + node `+0x8F |= 0x20`. The state block node `+0x420` (incl. the `+0x423` phase byte) is a per-frame OUTPUT — writes re-stamp (the failed pokes were all outputs). Page ids are PER SPRITE CLASS: the decode's guessed labels were falsified by the owner's full sweep (its "crouch 0x34" plays the DEATH animation on a time mage). The real map is `tools/probes/anim_catalog.jsonl` (time mage male, all 128 pages owner-labeled; 0x3c teleport, 0x32/0x3b invisible, 0x75/0x76 dragoon jump/landing, 0x34 die, 0x35 rise, 0x1c level-up jump, 0x37/0x38 flinch with displacement; book ends ~0x79). Other classes unswept (LW-114). Selection input = combat statuses `+0x61..+0x65` (Critical = `+0x63` bit 0x01).

**Evidence:** OWNER LIVE 2026-07-21 (LW-113), the owed INPUT poke fired and passed its pre-registered bar twice: poke 1 was consumed before the +0.10s sample and left the unit frozen in the requested pose (output block parked at phase 1); poke 2 caught the latch mid-act, `req=53` held through +0.25s and eaten by +0.50s. The unit visibly played the page both times, and a later real move event re-stamped it (the engine's own overwrite, as decoded, which is also the self-heal). Encoding confirmed: write u16 logicalId+1 to node `+0x10`. Owner then swept all 128 pages into `tools/probes/anim_catalog.jsonl`. Earlier `+0x423` OUTPUT pokes re-stamping in 0.5s remain the control that proved output-not-input. `anim-request-register` memo.

**Date:** 2026-07-10 mechanism, owner flip 2026-07-21

</details>

### [invisible-funnel-guardian] Holding composed Invisible on every player unit except one FUNNELS enemy AI targeting onto that one (Guardian provoke premise)

Holding the composed Invisible bit (band +0x47 bit 0x10) on every player-team unit except one funnels all enemy AI targeting onto the visible unit, even over adjacent hidden alternatives. Proven through the shipping mod, owner-flipped 2026-07-22.

<details><summary>How we got here</summary>

**Claim (original wording):** **Holding the composed Invisible bit on every player-team unit except one FUNNELS enemy AI targeting onto that one (the Guardian provoke premise)**

**Mechanism:** hold band `+0x47` bit `0x10` (`Offsets.AInvisible`/`AInvisibleBit`, the byte FeignDeath already writes) on every player-team unit EXCEPT the chosen one, re-stamped every 30ms, established BEFORE the enemy phase opens and never released (the pre-armed configuration the Guardian design ships). Party enumeration reads the team bit band `+0x1D2` bit `0x10` (allies 0), READ ONLY, and excludes out-of-play seats by combat `+0x01` == `0xFF`. Composed layer only: a raw composed write is an orphan flag with no effect and no expiry (see the orphan-flag row), which is why the AI reads it while the unit stays solid and untransparent

**Evidence:** Mandalia Plains, flat and open, mod OFF, `tools/probes/taunt_probe.py taunt 24`. NON-VACUITY CONTROL RAN FIRST: two enemy turns with no hold, both went for the exposed Time Mage. Then the hold hid slots 25 to 28, leaving ONLY the Time Mage (slot 24) visible at (4,0) in the far corner, while Beowulf stood ADJACENT to the enemy cluster, hidden. THREE of three enemy turns, every enemy disengaged from the adjacent hidden Beowulf and converged on the distant visible Time Mage, so proximity is not merely controlled for but INVERTED: the only visible unit was the least convenient target on the board. Zero WRITE-FAILED and zero migrated lines, so the hold verifiably held. Passing an adjacent alternative three times reads like hard EXCLUSION rather than a scoring preference, though Transparent is absent from the repo's transcription of the PSX `Check_if_Unit_can_be_Targeted` gate list. LIMITS, stated so this is not overread: pre-armed and held continuously, so it says nothing about arming timing; whole-battlefield, which is the design's degraded fallback and not its selective per-enemy goal (that needs the reveal side); one battle, one enemy composition, no casters observed OWNER PROVEN 2026-07-22 THROUGH THE SHIPPING MOD (WINDOW mode): F6-marked an enemy, the runtime held Invisible on every deployed player unit except the Defender bearer (Ramza), and every enemy ignored the invisible units and made straight for Ramza. (Slice per-turn timing is a separate problem, banked in a memory; the FUNNEL itself is confirmed.)

**Date:** 2026-07-22

</details>

### [roster-support-u16-key] Roster picked-ability block is THREE u16 ability Keys; picked support is a u16 KEY (Dual Wield = 477), the old u8-write success rode a resident high byte (LW-168)

The roster picked-ability block holds three u16 ability Keys (reaction +0x08, support +0x0A, movement +0x0C); the support field is a u16 Key (empty 0, Dual Wield 477 = id 221 + 256), and the earlier u8-write "success" only worked via a resident high byte. Proven, owner-flipped 2026-08-12 via the LW-168 live pass.

<details><summary>How we got here</summary>

**Claim (original wording):** The roster picked-ability block is THREE u16 ability Keys (reaction `+0x08`, support `+0x0A`, movement `+0x0C`): the picked SUPPORT is a u16 KEY, empty reads u16 0, and Dual Wield is Key 477 (live support id 221 + 256); the Gun Slinger Proven row's "u8 write of 221" success (2026-07-15) worked via a RESIDENT high byte, not because the field is a byte (LW-168)

**Mechanism:** Key = live support id + 256 (the Choir mapping, Key 483 -> id 227). A low-byte-only write onto a BARE unit (high byte 0) composes Key 221 = the placeholder ability "Toadja" (description "A221"), exactly what the LW-168 v1 fix produced on screen. On a unit with a support already picked the resident 0x01 high byte let the u8 write compose 0x01DD = 477 = Dual Wield, which is why the July pass looked correct. Low-byte-only READERS stay valid (Choir's 227 compare, GrowthEngine's pickedSupport): every real support Key carries high byte 0x01 with a distinct low byte. Any WRITER must write the full u16 Key. GunSlinger writes W16 477 and snapshots/restores the u16 verbatim since LW-168; legacy low-byte snapshot files migrate at load (255 to 0; 198..254 plus 256)

**Evidence:** discovery owner live 2026-08-12: equip screen rendered Toadja/A221 after the u8 221 write on a bare Chemist Ramza (screenshot); read-only dump across 7 roster units (picked supports read u16 480/463 with high byte 1; bare units read u16 0; siblings read 441 at `+0x08` and 487 at `+0x0C`, matching the unit's on-screen Auto-Potion reaction and Movement +2); a u16 477 poke rendered "Dual Wield" on menu re-open, owner eyewitness. OWNER FLIP 2026-08-12 (LW-168 live pass through the shipping mod): a bare unit with NO support set received the twin plus Dual Wield on screen ("correctly applied Dual Wield from nothing set"), gunslinger.json snapshotted the true empty (hasSupp true, origSupp 0), the equip screen's normalize-to-0 was re-asserted cleanly, and removing the pistol restored the slot to truly empty with no phantom ability

**Date:** 2026-08-12

</details>

### [poachers-den-carcass-store] Poacher's Den carcass store is a save-block u8 array at 0x1411A7A1B + (PoachItem Key - 1); roll at poach time; undead poachable; mapping from the Job sheet

The Poacher's Den carcass store is a save-block u8 array (count per carcass at 0x1411A7A1B + Key - 1, 96 Keys) read live by the Den UI, with the common/rare roll at poach time; the monster-to-carcass mapping comes from the game's Job sheet (the earlier job-byte-minus-95 arithmetic was corrected same day). Proven, owner-flipped 2026-08-12 via the LW-167 live pass.

<details><summary>How we got here</summary>

**Claim (original wording):** The Poacher's Den carcass store is a save-block u8 array: count per carcass at `0x1411A7A1B + (PoachItem Key - 1)`, 96 Keys, and the Den UI reads it live; the common/rare roll happens AT POACH TIME (distinct carcass Keys stored); undead monsters ARE poachable in IC; the monster job to carcass-Key mapping comes STRAIGHT from the game's Job sheet (each monster Job row carries its common and rare PoachItem keys in two unlabeled columns; human jobs carry 0/0 there; monster jobs START at 94 Chocobo, with 95 Black Chocobo). CORRECTION 2026-08-12 same day: this row first claimed species = job byte - 95; that arithmetic was a fit to ONE observed job byte plus an assumed floor of 96, and the owner's live pass FALSIFIED it (a Black Chocobo read job 95 and was refused). The Job-sheet join replaced it, hand-verified on six species. The old "job 94 = Dark Knight live" reading elsewhere in this ledger is now suspect: job 94 is the Chocobo (LW-167 premise probes)

**Mechanism:** PoachItem sheet decoded offline (nxd/poachitem.en.nxd via FF16Tools + Nenkai's nex layouts): 96 rows = 48 species x common/rare with carcass names, Cost/SellPrice, IsRare, species column, and produced item id; the store array was found by fingerprint scan (13 non-zero counts from the owner's Den reading matched as a 40-byte pattern at exactly one static address plus two Den-UI heap mirrors); mid-battle poaches do NOT write the array (a masked save-block diff across a poach showed zero store changes), the commit lands at battle END (win transition diff caught chocobo common 1 to 2), the same battle-end reconcile class as the broken/stolen gear row

**Evidence:** owner-driven probe session 2026-08-12: two vanilla poaches landed at Keys 11 (gobbledygook) and 1 (chocobo) in the store (the victims' job BYTES were never read live that session, an inference this row originally overstated as observation), the owner's Den reading cross-checked ALL 13 non-zero counts against the array read including both chocobo variants and Minotaur Key 51; WRITE side owner-eyewitnessed: a +1 poke on the goblin count rendered "Goblin Carcass 2" in the reopened Den and was restored after; held Skeleton and Skeletal Fiend carcasses prove IC poaches undead. tools/probes/poach_diff.py + scratchpad carcass scan. SECOND battle, same session: a MID-BATTLE poke on the goblin count SURVIVED the battle-end commit while the battle's own poach landed beside it (exploder 2 to 3), so the engine INCREMENTS the array rather than rebuilding it, and a mod write at credit time or exit edge is commit-safe. And the owner's control kill FALSIFIED the attack-only reading of the Poach description: Rend Weapon (a Knight ability) poached an exploder end to end, so weapon-strike ability kills poach vanilla-style even though the ability text says "standard Attack command"; consequence for LW-167: an ability-kill discriminator is MANDATORY for correctness (firing on ability kills would double-poach alongside vanilla), and the LW-166 "No Poaching." notice is attack-only in truth (a dormant-formula wielder can already poach through Rend-class abilities). OWNER FLIP 2026-08-12 (LW-167 live pass, evening session, owner-directed): five living poaches end to end through the shipping mod (goblin, red panther, skeleton x2, black goblin), every claim toast plus a one-shot corpse despawn with no crystal on screen, the mod's credit-time store writes all landed, and the Poacher's Den UI cross-checked EXACT against the store bytes; the last-enemy poach ended its battle normally with the count intact (tape flight_20260812_162404); a mid-pass battle LOSS rolled the store back with the reload, vanilla-consistent

**Date:** 2026-08-12

</details>

### [basic-attack-discriminator] Basic-Attack discriminator (LW-167 stage 4): killer's action record carries a kind=5 pending stamp, abil=0 for basic Attack

The killer's per-unit action record carries a kind=5 performing stamp naming the current pending action, with abil=0 for the basic Attack command; it survives to the credit moment and the engine restores it after reaction overwrites. Proven, owner-flipped 2026-08-12 via the LW-167 arming live pass with zero false fires.

<details><summary>How we got here</summary>

**Claim (original wording):** The basic-Attack discriminator (LW-167 stage 4): the KILLER's per-unit action record (frame +0x1A0, band +0x184) carries a kind=5 performing stamp naming the CURRENT PENDING ACTION, with abil = 0 for the basic Attack command and abil = the ability id otherwise; the stamp is in place before the damage tick, survives the fatal blow TO THE CREDIT MOMENT, and the engine RESTORES it after a reaction transiently overwrites it

**Mechanism:** Read at credit: killer band entry +0x184, byte +0xA == 5 AND u16 +0x2 == 0 means basic Attack (poach eligible); any other reading fails closed to "not an Attack", which can only ever under-poach. The VICTIM side of the record is noise at credit (its own stale last-action stamp, both kills observed), burying the victim-side lane; the earlier frame+0x1A0 row's killer-attribution xref refutation stands, this is a different (killer-side, pending-action) read

**Evidence:** owner-driven 4-beat battle 2026-08-12, tools/probes/arec_watch.py at 20Hz: beat 1 non-fatal Attack stamped the attacker kind=5 abil=0 BEFORE the victim's HP fell; beat 2 fatal Attack kill held abil=0 at the death edge and at the kill-diagnostic credit line 0.7s later; beat 4 fatal Rend Weapon kill held abil=141 at the blow and at credit; the owner's Auto-Potion (441) overwrote the stamp three separate times and the engine restored the pending action each time within about a second; battle ended by a normal win (no kill-all pollution). Flip to Proven rides the LW-167 arming live pass. OWNER FLIP 2026-08-12 (the LW-167 arming live pass completed, owner-directed): the pre-registered ability-control beat refused with kind=5 abil=102 exactly as predicted, and all five basic-Attack poaches of the evening session fired the discriminator true with zero false fires

**Date:** 2026-08-12

</details>

### [next-unit-computable] NEXT unit to act is COMPUTABLE from CT and Speed; only the head of the forecast is trustworthy (LW-118)

The next unit to act can be computed from each seat's CT (band +0x25) and Speed (band +0x24), reproducing the game's Combat Timeline head; only the head is trustworthy and the deep forecast stays unproven. Proven, owner-flipped 2026-08-12 after the Provoke regression pass; most recent date in the row is 2026-08-12.

<details><summary>How we got here</summary>

**Claim (original wording):** The NEXT unit to act is COMPUTABLE from CT and Speed: the game's own Combat Timeline is reproduced by a plain clock model, though only its head is trustworthy (LW-118)

**Mechanism:** Every band seat's scheduler CT (band `+0x25`, `Offsets.ACtSlam`) climbs by its Speed (band `+0x24`, `Offsets.ASpeed`) each engine tick; the unit whose CT is highest at or over 100 acts, then pays 100 back. Projecting that forward reproduces the on-screen upcoming-turn list. This is a READ of fields the runtime already trusts, no new write surface and no new address. Consequence: a feature can know who acts next BEFORE the turn opens, which is the thing reactive turn detection (actor pointer, menu-open turn flag) structurally cannot supply, since both only report the present. SCOPE, and it is narrow on purpose: the NEXT unit to act is the proven claim. A DEEP forecast is NOT proven and must not be built on without its own bar, because the two runs disagreed about how far it holds (see the evidence)

**Evidence:** owner live 2026-07-27, three independent checks, the third after a full game restart. (1) ORDER: a frozen prediction taken while the owner sat on an open menu was compared position by position against the game's own 9 deep Combat Timeline panel and matched, including the two identity checks the owner verified on screen (position 4 Ramza, position 7 the same Time Mage repeating). (2) SEQUENCE: `tools/probes/provoke_lookahead_probe.py validate 240` scored 10 of 10 NEXT UP hits across a played battle (seats 9, 16, 24, 8, 11, 25, 16, 9, 24, 11), detecting turn starts by the CT payment rather than the actor pointer; that run's frozen prediction held EXACT to six turns deep, then transposed the two closest Speeds on the field (seat 9 spd 13 against seat 16 spd 12) at positions 7 and 8 and self corrected at 9. (3) RESTART: the owner restarted the game to test whether the read survives a fresh launch; seats, CT and Speed all read sane again and a second `validate 150` scored 5 of 5 NEXT UP, but its depth window was right only 2 turns out and wrong at 3 and 4. So next up is 15 of 15 across two sessions and a restart, while depth ranged from 2 to 6 turns between runs, which is why the scope note above exists  OWNER FLIP 2026-08-12 (owner-directed after the Provoke regression pass): every hide edge of the pass carried live ETA readings that drove hide, far-off reveal and re-hide correctly; scope unchanged, the head of the forecast only, the deep forecast stays unproven

**Date:** 2026-07-27

</details>

### [ct-paid-at-turn-start] Scheduler CT is paid at turn START, not at turn end

A unit's CT is decremented by the 100 threshold as its turn opens, so the currently acting unit already reads its post-payment value, and the drop itself is a crisp turn-start edge. Proven, owner-flipped 2026-08-12 after the Provoke regression pass rode the payment edge with no false positives.

<details><summary>How we got here</summary>

**Claim (original wording):** Scheduler CT is paid at turn START, not at turn end

**Mechanism:** A unit's CT is decremented by the 100 threshold as its turn opens, so the unit currently acting already reads its POST payment value: the owner sat on a Time Mage's open menu with the Combat Timeline showing it at CT 100 and current, while the memory read for that same unit was CT 12 (its 112 minus the threshold). Any consumer that treats a low CT as "not due" while that unit is mid turn is reading it backwards, and any turn-start detector can use the drop itself as the crisp edge

**Evidence:** owner live 2026-07-27: paused menu screenshot (Combat Timeline position 1, CT 100) against `provoke_lookahead_probe.py snapshot` reading CT 12 for the same unit at the same moment; the validate run then detected all 10 turn starts off that drop with no false positives  OWNER FLIP 2026-08-12 (owner-directed after the Provoke regression pass, whose turn detection rode the payment edge with no false positives observed)

**Date:** 2026-07-27

</details>

### [cut-ability-provoke-recipe] A CUT ability can be given a new name, a new effect and a real player command slot: "Provoke" now exists in the game

A cut ability (id 189) can be renamed, granted as a real learned player command, and given a new effect by editing the live action table and a hand-authored inflict row; status Proven, with the owner flip on 2026-08-12 after the LW-123 regression pass saw the granted command cast in a real battle with the mark landing at 100 percent.

<details><summary>How we got here</summary>

**Claim (original wording):** **A CUT ability can be given a new name, a new effect and a real player command slot: "Provoke" now exists in the game**

**Mechanism:** Four independent levers composed, all live-verified 2026-07-22 in one session. (1) NAME: `tools/patch_ability_names.py` renames an arbitrary Ability-en Key; ability 189 (vanilla `Embrace`, reachable by nothing) rendered as "Provoke 189" with our description and IconId 32. (2) COMMAND SLOT: the shipped Barrage/ShadowBlade JobCommand injection, driven from `tools/probes/ability_grant_probe.py`, put it in a Squire's list (record 5, slot 5) with the learned bit held. (3) BEHAVIOUR TABLE: the ability ACTION table is hardcoded in the exe, 368 rows of 20 bytes, and there are TWO COPIES back to back one table-length apart: `0x14078961C` is a DECOY (edits read back correctly and change nothing) and `0x14078B2DC` is the copy the engine and the UI both consult. Row layout matches the modloader's `OverrideAbilityActionData` nex schema exactly: `+0..+3` u32 `unused` (reads `FF FF FF FF`, and the layout file itself calls it not used in game code, so it is a reliable row marker but not a designed sentinel), `+4` Range (0 means the engine supplies it), `+5` EffectArea, `+6` Vertical, `+7..+10` Flags1..Flags4, `+11` Element BITFIELD (0x80 Fire, 0x40 Lightning, 0x20 Ice, 0x10 Wind, 0x08 Earth, 0x04 Water, 0x02 Holy, 0x01 Dark; 74 rows nonzero, every one single-bit except id 158 Hallowed Bolt = Holy|Lightning), `+12` Formula, `+13` X, `+14` Y, `+15` InflictStatus, `+16` CT, `+17` MPCost, `+18..+19` padding (zero on all 736 physical entries). (4) EFFECT: `+15` is only an INDEX into the inflict-status table at `0x14080FBA0`, 128 rows of 6 bytes laid out `[mode][s0..s4]` with THE MODE BYTE FIRST, s0..s4 mirroring band composed `+0x45..+0x49` MSB-first; bit index equals the StatusEffectData Id. Mode values: `0x00` None, `0x10` Cancel, `0x20` Separate, `0x40` Random, `0x80` AllOrNothing. Repointing that ONE byte in the live action table changes what the ability inflicts, live, with no restart. Ability 189 final shape: range 5, single target, EffectArea 0, MP 0, CT 0, formula 56, InflictStatus repointed to index 29, a row we hand-wrote ourselves, applying StatusEffectData id 0 alone (mode `0x80` AllOrNothing first, then s0 = `0x80`); the earlier Berserk-alone shape at index 53 was abandoned when Berserk turned out to be resisted by 25 jobs including the whole boss tier

**Evidence:** Owner live, 2026-07-22, one session, each step eyewitnessed. Menu render confirmed by screenshot (Provoke 189 listed after Salve, marked Learned, description and icon correct). Decoy-vs-live copy PROVEN BY DIFFERENTIAL: `+15` held at 0 in the first copy read back as 0 and the ability still inflicted Immobilize at 100%; the same edit in the second copy immediately removed Immobilize from the ability card AND from the cast. Index semantics proven three ways: index 0 casts at 0% (the success roll is computed FROM the status, so no status means no chance), index 65 (DontMove, pre-existing) restored 100%, index 46 (Float, pre-existing) applied Float at 100% and the unit visibly floated, index 53 applied Berserk at 100%. FALSE NEGATIVE, RECORDED BECAUSE IT COST A CYCLE: a row hand-written into empty index 29 read back correctly and cast at 0%, which was first read as "the engine only resolves combinations it shipped". It is not. The inflict base in use at the time was ONE BYTE LATE (`0x14080FBA1`), which by luck still landed s0..s4 on the real status bytes (so every status decode was right) while reporting each row's mode from the NEXT row. The hand-written row therefore got correct status bytes and kept its own mode of `0x00` = None, and a None-mode row applies nothing, which is exactly a 0% chance. `findrow` separately proved there is exactly ONE inflict table (row 55's distinctive pattern occurred once in 256MB), so a decoy was never the explanation. CORRECTED AND PROVEN THE SAME DAY: a hand-written row at unused index 29 with the mode byte set to `0x80` (AllOrNothing) FIRST and the status bytes after it applied its status at 100%. So the inflict table is fully authorable and we are not limited to the 128 combinations the game shipped. Instruments `tools/probes/ability_table_probe.py` (read-only: verify / row / findrow / inflict / orphans) and `tools/probes/ability_grant_probe.py` (grant / action / poke, all snapshot-and-restore). LIMITS, TWO OF THEM RETIRED 2026-07-22 (owner-run): record 7 (Knight, the record the Defender actually needs) IS now exercised, and it holds THREE grants at once. A dump of record 7 with the probe holding Provoke showed ability 189 in slot 9 and Shadow Blade in slot 10 with six slots still free, so the slot-scoped release fix (commit 661cb63) survives contact with the real record and not only its unit tests. Separately, the in-process runtime CAN perform these table writes through the ordinary guarded path: all four addresses (both table bases, ability 189's `+15` byte and authored row 29) report page protections that `Mem.Writable` already accepts, and a bare `WriteProcessMemory` lands on every one of them, measured by `tools/probes/page_protect_probe.py`. No `VirtualProtect` capability is needed, and the claim in the grant probe's own docstring that these tables are read-only was wrong. ONE-SHOT PERSISTENCE NOW MEASURED, owner-run 2026-07-22, and it RETIRES this row's last write-side limit: holding both bytes and reading the re-assert counter on exit reported `1 write(s), stuck after one write` for EACH of them, so the engine rewrites neither the authored inflict row nor the action byte. The same run captured the vanilla values, which a restore path needs: ability 189's `+15` reads `0x2D` (45, the Immobilize row, which `inflictrefs` independently shows is SHARED with Leg Shot and Goo, so it must never be edited in place), and authored row 29 reads `00 00 00 00 00 00`, blank rather than merely unreferenced. LIMIT ON THE LIMIT: this measures a running session, not a table rebuild, so whether a battle load, save load or New Game re-derives these tables is still unmeasured; a read-first idempotent writer is correct either way and is what ships. STILL OPEN: Berserk immunity across jobs is unmeasured; the grant is job-global so enemy units of the same job inherit the command (RESOLVED 2026-07-23: the AI does not cast a zero-value command, so the inheritance is player-invisible; see the 2026-07-23 row below)  OWNER FLIP 2026-08-12 (LW-123 regression pass through the shipping mod: the granted command cast in a real battle, three full hold cycles, the mark landing at 100 percent including a re-cast on a released enemy)

**Date:** 2026-07-22

</details>

### [invented-status-id0-mark] A status can be INVENTED: blank StatusEffectData id 0 applies at 100% and performs nothing; Wall REJECTED (makes bearer invincible)

The shipped inert mark is the blank status id 0 (band +0x45 bit 0x80, UIStatusEffect Key 1), which applies at 100 percent and does nothing, while Wall was rejected for making its bearer invincible; Proven, owner-flipped 2026-08-12 when the mark landed at 100 percent on every cast of the regression pass.

<details><summary>How we got here</summary>

**Claim (original wording):** **A status can be INVENTED: the blank row at StatusEffectData id 0 (band `+0x45` bit `0x80`, UIStatusEffect Key 1) applies at 100%, performs nothing, and ships with a BLANK name and description we are free to write. `Wall` was the first choice and is REJECTED: it makes its bearer INVINCIBLE**

**Mechanism:** The status TEXT table is `nxd/uistatuseffect.en.nxd` inside the encrypted `data/enhanced/0004.en.pac` (extract with the repo's own `audit_nxd_bakes.unpack` + `lib/nxd.decode_nxd_to_sqlite`, both need `-g fft`). 45 rows; columns `Key, DLCFlags, Comment, Type, Name, Caption, Unknown14/18/1C/20/22`; localized, so the same per-language shape as `ability.en.nxd` and the SAME self-verifying rebuild pattern applies. **`Key` = our composed status BIT INDEX + 1**, verified on five independent statuses (Berserk bit 20 -> Key 21, Invisibility 19 -> 20, Slow 29 -> 30, Charmed 34 -> 35, Immobilize 36 -> 37). THREE ROWS SHIP BLANK (Name and Caption both null): Key 1 (bit 0, `+0x45` mask `0x80`, absent from this repo's own status map), Key 15 (bit 14, `+0x46` mask `0x02`, the bit our map calls Cursed), and **Key 32 (bit 31, `+0x48` mask `0x01`, Wall)**. Claiming a blank row corrupts no existing string, which renaming a real status (Slow, Berserk) would: status text is global to the status exactly as ability text is global to the ability id. Applied via a HAND-WRITTEN inflict row (mode `0x80` first, then the five status bytes) pointed at by the ability's action-row `+15`.

**Evidence:** Owner live 2026-07-22, one battle, four candidates cast in sequence. THE SYSTEM STATUSES ARE NOT FREE MARKERS, which is the finding that cost the most and generalises furthest: `Evading` (Key 7), `Performing` (Key 8) and `Critical` (Key 24) each carry placeholder or marked-for-deletion text and each DRIVES A REAL POSE when set, observed live: Evading put the target in the Defend stance (arms crossed), Performing in the Aim crouch, Critical in the low-HP crouch. They are internal states the engine renders from, not spare flags. `Wall` LOOKED like the perfect inert marker and is a TRAP: 100% chance, correct animation, the target kept walking normally, and the only visible change was an unfamiliar status icon. It also carries `IgnoreAttacks`, the flag KO and Crystal carry, so attacks against a unit wearing it read 0%. A marker that makes its bearer unkillable is worse than no marker. THE SHIPPED MARK IS THEREFORE Key 1 (bit 0, band `+0x45` mask `0x80`), the blank row this same analysis had already identified, which took three candidates to reach because id 0 is absent from this repo's own status map. It is inert in the way `Wall` only appeared to be: no behaviour, no pose, `CheckFlags: 0` and `Counter: 0`, so no duration either. Immunity, computed from `JobData.xml` `ImmuneStatus` across all 173 jobs: Berserk 25 jobs (14.5%), Slow 22 (12.7%), Immobilize 16 (9.2%), the system statuses 3 (1.7%). The boss tier resists nearly everything: Netherseer (job 18, Loffrey), job 28 and Cleric (job 44) are each immune to 37 of the 38 names in that enum and take ONLY KO. RESOLVED THE SAME SESSION, with the best available answer. The immunity enum carries 38 names against our 40 bits, and the two absent are `Cursed` and `Wall`. Cast at LOFFREY, a Netherseer (job 18) immune to 37 of those 38 and otherwise taking only KO: Provoke read 100% and the icon rendered over his head. So `Wall` sits OUTSIDE the immunity system entirely and nothing in the game resists it. SCOPE NOTE AND ITS RESOLUTION: that cast was `Wall`, before the mark moved to id 0, so it never covered the mark that ships. RE-RUN WITH THE SHIPPED MARK, owner live 2026-07-22: Ramza cast Provoke at Loffrey (Divine Knight, Lv 54, flagged Objective and Enemy, hp 807/807); the cursor read 100%, and `tools/probes/provoke_mark_probe.py find` immediately afterwards showed him at band slot 8 wearing status id 0 in BOTH layers (composed byte 0 `0x88` = the mark plus Charging, inflicted byte 0 `0x88`). So the SHIPPED mark, not merely its neighbour, lands on a boss-tier Objective unit at 100% and registers properly. LIMIT KEPT DELIBERATELY: that is ONE boss on ONE job. It evidences the acceptance criterion; it does not evidence the universal claim, which still rests on the enum-absence argument above. No per-job `ImmuneStatus` decode is in tree, so Divine Knight's own resist set was not checked. Free corroboration from the same read: Protect appeared in that unit's COMPOSED layer and NOT in inflicted, which is exactly the innate-versus-registry split this three-layer model predicts. Consequences: the marker lands on every unit including the boss tier, no item-card exception is needed, and the cast-detection lane that was being held in reserve for immune targets is not needed either. This is also why the marker must stay a status the engine does not otherwise use: an unresistable status is only safe because nothing else depends on it.  OWNER FLIP 2026-08-12 (same regression pass: the shipped mark landed at 100 percent on every cast of the session)

**Date:** 2026-07-22

</details>

### [status-removal-one-sided] Taking a status OFF is one-sided: the engine's refusal reads the COMPOSED layer, and composed is NOT re-derived from inflicted

Status removal is one-sided: the engine's already-has-it refusal reads the composed layer and composed is not rebuilt from inflicted, so the shipping posture clears both layers with mask-scoped writes; Proven, owner-flipped 2026-08-12 when the shipped both-layers clear worked through the mod.

<details><summary>How we got here</summary>

**Claim (original wording):** **Taking a status OFF is one-sided: the engine's "this unit already has it" refusal reads the COMPOSED layer, not the inflicted registry, and composed is NOT re-derived from inflicted**

**Mechanism:** Applying is two-sided and removing is not. A cast writes BOTH layers inside a single 33ms sample (band composed `+0x45..+0x49` and inflicted `+0x1D3..+0x1D7`), which is the registry model working as documented. Removal does not mirror it: clearing the composed bit alone releases the engine's refusal and the ability becomes castable again, while clearing the inflicted bit alone leaves the refusal in place. The composed bit also STAYS clear with the inflicted bit beside it still set, so composed is not rebuilt from inflicted on any timescale a 33ms loop cares about. SHIPPING POSTURE: clear BOTH, composed first. Composed is the half that does the work; the registry bit goes too because one left set is residue that may reach a save, and if a re-compose ever does fire the runtime simply clears it again on the next tick. Every write is a MASK-SCOPED read-modify-write, never a byte write: band `+0x45` is shared with Dead `0x20`, Undead `0x10`, Charging `0x08` and Jump `0x04`, and KillTracker's death detection reads that byte.

**Evidence:** Owner live 2026-07-22, `tools/probes/provoke_mark_probe.py`, one enemy at band slot 15 wearing the Provoke mark. Cast: composed `00 00 40 00 00` to `80 00 40 00 00` and inflicted `00 00 00 00 00` to `80 00 00 00 00` in one sample. Three clears, each held 3s: composed alone stayed clear and the recast was ALLOWED at 100%; inflicted alone stayed clear and the recast was still REFUSED; both stayed clear and the recast was allowed. The mask discipline earned itself on the first run, where the byte read `0x88`, the mark plus Charging on a mid-charge unit. CORRECTS `tools/probes/status_map.py`'s header, which said composed is re-derived from inflicted every frame and that a composed-only write is wasted; it also settles the tension that header recorded between that claim and the proven composed-only poison hold, in the poison hold's favour. LIMITS: one status (id 0), one unit, one battle; whether a re-compose fires on some longer edge (turn open, battle event, save reload) is unmeasured, and 3 seconds is not forever.  OWNER FLIP 2026-08-12 (same regression pass: a stray player-side mark self-scrubbed within a tick and the recast was accepted, the shipped both-layers clear working through the mod)

**Date:** 2026-07-22

</details>

### [facing-byte-band-0x35] Unit FACING is band +0x35 low 2 bits: 0=S 1=W 2=N 3=E, tracking the Wait facing wheel LIVE (the Bulwark back-tile input)

Unit facing lives in band +0x35's low 2 bits (0=S 1=W 2=N 3=E), tracks the Wait facing wheel live, and the behind-tile is (gx,gy) minus the look delta; Proven, owner-flipped 2026-08-12 when the Bulwark regression pass showed the facing-W plant barring the tile directly behind the wielder.

<details><summary>How we got here</summary>

**Claim (original wording):** **Unit FACING is band `+0x35` low 2 bits (combat `+0x51`, the SAME byte whose bit 7 is the terrain layer): 0=S(+y) 1=W 2=N 3=E, and it tracks the Wait facing wheel LIVE (the Bulwark back-tile input)**

**Mechanism:** Aligns the PSX battle-struct trio 0x47/0x48/0x49 (X, Y, elevation/facing bits 0x03) onto the proven band `+0x33` gx / `+0x34` gy / `+0x35` bit7 layer trio. AXIS CORRECTED BY THE LIVE PASS 2026-07-28 06:15: the unit LOOKS toward S=(0,-1) W=(-1,0) N=(0,+1) E=(+1,0); behind = (gx,gy) MINUS the look delta. Proof: a facing-N plant barred (x,y+1) and it rendered IN FRONT of the unit (owner-witnessed), the facing-W plant barred (x+1,y) correctly behind, and the battle-start snapshot had enemies at low y reading N while visually facing the players at high y. FFTHandsFree's "y+ = south" delta note is CONTRADICTED for grid deltas (their byte-to-visual-label proof stands; their axis comment does not). Render-side mirror: node `+0x2C` u16 angle on the 0x2000-step wheel (observed W=0x6000, N=0xE000, the expected 0x8000 apart). CONSUME THE BAND COPY: FFTHandsFree reads the static-array copy (`0x140893C00` slot `+0x35`) and once logged a player byte reading East while visually West (2026-04-24); this session the static array fingerprint-matched ZERO live slots, so that copy is stale/dead on current patch and is the likely home of their anomaly

**Evidence:** FFTHandsFree session-30 owner-visual proof of the encoding (4 units eyeballed: FacingByteDecoder.cs + its project_facing_byte_s30 memory, 2026-04-17). THIS repo live 2026-07-28, `tools/probes/facing_probe.py watch`: with the owner on Ramza's Wait wheel, seat 24 band `+0x35` stepped `0x01` W to `0x02` N PRE-confirm (t=282.24s) with node `+0x2C` mirroring; a battle teardown at t=288.75s zeroed then re-stamped every seat to its initial facing (rollback event, values restored exactly). The confirm-stamp-at-turn-end read was cut off by that rollback; it folds into the Bulwark owner live pass, where a wrong back tile is instantly visible (red circle-slash on the wrong side). Ready for the owner call  OWNER FLIP 2026-08-12 (Bulwark regression pass: the facing-W plant barred (6,10) directly behind the wielder at (5,10), owner-eyewitnessed with enemy move ranges respecting it)

**Date:** 2026-07-28

</details>

### [zero-value-command-ai-silent] A zero-value granted command is NOT chosen by enemy AI: the job-global JobCommand grant leaks PRESENCE but not USE

Enemy AI never casts a zero-value granted command, so the job-global Provoke grant leaks only the command's presence, not its use; Proven, owner-flipped 2026-08-12 on the 2026-07-23 evidence after the LW-123 regression pass completed clean.

<details><summary>How we got here</summary>

**Claim (original wording):** **A zero-value granted command is NOT chosen by enemy AI, so the job-global JobCommand grant leaks the command's PRESENCE but not its USE**

**Mechanism:** Provoke (ability 189) applies an inert zero-value mark (formula 56, 0 damage, StatusEffectData id 0, no effect the AI scores). The enemy AI selects abilities by utility, so a do-nothing ability scores about zero and is never cast even with UsableByAI SET and the command LEARNED. The mapped suppressor is the COMMON-data table (512 rows of 8 bytes at `0x140787F80`, byte `+7` mask `0x80` = UsableByAI; ability 189's byte at `0x14078856F`), confirmed a REAL flag field (401 of 512 rows set, single copy, no decoy twin), but it is NOT needed and NOT shipped: with no baseline where the AI casts Provoke, clearing the bit has no demonstrable behavioural effect, and an unproven ability-table write is the surface that has bitten this repo before. Worst case even if the leak fired: an enemy Provoke on a player unit hangs only the inert mark, and the hold engine reacts to an ENEMY wearing the mark, so nothing triggers.

**Evidence:** Owner live 2026-07-23: an enemy Knight carrying Provoke as a LEARNED command, usable-by-AI bit SET, never cast it across many turns of a real battle. Step-0 read `tools/probes/ability_grant_probe.py aiflag 189` = row `00 00 64 41 12 00 00 80`, byte `+7` = `0x80` SET, 401/512 set (a sane flag split), no twin. Closes `docs/PROVOKE_AC.md` criterion 0e (met-by-observation). LIMIT: one enemy, one job, not boss AI; a future AI-attractive Provoke would reopen it (the clear is ready).  OWNER FLIP 2026-08-12 (owner-directed on the 2026-07-23 evidence after the LW-123 regression pass completed clean)

**Date:** 2026-07-23

</details>

### [twin-crossbow-dual-fire] Dual-gun twin-fire generalizes to CROSSBOWS: second crossbow in roster off-hand + Dual Wield Key 477 makes Attack fire twice

Writing a second crossbow into the roster off-hand plus the Dual Wield support Key 477 renders both crossbows and makes the basic Attack fire twice, identical to the Gun Slinger lane; Proven via the LW-171 Crossfire live pass, owner-flipped 2026-08-12.

<details><summary>How we got here</summary>

**Claim (original wording):** The dual-gun twin-fire mechanism generalizes to CROSSBOWS: a second crossbow written into the roster off-hand (ROffHand +0x18) plus the Dual Wield support Key (477 at RSupport, u16) renders both crossbows on the equip screen and makes the basic Attack FIRE TWICE in battle, identical to the proven Gun Slinger lane

**Mechanism:** Same write pair as the PROVEN Gun Slinger roster row (2026-07-15) and the LW-168 u16 Key model: the probe duplicated the main-hand equip-space id into the off-hand and the roster already carried Key 477; battle construction baked both into the combat unit. No engine resistance beyond the known equip-screen normalize (the probe's re-assert handled it)

**Evidence:** owner live 2026-08-12: twin Eclipsebolts (equip-space id 78) via dualgun_probe twin 1 rendered in both hands on the equip screen ("Works!") and Attack fired twice in a real battle ("works like a charm"); owner wants a crossbow twin signature in the next release (captured as the LW-171 backlog row). Flip to Proven rides that feature's live pass  OWNER FLIP 2026-08-12 (the LW-171 Crossfire live pass, this row's pre-registered flip condition: the shipping mod loaded the twin Arbalest plus Dual Wield, Attack fired twice in battle, a kill credited on the crossbow lane, and the pistol lane re-verified beside it)

**Date:** 2026-08-12

</details>

### [terrain-grid-cursor-source] THE terrain grid: pathfinding/zone-control half CONTRADICTED 2026-07-28; grid is the cursor/inspection height source, real per-tile HEIGHT

The terrain grid at 0x140D8DCC0 is real per-tile height read live for the single inspected tile (height panel and camera pan), but the original zone-control/pathfinding claim was contradicted on 2026-07-28: it is not the move-range input, and the real walkability structure is unlocated. Grid writes persist for the whole process session and must be explicitly restored; the height-source half stands Proven.

<details><summary>How we got here</summary>

**Claim (original wording):** **THE terrain grid -- PATHFINDING CLAIM CONTRADICTED 2026-07-28 (the "zone control" half is FALSE; the row is retained here with this banner rather than deleted so nobody re-derives it). Extended live testing showed the grid is the CURSOR/INSPECTION source, read live for the SINGLE tile under inspection (height panel + camera pan, reproducible), NOT the move-range/pathfinding input. Diagnostic: holding a 4-tile ring on ALL NINE in-memory copies changed exactly ONE tile -- always the inspected one "behind Ramza" -- and left the move range and enemy pathing untouched across many attempts; a goblin walked through a held ring. The 2026-07-27 "tile not lit" was a false positive (a genuinely out-of-range tile misread as a write-caused exclusion). The reader `0x14027B25A` reads the height of the tile a UNIT STANDS ON (occupancy/elevation), not destination walkability. The real move-range walkability structure is UNLOCATED. What still stands: the grid is real per-tile HEIGHT (whole-map dump matched the battlefield), it is read live for inspection, cross-map rebuild is real, and the map-dims pair reads correctly. See the Contradicted section entry dated 2026-07-28.**  ORIGINAL (now-contradicted) CLAIM: static `0x140D8DCC0`, 8 bytes/tile, `idx = x + y*mapWidth + layerBit*0x100`, and it is the pathfinder's LIVE INPUT (writable terrain = zone control)

**Mechanism:** Found by tailing the occupancy read: CE what-accesses on a bystander's combat `+0x4F` at Move-open caught reader `0x14027B25A` computing the tile index (`imul ecx,r14d` with r14 = map width, layer bit from unit `+0x51 >> 7`) and reading `[0x140000010 + idx*8 + 0xD8DCB2/B3]`. Field decode so far: f2 low5 = HEIGHT (whole-map dump rendered a coherent 11x12 heightmap matching the live battlefield, dead rows past the map edge), f2 top3 = slope family (unconfirmed), f0 carries flag bits (a 0x40 difference observed between two adjacent occupied tiles, semantics OPEN). WRITE TEST: holding f2 = 20 (height-1 tile -> fake cliff) EXCLUDED the tile from the unit's move range AND the hover camera panned up to the phantom height; auto-restore returned it to normal. The engine believes the write end to end: pathing, cursor, camera. SAME-EVENING FOLLOW-UPS (Bulwark Phase 0): a second battle on a different map (10x18 vs 11x12) read a coherent heightmap at the same base, and a height write (byte +2) appeared to reset after a battle change. **THAT "rebuilt per load" CLAIM IS CONTRADICTED 2026-07-28 (late): the WALKABILITY byte +6 bit0 PERSISTS across battle restarts AND onto the WORLD MAP within one process session, and left set it CRASHED THE GAME (owner). So grid writes do NOT auto-clear on map change; they live for the whole process session. (The byte +2 "reset" was likely the renderer re-deriving, not a grid reload, or a different map instance.) SAFETY RULE: any grid write MUST be explicitly restored; never rely on a map reload to clean it. Only a full GAME RELAUNCH (new process, fresh from the on-disk exe) guarantees a clean grid -- all writes are RPM/WPM to process RAM and never touch disk or saves.** CLEANER-LEVER HUNT REFUTED: f3 = 20 held 90s (owner eyes: "nothing changed") and f0 flag bits 0xC0 held 60s (same verdict) -- neither blocks movement; HEIGHT is the lever and the camera-pan cosmetic is the accepted cost. Still open: slope/surface field semantics. MAP-DIMENSIONS COMPANION (same evening): the u8 pair at `0x140C6AD6A/B` read 11x12 then 10x18 across the two live maps, each matching the battlefield, and the pathfinder's own width register (r14 = 11 in the CE capture) matched the pair on the 11-wide map -- two-map correlation, not yet an isolated proof, so any consumer carries runtime sanity gates (W,H in 1..30, W*H &lt;= 256, wielder inside WxH). Instruments: `tools/probes/terrain_probe.py` (poke 103 2 20), the CE captures 2026-07-27 evening

**Evidence:** owner-witnessed live 2026-07-27 ("YES! The tile is not lit up... when I hover it the screen pans up"); probe held 60s and restored clean

**Date:** 2026-07-27

</details>

### [party-nameid-unique-key] Party roster nameIds are UNIQUE per occupied row, and the frame mirror covers every deployed player seat (the LW-252 identity key)

Every occupied party roster row carries a nonzero nameId distinct from every other row, and in battle every deployed player seat's frame nameId (Offsets.ANameId, the [frame-1fc-nameid-mirror] field) reads nonzero, maps to exactly one roster row, and holds steady through movement, damage, and the unit's own death; two deliberately deployed (level,brave,faith) fingerprint twins resolved to distinct nameIds. PROVEN, owner-flipped 2026-08-17 after the LW-252 live trial (below). The LW-252 identity rework (shipped 3b6786f) keys every locate, kill credit, and per-wielder grant on this fact and fails closed on the duplicate case this row rules out.

<details><summary>How we got here</summary>

**Claim:** the roster nameId can serve as the truly unique join key between a party roster
row and its live band seat, replacing the colliding (level,brave,faith) stat fingerprint
that fuels the wrong-unit bug (player report 2026-08-17: kills credited to the wrong weapon,
Dual Wield leaking onto a second unit).

**Mechanism:** roster RNameId (row +0x230, u16) is assigned per recruited unit; the combat
frame mirrors it at +0x1FC ([frame-1fc-nameid-mirror], PROVEN 2026-07-01). If no two
occupied rows share a nameId, then nameId equality is an exact identity join and every
stat-fingerprint ambiguity becomes resolvable instead of guessable.

**Evidence (probe `tools/probes/nameid_unique_probe.py`, read-only, addresses parsed from
Offsets.cs):**
* P1 `roster` verb, world map, owner's real save: 42 occupied rows, every nameId nonzero,
  zero duplicates. Bonus: rows 16 and 39 are exact (36,49,62) fingerprint twins (the two
  chocobos Phlegrai and Pisos) with DISTINCT nameIds 929 and 914, live proof that the
  collision class behind the player report occurs in an ordinary roster.
* P2 `battle` verb, run 1: owner deployed Ramza plus BOTH fingerprint twins. Seats 24/25
  read nameIds 929/914 and mapped to roster rows 16/39 respectively; identical on every
  field the current locate matches (level, brave, faith, unarmed weapon sentinel). Ramza
  appeared at his real seat AND his frozen (0,0) mirror seat, both nameId 1.
* P2 run 2, several turns later, one twin KO'd: all 18 plausible seats read byte-identical
  nameIds to run 1 while positions and HP moved; the KO'd twin (HP 0) KEPT nameId 929. So
  the mirror survives death, which also covers corpse-side identity.
* P2 run 3, battle RESTARTED with placement order reversed (Ramza first, twins after): the
  three player seats re-bound per the NEW placement order (Ramza moved seat 26 to 24), the
  nameId join still resolved every unit to its correct roster row, and all three nameIds
  were IDENTICAL across the reload. So band seats follow PLACEMENT order (never roster
  order), the join is order-proof by construction, and player nameIds are stable across
  battle loads. The frozen (0,0) mirror seat cloned the LAST-PLACED unit in both battles
  (Ramza in run 1, Pisos in run 3), each time with that unit's exact nameId.

**Recorded caveats:** (1) uniqueness sampled on ONE save (42 rows); a duplicate on another
save must still fail closed, never mis-resolve. (2) The frozen (0,0) mirror carries the
SAME nameId as the real seat, so the real-position twin filter stays mandatory; nameId
replaces the stats guessing, not the mirror handling. (3) Generic ENEMY nameIds re-roll per
load ([boss-canonical-nameid-stable]), so nameId is an in-session join key for players, not
a cross-save persistent identity. (4) No player seat ever read nameId 0 in these runs; the
unseeded-0 fail-safe convention in code stays.

**PROVEN flip evidence (owner live trial, 2026-08-17, the LW-252 acceptance battle):** dev
build 3b6786f deployed; owner fielded Ramza plus BOTH fingerprint-twin chocobos in a random
battle. Leg 1: Save the Queen claimed kills 17 and 18 normally (no over-refusal). Leg 2 bait:
each chocobo's killing blow resolved as a real player with no living weapon and went
UNCREDITED (log: "the acting player wields no Living Weapon"; tape: latch weapons=[] and
no-credit reason=untracked-weapon), where the pre-fix code credited such kills to the stale
latch. Leg 3: zero guard events, zero ambiguity warnings, clean battle-exit flush. The tape
additionally shows TWO independent turn counters for the one shared fingerprint (36,49,62):
the twins tracked as distinct units in live combat for the first time.

**Date:** 2026-08-17

</details>

### [twin-grant-inventory-desync] The twin-weapon grant's raw roster writes desync the game's inventory accounting: a player-equipped shield is DESTROYED without refund, and a lawfully unequipped twin is REFUNDED as a duplicate

The Gun Slinger / Crossfire twin grant (the [gunslinger-roster-offhand-support-writes] mechanism) and the game's own equip bookkeeping run rival ledgers over the same roster row, and both LW-193 failure shapes were caught on one tape 2026-08-17 (owner-driven drill, throwaway save): the game deleted a player-equipped shield with no inventory refund after the mod re-asserted the twin over the game's own normalize, and the game minted a second Outrider Pistol when the player lawfully re-equipped the main hand while the conjured twin sat in the off-hand slot. PROVEN, owner-flipped 2026-08-17, same session as the drill ("Flip it"); the owner watched every drill step live and the tape is banked in-tree.

<details><summary>How we got here</summary>

**Claim:** LW-193 (owner report 2026-08-13: Crossfire/Gun Slinger permanently deletes the wielder's off-hand item) is caused by the twin grant writing raw roster bytes that the game's inventory accounting never sees, with the destruction performed by the GAME's illegal-state cleanup and the loss made permanent by the absence of any refund.

**Mechanism:** the roster row has a shield slot (RShield +0x1A) SEPARATE from the dual-wield off-hand weapon slot (ROffHand +0x18); GunSlinger snapshots and writes only +0x18 and +0x0A (support), never +0x1A. The live gunslinger.json confirms it: every unit that ever received a twin holds origOff 255 (EMPTY), so no shield id was ever captured anywhere. When the player equips gear the game moves items between the sack (count[id] u8 at InventoryCountBase) and the row with full accounting; the mod's stamps move nothing. The game clears an illegal off-hand within ~1s of a player equip touching the row; the mod's re-assert branch (GunSlingerPolicy Write) stamps it back within ~1s, and the loop ends when the game finds an already-illegal state and zaps the shield slot with NO refund.

**Evidence (probe `tools/probes/offhand_shield_probe.py` watch, tape
`tools/probes/tapes/lw193_watch_20260817_055131.log`, all timestamps 2026-08-17, slot 0 Ramza):**
* 05:52:39 control leg, lawful flow: equipping gun 73 removed shield 138 from +0x1A AND refunded it (sack 138: 3 to 4). The game refunds when IT removes gear through its own flow.
* 05:52:43 twin stamp: oh 255 to 71, support to 477, no sack movement (mod writes are invisible to the sack).
* 05:52:54 the game cleared the twin (oh to 255); 05:52:55 the mod stamped it back. The re-assert war, one round per second.
* 05:52:57 player equipped shield 128 (sack 128: 2 to 1, sh to 128); the game again cleared the illegal oh; 05:52:58 the mod re-stamped oh 71. Row now holds main gun + twin gun + shield, a state the game considers impossible.
* 05:53:06 the game resolved the impossible state: sh 128 to 255 with NO sack refund. Sack 128 stayed at 1. The shield ceased to exist anywhere; this is the LW-193 loss, timestamped.
* 05:56:02 release leg: player equipped weapon 10 in the main hand; the game cleared the twin AND refunded it (sack 71: 0 to 2) despite the player owning ONE Outrider. Item duplication, the second failure shape.

**Recorded caveats:** (1) the supp byte flip-flopped 477/455 in lockstep with the game's normalizes, so the game holds its own authoritative copy of the support pick somewhere and rewrites the row from it; the mod's support re-assert fights the same unwinnable war. (2) The exact game-side trigger of the 05:53:06 zap (menu navigation vs a background legality sweep) was not isolated; the loss itself does not depend on it. (3) Battle-edge behavior (the gear commit at battleMode 0) was not exercised in this drill.

**Date:** 2026-08-17

</details>

### [wp-table-write-live-damage] Writing a weapon's WP in the resident stats table changes the next shot's damage mid-battle, no restart, and reverts clean

A weapon's power lives in one shared table the game re-reads on every shot, so raising a number there makes the very next attack hit harder with no reload, and putting the number back restores the old damage exactly. Owner ran the LW-316 probe live 2026-08-25: Stoneshooter (id 73, WP16, formula 3 = WP squared) read 291 on the baseline shot (owner had ~125 percent zodiac advantage on the target), held WP=4 read EXACTLY 16 (4 squared, unambiguous, no crit or variance can produce a teens number from 291), and the post-restore shot read 291 again, byte-exact revert. PROVEN, owner-flipped 2026-08-25 (the LW-316 live run). This un-gates the physical-gun WP growth lane (LW-317), with the engine write TURN-SCOPED like WeaponPalette's repaint because the table is shared: while held, every wielder of that id (enemies included) gets the changed damage.

<details><summary>How we got here</summary>

**Claim:** a mid-battle write to the resident ItemWeaponData stats table changes the damage of the wielder's next shot with no restart, and restoring the byte restores the damage.

**Mechanism:** resident stats table at `0x14080F690` + SecondTableId*8 (8-byte records: range, attackflags, formula, 0xFF, WP, evade, elem, onHit; SecondTableId read from ItemData `0x14080EA90` + id*12 + 4, == own id for weapons). The damage routine resolves the record per shot (precedent: the WeaponArtHook incident, [weapon-blade-art-walled] correction, where an art-id remap made Warbrand compute Broadsword damage live). Instrument: `tools/probes/warbrand_damage_probe.py --hold 4=N --id 73` (100ms hold, byte-exact restore on Ctrl+C); table base re-verified 127/127 rows against the shipped XML the same day.

**Evidence:** owner live run 2026-08-25, one battle: baseline 291 (WP16, zodiac-advantaged target), held WP4 -> 16 exact, restored -> 291 exact, same target throughout. Open observation, not load-bearing: 291 vs the plain 256 = WP squared model is unexplained in detail (owner reports ~125 percent zodiac, which predicts 320); the WP4 shot reading a plain 16 says the modifier is not a flat multiplier. The lane needs only "table WP drives damage live", which held in both directions.

**Date:** 2026-08-25

</details>

### [current-faith-write-scales-magic-gun] A held CURRENT-faith write scales a magic gun's spell damage linearly, both directions, and the forecast tracks it

Holding a unit's live Faith higher makes their magic gun's spells hit harder in exact proportion, and dropping it back drops the damage back, with the game's own damage forecast following the held value. Owner ran the LW-316 probe live 2026-08-25 with Ramza + Blaze Gun (id 75, formula 4, random-tier elemental cast): Faith 75 baseline 45 twice; held Faith 90 vs a time mage read forecast 55 and dealt 55; restored to 75 the same target read 45 = floor(55 * 75/90), the exact linear caster-side model. PROVEN, owner-flipped 2026-08-25 (the LW-316 live run). This un-gates the magic-gun Faith lane (LW-317) and confirms the design's cap-near-85 instinct: the same linearity raises damage TAKEN from enemy magic.

<details><summary>How we got here</summary>

**Claim:** writing the CURRENT faith copy (combat +0x2D, band +0x11; the proven StatHold lane, [[brave-faith-current-vs-orig-offsets]]) changes formula-4 magic-gun spell damage on the next shot, linearly with caster faith.

**Mechanism:** current faith is re-read per shot by the formula-4 damage calc AND by the pre-action damage forecast; the orig copy (+0x2C/+0x10) stays untouched as the locate fingerprint. Instrument: `tools/probes/lw316_lane_probes.py faith_hold <slot> <value>` (150ms re-assert, restore on Ctrl+C).

**Evidence:** owner live run 2026-08-25: baseline Faith 75, two shots at 45; held 90, forecast 55 = actual 55 (time mage, ~125 percent zodiac); restored 75, same target, 45 (= floor(55*75/90)). An earlier 90-damage shot against a different target under the hold is attributed to the gun's random spell-tier roll plus the target change, not counted as evidence. The bidirectional same-target forecast+actual pair is the clean isolate (no tier RNG in the forecast comparison).

**Date:** 2026-08-25

</details>

### [maxhp-hold-attribution-safe] A held raised MaxHP does NOT break kill attribution; current HP stays put, so the unit reads hurt until healed

Raising one unit's Max HP mid-battle leaves the mod's kill bookkeeping intact: with the hold live the unit's kill credited its own weapon cleanly, and the battle saved normally. Current HP does not follow the raised max, so the unit shows e.g. 624/724 (reads hurt, heals can top up to the new max); the bar renders sanely. Owner ran the LW-316 probe live 2026-08-25 on Ramza (624 -> held 724): kill number 11 credited to Chaos Blade at the credit edge with zero ambiguity, battle-end save clean. PROVEN, owner-flipped 2026-08-25 (the LW-316 live run). This un-gates the Knight Sword HP lane (LW-317: u16 hold, clamped 999); the lane ships with the reads-hurt behavior as designed-in (growth raises the ceiling, healing fills it).

<details><summary>How we got here</summary>

**Claim:** a re-asserted u16 write to band +0x16 (AMaxHp) on one player unit neither breaks the actor resolver's (maxHp, hp, level) keyed paths nor glitches the HP display; current HP (+0x14) is left alone to observe whether the engine moves it (it does not).

**Mechanism:** the condensed active-unit mirror re-copies from the authoritative struct, so the resolver's condensed-vs-band match stays consistent when the band value is held (the godmode mis-credit came from COLLISIONS, several units flattened to identical maxhp/hp, not from holding per se; the probe warns on (level, newmax) collisions). Instrument: `tools/probes/lw316_lane_probes.py maxhp_hold <slot> <newmax>` (50ms re-assert, 999 clamp, restore + hp re-clamp on Ctrl+C).

**Evidence:** owner live run 2026-08-25: Ramza 624/624 -> hold 724: card showed 624/724, current HP untouched by the engine; kill with the hold live: `[kill] Chaos Blade claims kill number 11` + `kill credit (weapon id 37 ...)` with no could-not-determine or ambiguity lines; `[battle-end] 1 kill credited (Chaos Blade 1) ... saved` (13:17:41). The probe's condensed-mirror console verdict was not read by the owner during the run; the end-to-end credit at the edge is the stronger fact and subsumes it. Same session, separate finding: the tier toast that queued in the PREVIOUS battle delivered a battle late (logged as LW-323, display-only, unrelated to the hold).

**Date:** 2026-08-25

</details>

## Uncertain — observed live, not yet isolated / built on

### [save-edge-hooks-key-bag-counts] Hooking the save serializer and the load-apply routine, and keying the extended-inventory bag counts by a hash of the save struct's header, gives each save its own counts across a load

Not yet observed (built 2026-08-27 late from a static read, docs/research/ITEM_CAP_261_BREAK_JOURNEY.md "save edges"). What the read established: the save struct is transient with its pointer in 0x141D407A0, its +0x100..+0x1B8 header carries the play time (+0x1B4) and the slot-list metadata, the serializer is 0x140218F78 and the load-apply routine 0x14021B070 (with 0x14021DDF0 as a second restore path), all plain entries behind clean prologues. What the live pass must show: the serializer hook fires on a manual save AND on the autosave (both write the same struct), the apply hook fires on a load with the struct pointer still valid after the original returns (the header is read AFTER the original), the header bytes read at save time equal those read at load time for the same save (the key matches, the replay picks the right entry), and the second routine does not fire with a stale struct on some unrelated action (a false replay). The owner's test 2 (slot A with 2, slot B never, A again) moves this row.

<details><summary>How we got here</summary>

**Claim (original wording):** the save struct header is a stable per-save identity readable at both edges through one global.

**Mechanism:** capstone over RPM on the serializer and both restore routines; the play-time globals cross-checked against the session clock.

**Evidence:** SaveEdgeTrackerTests, ExtendedBagSidecarTests, ExtendedInventoryTests over fakes; the disk prologues of all three entries; no game run.

**Date:** 2026-08-27

</details>

### [weapon-sprite-pair-drives-swing-art] The two-byte sprite/palette record at 0x140785CF0 + id*2 picks BOTH the drawing and the palette of a weapon's swing, and it is read on every swing

Observed 2026-08-27 00:50 and 00:56 by the owner on 1.5.2, mid-battle, no relaunch, no fresh battle: Save the Queen (id 34, vanilla record F0 0C) was rewritten from outside to 50 03 (Warbrand's axe drawing, Chaos Blade's palette nibble) and the very next swing drew Warbrand's art in purple (tools/probes/lw349_sprite_pair_graphic_swap_34.png); rewritten to F0 03 (Warbrand's own record) the next swing drew the same shape in Warbrand's steel (tools/probes/lw349_sprite_pair_palette_swap_34.png); restored to F0 0C afterwards. So byte 1 selects the drawing and byte 0 the palette, per draw. This is the mechanism [weapon-palette-assignment-walled] said did not exist: that row's probes used base 0x140785CF2, one item off. Not yet built on; owner flip pending.

<details><summary>How we got here</summary>

**Claim (original wording):** the punch with the Moonblade comes from the sprite-pair accessor thunk 0x1402B8E60 -> 0x14FEC80C0 returning NULL for ids without a range, and the record it returns for real weapons is what the plain sprite composer 0x14026BC60 (byte 1) and the copy-protected CLUT loader 0x14E92B95C (byte 0, high nibble times a stride into the 0x140D35750 palette workspace, then the low nibble) read.

**Mechanism:** static read of the live process with capstone over RPM (tools/probes/lw346_live_disasm.py, lw346_xref_scan.py), then the two-swing poke test above with tools/probes/lw346_sprite_pair_poke.py (undo file kept the vanilla bytes; restore verified).

**Evidence:** docs/research/ITEM_CAP_261_BREAK_JOURNEY.md (2026-08-27 00:00-00:50 and 00:50-01:00 sections); the two owner screenshots named above.

**Date:** 2026-08-27

</details>

### [inventory-default-order-drops-unknown-ids] The party inventory's default display order is rebuilt from a per-tab id table and drops any id the table lacks; appending id 261 to the table lists it

Observed 2026-08-26 20:55 by the owner on 1.5.2 (screenshot tools/probes/lw346_moonblade_listed_152.png): with the research rig boot-armed, three live pokes from outside (weapons order table [127] 0x00FF -> 0x0105 and [128] 0 -> 0x00FF at 0x1407B264E/0x1407B2650, scan guard imm 0x140285E2D 05 -> 06) and a re-open of the Items screen, id 261 was the last Weapons row with a full three-page card, sorted, equipped and unequipped. Not yet isolated as a single mechanism the mod owns: the tables are runtime-filled and the durable fix (a hook on the rebuild routine) is being built.

<details><summary>How we got here</summary>

**Claim (original wording):** the list rebuild 0x140285DF0(table, list) copies list words in table order and discards the rest; the weapons table (0x1407B2550 via 0x14067F498[0], 127 ids, FF on disk, filled at load) has no 261; the equip picker uses a second table set (0x14067FA90, weapons 0x141874540) regenerated from the last inventory sort.

**Mechanism:** read live with capstone over RPM (tools/probes/lw346_live_disasm.py, lw346_xref_scan.py); the live buffer 0x141811470 order matched the table exactly before the poke and held 261 last after it (tools/probes/lw346_inventory_snapshot.py --rows: 112 buffer entries, 112 UI rows in the same order).

**Evidence:** docs/research/ITEM_CAP_261_BREAK_JOURNEY.md (2026-08-27 early sections), the snapshot probe output, the owner's screenshot.

**Date:** 2026-08-26

</details>

### [validity-compaction-keeps-261-as-37] The list clean-up passes cannot drop id 261 while the rig's validity stub maps it to 37

Read live 2026-08-27 (no owner action): the thunk 0x1402B8EBC -> rig stub 0x114AC0000 (261..511 -> 37) -> original 0x14FED2C3F computes valid = (id-1) <= 252 || (id-256) <= 4 and returns EAX = 1, with both hidden constants re-read from 0x154F773F4 and 0x14EC34A0C; every compaction pass (sorter 0x140285B10 and siblings at 0x140286265, 0x1402879C3, 0x140287B23, 0x140288175, 0x1402882E2) masks the word with 0x3ff and calls that thunk. Status Uncertain because it is a code reading plus one consistent live observation (261 survived every comparator sort the owner tried), not an isolated probe of each pass.

<details><summary>How we got here</summary>

**Claim (original wording):** the night-3 plan to force six "keep" branches targets a gate that is not closed.

**Mechanism:** tools/probes/lw346_compaction_audit.py (read-only) confirmed every call target and branch byte; the routine behind the stub was disassembled through its jmp [rip] pointer.

**Evidence:** docs/research/ITEM_CAP_261_BREAK_JOURNEY.md (2026-08-27 early); owner sorts on 2026-08-26 kept the Moonblade listed.

**Date:** 2026-08-27

</details>

### [acquired-sort-ignores-rebuild-count] Sorting by Acquired rebuilds the list against the acquired-order list and keeps a stale count, which crashes on the last row when an id was dropped

Observed 2026-08-26 21:15 by the owner: sorting the inventory by Acquired with id 261 listed crashed at 0x14029FC5B (dump FFT_enhanced.exe.20664.dmp: read of the rig catalog at id 0x3FF, the 0xFFFF terminator masked). Code read: mode 8 of 0x140286228 calls the rebuild with the acquired list 0x141874726 and discards its return; the acquired list's maintainer 0x140286160 walks ids 1..260 (imm 0x140286187). The same shape explains the three night-3 crashes.

<details><summary>How we got here</summary>

**Claim (original wording):** the 112-rows-versus-111-entries mismatch comes from a rebuild whose count the caller ignores, not from a validity clean-up.

**Mechanism:** tools/probes/lw346_minidump_context.py on the dump; disassembly of 0x1402862A0..0x1402863E2 and 0x140286160..0x140286227.

**Evidence:** docs/research/ITEM_CAP_261_BREAK_JOURNEY.md (2026-08-26 21:15 section); Windows Application log Event 1000 fault offset 0x29fc5b.

**Date:** 2026-08-26

</details>

### [kills-pool-region-recurrence] The Kills string pool regions recur across launches; one base identical in every observed launch

Observed 2026-08-26 across four distinct game launches (flight tape coverage records, read-only): region 0x15DC00000 sat at the IDENTICAL base in every launch with a stable 363 to 370 site count; the largest region (about 1100 to 1400 sites) was stable within each launch and moved once between launches (0x15E800000 vs 0x163C00000); the third region drifted per launch inside the 0x4F9A00000000 neighborhood. Basis for LW-324's warm start (persist located sites at quit, verify then adopt at next launch). SITE level recurrence inside a recurring region is NOT yet measured, so the warm start must remain correct at zero percent adoption (adoption gated on the existing paint-time ownership verification) and must log its own adoption rate; the owner's LW-324 live pass doubles as the site-level measurement. UNCERTAIN until that read.

### [auto-battle-mode-byte] Per-unit auto-battle byte at combat+0x1EC drives the behaviour AND the overhead Auto tag, and a write forces the tag to re-render

Uncertain as of 2026-08-24: the owner isolated the byte in CE and drove it both directions live (0 = auto off, tag gone; 12 decimal = 0x0C = auto on, tag shown, AI acts), but the value's encoding (whether instruction modes change it) and the game's re-stamp behaviour are unprobed, and nothing is built on it yet.

<details><summary>How we got here</summary>

**Claim (original wording):** one byte per unit at combat+0x1EC turns vanilla auto-battle on and off; the overhead "Auto" tag follows the write, which also forces the tag's text to be re-set and re-rendered.

**Mechanism:** the tag widget re-derives its state from the unit: Dicene's fftivc.handsfree decompile names UpdateTagAnimFromBattleUnit (unit ptr at anim+0x148) and the manual-control flag combat+0x05 bit 0x08 (roster mirror partyUnit+0x04) that his mod clears in CopyUnitToBattleUnit / CopyJobEffectsToUnit / set_status_all hooks, those three being the game's re-stamp points. The tag TEXT is copied from a source string into a per-widget heap holder at show time and rendered to glyphs ONCE: thirteen bare "Auto" holders were marker-poked live and none changed on screen, then an owner auto toggle rewrote exactly two of them back to "Auto" (the live overhead + timeline holders) while eleven spares kept their markers.

**Evidence:** owner CE session 2026-08-24: FFT_enhanced.exe+1855ECC (= band slot 0 combat+0x1EC) written 0 and 12 both directions live, CE screenshot 21.42 (Found: 1, Previous 0, Value 12); tag and behaviour both followed. Probe rounds pre-crash: tools/probes/auto_text_probe.py classify (279 CE hits bucketed; the mass-poke crash class was node-name tables like TextAuto/ShowAuto), pokeall (13 holders marked, 11+ bytes slack each), check (exactly 2 rewritten on toggle). Decompile: ilspycmd over fftivc.handsfree.dll, session scratch only.

**Date:** 2026-08-24

</details>

### [card-materializes-from-named-pool] The equip card rebuilds its description text from the name-gated string pool at open, so painting the pool before the draw is what reaches the screen

The claim that the specific NAME-GATED pool copy (the one `PoolLocatorPolicy`'s NameWindow gate keeps, as against the ~26 name-less transient render copies it deliberately drops) is the copy the equip card materialises from is Uncertain as of 2026-08-17: it is the premise the entire LW-37 pool-paint design rests on and it has never had a ledger row of its own. Everything supporting it is one grade below ledger-PROVEN, and the nearest PROVEN row, [flavor-line-overwrite-displays], cannot discriminate: that probe wrote EVERY site the whole-heap sweep had discovered, pool copies and render copies alike, so it proves the card renders from paintable heap buffers without naming WHICH.

<details><summary>How we got here</summary>

**Claim (original wording):** The equip card re-materializes its description from the stable, name-bearing UE string pool on every open; a paint that lands in that pool before the draw reaches the screen, and a paint that lands after it does not (nothing repaints an already-drawn copy)

**Mechanism:** PoolLocator keeps only regions holding a fully baked entry (a "Kills: " literal tied to its owner's flavor AND the owner weapon's NAME within NameWindow=512 bytes); PoolLocator.Policy.cs's class doc records that transient FText/widget render copies carry flavor + Kills with NO adjacent name, and that without the name gate ~26 lower-addressed render copies won the distinct-weapon tiebreak and left the real pool unpainted. Once pool coverage latches, Display.cs's `if (!(_poolPaint && MaybePoolPaint()))` retires the whole-heap sweep, which was the only lane that ever wrote into those render copies, so from that moment the mod paints the pool and nothing else.

**Evidence:** LW-37 changelog row ("The card re-materializes its description from stable UE string-pool regions"); the 2026-07-07 in-place pool overwrite that made a reopened Gloomfang card display the probe string; LW-59's stale "+3" suffix persisting "in the very pool text the card materializes from" after a tally reset; the 2026-07-08 live pass through this exact code path. All commit and changelog grade, none of it a row here. The cheap settling drill is the LW-37 recipe repeated deliberately: write a distinctive marker into ONE located pool copy with the LWDEV path armed, reopen the card, and confirm the marker shows.

**Why it matters for LW-257:** the arc's commit 2 leans on this in exactly one place, the pending-set clear predicate preferring a site inside a located pool region. If the premise is wrong there, the failure is bounded and benign (a pending id retries an already-correct repaint until its 10-beat cap drops it, costing skip-if-equal passes and no wrong write), which is why that arc shipped without settling this first.

**Date:** 2026-08-17

</details>

### [pe-key-match-kitlane-mismatch-discriminator] PE-build-key match + JobCommand rec8/rec9 mismatch discriminates a mod rewrite from a game update (LW-112 guard split)

The claim that a matching PE build key combined with a rec8/rec9 landmark mismatch means another mod rewrote the table (so the split guard arms the mod and stands down only the weapon-command lane) is Uncertain: desk analysis plus a player field report support it, but the owner's pre-registered two-leg conflict-mod drill is still the pending live proof (2026-07-28).

<details><summary>How we got here</summary>

**Claim (original wording):** A PE-build-key MATCH combined with a JobCommand rec8/rec9 landmark mismatch discriminates "another mod rewrote the table" from "the game updated" (LW-112: on that shape the guard split arms the mod and stands down only the weapon-command lane)

**Mechanism:** every known game build changed BOTH PE key fields (4 builds on record, LaunchGuard.Landmarks.cs provenance block); the rec8/rec9 content is FILE-BAKED image data (AnchorScan tier-1 Proven row) that only runtime writers change; whole-row table writeback is the proven rewrite mechanism (LW-77 Proven row), so a matching exe carrying foreign table bytes has exactly one known cause. Implementation: main guard [pe-build-key, ramza-roster-row] full stand-down; kit-lane guard [jobcommand-table] steps only after main arms, disables only the Barrage/ShadowBlade/Provoke grants (LaunchGuard.KitLane.cs)

**Evidence:** desk analysis 2026-07-28 + the 2026-07-21 player field report (CustomJOB_ITEM alongside this mod popped the stand-down box naming landmark 2 while the game had not updated); the pre-registered two-leg conflict-mod drill (docs/DEV_TEST_RECIPES.md, LW-112 section) is the owner's pending live proof

**Date:** 2026-07-28

</details>

### [per-turn-normalize-repaints-boosted-baseline] Engine per-turn normalize re-paints a stat baseline INCLUDING a mod boost active at battle open (LW-90)

Uncertain claim that the game snapshots stats at battle open with a mod boost baked in, so every later normalize restores the boosted baseline unless the mod re-corrects it; evidenced by log lines and owner eyewitness on 2026-07-21, when the LastTarget corrective prong shipped.

<details><summary>How we got here</summary>

**Claim (original wording):** Engine per-turn normalize re-paints a unit's stat baseline INCLUDING a mod boost that was active at battle open (LW-90)

**Mechanism:** the game snapshots unit stats at/near battle open, while Iai's hold is already armed (~100ms after enter); every later normalize restores that boosted baseline, so a released opening boost RETURNS unless the mod re-corrects it. The Iai post-release corrective now watches both the ledger's restart residue and the hold's own last written target

**Evidence:** `livingweapon.log` 2026-07-21 11:03:30.295 and 11:04:08.235 `iai: restart residue re-corrected post-release (13 -> 11)` (the corrective catching the re-paint twice in the restarted battle), plus the owner's eyewitness in the SAME session's fresh battles (11:04-11:08): Speed rode the boost post-release where the residue-only corrective was blind; the LastTarget prong shipped on that evidence

**Date:** 2026-07-21

</details>

### [slot0-battle-phase-values] 1.5 slot0 battle phases: 0xFFFFFFFF at load churn, 0x10 at real enters, marker sticks post-victory so exits ride the debounce (LW-42)

Uncertain: slot0 reads 0xFFFFFFFF during battle-load churn and 0x10 at real enters, and post-victory the marker stayed armed so battles end via the no-excuse debounce rather than marker clearance; values are edge samples with 0x10 persistence through a >4s mode-1/5 stretch and the post-QUIT value still open (2026-07-21).

<details><summary>How we got here</summary>

**Claim (original wording):** 1.5 slot0 battle phases: 0xFFFFFFFF at battle-load churn edges, 0x10 at real enters (modes 2 and 3 observed), post-victory sampled BOTH 0x11 (07:28) and 0x10 still armed ~6s after mode fell to 0 (09:06), so battles end by the no-excuse debounce, not by marker clearance (LW-42)

**Mechanism:** u32 at `Offsets.Slot0`; `BattleState.PairArmed` and `InLiveBattle`'s mode-1/5 excuse re-anchored on 0x10 (`Offsets.Slot0InBattleMarker`). Values are EDGE SAMPLES; persistence claims between samples are inferences. The 07:24:14 enter plus 07:24:22 false exit (mode 1, ~7.6s later) IS the dead-excuse bug firing pre-live. The 09:06:49 exit is the stuck-marker guard working live POST-fix: marker armed, mode 0 for 6s, unpaused, event 0xFFFF, exit fired on schedule (the quit-stick defensive shape, exercised by a victory)

**Evidence:** `livingweapon.log` 2026-07-21 edge lines 07:24:14.801 / 07:24:22.397 / 07:24:36.059 / 07:28:17.215 / 09:03:49-54 (churn) / 09:04:06.790 / 09:06:49.673, plus flight_20260721_090649 mode records (last flip 2 to 0 at exit minus 6.0s; max mid-battle mode-5 stretch 3.61s, mode-0 dips to 11.19s suspended, no mid-battle exit). OPEN: 0x10 persistence through a GREATER-than-4s mode-1/5 stretch (the max observed was 3.61s, so the decisive slow-cast moment has not yet occurred; owner eyeball, TODO LW-42 Verify) and the post-QUIT value

**Date:** 2026-07-21

</details>

### [attack-card-reverts-hover-follower] Attack-card vanilla reverts are the HOVER-FOLLOWER, not the mirror seat; the mirror never created resolve ambiguity (LW-87 phase 0)

Uncertain: attack-card reverts during a player's own turn come from the cursor struct tracking the status-viewed unit (the LW-55 gates then correctly refuse), while the revolving mirror seat never created resolve ambiguity; supported by an owner live repro and an 8879-tick probe census on 2026-07-21.

<details><summary>How we got here</summary>

**Claim (original wording):** Attack-card vanilla reverts during a player's own turn are the HOVER-FOLLOWER, not the mirror seat: the condensed cursor struct tracks whichever unit is being viewed (the T-status detour), the LW-55 gates then correctly refuse, and the row composes vanilla until Attack is re-selected; the revolving mirror never created resolve ambiguity (LW-87 phase 0)

**Mechanism:** TurnQueue (level,hp,maxHp) follows the status-viewed unit while TqTeam stays turn-stable (the detour produced GATE refusals, which require team==0, never NOT-PLAYER-TURN); replaying the shipped cursor resolve outside the process gave verdict flips OK -> GATE-NotTurnOwner (slot 25, tflag 0) on the detour and back to OK on re-selecting Attack; mirror seat 28 revolved (~1s cadence) cloning nameId 1 then 271, EVERY sample at (0,0) with t=0 and full-duplicate fields, twin-filtered out on every tick; zero AMBIGUOUS verdicts and zero multi-t1 dup groups across 8879 ticks; NO-MATCH appeared only in battle-load churn. Consequence: the mirror-ambiguity fix candidate is dead; the cure is anchoring the card resolve on Band.FlagOwner (the LW-63 lane), whose roster bridge is already the player filter

**Evidence:** owner live repro 2026-07-21 12:27-12:28 (attack mode, hover, T status view, back out: row reverts to vanilla Attack; re-select Attack: row repaints) + tools/probes/cursor_resolve_probe.py, a 540s run whose PRINTED verdict-change lines span 12:26:30-12:28:25 (the probe prints only changes, so a line marks the START of a state, never a one-tick blip; the run's own end-of-run summary is the tick census: OK 8009 / GATE-NotTurnOwner 457 / NO-MATCH 223 / NOT-PLAYER-TURN 190 = 8879 ticks at the 60ms poll, D1 ambiguous-distinct-nameIds 0, D2 multi-t1-dup 0)

**Date:** 2026-07-21

</details>

### [turn-flag-holds-through-status-detour] Acting unit's turn flag holds at 1 through a status-screen detour, validating the turn-flags owner as the Attack-card anchor (LW-87)

Uncertain: band +0x19C ATurnFlag stays 1 on the acting unit through a status-screen detour, so the flag owner is a valid Attack-card anchor where the old TurnQueue resolve refused; backed by a post-deploy two-column probe tape and owner eyewitness on 2026-07-21.

<details><summary>How we got here</summary>

**Claim (original wording):** The acting unit's turn flag HOLDS at 1 all the way through a status-screen detour, so the turn-flags owner is a valid anchor for the Attack card (LW-87, the premise the Phase 2 review demanded before implementation)

**Mechanism:** band `+0x19C` ATurnFlag stays 1 on the ACTING unit while the player views another unit's status with T (the condensed cursor struct meanwhile follows the VIEWED unit); the shipped LW-87 resolve therefore keeps composing the acting unit's dossier where the old TurnQueue-anchored resolve refused NotTurnOwner. Bonus win measured the same session: during action resolution the cursor struct's team field flickers to 1 (enemy) while the player's turn is genuinely still open and the actor pointer parks on the struck victim (the known dwell semantics), and the flag anchor rides straight through that too.

**Evidence:** post-deploy two-column probe tape 2026-07-21 14:02-14:06 (tools/probes/cursor_resolve_probe.py, replaying BOTH resolves per tick): detour ticks 14:02:43.905 / 14:02:45.741 / 14:02:49.229 / 14:02:59.441 read `GATE-NotTurnOwner slot=25 tflag=0` for the shipped resolve while `FLAG-OK slot=24 weapon=42` held on the acting unit; livingweapon.log shows the paint line 14:02:46.128 with NO revert until the enemy turn at 14:03:14 (28s of held paint spanning the detours), against the pre-fix 12:16-13:17 sessions where every detour reverted; owner eyewitness same session on BOTH ally and enemy status views; zero WARN lines all session. The run's end-of-run census is the headline number: 581 ticks where the shipped resolve refused and the flag anchor answered, and 0 in the other direction (FLAG-OK 1614 vs OK 1033; FLAG-BRIDGE-FAIL 804 on enemy turns, the player filter working)

**Date:** 2026-07-21

</details>

### [stale-paint-orphaned-copies] Attack-card stale paint was orphaned copies after transient evictions (LW-91 cluster)

Uncertain: stale Attack-card paint is attributed to orphaned catalog copies left painted after transient re-verify evictions, with address identity of re-found copies and the freed-memory-never-rendered assumption unverified; the shipped strike-retention fix rests only on the retention prong (2026-07-21).

<details><summary>How we got here</summary>

**Claim (original wording):** Attack-card stale paint was orphaned copies after transient evictions (LW-91 cluster)

**Mechanism:** the painter evicted a cached catalog copy on ANY single failed re-verify and went census-blind 21-33s; orphans kept painted bytes (04:41:12 dev log: 7-copy cache, 2 evicted, "repainted" x5 and "reverted" x5, session ended census-blind); the same copy COUNT was re-found after each completed window (7 to 7 twice), supporting transient-misread dominance. CAVEATS: address identity of re-found copies not proven (log lines carried no addresses; the LW-91 fix tapes the label address on each eviction going forward), and "freed memory is never rendered" is unverified (row 63's widget {namePtr,descPtr} note), residual bounded by one menu rebuild. The shipped fix (5136f2e strike retention) rests only on the retention prong, which is correct under every reading.

**Evidence:** deployed livingweapon.log 2026-07-21 04:35 launch + the 07-14 22:07 rescue log (log_rescue_20260715); recon banked in the LW-91 build plan

**Date:** 2026-07-21

</details>

### [status-system-three-layer-decode] Status system = 40 ids in three 5-byte MSB-first layers; apply engine and compose pass located; external pending-field writes consumed but IGNORED (LW-58)

Uncertain: the status system is 40 ids across innate/inflicted/composed 5-byte MSB-first per-unit layers with the apply engine and per-frame compose pass located, and three live tapes showed raw external writes to the pending field are consumed but never applied, closing every external-write lane to status infliction (2026-07-09).

<details><summary>How we got here</summary>

**Claim (original wording):** Status system = 40 ids in three 5-byte MSB-first per-unit layers; the apply engine and per-frame compose pass located; EXTERNAL writes to the pending field are consumed but IGNORED (LW-58)

**Mechanism:** Layers, band-entry-relative: innate `+0x3B..+0x3F`; inflicted `+0x1D3..+0x1D7` (the "+0x18E mirrors" are OR-sources, not copies); composed `+0x45..+0x49`, rebuilt every frame as inflicted OR innate by the compose loops at `0x150BD0990`/`0x150BF6936` (`dest[+0x0A] = src[+0x198] | src[0]`). Apply engine prologue `0x150BF66DC` (ecx = unit slot, edx = mode): iterates ids 0..39 MSB-first, tests a per-unit field at combat `+0x1DB + id/8` (= image `0x141853E9E + slot*0x200 + 0x1D`), conflict-scans, copies a timer table, ORs the inflicted layer (`0x150BF6818` fired at the pop), dispatches `call 0x1401FB064(id+1, ...)` (checker `0x140278D68`); special cases: ids 9 and 34 (Charm) merge team-color bits into combat `+0x05`/`+0x1EE`, id 19 clears combat `+0x2E`. Known ids from proven bits: 2 Dead, 3 Undead, 4 Charging, 15 Treasure/Crystal (the pop queues it: the deathdiff transient band `+0x1C0`=0x01), 18 Reraise, 24 Poison, 25 Regen, 26 Protect, 27 Shell, 28 Haste, 34 Charm, 38 Reflect, 39 Doom. NEGATIVE, 3 live tapes: raw external writes to the pending field are consumed on the engine's schedule (5 to 17s) but never applied (poison 24 on a living unit; treasure 15 on a living unit AND a fresh corpse), so the field is scratch/output and every external-write lane to status infliction is CLOSED; in-process cold-calls of the apply/dispatch functions are the remaining lever. Second pop variant observed: dead `+0x45` 0x20 -> 0x40 (crystal) vs 0x20 -> 0x00 with marker (chest). Related sweep negative: no struct anywhere in memory holds a pointer to a unit slot beyond turn-forecast/eval scratch (render binding is by index/identity, `findrender` x3).

**Evidence:** owner session 2026-07-09: CE what-writes on band `+0x46` and `+0x1D4`, `tools/probes/disasm.py` dumps at `0x150BD0938`/`0x150BF6767`/`0x150BF66DC`, three `spawn_probe.py inflict` tapes, three `findrender` sweeps

**Date:** 2026-07-09

</details>

### [cold-call-apply-engine-inflicts-status] In-process COLD-CALL of the apply engine inflicts a status, but the treasure MODEL conversion is a turn-gated event the bit does not trigger (LW-58)

Uncertain: cold-calling the apply engine from the mod loop thread runs the real apply path and inflicts statuses (Haste applied with icon), but setting the treasure bit alone produced no chest model and a pathing crash, so model conversion is a turn-gated engine event, not a bit trigger (2026-07-09).

<details><summary>How we got here</summary>

**Claim (original wording):** In-process COLD-CALL of the apply engine INFLICTS a status (the lever the status-system row left open); but the treasure MODEL conversion is a turn-gated EVENT the status bit does not trigger (LW-58)

**Mechanism:** The remaining lever from the status-system row, proven live: write the pending-ADD bit synchronously, then cold-call `0x150BF66DC(engineSlot, mode)` (engineSlot = band seat - 8; `[UnmanagedFunctionPointer(Winapi)]`, MS x64 ecx/edx, ShowSpike idiom) from the mod loop thread. HASTE (id 28, mode 0): before composed 00 inflicted 00, after composed 08 inflicted 08 (the engine OR'd it into the inflicted layer, compose derived it), APPLIED, Haste icon rendered. TREASURE (id 15, modes 0 AND 1 both tested): SAME, before 00/00 -> after composed 01 inflicted 01. So the cold call runs the engine's real apply path where the ASYNC external write only got consumed-and-dropped: a proven status-infliction-on-command primitive (buffs/debuffs, engine-faithful). NEGATIVE for the spawn goal: the treasure bit applied but NO chest model rendered; the corpse lost its hearts, its status card bounced to Ramza, and an enemy pathing onto its tile CRASHED the game. So the treasure/crystal MODEL conversion is a turn-gated engine EVENT (crystal counter -> 0 on the unit's OWN turn, the despawn_probe crystallization path) that BUILDS the chest and sets the bit as one OUTPUT; setting the bit alone is a corrupt half-state, not a trigger. CONCLUSION: the apply engine is the status-effect lever, NOT the Body Double model-construction lever; model construction still needs the conversion/spawn EVENT routine (the plan's AddUnit/Draw endgame). Guards (all live-exercised): Mem.Readable + prologue-byte landmark on the target, IsCallableSeat range check, paused + inLive + foreground gates, newly-set-only APPLIED verdict.

**Evidence:** owner live 2026-07-09: `livingweapon.log` 23:27:08 (CANARY Haste APPLIED=True + icon) and 23:39:29 (TREASURE APPLIED=True, no model, tile-path crash); instrument `LivingWeapon/StatusSpike.cs` + `StatusApply.cs` + `StatusApplyTests.cs` on branch `feature/body-double-spawn` (dev-only, `#if LWDEV`, F2/F4)

**Date:** 2026-07-09

</details>

### [treasure-pop-state-flip-revert] Unit-to-treasure pop is a decoded in-slot state flip; REVERT re-enrolls the LOGIC but the field model stays welded and SpriteSet never changes (LW-58)

Uncertain: the unit-to-treasure pop is a decoded one-frame in-slot state flip whose revert re-enrolls the unit's logic (timeline, hearts, Phoenix Down revival) while the chest model stays welded (SpriteSet never edges) and the unit's own turn soft-locks the battle; observed in owner sessions 2026-07-09.

<details><summary>How we got here</summary>

**Claim (original wording):** Unit-to-treasure pop is a decoded in-slot state flip; REVERT re-enrolls the LOGIC (timeline, hearts, revival) but the field model stays welded, and the SpriteSet byte never changes (LW-58)

**Mechanism:** pop signature, one 100ms frame (`spawn_probe.py deathdiff` s15): dead `+0x45` bit 0x20 CLEARS (a crystal-suspect read 0x40 in the earlier watch tape), conversion marker band `+0x46` 0->1, crystal counter combat `+0x07` resets to 3, slam CT `+0x41` zeroes, mirror pair `+0x1D3/+0x1D4` (primaries +0x18E) tracks both, transients `+0x1C0`=1/`+0x1C9`=8 self-clear ~26s; combat `+0x00` (SpriteSet) NEVER edges, so the chest model is scene-graph-side (nothing in-slot to swap back). Writing the pre-pop bytes back (`spawn_probe.py revert`: marker+mirror 0, dead bit+mirror set, counter 3) re-enters the unit in the Combat Timeline, renders hearts over the chest, and a Phoenix Down REVIVES it (health bar over the chest); the model never re-materializes and the unit's own turn SOFT-LOCKS the battle. The pop is a repeatable mid-battle engine model-construction event: CE what-writes on `+0x46` at a pop is the entry to the conversion/construction routine (the AddUnit/Draw-equivalent spawn-primitive hunt; `spawn_probe.py addr` prints absolute addresses). Falsified along the way: the PSX Battle-Stats 0x0183 existence-enum placements (band `+0x199`/`+0x167`) and the ENTD-echo extrapolations (band `+0x172`/`+0x15B`) stayed flat through 4 deaths, an undead die-and-rise, and two pops. Untested variant: frog-cast on the reverted+revived unit before its own turn (Frog rebuild is a proven model-rebuild event; a frog can act).

**Evidence:** owner sessions 2026-07-09: watch tape (4 deaths, undead rise, 2 pops), deathdiff s15 chest pop (all edges in one frame), revert s15 eyewitness (hearts + timeline portrait + Phoenix-Down revive + soft-lock on its turn); `tools/probes/spawn_probe.py` findings block; plan.md (LW-58)

**Date:** 2026-07-09

</details>

### [callout-bubble-custom-text-piggyback] Callout bubble with CUSTOM TEXT via injected call, piggyback timing; arbitrary-time fire still OPEN

Uncertain: custom text renders in the game's callout bubble by firing the callout wrapper piggybacked inside an orchestrator-entry detour on the game-logic thread, but firing at an arbitrary time is still open (needs a logic-thread per-frame portal); owner-eyewitnessed 2026-07-02.

<details><summary>How we got here</summary>

**Claim (original wording):** **Callout bubble with CUSTOM TEXT via injected call -- PIGGYBACK timing (arbitrary-time fire still OPEN: needs a logic-thread per-frame portal)**

**Mechanism:** fire the game's callout wrapper `0x1400E8A84(junk, tokenBuf, 1)` on the game-LOGIC thread, piggybacked inside an orchestrator-entry detour (`0x140111D20`) right after a natural call returns, then immediately `SetTextStringAndCommit 0x14028F720(holder=ctrl+0x58, char*)` -- the full native ritual (widget rebind, flush, show `0x140409A00`, named-track arm, kick) renders OUR text. CONTEXT IS THE GATE (job/worker-pool engine, 130 threads; the orchestrator ran on a different tid every session): the ritual paints only when it runs INSIDE the action/callout job -- identical fires from a different job (render-thread portal `0x140409A88`, or a planted per-frame pump callback `0x14114B738` on a sibling worker) complete the whole ritual with byte-identical post-state and draw NOTHING. Piggyback owns that context for free. Type `ctrl+0x50` is caller-stamped request state (stamp before firing; 0 walks off both switch ladders = silent no-op). Token stream = template string-table id at `+0xC` + lstr/lnum binding tokens; controller resolves from statics `[[[0x143CD9DA8]+0x10]+0x48]+0x58` (no heap scan). Natural callouts (incl. plain attacks, id 0x800F streams) fire the orchestrator constantly = frequent piggyback portal

**Evidence:** ShowSpike v4-v7 ladder 2026-07-02 evening: 21:08 chase-only run rendered the raw 0x508C template via OUR injected call (natural call unpatched; screenshot); 21:36 ritual+Commit rendered "BUBBA LUVS THE ORCHESTRATOR" -- Patrick eyewitness both; `livingweapon.log` 21:36:19

**Date:** 2026-07-02

</details>

### [ct-array-address-stale-152] 2026-06-16 Combat Timeline ARRAY address does not hold on build 1.5.2 (recommend moving to Contradicted; owner call)

The old Combat Timeline array address 0x140d3a04c no longer matches any live unit on game build 1.5.2, and the row recommends moving the June array claim to Contradicted (owner call); the clock model row supplies the same answer so the array is no longer needed. Status Uncertain, most recent date 2026-07-27.

<details><summary>How we got here</summary>

**Claim (original wording):** The 2026-06-16 Combat Timeline ARRAY address does not hold on game build 1.5.2 (recommend moving that row to Contradicted; owner call)

**Mechanism:** Reading `0x140d3a04c` as 4 byte records returns a repeating `02 00 00 00 / a4 7f cf 40` pattern with zero records matching any live unit's (CT, tile) pair, where the June model predicts one record per unit carrying exactly those two bytes. Not a blocker for anything: the clock model row above supplies the same answer from fields already in `Offsets.cs`, so the array is no longer needed and nobody should spend a session hunting it. The original row was written with a tilde on its own address and predates both the 1.5.1 and 1.5.2 re-anchors

**Evidence:** owner live 2026-07-27, mid battle with real CT spread: `tools/probes/turn_queue_probe.py dump 24` reported 0 of 24 records matching, twice (once pre battle with every CT at 0, once mid battle with CT 12/80/96/96/104)

**Date:** 2026-07-27

</details>

### [ct-scheduler-adopts-band-slot] CT scheduler ADOPTS a hand-written band slot

A full unit cloned into an empty player-range band slot gets inserted into the turn order and becomes CT-eligible even with no graphic; the claim is Uncertain, observed live 2026-06-16.

<details><summary>How we got here</summary>

**Claim (original wording):** CT scheduler ADOPTS a hand-written band slot

**Mechanism:** a full unit cloned into an EMPTY band slot in the player range gets inserted into the turn order (Combat Timeline entry), CT-eligible — even with no graphic

**Evidence:** `probes/clone_probe.py clone/enrolldiff`; a timeline entry appeared live for the injected unit

**Date:** 2026-06-16

</details>

### [injectable-band-range-16-27] Player-injectable band range is seats ~16-27

Clones injected below band seat 16 do not enroll while seats 16-27 do, with 27 the last usable seat (about a 28-unit array cap); the claim is Uncertain, from a slot sweep dated 2026-06-16.

<details><summary>How we got here</summary>

**Claim (original wording):** Player-injectable band range is seats ~16–27

**Mechanism:** clones below seat 16 don't enroll; 16–27 do; 27 is the last (≈28-unit array cap)

**Evidence:** Patrick's slot sweep

**Date:** 2026-06-16

</details>

### [ct-4byte-record-array] Combat Timeline is a 4-byte-record array at ~0x140d3a04c

The Combat Timeline was observed as a 4-byte-record array at roughly 0x140d3a04c where injecting a unit inserts a head record (byte0 = CT, byte1 = a locator); the claim is Uncertain, dated 2026-06-16, and the later [ct-array-address-stale-152] entry reports the address no longer holds on build 1.5.2.

<details><summary>How we got here</summary>

**Claim (original wording):** Combat Timeline is a 4-byte-record array at ~`0x140d3a04c`

**Mechanism:** injecting a unit inserts a record at the head and shifts the rest down; record byte0 = CT (`0x64`=100), byte1 = a locator (matched the clone's gx)

**Evidence:** `enrolldiff --wide`, the visible insert-shift

**Date:** 2026-06-16

</details>

### [formation-fills-band-slot-in-place] Formation places a unit by filling a RESIDENT zeroed band slot IN PLACE

Formation writes the full ~0x200 unit recipe into a resident zeroed band slot in place, with no relocation or realloc, and the key fields are mapped; the claim is Uncertain, dated 2026-06-16.

<details><summary>How we got here</summary>

**Claim (original wording):** Formation places a unit by filling a RESIDENT zeroed band slot IN PLACE

**Mechanism:** no relocation/realloc; full ~0x200 recipe written from zero — fields mapped (weapon `+0x04`, PA/MA/Speed `+0x22/23/24`, init bytes inBattle `+0x12`, CT `+0x25`, `+0x1ce`=100)

**Evidence:** `probes/formation_diff.py` place_before→after + prebattle→inbattle diffs

**Date:** 2026-06-16

</details>

### [gear-loss-commits-post-battle] Broken/stolen gear commits to the PERSISTENT roster OUT of battle, not mid-battle

Broken or stolen gear only commits to the persistent roster after the battle (at the battleMode=0 reconcile), a QUIT reverts it entirely, and only a won battle commits; the claim is Uncertain, dated 2026-06-16.

<details><summary>How we got here</summary>

**Claim (original wording):** Broken/stolen gear commits to the PERSISTENT roster OUT of battle, not mid-battle

**Mechanism:** an enemy break empties the in-battle copy live, but the roster slot stays until `battleMode=0` (Ramza body `0x00b7→0x00ff` at the post-battle party-menu reconcile). Empty sentinel = `0x00FF`. A QUIT reverts it entirely → only a *won* battle commits. Unblocks Bait-n-Switch (snapshot-on-enter / restore-on-exit after the commit).

**Evidence:** `probes/roster_loss_trace.py` dual-watch

**Date:** 2026-06-16

</details>

### [crystallization-skips-stat-roster] Crystallization does NOT touch the stat-roster 0x1411A18D0

A crystallized unit's stat-roster slot at 0x1411A18D0 stays unchanged even though the unit is gone from Organize, so party membership lives in a separate, not-yet-located structure; the claim is Uncertain, dated 2026-06-16.

<details><summary>How we got here</summary>

**Claim (original wording):** Crystallization does NOT touch the stat-roster `0x1411A18D0`

**Mechanism:** a crystallized unit's stat slot is unchanged across 6.5 min incl. a 116 s out-of-battle window (every byte watched) yet the unit IS gone from the Organize screen → party membership lives in a SEPARATE structure (PSX-style), not this array. The post-battle roster-restore plan must target that structure (not yet located).

**Evidence:** `probes/roster_loss_trace.py` `[ROSTER*]` full-slot watch

**Date:** 2026-06-16

</details>

### [job-write-label-only-sprite-weld] Live combat-struct JOB write is LABEL-ONLY; BATTLEFIELD sprite also welded (but model-SELECT reads +0x00)

Writing the job byte live changes only the class name while command and moveset stay welded at construction; per the 2026-06-18 refinement the body is the separate SpriteSet byte +0x00, and a construction-time hook write does re-skin the battlefield model, so only the job/moveset weld stands. Status Uncertain, most recent date 2026-06-18.

<details><summary>How we got here</summary>

**Claim (original wording):** Live combat-struct JOB write is LABEL-ONLY; BATTLEFIELD sprite also welded (but the model-SELECT path reads `+0x00`)

**Mechanism:** writing combat `+0x03` (job) flips the class NAME but usable command + moveset are welded at construction. The learned-ability block `+0x98..+0x150` IS transplantable but cosmetic for the usable command. **REFINEMENT (2026-06-18):** the body is `+0x00` (SpriteSet), a SEPARATE byte from job -- a POST-construction live `+0x00` write re-derives only the STATUS-PAGE model preview (texture-gated), but a CONSTRUCTION-TIME hook write (the SkinSpike `CopyJobEffectsToUnit` hook) DOES re-skin the BATTLEFIELD model (textured, 2026-06-19 -- see that Proven row). So only the JOB/moveset weld stands; the field SPRITE is fully controllable at construction.

**Evidence:** live: Bomb->Thief (still a bomb, still Bomblet/Self-Destruct), Ramza Thief->Knight (label Knight, moveset still Steal); goblin +0x00->Ramza changed only the status preview, not the field

**Date:** 2026-06-18

</details>

### [agency-flag-shadow-0x1ee] Control/agency flag has a SHADOW copy at +0x1EE

The human/AI control flag appears to have a shadow copy at combat +0x1EE that another mod writes in lockstep with +0x05, implying a lone +0x05 write may revert; not yet probed by us, so the claim is Uncertain, dated 2026-06-18.

<details><summary>How we got here</summary>

**Claim (original wording):** Control/agency flag has a SHADOW copy at `+0x1EE`

**Mechanism:** Dicene's fftivc.unitcontrol writes the human/AI bit to BOTH combat `+0x05` AND `+0x1EE` in lockstep on every battle-entry -- implies a lone `+0x05` write may revert and the pair is what holds

**Evidence:** scout of dicene/fftivc.unitcontrol `Mod.cs`; not yet probed by us. Probe: write only +0x05, watch for snap-back vs the dual-write

**Date:** 2026-06-18

</details>

### [aarec-action-record-decode] Per-unit action record at frame +0x1A0 (Offsets.AArec) decodes to idx/abil/kind/xref

The per-unit action record at frame +0x1A0 is a valid per-unit action signal whose idx equals the unit's own seat-8-relative index, but the +0xB killer-xref hypothesis was REFUTED live and the record is not a kill-attribution shortcut; the +0x2 ability field is unverified for normal casts (the observed 441 is Auto-Potion, a reaction). Status Uncertain, most recent evidence date 2026-07-27 within a row dated 2026-07-01.

<details><summary>How we got here</summary>

**Claim (original wording):** Per-unit action record at frame `+0x1A0` (`Offsets.AArec` band-entry-relative `+0x184`) decodes to idx/abil/kind/xref

**Mechanism:** Sub-offsets relative to the record base: `+0x0` idx (engine index == that unit's own seat-8-relative index, SOLID); `+0x2` u16 ability id; `+0xA` kind tag (5 == performing / 6 == receiving, PROBABLE); `+0xB` candidate victim&lt;-&gt;attacker CROSS-REFERENCE (xref), UNPROVEN -- the diagnostic exists specifically to prove or refute this last field. Diagnostic-only (`KillTracker.CreditKill`'s `kill-diag` line, gated on `Tuning.VerboseEvents` via the `BattleLog` seam); the credit path never branches on it.

**Evidence:** `tools/probes/unitid_probe.py watch` all-seat capture, 17:57 session, 2026-07-01: player seats observed at idx 16/17/18 (== their own seat-8-relative index); enemy seats observed at idx 0/5/6; ability 441 recorded on both a performing (kind 5) and receiving (kind 6) frame in the same exchange (EVIDENCE CORRECTION 2026-07-27: 441 was recorded here as "a katana basic attack"; the pristine Ability-en table says 441 = Auto-Potion, a REACTION, sitting in an unbroken reaction block 435-453 -- which fits the both-frames observation and means NO deliberately cast ability has ever been observed stamping this record; treat cast-detection ideas built on `+0x2` as unverified for normal casts until one instrumented battle says otherwise); the `+0xB` xref field was observed matching the OTHER party's idx in one direction on one exchange and in the other direction on a second exchange -- one observation each way, not yet a pattern. Superseded the probe's earlier "global action counter" / "seq" comments (STALE, corrected in this same change). **LIVE RESULT 2026-07-01 evening (two kill-spree battles, 7 `kill-diag` samples): the killer-xref hypothesis for `+0xB` is REFUTED** -- on corpse records the field reads the VICTIM'S OWN idx when kind=6 (3x), a 255 sentinel when kind=5 (2x), and the record was already CLEARED (all zeros) at corpse-credit read time twice (the two-phase clear can outrun the credit) -- it never named the killer. The `idx == own seat-8` half was RE-CONFIRMED on every readable corpse (idx 11 == slot 19, idx 3, idx 2, idx 1, idx 0 == slot 8 -- all consistent). The record remains a valid per-unit ACTION signal (kind-5 stamps on the actor's own frame); it is NOT a kill-attribution shortcut. Do not rebuild the corpse-names-its-killer idea on this byte without NEW evidence.

**Date:** 2026-07-01

</details>

### [battle-menu-greyout-hold] Battle-menu un-grey: the UI GrayOut byte is NOT the gate; the combat-struct acted-state hold works (minimal-byte bisect open)

The UI GrayOut byte was resolved as NOT the visual gate and the UI grey is unholdable (dead end); the real gate is the active unit's combat-struct acted state, where holding 10 candidate bytes un-grays Abilities and makes it usable (multi-act live-confirmed) but drags facing along, so the minimal acted byte bisect is still open. Status Uncertain; most recent date 2026-06-23.

<details><summary>How we got here</summary>

**Claim (original wording):** Battle command-menu GREY-OUT flags are in-process holdable (keep ABILITIES usable)

**Mechanism:** each command-menu widget is a `0x170`-byte record in a launch-stable HEAP arena (documented base `0x436BFDE4F0`): a disabled-overlay FLAG byte at record `+0x1C`, a u64 NAME pointer at `+0x20` ("GrayOut"/"RadioButtonBattleMenu"/"CommandBg"/"TextTitle"). The engine RE-DERIVES the flag every ~16ms render frame, so a hold must out-pace it: the 33ms engine tick LOSES the race (live 2026-06-23: the in-mod hold logged ACTIVE with 3 GrayOut found + anchor validated, yet CE still read the bytes `1`), exactly like a CE freeze (`0x436BFDE7EC=0` reverted to `1` ~0.5s later). Fix = a dedicated **~8ms FastHold thread** (`BattleMenuGrey.StartFastHold`, `Tuning.BattleMenuFastHoldMs`), the same pattern that beat the Treasure-mark running-water wipe. Shipped behind `Config.BattleMenuUngray` (default OFF); per-record guarded scan, name-validates the arena (anchor) each ~200ms, holds every GrayOut at 0. Menu-shown gate = enum `0x140C6B1CC`==4 + `BattleMode` {2,3,4}. Provenance + reproduction: `docs/research/UI_GREY_HOLD.md`.

**Evidence:** live 2026-06-23: arena found + survived a crash+relaunch with identical values + name pointers; CE revert watched; the 33ms hold proven to lose the race in play. **LIVE RESULT 2026-06-23 (CONTRADICTS the un-grey premise):** the ~8ms FastHold WINS the write race -- CE shows the held byte fluctuating to `0` (engine writes 1@~16ms, we write 0@8ms; 0 lands frequently) -- BUT holding the GrayOut record's `+0x1C` byte at `0` does NOT un-grey ABILITIES on screen (greyed at both `0` and `1`). So that byte is NOT the visual gate (the located GrayOut flags were `0x436BFDE50C`/`0x436BFDF08C`/`0x436BFDFA9C`; none drives the Abilities grey). The hold MECHANISM (locate-by-name + menu gate + frame-winning 8ms re-stamp) is proven; the TARGET byte is wrong. **RESOLVED 2026-06-23 — the UI grey is UNHOLDABLE (dead end).** Forcing ALL 123 UI-arena bytes that correlate with the grey did NOTHING (Enter still blocked); the grey (look + input) is engine-derived from the unit's acted state every frame, NOT a holdable UI byte. The REAL gate is the active unit's COMBAT-STRUCT "acted" state: a same-unit preact/postact diff (`0x141850000–0x141862000`, near `CombatAnchor`) gave 12 candidates; holding 10 at pre-act values (flags `0x141855E9B`/`0x14185609E`→1, action-record cluster `0x141856080..0A0`→0) un-grays Abilities AND makes it USABLE — **multi-act live-confirmed**. BUG: the cluster includes facing → the attacker swings the wrong way; bisect to the minimal acted byte(s) + resolve per-unit offset. Clean alt: the proven ExtraTurn CT-slam. The `BattleMenuGrey` UI-hold module built to chase this was a dead end and was SCRAPPED 2026-06-23 (findings preserved here). Full notes: `docs/research/UI_GREY_HOLD.md`, probe `tools/probes/menu_gate_diff.ps1`.

**Date:** 2026-06-23

</details>

### [id261-equips-displays-moonblade] Item id261 (past the 261 cap) EQUIPS + DISPLAYS as "Moonblade"

Item id261, past the 261 cap, equips and displays as "Moonblade" via 4 hookable gates (catalog relocation, weapon-stat thunk redirect, validity thunk redirect, clamp patch), none a Denuvo floor; live-verified 2026-06-26 and marked ready for PROVEN flip, but the row still sits in Uncertain.

<details><summary>How we got here</summary>

**Claim (original wording):** Item id261 (past the 261 cap) EQUIPS + DISPLAYS as "Moonblade"

**Mechanism:** 4 gates, none a Denuvo floor: catalog RELOCATION (disp32 `0x1402B8CDE`→8KB buf, native accessor, fixes the r11 managed-detour clobber) + weapon-stat thunk `0x1402B8CE8` 5-byte near-stub redirect (clone 261-511→67) + validity thunk `0x1402B8F30` near-stub redirect (clone→37, stops the 5-slot zeroing loop) + clamp `0x140284c82` 06→07

**Evidence:** LIVE 2026-06-26: force-equip 0x0105 to Gallant Knight Ramza rHand `0x1411A7D24` → equip screen shows "Moonblade", persists (no zeroing). FFTHandsFree `capbreak-equip` `capbreak_arm_equip`. Ready for PROVEN flip

**Date:** 2026-06-26

</details>

### [id261-functional-in-battle] Item id261 is a FUNCTIONAL weapon in battle (real damage)

Battle construction keeps id261 in the combat struct's CWeapon slot and the weapon-stat thunk (261→67) drives the damage formula, live-verified 2026-06-26 with a 323-damage basic attack; ready for PROVEN flip but still listed Uncertain.

<details><summary>How we got here</summary>

**Claim (original wording):** Item id261 is a FUNCTIONAL weapon in battle (real damage)

**Mechanism:** battle construction KEEPS 261 in combat struct CWeapon `+0x20` (`0x141855D00`=`0x0105`, vs AI `0x00FF`); the weapon-stat thunk (261→67) drives the damage formula

**Evidence:** LIVE 2026-06-26: Ramza basic attack = **323** = PA22 × Warbrand WP15. `probes/combat_scan.py`. Ready for PROVEN flip

**Date:** 2026-06-26

</details>

### [id261-model-construction-slot-swap] id261 battlefield weapon MODEL is redirectable via a CONSTRUCTION-time slot swap

The swing model bakes at unit construction from the equip-slot id, so presenting a real-model id at build time makes the unit swing it (menu identity and battle model can differ); live-verified 2026-06-26 and ready for PROVEN flip, currently Uncertain.

<details><summary>How we got here</summary>

**Claim (original wording):** id261 battlefield weapon MODEL is redirectable via a CONSTRUCTION-time slot swap

**Mechanism:** the swing model bakes at unit construction from the equip-slot id (NOT live -- only the status menu live-reads `CWeapon` via `0x140360383`). Present a real-model id at build time → the unit swings it. Menu identity vs battle model can differ (menu reads roster live; battle model is the baked value). Fix = a battle-entry swap loop (roster 261→37 for the build, →261 in menus) OR hook the construction read. Also confirmed: the MENU/status model preview live-reads `CWeapon` (writing 67 showed Warbrand in the status screen).

**Evidence:** LIVE 2026-06-26: roster `0x1411A7D24`=37 → Ramza swung a visible Chaos Blade on the field; `0501`=261 → empty. Ready for PROVEN flip

**Date:** 2026-06-26

</details>

### [callout-bubble-show-flag-hijack] Battle callout bubble carries mod-supplied text via show-flag hijack

Polling the callout text-holder show flag and calling the game's SetTextStringAndCommit in-process on the rising edge renders mod-supplied text through the natural show path; eyewitness on-screen render and ~10 crash-free runs on 2026-07-02, status Uncertain.

<details><summary>How we got here</summary>

**Claim (original wording):** Battle callout bubble carries mod-supplied text via show-flag hijack

**Mechanism:** poll callout text-holder show flag (holder `+0x88`, holder = launch-stable `0x436B07D058` validated by vtable `0x140718278` + id `0x999`) every in-battle tick incl. battleMode-1 animation frames; on rising edge call the game's SetTextStringAndCommit (`0x14028F720`) in-process with our payload; the natural show path renders it.

**Evidence:** spike commit `b6a1ffe`; on-screen render of a custom payload during a Focus cast, eyewitness 2026-07-02; ~10 crash-free in-process call runs.

**Date:** 2026-07-02

</details>

### [new-game-no-battle-exit-edge] An in-session New Game never fires the battle exit edge (real events suspend the exit debounce, LW-56)

An in-session New Game never fires the battle exit edge: the new-game and prologue event ids (2, then 4, 5) are real events, real events suspend the exit debounce, so BattleState.In stays true from the previous battle into the Orbonne opener and no per-battle reset runs. Evidenced by a 2026-07-10 flight tape and log; status Uncertain.

<details><summary>How we got here</summary>

**Claim (original wording):** An in-session New Game never fires the battle exit edge: the new-game and prologue event ids (2, then 4, 5) are real events, and real events suspend the exit debounce, so BattleState.In stays true from the previous battle into the Orbonne opener and no per-battle reset runs (LW-56)

**Mechanism:** BattleState.cs IsRealEvent suspension (the accumulator guard)

**Evidence:** flight tape `flight_20260710_024710_battle-exit.jsonl` (one tape spans both battles, turns #1-#8 continuous) plus `livingweapon.prev.log` 02:42-02:47, 2026-07-10

**Date:** 2026-07-10

</details>

### [movement-destination-tile-write-lane] Movement is ORDERED, not performed: node +0x8C/+0x8D is a DESTINATION tile the engine's mover walks to

The destination-tile write lane is ALIVE but gated on the mover being active: rewriting node +0x8C/+0x8D while a unit walks is honoured (the unit shifted one further tile in flight), retiring the earlier three-round "dead method" verdict which only ever poked units at rest. Open questions remain (chaining destinations, animate vs snap, starting the mover from rest); status Uncertain, most recent date 2026-07-22.

<details><summary>How we got here</summary>

**Claim (original wording):** **Movement is ORDERED, not performed: node `+0x8C/+0x8D` is a DESTINATION tile the engine's mover then walks to (knockback and ordinary walking use the same machinery)**

**Mechanism:** the node's tile block is two triples, not one: `+0x88/+0x89/+0x8A` is the CURRENT tile (committed at the END of each step) and `+0x8C/+0x8D/+0x8E` is the DESTINATION. Write the destination and the engine lerps the world transform (`+0x4C/+0x4E/+0x50`) and commits current + the combat logic tile itself. `+0x8B` is a step/phase counter (0 at rest) and node `+0x12` is a movement MODE that reads 0x04 at rest, 0x0D while walking and 0x1A during a knockback, so the shove is a distinct mode of the same system. WRITE SIDE, THREE ROUNDS (2026-07-22, owner live), and the first verdict was PREMATURE. Round 1, destination alone: INERT BUT STICKY, the written tile held the full sample and was never reverted, but the mover never ran. Round 2, the engine's own full order replayed in its own sequence (destination, then counter 0x07, then mode 0x1A last): ALL THREE WRITES STUCK and read back unchanged across 1.2s, and the unit still did not move. So these fields are not the mover's inputs; they are its bookkeeping, visible to us and not commanded by us. Same shape as the LW-58 pending-field wall. The READ half stands and is valuable (we can observe any move, and tell a shove from a walk by the mode byte); BUT rounds 1 and 2 replayed only THREE of the FOURTEEN fields the engine wrote in its order burst, which is not a fair test. The unwritten remainder includes node +0xA8, which goes 0 to 1 at the order and 1 to 0 exactly at completion: an is-moving flag, the same shape as the AI roster index that was the single byte standing between LW-58 and a working spawn. ROUND 3 RESULT: INERT, and decisively so. The entire fourteen field burst was replayed in tape order with the is-moving flag written last, and every value held unchanged for two full seconds while the unit did not move. The engine did not even RESET our writes, which is the informative part: at rest, nothing reads this node's movement block at all. VERDICT: writing the mover's state from outside is a DEAD METHOD, settled across three rounds at one, three and fourteen fields. The trigger is elsewhere. The READ half stands and keeps its value (any move is observable, and the mode byte distinguishes a shove from a walk). *** THE WRITE LANE IS ALIVE, GATED ON THE MOVER BEING ACTIVE (owner live 2026-07-22, hijack). *** Rewriting the destination WHILE a unit walks IS honoured: the owner's target completed its normal walk to the engine's chosen tile and then shifted one further tile, to the value we wrote in flight. So the earlier three rounds were correct and useless: nothing reads this block AT REST, and the mover reads it while running. Applied at or after step completion rather than redirecting the step in progress. This reopens engine-driven movement as a write lane and retires the "dead method" verdict recorded above, which stands as a reminder that three negatives at the wrong moment are not a wall. OPEN: whether successive destinations can be chained to drive a unit anywhere, whether the shift animates or snaps, and whether the mover can be started from rest at all (every observation so far rides a move the ENGINE began). Prior text kept for provenance:  every round poked a unit AT REST, and the mover is only alive during a walk, which is the one window in which the destination is guaranteed to be read. knockback_probe.py hijack tests exactly that (poll for a move starting, then rewrite the destination in flight). Also confirmed the same session: an ENEMY warped onto unreachable terrain simply ends its turn, which makes the perch a SOFTLOCK hazard for enemies (unreachable, unkillable, and the victory check still counts them). UNTRIED, in cost order: a global differential diff during a real move to hunt an enrollment list (the shape that cracked LW-58, and the only remaining probe-scale idea); the composed imitation which already works (knockback_probe.py shove); the Dash family formula table experiment; and an in-process call to whatever actually drives the mover, which belongs with the deep levers. Cleanup: a half-written order leaves the two triples disagreeing, which reads as mid-move to anything comparing them (knockback_probe.py clear resets it).

**Evidence:** owner live 2026-07-22, one tape (`knockback_watch_004828.jsonl`, ~250Hz change-only, `tools/probes/knockback_probe.py watch`). A Squire's Rush pushed a unit (9,4) to (8,4). Order at t=12.2468: mode `+0x12` 0x04 to 0x1A, counter `+0x8B` 0 to 7, dest X `+0x8C` 0x09 to 0x08. The world slide began 18ms LATER (t=12.2652) and the current tile `+0x88` plus combat `+0x4F` did not commit to 8 until t=12.3852, 138ms after the order. World X ran 0x0A to 0xEE = 28*9+14 to 28*8+14, the proven tile formula. The SAME lead-then-follow appears on an ordinary 3-tile walk earlier on the same tape (t=6.49 to 6.74): dest Y `+0x8D` steps 1,2,3,4 and current Y `+0x89` follows each time about 84ms behind, with mode 0x0D and counter 5/6.

**Date:** 2026-07-22

</details>

### [ct-hold-zero-turn-denial] Holding the CT byte at zero DENIES a unit its turn (the inverse of the proven CT slam)

Holding combat +0x41 (band +0x25) at zero on a ~10Hz loop denies the unit its turn (skipped in the turn order) and releasing lets CT re-accrue from zero, composing with the animation-page freeze into a complete Stop effect; settled by an owner live run 2026-07-22, status Uncertain.

<details><summary>How we got here</summary>

**Claim (original wording):** **Holding the CT byte at zero DENIES a unit its turn (the inverse of the proven CT slam)**

**Mechanism:** combat `+0x41` (band `+0x25`) held at 0 on a ~10Hz loop; the unit accrues no charge, so the scheduler never reaches it and it is skipped in the turn order. Releasing the hold lets CT re-accrue FROM ZERO, which is authentic Stop economics (the accrued charge is genuinely lost). Composes with animation page 0x00 (the camera-facing freeze) into a complete Stop effect from two writes, no new engine work.

**Evidence:** owner live 2026-07-22 (LW-115), `battle_toolbag.py bench 17 25` with the menu CLOSED so the clock ran: the held unit's next turn never came and it visibly disappeared from the turn order, then returned normally after release. Two earlier runs confirmed the frozen pose but sat on an open menu, so the clock was frozen and the turn-order half could not be read; this run is the one that settles it.

**Date:** 2026-07-22

</details>

### [float-status-vs-levitate-job-ability] Float-the-STATUS and Levitate-the-JOB-ABILITY are different systems; the status bit is cosmetic

Clearing status id 17 (Float) across composed, inflicted and innate removes only the icon on a unit whose job grants Levitate; the hover comes from the job's innate movement ability, so grounding a flier needs the job's ability set, not the status layer. Owner live 2026-07-22; status Uncertain.

<details><summary>How we got here</summary>

**Claim (original wording):** **Float-the-STATUS and Levitate-the-JOB-ABILITY are different systems, which is why the status bit is cosmetic**

**Mechanism:** clearing status id 17 (band `+0x47` bit 0x40) across composed, inflicted AND innate removes the Float ICON and nothing else on a unit whose JOB grants Levitate: it keeps floating, because the hover comes from the job's innate movement ability (shown in the unit's job panel next to its other innates) and/or the render node Z, not from the status layer. This explains the standing oddity that SETTING Float on a normal unit only ever produced an icon. To actually ground a flier you must reach the job's ability set (RSM/job data), not its statuses.

**Evidence:** owner live 2026-07-22 on an Exploder (Bomb family, slot 3, worldZ -48): status cleared, icon gone, unit still airborne, and its job panel lists Levitate as an innate alongside Cannot Enter Water and Counter. `battle_toolbag.py status <slot> float off --all`.

**Date:** 2026-07-22

</details>

### [rsm-movement-field-derived-copy] The per-unit RSM movement field is a derived COPY: clearing it does not remove an innate job ability

Uncertain as of 2026-07-22: combat +0x9C reads/writes a unit's movement abilities, but zeroing it on a monster with an innate movement ability changes nothing; for innates the authoritative source is job data, and the field likely carries equipped R/S/M only.

<details><summary>How we got here</summary>

**Claim (original wording):** **The per-unit RSM movement field is a derived COPY: clearing it does not remove an innate job ability**

**Mechanism:** combat `+0x9C` (3 bytes, base id 230) reads a unit's movement abilities and is writable (Rapture parks its teleport image there), but zeroing it on a monster with an innate movement ability changes NOTHING: the unit keeps the behaviour, the job panel keeps listing the ability, and the field is not re-derived either (it stays zero). So for INNATE abilities this field is a copy that nothing consumes; the authoritative source is job data, the same place the JobCommand records live (the Barrage lane). Likely model: the per-unit field carries EQUIPPED R/S/M for units that equip them, while monster innates are applied from the job.

**Evidence:** owner live 2026-07-22 on an Exploder (Bomb family, slot 3): movement read `00 10 08` = ids 241 and 250, cleared to `00 00 00`, and across a full turn the unit kept floating, the panel kept showing Levitate, and a re-read still returned zeros. Its reaction field's `08` matches the Counter the same panel advertises, so the decoder is sound and the field simply is not load-bearing here. `battle_toolbag.py rsm`.

**Date:** 2026-07-22

</details>

### [raw-status-bit-flag-vs-effect] A raw status bit sets the FLAG; the engine's apply routine performs the EFFECT

Uncertain as of 2026-07-22: writing a status bit directly gives the icon and menu entry but not the engine's apply-time work; the split is per status (flags the AI reads each tick work raw, one-time apply effects like Frog's model swap do not).

<details><summary>How we got here</summary>

**Claim (original wording):** **A raw status bit sets the FLAG; the engine's apply routine performs the EFFECT**

**Mechanism:** writing a status bit directly produces the icon and the status-menu representation, but not the work the engine does when IT applies the same status. Frog is the clean demonstration: the bit gives a frog icon and a frog in the status menu while the battlefield model stays unchanged, because the model rebuild is a separate step of the apply path. Float is the same shape (icon, no hover, because the hover comes from elsewhere). Berserk by contrast is fully behavioural from the bit alone, so the split is per status: flags whose meaning the AI reads each tick work raw; effects requiring a one-time action at apply time do not. This is the concrete argument for the in-process apply-engine cold call `0x150BF66DC`, and the same lesson as the movement mover and the animation register: drive the engine, do not write its data.

**Evidence:** owner live 2026-07-22 on an Exploder: frog on gave icon plus menu with an unchanged map model; berserk on produced the full mindless-charge behaviour. `battle_toolbag.py status`.

**Date:** 2026-07-22

</details>

### [status-map-bit-math-icon-sweep] The status map's bit math is CORRECT: 19 of 23 never-exercised bits rendered their own icon on demand

Uncertain as of 2026-07-22: 19 of 23 mapped status bits rendered their own icon on a live unit (4 produced nothing), but the sweep ran with the clock frozen, so it proves the engine recognises each id, not what each id does; behaviour needs a second pass with the clock running.

<details><summary>How we got here</summary>

**Claim (original wording):** **The status map's bit math is CORRECT: 19 of 23 never-exercised bits rendered their own icon on demand**

**Mechanism:** setting each mapped bit (composed + inflicted) on a live unit produced that status's icon: petrify, blind, confuse, silence, vampire, oil, berserk, chicken, protect, shell, slow, stop, faith, innocent, sleep, dontmove, dontact, reflect, plus `wall` which rendered an icon the owner had never seen and which carries NO text description in the status menu. Four produced nothing at all (defending, performing, cursed, critical), which is either a no-icon status or a wrong bit and is not yet distinguished. METHOD CAVEAT THAT MATTERS MORE THAN THE RESULT: the sweep ran with the clock frozen (owner on an open menu), so NO unit took a turn and no behaviour could manifest; berserk, independently observed producing a full mindless charge the same evening, shows only "icon" in this pass. So this catalog proves the ENGINE RECOGNISES each id, not what each id does. Behaviour needs a second pass with the clock running. Consistent with the flag-versus-effect rule: petrify gave a stone icon and no petrification.

**Evidence:** owner live 2026-07-22 on a Goblin, 23 statuses in one sitting, `battle_toolbag.py sweep`, results in tools/probes/status_catalog.jsonl

**Date:** 2026-07-22

</details>

### [status-dispatch-callable-eleven-inert] The status dispatch is RESOLVABLE and SAFELY CALLABLE from a fixed address, but eleven fires applied nothing

Uncertain as of 2026-07-22: the fixed-image thunk 0x1401FB064 resolves the live status-dispatch routine every launch and cold calls are safe and repeatable, but eleven calls across every tried argument combination applied nothing; the argument dialect (and even the premise about the function's purpose) is unresolved.

<details><summary>How we got here</summary>

**Claim (original wording):** **The status dispatch is RESOLVABLE and SAFELY CALLABLE from a fixed address, but eleven fires applied nothing**

**Mechanism:** the v1 pinned apply engine `0x150BF66DC` is DEAD on this build (a live peek found unrelated code there). The fixed-image thunk `0x1401FB064` is `E9 rel32` and resolves to the live routine every launch (measured `0x14D39FFC0`, prologue verified before each call), which is the durable fix: no constant to go stale. Decode of the target: `sub rsp,0x28` / `movsxd r10,ecx` (arg1) / `mov r11d,edx` (arg2) / `movzx ecx,r8w` (arg3, SIXTEEN BITS) / `call 0x140260ABC` (a lookup in the FIXED image returning a pointer) / `test rax,rax` / `je +0x2F2` (NULL ABORTS THE WHOLE ROUTINE). Later: a global flag test `cmp dword [rip+...],0`, `cmp r10d,0x12` twice, `test r11d,r11d` then `cmp r11d,1` (arg2 behaves like a small mode), `mov rax,[rax+0x148]` and `cmp byte [rax+4],0` with more bail branches. RESULT: eleven cold calls, order (id+1, mode, subj), every combination of mode 0/1/2 with subj = engineSlot / band seat / nameId, ALL APPLIED=False, no crash, no corruption, and the pending byte never consumed. So the call is safe and repeatable and we are not yet speaking its dialect. UNTESTED, in order: the other four argument permutations; the state of that global flag; the deeper bail branches; and the premise itself, since the v1 header's claim that this dispatch means (id+1, mode, slot) was always a guess and may simply be wrong about the function's purpose.

**Evidence:** owner live 2026-07-22, `battle_toolbag.py engine`, `apply_engine_find.py dump`, spike log lines timestamped 02:30-02:45

**Date:** 2026-07-22

</details>

### [composed-status-write-orphan-flag] A raw COMPOSED status write is an ORPHAN FLAG: icon renders and other systems read it, but no effect is performed and it NEVER expires (the INFLICTED layer is the engine's registry)

Uncertain as of 2026-07-22: a composed-only status bit is an orphan flag that never expires and clears only when the unit is HIT (correcting four in-tree claims that acting breaks it); statuses whose effect is other systems reading the flag work raw, while one-time engine actions need the apply routine.

<details><summary>How we got here</summary>

**Claim (original wording):** **A raw COMPOSED status write is an ORPHAN FLAG: icon renders and other systems read it, but no effect is performed and it NEVER expires (the INFLICTED layer is the engine's registry)**

**Mechanism:** three per-unit layers, band-relative: innate `+0x3B..+0x3F`, inflicted `+0x1D3..+0x1D7`, composed `+0x45..+0x49`. A status the ENGINE applies is registered in INFLICTED; composed is the read-out the HUD icon and other systems (including AI targeting) consult. Write composed alone and nothing owns the flag, so no visible effect is performed and nothing expires it. SHARPENED RULE: if a status's effect is another system READING the flag (AI targeting, AI behaviour) a raw composed bit suffices (invisible, berserk); if the effect is a one-time engine ACTION (swap a model, petrify, freeze a clock) it needs the apply routine (frog, petrify, float)

**Evidence:** ONE unit carrying TWO statuses, one ours and one the game's, same session, mod OFF, 2026-07-22, Ladd at band slot 18. OURS: composed `+0x47` = `0x10` with inflicted `+0x1D5` = `0x00`; the icon rendered over his head, he stayed SOLID with no transparency, the bit survived his own ATTACK unchanged, survived 60s of running clock with no re-stamp (`awatch` flat at `0x10`), and cleared only when friendly AoE splash HIT him. THE ENGINE'S: Stop, applied by Agrias's area attack on the same unit, read composed `+0x48` = `0x02` AND inflicted `+0x1D6` = `0x02`, with a real effect (CT frozen, no parry, no reactions) and a real expiry (the status card says it wears off with time). The icon tracked our bit faithfully both on and off, so it is a reliable read-out and not a cached widget. CORRECTS FOUR IN-TREE CLAIMS that the bit breaks the moment the unit ACTS (`Offsets.cs` AInvisible block, `tools/probes/status_map.py` invisible hazard, `tools/probes/taunt_probe.py` re-stamp comment, `FeignDeath.cs` docstring): the real trigger is BEING HIT. CONSEQUENCES: a held hide still needs a re-stamp, for splash rather than for acting; and a composed-only bit does NOT self-heal, so the Guardian design's fail-safe must come from a watchdog rather than from decay

**Date:** 2026-07-22

</details>

### [mapsave-load-card-sites-verify-dead] After a mid-session map-save load the equip-card text buffers are reallocated and the painter's cached sites go verify-dead and drain, rather than false-verifying inside freed-but-intact memory

Uncertain as of 2026-08-12: a player report says cards render factory-reset after some loads while the painted number stays frozen; the claim that cached sites go verify-dead rather than false-verify has not yet been directly observed in-process, with the LW-163 live pass as the isolation.

<details><summary>How we got here</summary>

**Claim (original wording):** After a mid-session map-save load the equip-card text buffers are reallocated and the painter's cached sites go verify-dead and drain, rather than false-verifying inside freed-but-intact memory

**Mechanism:** A load rebuilds the menu render copies; CardSites.AnchorIsLive requires the flavor anchor AND the "Kills: " literal to both read back exactly, so a stale site survives only if the freed buffer keeps both intact

**Evidence:** Player report 2026-08-12: cards render factory-reset after some loads (fresh buffers built from base data) while the painted number stays frozen; not yet directly observed in-process, the LW-163 live pass is the isolation

**Date:** 2026-08-12

</details>

### [worldmap-menu-open-byte] A byte at 0x140D508D0 reads 1 exactly while a world-map menu (party, equip, shop, save) is open, 0 on the free map, during travel, and through the whole formation and battle-load flow

Uncertain as of 2026-08-17 (evidence strong, owner flip pending): found this session for the LW-193 consent rewrite, which wants the twin grant suppressed exactly while the player can touch equipment. The byte flipped 0 to 1 on every menu open and back on every close across a scripted torture tour, stayed 0 across four world-map travel hops, and stayed 0 through formation screens and battle loading in four separate battle runs.

<details><summary>How we got here</summary>

**Claim:** 0x140D508D0 (u8) is a world-map "a menu panel is open" flag suitable as the twin grant's suppression gate; PauseFlag and SubmenuFlag cannot serve (both are battle-scope status-card signals per their own doc comments, re-confirmed reading 0 in every world-map menu state this session).

**Mechanism:** sits in a UI panel-flag family around 0x140D508xx; sibling 0x140D508EC tracks it, 0x140D50916 is its clean inverse. Nearby decode from the same hunt: 0x140D49554 is a menu-transition fade countdown (steps 15..0 per transition) and 0x140D4955C its fade-done flag.

**Evidence (probe `tools/probes/menu_signal_probe.py`, method = the consistency-sampled multi-state solve from the 1.5.1 re-anchor):**
* Narrow solve over the three known candidate families came up EMPTY across 5 states (free, party, equip, save, free2), which is itself the proof that no previously known constant covers this.
* Wide solve over the full 17.5MB static span (12 samples per state, numpy constant-intersection): 1855 bytes separate menu from free, 146 of them clean 0/1 flags.
* Torture tour on the top 12 (tape `tools/probes/tapes/lw193_menusig_20260817_063908.log`): 0x140D508D0 flipped on party open/close twice, the equip trip, and the SHOP, silent across all travel hops; one 500ms double-pulse during the town-exit fade. The early favorite 0x140D506B4 was disqualified there, pulsing 50ms on every travel hop.
* Formation/battle behavior: read 0 through the entire formation and battle-load flow in the twinless runs (tapes lw193_twinless*.log), so a gate on this byte deliberately does NOT suppress the pre-battle window, which premise [twin-dualfire-construction-bound] requires.

**Recorded caveats:** (1) story dialogs, the world-map modal message box, and mid-battle menus were not sampled; the shipped gate should debounce flips and fail toward suppression when unreadable. (2) Not yet an Offsets.cs constant; promote it with the fix arc. (3) Single save, single session; the byte is a UI-family flag, so a game patch can move it like any static.

**Date:** 2026-08-17

</details>

### [twin-dualfire-construction-bound] Dual-wield twin-fire is WELDED at battle construction: a battle built with an empty roster off-hand fires ONE shot even after the twin is stamped into the roster mid-battle

Uncertain as of 2026-08-17 (evidence strong, owner flip pending): with the roster off-hand held EMPTY through unit construction and released on the visible battlefield, the mod's stamp landed 0.4s later and the wielder still rendered one pistol and fired ONE shot. Post-construction roster stamps reach neither the dual-fire nor the field visual. Consequence for the LW-193 consent rewrite: the twin must be present in the roster when a battle constructs, so grant suppression must never cover the formation flow (and per [worldmap-menu-open-byte], the chosen gate naturally does not).

<details><summary>How we got here</summary>

**Claim:** the question the menu-evaporation design hinged on: if the twin is absent at construction, does the in-battle re-assert still deliver double Attack? Answer: NO.

**Mechanism:** battle units are constructed from the roster; the constructed unit's weapon state (and its field model) never re-derives from later roster writes. The in-battle re-assert's real job is therefore keeping the twin in the ROSTER (the game clears it mid-battle; observed at 07:17:15 coinciding to the second with the autosave write) so the battle-end gear commit and the NEXT construction still carry it, not enabling the current battle's dual-fire.

**Evidence (probe `tools/probes/twinless_probe.py`, four owner-run battles 2026-08-17, tapes `tools/probes/tapes/lw193_twinless*.log`):**
* Runs 1-3 were CONFOUNDED, each teaching the construction timeline: battleMode flips at the FORMATION phase ~40s before units build (run 1); the battle band holds stale frozen seats from the previous battle so seat-counting fires instantly (run 2); the band is rewritten in STAGES with the encounter load first, so a bulk-rewrite trigger still fires pre-formation (run 3). In each, the mod's stamp won the race and the owner correctly saw two pistols at formation.
* Run 4 (hold4, human-released): the hold kept the off-hand EMPTY through formation (owner bait check: ONE pistol rendered at formation, vs two in the confounded runs), construction completed during the hold, the release marker landed at 07:16:22.343 and the mod stamped oh=71 supp=477 at 07:16:22.746. The owner then observed ONE pistol on the field unit and Attack fired ONE shot.
* The pre-fix baseline (twin present at construction) fires two shots, re-confirmed live in runs 1-3 the same hour.

**Recorded caveats:** (1) one weapon (Outrider Pistol) and one battle for the decisive leg; the crossbow twin shares the identical lane (LW-171 data-driven). (2) Whether a mid-battle Re-equip can re-derive the constructed unit's weapons (the LW-191 lane) remains its own open question; nothing here settles it. (3) The 07:17:15 game-side roster clear coinciding with the autosave write is an observation, not an isolated mechanism.

**Date:** 2026-08-17

</details>

### [retry-preserves-credited-identity] A battle retry preserves the credited band slot's identity (nameId and the lvl/br/fa/maxHp tuple), for enemies specifically

Uncertain as of 2026-08-17 (evidence strong, owner flip pending): RestartSentinel's identity-gated grace exemption (LW-233 FINDING 0) assumes a checkpoint retry's revived unit carries the SAME frame nameId and (lvl,br,fa,maxHp) tuple as the slot's credited victim. One live retry now backs the nameId half for a single generic enemy: band slot 15 (nameId 378, job 85, maxHp 306) was credited before the retry and re-killed after it with every one of those fields unchanged. Still short of PROVEN: n=1, one generic enemy, one retry, and this tape never directly observed level/brave/faith (the tuple's other three fields) -- only nameId, job, and maxHp. If nameId does not survive a retry in general, the gate produces a MISS (a retry goes uncaught), never a false positive -- the deliberate, safe direction this code chose.

<details><summary>How we got here</summary>

**Claim:** a battle retry (both the full-rewind and the mid-battle checkpoint depth, docs/TODO.md's LW-233 entry) preserves the revived band slot's frame nameId (Offsets.ANameId) and its (lvl,br,fa,maxHp) fingerprint closely enough that comparing them against the values captured at credit time reliably identifies "the same victim, revived by a retry" versus "a different unit landing in the same slot" (LW-108's starved-bracket hole).

**Mechanism:** RestartSentinel.PresentRevive requires BOTH the tuple AND a nonzero nameId to agree before exempting the battle-age grace floor (LivingWeapon/Kills/RestartSentinel.cs; the credited half is stamped in LivingWeapon/Kills/KillTracker.Corpses.cs's `_creditedNameId`). The nameId half leans on [party-nameid-unique-key]'s PROVEN finding that PLAYER nameIds are stable across a battle reload -- but that row's own caveat 3 says generic ENEMY nameIds RE-ROLL per load ([boss-canonical-nameid-stable]), and this fix's whole target population is enemy corpses. A checkpoint retry rewinds IN PLACE with no exit edge at all ([battle-retry-rewind-fingerprint]), which is not the same event as a documented reload, so whether the same re-roll behavior fires on a retry is untested either way.

**Contradicting case already on record for the tuple half:** KillTracker.Corpses.cs's identity-swap branch (ScanAlivePath, the tuple-mismatch trigger) documents that a checkpoint retry CAN restore brave/faith to their pre-death values (a status effect or Rend-family hit undone by the rewind) -- direct evidence the tuple half is not universally stable across a retry. That branch is structurally excluded from the identity exemption regardless of this row (FINDING 6(ii), same arc: the branch's own trigger condition guarantees the tuple never matches there), so today's code already treats it as grace-only, not a silent false-positive risk -- but it is proof the "identity survives a retry" premise has at least one known hole.

**Evidence (tape `tools/probes/tapes/lw233_death_retry_live_20260817.jsonl`, one owner-run retry, band slot 15):** the pre-retry `victim` record (t=2165156, wall 14:09:34) and the post-retry `victim` record (t=2503015, wall 14:15:12) both read `alive=(nameId=378,job=85,undead=0,has=1) edge=(nameId=378,...) credit=(nameId=378,...)`; the `census` records at t=2185515 (pre-retry) and t=2537734 (post-retry battle-exit) both carry `s15:378/85`; the `ev` healing/damage records for slot 15 (t=2134859, t=2240078's revive, t=2476031) all read maxHp 306. Independently corroborated in the owner's `livingweapon.prev.log` at 14:09:34 and 14:15:12, both nameId 378. Recorded limits: n=1, one generic enemy, one retry; level/brave/faith were never directly observed in this tape (only nameId/job/maxHp); and this in-place retry rewind ([battle-retry-rewind-fingerprint]) is a DIFFERENT operation from the fresh world-map load [boss-canonical-nameid-stable] measured when it found generic nameIds re-roll, so this evidence answers "does an in-place rewind preserve identity" and leaves the reload question exactly where [boss-canonical-nameid-stable] left it. FINDING 5 (2026-08-17) wired the credited/presented identity (both nameId and tuple) into the flight recorder's "restart" records so a FUTURE drill can answer this the same way without extra instrumentation -- read a tape with `python tools/parse_flight.py <file>` and compare the credited vs presented nameId/tuple fields.
**Date:** 2026-08-17

</details>

### [party-browse-screen-byte] A byte at 0x140D408E2 reads 1 exactly on the party menu's two safe BROWSE screens (the unit overview root and the Character Status page) and 0 on every screen where equipment can be changed

Uncertain as of 2026-08-17 (owner-driven torture tour the same afternoon; owner flip pending): found for the LW-193 owner-AC round, which wants the twin grant allowed to stamp exactly where the player can SEE it appear (backing out of the equip screen) but never where gear can be edited. Complements [worldmap-menu-open-byte]: that byte says "some menu is open", this one says "and it is one of the two read-only browse screens".

<details><summary>How we got here</summary>

**Claim:** 0x140D408E2 (u8) is a safe allow-list gate for out-of-battle twin stamping: 1 only on screens with no equip actions.

**Mechanism:** offline solve over the morning's five wide state captures (free/party/equip/save/free2, 12 samples each): exactly 3 bytes separated party-root from BOTH the equip screen and the save menu; 0x140D408E2 is the only clean 0/1 flag among them (the other two, 0x140C6AD38/39, are noisy cursor-region bytes). It sits in the same 0x140D4xxxx screen-flag family as SubmenuFlag.

**Evidence (owner-driven labeled torture tour, tape tools/probes/tapes/lw193_menusig_20260817_110948.log; stations announced by the owner in session, timestamps matched to transitions):**
* 11:10:02.566 party menu ROOT opened: 0 to 1.
* Held 1 through the Character Status page (owner parked there ~11:10:30-11:11:10; no transition).
* 11:11:10.633 entered Equipment and Abilities: 1 to 0.
* 11:11:45.998 ESC from E&A back to Status: 0 to 1 (the exact moment the owner's AC wants the twin to appear).
* 11:13:07.867 first tab away from the root: 1 to 0, and it stayed 0 through the remaining tabs, the menu close, world-map travel, and the SHOP (shop confirmed open 11:13:26 via the menu byte, browse byte stayed 0).
* 11:14:51.049 root reopened: 0 to 1; 11:14:54.627 tabbed to Inventory: 1 to 0; Chronicle and Options produced no rise (owner announced each; no transitions on the tape).
* Free world map reads 0 (wide solve, 24 samples across two free states).

**Recorded caveats:** (1) The E&A SUB-pages (the weapon picker and ability picker) were NOT probed; the gate built on this byte must deny by default so an unprobed screen can never enable writes. (2) Formation and battle were not visited on this tape; the gate never consults this byte there because [worldmap-menu-open-byte] already reads 0 through those flows, so no coverage is claimed. (3) One session, one save; the byte is a UI-family static and moves on game patches like any other (add to the PATCH_REANCHOR list when wired). (4) The shop's sub-flows (fitting/optimize) were not separately visited; deny-by-default covers them.

**Date:** 2026-08-17

</details>

### [battle-retry-rewind-fingerprint] A battle RETRY rewinds the battlefield with no exit edge of any kind, has TWO depths (battle-start and mid-battle checkpoint), and carries a unique tape fingerprint: an actor-pointer null blip at-or-before the restore plus units healed and relocated in the same instant

Uncertain as of 2026-08-17 (evidence re-derived independently from banked tapes; owner flip pending): when a player loses a battle and picks retry, the game rewinds the battlefield without ever telling the mod the battle ended. Across three retries on tape (one owner drill 2026-08-14, two in a real player session 2026-08-12) the recorder shows one battle start, no exit between attempts, and turn and kill bookkeeping running straight through, so an enemy the player was already paid for stood back up and paid out again (Vagabond kills 23 and 25 were the same soldier at band slot 15, and the phantom 25th kill fired a real growth toast). This is the LW-233 mechanism, cause and fingerprint.

<details><summary>How we got here</summary>

**Claim:** the LW-233 backlog account (written 2026-08-14) holds up under independent re-derivation, and the retry moment is machine-detectable with zero false positives on the banked evidence.

**Mechanism:** the retry screen keeps whatever sentinel drives the exit edge armed throughout, so no exit debounce ever completes and no flush fires between attempts; the credit latch then correctly re-arms revived corpses as it would for a Raise, and re-kills credit again. The tally saves on change and at exit, so the inflation is permanent in the save file.

**Evidence (independent re-derivation 2026-08-17 from tools/probes/tapes/lw233_retry_drill_20260814.jsonl plus the player's six 2026-08-12 flight tapes; denominator 7 files, 1484 records, 221 ev records, 20 healing events):**
* One battle-start and ZERO exit edges per retry, proven by flush continuity: every tape file's first record continues exactly from the prior flush, and the player's 67KB exit tape spans BOTH of that battle's retries under a single header with continuous time.
* Actor-pointer to-null transitions: exactly 3 in 1484 records, one per retry, zero elsewhere; the null lands at-or-before the restore by 0 to 172 ms (62 ms on the drill; 0 ms on player retry A). CAUTION: a null read is also documented once at battle-open idle (Offsets.ActorPtr provenance), so the detector must gate away from battle enter.
* Heal-plus-same-instant-relocate coincidences: 9 across all tapes, ALL at the three restore moments, zero in ordinary play (the 11 ordinary heals have no same-tick move and none is from 0 HP). "Same instant" spans up to 16 ms on tape, so a runtime window, not strict tick equality.
* TWO RETRY DEPTHS (new): the drill and player retry A rewound to battle start (full HP, first-seen tiles, 6 and 5 units restored); player retry B rewound to a MID-BATTLE CHECKPOINT (slot 11 revived 0 to 20 of 56 on its death tile with NO move; slot 24 restored 0 to 6 of 72 with a move), tripping the heal-relocate detector through ONE unit only. A fix must not assume full HP or start tiles, must arm on both detectors, and must rewind per revived victim, since checkpoint-surviving kills remain legitimately earned.
* Bookkeeping continuity: the player's global turn counter ran 1..27 monotonically across both retries while the genuinely new next battle reset it to 1; the drill's turn 3 acted edge landed 14.8 s after the restore.
* The double payout end to end: slot 15 credited count 23 at t=12326390/12326640, healed 0 to 58 and moved at the retry restore t=12446343, re-killed and credited count 25 at t=12513109/12513359, toast "Vagabond has gained its 25th kill and has grown to Vagabond+2" delivered 12513625.

**Recorded caveats:** (1) "a real Raise never relocates its target" is UNVERIFIABLE from these tapes: no genuine revive occurs anywhere in them, so the Raise-vs-restart discriminator rests on the zero-false-positive record, not on an observed Raise. (2) A third candidate signal, a same-instant multi-slot move cluster, fired at all three restores but also once at battle-start deployment, so it only serves gated away from battle enter. (3) A long battleMode-0 spell does NOT discriminate (12.6 and 13.2 s non-retry spells exist on the same tapes); mode 0 to 3 within ~16 ms of the restore is confirming context only. (4) One restore printed a heal to 69 of a unit whose true max reads 72 elsewhere; never key a detector on healed-to-max. (5) Flight records are on-change, so a unit rewound onto its current values leaves no record; the runtime's per-tick sampling sees more than the tape does.

**Date:** 2026-08-17

</details>

## Contradicted — probe before building on these

### [icon-refresh-unreliable] The "leave the list and come back" icon refresh is CONDITIONAL, condition unknown: it fired 2026-08-16 and refused 2026-08-25 and 2026-08-26 on the SAME game binary

Contradicted 2026-08-26, owner live: the 2026-08-16 recipe (an already-drawn equip icon picks up a pac change on the next list/tab load) cannot be relied on. In tonight's session a drawn icon stayed frozen through every eviction attempt short of restart, while on 2026-08-16 one tab round-trip refreshed it, and the PE build key proves both sessions ran the same exe. Something conditional governs the eviction and it is unidentified. Design rule until it is: treat already-drawn icons as restart-only; the pac write and the first-draw read remain dependable (see [live-icon-repaint], which carries the full evidence including the retracted first conclusion).

<details><summary>How we got here</summary>

**Claim (original wording):** navigating to another inventory tab and back re-loads the list and shows the new art; the refresh unit is the list/tab load, not the frame and not the process.

**The contradiction:** the 2026-08-26 LW-334 bisect, `live_icon_patch_probe.py` run by hand with the owner watching. First draw of the session read the patched pac (Tideward showed Emberward's red, both surfaces, screenshot 03:51). After that, nothing evicted: tab round-trip, close and reopen equipment, world map, save plus load, and a title-screen reload all kept the first-draw art while the pac on disk said the opposite (Galewall patched, Tideward restored, both byte-verified). The 2026-08-25 LW-334 observation has the same shape (splice bytes in the pac, menu reopen showed plain). On 2026-08-16 the identical stage 2 flipped Galewall after one tab round-trip.

**The retraction that sharpened it:** the first same-night conclusion blamed game patch 1.5.2. The owner was skeptical and the PE key killed it: the exe reads 0x6A5EA53C / 0x18D78000, the 1.5.2 build the guard learned 2026-07-24, three weeks BEFORE the 2026-08-16 pass; the mod armed normally in all three sessions. Same binary, opposite outcomes, so the difference lives in session state, not the game version. Known differences: 2026-08-16 was a fresh launch made for the probe; 2026-08-25 and 2026-08-26 were long sessions with heavy play behind them. Suspects: cache/VRAM pressure, session age, scene history.

**Next step:** the battle enter/exit rung WAS run later the same night (owner, fresh session, Galewall drawn pink then pac patched red): a full battle did NOT refresh it, so every candidate eviction has now failed at least once in both a stale and a fresh session, and the arc is PAUSED by the owner with the condition unfound. The remaining unexplored discriminators if anyone resumes: a fresh-launch stage 2 timed early (the 2026-08-16 pass was minutes after boot), and VRAM/cache-pressure manipulation. The same night also proved the pac itself PERSISTS across relaunches (incremental merge; see [live-icon-repaint]), so any future probe must restore explicitly. Until the condition is found, nothing ships on mid-session refresh; the launch-merge path (deploy_glow_tex.py today, the LW-336 runtime loose-tex sync as successor) does not need it.

</details>

### [terrain-height-byte-blocks-movement] Terrain grid HEIGHT (byte +2) write BLOCKS movement (the Bulwark premise, as ORIGINALLY built)

Contradicted: height byte +2 never gates movement; the real, player-side-proven lever (2026-07-28) is grid byte +6, and specifically bit 0x02 - the engine's own obstacle state - which blocks movement while keeping the tile selectable with the native invalid-destination cursor.

<details><summary>How we got here</summary>

**Claim (original wording):** Terrain grid HEIGHT (byte +2) write BLOCKS movement (the Bulwark premise, as ORIGINALLY built)

**The contradiction:** WRONG BYTE, not a wrong grid. Live 2026-07-28: holding a 4-tile ring's HEIGHT (+2) and TYPE (+0) on all 9 copies moved only the inspected tile's display and never altered pathing; a goblin walked through. Static RE (workflow wf_afe76ac4, offline exe decode) explains it: the move-range builder `0x14F164860` (fresh per query) reads grid bytes +3 (slope/water-depth), +4 (water-class), +6 (hard-impassable) and +0 only for the ==0x3F void check; it NEVER reads +2 for enterability (+2 feeds only climb-delta vs 2xJump) and reads +0 only into the cost table. So the bytes held all night are simply not walkability inputs.

**Next step:** STATIC-RE PREDICTION, not yet live-proven (do NOT flip on the disasm alone; the 07-27 over-claim came from exactly one unproven observation): the real lever is byte +6 bit 0x01 (`0x140D8DCB6 + tileIdx*8`). It makes the builder skip the enterable grant and stamp 0x80, excluding the tile for ALL movement classes incl. fly, AND is independently honored by the AI destination enumerator `0x140283784` (the goblin path), the point-to-point validator `0x14027DF2A`, occupancy rebuilds, and the cursor mask `0x14031F9E4`. Use bit0, not bit1 (bit1 alone still grants the walk class). Battle-scoped (grid reloads per map). PLAYER-SIDE PROVEN 2026-07-28 (owner live, THREE independent confirmations): writing +6 bit 0x01 makes the PLAYER move-range builder AND the cursor treat a tile impassable -- (1) a unit on a vetoed tile softlocks ("can't reach any valid tile"), (2) the tile goes unselectable, (3) an otherwise-reachable open tile drops out of the blue range. byte +2/height never did any of this. The "only some ring tiles blocked" confusion was a pure OBSERVABILITY artifact: a veto is only visible on a tile that was otherwise reachable, and ring tiles already blocked by adjacent units/allies show no change. **USE BIT 0x02, NOT BIT 0x01 -- IT IS THE ENGINE'S OWN OBSTACLE STATE (settled live 2026-07-28, and it REFUTES the static RE's advice to prefer bit0).** A/B on two tiles flanking one unit: bit0 (0x01) blocks movement but ALSO removes the tile from the cursor mask, so the player cannot even hover or select it. bit1 (0x02) blocks movement AND leaves the tile selectable, rendering the game's native red circle-slash "invalid destination" cursor -- the exact affordance a tree top shows. CONFIRMED BY THE MAP ITSELF: this battle's five natural obstacles (the trees, heights 17-20) all read byte+6 == 0x22, i.e. bit1 set. So writing 0x02 does not approximate an obstacle, it writes the state the engine already uses for one, and the player gets correct feedback for free. Consumer split from the disasm explains it: the cursor mask tests `b & 1` only, while the move-range builder (`(b>>1|b)&1`), the AI enumerator (`b & 3`) and the point-to-point validator (`b & 3`) all catch bit1. The RE synthesis's "bit1 alone still grants the walk class" warning is WRONG in practice. TWO HAZARDS, both live-confirmed: (a) EITHER bit on an OCCUPIED tile prevents the occupant from moving (owner tested bit1 on Ramza's own tile), so never veto a tile someone stands on; (b) NEVER blind-clear bit0/bit1 across a map to "clean up" -- the natural obstacles use the same bits, and doing exactly that erased this map's five trees (recovered, but only because the tile list was still known; the twin copy at 0x14089AC50 is NOT a pristine backup, it mirrors writes).

</details>

**HEIGHT IS NOT A WALL, AND RAISING IT IS DANGEROUS (settled 2026-07-28 at the CORRECTED base, so this verdict is trustworthy where the earlier ones were not): raising a vacant tile's height (byte +2) to 31 -- a 27-step climb no Jump stat can clear -- did NOT remove it from the move range; the tiles stayed SELECTABLE, and moving a unit onto one SOFTLOCKED THE GAME (owner live). Logical height changed while RENDER height did not, so the unit entered an impossible state. This confirms the disasm (byte +2 is not an enterability input; it only feeds climb-delta math elsewhere) and closes the question the owner raised: "why not just set an unreasonable height?" -- because it does not block, and it traps whoever steps there. USE BYTE +6 BIT 0x01, NEVER HEIGHT, for any denial mechanic.**

**GRID BASE CORRECTED 2026-07-28 (this was THE bug behind every confusing result): the record base is `0x140D8DCB0`, NOT `0x140D8DCC0`.** The wrong base is 16 bytes = 2 records high, so every write landed 2 tiles EAST of target -- which is exactly the "+2 in x" the owner kept reporting and which I repeatedly mis-attributed to his eyeballing of the iso map. Root cause of the error: two CE captures showed different bases for the same table (`RDX=0x140000000` vs `RDX=0x140000010`) and I built on the `+0x10` one; the disasm operands `[rdx + idx*8 + 0xD8DCB2]` and `+0xD8DCB3` put record bytes +2/+3 at `0x140D8DCB2/B3`, i.e. record base `0x140D8DCB0`. PROOF (owner live, decisive): vetoing Ramza's four orthogonal neighbours at the corrected base LOCKED HIM IN ON ALL FOUR SIDES ("That works. Bravo."), where the same ring at the old base blocked exactly one tile. Retro-fit check: both earlier partial results match the +2 shift exactly (a 4-neighbour ring at (4,3) produced a locked cluster centred 2 east; Ramza's ring at (9,8) left only the one shifted tile on-map). ANY code or probe using the old base is writing 2 tiles off.

TILE SYSTEM SOLVED 2026-07-28 (memory-vs-memory, no eyeballing): cross-checked every unit's gx/gy (combat +0x4F/+0x50) against its RENDER NODE world position (node +0x4C/+0x50, independent of gx/gy) -- 8/8 units matched EXACTLY, worldX = 28*gx + 14 and worldY = 28*gy + 14. So gx/gy ARE the grid tile coordinates, no offset; idx = x + y*mapWidth + layer*0x100 (layer = combat +0x51 >>7); grid record = 0x140D8DCC0 + idx*8, 8 bytes/tile. The all-night "+2 in x" was pure eyeball error counting empty tiles on the rotated iso map. (Aside: byte +2 low5 is NOT the full render height -- it reads ~2 below node Z consistently, another reason the height-write approach was doomed; irrelevant to the byte +6 veto.) Bulwark computing neighbors as (gx+/-1, gy+/-1) is correct; the ring tiles that "did not block" were already unreachable, not mis-addressed. STILL OWED before the FULL flip and before Bulwark ships: THE AI test -- an enemy paths AROUND a vetoed VACANT tile rather than through (the RE traces byte +6 bit0 to the AI enumerator 0x140283784, but that consumption is UNVERIFIED live, and AI-walks-through is exactly what failed on byte +2). Not run tonight (late). Bulwark vetoes only VACANT tiles, so the never-veto-an-occupant caveat is handled by design. PROBE HAZARD BANKED: TaskStop/hard-kill skips a hold's finally-restore and leaves bit0 dirt surviving a RETRY battle (full map reload clears it); the fix shipped this session is a FILE-SIGNALLED release (touch /tmp/release_veto), and recovery is clear-bit0 on the touched tiles.  OWED ITEM SETTLED 2026-08-12 (owner-eyewitnessed, Bulwark regression battle): an enemy pathed AROUND the vetoed vacant tile rather than through it, so the AI-enumerator consumption is verified live; owner-directed. |

### [charmlock-expiry-auth-copy-0x25] CharmLock's lock-turn expiry counts turns off auth-copy +0x25

Contradicted-then-resolved 2026-07-22: band +0x25 IS the CT byte (combat +0x41), which does tick and reach 100; the earlier "zero transitions" reading came from a dead probe instrument. The only open question is whether CharmLock's expiry path ever fires in practice, a code question.

<details><summary>How we got here</summary>

**Claim (original wording):** CharmLock's lock-turn expiry counts turns off auth-copy `+0x25`

**The contradiction:** `Offsets.ACtSlam` (+0x25) is documented write-only ("a live watcher saw zero transitions") and SpiritualFont's notes say NEITHER +0x25 nor +0x09 band reads ever reached ≥90 across full turns — yet CharmLock reads +0x25 on the ±1MB auth copies and charm-lock behaved in play. Possibly the auth-copy family ticks where band entries don't; possibly the expiry path has simply never fired (locks released by battle end instead).

**Next step:** RESOLVED 2026-07-22, no new probe needed: band `+0x25` IS combat `+0x41`, the CT byte read all evening by `battle_toolbag.py state`, which showed live values across the full range (10, 57 and 100 on three units in one dump) and `bench` watched it re-accrue from zero after a hold. So it ticks and it reaches 100. The "zero transitions" observation came from the ct_probe family, which LW-93 independently records as DEAD on 1.5.1 (its slot-marker filters exclude every unit), so the contradiction was a broken instrument rather than a game fact. What remains open is only whether CharmLock's expiry path ever fires in practice, which is a code question, not a memory one. Original next step, kept for provenance: if it never reaches ≥90, the N-turn expiry is dead code and the lock only ends at battle end

</details>

### [mhp-2000-valid-unit-bound] mhp == 2000 is a valid unit

Contradicted: the codebase disagrees with itself on whether max HP exactly 2000 counts as a valid unit (enemy scans accept it, other validators exclude it); next step is to check whether any IC unit can reach exactly 2000 and settle one bound everywhere. No date recorded in the row.

<details><summary>How we got here</summary>

**Claim (original wording):** `mhp == 2000` is a valid unit

**The contradiction:** five enemy scans accept 2000 inclusive (`Band.EnemyFingerprints`), while `Band.IsValid` / `EnemyOracle` / `Plague.IsValidEnemyMhp` exclude it

**Next step:** check whether any IC unit can reach exactly 2000 max HP; then settle ONE bound everywhere

</details>

### [treasure-master-move-highlight-lever] Treasure Master via the move-range HIGHLIGHT (paint tiles blue)

Contradicted / partial: an on-off gate at 0x140c64c68 keeps the highlight rendered when held nonzero (proven live 2026-06-11), but it is not the source - holding custom tiles into the move-path list changed nothing on screen, so the buffer feeding the blue quads is still unfound.

<details><summary>How we got here</summary>

**Claim (original wording):** Treasure Master via the move-range HIGHLIGHT (paint tiles blue)

**The contradiction:** PARTIAL LEVER FOUND, source not yet. The move-highlight has an on/off GATE at `0x140c64c68` (u8: ~4 idle, 13 in move) — write+HOLDING it nonzero keeps the highlight rendered even out of Move mode (proven live 2026-06-11, ~3.4M holds). BUT the GATE is not the SOURCE: holding a custom tile list into `0x140C66315` (the move-PATH/validation list, 7 bytes/tile) changed nothing on screen — the engine draws the blue quads from a different buffer (float world-coords at `0x140c80000+`). One-shot count writes are reverted (engine-maintained); only a hold sticks.

**Next step:** find the buffer that FEEDS the `0x140c80000` quad render (diff in-move vs out, focus the band between the count and the quads), or learn the tile→world-coord mapping to hold the quads directly. `probes/mark_probe.py hold/holdmany`.

</details>

### [terrain-fingerprint-battle-invariant] Terrain grid 0x140C65000 fields {2,3,4,5} are battle-invariant static geometry (the v3 fingerprint premise)

Contradicted: those terrain fields are not battle-invariant - weather (rain) perturbs them, so the hash can fail or drift (incidents 2026-06-12). Resolution: the fingerprint is now fully advisory; arming is gated by build-key, per-tick map-id, and per-tile resting quorum, with hash mismatches only logged.

<details><summary>How we got here</summary>

**Claim (original wording):** Terrain grid `0x140C65000` fields {2,3,4,5} are battle-invariant static geometry (the v3 fingerprint's whole premise)

**The contradiction:** **LIVE INCIDENT #4 (2026-06-12).** Map 74 (Siedge Weald) ARMED with a matching v3 hash, then the hash drifted mid-battle (`got=D00BD4BCE16AE1D4` vs `want=A9B8F51113401AA5`) ~26 s in — same battle, same map id. v1 (raw) → v2 (field-0) → v3 (fields {2,3,4,5}) each drifted on this map. So fields {2,3,4,5} are NOT battle-invariant on all maps (likely a unit stepping onto a tile / an action mutating its record). `livingweapon.log`.

**Next step:** **LIVE INCIDENT #5 (same day) — root cause is WEATHER.** Rain perturbs the hashed terrain fields, so a map captured in one weather state fails the *arm-time* gate in a rainy instance (found on maps 74/76/79/81, all raining). No weather metadata exists in the repo to enumerate which maps can rain, so per-map `nofp` is unwinnable (a clear-captured map silently shows no tiles for a player in rain). **Resolution: the fingerprint is now FULLY ADVISORY** (arm-time + mid-battle) — arming is gated by build-key (L0) + per-tick map-id (L1, unique per map) + per-tile resting quorum (L3); the hash mismatch only logs. `BattleDisarmed` removed; `nofp` obsolete. No v4 mask.

</details>

## Walled — do not retry without new information

### [crit-chance-settable] Crit-hit chance settable

Setting crit-hit chance is Walled: there is no data field or per-unit byte to hold, and the engine byte is Denuvo-walled. A full search hit the same wall as proc-rate 0x32.

<details><summary>How we got here</summary>

**Claim (original wording):** Crit-hit chance settable

**Wall:** no data field, no per-unit byte to hold, engine byte Denuvo-walled

**Evidence:** full search, same wall as proc-rate 0x32

</details>

### [guest-traitor-flip] Guest/Traitor allegiance flip

Flipping guest/traitor allegiance is Walled: it is an engine pool-relocation, not a holdable byte, with +0x46/+0x02/+0x1B/+0x23/+0x1DD all dead via a same-unit Entice heap diff.

<details><summary>How we got here</summary>

**Claim (original wording):** Guest/Traitor allegiance flip

**Wall:** engine pool-relocation, not a holdable byte

**Evidence:** +0x46/+0x02/+0x1B/+0x23/+0x1DD all dead via same-unit Entice heap diff

</details>

### [knockback-gxgy-overturned] Guaranteed Knockback via gx/gy writes (OVERTURNED 2026-07-10)

This wall was OVERTURNED 2026-07-10: a coherent triple-write (combat tile + node tile key + node world transform at node +0x4C/+0x4E/+0x50) moves the sprite AND logic together, so Knockback/teleport/swap are proven; see [teleport-swap-float-render-node-transform] in Proven.

<details><summary>How we got here</summary>

**Claim (original wording):** ~~Guaranteed Knockback via gx/gy writes~~ **OVERTURNED 2026-07-10**

**Wall:** The wall was "renderer never re-derives from gx/gy." The MISSING layer was the render node's WORLD transform (node `+0x4C/+0x4E/+0x50`), never located until a two-phase node byte-watch during a walk found it. A coherent triple-write (combat tile + node tile key + node world) moves the sprite AND logic together; Knockback/teleport/swap are proven. See the teleport Uncertain row.

**Evidence:** `probes/knockback_probe.py` (the old wall); `node_move_watch.py` / `node_world_fit.py` / `swap_units.py` (the break), 2026-07-10

</details>

### [treasure-native-tile-mark-overturned] Treasure Master via the NATIVE tile mark is walled (OVERTURNED 2026-06-11)

This wall was OVERTURNED 2026-06-11: the tile mark is a per-tile FLAG bit, and write+holding it renders the mark (see the PROVEN row above); the lesson is to toggle-scan for a FLAG before declaring a wall when an AoB/coordinate scan comes up empty.

<details><summary>How we got here</summary>

**Claim (original wording):** ~~Treasure Master via the NATIVE tile mark is walled~~ **OVERTURNED 2026-06-11**

**Wall:** The "no writable store" verdict was WRONG — it assumed a coordinate list (3.8 GB AoB scan, 0 hits) and never tried a differential toggle scan. The mark is a per-tile FLAG bit, and write+holding it renders the mark. See the PROVEN row above. Lesson: when an AoB/coordinate scan comes up empty, toggle-scan for a FLAG before declaring a wall. (The static `trap_treasure_tiles.json` data pass + FFTHandsFree `DetectMap()` are still the INPUT half — that part of the plan stands.)

</details>

### [bag-list-261-cap] >261 items in the BAG INVENTORY LIST

OVERTURN CANDIDATE 2026-08-26 (owner flip pending): the bag list showed id 261 on 1.5.2 once the default-order table carried it; see [inventory-default-order-drops-unknown-ids] under Uncertain. The wall was the display-order table, not a boot-built registry.

Getting more than 261 items into the bag inventory list stays Walled: the bag list is a boot-built registry that live cap-lifts cannot inject into. NARROWED 2026-06-26: the wall is ONLY the bag list; equip, equip-screen display, and battle function were broken through via force-equip plus the 4-gate hook chain.

<details><summary>How we got here</summary>

**Claim (original wording):** >261 items in the BAG INVENTORY LIST

**Wall:** the bag list is a boot-built registry; live cap-lifts don't inject into it (id261 never shown in the bag). **NARROWED 2026-06-26:** this wall is ONLY the bag list -- EQUIP + equip-screen display + battle function were BROKEN THROUGH by force-equip + the 4-gate hook chain (see the two id261 Uncertain rows). The registry gates the bag LIST, NOT equip.

**Evidence:** `docs/research/ITEM_CAP_261_BREAK_JOURNEY.md` (session 3 FINAL)

</details>

### [id261-model-asset-wall-overturned] id261 in-battle weapon MODEL is asset-walled (OVERTURNED 2026-06-26)

This wall was OVERTURNED 2026-06-26: the battlefield swing model is baked at unit construction from the equip-slot id, so presenting a real-model id at construction time gives the swing a model (live-proven with a Chaos Blade roster rHand swap); see the Uncertain row.

<details><summary>How we got here</summary>

**Claim (original wording):** ~~id261 in-battle weapon MODEL is asset-walled~~ **OVERTURNED 2026-06-26**

**Wall:** The "asset-bound, unredirectable" verdict was WRONG. The battlefield swing model is BAKED AT UNIT CONSTRUCTION from the equip-slot id (NOT live-read -- find-what-accesses on `CWeapon` during idle battle caught ONLY the status-menu reader `0x140360383`/`0x140360396`, nothing per-frame). Live proof: set roster rHand `0x1411A7D24`=37 (Chaos Blade) → entered a battle → Ramza swung a VISIBLE Chaos Blade. So presenting a real-model id at CONSTRUCTION time gives the swing a model. See the Uncertain row. Lesson (again): "asset-bound wall" was a construction-bake we hadn't probed with a slot swap.

</details>

### [external-probes-engine-code] External probes on engine code

External probes on engine code are Walled by Denuvo, though module statics ARE readable externally; the DLL is the only instrument for code-adjacent experiments.

<details><summary>How we got here</summary>

**Claim (original wording):** External probes on engine code

**Wall:** Denuvo; module statics ARE readable externally

**Evidence:** the DLL is the only instrument for code-adjacent experiments

</details>

### [heal-numeral-write-and-hold] Floating heal/damage NUMERAL via write-and-hold

Spawning a floating heal/damage numeral by write-and-hold stays Walled: the magnitude is a transient argument to the display-spawn routine, not a holdable field, and the 2026-07-27 update moved the wall one level up after a DLL cold-call of the builder FnNumberPopup drew nothing. Next lever if reopened: cold-call the caller one level up or detour the natural call site.

<details><summary>How we got here</summary>

**Claim (original wording):** Floating heal/damage NUMERAL via write-and-hold

**Wall:** The displayed magnitude lives in NO holdable per-unit field. Two live captures of a real Cure (`+142` twice, slot 24 `430->572`) show only HP (`+0x14/0x15`) plus `+0x22` (=42) and `+0x19f` (=2) changing -- both constant across heals, neither the magnitude. The number is a transient ARGUMENT to the engine's display-spawn routine during ability resolution, read once at event time, not polled -- so holding a field can't re-spawn it (the level-up-banner class). Our silent band `+0x14` writes (Wellspring/Font/Benediction) therefore stay numberless.

**Evidence:** `probes/heal_number_probe.py watch <slot>` 2026-06-16. Only path left: debugger (x64dbg/CE) breakpoint on the heal-popup spawn, recover its address+signature, call it from the DLL with our magnitude. **2026-07-27 UPDATE: that path was walked and the wall MOVED one level up.** CE what-writes found the builder `FnNumberPopup 0x140227CF8` (one caller `0x140228CF5`; args rcx=presentation `0x142FF5498`, rdx=layout `0x1407DC908`, r9=0xC-walker `0x1407B3520`; r8 is DEAD, never set by the caller); the per-unit displayed value is combat `+0x1C4` (u16, sits at the routine's rbp[+6], unit selected via global `[0x142FF3CF8]` = unitBase+0x1BE); the last-number global `0x1407832BE` is a write-only mirror (exactly one xref in the image: its own writer). The DLL cold-call (NumeralSpike, uncommitted) fired 4x live: paused, paused+actor-global-set, unpaused, unpaused+actor -- returned clean every time, drew NOTHING. So the builder is the pipeline's LAST step and the caller's pre-call record build (sibling calls to `0x140242afc` walking 0x88-stride records first) is load-bearing. Next lever if reopened: cold-call one level up (the caller fn `0x140228601`, needs its stack-local state) or detour the natural call site and re-enter with a swapped value/unit.

</details>

### [spawn-rendered-unit-overturned] Spawn a brand-new RENDERED unit mid-battle (write-and-hold) (OVERTURNED 2026-07-10)

This wall was OVERTURNED 2026-07-10: write-and-hold stays dead, but cold-calling the render-node builder 0x14026EBEC plus the data-only AI enroll lets a duplicated live donor spawn a real, drawn, controllable, AI-fighting unit; see [spawn-ai-unit-donor-duplication] in Proven.

<details><summary>How we got here</summary>

**Claim (original wording):** ~~Spawn a brand-new RENDERED unit mid-battle (write-and-hold)~~ **OVERTURNED 2026-07-10**

**Wall:** The write-and-hold path stays dead, but a DIFFERENT lever won: cold-call the render-node BUILDER `0x14026EBEC` (the render weld's first reachable door — it builds a node for an added unit) plus the data-only AI enroll (a cloned registry object + the one-byte AI-roster index `0x141873038`). A DUPLICATED live donor spawns a real, drawn, named, controllable, AI-FIGHTING unit. The old wall's premise ("no clean slot-indexed sprite table to forge") was right that you can't FORGE the graphic — the fix is to make the ENGINE build it. See the "SPAWN a real AI-fighting unit by DUPLICATING" Uncertain row.

**Evidence:** `BodyDoubleSpike` Canary 1-9, 2026-07-10; the render weld broken via the builder + the crash/freeze decodes. Old evidence: `probes/clone_probe.py`, `probes/formation_diff.py`, 2026-06-16.

</details>

### [enemy-reskin-rebuild-trigger] Live ENEMY field re-skin via a transform-status rebuild trigger

Live enemy field re-skin via a Frog/cure rebuild trigger is Walled (live-tested 2026-06-18): the cure rebuild restores the cached construction model rather than re-reading +0x00, so a re-skin needs a pre-construction +0x00 write (a construction hook), not a live struct write plus trigger.

<details><summary>How we got here</summary>

**Claim (original wording):** Live ENEMY field re-skin via a transform-status rebuild trigger

**Wall:** the field model DOES rebuild on Frog/cure, but Frog drives the body through a STATUS FLAG -- it does NOT write `+0x00` -- and the CURE rebuild restores the CACHED construction model, not a re-read of `+0x00`. So there is no live field-reskin trigger to hijack. Live enemy field re-skin needs a PRE-construction `+0x00` write (a construction hook), not a live struct write + trigger.

**Evidence:** live 2026-06-18: frogged enemy (slot 4, HP459/L96) kept `+0x00=0x82`; set `+0x00=0x03` (Ramza, resident) while frogged; cured -> returned as the GOBLIN, not Ramza

**Evidence (extra cell):** 2026-06-18

</details>

### [entd-blueprint-not-findable] Enemy ENTD blueprint not findable in live 1.5 memory

Finding the enemy ENTD blueprint in live 1.5 memory is Walled as of the 2026-06-18 session: the format is known but static sigs and content searches fail and caller scans hit Denuvo indirect calls, so it needs the unlocated CopyUnitToBattleUnit hook via an external debugger. The player roster blueprint at 0x1411A7D10 IS editable.

<details><summary>How we got here</summary>

**Claim (original wording):** Enemy ENTD blueprint not findable in live 1.5 memory

**Wall:** format known (FFTArchivist: 512 battles x 16 units x 40B records: SpriteSet/Job/Level/equip/X,Y) but Dicene's static sig is dead on 1.5, content search fails (enemies render as generic monster sprite 0x82), and a caller-scan of CopyJobEffectsToUnit/set_status_all found ZERO direct call sites = Denuvo indirect calls. NOT a quick search.

**Evidence:** 2026-06-18 session (`combat_scan.py` + exe scans). Needs the unlocated CopyUnitToBattleUnit hook via external debugger. The PLAYER roster blueprint (`0x1411A7D10`) IS editable = native job-change.

</details>

### [palette-code-sites] First code addresses that touch the weapon palette region

Two genuine RIP-relative instructions in `.code` reference the static weapon-palette region: `0x1401d462d` loads the whole 1024-byte region for what decodes as a memset, and `0x1401d50e1` loads the address of ONE palette (index 12). These are the first code addresses anyone has had for this mechanism. OBSERVED 2026-08-21 by displacement scan; NOT yet traced to the draw path; AWAITING OWNER FLIP.

<details><summary>How we got here</summary>

**Claim:** the code that handles the weapon palette is locatable by static analysis, without a debugger.

**Mechanism:** [resident-weapon-palette-buffer] gave two IMAGE-relative palette addresses (`0x140d35750`, `0x140d35950`), and the image base is fixed with no ASLR. x64 reaches static data by RIP-relative displacement, so a 4-byte scan of the executable sections for `target - rip_after` finds referencing code. NOTE the binary is Denuvo-protected and has NO `.text`: code lives in `.code` (0x140001000, 0x610000), `.xcode`, `.ecode`, `.xtext`, and the palette statics sit in `.xpdata`. A probe looking for `.text` reports a false dead end.

**Evidence, decoded by hand from the captured bytes:**

    0x1401d462b  33 d2                    xor  edx, edx
    0x1401d462d  48 8d 0d 1c 11 b6 00     lea  rcx, [rip+0xb6111c]   -> 0x140D35750
    0x1401d4634  41 b8 00 04 00 00        mov  r8d, 0x400
    0x1401d463a  e8 ..                    call

`0x1401d4634 + 0xb6111c = 0x140D35750` exactly, and 0x400 = 1024 covers BOTH static copies, which sit 0x200 apart. Shape is `memset(region, 0, 1024)`. Second site:

    0x1401d50e1  48 8d 0d e8 07 b6 00     lea  rcx, [rip+0xb607e8]   -> 0x140D358D0

`0x140D358D0 = 0x140d35750 + 12*32`, i.e. palette index 12 specifically, in the same function neighbourhood about 2.7 KB away. Loading ONE palette's address is the shape a per-palette selection would have.

**Not established, and this is the important part:** neither site is shown to be on the DRAW path. A memset is initialisation, not rendering. The palette-12 reference could equally be an unrelated struct offset that happens to land there. Nothing has been hooked, traced, or stepped. Two of the four raw scan hits were rejected as noise because their bytes do not form valid instructions at the matched position; that rejection was by hand, not by a decoder.

**Next cheapest steps:** find the callers of the function containing `0x1401d462d`; scan the DATA sections for an 8-byte pointer equal to either heap palette address, which is how a heap buffer is normally reached; and check whether `0x1401d50e1`'s palette-12 reference is data-driven or a constant. Denuvo makes any actual hook fragile, see the `denuvo-hook-launch-fragility` note.

**Date:** 2026-08-21

</details>

### [weapon-graphic-byte-not-sprite] RETRACTED: byte 1 of the item-graphics record is NOT the drawn weapon sprite

Byte 1 of the classic item-graphics record was written up as "the graphic", meaning the weapon sprite drawn in battle, and several claims were built on it. It cannot be that: its 16 distinct values put 43 weapons into a single value spanning 14 categories including daggers, bows, harps, bags and bombs, which visibly differ on screen. RETRACTED 2026-08-21. The PALETTE half of the same record is unaffected and remains proven.

<details><summary>How we got here</summary>

**The claim being retracted:** that byte 1 of the record at `0x02D3E6 + (itemId-1)*2` selects the weapon sprite, so a weapon's battle appearance is the pair (graphic, palette).

**Why it is false, from our own data.** Grouping the 127 mapped weapons by that byte gives 16 values. Value 0 alone holds 43 weapons spanning Knife, Sword, Pole, KnightSword, Gun, Crossbow, Bow, Instrument, Book, Polearm, Bag, Cloth, Throwing and Bomb: Dagger, Broadsword, Battle Axe, Bowgun, Bestiary, Catskin Bag, Shuriken and 36 others. A dagger, a harp and a handbag do not render as the same sprite in battle, and the owner has watched all three. Value 3 similarly mixes a flail, a katana, crossbows and bows.

**What this invalidates.** Three statements made earlier the same day, all resting on this byte: that 127 weapons render as 84 distinct (art, palette) appearances; that 43 of 127 are visually identical to another weapon; and specifically that Cutpurse and Vagabond are pixel-identical because both are "art 0, palette 14". None of those are supported. The true number of weapons sharing a sprite is UNKNOWN and nobody has established it.

**What survives, and why the build is unaffected.** The PALETTE half of the record decode is proven live and independently: palette 14 was written and the owner's Cutpurse (palette 14) changed colour while Galewind (palette 4) in the same battle did not, twice, with screenshots. LW-301 chooses colour per palette and never consults the sprite identity, so this retraction does not touch it. The sprite map was only ever wanted for an offline preview.

**Left open deliberately:** what byte 1 actually is. It was not needed for the colour work and guessing produced a confident wrong answer once already. A sprite map, if one is ever wanted, should be built from observation rather than from this field.

**Date:** 2026-08-21

</details>



### [weapon-palette-assignment-walled] WHICH palette a weapon uses cannot be changed by any known data channel

OVERTURN CANDIDATE 2026-08-27 (owner flip pending): the assignment IS a data channel, the two-byte record at 0x140785CF0 + id*2 (byte 0 palette nibbles, byte 1 drawing), read on every swing; the four levers below all missed it because the probes used 0x140785CF2 as the table base, one item off. See [weapon-sprite-pair-drives-swing-art] under Uncertain.

Repainting the sixteen palettes of FFTPack file 71 works and is PROVEN ([wep-spr-palette-block]).
Changing WHICH of them a given weapon draws from does not: the ItemData `<Palette>` byte, the
ItemData `<SpriteID>` byte, a mod-shipped `battle_bin.bin`, and a direct write to the resident
in-memory copy of that table were each tested live with untouched controls in the same battle and
with the serve or the write independently verified, and none moved a single pixel. Disk is
exhausted too: across 14.35 GB of install and 11.6 GB of extraction only one copy of the table
exists. The assignment is resolved once at startup into a render-side structure nobody has found.
WALLED 2026-08-19; reopen only with a hook on the weapon draw path. Owner to confirm the status
per this ledger's flip rule.

<details><summary>How we got here</summary>

**Claim:** a mod can assign a chosen palette to a chosen weapon, which is what an icon-matched
per-weapon recolour needs. Vanilla gives thirteen palettes to a hundred and twenty seven weapons,
so without reassignment the honest grain is per palette group, not per weapon.

**Four levers, four negatives, every one with a control.**

1. **ItemData `<Palette>`.** Three launches, twelve battle loads, four swords. Bytes moved 2 to 8,
   4 to 0 to 8; palettes did not move. The write reached game memory, logged at cell level:
   `[ItemData] prawl.fft.livingweapons changed ID 19 (Palette, value: 8)` and
   `Applying ItemData with 94 change(s)`. That byte instead keys the MENU ICON clut
   (`fftpack/tex/item/item_01.clut`), which scores z = +5.27 in a within-SpriteID permutation test
   against +1.21 for the weapon sheet bank; ice-named weapons name a palette holding a blue ramp
   6 of 6 times in the icon bank and 0 of 6 in the weapon bank. It coincides with the real battle
   nibble for only 6 of 127 weapons.
2. **ItemData `<SpriteID>`.** Rewritten from 14 to 33 (a sword to an axe), write in the log, the
   weapon never changed shape. SpriteID runs 0-178 and allocates blocks to rings, perfumes, shoes
   and armour, none of which has a battle sprite: it is the menu icon graphic. NOTE this
   contradicts a doc comment in `tools/generate.py` claiming SpriteID picks the drawn weapon
   graphic; that comment has no ledger row behind it and is wrong for battle art.
3. **A mod-shipped `battle_bin.bin`** (FFTPack file 0). Served from our copy five times with zero
   reads of the game's own, deployed bytes verified at the correct offsets, exactly two bytes
   different in a 1.4 MB file, only one copy of the table in the file. Nothing moved.
4. **A direct write to the resident copy.** A memory scan found four full-length copies of the
   254-byte record block: three matching our shipped file, and one still vanilla at
   `0x416DCA3CA6`, inside a COMPLETE vanilla `battle_bin` image at base `0x416DC768C0` (matches the
   file at +0x000000, +0x02D000, +0x100000 and +0x150000). That image cannot have come through the
   hooked path, because the FFTPack hook only ever serves 1 MB and the match extends well past it,
   so it is loaded before the modloader installs its sig-scanned hook. Poking item 22 there from
   0xF0 to 0x80 verified on readback, verified again AFTER a battle load, and the weapon still
   rendered palette 15. The table was not rebuilt; the renderer simply does not read it.

**The order-free sweep, which closes the "maybe it is just sorted differently" escape.** A multiset
is order independent, so a re-sorted table cannot hide. Scanned 4109 MB of committed memory for any
127-element window at strides 1, 2, 4, 8, 16 whose value multiset equals
{3:8, 4:9, 5:6, 6:7, 7:6, 8:10, 9:5, 10:9, 11:7, 12:5, 13:18, 14:20, 15:17}. Zero matches. Two
controls make that admissible: a shuffled copy planted at an unaligned offset inside a real 32 MB
game memory chunk was found, adding exactly one hit and no false positives; and the prefilter
examined 250,482 real candidate windows with a longest in-band run of 20,721 bytes, so it had
plenty to reject rather than rejecting everything trivially. Probe:
`tools/probes/lw289_order_free_scan.py`. Its histogram step is memory-bounded by SLICE after an
unbounded version drove three scan processes to about 10 GB each.

**The disk half, run independently in the sibling ColorCustomizer session and recorded in
the sibling repo's WEAPON_COLOR_CC_FINDINGS journal (Dev/FFTColorCustomizer) (committed 7d7cb104).** `FFT_enhanced.exe`
362 MB including the 349 MB Denuvo-wrapped `.xdata`, strides 1-64, six variants, both directions,
both nibble orders, plus the order-free multiset at strides 1/2/4/8/16: clean. All 192 nxd tables:
clean, and there is no item or weapon nxd at all. The extracted tree, 14,816 files and 11.60 GB:
exactly one table, the PSX one. The full install, all 66 pacs, 3,017 files and 14.35 GB: one hit,
and it was OUR deployed test copy, identified by a ZZ-only match whose missing XY half decoded to
precisely the two items we had altered. Three positive controls fired unprompted in the wild.

**Joint verdict.** The poke says the byte table is not read at draw time; the disk sweep says no
other copy exists to be read. Together those select transformed-at-startup: the assignment is
resolved once into a render-side structure and the byte table is vestigial thereafter.

**What would refute this.** The values stored transformed rather than reordered (arithmetic, an
offset, sub-byte packing, or welded into a shared byte with other fields), or never materialised as
a table and computed per draw. A multiset search sees through reordering and through none of those.

**Reopen with.** A hook on the weapon draw path in-process, which is the only instrument that can
watch the value being consumed rather than guessing where it is stored. Unpacking Denuvo to keep
searching statically was considered and rejected as expensive and likely to surface something the
renderer does not read anyway.

</details>
