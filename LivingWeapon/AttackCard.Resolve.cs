namespace LivingWeapon;

/// <summary>
/// AttackCard's resolve/decide half, split out of AttackCard.Paint.cs to keep that file (the
/// verify/write half) under the 200-line refactor trigger: a real seam, this file answers "what
/// should the row/tail say right now" while AttackCard.Paint.cs answers "make the live copies say it".
/// </summary>
internal sealed partial class AttackCard
{
    /// <summary>The desired plan for one composed (non-vanilla) tick: the full 74-byte split image
    /// AttackRow.Policy.BuildImage produced, plus how many characters its row name occupies (needed
    /// by AttackRow.Policy.OffsetBytes' descOff math on every write).</summary>
    private readonly record struct ComposedPlan(byte[] Image, int RowNameChars);

    /// <summary>Resolve the acting unit's row+tail plan, CURSOR-ONLY (owner decision 2026-07-06;
    /// see AttackCard.cs's class doc for the full account): when the cursor resolve does not
    /// answer, the plan is null (restore vanilla), full stop. The register fallback stage 2 kept
    /// here served the LAST ACTED player's weapon on another unit's turn whenever the cursor
    /// refused an ambiguous fingerprint (the owner watched Ramza's row carry a Spark Rod), so it
    /// is gone from this surface. The Note half names the evidence for AttackCard.Paint.cs's
    /// compose-change Debug lines: which weapon/provenance a plan carries, or exactly WHY the plan
    /// is vanilla ("no cursor answer" vs "cursor resolved but the row composes vanilla"). Once a
    /// rosterBase is in hand, the decision is AttackRow.Policy.ComposeRow's alone, driven off the
    /// RAW main hand + sprite byte, never the filtered/tracked Hands() set (which cannot tell
    /// "unarmed" from "wielding something untracked", and would miss the "Fists" case entirely).
    /// Doctrine unchanged: a wrong dossier is worse than vanilla, so any guard failure or
    /// ambiguity falls through, never a guess.</summary>
    private (ComposedPlan? Plan, string Note) ComposeCurrentPlan()
    {
        var resolved = _resolveCursor();
        if (resolved == null) return (null, "no cursor answer");

        long rosterBase = resolved.Value.RosterBase;
        int rawMainHand = _rawMainHand(rosterBase);
        byte sprite = _spriteOf(rosterBase);
        string? metaName = _meta.TryGetValue(rawMainHand, out var m) ? m.Name : null;
        int kills = _kills.TryGetValue(rawMainHand, out int k) ? k : 0;

        var decision = AttackRow.ComposeRow(rawMainHand, metaName, kills, sprite);
        switch (decision.Kind)
        {
            case RowKind.Named:
                return (ComposeNamedPlan(decision.RowName!, m!, kills), $"weapon {rawMainHand} via cursor");
            case RowKind.Fist:
                return (new ComposedPlan(AttackRow.BuildImage("Fists", AttackCardTail.FistTail), "Fists".Length),
                        "unarmed human (Fists) via cursor");
            default:
                return (null, $"cursor resolved raw hand {rawMainHand} but the row composes vanilla (untracked weapon, or unarmed non-human)");
        }
    }

    /// <summary>Builds the Named-row plan: the tail's budget is whatever the row name leaves behind
    /// in the shared 73-char footprint (AttackRow.Policy.FootprintChars minus the row name's own
    /// length and its separating NUL).</summary>
    private ComposedPlan ComposeNamedPlan(string rowName, WeaponMeta m, int kills)
    {
        int tier = Tuning.TierFor(kills);
        // LW-35 (owner direction 2026-07-06): Marks are release-hidden. The per-weapon deed
        // collection (LegendStore/Reliquary) continues untouched, only THIS display surface stops
        // consuming it, so markLabel is always null here.
        string? markLabel = null;
        // CHANGE 3 (owner decision 2026-07-06): a real signature always earns a clause: either
        // " armed" once the tier is reached, or a locked "Unlocks {label}" tease before it --
        // rather than only ever showing once earned.
        string? sigLabel = m.Signature != null && !string.IsNullOrEmpty(m.Signature.DisplayLabel)
            ? m.Signature.DisplayLabel
            : null;
        bool sigEarned = sigLabel != null && Signatures.Earned(m.Signature, tier);

        int budget = AttackRow.FootprintChars - rowName.Length - 1;
        string tail = AttackCardTail.ComposeTail(kills, markLabel, sigLabel, sigEarned, budget);
        return new ComposedPlan(AttackRow.BuildImage(rowName, tail), rowName.Length);
    }
}
