namespace LivingWeapon;

/// <summary>
/// AttackCardTail.ComposeTail is LW-31 stage 3's pure tail-compose policy (docs/TODO.md): once
/// AttackRow.Policy.ComposeRow has already renamed the row itself (weapon name + trimmed tier
/// suffix, or "Fists"), this composes only the DESCRIPTION TAIL that follows it in the split image.
///
/// THE KILLS CLAUSE (owner decision 2026-07-06) is a TIER-PROGRESS METER, not a flat count: below
/// max tier, "Kills: {kills}/{nextThreshold} to {nextSuffix}" where nextThreshold/nextSuffix come
/// off Tuning's own kill-tier thresholds and Suffix array (via <see cref="Tuning.NextThresholdForIn"/>,
/// so the meter can never drift out of sync with the tier math it displays); at max tier, plain
/// "Kills: {kills}" (nowhere further to climb). Zero kills is no longer special for a living
/// (named) weapon: the meter renders from kill zero exactly like any other below-max count.
///
/// THE SIGNATURE CLAUSE IS TEMPORARILY DISABLED (owner decision 2026-07-07): the clause described
/// below never renders right now, regardless of <paramref name="sigLabel"/>/<paramref
/// name="sigEarned"/>; <see cref="ComposeTail"/> forces it null unconditionally. The params are
/// retained (and still validated by tests) purely so re-enabling is a one-line revert. Historical
/// shape, kept for that revert (owner decision 2026-07-06, same day): one slot with two mutually
/// exclusive faces, keyed off the same Signatures.Earned check the old sig clause used: earned
/// renders "{sigLabel} armed" exactly as before; LOCKED (a real signature the wielder hasn't
/// reached the tier for yet) renders a tease, "Unlocks {sigLabel}", instead. No signature at all
/// (<paramref name="sigLabel"/> null, see <see cref="ComposeTail"/>) means no clause, meter only.
///
/// PUNCTUATION (owner nitpick 2026-07-06, same day): every clause is composed WITHOUT a terminal
/// period and clauses are joined by exactly ". " (<see cref="ClauseSep"/>), so the returned tail
/// never ends with '.' (the complaint was specifically the meter's "+." ending).
/// <see cref="GenericTail"/> and <see cref="FistTail"/> deliberately KEEP their periods: they are
/// fixed full sentences, not composed clause chains.
///
/// Both the optional Mark clause (LW-35 hides it from the production call site, but the pure
/// contract stays symmetric) and the signature clause stay purely additive and tried in the SAME
/// FIXED PRIORITY as before (never sorted by length): head, then Mark, then signature. Trailing
/// clauses are dropped WHOLE, from the tail inward, when they would overflow
/// <c>budgetChars</c> (renamed in the caller from AttackRow.Policy.FootprintChars minus the row
/// name's own length and separating NUL); a clause is NEVER truncated mid-way.
///
/// <see cref="GenericTail"/> now fires ONLY when even the mandatory head cannot fit the budget (a
/// defensive floor, not a "nothing earned yet" state; that rule died with the zero-kills special
/// case): a renamed row can never revert to a bare "Attack" desc. <see cref="FistTail"/> is the
/// unarmed-human row's fixed tail (AttackRow.Policy.RowKind.Fist bypasses this method entirely; the
/// caller uses the constant directly).
/// </summary>
internal static class AttackCardTail
{
    /// <summary>Floor for when even the mandatory "Kills: ..." head cannot fit the budget: a
    /// renamed row can never revert to a bare "Attack" desc, so this is the last resort every
    /// Named-row tail can fall back to. Keeps its trailing period (a fixed full sentence, exempt
    /// from the composed chain's no-trailing-period rule).</summary>
    internal const string GenericTail = "Attacks with the equipped weapon.";

    /// <summary>The unarmed-human row's fixed tail (AttackRow.Policy.RowKind.Fist). Not produced by
    /// <see cref="ComposeTail"/>; the Fist decision bypasses kills/Mark/Sig entirely. Keeps its
    /// trailing period (same exemption as <see cref="GenericTail"/>).</summary>
    internal const string FistTail = "Attacks with bare fists.";

    /// <summary>The one clause separator: interior periods only, so a composed tail can never end
    /// with '.' by construction (no trim pass needed).</summary>
    private const string ClauseSep = ". ";

    /// <param name="sigLabel">The weapon's signature display label, or null when it has none at
    /// all (no clause is composed in that case, regardless of <paramref name="sigEarned"/>).</param>
    /// <param name="sigEarned">Whether the wielder has reached the signature's tier: true renders
    /// "{sigLabel} armed"; false renders the locked tease "Unlocks {sigLabel}" instead. Ignored
    /// when <paramref name="sigLabel"/> is null.</param>
    public static string ComposeTail(int kills, string? markLabel, string? sigLabel, bool sigEarned, int budgetChars)
    {
        string head = ComposeHead(kills, Tuning.KillThresholds, Tuning.Suffix);
        if (head.Length > budgetChars) return GenericTail;   // not even the mandatory head fits

        // signature clause disabled for now (owner 2026-07-07); restore the earned/locked faces to re-enable
        string? sigClause = null;

        string withMark = markLabel != null ? head + ClauseSep + markLabel : head;
        string withSig = sigClause != null ? withMark + ClauseSep + sigClause : withMark;

        if (withSig.Length <= budgetChars) return withSig;
        if (withMark.Length <= budgetChars) return withMark;
        return head;
    }

    /// <summary>The tier-progress meter head: "Kills: {kills}/{nextThreshold} to {nextSuffix}"
    /// below max tier, plain "Kills: {kills}" at max tier (nowhere further to climb); no terminal
    /// period either way (the clause-chain rule above). Takes thresholds/suffixes explicitly
    /// (mirrors Tuning.TierForIn's own shape) so a test can exercise the DEV curve's degenerate
    /// boundaries directly without a DEV-flavored compile;
    /// <see cref="ComposeTail"/> always calls it with the compiled Tuning.KillThresholds/Suffix.</summary>
    internal static string ComposeHead(int kills, int[] thresholds, string[] suffixes)
    {
        int? next = Tuning.NextThresholdForIn(kills, thresholds);
        if (next == null) return $"Kills: {kills}";
        int tier = Tuning.TierForIn(kills, thresholds);
        return $"Kills: {kills}/{next} to {suffixes[tier + 1].TrimEnd()}";
    }
}
