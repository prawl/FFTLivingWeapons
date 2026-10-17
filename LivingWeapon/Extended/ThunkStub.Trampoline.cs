using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>LW-388 R12. The entry-trampoline half of <see cref="ThunkStub"/>. The instructions an accessor-entry jump overwrites still have to run,
/// so this emits a copy of them plus a jump back past the stolen span, like SwingIdFallbackHook does for its site.</summary>
internal static partial class ThunkStub
{
    /// <summary>Upper bound on an entry trampoline; the largest real shape is 34 bytes (4 stolen + 6 call + 2 skip + 8 address + 14 return jump).</summary>
    public const int EntryTrampolineMaxSize = 0x40;

    /// <summary>Builds the trampoline for the accessor entry at <paramref name="entryAddr"/> (live address). <paramref name="stolen"/> (at least five bytes)
    /// is what the hook overwrites and <paramref name="callAt"/> the offset of an <c>E8 rel32</c> call in it, or -1. A rel32 only works at its own address, so the call becomes an absolute indirect call:
    /// <code>
    /// stolen[0..callAt) | FF 15 02 00 00 00 (call [rip+2]) | EB 08 (skip the address) | target:8
    /// | stolen[callAt+5..) | FF 25 00 00 00 00 (jmp [rip+0]) | entryAddr + stolen.Length:8
    /// </code>
    /// The callee returns to the <c>EB 08</c>, which hops over the address.</summary>
    public static byte[] EmitEntryTrampoline(long entryAddr, byte[] stolen, int callAt)
    {
        if (stolen == null || stolen.Length < 5)
            throw new ArgumentException("the stolen span must be at least 5 bytes", nameof(stolen));
        if (callAt < -1)
            throw new ArgumentException("callAt must be -1 (no call) or an offset", nameof(callAt));
        if (callAt >= 0 && (callAt + 5 > stolen.Length || stolen[callAt] != 0xE8))
            throw new ArgumentException($"no E8 rel32 call at offset {callAt} of the stolen bytes", nameof(callAt));

        var t = new List<byte>();
        if (callAt < 0)
        {
            t.AddRange(stolen);
        }
        else
        {
            for (int i = 0; i < callAt; i++) t.Add(stolen[i]);
            long target = entryAddr + callAt + 5 + BitConverter.ToInt32(stolen, callAt + 1);
            t.AddRange(new byte[] { 0xFF, 0x15, 0x02, 0x00, 0x00, 0x00 });   // call [rip+2]
            t.AddRange(new byte[] { 0xEB, 0x08 });                           // jmp +8 over the address
            t.AddRange(BitConverter.GetBytes(target));
            for (int i = callAt + 5; i < stolen.Length; i++) t.Add(stolen[i]);
        }
        t.AddRange(new byte[] { 0xFF, 0x25, 0x00, 0x00, 0x00, 0x00 });       // jmp [rip+0]
        t.AddRange(BitConverter.GetBytes(entryAddr + stolen.Length));
        if (t.Count > EntryTrampolineMaxSize)
            throw new ArgumentException($"trampoline of {t.Count} bytes exceeds {EntryTrampolineMaxSize}", nameof(stolen));
        return t.ToArray();
    }
}
