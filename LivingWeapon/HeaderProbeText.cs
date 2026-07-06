using System;
using System.Text;

namespace LivingWeapon;

/// <summary>
/// Pure half of the LW-27 header-repaint research instrument (docs/TODO.md Now section): a
/// dev-build one-shot scans the heap for the equip card's generic brown header label
/// ("Description") and overwrites it with a same-length probe payload, to learn where the
/// header buffers live relative to the card body Display/CardSites already paints, whether a
/// write holds across card refreshes, the real slot capacity, and which encodings are on
/// screen. Everything here is pure string/byte logic, always compiled and unit-tested; the
/// #if LWDEV shell that drives the actual memory scan/read/write is HeaderSpike.cs.
/// </summary>
internal static class HeaderProbeText
{
    /// <summary>The generic header UI chrome this probe targets. 11 chars.</summary>
    internal const string Label = "Description";

    /// <summary>The same-length probe payload (target shape: "Kills: 100" plus a trailing
    /// space to reach Label's own length). Exactly 11 chars.</summary>
    internal const string Payload = "Kills: 100 ";

    // Compile-time-checked invariant (as close as C# allows: string.Length cannot appear in a
    // const expression, so this runs in the static constructor instead, before any other code
    // can observe Label or Payload): Payload must be exactly Label's length so HeaderSpike's F8
    // overwrite is always same-length by construction, never a runtime guess. A mismatch here
    // throws immediately at first touch of this class rather than silently corrupting a card
    // buffer later. The per-encoding byte-length analog (the actual write-safety property) is
    // separately covered by HeaderProbeTextTests.PayloadBytes_length_matches_Pattern_length,
    // the non-vacuous load-bearing test.
    static HeaderProbeText()
    {
        if (Payload.Length != Label.Length)
            throw new InvalidOperationException(
                $"HeaderProbeText.Payload length ({Payload.Length}) must equal Label length ({Label.Length})");
    }

    /// <summary>The label, encoded for the given encoding (1 = ASCII, 2 = UTF16LE).</summary>
    internal static byte[] Pattern(int enc) => ByteScan.Enc(Label, enc);

    /// <summary>The probe payload, encoded for the given encoding. Same byte length as
    /// <see cref="Pattern"/> for the same enc (proven by the load-bearing test), so an
    /// in-place overwrite never resizes the buffer.</summary>
    internal static byte[] PayloadBytes(int enc) => ByteScan.Enc(Payload, enc);

    /// <summary>True when Label matches at pos AND the match is a whole word: the character
    /// right after it is not an ASCII letter or digit (rejects "Descriptions"), and the
    /// character right before it is not an ASCII letter (rejects "XDescription"). A boundary
    /// character that falls outside the buffer, or that cannot be fully read as a plain ASCII
    /// char in this encoding, is treated as "not a letter/digit" so an edge hit is never
    /// wrongly rejected.</summary>
    internal static bool IsExactHit(byte[] buffer, int pos, int enc)
    {
        if (buffer == null || pos < 0) return false;
        byte[] pat = Pattern(enc);
        if (pos + pat.Length > buffer.Length) return false;
        for (int i = 0; i < pat.Length; i++)
            if (buffer[pos + i] != pat[i]) return false;

        if (IsBoundaryLetterOrDigit(buffer, pos + pat.Length, enc)) return false;   // "Descriptions"
        if (IsBoundaryLetter(buffer, pos - enc, enc)) return false;                 // "XDescription"
        return true;
    }

    /// <summary>A compact printable dump of the bytes in a fixed radius before and after the
    /// hit at pos (hex plus a printable-ASCII gloss), for logging the structural surroundings
    /// of a live hit. Clamps to the buffer bounds and never throws, including when pos is at or
    /// past either edge.</summary>
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

    /// <summary>True if the encoded char starting at charPos is a plain ASCII letter or digit.</summary>
    private static bool IsBoundaryLetterOrDigit(byte[] buffer, int charPos, int enc)
        => TryReadPlainAsciiChar(buffer, charPos, enc, out byte b) && IsAsciiLetterOrDigit(b);

    /// <summary>True if the encoded char starting at charPos is a plain ASCII letter.</summary>
    private static bool IsBoundaryLetter(byte[] buffer, int charPos, int enc)
        => TryReadPlainAsciiChar(buffer, charPos, enc, out byte b) && IsAsciiLetter(b);

    /// <summary>Reads one plain-ASCII char at charPos for the given encoding. Returns false
    /// (never throws) when charPos falls outside the buffer, the char cannot be fully read, or
    /// (enc 2) the high byte is nonzero, meaning it is not a plain ASCII char in UTF16LE.</summary>
    private static bool TryReadPlainAsciiChar(byte[] buffer, int charPos, int enc, out byte lo)
    {
        lo = 0;
        if (charPos < 0 || charPos >= buffer.Length) return false;
        lo = buffer[charPos];
        if (enc == 2)
        {
            int hiPos = charPos + 1;
            if (hiPos >= buffer.Length) return false;
            if (buffer[hiPos] != 0) return false;
        }
        return true;
    }

    private static bool IsAsciiLetter(byte b) => (b >= (byte)'A' && b <= (byte)'Z') || (b >= (byte)'a' && b <= (byte)'z');
    private static bool IsAsciiLetterOrDigit(byte b) => IsAsciiLetter(b) || (b >= (byte)'0' && b <= (byte)'9');
}
