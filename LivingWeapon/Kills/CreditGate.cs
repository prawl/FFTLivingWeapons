using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// LW-56 credit-time live-wielder gate for a corpse's culprit weapon list. Narrowing only, like
/// <see cref="CursorGate"/>. It can refuse a credit but never add or redirect one. It closes the case
/// where a stale attribution latch survives into a new battle and credits a weapon nobody carries.
/// The live truth comes in as a predicate, so the class stays pure and truth-table testable.
/// </summary>
internal static class CreditGate
{
    /// <summary>Split <paramref name="culprit"/> into (survivors, refused) by <paramref name="hasLiveWielder"/>(id),
    /// keeping input order. Every output id comes from culprit.</summary>
    internal static (List<int> survivors, List<int> refused) Decide(List<int> culprit, Func<int, bool> hasLiveWielder)
    {
        var survivors = new List<int>();
        var refused = new List<int>();
        foreach (int id in culprit)
            (hasLiveWielder(id) ? survivors : refused).Add(id);
        return (survivors, refused);
    }
}
