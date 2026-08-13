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
/// partial poach. Two of the four are double-fire guards against vanilla's own Poach support:
/// <c>weaponIsDormantFormula</c> (a weapon on a vanilla-readable formula must never also fire this
/// runtime feature -- vanilla already procs through it) and <c>wasBasicAttack</c> (vanilla poaches
/// an ABILITY kill too, owner-observed live 2026-08-12, so an ability kill must never double-poach
/// beside it). ARMED (LW-167 stage 4, 2026-08-12): Engine now wires <c>wasBasicAttack</c> to
/// <see cref="LivingPoach.ReadWasBasicAttack"/>, the real per-credit action-record discriminator --
/// see LivingPoach.cs's ctor doc -- so this gate reads a live signal in production, same as the
/// other three.
///
/// LW-167 (2026-08-12): the old <c>SpeciesOf</c>/<c>JobInMonsterRange</c> arithmetic (species =
/// victim job byte - 95, monsters band 96..144) is gone -- a live pass falsified it (a Black
/// Chocobo at job 95 was refused). The game's Job sheet carries each monster job's PoachItem
/// common/rare Keys directly (tools/extract_poach_map.py decodes them straight off Job-en); human
/// jobs carry 0/0 there. So <paramref name="jobMapped"/> (poach.json map-membership by the
/// victim's own job id, PoachMap.TryGetJob) IS the whole monster gate now, no range check needed.
/// </summary>
internal static class LivingPoachPolicy
{
    /// <summary>Roll 0..255: below this is Common, at/above is Rare -- 225/256 common, 31/256
    /// rare (ffhacktics-documented split; corrects the folk 224/32 belief).</summary>
    internal const int RollCommonThreshold = 225;

    /// <summary>The whole eligibility+roll decision in one call. <paramref name="jobMapped"/> is
    /// PoachMap.TryGetJob(victimJob, ...) -- the sole monster-eligibility gate (see class doc).
    /// <paramref name="roll"/> is 0..255 (the caller's injected roll seam, kept out of this pure
    /// function so it stays testable).</summary>
    internal static PoachVerdict Decide(bool weaponIsDormantFormula, bool killerHasPoach, bool wasBasicAttack,
        int victimJob, bool jobMapped, int roll)
    {
        if (!weaponIsDormantFormula) return PoachVerdict.None;
        if (!killerHasPoach) return PoachVerdict.None;
        if (!wasBasicAttack) return PoachVerdict.None;
        if (!jobMapped) return PoachVerdict.None;
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
