using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// LW-56: the credit-time live-wielder gate for a corpse's culprit weapon list. Doctrine mirrors
/// <see cref="CursorGate"/> (CursorGate.cs's own class doc): NARROWING-ONLY. The gate can only
/// turn a would-be credit into a refusal, never invent or redirect one: the culprit list already
/// named the weapon(s) whose actor latched this kill, and this pass only asks whether each one
/// still has a live wielder on the field right now (closing the incident this fix exists for: a
/// stale attribution latch surviving into a new battle, crediting a weapon nobody is carrying).
/// The truth arrives as a predicate, never a live memory read of its own, so this stays pure and
/// truth-table-testable: the predicate is the only place memory (or a fake) is ever touched.
/// </summary>
internal static class CreditGate
{
    /// <summary>Partition <paramref name="culprit"/> into (survivors, refused) by
    /// <paramref name="hasLiveWielder"/>(id), preserving input order in both outputs. Never adds,
    /// reorders, or promotes an id: every survivor and every refusal came from culprit itself.</summary>
    internal static (List<int> survivors, List<int> refused) Decide(List<int> culprit, Func<int, bool> hasLiveWielder)
    {
        var survivors = new List<int>();
        var refused = new List<int>();
        foreach (int id in culprit)
            (hasLiveWielder(id) ? survivors : refused).Add(id);
        return (survivors, refused);
    }
}
