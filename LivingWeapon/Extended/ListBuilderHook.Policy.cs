using System;

namespace LivingWeapon;

/// <summary>Pure, testable half of <see cref="ListBuilderHook"/>: byte/word conversion, the
/// truncation copy-back and the pool slot-address arithmetic. No hook or memory dependency.</summary>
internal sealed partial class ListBuilderHook
{
    /// <summary>Little-endian u16 words, one pair per two bytes. No terminator search: the caller
    /// already knows how many valid words came back (D2/Q5 -- every builder caller trusts the
    /// routine's own returned count rather than re-scanning for the terminator itself, and this
    /// hook does the same).</summary>
    internal static ushort[] ToWords(byte[] bytes)
    {
        var words = new ushort[bytes.Length / 2];
        for (int i = 0; i < words.Length; i++) words[i] = (ushort)(bytes[i * 2] | (bytes[i * 2 + 1] << 8));
        return words;
    }

    /// <summary>D2's copy-back: at most <paramref name="cap"/> of <paramref name="words"/>, in
    /// order, followed by <see cref="Terminator"/>. The exact bytes written into a stack caller's
    /// buffer, and (words: empty, cap: 0) the exact bytes for the D3 pool-exhaustion refusal.</summary>
    internal static byte[] TruncatedTail(ushort[] words, int cap)
    {
        int n = TruncatedCount(words, cap);
        var bytes = new byte[(n + 1) * 2];
        for (int i = 0; i < n; i++)
        {
            bytes[i * 2] = (byte)(words[i] & 0xFF);
            bytes[i * 2 + 1] = (byte)(words[i] >> 8);
        }
        bytes[n * 2] = (byte)(Terminator & 0xFF);
        bytes[n * 2 + 1] = (byte)(Terminator >> 8);
        return bytes;
    }

    /// <summary>How many entries <see cref="TruncatedTail"/> keeps: <c>min(words.Length, cap)</c>,
    /// the value the detour hands back to the caller in place of the original's own count.</summary>
    internal static int TruncatedCount(ushort[] words, int cap) => Math.Min(words.Length, Math.Max(cap, 0));

    /// <summary>D3: the byte address of pool slot <paramref name="index"/> (0..<see cref="PoolSlots"/>-1)
    /// on the page <paramref name="poolBase"/>.</summary>
    internal static long SlotAddr(long poolBase, int index) => poolBase + (long)index * SlotBytes;
}
