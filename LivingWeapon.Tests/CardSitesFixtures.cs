using System.Collections.Generic;
using LivingWeapon;

namespace LivingWeapon.Tests;

/// <summary>
/// Shared fixture helpers for CardSites tests.
/// </summary>
internal static class CardSitesFixtures
{
    internal static Dictionary<int, WeaponMeta> BuildMeta()
    {
        return new Dictionary<int, WeaponMeta>
        {
            { 1, new WeaponMeta { Name = "Sword", Flavor = "A fine blade", Wp = 15, Cat = "Sword", Formula = 1 } },
            { 2, new WeaponMeta { Name = "Axe", Flavor = "A hefty tool", Wp = 18, Cat = "Axe", Formula = 2 } },
        };
    }
}
