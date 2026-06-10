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
internal sealed partial class ExtraTurn
{
    private const int EntryWeapon = Offsets.CWeapon - Offsets.BandEntry;   // weapon id at +0x04 in the entry frame

    private static readonly LiveMemory BandMem = new();   // Band.IsValid takes IGameMemory; reads == Mem

    private readonly Dictionary<int, int> _kills;
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
    private bool _ambiguityLogged;                // log a locate tie ONCE per grant, not every 500ms

    public ExtraTurn(Dictionary<int, int> kills) { _kills = kills; }

    internal GrantState State => _state;

    public void ResetBattle()
    {
        if (_state != GrantState.Idle) Release(ReleaseReason.BattleReset);
        _lastCount = -1;
        _dbg = 0;
    }

    public void Tick(DateTime now)
    {
        int count = _kills.TryGetValue(ZwillId, out int k) ? k : 0;
        if (Tuning.TierFor(count) < AtTier || !ResolveWielder())
        {
            if (_state != GrantState.Idle) Release(ReleaseReason.GateLost);
            _lastCount = count;
            return;
        }

        bool freshKill = _lastCount >= 0 && count > _lastCount;
        _lastCount = count;
        if (freshKill)
        {
            if (_state == GrantState.Idle) Arm(now);
            else Log.Info($"extra-turn: kill landed while bonus is already pending ({_state}) -- chains are not granted");
        }
        if (_state == GrantState.Idle) return;

        if (now >= _hardStop) { Release(ReleaseReason.AbsoluteCap); return; }
        if (now >= _deadline) { Release(ReleaseReason.NoSignal); return; }

        _base = Locate();
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
                Log.Info($"extra-turn: state resolved to {s} (CT {ct}; {(s == GrantState.Owed ? "kill-turn still running -- two" : "kill-turn already over -- one")} pull-down(s) needed to confirm the bonus)");
            }
        }
        else
        {
            (_streak, _took, bool pullDown) = Observe(_streak, _took, ct);
            if (pullDown) Log.Info($"extra-turn: turn-end #{++_pullDowns} detected (CT {ct}, state {_state})");
            (GrantState next, bool consumed) = Step(_state, pullDown);
            if (consumed) { Release(ReleaseReason.Consumed); return; }
            if (next != _state) { _state = next; _deadline = now.AddSeconds(NoSignalSeconds); }
        }

        if (Slams(_state) && _base != 0 && Mem.Writable(_base + CtOff, 1)) Mem.W8(_base + CtOff, SlamCt);
        if (_dbg++ % 15 == 0) Log.Info($"extra-turn: {_state} -- wielder entry at 0x{_base:X}, CT {ct}, streak {_streak}, slam took={_took}");
    }

    private void Arm(DateTime now)
    {
        _state = GrantState.Arming; _base = 0; _prevCt = -1;
        _streak = 0; _took = false; _pullDowns = 0; _deadStreak = 0; _ambiguityLogged = false;
        _deadline = now.AddSeconds(NoSignalSeconds); _hardStop = now.AddSeconds(AbsoluteCapSeconds);
        Log.Info($"extra-turn: {LogNames.Weapon(ZwillId)} scored a kill -- arming extra-turn grant for the wielder (level {_wielder.lvl}, brave {_wielder.br}, faith {_wielder.fa})");
    }

    private void Release(ReleaseReason reason)
    {
        if (RestoreCt(reason) && _base != 0 && Mem.Writable(_base + CtOff, 1)) Mem.W8(_base + CtOff, 0);
        Log.Info($"extra-turn: grant ended ({reason}) from {_state} after {_pullDowns} turn-end(s) detected");
        _state = GrantState.Idle; _base = 0; _streak = 0; _took = false; _prevCt = -1; _deadStreak = 0;
        _ambiguityLogged = false;
    }

    private int ReadCt() => _base != 0 && Mem.Readable(_base + CtOff, 1) ? Mem.U8(_base + CtOff) : -1;
    private int ReadHp() => _base != 0 && Mem.Readable(_base + Offsets.AHp, 2) ? Mem.U16(_base + Offsets.AHp) : -1;

    /// <summary>The Zwill holder's roster fingerprint + hand item ids. False when no roster slot --
    /// or more than one (ambiguous) -- holds the Zwill in either hand.</summary>
    private bool ResolveWielder()
    {
        int found = 0;
        for (int r = 0; r < Offsets.RosterSlots; r++)
        {
            long rb = Offsets.RosterBase + (long)r * Offsets.RosterStride;
            if (!Mem.Readable(rb + Offsets.RNameId, 2)) continue;
            int lvl = Mem.U8(rb + Offsets.RLevel);
            if (lvl < 1 || lvl > 99) continue;
            int rh = Mem.U16(rb + Offsets.RRHand), lh = Mem.U16(rb + Offsets.RLHand), oh = Mem.U16(rb + Offsets.ROffHand);
            if (rh != ZwillId && lh != ZwillId && oh != ZwillId) continue;
            if (++found > 1) return false;          // two Zwill holders: ambiguous, no grant
            _wielder = (lvl, Mem.U8(rb + Offsets.RBrave), Mem.U8(rb + Offsets.RFaith));
            _hands.Clear();
            foreach (int id in new[] { rh, lh, oh })
                if (id != 0x00FF && id != 0xFFFF && !_hands.Contains(id)) _hands.Add(id);
        }
        return found == 1;
    }

    /// <summary>Per-tick Band walk for the wielder's LIVE entry: the weapon id must be one of their
    /// hands (the Zwill, or the main weapon when the Zwill rides the off-hand) AND brave/faith must
    /// match the roster. TWIN FILTER: a real-position match beats a (0,0) one (the frozen roster
    /// duplicate sits at (0,0) -- the exact tie that killed v7); among survivors an exact Zwill
    /// match outranks a hand match; a remaining tie means no slam (a miss beats slamming a stranger).
    /// Relocation needs no special chase: the walk re-finds the entry within one tick.</summary>
    private long Locate()
    {
        long match = 0, exact = 0;
        int matches = 0, exacts = 0;
        bool real = false;               // current candidates carry a real position
        var cands = new List<long>(2);   // tie candidates, for the once-per-grant ambiguity log
        for (int s = 0; s < Offsets.BandSlots; s++)
        {
            long e = Band.Entry(s);
            if (!Band.IsValid(BandMem, e)) continue;
            int wid = Mem.U16(e + EntryWeapon);
            if (!_hands.Contains(wid)) continue;
            if (Mem.U8(e + Offsets.ABrave) != _wielder.br || Mem.U8(e + Offsets.AFaith) != _wielder.fa) continue;
            bool realPos = Mem.U8(e + Offsets.AGx) != 0 || Mem.U8(e + Offsets.AGy) != 0;
            if (real && !realPos) continue;                                  // (0,0) twin loses to a live match
            if (realPos && !real)                                            // first live match: drop (0,0) ones
            {
                real = true; matches = 0; exacts = 0; match = 0; exact = 0; cands.Clear();
            }
            matches++; match = e; cands.Add(e);
            if (wid == ZwillId) { exacts++; exact = e; }
        }
        if (exacts == 1) return exact;
        if (matches == 1) return match;
        if (matches > 1 && !_ambiguityLogged)
        {
            _ambiguityLogged = true;     // once per grant: a surviving tie is diagnostic gold, not spam
            Log.Info($"extra-turn: wielder location is ambiguous ({matches} matching entries: " +
                     $"{string.Join(", ", cands.ConvertAll(c => "0x" + c.ToString("X")))}) -- turn gauge not slammed");
        }
        return 0;
    }
}
