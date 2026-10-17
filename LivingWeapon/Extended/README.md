# Extended/

The game's item tables stop at 261. These files add items past that. Three moves do it. The checks that say "is the number under 261" get raised. The tables that need room for new rows get copied into space we own, and the game is pointed at the copy. Everything else the game asks about an item, stats, range, art, category, gets answered by our own code for the new ids and passed through untouched for the old ones.

## How it works

Think of the game as a clerk asking questions about any item it meets. What kind of thing are you, what are your stats, is your number under 261. For most of those questions the clerk never looks anything up itself. It jumps to a small lookup routine and takes whatever answer comes back. We aim those jumps at our own code, which answers for the new items and hands everything else straight to the original. The new item never has to be in the game's own tables for those questions.

A few things the game does look up directly, the item catalog and the menu order tables. Those get copied into a roomier page of ours with the new rows added, and the reads are re-pointed at the copy.

The list of checks was found one refusal at a time. The history is in docs/research/ITEM_CAP_261_BREAK_JOURNEY.md.

## All or nothing

Everything installs once, at boot, as one transaction. Before any spot in the game's code is changed, the bytes there are read and compared with what we expect. If even one spot reads wrong, because a game update shifted it or another mod got there first, every step already done is undone in reverse and the game runs without the extra items. Delete the mod and a save holding an extra item loads with that hand empty and the item gone (docs/LIVE_LEDGER.md, capbreak-uninstall-is-clean-loss). The sidecar json is left behind in the mod's user folder. Only this mod ever reads it.

## The questions and who answers them

| The game asks | Answered in |
|---|---|
| What kind of item is 262? Its price, its category, its icon? | ExtendedCatalog.cs, ExtendedRecords.cs, TemplateRelocation*.cs |
| What are its stats, its range, its artwork? | AccessorEntryHook.cs, ThunkStub*.cs, ExtendedDonorThunks.cs |
| Is its number under 261? | BytePatchSet.cs, ExtendedSites.cs, ListRelocation*.cs |
| Why is it not in this menu? | ListBuilderHook*.cs, OrderRebuildHook*.cs, CategoryGetterHook.cs, ShopFlagsMirror.cs |
| How many are in the bag? (the save has no slot for a new item) | ../Persistence/ExtendedBagSidecar.cs, SaveEdgeHooks*.cs, SaveEdgeTracker.cs, InventoryResetHook.cs |
| What is it called and what does it look like? | item.en.nxd rows and the shipped .tex files, built by the pipeline, not here |

The rest is plumbing. ExtendedInventory.cs and its three partials hold the state, the install order (Arm), the hooks and the undo (Hooks), and the per tick work (Tick). ExtendedInventoryData.cs loads the shipped item tables from the mod folder. SwingIdFallbackHook.cs makes a new item swing like a real weapon instead of a bare fist. TemplateSync.cs keeps the copied order templates in step with the originals.

## Install order

The steps lean on each other, so the order is fixed. Cap patches, list relocation, template relocation, catalog, shops, accessor hooks, swing fix, then the detours. It is all in ExtendedInventory.Arm.cs. The first step that refuses stops the run and RollBack in Hooks.cs undoes the earlier steps in reverse.

## Words that show up a lot

- **site**. One exact spot in the game's code that we change.
- **cap**. A check that an item's number is under 261. Widening it means raising that number so the new items pass.
- **donor**. Two per item. The clone donor supplies behavior, the art donor supplies the sprite and icon. Only the stat row is the new item's own.
- **accessor thunk**. The small jump the game takes to read one item's data. We aim it at our own code, which answers for new items and passes everything else through.
- **template**, also called an order template. One of the game's lists that decides which items appear in a menu and in what order.
- **relocation**. Copying one of the game's tables into roomier space of ours and pointing the game at the copy.
- **sidecar**. extended_inventory.json in the mod's user folder, next to kills.json. The game save has no byte for a new item's bag count, so the count lives there and is put back when the save loads.

## Where to start

ExtendedRecords.cs first. It is only the record layout, no memory access. Then ExtendedInventory.Arm.cs for how the parts fit. After that pick one row of the table and follow it.

The byte layouts and sites were read from the 1.5.2 exe (now the copy protection free build, which loads at a random base, so every address is an offset from ImageBase) and are pinned by the Extended*Tests in LivingWeapon.Tests. The sites live in ExtendedSites.cs, the *.Sites.cs partials and Offsets.cs, each with the date and the probe or offline read it came from. After a game patch, follow docs/PATCH_REANCHOR.md.
