using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>Outcome of presenting one slot's revive to RestartSentinel. Refuse means do nothing.
/// UncreditNow means call KillTracker.UncreditKills now. Stashed means a null is
/// mid-flight and not yet qualified, so do nothing this tick; Tick's return value drains it later.</summary>
internal enum RevivePresentResult { Refuse, UncreditNow, Stashed }

/// <summary>
/// Checkpoint-retry detector. Retrying a lost battle rewinds the game in place with no exit
/// edge ([ledger battle-retry-rewind-fingerprint]), so
/// KillTracker would credit each revived enemy's kill a second time. This class watches the engine
/// actor pointer's raw-null state (ActorRegister.RawNullThisTick) and per-slot revive events, and
/// opens a latch the tracker checks before paying a phantom credit. It does no memory access, so
/// RestartSentinelTests drives it directly. Decision tables and constants are in
/// RestartSentinel.Policy.cs; the stash drain (ProcessStash) is in RestartSentinel.Stash.cs.
/// </summary>
internal sealed partial class RestartSentinel
{
    private readonly Action<string, string>? _recorder;

    private int _tick;
    private int _nullStreakTicks;
    /// <summary>Tick of the most recent qualified null (one that persisted long enough); 0 = none
    /// since the last reset.</summary>
    private int _lastQualifiedNullTick;
    private int _latchOpenTicksRemaining;
    private int _battleAgeTicks;
    private int _outOfLiveStreakTicks;
    private long _lastRetryRewinds = -1;
    private int _lastRetryHookTick;

    // The stash fields (_stash, _drainedThisTick) live in RestartSentinel.Stash.cs beside ProcessStash.

    /// <summary>True while a Retry is believed under way. A credited-corpse revive is then uncredited and a fresh
    /// dead-edge goes pending instead of crediting (see KillTracker.Corpses.cs).</summary>
    internal bool LatchOpen => _latchOpenTicksRemaining > 0;

    internal RestartSentinel(Action<string, string>? recorder = null) => _recorder = recorder;

    /// <summary>Full reset on battle enter/exit; KillTracker.ResetBattle calls this.</summary>
    internal void ResetBattle()
    {
        _tick = 0;
        _nullStreakTicks = 0;
        _lastQualifiedNullTick = 0;
        _latchOpenTicksRemaining = 0;
        _battleAgeTicks = 0;
        _outOfLiveStreakTicks = 0;
        _lastRetryHookTick = 0;
        _stash.Clear();
        _drainedThisTick.Clear();
    }

    /// <summary>Watches for the signs of a Retry, once per KillTracker.Poll and before the band scan so a null seen now
    /// can join a later revive. rawActorNull is true only on a literal zero actor pointer. Returns the drained stash entries.</summary>
    internal IReadOnlyList<(int Slot, List<int> Weapons, bool ViaFallback)> Tick(bool rawActorNull, bool inLiveish, long retryRewinds = 0)
    {
        _tick++;
        _battleAgeTicks++;
        if (_lastRetryRewinds >= 0 && retryRewinds > _lastRetryRewinds)
        {
            _lastRetryHookTick = _tick;
            _recorder?.Invoke("restart", $"retry hook seen count={retryRewinds}");
        }
        _lastRetryRewinds = retryRewinds;
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
            _stash.Clear();   // a stale stash must not survive the starved-bracket re-arm either
            justQualified = false;
        }

        ProcessStash(justQualified);

        if (_latchOpenTicksRemaining > 0) _latchOpenTicksRemaining--;

        return _drainedThisTick;
    }

    /// <summary>Decides whether one revived unit means the battle was Retried, see <see cref="RevivePresentResult"/>. Identity
    /// tuples are (lvl,br,fa,maxHp), credited versus now, from KillTracker.Corpses.cs. A match exempts the grace floor.</summary>
    internal RevivePresentResult PresentRevive(int slot, List<int> weapons, bool viaFallback, bool healedFromZero,
                                                (byte lvl, byte br, byte fa, ushort mhp) creditedIdentity,
                                                (byte lvl, byte br, byte fa, ushort mhp) presentedIdentity,
                                                ushort creditedNameId, ushort presentedNameId,
                                                bool braveFaithMayDiffer = false)
    {
        bool wasCredited = weapons.Count > 0;
        if (!wasCredited || !healedFromZero)
        {
            // Record declines. This only runs on a real revive event, not every idle tick.
            _recorder?.Invoke("restart", $"no-open reason={(wasCredited ? "not-healed-from-zero" : "not-credited")} slot={slot}");
            return RevivePresentResult.Refuse;
        }

        // Only call it the same unit when everything lines up, since taking back a real kill is worse than missing one.
        // Tuple and a nonzero nameId must both agree, since the tuple alone has collided between distinct
        // units [ledger party-nameid-unique-key]. 0 on either side means unavailable and fails closed.
        bool identityMatches = braveFaithMayDiffer
            ? RestartSentinelPolicy.SwapIdentityMatches(creditedIdentity, presentedIdentity, creditedNameId, presentedNameId)
            : creditedIdentity == presentedIdentity
                && creditedNameId != 0 && presentedNameId != 0 && creditedNameId == presentedNameId;
        string identityNote = IdentityTap(creditedIdentity, creditedNameId, presentedIdentity, presentedNameId);

        if (LatchOpen)
        {
            _latchOpenTicksRemaining = RestartSentinelPolicy.LatchTicks;   // re-openable. Extend on a fresh qualifying revive
            return RevivePresentResult.UncreditNow;
        }

        bool haveRetryHook = _lastRetryHookTick != 0;
        int ticksSinceRetryHook = haveRetryHook ? _tick - _lastRetryHookTick : int.MaxValue;
        if (RestartSentinelPolicy.ShouldOpenLatchFromRetryHook(wasCredited, healedFromZero, haveRetryHook, ticksSinceRetryHook))
        {
            LogOpenEdge($"retry hook seen {ticksSinceRetryHook} ticks before this revive, battle age {_battleAgeTicks} ticks, {identityNote}");
            return RevivePresentResult.UncreditNow;
        }

        bool haveQualifiedNull = _lastQualifiedNullTick != 0;
        int ticksSinceQualifiedNull = haveQualifiedNull ? _tick - _lastQualifiedNullTick : int.MaxValue;
        // Reviving from a status death never counts as a Retry, a known miss chosen over false positives.
        if (RestartSentinelPolicy.ShouldOpenLatch(wasCredited, healedFromZero, haveQualifiedNull,
                                                    ticksSinceQualifiedNull, _battleAgeTicks, identityMatches))
        {
            string graceNote = identityMatches && _battleAgeTicks <= RestartSentinelPolicy.GraceTicks
                ? ", grace exempted by a matching revived identity" : "";
            // Identity evidence goes on every open-edge record so a retry drill shows whether
            // nameId and the tuple survive a retry.
            LogOpenEdge($"null qualified {ticksSinceQualifiedNull} ticks before this revive, battle age {_battleAgeTicks} ticks{graceNote}, {identityNote}");
            return RevivePresentResult.UncreditNow;
        }

        if (RestartSentinelPolicy.ShouldStash(wasCredited, healedFromZero, _nullStreakTicks, _battleAgeTicks, identityMatches))
        {
            _stash.Add((slot, weapons, viaFallback, RestartSentinelPolicy.NullPersistTicks, identityMatches,
                        creditedIdentity, presentedIdentity, creditedNameId, presentedNameId));
            // A revive waiting on a Retry signal that never comes would vanish silently. This record and the
            // drop record in RestartSentinel.Stash.cs leave a trace on the flight tape.
            _recorder?.Invoke("restart", $"stash slot={slot} {identityNote}");
            return RevivePresentResult.Stashed;
        }

        // Best-effort refusal reason for the flight tape; diagnostic only, ties break in this order.
        string reason = !haveQualifiedNull && _nullStreakTicks == 0 ? "no-qualified-null"
            : haveQualifiedNull && ticksSinceQualifiedNull > RestartSentinelPolicy.JoinWindowTicks ? "outside-join-window"
            : !identityMatches && _battleAgeTicks <= RestartSentinelPolicy.GraceTicks ? "grace-not-cleared"
            : "null-not-yet-qualified";
        _recorder?.Invoke("restart", $"no-open reason={reason} slot={slot}, {identityNote}");
        return RevivePresentResult.Refuse;
    }

    /// <summary>Open the latch and log the plain-language retry line plus flight evidence. Shared by
    /// PresentRevive and ProcessStash so the console wording stays one string.</summary>
    private void LogOpenEdge(string evidence)
    {
        _latchOpenTicksRemaining = RestartSentinelPolicy.LatchTicks;
        ModLogger.Event(LogVerb.Kill,
            "The battle was retried; kills from the abandoned attempt are uncounted so nothing pays out twice.");
        _recorder?.Invoke("restart", $"latch open ({evidence})");
    }

    /// <summary>Short who-was-this tag for the flight recorder, shared by the open-edge, refusal and stash-drain
    /// records. L/B/F/H/N follows ActorRegister.cs (`fp=L{lvl}B{br}F{fa}`) and Puppeteer.cs (`nameId={...}`).</summary>
    private static string IdentityTap((byte lvl, byte br, byte fa, ushort mhp) credited, ushort creditedNameId,
                                       (byte lvl, byte br, byte fa, ushort mhp) presented, ushort presentedNameId)
        => $"credited=(L{credited.lvl}B{credited.br}F{credited.fa}H{credited.mhp}N{creditedNameId}) " +
           $"presented=(L{presented.lvl}B{presented.br}F{presented.fa}H{presented.mhp}N{presentedNameId})";
}
