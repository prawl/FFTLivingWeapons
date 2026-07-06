using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// AttackCard's compose/write half: resolves the acting unit's dossier line (the KillerStamp
/// seam), drives the three-way anchor rotation on a genuine compose-change, and does the
/// guarded per-copy verify+write (or eviction, when a cached copy no longer holds a known line).
/// </summary>
internal sealed partial class AttackCard
{
    /// <summary>Recompose every tick (cheap: no memory access beyond the register/roster reads
    /// already happening elsewhere this tick). A genuine content change rotates the anchor and
    /// repaints every cached copy immediately; an unchanged compose falls back to the throttled
    /// maintenance cadence (Display.MaintenanceMs's own pattern) so drift/new copies still converge.</summary>
    private void RepaintDriver()
    {
        string? composed = ComposeCurrentLine();
        if (composed != _current)
        {
            if (composed != null)
                ModLogger.Event(LogVerb.Display, "The Attack menu's description now carries a Living Weapon's dossier.");
            else
                ModLogger.Debug(LogVerb.Display, "attack-card desc reverting to vanilla: the acting unit's weapon is unarmed or unstoried");

            if (_current != null) _previous = _current;
            _current = composed;
            RepaintAll();
            return;
        }

        long now = _nowMs();
        if (now - _lastMaintenanceMs < MaintenanceMs) return;
        _lastMaintenanceMs = now;
        RepaintAll();
    }

    /// <summary>Resolve the acting unit's dossier line, CURSOR-FIRST with a register fallback
    /// (LW-31 stage-2 fix, ledger LW-31): tries ActorResolver.TryResolveCursorPlayer (the condensed
    /// turn-queue struct, which snaps to the unit whose turn just opened, BEFORE any action, per
    /// the 2026-07-05 TurnOwnerSpike tape) first, falling back to <see cref="RegisterHands"/> (the
    /// KillerStamp seam this method always trusted) only when the cursor's guards don't clear (an
    /// enemy/ally turn, the taped flicker, no roster bridge, or an unreadable struct: see
    /// ActorResolver.Cursor.cs for the full guard list). Null (restore vanilla) when NEITHER seam
    /// answers, the resolved player holds no tracked weapon, or that weapon's own compose has
    /// nothing to show yet (AttackCardText.Compose's decision 8 mirror). Doctrine: a wrong dossier
    /// is worse than vanilla, so any guard failure or ambiguity falls through, never a guess.</summary>
    private string? ComposeCurrentLine()
    {
        var hands = _resolveCursor() ?? RegisterHands();
        if (hands == null || hands.Count == 0) return null;   // no answer, or resolved-but-untracked

        int weaponId = hands[0];   // RRHand-priority (ActorResolver.Hands): the main/first tracked hand
        if (!_meta.TryGetValue(weaponId, out var m)) return null;

        int kills = _kills.TryGetValue(weaponId, out int k) ? k : 0;
        int tier = Tuning.TierFor(kills);
        string suffix = Tuning.Suffix[tier];

        var legend = _legends.Get(weaponId);
        var bestMark = VictimClass.BestMark(legend, out _);
        string? markLabel = bestMark.HasValue ? VictimClass.MarkTitle(bestMark.Value) : null;

        string? sigName = m.Signature != null && !string.IsNullOrEmpty(m.Signature.DisplayLabel)
                          && Signatures.Earned(m.Signature, tier)
            ? m.Signature.DisplayLabel
            : null;

        return AttackCardText.Compose(m.Name, suffix, kills, markLabel, sigName);
    }

    /// <summary>The register-only resolve this method used before the LW-31 stage-2 cursor-first
    /// fix: the KillerStamp seam (ActorRegister.LastPlayerRosterBase/LastPlayerArrivalTick/
    /// Trusted, then HandsFromRoster). Kept as the fallback for whenever the cursor's guards don't
    /// clear.</summary>
    private List<int>? RegisterHands()
    {
        if (!_register.Trusted || _register.LastPlayerArrivalTick <= 0 || _register.LastPlayerRosterBase == 0)
            return null;
        return _handsFromRoster(_register.LastPlayerRosterBase);
    }

    private void RepaintAll()
    {
        List<Hit>? toEvict = null;
        foreach (var hit in _hits)
            if (!SyncHit(hit)) (toEvict ??= new List<Hit>()).Add(hit);

        if (toEvict == null) return;
        foreach (var h in toEvict) _hits.Remove(h);
        _needsCensus = true;   // re-census after any eviction (the anchor discipline: never guess a foreign buffer)
    }

    /// <summary>Verify one cached copy still holds the standalone label plus one of the three
    /// known lines, then (skip-if-equal) write the desired state. Returns false when the copy is
    /// foreign (caller evicts it); true otherwise, written or not.</summary>
    private bool SyncHit(Hit hit)
    {
        byte[] labelPattern = AttackCardProbeText.Pattern(hit.Enc);
        if (!_mem.TryReadBytes(hit.LabelAddr, labelPattern.Length, out var curLabel) || !ByteEq(curLabel, labelPattern))
            return false;   // race guard: this buffer no longer holds the Attack label at all

        int capBytes = DescCapChars * hit.Enc;
        if (!_mem.TryReadBytes(hit.DescAddr, capBytes, out var descBuf)) return false;
        var (curText, _) = AttackCardProbeText.ReadDesc(descBuf, 0, hit.Enc, DescCapChars);

        bool isKnown = curText == AttackCardText.VanillaDesc || curText == _current || curText == _previous;
        if (!isKnown)
        {
            ModLogger.Debug(LogVerb.Display, "attack-card desc no longer holds a known line: evicting the cached copy for a later re-census");
            return false;
        }

        // LW-33: this read is a FULL live read (capped at DescCapChars, the same 73-char cap the
        // census itself uses) that just confirmed curText is one of the three known lines, an even
        // stronger observation than the census-time pin (AttackCard.Census.cs's FindHits, which
        // only ever sees a bounded scan window). Re-pin the footprint to the vanilla desc's own
        // 73 chars every time, repairing a footprint that was somehow poisoned short instead of
        // merely avoiding poisoning a fresh one: the true footprint can never legitimately be less
        // than 73 whenever a known line is sitting there (same induction as the census pin).
        hit.DescChars = AttackCardText.DefaultBudgetChars;

        string desired = _current ?? AttackCardText.VanillaDesc;
        if (curText == desired) return true;   // skip-if-equal

        if (!AttackCardProbeText.FitsFootprint(hit.DescChars, desired.Length))
        {
            ModLogger.Debug(LogVerb.Display, "attack-card desc is too small for the composed line: skipping this write");
            return true;   // still a known/live copy; just can't take this particular write
        }
        if (!_mem.Writable(hit.DescAddr, desired.Length * hit.Enc + hit.Enc)) return true;   // transient: retry next pass

        _mem.WriteBytes(hit.DescAddr, AttackCardProbeText.EncodeWithTerminator(desired, hit.Enc));
        ModLogger.Debug(LogVerb.Display, "attack-card desc repainted");
        return true;
    }

    /// <summary>Best-effort vanilla restore for ResetBattle: only touches a copy that currently
    /// holds a KNOWN line (vanilla itself, current, or previous); a foreign buffer is left alone.
    /// Never throws.</summary>
    private void RestoreVanillaBestEffort()
    {
        foreach (var hit in _hits)
        {
            try
            {
                byte[] labelPattern = AttackCardProbeText.Pattern(hit.Enc);
                if (!_mem.TryReadBytes(hit.LabelAddr, labelPattern.Length, out var curLabel) || !ByteEq(curLabel, labelPattern))
                    continue;

                int capBytes = DescCapChars * hit.Enc;
                if (!_mem.TryReadBytes(hit.DescAddr, capBytes, out var descBuf)) continue;
                var (curText, _) = AttackCardProbeText.ReadDesc(descBuf, 0, hit.Enc, DescCapChars);
                if (curText == AttackCardText.VanillaDesc) continue;   // already vanilla
                if (curText != _current && curText != _previous) continue;   // foreign: leave it alone

                if (!AttackCardProbeText.FitsFootprint(hit.DescChars, AttackCardText.VanillaDesc.Length)) continue;
                if (!_mem.Writable(hit.DescAddr, AttackCardText.VanillaDesc.Length * hit.Enc + hit.Enc)) continue;
                _mem.WriteBytes(hit.DescAddr, AttackCardProbeText.EncodeWithTerminator(AttackCardText.VanillaDesc, hit.Enc));
            }
            catch (Exception ex)
            {
                ModLogger.Error(LogVerb.Display, "Restoring the vanilla Attack desc failed for one table copy: " + ex.Message);
            }
        }
    }
}
