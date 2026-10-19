using System.Collections.Generic;
using System.Linq;

namespace LivingWeapon;

internal enum RewindMode { Store, Restore, Other }

internal static class RewindPolicy
{
    /// <summary>The checkpoint routine's mode argument. Only 0 and 1 move the bag, the rest are left alone.</summary>
    public static RewindMode ModeOf(int edx) => edx switch { 0 => RewindMode.Store, 1 => RewindMode.Restore, _ => RewindMode.Other };

    /// <summary>"261 x1, 262 x0" in id order. The hook has no names.</summary>
    public static string Describe(IReadOnlyDictionary<int, int> counts)
        => string.Join(", ", counts.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} x{kv.Value}"));
}

/// <summary>The newest few checkpoint structs and the extended totals owned when each one was saved.
/// The checkpoint struct carries no save header, so nothing keys it in the sidecar and the totals ride here keyed by the struct's address instead.</summary>
internal sealed class CheckpointTotalShadow
{
    // A Retry only ever puts back a recent struct, so the oldest falls off once eight are held.
    public const int Capacity = 8;

    private readonly Dictionary<long, IReadOnlyDictionary<int, int>> _byKey = new();
    private readonly List<long> _order = new();

    public int Count => _byKey.Count;

    public void RememberForStruct(long structAddr, IReadOnlyDictionary<int, int> counts)
    {
        _order.Remove(structAddr);
        _order.Add(structAddr);
        _byKey[structAddr] = counts;
        while (_order.Count > Capacity)
        {
            _byKey.Remove(_order[0]);
            _order.RemoveAt(0);
        }
    }

    public bool TryGetForStruct(long structAddr, out IReadOnlyDictionary<int, int> counts)
    {
        if (_byKey.TryGetValue(structAddr, out var found)) { counts = found; return true; }
        counts = new Dictionary<int, int>();
        return false;
    }
}
