using System;

namespace LivingWeapon;

/// <summary>
/// LW-346: the install/restore lifecycle for one redirected accessor thunk (the stateful half;
/// <see cref="ThunkStub"/> is the pure byte half). Sequence: read the thunk's five bytes and
/// refuse unless they are an <c>E9 rel32</c> jump (a stale address must become a logged refusal,
/// never a jump into the wrong function); decode the original target; emit the stub for that
/// target; allocate a page within rel32 reach of the thunk; write the stub; overwrite the thunk
/// with a jump to it. Restore puts the five original bytes back and deliberately leaks the stub
/// page (a game thread may be inside it; the rig's rule R3).
///
/// Every step returns a refusal string instead of throwing, so the boot arm can roll back the
/// whole extended-inventory set on the first refusal with one readable line (the rig's
/// CapBreakBootArm contract, ported).
/// </summary>
internal sealed class ThunkClone
{
    public long ThunkAddr { get; }
    public string Label { get; }
    public bool Installed { get; private set; }
    public long StubAddr { get; private set; }
    public long OriginalTarget { get; private set; }
    private byte[]? _original;

    public ThunkClone(long thunkAddr, string label)
    {
        ThunkAddr = thunkAddr;
        Label = label;
    }

    /// <summary>Installs the redirect. <paramref name="emitForTarget"/> receives the decoded
    /// original target and returns the stub bytes (one of the ThunkStub emitters, partially
    /// applied with its donors or rows). Idempotent: a second call while installed is a no-op
    /// success (installing twice would save the already-patched jump as the "original" and make
    /// Restore point the thunk at a dead page). Returns null on success, else the refusal.</summary>
    public string? Install(ICodePatcher patcher, INearAllocator allocator, Func<long, byte[]> emitForTarget)
    {
        if (Installed) return null;
        if (!patcher.TryRead(ThunkAddr, 5, out var entry))
            return $"{Label}: thunk 0x{ThunkAddr:X} is unreadable";
        if (!ThunkStub.IsJmpRel32(entry, ThunkAddr, out long target))
            return $"{Label}: thunk 0x{ThunkAddr:X} is not an E9 jump (reads {Convert.ToHexString(entry)})";

        byte[] stub;
        try { stub = emitForTarget(target); }
        catch (Exception ex) { return $"{Label}: stub emit failed ({ex.Message})"; }

        long page = allocator.Alloc(Math.Max(4096, stub.Length), ThunkAddr);
        if (page == 0) return $"{Label}: no executable page within reach of 0x{ThunkAddr:X}";
        var jmp = ThunkStub.EncodeThunkJmp(ThunkAddr, page);
        if (jmp == null) return $"{Label}: page 0x{page:X} is out of rel32 reach of 0x{ThunkAddr:X}";
        if (!patcher.TryWrite(page, stub)) return $"{Label}: stub write refused at 0x{page:X}";
        if (!patcher.TryWrite(ThunkAddr, jmp)) return $"{Label}: thunk write refused at 0x{ThunkAddr:X}";

        _original = entry;
        StubAddr = page;
        OriginalTarget = target;
        Installed = true;
        return null;
    }

    /// <summary>Puts the original five thunk bytes back. Idempotent; the stub page is never
    /// freed. Returns false only when the write itself was refused.</summary>
    public bool Restore(ICodePatcher patcher)
    {
        if (!Installed) return true;
        bool ok = patcher.TryWrite(ThunkAddr, _original!);
        if (ok) Installed = false;
        return ok;
    }
}
