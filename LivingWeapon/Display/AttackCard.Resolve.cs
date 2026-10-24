using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// AttackCard's decide half. What the row and tail should say right now. AttackCard.Paint.cs
/// writes it. The cursor-answer gate check sits here too.
/// </summary>
internal sealed partial class AttackCard
{
    /// <summary>The plan for one composed tick. The 74-byte image from AttackRow.BuildImage and the
    /// row name length (AttackRow.Policy.OffsetBytes needs it for descOff).</summary>
    private readonly record struct ComposedPlan(byte[] Image, int RowNameChars);

    /// <summary>Refusal dedup. Each (refusal kind, roster hand, band weapon, off hand flag) is reported once per
    /// battle so a stuck hover does not spam the log or flight ring. Cleared in ResetBattle.</summary>
    private readonly HashSet<(CursorRefusal Kind, int RosterHand, int BandWeapon, bool OffHand)> _reportedRefusals = new();

    /// <summary>Miss dedup. Each <see cref="CursorMiss"/> stage is reported once per battle.
    /// Cleared in ResetBattle.</summary>
    private readonly HashSet<CursorMiss> _reportedMisses = new();

    /// <summary>Works out what the Attack row should say for the unit whose turn it is, the one holding the turn flag.
    /// A null plan means vanilla, and Note says why for the compose Debug lines.</summary>
    private (ComposedPlan? Plan, string Note) ComposeCurrentPlan()
    {
        // Never fall back to a register, which showed the last actor's weapon (a Spark Rod on Ramza's row).
        var (resolved, miss) = _resolveCursor();
        if (resolved == null)
        {
            ReportMiss(miss);
            return (null, $"no cursor answer: {miss}");
        }

        var answer = resolved.Value;
        // Roster hand and band weapon can disagree (stale roster row, turn-flag race), so the gate must pass too.
        var refusal = CursorGate.Decide(answer.RosterHand, answer.BandWeapon, answer.TurnFlag, answer.RosterHandFromOffHand);
        if (refusal != CursorRefusal.None)
        {
            ReportRefusal(refusal, answer);
            return (null, $"cursor unit refused: {refusal} rosterHand={answer.RosterHand} bandWeapon={answer.BandWeapon} slot={answer.BandSlot} offHand={answer.RosterHandFromOffHand}");
        }

        long rosterBase = answer.RosterBase;
        int attackHand = answer.RosterHand;
        byte sprite = _spriteOf(rosterBase);
        string? metaName = _meta.TryGetValue(attackHand, out var m) ? m.Name : null;
        int kills = _kills.TryGetValue(attackHand, out int k) ? k : 0;

        // Decide from the attack hand and sprite byte, not Hands, which cannot tell unarmed from an untracked weapon.
        var decision = AttackRow.ComposeRow(attackHand, metaName, kills, sprite);
        switch (decision.Kind)
        {
            case RowKind.Named:
                return (ComposeNamedPlan(decision.RowName!, m!, kills, answer.RosterHandFromOffHand), $"weapon {attackHand} via cursor");
            case RowKind.Fist:
                return (new ComposedPlan(AttackRow.BuildImage("Fists", AttackCardTail.FistTail), "Fists".Length),
                        "unarmed human (Fists) via cursor");
            default:
                return (null, $"cursor resolved raw hand {attackHand} but the row composes vanilla (untracked weapon, or unarmed non-human)");
        }
    }

    /// <summary>Reports a unit the card refused to show, once per kind, hand, weapon and off hand flag via <see cref="_reportedRefusals"/>. <see cref="CursorRefusal.WeaponMismatch"/>
    /// is the real anomaly (a wrong dossier nearly painted), so Warn plus flight record. NotTurnOwner is a rare one-tick turn-flag race, so Debug plus flight record.</summary>
    private void ReportRefusal(CursorRefusal refusal, CursorAnswer answer)
    {
        var key = (refusal, answer.RosterHand, answer.BandWeapon, answer.RosterHandFromOffHand);
        if (!_reportedRefusals.Add(key)) return;

        string detail = $"rosterHand={answer.RosterHand} bandWeapon={answer.BandWeapon} slot={answer.BandSlot} offHand={answer.RosterHandFromOffHand}";
        if (refusal == CursorRefusal.WeaponMismatch)
            ModLogger.Warn(LogVerb.Display,
                $"The Attack card's cursor unit disagrees on weapon ({detail}); the row composes vanilla instead of a wrong dossier.");
        else
            ModLogger.Debug(LogVerb.Display,
                $"The Attack card's cursor unit's turn flag fell before this read ({detail}); the row composes vanilla for this one tick.");
        _recorder?.Invoke("card", $"{refusal} {detail}");
    }

    /// <summary>Logs the first time the card finds no unit to show, once per <see cref="CursorMiss"/> stage per battle, as a Debug
    /// line plus a flight "card" record ("miss:&lt;stage&gt;"). Never a Warn, since NoOwner fires between any two turns and BridgeFail on every enemy turn.</summary>
    private void ReportMiss(CursorMiss miss)
    {
        if (miss == CursorMiss.None) return;
        if (!_reportedMisses.Add(miss)) return;

        ModLogger.Debug(LogVerb.Display,
            $"The Attack card's cursor resolve has no answer this tick ({miss}); the row composes vanilla.");
        _recorder?.Invoke("card", $"miss:{miss}");
    }

    /// <summary>A weapon in the off hand never advertises its signature, since signatures fire from the main hand only.</summary>
    internal static string? SignatureLabel(WeaponMeta m, bool offHand) =>
        !offHand && m.Signature != null && !string.IsNullOrEmpty(m.Signature.DisplayLabel) ? m.Signature.DisplayLabel : null;

    /// <summary>Builds the Named-row plan. The tail gets the 73-char footprint minus the row name and its NUL.</summary>
    private ComposedPlan ComposeNamedPlan(string rowName, WeaponMeta m, int kills, bool offHand)
    {
        int tier = Tuning.TierFor(kills);
        // The card shows no Marks, though LegendStore still collects them.
        string? markLabel = null;
        // Shows the signature move for a main hand weapon only, " armed" once earned, else an "Unlocks {label}" tease.
        // An off hand weapon gets no clause, because signatures fire from the main hand only.
        string? sigLabel = SignatureLabel(m, offHand);
        bool sigEarned = sigLabel != null && Signatures.Earned(m.Signature, tier);

        int budget = AttackRow.FootprintChars - rowName.Length - 1;
        string tail = AttackCardTail.ComposeTail(kills, markLabel, sigLabel, sigEarned, budget);
        return new ComposedPlan(AttackRow.BuildImage(rowName, tail), rowName.Length);
    }
}
