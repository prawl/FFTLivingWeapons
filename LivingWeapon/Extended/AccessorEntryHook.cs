using System;

namespace LivingWeapon;

/// <summary>
/// Points one of the game's item lookups at a stub of ours, so a new item gets a sensible answer.
/// On the Denuvo free exe each of the ten lookups starts with real code, so the hook copies its first
/// instructions into a trampoline and jumps over them. Install checks those bytes, gets a page within rel32
/// reach, writes the trampoline at page + 0 and the stub (from <see cref="ThunkStub"/>) at page +
/// <see cref="StubOffset"/>, then puts a jump to the stub over the entry, padded with NOPs. Restore puts the
/// original bytes back and keeps the page, since a game thread may still be inside it.
/// The game's callers keep volatile registers alive across these calls, and a managed Reloaded.Hooks detour
/// clobbered r11 and crashed it [journal 2026-06-26], so the stubs are hand written and touch only rax and rcx,
/// like SwingIdFallbackHook. A failure comes back as a message so the boot arm can undo everything with one log line.
/// </summary>
internal sealed class AccessorEntryHook
{
    /// <summary>One page per hook, far more than the largest real stub (26 rows, 0x38 + 208 bytes).</summary>
    public const int PageSize = 4096;
    /// <summary>The trampoline, up to <see cref="ThunkStub.EntryTrampolineMaxSize"/> bytes, fits below this.</summary>
    public const int StubOffset = 0x40;

    public AccessorEntry Entry { get; }
    public string Label => Entry.Label;
    public long Addr => Entry.Addr;
    public bool Installed { get; private set; }
    public long StubAddr { get; private set; }
    public long TrampolineAddr { get; private set; }
    private byte[]? _original;

    public AccessorEntryHook(AccessorEntry entry) => Entry = entry;

    /// <summary>Null on success, else why not. <paramref name="emitForTarget"/> gets the trampoline's address and builds the stub.
    /// A second call does nothing, since the entry now holds our jump and a fresh install would refuse it and roll a working hook back.</summary>
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

    /// <summary>False if the game refused the write back. The page stays allocated on purpose.</summary>
    public bool Restore(ICodePatcher patcher)
    {
        if (!Installed) return true;
        bool ok = patcher.TryWrite(Addr, _original!);
        if (ok) Installed = false;
        return ok;
    }
}
