using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace LivingWeapon;

/// <summary>
/// In-process memory access -- but every read and write goes through
/// Read/WriteProcessMemory on our OWN process handle, NOT a raw pointer deref.
///
/// This is the single most important safety property of the runtime. RPM/WPM
/// validate the range in the kernel and return false on an unmapped/freed page;
/// a raw pointer deref AVs instead, and an access violation is an UNCATCHABLE
/// corrupted-state exception in .NET -> it crashes the whole game. The external
/// Python companion never crashed the game for exactly this reason (it used
/// RPM/WPM); doing the same in-process gives us the same fail-safe behavior. A
/// TOCTOU race (page freed between guard and access) now degrades to a caught
/// exception or a no-op, never a crash.
///
/// VirtualQuery (Readable/Writable/Regions) stays as a cheap pre-filter so we skip
/// obvious misses without a syscall, but it is NOT the safety net -- RPM/WPM are.
/// </summary>
internal static class Mem
{
    private static readonly nint Self = GetCurrentProcess();
    [ThreadStatic] private static byte[]? _scratch;

    /// <summary>Read n bytes; throws on a failed/partial read (callers that scan catch it).</summary>
    public static byte[] ReadBytes(long a, int n)
    {
        var buf = new byte[n];
        if (!ReadProcessMemory(Self, (nint)a, buf, (nuint)n, out var got) || (int)got != n)
            throw new InvalidOperationException("ReadProcessMemory failed");
        return buf;
    }

    public static bool TryReadBytes(long a, int n, out byte[] buf)
    {
        buf = new byte[n];
        return ReadProcessMemory(Self, (nint)a, buf, (nuint)n, out var got) && (int)got == n;
    }

    /// <summary>Best-effort write; a failed write (freed page) is a safe no-op, never a fault.</summary>
    public static void WriteBytes(long a, byte[] data)
        => WriteProcessMemory(Self, (nint)a, data, (nuint)data.Length, out _);

    public static void W8(long a, byte v)
    {
        var s = _scratch ??= new byte[8];
        s[0] = v;
        WriteProcessMemory(Self, (nint)a, s, 1, out _);
    }

    // scalar reads reuse a per-thread buffer (the engine loop is single-threaded);
    // a failed/freed read returns 0 rather than faulting.
    private static bool ReadScalar(long a, int n)
    {
        var s = _scratch ??= new byte[8];
        return ReadProcessMemory(Self, (nint)a, s, (nuint)n, out var got) && (int)got == n;
    }

    public static byte U8(long a) => ReadScalar(a, 1) ? _scratch![0] : (byte)0;
    public static ushort U16(long a) => ReadScalar(a, 2) ? (ushort)(_scratch![0] | (_scratch[1] << 8)) : (ushort)0;
    public static uint U32(long a) => ReadScalar(a, 4)
        ? (uint)(_scratch![0] | (_scratch[1] << 8) | (_scratch[2] << 16) | (_scratch[3] << 24)) : 0u;

    // little-endian parsers over a byte[] buffer (used by the region scan)
    public static ushort U16(byte[] b, int o) => (ushort)(b[o] | (b[o + 1] << 8));
    public static uint U32(byte[] b, int o) => (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));

    // ---- cheap pre-filter (skip a syscall on obviously-unmapped addresses) ----
    public static bool Readable(long addr, int len) => Probe(addr, len, false);
    public static bool Writable(long addr, int len) => Probe(addr, len, true);

    private static bool Probe(long addr, int len, bool needWrite)
    {
        if (VirtualQueryEx(Self, (nint)addr, out var mbi, (uint)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>()) == 0)
            return false;
        if (mbi.State != MEM_COMMIT || (mbi.Protect & (PAGE_GUARD | PAGE_NOACCESS)) != 0) return false;
        const uint writable = 0x04 | 0x08 | 0x40 | 0x80;   // RW | WriteCopy | ExecRW | ExecWriteCopy
        if ((mbi.Protect & (needWrite ? writable : READABLE)) == 0) return false;
        long b = (long)mbi.BaseAddress, e = b + (long)mbi.RegionSize;
        return addr >= b && addr + len <= e;
    }

    private const uint MEM_COMMIT = 0x1000;
    private const uint READABLE = 0x02 | 0x04 | 0x08 | 0x20 | 0x40 | 0x80;
    private const uint PAGE_GUARD = 0x100;
    private const uint PAGE_NOACCESS = 0x01;

    /// <summary>Yield (base, size) for every committed, readable, non-guard region.</summary>
    public static IEnumerable<(long baseAddr, long size)> Regions()
    {
        long addr = 0;
        var mbi = new MEMORY_BASIC_INFORMATION();
        int mbiSize = Marshal.SizeOf<MEMORY_BASIC_INFORMATION>();
        while (addr < 0x7FFF_FFFF_0000)
        {
            if (VirtualQueryEx(Self, (nint)addr, out mbi, (uint)mbiSize) == 0)
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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(nint h, nint addr, [Out] byte[] buf, nuint size, out nuint read);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(nint h, nint addr, byte[] buf, nuint size, out nuint written);

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
