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

    public KillTracker(Dictionary<int, int> kills, IGameMemory mem, ISet<int> weapons, BattleLog? events = null)
    {
        _kills = kills;
        _mem = mem;
        _register = new ActorRegister(mem);
        _resolver = new ActorResolver(mem, weapons, _register);
        _oracle = new EnemyOracle(mem);
        _events = events;
    }

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
        ResetBattleCorpses();   // clear per-battle band-scan state (Corpses.cs)
        ResetDelayed();         // clear delayed-action snapshot/arm state (Delayed.cs)
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
                    // Track whether the resolved actor holds any tracked weapon. Must be OUTSIDE the
                    // !SameSet guard (same placement rationale as the _lastActorFp refresh): two
                    // consecutive untracked actors share an empty set, so SameSet is true between
                    // them -- if gated inside, the second untracked actor would not refresh the flag
                    // and a following corpse would lose the sticky "untracked" verdict.
                    _latchResolvedEmpty = ws.Count == 0;
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
                        // Source tag mirrors TurnTracker's shipped [actor-ptr]/[tq-fallback] pair;
                        // no test asserts on these strings (inventory confirmed).
                        string src = _resolver.LastResolveViaRegister ? "actor-ptr" : "tq-fallback";
                        ModLogger.Log(ws.Count > 0
                            ? "turn: acting player wields " + string.Join(", ", ws.ConvertAll(id => LogNames.Weapon(id) + " (id " + id + ")")) + $" [{src}]"
                            : $"turn: acting player wields no tracked weapon -- this action's kills go uncredited [{src}]");
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
            _fallbackStreak = 0; _fallbackSet = new();
            ModLogger.Log("turn: no actor seen yet -- crediting the only player who has acted (first-kill fallback, weapons: " + string.Join(", ", ws.ConvertAll(id => LogNames.Weapon(id) + " (id " + id + ")")) + ")");
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

    /// <summary>Credit the given weapon set for a kill at band slot s (position gx,gy).</summary>
    internal bool CreditKill(int s, int gx, int gy, List<int> weapons)
    {
        bool changed = false;
        LogKillDiag(s, weapons);   // D4: evidence-accumulator diagnostic, zero behavioral dependence
        foreach (int w in weapons)
        {
            _kills.TryGetValue(w, out int c);
            _kills[w] = c + 1;
            ModLogger.Log($"kill: {LogNames.Weapon(w)} earns kill #{c + 1} (enemy fell at {gx},{gy})");
            changed = true;
        }
        _pending[s] = false;
        return changed;
    }

    /// <summary>D4 -- AREC kill diagnostic (evidence accumulator, ZERO behavioral dependence): read
    /// the corpse's action record (Offsets.AArec, band-entry-relative) and log one line through the
    /// dev BattleLog sink. Guarded read; skips silently when unreadable or when no BattleLog is
    /// wired (BattleLog now runs in every build flavor, Debug-tier: file-always, console only
    /// under the VerboseLog knob). +0xB is
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
        _events.KillDiag($"kill-diag: corpse AREC idx={idx} abil={abil} kind={kind} xref?={xref} -- credited [w:{string.Join(",", weapons)}]");
    }
}
