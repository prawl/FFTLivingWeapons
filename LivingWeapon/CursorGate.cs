namespace LivingWeapon;

/// <summary>
/// LW-55: what <see cref="CursorGate.Decide"/> answers for one cursor resolve, before
/// <see cref="AttackCard"/> composes anything. <see cref="None"/> means the caller may proceed
/// (agreement, or both sides agree-as-unarmed); the other two are refusals, split by tier so a
/// routine hover never spams a Warn:
///   <see cref="NotTurnOwner"/>: gate B failed (the matched band entry is not the genuine turn
///     owner, <see cref="Offsets.ATurnFlag"/> reads something other than 1). This is ROUTINE
///     hover behavior (targeting/reticle sweeps over allies and guests constantly), Debug tier.
///   <see cref="WeaponMismatch"/>: gate A failed (the roster right-hand weapon disagrees with the
///     matched band entry's own equipped weapon). This is a GENUINE anomaly, the exact live bug
///     LW-55 exists to close, Warn tier.
/// </summary>
internal enum CursorRefusal { None, NotTurnOwner, WeaponMismatch }

/// <summary>The raw facts <see cref="ActorResolver.TryResolveCursorPlayer"/> hands to
/// <see cref="CursorGate.Decide"/>: no doctrine applied by the resolver, only what it read.
/// <see cref="RosterHand"/> is the matched roster slot's raw right-hand weapon id
/// (<see cref="Offsets.RRHand"/>, unfiltered: shields and untracked ids ride along, exactly the
/// sentinel-vs-untracked distinction AttackRow.Policy.ComposeRow needs). <see cref="BandWeapon"/>
/// is that SAME unit's actually-equipped weapon read off its own band entry
/// (<see cref="Offsets.AWeapon"/>), the cross-check field the roster read alone cannot supply.
/// <see cref="TurnFlag"/> is that band entry's own turn-open flag (<see cref="Offsets.ATurnFlag"/>).
/// <see cref="BandSlot"/> is the matched band entry's own slot index, carried through for the
/// tripwire's evidence trail, never consulted by <see cref="CursorGate.Decide"/> itself.</summary>
internal readonly record struct CursorAnswer(long RosterBase, int RosterHand, int BandWeapon, byte TurnFlag, int BandSlot);

/// <summary>
/// LW-55: two NARROWING-ONLY cross-checks the Attack card's cursor resolve applies before
/// trusting a dossier. Doctrine: a wrong dossier is worse than vanilla, so <see cref="Decide"/> can
/// only ever turn a would-be compose INTO a refusal, never invent or promote a display value of
/// its own (the band weapon id is never promoted to display authority in this pass; AttackCard.Resolve.cs
/// composes strictly off the roster hand once the gates clear). Two gates, checked in this fixed
/// order:
///
/// GATE B (turn ownership, checked FIRST): a hovered unit's weapon agreement is irrelevant if it
/// is not even that unit's turn, and checking the flag first keeps a hover storm from ever being
/// mistaken for a weapon anomaly. The matched band entry's own <see cref="Offsets.ATurnFlag"/>
/// must read exactly 1 (the unit's move/act/wait menu is genuinely open); anything else refuses
/// <see cref="CursorRefusal.NotTurnOwner"/>.
///
/// GATE A (weapon agreement): the roster right-hand weapon id must agree with the matched band
/// entry's own equipped weapon, SENTINEL-NORMALIZED: the sentinel set is {0, 0xFF, 0xFFFF} (see
/// ActorResolver.cs ~:165/:303 and AttackRow.Policy.cs ~:170 for the same set used elsewhere in
/// this codebase). Both sides sentinel agrees-as-unarmed (<see cref="CursorRefusal.None"/>; the
/// row/tail decision then falls to AttackRow.Policy.ComposeRow's own Fists/vanilla sprite gate,
/// untouched by this class). Exactly one side sentinel, or two disagreeing armed ids, is the
/// genuine anomaly this gate exists to catch: <see cref="CursorRefusal.WeaponMismatch"/>.
/// </summary>
internal static class CursorGate
{
    private static bool IsSentinel(int weaponId) => weaponId == 0 || weaponId == 0xFF || weaponId == 0xFFFF;

    /// <summary>Gate B first, then gate A. See this class's own doc comment for the full rationale
    /// of both the ordering and the sentinel-agreement rule.</summary>
    internal static CursorRefusal Decide(int rosterHand, int bandWeapon, byte turnFlag)
    {
        if (turnFlag != 1) return CursorRefusal.NotTurnOwner;

        bool rosterSentinel = IsSentinel(rosterHand);
        bool bandSentinel = IsSentinel(bandWeapon);
        if (rosterSentinel && bandSentinel) return CursorRefusal.None;
        if (rosterSentinel != bandSentinel) return CursorRefusal.WeaponMismatch;
        return rosterHand == bandWeapon ? CursorRefusal.None : CursorRefusal.WeaponMismatch;
    }
}
