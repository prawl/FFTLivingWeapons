using System;

namespace LivingWeapon;

/// <summary>
/// The tiny machine code stubs that make the game answer for a new item as if it were an old one.
/// The game asks about an item (weapon stats, validity, type, range, sprite and palette) through ten
/// fixed entry points. On the Denuvo free build those are plain function entries, hooked by
/// AccessorEntryHook with a trampoline (ThunkStub.Trampoline.cs). A stub answers for ids 261 to 511
/// as a donor item or with a weapon row of our own. Stubs touch only rax and rcx, because the game's
/// callers keep volatile registers live across these calls and clobbering r11 crashed it once
/// [journal 2026-06-26]. Every offset is fixed so ThunkStubTests can pin the bytes.
/// </summary>
internal static partial class ThunkStub
{
    /// <summary>List words carry flag bits above bit 9, so the game masks ids with 0x3FF and the stubs must too.</summary>
    public const int IdMask = 0x3FF;

    public const int DonorStubHeader = 0x32;   // bytes before the donor table
    public const int RowStubHeader = 0x38;     // bytes before the row block
    public const int RowSize = 8;              // one ITEM_WEAPON_DATA row, as fftivc.utility.modloader lays it out

    /// <summary>A new item's id is swapped for its donor's in ecx before the game's own routine runs, so the
    /// game answers as if asked about the donor. Every other id goes through as it was.</summary>
    public static byte[] EmitDonorStub(int lo, int[] donors, long originalTarget)
    {
        if (donors == null || donors.Length == 0) throw new ArgumentException("at least one donor", nameof(donors));
        int hi = lo + donors.Length - 1;
        var s = new byte[DonorStubHeader + donors.Length * 4];
        EmitRangeCheck(s, lo, hi, jbRel8: 0x16, jaRel8: 0x0F);
        s[0x15] = 0x2D; WriteI32(s, 0x16, lo);                       // sub eax, lo
        s[0x1A] = 0x48; s[0x1B] = 0x8D; s[0x1C] = 0x0D; WriteI32(s, 0x1D, DonorStubHeader - 0x21);   // lea rcx,[rip+table]
        s[0x21] = 0x8B; s[0x22] = 0x0C; s[0x23] = 0x81;               // mov ecx,[rcx+rax*4]
        EmitAbsJmp(s, 0x24, originalTarget);                          // 0x24..0x31
        for (int i = 0; i < donors.Length; i++) WriteI32(s, DonorStubHeader + i * 4, donors[i]);
        return s;
    }

    /// <summary>A new weapon gets a pointer to its own stat row kept in the stub page, laid out like
    /// Offsets.ItemStatsBase with Power at +4. Every other id goes on to the game's routine.</summary>
    public static byte[] EmitRowStub(int lo, byte[][] rows, long originalTarget)
    {
        if (rows == null || rows.Length == 0) throw new ArgumentException("at least one row", nameof(rows));
        int hi = lo + rows.Length - 1;
        var s = new byte[RowStubHeader + rows.Length * RowSize];
        EmitRangeCheck(s, lo, hi, jbRel8: 0x1B, jaRel8: 0x14);
        s[0x15] = 0x2D; WriteI32(s, 0x16, lo);                                   // sub eax, lo
        s[0x1A] = 0x48; s[0x1B] = 0xC1; s[0x1C] = 0xE0; s[0x1D] = 0x03;          // shl rax, 3
        s[0x1E] = 0x48; s[0x1F] = 0x8D; s[0x20] = 0x0D; WriteI32(s, 0x21, RowStubHeader - 0x25);   // lea rcx,[rip+rows]
        s[0x25] = 0x48; s[0x26] = 0x01; s[0x27] = 0xC8;                          // add rax, rcx
        s[0x28] = 0xC3;                                                          // ret
        EmitAbsJmp(s, 0x29, originalTarget);                                     // 0x29..0x36; 0x37 stays 0 (pad)
        for (int i = 0; i < rows.Length; i++)
        {
            if (rows[i] == null || rows[i].Length != RowSize)
                throw new ArgumentException($"row {i} must be exactly {RowSize} bytes", nameof(rows));
            Array.Copy(rows[i], 0, s, RowStubHeader + i * RowSize, RowSize);
        }
        return s;
    }

    public const int SwingIdStubSize = 0x40;

    /// <summary>Lets a swing with a new weapon in hand find that weapon when the game's own copy of the id is empty.
    /// It sits over the 7 byte movzx at Offsets.FnSwingPrepIdCopy, and the layout below is pinned byte for byte.</summary>
    /// <remarks>Rax is that instruction's destination, rsi (acting slot &lt;&lt; 9) is only read, and the flags are
    /// free to clobber because the game's own code sets them again with a compare before it reads them.
    /// <code>
    /// 00 mov rax,standing(imm64) | 0A movzx eax,word [rax] | 0D test ax,ax | 10 jnz done(+0x20)
    /// 12 mov rax,handsBase(imm64) | 1C movzx eax,word [rax+rsi] | 20 cmp eax,lo | 25 jb zero(+0x09)
    /// 27 cmp eax,hi | 2C ja zero(+0x02) | 2E jmp done(+0x02) | 30 zero arm, xor eax,eax
    /// 32 done, jmp [rip+0] | 38 return address, 8 bytes   (total 0x40)
    /// </code>
    /// The hand word needs no mask, unlike the list words the accessors get. CWeapon (+0x20) is a plain
    /// u16 id with no flag bits, and a stray high bit would land outside [lo, hi] and take the zero arm.</remarks>
    public static byte[] EmitSwingIdFallbackStub(long standingWord, long handsBase, int lo, int hi, long returnAddr)
    {
        if (lo < 0) throw new ArgumentException("lo must be >= 0", nameof(lo));
        if (lo > hi) throw new ArgumentException("lo must be <= hi", nameof(hi));
        if (hi > IdMask) throw new ArgumentException($"hi must be <= {IdMask}", nameof(hi));

        var s = new byte[SwingIdStubSize];
        s[0x00] = 0x48; s[0x01] = 0xB8; Array.Copy(BitConverter.GetBytes(standingWord), 0, s, 0x02, 8);   // mov rax, standing
        s[0x0A] = 0x0F; s[0x0B] = 0xB7; s[0x0C] = 0x00;                       // movzx eax, word [rax]
        s[0x0D] = 0x66; s[0x0E] = 0x85; s[0x0F] = 0xC0;                       // test ax, ax
        s[0x10] = 0x75; s[0x11] = 0x20;                                       // jnz +0x20 -> done (0x32)
        s[0x12] = 0x48; s[0x13] = 0xB8; Array.Copy(BitConverter.GetBytes(handsBase), 0, s, 0x14, 8);   // mov rax, handsBase
        s[0x1C] = 0x0F; s[0x1D] = 0xB7; s[0x1E] = 0x04; s[0x1F] = 0x30;       // movzx eax, word [rax+rsi]
        s[0x20] = 0x3D; WriteI32(s, 0x21, lo);                                // cmp eax, lo
        s[0x25] = 0x72; s[0x26] = 0x09;                                       // jb zero (0x30)
        s[0x27] = 0x3D; WriteI32(s, 0x28, hi);                                // cmp eax, hi
        s[0x2C] = 0x77; s[0x2D] = 0x02;                                       // ja zero (0x30)
        s[0x2E] = 0xEB; s[0x2F] = 0x02;                                       // jmp done (0x32)
        s[0x30] = 0x31; s[0x31] = 0xC0;                                       // zero arm, xor eax, eax
        EmitAbsJmp(s, 0x32, returnAddr);                                      // done, jmp [rip+0] then the 8 byte return address
        return s;
    }

    /// <summary>Whether the five bytes are an <c>E9 rel32</c> jump, and where it goes.</summary>
    public static bool IsJmpRel32(byte[]? entry5, long thunkAddr, out long target)
    {
        target = 0;
        if (entry5 == null || entry5.Length < 5 || entry5[0] != 0xE9) return false;
        target = thunkAddr + 5 + BitConverter.ToInt32(entry5, 1);
        return true;
    }

    /// <summary>A five byte jump from <paramref name="thunkAddr"/> to <paramref name="stubAddr"/>,
    /// or null when the stub is too far away for a rel32.</summary>
    public static byte[]? EncodeThunkJmp(long thunkAddr, long stubAddr)
    {
        long delta = stubAddr - thunkAddr - 5;
        if (delta < int.MinValue || delta > int.MaxValue) return null;
        var jmp = new byte[5];
        jmp[0] = 0xE9;
        WriteI32(jmp, 1, (int)delta);
        return jmp;
    }

    private static void EmitRangeCheck(byte[] s, int lo, int hi, byte jbRel8, byte jaRel8)
    {
        s[0x00] = 0x89; s[0x01] = 0xC8;                 // mov eax, ecx
        s[0x02] = 0x25; WriteI32(s, 0x03, IdMask);      // and eax, 0x3FF
        s[0x07] = 0x3D; WriteI32(s, 0x08, lo);          // cmp eax, lo
        s[0x0C] = 0x72; s[0x0D] = jbRel8;               // jb pass
        s[0x0E] = 0x3D; WriteI32(s, 0x0F, hi);          // cmp eax, hi
        s[0x13] = 0x77; s[0x14] = jaRel8;               // ja pass
    }

    private static void EmitAbsJmp(byte[] s, int at, long target)
    {
        s[at] = 0xFF; s[at + 1] = 0x25;                 // jmp qword ptr [rip+0]
        // the four zero bytes after it make the rip offset 0, so the target is read from right behind
        var b = BitConverter.GetBytes(target);
        Array.Copy(b, 0, s, at + 6, 8);
    }

    private static void WriteI32(byte[] buf, int off, int v)
    {
        var b = BitConverter.GetBytes(v);
        Array.Copy(b, 0, buf, off, 4);
    }
}
