namespace LivingWeapon;

/// <summary>What to do with the roster off-hand slot for the wielder this tick.</summary>
internal enum GunSlingerOffAction { Leave, SnapshotAndWrite, Write, Restore }

/// <summary>What to do with the roster support slot for the wielder this tick.</summary>
internal enum GunSlingerSuppAction { Leave, SnapshotAndWrite, Write, Restore }

/// <summary>
/// Per-unit snapshot (persisted across sessions). Mutable so the caller can update
/// HasOff/HasSupp/OrigOff/OrigSupp after the policy returns SnapshotAndWrite.
/// </summary>
internal sealed class GunSlingerSnap
{
    public bool HasOff    { get; set; }
    public ushort OrigOff { get; set; }
    public bool HasSupp   { get; set; }
    public byte OrigSupp  { get; set; }
}

/// <summary>
/// Pure decisions for the Gun Slinger out-of-battle roster prep. No memory access.
///
/// EMPTY sentinels: read 0x00FF (255) or 0xFFFF (65535) for off-hand; 0xFF (255) for support.
/// Valid off-hand range for snapshotting: 1..315 OR the EMPTY sentinels.
/// Valid support range for snapshotting: 1..254 OR 255 (EMPTY). Reject 0 as garbage.
/// Validity gate fires only when no snap exists (HasOff/HasSupp == false); re-assert ignores it.
/// </summary>
internal static class GunSlingerPolicy
{
    private const ushort EmptyOffH1 = 0x00FF;   // 255
    private const ushort EmptyOffH2 = 0xFFFF;   // 65535
    private const byte EmptySupp = 0xFF;         // 255
    private const int MaxItemId = 315;

    /// <summary>True when an off-hand read value is a recognised EMPTY sentinel.</summary>
    private static bool IsEmptyOff(ushort v) => v == EmptyOffH1 || v == EmptyOffH2;

    /// <summary>True when an off-hand value is safe to snapshot (EMPTY sentinel OR plausible id).</summary>
    private static bool IsValidOff(ushort v) => IsEmptyOff(v) || (v >= 1 && v <= MaxItemId);

    /// <summary>
    /// Decide what to do with the wielder's roster off-hand slot.
    /// <paramref name="snap"/> is read-only; the caller mutates it after a SnapshotAndWrite decision.
    /// </summary>
    public static GunSlingerOffAction DesiredOffHand(bool mainIsGS, int twin, ushort off, GunSlingerSnap snap)
    {
        if (mainIsGS)
        {
            if (off == (ushort)twin)   return GunSlingerOffAction.Leave;
            if (snap.HasOff)           return GunSlingerOffAction.Write;
            // No snap yet: validate before snapshotting
            if (!IsValidOff(off))      return GunSlingerOffAction.Leave;
            return GunSlingerOffAction.SnapshotAndWrite;
        }
        else
        {
            if (snap.HasOff) return GunSlingerOffAction.Restore;
            return GunSlingerOffAction.Leave;
        }
    }

    /// <summary>
    /// Decide what to do with the wielder's roster support slot.
    /// <paramref name="snap"/> is read-only; the caller mutates it after a SnapshotAndWrite decision.
    /// </summary>
    public static GunSlingerSuppAction DesiredSupport(bool mainIsGS, byte supp, GunSlingerSnap snap)
    {
        if (mainIsGS)
        {
            if (supp == 221)    return GunSlingerSuppAction.Leave;
            if (snap.HasSupp)   return GunSlingerSuppAction.Write;
            // No snap yet: validate before snapshotting (reject 0 as garbage)
            if (supp == 0)      return GunSlingerSuppAction.Leave;
            return GunSlingerSuppAction.SnapshotAndWrite;
        }
        else
        {
            if (snap.HasSupp) return GunSlingerSuppAction.Restore;
            return GunSlingerSuppAction.Leave;
        }
    }
}
