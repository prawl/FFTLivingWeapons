namespace LivingWeapon;

/// <summary>
/// LW-31 stage 3 (docs/TODO.md). Composes the description tail after the row name that AttackRow.Policy.ComposeRow already set.
/// The head is a kill meter, "Kills: {kills}/{nextThreshold} to {nextSuffix}" below max tier (via <see cref="Tuning.NextThresholdForIn"/>), plain "Kills: {kills}" at max (2026-07-06).
/// The signature clause is disabled (2026-07-07); <see cref="ComposeTail"/> forces it null and the params stay so re-enabling is a one-line revert. Its old faces were "{sigLabel} armed" when earned and "Unlocks {sigLabel}" when locked.
/// Clauses join with <see cref="ClauseSep"/> and the tail never ends in a period (2026-07-06 complaint about the "+." ending). GenericTail and FistTail are fixed sentences and keep theirs.
/// Priority is head, Mark (LW-35 hides it at the call site), then signature. Trailing clauses drop whole when they overflow budgetChars, which the caller derives from AttackRow.Policy.FootprintChars minus the row name and its NUL.
/// GenericTail fires only when the head alone does not fit. Fist rows (AttackRow.Policy.RowKind.Fist) skip this method and use FistTail.
/// </summary>
internal static class AttackCardTail
{
    /// <summary>Last-resort tail when even the "Kills: ..." head does not fit, so a renamed row never reverts to a bare "Attack" desc.</summary>
    internal const string GenericTail = "Attacks with the equipped weapon.";

    /// <summary>The unarmed-human row's tail (AttackRow.Policy.RowKind.Fist). <see cref="ComposeTail"/> never produces it.</summary>
    internal const string FistTail = "Attacks with bare fists.";

    /// <summary>Clause separator. Interior periods only, so a composed tail cannot end with one.</summary>
    private const string ClauseSep = ". ";

    /// <param name="sigLabel">The weapon's signature label, or null for none (no clause then).</param>
    /// <param name="sigEarned">True renders "{sigLabel} armed", false the locked tease "Unlocks {sigLabel}". Ignored when sigLabel is null.</param>
    public static string ComposeTail(int kills, string? markLabel, string? sigLabel, bool sigEarned, int budgetChars)
    {
        string head = ComposeHead(kills, Tuning.KillThresholds, Tuning.Suffix);
        if (head.Length > budgetChars) return GenericTail;   // head alone does not fit

        // signature clause disabled (2026-07-07)
        string? sigClause = null;

        string withMark = markLabel != null ? head + ClauseSep + markLabel : head;
        string withSig = sigClause != null ? withMark + ClauseSep + sigClause : withMark;

        if (withSig.Length <= budgetChars) return withSig;
        if (withMark.Length <= budgetChars) return withMark;
        return head;
    }

    /// <summary>The meter head, "Kills: {kills}/{nextThreshold} to {nextSuffix}" below max tier and "Kills: {kills}" at max.
    /// Thresholds and suffixes are parameters (like Tuning.TierForIn) so tests can hit the DEV curve without a DEV build.</summary>
    internal static string ComposeHead(int kills, int[] thresholds, string[] suffixes)
    {
        int? next = Tuning.NextThresholdForIn(kills, thresholds);
        if (next == null) return $"Kills: {kills}";
        int tier = Tuning.TierForIn(kills, thresholds);
        return $"Kills: {kills}/{next} to {suffixes[tier + 1].TrimEnd()}";
    }
}
