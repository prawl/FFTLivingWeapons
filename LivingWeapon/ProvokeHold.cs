using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// LW-123 arc 2a: the runtime half that READS arc 1's mark. Arc 1 (Provoke.cs) grants the Defender's
/// "Provoke" command, planting an inert receipt (composed +0x45/0x80 and inflicted +0x1D3/0x80, the
/// blank status id 0) on the targeted enemy -- nothing reads it yet. This module polls the band for
/// that mark: while up on an enemy, with a tier-3 main-hand Defender bearer deployed and alive, the
/// hold hides every other player-side unit from the AI (SLICE mode: only during the marked enemy's
/// own turn, the clean facade -- Tuning.ProvokeSliceMode) so the AI's only visible target is the
/// bearer. Gates on the mark bit itself, never `meta[33].Signature` (arc 2b's data plumbing), so this
/// same code runs whether the mark came from a real cast or the DEV planter (ProvokeSpike.cs, LWDEV).
///
/// Split across three partials, a real seam: this file is the state machine; ProvokeHold.Scan.cs is
/// every band walk; ProvokeHold.Policy.cs is the pure decisions plus guarded bit writers, unit-tested
/// directly. See docs/PROVOKE_AC.md for the full premise ledger and the SLICE/WINDOW trade-off.
/// </summary>
internal sealed partial class ProvokeHold : ISignature
{
    void ISignature.Tick(in TickContext ctx) => Tick(ctx.Now, ctx.InLive);

    /// <summary>The blank status id 0 arc 1 plants as its mark (ProvokePolicy's class doc).</summary>
    internal const int MarkId = 0;

    private enum HoldState { Idle, Armed }

    private readonly IGameMemory _mem;
    private readonly Dictionary<int, int> _kills;
    private readonly bool _sliceMode;
    private readonly int _provokeTurns;
    private readonly List<long> _hideScratch = new();

    private HoldState _state;
    private (int nameId, int mhp, int lvl, int br, int fa) _markedId;
    private bool _wasMarkedActive;
    private int _markedTurns;
    private int _markedMissTicks;
    private double _liveElapsed;
    private DateTime _lastTick;
    private readonly HashSet<(int, int, int, int, int)> _flaggedNow = new();
    private readonly HashSet<(int, int, int, int, int)> _everFlagged = new();

    /// <param name="sliceMode">Overrides Tuning.ProvokeSliceMode; null uses the compiled default (the
    /// Engine.bannerToasts ctor-seam precedent) -- isolates the WINDOW fallback for tests.</param>
    /// <param name="provokeTurns">Overrides Tuning.ProvokeTurns; null uses the compiled default --
    /// isolates "still armed after one turn ends" from the release value 1 triggers immediately.</param>
    public ProvokeHold(Dictionary<int, int> kills, IGameMemory? mem = null, bool? sliceMode = null, int? provokeTurns = null)
    {
        _mem = mem ?? new LiveMemory();
        _kills = kills;
        _sliceMode = sliceMode ?? Tuning.ProvokeSliceMode;
        _provokeTurns = provokeTurns ?? Tuning.ProvokeTurns;
    }

    /// <summary>Both battle edges (enter + exit): reveal every identity ever flagged (ours only),
    /// scrub a still-live mark if armed, reset every counter -- AC 12/13. A hard PROCESS kill loses
    /// this (the flight recorder's "loses the ring" loss mode); protects same-process restarts only.</summary>
    public void ResetBattle()
    {
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
        ScrubPlayerSideMarks();   // LW-130: a friendly mark stranded right before this edge too
        EnterIdle();
        _liveElapsed = 0;
        _flaggedNow.Clear();
        _everFlagged.Clear();
    }

    public void Tick(DateTime now, bool inLive)
    {
        if (!inLive) return;

        ScrubPlayerSideMarks();   // LW-130: independent of hold state, an ally can be provoked anytime

        long bearerEntry = Wielder.ResolveDeployedMainHand(_mem, Provoke.DefenderId, out _);
        bool bearerPresent = bearerEntry != 0 && Tuning.TierOf(_kills, Provoke.DefenderId) >= 3;
        bool bearerAlive = bearerPresent && IsAlive(_mem, bearerEntry);
        var bearerId = bearerEntry != 0 ? ReadIdentity(_mem, bearerEntry) : default;

        if (_state == HoldState.Idle)
        {
            if (!FindMarkedEnemy(_mem, out long foundEntry, out var foundId) || !bearerPresent || !bearerAlive) return;
            Arm(foundId, foundEntry, now);
        }

        TickArmed(bearerPresent, bearerAlive, bearerId, now);
    }

    private void Arm((int nameId, int mhp, int lvl, int br, int fa) id, long entry, DateTime now)
    {
        _state = HoldState.Armed;
        _markedId = id;
        _wasMarkedActive = false;
        _markedTurns = 0;
        _markedMissTicks = 0;
        _liveElapsed = 0;
        _lastTick = now;
        _flaggedNow.Clear();
        int gx = _mem.U8(entry + Offsets.AGx), gy = _mem.U8(entry + Offsets.AGy);
        ModLogger.EventWithTrace(LogVerb.Signature,
            "The Defender's provoke takes hold; the marked enemy can see nothing but the Defender.",
            $"provoke hold arm (nameId {id.nameId}, tile {gx},{gy}, 0 units hidden so far)");
        Flight.Record("provoke", $"arm nameId={id.nameId} tile={gx},{gy}");
    }

    private void TickArmed(bool bearerPresent, bool bearerAlive,
        (int nameId, int mhp, int lvl, int br, int fa) bearerId, DateTime now)
    {
        long markedEntry = LocateByIdentity(_mem, _markedId);
        bool markedLocated = markedEntry != 0;
        _markedMissTicks = markedLocated ? 0 : _markedMissTicks + 1;
        bool markedMissedOut = !markedLocated && _markedMissTicks >= Tuning.ProvokeMarkedMissTicks;
        bool markedDead = markedLocated && !IsAlive(_mem, markedEntry);
        bool markedDisabled = markedLocated && IsDisabled(_mem, markedEntry, Tuning.ProvokeDisablingStatusIds);

        bool paused = _mem.U8(Offsets.PauseFlag) == 1;
        _liveElapsed = AccrueWatchdog(_liveElapsed, (now - _lastTick).TotalSeconds, paused);
        _lastTick = now;
        bool watchdogElapsed = WatchdogElapsed(_liveElapsed, Tuning.ProvokeWatchdogSeconds);

        // ACTOR-POINTER identity match gated on TqTeam==1 (an enemy turn): replaces the flaky
        // ATurnFlag read -- see MarkedIsActor's doc comment (ProvokeHold.Scan.cs) for why.
        bool enemyTurn = _mem.U16(Offsets.TurnQueue + Offsets.TqTeam) == 1;
        bool markedActive = enemyTurn && MarkedIsActor(_mem, _markedId);
        if (TurnEnded(_wasMarkedActive, markedActive)) _markedTurns++;
        _wasMarkedActive = markedActive;

        var reason = ReleaseReason(bearerPresent, bearerAlive, markedLocated, markedDead, markedMissedOut,
            markedDisabled, _markedTurns, _provokeTurns, watchdogElapsed);
        if (reason != Release.None) { ReleaseHold(reason, markedEntry); return; }

        HideAction action = _sliceMode ? (markedActive ? HideAction.Hide : HideAction.Reveal) : WindowAction();
        if (action == HideAction.Hide) HideAllExceptBearer(bearerId); else RevealFlagged();
    }

    private void HideAllExceptBearer((int nameId, int mhp, int lvl, int br, int fa) bearerId)
    {
        EnumerateHideTargets(_mem, bearerId, _hideScratch);
        foreach (long e in _hideScratch)
        {
            var id = ReadIdentity(_mem, e);
            if (HasInvisible(_mem, e) && !_flaggedNow.Contains(id)) continue;   // FeignDeath's -- never touch it
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
                $"provoke hold WATCHDOG release ({_everFlagged.Count} units were ever hidden)");
        else
            ModLogger.EventWithTrace(LogVerb.Signature, $"The provoke hold ends ({reason}); every hidden unit is visible again.",
                $"provoke hold release reason={reason} ({_everFlagged.Count} units were ever hidden)");
        Flight.Record("provoke", $"release reason={reason}");
        EnterIdle();
    }

    private void EnterIdle()
    {
        _state = HoldState.Idle;
        _markedId = default;
        _wasMarkedActive = false;
        _markedTurns = 0;
        _markedMissTicks = 0;
    }

    /// <summary>SEAM (R3, criterion 18): suppress the invisible ICON without the AI-ignore effect.
    /// NO-OP placeholder -- the flag and its icon are the SAME bit (band +0x47/0x10), so this needs a
    /// separate lever the Phase 5 icon probe has not identified yet. Called on every hide/reveal
    /// transition so wiring the real lever later is a one-function change.</summary>
    private void SuppressIcon(bool on, int hiddenCount) { }
}
