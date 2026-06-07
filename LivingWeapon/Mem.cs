using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace LivingWeapon;

/// <summary>
/// In-process memory access. We run inside FFT_enhanced.exe, so reading the
/// game's memory is a plain pointer dereference -- no ReadProcessMemory, no
/// cross-process syscall. Every fixed address in <see cref="Offsets"/> lives in
/// the always-mapped main module, so these reads never fault.
///
/// iter_regions/ReadBytes are only for the display scan (walking arbitrary
/// committed memory for the card's name strings); those go through VirtualQuery
/// so we never touch an unmapped page.
/// </summary>
internal static unsafe class Mem
{
    public static byte U8(long a) => *(byte*)a;
    public static ushort U16(long a) => *(ushort*)a;
    public static uint U32(long a) => *(uint*)a;

    public static void W8(long a, byte v) => *(byte*)a = v;

    public static void WriteBytes(long a, byte[] data)
    {
        fixed (byte* src = data)
            Buffer.MemoryCopy(src, (void*)a, data.Length, data.Length);
    }

    /// <summary>Read n bytes at a committed address into a managed array.</summary>
    public static byte[] ReadBytes(long a, int n)
    {
        var buf = new byte[n];
        Marshal.Copy((nint)a, buf, 0, n);
        return buf;
    }

    // little-endian parsers over a byte[] buffer (used by the region scan)
    public static ushort U16(byte[] b, int o) => (ushort)(b[o] | (b[o + 1] << 8));
    public static uint U32(byte[] b, int o) => (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));

    // ---- region walk for the display scan -------------------------------
    private const uint MEM_COMMIT = 0x1000;
    private const uint READABLE = 0x02 | 0x04 | 0x08 | 0x20 | 0x40 | 0x80;
    private const uint PAGE_GUARD = 0x100;
    private const uint PAGE_NOACCESS = 0x01;

    /// <summary>
    /// Yield (base, size) for every committed, readable, non-guard region.
    /// Caller bounds the work (this can cover gigabytes); never call it on the
    /// in-battle hot path.
    /// </summary>
    public static IEnumerable<(long baseAddr, long size)> Regions()
    {
        nint hProc = GetCurrentProcess();
        long addr = 0;
        var mbi = new MEMORY_BASIC_INFORMATION();
        int mbiSize = Marshal.SizeOf<MEMORY_BASIC_INFORMATION>();
        while (addr < 0x7FFF_FFFF_0000)
        {
            if (VirtualQueryEx(hProc, (nint)addr, out mbi, (uint)mbiSize) == 0)
                break;
            long b = (long)mbi.BaseAddress;
            long size = (long)mbi.RegionSize;
            long next = b + size;
            if (mbi.State == MEM_COMMIT && (mbi.Protect & READABLE) != 0
                && (mbi.Protect & (PAGE_GUARD | PAGE_NOACCESS)) == 0)
                yield return (b, size);
            addr = next > addr ? next : addr + 0x1000;
        }
    }

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern int VirtualQueryEx(nint hProcess, nint lpAddress, out MEMORY_BASIC_INFORMATION lpBuffer, uint dwLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORY_BASIC_INFORMATION
    {
        public nint BaseAddress;
        public nint AllocationBase;
        public uint AllocationProtect;
        public nuint RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }
}
