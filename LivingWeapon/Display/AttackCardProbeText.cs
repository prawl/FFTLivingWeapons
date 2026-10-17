using System;
using System.Text;

namespace LivingWeapon;

/// <summary>
/// Pure half of the Attack-menu census instrument. A
/// dev-build one-shot scans the heap for the standalone C-string "Attack" (the Abilities submenu
/// row label), whose hover-card Description follows it in the same packed table (2026-07-05:
/// writing past the label's 6 chars eats its NUL and bleeds into the desc). When the desc has
/// room, it overwrites only the desc with a payload that fits the desc's own footprint. The
/// #if LWDEV shell that drives the memory scan/read/write is AttackCardSpike.cs.
/// </summary>
internal static class AttackCardProbeText
{
    /// <summary>The Abilities submenu's standalone row label. 6 chars.</summary>
    internal const string Label = "Attack";

    /// <summary>The probe payload written into the desc only. "Kills: 100" is 10 chars, well under
    /// any plausible vanilla Attack description.</summary>
    internal const string Payload = "Kills: 100";

    // string.Length cannot appear in a const expression, so the length sanity check runs in the
    // static constructor. An absurdly long payload must not corrupt a card buffer.
    static AttackCardProbeText()
    {
        if (Payload.Length < 1 || Payload.Length > 32)
            throw new InvalidOperationException(
                $"AttackCardProbeText.Payload length ({Payload.Length}) must be between 1 and 32");
    }

    /// <summary>The label, encoded for the given encoding (1 = ASCII, 2 = UTF16LE).</summary>
    internal static byte[] Pattern(int enc) => ByteScan.Enc(Label, enc);

    /// <summary>The probe payload, encoded for the given encoding, without a terminator.</summary>
    internal static byte[] PayloadBytes(int enc) => ByteScan.Enc(Payload, enc);

    /// <summary>The probe payload plus one encoded NUL char. AttackCardSpike writes this, since the
    /// payload is shorter than the original desc and must re-terminate the string early. Delegates
    /// to <see cref="EncodeWithTerminator"/>.</summary>
    internal static byte[] PayloadWithTerminator(int enc) => EncodeWithTerminator(Payload, enc);

    /// <summary>Encode any string plus one encoded NUL terminator. General form of
    /// <see cref="PayloadWithTerminator"/>, used by AttackCard.cs (the production painter).</summary>
    internal static byte[] EncodeWithTerminator(string s, int enc)
    {
        byte[] textBytes = ByteScan.Enc(s, enc);
        var result = new byte[textBytes.Length + enc];
        Array.Copy(textBytes, result, textBytes.Length);
        // The trailing bytes are zero-initialized. The NUL terminator.
        return result;
    }

    /// <summary>Offset of the desc string relative to a label hit at pos. The label's encoded bytes
    /// plus its NUL (7 bytes for enc 1, 14 for enc 2).</summary>
    internal static int DescStart(int pos, int enc) => pos + (Label.Length + 1) * enc;

    /// <summary>True when the label matches at pos and is a standalone C-string. NUL (or buffer
    /// start) just before pos, NUL just after the label. Stricter than HeaderProbeText.IsExactHit
    /// because "Attack" also appears inside prose ("Attacks", "attack power"). A boundary that
    /// cannot be checked inside the buffer counts as a miss.</summary>
    internal static bool IsStandaloneHit(byte[] buffer, int pos, int enc)
    {
        if (buffer == null || pos < 0) return false;
        byte[] pat = Pattern(enc);
        if (pos + pat.Length > buffer.Length) return false;
        for (int i = 0; i < pat.Length; i++)
            if (buffer[pos + i] != pat[i]) return false;

        if (pos > 0)
        {
            int beforePos = pos - enc;
            if (!IsEncodedNul(buffer, beforePos, enc)) return false;
        }
        // pos == 0. Buffer start satisfies the "before" boundary.

        int afterPos = pos + pat.Length;
        if (!IsEncodedNul(buffer, afterPos, enc)) return false;

        return true;
    }

    /// <summary>Reads the NUL-terminated string at descStart, capped at capChars. Returns ("", 0)
    /// when descStart is out of range or immediately NUL.</summary>
    internal static (string Text, int Chars) ReadDesc(byte[] buffer, int descStart, int enc, int capChars)
    {
        if (buffer == null || descStart < 0 || descStart >= buffer.Length) return ("", 0);

        var sb = new StringBuilder();
        int pos = descStart;
        int count = 0;
        while (count < capChars)
        {
            if (pos < 0 || pos + enc > buffer.Length) break;   // ran off the buffer. Stop, don't throw
            if (IsEncodedNul(buffer, pos, enc)) break;         // terminator found

            char c = enc == 2 ? (char)(buffer[pos] | (buffer[pos + 1] << 8)) : (char)buffer[pos];
            sb.Append(c);
            count++;
            pos += enc;
        }
        return (sb.ToString(), count);
    }

    /// <summary>True when descChars (the original desc's char count from census time) is at least
    /// neededChars (the text to write, excluding its NUL), so the write stays inside the original
    /// footprint. Inclusive boundary. AttackCard.cs passes its composed line's length.</summary>
    internal static bool FitsFootprint(int descChars, int neededChars) => descChars >= neededChars;

    /// <summary>As above, fixed at this probe's Payload.Length.</summary>
    internal static bool FitsFootprint(int descChars) => FitsFootprint(descChars, Payload.Length);

    /// <summary>A hex plus printable-ASCII dump of the bytes within a fixed radius around the hit
    /// at pos, for logging. Clamps to the buffer bounds. See HeaderProbeText.FormatContext.</summary>
    internal static string FormatContext(byte[] buffer, int pos, int enc)
    {
        const int Radius = 48;
        if (buffer == null || buffer.Length == 0) return "(no buffer)";

        byte[] pat = Pattern(enc);
        int hitEnd = pos + pat.Length;

        int beforeEnd = Math.Max(0, Math.Min(pos, buffer.Length));
        int beforeStart = Math.Max(0, beforeEnd - Radius);
        int beforeLen = beforeEnd - beforeStart;

        int afterStart = Math.Max(0, Math.Min(hitEnd, buffer.Length));
        int afterLen = Math.Max(0, Math.Min(buffer.Length, afterStart + Radius) - afterStart);

        return "before: " + Dump(buffer, beforeStart, beforeLen) + " | after: " + Dump(buffer, afterStart, afterLen);
    }

    private static string Dump(byte[] buffer, int start, int len)
    {
        if (len <= 0) return "(none)";
        var hex = new StringBuilder(len * 3);
        var gloss = new StringBuilder(len);
        for (int i = 0; i < len; i++)
        {
            byte b = buffer[start + i];
            if (i > 0) hex.Append(' ');
            hex.Append(b.ToString("X2"));
            gloss.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
        }
        return hex + " [" + gloss + "]";
    }

    /// <summary>True if the encoded char at charPos is NUL. False when it falls outside the buffer.</summary>
    private static bool IsEncodedNul(byte[] buffer, int charPos, int enc)
    {
        if (charPos < 0 || charPos + enc > buffer.Length) return false;
        for (int i = 0; i < enc; i++)
            if (buffer[charPos + i] != 0) return false;
        return true;
    }
}
