using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace LivingWeapon;

/// <summary>
/// Decides whether a piece of on-screen text is one of our weapon names. It never touches game memory, so a unit test can check it.
/// </summary>
internal static class NameTapPolicy
{
    private const int TailChars = 2;
    private const int ClipChars = 60;

    /// <summary>Our weapon names, longest first, so a long name that contains a shorter one wins over it.</summary>
    internal static IReadOnlyList<(int Id, string Name)> Order(IReadOnlyDictionary<int, WeaponMeta> meta)
        => meta.Where(kv => !string.IsNullOrEmpty(kv.Value?.Name))
               .Select(kv => (Id: kv.Key, Name: kv.Value.Name))
               .OrderByDescending(p => p.Name.Length)
               .ThenBy(p => p.Id)
               .ToList();

    /// <summary>Finds the first of our weapon names in the text and the two characters after it, where the +1, +2 or +3 tag sits.</summary>
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

    /// <summary>The same search for text stored two bytes per letter (UTF-16LE), with where the name starts and the tag after it.</summary>
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

    /// <summary>True for a placeholder like &lt;string=lstr0&gt;, where the game fills a word in later, so a name there never shows in this text.</summary>
    internal static bool IsTokenHead(string text) => text.Contains("<string=", StringComparison.Ordinal);

    internal static string Clip(string text) => text.Length <= ClipChars ? text : text[..ClipChars];
}
