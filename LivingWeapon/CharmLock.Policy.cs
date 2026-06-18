using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// The pure decisions behind the charm-lock -- no memory access, so they're unit-tested directly.
/// The stateful orchestrator (the band scan + write+hold) lives in CharmLock.cs.
/// </summary>
internal sealed partial class CharmLock
{
    /// <summary>
    /// Newest-wins single-lock decision (anti-cheese: only ever ONE enemy is held). Given the
    /// currently-locked enemy (or null) and the enemies found charmed this scan, pick the lock
    /// target. A charmed enemy that ISN'T the current lock wins -- the lock moves to it and the
    /// previous is dropped. If only the current lock is (still) charmed, keep it. If nothing is
    /// charmed, do nothing. Other charmed enemies are left untouched as ordinary breakable charms.
    /// </summary>
    public static bool Decide(
        (int mhp, int lvl, int br, int fa)? current,
        IReadOnlyList<(int mhp, int lvl, int br, int fa)> charmed,
        out (int mhp, int lvl, int br, int fa) target,
        out bool dropPrevious)
    {
        target = default;
        dropPrevious = false;
        foreach (var fp in charmed)
            if (current is null || !fp.Equals(current.Value))
            {
                target = fp;
                dropPrevious = current is not null;
                return true;
            }
        return false;   // nothing charmed, or only the current lock is -> keep doing what we're doing
    }
}
