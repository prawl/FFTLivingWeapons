using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Why a corpse's kill was buried as untracked. Each of the stamp sites records its own reason so
/// the console no-credit line can say how the actor was resolved. None (0) is also the cleared
/// state, so Array.Clear works.
/// </summary>
internal enum UntrackedReason : byte
{
    None = 0,
    ChargedAction,   // cross-turn untracked charged action landing (the summoner arm window)
    EnemyTurn,       // death-edge TqTeam read said a non-player turn was in progress
    ActedLatch,      // the acted-period latch resolved a player with no tracked weapon
    ActorRegister,   // the actor register named a player with no tracked weapon (Stamp.cs bury)
    TurnFlags,       // the per-unit turn flags named a player with no tracked weapon (Stamp.cs bury)
}

/// <summary>
/// Band corpse scan and alive-edge belt, the per-slot death state machine of KillTracker. The
/// actor latch lives in KillTracker.cs and the enemy identity set in <see cref="EnemyOracle"/>.
///
/// A slot is dead when hp is 0 or the Dead status bit is set (+0x45/0x20, from the Doom research).
/// A status death such as Phoenix Down on undead sets the bit without hp reaching zero.
///
/// The alive-edge belt (_identityAlive) credits only on an alive-to-dead transition, and an
/// identity becomes creditable again once seen alive after a death. A dead identity at two
/// addresses still credits once, because the twin is never seen alive in between. Per-slot
/// 3-tick streaks filter phantom-load transients.
///
/// ScanCorpses reads each slot once into a <see cref="SlotSnapshot"/> and dispatches to
/// per-slot handlers. When HandleOrphanAliveEdge returns true it has repaired the belt but
/// stamped no culprit, and the caller must continue into ResolveCredit. docs/CHANGELOG.md has the
/// LW-150 entry for the pre-split shape.
///
/// LW-233 residual. ResolveCredit peeks the delayed-actor arm instead of consuming it before the
/// restart latch check. The arm (Tuning.DelayedActorWindow, ~12 ticks) is still much shorter than
/// RestartSentinelPolicy.LatchTicks (90), so an arm that fires while the latch is open can expire
/// unclaimed and fall back to the ordinary culprit. That is a miss, never a mis-credit (see
/// KillTracker.Delayed.cs).
/// </summary>
internal sealed partial class KillTracker
{
    private const int AliveNeeded = 3;  // consecutive valid alive ticks before seenAlive is set
    private const int DeadNeeded = 3;   // consecutive dead ticks before a credit attempt
    private const int FieldEvidenceNeeded = 3;  // consecutive qualifying ticks before MarkFielded

    // per-slot state (band slot index = 0..BandSlots-1)
    private readonly bool[] _seenAlive = new bool[Offsets.BandSlots];
    private readonly bool[] _deadCredited = new bool[Offsets.BandSlots];
    private readonly int[] _aliveStreak = new int[Offsets.BandSlots];
    private readonly int[] _deadStreak = new int[Offsets.BandSlots];
    // Consecutive ticks of scheduler participation (CT slam nonzero or turn flag ==1, real
    // position only), feeding EnemyOracle.MarkFielded. Debounced like the alive/dead streaks so one
    // load transient cannot mark a phantom seat as fielded.
    private readonly int[] _fieldEvidenceStreak = new int[Offsets.BandSlots];
    private readonly (byte lvl, byte br, byte fa)[] _slotId = new (byte, byte, byte)[Offsets.BandSlots];
    // Reliquary Phase 1 (docs/RELIQUARY_AC.md): victim identity captured at this slot's dead-edge
    // via VictimReader.Read, the same read VictimProbe uses. CreditKill consumes and clears it, and
    // the revive, identity-swap, reset and pending-expiry paths clear it for deaths that never
    // reach CreditKill.
    internal readonly VictimSnapshot[] _victimAtEdge = new VictimSnapshot[Offsets.BandSlots];
    // Weapon set latched at the alive->dead transition (deadStreak 0->1), copied from
    // _lastPlayerWeapons so a later re-latch cannot steal the credit. null = not yet stamped.
    private readonly List<int>?[] _lethalActor = new List<int>?[Offsets.BandSlots];
    // Whether the _lethalActor latch was a turn-queue-fallback resolve (set in KillTracker.Stamp.cs).
    // Feeds the battle-end fallback-attribution counter. Register-named stamps are false.
    internal readonly bool[] _lethalViaFallback = new bool[Offsets.BandSlots];
    // The kill fell during a resolved-but-untracked actor's live acted-period, so credit nobody.
    // Stamped at deadStreak 0->1 when _latchResolvedEmpty && _latched. The _latched guard keeps a
    // corpse dying after an untracked turn ends, before the next armed actor latches, from being
    // buried; that death has an unknown killer and goes pending.
    //
    // The in-period stamp covers summons resolving during the caster's own acted-period. The
    // cross-turn case is handled in KillTracker.Delayed.cs via _untrackedArmedTicks. The arm fires
    // at Charging-bit clear and sets this at deadStreak==1 ahead of the Count>0 branch, so a live
    // armed latch cannot absorb the summon's kill.
    private readonly UntrackedReason[] _lethalUntracked = new UntrackedReason[Offsets.BandSlots];

    // LW-233 (docs/LIVE_LEDGER.md row [battle-retry-rewind-fingerprint]): hp from this slot's most
    // recent dead tick. It is written only while the slot reads dead and left alone while alive,
    // because the AliveNeeded debounce (3 ticks) would otherwise overwrite it with already-positive
    // hp before the revive is recognized, and RestartSentinel's healedFromZero check
    // (_prevHp[s] == 0 && hp > 0) depends on it. A status death keeps hp positive, so it never
    // reads as a heal from zero. Filled from the SlotSnapshot, no extra memory reads.
    private readonly ushort[] _prevHp = new ushort[Offsets.BandSlots];
    // The post-CreditGate weapon set this slot was credited to, stamped in CreditKill
    // (KillTracker.cs). Empty = not credited. CreditKill always stores a fresh copy, so
    // RestartSentinel's uncredit can take this reference directly. Cleared with _lethalActor on
    // ResetBattleCorpses, re-arm completion and the identity-swap branch below.
    private readonly List<int>?[] _creditedWeapons = new List<int>?[Offsets.BandSlots];
    // Whether that credit used the turn-queue fallback, so an uncredit reverses the same
    // FallbackCredits counter CreditKill incremented.
    private readonly bool[] _creditedViaFallback = new bool[Offsets.BandSlots];
    // The (lvl,br,fa,maxHp) identity this slot was credited under, stamped in
    // CreditKill from slot.Id. RestartSentinel.PresentRevive compares it with the reviving unit's
    // current slot.Id, together with _creditedNameId below, to exempt the battle-age grace floor.
    // Cleared at the same three sites as _creditedWeapons.
    private readonly (byte lvl, byte br, byte fa, ushort mhp)[] _creditedIdentity = new (byte, byte, byte, ushort)[Offsets.BandSlots];

    // LW-233 finding 0 (2026-08-17): the credited victim's frame nameId (Offsets.ANameId), the second
    // half of RestartSentinel's identity gate. The (lvl,br,fa,maxHp) tuple alone collides:
    // docs/LIVE_LEDGER.md row [party-nameid-unique-key] shows two fingerprint twins with distinct
    // nameIds, which is why LW-252 moved other identity checks off the tuple. Both must match.
    // A false positive destroys earned kills and a miss only leaves the old bug, so the gate errs
    // toward missing. Stamped in CreditKill from the _victimAtEdge[s] snapshot. 0 = unavailable,
    // which RestartSentinel treats as fail-closed. Cleared with _creditedIdentity.
    //
    // Unverified (docs/LIVE_LEDGER.md row [retry-preserves-credited-identity]): whether a battle
    // retry keeps a revived unit's nameId. Player nameIds are stable across a reload
    // ([party-nameid-unique-key], caveat 3) but generic enemy nameIds re-roll per load
    // ([boss-canonical-nameid-stable]). If a retry re-rolls them, enemy retries become misses.
    private readonly ushort[] _creditedNameId = new ushort[Offsets.BandSlots];

    // Alive-edge belt. True = identity last seen alive (creditable), false = last seen dead.
    // Set true on seenAlive and on revive, false on credit or expiry without an actor.
    private readonly Dictionary<(byte lvl, byte br, byte fa, ushort mhp), bool> _identityAlive = new();

    /// <summary>Console phrase naming how the buried kill's actor was resolved, one per
    /// <see cref="UntrackedReason"/>. None never reaches the line.</summary>
    internal static string ResolveSourcePhrase(UntrackedReason reason) => reason switch
    {
        UntrackedReason.ChargedAction => "a charged-action landing",
        UntrackedReason.EnemyTurn => "an enemy-turn team read",
        UntrackedReason.ActedLatch => "the acted-period latch",
        UntrackedReason.ActorRegister => "the actor register",
        UntrackedReason.TurnFlags => "the turn flags",
        _ => "an unknown resolve path",
    };

    /// <summary>Reset per-battle band-scan and coverage state. Called from ResetBattle (KillTracker.cs).</summary>
    private void ResetBattleCorpses()
    {
        Array.Clear(_seenAlive, 0, _seenAlive.Length);
        Array.Clear(_deadCredited, 0, _deadCredited.Length);
        Array.Clear(_aliveStreak, 0, _aliveStreak.Length);
        Array.Clear(_deadStreak, 0, _deadStreak.Length);
        Array.Clear(_fieldEvidenceStreak, 0, _fieldEvidenceStreak.Length);
        Array.Clear(_slotId, 0, _slotId.Length);
        Array.Clear(_lethalActor, 0, _lethalActor.Length);
        Array.Clear(_lethalUntracked, 0, _lethalUntracked.Length);
        Array.Clear(_lethalViaFallback, 0, _lethalViaFallback.Length);
        Array.Clear(_victimAtEdge, 0, _victimAtEdge.Length);   // Reliquary. Clear every slot's captured snapshot
        Array.Clear(_prevHp, 0, _prevHp.Length);               // LW-233
        Array.Clear(_creditedWeapons, 0, _creditedWeapons.Length);
        Array.Clear(_creditedViaFallback, 0, _creditedViaFallback.Length);
        Array.Clear(_creditedIdentity, 0, _creditedIdentity.Length);   // LW-233
        Array.Clear(_creditedNameId, 0, _creditedNameId.Length);   // LW-233 FINDING 0
        _identityAlive.Clear();
        _oracle.ResetBattle();   // enemy identities + the coverage tick/flag
        _victimProbe.ResetBattle();   // P1 probe. Clear every slot's alive/edge snapshots
    }

    /// <summary>one band slot's per-tick memory read, taken once at the top of the
    /// ScanCorpses loop and passed by <c>in</c> reference to each per-slot handler. <see cref="Id"/>
    /// is the (lvl,br,fa,maxHp) identity tuple used as the oracle and alive-edge key.</summary>
    private readonly struct SlotSnapshot
    {
        internal readonly long Addr;
        internal readonly ushort Hp;
        internal readonly int Gx;
        internal readonly int Gy;
        internal readonly ushort MaxHp;
        internal readonly byte Lvl;
        internal readonly byte Br;
        internal readonly byte Fa;
        internal readonly bool DeadBit;
        internal readonly bool IsDead;
        // LW-233 finding 0 (2026-08-17): this slot's frame nameId (Offsets.ANameId). Mem.U16 returns
        // 0 on an unreadable page, so no Readable() branch is needed, and 0 is the existing "no
        // resolved identity" value that RestartSentinel's gate fails closed on. See _creditedNameId.
        internal readonly ushort NameId;

        internal SlotSnapshot(long addr, ushort hp, int gx, int gy, ushort maxHp, byte lvl, byte br, byte fa, bool deadBit, ushort nameId)
        {
            Addr = addr;
            Hp = hp;
            Gx = gx;
            Gy = gy;
            MaxHp = maxHp;
            Lvl = lvl;
            Br = br;
            Fa = fa;
            DeadBit = deadBit;
            NameId = nameId;
            IsDead = hp == 0 || deadBit;
        }

        internal (byte lvl, byte br, byte fa, ushort mhp) Id => (Lvl, Br, Fa, MaxHp);
    }

    /// <summary>Band corpse scan + identity capture. Returns true if the tally changed.</summary>
    private bool ScanCorpses(bool onField)
    {
        bool changed = false;
        if (onField) _oracle.TickField();   // capture enemy identities + the once-per-battle coverage log

        for (int s = 0; s < Offsets.BandSlots; s++)
        {
            long addr = Band.Entry(s);
            if (!Band.IsValid(_mem, addr)) { _aliveStreak[s] = 0; _deadStreak[s] = 0; _fieldEvidenceStreak[s] = 0; continue; }

            var slot = new SlotSnapshot(
                addr,
                _mem.U16(addr + Offsets.AHp),
                _mem.U8(addr + Offsets.AGx),
                _mem.U8(addr + Offsets.AGy),
                _mem.U16(addr + Offsets.AMaxHp),
                _mem.U8(addr + Offsets.ALevel),
                _mem.U8(addr + Offsets.ABrave),
                _mem.U8(addr + Offsets.AFaith),
                // Dead bit +0x45/0x20 (Doom research). U8 returns 0 on unreadable address -- safe.
                (_mem.U8(addr + Offsets.ADeadStatus) & Offsets.ADeadBit) != 0,
                _mem.U16(addr + Offsets.ANameId));   // LW-233 FINDING 0. The presented-identity nameId half

            if (onField) ScanFieldEvidence(s, in slot);

            if (!slot.IsDead) { ScanAlivePath(s, onField, in slot, ref changed); continue; }

            // Record the dead hp before dispatching so early returns keep it. Alive slots
            // are left alone on purpose, see _prevHp.
            _prevHp[s] = slot.Hp;
            ScanDeadPath(s, onField, in slot, ref changed);
        }
        return changed;
    }

    /// <summary>Dev event timeline tap plus the LW-34 scheduler-participation streak feeding
    /// EnemyOracle.MarkFielded.</summary>
    private void ScanFieldEvidence(int s, in SlotSnapshot slot)
    {
        _events?.Observe(s, slot.Hp, slot.MaxHp, slot.Gx, slot.Gy, _actorTag);

        // Scheduler participation (CT slam nonzero or turn flag ==1; LW-63 raw-byte
        // semantics, so a flag is not a plain boolean). The real-position filter keeps a frozen
        // (0,0) mirror's stale evidence out. Phantom seats have sane stats, full HP and real tiles
        // (tape-evidenced 2026-07-11), so scheduler participation is the only discriminator.
        byte slam = _mem.U8(slot.Addr + Offsets.ACtSlam);
        byte tflag = _mem.U8(slot.Addr + Offsets.ATurnFlag);
        if ((slam != 0 || tflag == 1) && (slot.Gx != 0 || slot.Gy != 0))
        {
            if (++_fieldEvidenceStreak[s] >= FieldEvidenceNeeded)
                _oracle.MarkFielded(slot.Id);
        }
        else
        {
            _fieldEvidenceStreak[s] = 0;
        }
    }

    /// <summary>Alive-side streak and belt bookkeeping. The caller continues its loop after this
    /// returns.</summary>
    private void ScanAlivePath(int s, bool onField, in SlotSnapshot slot, ref bool changed)
    {
        _deadStreak[s] = 0;
        if (!onField) return;
        if (_seenAlive[s] && (_slotId[s].lvl != slot.Lvl || _slotId[s].br != slot.Br || _slotId[s].fa != slot.Fa))
        {
            // The previous identity may have been credited. A checkpoint retry can restore
            // brave/faith (a status or Rend-family hit undone by the rewind) as the corpse revives
            // from 0 hp, which lands here and not in the re-arm block below. Present it to the
            // sentinel before the reset wipes the old state. A null list means never credited.
            //
            // Finding 6(ii) residual (2026-08-17), not handled. This branch only runs when the tuple
            // differs from _slotId[s], and _creditedIdentity[s] was stamped from the tuple that
            // seeded _slotId[s], so slot.Id can never equal _creditedIdentity[s] here. The identity
            // match is always false on this branch and a retry relies on the battle-age grace
            // (RestartSentinelPolicy.GraceTicks) alone. A victim that was Rend-Braved, Charmed or
            // Praised before dying and revived within the drill's battleMode-0 window, which starves
            // grace, still double-counts.
            var creditedHere = _creditedWeapons[s];
            if (creditedHere != null)
            {
                bool healedFromZero = _prevHp[s] == 0 && slot.Hp > 0;
                if (_restart.PresentRevive(s, creditedHere, _creditedViaFallback[s], healedFromZero,
                                            _creditedIdentity[s], slot.Id,
                                            _creditedNameId[s], slot.NameId) == RevivePresentResult.UncreditNow)
                {
                    UncreditKills(creditedHere, _creditedViaFallback[s], s);
                    changed = true;
                }
            }
            _seenAlive[s] = false; _aliveStreak[s] = 0;
            _deadCredited[s] = false; _pending[s] = false;
            _lethalActor[s] = null; _lethalUntracked[s] = UntrackedReason.None;
            _creditedWeapons[s] = null; _creditedViaFallback[s] = false;   // a stale set could misattribute a later revive
            _creditedIdentity[s] = default;   // mirrors _creditedWeapons' own clear, same reasoning
            _creditedNameId[s] = 0;   // LW-233 FINDING 0. Mirrors _creditedIdentity's own clear
            _victimProbe.Reset(s);   // P1 probe. A slot-reuse identity swap invalidates any stale snapshot
            _victimAtEdge[s] = default;   // Reliquary. Same invalidation for the captured snapshot
            return;
        }
        _aliveStreak[s]++;
        if (_aliveStreak[s] >= AliveNeeded && !_seenAlive[s])
        {
            _seenAlive[s] = true;
            _slotId[s] = (slot.Lvl, slot.Br, slot.Fa);
            _identityAlive[slot.Id] = true;   // alive-edge. Creditable now
        }
        if (_seenAlive[s])
        {
            // revive after a credited death. Re-enable the alive-edge for this identity
            if (_deadCredited[s])
            {
                _identityAlive[slot.Id] = true;
                // A credited corpse came back alive. If a checkpoint retry's rewind is why
                // (RestartSentinel's latch, possibly via its deferred-verdict stash), the credit
                // was a phantom, so pay it back before the clears below.
                var creditedHere = _creditedWeapons[s];
                if (creditedHere != null)
                {
                    bool healedFromZero = _prevHp[s] == 0 && slot.Hp > 0;
                    if (_restart.PresentRevive(s, creditedHere, _creditedViaFallback[s], healedFromZero,
                                                _creditedIdentity[s], slot.Id,
                                                _creditedNameId[s], slot.NameId) == RevivePresentResult.UncreditNow)
                    {
                        UncreditKills(creditedHere, _creditedViaFallback[s], s);
                        changed = true;
                    }
                }
            }
            _deadCredited[s] = false;
            _pending[s] = false;
            _lethalActor[s] = null; _lethalUntracked[s] = UntrackedReason.None;
            _creditedWeapons[s] = null; _creditedViaFallback[s] = false;   // clear every seenAlive tick, mirrors _lethalActor above
            _creditedIdentity[s] = default;   // mirrors _creditedWeapons' own clear, same reasoning
            _creditedNameId[s] = 0;   // LW-233 FINDING 0. Mirrors _creditedIdentity's own clear
            // P1 probe. A revived victim's old dead-edge snapshot is stale. Clear it before
            // CaptureAlive below repopulates the alive half, since this runs every seenAlive tick.
            _victimProbe.Reset(s);
            // Reliquary. A no-credit death never reaches CreditKill's consume, so clear here or a
            // revived identity carries a stale snapshot into its next death. After a real credit
            // CreditKill has already cleared it, so this is a no-op then.
            _victimAtEdge[s] = default;
        }
        _victimProbe.CaptureAlive(s, slot.Addr);   // P1 probe. Every consistent alive on-field tick
    }

    /// <summary>Dispatcher for one dead slot. Guard clauses, then StampDeadEdge, OracleGate,
    /// HandleOrphanAliveEdge and ResolveCredit.</summary>
    private void ScanDeadPath(int s, bool onField, in SlotSnapshot slot, ref bool changed)
    {
        // dead path (hp==0 or Dead bit set)
        if (_deadCredited[s]) return;
        if (!_seenAlive[s]) return;
        if (_slotId[s].lvl != slot.Lvl || _slotId[s].br != slot.Br || _slotId[s].fa != slot.Fa) return;
        if (!onField) return;

        StampDeadEdge(s, in slot);

        if (_deadStreak[s] < DeadNeeded) return;

        if (!OracleGate(s, in slot)) return;

        // alive-edge. Credit only on alive->dead transition; a frozen twin is never alive here.
        // Split PRESENT-false from ABSENT. Present-false means the identity already
        // resolved this battle (credited or no-credit) or is a same-tuple mirror clone, so keep
        // blocking. Absent means the alive-edge was stamped under a different maxHp than this death
        // (ScanAlivePath guarantees the same lvl/br/fa), a real uncredited enemy death.
        if (_identityAlive.TryGetValue(slot.Id, out bool wasAlive))
        {
            if (!wasAlive)
            {
                _deadCredited[s] = true;
                ModLogger.Warn(LogVerb.Kill, $"Blocked a repeat credit: an enemy whose identity was already resolved this battle died again at battle slot {s}.");
                _recorder?.Invoke("kill", $"no-credit slot={s} reason=identity-already-resolved");
                return;
            }
            // wasAlive == true. Normal creditable path; fall through below.
        }
        else
        {
            // False means the orphan handler already resolved the slot, so stop. True means it
            // repaired the belt but stamped no culprit, so continue into ResolveCredit.
            if (!HandleOrphanAliveEdge(s, in slot, ref changed)) return;
        }

        ResolveCredit(s, in slot, ref changed);
    }

    /// <summary>Stamp the actor at the alive->dead edge. The TqTeam read stays here, gated on
    /// deadStreak==1, so it captures the team at the moment of death.</summary>
    private void StampDeadEdge(int s, in SlotSnapshot slot)
    {
        _deadStreak[s]++;
        // Only one branch fires per edge; they are mutually exclusive on _lastPlayerWeapons.Count.
        if (_deadStreak[s] == 1)
        {
            _victimProbe.CaptureDeadEdge(s, slot.Addr);   // P1 probe. Log-only, zero behavioral dependence
            _victimAtEdge[s] = VictimReader.Read(_mem, slot.Addr);   // Reliquary. The behavioral capture CreditKill consumes
            _oracle.MarkDead(slot.Id);   // LW-34 evidence. A corpse was definitionally fielded
            // TqTeam may flip a few ticks after death, so read it at the edge. Divert only on a
            // confident non-player team (1=enemy, 2=ally/guest). Any other value (0=player, garbage,
            // or an unreadable 0) takes the normal credit path so a bad read cannot suppress a
            // legit player kill (docs/LIVE_LEDGER.md row 45, TqTeam fail-safe rule). A tracked
            // delayed action still wins, since ConsumeDelayedCulprit() is checked first at credit
            // time and the no-credit branch requires `delayed == null && _lethalUntracked[s]`.
            int team = _mem.Readable(Offsets.TurnQueue + Offsets.TqTeam, 2) ? _mem.U16(Offsets.TurnQueue + Offsets.TqTeam) : 0;
            bool nonPlayerTurn = team == 1 || team == 2;   // 1=enemy, 2=ally/guest; both are AI turns
            if (_untrackedArmedTicks > 0)
            {
                // A cross-turn untracked charged action (a summoner's summon) is landing inside its
                // arm window, so bury the kill whatever the live latch says. This goes ahead of the
                // Count>0 branch so an armed ally acting between cast and landing cannot absorb the
                // kill. An unrelated armed kill inside the window becomes a no-credit miss, not a
                // mis-credit. The arm is not consumed, so every AoE victim in the window is stamped,
                // and the window timer expires it. A tracked delayed action still wins at credit time
                // via the `delayed == null` guard.
                _lethalUntracked[s] = UntrackedReason.ChargedAction;
            }
            else if (_lastPlayerWeapons.Count > 0)
            {
                if (nonPlayerTurn)
                    _lethalUntracked[s] = UntrackedReason.EnemyTurn;   // counter/reaction on a non-player turn, so the stale player latch did not deal this blow; no-credit (miss beats mis-credit)
                else
                    // Tracked actor latched on a player turn. Consult the death-edge culprit
                    // stamp (KillTracker.Stamp.cs), which keeps the latch unless the register
                    // names a fresher, disagreeing killer.
                    StampCulprit(s);
            }
            else if (_latchResolvedEmpty && _latched)
            {
                // The kill fell during a resolved-but-untracked actor's live acted-period
                // (summoner, dancer, item-user). The _latched guard excludes a different armed unit
                // that acted before its own acted edge while the empty latch is still sticky; that
                // unit must still be credited through the pending path. The bury consults the
                // death-edge culprit hypothesis first (KillTracker.Stamp.cs).
                StampCulpritFromEmptyLatch(s);
            }
            else if (!nonPlayerTurn)
            {
                // Empty latch, no untracked verdict, player turn. The pending-hole extension
                // (see StampCulpritFromHypothesisOnly, KillTracker.Stamp.cs).
                StampCulpritFromHypothesisOnly(s);
            }
        }
    }

    /// <summary>Tracked-enemy gate. False means not a tracked enemy (already marked no-credit) and
    /// the caller stops.</summary>
    private bool OracleGate(int s, in SlotSnapshot slot)
    {
        if (!_oracle.Contains(slot.Id))
        {
            _deadCredited[s] = true;
            ModLogger.Debug(LogVerb.Kill, $"the unit at battle slot {s} died but was not a tracked enemy; no credit given (normal for player and guest deaths)");
            _recorder?.Invoke("kill", $"no-credit slot={s} reason=not-tracked-enemy");
            return false;
        }
        return true;
    }

    /// <summary>LW-68 orphaned alive-edge repair. The caller guarantees the slot is seen-alive and
    /// oracle-confirmed. Returns false when already resolved (buried or credited). Returns true when
    /// no culprit is stamped yet, and the caller must then run ResolveCredit.</summary>
    private bool HandleOrphanAliveEdge(int s, in SlotSnapshot slot, ref bool changed)
    {
        // Repair the belt and credit from this slot's own death-edge stamp only. This skips
        // ConsumeDelayedCulprit, because the global delayed latch belongs to a charged action in
        // flight and an orphan at a lower slot index must not steal it from its real victim. A
        // stamped no-credit verdict (_lethalUntracked) is still honored.
        _recorder?.Invoke("kill", $"orphan-alive-edge slot={s} mhp={slot.MaxHp}");
        _identityAlive[slot.Id] = true;   // repair the orphaned edge for any later revive/re-death
        if (_lethalUntracked[s] != UntrackedReason.None)
        {
            _deadCredited[s] = true; _pending[s] = false;
            LogUntrackedBury(s);
            return false;
        }
        var orphanCulprit = _lethalActor[s] ?? _lastPlayerWeapons;
        if (orphanCulprit.Count > 0)
        {
            bool orphanViaFallback = _lethalActor[s] != null ? _lethalViaFallback[s] : _latchViaFallback;
            _deadCredited[s] = true;
            if (CreditKill(s, slot.Gx, slot.Gy, orphanCulprit, orphanViaFallback, identity: slot.Id)) { _identityAlive[slot.Id] = false; changed = true; }
            return false;
        }
        // No culprit stamped yet, so continue into the shared pending/credit machinery.
        return true;
    }

    /// <summary>the untracked-bury log, shared by <see cref="HandleOrphanAliveEdge"/> and
    /// <see cref="ResolveCredit"/> so the console and the flight tape cannot drift. Names the victim
    /// from the dead-edge snapshot and how the untracked actor was resolved. The wording is frozen
    /// (docs/LOGGING.md). The verdict state writes stay at the call sites because the orphan path
    /// keeps _identityAlive true.</summary>
    private void LogUntrackedBury(int s)
    {
        var vs = _victimAtEdge[s];
        string fellPhrase = VictimClass.FellPhrase(vs.Has, vs.Job, vs.Undead);
        string fallenNoun = fellPhrase.StartsWith("an ") ? fellPhrase.Substring(3) : fellPhrase.Substring(2);
        ModLogger.Event(LogVerb.Kill, $"The fallen {fallenNoun} at battle slot {s} was slain by a player carrying no Living Weapon; the kill is deliberately left uncredited (actor resolved via {ResolveSourcePhrase(_lethalUntracked[s])}).");
        _recorder?.Invoke("kill", $"no-credit slot={s} reason=untracked-weapon via={_lethalUntracked[s]}");
    }

    /// <summary>Credit, pending and expiry handling, reached from the normal wasAlive path and from
    /// HandleOrphanAliveEdge's fall-through.</summary>
    private void ResolveCredit(int s, in SlotSnapshot slot, ref bool changed)
    {
        // Prefer the delayed actor (snapshotted at commit, armed at landing). The
        // `delayed == null` guard on the no-credit branch below orders delayed ahead of
        // _lethalUntracked, so a tracked Jump/charge committer whose kill matures while an untracked
        // unit is latched still wins. Both can exist on one slot because the delayed arm is set
        // separately via TrackDelayed (T4 pins it).
        //
        // Peek the arm instead of consuming it. ConsumeDelayedCulprit's first-credit-wins
        // would burn the arm on a tick that credits nobody (restart-latch suppression below). Reads
        // the same private fields ConsumeDelayedCulprit guards on (KillTracker.Delayed.cs). The arm
        // is consumed only once this tick will use it; bury, pending and expire leave it intact.
        bool delayedPeek = _delayedArmedTicks > 0 && _delayedActor != null && _delayedActor.Count > 0;
        List<int>? delayedCandidate = delayedPeek ? _delayedActor : null;
        if (delayedCandidate == null && _lethalUntracked[s] != UntrackedReason.None)
        {
            // Stamped during a roster player's turn with no tracked weapon. Credit nobody. A
            // summoner/dancer/item-user's AoE kill is intentionally uncredited.
            _deadCredited[s] = true; _pending[s] = false; _identityAlive[slot.Id] = false;
            LogUntrackedBury(s);
            return;
        }
        var culprit = delayedCandidate ?? _lethalActor[s] ?? _lastPlayerWeapons;
        // While the restart latch is open, a fresh dead-edge may be the rewind phantom-death
        // lane (a pre-checkpoint corpse Raised post-checkpoint snapping back to dead at the
        // restore), so hold it pending instead of crediting. The pending/expiry code below resolves
        // it once the latch closes. This can outlast an armed delayed culprit if the latch stays open
        // past Tuning.DelayedActorWindow, the residual described in the class doc.
        bool restartSuppressed = culprit.Count > 0 && _restart.LatchOpen;
        if (culprit.Count > 0 && !restartSuppressed)
        {
            List<int>? delayed = delayedCandidate != null ? ConsumeDelayedCulprit() : null;
            if (delayed != null)
                ModLogger.EventWithTrace(LogVerb.Credit,
                    $"Crediting the charged attack that just landed (a Jump or a spellcast) to the unit that committed it, wielding {string.Join(", ", delayed.ConvertAll(LogNames.Weapon))}, not the unit acting now.",
                    $"delayed-credit detail (delayed=[{string.Join(",", delayed)}] stamped=[{string.Join(",", _lethalActor[s] as IEnumerable<int> ?? Array.Empty<int>())}] current=[{string.Join(",", _lastPlayerWeapons)}], battle slot {s})");
            else if (_lethalActor[s] != null && !ActorResolver.SameSet(_lethalActor[s]!, _lastPlayerWeapons))
                ModLogger.EventWithTrace(LogVerb.Credit,
                    $"Crediting the unit that landed the finishing blow, wielding {string.Join(", ", culprit.ConvertAll(LogNames.Weapon))}, rather than the unit that acted most recently.",
                    $"finishing-blow detail (lethal=[{string.Join(",", culprit)}] live-latch=[{string.Join(",", _lastPlayerWeapons)}], battle slot {s}, dead-streak {_deadStreak[s]})");
            bool viaFallback = delayed == null && (_lethalActor[s] != null ? _lethalViaFallback[s] : _latchViaFallback);
            bool c = CreditKill(s, slot.Gx, slot.Gy, culprit, viaFallback, delayedOrCharged: delayed != null, identity: slot.Id);
            _deadCredited[s] = true;
            // LW-68/D2/B5. Burn the shared alive-edge only when a credit landed. A refused credit
            // (no live wielder) leaves it true so a later wielder-backed kill of the same tuple can
            // still credit. The other no-credit paths adjudicated or exhausted the search, so only
            // this one needs the guard.
            if (c) { _identityAlive[slot.Id] = false; changed = true; }
        }
        else if (!_pending[s])
        {
            _pending[s] = true; _pendingAge[s] = 0; _pendingFalls[s] = _actedFalls;
            var vs = _victimAtEdge[s];
            string fell = VictimClass.FellPhrase(vs.Has, vs.Job, vs.Undead);
            string opener = char.ToUpperInvariant(fell[0]) + fell.Substring(1);
            _klog.Info(slot.DeadBit && slot.Hp > 0
                ? $"{opener} fell at ({slot.Gx},{slot.Gy}); killed by a status effect, waiting to see whose attack caused it (battle slot {s})."
                : $"{opener} fell at ({slot.Gx},{slot.Gy}); waiting to identify the killer (battle slot {s}).");
            _recorder?.Invoke("kill", $"pending slot={s} at=({slot.Gx},{slot.Gy}) status={(slot.DeadBit && slot.Hp > 0 ? 1 : 0)}");
        }
        else if (_actedFalls - _pendingFalls[s] >= ExpireFalls || ++_pendingAge[s] > PendingTtl)
        {
            _deadCredited[s] = true; _pending[s] = false;
            _lethalActor[s] = null; _lethalUntracked[s] = UntrackedReason.None;
            _victimAtEdge[s] = default;   // Reliquary. This no-credit path never reaches CreditKill's consume
            _identityAlive[slot.Id] = false;
            _klog.Warn($"The killer of the enemy at battle slot {s} could not be determined; the kill goes uncredited.");
            ModLogger.Debug(LogVerb.Trace, $"kill expiry detail (waited {_pendingAge[s]} ticks, {_actedFalls - _pendingFalls[s]} turn edges, battle slot {s})");
            _recorder?.Invoke("kill", $"no-credit slot={s} reason=expired-unresolved waited={_pendingAge[s]}ticks");
        }
    }
}
