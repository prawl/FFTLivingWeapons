using System;

namespace LivingWeapon;

/// <summary>
/// Which glow tier to paint, read from memory. WeaponPalette.Policy.cs holds the pure
/// decision table; PaintBanks/RestoreBanks in WeaponPalette.cs do the painting.
/// </summary>
internal sealed partial class WeaponPalette
{
    /// <summary>Only a player wielder's swing glows. Reads the acting unit's
    /// (level,brave,faith), the same fields <see cref="Band.FlagOwner"/> uses, and asks the roster
    /// whether any row wields <paramref name="weaponId"/> at that fingerprint. Judges on matchCount
    /// &gt;= 1, not the -1 return of <see cref="Wielder.ResolveAnyHandNameId(IGameMemory,int,(int,int,int),out int)"/>,
    /// which is -1 for both zero and 2+ matches; 2+ is still a party unit. Zero matches means an
    /// enemy (or an ally/guest on a roster row, an accepted residual, LW-295 build plan R3) and
    /// returns tier 0, plain.</summary>
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
