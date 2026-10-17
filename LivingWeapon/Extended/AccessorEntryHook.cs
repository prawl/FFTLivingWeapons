using System;

namespace LivingWeapon;

/// <summary>
/// LW-346 / LW-388 R12. Install/restore for one hooked accessor entry (<see cref="ThunkStub"/>
/// holds the byte emitters). The Denuvo build had a five-byte <c>E9</c> thunk at each of the ten
/// accessors; the Denuvo-free exe (read on disk 2026-10-16, buildid 24304444) has the real
/// bodies there, so the hook steals the first instructions instead of decoding a jump. Install
/// checks the expected bytes, allocates a page within rel32 reach, writes the trampoline at
/// page + 0 and the stub at page + <see cref="StubOffset"/>, then overwrites the entry with a jump
/// to the stub, NOP-padded. Restore writes the original bytes back and leaks the page, since a
/// game thread may be inside it (rig rule R3).
/// Callers keep volatile registers live across these calls (June r11 crash, journal 2026-06-26),
/// so a managed Reloaded.Hooks detour is unsafe. The stubs touch only rax and rcx. The twin is
/// SwingIdFallbackHook. Failures return a refusal string so the boot arm can roll back the
/// extended-inventory set with one log line (CapBreakBootArm in the rig).
/// </summary>
internal sealed class AccessorEntryHook
{
    /// <summary>One page per hook; the largest real stub (26 rows) is 0x38 + 208 bytes.</summary>
    public const int PageSize = 4096;
    /// <summary>Stub offset in the page; the trampoline (up to
    /// <see cref="ThunkStub.EntryTrampolineMaxSize"/> bytes) sits below it.</summary>
    public const int StubOffset = 0x40;

    public AccessorEntry Entry { get; }
    public string Label => Entry.Label;
    public long Addr => Entry.Addr;
    public bool Installed { get; private set; }
    public long StubAddr { get; private set; }
    public long TrampolineAddr { get; private set; }
    private byte[]? _original;

    public AccessorEntryHook(AccessorEntry entry) => Entry = entry;

    /// <summary>Installs the hook. <paramref name="emitForTarget"/> takes the trampoline address
    /// and returns the stub bytes; it is called once. A second call while installed is a no-op, since
    /// it would save the patched jump as the original. Returns null on success, else the refusal.</summary>
    public string? Install(ICodePatcher patcher, INearAllocator allocator, Func<long, byte[]> emitForTarget)
    {
        if (Installed) return null;
        int len = Entry.Expected.Length;
        if (!patcher.TryRead(Addr, len, out var entry))
            return $"{Label}: entry 0x{Addr:X} is unreadable";
        if (!entry.AsSpan().SequenceEqual(Entry.Expected))
            return $"{Label}: entry 0x{Addr:X} does not carry the expected bytes (reads {Convert.ToHexString(entry)}, expected {Convert.ToHexString(Entry.Expected)})";

        long page = allocator.Alloc(PageSize, Addr);
        if (page == 0) return $"{Label}: no executable page within reach of 0x{Addr:X}";

        byte[] trampoline;
        try { trampoline = ThunkStub.EmitEntryTrampoline(Addr, Entry.Expected, Entry.CallAt); }
        catch (ArgumentException ex) { return $"{Label}: trampoline emit failed ({ex.Message})"; }
        if (trampoline.Length > StubOffset)
            return $"{Label}: trampoline of {trampoline.Length} bytes does not fit below the stub";

        byte[] stub;
        try { stub = emitForTarget(page); }
        catch (Exception ex) { return $"{Label}: stub emit failed ({ex.Message})"; }
        if (StubOffset + stub.Length > PageSize)
            return $"{Label}: stub of {stub.Length} bytes does not fit in the page";

        var jmp = ThunkStub.EncodeThunkJmp(Addr, page + StubOffset);
        if (jmp == null) return $"{Label}: page 0x{page:X} is out of rel32 reach of 0x{Addr:X}";
        var entryBytes = new byte[len];
        Array.Fill(entryBytes, (byte)0x90);
        Array.Copy(jmp, entryBytes, jmp.Length);

        if (!patcher.TryWrite(page, trampoline)) return $"{Label}: trampoline write refused at 0x{page:X}";
        if (!patcher.TryWrite(page + StubOffset, stub)) return $"{Label}: stub write refused at 0x{page + StubOffset:X}";
        if (!patcher.TryWrite(Addr, entryBytes)) return $"{Label}: entry write refused at 0x{Addr:X}";

        _original = entry;
        TrampolineAddr = page;
        StubAddr = page + StubOffset;
        Installed = true;
        return null;
    }

    /// <summary>Restores the original entry bytes; the page is not freed. False if the write was refused.</summary>
    public bool Restore(ICodePatcher patcher)
    {
        if (!Installed) return true;
        bool ok = patcher.TryWrite(Addr, _original!);
        if (ok) Installed = false;
        return ok;
    }
}
