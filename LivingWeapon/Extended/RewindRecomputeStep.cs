using System;
using System.Linq;

namespace LivingWeapon;

/// <summary>
/// The tick half of the rewind fix. A restore counter rising schedules two recompute passes, because the game's own
/// roster revert can land a few milliseconds after our write. On both Retry roads the game's inventory reset runs about
/// 20 ms before the checkpoint restore and the roster revert lands a few ms after it, measured on the night of October 18. Counters are read as functions so the hooks may arrive late.
/// </summary>
internal sealed class RewindRecomputeStep
{
    private readonly RewindTotals _totals;
    private readonly (string Name, Func<long> Read)[] _sources;
    private readonly long[] _last;
    private readonly Action<string, string> _record;
    private readonly RecomputeScheduler _scheduler = new();
    private string _reason = "";

    public RewindRecomputeStep(RewindTotals totals, Action<string, string>? record, params (string Name, Func<long> Read)[] sources)
    {
        _totals = totals;
        _record = record ?? Flight.Record;
        _sources = sources;
        _last = sources.Select(s => s.Read()).ToArray();
    }

    public void Step(long tick)
    {
        try
        {
            var risen = Enumerable.Range(0, _sources.Length).Where(i => _sources[i].Read() > _last[i]).ToList();
            foreach (int i in risen) _last[i] = _sources[i].Read();
            if (risen.Count > 0)
            {
                _reason = string.Join(" and ", risen.Select(i => _sources[i].Name));
                _scheduler.Schedule(tick);
            }
            if (!_scheduler.Due(tick)) return;
            long refusedBefore = _totals.Refused;
            bool wrote = _totals.Recompute(_reason, out string change);
            bool refused = _totals.Refused > refusedBefore;
            if (refused) ModLogger.Warn(LogVerb.Save, "The new weapons' counts could not be re derived after the rewind (write refused); they may read one off until the next save loads.");
            if (wrote) ModLogger.Event(LogVerb.Save, $"After the battle was rewound the new weapons' counts were re derived from what the party holds ({change}).");
            if (refused || wrote || change.Length > 0) _record("rewind-recompute", change);
        }
        catch (Exception) { /* a fault here must never reach the tick */ }
    }
}
