using System;

namespace LivingWeapon;

/// <summary>
/// The census-proven vanilla Attack desc constant and its footprint budget (docs/TODO.md LW-31).
/// Stage 2 shipped this class's own Compose method (a flat "{name}{suffix} Kills: N." desc line,
/// since the row itself still read the generic "Attack" prison); stage 3 cracked the row rename
/// (AttackRow.Policy.ComposeRow) and moved the description half to AttackCardTail.ComposeTail, so
/// Compose is retired: this class now exists only to hold the shared vanilla-text facts both of
/// those consult (AttackRow.Policy.FootprintChars reuses <see cref="DefaultBudgetChars"/>, and
/// AttackCard.cs's vanilla plan/image is built directly off <see cref="VanillaDesc"/>).
/// </summary>
internal static class AttackCardText
{
    /// <summary>The canonical vanilla Attack description, live-census-proven exactly 73 ASCII
    /// chars (AttackCardSpike, commit 1272a6c). AttackCard.cs restores exactly this text whenever
    /// the resolved plan is vanilla (an unarmed/unstoried acting unit, or battle exit).</summary>
    internal const string VanillaDesc = "Attacks with the equipped weapon, or bare fists if no weapon is equipped.";

    /// <summary>The default write budget: the vanilla desc's own length, so a composed line can
    /// never exceed the footprint the census originally found.</summary>
    internal const int DefaultBudgetChars = 73;

    // Compile-time-checked invariant (as close as C# allows, mirrors AttackCardProbeText's own
    // static-ctor sanity check): if the census's proven 73-char fact is ever mistyped here, fail
    // loudly at class load instead of silently mis-budgeting every composed line.
    static AttackCardText()
    {
        if (VanillaDesc.Length != DefaultBudgetChars)
            throw new InvalidOperationException(
                $"AttackCardText.VanillaDesc length ({VanillaDesc.Length}) must equal DefaultBudgetChars ({DefaultBudgetChars})");
    }
}
