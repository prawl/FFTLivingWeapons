using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Counts kills and attributes each to the acting player's weapon(s). Ported from the
/// FFTHandsFree active-unit resolver:
///   * STATE-BASED death -- credit a corpse exactly once (DeadCredited), reset when the
///     slot is seen alive. Survives the move / MaxHP-flicker race that defeats naive
///     "catch HP cross 0" detection. A corpse seen before its killer's `acted` flag
///     latches is held PENDING (not dropped) and credited a tick later.
///   * ATTRIBUTE BY CORPSE TEAM -- only band-slot entries whose identity was captured from
///     the static array (inb==1, enemy-side slots) can earn a kill credit. Player corpses,
///     guests, and any uncaptured identity are structurally excluded.
///   * THE ACTING PLAYER's weapons come from ActorResolver (HP+MaxHP+level -> band entry
///     -> level/brave/faith fingerprint -> roster hands). A DUAL-WIELDER latches BOTH
///     hands, so one kill credits both blades; a hand holding a shield is never credited.
///
/// Memory access is injected (IGameMemory) so the attribution logic is unit-testable.
/// The band corpse scan + alive-edge belt live in KillTracker.Corpses.cs; the enemy-side
/// identity set is <see cref="EnemyOracle"/>.
/// </summary>
internal sealed partial class KillTracker
{
    internal const int PendingTtl = 900; // ~30s wall-clock BACKSTOP at the 33ms tick: the REAL expiry is
                                         //   two debounced acted-falling edges (a killer latches during its
                                         //   own acted period, BEFORE its fall) -- this only catches a frozen
                                         //   scene with no turn edges at all
    internal const int UnfreezeTicks = 3; // acted must read 0 this long before the acted-period ends --
                                          //   the byte transiently drifts to 0 after a confirmed action
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
    // Register-tick birth stamp for a pending corpse (0 = not pending / not yet stamped). SAME
    // CURRENCY as ActorRegister.Tick/ArrivalTick -- unlike _pendingAge (which only advances on
    // onField Polls, KillTracker.Corpses.cs), this advances in lockstep with the register clock
    // (ActorRegister.Update runs every Poll, onField or not), so it is a valid comparand against
    // ArrivalTick even across off-field stretches (mid-battle dialogue/pause). Stamped/cleared in
    // UpdateCorpseAnchor -- see its doc comment for why it can't be stamped from Corpses.cs.
    internal readonly long[] _pendingBirthTick = new long[Offsets.BandSlots];
    internal List<int> _lastPlayerWeapons = new();   // the acting player's weapon(s); a dual-wielder latches both
    internal int _lastPlayerMainHand;                // RRHand id of the last latched actor (0 when none)
    internal (int lvl, int br, int fa) _lastActorFp; // fingerprint of the unit latched this acted-period
    // LW-252 stage 3: the latched actor's own frame nameId, mirrored beside _lastActorFp through
    // the same Seed/Step/Apply round trip (SeedLatchOutputs/ApplyLatchOutputs below); see
    // ActedPeriodOutputs.LastActorNameId's doc comment for the refresh/Reset contract. 0 = no
    // resolved identity (never-latched, ambiguous, or a first-kill-fallback latch).
    internal int _lastActorNameId;
    // Register tick of the period's most recent SUCCESSFUL latch resolve (stamped at BOTH
    // latch-confirm sites: the acted==1 TryResolveActingPlayer success below, and
    // FirstKillFallback's acceptance block). KillerStamp's ordering gate compares a register
    // arrival against this -- an arrival the latch already knew about (at or before this tick)
    // cannot outvote it. See KillerStamp.cs.
    internal int _lastResolveTick;
    private bool _latched;                           // a player resolved this acted-period -> frozen until it ends
    private bool _periodOpen;                        // an acted-period is open (EDGE-GUARDED: set once per rise,
                                                      //   cleared only on the debounced fall -- distinct from _latched,
                                                      //   which stays false through a whole period of failed resolves)
    internal bool _latchResolvedEmpty;              // most recent SUCCESSFUL resolve produced an EMPTY weapon set
                                                    //   (resolved-but-untracked actor: summoner/dancer/item-user
                                                    //   with no living weapon). Drives the FirstKillFallback bail +
                                                    //   the untracked corpse stamp. Sticky until the next successful
                                                    //   resolve so a later corpse stamped while untracked is correctly
                                                    //   blocked even if the active struct drifts between turns.
    internal int _actedFalls;                        // battle-local count of debounced acted-falling edges
    private string _actorTag = "";                   // cached "10,52" form of the latch, for event lines
    internal readonly BattleLog? _events;            // dev event timeline (damage/heal/move); null = off
    // Flight recorder tap (optional; null/no-op default keeps every existing test green
    // unmodified). Engine wires this to Flight.Record, and it is threaded down into the
    // ActorRegister this class constructs so pointer transitions get tapped too.
    private readonly Action<string, string>? _recorder;
    // Reliquary P1 probe instrumentation (docs/RELIQUARY_AC.md) -- log-only victim-identity
    // capture at three lifecycle points; see VictimProbe's doc comment. Internal (not private)
    // so tests can inspect the wired instance's snapshots directly, matching this file's existing
    // convention for test-observable per-slot state (e.g. _pending, _pendingAge above).
    internal readonly VictimProbe _victimProbe;
    // Reliquary P2 probe instrumentation (docs/RELIQUARY_AC.md) -- once-per-battle both-teams
    // identity census fired at EnemyOracle's coverage-complete edge; log/flight only, zero
    // behavioral dependence. See BattleCensus's doc comment.
    internal readonly BattleCensus _census;
    // Reliquary Phase 1 (docs/RELIQUARY_AC.md): optional deed sink CreditKill reports every
    // credited kill's captured victim to (or a miss, if none was captured). Null default keeps
    // every pre-Reliquary test and call site byte-identical -- see CreditKill's doc comment.
    private readonly IDeedSink? _deeds;

    // Logging facelift: the audit's ARMED-GATE flag ("any Living Weapon deployed this battle"),
    // implemented as the sticky per-battle latch the audit blesses: set the first time any
    // resolve latches a tracked weapon (the acted-period latch or the first-kill fallback),
    // cleared in ResetBattle. Gates the console emission of the per-corpse pending/expiry lines
    // and the EnemyOracle coverage lines, so a battle with no Living Weapon fielded stays quiet
    // on the console (the file still gets everything, per the two-sink doctrine).
    internal bool AnyTrackedWeaponThisBattle { get; private set; }
    // Scoped [kill] logger over that latch; routes the GATE-ON-ARMED corpse lines.
    private readonly ScopedLogger _klog;

    // Battle-exit match-report counters (logging facelift stage 3):
    // kills credited per weapon THIS battle (weaponId -> count); cleared in ResetBattle. The
    // battle-end summary derives tier crossings from this delta against the lifetime tally.
    internal readonly Dictionary<int, int> BattleCredits = new();
    // Corpse credits whose culprit latch was resolved via the turn-queue fallback (not the
    // engine actor pointer) this battle; feeds the summary's fallback-attribution clause.
    // Cleared in ResetBattle.
    internal int FallbackCredits { get; private set; }
    // The most recent successful latch resolve came from the tq fallback (or the first-kill
    // fallback), not the actor pointer; consulted by CreditKill's viaFallback argument sites.
    internal bool _latchViaFallback;
    // LW-56: the credit-time live-wielder gate's truth source (CreditGate.Decide's predicate).
    // Null (every pre-LW-56 call site, and every test that omits it) means the gate is OFF: every
    // culprit id survives, byte-identical to the pre-LW-56 behavior. Production wires this to
    // Wielder.HasLiveWielder over the same live memory every other roster/band consumer uses
    // (Engine.cs).
    private readonly Func<int, bool>? _hasLiveWielder;
    // S3 (LW-150): the acted-period latch state machine (KillTracker.cs's old Poll acted-block +
    // FirstKillFallback), extracted to ActedPeriodLatch.cs. THE MIRROR-OUTPUTS DESIGN: the latch
    // never touches this class's fields directly -- _latchIO is the one persistent transient
    // carrier, seeded from this class's own fields before every Step/Reset call and copied back
    // onto them immediately after (SeedLatchOutputs/ApplyLatchOutputs below). Constructed once so
    // Step doesn't allocate a fresh carrier every 33ms tick.
    private readonly ActedPeriodLatch _latch;
    private readonly ActedPeriodOutputs _latchIO = new();

    // LW-233: the checkpoint-retry detector (docs/LIVE_LEDGER.md row
    // [battle-retry-rewind-fingerprint]). Ticked once per Poll alongside _register; consulted by
    // KillTracker.Corpses.cs's revive detection (uncredit) and ResolveCredit (latch-open dead-edge
    // suppression). See RestartSentinel.cs for the mechanism.
    private readonly RestartSentinel _restart;

    public KillTracker(Dictionary<int, int> kills, IGameMemory mem, ISet<int> weapons, BattleLog? events = null,
                        Action<string, string>? recorder = null, IDeedSink? deeds = null,
                        Func<int, bool>? hasLiveWielder = null)
    {
        _kills = kills;
        _mem = mem;
        _recorder = recorder;
        // LW-56 A1: EnemyOracle is constructed first (moved ahead of ActorRegister) so the
        // oracle-exclusion lambda below closes over an already-assigned field: the lambda still
        // reads _oracle at CALL time (mid-battle, long after this ctor returns), not at capture
        // time, so this ordering is a compiler-friendliness nicety, not a behavioral dependency.
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

    /// <summary>LW-233: read-only exposure of this tracker's own retry-rewind detector, mirroring
    /// <see cref="Register"/>'s test-observability convention. Callers must never mutate it
    /// (Tick/ResetBattle stay this class's own responsibility).</summary>
    internal RestartSentinel Restart => _restart;

    /// <summary>Read-only exposure of this tracker's own actor-pointer ownership register
    /// (TurnOwnerSpike under LWDEV; AttackCard no longer consumes it since the 2026-07-06
    /// cursor-only fix): the SAME instance KillerStamp already trusts, not a second independent
    /// register (two registers ticking the same memory would double the reads for no benefit and
    /// risk two slightly different in-flight snapshots mid-tick). Callers must never mutate it
    /// (Update/ResetBattle stay this class's own responsibility).</summary>
    internal ActorRegister Register => _register;

    /// <summary>LW-56 D11/A3: thin wrapper over <see cref="BattleCensus.EmitExit"/>, called by
    /// Engine on the battle-exit edge (before the flight flush) so every exit tape carries a
    /// fresh census regardless of whether the enter-side coverage-done census ever fired.</summary>
    internal void EmitExitCensus() => _census.EmitExit();

    /// <summary>LW-31 stage-2 fix (grown in stage 3 to also surface rosterBase; retyped LW-55 to
    /// hand back the raw <see cref="CursorAnswer"/> instead of a filtered weapon list; retyped
    /// again LW-87 to also surface the <see cref="CursorMiss"/> stage: the ONLY resolve seam
    /// AttackCard consumes since the 2026-07-06 cursor-only fix): this tracker's
    /// ActorResolver.TryResolveCursorPlayer, exposed for AttackCard's dossier resolve
    /// (AttackCard.Resolve.cs). Null Answer = no cursor answer (guard failure or ambiguity), with
    /// Miss naming which resolve stage refused; non-null Answer = a confident resolve (Miss stays
    /// <see cref="CursorMiss.None"/>), with the raw roster/band facts AttackCard.Resolve.cs's
    /// CursorGate.Decide judges before any composing happens.</summary>
    internal Func<(CursorAnswer? Answer, CursorMiss Miss)> ResolveCursorPlayer =>
        () => _resolver.TryResolveCursorPlayer(out var answer, out var miss) ? (answer, miss) : ((CursorAnswer?)null, miss);

    /// <summary>This tracker's ActorResolver.SpriteOf, exposed for AttackCard's row-rename resolve
    /// (LW-31 stage 3): the roster slot's SpriteSet byte (the human/monster gate).</summary>
    internal Func<long, byte> SpriteOf => _resolver.SpriteOf;

    /// <summary>Reset per-battle state. Call on battle enter and exit. The next Poll runs cleanly:
    /// the seen-alive guard ensures any pre-existing corpse (never seen alive) is ineligible.</summary>
    public void ResetBattle()
    {
        Array.Clear(_pending, 0, _pending.Length);
        Array.Clear(_pendingAge, 0, _pendingAge.Length);
        Array.Clear(_pendingFalls, 0, _pendingFalls.Length);
        Array.Clear(_pendingBirthTick, 0, _pendingBirthTick.Length);
        // S3 (LW-150): the latch's own Reset (its clean-move fields) plus every acted-period
        // mirror-output field's battle-start default, written into _latchIO and copied back --
        // reproduces every one of the original inline assignments this replaced (including the
        // _lastPlayerWeapons list REPLACE), see ActedPeriodLatch.Reset's doc comment.
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

    /// <summary>S3 (LW-150): seed <see cref="_latchIO"/> with the CURRENT value of every
    /// acted-period mirror-output field, immediately before handing it to
    /// <see cref="ActedPeriodLatch.Step"/> -- the latch reads these as its inputs (e.g. whether
    /// it is already latched, the weapon set to SameSet-compare against) exactly as the original
    /// inline code read its own bare fields.</summary>
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

    /// <summary>S3 (LW-150): the write-back half of the mirror-outputs design -- copies every
    /// field <see cref="_latchIO"/> now holds back onto this class's own same-named fields, the
    /// ones every other partial/consumer/test reads. Called after both
    /// <see cref="ActedPeriodLatch.Step"/> (from Poll) and <see cref="ActedPeriodLatch.Reset"/>
    /// (from ResetBattle). The order of the individual assignments below is not itself observable
    /// (nothing reads any of these fields between them -- every reader runs either before Seed or
    /// after Apply), so it need not mirror the original inline statement order.</summary>
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

    /// <summary>One in-battle tick. <paramref name="onField"/> gates streak accumulation --
    /// off-field ticks (load screens / menu flickers) don't advance alive/dead counters.
    /// Returns true if the tally changed.</summary>
    public bool Poll(bool onField)
    {
        bool changed;

        _register.Update();       // ownership tracker: one read of the engine actor pointer per tick
        // LW-233: tick the retry-rewind detector BEFORE this same tick's corpse scan, so a null
        // observed this tick can join a revive detected later in the same Poll (Corpses.cs). The
        // return value drains the DEFERRED-VERDICT stash (the 0ms-join fix): a revive presented
        // last tick while the null was still mid-flight, now qualified -- apply the uncredit(s) it
        // was waiting on right here so the tally is correct before anything else this tick reads it.
        var restartDrained = _restart.Tick(_register.RawNullThisTick, onField);
        bool drainedChanged = restartDrained.Count > 0;
        foreach (var (drainSlot, drainWeapons, drainViaFallback) in restartDrained)
            UncreditKills(drainWeapons, drainViaFallback, drainSlot);
        UpdateCorpseAnchor();     // V1 corpse-anchor veto, pushed to the resolver for this tick's resolves

        // S3 (LW-150): the acted-period latch (latches the acting player's weapon(s) ONCE per
        // acted-period, freezing until the debounced acted-falling edge) plus the first-kill
        // fallback (no prior latch + a corpse waiting -> resolve the actor without the acted
        // gate), both extracted to ActedPeriodLatch.cs -- see its class doc for the mechanism.
        // Seed/Step/Apply is the mirror-outputs write-back: AnyPending() is evaluated here (the
        // same point in the sequence FirstKillFallback used to read it from) since it is the one
        // external input the latch needs that isn't itself a mirror-output field.
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

    /// <summary>LW-252 stage 3: the acting unit's own frame nameId, latched alongside
    /// <see cref="LastActorFingerprint"/> (same refresh timing, same Reset-clears contract). 0 =
    /// no resolved identity (never-latched, an ambiguous/enemy acted-period, or a first-kill-
    /// fallback latch -- see <see cref="ActedPeriodLatch.Step"/>'s and FirstKillFallback's own
    /// notes). Consumers must not cache across ticks (same caveat as every other latch field).</summary>
    public int LastActorNameId => _lastActorNameId;

    /// <summary>The acting player's weapon id(s) from the most recent latched actor.
    /// Empty at battle start and for any turn where no player actor was resolved.
    /// Consumers must not cache the reference across ticks (ResetBattle replaces it).</summary>
    public List<int> LastPlayerWeapons => _lastPlayerWeapons;

    /// <summary>The RRHand weapon id of the most recently latched actor, or 0 when none. A
    /// Living Weapon earns kills in any hand, but commands its gift only from the main hand --
    /// signature callers use this to gate activation on the main-hand weapon only.</summary>
    public int LastPlayerMainHand => _lastPlayerMainHand;

    internal bool AnyPending()
    {
        for (int s = 0; s < _pending.Length; s++) if (_pending[s]) return true;
        return false;
    }

    /// <summary>V1 corpse-anchor veto: refuses the register path (for every resolve this tick)
    /// whenever any pending corpse's REGISTER-TICK birth stamp is AT OR AFTER the register's
    /// current owner's arrival -- a killer's turn must CONTAIN the death, so an owner who arrived
    /// at/after a corpse went pending cannot be its killer.
    ///
    /// Compares same-currency register ticks (<see cref="_pendingBirthTick"/> vs
    /// <see cref="ActorRegister.ArrivalTick"/>), NOT the superseded _pendingAge/OwnershipAge pair:
    /// _pendingAge only advances on onField Polls (KillTracker.Corpses.cs bails `if (!onField)
    /// continue;` before it), while ActorRegister.Tick (and thus OwnershipAge) advances on every
    /// Poll including off-field ones (Update() runs unconditionally) -- so during an off-field
    /// stretch (mid-battle dialogue/pause) the old duration comparison under-counted the corpse's
    /// true age and could wrongly admit a new owner. The strict `>` it used also let the exact
    /// equality case escape; the `&lt;=` below closes that too.
    ///
    /// _pendingBirthTick can't be stamped from Corpses.cs (a pending flag transitions false->true
    /// there, but that file is a fixed plan boundary for this fix) -- so it is stamped HERE, the
    /// first Poll after a slot goes pending. Poll order is Update() -> UpdateCorpseAnchor() ->
    /// ... -> ScanCorpses() (KillTracker.cs), so the earliest this method can OBSERVE
    /// _pending[s]==true is the Poll immediately after ScanCorpses set it -- one register tick
    /// later than the true birth tick, which `_register.Tick - 1` exactly recovers.</summary>
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

    /// <summary>Credit the given weapon set for a kill at band slot s (position gx,gy). LW-56:
    /// when a live-wielder gate is wired (<see cref="_hasLiveWielder"/> non-null), the incoming
    /// culprit list is partitioned FIRST via <see cref="CreditGate.Decide"/> into survivors
    /// (credited normally) and refused (a stale attribution latch naming a weapon nobody on the
    /// field currently wields, e.g. one that survived a mis-timed New Game): everything below,
    /// the deed sink, the tally, BattleCredits, FallbackCredits, and the credit log, operates on
    /// survivors only. A refusal never throws away the corpse silently: each refused id gets one
    /// flight no-credit record plus one console/file ruling naming every refused weapon. Reliquary
    /// Phase 1: reports the slot's captured victim snapshot (KillTracker.Corpses.cs's
    /// _victimAtEdge) to the injected IDeedSink, once, then consumes it -- a missing snapshot
    /// (docs/RELIQUARY_AC.md's missing-snapshot failure mode) still increments the tally exactly
    /// as before, just with a DeedMiss instead of a RecordDeed. Never mutates/retains
    /// <paramref name="weapons"/> (may alias _lastPlayerWeapons). <paramref name="viaFallback"/>
    /// marks a credit whose culprit latch came from the turn-queue fallback rather than the
    /// actor pointer -- it feeds the battle-end summary's fallback-attribution counter.
    /// <paramref name="delayedOrCharged"/> (LW-167): true when this credit came from the delayed-
    /// actor path (KillTracker.Delayed.cs's ConsumeDelayedCulprit -- a Jump or charged spellcast
    /// that just landed); reported alongside every RecordPoachDeed call so a future consumer
    /// (stage 4's action discriminator) can tell a delayed strike from an ordinary one. Defaults
    /// false, matching every pre-existing call site that never passes it.</summary>
    internal bool CreditKill(int s, int gx, int gy, List<int> weapons, bool viaFallback = false, bool delayedOrCharged = false)
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
        // LW-233: stamp the POST-CreditGate survivor set this slot was actually credited to (empty
        // when the gate refused everyone), for RestartSentinel's revive-uncredit path
        // (KillTracker.Corpses.cs). A fresh List copy -- `credited` may alias `weapons`, which may
        // alias `_lastPlayerWeapons` (a mutable list this class replaces wholesale on the next
        // acted-period resolve; retaining a reference to it would let a LATER latch silently
        // rewrite what THIS slot thinks it was credited to).
        _creditedWeapons[s] = new List<int>(credited);
        _creditedViaFallback[s] = viaFallback;
        LogKillDiag(s, credited);   // D4: evidence-accumulator diagnostic, zero behavioral dependence
        _victimProbe.LogAtCredit(s);   // Reliquary P1 probe: log-only, zero behavioral dependence
        VictimSnapshot snap = _victimAtEdge[s];
        if (snap.Has)
            foreach (int w in credited)
            {
                _deeds?.RecordDeed(w, in snap);
                _deeds?.RecordPoachDeed(w, in snap, s, delayedOrCharged, viaFallback);
            }
        else
            _deeds?.DeedMiss(s);
        _victimAtEdge[s] = default;   // consume-once: this slot's next death gets a fresh capture
        string fell = VictimClass.FellPhrase(snap.Has, snap.Job, snap.Undead);
        if (viaFallback && credited.Count > 0) FallbackCredits++;   // one corpse = one fallback credit
        foreach (int w in credited)
        {
            _kills.TryGetValue(w, out int c);
            _kills[w] = c + 1;
            BattleCredits.TryGetValue(w, out int bc);
            BattleCredits[w] = bc + 1;   // the battle-end summary's per-weapon ledger
            ModLogger.EventWithTrace(LogVerb.Kill,
                $"{LogNames.Weapon(w)} claims kill number {c + 1}, felling {fell} at ({gx},{gy}).",
                $"kill credit (weapon id {w}, victim nameId {snap.NameId}, victim job {snap.Job}, battle slot {s})");
            _recorder?.Invoke("kill", $"credit weapon={w} count={c + 1} at=({gx},{gy}) slot={s}");
            changed = true;
        }
        _pending[s] = false;
        return changed;
    }

    /// <summary>LW-233: the ONE inverse of CreditKill's per-weapon mutation above (the foreach at
    /// :425-436) -- reverses exactly the weapon set a corpse was credited to when RestartSentinel's
    /// latch proves a checkpoint retry's rewind means that victim never really died. Called from
    /// KillTracker.Corpses.cs's revive detection (the re-arm block and the identity-swap branch),
    /// passing the exact <c>_creditedWeapons[s]</c> CreditKill itself stamped -- so this can never
    /// diverge from what was actually incremented. Floors each weapon's tally and BattleCredits at
    /// 0 with a WARN if a floor would have gone negative (a bookkeeping-desync tripwire that should
    /// never fire, since the credited set is CreditKill's own record of what it added).
    /// FallbackCredits mirrors CreditKill's own "one corpse = one fallback credit" bookkeeping
    /// (decremented once per call, not once per weapon). One flight record per weapon;
    /// deliberately NO console line per weapon here -- the sentinel's own latch-open edge
    /// (RestartSentinel.cs) already told the player once, and a line per uncredited weapon would
    /// just be noise on top of it.</summary>
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

    /// <summary>D4 -- AREC kill diagnostic (evidence accumulator, ZERO behavioral dependence): read
    /// the corpse's action record (Offsets.AArec, band-entry-relative) and log one line through the
    /// dev BattleLog sink. Guarded read; skips silently when unreadable or when no BattleLog is
    /// wired (BattleLog now runs in every build flavor, Debug-tier: file-always, console only
    /// when the console level is raised to Debug). +0xB is
    /// logged as a HYPOTHESIS (xref?=) -- see docs/LIVE_LEDGER.md's Uncertain AREC row. The credit
    /// path in CreditKill above never consults this.</summary>
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
