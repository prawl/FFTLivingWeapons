using System;

namespace LivingWeapon;

/// <summary>
/// LW-295: WeaponPalette's "what glow tier do we want painted" half -- the memory-reading
/// counterpart to WeaponPalette.Policy.cs's pure decision table. Split into its own partial
/// (WeaponPalette.cs's stateful half was already at the 200-line refactor trigger) because the
/// real seam here is "what do we want painted" (this file) versus "how the banks are painted"
/// (WeaponPalette.cs's PaintBanks/RestoreBanks, unchanged by this feature).
/// </summary>
internal sealed partial class WeaponPalette
{
    /// <summary>Only a PLAYER wielder's swing glows (owner decision, LW-295): reads the acting
    /// unit's own (level,brave,faith) fingerprint -- the same fields <see cref="Band.FlagOwner"/>'s
    /// caller already trusts, FlagOwner's own reads -- and asks the roster whether any row wields
    /// <paramref name="weaponId"/> in either hand at that exact fingerprint. Judged on
    /// matchCount &gt;= 1, NEVER the resolver's own -1 return: the 4-arg
    /// <see cref="Wielder.ResolveAnyHandNameId(IGameMemory,int,(int,int,int),out int)"/> returns
    /// -1 for BOTH zero matches and 2+ matches (the historic conflation bug), but here ambiguity
    /// (2+ roster rows sharing this weapon+fingerprint) is still unambiguously a PLAYER wielder --
    /// the question this gate asks is only party-ness, never which party unit. Zero matches means
    /// an enemy (or an ally/guest occupying a roster row -- an accepted residual, see the LW-295
    /// build plan's R3) -- returns tier 0 (plain, no glow).</summary>
    private int DesiredGlow(IGameMemory mem, long entry, int weaponId)
    {
        var fp = ((int)mem.U8(entry + Offsets.ALevel), (int)mem.U8(entry + Offsets.ABrave),
            (int)mem.U8(entry + Offsets.AFaith));
        Wielder.ResolveAnyHandNameId(mem, weaponId, fp, out int matchCount);
        if (matchCount == 0) return 0;   // no roster row wields it at this fingerprint -- an enemy
        int tier = Tuning.TierOf(_kills, weaponId);
        return Math.Clamp(tier, 0, Tuning.GlowFactors.Length - 1);
    }
}
