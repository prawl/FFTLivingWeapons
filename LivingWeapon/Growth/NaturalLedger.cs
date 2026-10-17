using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>Combat stat bytes the capture-natural holds own. Speed/Pa/Ma are the original trio (CSpeed/CPa/CMa); Brave/Faith are
/// the capped current holds (CBraveCurrent/CFaithCurrent). MaxHp (CMaxHp) is a u16 hold named here for lane vocabulary
/// but does not thread NaturalLedger; see HoldU16 in GrowthEngine.Lanes.cs.</summary>
internal enum StatLane { Speed, Pa, Ma, Brave, Faith, MaxHp }

/// <summary>
/// Remembers the targets the mod wrote, per battle attempt, so a battle restart cannot turn the held boost into a unit's natural (seen 2026-07-14 with the Iai opening hold). Otherwise Iai restores the boosted value and growth holds compound across restarts.
/// Entries key on (roster nameId, stat lane) and keep Current and Prev target lists. Every hold evaluation records its target, even when the write is elided. OnBattleReset promotes Current to Prev only when Current is non-empty, since Engine.ResetBattleState fires on both battle edges.
/// A first-sight capture that matches a recorded target, while a different accepted natural is on file at the same roster level, is restart residue. The ledger returns the accepted natural and reports the residue as an ownership token (that the post-restart normalize restores the baked value is unverified; the Iai post-release corrective hold will show).
/// The level check matters because a boost target is often natural+1, so an ordinary level-up could land on a recorded target. Restarts never commit a level (LIVE_LEDGER "mid-battle level-up drift" row). Accepted corner, an equipment change to exactly a recorded target still deflates until the next level.
/// Fail-open for nameId &lt;= 0 (as Iai's address fallback); an unmatched first sight is accepted as natural. Session-scoped, Clear() rides the new-game edge, so relaunch-resume-then-restart is unguarded (accepted).
/// </summary>
internal sealed class NaturalLedger
{
    private const int NaturalSaneMin = 1, NaturalSaneMax = 99;
    private const int TargetMin = 1, TargetMax = 255;
    internal const int TargetCap = 16;   // per-entry ring bound; oldest dropped

    private sealed class Entry
    {
        public int AcceptedNatural = -1;
        public int AcceptedLevel = -1;   // roster level at accept time
        public List<int> Prev = new();
        public List<int> Current = new();
    }

    private readonly Dictionary<(int nameId, StatLane lane), Entry> _entries = new();

    // Counts every RecordWrite call, early returns included, for the cadence pins in GrowthEngineCadenceTests.cs
    internal int RecordWriteCalls { get; private set; }

    /// <summary>Promote Current to Prev for entries that recorded anything this attempt. Safe to call repeatedly per transition.</summary>
    public void OnBattleReset()
    {
        foreach (var e in _entries.Values)
        {
            if (e.Current.Count == 0) continue;
            e.Prev = e.Current;
            e.Current = new List<int>();
        }
    }

    /// <summary>Forget everything (the new-game edge).</summary>
    public void Clear() => _entries.Clear();

    /// <summary>Record the target a hold computed this evaluation, written or elided. Dedups within the attempt, ring-capped, no-op for nameId &lt;= 0 or out of byte range.</summary>
    public void RecordWrite(int nameId, StatLane lane, int target)
    {
        RecordWriteCalls++;
        if (nameId <= 0 || target < TargetMin || target > TargetMax) return;
        if (!_entries.TryGetValue((nameId, lane), out var e))
            _entries[(nameId, lane)] = e = new Entry();
        if (e.Current.Contains(target)) return;
        if (e.Current.Count >= TargetCap) e.Current.RemoveAt(0);   // drop the oldest
        e.Current.Add(target);
    }

    /// <summary>The natural to trust for a first-sight capture. <paramref name="level"/> is the roster level; a correction needs it to
    /// equal the accept-time stamp. <paramref name="baked"/> is the residue value when a correction fired, else 0.</summary>
    public int FilterCapture(int nameId, StatLane lane, int firstSight, int level, out int baked)
    {
        baked = 0;
        if (nameId <= 0) return firstSight;
        if (!_entries.TryGetValue((nameId, lane), out var e))
        {
            _entries[(nameId, lane)] = new Entry { AcceptedNatural = firstSight, AcceptedLevel = level };
            return firstSight;
        }
        bool residue = (e.Prev.Contains(firstSight) || e.Current.Contains(firstSight))
                       && e.AcceptedNatural >= NaturalSaneMin && e.AcceptedNatural <= NaturalSaneMax
                       && e.AcceptedNatural != firstSight
                       && e.AcceptedLevel == level;   // a level-up is a real change
        if (residue)
        {
            baked = firstSight;
            return e.AcceptedNatural;   // not refreshed, so restart N+1 corrects too
        }
        e.AcceptedNatural = firstSight;
        e.AcceptedLevel = level;
        return firstSight;
    }
}
