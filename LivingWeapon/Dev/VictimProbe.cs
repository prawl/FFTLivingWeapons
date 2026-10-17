using System;

namespace LivingWeapon;

/// <summary>
/// Reliquary P1 probe (docs/RELIQUARY_AC.md Phase 0, "P1 -- victim snapshot integrity"). Log-only; KillTracker calls it to observe.
/// Captures a corpse's nameId (Offsets.ANameId), job byte (Offsets.AJob) and undead bit
/// (Offsets.ADeadStatus &amp; Offsets.AUndeadBit) at three points so a live run can compare them:
///   (a) alive. Every consistent alive on-field tick (KillTracker.Corpses.cs ScanCorpses).
///   (b) edge. The tick a slot's dead-streak first starts (deadStreak == 1).
///   (c) credit. A fresh read at CreditKill time (KillTracker.cs), logged beside the other two.
/// </summary>
internal sealed class VictimProbe
{
    private readonly IGameMemory _mem;
    private readonly Action<string, string>? _recorder;

    private readonly VictimSnapshot[] _alive = new VictimSnapshot[Offsets.BandSlots];
    private readonly VictimSnapshot[] _edge = new VictimSnapshot[Offsets.BandSlots];

    public VictimProbe(IGameMemory mem, Action<string, string>? recorder)
    {
        _mem = mem;
        _recorder = recorder;
    }

    /// <summary>Alive-point capture for band slot s at address addr, from ScanCorpses each consistent alive tick.</summary>
    internal void CaptureAlive(int s, long addr) => _alive[s] = Read(addr);

    /// <summary>Dead-edge capture for band slot s at address addr, from ScanCorpses when deadStreak == 1.</summary>
    internal void CaptureDeadEdge(int s, long addr) => _edge[s] = Read(addr);

    /// <summary>Credit-point capture and log. Takes a fresh read at CreditKill time (the engine may have recycled the slot
    /// since the edge tick, which is the question), logs all three points as matching lines via ModLogger.Debug and
    /// fires one recorder tap carrying all three tuples.</summary>
    internal void LogAtCredit(int s)
    {
        VictimSnapshot credit = VictimReader.Read(_mem, Band.Entry(s));
        VictimSnapshot alive = _alive[s];
        VictimSnapshot edge = _edge[s];

        LogLine(s, "alive", alive);
        LogLine(s, "edge", edge);
        LogLine(s, "credit", credit);

        _recorder?.Invoke("victim",
            $"slot={s} alive={Tuple(alive)} edge={Tuple(edge)} credit={Tuple(credit)}");
    }

    private static void LogLine(int s, string point, VictimSnapshot snap)
        => ModLogger.Debug(LogVerb.Trace, $"victim-probe: slot={s} point={point} nameId={snap.NameId} job={snap.Job} undead={(snap.Undead ? 1 : 0)} has={(snap.Has ? 1 : 0)}");

    private static string Tuple(VictimSnapshot snap)
        => $"(nameId={snap.NameId},job={snap.Job},undead={(snap.Undead ? 1 : 0)},has={(snap.Has ? 1 : 0)})";

    /// <summary>Guarded three-field read via the shared <see cref="VictimReader"/>, so this probe and
    /// KillTracker.Corpses.cs's Phase 1 capture agree on what a sane read is.</summary>
    private VictimSnapshot Read(long addr) => VictimReader.Read(_mem, addr);

    internal VictimSnapshot AliveSnapshot(int s) => _alive[s];
    internal VictimSnapshot EdgeSnapshot(int s) => _edge[s];

    /// <summary>Clear both snapshots for one slot on identity change or revive.</summary>
    internal void Reset(int s)
    {
        _alive[s] = default;
        _edge[s] = default;
    }

    /// <summary>Clear every slot. Called from KillTracker.ResetBattleCorpses on battle enter/exit.</summary>
    internal void ResetBattle()
    {
        Array.Clear(_alive, 0, _alive.Length);
        Array.Clear(_edge, 0, _edge.Length);
    }
}
