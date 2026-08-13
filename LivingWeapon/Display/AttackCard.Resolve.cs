using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// AttackCard's resolve/decide half, split out of AttackCard.Paint.cs to keep that file (the
/// verify/write half) under the 200-line refactor trigger: a real seam, this file answers "what
/// should the row/tail say right now" while AttackCard.Paint.cs answers "make the live copies say it".
/// LW-55 folded the cursor-answer's narrowing-only gate check into this same seam: judging the
/// raw facts is part of "what should the row say", not the write half's concern.
/// </summary>
internal sealed partial class AttackCard
{
    /// <summary>The desired plan for one composed (non-vanilla) tick: the full 74-byte split image
    /// AttackRow.Policy.BuildImage produced, plus how many characters its row name occupies (needed
    /// by AttackRow.Policy.OffsetBytes' descOff math on every write).</summary>
    private readonly record struct ComposedPlan(byte[] Image, int RowNameChars);

    /// <summary>LW-55's tripwire dedup set: a (refusal kind, roster hand, band weapon) triple that
    /// has already logged/recorded once this battle is never reported again, so a stuck hover does
    /// not spam either the log or the flight ring every tick. Cleared in AttackCard.cs's
    /// ResetBattle. Lives here (not AttackCard.cs, already at the 200-line trigger) because
    /// reporting a refusal is part of the resolve/decide half's own job.</summary>
    private readonly HashSet<(CursorRefusal Kind, int RosterHand, int BandWeapon)> _reportedRefusals = new();

    /// <summary>LW-87's tripwire dedup set: each <see cref="CursorMiss"/> stage that has already
    /// logged/recorded once this battle is never reported again, so a routine no-owner/bridge-fail
    /// gap does not spam either the log or the flight ring every tick. Cleared in AttackCard.cs's
    /// ResetBattle. Lives here (not AttackCard.cs, already at the 200-line trigger) because
    /// reporting a resolve miss is part of the resolve/decide half's own job.</summary>
    private readonly HashSet<CursorMiss> _reportedMisses = new();

    /// <summary>Resolve the acting unit's row+tail plan, CURSOR-ONLY (owner decision 2026-07-06;
    /// see AttackCard.cs's class doc for the full account): when the cursor resolve does not
    /// answer, the plan is null (restore vanilla), full stop. The register fallback stage 2 kept
    /// here served the LAST ACTED player's weapon on another unit's turn whenever the cursor
    /// refused an ambiguous fingerprint (the owner watched Ramza's row carry a Spark Rod), so it
    /// is gone from this surface. LW-55 adds a second refusal source on TOP of "no cursor answer
    /// at all": even a confident cursor answer is judged by <see cref="CursorGate.Decide"/> before
    /// it may compose anything, since the cursor resolve's raw roster-hand/band-weapon facts can
    /// disagree (a stale roster row, or a falling-edge race on the turn-flag byte itself, LW-87) in
    /// ways the resolver itself cannot see. The Note half names the evidence for AttackCard.Paint.cs's compose-change Debug
    /// lines: which weapon/provenance a plan carries, or exactly WHY the plan is vanilla. Once the
    /// gate clears, the decision is AttackRow.Policy.ComposeRow's alone, driven off the RAW main
    /// hand + sprite byte, never the filtered/tracked Hands() set (which cannot tell "unarmed"
    /// from "wielding something untracked", and would miss the "Fists" case entirely). Doctrine
    /// unchanged: a wrong dossier is worse than vanilla, so any guard or gate failure falls
    /// through, never a guess.</summary>
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

    /// <summary>LW-55's tiered tripwire: <see cref="CursorRefusal.WeaponMismatch"/> is the genuine
    /// anomaly this whole fix exists to catch (a wrong dossier was one tick from painting) and
    /// gets a Warn plus a flight record; <see cref="CursorRefusal.NotTurnOwner"/> is, since LW-87's
    /// re-anchor, a RARE falling-edge race (the flag owner's own turn-flag byte re-read low a tick
    /// after Band.FlagOwner's walk found it high) and gets Debug plus a flight record only, never a
    /// Warn (a benign one-tick refusal, not a genuine anomaly). Deduped to ONE report per (kind,
    /// rosterHand, bandWeapon) key per battle via <see cref="_reportedRefusals"/>.</summary>
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

    /// <summary>LW-87's resolve-stage observability tap: the FIRST occurrence of each
    /// <see cref="CursorMiss"/> stage per battle gets one Debug line plus one flight "card" record
    /// ("miss:&lt;stage&gt;"); every later tick with the SAME stage is silent
    /// (<see cref="_reportedMisses"/>, cleared in AttackCard.cs's ResetBattle). NEVER a Warn:
    /// <see cref="CursorMiss.NoOwner"/> fires in the gap between any two units' turns and
    /// <see cref="CursorMiss.BridgeFail"/> fires on every enemy turn -- both routine, not the
    /// genuine anomaly <see cref="ReportRefusal"/>'s WeaponMismatch tier exists to catch.
    /// <see cref="CursorMiss.None"/> is a no-op (nothing to report on a successful resolve).</summary>
    private void ReportMiss(CursorMiss miss)
    {
        if (miss == CursorMiss.None) return;
        if (!_reportedMisses.Add(miss)) return;

        ModLogger.Debug(LogVerb.Display,
            $"The Attack card's cursor resolve has no answer this tick ({miss}); the row composes vanilla.");
        _recorder?.Invoke("card", $"miss:{miss}");
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
