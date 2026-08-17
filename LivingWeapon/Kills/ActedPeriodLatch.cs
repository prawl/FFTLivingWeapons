using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// S3 (LW-150): the transient carrier the acted-period latch reads its current state from and
/// writes its new state back to, once per <see cref="ActedPeriodLatch.Step"/> (or
/// <see cref="ActedPeriodLatch.Reset"/>) call. THE MIRROR-OUTPUTS DESIGN (adversarial-review
/// locked, field-ownership table is LAW): every field here still physically lives on KillTracker
/// as a same-named private field (other partials/tests/consumers read those, unchanged) -- this
/// class never touches KillTracker's fields directly. KillTracker seeds one of these from its own
/// fields immediately before calling into the latch, then copies every field back onto itself
/// immediately after (KillTracker.cs's SeedLatchOutputs/ApplyLatchOutputs). The latch itself never
/// retains a reference to KillTracker, so it stays constructible and testable from a bare
/// IGameMemory + ActorResolver + ActorRegister, with no KillTracker instance required.
/// </summary>
internal sealed class ActedPeriodOutputs
{
    internal bool PeriodOpen;
    internal bool Latched;
    // REPLACE-not-mutate: the latch only ever reassigns this reference (a fresh list from the
    // resolver, or the SAME reference carried through unchanged); it never calls .Add/.Clear on
    // whatever list is currently here. KillTracker.ResetBattle and the latch's own Reset both
    // replace it with a new empty list, matching today's `_lastPlayerWeapons = new();`.
    internal List<int> LastPlayerWeapons = new();
    internal int LastPlayerMainHand;
    internal (int lvl, int br, int fa) LastActorFp;
    // LW-252 stage 3: the acting unit's own frame nameId, carried beside LastActorFp through the
    // whole latch lifecycle (same refresh timing, same Reset/FirstKillFallback treatment) so
    // KillTracker's delayed-action committer compare (TrackDelayed) can verify identity by nameId
    // instead of bare fingerprint once both sides have one -- see ActedPeriodLatch.Step's refresh
    // site for why it sits OUTSIDE the !SameSet guard exactly like LastActorFp does. 0 = no
    // resolved actor this latch has ever named a nameId for (never-latched, an ambiguous/enemy
    // acted-period, or the first-kill fallback -- see FirstKillFallback's own note).
    internal int LastActorNameId;
    internal bool LatchResolvedEmpty;
    internal bool LatchViaFallback;
    internal int LastResolveTick;
    internal string ActorTag = "";
    internal int ActedFalls;
    // Sticky/monotonic within a battle (only ever set true by the latch; only Reset sets it
    // false). Seeded from KillTracker's own AnyTrackedWeaponThisBattle property before every
    // Step call so a straight copy-back is always correct (see KillTracker.cs).
    internal bool AnyTrackedWeaponThisBattle;
}

/// <summary>
/// S3 (LW-150): the acted-period latch state machine, extracted out of KillTracker.cs's Poll
/// (the acted==1/acted==0 block, lines 225-300 pre-extraction) and FirstKillFallback (lines
/// 319-344 pre-extraction) verbatim -- every read/write below is the SAME statement, just
/// addressed through the <see cref="ActedPeriodOutputs"/> carrier (<c>o.Field</c>) instead of a
/// bare KillTracker field (<c>_field</c>), and through this class's own private fields for the
/// three pieces of state nothing outside KillTracker.cs ever read (<see cref="_actedLow"/>,
/// <see cref="_fallbackSet"/>, <see cref="_fallbackStreak"/>). See ActedPeriodOutputs' doc comment
/// for the write-back contract these mirror-output fields honor.
///
/// Latches the acting player's weapon(s) ONCE per acted-period. acted==1 marks an action
/// complete, but the condensed struct follows the CURSOR (BATTLE_COORDINATES.md) and acted stays
/// 1 for the rest of the turn -- so re-resolving every tick let a post-act hover over an ALLY
/// steal the latch. The first successful resolve of the period is the actor (the struct shows
/// them when their action lands); freeze on it until acted stays 0 for UnfreezeTicks (the byte
/// drifts to 0 transiently after confirmed actions). FirstKillFallback recovers the very first
/// kill of a battle (whose killing action's acted edge can land seconds after the corpse, with no
/// prior latch to fall back on): while a corpse is pending and no latch exists, it resolves the
/// actor WITHOUT the acted gate, gated on a stability streak so a flickering hover can't latch.
/// </summary>
internal sealed class ActedPeriodLatch
{
    private readonly IGameMemory _mem;
    private readonly ActorResolver _resolver;
    private readonly ActorRegister _register;
    private readonly Action<string, string>? _recorder;

    // CLEAN MOVES (field-ownership table): nothing outside this class reads these.
    private int _actedLow;                   // consecutive acted==0 ticks (drift-debounced period end)
    private List<int> _fallbackSet = new();  // the resolve being stability-counted by the no-actor fallback
    private int _fallbackStreak;             // consecutive identical non-empty resolves

    internal ActedPeriodLatch(IGameMemory mem, ActorResolver resolver, ActorRegister register,
                               Action<string, string>? recorder)
    {
        _mem = mem;
        _resolver = resolver;
        _register = register;
        _recorder = recorder;
    }

    /// <summary>One tick of the acted-period latch plus the first-kill fallback, in that order
    /// (matching KillTracker.cs's original Poll sequencing exactly). <paramref name="o"/> must
    /// already be seeded with the CURRENT value of every mirror-output field (KillTracker's own
    /// SeedLatchOutputs); this method mutates it in place. <paramref name="anyPending"/> is
    /// KillTracker.AnyPending() evaluated by the caller -- the only external state
    /// FirstKillFallback's bail needs that isn't itself a mirror-output field.</summary>
    internal void Step(ActedPeriodOutputs o, bool anyPending)
    {
        // Latch the acting player's weapon(s) ONCE per acted-period. See the class doc for why
        // the freeze exists and why the roster fingerprint (not the team field) is the player test.
        if (_mem.U8(Offsets.Acted) == 1)
        {
            _actedLow = 0;
            // Period Begin is EDGE-GUARDED: fires once on the first acted==1 tick of the period
            // (regardless of whether a resolve succeeds that tick), never re-fires mid-period --
            // a sub-UnfreezeTicks Acted drift dip never reaches the End branch below, so
            // o.PeriodOpen stays true and this can't refresh the resolver's periodStartTick out
            // from under it.
            if (!o.PeriodOpen)
            {
                o.PeriodOpen = true;
                _resolver.BeginActedPeriod(_register.Tick);
            }
            if (!o.Latched)
            {
                // A RESOLVED player always replaces the latch -- even with an EMPTY weapon set
                // (untracked gear like DLC weapons, or unarmed): their kills go honestly
                // uncredited instead of paying out to the previous actor (live bug: a Throw
                // Stone kill by a DLC-armed Ramza credited the prior actor's crossbow). Only
                // an UNRESOLVED acted-period (enemy acting / the Acted-byte flake) leaves the
                // previous latch sticky.
                if (_resolver.TryResolveActingPlayer(out var ws))
                {
                    o.Latched = true;
                    o.LastResolveTick = _register.Tick;   // KillerStamp's ordering-gate comparand (KillerStamp.cs)
                    // Track whether the resolved actor holds any tracked weapon. Must be OUTSIDE the
                    // !SameSet guard (same placement rationale as the LastActorFp refresh below): two
                    // consecutive untracked actors share an empty set, so SameSet is true between
                    // them -- if gated inside, the second untracked actor would not refresh the flag
                    // and a following corpse would lose the sticky "untracked" verdict.
                    o.LatchResolvedEmpty = ws.Count == 0;
                    if (ws.Count > 0) o.AnyTrackedWeaponThisBattle = true;   // sticky armed gate (facelift)
                    // LW-63: a flags-sourced resolve is pointer-quality evidence, same as a
                    // register-sourced one -- only a genuine turn-queue fallback counts here.
                    o.LatchViaFallback = _resolver.LastResolveSource == ResolveSource.TqFallback;
                    // Refresh the acting fingerprint once per acted-period (on the latch edge).
                    // MUST be outside the !SameSet guard: two Arcanum holders share weapon set {30},
                    // so SameSet is true between them -- if gated inside, switching between two
                    // same-weapon wielders would never update the fingerprint (the Larceny bug).
                    // LW-252 stage 3: the acting unit's nameId refreshes on the SAME call, SAME
                    // placement, for the SAME reason -- two same-set wielders handing off must not
                    // leave a stale identity behind either (TrackDelayed's committer compare would
                    // otherwise keep matching the PREVIOUS wielder's nameId against the new one's
                    // band seat).
                    bool resolvedFp = _resolver.TryResolveActingFingerprint(out var afp, out var actorNameId);
                    o.LastActorFp = resolvedFp ? afp : default;
                    o.LastActorNameId = actorNameId;   // 0 on any unresolved/ambiguous path (the resolver's own contract)
                    if (!ActorResolver.SameSet(ws, o.LastPlayerWeapons))
                    {
                        o.LastPlayerWeapons = ws;
                        o.LastPlayerMainHand = ws.Count > 0 ? _resolver.ResolveActingMainHand() : 0;
                        o.ActorTag = string.Join(",", ws);
                        // Source tag mirrors TurnTracker's shipped resolve-source pair, now a
                        // three-way (LW-63 adds the turn-flags lane); no test asserts on these
                        // strings (inventory confirmed).
                        string src = _resolver.LastResolveSource switch
                        {
                            ResolveSource.Flags => "the turn flags",
                            ResolveSource.Register => "the actor pointer",
                            _ => "the turn-queue fallback",
                        };
                        ModLogger.Debug(LogVerb.Credit, ws.Count > 0
                            ? "the acting player wields " + string.Join(", ", ws.ConvertAll(id => LogNames.Weapon(id) + " (weapon id " + id + ")")) + $", resolved via {src}"
                            : $"the acting player wields no Living Weapon; this action's kills will go uncredited (resolved via {src})");
                        string srcTag = _resolver.LastResolveSource switch
                        {
                            ResolveSource.Flags => "turn-flags",
                            ResolveSource.Register => "actor-ptr",
                            _ => "tq-fallback",
                        };
                        _recorder?.Invoke("kill", $"latch weapons=[{o.ActorTag}] mainHand={o.LastPlayerMainHand} src={srcTag}");
                    }
                }
            }
        }
        // The debounced acted-falling edge (acted low for UnfreezeTicks) is one turn-end event: count it
        // once per period (drives the two-edge pending expiry) as the latch unfreezes.
        else if (_actedLow < KillTracker.UnfreezeTicks && ++_actedLow >= KillTracker.UnfreezeTicks)
        {
            o.Latched = false;
            o.ActedFalls++;
            o.PeriodOpen = false;
            _resolver.EndActedPeriod();
        }

        FirstKillFallback(o, anyPending);
    }

    /// <summary>First-kill fix: the killing action's `acted` edge can arrive seconds after the corpse,
    /// and the FIRST action of a battle has no prior latch to fall back on. While a corpse is pending
    /// and no latch exists, resolve the actor WITHOUT the acted gate -- but only when not paused, and
    /// accept only after <see cref="KillTracker.FallbackStreak"/> consecutive identical non-empty
    /// resolves (a stability gate, so a flickering hover can't latch). Inert once a real latch exists,
    /// so a post-act ally hover can never steal credit.</summary>
    private void FirstKillFallback(ActedPeriodOutputs o, bool anyPending)
    {
        if (o.LastPlayerWeapons.Count > 0 || o.LatchResolvedEmpty || !anyPending || _mem.U8(Offsets.PauseFlag) != 0)
        {
            _fallbackStreak = 0; _fallbackSet = new();
            return;
        }
        var ws = _resolver.ResolveActingWeapons();
        if (ws.Count == 0) { _fallbackStreak = 0; _fallbackSet = new(); return; }
        if (_fallbackStreak > 0 && ActorResolver.SameSet(ws, _fallbackSet)) _fallbackStreak++;
        else { _fallbackSet = ws; _fallbackStreak = 1; }
        if (_fallbackStreak >= KillTracker.FallbackStreak)
        {
            o.LastPlayerWeapons = ws;
            o.LastPlayerMainHand = _resolver.ResolveActingMainHand();
            // LW-252 stage 3: NO nameId set here on purpose -- ResolveActingWeapons/ResolveActingMainHand
            // above only ever surface weapon ids, never an identity, and this path only reaches
            // acceptance when o.LastActorNameId is still at its Reset-time default (0: the acted-path
            // branch above is the only latch-confirm site that resolves a fingerprint at all, and it
            // never ran this battle -- that is this method's own precondition, `o.LastPlayerWeapons.Count
            // == 0` at entry). Leaving it untouched keeps it 0, so TrackDelayed's committer compare
            // correctly falls back to fp for a first-kill-fallback-latched actor.
            o.ActorTag = string.Join(",", ws);
            o.LastResolveTick = _register.Tick;   // KillerStamp's ordering-gate comparand (KillerStamp.cs)
            _fallbackStreak = 0; _fallbackSet = new();
            o.AnyTrackedWeaponThisBattle = true;   // ws is non-empty here: a tracked weapon is fielded
            o.LatchViaFallback = true;             // first-kill fallback is a fallback resolve by definition
            ModLogger.EventWithTrace(LogVerb.Credit,
                "No actor had been identified yet; crediting the only player who has acted (first-kill fallback), wielding " + string.Join(", ", ws.ConvertAll(LogNames.Weapon)) + ".",
                $"first-kill fallback latched (weapon ids {o.ActorTag})");
            _recorder?.Invoke("kill", $"latch weapons=[{o.ActorTag}] mainHand={o.LastPlayerMainHand} src=first-kill-fallback");
        }
    }

    /// <summary>Reset per-battle state: this class's own clean-move fields, plus every
    /// mirror-output field's battle-start default (including <see cref="ActedPeriodOutputs.LastPlayerWeapons"/>'s
    /// list REPLACE) written into <paramref name="o"/> for KillTracker.ResetBattle to copy back --
    /// reproduces KillTracker.cs's original ResetBattle assignments exactly, just consolidated
    /// into one call instead of scattered across it.</summary>
    internal void Reset(ActedPeriodOutputs o)
    {
        _actedLow = 0;
        _fallbackSet = new();
        _fallbackStreak = 0;

        o.LastPlayerWeapons = new();
        o.LastPlayerMainHand = 0;
        o.LastActorFp = default;
        o.LastActorNameId = 0;   // LW-252 stage 3: clears beside LastActorFp
        o.LastResolveTick = 0;
        o.Latched = false;
        o.PeriodOpen = false;
        o.LatchResolvedEmpty = false;   // battle start = never-resolved; genuine first kill still uses the fallback
        o.ActorTag = "";
        o.ActedFalls = 0;
        o.AnyTrackedWeaponThisBattle = false;   // re-quiet the armed gate for the next battle
        o.LatchViaFallback = false;
    }
}
