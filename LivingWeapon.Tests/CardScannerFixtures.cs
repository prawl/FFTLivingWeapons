using System.Collections.Generic;
using LivingWeapon;

namespace LivingWeapon.Tests;

/// <summary>
/// Shared fixture builders for CardScanner tests.
/// Reduces duplication across CardScanner test files.
/// </summary>
internal static class CardScannerFixtures
{
    internal static WeaponMeta BuildMeta(string name, string flavor)
    {
        return new WeaponMeta { Name = name, Flavor = flavor };
    }

    internal static Dictionary<int, WeaponMeta> BuildMetaMap(params (int, string, string)[] items)
    {
        var map = new Dictionary<int, WeaponMeta>();
        foreach (var (id, name, flavor) in items)
            map[id] = BuildMeta(name, flavor);
        return map;
    }

    /// <summary>
    /// Build a full card buffer: name + 2-char suffix slot + description.
    /// Description = flavor + " filler." + "\n\nKills: NNNN" (where NNNN is a 4-char slot).
    /// Thin wrapper over CardFixtures.WriteCard (no pad between suffix and flavor; the
    /// " filler." rides between flavor and the kills literal -- this suite's exact layout).
    /// </summary>
    internal static byte[] BuildCard(string name, string flavor, int enc)
    {
        // Generous scratch: UTF-16 doubles every byte; the fixed parts total well under 64 chars.
        var buf = new byte[(name.Length + flavor.Length + 64) * 2];
        var (_, _, killsSlotPos) = CardFixtures.WriteCard(buf, 0, name, flavor, enc,
                                                          pad: "", filler: " filler.");
        int end = killsSlotPos + ByteScan.Enc(Signatures.KillsMeterSlot(0), enc).Length;
        var card = new byte[end];
        System.Array.Copy(buf, card, end);
        return card;
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
