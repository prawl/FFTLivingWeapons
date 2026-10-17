using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Lets a town shop stock an extended-inventory item. The shop buy-list builder reads town flags from the 256-row ITEM_SHOPS_DATA table
/// (<see cref="Offsets.ShopFlagsTable"/>), which has no rows past 255 (the builder's loop cap is widened by ExtendedSites). This class gives the builder a mirror page
/// holding a live copy of the 512 vanilla bytes plus our flags for ids 261+, and re-points the builder's two references to the table at it
/// (a rip-relative lea for the high byte, an image-relative disp32 for the low byte).
/// The original table stays in place. The modloader and other mods keep writing it (at OnAllModsLoaded, after this arm) and the new-stock badge scan reads it.
/// <see cref="Sync"/> recopies it into the mirror when it changes, so partner shop edits still land.
/// </summary>
internal sealed class ShopFlagsMirror
{
    public const int VanillaBytes = 512;
    public const int PageSize = 4096;   // rows up to id 511 need 1024 bytes; a page is the allocator's floor
    private const int VanillaHighByteRel32 = 0x003F6989;   // lea r12,[rip+0x3F6989] -> 0x14067F891
    private const int VanillaLowByteDisp32 = 0x0067F890;   // [rcx + rbp + 0x67F890], rbp = image base

    public bool Installed { get; private set; }
    public long MirrorAddr { get; private set; }
    private byte[]? _savedRel32, _savedDisp32;
    private byte[]? _lastSynced;

    /// <summary>Null on success, else the refusal (nothing changed). Both builder sites must still
    /// carry their vanilla displacement (a different value means another patch got there first).</summary>
    public string? Install(ICodePatcher patcher, INearAllocator allocator, IReadOnlyList<(int Id, ushort Flags)> rows)
    {
        if (Installed) return null;
        foreach (var (id, _) in rows)
            if (id < ExtendedCatalog.FirstExtendedId || id > ExtendedCatalog.LastExtendedId)
                return $"shops: id {id} is outside {ExtendedCatalog.FirstExtendedId}..{ExtendedCatalog.LastExtendedId}";
        if (!patcher.TryRead(Offsets.ShopBuilderHighByteLeaRel32, 4, out var rel) || !patcher.TryRead(Offsets.ShopBuilderLowByteDisp32, 4, out var disp))
            return "shops: a builder site is unreadable";
        if (BitConverter.ToInt32(rel, 0) != VanillaHighByteRel32)
            return $"shops: high-byte lea reads {Convert.ToHexString(rel)}, expected vanilla {VanillaHighByteRel32:X8} (already redirected or moved)";
        if (BitConverter.ToInt32(disp, 0) != VanillaLowByteDisp32)
            return $"shops: low-byte disp32 reads {Convert.ToHexString(disp)}, expected vanilla {VanillaLowByteDisp32:X8} (already redirected or moved)";

        long buf = allocator.Alloc(PageSize, ImageBase.Base);
        if (buf == 0) return "shops: no page within reach of the image base";
        long newRel = (buf + 1) - Offsets.ShopBuilderHighByteLeaNextIp;
        // Rbp holds the live image base at the low-byte read, so the disp32 is an RVA.
        long newDisp = buf - ImageBase.Base;
        if (newRel < int.MinValue || newRel > int.MaxValue || newDisp < int.MinValue || newDisp > int.MaxValue)
            return $"shops: mirror 0x{buf:X} is out of rel32/disp32 reach";
        if (!patcher.TryWrite(buf, new byte[PageSize])) return $"shops: zero-fill refused at 0x{buf:X}";
        foreach (var (id, flags) in rows)
            if (!patcher.TryWrite(buf + (long)id * 2, BitConverter.GetBytes(flags))) return $"shops: row write refused for id {id}";

        _savedRel32 = rel;
        _savedDisp32 = disp;
        if (!patcher.TryWrite(Offsets.ShopBuilderLowByteDisp32, BitConverter.GetBytes((int)newDisp))) return "shops: low-byte disp32 write refused";
        if (!patcher.TryWrite(Offsets.ShopBuilderHighByteLeaRel32, BitConverter.GetBytes((int)newRel)))
        {
            patcher.TryWrite(Offsets.ShopBuilderLowByteDisp32, disp);   // never leave the two halves split
            return "shops: high-byte lea write refused";
        }
        MirrorAddr = buf;
        Installed = true;
        return null;
    }

    /// <summary>Copy the vanilla 512 bytes into the mirror when they differ from the last copy; true when a copy was written.</summary>
    public bool Sync(ICodePatcher patcher)
    {
        if (!Installed) return false;
        if (!patcher.TryRead(Offsets.ShopFlagsTable, VanillaBytes, out var now)) return false;
        if (_lastSynced != null && Same(_lastSynced, now)) return false;
        if (!patcher.TryWrite(MirrorAddr, now)) return false;
        _lastSynced = now;
        return true;
    }

    /// <summary>Points both builder sites back at the vanilla table. The mirror page is never freed.</summary>
    public bool Restore(ICodePatcher patcher)
    {
        if (!Installed) return true;
        bool ok = patcher.TryWrite(Offsets.ShopBuilderHighByteLeaRel32, _savedRel32!)
                & patcher.TryWrite(Offsets.ShopBuilderLowByteDisp32, _savedDisp32!);
        if (ok) Installed = false;
        return ok;
    }

    private static bool Same(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }
}
