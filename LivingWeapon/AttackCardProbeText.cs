using System;
using System.Text;

namespace LivingWeapon;

/// <summary>
/// Pure half of the LW-31 Attack-menu census instrument (docs/TODO.md Now section, stage 1): a
/// dev-build one-shot scans the heap for the standalone C-string "Attack" (the Abilities
/// submenu's row label, which the hover card's Description immediately follows in the same
/// packed table (owner-proven live 2026-07-05: writing past the label's 6 chars eats its NUL
/// and bleeds into the desc), and, when the desc has room, overwrites ONLY the desc with a
/// probe payload that never exceeds the desc's own footprint. Everything here is pure
/// string/byte logic, always compiled and unit-tested; the #if LWDEV shell that drives the
/// actual memory scan/read/write is AttackCardSpike.cs.
/// </summary>
internal static class AttackCardProbeText
{
    /// <summary>The Abilities submenu's standalone row label. 6 chars.</summary>
    internal const string Label = "Attack";

    /// <summary>The probe payload written into the DESC only (never the label). Deliberately
    /// short: "Kills: 100" is 10 chars, well under any plausible vanilla Attack description, so
    /// the footprint check (<see cref="FitsFootprint"/>) always has room to spare in practice.</summary>
    internal const string Payload = "Kills: 100";

    // Compile-time-checked invariant (as close as C# allows: string.Length cannot appear in a
    // const expression, so this runs in the static constructor instead, before any other code
    // can observe Payload): the payload must be a plausible short probe string, well under any
    // real description's length, so a bug that sets it absurdly long can never silently corrupt
    // a card buffer. Mirrors HeaderProbeText's static-ctor sanity check.
    static AttackCardProbeText()
    {
        if (Payload.Length < 1 || Payload.Length > 32)
            throw new InvalidOperationException(
                $"AttackCardProbeText.Payload length ({Payload.Length}) must be between 1 and 32");
    }

    /// <summary>The label, encoded for the given encoding (1 = ASCII, 2 = UTF16LE).</summary>
    internal static byte[] Pattern(int enc) => ByteScan.Enc(Label, enc);

    /// <summary>The probe payload, encoded for the given encoding, WITHOUT a terminator.</summary>
    internal static byte[] PayloadBytes(int enc) => ByteScan.Enc(Payload, enc);

    /// <summary>The probe payload plus one encoded NUL char (1 byte for enc 1, 2 zero bytes for
    /// enc 2): this is what AttackCardSpike actually writes into a desc, since the payload is
    /// SHORTER than the original desc, so the write must re-terminate the string early rather
    /// than leaving the tail of the old desc dangling after it. Delegates to the general
    /// <see cref="EncodeWithTerminator"/> below (promoted for AttackCard.cs's production
    /// composer, whose written text varies in content/length unlike this fixed dev Payload).</summary>
    internal static byte[] PayloadWithTerminator(int enc) => EncodeWithTerminator(Payload, enc);

    /// <summary>Encode any string plus one encoded NUL terminator (1 zero byte for enc 1, 2 for
    /// enc 2). General form of <see cref="PayloadWithTerminator"/>: AttackCard.cs (LW-31's
    /// production painter) writes an arbitrary composed line here, not the fixed dev Payload.</summary>
    internal static byte[] EncodeWithTerminator(string s, int enc)
    {
        byte[] textBytes = ByteScan.Enc(s, enc);
        var result = new byte[textBytes.Length + enc];
        Array.Copy(textBytes, result, textBytes.Length);
        // result[textBytes.Length .. ] is already zero-initialized: the NUL terminator.
        return result;
    }

    /// <summary>The offset where the desc string begins, relative to a label hit at pos: past
    /// the label's own encoded bytes plus its NUL terminator. enc 1: 6 bytes + 1 = 7. enc 2:
    /// 12 bytes + 2 = 14.</summary>
    internal static int DescStart(int pos, int enc) => pos + (Label.Length + 1) * enc;

    /// <summary>True when the label matches at pos AND it is a STANDALONE C-string: the encoded
    /// char immediately before pos is NUL (or pos is the very start of the buffer), and the
    /// encoded char immediately after the label is NUL. Stricter than a simple word-boundary
    /// check (HeaderProbeText.IsExactHit's letter-boundary rule) because "Attack" legitimately
    /// appears inside prose ("Attacks", "attack power"): only the standalone row/hover-card
    /// string is the target here. A boundary that cannot be proven NUL within the buffer (an
    /// edge with no bytes to check) is treated as NOT a hit: this probe would rather miss an
    /// edge-of-buffer copy than guess wrong and corrupt live prose.</summary>
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
        // pos == 0: buffer start counts as a satisfied "before" boundary.

        int afterPos = pos + pat.Length;
        if (!IsEncodedNul(buffer, afterPos, enc)) return false;

        return true;
    }

    /// <summary>Reads the NUL-terminated string starting at descStart, capped at capChars.
    /// Returns ("", 0) when descStart is out of range or immediately NUL. Never throws, even
    /// when the string runs all the way to the buffer end without a NUL.</summary>
    internal static (string Text, int Chars) ReadDesc(byte[] buffer, int descStart, int enc, int capChars)
    {
        if (buffer == null || descStart < 0 || descStart >= buffer.Length) return ("", 0);

        var sb = new StringBuilder();
        int pos = descStart;
        int count = 0;
        while (count < capChars)
        {
            if (pos < 0 || pos + enc > buffer.Length) break;   // ran off the buffer: stop, don't throw
            if (IsEncodedNul(buffer, pos, enc)) break;         // terminator found

            char c = enc == 2 ? (char)(buffer[pos] | (buffer[pos + 1] << 8)) : (char)buffer[pos];
            sb.Append(c);
            count++;
            pos += enc;
        }
        return (sb.ToString(), count);
    }

    /// <summary>General form: true when descChars (the original desc's own char count, found at
    /// census time) is at least neededChars (the text about to be written, EXCLUDING its own NUL
    /// terminator): the write can never exceed the original desc's own byte footprint. Boundary
    /// is inclusive: a desc exactly neededChars long fits exactly. AttackCard.cs (LW-31's
    /// production composer) checks its own composed line's length here instead of the fixed dev
    /// Payload.Length the 1-arg overload below fixes.</summary>
    internal static bool FitsFootprint(int descChars, int neededChars) => descChars >= neededChars;

    /// <summary>True when the write NEVER exceeds the original desc's own footprint, fixed at
    /// this probe's own Payload.Length. Delegates to the general 2-arg overload above.</summary>
    internal static bool FitsFootprint(int descChars) => FitsFootprint(descChars, Payload.Length);

    /// <summary>A compact printable dump of the bytes in a fixed radius before and after the
    /// hit at pos (hex plus a printable-ASCII gloss), for logging the structural surroundings of
    /// a live hit. Clamps to the buffer bounds and never throws, including when pos is at or
    /// past either edge. Mirrors HeaderProbeText.FormatContext.</summary>
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

    /// <summary>True if the encoded char starting at charPos is a NUL char (1 zero byte for
    /// enc 1, 2 zero bytes for enc 2). False (never throws) when charPos falls outside the
    /// buffer or the char cannot be fully read.</summary>
    private static bool IsEncodedNul(byte[] buffer, int charPos, int enc)
    {
        if (charPos < 0 || charPos + enc > buffer.Length) return false;
        for (int i = 0; i < enc; i++)
            if (buffer[charPos + i] != 0) return false;
        return true;
    }
}
