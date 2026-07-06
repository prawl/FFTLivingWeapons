using System;
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

    /// <summary>Encode a support-ability id into its (byteOffset, mask) in the 4-byte support bitfield
    /// (base id 198, MSB-first). False (and zeroed outs) when the id falls outside the field. The pure
    /// encoder shared by ResolveSupport and any direct support-bit consumer (Choir).</summary>
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
        return SupportBit(sig.AbilityId, out byteOffset, out mask);
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

    /// <summary>The shared half of every signature's activation gate: a signature exists and its
    /// kill-tier is earned. Each feature ANDs this with its own payload check (CrippleTurns &gt; 0,
    /// RaptureMove, ...), which is real per-feature knowledge -- the tier math lives only here.</summary>
    public static bool Earned(WeaponSignature? sig, int tier) => sig is not null && tier >= sig.AtTier;

    /// <summary>True when the acting unit's main-hand weapon is this weapon. A Living Weapon
    /// earns kills in any hand, but commands its gift only from the main hand.</summary>
    public static bool IsActingMainHand(int mainHand, int weaponId) => mainHand == weaponId && mainHand != 0;

    /// <summary>A fresh kill landed on the tally since the last look: the diff that lets a
    /// signature ride the proven KillTracker credit instead of running a second death detector.
    /// lastCount &lt; 0 = unprimed (first look of a battle) -- never fresh.</summary>
    public static bool FreshKill(int lastCount, int count) => lastCount >= 0 && count > lastCount;

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
    /// never changes. Counts wrap at 10000 (the slot is 4 chars). Negative counts (corrupt
    /// kills.json) clamp to 0 before the modulo to preserve the 4-char invariant.
    /// SUPERSEDED on the equip card by <see cref="KillsMeterSlot"/> (owner decision 2026-07-06):
    /// kept, since CardSites moved to the meter slot, this becomes test-only after that move. Left
    /// intact (its ByteScanTests-adjacent coverage stays green), rather than deleted, since
    /// nothing forces a rename of a working, still-referenced-by-tests helper.</summary>
    public static string KillsSlot(int count) => (Math.Max(0, count) % 10000).ToString().PadRight(4);

    /// <summary>Width (chars) of the equip card's painted meter-body slot: the widest production
    /// tier-progress body under Tuning.ProdThresholds {5,25,50} is 11 chars ("49/50 to +3",
    /// "24/25 to +2", "25/50 to +3", all exactly 11). The SINGLE source of the 11: every C#
    /// site that paints or validates the meter slot (ByteScan's validator width, CardPatterns'
    /// widest-slot math, CardScanner's slot width, CardSites' paint width) references this
    /// constant, never a literal. The Python bake (tools/lib/flavor.py's
    /// KILLS_SLOT_BODY_CHARS) is pinned to the same number by an analyze.py lockstep check.</summary>
    public const int KillsMeterSlotChars = 11;

    /// <summary>The equip card's Kills line body, fitted to <see cref="KillsMeterSlotChars"/>:
    /// VERBATIM-reused from the Attack card's own tier-progress meter
    /// (<see cref="AttackCardTail.ComposeHead"/>), never a re-implementation of the format
    /// string. ComposeHead always returns a string starting "Kills: "; this strips that prefix
    /// and pads (or, only ever reachable above an 11-digit count under prod, see below,
    /// truncates) the remainder to the fixed slot width so the painted byte width never changes.
    /// The truncate branch is unreachable under prod thresholds (the widest sub-max body is
    /// exactly 11 chars, "49/50 to +3"; the max-tier body is a bare count, which only exceeds 11
    /// chars past a 11-digit kill total), documented rather than asserted-against, since a
    /// corrupt kills.json could theoretically still produce one, and truncating (never crashing)
    /// is strictly better than the old KillsSlot's silent %10000 wrap: the max-tier meter now
    /// shows the RAW count.</summary>
    public static string KillsMeterSlotIn(int kills, int[] thresholds, string[] suffixes)
    {
        string full = AttackCardTail.ComposeHead(kills, thresholds, suffixes);
        string body = full.Substring("Kills: ".Length);
        return body.Length >= KillsMeterSlotChars
            ? body.Substring(0, KillsMeterSlotChars)
            : body.PadRight(KillsMeterSlotChars);
    }

    /// <summary>KillsMeterSlotIn driven by the compiled build's own Tuning.KillThresholds/Suffix
    /// (dev {1,2,3} under LWDEV, prod {5,25,50} otherwise): the call CardSites' paint path
    /// actually makes.</summary>
    public static string KillsMeterSlot(int kills) => KillsMeterSlotIn(kills, Tuning.KillThresholds, Tuning.Suffix);

    /// <summary>Once-per-transition latch for a nag condition that can hold true across many
    /// ticks (e.g. "no empty JobCommand slot", "record not readable yet"): fires (returns true)
    /// once on the false-&gt;true rising edge, stays silent while the condition remains true, and
    /// re-arms the next time it clears. Shared by any per-tick "still stuck" diagnostic that must
    /// not spam its sink every 33ms -- Barrage/ShadowBlade's inject-blocked nags (logging
    /// overhaul; even a Debug-tier line would otherwise bloat the file for no new information).</summary>
    public static bool StuckEdge(ref bool latched, bool condition)
    {
        if (!condition) { latched = false; return false; }
        if (latched) return false;
        latched = true;
        return true;
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
