# Cold read log

STATUS: CONTRACT (the pre-commit half of the readability quiz gate reads this file)

Every reader run of the cold read appends one entry here, before the handoff, so the record
outlives the handoff. Plain version, before a production change is called done a fresh
reader who has never seen the work answers three pre registered questions from the comment
stripped code alone. A miss means the understanding is not in the code. Nothing in this file
is a score. Patrick reads the misses.

Entry shape, written by `quiz_gate.sh entry` and parsed by `quiz_gate.sh precommit`:

    ## Cold read <date time>, <ticket>, base <turn start SHA>
    Run cap <n> (<changed production lines> changed production lines). Run <k> of <n>.
    Files: <every production .cs file in the diff, plus any callee added to the packet>
    Q1 (failure path): ...
    Expected: ...
    Reader: ...
    Result: pass | miss, fix name | miss, fix helper | miss, fix comment | open | bad question
    Q2 (ordering or threading): ...
    Q3 (free): ...
    Unprompted: <what the reader found that nobody asked about>

The cap line comes before any result, so the limit exists before an answer is read against
it. Entries are keyed on the turn start SHA; the pre-commit check accepts a commit only when
the entries for its SHA (or HEAD) together name every changed production file. Rolls by
release: at each release tag this file moves to docs/archive/COLD_READ_LOG_<version>.md with
an ARCHIVED stamp and a fresh one starts here. Method and question guidance live in the
cold-read skill of the home setup repo, beside the hooks.

## Cold read 2026-10-19 02:17, LW-452, base 40b24ac3
Run cap 3 (557 changed production lines). Run 1 of 3.
Files: LivingWeapon/Engine.cs, LivingWeapon/Extended/ExtendedInventory.Hooks.cs, LivingWeapon/Extended/ExtendedInventory.Tick.cs, LivingWeapon/Extended/ExtendedInventory.cs, LivingWeapon/Extended/RewindRecomputeStep.cs, LivingWeapon/Extended/RewindRestoreHook.Policy.cs, LivingWeapon/Extended/RewindRestoreHook.cs, LivingWeapon/Extended/RewindTotals.Policy.cs, LivingWeapon/Extended/RewindTotals.cs, LivingWeapon/Extended/SaveEdgeHooks.Detours.cs, LivingWeapon/Extended/SaveEdgeHooks.cs, LivingWeapon/Offsets.cs, LivingWeapon/Extended/InventoryResetHook.cs
Q1 (failure path): If the pre battle snapshot's key has no sidecar record, or a checkpoint is put back that the mod never saw being saved, or no total was ever captured, what does each path write and log, and can the re derive step make a count worse?
Expected: flee with no record, the charts are adopted, nothing is replayed, no load edge is published, one Warn names the key; checkpoint with no shadow, nothing is written and one Warn per session; recompute with no captured total returns false and writes nothing; a recompute or a put back whose roster or bag read failed writes nothing and counts Incomplete, and a capture with a failed read keeps the previous total; a captured total is bag plus hands at the latest snapshot, so a recompute can only move a count toward total minus hands, never below zero, and it writes only when the byte differs. The no record Warn says the counts will be re derived from the last total captured, because the rewind counter rises before the callback runs.
Reader: all three paths read correctly from the code. No record, AfterRewind warns naming the key, records rewind-restore record=missing, returns before the replay and before Capture("rewind"). Unrecorded checkpoint, RestoreCounts bumps _unrecorded, warns once behind _warnedUnrecorded, writes nothing, does not raise _restores. No total, Recompute returns false and Step is silent. On "worse", the reader says yes in principle, Recompute trusts _latest and an id missing from latest writes 0, and it cannot see from the packet how stale _latest can get. It did not mention the Incomplete count on a failed read.
Result: pass
Q2 (ordering): On a Retry from Formation three different pieces of code write the seven extended bag counts within about thirty milliseconds, and the game rewinds the roster hands during that window. In what order do they run, which write is the one that ends up correct, and why does the re derive step run twice instead of once?
Expected: the inventory reset hook runs first and puts the live bytes back unchanged; the checkpoint restore hook runs next and writes total minus the hands held at that instant; the game may revert the roster hands after either of those; the tick step sees the restore counters rise, schedules a recompute at tick plus 3 and tick plus 15, and each pass writes total minus hands now. The last pass after the roster has settled is the correct one; the second pass exists because the roster revert can land after the first.
Reader: named DetourCheckpoint, DetourRestore via ReplayOnLoad, and Step as the three writers, never InventoryResetHook even though its file was in the packet. Could not order the first two, "depends on how the game calls FnCheckpointCopy and FnSnapshotRestore, neither call site is in the packet". Got the tick step last at tick+3 and tick+15 from Schedule and Due, and that Recompute writes only when want differs. On why twice, "the code gives no reason", the roster-settles reading is "an inference, not something in the packet". Also noted PollMs 33 makes the window about 100 to 500 ms, not 30.
Result: miss, fix comment
Q3 (free): A Warbrand is stolen or broken in battle and the battle then ends normally. What stops the re derive step from minting that Warbrand back into the bag, and when does the mod learn the weapon is gone?
Expected: the recompute runs only when a rewind counter rose (checkpoint restores or rewinds handed on), neither of which happens at a normal battle end or at a load, so no recompute runs; the game's inventory reset is deliberately not a trigger because it also runs at loads and after won battles; the next serialize edge (and the next load edge) captures total again as bag plus hands with the weapon absent, so the total drops by one then.
Reader: nothing inside Recompute stops it; the gate is Step calling Recompute only when Due fires, which needs CheckpointRestores or RewindsHandedOn to rise, and only ProcessRewind and RestoreCounts raise those; OnBattleExit does not touch the extended inventory. The loss is learned at the next Capture, with all four call sites listed (save, checkpoint, rewind, load). Did not name the inventory reset as a deliberate non trigger.
Result: pass
Unprompted: AfterRewind logs "put back with it" before the replayed check, so a replay that threw or returned early (not Armed, no items) still reads as success (this is LW-455, found again). CheckpointTotalShadow evicts at Capacity 8, after which a real checkpoint triggers the "never saw being saved" warning (adjacent to LW-454). ReadHands counts RRHand and ROffHand only, not RLHand or RShield. SaveCounts bumps _savesIncomplete with no log. Several empty catch blocks (Step, ProcessRewind, ProcessCheckpoint, SerializeCore, AfterRewind, Recompute). RewindRestoreHook.Install nulls both hook fields after Release when the second Activate throws. Engine.Tick has no per phase try/catch so one throwing phase skips extended-rewind. Offsets.InventoryCountBase and BagCountArray are the same address.

## Cold read 2026-10-19 06:36, LW-453, base 8d66dc7f
Run cap 0 (8 changed production lines). Run 1 of 0.
Files: LivingWeapon/Extended/OrderRebuildHook.Policy.cs, LivingWeapon/Extended/OrderRebuildHook.Seat.cs, LivingWeapon/Extended/OrderRebuildHook.cs
Q1 (failure path): After the game's own rebuild runs, suppose the second read of the list fails, or the write that adds the dropped ids back is refused. What count does the hook hand back to the game in each case, and what lands in the log?
Expected: Both cases return the game's own count unchanged. A failed second read logs nothing. A refused write logs one Warn that the item list refused the re-append write.
Reader: Both hand back the game's own count. The failed second read returns count silently. The refused write logs the Warn "refused the re-append write" and returns count. Added the footprint refusal Warn and that a list with no FFFF in 260 words also reads as null.
Result: pass
Q2 (ordering): Inside one call, in what order do the seating of owned ids into the template, the first read of the list, the game's own rebuild, and the re-append happen, and which of those steps is guaranteed to run even if another one throws?
Expected: Seating first, then the first list read, then the game's rebuild exactly once, then the re-append. The game's rebuild always runs, because it is called once outside any try while seating and re-append are each wrapped and swallow their exceptions.
Reader: Seat, first read, original, re-append. The rebuild still runs if seating throws, and the game's count still returns if re-append throws. Flagged that the first read sits outside any try, so a throw there would skip the rebuild; could not tell whether TryRead throws because ICodePatcher was not in the packet.
Result: pass (the reader was sharper than the expected answer; ICodePatcher.TryRead returns false on failure by contract, so the unguarded read is safe only by that contract)
Q3 (free): The re-append only brings back a dropped word when the bag holds that id or when bit 0x4000 is set on the word. Why both conditions, and what went wrong without the filter?
Expected: The old list can still carry ids sold or equipped away since, and adding those back drew an empty row. Bit 0x4000 is the E badge, set while every copy is worn, so the bag byte reads 0 yet the player still owns the item and it must stay in the list.
Reader: Cannot answer from the packet. Saw the filter and the EquippedBadge name but would not infer the badge meaning or the old empty row bug.
Result: bad question (the why lives only in comments, so a stripped packet cannot carry it; a name like StillOwned for the filter would carry half of it)
Unprompted: The first list read is outside any try (safe only by the ICodePatcher contract). _refusalWarned is a plain bool, so warn once is racy under concurrent rebuilds. Seated counts planned ids, and the repair path returns before counting. Owned is called on any dropped id though only extended ids are seated. Two empty catches leave no trace when seating or re-append throws.
