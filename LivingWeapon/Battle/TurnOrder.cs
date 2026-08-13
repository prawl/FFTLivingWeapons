using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// LW-127: is a given ENEMY seat the next one to act, and how far off is another? Ports the CT+Speed
/// clock model the 2026-07-27 phase-0 probe validated 15/15 across two sessions and a restart
/// (tools/probes/provoke_lookahead_probe.py's project() function -- NOT its leaders(), which the v1
/// plan mistakenly cited; the two diverge whenever two min-ETA units carry different Speeds, exactly
/// the near-tie the ledger row already recorded transposing).
///
/// PLAYER SEATS ARE EXCLUDED ENTIRELY (deliberate, not an oversight): ExtraTurn slams CT band +0x25
/// on a player seat (the extra-turn grant), Iai holds Speed band +0x24 on a player seat (the
/// opening-turn speed lock), and GrowthEngine holds the same Speed byte for the wielder's own stat
/// growth -- Offsets.cs's ACtSlam doc says it plainly: "do NOT trust for the player's own turns"
/// (stale/frozen ~85 during that unit's own input menu). Ranking enemy seats only stays inside the
/// documented-clean read and is untouched by anything this mod itself writes.
///
/// No state, no battle lifecycle -- every call is a fresh band walk against whatever IGameMemory it
/// is given, so this is reusable wherever a "what happens next in the turn order" question comes up
/// (LW-127 is the first consumer; LW-139 is a planned second).
/// </summary>
internal static class TurnOrder
{
    /// <summary>The scheduler's own act threshold: a seat whose CT reaches this many points takes
    /// its turn. 100, per Offsets.ACtSlam's own doc comment and the probe's ACT_THRESHOLD constant.</summary>
    internal const int ActThreshold = 100;

    /// <summary>Every valid, on-field, ENEMY-side, alive candidate with a sane hp/maxHp pair and a
    /// nonzero Speed -- the same rejections the probe's seat_valid() learned the hard way live (a
    /// seat read hp 437 of maxHp 96 at tile (255,129), Speed 47, that dominated the whole prediction
    /// until it was excluded; a zero Speed can never climb toward the threshold, so it can neither
    /// win nor produce a meaningful ETA).</summary>
    private static bool IsCandidate(IGameMemory mem, long e)
    {
        if (!Band.IsValid(mem, e)) return false;
        if (mem.U8(e + Offsets.AGateByte) == Offsets.AGateHiddenValue) return false;
        if ((mem.U8(e + Offsets.AFriendFoe) & Offsets.AFriendFoeEnemyBit) == 0) return false;
        int hp = mem.U16(e + Offsets.AHp);
        int maxHp = mem.U16(e + Offsets.AMaxHp);
        if (hp <= 0 || hp > maxHp) return false;
        if ((mem.U8(e + Offsets.ADeadStatus) & Offsets.ADeadBit) != 0) return false;
        return mem.U8(e + Offsets.ASpeed) > 0;
    }

    /// <summary>Raw ticks until a seat's own CT reaches the act threshold: 0 if already there or past
    /// it (CT overshoots live -- the probe read a live unit at CT 110), else the ceiling of the
    /// remaining distance over its Speed.</summary>
    private static int RawEta(int ct, int speed) =>
        ct >= ActThreshold ? 0 : (int)Math.Ceiling((ActThreshold - ct) / (double)speed);

    /// <summary>The single-step clock projection (the probe's project(), depth 1): every candidate
    /// waits the SAME number of ticks -- the minimum raw ETA among them, i.e. how long until the
    /// very first one crosses the threshold -- then the candidate with the highest CT after that
    /// wait acts (an overshoot tie-break: a unit that reads further past the threshold has been
    /// waiting longer). <paramref name="leaderEta"/> is that shared wait, in ticks. False when no
    /// seat is a candidate at all (branch 5's trigger, the "unusable read" default).
    ///
    /// <paramref name="scratch"/> is CALLER-OWNED (mirrors ProvokeHold's own `_hideScratch`): this
    /// type is stateless by contract, and the flight/logging paths run off the engine thread, so a
    /// static mutable buffer here would be a threading hazard. Cleared on entry, so the caller only
    /// needs to allocate it once and reuse it every tick.</summary>
    internal static bool TryNextEnemyToAct(IGameMemory mem, List<(long e, int ct, int speed)> scratch,
        out long entry, out int leaderEta)
    {
        entry = 0;
        leaderEta = 0;
        scratch.Clear();
        for (int s = 0; s < Offsets.BandSlots; s++)
        {
            long e = Band.Entry(s);
            if (!IsCandidate(mem, e)) continue;
            scratch.Add((e, mem.U8(e + Offsets.ACtSlam), mem.U8(e + Offsets.ASpeed)));
        }
        if (scratch.Count == 0) return false;

        int wait = int.MaxValue;
        foreach (var c in scratch) wait = Math.Min(wait, RawEta(c.ct, c.speed));

        long winner = 0;
        int bestCt = int.MinValue;
        foreach (var c in scratch)
        {
            int projected = c.ct + c.speed * wait;
            if (projected > bestCt) { bestCt = projected; winner = c.e; }
        }

        entry = winner;
        leaderEta = wait;
        return true;
    }

    /// <summary>The raw ETA for one specific already-located seat, independent of any ranking --
    /// used to measure how far OFF an already-excluded (not-next) marked enemy is from the leader.
    /// False when the seat is not itself a candidate (BLOCKER-1, adversarial review): a marked
    /// entry comes from LocateByIdentity, which only requires Band.IsValid -- it says nothing about
    /// on-field, enemy-side, or hp&lt;=maxHp. A seat the RANKING itself would refuse (a ghost seat, a
    /// garbage hp&gt;maxHp read, a frozen twin) must not be allowed to produce a number either, or a
    /// stale/zero CT reads as "clearly far off" and reveals the party while the marked enemy is
    /// still genuinely queued. Runs the SAME <see cref="IsCandidate"/> gate TryNextEnemyToAct's own
    /// ranking uses, so both callers agree on what counts as a usable read.</summary>
    internal static bool TryEta(IGameMemory mem, long entry, out int eta)
    {
        eta = 0;
        if (!IsCandidate(mem, entry)) return false;
        eta = RawEta(mem.U8(entry + Offsets.ACtSlam), mem.U8(entry + Offsets.ASpeed));
        return true;
    }
}
