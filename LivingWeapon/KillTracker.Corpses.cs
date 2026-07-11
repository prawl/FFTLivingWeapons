using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// WHY a corpse's kill was buried as untracked (the per-slot verdict formerly a bare bool).
/// Widened for the logging facelift's owner-flagged skeleton line: the console no-credit ruling
/// now names HOW the untracked actor was resolved, so each of the four stamp sites records its
/// own reason. None (0) doubles as the cleared state (Array.Clear compatible).
/// </summary>
internal enum UntrackedReason : byte
{
    None = 0,
    ChargedAction,   // cross-turn untracked charged action landing (the summoner arm window)
    EnemyTurn,       // death-edge TqTeam read said a non-player turn was in progress
    ActedLatch,      // the acted-period latch resolved a player with no tracked weapon
    ActorRegister,   // the actor register named a player with no tracked weapon (Stamp.cs bury)
}

/// <summary>
/// Band corpse scan and alive-edge belt -- the per-slot death state machine half of
/// KillTracker (the actor latch lives in KillTracker.cs; the enemy-side identity set is
/// <see cref="EnemyOracle"/>).
///
/// DEAD DETECTION: hp==0 OR Dead status bit (+0x45/0x20, proven from Doom research). A
/// status-death (e.g. Phoenix Down on undead) fires the bit without HP ever reaching zero.
///
/// ALIVE-EDGE BELT: replaces the once-ever identity dedup. Tracks per-identity last-seen-alive
/// state (_identityAlive). Credit fires only on alive->dead TRANSITION. The identity becomes
/// creditable again once observed alive (hp>0, Dead bit clear) after a death. Frozen-twin
/// protection survives: a dead identity at two addresses simultaneously credits once, because
/// the twin is never independently observed alive in between.
///
/// Per-slot alive/dead streaks with 3-tick guards defeat phantom-load transients.
/// </summary>
internal sealed partial class KillTracker
{
    private const int AliveNeeded = 3;  // consecutive valid alive ticks before seenAlive is set
    private const int DeadNeeded = 3;   // consecutive dead ticks before a credit attempt

    // per-slot state (band slot index = 0..BandSlots-1)
    private readonly bool[] _seenAlive = new bool[Offsets.BandSlots];
    private readonly bool[] _deadCredited = new bool[Offsets.BandSlots];
    private readonly int[] _aliveStreak = new int[Offsets.BandSlots];
    private readonly int[] _deadStreak = new int[Offsets.BandSlots];
    private readonly (byte lvl, byte br, byte fa)[] _slotId = new (byte, byte, byte)[Offsets.BandSlots];
    // Reliquary Phase 1 (docs/RELIQUARY_AC.md): the victim identity snapshot captured at THIS
    // slot's dead-edge (deadStreak 0->1), via the shared VictimReader.Read -- the same guarded
    // read VictimProbe uses for its log-only capture, so the two can never disagree. Consumed
    // EXACTLY ONCE at CreditKill (KillTracker.cs), which clears it on BOTH the credited and
    // no-credit branches. Cleared on all four reset paths that also clear _lethalActor below (a
    // no-credit death never reaches CreditKill's consume, so the revive/identity-swap/reset/
    // pending-expiry clears are the only thing preventing a stale snapshot from stranding).
    internal readonly VictimSnapshot[] _victimAtEdge = new VictimSnapshot[Offsets.BandSlots];
    // Weapon set latched when this slot's dead-streak started (alive->dead transition); null = not yet stamped.
    // Copied from _lastPlayerWeapons at deadStreak 0->1 so a later re-latch cannot steal the credit.
    private readonly List<int>?[] _lethalActor = new List<int>?[Offsets.BandSlots];
    // Whether the latch copied into _lethalActor[s] was itself a turn-queue-fallback resolve
    // (stamped beside _lethalActor in KillTracker.Stamp.cs); feeds the battle-end summary's
    // fallback-attribution counter at credit time. Register-named stamps are pointer-derived,
    // so they stamp false.
    internal readonly bool[] _lethalViaFallback = new bool[Offsets.BandSlots];
    // Alive->dead edge fell during a resolved-but-untracked actor's LIVE acted-period -- credit nobody.
    // Stamped at deadStreak 0->1 when _latchResolvedEmpty && _latched (the actor was identified as a roster
    // player but held no living weapon). The && _latched guard is LOAD-BEARING: it prevents a false stamp
    // in the sticky interval after an untracked turn ends but before the next armed actor latches
    // (a corpse dying in that gap is from an unknown killer and must go pending, not be silently buried).
    //
    // NOTE: the in-period stamp (_latchResolvedEmpty && _latched) covers summons that resolve during
    // the caster's own acted-period (the witnessed IC case: damage at cast completion, acted==1). The
    // cross-turn case (kill matures after _latched falls) is handled by KillTracker.Delayed.cs via
    // _untrackedArmedTicks: the arm fires at Charging-bit clear (landing) and sets _lethalUntracked at
    // deadStreak==1 AHEAD of the Count>0 branch, so a live armed latch cannot absorb the summon's kill.
    // Facelift: widened from bool[] to UntrackedReason[] so the no-credit console line can name the
    // resolve source; None == the old false, any other value == the old true.
    private readonly UntrackedReason[] _lethalUntracked = new UntrackedReason[Offsets.BandSlots];

    // Alive-edge belt: true = identity last observed alive (creditable); false = last observed dead.
    // Set true on seenAlive and on revive; set false on credit or expiry-without-actor.
    private readonly Dictionary<(byte lvl, byte br, byte fa, ushort mhp), bool> _identityAlive = new();

    /// <summary>The console phrase naming HOW the buried kill's actor was resolved -- the
    /// owner-flagged skeleton line's "(actor resolved via ...)" clause. One phrase per
    /// <see cref="UntrackedReason"/> stamp site; None never reaches the line (the no-credit
    /// branch only fires on a non-None verdict).</summary>
    internal static string ResolveSourcePhrase(UntrackedReason reason) => reason switch
    {
        UntrackedReason.ChargedAction => "a charged-action landing",
        UntrackedReason.EnemyTurn => "an enemy-turn team read",
        UntrackedReason.ActedLatch => "the acted-period latch",
        UntrackedReason.ActorRegister => "the actor register",
        _ => "an unknown resolve path",
    };

    /// <summary>Reset per-battle band-scan and coverage state. Called from ResetBattle (KillTracker.cs).</summary>
    private void ResetBattleCorpses()
    {
        Array.Clear(_seenAlive, 0, _seenAlive.Length);
        Array.Clear(_deadCredited, 0, _deadCredited.Length);
        Array.Clear(_aliveStreak, 0, _aliveStreak.Length);
        Array.Clear(_deadStreak, 0, _deadStreak.Length);
        Array.Clear(_slotId, 0, _slotId.Length);
        Array.Clear(_lethalActor, 0, _lethalActor.Length);
        Array.Clear(_lethalUntracked, 0, _lethalUntracked.Length);
        Array.Clear(_lethalViaFallback, 0, _lethalViaFallback.Length);
        Array.Clear(_victimAtEdge, 0, _victimAtEdge.Length);   // Reliquary: clear every slot's captured snapshot
        _identityAlive.Clear();
        _oracle.ResetBattle();   // enemy identities + the coverage tick/flag
        _victimProbe.ResetBattle();   // P1 probe: clear every slot's alive/edge snapshots
    }

    /// <summary>Band corpse scan + identity capture. Returns true if the tally changed.</summary>
    private bool ScanCorpses(bool onField)
    {
        bool changed = false;
        if (onField) _oracle.TickField();   // capture enemy identities + the once-per-battle coverage log

        for (int s = 0; s < Offsets.BandSlots; s++)
        {
            long addr = Band.Entry(s);
            if (!Band.IsValid(_mem, addr)) { _aliveStreak[s] = 0; _deadStreak[s] = 0; continue; }

            ushort hp = _mem.U16(addr + Offsets.AHp);
            int gx = _mem.U8(addr + Offsets.AGx);
            int gy = _mem.U8(addr + Offsets.AGy);
            ushort mhp = _mem.U16(addr + Offsets.AMaxHp);
            byte lvl = _mem.U8(addr + Offsets.ALevel);
            byte br = _mem.U8(addr + Offsets.ABrave);
            byte fa = _mem.U8(addr + Offsets.AFaith);
            // Dead bit: +0x45/0x20 (Doom research). U8 returns 0 on unreadable address -- safe.
            bool deadBit = (_mem.U8(addr + Offsets.ADeadStatus) & Offsets.ADeadBit) != 0;
            bool isDead = hp == 0 || deadBit;

            if (onField) _events?.Observe(s, hp, mhp, gx, gy, _actorTag);

            if (!isDead)
            {
                _deadStreak[s] = 0;
                if (!onField) continue;
                if (_seenAlive[s] && (_slotId[s].lvl != lvl || _slotId[s].br != br || _slotId[s].fa != fa))
                {
                    _seenAlive[s] = false; _aliveStreak[s] = 0;
                    _deadCredited[s] = false; _pending[s] = false;
                    _lethalActor[s] = null; _lethalUntracked[s] = UntrackedReason.None;
                    _victimProbe.Reset(s);   // P1 probe: a slot-reuse identity swap invalidates any stale snapshot
                    _victimAtEdge[s] = default;   // Reliquary: same invalidation for the captured snapshot
                    continue;
                }
                _aliveStreak[s]++;
                if (_aliveStreak[s] >= AliveNeeded && !_seenAlive[s])
                {
                    _seenAlive[s] = true;
                    _slotId[s] = (lvl, br, fa);
                    _identityAlive[(lvl, br, fa, mhp)] = true;   // alive-edge: creditable now
                }
                if (_seenAlive[s])
                {
                    // revive after a credited death: re-enable the alive-edge for this identity
                    if (_deadCredited[s]) _identityAlive[(lvl, br, fa, mhp)] = true;
                    _deadCredited[s] = false;
                    _pending[s] = false;
                    _lethalActor[s] = null; _lethalUntracked[s] = UntrackedReason.None;
                    // P1 probe: a revived victim's OLD dead-edge snapshot is stale -- clear it here,
                    // BEFORE CaptureAlive below repopulates the alive half this same tick (ordering
                    // matters: this runs every seenAlive tick, not only true revives, so the capture
                    // must come after or it would immediately erase what it just captured).
                    _victimProbe.Reset(s);
                    // Reliquary: a no-credit death (unknown identity, duplicate, untracked-weapon,
                    // expired-unresolved) never reaches CreditKill's consume-once, so without this
                    // clear a revived identity would carry a STALE captured snapshot into its next
                    // death. Safe to run every seenAlive tick (not just true revives): CreditKill
                    // already cleared it on any real credit, so this is a no-op then.
                    _victimAtEdge[s] = default;
                }
                _victimProbe.CaptureAlive(s, addr);   // P1 probe: every consistent alive on-field tick
                continue;
            }

            // dead path (hp==0 or Dead bit set)
            if (_deadCredited[s]) continue;
            if (!_seenAlive[s]) continue;
            if (_slotId[s].lvl != lvl || _slotId[s].br != br || _slotId[s].fa != fa) continue;
            if (!onField) continue;

            _deadStreak[s]++;
            // Stamp the actor at the alive->dead edge (deadStreak 0->1).
            // Only one branch fires per edge; both are mutually exclusive on _lastPlayerWeapons.Count.
            if (_deadStreak[s] == 1)
            {
                _victimProbe.CaptureDeadEdge(s, addr);   // P1 probe: log-only, zero behavioral dependence
                _victimAtEdge[s] = VictimReader.Read(_mem, addr);   // Reliquary: the behavioral capture CreditKill consumes
                // Read TqTeam at the death EDGE so it captures the team at the moment of death
                // (it may flip a few ticks later). Divert ONLY on a confident non-player team
                // (1=enemy, 2=ally/guest) -- any other value (0=player, garbage, or unreadable
                // defaulting to 0) takes the normal credit path: a bad read must never suppress
                // a legit player kill (docs/LIVE_LEDGER.md row 45, TqTeam fail-safe rule). The
                // tracked-delayed path still wins: ConsumeDelayedCulprit() is checked first at
                // credit time and the no-credit branch is gated `delayed == null && _lethalUntracked[s]`.
                int team = _mem.Readable(Offsets.TurnQueue + Offsets.TqTeam, 2) ? _mem.U16(Offsets.TurnQueue + Offsets.TqTeam) : 0;
                bool nonPlayerTurn = team == 1 || team == 2;   // 1=enemy, 2=ally/guest; both are AI turns
                if (_untrackedArmedTicks > 0)
                {
                    // A cross-turn UNTRACKED charged action (a summoner's summon) is landing within its
                    // arm window: bury the kill regardless of the current live latch. FIRST (ahead of the
                    // Count>0 branch) so an armed ally that acted BETWEEN the cast and the landing cannot
                    // absorb the summon's kill (doctrine: miss beats mis-credit -- an unrelated armed kill
                    // inside the tight window degrades to a no-credit MISS, never a mis-credit). The arm is
                    // NOT consumed here, so every AoE victim maturing in the window is stamped; the window
                    // timer expires it. A real TRACKED delayed action still wins at credit time via the
                    // `delayed == null` guard below (the untracked arm only buries when nothing tracked claims it).
                    _lethalUntracked[s] = UntrackedReason.ChargedAction;
                }
                else if (_lastPlayerWeapons.Count > 0)
                {
                    if (nonPlayerTurn)
                        _lethalUntracked[s] = UntrackedReason.EnemyTurn;   // counter/reaction during a non-player turn: the stale player latch did NOT deal this blow -- no-credit (miss beats mis-credit)
                    else
                        // Tracked actor latched on a player turn: consult the death-edge culprit
                        // stamp (KillTracker.Stamp.cs) -- today's latch unless the register names a
                        // fresher, disagreeing killer.
                        StampCulprit(s);
                }
                else if (_latchResolvedEmpty && _latched)
                {
                    // Kill edge fell during a resolved-but-untracked actor's LIVE acted-period
                    // (summoner/dancer/item-user, no living weapon). The && _latched guard is
                    // LOAD-BEARING: it excludes the inverse race, a DIFFERENT armed unit that
                    // acted before its own acted edge while the empty latch is still sticky (that
                    // case must keep crediting the armed unit via the pending path, not be buried
                    // here). The bury now consults the death-edge culprit hypothesis first
                    // (KillTracker.Stamp.cs) rather than crediting nobody unconditionally.
                    StampCulpritFromEmptyLatch(s);
                }
                else if (!nonPlayerTurn)
                {
                    // Empty latch, no untracked verdict, player turn: the pending-hole extension
                    // (see StampCulpritFromHypothesisOnly, KillTracker.Stamp.cs).
                    StampCulpritFromHypothesisOnly(s);
                }
            }

            if (_deadStreak[s] < DeadNeeded) continue;

            var id = (lvl, br, fa, mhp);
            if (!_oracle.Contains(id))
            {
                _deadCredited[s] = true;
                ModLogger.Debug(LogVerb.Kill, $"the unit at battle slot {s} died but was not a tracked enemy; no credit given (normal for player and guest deaths)");
                _recorder?.Invoke("kill", $"no-credit slot={s} reason=not-tracked-enemy");
                continue;
            }

            // alive-edge: credit only on alive->dead transition; a frozen twin is never alive here.
            // LW-65: split PRESENT-false from ABSENT. PRESENT-false means the identity already
            // resolved this battle (credited OR no-credit) or a simultaneous same-tuple mirror
            // clone: keep blocking. ABSENT means the alive-edge was stamped under a DIFFERENT
            // maxHp than this death (drift between :155's mark and this tuple, guaranteed by
            // :182/:183 to share lvl/br/fa) -- a real, uncredited enemy death, not a duplicate.
            if (_identityAlive.TryGetValue(id, out bool wasAlive))
            {
                if (!wasAlive)
                {
                    _deadCredited[s] = true;
                    ModLogger.Warn(LogVerb.Kill, $"Blocked a repeat credit: an enemy whose identity was already resolved this battle died again at battle slot {s}.");
                    _recorder?.Invoke("kill", $"no-credit slot={s} reason=identity-already-resolved");
                    continue;
                }
                // wasAlive == true: normal creditable path; fall through below.
            }
            else
            {
                // ABSENT: LW-65 orphaned alive-edge. Guaranteed seen-alive (:182) and
                // oracle-confirmed (above); repair the belt and credit from THIS slot's own
                // death-edge stamp only. Deliberately does NOT consult the global delayed-culprit
                // latch (ConsumeDelayedCulprit): that belongs to a charged action mid-flight, and
                // an orphan scanned at a lower slot index must not steal it from its real victim.
                // A stamped no-credit verdict (_lethalUntracked) IS still honored here.
                _recorder?.Invoke("kill", $"orphan-alive-edge slot={s} mhp={mhp}");
                _identityAlive[id] = true;   // repair the orphaned edge for any later revive/re-death
                if (_lethalUntracked[s] != UntrackedReason.None)
                {
                    _deadCredited[s] = true; _pending[s] = false;
                    var vs0 = _victimAtEdge[s];
                    string fellPhrase0 = VictimClass.FellPhrase(vs0.Has, vs0.Job, vs0.Undead);
                    string fallenNoun0 = fellPhrase0.StartsWith("an ") ? fellPhrase0.Substring(3) : fellPhrase0.Substring(2);
                    ModLogger.Event(LogVerb.Kill, $"The fallen {fallenNoun0} at battle slot {s} was slain by a player carrying no Living Weapon; the kill is deliberately left uncredited (actor resolved via {ResolveSourcePhrase(_lethalUntracked[s])}).");
                    _recorder?.Invoke("kill", $"no-credit slot={s} reason=untracked-weapon via={_lethalUntracked[s]}");
                    continue;
                }
                var orphanCulprit = _lethalActor[s] ?? _lastPlayerWeapons;
                if (orphanCulprit.Count > 0)
                {
                    bool orphanViaFallback = _lethalActor[s] != null ? _lethalViaFallback[s] : _latchViaFallback;
                    _deadCredited[s] = true;
                    if (CreditKill(s, gx, gy, orphanCulprit, orphanViaFallback)) { _identityAlive[id] = false; changed = true; }
                    continue;
                }
                // No culprit stamped yet: the belt is now repaired, so fall through to the shared
                // pending/credit machinery below, which treats this as an ordinary seen-alive
                // corpse from here on.
            }

            // Prefer the delayed actor (snapshotted at commit, armed at landing) when available.
            // The explicit `delayed == null` guard on the no-credit branch below is what orders
            // delayed BEFORE _lethalUntracked: a tracked Jump/charge committer whose kill matures
            // while an untracked unit is latched still wins. The two CAN coexist on the same slot
            // (delayed is armed separately via TrackDelayed, not stamped at deadStreak==1), so the
            // guard -- not mutual exclusion -- is the load-bearing ordering (T4 pins it).
            var delayed = ConsumeDelayedCulprit();
            if (delayed == null && _lethalUntracked[s] != UntrackedReason.None)
            {
                // Killing edge was stamped during a roster player's turn with no tracked weapon.
                // Credit nobody: the summoner/dancer/item-user's AoE kill is intentionally uncredited.
                // THE OWNER-FLAGGED LINE: the console ruling names the victim (from the slot's
                // still-populated dead-edge snapshot) and HOW the untracked actor was resolved.
                _deadCredited[s] = true; _pending[s] = false; _identityAlive[id] = false;
                var vs = _victimAtEdge[s];
                string fellPhrase = VictimClass.FellPhrase(vs.Has, vs.Job, vs.Undead);
                string fallenNoun = fellPhrase.StartsWith("an ") ? fellPhrase.Substring(3) : fellPhrase.Substring(2);
                ModLogger.Event(LogVerb.Kill,
                    $"The fallen {fallenNoun} at battle slot {s} was slain by a player carrying no Living Weapon; the kill is deliberately left uncredited (actor resolved via {ResolveSourcePhrase(_lethalUntracked[s])}).");
                _recorder?.Invoke("kill", $"no-credit slot={s} reason=untracked-weapon via={_lethalUntracked[s]}");
                continue;
            }
            var culprit = delayed ?? _lethalActor[s] ?? _lastPlayerWeapons;
            if (culprit.Count > 0)
            {
                if (delayed != null)
                    ModLogger.EventWithTrace(LogVerb.Credit,
                        $"Crediting the charged attack that just landed (a Jump or a spellcast) to the unit that committed it, wielding {string.Join(", ", delayed.ConvertAll(LogNames.Weapon))}, not the unit acting now.",
                        $"delayed-credit detail (delayed=[{string.Join(",", delayed)}] stamped=[{string.Join(",", _lethalActor[s] as IEnumerable<int> ?? Array.Empty<int>())}] current=[{string.Join(",", _lastPlayerWeapons)}], battle slot {s})");
                else if (_lethalActor[s] != null && !ActorResolver.SameSet(_lethalActor[s]!, _lastPlayerWeapons))
                    ModLogger.EventWithTrace(LogVerb.Credit,
                        $"Crediting the unit that landed the finishing blow, wielding {string.Join(", ", culprit.ConvertAll(LogNames.Weapon))}, rather than the unit that acted most recently.",
                        $"finishing-blow detail (lethal=[{string.Join(",", culprit)}] live-latch=[{string.Join(",", _lastPlayerWeapons)}], battle slot {s}, dead-streak {_deadStreak[s]})");
                bool viaFallback = delayed == null && (_lethalActor[s] != null ? _lethalViaFallback[s] : _latchViaFallback);
                bool c = CreditKill(s, gx, gy, culprit, viaFallback);
                _deadCredited[s] = true;
                // LW-65/D2/B5: only burn the shared alive-edge when a credit actually landed.
                // A fully-refused credit (no live wielder) tallies nothing, so the identity was
                // NOT resolved for crediting purposes; leaving the edge true lets a later genuine
                // same-tuple wielder-backed kill still credit. Unlike the :276 (untracked-weapon)
                // and :318 (expired-unresolved) no-credit paths, this one neither adjudicated a
                // killer nor exhausted the search, so it alone gets the guard.
                if (c) { _identityAlive[id] = false; changed = true; }
            }
            else if (!_pending[s])
            {
                _pending[s] = true; _pendingAge[s] = 0; _pendingFalls[s] = _actedFalls;
                var vs = _victimAtEdge[s];
                string fell = VictimClass.FellPhrase(vs.Has, vs.Job, vs.Undead);
                string opener = char.ToUpperInvariant(fell[0]) + fell.Substring(1);
                _klog.Info(deadBit && hp > 0
                    ? $"{opener} fell at ({gx},{gy}); killed by a status effect, waiting to see whose attack caused it (battle slot {s})."
                    : $"{opener} fell at ({gx},{gy}); waiting to identify the killer (battle slot {s}).");
                _recorder?.Invoke("kill", $"pending slot={s} at=({gx},{gy}) status={(deadBit && hp > 0 ? 1 : 0)}");
            }
            else if (_actedFalls - _pendingFalls[s] >= ExpireFalls || ++_pendingAge[s] > PendingTtl)
            {
                _deadCredited[s] = true; _pending[s] = false;
                _lethalActor[s] = null; _lethalUntracked[s] = UntrackedReason.None;
                _victimAtEdge[s] = default;   // Reliquary: this no-credit path never reaches CreditKill's consume
                _identityAlive[id] = false;
                _klog.Warn($"The killer of the enemy at battle slot {s} could not be determined; the kill goes uncredited.");
                ModLogger.Debug(LogVerb.Trace, $"kill expiry detail (waited {_pendingAge[s]} ticks, {_actedFalls - _pendingFalls[s]} turn edges, battle slot {s})");
                _recorder?.Invoke("kill", $"no-credit slot={s} reason=expired-unresolved waited={_pendingAge[s]}ticks");
            }
        }
        return changed;
    }
}
