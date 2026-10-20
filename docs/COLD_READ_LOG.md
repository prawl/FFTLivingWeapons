# Cold read log

STATUS: CONTRACT (the pre-commit half of the readability quiz gate reads this file)

Every reader run of the cold read appends one entry here, before the handoff, so the record
outlives the handoff. Plain version, before a production change is called done a fresh
reader who has never seen the work answers three pre registered questions from the comment
stripped code alone. A miss means the understanding is not in the code. Nothing in this file
is a score. Patrick reads the misses.

Entry shape, written by `quiz_gate.sh entry` and read by the Stop and pre-commit hooks:

    ## Cold read <date time>, <ticket>, base <turn start SHA>
    Run cap <n> (<added production lines> changed production lines). Run <k> of <n>.
    Files: <every production .cs file that gained lines, plus any callee added to the packet>
    Hashes: <file>=<hash of its comment stripped code when the entry was written>; ...
    Canary: cold | WARM | guess | none
    Q1 (failure path): ...
    Expected: ...
    Reader: ...
    Result: pass | miss, fix name | miss, fix helper | miss, fix comment | open | bad question
    Q2 (ordering or threading): ...
    Q3 (free): ...
    Unprompted: <what the reader found that nobody asked about>

The cap line comes before any result, so the limit exists before an answer is read against
it. An entry vouches for a file only while the file's comment stripped code still matches its
recorded hash and its base is in the history of HEAD, so a comment touch up keeps it valid
and any code change needs a new entry. Q4 of every packet is a canary whose answer lives only
in notes the reader must not see; WARM means the reader saw them and its passes are warm. A
calibration entry measures the gate, not a change, and sits outside the cap. Rolls by
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
Result: miss, fix name (relabeled under the four stage rules. The why lived only in comments, which makes it a miss, not a bad question; a name like StillOwned for the filter carries half of it, and the badge meaning is a game fact for a comment)
Unprompted: The first list read is outside any try (safe only by the ICodePatcher contract). _refusalWarned is a plain bool, so warn once is racy under concurrent rebuilds. Seated counts planned ids, and the repair path returns before counting. Owned is called on any dropped id though only extended ids are seated. Two empty catches leave no trace when seating or re-append throws.

## Cold read 2026-10-19 09:07, calibration LW-452 rewind fix, base 0e29f898
Calibration run, outside the cap (0 changed production lines).
Files: LivingWeapon/Engine.cs, LivingWeapon/Extended/ExtendedInventory.Hooks.cs, LivingWeapon/Extended/ExtendedInventory.Tick.cs, LivingWeapon/Extended/ExtendedInventory.cs, LivingWeapon/Extended/RewindRecomputeStep.cs, LivingWeapon/Extended/RewindRestoreHook.Policy.cs, LivingWeapon/Extended/RewindRestoreHook.cs, LivingWeapon/Extended/RewindTotals.Policy.cs, LivingWeapon/Extended/RewindTotals.cs, LivingWeapon/Extended/SaveEdgeHooks.Detours.cs, LivingWeapon/Extended/SaveEdgeHooks.cs, LivingWeapon/Offsets.cs, LivingWeapon/Extended/InventoryResetHook.cs
Hashes: LivingWeapon/Engine.cs=d2ef6637dfb879e7; LivingWeapon/Extended/ExtendedInventory.Hooks.cs=5021c5af70cb577a; LivingWeapon/Extended/ExtendedInventory.Tick.cs=bfe8ce18c93b7748; LivingWeapon/Extended/ExtendedInventory.cs=b503c0bfe99880a3; LivingWeapon/Extended/InventoryResetHook.cs=ae5994a7bc0fc97a; LivingWeapon/Extended/RewindRecomputeStep.cs=d3cd43ca5ffb9ad6; LivingWeapon/Extended/RewindRestoreHook.Policy.cs=fc9da4f9a1340a3b; LivingWeapon/Extended/RewindRestoreHook.cs=039968602b1830b1; LivingWeapon/Extended/RewindTotals.Policy.cs=33179b750b931408; LivingWeapon/Extended/RewindTotals.cs=73a058db254f275e; LivingWeapon/Extended/SaveEdgeHooks.Detours.cs=442050885ee92240; LivingWeapon/Extended/SaveEdgeHooks.cs=f2133bff31ad94e8; LivingWeapon/Offsets.cs=a98e6ca36266eeca
Canary: cold
Q1 (failure path): If the pre battle snapshot's key has no sidecar record, or a checkpoint is put back that the mod never saw being saved, or no total was ever captured, what does each path write and log, and can the re derive step make a count worse?
Expected: flee with no record, the charts are adopted, nothing is replayed, no load edge is published, one Warn names the key; checkpoint with no shadow, nothing is written and one Warn per session; recompute with no captured total returns false and writes nothing; a recompute or a put back whose roster or bag read failed writes nothing and counts Incomplete, and a capture with a failed read keeps the previous total; a captured total is bag plus hands at the latest snapshot, so a recompute can only move a count toward total minus hands, never below zero, and it writes only when the byte differs. The no record Warn says the counts will be re derived from the last total captured, because the rewind counter rises before the callback runs.
Reader: all three paths right, with the same anchors as the warm run. It also found on its own that the rewind counter rises before the callback, so a missing record still schedules a recompute, and that a failed capture keeps the old total. On worse, it said a stale latest total could give a stale count, and noted the clamp at zero.
Result: pass
Q2 (ordering): On a Retry from Formation three different pieces of code write the seven extended bag counts within about thirty milliseconds, and the game rewinds the roster hands during that window. In what order do they run, which write is the one that ends up correct, and why does the re derive step run twice instead of once?
Expected: the inventory reset hook runs first and puts the live bytes back unchanged; the checkpoint restore hook runs next and writes total minus the hands held at that instant; the game may revert the roster hands after either of those; the tick step sees the restore counters rise, schedules a recompute at tick plus 3 and tick plus 15, and each pass writes total minus hands now. The last pass after the roster has settled is the correct one; the second pass exists because the roster revert can land after the first.
Reader: named the two detours and the tick step at tick plus 3 and tick plus 15, could not order the two detours because neither call site is in the packet, and said no code states why the recompute runs twice.
Result: miss, same as the warm run. The call order and the late roster revert are game facts a stripped packet cannot carry, already fixed at the comment tier
Q3 (free): A Warbrand is stolen or broken in battle and the battle then ends normally. What stops the re derive step from minting that Warbrand back into the bag, and when does the mod learn the weapon is gone?
Expected: the recompute runs only when a rewind counter rose (checkpoint restores or rewinds handed on), neither of which happens at a normal battle end or at a load, so no recompute runs; the game's inventory reset is deliberately not a trigger because it also runs at loads and after won battles; the next serialize edge (and the next load edge) captures total again as bag plus hands with the weapon absent, so the total drops by one then.
Reader: the gate is Step, which needs CheckpointRestores or RewindsHandedOn to rise, and a normal battle end raises neither. The loss is learned at the next Capture, with the save, load, rewind and checkpoint captures listed. Did not name the inventory reset as a deliberate non trigger.
Result: pass
Unprompted: RewindsHandedOn rises only when the header read succeeds, so an unreadable header schedules no recompute. RewindRecomputeStep.Step reads each counter twice, so a rise between the two reads is absorbed without a schedule. CheckpointTotalShadow evicts at 8 and a reused address overwrites silently (adjacent to LW-454). Empty catch blocks in Step, ProcessRewind, ProcessCheckpoint and SafeLog. The checkpoint tail bytes embed 0x7E90 as a literal with no link to Offsets.CheckpointBagOff. It did not re find LW-455 this time.

## Cold read 2026-10-19 09:07, calibration LW-453 corpse scan on compare/corpses-readability, base a8072bc8
Calibration run, outside the cap (30 changed production lines).
Files: LivingWeapon/Chronicle/VictimClass.cs, LivingWeapon/Kills/KillTracker.Corpses.cs, LivingWeapon/Kills/KillTracker.Delayed.cs
Hashes: LivingWeapon/Chronicle/VictimClass.cs=71839b8adcc015ab; LivingWeapon/Kills/KillTracker.Corpses.cs=1c33780c082d7a9e; LivingWeapon/Kills/KillTracker.Delayed.cs=21ea8f4ce8805326
Canary: cold
Q1 (failure path): A death has been waiting for a killer, and on this tick the number of turn ends since it fell has already reached the limit. Does the wait's tick count move this tick, and what happens to the death?
Expected: The tick count does not move, because the turn limit is checked first and the count only advances when that limit has not tripped. The death expires uncredited and the log reports the tick count as it stood.
Reader: no, the turn limit is checked first and the count only advances when it has not tripped, then ExpirePendingUncredited closes the death with no credit and logs it.
Result: pass
Q2 (ordering): A charged attack is armed when a death becomes creditable, but the restart latch is open on this tick. Is the charge still armed after this tick, and on the later tick that pays, what disarms it and what list is used?
Expected: Still armed, because ResolveCredit only peeks. On the paying tick ConsumeDelayedCulprit calls the same peek, sets the armed tick count to zero, and returns that same delayed actor list, which is the one credited.
Reader: still armed, because ResolveCredit only peeks. On the paying tick ConsumeDelayedCulprit sets the armed count to zero and the credited list is the delayed actor list.
Result: pass
Q3 (free): When the mod logs a kill left unpaid because the killer had no Living Weapon and no victim snapshot was captured, what noun follows "The fallen", and can the article form and the bare form of that phrase ever disagree?
Expected: "enemy". They cannot disagree, because FellPhrase is built by putting an article in front of FellNoun.
Reader: enemy, and the two forms cannot disagree because FellPhrase wraps FellNoun with an article.
Result: pass
Unprompted: IsHumanJob covers jobs 74 to 95 but the job name table covers 74 to 93, so job 95 classifies as human and is named foe. A pending death whose delayed charge runs out falls back to the lethal actor or the last player weapons. A non null delayed candidate skips the untracked early bury. ExpirePendingUncredited leaves the credited weapon, identity and name id fields set.

## Cold read 2026-10-19 11:35, calibration Extended sweep rewind, base 2dd77414
Calibration run, outside the cap (0 changed production lines).
Files: LivingWeapon/Extended/RewindRestoreHook.cs, LivingWeapon/Extended/RewindRestoreHook.Policy.cs, LivingWeapon/Extended/RewindTotals.cs, LivingWeapon/Extended/RewindTotals.Policy.cs, LivingWeapon/Extended/RewindRecomputeStep.cs
Hashes: LivingWeapon/Extended/RewindRecomputeStep.cs=2138d76926d77d5a; LivingWeapon/Extended/RewindRestoreHook.Policy.cs=fc9da4f9a1340a3b; LivingWeapon/Extended/RewindRestoreHook.cs=f0eb9f8b18bec016; LivingWeapon/Extended/RewindTotals.Policy.cs=33179b750b931408; LivingWeapon/Extended/RewindTotals.cs=73a058db254f275e
Canary: cold
Q1 (failure path): A Retry puts back a checkpoint struct the mod did record, but the roster hands read fails inside the put back. What happens to the latest total, the extended bag bytes, the counters and the log, and does anything later repair the bag?
Expected: PutBack adopts the recorded totals as Latest and bumps Captures before it reads the hands. The hands read fails, so Incomplete rises, no bag byte is written and an empty result comes back. RestoreCounts still bumps CheckpointRestores and still logs a flight record and an Event, with an empty counts description. Because CheckpointRestores rose, the tick step schedules recompute passes at tick plus 3 and plus 15, and those re derive the bag from the adopted Latest as total minus hands once the reads succeed.
Reader: Got every part of the failed put back right, including Latest adopted first, nothing written, CheckpointRestores still counted and an Event that reads as success with empty counts. It named the recompute step as the repair from the adopted Latest at plus 3 and plus 15, but could not confirm that CheckpointRestores is one of its trigger sources because the wiring lives outside the packet.
Result: bad question (audited, the recompute wiring lives in ExtendedInventory.Hooks.cs, outside the packet)
Q2 (ordering or threading): The tick step is called every tick from 100 through 105, then not again until tick 130. Source A's counter rises at tick 100 and source B's counter rises at tick 105. How many recompute passes run, at which ticks, and what reason does each carry?
Expected: Two passes. Tick 100 schedules passes at 103 and 115; the pass at 103 runs with reason A. The rise at 105 replaces the schedule with 108 and 120, dropping 115. At tick 130 both 108 and 120 are past due, Due removes them together and returns true once, so a single pass runs at 130 with reason B.
Reader: Two passes, at 103 with reason A and at 130 with reason B. The reschedule at 105 clears the pending 115, and at 130 both overdue passes are removed together and run once.
Result: pass
Q3 (free): Mode 0 fires for a checkpoint struct address that already holds a record from an earlier save, but this time the bag read fails. A Retry then restores that struct. Which totals go back into the bag? Separately, what happens when a ninth distinct struct address is saved, and then a Retry restores the first one?
Expected: The failed capture leaves Latest unchanged and SaveCounts counts SavesIncomplete and returns without touching the shadow, so the older record for that address survives and the Retry puts back those older totals. A ninth distinct address evicts the least recently remembered one (saving an address again moves it to newest). Restoring an evicted struct finds no record, so the bag is left alone, CheckpointRestoresUnrecorded rises, a flight record is written and the warning prints only once per launch.
Reader: The old record survives the failed save and the Retry puts back the earlier, stale totals. The ninth address evicts the least recently saved one, a re save refreshes order but a restore does not, and restoring the evicted struct bumps the unrecorded count, writes a flight record, warns once and leaves the bag alone.
Result: pass
Unprompted: The put back counts as a restore and logs a success Event even when it wrote nothing, and it changes the latest total before its reads can fail, so a failed put back still moves what later recomputes aim for. Empty catch blocks in the detours and in Recompute drop faults with no counter or log. The once per launch warning flags are plain bools with no lock, so two game threads could each print the warning. Install returns early when the restore hook exists even if the checkpoint hook does not. The Store capture is read after the game's original routine runs, which depends on what that routine does.

## Cold read 2026-10-19 11:35, calibration Extended sweep save-edges, base 2dd77414
Calibration run, outside the cap (0 changed production lines).
Files: LivingWeapon/Extended/SaveEdgeHooks.cs, LivingWeapon/Extended/SaveEdgeHooks.Detours.cs, LivingWeapon/Extended/SaveEdgeTracker.cs
Hashes: LivingWeapon/Extended/SaveEdgeHooks.Detours.cs=6c873a4e5debe4a7; LivingWeapon/Extended/SaveEdgeHooks.cs=a0d3329000a33114; LivingWeapon/Extended/SaveEdgeTracker.cs=7c6681cc68293b9a
Canary: cold
Q1 (failure path): In AfterRewind, compare two failures. (a) The has-record check says no record exists for the key. (b) The record exists but the bag replay callback throws. For each, say whether TemplateSync.Adopt is called, whether the tracker's OnApplied is called, which publishes the load edge, whether the onRewound callback is called, and what gets logged.
Expected: (a) Adopt is called (it comes before the check), then a warning is logged and a "record=missing" flight entry is written, and it returns early; OnApplied and onRewound are not called, so no load edge is published. (b) Adopt is called, the replay fault is swallowed and replayed becomes false, OnApplied is still called so the edge is published, the normal "bag counts were put back" event line and a "record=found" flight entry are still written, but onRewound is skipped.
Reader: Got both branches exactly, including Adopt before the check, the early return with the warning and record=missing, and in the throw case the edge still published, onRewound skipped and the success line logged anyway.
Result: pass
Q2 (ordering or threading): Before Engine's tick drains the tracker, the game serializes save A, then serializes save B, then applies a load of save C. What do TryTakePendingSave and TryTakePendingLoad return on the next drain, what is CurrentKey, and why is there a lock at all?
Expected: TryTakePendingSave returns true with B's key and B's counts only (A was overwritten, a newer edge supersedes an undrained one); TryTakePendingLoad returns true with C's key; CurrentKey is C's key. A second take of either returns false. The lock exists because the detours write the pending slots on the game's thread while the tick drains them on another thread. The counts are copied into a new dictionary so the caller's map cannot change the pending one.
Reader: Got B's key and copied counts, C's load key, CurrentKey as C and the false second takes. On the lock it said it only matters if hooks and drain run on different threads and that it could not confirm which threads they are.
Result: miss, fix comment
Q3 (free): During Install, all three prologues match, the serialize and apply hooks are created, then creating the load-restore-b hook throws. What state are the hooks left in, what does Install return, and what does a second Install call on the same object return?
Expected: The first two hooks were activated and are then disabled by Release; Install returns the "install failed" message. A second Install returns null (success) immediately because _serialize is already non-null, so the caller is told it succeeded although no hook is active; IsActive stays false.
Reader: Said the first two hooks end up disabled, the third field stays null, Install returns the install failed message, and a second Install returns null because the _serialize guard fires, with nothing re-armed and IsActive false.
Result: pass
Unprompted: A failed Install followed by a retry reports success with every hook disabled, because Release only disables and never clears the fields. On a rewind whose replay threw, the log still says the counts were put back. Each hook is live before its field is assigned, and the detours dereference that field with no try, so a call landing in that gap would throw into the game. Many catch blocks swallow faults with no trace, and ReadCounts turns an unreadable byte into a zero count. If one load fires both restore routines, the replay and the load edge run twice. The canary flags are unlocked bools and stay unset if the repair throws, so a first time line can repeat.

## Cold read 2026-10-19 11:35, calibration Extended sweep inventory-reset, base 2dd77414
Calibration run, outside the cap (0 changed production lines).
Files: LivingWeapon/Extended/InventoryResetHook.cs
Hashes: LivingWeapon/Extended/InventoryResetHook.cs=c50a2a51fcd7254a
Canary: cold
Q1 (failure path): The game's reset ran and wiped some extended bag bytes, but the write that puts the saved bytes back is refused. What does the hook log, does the Restores counter move, and what does the detour return to the game?
Expected: It logs one Warn line (Save verb) saying the counts could not be put back and returns from Restore early, so Restores does not increment, no flight record is written and the canary flag is untouched. The detour still returns the original routine's result unchanged, and Runs was already incremented at entry.
Reader: One Warn line through SafeLog, then an early return before the Restores increment, and Process still hands back the original's result unchanged. It did not spell out the skipped flight record or Runs, but the early return it quoted covers them.
Result: pass
Q2 (ordering or threading): During a restore the write succeeds and Restores increments, then Flight.Record throws. What state is the hook left in, and at what log level will the next successful restore report itself?
Expected: The bytes are already written back and Restores already counts it. The exception escapes Restore and is swallowed by the try in Process, so the original's result is still returned and the original ran exactly once (it sits outside the try). Because the canary flag is set only after Flight.Record, it stays false, so the next successful restore logs at Event level, not Debug.
Reader: Bytes restored and Restores counted, the exception is caught by the empty catch in Process and the result is returned normally, the canary stays false so no line is written for this restore and the next one logs at Event level.
Result: pass
Q3 (free): Release has been called, then Install is called again on the same instance. Does Install re-check the entry prologue, does it create a new hook, and what does it return?
Expected: No prologue check and no new hook. Because the hook field is still non-null, Install just re-enables the existing hook if it is disabled and returns null (success).
Reader: No prologue check and no new hook, since Release only disables and the hook field stays set, so Install re-enables it and returns null. It added that a throwing Enable is not caught there.
Result: pass
Unprompted: The detour reads the hook field to call the original, but that field is set only after Activate returns, so a game call landing in that gap would hit a null and throw into the game. Both empty catches swallow errors with no trace, so a restore that throws leaves nothing in the log. The saved bytes are written back whole, which would also undo any real change the game made to those bytes during the reset. The canary flag is a plain bool while the counters use Interlocked. A failed CreateHook leaves the keepalive set and the hook null, so a later Install retries from scratch. The expected prologue is a public mutable array.

## Cold read 2026-10-19 11:35, calibration Extended sweep extended-inventory, base 2dd77414
Calibration run, outside the cap (0 changed production lines).
Files: LivingWeapon/Extended/ExtendedInventory.cs, LivingWeapon/Extended/ExtendedInventory.Arm.cs, LivingWeapon/Extended/ExtendedInventory.Hooks.cs, LivingWeapon/Extended/ExtendedInventory.Tick.cs, LivingWeapon/Extended/ExtendedInventoryData.cs
Hashes: LivingWeapon/Extended/ExtendedInventory.Arm.cs=82fbb8c166cade9a; LivingWeapon/Extended/ExtendedInventory.Hooks.cs=5021c5af70cb577a; LivingWeapon/Extended/ExtendedInventory.Tick.cs=bfe8ce18c93b7748; LivingWeapon/Extended/ExtendedInventory.cs=b503c0bfe99880a3; LivingWeapon/Extended/ExtendedInventoryData.cs=3de7990aff29775d
Canary: cold
Q1 (failure path): During BootArm, the save edge hooks are refused by the hook layer, and every earlier step succeeded. What is undone and in what order, what happens to the bag seed, and what state are Armed, Refusal, the door and BagCountBase left in?
Expected: Install returns the refusal, so RollBack runs in reverse install order: the hooks already installed are released, namely reset, list builder, order rebuild and category getter, then the swing id fallback, the accessor hooks newest first, shops, catalog, template relocation, list relocation and finally the cap patches. SeedBag never runs. Armed stays false, Refusal holds the reason, the door is set Failed and a Warn line is logged; BagCountBase falls back to the vanilla Offsets.BagCountArray because the relocation is no longer installed.
Reader: It gave the full reverse rollback order, noted the half installed save hooks are released too, and said SeedBag never runs, Armed stays false, Refusal holds the reason and the door goes Failed. It could not say which address BagCountBase returns because the relocation type's Installed flag after Restore is outside the packet.
Result: bad question (audited, where the count base lands lives in ListRelocation.cs, outside the packet)
Q2 (ordering or threading): A save is loaded. The load detour runs ReplayOnLoad on the game's thread, then a later StepBagSidecar on the tick drains the load and maybe a save in the same window. In what order are load and save drained, and what does the tick do differently when the detour's stored plan matches the pending load key versus when it does not or there is none?
Expected: The load is drained before the save, so a save that followed the load records the replayed counts. If the detour's plan has the same key, the tick reuses that plan instead of resolving the sidecar again and does NOT re-run TemplateSeat; it only logs the detour's repair and refusal notes. If the key differs or there is no plan, the tick resolves the plan itself from the sidecar and runs TemplateSeat itself from the tick. Either way it reapplies the bag counts through BagCountBase and clears the stored plan.
Reader: Load is drained before save after the tripwire check. A matching key reuses the detour plan, skips Resolve and skips the tick's own TemplateSeat; a missing or mismatched plan resolves and seats on the tick. Both paths reapply the counts, clear the stored plan and log the notes.
Result: pass
Q3 (free): Why must extended item ids be contiguous starting at FirstExtendedId, and what does Load hand BootArm if just one of three item rows has an out of range Price?
Expected: The stubs and WeaponRowAddr index per item arrays, the weapon rows and clone and art donors ordered by id, by id minus FirstExtendedId, so a gap would make an id read another item's row or donor. One bad row makes Load return zero items with the error listed and FolderPresent true, so BootArm refuses the whole arm as "tables did not validate" rather than shipping the other two.
Reader: It pointed at WeaponRowAddr's id minus FirstExtendedId indexing and the id ordered arrays handed to the stub emitters with the low id, but declined to state the consequence of a gap without the emitter bodies. The Price case it got exactly: zero items, the error listed, FolderPresent true, and BootArm refuses on the not Ok branch.
Result: miss, fix helper (audited, a slot helper would carry the contiguous id contract)
Unprompted: The detour's two note strings are appended on the game thread and read then cleared on the tick, so a note can be lost between the read and the clear. The detour's bag write passes no refused write callback, so a refused write there is only logged if the tick drains that load. Notes from a detour whose load never reaches the tick get logged under the next load. SeedCount is never range checked and wraps when cast to a byte. The armed log line hard codes "6 hooks" and the swing fallback. A test installHooks delegate skips the null hooks refusal. One bad Id in a table file stops parsing the rest of that file with a single error. Any install failure shows the same "game code is not where expected" door text, even when the hook layer refused. RollBack releases a save hook whose own install failed.

## Cold read 2026-10-19 11:35, calibration Extended sweep list-builder, base 2dd77414
Calibration run, outside the cap (0 changed production lines).
Files: LivingWeapon/Extended/ListBuilderHook.cs, LivingWeapon/Extended/ListBuilderHook.Policy.cs, LivingWeapon/Extended/ListBuilderHook.Pool.cs
Hashes: LivingWeapon/Extended/ListBuilderHook.Policy.cs=9e4b569f16b758dc; LivingWeapon/Extended/ListBuilderHook.Pool.cs=9d7073ddb8d76b1a; LivingWeapon/Extended/ListBuilderHook.cs=4ea359af405ffea9
Canary: cold
Q1 (failure path): A stack caller (any buffer other than the static menu buffer) calls the builder. The original runs and reports 200 entries, but the write of the truncated list into the caller's buffer is refused. What does the caller's buffer end up holding, what count is returned, what is logged, and why must the count not be 200?
Expected: CopyBackRefused runs. It bumps the refusal counter, makes one more best effort write of an empty list (just the 0xFFFF terminator) into the caller's buffer, logs a Warn the first time in the session and Debug after, and the detour returns 0. The slot is still released in the finally. The count must not be 200 because the original wrote its list into the pool slot, not the caller's buffer, so a caller trusting 200 would append and terminate past its small stack buffer.
Reader: Got the refusal counter, the best effort terminator write, Warn once then Debug, and the 0 return. For why not 200 it only said the count would claim more than the 149 cap allows and the list never reached the buffer, missing that the caller appends at that index and runs past its stack buffer.
Result: miss, fix comment
Q2 (ordering or threading): While one call holds a pool slot, the original builder reenters the hook on the same thread. Which slot does the inner call get, at what moment is the outer call's slot released, including when the original throws, and what happens to a fifth nested call while four are live?
Expected: AcquireSlot scans from index 0 with a compare exchange, so the inner call takes the lowest free slot (slot 1 if the outer holds slot 0). The outer slot is released in the finally right after CopyBack returns, or as the game's exception propagates out of the original, since the original call has no catch. A fifth call finds all four busy, gets -1, and PoolExhausted writes an empty list into its buffer and returns 0 without ever calling the original, warning once per session.
Reader: Inner call gets slot 1 from the lowest first compare exchange scan, the outer slot is released in the finally after the copy-back or as a throw from the original propagates, and a fifth call gets -1 and PoolExhausted returns 0 without calling the original, warning once.
Result: pass
Q3 (free): In CopyBack, what happens when the original reports a count of 0, and what happens when it reports a corrupt count of 600? Give the number of words read from the slot, the count returned, and which counters move.
Expected: Count 0 (or negative) skips the slot read entirely, writes an empty terminator only list to the caller and returns 0, with no counter moved, because the patcher's TryRead refuses a non positive length and would log a false refusal. Count 600 is clamped to SlotBytes/2 minus 1 = 511 words so the read stays inside the 0x400 byte slot, those words are cut to StackCallerCap 149 plus a terminator, Truncations goes up by one, and 149 is returned.
Reader: Count 0 reads nothing, writes a lone terminator and returns 0 with no copy-back counter moving. Count 600 clamps to 511 words read, writes 149 plus a terminator, bumps Truncations and returns 149.
Result: pass
Unprompted: Both empty list fallback writes ignore the TryWrite result, so a buffer that refuses writes keeps stale contents while the caller is told 0. The warn once flags are plain bools with check then set, so two threads could both Warn. The passthrough branch applies no cap or copy-back. Install and Release do no locking around the hook field. The reason for 149 and the meaning of spare and mode are not visible in code.

## Cold read 2026-10-19 11:35, calibration Extended sweep list-relocation, base 2dd77414
Calibration run, outside the cap (0 changed production lines).
Files: LivingWeapon/Extended/ListRelocation.cs, LivingWeapon/Extended/ListRelocation.Fields.cs, LivingWeapon/Extended/ListRelocation.Sites.cs
Hashes: LivingWeapon/Extended/ListRelocation.Fields.cs=c3e28ee0b017697d; LivingWeapon/Extended/ListRelocation.Sites.cs=a648980b6238c435; LivingWeapon/Extended/ListRelocation.cs=b0ff571a94583e34
Canary: cold
Q1 (failure path): During Install, the first 29 field rewrites succeed and the 30th is refused. What state is the process left in afterwards: what happens to the 29 rewritten fields, the new page, Installed, CountBase and PageAddr, and will CheckOldBlocks ever warn about this attempt?
Expected: The 29 written fields are written back to their Vanilla values in reverse order (the results of those rollback writes are ignored), and the 30th is untouched. The page stays allocated, zero filled and holding both list copies; it is simply abandoned (leaked). Installed stays false, CountBase stays Offsets.BagCountArray, SiblingBase stays Offsets.SiblingListArray and PageAddr stays 0. The snapshots were already taken, but CheckOldBlocks returns null because Installed is false. The call returns "field write refused at 0x...".
Reader: The 29 fields are rolled back in reverse with the rollback results ignored, the page is never freed, Installed stays false with PageAddr 0 and both bases on their vanilla defaults, and CheckOldBlocks stays silent because Installed is false even though the snapshots were set.
Result: pass
Q2 (ordering or threading): Why must both list copies land on the new page before the first field rewrite, and why is _installed assigned after PageAddr, CountBase and SiblingBase rather than before?
Expected: As soon as one field points at the page, game code reads and writes the item lists through it, so the page must already hold the live counts and flags (otherwise the game sees zeros). _installed is volatile and is read from other threads (ExtendedInventory.BagCountBase); writing it last publishes the other fields, so any thread that sees Installed true also sees the new page bases, never the vanilla ones.
Reader: Once a field is rewritten the game reads its list from the new page, so without the copies it would see the zero filled page. The volatile write of _installed last has release semantics, so a thread reading Installed true also sees the three new values; it could not name the consumer.
Result: pass
Q3 (free): After a successful Install, the player plays for a while, then Restore succeeds. What item counts does the game see next, what happens to the page and PageAddr, and what would a second Install copy?
Expected: Restore only writes the 55 fields back; it never copies the page back. So the game resumes using the old blocks, which still hold the counts as they were at the original Install, and every change made on the page since is invisible to it. The page is not freed and PageAddr still names it (only CountBase, SiblingBase and Installed reset). A second Install passes the vanilla check, allocates a fresh page and copies those stale old blocks onto it.
Reader: Restore points the game back at the old blocks with no copy back, leaves the page unfreed and PageAddr still naming it, resets only the bases and the flag, and a second Install re-snapshots the old blocks so the play session counts held on the first page are lost.
Result: pass
Unprompted: Every failure after the page is allocated (reach check, zero fill, snapshot, copies) returns without freeing it, and only the field write failure rolls anything back. A partly failed Restore keeps going, leaves Installed true and the bases on the page while some fields are already vanilla, and returns false. Install and Restore have no lock; only the installed flag is volatile. The warned once flag survives Restore and a reinstall, so after one warning the tripwire stays silent forever. The tripwire compares 0x105 bytes against a 0x110 snapshot, which is safe but leaves the last 11 bytes unchecked. The Vanilla, Trail and Off values have no visible source in the stripped code.

## Cold read 2026-10-19 11:35, calibration Extended sweep template, base 2dd77414
Calibration run, outside the cap (0 changed production lines).
Files: LivingWeapon/Extended/TemplateRelocation.cs, LivingWeapon/Extended/TemplateRelocation.Sites.cs, LivingWeapon/Extended/TemplateSync.cs
Hashes: LivingWeapon/Extended/TemplateRelocation.Sites.cs=8c4ac8ff3da878c2; LivingWeapon/Extended/TemplateRelocation.cs=61b448b983f83830; LivingWeapon/Extended/TemplateSync.cs=70aca07d18518de5
Canary: cold
Q1 (failure path): While installed, Restore is called and the write to one of the ten fields is refused. What does Restore return, does it still try the remaining fields, what is Installed afterward, and what do Regions, Project and Adopt do from then on?
Expected: It returns false. It still attempts every other field (the results are combined with &=, which does not short circuit), so the other fields go back to vanilla. Installed stays true, so Regions keeps reporting the page regions and Project and Adopt keep syncing between the page and the old blocks even though some fields now point back at vanilla; a later Restore call retries all ten writes.
Reader: Returns false, still tries all ten writes because &= does not short circuit, Installed stays true, so Regions keeps returning the page regions and Project and Adopt keep working on the page; a retry rewrites all ten.
Result: pass
Q2 (ordering or threading): In Install, why is PageAddr assigned before _installed, and why is _installed volatile? What could another thread calling Project or reading Regions see if the two assignments were swapped?
Expected: _installed is the publish flag. Writing it last with a volatile write means any thread that reads Installed as true is guaranteed to also see the page address. Swapped, a reader could see Installed true while PageAddr is still 0, so Project and Adopt would read and write at the small chart offsets near address 0 and Regions would hand out regions based at 0 instead of the page.
Reader: The volatile flag written after the plain PageAddr means a thread seeing Installed true also sees the address; swapped, a thread could see Installed true with PageAddr 0, so Regions hands out 0x0 and 0x400 and Project and Adopt touch 0 plus the chart offset. It hedged only on which threads call these.
Result: pass
Q3 (free): The inventory chart on the page holds more ids than the old save block can carry. On the save edge, what exactly does Project write into the old block, and what happens to the extra ids across a save and reload?
Expected: It writes the page's words in page order up to the first end marker, truncated to capacity minus one ids (the capacity counts the marker slot), then the end marker, then zero fill out to the old block's byte span. The ids past that point are not in the save; on load Adopt copies only the truncated block back onto the page, so they are dropped.
Reader: Project writes the page words up to the first end marker, clamped to capacity minus one ids, then the marker, then zeros to the span, so the extra ids are dropped from the old block. It noted Adopt overwrites the page with only what the old block carried, but said the save and reload ordering lives in a caller outside the packet.
Result: bad question (audited, no save or load caller in the packet)
Unprompted: A refused Restore leaves a half restored state with Installed still true, and a later Install would allocate a second page and orphan the first. Pages are never freed on any Install refusal and RollBack ignores its own write results. Project and Adopt skip failed reads and ignore failed writes silently. Install has no lock against a concurrent second Install. RegionImage does not check that the span fits the region. The rsi field's minus 0x1E6 offset lands inside the picker region, which reads as a slip unless the reader knows the game indexes from 0x1E6 past that base. Slots, CapSites and Charts build a new array per read while RipFields is a static field.

## Cold read 2026-10-19 11:35, calibration Extended sweep order-rebuild, base 2dd77414
Calibration run, outside the cap (0 changed production lines).
Files: LivingWeapon/Extended/OrderRebuildHook.cs, LivingWeapon/Extended/OrderRebuildHook.Policy.cs, LivingWeapon/Extended/OrderRebuildHook.Seat.cs
Hashes: LivingWeapon/Extended/OrderRebuildHook.Policy.cs=45e8e05b5a5d71dc; LivingWeapon/Extended/OrderRebuildHook.Seat.cs=23e4d7caf57fcd26; LivingWeapon/Extended/OrderRebuildHook.cs=c5156c7b2e9acd0f
Canary: cold
Q1 (failure path): During one rebuild the game drops extended item id 262 from a menu list, and the one byte read of the bag count for id 262 fails. What happens to 262 in the seating step and in the re-append step, and what count does the detour return if 262 was the only dropped word?
Expected: An unreadable bag byte counts as not owned, so 262 is not seated into the template. In the re-append step the dropped word is filtered out unless its word carries the E badge bit (0x4000, every copy worn); without the badge nothing is written and the game's own count is returned unchanged. With the badge it is re-appended anyway and the count grows by one.
Reader: A failed read makes Owned false, so 262 is left out of the seating list, and in re-append it is filtered out and the game's count is returned unchanged. It also named the exception where a word carrying the 0x4000 badge is re-appended anyway.
Result: pass
Q2 (ordering or threading): Process runs seat, then reads the list, then calls the game's rebuild, then re-appends. If seating successfully put id 262 into the weapon template, does the re-append step also add 262 to the list, and why is the call to the original rebuild kept outside any try block?
Expected: No. Because 262 was seated first, the game's own rebuild keeps it, so it is present in the after list and DroppedWords skips it (match is on the masked id). The original is called exactly once and outside a try so its own exception is never swallowed or followed by a second call; only the seating and re-append halves are guarded, and on a re-append exception the game's count stands.
Reader: It got the mechanics (re-append only adds an id missing from the after list) but could not say the game's rebuild keeps a seated id, since that is game behavior, and it could not say why the original call sits outside the try.
Result: bad question (audited, the why rests on game behavior outside the packet)
Q3 (free): When the re-append succeeds, where exactly does it write, how many bytes, and why can that write never run past what the list held before the rebuild?
Expected: It writes at list plus after.Length times 2 (right where the old terminator sat): the dropped words as little endian u16 plus a new 0xFFFF terminator, so (dropped + 1) times 2 bytes. It only writes when after.Length + dropped.Length is at most before.Length, the input's own word count, so the new terminator lands no later than the old input terminator did. Otherwise it warns and returns the game's count.
Reader: It writes at the after list's terminator slot, dropped words plus a 0xFFFF terminator, and the guard keeps the end at or before the old input terminator slot.
Result: pass
Unprompted: The detour reads the hook field before it is assigned, so a call landing between Activate and the assignment would throw a null error inside the detour. The success path returns the after length plus dropped rather than the game's count plus dropped, which quietly assumes the two agree. Empty catches in Process swallow seat and re-append failures with no log, and a list with no terminator skips the re-append silently. The badge rule re-appends a badged word whether or not it is owned, with no visible reason. The first refusal flag is a plain bool while the counters use Interlocked. The repaired branch returns before counting seated ids. Release only disables the hook, and Install re-enables rather than reinstalls. DroppedWords keeps only the first word per id.

## Cold read 2026-10-19 11:35, calibration Extended sweep thunks, base 2dd77414
Calibration run, outside the cap (0 changed production lines).
Files: LivingWeapon/Extended/ThunkStub.cs, LivingWeapon/Extended/ThunkStub.Trampoline.cs, LivingWeapon/Extended/ExtendedDonorThunks.cs, LivingWeapon/Offsets.cs
Hashes: LivingWeapon/Extended/ExtendedDonorThunks.cs=dff4e2c71951d3cc; LivingWeapon/Extended/ThunkStub.Trampoline.cs=3069287d77b217d6; LivingWeapon/Extended/ThunkStub.cs=7a6d9542bd8a0a2e; LivingWeapon/Offsets.cs=a98e6ca36266eeca
Canary: cold
Q1 (failure path): The swing id fallback stub is built with lo=261 and hi=511. The standing word reads 0, and the hand word at handsBase+rsi reads 0x0405 (a stray high bit on id 5). What value leaves the stub in eax, and where does execution go next? Contrast it with what a donor stub built with lo=261 does when it is entered with ecx=0x0405.
Expected: The swing stub compares the hand word unmasked, so 0x405 is above hi, the ja takes the zero arm, eax becomes 0, and it jumps through the absolute jmp to returnAddr. The donor stub masks first (and eax with 0x3FF gives 5), 5 is below lo, so it jumps straight to the original target with ecx still 0x0405 untouched (only eax was clobbered with the masked id).
Reader: Traced both byte by byte. Swing stub takes the ja to xor eax,eax and jumps to returnAddr with 0; the donor stub masks to 5, takes jb to the absolute jump and runs the original with ecx still 0x0405.
Result: pass
Q2 (ordering or threading): When the range-base entry hook's trampoline runs, trace the control flow from its first byte to the moment it leaves the trampoline. For the relocated call, which entry is the target (name the entry, and is that entry hooked too), where does the callee return to, and at what address does the trampoline resume the original function?
Expected: It runs the stolen sub rsp,28h, then call [rip+2] through an absolute target computed as entryAddr+4+5+rel32 (rel32 0xFFFFFFB7), which is the range-index entry. That entry is itself hooked, so the call first enters the range-index hook and its stub before the original range-index body. The callee returns to the EB 08 right after the call, which hops the 8-byte target address, then (no stolen bytes after the call) the jmp [rip+0] resumes the original at entryAddr + 9, the end of the stolen span.
Reader: Computed the target as range base minus 0x40, matched it to ThunkRangeIndex, had the callee return to the EB 08 hop and resume at entryAddr + 9. On whether range index is hooked it said only that the entry is listed in DonorThunks so it is a hook candidate, and that the installer is outside the packet.
Result: miss, fix name (audited, nothing in the code says the donor entries are hooked themselves)
Q3 (free): For an id inside [lo, hi], how do the donor stub and the row stub differ in what they hand back to the game, and does either one still run the original accessor? Which registers can each stub change?
Expected: The donor stub replaces ecx with donors[id minus lo] read from the table at the stub's end and then jumps into the original accessor, so the original runs with a substitute id. The row stub never runs the original; it returns rax pointing at that id's own 8-byte row inside the stub (header 0x38 plus (id minus lo) times 8) with a ret. Both touch only rax and rcx (and flags); the pass through arm leaves rcx intact with eax clobbered by the masked id.
Reader: Donor stub loads donors[id minus lo] into ecx and jumps to the original; row stub returns a pointer to the 8 byte row in rax with ret and skips the original. Both change only rax, rcx and flags, and out of range ecx survives while eax is clobbered.
Result: pass
Unprompted: The swing stub checks hi against IdMask yet never masks, so any stray high bit gives 0 and a nonzero standing word goes out unchecked. The donor and row emitters do not validate lo or hi against the mask or zero. EncodeThunkJmp returns null silently when the stub is out of 2 GB reach, so the caller must check. The trampoline relocates only one E8 call and would copy any other rip relative bytes unrelocated, and it trusts the stolen span ends on an instruction boundary. The relocated call target is read from the stolen bytes, which is only right if they were read before patching. WeaponStat shares its expected bytes with the four siblings. In Offsets, InventoryCountBase and BagCountArray are the same address, and a few bases hardcode 24 and 16 slot counts instead of a named constant.

## Cold read 2026-10-19 11:35, calibration Extended sweep small-hooks, base 2dd77414
Calibration run, outside the cap (0 changed production lines).
Files: LivingWeapon/Extended/AccessorEntryHook.cs, LivingWeapon/Extended/CategoryGetterHook.cs, LivingWeapon/Extended/SwingIdFallbackHook.cs
Hashes: LivingWeapon/Extended/AccessorEntryHook.cs=0a12cc2c9ba9300d; LivingWeapon/Extended/CategoryGetterHook.cs=d3304f050967c6a0; LivingWeapon/Extended/SwingIdFallbackHook.cs=5b47697c69972c8e
Canary: cold
Q1 (failure path): In AccessorEntryHook.Install, the trampoline and stub writes succeed but the final write over the entry is refused. What state remains in the entry bytes, the allocated page, Installed and the saved original, and what does a later Restore call do?
Expected: The entry still holds its original bytes, so the game never reaches the hook. The page stays allocated with the trampoline and stub in it and is never freed (leaked). Installed stays false and the original bytes are not saved, so Restore returns true without writing anything. A retry of Install would allocate a fresh page.
Reader: Entry unchanged, page stays allocated holding trampoline and stub with nothing freeing it, Installed false, original null, Restore returns true with no write, and a retry allocates a new page.
Result: pass
Q2 (ordering or threading): Both AccessorEntryHook and SwingIdFallbackHook write their page contents first and the jump over the game code last, and Restore only rewrites the original bytes without freeing the page. Why that order, and why not free the page on Restore?
Expected: The jump over the game code is what lets game threads enter the page, so it must land only after the trampoline and stub are fully in place, or a thread could execute a half written page. The page is not freed on Restore because a game thread may still be executing inside the stub or trampoline at that moment; freeing it would crash that thread.
Reader: Cannot answer from the packet. It saw the page writes come before the site write and that Restore never touches the page, but would not claim why, since nothing in the stripped code mentions other threads.
Result: bad question
Q3 (free): SwingIdFallbackHook overwrites a 7 byte instruction. Why does it put exactly two 0x90 bytes after the jump, and with lo = 300 and count = 5, which ids does the stub treat as ours?
Expected: The jump is 5 bytes and the original movzx is 7, so the two leftover bytes are padded with NOPs so no fragment of the old instruction is left at the site (the stub returns to site plus 7 anyway). The stub covers ids 300 through 304 inclusive (lo to lo + count minus 1).
Reader: The two NOPs fill the 7 byte instruction after a 5 byte jump, deduced from the arithmetic. The stub gets 300 and 304 as bounds via lo + count minus 1, though it hedged on inclusivity since the emitter is not in the packet.
Result: pass
Unprompted: CategoryGetterHook assigns its hook field only after Activate returns, so a game call landing in that window would hit a null hook inside Detour and throw on a game thread. Release swallows a failed disable silently. AccessorEntryHook copies the jump into the entry buffer outside any guard, so an expected entry shorter than the jump would throw instead of refusing. SwingIdFallbackHook passes a hardcoded true into ShouldArm, which makes that parameter look meaningful when it is not. Neither Restore can undo a partly written entry because the original is saved only after all writes succeed.

## Cold read 2026-10-19 11:35, calibration Extended sweep catalog, base 2dd77414
Calibration run, outside the cap (0 changed production lines).
Files: LivingWeapon/Extended/ExtendedCatalog.cs, LivingWeapon/Extended/ExtendedRecords.cs, LivingWeapon/Extended/ExtendedSites.cs
Hashes: LivingWeapon/Extended/ExtendedCatalog.cs=ade0bf576173431a; LivingWeapon/Extended/ExtendedRecords.cs=ebf20b31414339f6; LivingWeapon/Extended/ExtendedSites.cs=714000961d7202aa
Canary: cold
Q1 (failure path): In ExtendedCatalog.Install, the allocator succeeds and the zero fill succeeds, but then the write of one caller supplied record (say id 300) is refused. What state is the catalog left in (Installed, BufferAddr, the saved disp32, the game's accessor, the allocated buffer), and what does a second Install call afterwards do?
Expected: Install returns a refusal string naming the id. Installed stays false, BufferAddr stays unset (0) and the saved disp32 is not recorded, and the disp32 is never written, so the game's accessor still points at the vanilla block. The allocated buffer is simply abandoned (nothing frees it). A second Install starts over: the disp32 still reads vanilla so the check passes, and it allocates a fresh buffer.
Reader: It named the refusal string, said Installed, BufferAddr and the saved disp32 all stay unset, the accessor stays vanilla, the buffer is abandoned, and a retry passes the vanilla check and allocates again.
Result: pass
Q2 (ordering or threading): Install writes, in order, the zero fill, the vanilla 256..260 copy, the caller records, and only then the disp32. Why must the disp32 come last, and what would break if it moved to just after allocation and a later record write then failed? Also, if a caller record carried id 258, which write would win?
Expected: The disp32 write is what makes the game's accessor read the new buffer, so the buffer must be fully populated first; moved earlier, the game could read zeros where the 256..260 "+" records should be, and a later failure would return with Installed false while the accessor points at a half built buffer, and Restore would then do nothing because it returns early when not Installed. Id 258 can never reach the write loop: the validation loop refuses any id outside 261..511 before anything is written, so caller records never overwrite the copied vanilla records.
Reader: It said the disp32 is the redirect so it must follow a full buffer, that moving it up leaves the game reading a half filled buffer while Installed is false so Restore does nothing, and that id 258 is refused by the up front range check before anything is written.
Result: pass
Q3 (free): For an extended count N, what byte does ExtendedSites write at the shop buy-list loop site, at the count-getter site, and at a PlusN site whose old byte is 0x06? Does anything in ExtendedSites stop an N too large for those bytes, and what is the valid id range a caller may hand ExtendedCatalog.Install?
Expected: The shop loop site gets 0x05 + N (its old byte 0x00 is ignored), the count-getter (HighByte kind) gets 0x02 whatever N is, and a PlusN site gets old + N, so 0x06 + N, cast to a byte with no check. Nothing in ExtendedSites enforces MaxExtendedCount (121); Build and NewByte accept any n and silently wrap, so the caller must enforce the limit. Install accepts only ids 261..511 with 12 byte records, refusing anything else before touching memory.
Reader: It gave 0x05 + n, a fixed 0x02, and 0x06 + n, said nothing in the file checks n or references MaxExtendedCount so the byte casts wrap, and gave ids 261 to 511 with 12 byte records.
Result: pass
Unprompted: A failed Install after allocation leaks the buffer and every retry allocates another. A refused Restore write leaves Installed true, and a good Restore still leaves BufferAddr set. Only ids 256 to 260 are copied, so ids below 256 read zero if the game ever reads them through this base. The HighByte widen kind ignores N and always writes 0x02, so the name oversells it. The Old byte on each site is never compared against the live byte in this packet. PostLoadSites is empty, so PostLoadPatches always returns nothing. Nothing asserts that 512 records of 12 bytes fit the 8192 byte buffer. Unused_2 is an accepted type flag despite its name.

## Cold read 2026-10-19 11:35, calibration Extended sweep patches-shop, base 2dd77414
Calibration run, outside the cap (0 changed production lines).
Files: LivingWeapon/Extended/BytePatchSet.cs, LivingWeapon/Extended/ShopFlagsMirror.cs
Hashes: LivingWeapon/Extended/BytePatchSet.cs=36bdc87f4cba40b0; LivingWeapon/Extended/ShopFlagsMirror.cs=e04f9a27f281cd5b
Canary: cold
Q1 (failure path): In ShopFlagsMirror.Install, suppose the allocator returns a page and the row writes succeed, but the write to the high-byte lea site is refused. What is the state of the two builder sites, the mirror page, Installed and MirrorAddr afterwards, and is the "nothing changed" promise fully true?
Expected: The low-byte disp32 site is written back to its saved vanilla bytes so the two halves are never split, and the high-byte site was never changed, so both sites read vanilla. Installed stays false and MirrorAddr is not set. The promise is not fully true: the allocated, zero-filled and row-filled mirror page is left allocated (leaked, never freed), and the saved rel32/disp32 fields have been assigned. The low-byte restore write is itself unchecked.
Reader: Both sites end at vanilla because the low byte is written back, Installed and MirrorAddr stay unset, the saved fields stay set, and the filled page leaks with a retry allocating another. It also flagged the unchecked restore write.
Result: pass
Q2 (ordering or threading): In BytePatchSet.Apply with five patches, the third write is refused. Which bytes end up in memory, in what order are they restored, what does the refusal message report, and why does a byte mismatch at site five never leave sites one to four written?
Expected: Patches one and two had landed, so Rollback writes their Old bytes back newest first (two, then one), clears the applied list (AppliedCount 0), and the message says "2 earlier patch(es) rolled back". Site three is untouched. A mismatch at any site is caught in the first loop, which reads and checks every site before the second loop writes anything, so a mismatch refuses with nothing written. Rollback ignores its own write failures.
Reader: Patch two then patch one are restored to their old bytes, the message reports 2 rolled back, and the read and compare loop finishes before any write so a mismatch writes nothing. It noted that Rollback ignores failed restore writes.
Result: pass
Q3 (free): Why can ShopFlagsMirror.Sync recopy the vanilla table into the mirror on every change without erasing the flags for extended ids, and how many item ids could the mirror page address?
Expected: Each id's flags are two bytes at mirror + id * 2. Sync writes only the first 512 bytes (the 256 vanilla rows, ids 0 to 255), while extended rows (id 261 and up) start at byte 522, past what Sync touches. The page is 4096 bytes, so it addresses ids 0 to 2047.
Reader: It got 2048 ids and that Sync only covers ids 0 to 255, but could not confirm the extended rows sit past that because the first extended id lives in ExtendedCatalog, which was not in the packet.
Result: bad question
Unprompted: Restore tries both sites even if one fails, and on a partial failure leaves Installed true with the two halves possibly split. Sync returns false both for unchanged and for a failed write, so a caller cannot tell them apart. A second Apply on BytePatchSet silently reports success even with different patches. PendingPatch can land in Unwritable after the byte was actually written (read back mismatch) and has no undo. The plus one on the high byte lea target implies a split byte row layout the code never explains. It also worried ids past 2047 could write off the page, which cannot happen since the last extended id is 511.

## Cold read 2026-10-19 13:42, LW-458, base 208d7468
Run cap 1 (47 changed production lines). Run 1 of 1.
Files: LivingWeapon/Engine.cs, LivingWeapon/Extended/ExtendedInventory.cs, LivingWeapon/Extended/RewindRestoreHook.cs, LivingWeapon/Kills/KillTracker.cs, LivingWeapon/Kills/RestartSentinel.Policy.cs, LivingWeapon/Kills/RestartSentinel.cs
Hashes: LivingWeapon/Engine.cs=bcbeae7d0bbc118e; LivingWeapon/Extended/ExtendedInventory.cs=4ecfabd0e4fdf63b; LivingWeapon/Extended/RewindRestoreHook.cs=a94aba7223444b84; LivingWeapon/Kills/KillTracker.cs=dd1b267b60f1ccb2; LivingWeapon/Kills/RestartSentinel.Policy.cs=13c7824ed0d8841f; LivingWeapon/Kills/RestartSentinel.cs=b2fb39bf7a580231
Q1 (failure path): The checkpoint restore hook fires, but the kill tracker was never told where to read the restore count (the source was never wired). What does the retry guard see each tick, and does anything throw or change compared with before this change?
Expected: Poll passes 0 every tick, so the count never rises past its baseline, no hook stamp is ever made, and the guard behaves exactly as before, relying on the pointer null streak alone. Nothing throws.
Reader: Poll passes 0 every tick, the first tick takes the baseline, no stamp is ever made, the null streak path is untouched, nothing throws. Could not compare with the old code, which the packet lacks.
Result: pass
Q2 (ordering): During a Retry the field reads not live for longer than the re arm threshold, and the checkpoint restore lands somewhere inside that dark stretch. Does the re arm wipe the hook stamp, and what does the battle age read when the revive is presented?
Expected: The re arm clears the null stamp, the latch, the stash and the battle age, but not the hook stamp, so a revive within 150 ticks of the restore still opens the latch. Battle age restarts from the re arm, which is why the hook path ignores the grace.
Reader: The re arm zeroes the latch, null streak, qualified null, battle age and stash but not the hook stamp, so a revive within 150 ticks still opens. Battle age counts from the last re arm and the hook path ignores it.
Result: pass
Q3 (free): The very first time the sentinel ticks in a session the restore count already reads 5 from earlier battles. Is that treated as a fresh Retry, and what makes a later Retry count?
Expected: No. The first tick only stores the count as a baseline (the stored value starts at minus one). Only a later tick reading a count higher than the stored one stamps the hook, and ResetBattle clears the stamp but keeps the stored count.
Reader: No. The stored count starts at minus one so the first tick only sets the baseline to 5. A later higher count stamps. Flagged that a hook armed late could read as a jump.
Result: pass
Unprompted: The restore counter rises even when no bag record was found, and that still counts as a Retry, as intended. ProcessCheckpoint and ProcessRewind swallow exceptions silently. A fourth question the reader invented about the Mods folder was ignored.

## Cold read 2026-10-19 23:22, Extended sweep trial, base 7f157f7c
Run cap 2 (225 changed production lines). Runs used 1 of 2.
Files: LivingWeapon/Extended/RewindTotals.cs, LivingWeapon/Extended/RewindTotals.Policy.cs, LivingWeapon/Extended/RewindRecomputeStep.cs, LivingWeapon/Offsets.cs
Hashes: LivingWeapon/Extended/RewindRecomputeStep.cs=2138d76926d77d5a; LivingWeapon/Extended/RewindTotals.Policy.cs=33179b750b931408; LivingWeapon/Extended/RewindTotals.cs=73a058db254f275e
Stage 1 (cold read): run 1, 3 pass, canary cold
Stage 2 (plain words): stripped code identical before and after in 3 file(s); comment lines 21 before stage 1, 21 after; rules clean
Stage 3 (warnings): 0 warning(s) found; 0 fixed in code, 0 kept with a reason
Stage 4 (accuracy): 0 comment(s) flagged over 1 read(s); 0 fixed and confirmed by a fresh read, 0 reader wrong, 0 open
### Run 1
Canary: cold
Q1 (failure path): Recompute runs with a captured total, the roster hands read fine, but one bag byte is unreadable. What does it write, which counters move, what does change hold, what does it return, and what does RewindRecomputeStep.Step then log or record?
Expected: ReadBag returns null, so nothing is written. Recomputes and Incomplete each go up by one and LastReason is set. change is "a roster or bag read failed" and it returns false. Step logs no Warn and no Event, because nothing was written or refused, but change is not empty so it records one flight entry "rewind-recompute" with that text.
Reader: Nothing written, Recomputes and Incomplete up one, LastReason set, change "a roster or bag read failed", false; Step logs neither Warn nor Event and records one rewind-recompute flight entry with that text.
Result: pass
Q2 (ordering or threading): The checkpoint restore counter rises on tick 100 and the flee rewind counter rises on tick 110, with Step called every tick. On which ticks does Recompute run, and with what reason each time?
Expected: Tick 100 schedules passes at 103 and 115. Recompute runs at 103 with reason "checkpoint restore". Tick 110 clears the pending 115 and schedules 113 and 125, and the reason becomes "flee rewind". Recompute runs at 113 and 125 with reason "flee rewind". The 115 pass never runs.
Reader: Runs at 103 with the first source's name, then 113 and 125 with the second's; the second Schedule clears the 115 pass. It noted the names themselves are constructor arguments outside the packet.
Result: pass
Q3 (free): ReadHands returns null when a single roster row cannot be read, instead of counting the rows it could read. Why, and what does PutBack do in that case?
Expected: A short hands count makes total minus hands too large, so the bag would be written with copies the party does not own, minting weapons. With null, PutBack still adopts the totals as Latest and bumps Captures, but writes nothing, bumps Incomplete and returns an empty map.
Reader: Read the why from BagFor, a hands count that skipped rows is too low so a too high bag value gets written, and null lets callers refuse; PutBack sets Latest, bumps Captures, then bumps Incomplete, writes nothing and returns an empty map.
Result: pass
Unprompted: A throw partway through Recompute's write loop leaves some bytes written with no correction counted. LastReason is set before the read failure check and is not under the lock. Step reads each counter twice, so a rise between the two reads is folded in silently. wrote leans on a string ending in "refused". A null Latest consumes the due pass with no trace.

## Cold read 2026-10-19 23:25, Extended sweep trial 2, base 7f157f7c
Run cap 1 (136 changed production lines). Runs used 1 of 1.
Files: LivingWeapon/Extended/InventoryResetHook.cs
Hashes: LivingWeapon/Extended/InventoryResetHook.cs=c50a2a51fcd7254a
Stage 1 (cold read): run 1, 2 pass, 1 miss, fix comment, canary cold
Stage 2 (plain words): stripped code identical before and after in 1 file(s); comment lines 32 before stage 1, 28 after; rules clean
Stage 3 (warnings): 1 warning(s) found; 0 fixed in code, 1 kept with a reason
Stage 4 (accuracy): 0 comment(s) flagged over 1 read(s); 0 fixed and confirmed by a fresh read, 0 reader wrong, 0 open
### Run 1
Canary: cold
Q1 (failure path): The detour runs, and the read of the new items' bag bytes before the reset fails. What does the detour do, what is written, which counters move, and what is logged?
Expected: before is null, the game's reset still runs once, Restore returns at once, nothing is written, Runs goes up by one and Restores does not, and nothing is logged or recorded.
Reader: before is null, the reset still runs and its result returns, Restore exits at once, nothing written, Runs moves and Restores does not, nothing logged.
Result: pass
Q2 (ordering or threading): In Process, in what order do the Runs count, the bag read, the game's reset and the restore happen, and what happens if the game's reset throws, or if the restore throws?
Expected: Runs is counted first, then the bag is read, then the game's reset is called once outside any try, then the restore runs inside a try. A throw from the game's reset goes straight back to the game and no restore happens. A throw from the restore is swallowed and the game's result is still returned.
Reader: Runs, then the read, then the reset unguarded, then Restore in a try, then the return; a reset throw skips Restore and leaves Process, a Restore throw is swallowed and the result returns.
Result: pass
Q3 (free): Why does Restore read the bytes again and compare them with the saved ones before writing, and what does the kept number in its log line count?
Expected: The reset only sometimes wipes the bytes, so it writes and logs only when they changed, which keeps a reset that left them alone silent. kept counts the new items that had at least one copy before the reset. The first restore logs at Info and later ones at Debug, and every restore is recorded in the flight log.
Reader: Got the compare and the silent no change path and kept as the nonzero saved bytes, but said the code gives no reason for the compare and did not name the Info then Debug logging.
Result: miss, fix comment (the reset not always touching the bytes is a game fact no code can say)
Unprompted: A restore writes the whole saved range back when any byte differs. The bag read sits outside any try, safe only because ICodePatcher never throws. The first restore flag is a plain bool. Release only disables the hook.
### Stage detail
W1: LivingWeapon/Extended/InventoryResetHook.cs:63 | /// <summary>Null on success, else why not. Safe to call twice.</summary>
Resolution: kept, it promises a second Install is harmless rather than warning of a trap, and the early return already enforces it

## Cold read 2026-10-20 00:51, Seeded trial, base 7f157f7c
Run cap 1 (87 changed production lines). Runs used 1 of 1.
Files: LivingWeapon/Extended/CategoryGetterHook.cs
Hashes: LivingWeapon/Extended/CategoryGetterHook.cs=d3304f050967c6a0
Guard: {"LivingWeapon/Extended/CategoryGetterHook.cs":{"budget":13,"kept":["// Must not be installed before the catalog, because the donors answer for ids only the catalog adds. /// <summary>Null on success, else the refusal. Idempotent.</summary>","/* swallowed: release must never throw */"],"raw":"445437017c1862a2"}}
Stage 1 (cold read): run 1, 2 pass, 1 bad question, canary WARM
Stage 2 (plain words): stripped code identical before and after in 1 file(s); comment lines 13 before stage 1, 13 after; rules clean
Stage 3 (warnings): 2 warning(s) found (2 only in the original comments); 0 fixed in code, 2 kept with a reason; no code changed
Stage 4 (accuracy): 1 comment(s) flagged over 2 read(s); 1 fixed and confirmed by a fresh read, 0 reader wrong, 0 open; control caught on 2 counted read(s), missed on 0 refused read(s); 5 unsourced reason(s)
### Run 1
Canary: WARM
Q1 (failure path): In Install, the prologue matches but CreateHook throws. What does Install return, what state are _hook and _keepalive left in, and what does a second Install call do?
Expected: It returns "category-getter: hook install failed (<message>)". _keepalive is already set to the Detour delegate but _hook stays null, because the throw happens before the assignment. A second Install sees _hook null, so it reads and checks the prologue again and tries CreateHook again.
Reader: Returns the install failed message, _keepalive set, _hook null because the assignment never happens, a second Install skips the guard, rereads the prologue and tries CreateHook again.
Result: pass
Q2 (ordering or threading): A call reaches Detour. In what order do the two counters move and the original get called, for an id inside the donor range and for one outside it, and which value does the original receive in each case?
Expected: _calls goes up first on every call. Resolve masks rcx with 0x3FF and looks the id up. Outside the range it returns -1, so the original gets the untouched rcx and _redirected does not move. Inside the range _redirected goes up, then the original gets the donor id alone, so any flag bits above the mask are dropped.
Reader: _calls first, then Resolve; inside the range _redirected then the original with the donor value, outside it the original with the unchanged rcx and no _redirected. It said the mask value is outside the packet.
Result: pass
Q3 (free): Why is a managed Reloaded.Hooks detour safe for this getter, when the per item accessors needed AccessorEntryHook instead?
Expected: The getter's callers keep no volatile registers live across the call, so a managed detour that clobbers r11 does no harm. The accessors' callers do keep r11 live, and a managed detour there crashed the game, so they got hand written stubs that touch only rax and rcx.
Reader: Cannot answer from the packet, missing AccessorEntryHook and the per item accessor code.
Result: bad question (needs AccessorEntryHook)
Unprompted: Detour dereferences _hook with a bang, and Activate can enable the hook before the assignment lands, so an early call would throw inside the game. Release swallows every failure, so a failed Disable leaves the hook live with no trace. Release never clears _hook, so IsActive cannot tell a released hook from one never installed.
### Stage detail
Read 1 control planted in LivingWeapon/Extended/CategoryGetterHook.cs as _spareTally9e7d = 62, caught, 12s
Read 1 F2: LivingWeapon/Extended/CategoryGetterHook.cs | `/// <summary>Always returns a donor id for the raw rcx; it never hands back -1.</summary>` | `Resolve` returns -1 when `i < 0` or `i >= donors.Length`. `Detour` relies on that with `if (donor < 0)`.
Read 1 U1: LivingWeapon/Extended/CategoryGetterHook.cs | `// The catalog goes in first, so the donors find the ids it adds.` | unsourced reason
Read 1 U2: LivingWeapon/Extended/CategoryGetterHook.cs | `/* a release that throws would take the rollback down with it */` | unsourced reason
Read 2 control planted in LivingWeapon/Extended/CategoryGetterHook.cs as _spareTally7009 = 46, caught, 11s
Read 2 U1: LivingWeapon/Extended/CategoryGetterHook.cs | A managed detour is safe here because the getter's callers keep no volatile registers live across the call (as with PromptSwapHook). | unsourced reason
Read 2 U2: LivingWeapon/Extended/CategoryGetterHook.cs | The per item accessors' callers do, which is why they use AccessorEntryHook instead. | unsourced reason
Read 2 U3: LivingWeapon/Extended/CategoryGetterHook.cs | // The catalog goes in first, so the donors find the ids it adds. | unsourced reason
Read 2 U4: LivingWeapon/Extended/CategoryGetterHook.cs | /* a release that throws would take the rollback down with it */ | unsourced reason
Read 2 U5: LivingWeapon/Extended/CategoryGetterHook.cs | // Runs on every item the menus touch, so it stays free of logging and allocation. | unsourced reason
W1 (original, reworded or removed in stage 2): LivingWeapon/Extended/CategoryGetterHook.cs:55 | // Must not be installed before the catalog, because the donors answer for ids only the catalog adds. /// <summary>Null on success, else the refusal. Idempotent.</summary>
Resolution: kept, the install order is set in ExtendedInventory.Arm.cs and this class cannot see or enforce it
W2 (original, reworded or removed in stage 2): LivingWeapon/Extended/CategoryGetterHook.cs:75 | /* swallowed: release must never throw */
Resolution: kept, the empty catch already enforces it and no type can say a rollback must not throw
Unprompted rows: 3 new (LW-468, LW-469, LW-470), 0 repeated (none)

## Cold read 2026-10-20 00:58, Rerun trial 1, base 7f157f7c
Run cap 2 (225 changed production lines). Runs used 2 of 2.
Files: LivingWeapon/Extended/RewindTotals.cs, LivingWeapon/Extended/RewindTotals.Policy.cs, LivingWeapon/Extended/RewindRecomputeStep.cs, LivingWeapon/Offsets.cs
Hashes: LivingWeapon/Extended/RewindRecomputeStep.cs=2138d76926d77d5a; LivingWeapon/Extended/RewindTotals.Policy.cs=33179b750b931408; LivingWeapon/Extended/RewindTotals.cs=73a058db254f275e
Guard: {"LivingWeapon/Extended/RewindRecomputeStep.cs":{"budget":5,"kept":["/* a fault here must never reach the tick */"],"raw":"b0da6dc45d984232"},"LivingWeapon/Extended/RewindTotals.Policy.cs":{"budget":5,"kept":[],"raw":"871c949e89117c9e"},"LivingWeapon/Extended/RewindTotals.cs":{"budget":11,"kept":["// a fault must never reach the tick"],"raw":"4798c32a5c72b00c"}}
Stage 1 (cold read): run 1, 1 pass, 1 bad question, 1 miss, fix comment, canary cold
Stage 2 (plain words): stripped code identical before and after in 3 file(s); comment lines 21 before stage 1, 19 after; rules clean
Stage 3 (warnings): 2 warning(s) found (2 only in the original comments); 0 fixed in code, 2 kept with a reason; no code changed
Stage 4 (accuracy): 0 comment(s) flagged over 1 read(s); 0 fixed and confirmed by a fresh read, 0 reader wrong, 0 open; control caught on 1 counted read(s), missed on 0 refused read(s); 3 unsourced reason(s)
### Run 1
Canary: cold
Q1 (failure path): Recompute runs with a captured total, the roster hands read fine, but one bag byte is unreadable. What does it write, which counters move, what does change hold, what does it return, and what does RewindRecomputeStep.Step then log or record?
Expected: ReadBag returns null, so nothing is written. Recomputes and Incomplete each go up by one and LastReason is set. change is "a roster or bag read failed" and it returns false. Step logs no Warn and no Event, because nothing was written or refused, but change is not empty so it records one flight entry "rewind-recompute" with that text.
Reader: Nothing written, Recomputes and Incomplete rise, Refused and Corrections do not, change is "a roster or bag read failed", returns false; Step fires neither Warn nor Event and records one rewind-recompute entry with that text.
Result: pass
Q2 (ordering or threading): The checkpoint restore counter rises on tick 100 and the flee rewind counter rises on tick 110, with Step called every tick. On which ticks does Recompute run, and with what reason each time?
Expected: Tick 100 schedules passes at 103 and 115. Recompute runs at 103 with reason "checkpoint restore". Tick 110 clears the pending 115 and schedules 113 and 125, and the reason becomes "flee rewind". Recompute runs at 113 and 125 with reason "flee rewind". The 115 pass never runs.
Reader: Runs at 103, 113 and 125, the second Schedule drops the 115 pass; for the reason strings it said cannot answer from the packet, missing the construction site of RewindRecomputeStep.
Result: bad question (needs DefaultInstallHooks)
Q3 (free): ReadHands returns null when a single roster row cannot be read, instead of counting the rows it could read. Why, and what does PutBack do in that case?
Expected: A short hands count makes total minus hands too large, so the bag would be written with copies the party does not own, minting weapons. With null, PutBack still adopts the totals as Latest and bumps Captures, but writes nothing, bumps Incomplete and returns an empty map.
Reader: Read from BagFor that a partial hand count is too low and would push a too large bag value into the write, and said that is a consequence in the code rather than a stated reason; PutBack sets Latest, bumps Captures, then bumps Incomplete, writes nothing and returns an empty map.
Result: miss, fix comment (the why is a consequence of BagFor the reader would not state as intent; the ReadHands summary now says it)
Unprompted: Step and Recompute swallow exceptions with no log or counter. A null Latest makes Recompute return false with no trace while the due pass is spent. wrote rests on a name not ending in "refused". Step reads each counter twice per tick. LastReason is written outside the lock. ReadHands counts only RRHand and ROffHand and never RLHand or RShield. Capture stores unclamped totals while BagFor clamps to 0..255. Offsets.InventoryCountBase and Offsets.BagCountArray are two names for 0x1411A7C00.
### Stage detail
Read 1 control planted in LivingWeapon/Extended/RewindRecomputeStep.cs as _holdTicks3c37 = 42, caught, 17s
Read 1 U1: LivingWeapon/Extended/RewindTotals.cs | "// a throw here would stop every later step in the tick" | unsourced reason
Read 1 U2: LivingWeapon/Extended/RewindRecomputeStep.cs | "Counters are read through functions because their hooks may arrive late." | unsourced reason
Read 1 U3: LivingWeapon/Extended/RewindRecomputeStep.cs | "/* a throw here would stop every later step in the tick */" | unsourced reason
W1 (original, reworded or removed in stage 2): LivingWeapon/Extended/RewindTotals.cs:135 | // a fault must never reach the tick
Resolution: kept, the empty catch is the whole guard and no type can say a tick step must not throw
W2 (original, reworded or removed in stage 2): LivingWeapon/Extended/RewindRecomputeStep.cs:47 | /* a fault here must never reach the tick */
Resolution: kept, the empty catch is the whole guard and no type can say a tick step must not throw
Unprompted rows: 7 new (LW-471, LW-472, LW-473, LW-474, LW-475, LW-476, LW-477), 0 repeated (none)

## Cold read 2026-10-20 11:45, Seeded trial build 3, base 7f157f7c
Run cap 1 (87 changed production lines). Runs used 1 of 1.
Files: LivingWeapon/Extended/CategoryGetterHook.cs
Hashes: LivingWeapon/Extended/CategoryGetterHook.cs=17437a8ea40e9f3c
Guard: {"LivingWeapon/Extended/CategoryGetterHook.cs":{"budget":13,"kept":["a release that throws would take the rollback down with it"],"raw":"76e2fe0eec9766b2"}}
Supersedes: Cold read 2026-10-20 00:51, Seeded trial, base 7f157f7c | rerun on the third build from the original file
Stage 1 (cold read): run 1, 2 pass, 1 bad question, canary cold
Stage 2 (plain words): stripped code identical before and after in 1 file(s); comment lines 13 before stage 1, 13 after; rules clean
Stage 3 (warnings): 2 warning(s) found (1 only in the original comments); 1 fixed in code, 1 kept with a reason; code changed in CategoryGetterHook.cs (3 lines)
Stage 4 (accuracy): 1 comment(s) flagged over 2 read(s); 1 fixed and confirmed by a fresh read, 0 reader wrong, 0 open; control kind missing member and wrong return caught on 2 counted read(s), missed on 0 refused read(s); 3 unsourced reason(s); 0 kept warning(s) code could enforce
### Run 1
Canary: cold
Q1 (failure path): In Install, the prologue matches but CreateHook throws. What does Install return, what state are _hook and _keepalive left in, and what does a second Install call do?
Expected: It returns "category-getter: hook install failed (<message>)". _keepalive is already set to the Detour delegate but _hook stays null, because the throw happens before the assignment. A second Install sees _hook null, so it reads and checks the prologue again and tries CreateHook again.
Reader: Returns the install failed message from the catch, _keepalive stays set to Detour, _hook stays null because the assignment comes after CreateHook, and a second Install skips the early return, rereads and rechecks the prologue and tries CreateHook again, overwriting _keepalive.
Result: pass
Q2 (ordering or threading): A call reaches Detour. In what order do the two counters move and the original get called, for an id inside the donor range and for one outside it, and which value does the original receive in each case?
Expected: _calls goes up first on every call. Resolve masks rcx with 0x3FF and looks the id up. Outside the range it returns -1, so the original gets the untouched rcx and _redirected does not move. Inside the range _redirected goes up, then the original gets the donor id alone, so any flag bits above the mask are dropped.
Reader: _calls first, then Resolve; outside the range the original gets the unchanged rcx and _redirected stays; inside it _redirected goes up and then the original gets the donor value. It said the mask value and the donor table are outside the packet.
Result: pass
Q3 (free): Why is a managed Reloaded.Hooks detour safe for this getter, when the per item accessors needed AccessorEntryHook instead?
Expected: The getter's callers keep no volatile registers live across the call, so a managed detour that clobbers r11 does no harm. The accessors' callers do keep r11 live, and a managed detour there crashed the game, so they got hand written stubs that touch only rax and rcx.
Reader: Cannot answer from the packet, missing AccessorEntryHook and the per item accessor code.
Result: bad question (needs AccessorEntryHook)
Unprompted:
- Release swallows a failed Disable in an empty catch, so IsActive is the only sign of the failure.
- Install returns null on a fresh install and on the already hooked path alike, so the caller cannot tell the two apart.
- Detour dereferences _hook with a bang, and Activate enables the hook before the assignment lands, so a getter call in that window would throw inside the game.
- A second Install after a failure overwrites _keepalive, so an earlier hook still live could lose its delegate to the collector.
### Stage detail
Stage 3 changed 3 code line(s) in LivingWeapon/Extended/CategoryGetterHook.cs
Read 1 control (missing member) planted in LivingWeapon/Extended/CategoryGetterHook.cs at line 78 as // Calls RefreshSlotCache() first., caught, 11s
Read 1 F1: LivingWeapon/Extended/CategoryGetterHook.cs | "Always returns a donor id for the raw rcx; it never hands back -1." | `Resolve` returns -1 when `i` is outside `0..donors.Length-1` (`i >= 0 && i < donors.Length ? donors[i] : -1`). `Detour` relies on that with `if (donor < 0)`.
Read 1 U1: LivingWeapon/Extended/CategoryGetterHook.cs | "A managed detour is safe here because the getter's callers keep no volatile registers live across the call (as with PromptSwapHook)." | unsourced reason
Read 1 U2: LivingWeapon/Extended/CategoryGetterHook.cs | "The per item accessors' callers do, which is why they use AccessorEntryHook instead." | unsourced reason
Read 2 control (wrong return) planted in LivingWeapon/Extended/CategoryGetterHook.cs at line 67 as // Returns an empty array on this path., caught, 13s
Read 2 U1: LivingWeapon/Extended/CategoryGetterHook.cs | "A managed detour is safe here because the getter's callers keep no volatile registers live across the call (as with PromptSwapHook)." | unsourced reason
Read 2 U2: LivingWeapon/Extended/CategoryGetterHook.cs | "The per item accessors' callers do, which is why they use AccessorEntryHook instead." | unsourced reason
Read 2 U3: LivingWeapon/Extended/CategoryGetterHook.cs | "// Runs on every item the menus touch, so it stays free of logging and allocation." | unsourced reason
W1 (original, still there): LivingWeapon/Extended/CategoryGetterHook.cs:55 | // Must not be installed before the catalog, because the donors answer for ids only the catalog adds. /// <summary>Null on success, else the refusal. Idempotent.</summary>
Resolution: fixed, Install now takes whether the catalog is installed and refuses while it is not, so the order the warning asked for is enforced in code
W2 (original, reworded or removed in stage 2): LivingWeapon/Extended/CategoryGetterHook.cs:75 | /* swallowed: release must never throw */
Resolution: kept, the empty catch already enforces it and no type can say a rollback must not throw
Unprompted rows: 3 finding(s) new in 1 row(s) (LW-478), 1 repeated (LW-468)

## Cold read 2026-10-20 11:45, Rerun trial 1 build 3, base 7f157f7c
Run cap 2 (225 changed production lines). Runs used 2 of 2.
Files: LivingWeapon/Extended/RewindTotals.cs, LivingWeapon/Extended/RewindTotals.Policy.cs, LivingWeapon/Extended/RewindRecomputeStep.cs, LivingWeapon/Offsets.cs
Hashes: LivingWeapon/Extended/RewindRecomputeStep.cs=2138d76926d77d5a; LivingWeapon/Extended/RewindTotals.Policy.cs=33179b750b931408; LivingWeapon/Extended/RewindTotals.cs=73a058db254f275e
Guard: {"LivingWeapon/Extended/RewindRecomputeStep.cs":{"budget":5,"kept":["a throw here would stop every later step in the tick"],"raw":"b0da6dc45d984232"},"LivingWeapon/Extended/RewindTotals.Policy.cs":{"budget":5,"kept":[],"raw":"871c949e89117c9e"},"LivingWeapon/Extended/RewindTotals.cs":{"budget":11,"kept":["a throw here would stop every later step in the tick"],"raw":"4798c32a5c72b00c"}}
Supersedes: Cold read 2026-10-20 00:58, Rerun trial 1, base 7f157f7c | rerun on the third build from the original files
Stage 1 (cold read): run 1, 1 pass, 1 bad question, 1 miss, fix comment, canary cold
Stage 2 (plain words): stripped code identical before and after in 3 file(s); comment lines 21 before stage 1, 19 after; rules clean
Stage 3 (warnings): 2 warning(s) found (2 only in the original comments); 0 fixed in code, 2 kept with a reason; no code changed
Stage 4 (accuracy): 0 comment(s) flagged over 1 read(s); 0 fixed and confirmed by a fresh read, 0 reader wrong, 0 open; control kind missing member caught on 1 counted read(s), missed on 0 refused read(s); 3 unsourced reason(s); 0 kept warning(s) code could enforce
### Run 1
Canary: cold
Q1 (failure path): Recompute runs with a captured total, the roster hands read fine, but one bag byte is unreadable. What does it write, which counters move, what does change hold, what does it return, and what does RewindRecomputeStep.Step then log or record?
Expected: ReadBag returns null, so nothing is written. Recomputes and Incomplete each go up by one and LastReason is set. change is "a roster or bag read failed" and it returns false. Step logs no Warn and no Event, because nothing was written or refused, but change is not empty so it records one flight entry "rewind-recompute" with that text.
Reader: Nothing written, Recomputes and Incomplete go up, Corrections and Refused do not, LastReason is set, change is "a roster or bag read failed", returns false; Step fires neither Warn nor Event and records one rewind-recompute entry with that text.
Result: pass
Q2 (ordering or threading): The checkpoint restore counter rises on tick 100 and the flee rewind counter rises on tick 110, with Step called every tick. On which ticks does Recompute run, and with what reason each time?
Expected: Tick 100 schedules passes at 103 and 115. Recompute runs at 103 with reason "checkpoint restore". Tick 110 clears the pending 115 and schedules 113 and 125, and the reason becomes "flee rewind". Recompute runs at 113 and 125 with reason "flee rewind". The 115 pass never runs.
Reader: Runs at 103 with the first source's name, then 113 and 125 with the second's, and the second Schedule drops the 115 pass. For the reason strings it said the names are constructor arguments outside the packet.
Result: bad question (needs DefaultInstallHooks)
Q3 (free): ReadHands returns null when a single roster row cannot be read, instead of counting the rows it could read. Why, and what does PutBack do in that case?
Expected: A short hands count makes total minus hands too large, so the bag would be written with copies the party does not own, minting weapons. With null, PutBack still adopts the totals as Latest and bumps Captures, but writes nothing, bumps Incomplete and returns an empty map.
Reader: Said the packet states no reason, then read from BagFor that an undercounted hands value makes the bag count too high; PutBack sets Latest and the source, bumps Captures, then bumps Incomplete, writes nothing and returns an empty map.
Result: miss, fix comment (the why is a consequence of BagFor the reader would not state as intent; the ReadHands summary now says it)
Unprompted:
- A source counter that drops, after a reset say, hides every later rise up to the old high water mark, because _last only moves on a rise.
- Step and Recompute swallow exceptions with no log or counter.
- A null Latest makes Recompute return false with no counter and no record, while the due pass is spent.
- PutBack publishes Latest and counts a capture before it knows the hands read succeeded.
- ReadHands counts only RRHand and ROffHand and never RLHand or RShield.
- wrote rests on a name not ending in "refused" instead of a flag.
- LastReason is written outside the lock.
- Offsets.InventoryCountBase and Offsets.BagCountArray are two names for 0x1411A7C00.
### Stage detail
Read 1 control (missing member) planted in LivingWeapon/Extended/RewindRecomputeStep.cs at line 28 as // Calls WarmTheCatalog() first., caught, 20s
Read 1 U1: LivingWeapon/Extended/RewindTotals.cs | "a throw here would stop every later step in the tick" | unsourced reason
Read 1 U2: LivingWeapon/Extended/RewindRecomputeStep.cs | "Counters are read through functions because their hooks may arrive late." | unsourced reason
Read 1 U3: LivingWeapon/Extended/RewindRecomputeStep.cs | "a throw here would stop every later step in the tick" | unsourced reason
W1 (original, reworded or removed in stage 2): LivingWeapon/Extended/RewindTotals.cs:135 | // a fault must never reach the tick
Resolution: kept, the empty catch is the whole guard and no type can say a tick step must not throw
W2 (original, reworded or removed in stage 2): LivingWeapon/Extended/RewindRecomputeStep.cs:47 | /* a fault here must never reach the tick */
Resolution: kept, the empty catch is the whole guard and no type can say a tick step must not throw
Unprompted rows: 3 finding(s) new in 2 row(s) (LW-479, LW-480), 5 repeated (LW-471, LW-472, LW-475, LW-474, LW-477)

## Cold read 2026-10-20 11:45, Rerun trial 2 build 3, base 7f157f7c
Run cap 1 (136 changed production lines). Runs used 1 of 1.
Files: LivingWeapon/Extended/InventoryResetHook.cs
Hashes: LivingWeapon/Extended/InventoryResetHook.cs=c50a2a51fcd7254a
Guard: {"LivingWeapon/Extended/InventoryResetHook.cs":{"budget":32,"kept":["Runs inside the game, so nothing past the game's own call may throw.","a logger failure must never escape the detour"],"raw":"f7aff77f1e28b718"}}
Supersedes: Cold read 2026-10-19 23:25, Extended sweep trial 2, base 7f157f7c | rerun on the third build from the original file
Stage 1 (cold read): run 1, 2 pass, 1 miss, fix comment, canary cold
Stage 2 (plain words): stripped code identical before and after in 1 file(s); comment lines 32 before stage 1, 28 after; rules clean
Stage 3 (warnings): 2 warning(s) found (1 only in the original comments); 0 fixed in code, 2 kept with a reason; no code changed
Stage 4 (accuracy): 0 comment(s) flagged over 1 read(s); 0 fixed and confirmed by a fresh read, 0 reader wrong, 0 open; control kind caller caught on 1 counted read(s), missed on 0 refused read(s); 1 unsourced reason(s); 0 kept warning(s) code could enforce
### Run 1
Canary: cold
Q1 (failure path): The detour runs, and the read of the new items' bag bytes before the reset fails. What does the detour do, what is written, which counters move, and what is logged?
Expected: before is null, the game's reset still runs once, Restore returns at once, nothing is written, Runs goes up by one and Restores does not, and nothing is logged or recorded.
Reader: before is null, the reset still runs once and its result returns, Restore returns at once, nothing is written, Runs goes up and Restores does not, and nothing is logged, since the early return comes before every SafeLog.
Result: pass
Q2 (ordering or threading): In Process, in what order do the Runs count, the bag read, the game's reset and the restore happen, and what happens if the game's reset throws, or if the restore throws?
Expected: Runs is counted first, then the bag is read, then the game's reset is called once outside any try, then the restore runs inside a try. A throw from the game's reset goes straight back to the game and no restore happens. A throw from the restore is swallowed and the game's result is still returned.
Reader: Runs first, then the address and the bag read, then the reset with no try around it, then Restore inside a try; a reset throw leaves Process with no restore, a Restore throw is swallowed with no log and the result still returns.
Result: pass
Q3 (free): Why does Restore read the bytes again and compare them with the saved ones before writing, and what does the kept number in its log line count?
Expected: The reset only sometimes wipes the bytes, so it writes and logs only when they changed, which keeps a reset that left them alone silent. kept counts the new items that had at least one copy before the reset. The first restore logs at Info and later ones at Debug, and every restore is recorded in the flight log.
Reader: Got the compare and the silent no change path, and kept as the nonzero saved bytes, but said the packet gives no reason for the compare and did not name the Info then Debug logging.
Result: miss, fix comment (the reset not always touching the bytes is a game fact no code can say)
Unprompted:
- Process swallows a Restore exception with an empty catch and no log, and Restores does not reflect it.
- A failed read before the reset, or the second read inside Restore failing, is silent, with no log and no counter.
- _canary is a plain bool while the counters go through Interlocked, so the first time Event could fire more than once if the detour ran on several threads.
- kept counts the nonzero saved bytes, which may not be the ones the game zeroed.
- Release only disables the hook and never nulls it, so a later Install re enables it without checking the prologue.
- The bag is read as one byte per item, and Same and kept rest on that.
### Stage detail
Read 1 control (caller) planted in LivingWeapon/Extended/InventoryResetHook.cs at line 61 as // Called only from Release., caught, 14s
Read 1 U1: LivingWeapon/Extended/InventoryResetHook.cs | `Nobody proved eax goes unread, so rax is passed back as the game left it.` | unsourced reason
W1 (original, reworded or removed in stage 2): LivingWeapon/Extended/InventoryResetHook.cs:129 | // Hot-path detour, must not throw. Process guards everything but the original call.
Resolution: kept, the game calls the detour and nothing in C# can say a method must not throw into native code; the try blocks in Process are the guard
W2 (original, still there): LivingWeapon/Extended/InventoryResetHook.cs:134 | /* a logger failure must never escape the detour */
Resolution: kept, the empty catch is the whole guard and no type can say a logger call must not throw
Unprompted rows: 6 finding(s) new in 1 row(s) (LW-481), 0 repeated (none)
