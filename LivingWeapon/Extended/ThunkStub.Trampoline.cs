using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// LW-388 R12: the entry trampoline half of <see cref="ThunkStub"/>. Split from the id-rewrite
/// stubs because it is a different job: those stubs invent new answers for ids past the vanilla
/// cap, while this emitter RE-EXECUTES game code. When an accessor entry is overwritten with our
/// five-byte jump, the instructions it held (the "stolen" bytes) must still run for every id the
/// stub passes through; the trampoline is that copy, followed by a jump back into the original
/// function just past the stolen span. The twin of what SwingIdFallbackHook does for its site.
/// </summary>
internal static partial class ThunkStub
{
    /// <summary>Upper bound on an entry trampoline. The largest real shape is 34 bytes (4 stolen
    /// + 6 call + 2 skip + 8 address + 14 return jump); 0x40 leaves the stub a fixed home.</summary>
    public const int EntryTrampolineMaxSize = 0x40;

    /// <summary>
    /// Builds the trampoline for an accessor entry at <paramref name="entryAddr"/> (LIVE address)
    /// whose first bytes <paramref name="stolen"/> (at least five, the size of our jump) are
    /// replaced by the hook. <paramref name="callAt"/> is the offset of one <c>E8 rel32</c> call
    /// inside the stolen bytes, or -1 when there is none. A rel32 is only valid at its own
    /// address, so a stolen call is rewritten as an absolute indirect call:
    /// <code>
    /// stolen[0..callAt) | FF 15 02 00 00 00 (call [rip+2]) | EB 08 (skip the address) | target:8
    /// | stolen[callAt+5..) | FF 25 00 00 00 00 (jmp [rip+0]) | entryAddr + stolen.Length:8
    /// </code>
    /// The callee returns to the <c>EB 08</c>, which hops over the address bytes. Stack depth at
    /// the call equals the original's because everything before it ran verbatim. The target is
    /// decoded from the stolen rel32, which is relocation-invariant, so it comes out live.
    /// </summary>
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
