namespace LivingWeapon;

/// <summary>
/// Guarded single-bit writes on live game memory, taking the (byteOffset, mask) results of the
/// <see cref="Signatures"/> encoders. Both ops return false and write nothing on an unwritable
/// page or a failed pre-read, so a bad read can never be taken for a zero byte.
/// </summary>
internal static class MemBits
{
    // Both public overloads go through this; the real logic is in the IGameMemory overloads so a fake can test it.
    private static readonly LiveMemory _live = new();

    /// <summary>Guarded OR-set of a single bit; returns true when the bit reads set afterwards.
    /// <paramref name="wasSet"/> is the state before the write, so false means the engine had
    /// cleared the bit and we re-armed it.</summary>
    public static bool OrSet(long addr, byte mask, out bool wasSet) => OrSet(_live, addr, mask, out wasSet);

    /// <summary>Guarded AND-clear of a single bit; returns true when the bit reads clear afterwards.
    /// Release counterpart of <see cref="OrSet(long, byte, out bool)"/>.</summary>
    public static bool Clear(long addr, byte mask) => Clear(_live, addr, mask);

    /// <summary>OrSet over the IGameMemory seam. A failed pre-read returns false with no write;
    /// the old Mem.U8 pre-read gave 0 on failure and wrote the bare mask, zeroing the other 7 bits.</summary>
    internal static bool OrSet(IGameMemory mem, long addr, byte mask, out bool wasSet)
    {
        wasSet = false;
        if (!mem.Writable(addr, 1)) return false;
        if (!mem.TryReadBytes(addr, 1, out var pre)) return false;   // failed read. Bail, no write
        byte cur = pre[0];
        wasSet = (cur & mask) != 0;
        if (!wasSet) mem.W8(addr, (byte)(cur | mask));
        return (mem.U8(addr) & mask) != 0;
    }

    /// <summary>Clear over the IGameMemory seam. A failed pre-read returns false; the old
    /// read-as-0 path skipped the write and then reported a clear it never verified.</summary>
    internal static bool Clear(IGameMemory mem, long addr, byte mask)
    {
        if (!mem.Writable(addr, 1)) return false;
        if (!mem.TryReadBytes(addr, 1, out var pre)) return false;   // failed read. Bail, no false "clear"
        byte cur = pre[0];
        if ((cur & mask) != 0) mem.W8(addr, (byte)(cur & ~mask));
        return (mem.U8(addr) & mask) == 0;
    }
}
