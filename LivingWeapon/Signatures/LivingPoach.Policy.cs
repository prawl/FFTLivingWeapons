using System;

namespace LivingWeapon;

/// <summary>The eligibility+roll decision's outcome. No poach, or which carcass tier landed.</summary>
internal enum PoachVerdict { None, Common, Rare }

/// <summary>
/// LW-167 Living Poach pure eligibility and roll-split policy. LivingPoach.cs gathers the inputs and acts
/// on the verdict. Every gate in <see cref="Decide"/> must pass or the result is None. Two guard against
/// double-firing with vanilla Poach, <c>weaponIsDormantFormula</c> and <c>wasBasicAttack</c> (vanilla also
/// poaches ability kills, seen 2026-08-12), armed in LW-167 stage 4 via <see cref="LivingPoach.ReadWasBasicAttack"/>.
/// The old SpeciesOf/JobInMonsterRange arithmetic (job byte, 95) was dropped 2026-08-12 after a Black Chocobo
/// at job 95 was refused. <paramref name="jobMapped"/> (poach.json map, tools/extract_poach_map.py) is the monster gate.
/// </summary>
internal static class LivingPoachPolicy
{
    /// <summary>Roll 0..255. Below this is Common, at or above is Rare (225/256 vs 31/256, the ffhacktics split, not the folk 224/32).</summary>
    internal const int RollCommonThreshold = 225;

    /// <summary>The whole eligibility and roll decision. <paramref name="jobMapped"/> is PoachMap.TryGetJob(victimJob, ...);
    /// <paramref name="roll"/> is 0..255 from the caller's injected seam.</summary>
    internal static PoachVerdict Decide(bool weaponIsDormantFormula, bool killerHasPoach, bool wasBasicAttack,
        int victimJob, bool jobMapped, int roll)
    {
        if (!weaponIsDormantFormula) return PoachVerdict.None;
        if (!killerHasPoach) return PoachVerdict.None;
        if (!wasBasicAttack) return PoachVerdict.None;
        if (!jobMapped) return PoachVerdict.None;
        return roll < RollCommonThreshold ? PoachVerdict.Common : PoachVerdict.Rare;
    }

    /// <summary>Strip a trailing <c>&lt;Icon=...&gt;</c> tag (poach.json's rare-variant suffix, e.g. "Chocobo Carcass&lt;Icon=103&gt;")
    /// for plain-text surfaces like the toast.</summary>
    internal static string StripIconMarkup(string name)
    {
        int i = name.IndexOf("<Icon=", StringComparison.Ordinal);
        if (i < 0) return name;
        int close = name.IndexOf('>', i);
        return close < 0 ? name.Substring(0, i) : name.Remove(i, close - i + 1);
    }
}
