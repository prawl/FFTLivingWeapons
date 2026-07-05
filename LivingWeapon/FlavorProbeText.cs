using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Pure half of the P4 flavor-render probe (docs/RELIQUARY_AC.md P4): a dev-build one-shot
/// overwrites ONE weapon's equip-card flavor line with a same-length ASCII test string to prove
/// the on-screen card renders from the buffers Display/CardSites already paints. Everything here
/// is pure string/collection logic, always compiled and unit-tested; the #if LWDEV shell that
/// drives the actual memory read/write is FlavorSpike.cs.
/// </summary>
internal static class FlavorProbeText
{
    private const string BaseText = "P4 FLAVOR PROBE -- THE BLADE REMEMBERS";

    /// <summary>Returns exactly charCount ASCII chars: BaseText truncated when charCount is
    /// shorter, right-padded with spaces when it is longer. charCount &lt;= 0 returns "".</summary>
    internal static string Compose(int charCount)
    {
        if (charCount <= 0) return "";
        return charCount <= BaseText.Length ? BaseText.Substring(0, charCount) : BaseText.PadRight(charCount);
    }

    /// <summary>Char count for a flavor pattern's encoded byte length: UTF-16LE (enc 2) is
    /// 2 bytes/char, so the byte length halves back to a char count; ASCII (enc 1) byte length
    /// IS the char count.</summary>
    internal static int CharCount(int flavorByteLen, int enc) => enc == 2 ? flavorByteLen / 2 : flavorByteLen;

    /// <summary>The lowest weapon id with at least one Kills site cached, 0 when none. Suffix-only
    /// sites (IsKills: false) are ignored -- the probe overwrites the FLAVOR anchor, which only a
    /// kills site points at.</summary>
    internal static int TargetWeapon(IEnumerable<CardSites.Site> sites)
    {
        int best = 0;
        foreach (var s in sites)
        {
            if (!s.IsKills) continue;
            if (best == 0 || s.Id < best) best = s.Id;
        }
        return best;
    }
}
