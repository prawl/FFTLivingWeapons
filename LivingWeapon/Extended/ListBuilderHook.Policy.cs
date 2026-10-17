using System;

namespace LivingWeapon;

/// <summary>Pure half of <see cref="ListBuilderHook"/> (byte/word conversion, truncation copy-back, pool slot addresses).</summary>
internal sealed partial class ListBuilderHook
{
    /// <summary>Little-endian u16 words, one per two bytes. No terminator search, since the caller
    /// already has the valid word count (D2/Q5).</summary>
    internal static ushort[] ToWords(byte[] bytes)
    {
        var words = new ushort[bytes.Length / 2];
        for (int i = 0; i < words.Length; i++) words[i] = (ushort)(bytes[i * 2] | (bytes[i * 2 + 1] << 8));
        return words;
    }

    /// <summary>D2's copy-back. At most <paramref name="cap"/> of <paramref name="words"/>, then <see cref="Terminator"/>.
    /// Empty words with cap 0 gives the bytes for the D3 pool-exhaustion refusal.</summary>
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

    /// <summary>Entries <see cref="TruncatedTail"/> keeps, <c>min(words.Length, cap)</c>. The detour returns it as the count.</summary>
    internal static int TruncatedCount(ushort[] words, int cap) => Math.Min(words.Length, Math.Max(cap, 0));

    /// <summary>D3 byte address of pool slot <paramref name="index"/> (0..<see cref="PoolSlots"/>-1) on page <paramref name="poolBase"/>.</summary>
    internal static long SlotAddr(long poolBase, int index) => poolBase + (long)index * SlotBytes;
}
