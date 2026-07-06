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
    public static bool MatchesAny(byte[] buf, int pos, IReadOnlyList<byte[]> options, int sw)
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

    /// <summary>Slot validator for the "Kills NNNN" counter: left-aligned digits padded with spaces.
    /// Char 0 must be an ASCII digit; chars 1..3 are digits until the first space, then only spaces
    /// (a digit after a space = not our slot). This is a strict superset of the old all-digits form
    /// ("0042"-era buffers still validate). enc-aware: UTF-16 high byte must be 0x00 for every
    /// char (covers both digits and the 0x20 space).</summary>
    public static bool KillsDigits(byte[] buf, int pos, int enc)
    {
        // char 0 must be a digit
        if (buf[pos] is < (byte)'0' or > (byte)'9') return false;
        if (enc == 2 && buf[pos + 1] != 0) return false;
        bool seenSpace = false;
        for (int d = 1; d < 4; d++)
        {
            byte b = buf[pos + d * enc];
            if (enc == 2 && buf[pos + d * enc + 1] != 0) return false;
            if (seenSpace)
            {
                if (b != (byte)' ') return false;   // digit after space -> not our slot
            }
            else if (b == (byte)' ')
            {
                seenSpace = true;
            }
            else if (b is < (byte)'0' or > (byte)'9')
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Slot validator for the equip card's tier-progress METER body ("0/5 to +   ",
    /// "49/50 to +3", "55         ", width chars wide, Signatures.KillsMeterSlotChars in
    /// production). Char 0 must be an ASCII digit (the count always leads, same rule as
    /// KillsDigits); every char across the WHOLE width is restricted to the meter's own
    /// alphabet (digits, '/', ' ', '+', and the two letters of "to") so a stale unrelated buffer
    /// past a short literal never misreads as valid. enc-aware: UTF-16 high byte must be 0x00 for
    /// every char. Bounds-safe: false if [pos, pos + width*enc) does not fit in buf. Additive:
    /// KillsDigits above is untouched (stays test-only after CardScanner/CardSites move to this
    /// wider validator).</summary>
    public static bool MeterSlotDigits(byte[] buf, int pos, int enc, int width)
    {
        if (pos < 0 || pos + width * enc > buf.Length) return false;
        if (buf[pos] is < (byte)'0' or > (byte)'9') return false;
        for (int i = 0; i < width; i++)
        {
            byte b = buf[pos + i * enc];
            if (enc == 2 && buf[pos + i * enc + 1] != 0) return false;
            bool ok = (b is >= (byte)'0' and <= (byte)'9')
                      || b == (byte)'/' || b == (byte)' ' || b == (byte)'+'
                      || b == (byte)'t' || b == (byte)'o';
            if (!ok) return false;
        }
        return true;
    }

    /// <summary>Find all occurrences of needle in buf within [from, toExclusive), appending
    /// absolute positions where the match STARTS. The needle itself may extend past toExclusive
    /// as long as it fits in buf. Empty needle yields no hits.</summary>
    public static void FindAll(byte[] buf, byte[] needle, int from, int toExclusive,
                               List<int> hits)
    {
        if (needle.Length == 0) return;
        int pos = from;
        var span = buf.AsSpan();
        while (pos <= toExclusive - 1)
        {
            int rel = span.Slice(pos).IndexOf(needle.AsSpan());
            if (rel < 0) break;
            pos += rel;
            if (pos < toExclusive && pos + needle.Length <= buf.Length)
                hits.Add(pos);
            pos++;
        }
    }

}
