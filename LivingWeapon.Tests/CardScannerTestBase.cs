using System.Collections.Generic;
using LivingWeapon;

namespace LivingWeapon.Tests;

/// <summary>
/// Shared fixture builders for CardScanner tests.
/// Reduces duplication across CardScanner test files.
/// </summary>
internal static class CardScannerTestBase
{
    internal static WeaponMeta BuildMeta(int id, string name, string flavor)
    {
        return new WeaponMeta { Name = name, Flavor = flavor };
    }

    internal static Dictionary<int, WeaponMeta> BuildMetaMap(params (int, string, string)[] items)
    {
        var map = new Dictionary<int, WeaponMeta>();
        foreach (var (id, name, flavor) in items)
            map[id] = BuildMeta(id, name, flavor);
        return map;
    }

    /// <summary>
    /// Build a full card buffer: name + 2-char suffix slot + description.
    /// Description = flavor + " filler." + "\n\nKills: NNNN" (where NNNN is a 4-char slot).
    /// </summary>
    internal static byte[] BuildCard(string name, string flavor, int enc)
    {
        var parts = new List<byte>();
        var nameB = ByteScan.Enc(name, enc);
        var suffixB = ByteScan.Enc("  ", enc);   // 2-char slot (2 spaces)
        var flavorB = ByteScan.Enc(flavor, enc);
        var fillerB = ByteScan.Enc(" filler.", enc);
        var newlineB = ByteScan.Enc("\n\n", enc);
        var killsB = ByteScan.Enc("Kills: ", enc);
        var killsSlotB = ByteScan.Enc("0   ", enc);   // 4-char slot (left-aligned digit + spaces)

        parts.AddRange(nameB);
        parts.AddRange(suffixB);
        parts.AddRange(flavorB);
        parts.AddRange(fillerB);
        parts.AddRange(newlineB);
        parts.AddRange(killsB);
        parts.AddRange(killsSlotB);

        return parts.ToArray();
    }

    /// <summary>
    /// Build a two-card buffer: cardA then cardB, contiguous.
    /// Used to test that B's "Kills: " slot is tied to B's flavor, not A's.
    /// </summary>
    internal static byte[] BuildTwoCards(string nameA, string flavorA, string nameB, string flavorB, int enc)
    {
        var parts = new List<byte>();
        parts.AddRange(BuildCard(nameA, flavorA, enc));
        parts.AddRange(BuildCard(nameB, flavorB, enc));
        return parts.ToArray();
    }
}
