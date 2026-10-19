using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Why a kill went unpaid because the killer carried no Living Weapon. Each reason names the clue
/// that told us who the killer was, so the console line can say it. None is zero, so wiping the
/// array resets every slot.
/// </summary>
internal enum UntrackedReason : byte
{
    None = 0,
    ChargedAction,   // an unarmed unit's summon or charged spell landed after the caster's turn
    EnemyTurn,       // the death came on an enemy or guest turn, so a counter or reaction did it
    ActedLatch,      // the unit acting this turn had no Living Weapon
    ActorRegister,   // the game's attacker pointer named a unit with no Living Weapon
    TurnFlags,       // the per unit turn flags named a unit with no Living Weapon
}

/// <summary>
/// Watches every unit slot each tick, spots enemy deaths and decides whose weapon gets the kill.
/// The guess at who is acting lives in KillTracker.cs and the list of real enemies in
/// <see cref="EnemyOracle"/>.
///
/// A unit is dead when its hp is 0 or its Dead status bit is set (+0x45 bit 0x20). The bit matters
/// because some deaths, like a Phoenix Down on an undead, never take hp to zero.
///
/// A kill pays only when a unit goes from alive to dead, and that unit must be seen alive again
/// before another death of it can pay. The game can show one corpse in two slots, and the copy is
/// never seen alive, so it never pays twice. Alive and dead each need 3 ticks in a row before they
/// count, which filters out flickers while a map loads.
///
/// Retry gap. ResolveCredit only peeks at a landed charged attack instead of claiming it. The
/// charge stays armed for about 12 ticks (Tuning.DelayedActorWindow) while the restart latch holds
/// for 90 (RestartSentinelPolicy.LatchTicks), so a charge landing while the latch is open can time
/// out first and the kill falls back to the ordinary killer guess. At worst a kill is missed, never
/// paid to the wrong weapon (see KillTracker.Delayed.cs).
/// </summary>
internal sealed partial class KillTracker
{
    private const int AliveNeeded = 3;  // ticks in a row reading alive before the unit is trusted
    private const int DeadNeeded = 3;   // ticks in a row reading dead before we try to pay
    private const int FieldEvidenceNeeded = 3;  // ticks in a row taking turns before it counts as fielded

    // One entry per unit slot in the battle.
    private readonly bool[] _seenAlive = new bool[Offsets.BandSlots];
    private readonly bool[] _deadCredited = new bool[Offsets.BandSlots];   // this death is settled, paid or not
    private readonly int[] _aliveStreak = new int[Offsets.BandSlots];
    private readonly int[] _deadStreak = new int[Offsets.BandSlots];
    // Ticks in a row this unit looked like it was really taking turns, its charge time moving or its
    // turn flag up, while standing on a real tile. A ghost copy can flash that for a tick during a
    // load, so it has to hold a few ticks before EnemyOracle counts the unit as fielded.
    private readonly int[] _fieldEvidenceStreak = new int[Offsets.BandSlots];
    private readonly (byte lvl, byte br, byte fa)[] _slotId = new (byte, byte, byte)[Offsets.BandSlots];   // saved when first trusted alive
    // Who the victim was (job, undead and so on), read the moment it died. CreditKill uses it up.
    // Every death that never reaches CreditKill wipes it itself, or it would leak into that unit's
    // next death.
    internal readonly VictimSnapshot[] _victimAtEdge = new VictimSnapshot[Offsets.BandSlots];
    // The killer's weapons, copied the moment the unit died so a later turn cannot steal the kill.
    // Null means no killer stamped yet.
    private readonly List<int>?[] _lethalActor = new List<int>?[Offsets.BandSlots];
    // True when that killer was a best guess from the turn order rather than the game naming the
    // attacker. The battle end report counts these guesses.
    internal readonly bool[] _lethalViaFallback = new bool[Offsets.BandSlots];
    // Set when the killer was a unit with no Living Weapon, so the kill pays nobody. The reason says
    // which clue told us. An unarmed unit acting now is blamed only while its turn is still going.
    // After its turn ends, and before the next armed unit is picked up, the killer is unknown and
    // the death waits.
    //
    // A summon that lands a turn after it was cast is stamped from KillTracker.Delayed.cs ahead of
    // everything else, so an armed ally acting in between cannot take the summon's kill.
    private readonly UntrackedReason[] _lethalUntracked = new UntrackedReason[Offsets.BandSlots];

    // Hp from the last tick this slot read dead. Alive ticks leave it alone, because the 3 tick
    // alive wait would overwrite it with fresh hp before the revive is noticed, and the restart
    // check needs to see the jump from 0 hp to alive (docs/LIVE_LEDGER.md row
    // [battle-retry-rewind-fingerprint]). A status death keeps hp above 0, so it never looks like a
    // heal from zero.
    private readonly ushort[] _prevHp = new ushort[Offsets.BandSlots];
    // The weapons this death paid, after CreditGate filtered them. Null means unpaid. CreditKill
    // always stores a fresh list here, never a shared one, which is why the restart check can take
    // the kill back using this exact list without copying it.
    private readonly List<int>?[] _creditedWeapons = new List<int>?[Offsets.BandSlots];
    // Whether that payment was a turn order guess, so taking it back also takes back the guess count.
    private readonly bool[] _creditedViaFallback = new bool[Offsets.BandSlots];
    // Level, brave, faith and max hp of the unit when it was paid. If a revived unit matches this
    // and the name id below, the restart check may take the kill back even early in a battle.
    // Wiped wherever _creditedWeapons is.
    private readonly (byte lvl, byte br, byte fa, ushort mhp)[] _creditedIdentity = new (byte, byte, byte, ushort)[Offsets.BandSlots];

    // The paid victim's name id, the other half of that match. Level, brave, faith and max hp alone
    // can be the same for two different units (docs/LIVE_LEDGER.md row [party-nameid-unique-key]).
    // Taking back a real kill is worse than missing a fake one, so both halves must match, and 0
    // means unknown and never matches.
    //
    // Unproven (docs/LIVE_LEDGER.md row [retry-preserves-credited-identity]) whether a retry keeps a
    // revived unit's name id. Player ids survive a reload but generic enemy ids are rerolled each
    // load, so if a retry rerolls them an enemy retry is simply missed.
    private readonly ushort[] _creditedNameId = new ushort[Offsets.BandSlots];

    // Shared by every slot. True means this unit was last seen alive, so its death can pay. False
    // means its death is already settled. Missing means never seen alive with this exact max hp.
    private readonly Dictionary<(byte lvl, byte br, byte fa, ushort mhp), bool> _identityAlive = new();

    /// <summary>Console words for how the unarmed killer was found. None is never printed.</summary>
    internal static string ResolveSourcePhrase(UntrackedReason reason) => reason switch
    {
        UntrackedReason.ChargedAction => "a charged-action landing",
        UntrackedReason.EnemyTurn => "an enemy-turn team read",
        UntrackedReason.ActedLatch => "the acted-period latch",
        UntrackedReason.ActorRegister => "the actor register",
        UntrackedReason.TurnFlags => "the turn flags",
        _ => "an unknown resolve path",
    };

    /// <summary>Wipe every slot's death and payment record when a battle starts or ends.</summary>
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
        Array.Clear(_victimAtEdge, 0, _victimAtEdge.Length);
        Array.Clear(_prevHp, 0, _prevHp.Length);
        Array.Clear(_creditedWeapons, 0, _creditedWeapons.Length);
        Array.Clear(_creditedViaFallback, 0, _creditedViaFallback.Length);
        Array.Clear(_creditedIdentity, 0, _creditedIdentity.Length);
        Array.Clear(_creditedNameId, 0, _creditedNameId.Length);
        _identityAlive.Clear();
        _oracle.ResetBattle();   // also forgets which units were enemies
        _victimProbe.ResetBattle();
    }

    /// <summary>Everything read from one unit slot this tick, read once and handed to every step.
    /// <see cref="Id"/> is level, brave, faith and max hp, the key used to tell units apart.</summary>
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
        // An unreadable page reads as 0, which already means unknown, so no extra check is needed.
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

    /// <summary>Check every unit slot once this tick. True when a kill was paid or taken back.</summary>
    private bool ScanCorpses(bool onField)
    {
        bool changed = false;
        if (onField) _oracle.TickField();   // learns which units are enemies

        for (int s = 0; s < Offsets.BandSlots; s++)
        {
            long addr = Band.Entry(s);
            // An empty or garbage slot breaks every streak.
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
                // An unreadable byte reads 0, which counts as not dead.
                (_mem.U8(addr + Offsets.ADeadStatus) & Offsets.ADeadBit) != 0,
                _mem.U16(addr + Offsets.ANameId));

            if (onField) ScanFieldEvidence(s, in slot);

            if (!slot.IsDead) { ScanAlivePath(s, onField, in slot, ref changed); continue; }

            // Saved first so the early exits in the dead path cannot skip it.
            _prevHp[s] = slot.Hp;
            ScanDeadPath(s, onField, in slot, ref changed);
        }
        return changed;
    }

    /// <summary>Feeds the debug event timeline, and counts how long this unit has really been taking
    /// turns so EnemyOracle can tell real enemies from ghost copies.</summary>
    private void ScanFieldEvidence(int s, in SlotSnapshot slot)
    {
        _events?.Observe(s, slot.Hp, slot.MaxHp, slot.Gx, slot.Gy, _actorTag);

        // Ghost copies have normal stats, full hp and a real tile (seen on the flight tape), so taking
        // turns is the only tell. The turn flag holds other values too, so only exactly 1 counts. A
        // frozen copy parked at tile (0,0) is ignored.
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

    /// <summary>This slot reads alive. Count toward trusting it, and if it was a paid corpse that
    /// came back, ask whether a retry undid the kill.</summary>
    private void ScanAlivePath(int s, bool onField, in SlotSnapshot slot, ref bool changed)
    {
        _deadStreak[s] = 0;
        if (!onField) return;
        if (_seenAlive[s] && RecordedIdentityChanged(s, in slot))
        {
            // A different unit took this slot, or a retry rewound brave or faith (a Rend or status
            // hit undone) as the corpse came back. Give the restart check its shot at the old
            // payment before the record is wiped.
            //
            // Known gap. The paid identity was saved from the old level, brave and faith, so it
            // cannot match here, and the restart check only fires once the battle is old enough
            // (RestartSentinelPolicy.GraceTicks). If the battle age is still young at the revive,
            // say because the field read empty long enough to reset it, the kill counts twice.
            UncreditIfRestartRevive(s, in slot, ref changed);
            _seenAlive[s] = false; _aliveStreak[s] = 0;
            ForgetDeathAndCredit(s);
            return;
        }
        _aliveStreak[s]++;
        if (_aliveStreak[s] >= AliveNeeded && !_seenAlive[s])
        {
            _seenAlive[s] = true;
            _slotId[s] = (slot.Lvl, slot.Br, slot.Fa);
            _identityAlive[slot.Id] = true;   // its next death can pay
        }
        if (_seenAlive[s])
        {
            if (_deadCredited[s])
            {
                _identityAlive[slot.Id] = true;
                UncreditIfRestartRevive(s, in slot, ref changed);
            }
            ForgetDeathAndCredit(s);
        }
        _victimProbe.CaptureAlive(s, slot.Addr);   // debug probe only
    }

    /// <summary>True when level, brave or faith no longer match what this slot showed when first
    /// trusted alive, so it is another unit or a rewound one. Max hp is left out because one unit can
    /// show a different max hp alive than dead (see ScanDeadPath).</summary>
    private bool RecordedIdentityChanged(int s, in SlotSnapshot slot) =>
        _slotId[s].lvl != slot.Lvl || _slotId[s].br != slot.Br || _slotId[s].fa != slot.Fa;

    /// <summary>A paid corpse is standing again. If a checkpoint retry rewound the battle, that kill
    /// never really happened, so take it back. The restart check may hold its answer and decide a
    /// few ticks later instead. Does nothing for a slot that was never paid.</summary>
    private void UncreditIfRestartRevive(int s, in SlotSnapshot slot, ref bool changed)
    {
        var creditedHere = _creditedWeapons[s];   // safe to hand over as is, see _creditedWeapons
        if (creditedHere == null) return;
        bool healedFromZero = _prevHp[s] == 0 && slot.Hp > 0;
        if (_restart.PresentRevive(s, creditedHere, _creditedViaFallback[s], healedFromZero,
                                    _creditedIdentity[s], slot.Id,
                                    _creditedNameId[s], slot.NameId) == RevivePresentResult.UncreditNow)
        {
            UncreditKills(creditedHere, _creditedViaFallback[s], s);
            changed = true;
        }
    }

    /// <summary>Wipe this slot's death verdict, killer stamps, payment record and victim snapshots.
    /// Runs when the unit changes and on every trusted alive tick, so nothing old can pay for a
    /// later death or confuse a later revive.</summary>
    private void ForgetDeathAndCredit(int s)
    {
        _deadCredited[s] = false;
        _pending[s] = false;
        _lethalActor[s] = null; _lethalUntracked[s] = UntrackedReason.None;
        _creditedWeapons[s] = null; _creditedViaFallback[s] = false;
        _creditedIdentity[s] = default;
        _creditedNameId[s] = 0;
        // Must run before the probe's alive capture later this tick.
        _victimProbe.Reset(s);
        // An unpaid death never reaches CreditKill to use this up, so without the wipe a revived unit
        // would carry a stale victim into its next death.
        _victimAtEdge[s] = default;
    }

    /// <summary>This slot reads dead. Skip it if already settled, never trusted alive, a different
    /// unit, or off the field. Otherwise stamp the killer, check it is a real enemy, and try to
    /// pay.</summary>
    private void ScanDeadPath(int s, bool onField, in SlotSnapshot slot, ref bool changed)
    {
        if (_deadCredited[s]) return;
        if (!_seenAlive[s]) return;
        if (RecordedIdentityChanged(s, in slot)) return;
        if (!onField) return;

        StampDeadEdge(s, in slot);

        if (_deadStreak[s] < DeadNeeded) return;

        if (!OracleGate(s, in slot)) return;

        // Pay only if this unit was last seen alive. False means its death is already settled, or
        // this is a mirror copy of one that already died, so block it. Missing means it was seen
        // alive with a different max hp than it died with. Level, brave and faith already match, so
        // it is a real enemy death nobody has paid yet.
        if (_identityAlive.TryGetValue(slot.Id, out bool wasAlive))
        {
            if (!wasAlive) { BlockRepeatCredit(s); return; }
        }
        else
        {
            if (!HandleOrphanAliveEdge(s, in slot, ref changed)) return;
        }

        ResolveCredit(s, in slot, ref changed);
    }

    private void BlockRepeatCredit(int s)
    {
        _deadCredited[s] = true;
        ModLogger.Warn(LogVerb.Kill, $"Blocked a repeat credit: an enemy whose identity was already resolved this battle died again at battle slot {s}.");
        _recorder?.Invoke("kill", $"no-credit slot={s} reason=identity-already-resolved");
    }

    /// <summary>Remember who killed this unit, on the first dead tick only. The turn order and the
    /// acting unit guess move on within ticks, so they are only trusted at the moment of death.</summary>
    private void StampDeadEdge(int s, in SlotSnapshot slot)
    {
        _deadStreak[s]++;
        if (_deadStreak[s] != 1) return;

        _victimProbe.CaptureDeadEdge(s, slot.Addr);   // debug probe only
        _victimAtEdge[s] = VictimReader.Read(_mem, slot.Addr);   // the victim CreditKill reports
        _oracle.MarkDead(slot.Id);   // anything that died was on the field
        bool aiTurn = TurnQueueShowsAiTurn();
        if (_untrackedArmedTicks > 0)
        {
            // An unarmed unit's summon or charged spell is landing, so nobody gets this kill,
            // whoever looks active. This check comes first so an armed ally acting between cast and
            // landing cannot take it. An armed unit's unrelated kill in that window is lost, which
            // beats paying the wrong weapon. The charge stays armed so every unit caught in the blast
            // is stamped, and its own timer clears it. A tracked charged attack still wins later in
            // ResolveCredit.
            _lethalUntracked[s] = UntrackedReason.ChargedAction;
        }
        else if (_lastPlayerWeapons.Count > 0)
        {
            // An enemy or guest turn means a counter or reaction struck, not the player who acted
            // last, so pay nobody. Missing a kill beats paying the wrong weapon.
            if (aiTurn)
                _lethalUntracked[s] = UntrackedReason.EnemyTurn;
            else
                // A player with a Living Weapon is acting. Keep that guess unless the game's attacker
                // pointer names someone else more recently (KillTracker.Stamp.cs).
                StampCulprit(s);
        }
        else if (_latchResolvedEmpty && _latched)
        {
            // The acting unit has no Living Weapon (a summoner, dancer or item user) and its turn is
            // still going. Without the _latched check an armed unit that struck before its own turn
            // was picked up would be buried here too, when it should wait and get paid.
            StampCulpritFromEmptyLatch(s);
        }
        else if (!aiTurn)
        {
            // Nobody picked up yet on a player turn. Try the other killer clues before the death
            // waits (KillTracker.Stamp.cs).
            StampCulpritFromHypothesisOnly(s);
        }
    }

    /// <summary>True only when the turn order clearly shows an enemy (1) or an ally guest (2) acting.
    /// Anything else, even an unreadable 0, counts as a player turn, so a bad read can never cost a
    /// player a real kill (docs/LIVE_LEDGER.md row 45).</summary>
    private bool TurnQueueShowsAiTurn()
    {
        // The team value can change a few ticks after a death, so only the first dead tick asks.
        int team = _mem.Readable(Offsets.TurnQueue + Offsets.TqTeam, 2) ? _mem.U16(Offsets.TurnQueue + Offsets.TqTeam) : 0;
        return team == 1 || team == 2;
    }

    /// <summary>Only real enemies pay. Anything else, like a fallen ally or guest, is settled unpaid
    /// and the caller stops.</summary>
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

    /// <summary>This enemy's alive record is missing because its max hp changed. Mend the record and
    /// settle from this slot's own death stamp. Returns true only when no killer was stamped, and
    /// then the caller must go on into ResolveCredit.</summary>
    private bool HandleOrphanAliveEdge(int s, in SlotSnapshot slot, ref bool changed)
    {
        // A landed charged attack is ignored here. It belongs to its real victim, and a lower slot
        // scanned first must not steal it. An unarmed killer verdict still holds.
        _recorder?.Invoke("kill", $"orphan-alive-edge slot={s} mhp={slot.MaxHp}");
        _identityAlive[slot.Id] = true;   // mended so a later revive and death behave
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
        return true;
    }

    /// <summary>The log line for a kill left unpaid because the killer had no Living Weapon. Both
    /// callers share it so the console and the flight tape always agree, and its wording is frozen
    /// (docs/LOGGING.md). Each caller settles the death itself, because the orphan path keeps the
    /// unit marked alive.</summary>
    private void LogUntrackedBury(int s)
    {
        var vs = _victimAtEdge[s];
        string fellPhrase = VictimClass.FellPhrase(vs.Has, vs.Job, vs.Undead);
        string fallenNoun = fellPhrase.StartsWith("an ") ? fellPhrase.Substring(3) : fellPhrase.Substring(2);
        ModLogger.Event(LogVerb.Kill, $"The fallen {fallenNoun} at battle slot {s} was slain by a player carrying no Living Weapon; the kill is deliberately left uncredited (actor resolved via {ResolveSourcePhrase(_lethalUntracked[s])}).");
        _recorder?.Invoke("kill", $"no-credit slot={s} reason=untracked-weapon via={_lethalUntracked[s]}");
    }

    /// <summary>Pay the kill, start waiting for a killer, or give up after waiting too long.</summary>
    private void ResolveCredit(int s, in SlotSnapshot slot, ref bool changed)
    {
        // A charged attack that just landed (a Jump or a spell) beats everything, even an unarmed
        // killer verdict, because its committer struck the blow whoever is acting now.
        //
        // Peek at the charge rather than claim it, since this tick may still pay nobody while the
        // restart latch holds the kill. It is claimed only on the tick that pays. This peek repeats
        // the check inside ConsumeDelayedCulprit (KillTracker.Delayed.cs), so changing one means
        // changing the other.
        bool delayedPeek = _delayedArmedTicks > 0 && _delayedActor != null && _delayedActor.Count > 0;
        List<int>? delayedCandidate = delayedPeek ? _delayedActor : null;
        if (delayedCandidate == null && _lethalUntracked[s] != UntrackedReason.None)
        {
            // The killer had no Living Weapon, so nobody is paid. A summoner, dancer or item user's
            // area kill is meant to go unpaid.
            _deadCredited[s] = true; _pending[s] = false; _identityAlive[slot.Id] = false;
            LogUntrackedBury(s);
            return;
        }
        var culprit = delayedCandidate ?? _lethalActor[s] ?? _lastPlayerWeapons;
        // While the restart latch is open this death may be fake. A unit that died before the
        // checkpoint and was raised after it drops dead again when a retry rewinds. Hold it as
        // waiting and settle it once the latch closes. A landed charge can time out meanwhile, the
        // gap told in the class summary.
        bool restartSuppressed = culprit.Count > 0 && _restart.LatchOpen;
        if (culprit.Count > 0 && !restartSuppressed)
        {
            List<int>? delayed = delayedCandidate != null ? ConsumeDelayedCulprit() : null;
            LogWhyNotLiveLatch(s, delayed, culprit);
            bool viaFallback = delayed == null && (_lethalActor[s] != null ? _lethalViaFallback[s] : _latchViaFallback);
            bool c = CreditKill(s, slot.Gx, slot.Gy, culprit, viaFallback, delayedOrCharged: delayed != null, identity: slot.Id);
            _deadCredited[s] = true;
            // Mark the unit dead on the record only if the kill paid. A refused payment (nobody on
            // the field holds that weapon) leaves it marked alive, so a later armed kill of the same
            // unit can still pay. The other unpaid paths settled the death for good.
            if (c) { _identityAlive[slot.Id] = false; changed = true; }
        }
        else if (!_pending[s])
        {
            StartPending(s, in slot);
        }
        else if (PendingTimedOut(s))
        {
            ExpirePendingUncredited(s, in slot);
        }
    }

    /// <summary>Give up once too many turns have ended since the death, or too many ticks passed.
    /// Every call adds a tick to the wait, so calling it twice in one tick doubles the wait. The tick
    /// count stops when the turn limit trips first, so the log can show fewer ticks than PendingTtl.</summary>
    private bool PendingTimedOut(int s) =>
        _actedFalls - _pendingFalls[s] >= ExpireFalls || ++_pendingAge[s] > PendingTtl;

    /// <summary>Explain the pick when the paid unit is not simply the one acting now. Says nothing
    /// for an ordinary payment.</summary>
    private void LogWhyNotLiveLatch(int s, List<int>? delayed, List<int> culprit)
    {
        if (delayed != null)
            ModLogger.EventWithTrace(LogVerb.Credit,
                $"Crediting the charged attack that just landed (a Jump or a spellcast) to the unit that committed it, wielding {string.Join(", ", delayed.ConvertAll(LogNames.Weapon))}, not the unit acting now.",
                $"delayed-credit detail (delayed=[{string.Join(",", delayed)}] stamped=[{string.Join(",", _lethalActor[s] as IEnumerable<int> ?? Array.Empty<int>())}] current=[{string.Join(",", _lastPlayerWeapons)}], battle slot {s})");
        else if (_lethalActor[s] != null && !ActorResolver.SameSet(_lethalActor[s]!, _lastPlayerWeapons))
            ModLogger.EventWithTrace(LogVerb.Credit,
                $"Crediting the unit that landed the finishing blow, wielding {string.Join(", ", culprit.ConvertAll(LogNames.Weapon))}, rather than the unit that acted most recently.",
                $"finishing-blow detail (lethal=[{string.Join(",", culprit)}] live-latch=[{string.Join(",", _lastPlayerWeapons)}], battle slot {s}, dead-streak {_deadStreak[s]})");
    }

    /// <summary>No killer known yet, or the restart latch is holding the kill. Starts the tick count
    /// and the turn count that decide when to give up.</summary>
    private void StartPending(int s, in SlotSnapshot slot)
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

    /// <summary>Give up on a waiting death and mark the unit dead on the record, so it cannot pay
    /// again this battle unless it is seen alive first.</summary>
    private void ExpirePendingUncredited(int s, in SlotSnapshot slot)
    {
        _deadCredited[s] = true; _pending[s] = false;
        _lethalActor[s] = null; _lethalUntracked[s] = UntrackedReason.None;
        _victimAtEdge[s] = default;   // this unpaid path never reaches CreditKill to use it up
        _identityAlive[slot.Id] = false;
        _klog.Warn($"The killer of the enemy at battle slot {s} could not be determined; the kill goes uncredited.");
        ModLogger.Debug(LogVerb.Trace, $"kill expiry detail (waited {_pendingAge[s]} ticks, {_actedFalls - _pendingFalls[s]} turn edges, battle slot {s})");
        _recorder?.Invoke("kill", $"no-credit slot={s} reason=expired-unresolved waited={_pendingAge[s]}ticks");
    }
}
