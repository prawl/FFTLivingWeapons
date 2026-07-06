namespace LivingWeapon;

/// <summary>
/// The enc==2 (UTF16) dead path, split out of AttackCard.Paint.cs to keep that file under the
/// 200-line refactor trigger. AttackRow's record mechanism is enc1-only (AttackRow.Policy.cs's
/// class doc); the live census has found zero enc2 "Attack" catalogs, so this exists purely as a
/// safety net in case one ever appears. An enc2 copy NEVER receives a composed image; its only
/// possible fate is vanilla-restore, mirroring the flat-text write AttackCard used before stage 3's
/// split-image mechanism existed, but now scoped to "vanilla only" instead of "whatever the desired
/// plan says" (the desired plan may be a split image today, which has no meaning for a flat-text
/// UTF16 buffer).
/// </summary>
internal sealed partial class AttackCard
{
    /// <summary>Verify the label, then require the desc to ALREADY read vanilla to keep this copy
    /// cached: an enc2 catalog is never a candidate for the split-image treatment, so unlike the
    /// enc1 path's three-way anchor, there is nothing else it could legitimately hold. Evicts
    /// (returns false, zero writes) on a label mismatch, an unreadable desc, or any non-vanilla
    /// text: the eviction itself performs no restore attempt here; RestoreVanillaBestEffortEnc2
    /// is the only place that ever attempts to write this branch's vanilla text back.</summary>
    private bool SyncHitEnc2(Hit hit)
    {
        byte[] labelPattern = AttackCardProbeText.Pattern(2);
        if (!_mem.TryReadBytes(hit.LabelAddr, labelPattern.Length, out var curLabel) || !ByteEq(curLabel, labelPattern))
            return false;

        int capBytes = DescCapChars * 2;
        if (!_mem.TryReadBytes(hit.DescAddr, capBytes, out var descBuf)) return false;
        var (curText, _) = AttackCardProbeText.ReadDesc(descBuf, 0, 2, DescCapChars);

        if (curText != AttackCardText.VanillaDesc)
        {
            ModLogger.Debug(LogVerb.Display, "attack row (utf16 copy) no longer holds vanilla: evicting the cached copy for a later re-census");
            return false;
        }

        hit.DescChars = AttackCardText.DefaultBudgetChars;   // LW-33 re-pin: a full live read just confirmed vanilla
        return true;   // already vanilla; enc2 never receives a composed write
    }

    /// <summary>Best-effort vanilla restore for an enc2 copy: attempts a flat vanilla-text write ONLY
    /// when the current text is not already vanilla (a drift that happened between this hit's last
    /// SyncHitEnc2 pass and battle exit). Never writes offset/record bytes: enc2 catalogs have no
    /// meaningful record to touch here.</summary>
    private void RestoreVanillaBestEffortEnc2(Hit hit)
    {
        byte[] labelPattern = AttackCardProbeText.Pattern(2);
        if (!_mem.TryReadBytes(hit.LabelAddr, labelPattern.Length, out var curLabel) || !ByteEq(curLabel, labelPattern)) return;

        int capBytes = DescCapChars * 2;
        if (!_mem.TryReadBytes(hit.DescAddr, capBytes, out var descBuf)) return;
        var (curText, _) = AttackCardProbeText.ReadDesc(descBuf, 0, 2, DescCapChars);
        if (curText == AttackCardText.VanillaDesc) return;   // already vanilla

        if (!AttackCardProbeText.FitsFootprint(hit.DescChars, AttackCardText.VanillaDesc.Length)) return;
        if (!_mem.Writable(hit.DescAddr, AttackCardText.VanillaDesc.Length * 2 + 2)) return;
        _mem.WriteBytes(hit.DescAddr, AttackCardProbeText.EncodeWithTerminator(AttackCardText.VanillaDesc, 2));
    }
}
