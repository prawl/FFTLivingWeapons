using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>Pure, testable half of <see cref="OrderRebuildHook"/>: list parsing, the dropped-word
/// diff, and the re-append byte encoding. No hook or memory dependency.</summary>
internal sealed partial class OrderRebuildHook
{
    /// <summary>Little-endian u16 words up to (not including) the first <see cref="Terminator"/>.
    /// Null when no terminator is found inside the buffer (fail-safe: the caller passes through);
    /// an empty list (terminator first) is an empty array.</summary>
    public static ushort[]? ParseList(byte[] bytes)
    {
        int max = bytes.Length / 2;
        for (int i = 0; i < max; i++)
        {
            if (Word(bytes, i) == Terminator)
            {
                var result = new ushort[i];
                for (int j = 0; j < i; j++) result[j] = Word(bytes, j);
                return result;
            }
        }
        return null;
    }

    /// <summary>The words of <paramref name="input"/> whose masked id (<c>w &amp; IdMask</c>) is
    /// absent from <paramref name="output"/>, in input order, each id at most once (first
    /// occurrence wins, its flag bits preserved).</summary>
    public static ushort[] DroppedWords(ushort[] input, ushort[] output)
    {
        var present = new HashSet<int>();
        foreach (var w in output) present.Add(w & IdMask);
        var seen = new HashSet<int>();
        var dropped = new List<ushort>();
        foreach (var w in input)
        {
            int id = w & IdMask;
            if (present.Contains(id) || !seen.Add(id)) continue;
            dropped.Add(w);
        }
        return dropped.ToArray();
    }

    /// <summary><paramref name="dropped"/> as little-endian words followed by the terminator: the
    /// exact tail written over the rebuilt list's own terminator.</summary>
    public static byte[] TailBytes(ushort[] dropped)
    {
        var tail = new byte[dropped.Length * 2 + 2];
        for (int i = 0; i < dropped.Length; i++)
        {
            tail[i * 2] = (byte)(dropped[i] & 0xFF);
            tail[i * 2 + 1] = (byte)(dropped[i] >> 8);
        }
        tail[dropped.Length * 2] = (byte)(Terminator & 0xFF);
        tail[dropped.Length * 2 + 1] = (byte)(Terminator >> 8);
        return tail;
    }

    private static ushort Word(byte[] b, int i) => (ushort)(b[i * 2] | (b[i * 2 + 1] << 8));
}
