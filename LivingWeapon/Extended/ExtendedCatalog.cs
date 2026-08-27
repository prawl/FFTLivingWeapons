using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// LW-346: relocates the game's EXTENDED item catalog (ids 256..260, the "+" items) into a buffer
/// this mod owns and fills the slots past 260 with our own 12-byte records. The catalog accessor
/// at 0x1402B8C44 reaches ids &gt;= 256 through <c>lea rax,[rax*4 + disp32]</c> with the disp32
/// at <see cref="Offsets.ExtCatalogDisp32"/>; rewriting those four bytes to point at our buffer
/// leaves the accessor 100 percent native (the register-safe design the FFTHandsFree rig settled
/// on after a managed detour clobbered r11: docs/research/ITEM_CAP_261_BREAK_JOURNEY.md,
/// 2026-06-26 session 3; re-anchored for 1.5.2 on 2026-08-26, owner-observed the same night).
///
/// The buffer is 8 KB: the accessor masks ids with 0x1FF, so a stray id up to 511 indexes at most
/// 511 * 12 = 0x17F4, inside the page, and reads a zero record instead of faulting.
/// </summary>
internal sealed class ExtendedCatalog
{
    public const int DlcLo = 256;
    public const int DlcHi = 260;
    public const int FirstExtendedId = 261;
    public const int LastExtendedId = 511;
    public const int RecordSize = ExtendedRecords.CatalogRecordSize;
    public const int BufferSize = 8192;

    public bool Installed { get; private set; }
    public long BufferAddr { get; private set; }
    private byte[]? _savedDisp32;

    /// <summary>Null on success, else the refusal (nothing changed). The disp32 must still read
    /// its vanilla value (buffer base minus image base): a different value means the accessor
    /// was already redirected (the research marker still armed) or the site moved.</summary>
    public string? Install(ICodePatcher patcher, INearAllocator allocator, IReadOnlyList<(int Id, byte[] Record)> records)
    {
        if (Installed) return null;
        foreach (var (id, rec) in records)
        {
            if (id < FirstExtendedId || id > LastExtendedId) return $"catalog: id {id} is outside {FirstExtendedId}..{LastExtendedId}";
            if (rec == null || rec.Length != RecordSize) return $"catalog: id {id} record must be {RecordSize} bytes";
        }
        if (!patcher.TryRead(Offsets.ExtCatalogDisp32, 4, out var cur))
            return $"catalog: disp32 site 0x{Offsets.ExtCatalogDisp32:X} is unreadable";
        var vanilla = EncodeDisp32(Offsets.ExtCatalogBase, Offsets.ModuleBase)!;
        if (!BytesEqual(cur, vanilla))
            return $"catalog: disp32 reads {Convert.ToHexString(cur)}, expected vanilla {Convert.ToHexString(vanilla)} (already redirected or moved)";

        long buf = allocator.Alloc(BufferSize, Offsets.ModuleBase);
        if (buf == 0) return "catalog: no page within reach of the image base";
        var disp = EncodeDisp32(buf, Offsets.ModuleBase);
        if (disp == null) return $"catalog: buffer 0x{buf:X} is out of disp32 reach";
        if (!patcher.TryWrite(buf, new byte[BufferSize])) return $"catalog: zero-fill refused at 0x{buf:X}";

        foreach (var (src, dstOff) in CopyPlan())
        {
            if (!patcher.TryRead(src, RecordSize, out var rec)) return $"catalog: vanilla record at 0x{src:X} is unreadable";
            if (!patcher.TryWrite(buf + dstOff, rec)) return $"catalog: record copy refused at 0x{buf + dstOff:X}";
        }
        foreach (var (id, rec) in records)
            if (!patcher.TryWrite(buf + (long)id * RecordSize, rec)) return $"catalog: record write refused for id {id}";

        _savedDisp32 = cur;
        if (!patcher.TryWrite(Offsets.ExtCatalogDisp32, disp)) return "catalog: disp32 write refused";
        BufferAddr = buf;
        Installed = true;
        return null;
    }

    /// <summary>Points the accessor back at the vanilla block. The buffer is never freed (a game
    /// thread may be mid-read). Idempotent.</summary>
    public bool Restore(ICodePatcher patcher)
    {
        if (!Installed) return true;
        bool ok = patcher.TryWrite(Offsets.ExtCatalogDisp32, _savedDisp32!);
        if (ok) Installed = false;
        return ok;
    }

    /// <summary>The signed 32-bit displacement (little-endian) the lea needs to reach
    /// <paramref name="buf"/> from <paramref name="moduleBase"/>; null when out of range. A wrong
    /// value here sends the accessor into garbage, which is why it is pinned by a unit test.</summary>
    public static byte[]? EncodeDisp32(long buf, long moduleBase)
    {
        long disp = buf - moduleBase;
        if (disp < int.MinValue || disp > int.MaxValue) return null;
        return BitConverter.GetBytes((int)disp);
    }

    /// <summary>(source address, destination offset) for the five vanilla "+" records that must
    /// keep resolving after the relocation.</summary>
    public static IEnumerable<(long Src, long DstOff)> CopyPlan()
    {
        for (int id = DlcLo; id <= DlcHi; id++)
            yield return (Offsets.ExtCatalogBase + (long)id * RecordSize, (long)id * RecordSize);
    }

    private static bool BytesEqual(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }
}
