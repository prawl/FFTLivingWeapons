namespace LivingWeapon;

using System;
using System.Collections.Generic;

/// <summary>
/// Counts completed turns this battle for timed signatures. Each rising edge of Offsets.Acted bumps
/// <see cref="GlobalTurns"/> (anyone's turn, so Larceny's buff expiry survives a parked wielder) and
/// credits the active unit, resolved by Band.FlagOwner (LW-94, band +0x19C ATurnFlag, 2026-07-09), then
/// Band.ActorEntry (2026-07-01, tools/probes/unitid_probe.py "watch"; the pointer parks on the action's
/// target at a caster's acted edge), then the turn-queue fingerprint match. The fallback bails on an
/// ambiguous match but can still credit the wrong unit when one matches unambiguously (F1).
/// Counts live in <see cref="WielderKeyedStore{TState}"/> (LW-252 stage 5. NameId first, fp fallback).
/// </summary>
internal sealed class TurnTracker
{
    private readonly IGameMemory _mem;
    private readonly WielderKeyedStore<Box<int>> _turnStore = new();
    private bool _wasActed;
    // Optional flight recorder tap; Engine wires it to Flight.Record.
    private readonly Action<string, string>? _recorder;

    public TurnTracker(IGameMemory mem, Action<string, string>? recorder = null)
    {
        _mem = mem;
        _recorder = recorder;
    }

    /// <summary>Turns taken by any unit this battle. Buff timers ride this clock because it never
    /// stalls on an unidentified or parked unit.</summary>
    public int GlobalTurns { get; private set; }

    /// <summary>Forget all turn counts. Call on battle enter and exit.</summary>
    public void ResetBattle()
    {
        _turnStore.Clear();
        _wasActed = false;
        GlobalTurns = 0;
    }

    /// <summary>Completed turns this battle for this fingerprint (0 if none). Delegates with nameId 0,
    /// the fp lane; an fp shared by 2+ seeded units returns 0. Callers with a real nameId should use
    /// <see cref="Turns(int,int,int,int)"/> so the query key matches the credit key.</summary>
    public int Turns(int level, int brave, int faith) => Turns(0, level, brave, faith);

    /// <summary>LW-252 stage 5. Lookup by <paramref name="nameId"/>, falling back to the fp lane when
    /// it is 0 or unresolved (see WielderKeyedStore). Returns 0 on any miss; Probe never creates, so
    /// this is a pure read.</summary>
    public int Turns(int nameId, int level, int brave, int faith)
        => _turnStore.Probe(nameId, (level, brave, faith))?.Value ?? 0;

    /// <summary>One tick. On the acted flag's rising edge, bump the global clock and credit a turn to
    /// the active unit if it can be identified.</summary>
    public void Poll()
    {
        bool acted = _mem.U8(Offsets.Acted) == 1;
        if (acted && !_wasActed)
        {
            GlobalTurns++;   // bumped before attribution, so it never depends on naming the actor
            _recorder?.Invoke("turn", $"acted rising edge -- global turn #{GlobalTurns}");
            bool viaFlags = TryActiveViaFlags(out var fp, out long entry);
            bool viaPointer = !viaFlags && TryActiveViaPointer(out fp, out entry);
            bool viaFallback = !viaFlags && !viaPointer && TryActiveFingerprint(out fp, out entry);
            if (viaFlags || viaPointer || viaFallback)
            {
                // LW-252 stage 5. NameId from the entry that resolved fp (0 if unreadable).
                // Settle only runs without a nameId; name-keyed credits never drift.
                int nameId = entry != 0 ? _mem.U16(entry + Offsets.ANameId) : 0;
                var key = nameId > 0 ? fp : Settle(fp);
                var state = _turnStore.GetOrCreate(nameId, key, () => new Box<int>());
                if (state != null)   // null = ambiguous nameId-0 twin, no credit this tick
                {
                    state.Value++;
                    int n = state.Value;
                    string source = viaFlags ? "the turn flags" : viaPointer ? "the actor pointer" : "the turn-queue fallback";
                    // Per-turn heartbeat is flight evidence only, so it stays off the console.
                    ModLogger.Debug(LogVerb.Turn,
                        $"a unit finished its turn (number {n} this battle) (identity level {key.Item1} brave {key.Item2} faith {key.Item3}, via {source})");
                    string src = viaFlags ? "turn-flags" : viaPointer ? "actor-ptr" : "tq-fallback";
                    _recorder?.Invoke("turn", $"credit level={key.Item1} brave={key.Item2} faith={key.Item3} count={n} src={src}");
                }
            }
        }
        else if (!acted && _wasActed)
        {
            _recorder?.Invoke("turn", "acted falling edge");
        }
        _wasActed = acted;
    }

    /// <summary>The turn-flags owner's (level,brave,faith) via Band.FlagOwner. Tried first
    /// because the actor pointer parks on the action's target. False when there is no owner or two
    /// disagree, then callers use <see cref="TryActiveViaPointer"/>. Also outs the entry address
    /// (LW-252 stage 5).</summary>
    private bool TryActiveViaFlags(out (int, int, int) fp, out long entry)
    {
        fp = default;
        if (!Band.FlagOwner(_mem, out entry, out _)) { entry = 0; return false; }
        fp = (_mem.U8(entry + Offsets.ALevel), _mem.U8(entry + Offsets.ABrave), _mem.U8(entry + Offsets.AFaith));
        return true;
    }

    /// <summary>LW-94 part 2. A mid-battle level-up drifts the band fingerprint (observed 36 to 37)
    /// while consumers such as Renewal.cs query the pre-battle roster fingerprint. If this key has
    /// no count but (level-1, brave, faith) does, the turn settles onto the old key. It can
    /// mis-settle a distinct fresh unit landing on the same key, an accepted rarity like the other
    /// fingerprint-keying caveats. Probes the fp lane (nameId 0) so name-keyed owners still count
    /// (LW-252 stage 5).</summary>
    private (int, int, int) Settle((int, int, int) fp)
    {
        var (lvl, br, fa) = fp;
        var priorLevelKey = (lvl - 1, br, fa);
        if (_turnStore.Probe(0, fp) == null && _turnStore.Probe(0, priorLevelKey) != null) return priorLevelKey;
        return fp;
    }

    /// <summary>The active unit's (level,brave,faith) via the engine's ActorPtr global
    /// (Band.ActorEntry). False when the pointer or entry is invalid, then callers use
    /// <see cref="TryActiveFingerprint"/>. Also outs the entry address (LW-252 stage 5).</summary>
    private bool TryActiveViaPointer(out (int, int, int) fp, out long entry)
    {
        fp = default;
        entry = Band.ActorEntry(_mem);
        if (entry == 0 || !Band.IsValid(_mem, entry)) { entry = 0; return false; }
        fp = (_mem.U8(entry + Offsets.ALevel), _mem.U8(entry + Offsets.ABrave), _mem.U8(entry + Offsets.AFaith));
        return true;
    }

    /// <summary>The turn-queue unit's (level,brave,faith) via Band.ActiveOwner, the band walk shared
    /// with FeignDeath, Puppeteer and Mushin (see it for the ambiguity bail and twin filter). Also
    /// outs the entry address (LW-252 stage 5).</summary>
    private bool TryActiveFingerprint(out (int, int, int) fp, out long entry)
        => Band.ActiveOwner(_mem, out fp, out entry);
}
