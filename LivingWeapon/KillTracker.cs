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
    private int _actedLow;                           // consecutive acted==0 ticks (drift-debounced period end)
    internal int _actedFalls;                        // battle-local count of debounced acted-falling edges
    private string _actorTag = "";                   // cached "10,52" form of the latch, for event lines
    private List<int> _fallbackSet = new();          // the resolve being stability-counted by the no-actor fallback
    private int _fallbackStreak;                     // consecutive identical non-empty resolves
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

    public KillTracker(Dictionary<int, int> kills, IGameMemory mem, ISet<int> weapons, BattleLog? events = null,
                        Action<string, string>? recorder = null, IDeedSink? deeds = null)
    {
        _kills = kills;
        _mem = mem;
        _recorder = recorder;
        _register = new ActorRegister(mem, recorder);
        _resolver = new ActorResolver(mem, weapons, _register);
        _killerStamp = new KillerStamp(_register, _resolver.HandsFromRoster);
        _oracle = new EnemyOracle(mem, () => AnyTrackedWeaponThisBattle);
        _events = events;
        _victimProbe = new VictimProbe(mem, recorder);
        _census = new BattleCensus(mem, recorder);
        _deeds = deeds;
        _klog = ModLogger.For(LogVerb.Kill, () => AnyTrackedWeaponThisBattle);
    }

    /// <summary>Read-only exposure of this tracker's own actor-pointer ownership register
    /// (TurnOwnerSpike under LWDEV; AttackCard no longer consumes it since the 2026-07-06
    /// cursor-only fix): the SAME instance KillerStamp already trusts, not a second independent
    /// register (two registers ticking the same memory would double the reads for no benefit and
    /// risk two slightly different in-flight snapshots mid-tick). Callers must never mutate it
    /// (Update/ResetBattle stay this class's own responsibility).</summary>
    internal ActorRegister Register => _register;

    /// <summary>LW-31 stage-2 fix (grown in stage 3 to also surface rosterBase; the ONLY resolve
    /// seam AttackCard consumes since the 2026-07-06 cursor-only fix): this tracker's
    /// ActorResolver.TryResolveCursorPlayer, exposed for AttackCard's dossier resolve
    /// (AttackCard.Resolve.cs). Null = no cursor answer (guard failure or ambiguity); non-null
    /// (weapons possibly empty) = a confident resolve, with rosterBase the matched roster slot
    /// AttackCard's row-rename resolve reads the RAW main hand and sprite byte from.</summary>
    internal Func<(List<int> Weapons, long RosterBase)?> ResolveCursorPlayer =>
        () => _resolver.TryResolveCursorPlayer(out var w, out long rb) ? (w, rb) : ((List<int>, long)?)null;

    /// <summary>This tracker's ActorResolver.RawMainHand, exposed for AttackCard's row-rename
    /// resolve (LW-31 stage 3): the raw, untracked-filter-free RRHand weapon id.</summary>
    internal Func<long, int> RawMainHand => _resolver.RawMainHand;

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
        _lastPlayerWeapons = new();
        _lastPlayerMainHand = 0;
        _lastActorFp = default;
        _lastResolveTick = 0;
        _latched = false;
        _periodOpen = false;
        _register.ResetBattle();
        _resolver.EndActedPeriod();
        _latchResolvedEmpty = false;   // battle start = never-resolved; genuine first kill still uses the fallback
        _actedLow = 0;
        _actedFalls = 0;
        _actorTag = "";
        _fallbackSet = new();
        _fallbackStreak = 0;
        _events?.ResetBattle();
        AnyTrackedWeaponThisBattle = false;   // re-quiet the armed gate for the next battle
        BattleCredits.Clear();                // per-battle match-report counters
        FallbackCredits = 0;
        _latchViaFallback = false;
        ResetBattleCorpses();   // clear per-battle band-scan state (Corpses.cs)
        ResetDelayed();         // clear delayed-action snapshot/arm state (Delayed.cs)
        _census.ResetBattle();  // re-arm the P2 identity-census probe (BattleCensus.cs)
    }

    /// <summary>One in-battle tick. <paramref name="onField"/> gates streak accumulation --
    /// off-field ticks (load screens / menu flickers) don't advance alive/dead counters.
    /// Returns true if the tally changed.</summary>
    public bool Poll(bool onField)
    {
        bool changed = false;

        _register.Update();       // ownership tracker: one read of the engine actor pointer per tick
        UpdateCorpseAnchor();     // V1 corpse-anchor veto, pushed to the resolver for this tick's resolves

        // Latch the acting player's weapon(s) ONCE per acted-period. acted==1 marks an action
        // complete, but the condensed struct follows the CURSOR (BATTLE_COORDINATES.md) and acted
        // stays 1 for the rest of the turn -- so re-resolving every tick let a post-act hover over
        // an ALLY steal the latch. The first successful resolve of the period is the actor (the
        // struct shows them when their action lands); freeze on it until acted stays 0 for
        // UnfreezeTicks (the byte drifts to 0 transiently after confirmed actions). The roster
        // fingerprint -- not the unreliable team field -- is the player test: enemies resolve empty,
        // so an enemy's acted-period never latches and the previous player's latch stays sticky.
        if (_mem.U8(Offsets.Acted) == 1)
        {
            _actedLow = 0;
            // Period Begin is EDGE-GUARDED: fires once on the first acted==1 tick of the period
            // (regardless of whether a resolve succeeds that tick), never re-fires mid-period --
            // a sub-UnfreezeTicks Acted drift dip never reaches the End branch below, so _periodOpen
            // stays true and this can't refresh the resolver's periodStartTick out from under it.
            if (!_periodOpen)
            {
                _periodOpen = true;
                _resolver.BeginActedPeriod(_register.Tick);
            }
            if (!_latched)
            {
                // A RESOLVED player always replaces the latch -- even with an EMPTY weapon set
                // (untracked gear like DLC weapons, or unarmed): their kills go honestly
                // uncredited instead of paying out to the previous actor (live bug: a Throw
                // Stone kill by a DLC-armed Ramza credited the prior actor's crossbow). Only
                // an UNRESOLVED acted-period (enemy acting / the Acted-byte flake) leaves the
                // previous latch sticky.
                if (_resolver.TryResolveActingPlayer(out var ws))
                {
                    _latched = true;
                    _lastResolveTick = _register.Tick;   // KillerStamp's ordering-gate comparand (KillerStamp.cs)
                    // Track whether the resolved actor holds any tracked weapon. Must be OUTSIDE the
                    // !SameSet guard (same placement rationale as the _lastActorFp refresh): two
                    // consecutive untracked actors share an empty set, so SameSet is true between
                    // them -- if gated inside, the second untracked actor would not refresh the flag
                    // and a following corpse would lose the sticky "untracked" verdict.
                    _latchResolvedEmpty = ws.Count == 0;
                    if (ws.Count > 0) AnyTrackedWeaponThisBattle = true;   // sticky armed gate (facelift)
                    _latchViaFallback = !_resolver.LastResolveViaRegister; // fallback-attribution counter feed
                    // Refresh the acting fingerprint once per acted-period (on the latch edge).
                    // MUST be outside the !SameSet guard: two Arcanum holders share weapon set {30},
                    // so SameSet is true between them -- if gated inside, switching between two
                    // same-weapon wielders would never update the fingerprint (the Larceny bug).
                    _lastActorFp = _resolver.TryResolveActingFingerprint(out var afp) ? afp : default;
                    if (!ActorResolver.SameSet(ws, _lastPlayerWeapons))
                    {
                        _lastPlayerWeapons = ws;
                        _lastPlayerMainHand = ws.Count > 0 ? _resolver.ResolveActingMainHand() : 0;
                        _actorTag = string.Join(",", ws);
                        // Source tag mirrors TurnTracker's shipped resolve-source pair;
                        // no test asserts on these strings (inventory confirmed).
                        string src = _resolver.LastResolveViaRegister ? "the actor pointer" : "the turn-queue fallback";
                        ModLogger.Debug(LogVerb.Credit, ws.Count > 0
                            ? "the acting player wields " + string.Join(", ", ws.ConvertAll(id => LogNames.Weapon(id) + " (weapon id " + id + ")")) + $", resolved via {src}"
                            : $"the acting player wields no Living Weapon; this action's kills will go uncredited (resolved via {src})");
                        _recorder?.Invoke("kill", $"latch weapons=[{_actorTag}] mainHand={_lastPlayerMainHand} src={(_resolver.LastResolveViaRegister ? "actor-ptr" : "tq-fallback")}");
                    }
                }
            }
        }
        // The debounced acted-falling edge (acted low for UnfreezeTicks) is one turn-end event: count it
        // once per period (drives the two-edge pending expiry below) as the latch unfreezes.
        else if (_actedLow < UnfreezeTicks && ++_actedLow >= UnfreezeTicks)
        {
            _latched = false;
            _actedFalls++;
            _periodOpen = false;
            _resolver.EndActedPeriod();
        }

        FirstKillFallback();   // no prior latch + a corpse waiting -> resolve the actor without the acted gate

        TrackDelayed(onField);   // snapshot/arm the committer of a delayed action (Delayed.cs)

        changed = ScanCorpses(onField);   // band corpse scan + identity capture (Corpses.cs)

        _census.Tick(_oracle.CoverageDone);   // P2 probe: fires once, right after the oracle's own tick

        return changed;
    }

    /// <summary>First-kill fix: the killing action's `acted` edge can arrive seconds after the corpse,
    /// and the FIRST action of a battle has no prior latch to fall back on. While a corpse is pending
    /// and no latch exists, resolve the actor WITHOUT the acted gate -- but only when not paused, and
    /// accept only after <see cref="FallbackStreak"/> consecutive identical non-empty resolves (a
    /// stability gate, so a flickering hover can't latch). Inert once a real latch exists, so a
    /// post-act ally hover can never steal credit.</summary>
    private void FirstKillFallback()
    {
        if (_lastPlayerWeapons.Count > 0 || _latchResolvedEmpty || !AnyPending() || _mem.U8(Offsets.PauseFlag) != 0)
        {
            _fallbackStreak = 0; _fallbackSet = new();
            return;
        }
        var ws = _resolver.ResolveActingWeapons();
        if (ws.Count == 0) { _fallbackStreak = 0; _fallbackSet = new(); return; }
        if (_fallbackStreak > 0 && ActorResolver.SameSet(ws, _fallbackSet)) _fallbackStreak++;
        else { _fallbackSet = ws; _fallbackStreak = 1; }
        if (_fallbackStreak >= FallbackStreak)
        {
            _lastPlayerWeapons = ws;
            _lastPlayerMainHand = _resolver.ResolveActingMainHand();
            _actorTag = string.Join(",", ws);
            _lastResolveTick = _register.Tick;   // KillerStamp's ordering-gate comparand (KillerStamp.cs)
            _fallbackStreak = 0; _fallbackSet = new();
            AnyTrackedWeaponThisBattle = true;   // ws is non-empty here: a tracked weapon is fielded
            _latchViaFallback = true;            // first-kill fallback is a fallback resolve by definition
            ModLogger.EventWithTrace(LogVerb.Credit,
                "No actor had been identified yet; crediting the only player who has acted (first-kill fallback), wielding " + string.Join(", ", ws.ConvertAll(LogNames.Weapon)) + ".",
                $"first-kill fallback latched (weapon ids {_actorTag})");
            _recorder?.Invoke("kill", $"latch weapons=[{_actorTag}] mainHand={_lastPlayerMainHand} src=first-kill-fallback");
        }
    }

    /// <summary>The (level,brave,faith) of the unit latched this acted-period, or default when
    /// none/ambiguous. Consumers must not cache across ticks.</summary>
    public (int lvl, int br, int fa) LastActorFingerprint => _lastActorFp;

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

    /// <summary>Credit the given weapon set for a kill at band slot s (position gx,gy). Reliquary
    /// Phase 1: reports the slot's captured victim snapshot (KillTracker.Corpses.cs's
    /// _victimAtEdge) to the injected IDeedSink, once, then consumes it -- a missing snapshot
    /// (docs/RELIQUARY_AC.md's missing-snapshot failure mode) still increments the tally exactly
    /// as before, just with a DeedMiss instead of a RecordDeed. Never mutates/retains
    /// <paramref name="weapons"/> (may alias _lastPlayerWeapons). <paramref name="viaFallback"/>
    /// marks a credit whose culprit latch came from the turn-queue fallback rather than the
    /// actor pointer -- it feeds the battle-end summary's fallback-attribution counter.</summary>
    internal bool CreditKill(int s, int gx, int gy, List<int> weapons, bool viaFallback = false)
    {
        bool changed = false;
        LogKillDiag(s, weapons);   // D4: evidence-accumulator diagnostic, zero behavioral dependence
        _victimProbe.LogAtCredit(s);   // Reliquary P1 probe: log-only, zero behavioral dependence
        VictimSnapshot snap = _victimAtEdge[s];
        if (snap.Has)
            foreach (int w in weapons) _deeds?.RecordDeed(w, in snap);
        else
            _deeds?.DeedMiss(s);
        _victimAtEdge[s] = default;   // consume-once: this slot's next death gets a fresh capture
        string fell = VictimClass.FellPhrase(snap.Has, snap.Job, snap.Undead);
        if (viaFallback && weapons.Count > 0) FallbackCredits++;   // one corpse = one fallback credit
        foreach (int w in weapons)
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
