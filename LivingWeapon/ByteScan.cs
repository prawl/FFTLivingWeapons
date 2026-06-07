using System;
using System.Collections.Generic;
using System.Text;

namespace LivingWeapon;

/// <summary>Byte-pattern helpers for the display scan: name/slot encoding + searching.</summary>
internal static class ByteScan
{
    /// <summary>ASCII bytes with non-ASCII chars DROPPED (not replaced with '?'). Matches the
    /// bake; Encoding.ASCII would emit 0x3F and silently never match an accented name.</summary>
    public static byte[] Ascii(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s) if (c < 0x80) sb.Append(c);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    public static byte[] Utf16(string s) => Encoding.Unicode.GetBytes(s);

    /// <summary>enc 1 = ASCII, enc 2 = UTF-16-LE.</summary>
    public static byte[] Enc(string s, int enc) => enc == 1 ? Ascii(s) : Utf16(s);

    public static List<byte[]> Slots(int enc, string[] values)
    {
        var list = new List<byte[]>(values.Length);
        foreach (var v in values) list.Add(Enc(v, enc));
        return list;
    }

    /// <summary>True if buf[pos..pos+sw] equals any of the option byte arrays.</summary>
    public static bool MatchesAny(byte[] buf, int pos, List<byte[]> options, int sw)
    {
        foreach (var o in options)
        {
            if (o.Length != sw) continue;
            bool ok = true;
            for (int j = 0; j < sw; j++)
                if (buf[pos + j] != o[j]) { ok = false; break; }
            if (ok) return true;
        }
        return false;
    }

    /// <summary>Index of needle within buf[start..end), or -1.</summary>
    public static int FindIn(byte[] buf, byte[] needle, int start, int end)
    {
        if (needle.Length == 0 || end - start < needle.Length) return -1;
        int rel = buf.AsSpan(start, end - start).IndexOf(needle.AsSpan());
        return rel < 0 ? -1 : start + rel;
    }
}
