using System;
using System.Runtime.InteropServices;

namespace LivingWeapon;

/// <summary>
/// LW-346: the byte-patch seam for CODE pages (the extended-items boot arm and its thunk stubs).
///
/// WHY A SECOND SEAM NEXT TO <see cref="Mem"/>: Mem's writes are gated on
/// <see cref="Mem.WritesEnabled"/>, which LaunchGuard flips true only once a save has loaded and
/// Ramza's roster row verifies. The extended-items patches MUST land before the game runs a single
/// instruction (the rig proved the menu registry and the bag array are built once at boot,
/// docs/research/ITEM_CAP_261_BREAK_JOURNEY.md), so they cannot wait for that edge. They are
/// gated differently instead, and only through this seam: the caller (ExtendedItems.BootArm)
/// verifies the PE build-key landmark FIRST, and every patch site verifies its expected OLD byte
/// before it is written, with a full rollback when any site disagrees. A patched game changes the
/// PE key; a moved site changes the old byte; either refusal is one log line, never a write.
///
/// SAFETY: same doctrine as Mem -- never a raw pointer deref. Reads are ReadProcessMemory on our
/// own handle; writes lift the page to PAGE_EXECUTE_READWRITE with VirtualProtect, write through
/// WriteProcessMemory, and restore the previous protection. Every call returns false instead of
/// throwing: an unmapped page is a refusal, not an access violation (an AV is an uncatchable
/// corrupted-state exception in .NET and would crash the whole game).
///
/// Tests inject a dictionary-backed fake; the pinned-buffer suite drives the live adapter
/// through the real VirtualProtect/WPM path (the LiveMemory precedent).
/// </summary>
internal interface ICodePatcher
{
    /// <summary>Read <paramref name="count"/> bytes at <paramref name="address"/>; false (and an
    /// empty buffer) on any failure, never an exception.</summary>
    bool TryRead(long address, int count, out byte[] bytes);

    /// <summary>Write <paramref name="bytes"/> at <paramref name="address"/>; false on any
    /// failure (protection change refused, partial write), never an exception.</summary>
    bool TryWrite(long address, byte[] bytes);
}

/// <summary>Production <see cref="ICodePatcher"/>: VirtualProtect + ReadProcessMemory /
/// WriteProcessMemory on the current process, protection restored after every write.</summary>
internal sealed class LiveCodePatcher : ICodePatcher
{
    private const uint PAGE_EXECUTE_READWRITE = 0x40;
    private static readonly nint Self = GetCurrentProcess();

    public bool TryRead(long address, int count, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (count <= 0 || address <= 0) return false;
        var buf = new byte[count];
        try
        {
            if (!ReadProcessMemory(Self, (nint)address, buf, (nuint)count, out var got) || (int)got != count)
                return false;
        }
        catch (Exception) { return false; }
        bytes = buf;
        return true;
    }

    public bool TryWrite(long address, byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0 || address <= 0) return false;
        try
        {
            if (!VirtualProtect((nint)address, (nuint)bytes.Length, PAGE_EXECUTE_READWRITE, out uint old))
                return false;
            try
            {
                return WriteProcessMemory(Self, (nint)address, bytes, (nuint)bytes.Length, out var written)
                       && (int)written == bytes.Length;
            }
            finally
            {
                VirtualProtect((nint)address, (nuint)bytes.Length, old, out _);
            }
        }
        catch (Exception) { return false; }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualProtect(nint lpAddress, nuint dwSize, uint flNewProtect, out uint lpflOldProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(nint h, nint addr, [Out] byte[] buf, nuint size, out nuint read);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(nint h, nint addr, byte[] buf, nuint size, out nuint written);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();
}
