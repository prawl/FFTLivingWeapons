using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// AttackCard's decide half. What the row and tail should say right now. AttackCard.Paint.cs
/// writes it. LW-55 put the cursor-answer gate check here too.
/// </summary>
internal sealed partial class AttackCard
{
    /// <summary>The plan for one composed tick. The 74-byte image from AttackRow.BuildImage and the
    /// row name length (AttackRow.Policy.OffsetBytes needs it for descOff).</summary>
    private readonly record struct ComposedPlan(byte[] Image, int RowNameChars);

    /// <summary>LW-55 dedup. Each (refusal kind, roster hand, band weapon) is reported once per
    /// battle so a stuck hover does not spam the log or flight ring. Cleared in ResetBattle.</summary>
    private readonly HashSet<(CursorRefusal Kind, int RosterHand, int BandWeapon)> _reportedRefusals = new();

    /// <summary>LW-87 dedup. Each <see cref="CursorMiss"/> stage is reported once per battle.
    /// Cleared in ResetBattle.</summary>
    private readonly HashSet<CursorMiss> _reportedMisses = new();

    /// <summary>Resolve the acting unit's row and tail plan from the cursor only (2026-07-06, see
    /// AttackCard.cs). No cursor answer gives a null plan (vanilla); the old register fallback showed
    /// the last actor's weapon (a Spark Rod on Ramza's row) and was removed. a cursor answer
    /// must also pass <see cref="CursorGate.Decide"/>, since roster hand and band weapon can disagree
    /// (stale roster row, LW-87 turn-flag race). Note says why a plan is vanilla, for the compose
    /// Debug lines. After the gate, AttackRow.ComposeRow decides from the raw main hand and sprite
    /// byte, not Hands(), which cannot tell unarmed from an untracked weapon.</summary>
    private (ComposedPlan? Plan, string Note) ComposeCurrentPlan()
    {
        var (resolved, miss) = _resolveCursor();
        if (resolved == null)
        {
            ReportMiss(miss);
            return (null, $"no cursor answer: {miss}");
        }

        var answer = resolved.Value;
        var refusal = CursorGate.Decide(answer.RosterHand, answer.BandWeapon, answer.TurnFlag);
        if (refusal != CursorRefusal.None)
        {
            ReportRefusal(refusal, answer);
            return (null, $"cursor unit refused: {refusal} rosterHand={answer.RosterHand} bandWeapon={answer.BandWeapon} slot={answer.BandSlot}");
        }

        long rosterBase = answer.RosterBase;
        int rawMainHand = answer.RosterHand;
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

    /// <summary>LW-55 tripwire. <see cref="CursorRefusal.WeaponMismatch"/> is the real anomaly (a
    /// wrong dossier nearly painted): Warn plus flight record. <see cref="CursorRefusal.NotTurnOwner"/>
    /// is, since LW-87, a rare one-tick turn-flag race. Debug plus flight record. Deduped via
    /// <see cref="_reportedRefusals"/>.</summary>
    private void ReportRefusal(CursorRefusal refusal, CursorAnswer answer)
    {
        var key = (refusal, answer.RosterHand, answer.BandWeapon);
        if (!_reportedRefusals.Add(key)) return;

        string detail = $"rosterHand={answer.RosterHand} bandWeapon={answer.BandWeapon} slot={answer.BandSlot}";
        if (refusal == CursorRefusal.WeaponMismatch)
            ModLogger.Warn(LogVerb.Display,
                $"The Attack card's cursor unit disagrees on weapon ({detail}); the row composes vanilla instead of a wrong dossier.");
        else
            ModLogger.Debug(LogVerb.Display,
                $"The Attack card's cursor unit's turn flag fell before this read ({detail}); the row composes vanilla for this one tick.");
        _recorder?.Invoke("card", $"{refusal} {detail}");
    }

    /// <summary>the first miss of each <see cref="CursorMiss"/> stage per battle gets a Debug
    /// line and a flight "card" record ("miss:&lt;stage&gt;"), deduped by <see cref="_reportedMisses"/>.
    /// Never a Warn. NoOwner fires between any two turns and BridgeFail on every enemy turn.</summary>
    private void ReportMiss(CursorMiss miss)
    {
        if (miss == CursorMiss.None) return;
        if (!_reportedMisses.Add(miss)) return;

        ModLogger.Debug(LogVerb.Display,
            $"The Attack card's cursor resolve has no answer this tick ({miss}); the row composes vanilla.");
        _recorder?.Invoke("card", $"miss:{miss}");
    }

    /// <summary>Builds the Named-row plan. The tail gets the 73-char footprint minus the row name and its NUL.</summary>
    private ComposedPlan ComposeNamedPlan(string rowName, WeaponMeta m, int kills)
    {
        int tier = Tuning.TierFor(kills);
        // Marks are hidden from this surface; LegendStore/Reliquary still collect them.
        string? markLabel = null;
        // A signature always gets a clause, " armed" once earned, else an "Unlocks {label}" tease (2026-07-06).
        string? sigLabel = m.Signature != null && !string.IsNullOrEmpty(m.Signature.DisplayLabel)
            ? m.Signature.DisplayLabel
            : null;
        bool sigEarned = sigLabel != null && Signatures.Earned(m.Signature, tier);

        int budget = AttackRow.FootprintChars - rowName.Length - 1;
        string tail = AttackCardTail.ComposeTail(kills, markLabel, sigLabel, sigEarned, budget);
        return new ComposedPlan(AttackRow.BuildImage(rowName, tail), rowName.Length);
    }
}
