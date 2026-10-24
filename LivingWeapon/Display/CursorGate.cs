namespace LivingWeapon;

/// <summary>
/// Answer of <see cref="CursorGate.Decide"/> before <see cref="AttackCard"/> composes anything.
/// <see cref="None"/> lets the caller proceed; the other two are refusals, split by log tier.
///   <see cref="NotTurnOwner"/> gate B failed, <see cref="Offsets.ATurnFlag"/> of the matched band entry is not 1. Since the
///     re-anchor onto <see cref="Band.FlagOwner"/> this is a rare falling-edge race, so Debug tier.
///   <see cref="WeaponMismatch"/> gate A failed, the roster right-hand weapon disagrees with the band entry's weapon.
///     A genuine anomaly (the wrong-dossier bug this gate closes), Warn tier.
/// </summary>
internal enum CursorRefusal { None, NotTurnOwner, WeaponMismatch }

/// <summary>The raw facts <see cref="ActorResolver.TryResolveCursorPlayer"/> hands to <see cref="CursorGate.Decide"/>.
/// <see cref="RosterHand"/> is the weapon the Attack row names, the right hand (<see cref="Offsets.RRHand"/>) or else
/// the off hand, unfiltered so shields and untracked ids ride along (AttackRow.Policy.ComposeRow needs the sentinel-vs-untracked distinction).
/// <see cref="BandWeapon"/> is the same unit's equipped weapon from its band entry (<see cref="Offsets.AWeapon"/>).
/// <see cref="TurnFlag"/> is that entry's turn-open flag (<see cref="Offsets.ATurnFlag"/>).
/// <see cref="BandSlot"/> is the entry's slot index, kept for the tripwire's evidence trail only.</summary>
internal readonly record struct CursorAnswer(long RosterBase, int RosterHand, int BandWeapon, byte TurnFlag, int BandSlot,
    bool RosterHandFromOffHand = false);

/// <summary>
/// Cross-checks applied before the Attack card trusts a dossier. They can only turn a compose into a refusal, never
/// supply a display value (AttackCard.Resolve.cs composes off the roster hand once they clear), since a wrong dossier is
/// worse than vanilla. Gate B runs first, so a benign re-read race is not mistaken for a weapon anomaly. The band entry's
/// <see cref="Offsets.ATurnFlag"/> must read 1, else <see cref="CursorRefusal.NotTurnOwner"/>. Gate A needs the roster
/// right-hand id to match the band weapon after sentinel normalization ({0, 0xFF, 0xFFFF}, as in ActorResolver.cs ~:165/:303
/// and AttackRow.Policy.cs ~:170). Both sentinel agrees as unarmed (<see cref="CursorRefusal.None"/>, then
/// AttackRow.Policy.ComposeRow's Fists/vanilla sprite gate decides); one sentinel, or two different armed ids, is
/// <see cref="CursorRefusal.WeaponMismatch"/>. Exception, an off hand weapon passes a sentinel band weapon (a shield first unit's band copy has none).
/// </summary>
internal static class CursorGate
{
    internal static bool IsSentinel(int weaponId) => weaponId == 0 || weaponId == 0xFF || weaponId == 0xFFFF;

    /// <summary>The weapon the Attack row names. Off hand only shows the off hand, two weapons show the main hand,
    /// and an empty pair stays the right hand so unarmed reads Fists.</summary>
    internal static (int Hand, bool FromOffHand) AttackHand(int rightHand, int offHand)
    {
        if (!IsSentinel(rightHand)) return (rightHand, false);
        return IsSentinel(offHand) ? (rightHand, false) : (offHand, true);
    }

    /// <summary>Gate B first, then gate A (see the class doc).</summary>
    internal static CursorRefusal Decide(int rosterHand, int bandWeapon, byte turnFlag, bool rosterHandFromOffHand = false)
    {
        if (turnFlag != 1) return CursorRefusal.NotTurnOwner;

        bool rosterSentinel = IsSentinel(rosterHand);
        bool bandSentinel = IsSentinel(bandWeapon);
        if (rosterSentinel && bandSentinel) return CursorRefusal.None;
        if (rosterHandFromOffHand && !rosterSentinel && bandSentinel) return CursorRefusal.None;
        if (rosterSentinel != bandSentinel) return CursorRefusal.WeaponMismatch;
        return rosterHand == bandWeapon ? CursorRefusal.None : CursorRefusal.WeaponMismatch;
    }
}
