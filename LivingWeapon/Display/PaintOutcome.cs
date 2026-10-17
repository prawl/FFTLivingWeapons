namespace LivingWeapon;

/// <summary>
/// CardSites.PaintSiteWithResult's per-site verdict (LW-257 commit 1), shaped like SyncOutcome
/// (SyncOutcome.cs). It replaces the old AnchorIsLive bool and PaintResult.NoWrite, which made a
/// transient unreadable anchor look like a buffer-reuse mismatch and evicted the site on the first miss.
/// Wrote and AlreadyEqual keep the site cached; every other value names why it was refused or evicted.
/// </summary>
internal enum PaintOutcome
{
    Wrote,
    AlreadyEqual,
    SlotUnreadable,
    SlotShapeRefused,
    NotWritable,
    AnchorMismatch,
    AnchorUnreadable,
}

/// <summary>Short lowercase reason phrase for flight tap and log lines, as in
/// SyncOutcomeReason.Phrase.</summary>
internal static class PaintOutcomeReason
{
    public static string Phrase(this PaintOutcome outcome) => outcome switch
    {
        PaintOutcome.Wrote => "wrote",
        PaintOutcome.AlreadyEqual => "already-equal",
        PaintOutcome.SlotUnreadable => "slot-unreadable",
        PaintOutcome.SlotShapeRefused => "slot-shape-refused",
        PaintOutcome.NotWritable => "not-writable",
        PaintOutcome.AnchorMismatch => "anchor-mismatch",
        PaintOutcome.AnchorUnreadable => "anchor-unreadable",
        _ => "unknown",
    };
}
