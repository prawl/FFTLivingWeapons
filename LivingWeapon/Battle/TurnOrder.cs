using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Is a given enemy seat the next to act, and how far off is another? Ports the CT+Speed
/// clock model the 2026-07-27 phase-0 probe validated 15/15 (project in
/// tools/probes/provoke_lookahead_probe.py, not its leaders, which diverges when two min-ETA
/// units carry different Speeds).
/// Player seats are excluded. ExtraTurn slams CT (band +0x25), Iai holds Speed (band +0x24), and
/// GrowthEngine holds Speed for growth. Offsets.ACtSlam says not to trust CT for the player's own turns.
/// Stateless. Each call walks the band fresh, so a turn-order rebuild can reuse it.
/// </summary>
internal static class TurnOrder
{
    /// <summary>CT at which a seat takes its turn (Offsets.ACtSlam doc, probe's ACT_THRESHOLD).</summary>
    internal const int ActThreshold = 100;

    /// <summary>A valid, on-field, enemy-side, alive seat with sane hp/maxHp and nonzero Speed, matching the
    /// probe's seat_valid. One seat read hp 437 of maxHp 96 at tile (255,129), Speed 47, and skewed the
    /// whole prediction; zero Speed never reaches the threshold.</summary>
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

    /// <summary>Ticks until a seat's CT reaches the threshold 0 if already there (CT can overshoot, the
    /// probe read 110), else ceil(remaining / Speed).</summary>
    private static int RawEta(int ct, int speed) =>
        ct >= ActThreshold ? 0 : (int)Math.Ceiling((ActThreshold - ct) / (double)speed);

    /// <summary>The probe's project at depth 1. Every candidate waits the minimum raw ETA, then the
    /// one with the highest CT acts (a further overshoot has waited longer). <paramref name="leaderEta"/>
    /// is that wait. False when no seat is a candidate. <paramref name="scratch"/> is caller-owned like
    /// ProvokeHold's _hideScratch. This type is stateless and flight/logging run off the engine thread.</summary>
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

    /// <summary>Raw ETA for one already-located seat, to measure how far off a not-next marked enemy is.
    /// False when the seat fails <see cref="IsCandidate"/>: LocateByIdentity only checks Band.IsValid, so a
    /// ghost seat or garbage hp&gt;maxHp read could report a stale CT as "far off" and reveal the party.</summary>
    internal static bool TryEta(IGameMemory mem, long entry, out int eta)
    {
        eta = 0;
        if (!IsCandidate(mem, entry)) return false;
        eta = RawEta(mem.U8(entry + Offsets.ACtSlam), mem.U8(entry + Offsets.ASpeed));
        return true;
    }
}
