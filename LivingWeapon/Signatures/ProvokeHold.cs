using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Reads the mark that Provoke.cs plants (composed +0x45/0x80 and
/// inflicted +0x1D3/0x80, blank status id 0) on the targeted enemy. While the mark is up and a tier-3
/// main-hand Defender bearer is deployed and alive, every other player-side unit is hidden from the AI.
/// Hide is the default; a reveal needs a player-side turn or TurnOrder.cs's CT+Speed lookahead showing
/// the marked enemy is clearly further off (ProvokeHold.Policy.ActionFor). Gates
/// on the mark bit, not `meta[33].Signature`, so a real cast and the DEV planter (ProvokeSpike.cs,
/// LWDEV) behave alike. Split. This file is the state machine, .Scan.cs the band walks, .Policy.cs the
/// pure decisions and guarded bit writers. See docs/PROVOKE_AC.md.
/// </summary>
internal sealed partial class ProvokeHold : ISignature
{
    void ISignature.Tick(in TickContext ctx) => Tick(ctx.Now, ctx.InLive);

    /// <summary>The blank status id 0 Provoke.cs plants as its mark (ProvokePolicy's class doc).</summary>
    internal const int MarkId = 0;

    private enum HoldState { Idle, Armed }

    private readonly IGameMemory _mem;
    private readonly Dictionary<int, int> _kills;
    private readonly bool _enabled;
    private readonly int _provokeTurns;
    private readonly List<long> _hideScratch = new();
    /// <summary>Scratch list for TurnOrder.TryNextEnemyToAct. TurnOrder keeps no static buffer because
    /// the flight/logging paths run off the engine thread.</summary>
    private readonly List<(long e, int ct, int speed)> _turnOrderScratch = new();

    private HoldState _state;
    private (int nameId, int mhp, int lvl, int br, int fa) _markedId;
    /// <summary>true once the marked enemy became the actor by a rising edge after arming.
    /// Only then may a falling edge complete a turn, since the actor pointer at arm time is parked
    /// on the Provoke cast's target.</summary>
    private bool _sawFreshRise;
    /// <summary>Once the marked enemy is next to act, or acting after a fresh
    /// rise (<see cref="_sawFreshRise"/>), stay hidden until its turn completes. CT is paid at turn
    /// start, so TryEta briefly reads the marked enemy as far off and would reveal the party inside
    /// the AI's commit window. Set this tick, applied from the next (see TickArmed); cleared when
    /// <see cref="_markedTurns"/> increments and in EnterIdle.</summary>
    private bool _engaged;
    /// <summary>The marked enemy's last known tile (criterion 15). Kept because EnemyGone and
    /// EnemyDead leave no readable seat to re-read at release.</summary>
    private (int gx, int gy) _markedTile;
    private HideAction? _lastAction;   // the on-change latch behind LogHideEdge
    private bool _wasMarkedActive;
    private int _markedTurns;
    private int _markedMissTicks;
    private double _liveElapsed;
    private DateTime _lastTick;
    private readonly HashSet<(int, int, int, int, int)> _flaggedNow = new();
    private readonly HashSet<(int, int, int, int, int)> _everFlagged = new();

    /// <param name="provokeTurns">Overrides Tuning.ProvokeTurns; null uses the compiled default.</param>
    /// <param name="enabled">Overrides <see cref="Tuning.ProvokeEnabled"/>, the ship switch (re-armed
    /// by commit 43de63e); null uses the compiled default. ProvokeHoldTests builds every hold through
    /// its Hold(...) factory with true so the suite stays meaningful if the default flips off.</param>
    public ProvokeHold(Dictionary<int, int> kills, IGameMemory? mem = null,
        int? provokeTurns = null, bool? enabled = null)
    {
        _mem = mem ?? new LiveMemory();
        _kills = kills;
        _enabled = enabled ?? Tuning.ProvokeEnabled;
        _provokeTurns = provokeTurns ?? Tuning.ProvokeTurns;
    }

    /// <summary>Both battle edges. Reveal every identity ever flagged (ours only), scrub a live mark
    /// if armed, reset counters (AC 12/13). A hard process kill loses this, so it only covers
    /// same-process restarts.</summary>
    public void ResetBattle()
    {
        // Ship switch. Disabled means no writes, including these edge reveals and scrubs.
        if (!_enabled) return;
        foreach (var id in _everFlagged)
        {
            long e = LocateByIdentity(_mem, id);
            if (e != 0) SetInvisible(_mem, e, false);
        }
        if (_state == HoldState.Armed)
        {
            long markedEntry = LocateByIdentity(_mem, _markedId);
            if (markedEntry != 0) ClearMark(_mem, markedEntry);
        }
        ScrubPlayerSideMarks();   // a friendly mark stranded right before this edge too
        EnterIdle();
        _liveElapsed = 0;
        _flaggedNow.Clear();
        _everFlagged.Clear();
    }

    public void Tick(DateTime now, bool inLive)
    {
        // Ship switch, checked before any scan or write. See Tuning.ProvokeEnabled for why the
        // items.json disarm alone is not enough.
        if (!_enabled) return;
        if (!inLive) return;

        ScrubPlayerSideMarks();   // independent of hold state, an ally can be provoked anytime

        long bearerEntry = Wielder.ResolveDeployedMainHand(_mem, Provoke.DefenderId, out _);
        bool bearerPresent = bearerEntry != 0 && Tuning.TierOf(_kills, Provoke.DefenderId) >= 3;
        bool bearerAlive = bearerPresent && IsAlive(_mem, bearerEntry);
        var bearerId = bearerEntry != 0 ? ReadIdentity(_mem, bearerEntry) : default;

        if (_state == HoldState.Idle)
        {
            if (!FindMarkedEnemy(_mem, out long foundEntry, out var foundId)) { _unarmableTicks = 0; return; }
            if (!bearerPresent || !bearerAlive) { ScrubUnarmableMark(foundEntry); return; }
            _unarmableTicks = 0;
            Arm(foundId, foundEntry, now);
        }

        TickArmed(bearerPresent, bearerAlive, bearerId, now);
    }

    private void Arm((int nameId, int mhp, int lvl, int br, int fa) id, long entry, DateTime now)
    {
        _state = HoldState.Armed;
        _markedId = id;
        // Seed from current state, since the cast parked the actor pointer on this enemy
        // and seeding false would fake a rising edge.
        _wasMarkedActive = MarkedIsActor(_mem, id) && !PlayerSideOwnsTurn(_mem);
        _sawFreshRise = false;
        _markedTurns = 0;
        _markedMissTicks = 0;
        _liveElapsed = 0;
        _lastTick = now;
        _flaggedNow.Clear();
        _markedTile = ReadTile(_mem, entry);
        ModLogger.EventWithTrace(LogVerb.Signature,
            "The Defender's provoke takes hold; the marked enemy can see nothing but the Defender.",
            $"provoke hold arm (nameId {id.nameId}, tile {TileText}, 0 units hidden so far)");
        Flight.Record("provoke", $"arm nameId={id.nameId} tile={TileText}");
    }

    private void TickArmed(bool bearerPresent, bool bearerAlive,
        (int nameId, int mhp, int lvl, int br, int fa) bearerId, DateTime now)
    {
        long markedEntry = LocateByIdentity(_mem, _markedId);
        bool markedLocated = markedEntry != 0;
        if (markedLocated) _markedTile = ReadTile(_mem, markedEntry);   // criterion 15. Keep it fresh while we still can
        _markedMissTicks = markedLocated ? 0 : _markedMissTicks + 1;
        bool markedMissedOut = !markedLocated && _markedMissTicks >= Tuning.ProvokeMarkedMissTicks;
        bool markedDead = markedLocated && !IsAlive(_mem, markedEntry);
        bool markedDisabled = markedLocated && IsDisabled(_mem, markedEntry, Tuning.ProvokeDisablingStatusIds);

        bool paused = _mem.U8(Offsets.PauseFlag) == 1;
        _liveElapsed = AccrueWatchdog(_liveElapsed, (now - _lastTick).TotalSeconds, paused);
        _lastTick = now;
        bool watchdogElapsed = WatchdogElapsed(_liveElapsed, Tuning.ProvokeWatchdogSeconds);

        // The release gate and the hide/reveal share one read per tick. Does a player-side
        // seat own the per-unit turn flag (ProvokeHold.Scan.cs)? Needed every armed tick.
        bool playerTurn = PlayerSideOwnsTurn(_mem);
        bool markedActive = MarkedIsActor(_mem, _markedId) && !playerTurn;
        // A falling edge completes a turn only after a rise since arming; cleared on use so
        // each turn needs its own rise (ProvokeTurns > 1).
        if (!_wasMarkedActive && markedActive) _sawFreshRise = true;
        // The falling edge that completes a turn also clears the engagement latch.
        if (TurnEnded(_wasMarkedActive, markedActive) && _sawFreshRise) { _markedTurns++; _sawFreshRise = false; _engaged = false; }
        _wasMarkedActive = markedActive;

        var reason = ReleaseReason(bearerPresent, bearerAlive, markedLocated, markedDead, markedMissedOut,
            markedDisabled, _markedTurns, _provokeTurns, watchdogElapsed);
        if (reason != Release.None) { ReleaseHold(reason, markedEntry); return; }

        var look = GatherLookahead(markedEntry);
        // Decide with the latch as it stood before this tick's signals, so why=next/why=actor still
        // logs its real reason once; the latch overrides from the following tick (the cast 3 window).
        var (action, actionReason) = ActionForWithReason(_engaged, playerTurn, markedActive, look.markedIsNext, look.markedIsFarOff);
        if (look.markedIsNext || (markedActive && _sawFreshRise)) _engaged = true;

        var decision = (action, actionReason, look.lookaheadFired, look.leaderEta, look.markedEta, look.nextNameId);
        if (decision.action == HideAction.Hide) HideAllExceptBearer(bearerId); else RevealFlagged();
        LogHideEdge(decision);
    }

    private void HideAllExceptBearer((int nameId, int mhp, int lvl, int br, int fa) bearerId)
    {
        EnumerateHideTargets(_mem, bearerId, _hideScratch);
        foreach (long e in _hideScratch)
        {
            var id = ReadIdentity(_mem, e);
            if (HasInvisible(_mem, e) && !_flaggedNow.Contains(id)) continue;   // FeignDeath's, leave it
            if (SetInvisible(_mem, e, true)) { _flaggedNow.Add(id); _everFlagged.Add(id); }
            else ModLogger.WarnWithTrace(LogVerb.Signature, "Could not hide a unit for the provoke hold; the guarded write was refused",
                $"provoke hide refused (nameId {id.nameId})");
        }
        SuppressIcon(true, _flaggedNow.Count);
    }

    private void RevealFlagged()
    {
        foreach (var id in _flaggedNow)
        {
            long e = LocateByIdentity(_mem, id);
            if (e != 0) SetInvisible(_mem, e, false);
        }
        _flaggedNow.Clear();
        SuppressIcon(false, 0);
    }

    private void ReleaseHold(Release reason, long markedEntry)
    {
        RevealFlagged();
        if (markedEntry != 0 && !ClearMark(_mem, markedEntry))
            ModLogger.WarnWithTrace(LogVerb.Signature, "Could not scrub the provoke mark on release; the guarded write was refused",
                $"provoke mark-clear refused (reason {reason})");

        if (reason == Release.Watchdog)
            ModLogger.WarnWithTrace(LogVerb.Signature, "The provoke hold timed out and released on its own; something kept the marked enemy from ever finishing its turn",
                $"provoke hold WATCHDOG release (tile {TileText}, {_everFlagged.Count} units were ever hidden)");
        else
            ModLogger.EventWithTrace(LogVerb.Signature, $"The provoke hold ends ({reason}); every hidden unit is visible again.",
                $"provoke hold release reason={reason} (tile {TileText}, {_everFlagged.Count} units were ever hidden)");
        Flight.Record("provoke", $"release reason={reason} tile={TileText}");
        EnterIdle();
    }

    /// <summary>The tile as both log lines and both flight records print it (criterion 15).</summary>
    private string TileText => $"{_markedTile.gx},{_markedTile.gy}";

    /// <summary>Logs one line per hide/reveal transition via the <see cref="_lastAction"/> latch.
    /// The 2026-07-27 tape could not tell "hid nobody" from "revealed all along". The line
    /// carries which branch decided (<paramref name="d"/>.reason, see ActionForWithReason) and, if
    /// the lookahead ran, both ETAs and the next enemy's nameId; otherwise `lookahead=fallback`.
    /// A null markedEta (TryEta refused, e.g. an off-field or hp&gt;maxHp seat) prints `markedEta=?`
    /// because a real 0 means due now.</summary>
    private void LogHideEdge((HideAction action, string reason, bool lookaheadFired, int leaderEta,
        int? markedEta, int nextNameId) d)
    {
        if (_lastAction == d.action) return;
        _lastAction = d.action;
        string markedEtaText = d.markedEta.HasValue ? d.markedEta.Value.ToString() : "?";
        string evidence = d.lookaheadFired
            ? $"why={d.reason} leaderEta={d.leaderEta} nextNameId={d.nextNameId} markedEta={markedEtaText}"
            : $"why={d.reason} lookahead=fallback";
        ModLogger.Debug(LogVerb.Signature, d.action == HideAction.Hide
            ? $"provoke hide: {_flaggedNow.Count} unit(s) now hidden from the marked enemy ({evidence})"
            : $"provoke reveal: every unit the hold hid is visible again ({evidence})");
        // Flight vocabulary is frozen, so this reuses the "provoke" record type.
        Flight.Record("provoke", $"{(d.action == HideAction.Hide ? "hide" : "reveal")}-edge {evidence}");
    }

    private void EnterIdle()
    {
        _state = HoldState.Idle;
        _markedId = default;
        _markedTile = default;
        _unarmableTicks = 0;   // no partial run across a release or battle edge
        _lastAction = null;    // the next hold logs its own first hide/reveal edge
        _wasMarkedActive = false;
        _markedTurns = 0;
        _markedMissTicks = 0;
        _engaged = false;   // no stale latch into the next arm
    }

    /// <summary>Placeholder (AC criterion 18) for hiding the invisible icon without the AI-ignore
    /// effect. The flag and icon share one bit (band +0x47/0x10) and the Phase 5 icon probe has not
    /// found a separate lever. Called on every hide/reveal transition.</summary>
    private void SuppressIcon(bool on, int hiddenCount) { }
}
