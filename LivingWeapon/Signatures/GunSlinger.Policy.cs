using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>What to do with the roster off-hand slot for the wielder this tick.</summary>
internal enum GunSlingerOffAction { Leave, SnapshotAndWrite, Write, Restore, StandDownNoWrite, WriteEmptyAndDrop }

/// <summary>What to do with the roster support slot for the wielder this tick.</summary>
internal enum GunSlingerSuppAction { Leave, SnapshotAndWrite, Write, Restore }

/// <summary>
/// Per-unit snapshot (persisted across sessions). Mutable so the caller can update
/// HasOff/HasSupp/OrigOff/OrigSupp after the policy returns SnapshotAndWrite.
/// </summary>
internal sealed class GunSlingerSnap
{
    public bool HasOff { get; set; }
    public ushort OrigOff { get; set; }
    public bool HasSupp { get; set; }
    public ushort OrigSupp { get; set; }
}

/// <summary>
/// Pure decisions for the Gun Slinger roster prep (LW-193/LW-194 consent model). No memory access;
/// the caller supplies every input. An empty off-hand invites the twin; anything the player put in
/// the off-hand or shield slot declines it, and no write lands on a player-held item in any lane.
/// Empty sentinels. Off-hand 0x00FF or 0xFFFF; shield 255 (seen on an empty slot), 0xFFFF, or 0
/// (see <see cref="IsEmptyShield"/>). The support slot is a u16 ability key at +0x0A (reaction +0x08,
/// movement +0x0C) and reads 0 when empty. Dual Wield is Key 477 (support id 221 + 256, seen
/// 2026-08-12, a low-byte 221 write rendered "Toadja", Key 221). Support values are
/// snapshotted and restored as full u16s so the true empty (0) round-trips.
/// A flagged twin id in the off-hand is ours only with <see cref="GunSlingerSnap.HasOff"/>
/// (<see cref="IsOurs"/>); without it the weapon is the player's. Any other non-empty value is a
/// player item and withholds consent (<see cref="IsPlayerItem"/>).
/// </summary>
internal static class GunSlingerPolicy
{
    private const ushort EmptyOffH1 = 0x00FF;   // 255
    private const ushort EmptyOffH2 = 0xFFFF;   // 65535

    /// <summary>Sentinel written when clearing a slot (LW-193 WriteEmptyAndDrop): 255, what the game
    /// wrote when it cleared a shield (tape lw193_watch_20260817_055131.log, 05:53:06 "sh 128 to 255").</summary>
    internal const ushort EmptyOffHand = EmptyOffH1;

    /// <summary>True for 255 or 65535. A raw 0 is treated as garbage and declines the grant
    /// via <see cref="IsPlayerItem"/>.</summary>
    private static bool IsEmptyOff(ushort v) => v == EmptyOffH1 || v == EmptyOffH2;

    /// <summary>True when the shield slot counts as empty. Also accepts 0, so a never-written
    /// shield byte does not veto the grant forever.</summary>
    private static bool IsEmptyShield(ushort v) => v == 0 || IsEmptyOff(v);

    /// <summary>Ours. Off holds any flagged twin id (a swap between two flagged weapons leaves the
    /// old twin there) and a grant is on record. HasOff is the ownership marker.</summary>
    private static bool IsOurs(ushort off, GunSlingerSnap snap, HashSet<int> twinIds) => snap.HasOff && twinIds.Contains(off);

    /// <summary>Not empty and not ours. A player choice or garbage; never write over it.</summary>
    private static bool IsPlayerItem(ushort off, GunSlingerSnap snap, HashSet<int> twinIds) => !IsEmptyOff(off) && !IsOurs(off, snap, twinIds);

    /// <summary>How long the field stays dark (<c>BattleState.OnField</c> false) in battle before a
    /// cleared off-hand is read as battle-end teardown rather than a flicker. Tape lw193_watch_*.log
    /// 10:42:38-10:43:58. The game cleared oh (71-&gt;255) at 10:43:53.626 inside the ~4s exit debounce,
    /// the re-assert stamped it back 0.2s later, and the post-battle reconcile refunded that phantom
    /// stamp as a sack item at 10:43:58.588. 2s sits under that debounce and over mid-battle OnField
    /// flickers (GunSlinger.cs PrepRoster, 2026-07-04). <paramref name="secondsOffField"/> is Engine's
    /// raw <c>_lastField</c> clock.</summary>
    internal const double TwilightHoldSeconds = 2.0;

    /// <summary>True when the re-assert should be withheld. In battle and dark past
    /// <see cref="TwilightHoldSeconds"/>. inBattle is in the AND so a stale out-of-battle clock can
    /// never trip it.</summary>
    private static bool IsTwilight(bool inBattle, double secondsOffField) => inBattle && secondsOffField > TwilightHoldSeconds;

    /// <summary>First matching rule wins. <paramref name="snap"/> is read-only (the caller mutates
    /// it); <paramref name="shield"/> is RShield +0x1A, read only; <paramref name="twinIds"/> is every
    /// flagged weapon id. Evidence. Docs/LIVE_LEDGER.md [twin-grant-inventory-desync].</summary>
    public static GunSlingerOffAction DesiredOffHand(bool mainIsGS, int twin, ushort off, ushort shield,
        GunSlingerSnap snap, bool menuOpen, bool inBattle, HashSet<int> twinIds, double secondsOffField = 0.0)
    {
        // Rule 1. No twin-lane write while a world-map menu is open. Scoped to !inBattle because the
        // menu byte reads 0 through formation/battle-load, when the twin must be present
        // ([twin-dualfire-construction-bound]). The browse-screen lane in GunSlinger.MenuGate.cs
        // is the only out-of-battle allow path.
        if (menuOpen && !inBattle) return GunSlingerOffAction.Leave;

        bool ours = IsOurs(off, snap, twinIds);

        // Rule 2. The wielder switched away from every flagged weapon.
        if (!mainIsGS)
        {
            if (!snap.HasOff) return GunSlingerOffAction.Leave;
            if (ours)
                return (IsEmptyShield(shield) || IsEmptyOff(snap.OrigOff))
                    ? GunSlingerOffAction.Restore
                    : GunSlingerOffAction.WriteEmptyAndDrop;   // legacy real gear can't rejoin a shield-occupied hand
            // Off-hand was cleared or replaced under the hold. Stand down, write nothing.
            return GunSlingerOffAction.StandDownNoWrite;   // snap.HasOff guaranteed true above
        }

        // Rule 3. A shield withholds consent in every lane, in battle too (the game cleans up
        // shield plus dual-wield as an impossible state, [twin-grant-inventory-desync]).
        if (!IsEmptyShield(shield))
        {
            if (ours)
                return IsEmptyOff(snap.OrigOff) ? GunSlingerOffAction.Restore : GunSlingerOffAction.WriteEmptyAndDrop;
            return snap.HasOff ? GunSlingerOffAction.StandDownNoWrite : GunSlingerOffAction.Leave;
        }

        // Rule 4. Off==twin is steady; another flagged id is a stale twin from a main-hand swap,
        // so re-stamp the current twin over it.
        if (ours)
            return off == (ushort)twin ? GunSlingerOffAction.Leave : GunSlingerOffAction.Write;

        // Rule 5. A player item or garbage withholds consent.
        if (IsPlayerItem(off, snap, twinIds))
            return snap.HasOff ? GunSlingerOffAction.StandDownNoWrite : GunSlingerOffAction.Leave;

        // Rule 6. Off is empty, the only state that invites the twin.
        if (snap.HasOff)
            // Twilight guard. Withhold the re-assert (HasOff stays true) while the field is dark.
            return IsTwilight(inBattle, secondsOffField) ? GunSlingerOffAction.Leave : GunSlingerOffAction.Write;
        // SnapshotAndWrite is out-of-battle only; a mid-battle flicker must not snapshot gear.
        return inBattle ? GunSlingerOffAction.Leave : GunSlingerOffAction.SnapshotAndWrite;
    }

    /// <summary>Decide what to do with the roster support slot; same consent check and twilight
    /// guard as <see cref="DesiredOffHand"/>.</summary>
    public static GunSlingerSuppAction DesiredSupport(bool mainIsGS, ushort supp, ushort off, ushort shield,
        GunSlingerSnap snap, bool menuOpen, bool inBattle, HashSet<int> twinIds, double secondsOffField = 0.0)
    {
        // Rule 1. Same menu gate as DesiredOffHand.
        if (menuOpen && !inBattle) return GunSlingerSuppAction.Leave;

        // Rule 2. The wielder switched away from every flagged weapon.
        if (!mainIsGS)
            return snap.HasSupp ? GunSlingerSuppAction.Restore : GunSlingerSuppAction.Leave;

        // Rule 3. Consent withheld (shield, or a player item in the off-hand): Dual Wield comes off too.
        bool consentWithheld = !IsEmptyShield(shield) || IsPlayerItem(off, snap, twinIds);
        if (consentWithheld)
            return snap.HasSupp ? GunSlingerSuppAction.Restore : GunSlingerSuppAction.Leave;

        // Rule 4. Already Dual Wield, steady state.
        if (supp == GunSlinger.DualWieldKey) return GunSlingerSuppAction.Leave;

        // Rule 5. Grant or re-assert; same out-of-battle and twilight limits as the off-hand.
        if (snap.HasSupp)
            return IsTwilight(inBattle, secondsOffField) ? GunSlingerSuppAction.Leave : GunSlingerSuppAction.Write;
        return inBattle ? GunSlingerSuppAction.Leave : GunSlingerSuppAction.SnapshotAndWrite;
    }

    /// <summary>Legacy gunslinger.json (2.3.2 and earlier) stored the support Key as a u8 by
    /// mistake, so only the low byte survived (real Keys are high byte 0x01, 454..510), with 255 as an assumed-empty sentinel. Maps 198..254 to full
    /// Keys and 255 to 0; current values pass through.</summary>
    internal static ushort MigrateLegacySupp(int stored) =>
        stored == 255 ? (ushort)0
      : stored >= 198 && stored <= 254 ? (ushort)(stored + 256)
      : (ushort)stored;
}
