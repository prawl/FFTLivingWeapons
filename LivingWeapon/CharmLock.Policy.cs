using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// The pure decisions behind the charm-lock -- no memory access, so they're unit-tested directly.
/// The stateful orchestrator (the band scan + write+hold) lives in CharmLock.cs.
/// </summary>
internal sealed partial class CharmLock
{
    /// <summary>No live-battlefield heartbeat for this long => the lock deactivates. The in-battle
    /// sentinel (slot9) goes sticky on the world map, so a battle can "end" while the engine still
    /// thinks it's running; without this the lock would scan + log forever post-battle.</summary>
    public const double TimeoutMs = 2000;

    /// <summary>A completed turn = the unit's CT was (near-)full and has since reset notably lower.</summary>
    public static bool IsTurn(int lastCt, int curCt) => lastCt >= 90 && curCt < 70;

    /// <summary>A genuine in-battle frame, for feeding the heartbeat and gating writes.
    /// battleMode 2/3/4 covers active-turn frames. The slot0==0xFF in-battle marker stays set
    /// through cast/attack targeting (battleMode 1/5) where gating on {2,3,4} alone starves the
    /// beat -- but the marker CANNOT be trusted alone: QUITTING a battle leaves slot0 STUCK at
    /// 0xFF on the world map (probe-verified 2026-06-10; a normal victory clears it to 0x66),
    /// which kept battles "live" forever -- no exit edge, endless charm holds. So a marker-only
    /// frame counts as live only with an EXCUSE for battleMode reading 0: targeting modes 1/5,
    /// the pause flag, or a real event id (mid-battle dialogue).</summary>
    public static bool InLiveBattle(uint slot0, int battleMode, bool paused, int eventId) =>
        battleMode == 2 || battleMode == 3 || battleMode == 4
        || (slot0 == 0xFF && (battleMode == 1 || battleMode == 5
                              || paused || BattleState.IsRealEvent(eventId)));

    /// <summary>True once it's been longer than <paramref name="timeoutMs"/> since the last heartbeat.</summary>
    public static bool HeartbeatExpired(DateTime now, DateTime lastBeat, double timeoutMs) =>
        (now - lastBeat).TotalMilliseconds > timeoutMs;

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
