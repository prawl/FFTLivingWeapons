namespace LivingWeapon;

/// <summary>
/// Guarded single-bit writes on live game memory -- the mutation half the pure
/// <see cref="Signatures"/> encoders hand their (byteOffset, mask) results to.
/// Both ops are fail-safe on an unwritable page (return false, touch nothing)
/// and never disturb neighboring bits.
/// </summary>
internal static class MemBits
{
    /// <summary>Guarded OR-set of a single bit, returning the post-write read-back (true == the
    /// bit reads SET afterwards). Never clears anything; fail-safe false on an unwritable page.
    /// <paramref name="wasSet"/> is the PRE-write state -- the live-test signal: true means the
    /// engine KEPT the bit since the last hold, false means it was cleared and we just re-armed
    /// it (the post-write read-back alone always says SET on a writable page).</summary>
    public static bool OrSet(long addr, byte mask, out bool wasSet)
    {
        wasSet = false;
        if (!Mem.Writable(addr, 1)) return false;
        int cur = Mem.U8(addr);
        wasSet = (cur & mask) != 0;
        if (!wasSet) Mem.W8(addr, (byte)(cur | mask));
        return (Mem.U8(addr) & mask) != 0;
    }

    /// <summary>Guarded AND-clear of a single bit, returning the read-back (true == the bit reads
    /// CLEAR afterwards). Touches nothing else in the byte; fail-safe false on an unwritable page.
    /// The unequip-release counterpart of <see cref="OrSet"/>.</summary>
    public static bool Clear(long addr, byte mask)
    {
        if (!Mem.Writable(addr, 1)) return false;
        int cur = Mem.U8(addr);
        if ((cur & mask) != 0) Mem.W8(addr, (byte)(cur & ~mask));
        return (Mem.U8(addr) & mask) == 0;
    }
}
