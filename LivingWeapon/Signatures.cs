using System.Collections.Generic;

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

    /// <summary>First movement-passive ability id (== bit 0 of the movement bitfield).</summary>
    public const int MovementBase = 230;

    /// <summary>Width of the movement bitfield in bytes (24 addressable movement ids).</summary>
    public const int MovementBytes = 3;

    /// <summary>
    /// Encode a movement-ability id into its (byteOffset, mask) in the 3-byte movement bitfield
    /// (<see cref="Offsets.CMovement"/> = +0x9C, base id 230, MSB-first -- the same shape as the
    /// support field). False outside the field. NOTE: unlike supports, movement is
    /// exactly-ONE-effective -- LIVE-PROVEN 2026-06-10: both Spiritual Font bits (237+238) held
    /// perfectly, but the engine honored only Lifefont. Never grant a movement pair.
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

    /// <summary>Resolve a MOVEMENT-bit grant at the wielder's kill-tier into the (id, byteOffset,
    /// mask) writes to hold -- empty when there are no movement ids, the tier isn't earned, or
    /// every id falls outside the field. The generic PURE encoder for any future movement-bit
    /// grant (the tier gate stays unit-tested, like ResolveSupport's); no shipped signature
    /// carries one today (Spiritual Font's bits were retired -- the engine honors only one
    /// movement passive, so its restore is runtime-written, see SpiritualFont.cs).</summary>
    public static List<(int id, int off, byte mask)> ResolveMovementGrant(int[]? moveAbilityIds, int atTier, int tier)
    {
        var grants = new List<(int, int, byte)>();
        if (moveAbilityIds is null || tier < atTier) return grants;
        foreach (int id in moveAbilityIds)
            if (ResolveMovement(id, out int off, out byte mask)) grants.Add((id, off, mask));
        return grants;
    }

    /// <summary>Guarded OR-set of a single bit, returning the post-write read-back (true == the
    /// bit reads SET afterwards). Never clears anything; fail-safe false on an unwritable page.
    /// <paramref name="wasSet"/> is the PRE-write state -- the live-test signal: true means the
    /// engine KEPT the bit since the last hold, false means it was cleared and we just re-armed
    /// it (the post-write read-back alone always says SET on a writable page).</summary>
    public static bool OrBit(long addr, byte mask, out bool wasSet)
    {
        wasSet = false;
        if (!Mem.Writable(addr, 1)) return false;
        int cur = Mem.U8(addr);
        wasSet = (cur & mask) != 0;
        if (!wasSet) Mem.W8(addr, (byte)(cur | mask));
        return (Mem.U8(addr) & mask) != 0;
    }

    /// <summary>Guarded AND-clear of a single bit, returning the read-back (true == the bit reads
    /// CLEAR afterwards). Touches nothing else in the byte; fail-safe false on an unwritable page.
    /// The unequip-release counterpart of <see cref="OrBit"/>.</summary>
    public static bool ClearBit(long addr, byte mask)
    {
        if (!Mem.Writable(addr, 1)) return false;
        int cur = Mem.U8(addr);
        if ((cur & mask) != 0) Mem.W8(addr, (byte)(cur & ~mask));
        return (Mem.U8(addr) & mask) == 0;
    }

    /// <summary>Should a latched support grant be AND-cleared? Only when the granting weapon has
    /// actually left the wielder's hands AND the bit isn't the player's own picked support
    /// (stripping a player-chosen Swiftspell because a rod was stolen would be theft squared).</summary>
    public static bool ShouldClearOnUnequip(bool stillWielded, int pickedSupport, int abilityId)
        => !stillWielded && pickedSupport != abilityId;

    /// <summary>Supports that a LIVE-set bit can't actually grant: their effect is baked at battle
    /// build, not re-read from the bitfield each calculation. HP Boost / MP Boost change a derived
    /// stat (max HP/MP) computed once at build; Doublehand / Dual Wield are equip-time (how the unit
    /// holds weapons). Wiring one as a signature is a design bug -- the grant read-back log warns on
    /// it. (Calculation-gating supports -- Attack/Defense/Magick(Def) Boost, Concentration -- DO work
    /// live, because the engine re-reads the bit when it computes that hit/damage.)</summary>
    public static bool IsBuildTimeOnly(int abilityId) =>
        abilityId == 220 || abilityId == 221 || abilityId == 228;   // Doublehand, Dual Wield, HP Boost

    // --- card display: the painted Kills counter (the tried-and-true text paint, same
    //     mechanism as the +N suffix). The old painted "Grant <ability>" label is GONE: the
    //     baked "While this weapon is equipped at +3, ..." sentence states the ability on
    //     every card, and unequipped cards (which the painter never touches) showed the
    //     slot as a floating bare "Grant". The +N name suffix is the earned-state signal. ---

    /// <summary>The Kills counter fitted to its fixed 4-char slot: left-aligned digits, space-padded
    /// ("0   ", "42  ", "1337"). The card reads naturally ("Kills: 42") while the painted byte width
    /// never changes. Counts wrap at 10000 (the slot is 4 chars).</summary>
    public static string KillsSlot(int count) => (count % 10000).ToString().PadRight(4);

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
