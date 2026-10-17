using System;
using System.Runtime.InteropServices;

namespace LivingWeapon;

/// <summary>
/// Byte-patch seam for code pages (the extended-inventory boot arm and its thunk stubs).
/// Separate from <see cref="Mem"/> because Mem's writes wait for <see cref="Mem.WritesEnabled"/>, which LaunchGuard sets only after
/// a save loads, but these patches must land before the game runs (menu registry and bag array are built once at boot,
/// docs/research/ITEM_CAP_261_BREAK_JOURNEY.md). Each site verifies its expected old byte before writing and rolls back on any
/// mismatch; a moved site is one log line, not a write. (Until LW-397 the caller also required a matching PE build key.)
/// No raw pointer derefs. Reads use ReadProcessMemory; writes lift the page to PAGE_EXECUTE_READWRITE with VirtualProtect,
/// call WriteProcessMemory, then restore protection. Failures return false, since an AV is uncatchable in .NET.
/// Tests inject a dictionary-backed fake; the pinned-buffer suite drives the live adapter (as for LiveMemory).
/// </summary>
internal interface ICodePatcher
{
    /// <summary>Read <paramref name="count"/> bytes at <paramref name="address"/>; false and an empty buffer on failure.</summary>
    bool TryRead(long address, int count, out byte[] bytes);

    /// <summary>Write <paramref name="bytes"/> at <paramref name="address"/>; false on failure.</summary>
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
