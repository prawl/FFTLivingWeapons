using System;

namespace LivingWeapon;

/// <summary>
/// Stops a new weapon from swinging as bare hands.
/// When the game prepares a swing it copies the weapon id word with a seven byte movzx at
/// <see cref="Offsets.FnSwingPrepIdCopy"/>, and for a new id that word is 0 at swing time, so the art lookup draws
/// empty hands [ledger empty-tile-hand-read-bounds]. Install replaces that site with a jump plus two pads into a
/// stub (<see cref="ThunkStub.EmitSwingIdFallbackStub"/>) that puts the right hand's id in when the word is 0 and
/// that hand holds one of ours. Each step comes back as a message on failure, and Restore keeps the stub page
/// because a game thread may still be inside it, as AccessorEntryHook does.
/// </summary>
internal sealed class SwingIdFallbackHook
{
    /// <summary>movzx eax, word [rip+0x52E700], the seven bytes the site carries.</summary>
    public static readonly byte[] ExpectedSite = { 0x0F, 0xB7, 0x05, 0x00, 0xE7, 0x52, 0x00 };

    public long SiteAddr { get; }
    public bool Installed { get; private set; }
    public long StubAddr { get; private set; }
    private byte[]? _original;

    public SwingIdFallbackHook(long siteAddr = 0)
    {
        SiteAddr = siteAddr == 0 ? Offsets.FnSwingPrepIdCopy : siteAddr;   // a rebased address cannot be a default parameter
    }

    /// <summary>Null on success, else why not. Covers the new ids from lo to lo plus count minus one. Safe to call again.</summary>
    public string? Install(ICodePatcher patcher, INearAllocator allocator, int lo, int count)
    {
        if (Installed) return null;
        if (count < 1) return "swing-id fallback: no extended ids";

        if (!patcher.TryRead(SiteAddr, ExpectedSite.Length, out var site))
            return $"swing-id fallback: 0x{SiteAddr:X} is unreadable";
        if (!ShouldArm(true, site))
            return $"swing-id fallback: 0x{SiteAddr:X} does not carry the expected movzx (reads {Convert.ToHexString(site)})";

        long returnAddr = SiteAddr + ExpectedSite.Length;
        byte[] stub;
        try { stub = ThunkStub.EmitSwingIdFallbackStub(Offsets.SwingIdWord, Offsets.BattleUnitsBase + Offsets.CWeapon, lo, lo + count - 1, returnAddr); }
        catch (Exception ex) { return $"swing-id fallback: stub emit failed ({ex.Message})"; }

        long page = allocator.Alloc(Math.Max(4096, stub.Length), SiteAddr);
        if (page == 0) return $"swing-id fallback: no executable page within reach of 0x{SiteAddr:X}";

        var jmp = ThunkStub.EncodeThunkJmp(SiteAddr, page);
        if (jmp == null) return $"swing-id fallback: page 0x{page:X} is out of rel32 reach of 0x{SiteAddr:X}";
        var siteBytes = new byte[ExpectedSite.Length];
        Array.Copy(jmp, siteBytes, jmp.Length);
        siteBytes[jmp.Length] = 0x90;
        siteBytes[jmp.Length + 1] = 0x90;

        if (!patcher.TryWrite(page, stub)) return $"swing-id fallback: stub write refused at 0x{page:X}";
        if (!patcher.TryWrite(SiteAddr, siteBytes)) return $"swing-id fallback: site write refused at 0x{SiteAddr:X}";

        _original = site;
        StubAddr = page;
        Installed = true;
        return null;
    }

    /// <summary>False only if the game refused the write back. Safe to call again.</summary>
    public bool Restore(ICodePatcher patcher)
    {
        if (!Installed) return true;
        bool ok = patcher.TryWrite(SiteAddr, _original!);
        if (ok) Installed = false;
        return ok;
    }

    /// <summary>The pure decision, so a test can feed it bytes.</summary>
    internal static bool ShouldArm(bool readOk, byte[]? site)
        => readOk && site != null && site.Length == ExpectedSite.Length && site.AsSpan().SequenceEqual(ExpectedSite);
}
