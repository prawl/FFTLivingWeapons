using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Pure decision for a Living Weapon granting its passive at a kill-tier (the Gloomfang ->
/// Concentration pilot). No memory access; GrowthEngine does the guarded write+hold. Only support
/// passives are wired, since they stack with the unit's own supports. Supports are a 4-byte
/// bitfield on the combat struct (<see cref="Offsets.CSupport"/> = +0x98), MSB-first, id 198 = bit 0.
/// </summary>
internal static class Signatures
{
    /// <summary>First support-passive ability id (bit 0 of the support bitfield).</summary>
    public const int SupportBase = 198;

    /// <summary>Width of the support bitfield in bytes (32 support ids).</summary>
    public const int SupportBytes = 4;

    /// <summary>Encode a support-ability id into (byteOffset, mask) in the 4-byte support bitfield
    /// (base 198, MSB-first). False, with zeroed outs, when the id is outside the field.</summary>
    public static bool SupportBit(int abilityId, out int byteOffset, out byte mask)
    {
        byteOffset = 0;
        mask = 0;
        int pos = abilityId - SupportBase;
        if (pos < 0 || pos >= SupportBytes * 8) return false;
        byteOffset = pos / 8;
        mask = (byte)(0x80 >> (pos % 8));
        return true;
    }

    /// <summary>
    /// Resolve a weapon's signature at the wielder's kill-tier into the support-bit write to hold.
    /// False when there is no support signature, the tier is not earned, or the id is outside the field.
    /// </summary>
    public static bool ResolveSupport(WeaponSignature? sig, int tier, out int byteOffset, out byte mask)
    {
        byteOffset = 0;
        mask = 0;
        if (sig is null) return false;
        if (sig.Slot != "support") return false;   // additive only, reactions/movement own a slot
        if (tier < sig.AtTier) return false;        // not earned yet
        return SupportBit(sig.AbilityId, out byteOffset, out mask);
    }

    /// <summary>First movement-passive ability id (== bit 0 of the movement bitfield).</summary>
    public const int MovementBase = 230;

    /// <summary>Width of the movement bitfield in bytes (24 addressable movement ids).</summary>
    public const int MovementBytes = 3;

    /// <summary>
    /// Encode a movement-ability id into (byteOffset, mask) in the 3-byte movement bitfield
    /// (<see cref="Offsets.CMovement"/> = +0x9C, base id 230, MSB-first). False outside the field.
    /// Unlike supports, only one movement bit takes effect: on 2026-06-10 both Spiritual Font bits
    /// (237+238) held but the engine honored only Lifefont. Never grant a movement pair.
    /// </summary>
    public static bool ResolveMovement(int abilityId, out int byteOffset, out byte mask)
    {
        byteOffset = 0;
        mask = 0;
        int pos = abilityId - MovementBase;
        if (pos < 0 || pos >= MovementBytes * 8) return false;
        byteOffset = pos / 8;
        mask = (byte)(0x80 >> (pos % 8));
        return true;
    }

    /// <summary>Resolve a movement-bit grant at the wielder's kill-tier into (id, byteOffset, mask)
    /// writes; empty when there are no ids, the tier is not earned, or every id is outside the field.
    /// No shipped signature uses it today (Spiritual Font's bits were retired, see SpiritualFont.cs).</summary>
    public static List<(int id, int off, byte mask)> ResolveMovementGrant(int[]? moveAbilityIds, int atTier, int tier)
    {
        var grants = new List<(int, int, byte)>();
        if (moveAbilityIds is null || tier < atTier) return grants;
        foreach (int id in moveAbilityIds)
            if (ResolveMovement(id, out int off, out byte mask)) grants.Add((id, off, mask));
        return grants;
    }

    /// <summary>Shared activation gate: a signature exists and its kill-tier is earned. Each feature
    /// ANDs this with its own payload check.</summary>
    public static bool Earned(WeaponSignature? sig, int tier) => sig is not null && tier >= sig.AtTier;

    /// <summary>True when the acting unit's main-hand weapon is this weapon. A Living Weapon
    /// earns kills in any hand, but commands its gift only from the main hand.</summary>
    public static bool IsActingMainHand(int mainHand, int weaponId) => mainHand == weaponId && mainHand != 0;

    /// <summary>A fresh kill landed on the tally since the last look. lastCount &lt; 0 means unprimed
    /// (first look of a battle) and is never fresh.</summary>
    public static bool FreshKill(int lastCount, int count) => lastCount >= 0 && count > lastCount;

    /// <summary>Should a latched support grant be AND-cleared? Only once the granting weapon has left
    /// the wielder's hands and the bit is not the player's own picked support.</summary>
    public static bool ShouldClearOnUnequip(bool stillWielded, int pickedSupport, int abilityId)
        => !stillWielded && pickedSupport != abilityId;

    /// <summary>Supports a live-set bit cannot grant because the effect is baked at battle build:
    /// HP Boost / MP Boost (derived max HP/MP), Doublehand / Dual Wield (equip-time). Wiring one as a
    /// signature is a design bug and the grant read-back log warns on it. Calculation-gating supports
    /// (Attack/Defense/Magick Boost, Concentration) work live.</summary>
    public static bool IsBuildTimeOnly(int abilityId) =>
        abilityId == 220 || abilityId == 221 || abilityId == 228;   // Doublehand, Dual Wield, HP Boost

    // Card display: the painted Kills counter. The old painted "Grant <ability>" label is gone;
    // the baked "While this weapon is equipped at +3, ..." sentence names the ability on every card.

    /// <summary>The Kills counter in a fixed 4-char slot, left-aligned and space-padded ("42  ").
    /// Wraps at 10000; negative counts clamp to 0. Superseded on the equip card by
    /// <see cref="KillsMeterSlot"/> (2026-07-06); kept for its tests.</summary>
    public static string KillsSlot(int count) => (Math.Max(0, count) % 10000).ToString().PadRight(4);

    /// <summary>Width (chars) of the equip card's painted meter-body slot. The widest prod body
    /// under Tuning.ProdThresholds {5,10,15} is 11 ("14/15 to +3"). Every C# site that paints or
    /// validates the slot uses this constant; tools/lib/flavor.py KILLS_SLOT_BODY_CHARS is pinned
    /// to it by an analyze.py lockstep check.</summary>
    public const int KillsMeterSlotChars = 11;

    /// <summary>The equip card's Kills line body, fitted to <see cref="KillsMeterSlotChars"/>. Reuses
    /// the Attack card's meter (<see cref="AttackCardTail.ComposeHead"/>), strips the "Kills: " prefix
    /// and pads or truncates to the slot width. Truncation only happens past an 11-digit count
    /// (corrupt kills.json); the max-tier meter shows the raw count, not a %10000 wrap.</summary>
    public static string KillsMeterSlotIn(int kills, int[] thresholds, string[] suffixes)
    {
        string full = AttackCardTail.ComposeHead(kills, thresholds, suffixes);
        string body = full.Substring("Kills: ".Length);
        return body.Length >= KillsMeterSlotChars
            ? body.Substring(0, KillsMeterSlotChars)
            : body.PadRight(KillsMeterSlotChars);
    }

    /// <summary>KillsMeterSlotIn using the build's own Tuning.KillThresholds/Suffix (dev {1,2,3}
    /// under LWDEV, prod {5,10,15} otherwise). This is what CardSites paints.</summary>
    public static string KillsMeterSlot(int kills) => KillsMeterSlotIn(kills, Tuning.KillThresholds, Tuning.Suffix);

    /// <summary>Once-per-transition latch for a nag condition that holds across many ticks: returns
    /// true once on the false-&gt;true edge, then stays silent until the condition clears. Used by
    /// Barrage/ShadowBlade's inject-blocked nags so they do not log every 33ms.</summary>
    public static bool StuckEdge(ref bool latched, bool condition)
    {
        if (!condition) { latched = false; return false; }
        if (latched) return false;
        latched = true;
        return true;
    }

    /// <summary>True if the signature's HP condition is met. No condition (HpBelow &lt;= 0) is
    /// always on; false when maxHp is non-positive. Integer math (hp*100 &lt; maxHp*HpBelow). Used
    /// for "while HP &lt; N%" grants (Mortal Coil's Attack Boost); the caller only OR-sets the bit.</summary>
    public static bool ConditionMet(WeaponSignature? sig, int hp, int maxHp)
    {
        if (sig is null) return false;
        if (sig.HpBelow <= 0) return true;          // no condition -> always-on
        if (maxHp <= 0) return false;
        return hp * 100 < maxHp * sig.HpBelow;
    }
}
