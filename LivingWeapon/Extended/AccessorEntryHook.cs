using System;

namespace LivingWeapon;

/// <summary>
/// LW-346 / LW-388 R12: the install/restore lifecycle for one hooked accessor entry (the
/// stateful half; <see cref="ThunkStub"/> is the pure byte half). On the Denuvo build each of
/// the ten per-item accessors was a five-byte <c>E9</c> thunk, so redirecting it meant decoding
/// the jump's destination. The Denuvo-free exe (read on disk 2026-10-16, buildid 24304444)
/// carries the real function bodies at those addresses, so there is no jump to decode: the hook
/// STEALS the first instructions instead. Sequence: verify the entry carries exactly the
/// expected bytes (a stale address or a foreign build becomes a logged refusal, never a jump
/// into the wrong function); allocate ONE page within rel32 reach; write a trampoline at
/// page + 0 (the stolen instructions re-executed, then a jump back past them) and the stub at
/// page + <see cref="StubOffset"/> (whose pass path targets the trampoline); only then overwrite
/// the entry with a five-byte jump to the stub, NOP-padded to the stolen length. Restore puts
/// the original bytes back and deliberately leaks the page (a game thread may be inside it; the
/// rig's rule R3).
///
/// REGISTER CONTRACT: the callers of these ten accessors were observed keeping volatile
/// registers live across the call (the June r11 crash, journal 2026-06-26), so a managed
/// Reloaded.Hooks detour is not safe here. The stubs touch only rax and rcx and the trampoline
/// runs the original instructions unchanged, so the register footprint is the original
/// function's. The twin is SwingIdFallbackHook, which plants its own jump the same way.
///
/// Every step returns a refusal string instead of throwing, so the boot arm can roll back the
/// whole extended-inventory set on the first refusal with one readable line (the rig's
/// CapBreakBootArm contract, ported).
/// </summary>
internal sealed class AccessorEntryHook
{
    /// <summary>One allocation per hook: the allocator's floor is a page anyway, and the largest
    /// real stub (26 rows) is 0x38 + 208 bytes.</summary>
    public const int PageSize = 4096;
    /// <summary>Where the stub sits in the page; the trampoline (at most
    /// <see cref="ThunkStub.EntryTrampolineMaxSize"/> bytes) lives below it at page + 0.</summary>
    public const int StubOffset = 0x40;

    public AccessorEntry Entry { get; }
    public string Label => Entry.Label;
    public long Addr => Entry.Addr;
    public bool Installed { get; private set; }
    public long StubAddr { get; private set; }
    public long TrampolineAddr { get; private set; }
    private byte[]? _original;

    public AccessorEntryHook(AccessorEntry entry) => Entry = entry;

    /// <summary>Installs the hook. <paramref name="emitForTarget"/> receives the trampoline
    /// address (the stub's pass target) and returns the stub bytes (one of the ThunkStub
    /// emitters, partially applied with its donors or rows); it is called exactly once.
    /// Idempotent: a second call while installed is a no-op success (installing twice would save
    /// the already-patched jump as the "original"). Returns null on success, else the refusal.</summary>
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

    /// <summary>Puts the original entry bytes back. Idempotent; the page is never freed. Returns
    /// false only when the write itself was refused.</summary>
    public bool Restore(ICodePatcher patcher)
    {
        if (!Installed) return true;
        bool ok = patcher.TryWrite(Addr, _original!);
        if (ok) Installed = false;
        return ok;
    }
}
