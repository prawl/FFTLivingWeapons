using System;

namespace LivingWeapon;

/// <summary>
/// Stateful half of the swing-id fallback (<see cref="ThunkStub.EmitSwingIdFallbackStub"/> emits the
/// bytes), AccessorEntryHook's twin for a mid-instruction jump. The swing-prep routine copies the swing-id
/// word with a 7-byte movzx at <see cref="Offsets.FnSwingPrepIdCopy"/>; for a moved id the word is 0 at swing
/// time and the art lookup draws bare hands (docs/LIVE_LEDGER.md [empty-tile-hand-read-bounds]). Install
/// rewrites the site as an E9 jmp plus two 0x90 pads into a stub that substitutes the hand id when the
/// word is 0 and the acting unit's right hand holds one of our ids. Steps return a refusal string; Restore
/// leaks the stub page because a game thread may be inside it (the rig's rule).
/// </summary>
internal sealed class SwingIdFallbackHook
{
    /// <summary>0F B7 05 00 E7 52 00 = movzx eax, word [rip+0x52E700] (read on disk 2026-08-31).</summary>
    public static readonly byte[] ExpectedSite = { 0x0F, 0xB7, 0x05, 0x00, 0xE7, 0x52, 0x00 };

    public long SiteAddr { get; }
    public bool Installed { get; private set; }
    public long StubAddr { get; private set; }
    private byte[]? _original;

    public SwingIdFallbackHook(long siteAddr = 0)
    {
        SiteAddr = siteAddr == 0 ? Offsets.FnSwingPrepIdCopy : siteAddr;   // 0 = the live pin (a rebased pin cannot be a default value)
    }

    /// <summary>Installs the redirect for extended ids [lo, lo + count, 1]. Idempotent; returns null or the refusal.</summary>
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

    /// <summary>Puts the seven original site bytes back. Idempotent; false only if the write was refused.</summary>
    public bool Restore(ICodePatcher patcher)
    {
        if (!Installed) return true;
        bool ok = patcher.TryWrite(SiteAddr, _original!);
        if (ok) Installed = false;
        return ok;
    }

    /// <summary>True if a readable 7-byte buffer matches <see cref="ExpectedSite"/>.</summary>
    internal static bool ShouldArm(bool readOk, byte[]? site)
        => readOk && site != null && site.Length == ExpectedSite.Length && site.AsSpan().SequenceEqual(ExpectedSite);
}
