namespace LivingWeapon;

using System;
using System.Collections.Generic;

/// <summary>
/// Counts completed turns in the current battle for TIMED signatures. On every rising edge of the
/// global "acted" flag (Offsets.Acted) it advances TWO clocks:
///   * <see cref="GlobalTurns"/> -- the attribution-FREE count of turns taken by ANYONE. Bumped on
///     the edge whether or not the actor can be identified, so a buff timer riding it (Larceny's
///     stolen-buff expiry) is immune to a parked wielder -- the world's turns keep coming even when
///     the held unit's own turn never does.
///   * the PER-UNIT count -- credits one turn to the ACTIVE unit (e.g. Galewind's Speed +3 for the
///     wielder's first 3 turns), resolved POINTER-FIRST: the engine's own ActorPtr global names the
///     acting unit's band entry directly (Band.ActorEntry), live-proven 2026-07-01
///     (tools/probes/unitid_probe.py "watch") to name the right seat at the exact instant the old
///     turn-queue fingerprint was ambiguous. When the pointer is invalid (0 / unaligned / out of
///     range / entry fails Band.IsValid) this falls back to the prior path: turn-queue
///     HP/MaxHP/level -> BAND entry -> (level,brave,faith) fingerprint.
///
/// Fallback ambiguity bail: if multiple BAND entries match the turn-queue HP/MaxHP/level but have
/// DIFFERENT (level,brave,faith) fingerprints, no PER-UNIT turn is credited (miss beats mis-credit).
/// GlobalTurns still advances -- it never needs to know who acted. Same-fingerprint multiples (twin)
/// are fine -- both entries resolve to the same unit. HONEST CAVEAT (F1): the fallback is NOT
/// "never mis-credit" -- it bails only on an AMBIGUOUS match; a TQ tuple that unambiguously matches
/// the WRONG unit IS credited (this condensed struct has separately been observed tracking the
/// CURSOR rather than the turn owner in some frames -- a distinct, pre-existing exposure of this
/// same struct). That is exactly today's status-quo exposure, so retaining the fallback is
/// strictly-no-worse than before this fix -- it only ever runs when the pointer itself is
/// unresolvable.
///
/// Memory access is injected (IGameMemory) so the counting is unit-testable with no live game.
/// </summary>
internal sealed class TurnTracker
{
    private readonly IGameMemory _mem;
    private readonly Dictionary<(int level, int brave, int faith), int> _turns = new();
    private bool _wasActed;
    // Flight recorder tap (optional; null/no-op default keeps every existing test green
    // unmodified). Engine wires this to Flight.Record -- see the injected-BattleLog-sink
    // precedent in KillTracker's ctor for the same shape.
    private readonly Action<string, string>? _recorder;

    public TurnTracker(IGameMemory mem, Action<string, string>? recorder = null)
    {
        _mem = mem;
        _recorder = recorder;
    }

    /// <summary>Turns taken by ANY unit this battle -- the attribution-free clock buff timers ride
    /// (it never stalls on a unit we can't fingerprint, nor on one the player parks).</summary>
    public int GlobalTurns { get; private set; }

    /// <summary>Forget all turn counts. Call on battle enter and exit.</summary>
    public void ResetBattle()
    {
        _turns.Clear();
        _wasActed = false;
        GlobalTurns = 0;
    }

    /// <summary>Completed turns this battle for the unit with this fingerprint (0 if none).</summary>
    public int Turns(int level, int brave, int faith) =>
        _turns.TryGetValue((level, brave, faith), out int t) ? t : 0;

    /// <summary>One tick. On the rising edge of the acted flag, advance the global clock (always) and
    /// credit a turn to the active unit (when it can be fingerprinted).</summary>
    public void Poll()
    {
        bool acted = _mem.U8(Offsets.Acted) == 1;
        if (acted && !_wasActed)
        {
            GlobalTurns++;   // SOMEONE acted -- bump the attribution-free clock before we try to name them
            // (No per-turn log here: this clock fires every turn of every battle and is shared infra, so a
            // line naming Larceny read as "Larceny spam" even with no Arcanum fielded. The per-unit turn
            // line below is generic and only logs when an actor is identified.)
            _recorder?.Invoke("turn", $"acted rising edge -- global turn #{GlobalTurns}");
            bool viaPointer = TryActiveViaPointer(out var fp);
            if (viaPointer || TryActiveFingerprint(out fp))
            {
                int n = (_turns.TryGetValue(fp, out int t) ? t : 0) + 1;
                _turns[fp] = n;
                // Facelift: the per-turn heartbeat is black-box evidence, never match report;
                // demoted off the console (the biggest console-ceiling violation in the audit).
                ModLogger.Debug(LogVerb.Turn,
                    $"a unit finished its turn (number {n} this battle) (identity level {fp.Item1} brave {fp.Item2} faith {fp.Item3}, via {(viaPointer ? "the actor pointer" : "the turn-queue fallback")})");
                _recorder?.Invoke("turn", $"credit level={fp.Item1} brave={fp.Item2} faith={fp.Item3} count={n} src={(viaPointer ? "actor-ptr" : "tq-fallback")}");
            }
        }
        else if (!acted && _wasActed)
        {
            _recorder?.Invoke("turn", "acted falling edge");
        }
        _wasActed = acted;
    }

    /// <summary>The active unit's (level,brave,faith) via the engine's own ActorPtr global
    /// (Band.ActorEntry). Returns false when the pointer is invalid or the pointed-to entry
    /// fails Band.IsValid -- callers fall back to <see cref="TryActiveFingerprint"/>.</summary>
    private bool TryActiveViaPointer(out (int, int, int) fp)
    {
        fp = default;
        long addr = Band.ActorEntry(_mem);
        if (addr == 0 || !Band.IsValid(_mem, addr)) return false;
        fp = (_mem.U8(addr + Offsets.ALevel), _mem.U8(addr + Offsets.ABrave), _mem.U8(addr + Offsets.AFaith));
        return true;
    }

    /// <summary>The active (turn-queue) unit's (level,brave,faith), delegated to Band.ActiveOwner
    /// (the shared band walk every own-turn-detecting signature now uses: FeignDeath, Puppeteer,
    /// Mushin, and this tracker). See its doc comment for the ambiguity-bail and twin-filter
    /// contract; this pure extraction changed no behavior (every existing test above stays
    /// green, proving parity).</summary>
    private bool TryActiveFingerprint(out (int, int, int) fp)
        => Band.ActiveOwner(_mem, out fp, out _);
}
