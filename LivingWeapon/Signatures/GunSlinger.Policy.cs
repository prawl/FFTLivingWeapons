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
    public ushort OrigSupp { get; set; }
}

/// <summary>
/// Pure decisions for the Gun Slinger out-of-battle roster prep. No memory access.
///
/// EMPTY sentinels: the off-hand reads 0x00FF (255) or 0xFFFF (65535); the SUPPORT slot is a
/// u16 ABILITY KEY at +0x0A (reaction +0x08 / movement +0x0C are its u16 siblings) and reads 0
/// when empty. Dual Wield = Key 477 = live support id 221 + 256 (owner-observed live 2026-08-12,
/// LW-168: a low-byte-only 221 write onto a bare unit rendered the placeholder ability
/// "Toadja", Key 221; a u16 477 poke rendered Dual Wield). Every u16 support value is
/// snapshottable and restored verbatim, so the true empty (0) round-trips instead of being
/// eaten.
/// Valid off-hand range for snapshotting: 1..315 OR the EMPTY sentinels.
/// The off-hand validity gate fires only when no snap exists (HasOff == false); re-assert
/// ignores it. The support lane has no validity gate at all.
/// </summary>
internal static class GunSlingerPolicy
{
    private const ushort EmptyOffH1 = 0x00FF;   // 255
    private const ushort EmptyOffH2 = 0xFFFF;   // 65535
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
    public static GunSlingerSuppAction DesiredSupport(bool mainIsGS, ushort supp, GunSlingerSnap snap)
    {
        if (mainIsGS)
        {
            if (supp == GunSlinger.DualWieldKey) return GunSlingerSuppAction.Leave;
            if (snap.HasSupp)   return GunSlingerSuppAction.Write;
            return GunSlingerSuppAction.SnapshotAndWrite;
        }
        else
        {
            if (snap.HasSupp) return GunSlingerSuppAction.Restore;
            return GunSlingerSuppAction.Leave;
        }
    }

    /// <summary>
    /// Legacy gunslinger.json files (release 2.3.2 and earlier) recorded only the LOW BYTE of
    /// the support Key (the field was misread as u8). Real support Keys live at high byte 0x01
    /// (454..510), so a legacy file holds 198..254 for a real support and 255 for the old,
    /// never-observed assumed-empty sentinel. Map low bytes back to their Keys, the phantom 255
    /// to the true empty (0), and pass current-format values (0, or >= 256) through verbatim.
    /// </summary>
    internal static ushort MigrateLegacySupp(int stored) =>
        stored == 255 ? (ushort)0
      : stored >= 198 && stored <= 254 ? (ushort)(stored + 256)
      : (ushort)stored;
}
