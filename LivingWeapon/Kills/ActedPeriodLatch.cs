using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Carries the latch state in and out of each <see cref="ActedPeriodLatch.Step"/> /
/// <see cref="ActedPeriodLatch.Reset"/> call. The fields live on KillTracker; it seeds this before the call
/// and copies back after (SeedLatchOutputs/ApplyLatchOutputs), so the latch needs no KillTracker reference.
/// </summary>
internal sealed class ActedPeriodOutputs
{
    internal bool PeriodOpen;
    internal bool Latched;
    // Reassigned, never mutated (no Add/Clear); ResetBattle and Reset both swap in a new empty list.
    internal List<int> LastPlayerWeapons = new();
    internal int LastPlayerMainHand;
    internal (int lvl, int br, int fa) LastActorFp;
    // The acting unit's frame nameId, refreshed beside LastActorFp (outside the !SameSet
    // guard) so TrackDelayed can compare identity by nameId. 0 means none was resolved (never latched,
    // ambiguous or enemy period, or the first-kill fallback).
    internal int LastActorNameId;
    internal bool LatchResolvedEmpty;
    internal bool LatchViaFallback;
    internal int LastResolveTick;
    internal string ActorTag = "";
    internal int ActedFalls;
    // Sticky within a battle; only Reset clears it. Seeded from KillTracker.AnyTrackedWeaponThisBattle.
    internal bool AnyTrackedWeaponThisBattle;
}

/// <summary>
/// The acted-period latch, moved out of KillTracker.Poll and FirstKillFallback unchanged
/// apart from addressing state through <see cref="ActedPeriodOutputs"/>. It latches the acting player's
/// weapons once per acted-period. The condensed struct follows the cursor (BATTLE_COORDINATES.md) and
/// acted stays 1 all turn, so the first successful resolve is kept until acted stays 0 for UnfreezeTicks.
/// <see cref="_actedLow"/>, <see cref="_fallbackSet"/> and <see cref="_fallbackStreak"/> are private here.
/// </summary>
internal sealed class ActedPeriodLatch
{
    private readonly IGameMemory _mem;
    private readonly ActorResolver _resolver;
    private readonly ActorRegister _register;
    private readonly Action<string, string>? _recorder;

    // Fields nothing outside this class reads.
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

    /// <summary>One tick of the latch, then the first-kill fallback. <paramref name="o"/> must be seeded
    /// from KillTracker first; <paramref name="anyPending"/> is KillTracker.AnyPending.</summary>
    internal void Step(ActedPeriodOutputs o, bool anyPending)
    {
        // Latch the acting player's weapons once per acted-period (see the class doc).
        if (_mem.U8(Offsets.Acted) == 1)
        {
            _actedLow = 0;
            // Begin fires once on the first acted==1 tick; a brief Acted dip never reaches End, so it cannot re-fire.
            if (!o.PeriodOpen)
            {
                o.PeriodOpen = true;
                _resolver.BeginActedPeriod(_register.Tick);
            }
            if (!o.Latched)
            {
                // A resolved player always replaces the latch, even with an empty weapon set, so untracked
                // gear goes uncredited (live bug. A DLC-armed Ramza's Throw Stone kill paid the prior actor's
                // crossbow). An unresolved period (enemy acting, Acted-byte flake) keeps the old latch.
                if (_resolver.TryResolveActingPlayer(out var ws))
                {
                    o.Latched = true;
                    o.LastResolveTick = _register.Tick;   // KillerStamp's ordering-gate comparand (KillerStamp.cs)
                    // Outside the !SameSet guard for the same reason as the fingerprint below. Two untracked
                    // actors share an empty set and the second would otherwise keep the first's verdict.
                    o.LatchResolvedEmpty = ws.Count == 0;
                    if (ws.Count > 0) o.AnyTrackedWeaponThisBattle = true;   // sticky armed gate (facelift)
                    // A flags-sourced resolve counts as pointer quality; only a turn-queue fallback does not.
                    o.LatchViaFallback = _resolver.LastResolveSource == ResolveSource.TqFallback;
                    // Refresh the acting fingerprint on the latch edge, outside the !SameSet guard. Two Arcanum
                    // holders share weapon set {30}, so inside it a handoff never updated it (the Larceny bug).
                    // The nameId refreshes at the same place and for the same reason, so a
                    // same-set handoff does not leave the previous wielder's identity behind.
                    bool resolvedFp = _resolver.TryResolveActingFingerprint(out var afp, out var actorNameId);
                    o.LastActorFp = resolvedFp ? afp : default;
                    o.LastActorNameId = actorNameId;   // 0 on any unresolved/ambiguous path (the resolver's own contract)
                    if (!ActorResolver.SameSet(ws, o.LastPlayerWeapons))
                    {
                        o.LastPlayerWeapons = ws;
                        o.LastPlayerMainHand = ws.Count > 0 ? _resolver.ResolveActingMainHand() : 0;
                        o.ActorTag = string.Join(",", ws);
                        // Source tag is a three-way since the turn-flags lane was added.
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
        // Debounced acted-falling edge (acted low for UnfreezeTicks) is one turn end; count it once per period.
        else if (_actedLow < KillTracker.UnfreezeTicks && ++_actedLow >= KillTracker.UnfreezeTicks)
        {
            o.Latched = false;
            o.ActedFalls++;
            o.PeriodOpen = false;
            _resolver.EndActedPeriod();
        }

        FirstKillFallback(o, anyPending);
    }

    /// <summary>First-kill fix. The killing action's acted edge can land seconds after the corpse and the
    /// first action of a battle has no earlier latch. While a corpse is pending and no latch exists, resolve
    /// the actor without the acted gate, unpaused, and accept after <see cref="KillTracker.FallbackStreak"/>
    /// identical non-empty resolves. Does nothing once a latch exists.</summary>
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
            // LastActorNameId stays 0 here. This path only runs when the acted path never
            // latched this battle, so there is no identity, and TrackDelayed falls back to the fingerprint.
            o.ActorTag = string.Join(",", ws);
            o.LastResolveTick = _register.Tick;   // KillerStamp's ordering-gate comparand (KillerStamp.cs)
            _fallbackStreak = 0; _fallbackSet = new();
            o.AnyTrackedWeaponThisBattle = true;   // ws is non-empty here. A tracked weapon is fielded
            o.LatchViaFallback = true;             // first-kill fallback is a fallback resolve by definition
            ModLogger.EventWithTrace(LogVerb.Credit,
                "No actor had been identified yet; crediting the only player who has acted (first-kill fallback), wielding " + string.Join(", ", ws.ConvertAll(LogNames.Weapon)) + ".",
                $"first-kill fallback latched (weapon ids {o.ActorTag})");
            _recorder?.Invoke("kill", $"latch weapons=[{o.ActorTag}] mainHand={o.LastPlayerMainHand} src=first-kill-fallback");
        }
    }

    /// <summary>Reset per-battle state. This class's own fields, plus the battle-start default of every
    /// output field written into <paramref name="o"/> for KillTracker.ResetBattle to copy back.</summary>
    internal void Reset(ActedPeriodOutputs o)
    {
        _actedLow = 0;
        _fallbackSet = new();
        _fallbackStreak = 0;

        o.LastPlayerWeapons = new();
        o.LastPlayerMainHand = 0;
        o.LastActorFp = default;
        o.LastActorNameId = 0;   // Clears beside LastActorFp
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
