namespace LivingWeapon;

/// <summary>
/// Guarded single-bit writes on live game memory -- the mutation half the pure
/// <see cref="Signatures"/> encoders hand their (byteOffset, mask) results to.
/// Both ops are fail-safe on an unwritable page (return false, touch nothing)
/// and never disturb neighboring bits. LW-145 fix 1: a FAILED pre-read is now its own outcome
/// (false, no write) -- never silently treated as a real zero byte, which used to let a bad
/// read write the bare mask and zero the other 7 bits (OrSet), or claim a bit read clear that
/// was never actually verified (Clear). See OrSet/Clear below for the shared reasoning.
/// </summary>
internal static class MemBits
{
    // The one production IGameMemory both public overloads delegate through -- LiveMemory wraps
    // Mem 1:1, so the SUCCESS path is byte-identical to the pre-fix direct Mem.* calls, while the
    // real logic below is testable directly against a fake over the IGameMemory seam.
    private static readonly LiveMemory _live = new();

    /// <summary>Guarded OR-set of a single bit, returning the post-write read-back (true == the
    /// bit reads SET afterwards). Never clears anything; fail-safe false on an unwritable page.
    /// <paramref name="wasSet"/> is the PRE-write state -- the live-test signal: true means the
    /// engine KEPT the bit since the last hold, false means it was cleared and we just re-armed
    /// it (the post-write read-back alone always says SET on a writable page).</summary>
    public static bool OrSet(long addr, byte mask, out bool wasSet) => OrSet(_live, addr, mask, out wasSet);

    /// <summary>Guarded AND-clear of a single bit, returning the read-back (true == the bit reads
    /// CLEAR afterwards). Touches nothing else in the byte; fail-safe false on an unwritable page.
    /// The unequip-release counterpart of <see cref="OrSet(long, byte, out bool)"/>.</summary>
    public static bool Clear(long addr, byte mask) => Clear(_live, addr, mask);

    /// <summary>The real OrSet logic, over the IGameMemory seam so a fake can prove the failed-
    /// pre-read case: a Writable page whose TryReadBytes still fails now returns false with NO
    /// write. The old `cur = Mem.U8(addr)` pre-read fails safe to 0 on an unreadable page, which
    /// meant a failed read would have written the BARE MASK -- zeroing the other 7 bits, exactly
    /// the neighbor-bit disturbance the class doc forbids.</summary>
    internal static bool OrSet(IGameMemory mem, long addr, byte mask, out bool wasSet)
    {
        wasSet = false;
        if (!mem.Writable(addr, 1)) return false;
        if (!mem.TryReadBytes(addr, 1, out var pre)) return false;   // failed read: bail, no write
        byte cur = pre[0];
        wasSet = (cur & mask) != 0;
        if (!wasSet) mem.W8(addr, (byte)(cur | mask));
        return (mem.U8(addr) & mask) != 0;
    }

    /// <summary>The real Clear logic, over the IGameMemory seam. Fixes the dual defect: the old
    /// failed-read-as-0 both skipped the write (0 &amp; mask is always 0, "already clear") AND
    /// reported success (the post-read failed the same way, also reading 0) -- an unverified
    /// answer dressed as a confirmed one. A failed pre-read now returns false outright.</summary>
    internal static bool Clear(IGameMemory mem, long addr, byte mask)
    {
        if (!mem.Writable(addr, 1)) return false;
        if (!mem.TryReadBytes(addr, 1, out var pre)) return false;   // failed read: bail, no false "clear"
        byte cur = pre[0];
        if ((cur & mask) != 0) mem.W8(addr, (byte)(cur & ~mask));
        return (mem.U8(addr) & mask) == 0;
    }
}
