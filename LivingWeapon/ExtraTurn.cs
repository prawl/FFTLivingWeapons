using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Zwill +3 "extra turn on kill", v8. On a kill credited to the Zwill, SLAM the wielder's scheduler
/// CT (band entry +0x25 == combat base+0x41, live-proven) every tick until the bonus move is
/// consumed. The grant is driven entirely off the killer's OWN CT: the condensed active-unit struct
/// follows cursor HOVER (FFTHandsFree BATTLE_COORDINATES.md), so it is never consulted -- not for
/// "is the killer acting" and not for the killer's identity. The killer IS the Zwill's roster
/// wielder, located on the Band walk (the LIVE source -- the static array freezes on restart) by
/// hand-weapon id + brave/faith, with the TWIN FILTER that unblocked v7: a roster unit has a frozen
/// duplicate copy in the band at position (0,0); the live copy carries a real grid position, so a
/// real-position match outranks a (0,0) one (live-proven: 14184F8AC at (7,4) vs twin 1418500AC).
///
/// The engine pulls CT below the consume floor only when a turn of that unit ENDS, and we read
/// before every re-slam, so turn-ends are countable events (see ExtraTurn.Policy.cs). The no-signal
/// window is a pathology timeout (healthy holds refresh it), the 90s cap guarantees termination,
/// and every non-consumed release restores CT=0 so a parked slam can never ghost-grant a turn.
/// Every read/write is VirtualQuery-guarded (Mem). Hardcoded weapon/tier for the prototype.
/// </summary>
internal sealed partial class ExtraTurn : ISignature
{
    void ISignature.Tick(in TickContext ctx) => Tick(ctx.Now);
    private readonly Dictionary<int, int> _kills;
    private readonly IGameMemory _mem;
    private readonly List<int> _hands = new();    // the wielder's real hand item ids (incl. the Zwill)
    private GrantState _state;
    private int _lastCount = -1;
    private (int lvl, int br, int fa) _wielder;   // the Zwill holder's roster fingerprint
    private long _base;                           // located band entry (slam target at +CtOff)
    private int _prevCt = -1;                     // previous read, for Classify's agreeing pair
    private int _streak;                          // consecutive full reads (Observe)
    private bool _took;                           // our slam (or the engine's hold) reached full CT
    private int _deadStreak;                      // consecutive HP==0 reads on the located entry
    private DateTime _deadline;                   // no-signal timeout; refreshed while the hold is healthy
    private DateTime _hardStop;                   // absolute per-grant cap
    private int _pullDowns;
    private int _dbg;

    public ExtraTurn(Dictionary<int, int> kills, IGameMemory mem) { _kills = kills; _mem = mem; }

    internal GrantState State => _state;
    /// <summary>The located band entry address; non-zero when the wielder was found this tick.
    /// Exposed for tests: a non-zero value confirms Locate succeeded (Band.Entry (0,0) twin scenario).</summary>
    internal long LocatedBase => _base;

    public void ResetBattle()
    {
        if (_state != GrantState.Idle) Release(ReleaseReason.BattleReset);
        _lastCount = -1;
        _dbg = 0;
    }

    public void Tick(DateTime now)
    {
        int count = _kills.TryGetValue(ZwillId, out int k) ? k : 0;
        // Signatures fire from the main hand only: a Zwill in the off-hand does not arm the grant.
        if (Tuning.TierFor(count) < AtTier || !Wielder.TryResolveMainHand(_mem, ZwillId, out _wielder, _hands))
        {
            if (_state != GrantState.Idle) Release(ReleaseReason.GateLost);
            _lastCount = count;
            return;
        }

        bool freshKill = Signatures.FreshKill(_lastCount, count);
        _lastCount = count;
        if (freshKill)
        {
            if (_state == GrantState.Idle) Arm(now);
            else ModLogger.Log($"extra-turn: another kill landed while an extra turn is already queued up -- it will not stack [state={_state}]");
        }
        if (_state == GrantState.Idle) return;

        if (now >= _hardStop) { Release(ReleaseReason.AbsoluteCap); return; }
        if (now >= _deadline) { Release(ReleaseReason.NoSignal); return; }

        _base = Wielder.Locate(_mem, ZwillId, _hands, _wielder);
        int ct = ReadCt();
        if (Healthy(ct)) _deadline = now.AddSeconds(NoSignalSeconds);

        // Liveness: a located entry holding HP 0 for a few ticks = the killer died mid-window.
        if (_base != 0 && ReadHp() == 0) { if (++_deadStreak >= 3) { Release(ReleaseReason.KillerDead); return; } }
        else _deadStreak = 0;

        if (_state == GrantState.Arming)
        {
            GrantState? cls = Classify(_prevCt, ct);
            _prevCt = ct;
            if (cls is { } s)
            {
                _state = s;
                ModLogger.Log($"extra-turn: figuring out timing for the bonus turn -- {(s == GrantState.Owed ? "two" : "one")} more turn-end(s) must pass before it fires [state={s} CT={ct}]");
            }
        }
        else
        {
            (_streak, _took, bool pullDown) = Observe(_streak, _took, ct);
            if (pullDown) ModLogger.LogDebug($"extra-turn: turn-end #{++_pullDowns} detected (CT {ct}, state {_state})");
            (GrantState next, bool consumed) = Step(_state, pullDown);
            if (consumed) { Release(ReleaseReason.Consumed); return; }
            if (next != _state) { _state = next; _deadline = now.AddSeconds(NoSignalSeconds); }
        }

        if (Slams(_state) && _base != 0 && _mem.Writable(_base + CtOff, 1)) _mem.W8(_base + CtOff, SlamCt);
        if (_dbg++ % 15 == 0) ModLogger.LogDebug($"extra-turn: {_state} -- wielder entry at 0x{_base:X}, CT {ct}, streak {_streak}, slam took={_took}");
    }

    private void Arm(DateTime now)
    {
        _state = GrantState.Arming; _base = 0; _prevCt = -1;
        _streak = 0; _took = false; _pullDowns = 0; _deadStreak = 0;
        _deadline = now.AddSeconds(NoSignalSeconds); _hardStop = now.AddSeconds(AbsoluteCapSeconds);
        ModLogger.Log($"extra-turn: {LogNames.Weapon(ZwillId)} scored a kill -- the wielder will get an extra turn (level {_wielder.lvl}, brave {_wielder.br}, faith {_wielder.fa})");
    }

    private void Release(ReleaseReason reason)
    {
        if (RestoreCt(reason) && _base != 0 && _mem.Writable(_base + CtOff, 1)) _mem.W8(_base + CtOff, 0);
        ModLogger.Log($"extra-turn: the extra turn ended ({reason}) after {_pullDowns} turn-end(s) [was in state {_state}]");
        _state = GrantState.Idle; _base = 0; _streak = 0; _took = false; _prevCt = -1; _deadStreak = 0;
    }

    private int ReadCt() => _base != 0 && _mem.Readable(_base + CtOff, 1) ? _mem.U8(_base + CtOff) : -1;
    private int ReadHp() => _base != 0 && _mem.Readable(_base + Offsets.AHp, 2) ? _mem.U16(_base + Offsets.AHp) : -1;

}
