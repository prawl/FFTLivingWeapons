using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>The three combat stat bytes the capture-natural holds own (CSpeed/CPa/CMa).</summary>
internal enum StatLane { Speed, Pa, Ma }

/// <summary>
/// LW-90: the cross-battle written-target memory that keeps a battle RESTART from turning the
/// mod's own held stat boost into a unit's "natural". The game's restart rebuild can carry the
/// held (boosted) byte (observed live 2026-07-14, the Iai opening hold; the mod's own
/// bookkeeping read healthy both runs), so a fresh per-battle first-sight capture would adopt
/// the boost as natural: Iai then restores the boosted value at release, and the growth-family
/// holds compound on a polluted base across repeated restarts.
///
/// Mechanism: every hold EVALUATION records the target it holds (per evaluation, not per
/// physical write: the holds elide the W8 when the byte already equals the target, and a
/// post-restart battle starts in exactly that state). Entries are keyed by (roster nameId,
/// stat lane) and hold a rolling two-slot target history: Current (this battle attempt) and
/// Prev (the last attempt that recorded anything). OnBattleReset promotes Current to Prev ONLY
/// when Current is non-empty: Engine.ResetBattleState deliberately fires on BOTH battle edges
/// (a clean restart resets twice, and battle-load churn adds more), so an unconditional swap
/// would wipe the history before the restarted battle's capture could consult it (the plan
/// review's blocker). A capture whose first sight EXACTLY matches a recorded target (Prev or
/// Current: Current covers a same-battle struct relocation re-capture) while a different
/// accepted natural is on file AT THE SAME ROSTER LEVEL is restart residue: the ledger
/// returns the accepted natural and reports the residue value, which the call site keeps as
/// a recognized ownership token (if the engine's post-restart normalize restores the baked
/// value -- plausible but UNVERIFIED inference; the Iai post-release corrective hold is the
/// live discriminator -- a corrected hold that did not recognize it would go foreign).
///
/// THE LEVEL KEY (the implementation review's major): a boost target is often natural+1
/// (10-15% factors on single-digit stats, Iai's +1 margin), so a unit's ordinary +1 level-up
/// gain can land its NEW true natural exactly on a recorded target; without a discriminator
/// the correction would then eat the earned point every battle, forever (the corrected hold
/// re-records the same colliding target each time). Restarts never commit a level (the
/// roster keeps its pre-battle level until battle END, LIVE_LEDGER "mid-battle level-up
/// drift" row), so the accepted natural is stamped with the capture-time roster level and a
/// correction fires only when the levels match: a level-up breaks the loop, a restart never
/// does. Residual corner, accepted + logged: an EQUIPMENT change (no level move) to exactly
/// a recorded target still corrects wrongly, deflating by the boost delta until the unit's
/// next level or a different capture value.
///
/// Fail-open: nameId &lt;= 0 bypasses entirely (the degraded lane, mirroring Iai's
/// address-fallback); an unmatched first sight is accepted as natural and refreshes the entry.
/// Session-scoped: Clear() rides the new-game edge; nothing persists to disk, so a
/// relaunch-resume-then-restart sequence is unguarded (accepted corner).
/// </summary>
internal sealed class NaturalLedger
{
    private const int NaturalSaneMin = 1, NaturalSaneMax = 99;
    private const int TargetMin = 1, TargetMax = 255;
    internal const int TargetCap = 16;   // per-entry ring bound; oldest dropped

    private sealed class Entry
    {
        public int AcceptedNatural = -1;
        public int AcceptedLevel = -1;   // roster level at accept time (the level key)
        public List<int> Prev = new();
        public List<int> Current = new();
    }

    private readonly Dictionary<(int nameId, StatLane lane), Entry> _entries = new();

    /// <summary>Promote Current to Prev, ONLY for entries that recorded anything this attempt.
    /// Safe to call any number of times per transition (Engine resets on both edges).</summary>
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

    /// <summary>Record a target a hold computed for this unit+lane this evaluation (whether or
    /// not the byte write was elided). Dedup within the attempt; ring-capped; no-op off the
    /// identity lane (nameId &lt;= 0) or outside byte range.</summary>
    public void RecordWrite(int nameId, StatLane lane, int target)
    {
        if (nameId <= 0 || target < TargetMin || target > TargetMax) return;
        if (!_entries.TryGetValue((nameId, lane), out var e))
            _entries[(nameId, lane)] = e = new Entry();
        if (e.Current.Contains(target)) return;
        if (e.Current.Count >= TargetCap) e.Current.RemoveAt(0);   // ring: drop the oldest
        e.Current.Add(target);
    }

    /// <summary>The natural to trust for a first-sight capture. <paramref name="level"/> is the
    /// unit's roster level (the level key: corrections require it to equal the accept-time
    /// stamp; restarts never move it, level-ups do). <paramref name="baked"/> is the residue
    /// value when a correction fired (the caller keeps it as an ownership token), else 0.</summary>
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
                       && e.AcceptedLevel == level;   // the level key: a level-up is a REAL change
        if (residue)
        {
            baked = firstSight;
            return e.AcceptedNatural;   // deliberately NOT refreshed: restart N+1 corrects too
        }
        e.AcceptedNatural = firstSight;
        e.AcceptedLevel = level;
        return firstSight;
    }
}
