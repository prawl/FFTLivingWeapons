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
    /// <summary>LW-50 write gate: production flips this FALSE in Mod.StartEngine, before the
    /// Engine (and its FastHold thread) is constructed, and only LaunchGuard's Armed edge flips it
    /// back true. Default TRUE keeps every existing pinned-buffer suite unchanged: the whole test
    /// assembly already runs serialized ([assembly: CollectionBehavior(DisableTestParallelization
    /// = true)], TestLoggingSetup.cs:12), so a test that flips this false restores it in a
    /// finally. Volatile: read on the engine's background loop thread, written from the same
    /// thread's LaunchGuard.Step edge in production, but the field is shared state, not
    /// thread-confined by construction.</summary>
    internal static volatile bool WritesEnabled = true;

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

    /// <summary>
    /// ReadProcessMemory into a caller-managed buffer. Returns n on a full read, else 0.
    /// Guards n <= buf.Length; an undersized buffer returns 0 (no allocation, no exception).
    /// </summary>
    public static int ReadInto(long a, byte[] buf, int n)
    {
        if (n > buf.Length) return 0;
        return ReadProcessMemory(Self, (nint)a, buf, (nuint)n, out var got) && (int)got == n ? n : 0;
    }

    /// <summary>Best-effort write; a failed write (freed page) is a safe no-op, never a fault.</summary>
    public static void WriteBytes(long a, byte[] data)
    {
        if (!WritesEnabled) return;
        WriteProcessMemory(Self, (nint)a, data, (nuint)data.Length, out _);
    }

    public static void W8(long a, byte v)
    {
        if (!WritesEnabled) return;
        var s = _scratch ??= new byte[8];
        s[0] = v;
        WriteProcessMemory(Self, (nint)a, s, 1, out _);
    }

    public static void W16(long a, ushort v)
    {
        if (!WritesEnabled) return;
        var s = _scratch ??= new byte[8];
        s[0] = (byte)(v & 0xFF);
        s[1] = (byte)(v >> 8);
        WriteProcessMemory(Self, (nint)a, s, 2, out _);
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

    /// <summary>One atomic 8-byte RPM read (_scratch is byte[8], so ReadScalar(a,8) is safe).
    /// Used for engine pointer globals (Offsets.ActorPtr) where a torn 8x1-byte composite could
    /// mix two pointer values mid-transition.</summary>
    public static ulong U64(long a)
    {
        if (!ReadScalar(a, 8)) return 0UL;
        ulong v = 0;
        for (int i = 0; i < 8; i++) v |= (ulong)_scratch![i] << (i * 8);
        return v;
    }

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
    private const uint MEM_PRIVATE = 0x20000;
    private const uint WRITABLE = 0x04 | 0x08 | 0x40 | 0x80;   // RW | WriteCopy | ExecRW | ExecWriteCopy
    private const uint READABLE = 0x02 | 0x04 | 0x08 | 0x20 | 0x40 | 0x80;
    private const uint PAGE_GUARD = 0x100;
    private const uint PAGE_NOACCESS = 0x01;

    /// <summary>Yield (base, size) for every committed, PRIVATE, writable, non-guard region --
    /// the process heap where the game's UI render copies (the card text we paint) live. Skips
    /// module (IMAGE) and file-backed (MAPPED) memory: the display can't write there and the
    /// card text is never there, so excluding them shrinks the paint scan from GBs to the heap
    /// (much faster, which is what lets the counter refresh near-instantly).</summary>
    public static IEnumerable<(long baseAddr, long size)> Regions()
    {
        long addr = 0;
        MEMORY_BASIC_INFORMATION mbi;   // first touched via `out mbi` below; no initializer needed
        int mbiSize = Marshal.SizeOf<MEMORY_BASIC_INFORMATION>();
        while (addr < 0x7FFF_FFFF_0000)
        {
            if (VirtualQueryEx(Self, (nint)addr, out mbi, (uint)mbiSize) == 0)
                break;
            long b = (long)mbi.BaseAddress;
            long size = (long)mbi.RegionSize;
            long next = b + size;
            if (mbi.State == MEM_COMMIT && mbi.Type == MEM_PRIVATE && (mbi.Protect & WRITABLE) != 0
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
