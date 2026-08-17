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
    public bool HasOff    { get; set; }
    public ushort OrigOff { get; set; }
    public bool HasSupp   { get; set; }
    public ushort OrigSupp { get; set; }
}

/// <summary>
/// Pure decisions for the Gun Slinger roster prep -- the LW-193/LW-194 consent model. No memory
/// access; the caller supplies every input (menuOpen already debounced/fail-safed, shield
/// already read). The load-bearing rule: an EMPTY off-hand invites the twin, ANYTHING the
/// player equipped there or in the shield slot declines it, and no mod write ever lands on top
/// of a player-held item in ANY lane, including the in-battle re-assert.
///
/// EMPTY sentinels: the off-hand reads 0x00FF (255) or 0xFFFF (65535); the shield slot uses the
/// same two sentinels (255 live-observed on an empty slot) plus 0, treated as empty defensively
/// (see <see cref="IsEmptyShield"/> -- deliberately broader than the off-hand's own check, so a
/// shield read that never got seeded by anything still reads as "no shield in the way" rather
/// than permanently withholding consent). The SUPPORT slot is a u16 ABILITY KEY at +0x0A
/// (reaction +0x08 / movement +0x0C are its u16 siblings) and reads 0 when empty. Dual Wield =
/// Key 477 = live support id 221 + 256 (owner-observed live 2026-08-12, LW-168: a low-byte-only
/// 221 write onto a bare unit rendered the placeholder ability "Toadja", Key 221; a u16 477 poke
/// rendered Dual Wield). Every u16 support value is snapshottable and restored verbatim, so the
/// true empty (0) round-trips instead of being eaten.
///
/// Ownership: a flagged weapon id sitting in the off-hand is OURS only paired with
/// <see cref="GunSlingerSnap.HasOff"/> -- a twin id WITHOUT HasOff is a player-owned real
/// weapon (dual-wield builds exist) and is never touched (<see cref="IsOurs"/>). Anything else
/// non-empty and not-ours is a PLAYER-ITEM, which withholds consent
/// (<see cref="IsPlayerItem"/>); this replaces the old validity-gated snapshot-anything
/// behavior -- garbage and real player gear alike now decline the grant instead of being
/// swept up into a snapshot.
/// </summary>
internal static class GunSlingerPolicy
{
    private const ushort EmptyOffH1 = 0x00FF;   // 255
    private const ushort EmptyOffH2 = 0xFFFF;   // 65535

    /// <summary>The sentinel this mod WRITES when it clears a slot (LW-193 WriteEmptyAndDrop):
    /// 255, the same value the game itself was observed writing when it cleared a shield (tape
    /// lw193_watch_20260817_055131.log, 05:53:06 "sh 128 to 255"). Either recognised EMPTY
    /// sentinel round-trips through <see cref="IsEmptyOff"/> on the next read, so writing 255
    /// specifically (rather than 65535) is a style choice, not a correctness requirement.
    /// (The owner-AC round removed the menu-open evaporation edge that used to be this
    /// constant's other writer -- see GunSlinger.MenuGate.cs's class doc.)</summary>
    internal const ushort EmptyOffHand = EmptyOffH1;

    /// <summary>True when an off-hand read value is a recognised EMPTY sentinel. STRICT (255 or
    /// 65535 only, never 0): a raw 0 off-hand read is treated as unexplained/garbage, not empty
    /// -- it declines the grant via <see cref="IsPlayerItem"/> exactly like any other real value
    /// would, which is the same safe outcome the old validity gate produced for garbage reads.</summary>
    private static bool IsEmptyOff(ushort v) => v == EmptyOffH1 || v == EmptyOffH2;

    /// <summary>True when a shield-slot read counts as "no shield in the way". Broader than
    /// <see cref="IsEmptyOff"/> by design: 0 is ALSO treated as empty here (defensively -- an
    /// unseeded/never-written shield byte must read as consenting, not as a permanent veto).</summary>
    private static bool IsEmptyShield(ushort v) => v == 0 || IsEmptyOff(v);

    /// <summary>OURS: off holds ANY flagged twin id (not just the wielder's current one -- a
    /// main-hand swap between two flagged weapons leaves the OLD twin sitting there) AND a grant
    /// is on record for this snapshot. HasOff is the ownership discriminator, not the id alone.</summary>
    private static bool IsOurs(ushort off, GunSlingerSnap snap, HashSet<int> twinIds) => snap.HasOff && twinIds.Contains(off);

    /// <summary>PLAYER-ITEM: not empty, not ours -- a real player choice (or unexplained
    /// garbage), either way something the mod must never write over.</summary>
    private static bool IsPlayerItem(ushort off, GunSlingerSnap snap, HashSet<int> twinIds) => !IsEmptyOff(off) && !IsOurs(off, snap, twinIds);

    /// <summary>How long the battlefield must have gone continuously dark (sustained off the
    /// live field -- <c>BattleState.OnField</c> false) before the in-battle re-assert stops
    /// trusting a cleared off-hand as "the game normalizing mid-battle" and starts trusting it
    /// as "the battle-end teardown; do not re-stamp". Owner live pass, tape lw193_watch_*.log
    /// 10:42:38-10:43:58: the game silently cleared oh (71-&gt;255) at 10:43:53.626 during the
    /// ~4s battle-end exit debounce (BattleState.In still true), the pre-fix unconditional
    /// re-assert stamped it right back 0.2s later, and the post-battle gear reconcile then
    /// "returned" that phantom stamp as a genuine sack refund at 10:43:58.588 -- a mint. 2s is
    /// comfortably under the ~4s exit debounce (so the twilight is caught well before the
    /// battle-exit edge even fires) and comfortably over any live mid-battle mode flicker
    /// (enemy turns / targeting / pauses read OnField false for well under a second at a time;
    /// the 2026-07-04 in-battle re-assert exists precisely to survive those, so a bare
    /// instantaneous OnField gate would starve it -- see GunSlinger.cs's PrepRoster doc).
    /// <paramref name="secondsOffField"/> is the caller's honest raw signal (Engine's own
    /// <c>_lastField</c> clock, threaded through unmodified); this constant is the only
    /// threshold applied to it, kept here so the whole decision -- including its tuning -- lives
    /// in the one place that IS the table.</summary>
    internal const double TwilightHoldSeconds = 2.0;

    /// <summary>True only when the re-assert should be WITHHELD this tick: in battle AND the
    /// field has been dark past <see cref="TwilightHoldSeconds"/>. Gated on inBattle so an
    /// out-of-battle caller's (possibly large/stale) secondsOffField reading can never reach the
    /// out-of-battle lanes -- those never call this helper's result at all, but the explicit AND
    /// makes that safety structural, not just a consequence of call-site discipline.</summary>
    private static bool IsTwilight(bool inBattle, double secondsOffField) => inBattle && secondsOffField > TwilightHoldSeconds;

    /// <summary>
    /// Decide what to do with the wielder's roster off-hand slot. <paramref name="snap"/> is
    /// read-only; the caller mutates it per the returned action. <paramref name="shield"/> is
    /// RShield +0x1A, READ ONLY -- this policy never instructs a shield write.
    /// <paramref name="menuOpen"/> is already debounced/fail-safed by the caller.
    /// <paramref name="twinIds"/> is every flagged weapon id, for the ownership discriminator.
    /// <paramref name="secondsOffField"/> is how long the battlefield has read continuously off
    /// the live field (Engine's own clock, see <see cref="TwilightHoldSeconds"/>); defaults to
    /// 0.0 ("on field") so every pre-existing caller is unaffected.
    /// First match wins; this method (with DesiredSupport below) IS the decision table -- there
    /// is no separate copy elsewhere. See the class doc for the shared vocabulary (EMPTY/OURS/
    /// PLAYER-ITEM) and docs/LIVE_LEDGER.md's [twin-grant-inventory-desync] row for the evidence
    /// this table was built to fix.
    /// </summary>
    public static GunSlingerOffAction DesiredOffHand(bool mainIsGS, int twin, ushort off, ushort shield,
        GunSlingerSnap snap, bool menuOpen, bool inBattle, HashSet<int> twinIds, double secondsOffField = 0.0)
    {
        // Rule 1: a world-map menu (party/equip/shop/save) is open -- the player can touch
        // equipment there, so no twin-lane write lands while it's up. Scoped to !inBattle: the
        // menu byte deliberately reads 0 through the whole formation/battle-load flow (premise
        // [twin-dualfire-construction-bound] needs the twin present at construction), so this
        // gate must never suppress that window. No write of any kind lands during a menu; the
        // browse-screen lane in GunSlinger.MenuGate.cs is the only out-of-battle allow path.
        if (menuOpen && !inBattle) return GunSlingerOffAction.Leave;

        bool ours = IsOurs(off, snap, twinIds);

        // Rule 2: the wielder switched away from every flagged weapon.
        if (!mainIsGS)
        {
            if (!snap.HasOff) return GunSlingerOffAction.Leave;
            if (ours)
                return (IsEmptyShield(shield) || IsEmptyOff(snap.OrigOff))
                    ? GunSlingerOffAction.Restore
                    : GunSlingerOffAction.WriteEmptyAndDrop;   // legacy real gear can't rejoin a shield-occupied hand
            // The game or the player cleared/replaced the off-hand out from under the hold:
            // stand down without touching whatever (or nothing) is there now.
            return GunSlingerOffAction.StandDownNoWrite;   // snap.HasOff guaranteed true above
        }

        // Rule 3: consent withheld by a shield, in EVERY lane including in battle -- the
        // formation-destruction fix. A shield in play can never coexist with a dual-wielded
        // off-hand (the game's own "impossible state" cleanup, [twin-grant-inventory-desync]).
        if (!IsEmptyShield(shield))
        {
            if (ours)
                return IsEmptyOff(snap.OrigOff) ? GunSlingerOffAction.Restore : GunSlingerOffAction.WriteEmptyAndDrop;
            return snap.HasOff ? GunSlingerOffAction.StandDownNoWrite : GunSlingerOffAction.Leave;
        }

        // Rule 4: ownership discriminator. off==twin is the steady state; any OTHER flagged id
        // is a stale twin left over from a main-hand swap between two flagged weapons -- still
        // ours, so re-stamp the CURRENT twin over it.
        if (ours)
            return off == (ushort)twin ? GunSlingerOffAction.Leave : GunSlingerOffAction.Write;

        // Rule 5: a real player item (or unexplained garbage) withholds consent outright.
        if (IsPlayerItem(off, snap, twinIds))
            return snap.HasOff ? GunSlingerOffAction.StandDownNoWrite : GunSlingerOffAction.Leave;

        // Rule 6: off is EMPTY -- the only state that invites the twin.
        if (snap.HasOff)
            // The battle-end TWILIGHT guard: withhold the re-assert (not abandon the grant --
            // HasOff stays true) while sustained off the live field in battle. See
            // IsTwilight/TwilightHoldSeconds for the tape this fixes.
            return IsTwilight(inBattle, secondsOffField) ? GunSlingerOffAction.Leave : GunSlingerOffAction.Write;
        // SnapshotAndWrite stays out-of-battle-only: a mid-battle roster flicker must never seed
        // a snapshot of "original gear" that was never real in the first place.
        return inBattle ? GunSlingerOffAction.Leave : GunSlingerOffAction.SnapshotAndWrite;
    }

    /// <summary>
    /// Decide what to do with the wielder's roster support slot. Params mirror
    /// <see cref="DesiredOffHand"/>; <paramref name="off"/> and <paramref name="shield"/> feed
    /// the SAME consent check (a shield or a player-held off-hand item withholds Dual Wield too,
    /// not just the twin weapon itself). <paramref name="secondsOffField"/> feeds the SAME
    /// twilight guard as the off-hand's rule 6a -- the support twin re-assert is the other half
    /// of the same tape-caught mint.
    /// </summary>
    public static GunSlingerSuppAction DesiredSupport(bool mainIsGS, ushort supp, ushort off, ushort shield,
        GunSlingerSnap snap, bool menuOpen, bool inBattle, HashSet<int> twinIds, double secondsOffField = 0.0)
    {
        // Rule 1: see DesiredOffHand's identical gate for the full rationale.
        if (menuOpen && !inBattle) return GunSlingerSuppAction.Leave;

        // Rule 2: the wielder switched away from every flagged weapon.
        if (!mainIsGS)
            return snap.HasSupp ? GunSlingerSuppAction.Restore : GunSlingerSuppAction.Leave;

        // Rule 3: consent withheld (shield in play, or the off-hand holds a player item) --
        // Dual Wield comes off right alongside the twin weapon it no longer accompanies.
        bool consentWithheld = !IsEmptyShield(shield) || IsPlayerItem(off, snap, twinIds);
        if (consentWithheld)
            return snap.HasSupp ? GunSlingerSuppAction.Restore : GunSlingerSuppAction.Leave;

        // Rule 4: already Dual Wield -- steady state.
        if (supp == GunSlinger.DualWieldKey) return GunSlingerSuppAction.Leave;

        // Rule 5: grant or re-assert. SnapshotAndWrite stays out-of-battle-only, mirroring the
        // off-hand's rule 6 rationale -- a mid-battle flicker must never seed a support snapshot.
        // The re-assert Write shares the off-hand's twilight guard (rule 6a): the support twin
        // must not re-stamp during the battle-end teardown either.
        if (snap.HasSupp)
            return IsTwilight(inBattle, secondsOffField) ? GunSlingerSuppAction.Leave : GunSlingerSuppAction.Write;
        return inBattle ? GunSlingerSuppAction.Leave : GunSlingerSuppAction.SnapshotAndWrite;
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
