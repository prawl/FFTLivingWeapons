namespace LivingWeapon;

/// <summary>
/// The enc==2 (UTF16) path, split out of AttackCard.Paint.cs for the 200-line trigger. AttackRow's
/// record mechanism is enc1-only (AttackRow.Policy.cs), and the census has found no enc2 "Attack"
/// catalogs, so this is a safety net. An enc2 copy never gets a composed image, only a vanilla restore.
/// </summary>
internal sealed partial class AttackCard
{
    /// <summary>Verify the label, then require the desc to already read vanilla. Rejects with zero writes
    /// on a label mismatch, an unreadable desc, or non-vanilla text; only RestoreVanillaBestEffortEnc2 writes.</summary>
    private SyncOutcome SyncHitEnc2(Hit hit)
    {
        byte[] labelPattern = AttackCardProbeText.Pattern(2);
        if (!_mem.TryReadBytes(hit.LabelAddr, labelPattern.Length, out var curLabel) || !ByteEq(curLabel, labelPattern))
            return SyncOutcome.LabelGone;

        int capBytes = DescCapChars * 2;
        if (!_mem.TryReadBytes(hit.DescAddr, capBytes, out var descBuf)) return SyncOutcome.DescUnreadable;
        var (curText, _) = AttackCardProbeText.ReadDesc(descBuf, 0, 2, DescCapChars);

        if (curText != AttackCardText.VanillaDesc) return SyncOutcome.NonVanillaUtf16;

        hit.DescChars = AttackCardText.DefaultBudgetChars;   // LW-33 re-pin: a full live read just confirmed vanilla
        return SyncOutcome.Ok;   // already vanilla; enc2 never receives a composed write
    }

    /// <summary>Best-effort flat vanilla-text write for an enc2 copy, only when the text drifted since the
    /// last SyncHitEnc2 pass. Does not touch offset/record bytes.</summary>
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
