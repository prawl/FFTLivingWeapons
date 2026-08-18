namespace LivingWeapon;

/// <summary>
/// CardSites.PaintSiteWithResult's per-site verdict (LW-257 commit 1). Mirrors SyncOutcome's
/// shape (AttackCard's own per-copy verify outcome, SyncOutcome.cs) so the equip-card paint path
/// gets the same fidelity AttackCard already had: CardSites previously collapsed every one of
/// these into either a bare bool (the old AnchorIsLive) or an anonymous PaintResult.NoWrite
/// (CardSites.cs's five collapsed early returns before this change), which is what let a single
/// transient unreadable anchor read look identical to a genuine buffer-reuse mismatch and get
/// evicted on the very first miss (the bug this arc fixes). Wrote and AlreadyEqual are both
/// healthy outcomes (the site stays cached); every other value names a specific reason the site
/// was refused a write, or evicted, this pass.
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

/// <summary>Short lowercase reason phrase for the flight tap / diagnostic log lines. Mirrors
/// SyncOutcomeReason.Phrase's convention exactly.</summary>
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
