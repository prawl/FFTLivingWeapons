using System;
using System.Linq;

namespace LivingWeapon;

/// <summary>
/// Overwrites one already-resolved cache record. Split out of IconCacheHide.cs, which decides what to hide.
/// </summary>
internal sealed partial class IconCacheHide
{
    /// <summary>Re-verifies, writes via Writable+WriteBytes, then reads back and refuses on any mismatch.
    /// <paramref name="reason"/> says why a write was refused ("" on success).</summary>
    private bool TryHideRecord(long holderAddr, long stringAddr, int fieldOffset, int headSize,
        Func<byte[], bool> passes, out string reason)
    {
        if (!_mem.TryReadBytes(holderAddr, headSize, out var head) || BitConverter.ToInt64(head, 0) != stringAddr || !passes(head))
        {
            reason = "the record no longer classifies (changed since the sweep)";
            return false;
        }

        var nameBytes = IconCacheHidePolicy.HideNameBytes(stringAddr);
        long fieldAddr = holderAddr + fieldOffset;
        if (!_mem.Writable(holderAddr, nameBytes.Length) || !_mem.Writable(fieldAddr, 4))
        {
            reason = "the region is no longer writable";
            return false;
        }
        _mem.WriteBytes(holderAddr, nameBytes);
        _mem.WriteBytes(fieldAddr, IconCacheHidePolicy.ZeroU32);

        bool ok = _mem.TryReadBytes(holderAddr, nameBytes.Length, out var backName) && backName.SequenceEqual(nameBytes)
            && _mem.TryReadBytes(fieldAddr, 4, out var backField) && backField.SequenceEqual(IconCacheHidePolicy.ZeroU32);
        reason = ok ? "" : "the write did not read back as written";
        return ok;
    }

    private bool TryHideTexRecord(long holderAddr, long stringAddr, int expectedLength, uint expectedHash, out string reason) =>
        TryHideRecord(holderAddr, stringAddr, IconCacheHidePolicy.TexHashOffset, IconCacheHidePolicy.TexHeadSize, head =>
            BitConverter.ToInt64(head, IconCacheHidePolicy.TexSizeOffset) == expectedLength &&
            BitConverter.ToUInt32(head, IconCacheHidePolicy.TexHashOffset) == expectedHash, out reason);

    // Zeros the id at +8 only; +0xC is uncharacterized.
    private bool TryHidePartsEntry(long holderAddr, long stringAddr, uint expectedId, out string reason) =>
        TryHideRecord(holderAddr, stringAddr, IconCacheHidePolicy.PartsIdOffset, IconCacheHidePolicy.PartsHeadSize, head =>
            BitConverter.ToUInt32(head, IconCacheHidePolicy.PartsIdOffset) == expectedId &&
            BitConverter.ToUInt16(head, IconCacheHidePolicy.PartsStateOffset) == IconCacheHidePolicy.PartsExpectedState, out reason);
}
