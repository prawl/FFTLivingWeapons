using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;

namespace LivingWeapon;

/// <summary>
/// Keeps a heal from pushing into a cache while a screen's own build is still loading into it.
/// A build shows itself as a lookup whose key sits on the caller's stack.
/// </summary>
internal sealed partial class IconCacheHeal
{
    private readonly ConcurrentDictionary<nint, long> _builderSeen = new();
    private readonly Dictionary<(nint Manager, string Name), long> _deferred = new();

    internal Func<long> Clock { get; set; } = () => Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency;

    /// <summary>True when no cache saw a screen build inside the window. A stamp is kept for every stack key lookup, tracked names or not.</summary>
    public bool BuildersQuiet
    {
        get
        {
            long now = Clock();
            foreach (var seen in _builderSeen.Values)
                if (IconCacheHealPolicy.InBuilderWindow(now, seen)) return false;
            return true;
        }
    }

    internal int AttemptCount(nint manager, string name)
    {
        lock (_lock) return _attempts.TryGetValue((manager, name), out int n) ? n : 0;
    }

    private nint Guarded(nint manager, nint key, nint entry)
    {
        if (IconCacheHealPolicy.IsStackKey(key, _stack()))
        {
            _builderSeen[manager] = Clock();
            return entry;
        }
        return entry != 0 || _trackedCount == 0 ? entry : Heal(manager, key);
    }

    /// <summary>Caller holds the lock. True while a build touched this cache inside the window.</summary>
    private bool BuilderBusy(nint manager, string name)
    {
        long now = Clock();
        if (!_builderSeen.TryGetValue(manager, out long seen) || !IconCacheHealPolicy.InBuilderWindow(now, seen)) return false;
        bool first = !_deferred.TryGetValue((manager, name), out long last) || !IconCacheHealPolicy.InBuilderWindow(now, last);
        if (first)
        {
            _deferred[(manager, name)] = now;
            ModLogger.Debug(LogVerb.Display, $"IconCacheHeal heal deferred, a screen is still loading into cache 0x{(long)manager:X} ({IconCacheHealPolicy.Tail(name)}).");
        }
        return true;
    }
}
