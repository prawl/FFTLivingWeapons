using System;

namespace LivingWeapon;

/// <summary>The eligibility+roll decision's outcome: no poach, or which carcass tier landed.</summary>
internal enum PoachVerdict { None, Common, Rare }

/// <summary>
/// LW-167 Living Poach -- the pure eligibility + roll-split policy (docs plan v2's "Locked
/// design"). No memory access: LivingPoach.cs is the stateful executor that gathers these inputs
/// (from the widened deed sink, the injected killer-support/action-shape delegates, and its own
/// PoachMap/meta lookups) and acts on the verdict this returns.
///
/// Every gate in <see cref="Decide"/> is an INDEPENDENT AND -- a false anywhere is None, never a
/// partial poach. Two of the five are double-fire guards against vanilla's own Poach support:
/// <c>weaponIsDormantFormula</c> (a weapon on a vanilla-readable formula must never also fire this
/// runtime feature -- vanilla already procs through it) and <c>wasBasicAttack</c> (vanilla poaches
/// an ABILITY kill too, owner-observed live 2026-08-12, so an ability kill must never double-poach
/// beside it). <c>wasBasicAttack</c> is hard-wired false at the Engine wiring site until stage 4
/// (the action discriminator) lands -- see LivingPoach.cs's ctor doc -- so the feature stays
/// structurally disarmed in production through stage 2, regardless of what the other four gates read.
/// </summary>
internal static class LivingPoachPolicy
{
    /// <summary>Roll 0..255: below this is Common, at/above is Rare -- 225/256 common, 31/256
    /// rare (ffhacktics-documented split; corrects the folk 224/32 belief).</summary>
    internal const int RollCommonThreshold = 225;

    /// <summary>Monster species index (poach.json's key) from a victim's job byte: job - 95.
    /// Only meaningful within <see cref="JobInMonsterRange"/>; pure arithmetic otherwise, no
    /// range check of its own.</summary>
    internal static int SpeciesOf(int job) => job - 95;

    /// <summary>True for this range check's own band (96..144) -- not quite the same as
    /// "mapped": poach.json maps species 1..48 = jobs 96..143, so job 144 (species 49) passes
    /// this structural check but is refused downstream by the map gate (speciesMapped), same as
    /// every job past it. Job 95 maps to species 0, one below the band, and job 145+ maps to
    /// species 50+, past poach.json's 48 species -- both of those are excluded structurally by
    /// this range check itself, independent of whatever a (possibly stale) map lookup answered.</summary>
    internal static bool JobInMonsterRange(int job) => job is >= 96 and <= 144;

    /// <summary>The whole eligibility+roll decision in one call. <paramref name="roll"/> is 0..255
    /// (the caller's injected roll seam, kept out of this pure function so it stays testable).</summary>
    internal static PoachVerdict Decide(bool weaponIsDormantFormula, bool killerHasPoach, bool wasBasicAttack,
        int victimJob, bool speciesMapped, int roll)
    {
        if (!weaponIsDormantFormula) return PoachVerdict.None;
        if (!killerHasPoach) return PoachVerdict.None;
        if (!wasBasicAttack) return PoachVerdict.None;
        if (!JobInMonsterRange(victimJob)) return PoachVerdict.None;
        if (!speciesMapped) return PoachVerdict.None;
        return roll < RollCommonThreshold ? PoachVerdict.Common : PoachVerdict.Rare;
    }

    /// <summary>Strip a trailing <c>&lt;Icon=...&gt;</c> markup tag (poach.json's rare-variant
    /// name suffix, e.g. "Chocobo Carcass&lt;Icon=103&gt;") for plain-text display surfaces
    /// (the toast). A name carrying no markup passes through unchanged.</summary>
    internal static string StripIconMarkup(string name)
    {
        int i = name.IndexOf("<Icon=", StringComparison.Ordinal);
        if (i < 0) return name;
        int close = name.IndexOf('>', i);
        return close < 0 ? name.Substring(0, i) : name.Remove(i, close - i + 1);
    }
}
