namespace LivingWeapon;

/// <summary>
/// SyncHit/SyncHitEnc2's per-copy verify outcome, split out of AttackCard.Paint.cs. Lets FindHits
/// (AttackCard.Census.cs) and RepaintAll (AttackCard.Paint.cs) see why a copy was not kept without logging each
/// candidate, since a census walks thousands of foreign "Attack" strings per battle. Ok includes a transient not-yet-writable retry.
/// </summary>
internal enum SyncOutcome
{
    Ok,
    LabelGone,
    DescUnreadable,
    ForeignFootprint,
    ShapeMismatch,
    ForeignShape,
    NonVanillaUtf16,
}

/// <summary>Short reason phrase for RepaintAll's eviction log line; never called with Ok.</summary>
internal static class SyncOutcomeReason
{
    public static string Phrase(this SyncOutcome outcome) => outcome switch
    {
        SyncOutcome.LabelGone => "label-gone",
        SyncOutcome.DescUnreadable => "desc-unreadable",
        SyncOutcome.ForeignFootprint => "foreign-footprint",
        SyncOutcome.ShapeMismatch => "shape-mismatch",
        SyncOutcome.ForeignShape => "foreign-shape",
        SyncOutcome.NonVanillaUtf16 => "utf16-non-vanilla",
        _ => "unknown",
    };
}
