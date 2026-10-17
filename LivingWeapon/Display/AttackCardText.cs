using System;

namespace LivingWeapon;

/// <summary>
/// Vanilla Attack description text and its footprint budget (LW-31, docs/TODO.md).
/// AttackRow.Policy.ComposeRow and AttackCardTail.ComposeTail build the composed row and
/// description; this class holds the shared facts. <see cref="DefaultBudgetChars"/> is the
/// budget and AttackCard.cs builds its vanilla plan from <see cref="VanillaDesc"/>.
/// </summary>
internal static class AttackCardText
{
    /// <summary>Vanilla Attack description, 73 ASCII chars per the census (AttackCardSpike,
    /// commit 1272a6c). AttackCard.cs restores it when the plan is vanilla.</summary>
    internal const string VanillaDesc = "Attacks with the equipped weapon, or bare fists if no weapon is equipped.";

    /// <summary>Write budget. The vanilla description's length.</summary>
    internal const int DefaultBudgetChars = 73;

    // Fails at class load if VanillaDesc is mistyped (same check as AttackCardProbeText).
    static AttackCardText()
    {
        if (VanillaDesc.Length != DefaultBudgetChars)
            throw new InvalidOperationException(
                $"AttackCardText.VanillaDesc length ({VanillaDesc.Length}) must equal DefaultBudgetChars ({DefaultBudgetChars})");
    }
}
