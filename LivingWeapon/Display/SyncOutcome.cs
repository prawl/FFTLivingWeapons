namespace LivingWeapon;

/// <summary>
/// SyncHit/SyncHitEnc2's per-copy verify outcome. Split out of AttackCard.Paint.cs so a census
/// sweep's caller (AttackCard.Census.cs's FindHits) and a live re-verify's caller (AttackCard.Paint.cs's
/// RepaintAll) can tell WHY a copy was not adopted/kept without either of them logging per-candidate:
/// a census walks thousands of foreign "Attack" strings a battle, most rejected, none of them a real
/// eviction (they were never cached). Ok covers every success path, including a transient
/// not-yet-writable retry (next pass, not a rejection); every other value names a specific reason a
/// caller should treat the copy as foreign/gone.
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

/// <summary>Short lowercase reason phrase for RepaintAll's single eviction log line. Ok is never
/// passed here (callers only ask for a phrase when outcome != Ok).</summary>
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
