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

## Cold read 2026-10-19 06:57, LW-000, base 207ee4ba
Run cap 2 (355 changed production lines). Run 1 of 2.
Files: LivingWeapon/Kills/KillTracker.Corpses.cs
Q1 (failure path): A seat that was seen alive now reads dead, but its level has changed since it was first seen alive. What does ScanCorpses do for that seat this tick, and what state does it save?
Expected: ScanDeadPath returns at the slot id guard. Only _prevHp is saved (done before the dead path). No streak, snapshot or credit is touched, and the reset waits for the alive side.
Reader: Same. Quoted the guard and named _prevHp as the only saved state.
Result: pass
Q2 (ordering): A credited enemy's seat reads alive again with different brave. What runs before the credit records are cleared, and why that order?
Expected: PresentRevive is asked first, then UncreditKills if it says UncreditNow, then the wipe. Clearing first would lose _creditedWeapons so a Retry kill stays paid.
Reader: Order right. The why was inferred from the arguments, and it could not say what a Retry does to the kill.
Result: pass
Q3 (free): In ScanDeadPath, what do _identityAlive present with false, present with true, and missing each cause?
Expected: False blocks the repeat credit and marks the seat settled. True proceeds to ResolveCredit. Missing is an orphan edge handled by HandleOrphanAliveEdge, which writes true first, then buries, credits or passes on.
Reader: Same, with all three branches traced.
Result: pass
Unprompted: The silent identity mismatch return in ScanDeadPath, and a claim that ResetBattleCorpses skips the pending arrays. That claim is wrong, since KillTracker.cs:135 clears them.

## Cold read 2026-10-19 07:02, LW-000, base 207ee4ba
Run cap 2 (355 changed production lines). Run 2 of 2.
Files: LivingWeapon/Kills/KillTracker.Corpses.cs
Q1 (failure path): CreditKill returns false (killer holds no Living Weapon). What state do _deadCredited, _identityAlive and changed end in, and can a later matching enemy pay out?
Expected: _deadCredited true, changed unchanged, _identityAlive left true, so a later armed kill of a matching enemy still counts.
Reader: Same. Traced all four states from the call site and said the repeat credit block does not fire. Could not say why CreditKill refused, since its body is outside the packet.
Result: pass
Q2 (ordering): A seat reads dead on ticks 1, 2, 3. Which tick reads the turn and stamps the killer, which first pays out, why no repeat stamp?
Expected: Tick 1 only, because the turn can change after the death. Payout first on tick 3 through OracleGate, identity check and ResolveCredit.
Reader: Same on ticks. The why was inferred from the guard, and it said the packet does not state it.
Result: pass
Q3 (free): Why is _prevHp written only on dead looking ticks?
Expected: A retry rewinds a dead unit, and the 0 to positive jump is the fingerprint. Writing on alive ticks would overwrite the 0 during the three tick alive wait. A status death keeps hp above 0.
Reader: Got the mechanism (alive ticks never write it, so the comparison sees the last dead value). Missed the retry rewind reason; said the intent is not in the packet.
Result: miss, fix comment (the why already sits in the comment above _prevHp, which a stripped packet cannot carry; no name or helper would hold a retry fact)
Unprompted: ScanAlivePath clears credit state in two near identical blocks. ResolveCredit sets _deadCredited true even when CreditKill refuses, silently. A failed Band.IsValid leaves _seenAlive and _prevHp alone. The expiry log reads _pendingAge after a short circuited increment. ResetBattleCorpses does not clear the pending arrays here (they are cleared in KillTracker.cs).

## Cold read 2026-10-19 06:52, LW-453, base 207ee4ba
Run cap 3 (owner set for this readability pass; the file had no diff, so it rode in as an extra). Run 1 of 3.
Files: LivingWeapon/Kills/KillTracker.Corpses.cs, LivingWeapon/Kills/KillTracker.Delayed.cs
Q1 (failure path): A tracked enemy's corpse reaches the normal credit branch of ResolveCredit with a nonempty culprit and the restart latch closed, but CreditKill returns false (it refused the credit). What is left in _deadCredited, _identityAlive and the changed flag for that slot, and can a later death of the same identity still be credited?
Expected: _deadCredited[s] is set true regardless, _identityAlive for slot.Id stays true (it is set false only when CreditKill returned true), and changed is not set. Because the identity is still marked alive in the belt, a later wielder backed kill of the same identity tuple can still credit.
Reader: _deadCredited set true unconditionally, _identityAlive left as it was (true from the alive edge or the orphan repair), changed not set. Because the entry stays true the repeat credit block does not fire, so the identity can credit again once the slot revives and clears _deadCredited. Could not list CreditKill's refusal reasons, not in the packet.
Result: pass
Q2 (ordering): On the tick a slot first reads dead, _untrackedArmedTicks is above zero, _lastPlayerWeapons holds a tracked weapon set, and a tracked delayed actor is armed. What does StampDeadEdge stamp, and later when credit is attempted with the restart latch closed, who is credited and is the delayed arm consumed?
Expected: The arm branch runs first, so _lethalUntracked is set to ChargedAction and StampCulprit is not called. Credit is attempted on the third dead tick. ResolveCredit peeks the delayed arm before the untracked check; because the delayed candidate is non null the no credit bury is skipped, the culprit is the delayed actor, ConsumeDelayedCulprit consumes the arm, and CreditKill credits the delayed committer's weapons with delayedOrCharged true and viaFallback false.
Reader: arm branch first, _lethalUntracked = ChargedAction, StampCulprit skipped. At credit time ResolveCredit peeks the delayed arm, the non null candidate skips the bury, ConsumeDelayedCulprit zeroes the arm and CreditKill runs with delayedOrCharged true. Added two caveats beyond the expected answer, that an identity absent from the belt goes through HandleOrphanAliveEdge which buries on the ChargedAction stamp without peeking the delayed arm, and that both arms must outlive the three tick dead streak.
Result: pass (the reader was sharper than the expected answer on the orphan path)
Q3 (free): ScanAlivePath clears a slot's credit and lethal state in two different places. What condition triggers each, which one presents the old credit to the restart sentinel, and which one also resets _seenAlive?
Expected: The first fires when the slot was already seen alive and its level, brave or faith now differ from the recorded _slotId (an identity swap or slot reuse); it presents any credited weapons to the sentinel, resets _seenAlive and the alive streak along with all credit, lethal and victim state, and returns early so the new identity must rebuild its alive streak. The second runs on every alive tick once _seenAlive is true; it presents to the sentinel only when the slot had been credited (_deadCredited true, a revive), re marks the identity alive, and clears the credit, lethal and victim state but never resets _seenAlive.
Reader: first block fires on seenAlive with a changed lvl, brave or faith (a different unit in the slot), presents any credited weapons, resets _seenAlive and _aliveStreak and returns. Second fires on every seenAlive tick, presents only when _deadCredited, never resets _seenAlive or the streak. Listed the identical clears in both.
Result: pass
Unprompted: _prevHp is never refreshed while alive, so healedFromZero reads the last dead tick's hp. A restart suppressed credit falls into the pending branch. The orphan bury leaves the belt true while the ResolveCredit bury and the expiry set it false. LogUntrackedBury assumes FellPhrase starts with a or an. The identity change test and the PresentRevive call are each written out twice, and the two state clears in ScanAlivePath are line for line the same.

## Cold read 2026-10-19 06:55, LW-453, base 207ee4ba
Run cap 3 (owner set for this readability pass; 170 changed production lines). Run 2 of 3.
Files: LivingWeapon/Kills/KillTracker.Corpses.cs, LivingWeapon/Kills/KillTracker.Delayed.cs
Q1 (failure path): A slot that was seen alive but never credited suddenly reads alive with a different brave value. Is the restart sentinel consulted, which per slot state survives the tick, and what must happen before a death in that slot can be credited again?
Expected: UncreditIfRestartRevive returns at once because _creditedWeapons is null, so PresentRevive is not called and changed is untouched. _seenAlive and _aliveStreak go to zero and ForgetDeathAndCredit clears the verdict, culprit, credit and victim state, then the method returns before CaptureAlive. _slotId, _prevHp and the _identityAlive entries survive. The slot must read alive for AliveNeeded (3) consecutive on field ticks to set _seenAlive and record the new tuple; until then ScanDeadPath returns early on the missing _seenAlive.
Reader: sentinel not consulted, the null _creditedWeapons returns before PresentRevive. Listed the ForgetDeathAndCredit clears, and _slotId, _prevHp, _fieldEvidenceStreak and _identityAlive as surviving. Also caught that _lethalViaFallback is not cleared by ForgetDeathAndCredit. Three alive ticks to re set _seenAlive and the new _slotId, then the dead path gates. Said it could not judge OracleGate for the new tuple without the oracle.
Result: pass
Q2 (ordering): On an alive on field tick of a slot that is already seen alive and was credited for its last death, in what order do the alive streak bump, the alive edge belt write, the sentinel presentation, the credit clear and the probe alive capture run, and what breaks if the presentation moved after the clear?
Expected: _aliveStreak increments first, then _identityAlive for the identity is set true, then UncreditIfRestartRevive presents the credited weapons, fallback flag, identity and nameId, then ForgetDeathAndCredit clears them, then CaptureAlive runs. Moved after the clear, _creditedWeapons would already be null, so the sentinel would never see the credit and a retry phantom credit could never be paid back.
Reader: dead streak zeroed, alive streak bumped, threshold block skipped because already seen alive, belt write inside the _deadCredited block, UncreditIfRestartRevive, ForgetDeathAndCredit, CaptureAlive. Moved after the clear, creditedHere is null so PresentRevive and UncreditKills never run and a restart revive keeps its credit. Added that CaptureAlive before the clear would be lost to the probe reset.
Result: pass
Q3 (free): ExpirePendingUncredited has just run for a death with identity tuple T. Another band slot holding the same tuple T, which has been alive for many ticks, now dies. Can it be credited, and what event would make T creditable again?
Expected: No. ScanDeadPath finds T in _identityAlive with false, marks the slot _deadCredited and logs the blocked repeat credit. A slot that was already seen alive does not rewrite the belt on later alive ticks. T becomes creditable again only when some slot with T newly completes the AliveNeeded streak (setting _seenAlive) or a slot credited under T revives, either of which sets _identityAlive[T] true.
Reader: no, the false belt entry hits the blocked repeat credit branch. A long seen alive slot writes the belt only at the threshold block or after a credited death, so it does not rescue T. Creditable again on a fresh seenAlive under T, a revive of a credited slot under T, or ResetBattleCorpses.
Result: pass
Unprompted: _lethalViaFallback is not cleared by ForgetDeathAndCredit (pre existing, every reader of it is gated on _lethalActor being non null). The expiry test only bumps _pendingAge while the turn edge clause is false. The blocked repeat branch leaves _pending as it was. An invalid band slot resets streaks but not _seenAlive or _slotId. The delayed arm is a single global, so the first slot to die in the window takes it.

## Cold read 2026-10-19 06:58, LW-453, base 9db62ce7
Run cap 3 (owner set for this readability pass; 129 changed production lines since run 2, mostly the StampDeadEdge unindent). Run 3 of 3.
Files: LivingWeapon/Kills/KillTracker.Corpses.cs, LivingWeapon/Kills/KillTracker.Stamp.cs
Q1 (failure path): On a slot's first dead tick the turn queue team bytes are unreadable, no untracked arm is set, _lastPlayerWeapons is empty, the latch did not resolve empty, and no acted period is open. What gets stamped for that slot, and what happens on the third dead tick if the enemy is tracked, its alive edge is true and no delayed actor is armed?
Expected: TurnQueueShowsAiTurn returns false because the unreadable team counts as 0, so the last branch calls StampCulpritFromHypothesisOnly, which returns at once with no period open. Nothing is stamped, _lethalActor stays null and _lethalUntracked stays None. On the third dead tick ResolveCredit finds no delayed candidate and no untracked stamp, the culprit falls back to the empty _lastPlayerWeapons, so StartPending runs, setting _pending, zeroing _pendingAge, snapshotting _actedFalls, logging the waiting to identify the killer line and recording a pending tape event.
Reader: unreadable team reads 0, aiTurn false, the chain reaches StampCulpritFromHypothesisOnly which returns on the closed period, nothing stamped. Third tick passes the gates, no delayed candidate and no untracked stamp, empty culprit skips credit, StartPending sets _pending, age 0, falls snapshot and logs the waiting line. Assumed _periodOpen is the acted period flag since its declaration was not in the packet.
Result: pass
Q2 (ordering): A slot is pending because the restart latch was open when its corpse matured, and a tracked culprit is latched. On each later tick, which of crediting and expiry is checked first, does the tick age advance while the latch stays open, and what happens on the tick the latch closes?
Expected: The credit branch is checked first on every tick, but while the latch is open restartSuppressed sends the slot past it to the pending branches; since _pending is already true, PendingTimedOut runs and advances _pendingAge unless the turn edge limit already tripped, so the slot can expire while suppressed. On the tick the latch closes the credit branch wins, CreditKill runs, and PendingTimedOut is not evaluated that tick so the age does not advance.
Reader: credit branch first, blocked by restartSuppressed while the latch is open, then the already pending check, then PendingTimedOut, whose short circuit bumps the age only while the falls test is false. On the closing tick the credit branch runs before expiry is evaluated, even past the limits, then LogWhyNotLiveLatch and CreditKill.
Result: pass
Q3 (free): A dead slot can end without credit through OracleGate, BlockRepeatCredit, the untracked bury in ResolveCredit, the untracked bury in HandleOrphanAliveEdge, or ExpirePendingUncredited. For each, what is the identity's alive edge entry in _identityAlive left as?
Expected: OracleGate leaves it untouched. BlockRepeatCredit leaves it false, which is why it fired. The ResolveCredit bury sets it false. The orphan bury leaves it true, because HandleOrphanAliveEdge repairs it to true before the bury check. ExpirePendingUncredited sets it false. All five set _deadCredited true.
Reader: untouched, stays false, set false, left true after the repair at the top of HandleOrphanAliveEdge, set false. Noted OracleGate runs before the belt lookup.
Result: pass
Unprompted: The two untracked buries disagree on the belt, so a second death of the same tuple is blocked after one and not the other (open, a design question; behavior was out of scope). The credit branch does not clear _pending in this file (CreditKill clears it in KillTracker.cs, outside the packet). PendingTimedOut increments inside a predicate. RecordedIdentityChanged leaves max hp out while the belt key includes it. An unreadable turn queue biases toward stamping, by design.

## Cold read 2026-10-19 07:46, LW-453, base a8072bc8
Run cap 1 (62 changed production lines). Run 1 of 1.
Files: LivingWeapon/Chronicle/VictimClass.cs, LivingWeapon/Kills/KillTracker.Corpses.cs, LivingWeapon/Kills/KillTracker.Delayed.cs
Q1 (failure path): A death has been waiting for a killer, and on this tick the number of turn ends since it fell has already reached the limit. Does the wait's tick count move this tick, and what happens to the death?
Expected: The tick count does not move, because the turn limit is checked first and the count only advances when that limit has not tripped. The death expires uncredited and the log reports the tick count as it stood.
Reader: The count does not move since turnsUp is checked first, and the death expires uncredited with the could not be determined line.
Result: pass
Q2 (ordering): A charged attack is armed when a death becomes creditable, but the restart latch is open on this tick. Is the charge still armed after this tick, and on the later tick that pays, what disarms it and what list is used?
Expected: Still armed, because ResolveCredit only peeks. On the paying tick ConsumeDelayedCulprit calls the same peek, sets the armed tick count to zero, and returns that same delayed actor list, which is the one credited.
Reader: Still armed after the suppressed tick since ResolveCredit only peeks. On the paying tick ConsumeDelayedCulprit zeroes the armed count and the delayed actor list is the culprit credited. Noted the window can lapse first.
Result: pass
Q3 (free): When the mod logs a kill left unpaid because the killer had no Living Weapon and no victim snapshot was captured, what noun follows "The fallen", and can the article form and the bare form of that phrase ever disagree?
Expected: "enemy". They cannot disagree, because FellPhrase is built by putting an article in front of FellNoun.
Reader: "enemy", from FellNoun when no snapshot was captured. They cannot disagree because FellPhrase wraps FellNoun with WithArticle.
Result: pass
Unprompted: StartPending and LogUntrackedBury build the victim phrase on two paths. BlockRepeatCredit leaves _pending and _lethalActor set until the next revive. A death in a slot never seen alive for three ticks drops with no log.
