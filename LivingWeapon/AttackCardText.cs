using System;

namespace LivingWeapon;

/// <summary>
/// LW-31 stage 2's pure compose policy (docs/TODO.md): the battle Abilities menu's Attack
/// hover-card Description becomes the acting unit's weapon dossier. Census-proven surface
/// (AttackCardSpike, commit 1272a6c): the standalone C-string "Attack" is immediately followed by
/// the desc string, canonically the 73-char <see cref="VanillaDesc"/> (ASCII only in practice,
/// per the census). This class composes the REPLACEMENT desc text; AttackCard.cs (the runtime
/// half) owns finding, writing, and restoring the live table copies.
///
/// TOTAL ORDER: the mandatory head is the weapon's own identity plus its kill count:
/// "{name}{suffix} Kills: {kills}.", since the row itself keeps reading the generic "Attack"
/// until stage 3 (the row-rename crack), so this desc is the only surface that can say WHICH
/// weapon the hover describes. Two further clauses are purely additive and tried in FIXED
/// PRIORITY (never sorted by length, the same discipline as CardLine.Compose): " {Mark}." (the
/// highest earned Mark title) then " {SigName} armed." (the weapon's signature display label,
/// once its tier is earned). Trailing clauses are dropped WHOLE, from the tail inward, when they
/// would overflow budgetChars; a clause is NEVER truncated mid-way. If even the mandatory head
/// does not fit, the whole compose is null (nothing to show; the caller restores the vanilla
/// desc). Also null outright when the weapon has zero kills AND no Mark: nothing has been earned
/// yet, so the vanilla desc stays.
///
/// Unlike CardLine.Compose (which overwrites a FIXED-length flavor pattern in place and so must
/// right-pad to an exact budget), this write is NUL-terminated and need not fill the buffer: the
/// composed line is free to be shorter than the vanilla original. No padding rule applies here;
/// AttackCard.cs's own footprint check (AttackCardProbeText.FitsFootprint) covers the rest.
/// </summary>
internal static class AttackCardText
{
    /// <summary>The canonical vanilla Attack description, live-census-proven exactly 73 ASCII
    /// chars (AttackCardSpike, commit 1272a6c). AttackCard.cs restores exactly this text whenever
    /// Compose returns null (an unarmed/unstoried acting unit, or battle exit).</summary>
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

    /// <summary>Compose the Attack-menu desc replacement for one weapon, or null (meaning:
    /// restore the vanilla desc). <paramref name="suffix"/> is the raw 2-char card-slot value
    /// (Tuning.Suffix); its trailing padding is trimmed before concatenation ("+ " -&gt; "+",
    /// "  " -&gt; "").</summary>
    public static string? Compose(string weaponName, string suffix, int kills,
                                   string? markLabel, string? sigName, int budgetChars = DefaultBudgetChars)
    {
        if (kills <= 0 && markLabel == null) return null;   // decision 8 mirror: nothing earned yet

        string identity = weaponName + suffix.TrimEnd();
        string head = $"{identity} Kills: {kills}.";
        if (head.Length > budgetChars) return null;   // not even the mandatory head fits: nothing composable

        string withMark = markLabel != null ? head + $" {markLabel}." : head;
        string withSig = sigName != null ? withMark + $" {sigName} armed." : withMark;

        if (withSig.Length <= budgetChars) return withSig;
        if (withMark.Length <= budgetChars) return withMark;
        return head;
    }
}
