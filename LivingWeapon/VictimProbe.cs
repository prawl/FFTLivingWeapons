using System;

namespace LivingWeapon;

/// <summary>
/// Reliquary P1 probe instrumentation (docs/RELIQUARY_AC.md's Phase 0, "P1 -- victim snapshot
/// integrity"). Log-only: zero behavioral effect on kill crediting, pending, oracle, or streak
/// logic -- KillTracker calls into this purely to observe.
///
/// Captures a corpse's identity -- nameId (Offsets.ANameId), job byte (Puppeteer.JobOff), and the
/// undead bit (Offsets.ADeadStatus &amp; Offsets.AUndeadBit) -- at three lifecycle points so a later
/// live run can compare them and answer which point reads sane victim identity on a corpse:
///   (a) ALIVE  -- every consistent alive on-field tick (KillTracker.Corpses.cs's ScanCorpses).
///   (b) EDGE   -- the tick a slot's dead-streak first starts (deadStreak == 1).
///   (c) CREDIT -- a FRESH read taken at CreditKill time (KillTracker.cs), logged alongside the
///       alive/edge snapshots already on file so a human can diff all three in one place.
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

    /// <summary>Alive-point capture for band slot s at band-entry address addr. Called from
    /// ScanCorpses on every consistent alive on-field tick (KillTracker.Corpses.cs).</summary>
    internal void CaptureAlive(int s, long addr) => _alive[s] = Read(addr);

    /// <summary>Dead-edge capture for band slot s at band-entry address addr. Called from
    /// ScanCorpses when a slot's dead-streak first starts (deadStreak == 1).</summary>
    internal void CaptureDeadEdge(int s, long addr) => _edge[s] = Read(addr);

    /// <summary>Credit-point capture + log. Takes a FRESH read at CreditKill time (the slot may
    /// have been reused/recycled by the engine since the edge tick -- that is exactly the question
    /// P1 is asking), then logs all three capture points as structurally identical lines through
    /// ModLogger.Debug (so a human can diff them) and fires ONE recorder tap carrying all three
    /// tuples together.</summary>
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

    /// <summary>Guarded three-field read -- delegates to the shared <see cref="VictimReader"/>
    /// (extracted from this method; see its doc comment for the guard rationale) so this probe's
    /// log-only capture and KillTracker.Corpses.cs's Phase 1 behavioral capture never disagree on
    /// what "a sane read" means.</summary>
    private VictimSnapshot Read(long addr) => VictimReader.Read(_mem, addr);

    internal VictimSnapshot AliveSnapshot(int s) => _alive[s];
    internal VictimSnapshot EdgeSnapshot(int s) => _edge[s];

    /// <summary>Clear both snapshots for one slot -- called on identity change (a new unit reused
    /// the slot) and on revive (the old death's snapshot is stale once the victim is alive again).</summary>
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
