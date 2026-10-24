using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace LivingWeapon;

/// <summary>
/// Pure matching for the name tap, so what counts as a weapon name hit is a plain unit test.
/// </summary>
internal static class NameTapPolicy
{
    private const int TailChars = 2;
    private const int ClipChars = 60;

    /// <summary>Longest name first, so a name that contains a shorter one is tried before it.</summary>
    internal static IReadOnlyList<(int Id, string Name)> Order(IReadOnlyDictionary<int, WeaponMeta> meta)
        => meta.Where(kv => !string.IsNullOrEmpty(kv.Value?.Name))
               .Select(kv => (Id: kv.Key, Name: kv.Value.Name))
               .OrderByDescending(p => p.Name.Length)
               .ThenBy(p => p.Id)
               .ToList();

    /// <summary>First name in the given order found in text, with up to two chars after its first occurrence.</summary>
    internal static bool TryMatch(string text, IReadOnlyList<(int Id, string Name)> ordered, out int id, out string tail)
    {
        foreach (var (nameId, name) in ordered)
        {
            int at = text.IndexOf(name, StringComparison.Ordinal);
            if (at < 0) continue;
            int from = at + name.Length;
            id = nameId;
            tail = text.Substring(from, Math.Min(TailChars, text.Length - from));
            return true;
        }
        id = -1;
        tail = "";
        return false;
    }

    /// <summary>First name in the given order whose UTF-16LE bytes sit anywhere in raw, byte aligned, with up to two wide chars after it.</summary>
    internal static bool TryMatchUtf16(byte[] raw, IReadOnlyList<(int Id, string Name)> ordered, out int id, out int offset, out string tail)
    {
        foreach (var (nameId, name) in ordered)
        {
            var wide = ByteScan.Utf16(name);
            int at = raw.AsSpan().IndexOf(wide);
            if (at < 0) continue;
            int from = at + wide.Length;
            int len = Math.Min(TailChars * 2, raw.Length - from) / 2 * 2;
            id = nameId;
            offset = at;
            tail = Encoding.Unicode.GetString(raw, from, len);
            int nul = tail.IndexOf('\0');
            if (nul >= 0) tail = tail[..nul];
            return true;
        }
        id = -1;
        offset = -1;
        tail = "";
        return false;
    }

    /// <summary>True when the text holds a bound string token, which a name match cannot see through.</summary>
    internal static bool IsTokenHead(string text) => text.Contains("<string=", StringComparison.Ordinal);

    internal static string Clip(string text) => text.Length <= ClipChars ? text : text[..ClipChars];
}
