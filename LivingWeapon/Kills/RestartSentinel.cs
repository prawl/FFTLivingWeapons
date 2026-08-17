using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>Outcome of presenting one slot's revive event to RestartSentinel. <see cref="Refuse"/>:
/// nothing here (no credit to reverse, no retry evidence, or grace still holds) -- the caller does
/// nothing. <see cref="UncreditNow"/>: the latch is open (just now, or already) -- the caller calls
/// KillTracker.UncreditKills immediately. <see cref="Stashed"/> (LW-233 fix): a null is currently
/// mid-flight but has not yet qualified -- the caller does nothing THIS tick; RestartSentinel.Tick's
/// return value drains it later, either into an uncredit (the null qualified in time) or into
/// nothing at all (the null broke first).</summary>
internal enum RevivePresentResult { Refuse, UncreditNow, Stashed }

/// <summary>
/// LW-233: the checkpoint-retry detector. Losing a battle and picking retry rewinds the game IN
/// PLACE with NO exit edge (docs/LIVE_LEDGER.md row [battle-retry-rewind-fingerprint]), so
/// KillTracker's ordinary state-based death detection re-credits every revived enemy's kill a
/// second time when it is re-killed -- permanent kill-tally inflation. This class watches two
/// signals KillTracker.Corpses.cs already has for free (the engine actor pointer's raw-null state,
/// via ActorRegister.RawNullThisTick, and each slot's own revive events) and opens a LATCH the
/// tracker consults before paying out a phantom re-credit or a phantom re-kill.
///
/// NO memory access here (unlike most of this runtime's modules) -- every input arrives as a plain
/// value from the caller, so this class is unit-tested directly with no fake memory at all
/// (RestartSentinelTests.cs). The pure ShouldOpenLatch/ShouldStash decision tables live in
/// RestartSentinel.Policy.cs. THIS FILE is the core tick/latch machinery (Tick's null-streak/
/// battle-age/out-of-live bookkeeping, PresentRevive, LogOpenEdge); RestartSentinel.Stash.cs holds
/// the deferred-verdict stash's own per-tick TTL/drain bookkeeping (ProcessStash) -- split once the
/// stash mechanism pushed this file past the 200-line refactor trigger.
///
/// THE TWO TICK INPUTS, per Tick(): rawActorNull (true only on a literal raw-zero actor pointer
/// read, never a shape-fail or an unreadable page -- see ActorRegister.RawNullThisTick's own doc)
/// and inLiveish (the same OnField input KillTracker.Poll already threads through). PresentRevive is
/// called zero or more times per tick, inline from KillTracker.Corpses.cs's single-pass band scan,
/// once per slot that just came back alive from a credited death.
///
/// LATCH RULE (asymmetric, per-event, plan v2): opens the moment a revive presented with
/// wasCredited &amp;&amp; healedFromZero both true lands while a QUALIFIED null (persisted
/// <see cref="RestartSentinelPolicy.NullPersistTicks"/> ticks) occurred at-or-before it, within
/// <see cref="RestartSentinelPolicy.JoinWindowTicks"/>, past the battle's own opening
/// <see cref="RestartSentinelPolicy.GraceTicks"/>. Once open, ANY later wasCredited+healedFromZero
/// revive re-arms the full <see cref="RestartSentinelPolicy.LatchTicks"/> duration without needing
/// its own fresh null-join. A status-death revive (healedFromZero false) never opens, extends, or
/// stashes -- the named precision-first residual (a miss, never a false positive).
///
/// DEFERRED VERDICT (LW-233 fix, verifier-caught -- the 0ms-join gap): retry A's REAL tape shows
/// the raw null and the credited corpses' alive-again read landing on the SAME tick. A REVIVE is
/// recognized IMMEDIATELY, on the very first tick hp reads positive again: KillTracker.Corpses.cs's
/// _seenAlive/_aliveStreak are never reset by death, so the AliveNeeded debounce only ever gates a
/// slot's FIRST-ever alive sighting, never a revival. So when the null and the revive land on the
/// same tick, the null streak the sentinel sees at that exact presentation is only 1 -- one short
/// of qualifying. The original per-event design simply refused that event, and a tick later, when
/// the null DID qualify, the evidence (the corpse's credited-weapon set) was already gone (cleared
/// by the ordinary re-arm).
/// PresentRevive now returns <see cref="RevivePresentResult.Stashed"/> for exactly this window
/// (RestartSentinelPolicy.ShouldStash): the (slot, weapons, viaFallback) is held with a TTL of
/// <see cref="RestartSentinelPolicy.NullPersistTicks"/> ticks. Tick() drains it -- through its own
/// return value -- the moment the streak reaches NullPersistTicks (opening the latch exactly as the
/// direct path would, plus every other still-live stash entry from the same in-progress null), or
/// drops it silently if the streak breaks first (a real Raise never has an active null streak, so
/// that path stays byte-identical to today). "At-or-before" holds structurally: a revive can only
/// be stashed while a null has ALREADY started (streak &gt;= 1), never before.
///
/// OUT-OF-LIVE RE-ARM: a sustained stretch of inLiveish==false (<see cref="RestartSentinelPolicy.OutOfLiveRearmTicks"/>
/// ticks) closes the latch, forgets the null history, drops any pending stash, and resets the
/// battle-age clock to zero -- the LW-108 starved-bracket hole, where a battle-to-battle transition
/// fast enough to eat the normal enter-edge debounce leaves KillTracker.ResetBattle uncalled, so
/// battle age on its own cannot be trusted to mean "still the same encounter". This is the
/// sentinel's OWN independent re-arm, not a substitute for ResetBattle (which also fully resets
/// this class -- see below).
/// </summary>
internal sealed partial class RestartSentinel
{
    private readonly Action<string, string>? _recorder;

    private int _tick;
    private int _nullStreakTicks;
    /// <summary>The tick of the most recent QUALIFIED null (persisted long enough); 0 = none since
    /// the last ResetBattle/out-of-live re-arm.</summary>
    private int _lastQualifiedNullTick;
    private int _latchOpenTicksRemaining;
    private int _battleAgeTicks;
    private int _outOfLiveStreakTicks;

    // The stash's own state (_stash, _drainedThisTick) lives in RestartSentinel.Stash.cs beside
    // ProcessStash, the logic that owns it -- Tick() (below) and PresentRevive still reach into it
    // (clear/add/return), same cross-file field access this codebase already uses throughout
    // KillTracker's own partial-class split (e.g. KillTracker.Corpses.cs's _creditedWeapons, read
    // and written from KillTracker.cs's CreditKill/UncreditKills).

    /// <summary>True while a credited-corpse revive should be uncredited and a fresh dead-edge
    /// should go pending instead of crediting -- see KillTracker.Corpses.cs's two consult sites.</summary>
    internal bool LatchOpen => _latchOpenTicksRemaining > 0;

    internal RestartSentinel(Action<string, string>? recorder = null) => _recorder = recorder;

    /// <summary>Full reset on battle enter/exit, mirroring every other per-battle state machine in
    /// this tracker (KillTracker.ResetBattle calls this alongside _register.ResetBattle()).</summary>
    internal void ResetBattle()
    {
        _tick = 0;
        _nullStreakTicks = 0;
        _lastQualifiedNullTick = 0;
        _latchOpenTicksRemaining = 0;
        _battleAgeTicks = 0;
        _outOfLiveStreakTicks = 0;
        _stash.Clear();
        _drainedThisTick.Clear();
    }

    /// <summary>One tick, called once per KillTracker.Poll (mirrors ActorRegister.Update's
    /// every-tick cadence) BEFORE that same tick's band scan presents any revives, so a null
    /// observed this tick can join a revive detected later in the same tick. Returns every stash
    /// entry that drained THIS tick (empty on almost every call) -- the caller (KillTracker.Poll)
    /// applies UncreditKills to each and folds the drain into its own changed-flag return.</summary>
    internal IReadOnlyList<(int Slot, List<int> Weapons, bool ViaFallback)> Tick(bool rawActorNull, bool inLiveish)
    {
        _tick++;
        _battleAgeTicks++;
        _drainedThisTick.Clear();
        bool justQualified = false;

        if (rawActorNull)
        {
            _nullStreakTicks++;
            if (_nullStreakTicks == RestartSentinelPolicy.NullPersistTicks) justQualified = true;
            if (RestartSentinelPolicy.NullQualifies(_nullStreakTicks))
                _lastQualifiedNullTick = _tick;
        }
        else
        {
            _nullStreakTicks = 0;
        }

        if (inLiveish)
        {
            _outOfLiveStreakTicks = 0;
        }
        else if (++_outOfLiveStreakTicks >= RestartSentinelPolicy.OutOfLiveRearmTicks)
        {
            _latchOpenTicksRemaining = 0;
            _nullStreakTicks = 0;
            _lastQualifiedNullTick = 0;
            _battleAgeTicks = 0;
            _outOfLiveStreakTicks = 0;
            _stash.Clear();   // LW-233 fix: a stale stash must not survive the starved-bracket re-arm either
            justQualified = false;
        }

        ProcessStash(justQualified);

        if (_latchOpenTicksRemaining > 0) _latchOpenTicksRemaining--;

        return _drainedThisTick;
    }

    /// <summary>Present one slot's revive event. See <see cref="RevivePresentResult"/> for what
    /// each outcome means and what the caller should do with it.</summary>
    internal RevivePresentResult PresentRevive(int slot, List<int> weapons, bool viaFallback, bool healedFromZero)
    {
        bool wasCredited = weapons.Count > 0;
        if (!wasCredited || !healedFromZero) return RevivePresentResult.Refuse;

        if (LatchOpen)
        {
            _latchOpenTicksRemaining = RestartSentinelPolicy.LatchTicks;   // re-openable: extend on a fresh qualifying revive
            return RevivePresentResult.UncreditNow;
        }

        bool haveQualifiedNull = _lastQualifiedNullTick != 0;
        int ticksSinceQualifiedNull = haveQualifiedNull ? _tick - _lastQualifiedNullTick : int.MaxValue;
        if (RestartSentinelPolicy.ShouldOpenLatch(wasCredited, healedFromZero, haveQualifiedNull,
                                                    ticksSinceQualifiedNull, _battleAgeTicks))
        {
            LogOpenEdge($"null qualified {ticksSinceQualifiedNull} ticks before this revive, battle age {_battleAgeTicks} ticks");
            return RevivePresentResult.UncreditNow;
        }

        if (RestartSentinelPolicy.ShouldStash(wasCredited, healedFromZero, _nullStreakTicks, _battleAgeTicks))
        {
            _stash.Add((slot, weapons, viaFallback, RestartSentinelPolicy.NullPersistTicks));
            return RevivePresentResult.Stashed;
        }

        return RevivePresentResult.Refuse;
    }

    /// <summary>Open the latch and log the one plain-language retry line plus its flight evidence.
    /// Shared by the direct-open path (PresentRevive) and the stash-drain path (ProcessStash) so
    /// the console wording stays a single frozen string regardless of which path fired it.</summary>
    private void LogOpenEdge(string evidence)
    {
        _latchOpenTicksRemaining = RestartSentinelPolicy.LatchTicks;
        ModLogger.Event(LogVerb.Kill,
            "The battle was retried; kills from the abandoned attempt are uncounted so nothing pays out twice.");
        _recorder?.Invoke("restart", $"latch open ({evidence})");
    }
}
