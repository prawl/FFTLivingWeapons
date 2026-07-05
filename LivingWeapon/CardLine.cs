using System;

namespace LivingWeapon;

/// <summary>
/// Reliquary Phase 1's pure card-flavor composer (docs/RELIQUARY_AC.md Display section). Turns
/// a weapon's deed ledger (LegendStore's WeaponLegend view) into the story-line replacement for
/// its baked flavor -- or null, meaning "keep the baked line" (decision 8: no deeds recorded ->
/// always null).
///
/// TOTAL ORDER (Phase 1; decision 10 descopes the Legends branch to Phase 2): highest-count
/// archetype AMONG EARNED MARKS (a count alone never qualifies -- only a threshold-crossed
/// entry in Marks) &gt; last-victim (any recorded deed, even below every Mark threshold) &gt; null.
///
/// Forms are tried in FIXED PRIORITY (A, B, C, D -- NOT sorted by length), so a narrow budget can
/// never invert the total order by letting a lower-priority form "win" merely because it is
/// shorter than a higher-priority one that would also have fit:
///   A: "{name}, {title} -- {n} {noun} felled; last, {a(n) victimNoun}."
///   B: "{name}, {title} -- {n} {noun} felled."
///   C: "{name} -- {k} felled; last, {a(n) victimNoun}."   (no earned mark, or A/B don't fit)
///   D: "{name} -- {k} felled."
///   none fits -> null (also when there ARE deeds but literally no form fits -- see
///   CardLineTests.Sasukes_blade_26_budget_is_always_null).
/// {n} = the chosen mark archetype's count; {k} = totalKills (the tally, not a deed count).
/// The result is RIGHT-PADDED with spaces to exactly budgetChars.
/// </summary>
internal static class CardLine
{
    public static string? Compose(string weaponName, int totalKills, WeaponLegend legend, int budgetChars)
    {
        // Decision 8: no deed has ever been recorded for this weapon -> always null (baked stays).
        if (legend.LastVictimCls < 0) return null;

        VictimClass.Archetype? bestMark = null;
        int bestCount = -1;
        foreach (int idx in legend.Marks)
        {
            int count = legend.Counts[idx];
            // Tie-break by Archetype ENUM ORDER: a strictly higher count always wins; on an exact
            // tie, only take the new candidate if its enum value is LOWER than the incumbent's.
            if (count > bestCount || (count == bestCount && bestMark.HasValue && idx < (int)bestMark.Value))
            {
                bestMark = (VictimClass.Archetype)idx;
                bestCount = count;
            }
        }

        bool undead = legend.LastVictimCls == (int)VictimClass.Archetype.Undead;
        string victimPhrase = VictimClass.WithArticle(VictimClass.VictimNoun(legend.LastVictimJob, undead));

        if (bestMark.HasValue)
        {
            string title = VictimClass.MarkTitle(bestMark.Value);
            string noun = VictimClass.CountNoun(bestMark.Value);
            string? formA = TryPad($"{weaponName}, {title} -- {bestCount} {noun} felled; last, {victimPhrase}.", budgetChars);
            if (formA != null) return formA;
            string? formB = TryPad($"{weaponName}, {title} -- {bestCount} {noun} felled.", budgetChars);
            if (formB != null) return formB;
        }

        string? formC = TryPad($"{weaponName} -- {totalKills} felled; last, {victimPhrase}.", budgetChars);
        if (formC != null) return formC;
        string? formD = TryPad($"{weaponName} -- {totalKills} felled.", budgetChars);
        if (formD != null) return formD;

        return null;
    }

    /// <summary>Right-pad to exactly budgetChars and return it iff the raw candidate fits AND
    /// the padded result is paintable; null (never a truncated/garbled candidate) otherwise.</summary>
    private static string? TryPad(string candidate, int budgetChars)
    {
        if (candidate.Length > budgetChars) return null;
        string padded = candidate.PadRight(budgetChars);
        return IsPaintable(padded, budgetChars) ? padded : null;
    }

    /// <summary>True iff s is exactly budgetChars long and every char is printable ASCII
    /// (0x20..0x7E). Asserted (via TryPad, above) on every non-null Compose return --
    /// ByteScan.Ascii DROPS non-ASCII chars when baking the 8-bit encoding, which would desync
    /// the 8-bit and UTF-16LE pattern lengths CardPatterns/EarnedAnchors rely on being equal
    /// (load-bearing). The ASCII range check also excludes an em dash (U+2014, far above 0x7E)
    /// without needing to embed that character in this file -- the house style is ASCII " -- "
    /// (two hyphens), which is all Compose's own templates ever emit.</summary>
    public static bool IsPaintable(string s, int budgetChars)
    {
        if (s.Length != budgetChars) return false;
        foreach (char c in s)
            if (c is < ' ' or > '~') return false;
        return true;
    }
}
