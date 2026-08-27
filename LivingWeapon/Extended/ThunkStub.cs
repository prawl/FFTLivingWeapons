using System;

namespace LivingWeapon;

/// <summary>
/// LW-346: the pure byte emitters for the accessor-thunk clones. The game reaches every per-item
/// accessor (weapon stats, validity, type probe, range index, sprite/palette pair, ...) through a
/// five-byte <c>E9 jmp rel32</c> thunk into copy-protected code; redirecting the thunk to a tiny
/// stub we own lets ids past the vanilla cap (261..511) answer as a chosen DONOR item, or, for the
/// weapon-stat accessor, answer with a row we author. The FFTHandsFree rig's constant-donor stub
/// (ThunkRedirect.EmitCloneStub, owner-observed on 1.5.2) is generalised here to a per-id table
/// so each new item names its own donors (docs/research/ITEM_CAP_261_BREAK_JOURNEY.md, the
/// 2026-08-27 03:20 blueprint, piece 3).
///
/// REGISTER CONTRACT (the June r11 lesson, journal 2026-06-26 session 3): the originals are tiny
/// leaves the compiler trusts to leave every register but rax/rcx alone, so the stubs touch
/// ONLY rax (the return register, clobbered by any call) and rcx (the id argument they replace);
/// rdx (the type probe's second argument) and r8..r11 pass through untouched. No stack, no
/// flags a caller could depend on across a call.
///
/// Layouts are fixed-offset so a unit test can pin them byte for byte (see ThunkStubTests).
/// </summary>
internal static class ThunkStub
{
    /// <summary>The accessors mask their id argument with 0x3FF (list words carry flag bits above
    /// bit 9); the stubs mask the same way before the range check.</summary>
    public const int IdMask = 0x3FF;

    public const int DonorStubHeader = 0x32;   // bytes before the donor table
    public const int RowStubHeader = 0x38;     // bytes before the row block
    public const int RowSize = 8;              // ITEM_WEAPON_DATA (fftivc.utility.modloader) is 8 bytes

    /// <summary>
    /// Donor stub: ids in [lo, lo + donors.Length) are rewritten to <c>donors[id - lo]</c> in ecx
    /// before jumping to <paramref name="originalTarget"/>; every other id passes through with
    /// rcx untouched.
    /// <code>
    /// 00 mov eax,ecx | 02 and eax,3FFh | 07 cmp eax,lo | 0C jb pass | 0E cmp eax,hi | 13 ja pass
    /// 15 sub eax,lo | 1A lea rcx,[rip+table] | 21 mov ecx,[rcx+rax*4]
    /// 24 pass: jmp [rip+0] | 2A target:8 | 32 table: 4*n
    /// </code>
    /// </summary>
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

    /// <summary>
    /// Row stub (the weapon-stat accessor): ids in [lo, lo + rows.Length) RETURN a pointer to
    /// their own 8-byte row inside the stub page (rows[id - lo]); every other id jumps to
    /// <paramref name="originalTarget"/> untouched. The row bytes are ITEM_WEAPON_DATA exactly as
    /// the game's own table holds them (Offsets.ItemStatsBase rows, 8 bytes, Power at +4).
    /// <code>
    /// 00 mov eax,ecx | 02 and eax,3FFh | 07 cmp eax,lo | 0C jb pass | 0E cmp eax,hi | 13 ja pass
    /// 15 sub eax,lo | 1A shl rax,3 | 1E lea rcx,[rip+rows] | 25 add rax,rcx | 28 ret
    /// 29 pass: jmp [rip+0] | 2F target:8 | 37 pad | 38 rows: 8*n
    /// </code>
    /// </summary>
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

    /// <summary>True iff <paramref name="entry5"/> is an <c>E9 rel32</c> jump; decodes its absolute
    /// destination (thunk + 5 + rel32).</summary>
    public static bool IsJmpRel32(byte[]? entry5, long thunkAddr, out long target)
    {
        target = 0;
        if (entry5 == null || entry5.Length < 5 || entry5[0] != 0xE9) return false;
        target = thunkAddr + 5 + BitConverter.ToInt32(entry5, 1);
        return true;
    }

    /// <summary>The five bytes that turn the thunk at <paramref name="thunkAddr"/> into a jump to
    /// <paramref name="stubAddr"/>; null when the stub is out of rel32 reach (the allocator's
    /// contract makes that a bug, but a null here is a refusal, never a wild jump).</summary>
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
        // s[at+2..at+5] = 0 (rip+0)
        var b = BitConverter.GetBytes(target);
        Array.Copy(b, 0, s, at + 6, 8);
    }

    private static void WriteI32(byte[] buf, int off, int v)
    {
        var b = BitConverter.GetBytes(v);
        Array.Copy(b, 0, buf, off, 4);
    }
}
