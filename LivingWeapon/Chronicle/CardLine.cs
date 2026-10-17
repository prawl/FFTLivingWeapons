using System;

namespace LivingWeapon;

/// <summary>
/// Reliquary Phase 1's card-flavor composer (docs/RELIQUARY_AC.md Display section). Turns a weapon's
/// deed ledger (LegendStore's WeaponLegend) into the story line that replaces its baked flavor, or
/// null to keep the baked line (decision 8: no deeds recorded). Order (decision 10 defers Legends
/// to Phase 2): highest-count archetype among earned Marks &gt; last victim &gt; null.
/// Forms are tried in fixed priority A, B, C, D, not by length, so a short low-priority form cannot
/// beat a longer one that also fits. {n} is the mark archetype's count, {k} is totalKills.
///   A: "{name}, {title}: {n} {noun} felled; last, {a(n) victimNoun}."
///   B: "{name}, {title}: {n} {noun} felled."
///   C: "{name}: {k} felled; last, {a(n) victimNoun}."
///   D: "{name}: {k} felled."
/// None fitting gives null (CardLineTests.Sasukes_blade_26_budget_is_always_null). The result is
/// right-padded to budgetChars. The separator is ": " (wording pass 2026-07-05); an em dash would be
/// dropped by ByteScan.Ascii and desync the encodings, see <see cref="IsPaintable"/>.
/// </summary>
internal static class CardLine
{
    // A weapon composes only if its budget holds form D at a 5-digit kill count: name + ": " (2) +
    // 5 digits + " felled." (8). Forms only lengthen as the count grows, and nothing repaints back to
    // the baked line, so a non-null to null flip would strand a stale line. Sasuke's Blade (budget 26 <
    // 14+15) is the one weapon this excludes today. CardLineTests.Digit_headroom_property_all_121 covers it.
    private const int FormDFixedChars = 2 + 8;   // the ": " separator + " felled."
    private const int DigitHeadroom = 5;

    public static string? Compose(string weaponName, int totalKills, WeaponLegend legend, int budgetChars)
    {
        // Decision 8: no deed recorded, so the baked line stays.
        if (legend.LastVictimCls < 0) return null;

        // Headroom guard (see the constants above).
        if (budgetChars < weaponName.Length + FormDFixedChars + DigitHeadroom) return null;

        // Ties go to the lower Archetype enum value (VictimClass.BestMark, shared with AttackCard.cs, LW-31).
        VictimClass.Archetype? bestMark = VictimClass.BestMark(legend, out int bestCount);

        bool undead = legend.LastVictimCls == (int)VictimClass.Archetype.Undead;
        string victimPhrase = VictimClass.WithArticle(VictimClass.VictimNoun(legend.LastVictimJob, undead));

        if (bestMark.HasValue)
        {
            string title = VictimClass.MarkTitle(bestMark.Value);
            string noun = VictimClass.CountNoun(bestMark.Value);
            string? formA = TryPad($"{weaponName}, {title}: {bestCount} {noun} felled; last, {victimPhrase}.", budgetChars);
            if (formA != null) return formA;
            string? formB = TryPad($"{weaponName}, {title}: {bestCount} {noun} felled.", budgetChars);
            if (formB != null) return formB;
        }

        string? formC = TryPad($"{weaponName}: {totalKills} felled; last, {victimPhrase}.", budgetChars);
        if (formC != null) return formC;
        string? formD = TryPad($"{weaponName}: {totalKills} felled.", budgetChars);
        if (formD != null) return formD;

        return null;
    }

    /// <summary>Right-pads to budgetChars and returns it if the candidate fits and is paintable, else null.</summary>
    private static string? TryPad(string candidate, int budgetChars)
    {
        if (candidate.Length > budgetChars) return null;
        string padded = candidate.PadRight(budgetChars);
        return IsPaintable(padded, budgetChars) ? padded : null;
    }

    /// <summary>True when s is budgetChars long and all printable ASCII (0x20..0x7E). ByteScan.Ascii drops
    /// non-ASCII chars, which would desync the 8-bit and UTF-16LE pattern lengths that CardPatterns/EarnedAnchors
    /// need equal. The range also rules out an em dash (U+2014).</summary>
    public static bool IsPaintable(string s, int budgetChars)
    {
        if (s.Length != budgetChars) return false;
        foreach (char c in s)
            if (c is < ' ' or > '~') return false;
        return true;
    }
}
