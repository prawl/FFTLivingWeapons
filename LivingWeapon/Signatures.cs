namespace LivingWeapon;

/// <summary>
/// The pure decision behind a Living Weapon granting its iconic passive at a kill-tier
/// (the Gloomfang -> Concentration pilot). No memory access -- GrowthEngine does the
/// guarded write+hold from the (byteOffset, mask) this returns.
///
/// Only SUPPORT passives are wired: they STACK with the unit's own supports, so granting
/// one never hijacks the player's choice (reactions/movement own a single slot apiece and
/// are off-limits as signatures). Supports live in a 4-byte bitfield on the combat struct
/// (<see cref="Offsets.CSupport"/> = +0x98), MSB-first, with ability id 198 as bit 0.
/// </summary>
internal static class Signatures
{
    /// <summary>First support-passive ability id (== bit 0 of the support bitfield).</summary>
    public const int SupportBase = 198;

    /// <summary>Width of the support bitfield in bytes (so 32 addressable support ids).</summary>
    public const int SupportBytes = 4;

    /// <summary>
    /// Resolve a weapon's signature at the wielder's current kill-tier into the support-bit
    /// write to hold, or false to write nothing. False when: no signature, not a support
    /// signature, the tier isn't earned yet, or the id falls outside the support field.
    /// </summary>
    public static bool ResolveSupport(WeaponSignature? sig, int tier, out int byteOffset, out byte mask)
    {
        byteOffset = 0;
        mask = 0;
        if (sig is null) return false;
        if (sig.Slot != "support") return false;   // additive-only: reactions/movement hijack a slot
        if (tier < sig.AtTier) return false;        // not earned yet

        int pos = sig.AbilityId - SupportBase;
        if (pos < 0 || pos >= SupportBytes * 8) return false;
        byteOffset = pos / 8;
        mask = (byte)(0x80 >> (pos % 8));
        return true;
    }

    // --- card display: the painted "Grant <ability>" label (the tried-and-true text paint,
    //     same mechanism as the +N suffix and the Kills counter) ---

    /// <summary>Fixed character width of the baked grant slot. MUST match GRANT_WIDTH in
    /// tools/patch_names.py (they bake/paint the same region). 16 fits the longest shipping
    /// knife label ("Magick Def Boost").</summary>
    public const int GrantWidth = 16;

    /// <summary>True when this weapon's signature should paint its label on the card: it has a
    /// label and the wielder's kill-tier has reached the grant's tier.</summary>
    public static bool ShowsGrant(WeaponSignature? sig, int tier) =>
        sig != null && !string.IsNullOrEmpty(sig.DisplayLabel) && tier >= sig.AtTier;

    /// <summary>The label fitted to the baked slot: padded with spaces to GrantWidth, or
    /// truncated if longer. Pass "" to blank the slot (e.g. below the grant's tier).</summary>
    public static string GrantSlot(string label)
    {
        label ??= "";
        return label.Length >= GrantWidth ? label.Substring(0, GrantWidth) : label.PadRight(GrantWidth);
    }

    /// <summary>True if this signature's HP condition (if any) is currently met. No condition
    /// (HpBelow &lt;= 0) is always-on. The gate is integer math (hp*100 &lt; maxHp*HpBelow) so it
    /// needs no float; safe (false) when maxHp is non-positive (struct unlocated / unit dead).
    /// Used for "while HP &lt; N%" grants (e.g. Mortal Coil's Attack Boost). Arm-and-stays: the
    /// caller only ever OR-sets the bit, so a recovered unit keeps the buff for the battle.</summary>
    public static bool ConditionMet(WeaponSignature? sig, int hp, int maxHp)
    {
        if (sig is null) return false;
        if (sig.HpBelow <= 0) return true;          // no condition -> always-on
        if (maxHp <= 0) return false;
        return hp * 100 < maxHp * sig.HpBelow;
    }
}
