using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Counts kills and credits each to the acting player's weapon(s). Ported from the FFTHandsFree
/// active-unit resolver. Death is state-based (a corpse is credited once, re-armed when the slot is
/// seen alive); a corpse seen before its killer's `acted` flag latches is held pending. Only enemy-side
/// band slots (inb==1, identity captured from the static array) can earn credit. Weapons come from
/// ActorResolver (HP+MaxHP+level -> band entry -> level/brave/faith fingerprint -> roster hands); a
/// dual-wielder credits both hands, a shield hand never. Corpse scan: KillTracker.Corpses.cs.
/// Enemy-side identity set: <see cref="EnemyOracle"/>.
/// </summary>
internal sealed partial class KillTracker
{
    internal const int PendingTtl = 900; // ~30s backstop at the 33ms tick; the real expiry is two acted-falling edges (a killer latches
                                         //   before its own fall). This only catches a frozen scene with no turn edges.
    internal const int UnfreezeTicks = 3; // acted must read 0 this long before the period ends; it drifts to 0 after a confirmed action
    internal const int ExpireFalls = 2;  // pending corpse expires after this many acted-falling edges uncredited
    internal const int FallbackStreak = 3; // consecutive identical non-empty resolves before the no-actor

    private readonly Dictionary<int, int> _kills;            // weapon id -> kill count
    internal readonly IGameMemory _mem;
    private readonly ActorResolver _resolver;
    private readonly ActorRegister _register;                // engine actor-pointer ownership tracker (register-first resolve)
    private readonly EnemyOracle _oracle;                    // which identities are enemy-side (creditable)
    internal readonly bool[] _pending = new bool[Offsets.BandSlots];   // corpse seen, awaiting an actor latch
    internal readonly int[] _pendingAge = new int[Offsets.BandSlots];  // ticks a corpse has waited (backstop)
    internal readonly int[] _pendingFalls = new int[Offsets.BandSlots];// _actedFalls when the corpse went pending
    // Register-tick birth stamp of a pending corpse (0 = not pending or not stamped yet). Same clock as
    // ActorRegister.Tick/ArrivalTick; _pendingAge stalls on off-field Polls. Stamped in UpdateCorpseAnchor.
    internal readonly long[] _pendingBirthTick = new long[Offsets.BandSlots];
    internal List<int> _lastPlayerWeapons = new();   // the acting player's weapon(s); a dual-wielder latches both
    internal int _lastPlayerMainHand;                // RRHand id of the last latched actor (0 when none)
    internal (int lvl, int br, int fa) _lastActorFp; // fingerprint of the unit latched this acted-period
    // LW-252 stage 3: the latched actor's frame nameId, mirrored beside _lastActorFp through
    // Seed/ApplyLatchOutputs (see ActedPeriodOutputs.LastActorNameId). 0 = no resolved identity.
    internal int _lastActorNameId;
    // Register tick of the period's last successful latch resolve (stamped at both latch-confirm sites).
    // KillerStamp ignores register arrivals at or before it. See KillerStamp.cs.
    internal int _lastResolveTick;
    private bool _latched;                           // a player resolved this acted-period -> frozen until it ends
    private bool _periodOpen;                        // set once per rise, cleared on the debounced fall; stays false through failed resolves
    internal bool _latchResolvedEmpty;              // last successful resolve gave an empty weapon set (summoner/dancer/item-user);
                                                    //   gates the FirstKillFallback bail and the untracked corpse stamp. Sticky until the next success.
    internal int _actedFalls;                        // battle-local count of debounced acted-falling edges
    private string _actorTag = "";                   // cached "10,52" form of the latch, for event lines
    internal readonly BattleLog? _events;            // dev event timeline (damage/heal/move); null = off
    // Flight recorder tap (null = off); also passed to the ActorRegister built below.
    private readonly Action<string, string>? _recorder;
    // Reliquary P1 probe (docs/RELIQUARY_AC.md): log-only victim-identity capture. Internal so tests can read it.
    internal readonly VictimProbe _victimProbe;
    // Reliquary P2 probe (docs/RELIQUARY_AC.md): once-per-battle identity census at EnemyOracle's coverage-complete edge.
    internal readonly BattleCensus _census;
    // Reliquary Phase 1 (docs/RELIQUARY_AC.md): optional sink CreditKill reports each credited victim (or a miss) to.
    private readonly IDeedSink? _deeds;

    // Logging facelift: sticky per-battle latch, set when any resolve latches a tracked weapon, cleared in
    // ResetBattle. Gates console output of the per-corpse pending/expiry and EnemyOracle coverage lines;
    // the file still gets everything.
    internal bool AnyTrackedWeaponThisBattle { get; private set; }
    // Scoped [kill] logger gated on that latch.
    private readonly ScopedLogger _klog;

    // Battle-exit match-report counters (logging facelift stage 3): kills credited per weapon this battle,
    // compared against the lifetime tally for tier crossings. Cleared in ResetBattle.
    internal readonly Dictionary<int, int> BattleCredits = new();
    // Credits this battle whose latch came from the turn-queue fallback (battle-end summary). Cleared in ResetBattle.
    internal int FallbackCredits { get; private set; }
    // Latest latch resolve came from the tq or first-kill fallback; read by CreditKill's viaFallback sites.
    internal bool _latchViaFallback;
    // LW-56: credit-time live-wielder gate (CreditGate.Decide). Null = gate off, every culprit survives.
    // Engine.cs wires Wielder.HasLiveWielder.
    private readonly Func<int, bool>? _hasLiveWielder;
    // LW-150 S3: acted-period latch state machine (ActedPeriodLatch.cs). It never touches this class's
    // fields; _latchIO is seeded from them before Step/Reset and copied back after. Reused to avoid a
    // per-tick allocation.
    private readonly ActedPeriodLatch _latch;
    private readonly ActedPeriodOutputs _latchIO = new();

    // LW-233: checkpoint-retry detector (docs/LIVE_LEDGER.md row [battle-retry-rewind-fingerprint]), ticked
    // per Poll; feeds revive detection in KillTracker.Corpses.cs and ResolveCredit. See RestartSentinel.cs.
    private readonly RestartSentinel _restart;

    public KillTracker(Dictionary<int, int> kills, IGameMemory mem, ISet<int> weapons, BattleLog? events = null,
                        Action<string, string>? recorder = null, IDeedSink? deeds = null,
                        Func<int, bool>? hasLiveWielder = null)
    {
        _kills = kills;
        _mem = mem;
        _recorder = recorder;
        // LW-56 A1: built before ActorRegister; the exclusion lambda below reads _oracle at call time.
        _oracle = new EnemyOracle(mem, () => AnyTrackedWeaponThisBattle);
        _register = new ActorRegister(mem, recorder,
            id => _oracle.Contains(((byte)id.lvl, (byte)id.br, (byte)id.fa, (ushort)id.maxHp)));
        _resolver = new ActorResolver(mem, weapons, _register);
        // LW-63 D4: the flags-first hypothesis lane, consulted ahead of the register snapshot.
        _killerStamp = new KillerStamp(_register, _resolver.HandsFromRoster, _resolver.TryResolveFlagKiller);
        _latch = new ActedPeriodLatch(mem, _resolver, _register, recorder);
        _events = events;
        _victimProbe = new VictimProbe(mem, recorder);
        _census = new BattleCensus(mem, recorder);
        _deeds = deeds;
        _hasLiveWielder = hasLiveWielder;
        _klog = ModLogger.For(LogVerb.Kill, () => AnyTrackedWeaponThisBattle);
        _restart = new RestartSentinel(recorder);
    }

    /// <summary>LW-233: the retry-rewind detector, read-only for callers.</summary>
    internal RestartSentinel Restart => _restart;

    /// <summary>The actor-pointer ownership register (TurnOwnerSpike under LWDEV; AttackCard stopped using
    /// it with the 2026-07-06 cursor-only fix). Same instance KillerStamp uses; callers must not mutate it.</summary>
    internal ActorRegister Register => _register;

    /// <summary>LW-56 D11/A3: wraps <see cref="BattleCensus.EmitExit"/>; Engine calls it on the battle-exit
    /// edge, before the flight flush.</summary>
    internal void EmitExitCensus() => _census.EmitExit();

    /// <summary>LW-31 stage 2/3, LW-55, LW-87: ActorResolver.TryResolveCursorPlayer for AttackCard.Resolve.cs
    /// (its only resolve seam since the 2026-07-06 cursor-only fix). Null Answer = no answer, with Miss naming
    /// the refusing stage; otherwise CursorGate.Decide judges the raw <see cref="CursorAnswer"/> facts
    /// (<see cref="CursorMiss.None"/> on success).</summary>
    internal Func<(CursorAnswer? Answer, CursorMiss Miss)> ResolveCursorPlayer =>
        () => _resolver.TryResolveCursorPlayer(out var answer, out var miss) ? (answer, miss) : ((CursorAnswer?)null, miss);

    /// <summary>ActorResolver.SpriteOf for AttackCard's row-rename resolve (LW-31 stage 3): the roster slot's
    /// SpriteSet byte.</summary>
    internal Func<long, byte> SpriteOf => _resolver.SpriteOf;

    /// <summary>Reset per-battle state on battle enter and exit. A pre-existing corpse never seen alive stays ineligible.</summary>
    public void ResetBattle()
    {
        Array.Clear(_pending, 0, _pending.Length);
        Array.Clear(_pendingAge, 0, _pendingAge.Length);
        Array.Clear(_pendingFalls, 0, _pendingFalls.Length);
        Array.Clear(_pendingBirthTick, 0, _pendingBirthTick.Length);
        // LW-150 S3: latch Reset plus battle-start defaults for the mirrored fields (see ActedPeriodLatch.Reset).
        _latch.Reset(_latchIO);
        ApplyLatchOutputs(_latchIO);
        _register.ResetBattle();
        _resolver.EndActedPeriod();
        _events?.ResetBattle();
        BattleCredits.Clear();                // per-battle match-report counters
        FallbackCredits = 0;
        ResetBattleCorpses();   // clear per-battle band-scan state (Corpses.cs)
        ResetDelayed();         // clear delayed-action snapshot/arm state (Delayed.cs)
        _census.ResetBattle();  // re-arm the P2 identity-census probe (BattleCensus.cs)
        _restart.ResetBattle(); // LW-233: forget any latch/null history from the prior battle
    }

    /// <summary>LW-150 S3: copy this class's acted-period fields into <see cref="_latchIO"/> before
    /// <see cref="ActedPeriodLatch.Step"/>.</summary>
    private void SeedLatchOutputs(ActedPeriodOutputs o)
    {
        o.PeriodOpen = _periodOpen;
        o.Latched = _latched;
        o.LastPlayerWeapons = _lastPlayerWeapons;
        o.LastPlayerMainHand = _lastPlayerMainHand;
        o.LastActorFp = _lastActorFp;
        o.LastActorNameId = _lastActorNameId;   // LW-252 stage 3: seeds beside LastActorFp
        o.LatchResolvedEmpty = _latchResolvedEmpty;
        o.LatchViaFallback = _latchViaFallback;
        o.LastResolveTick = _lastResolveTick;
        o.ActorTag = _actorTag;
        o.ActedFalls = _actedFalls;
        o.AnyTrackedWeaponThisBattle = AnyTrackedWeaponThisBattle;
    }

    /// <summary>LW-150 S3: copy <see cref="_latchIO"/> back onto this class's fields after
    /// <see cref="ActedPeriodLatch.Step"/> (Poll) and <see cref="ActedPeriodLatch.Reset"/> (ResetBattle).
    /// Assignment order is unobservable.</summary>
    private void ApplyLatchOutputs(ActedPeriodOutputs o)
    {
        _periodOpen = o.PeriodOpen;
        _latched = o.Latched;
        _lastPlayerWeapons = o.LastPlayerWeapons;
        _lastPlayerMainHand = o.LastPlayerMainHand;
        _lastActorFp = o.LastActorFp;
        _lastActorNameId = o.LastActorNameId;   // LW-252 stage 3: applies beside LastActorFp
        _latchResolvedEmpty = o.LatchResolvedEmpty;
        _latchViaFallback = o.LatchViaFallback;
        _lastResolveTick = o.LastResolveTick;
        _actorTag = o.ActorTag;
        _actedFalls = o.ActedFalls;
        AnyTrackedWeaponThisBattle = o.AnyTrackedWeaponThisBattle;
    }

    /// <summary>One in-battle tick. Off-field ticks (load screens, menu flickers) do not advance alive/dead
    /// counters. Returns true if the tally changed.</summary>
    public bool Poll(bool onField)
    {
        bool changed;

        _register.Update();       // one actor-pointer read per tick
        // LW-233: tick the retry detector before this tick's corpse scan so a null seen now can join a revive
        // detected later in the same Poll. Its return drains the deferred-verdict stash (0ms-join fix): apply
        // those uncredits before anything reads the tally.
        var restartDrained = _restart.Tick(_register.RawNullThisTick, onField);
        bool drainedChanged = restartDrained.Count > 0;
        foreach (var (drainSlot, drainWeapons, drainViaFallback) in restartDrained)
            UncreditKills(drainWeapons, drainViaFallback, drainSlot);
        UpdateCorpseAnchor();     // V1 corpse-anchor veto for this tick's resolves

        // LW-150 S3: acted-period latch plus first-kill fallback (ActedPeriodLatch.cs), via Seed/Step/Apply.
        // AnyPending() is passed in because it is the one input that is not a mirrored field.
        SeedLatchOutputs(_latchIO);
        _latch.Step(_latchIO, AnyPending());
        ApplyLatchOutputs(_latchIO);

        TrackDelayed(onField);   // snapshot/arm the committer of a delayed action (Delayed.cs)

        changed = ScanCorpses(onField) || drainedChanged;   // band corpse scan + identity capture (Corpses.cs)

        _census.Tick(_oracle.CoverageDone);   // P2 probe: fires once, right after the oracle's own tick

        return changed;
    }

    /// <summary>The (level,brave,faith) of the unit latched this acted-period, or default when
    /// none/ambiguous. Consumers must not cache across ticks.</summary>
    public (int lvl, int br, int fa) LastActorFingerprint => _lastActorFp;

    /// <summary>LW-252 stage 3: the acting unit's frame nameId, latched with <see cref="LastActorFingerprint"/>.
    /// 0 = no resolved identity (never latched, ambiguous/enemy period, or first-kill-fallback latch; see
    /// <see cref="ActedPeriodLatch.Step"/>). Do not cache across ticks.</summary>
    public int LastActorNameId => _lastActorNameId;

    /// <summary>The acting player's weapon id(s) from the latest latched actor; empty at battle start or when
    /// no player actor resolved. Do not cache across ticks (ResetBattle replaces the list).</summary>
    public List<int> LastPlayerWeapons => _lastPlayerWeapons;

    /// <summary>The RRHand weapon id of the latest latched actor, or 0. A Living Weapon earns kills in any
    /// hand but commands its gift only from the main hand; signatures gate on this.</summary>
    public int LastPlayerMainHand => _lastPlayerMainHand;

    internal bool AnyPending()
    {
        for (int s = 0; s < _pending.Length; s++) if (_pending[s]) return true;
        return false;
    }

    /// <summary>V1 corpse-anchor veto: refuses the register path for this tick's resolves when any pending
    /// corpse's birth tick is at or after the current owner's arrival, since a killer's turn must contain
    /// the death. Compares register ticks (<see cref="_pendingBirthTick"/> vs <see cref="ActorRegister.ArrivalTick"/>)
    /// because _pendingAge stalls on off-field Polls (Corpses.cs bails before it) while the register clock does not.
    /// The birth tick is stamped here, not in Corpses.cs: Poll order is Update, UpdateCorpseAnchor, ScanCorpses,
    /// so the earliest this sees _pending[s] is one tick after it was set, hence `_register.Tick - 1`.</summary>
    private void UpdateCorpseAnchor()
    {
        bool ok = true;
        for (int s = 0; s < _pending.Length; s++)
        {
            if (_pending[s])
            {
                if (_pendingBirthTick[s] == 0) _pendingBirthTick[s] = _register.Tick - 1;
                if (_pendingBirthTick[s] != 0 && _pendingBirthTick[s] <= _register.ArrivalTick) ok = false;
            }
            else
            {
                _pendingBirthTick[s] = 0;
            }
        }
        _resolver.SetCorpseAnchorOk(ok);
    }

    /// <summary>Credit the weapon set for a kill at band slot s (position gx,gy). LW-56: with a live-wielder
    /// gate wired, culprits are first split by <see cref="CreditGate.Decide"/>; refused ids (a stale latch
    /// naming a weapon nobody wields, e.g. after a mis-timed New Game) get a flight no-credit record and one
    /// log line, and everything below runs on survivors only.
    /// Reliquary Phase 1: reports the slot's victim snapshot (_victimAtEdge, Corpses.cs) to the deed sink once,
    /// then consumes it; no snapshot (docs/RELIQUARY_AC.md missing-snapshot case) still counts the kill, with a
    /// DeedMiss. Does not mutate <paramref name="weapons"/> (may alias _lastPlayerWeapons).
    /// <paramref name="viaFallback"/>: the latch came from the turn-queue fallback (battle-end summary counter).
    /// <paramref name="delayedOrCharged"/> (LW-167): the credit came from the delayed-actor path (Delayed.cs
    /// ConsumeDelayedCulprit), passed on to RecordPoachDeed.
    /// <paramref name="identity"/> (LW-233 live-drill residual, 2026-08-17): the (lvl,br,fa,maxHp) the kill was
    /// credited under, stamped into _creditedIdentity for RestartSentinel's grace exemption. The zero-tuple
    /// default is safe (FINDING 4, 2026-08-17): <see cref="Band.IsValid"/> (Battle/Band.cs:54-67) floors lvl,
    /// brave, faith and maxHp at 1, so no real slot.Id is zero. The victim nameId is stamped from the same
    /// snapshot (_creditedNameId, Corpses.cs); 0 is the "no resolved identity" sentinel, so both defaults
    /// fail closed.</summary>
    internal bool CreditKill(int s, int gx, int gy, List<int> weapons, bool viaFallback = false, bool delayedOrCharged = false,
                              (byte lvl, byte br, byte fa, ushort mhp) identity = default)
    {
        bool changed = false;
        List<int> credited = weapons;
        if (_hasLiveWielder != null)
        {
            var (survivors, refused) = CreditGate.Decide(weapons, _hasLiveWielder);
            credited = survivors;
            if (refused.Count > 0)
            {
                string names = string.Join(", ", refused.ConvertAll(LogNames.Weapon));
                string itThem = refused.Count == 1 ? "it" : "them";
                ModLogger.Event(LogVerb.Kill,
                    $"The kill at battle slot {s} names {names}, but no unit on the field wields {itThem}; the credit is deliberately refused (stale attribution).");
                foreach (int w in refused)
                    _recorder?.Invoke("kill", $"no-credit slot={s} reason=no-live-wielder weapon={w}");
            }
        }
        // LW-233: stamp the post-gate survivor set for RestartSentinel's revive-uncredit (Corpses.cs). Copy the
        // list: `credited` may alias _lastPlayerWeapons, which the next latch replaces.
        _creditedWeapons[s] = new List<int>(credited);
        _creditedViaFallback[s] = viaFallback;
        _creditedIdentity[s] = identity;   // LW-233
        LogKillDiag(s, credited);   // D4 diagnostic, no behavioral effect
        _victimProbe.LogAtCredit(s);   // Reliquary P1 probe, log-only
        VictimSnapshot snap = _victimAtEdge[s];
        // LW-233 FINDING 0 (2026-08-17): the victim's nameId from the same snapshot (see _creditedNameId,
        // Corpses.cs); 0 when there was no sane read at the death edge.
        _creditedNameId[s] = snap.Has ? snap.NameId : (ushort)0;
        if (snap.Has)
            foreach (int w in credited)
            {
                _deeds?.RecordDeed(w, in snap);
                _deeds?.RecordPoachDeed(w, in snap, s, delayedOrCharged, viaFallback);
            }
        else
            _deeds?.DeedMiss(s);
        _victimAtEdge[s] = default;   // consume once
        string fell = VictimClass.FellPhrase(snap.Has, snap.Job, snap.Undead);
        if (viaFallback && credited.Count > 0) FallbackCredits++;   // one corpse = one fallback credit
        foreach (int w in credited)
        {
            _kills.TryGetValue(w, out int c);
            _kills[w] = c + 1;
            BattleCredits.TryGetValue(w, out int bc);
            BattleCredits[w] = bc + 1;   // per-weapon ledger for the battle-end summary
            ModLogger.EventWithTrace(LogVerb.Kill,
                $"{LogNames.Weapon(w)} claims kill number {c + 1}, felling {fell} at ({gx},{gy}).",
                $"kill credit (weapon id {w}, victim nameId {snap.NameId}, victim job {snap.Job}, battle slot {s})");
            _recorder?.Invoke("kill", $"credit weapon={w} count={c + 1} at=({gx},{gy}) slot={s}");
            changed = true;
        }
        _pending[s] = false;
        return changed;
    }

    /// <summary>LW-233: inverse of CreditKill's per-weapon increment, for RestartSentinel's checkpoint-retry
    /// revive (Corpses.cs re-arm block and identity-swap branch); callers pass the _creditedWeapons[s] CreditKill
    /// stamped. Floors tallies at 0 with a WARN if that would go negative (desync tripwire). FallbackCredits drops
    /// once per call. One flight record per weapon, no console line (RestartSentinel's latch-open edge already
    /// told the player).</summary>
    internal void UncreditKills(IReadOnlyList<int> weapons, bool viaFallback, int slot)
    {
        foreach (int w in weapons)
        {
            _kills.TryGetValue(w, out int c);
            if (c > 0)
            {
                _kills[w] = c - 1;
            }
            else
            {
                _kills[w] = 0;
                ModLogger.Warn(LogVerb.Kill,
                    $"A retry uncredit tried to remove a kill from {LogNames.Weapon(w)} that already read zero; the tally stays at zero (bookkeeping desync).");
            }

            BattleCredits.TryGetValue(w, out int bc);
            BattleCredits[w] = bc > 0 ? bc - 1 : 0;

            _recorder?.Invoke("restart", $"uncredit weapon={w} slot={slot}");
        }
        if (viaFallback && weapons.Count > 0 && FallbackCredits > 0) FallbackCredits--;
    }

    /// <summary>D4: AREC kill diagnostic, no effect on credit. Reads the corpse action record (Offsets.AArec)
    /// and logs one BattleLog line; skips if unreadable or no BattleLog. +0xB is logged as a hypothesis (xref?=),
    /// see docs/LIVE_LEDGER.md Uncertain AREC row.</summary>
    private void LogKillDiag(int s, List<int> weapons)
    {
        if (_events == null) return;
        long addr = Band.Entry(s) + Offsets.AArec;
        if (!_mem.Readable(addr, 0xC)) return;
        int idx = _mem.U8(addr);
        int abil = _mem.U16(addr + 0x2);
        int kind = _mem.U8(addr + 0xA);
        int xref = _mem.U8(addr + 0xB);
        _events.KillDiag($"kill-diagnostic: corpse action record index={idx} ability={abil} kind={kind} cross-reference?={xref}; credited (weapon ids {string.Join(",", weapons)})");
    }
}
