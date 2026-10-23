using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Gives the new items an entry in the game's item catalog, by moving the catalog's extra page somewhere bigger.
/// The game keeps ids 256 to 260 (the "+" items) in a small block, and the accessor at 0x1402B8C44 reaches it with
/// <c>lea rax,[rax*4 + disp32]</c>. Changing that disp32 at <see cref="Offsets.ExtCatalogDisp32"/> points the accessor
/// at an 8 KB buffer of ours with our 12 byte records past 260, and the accessor itself stays the game's own code,
/// since a managed detour there clobbered r11 and crashed the game [journal 2026-06-26]. Ids are masked with 0x1FF,
/// so a stray id reads at most 511 * 12 = 0x17F4 into the buffer and finds a zero record.
/// </summary>
internal sealed class ExtendedCatalog
{
    public const int DlcLo = 256;
    public const int DlcHi = 260;
    public const int FirstExtendedId = 261;
    public const int LastExtendedId = 511;
    public const int RecordSize = ExtendedRecords.CatalogRecordSize;
    public const int BufferSize = 8192;

    // The stubs find an item's row and donors at id minus 261, so a gap in the ids would hand an item its neighbor's.
    public static int SlotOf(int id) => id - FirstExtendedId;

    public bool Installed { get; private set; }
    public long BufferAddr { get; private set; }
    private byte[]? _savedDisp32;

    /// <summary>Null on success, else why not. The disp32 must still point at the game's own block, or something
    /// else already moved it or a patch moved the code.</summary>
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
        // Both addresses are where the game actually loaded, so their difference is the offset in the file.
        var vanilla = EncodeDisp32(Offsets.ExtCatalogBase, ImageBase.Base)!;
        if (!BytesEqual(cur, vanilla))
            return $"catalog: disp32 reads {Convert.ToHexString(cur)}, expected vanilla {Convert.ToHexString(vanilla)} (already redirected or moved)";

        long buf = allocator.Alloc(BufferSize, ImageBase.Base);
        if (buf == 0) return "catalog: no page within reach of the image base";
        // The accessor adds the image base at run time, so the new disp32 is measured from it too.
        var disp = EncodeDisp32(buf, ImageBase.Base);
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

    /// <summary>Points the accessor back at the game's block. The buffer stays allocated because a game thread may be reading it.</summary>
    public bool Restore(ICodePatcher patcher)
    {
        if (!Installed) return true;
        bool ok = patcher.TryWrite(Offsets.ExtCatalogDisp32, _savedDisp32!);
        if (ok) Installed = false;
        return ok;
    }

    /// <summary>The disp32 that reaches <paramref name="buf"/> from <paramref name="moduleBase"/>, or null when too far.</summary>
    public static byte[]? EncodeDisp32(long buf, long moduleBase)
    {
        long disp = buf - moduleBase;
        if (disp < int.MinValue || disp > int.MaxValue) return null;
        return BitConverter.GetBytes((int)disp);
    }

    /// <summary>The game's five "+" items have to come along, or they would read as empty.</summary>
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
