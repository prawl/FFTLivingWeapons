using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// The pure battle-end match-report composer (logging facelift stage 3): turns the per-battle
/// counters (KillTracker.BattleCredits / Reliquary.BattleMarks / the fallback-attribution count
/// / TurnTracker.GlobalTurns) into the one [battle-end] console line. Pure over its inputs
/// (weapon naming and tier routing are injected) so the whole shape is unit-tested without an
/// Engine. Tier crossings are DERIVED, not counted: for each credited weapon, compare the tier
/// at (lifetime - battleCredits) against the tier at lifetime.
/// </summary>
internal static class BattleSummary
{
    /// <param name="battleCredits">weaponId -&gt; kills credited this battle.</param>
    /// <param name="lifetimeKills">the shared lifetime tally (already includes this battle).</param>
    /// <param name="marks">Marks earned this battle, in earn order.</param>
    /// <param name="fallbackCredits">corpse credits attributed via the turn-queue fallback.</param>
    /// <param name="turns">completed turns this battle (TurnTracker.GlobalTurns).</param>
    /// <param name="nameOf">weapon id -&gt; display name (LogNames.Weapon).</param>
    /// <param name="tierOf">kill count -&gt; tier (Tuning.TierFor).</param>
    public static string Compose(
        IReadOnlyDictionary<int, int> battleCredits,
        IReadOnlyDictionary<int, int> lifetimeKills,
        IReadOnlyList<(int weaponId, VictimClass.Archetype mark)> marks,
        int fallbackCredits,
        int turns,
        Func<int, string> nameOf,
        Func<int, int> tierOf)
    {
        string turnsClause = $"{Plural(turns, "turn")}; the kill tally and legends are saved.";

        int totalKills = 0;
        foreach (var kv in battleCredits) totalKills += kv.Value;
        if (totalKills == 0)
            return $"Battle ended: no kills were credited; {turnsClause}";

        // Kills clause: names with battle counts, most kills first (ties by name for stability).
        var credited = new List<KeyValuePair<int, int>>(battleCredits);
        credited.Sort((a, b) => a.Value != b.Value ? b.Value.CompareTo(a.Value)
                                                   : string.CompareOrdinal(nameOf(a.Key), nameOf(b.Key)));
        var killNames = new List<string>(credited.Count);
        foreach (var kv in credited) killNames.Add($"{nameOf(kv.Key)} {kv.Value}");
        string killsClause = $"{Plural(totalKills, "kill")} credited ({string.Join(", ", killNames)})";

        // Marks clause: named with their Slayer titles, in earn order.
        string marksClause;
        if (marks.Count == 0) marksClause = "0 Marks earned";
        else
        {
            var markNames = new List<string>(marks.Count);
            foreach (var (weaponId, mark) in marks)
                markNames.Add($"{nameOf(weaponId)} the {VictimClass.MarkTitle(mark)}");
            marksClause = $"{Plural(marks.Count, "Mark")} earned ({string.Join(", ", markNames)})";
        }

        // Tier crossings DERIVED from the credit delta against the lifetime tally.
        var crossed = new List<string>();
        foreach (var kv in credited)
        {
            lifetimeKills.TryGetValue(kv.Key, out int lifetime);
            int before = tierOf(lifetime - kv.Value);
            int after = tierOf(lifetime);
            if (after > before) crossed.Add($"{nameOf(kv.Key)} tier {after}");
        }
        string tiersClause = crossed.Count == 0
            ? $"{Plural(0, "tier")} reached"
            : $"{Plural(crossed.Count, "tier")} reached ({string.Join(", ", crossed)})";

        string fallbackClause = fallbackCredits > 0
            ? $", {Plural(fallbackCredits, "kill")} credited by fallback attribution" : "";

        return $"Battle ended: {killsClause}, {marksClause}, {tiersClause}{fallbackClause}; {turnsClause}";
    }

    internal static string Plural(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";
}
