using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Why a dead enemy's kill went to nobody. The killer was a player holding no Living Weapon, and
/// each value says which clue told us who the killer was, so the console line can name it.
/// None is zero so wiping the array resets every slot to "no verdict".
/// </summary>
internal enum UntrackedReason : byte
{
    None = 0,
    ChargedAction,   // a summon or other slow spell from an unarmed player landed after its caster's turn
    EnemyTurn,       // the death happened on an enemy or guest turn, so the last player to act did not do it
    ActedLatch,      // the unit that just acted was a player with no Living Weapon
    ActorRegister,   // the game's own "who is attacking" record named an unarmed player
    TurnFlags,       // the per unit "has moved, has acted" flags pointed at an unarmed player
}

/// <summary>
/// Watches every unit on the battlefield each tick and decides, when an enemy dies, which weapon
/// earns the kill. Who acted last is tracked in KillTracker.cs and which units are enemies in
/// <see cref="EnemyOracle"/>.
///
/// A unit counts as dead when its hp is 0 or its Dead status bit is on. Some deaths set the bit
/// while hp stays above zero, such as a Phoenix Down thrown at an undead enemy.
///
/// A unit is known by its level, brave, faith and max hp. Each one may pay out a kill only after
/// we have seen it alive, and only once per death. The game sometimes keeps a frozen copy of a
/// corpse in a second seat. That copy is never seen alive, so it can never pay out twice.
/// Alive or dead must hold for three ticks in a row before we believe it, which filters out the
/// junk frames the game shows while loading. A tick is about 33ms.
///
/// A known gap. A Jump or charged spell is remembered for only about 12 ticks after it lands,
/// while the "battle just restarted" guard stays up for 90. If both are up at once the charged
/// kill can go to nobody. That is a lost kill, never a kill given to the wrong weapon.
/// </summary>
internal sealed partial class KillTracker
{
    private const int AliveNeeded = 3;
    private const int DeadNeeded = 3;
    private const int FieldEvidenceNeeded = 3;

    // Each array below has one entry per battle seat, the game's list of units on the field.
    private readonly bool[] _seenAlive = new bool[Offsets.BandSlots];
    // True once this death is settled, whether a weapon got the kill or nobody did.
    private readonly bool[] _deadCredited = new bool[Offsets.BandSlots];
    private readonly int[] _aliveStreak = new int[Offsets.BandSlots];
    private readonly int[] _deadStreak = new int[Offsets.BandSlots];
    // How long this seat has looked like a real unit taking turns. The enemy list also holds fake
    // "maybe spawns" that look real in every other way, and taking turns is what gives them away.
    private readonly int[] _fieldEvidenceStreak = new int[Offsets.BandSlots];
    // Level, brave and faith when first seen alive. If they change, someone new has the seat.
    private readonly (byte lvl, byte br, byte fa)[] _slotId = new (byte, byte, byte)[Offsets.BandSlots];
    // Who the victim was, photographed the moment it died, so the kill message can name it.
    // Every path that ends a death without a credit must wipe it, or the next death reuses it.
    internal readonly VictimSnapshot[] _victimAtEdge = new VictimSnapshot[Offsets.BandSlots];
    // The killer's weapons, copied the moment the victim dropped. A copy, so a unit that acts a
    // moment later cannot steal the kill. Null means nobody has been blamed yet.
    private readonly List<int>?[] _lethalActor = new List<int>?[Offsets.BandSlots];
    // True when the killer was a best guess from the turn order and not a direct reading. Only
    // feeds an end of battle count of how often we had to guess.
    internal readonly bool[] _lethalViaFallback = new bool[Offsets.BandSlots];
    // Set when the killer was a player with no Living Weapon, so the kill goes to nobody. A death
    // in the quiet gap after an unarmed player's turn ends is left unmarked, because the killer
    // is unknown there, so it waits for one instead of being thrown away.
    private readonly UntrackedReason[] _lethalUntracked = new UntrackedReason[Offsets.BandSlots];

    // Hp on the last tick this seat looked dead. A retry rewinds a dead unit back to life, and the
    // jump from 0 hp to positive hp is how RestartSentinel spots it. Writing it on alive ticks too
    // would replace the 0 during the three tick alive wait, before anyone checks it.
    // A status death keeps hp above 0, so it never looks like a heal from zero.
    // Evidence in docs/LIVE_LEDGER.md row [battle-retry-rewind-fingerprint].
    private readonly ushort[] _prevHp = new ushort[Offsets.BandSlots];
    // The weapons that got this seat's kill. If a retry brings the victim back, RestartSentinel
    // takes the kill back from exactly these. Null means no kill was paid out.
    private readonly List<int>?[] _creditedWeapons = new List<int>?[Offsets.BandSlots];
    // Whether that kill was a guess, so taking it back also fixes the guess count.
    private readonly bool[] _creditedViaFallback = new bool[Offsets.BandSlots];
    // Who the victim was when paid out. A unit coming back to life is checked against this and
    // the name id below. A match proves a retry rewound this very enemy, which lets the kill be
    // taken back even in the first few seconds of a battle, when it otherwise would not be.
    private readonly (byte lvl, byte br, byte fa, ushort mhp)[] _creditedIdentity = new (byte, byte, byte, ushort)[Offsets.BandSlots];

    // The victim's name id, the second half of that check. Two different units can share level,
    // brave, faith and max hp, so the stats alone are not enough (docs/LIVE_LEDGER.md row
    // [party-nameid-unique-key]). Wrongly taking back a kill destroys real progress, while missing
    // one only leaves a bonus kill, so when unsure the check says "not the same". 0 means unknown.
    //
    // Unproven. Generic enemies get fresh name ids on each load. If a retry does that too, an
    // enemy kill undone by an early retry still counts (LIVE_LEDGER row [retry-preserves-credited-identity]).
    private readonly ushort[] _creditedNameId = new ushort[Offsets.BandSlots];

    // True means this unit was last seen alive and may pay out. False means it already died once
    // and paid out or was given up on. Shared across seats so a corpse copy cannot pay twice.
    private readonly Dictionary<(byte lvl, byte br, byte fa, ushort mhp), bool> _identityAlive = new();

    internal static string ResolveSourcePhrase(UntrackedReason reason) => reason switch
    {
        UntrackedReason.ChargedAction => "a charged-action landing",
        UntrackedReason.EnemyTurn => "an enemy-turn team read",
        UntrackedReason.ActedLatch => "the acted-period latch",
        UntrackedReason.ActorRegister => "the actor register",
        UntrackedReason.TurnFlags => "the turn flags",
        _ => "an unknown resolve path",
    };

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
        _oracle.ResetBattle();
        _victimProbe.ResetBattle();
    }

    /// <summary>Everything we need from one seat, read once per tick so every check below sees
    /// the same moment.</summary>
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
        // An unreadable page reads as 0, which already means "unknown", so no extra check.
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

    /// <summary>Returns true if any weapon's kill count changed.</summary>
    private bool ScanCorpses(bool onField)
    {
        bool changed = false;
        if (onField) _oracle.TickField();

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
                // Mapped while studying Doom. An unreadable byte reads as 0, so a bad read never invents a death.
                (_mem.U8(addr + Offsets.ADeadStatus) & Offsets.ADeadBit) != 0,
                _mem.U16(addr + Offsets.ANameId));

            if (onField) ScanFieldEvidence(s, in slot);

            if (!slot.IsDead) { ScanAlivePath(s, onField, in slot, ref changed); continue; }

            // Saved before the dead path so its early exits cannot skip it.
            _prevHp[s] = slot.Hp;
            ScanDeadPath(s, onField, in slot, ref changed);
        }
        return changed;
    }

    /// <summary>Marks a seat as a real fighter once it has taken part in the turn order for a
    /// few ticks in a row.</summary>
    private void ScanFieldEvidence(int s, in SlotSnapshot slot)
    {
        _events?.Observe(s, slot.Hp, slot.MaxHp, slot.Gx, slot.Gy, _actorTag);

        // A unit is in the turn order when its turn gauge byte is nonzero or its turn flag reads
        // exactly 1. The flag holds other values too, so 1 is not the same as "nonzero".
        // A frozen copy parked at tile (0,0) keeps stale values, so that tile is ignored.
        // Fake spawns have normal stats, full hp and real tiles, so this is the only tell.
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

    private void ScanAlivePath(int s, bool onField, in SlotSnapshot slot, ref bool changed)
    {
        _deadStreak[s] = 0;
        if (!onField) return;
        if (_seenAlive[s] && (_slotId[s].lvl != slot.Lvl || _slotId[s].br != slot.Br || _slotId[s].fa != slot.Fa))
        {
            // A different unit now sits in this seat, or the same one with changed stats. A retry
            // can do the second when it undoes a brave or faith hit while bringing the corpse back.
            // Give RestartSentinel a chance to take back the old kill before the wipe below.
            //
            // A known gap. The stats differ here by definition, so the "same enemy" check always
            // fails. A retry restarts the battle clock, and in its first five seconds a take back
            // needs that check. So a victim whose brave or faith was changed before it died still
            // counts twice after a retry.
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
            _creditedWeapons[s] = null; _creditedViaFallback[s] = false;
            _creditedIdentity[s] = default;
            _creditedNameId[s] = 0;
            _victimProbe.Reset(s);
            _victimAtEdge[s] = default;
            return;
        }
        _aliveStreak[s]++;
        if (_aliveStreak[s] >= AliveNeeded && !_seenAlive[s])
        {
            _seenAlive[s] = true;
            _slotId[s] = (slot.Lvl, slot.Br, slot.Fa);
            _identityAlive[slot.Id] = true;
        }
        if (_seenAlive[s])
        {
            if (_deadCredited[s])
            {
                // Back from the dead, so this unit may pay out again.
                _identityAlive[slot.Id] = true;
                // If a retry is what brought it back, the kill never really happened. Ask
                // RestartSentinel and take the kill back before the wipe below.
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
            // A living unit carries no death record. Leftovers would blame the wrong weapon later.
            _deadCredited[s] = false;
            _pending[s] = false;
            _lethalActor[s] = null; _lethalUntracked[s] = UntrackedReason.None;
            _creditedWeapons[s] = null; _creditedViaFallback[s] = false;
            _creditedIdentity[s] = default;
            _creditedNameId[s] = 0;
            _victimProbe.Reset(s);
            _victimAtEdge[s] = default;
        }
        _victimProbe.CaptureAlive(s, slot.Addr);   // the probe only feeds the log, nothing decides on it
    }

    private void ScanDeadPath(int s, bool onField, in SlotSnapshot slot, ref bool changed)
    {
        if (_deadCredited[s]) return;
        if (!_seenAlive[s]) return;
        if (_slotId[s].lvl != slot.Lvl || _slotId[s].br != slot.Br || _slotId[s].fa != slot.Fa) return;
        if (!onField) return;

        StampDeadEdge(s, in slot);

        if (_deadStreak[s] < DeadNeeded) return;

        if (!OracleGate(s, in slot)) return;

        // Missing from _identityAlive means the unit was seen alive under a different max hp, so
        // this is still a real first death. Present as false means it already died and was
        // settled this battle, or this is a frozen copy of a corpse, so stop.
        if (_identityAlive.TryGetValue(slot.Id, out bool wasAlive))
        {
            if (!wasAlive)
            {
                _deadCredited[s] = true;
                ModLogger.Warn(LogVerb.Kill, $"Blocked a repeat credit: an enemy whose identity was already resolved this battle died again at battle slot {s}.");
                _recorder?.Invoke("kill", $"no-credit slot={s} reason=identity-already-resolved");
                return;
            }
        }
        else
        {
            if (!HandleOrphanAliveEdge(s, in slot, ref changed)) return;
        }

        ResolveCredit(s, in slot, ref changed);
    }

    /// <summary>Runs every dead tick, but the real work happens only on the first one, the
    /// moment of death, while the clues about who did it are still fresh.</summary>
    private void StampDeadEdge(int s, in SlotSnapshot slot)
    {
        _deadStreak[s]++;
        if (_deadStreak[s] == 1)
        {
            _victimProbe.CaptureDeadEdge(s, slot.Addr);
            _victimAtEdge[s] = VictimReader.Read(_mem, slot.Addr);
            _oracle.MarkDead(slot.Id);   // only a unit that was really in the fight can leave a corpse
            // Whose turn it is can change a few ticks after the death, so read it now. Only a
            // clear 1 (enemy) or 2 (guest) counts. Anything else, junk included, is treated as a
            // player turn, so a bad read can never cost a player a kill (LIVE_LEDGER row 45).
            int team = _mem.Readable(Offsets.TurnQueue + Offsets.TqTeam, 2) ? _mem.U16(Offsets.TurnQueue + Offsets.TqTeam) : 0;
            bool nonPlayerTurn = team == 1 || team == 2;
            if (_untrackedArmedTicks > 0)
            {
                // An unarmed player's summon is landing right now. Blame it before asking who is
                // acting, since another player may have taken a turn between cast and landing.
                // Every victim of the blast gets this mark until the window closes. An armed
                // player's kill that sneaks into the same window is lost, never misgiven.
                _lethalUntracked[s] = UntrackedReason.ChargedAction;
            }
            else if (_lastPlayerWeapons.Count > 0)
            {
                if (nonPlayerTurn)
                    _lethalUntracked[s] = UntrackedReason.EnemyTurn;   // a counterattack on an enemy turn, not the last player's swing
                else
                    // An armed player is acting. Keep that player unless the game's own attacker
                    // record names a more recent, different killer.
                    StampCulprit(s);
            }
            else if (_latchResolvedEmpty && _latched)
            {
                // An unarmed player (summoner, dancer, item user) is mid turn. The _latched check
                // stops an armed player who struck before their own turn registered from being
                // thrown away along with them.
                StampCulpritFromEmptyLatch(s);
            }
            else if (!nonPlayerTurn)
            {
                // Nobody is latched on a player turn. Try the best guess from the turn clues.
                StampCulpritFromHypothesisOnly(s);
            }
        }
    }

    /// <summary>Only enemies pay out. Players and guests dying stop here.</summary>
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

    /// <summary>A dead enemy we never wrote down as alive under these exact stats. Returns false
    /// when it was settled here, true when nobody has been blamed yet and normal handling must
    /// take over.</summary>
    private bool HandleOrphanAliveEdge(int s, in SlotSnapshot slot, ref bool changed)
    {
        // Only this seat's own death record is used. A charged attack waiting to land belongs to
        // some other victim, and this seat may be checked first just because of seat order.
        _recorder?.Invoke("kill", $"orphan-alive-edge slot={s} mhp={slot.MaxHp}");
        _identityAlive[slot.Id] = true;   // write it down now so a later revive and death work normally
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

    /// <summary>One message for "an unarmed player made this kill", shared by both callers so the
    /// console and the flight log always say the same thing. The wording is frozen in
    /// docs/LOGGING.md. The caller sets the verdict, since the orphan path keeps the unit
    /// marked alive.</summary>
    private void LogUntrackedBury(int s)
    {
        var vs = _victimAtEdge[s];
        string fellPhrase = VictimClass.FellPhrase(vs.Has, vs.Job, vs.Undead);
        string fallenNoun = fellPhrase.StartsWith("an ") ? fellPhrase.Substring(3) : fellPhrase.Substring(2);
        ModLogger.Event(LogVerb.Kill, $"The fallen {fallenNoun} at battle slot {s} was slain by a player carrying no Living Weapon; the kill is deliberately left uncredited (actor resolved via {ResolveSourcePhrase(_lethalUntracked[s])}).");
        _recorder?.Invoke("kill", $"no-credit slot={s} reason=untracked-weapon via={_lethalUntracked[s]}");
    }

    /// <summary>Pays the kill out, waits for a killer to show up, or gives up.</summary>
    private void ResolveCredit(int s, in SlotSnapshot slot, ref bool changed)
    {
        // A Jump or charged spell that just landed beats everyone, even an unarmed player who
        // happens to be acting, because the unit that started it is the real killer.
        //
        // Only look at it here. Using it up now would waste it on a tick that ends up paying
        // nobody, such as while a restart is being checked below.
        bool delayedPeek = _delayedArmedTicks > 0 && _delayedActor != null && _delayedActor.Count > 0;
        List<int>? delayedCandidate = delayedPeek ? _delayedActor : null;
        if (delayedCandidate == null && _lethalUntracked[s] != UntrackedReason.None)
        {
            _deadCredited[s] = true; _pending[s] = false; _identityAlive[slot.Id] = false;
            LogUntrackedBury(s);
            return;
        }
        var culprit = delayedCandidate ?? _lethalActor[s] ?? _lastPlayerWeapons;
        // Right after a retry, an enemy that was raised and then rewound can drop dead again
        // without anyone hitting it. So while the restart guard is up, wait instead of paying.
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
            // A refused payout (the killer no longer holds a Living Weapon) leaves the unit open,
            // so a later armed kill of a matching enemy can still count.
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
        // Give up after two more turns end with no killer, or about 30 seconds as a backstop.
        else if (_actedFalls - _pendingFalls[s] >= ExpireFalls || ++_pendingAge[s] > PendingTtl)
        {
            _deadCredited[s] = true; _pending[s] = false;
            _lethalActor[s] = null; _lethalUntracked[s] = UntrackedReason.None;
            _victimAtEdge[s] = default;
            _identityAlive[slot.Id] = false;
            _klog.Warn($"The killer of the enemy at battle slot {s} could not be determined; the kill goes uncredited.");
            ModLogger.Debug(LogVerb.Trace, $"kill expiry detail (waited {_pendingAge[s]} ticks, {_actedFalls - _pendingFalls[s]} turn edges, battle slot {s})");
            _recorder?.Invoke("kill", $"no-credit slot={s} reason=expired-unresolved waited={_pendingAge[s]}ticks");
        }
    }
}
