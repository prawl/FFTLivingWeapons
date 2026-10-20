using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace LivingWeapon;

/// <summary>
/// How many of each new weapon the party owns, counting the bag and every hand that holds one.
/// A rewind puts the roster and the game's bag back from two different moments, so after one the bag is set to
/// the total minus what the hands hold now. Only the right and off hand of the fifty roster slots are counted,
/// so a weapon stolen or broken in battle drops out of the total at the next capture.
/// </summary>
internal sealed class RewindTotals
{
    private readonly ICodePatcher _mem;
    private readonly IReadOnlyList<int> _ids;
    private readonly long _bagBase, _rosterBase;
    private readonly int _slots, _stride;
    private readonly Func<int, string> _nameOf;
    private readonly object _gate = new();
    private IReadOnlyDictionary<int, int>? _latest;
    private string? _latestSource;
    private long _captures, _recomputes, _corrections, _refused, _incomplete;

    public IReadOnlyDictionary<int, int>? Latest { get { lock (_gate) return _latest; } }
    public string? LatestSource { get { lock (_gate) return _latestSource; } }
    public long Captures => Interlocked.Read(ref _captures);
    public long Recomputes => Interlocked.Read(ref _recomputes);
    public long Corrections => Interlocked.Read(ref _corrections);
    public long Refused => Interlocked.Read(ref _refused);
    public long Incomplete => Interlocked.Read(ref _incomplete);
    public string? LastReason { get; private set; }

    public RewindTotals(ICodePatcher mem, IReadOnlyList<int> extendedIds, long bagBase, long rosterBase = 0,
        int slots = Offsets.RosterSlots, int stride = Offsets.RosterStride, Func<int, string>? nameOf = null)
    {
        _mem = mem;
        _ids = extendedIds;
        _bagBase = bagBase == 0 ? Offsets.BagCountArray : bagBase;
        _rosterBase = rosterBase == 0 ? Offsets.RosterBase : rosterBase;   // a rebased address cannot be a default parameter
        _slots = slots;
        _stride = stride;
        _nameOf = nameOf ?? (id => id.ToString());
    }

    /// <summary>Null when any roster row cannot be read, because a short count would make the bag hand out weapons nobody owns.</summary>
    public IReadOnlyDictionary<int, int>? ReadHands()
    {
        var hands = _ids.ToDictionary(id => id, _ => 0);
        for (int slot = 0; slot < _slots; slot++)
        {
            if (!_mem.TryRead(_rosterBase + (long)slot * _stride + Offsets.RRHand, Offsets.ROffHand - Offsets.RRHand + 2, out var row)) return null;
            foreach (int at in new[] { 0, Offsets.ROffHand - Offsets.RRHand })
            {
                int id = BitConverter.ToUInt16(row, at);
                if (hands.ContainsKey(id)) hands[id]++;
            }
        }
        return hands;
    }

    /// <summary>Null when any byte cannot be read, for the same reason.</summary>
    public IReadOnlyDictionary<int, int>? ReadBag()
    {
        var bag = new Dictionary<int, int>();
        foreach (int id in _ids)
        {
            if (!_mem.TryRead(_bagBase + id, 1, out var b)) return null;
            bag[id] = b[0];
        }
        return bag;
    }

    /// <summary>False when a read failed, and then the last good total stands.</summary>
    public bool Capture(string source)
    {
        var bag = ReadBag();
        var hands = ReadHands();
        if (bag == null || hands == null) { Interlocked.Increment(ref _incomplete); return false; }
        var total = _ids.ToDictionary(id => id, id => bag[id] + hands[id]);
        lock (_gate) { _latest = total; _latestSource = source; }
        Interlocked.Increment(ref _captures);
        return true;
    }

    /// <summary>The totals a Retry checkpoint saved become the current ones even when the hands cannot be read, so the next recompute still has them.</summary>
    public IReadOnlyDictionary<int, int> PutBack(IReadOnlyDictionary<int, int> totals, string source)
    {
        lock (_gate) { _latest = totals; _latestSource = source; }
        Interlocked.Increment(ref _captures);
        var hands = ReadHands();
        if (hands == null) { Interlocked.Increment(ref _incomplete); return new Dictionary<int, int>(); }
        var wrote = new Dictionary<int, int>();
        foreach (int id in _ids)
        {
            int n = TotalsPolicy.BagFor(totals.TryGetValue(id, out int t) ? t : 0, hands[id]);
            if (_mem.TryWrite(_bagBase + id, new[] { (byte)n })) wrote[id] = n;
            else Interlocked.Increment(ref _refused);
        }
        return wrote;
    }

    /// <summary>Only bytes that are wrong get written, so a quiet recompute leaves no trace in memory or the log.</summary>
    public bool Recompute(string reason, out string change)
    {
        change = "";
        try
        {
            var latest = Latest;
            if (latest == null) return false;
            Interlocked.Increment(ref _recomputes);
            LastReason = reason;
            var hands = ReadHands();
            var bag = ReadBag();
            if (hands == null || bag == null)
            {
                Interlocked.Increment(ref _incomplete);
                change = "a roster or bag read failed";
                return false;
            }
            var parts = new List<string>();
            foreach (int id in _ids)
            {
                int want = TotalsPolicy.BagFor(latest.TryGetValue(id, out int t) ? t : 0, hands[id]);
                if (want == bag[id]) continue;
                if (_mem.TryWrite(_bagBase + id, new[] { (byte)want })) parts.Add($"{_nameOf(id)} {bag[id]} to {want}");
                else { Interlocked.Increment(ref _refused); parts.Add($"{_nameOf(id)} write refused"); }
            }
            change = string.Join(", ", parts);
            bool wrote = parts.Any(p => !p.EndsWith("refused"));
            if (wrote) Interlocked.Increment(ref _corrections);
            return wrote;
        }
        catch (Exception) { return false; }   // a throw here would stop every later step in the tick
    }
}
