using System;
using System.Runtime.InteropServices;

namespace LivingWeapon;

/// <summary>
/// LW-346: executable memory within +-2 GB of a target address, for the thunk-clone stubs (a
/// five-byte E9 jmp rel32 can only reach that far) and the relocated extended catalog (the
/// accessor's <c>lea rax,[rax*4 + disp32]</c> is a signed 32-bit displacement from the image
/// base). Ported from the FFTHandsFree research rig's INearAllocator/LiveNearAllocator
/// (branch capbreak-equip, owner-observed working on 1.5.2 2026-08-26/27).
///
/// The live adapter walks MEM_FREE regions with VirtualQueryEx and commits the first 64 KB-aligned
/// candidate inside the window with VirtualAlloc(PAGE_EXECUTE_READWRITE). It never frees: a
/// stub page may be mid-execution on a game thread at any moment, and one leaked page per
/// process lifetime is the price of never racing it (the rig's own rule, R3). Tests inject a
/// fake that hands back a deterministic in-window address.
/// </summary>
internal interface INearAllocator
{
    /// <summary>Allocate at least <paramref name="size"/> executable bytes within int32 reach of
    /// <paramref name="nearAddr"/>. 0 when no region qualifies; never throws.</summary>
    long Alloc(int size, long nearAddr);
}

internal sealed class LiveNearAllocator : INearAllocator
{
    private const uint MEM_FREE = 0x10000;
    private const uint MEM_RESERVE = 0x2000;
    private const uint MEM_COMMIT = 0x1000;
    private const uint PAGE_EXECUTE_READWRITE = 0x40;
    /// <summary>Just under 2 GB, so thunk -&gt; stub and stub -&gt; image both encode as rel32.</summary>
    internal const long SearchRange = 0x7FFF0000L;
    /// <summary>VirtualAlloc snaps a requested base DOWN to 64 KB; candidates are rounded UP first.</summary>
    internal const int AllocGranule = 0x10000;

    public long Alloc(int size, long nearAddr)
    {
        try
        {
            nint process = GetCurrentProcess();
            long lo = nearAddr - SearchRange;
            long scan = lo < 0 ? 0 : lo;
            long hi = nearAddr + SearchRange;
            while (scan < hi)
            {
                if (VirtualQueryEx(process, (nint)scan, out var mbi, (uint)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>()) == 0)
                    break;
                long regionBase = (long)mbi.BaseAddress, regionSize = (long)mbi.RegionSize;
                if (mbi.State == MEM_FREE && regionSize >= size
                    && PickAllocBase(regionBase, regionSize, size, nearAddr, SearchRange, AllocGranule, out long candidate))
                {
                    nint got = VirtualAlloc((nint)candidate, (nuint)size, MEM_RESERVE | MEM_COMMIT, PAGE_EXECUTE_READWRITE);
                    if (got != 0) return (long)got;
                }
                long next = regionBase + regionSize;
                if (next <= scan) break;   // malformed region: refuse rather than spin
                scan = next;
            }
        }
        catch (Exception) { /* fall through: 0 = refused */ }
        return 0;
    }

    /// <summary>Pure: the lowest granule-aligned address inside a free region that also lies in
    /// [nearAddr - searchRange, nearAddr + searchRange) with room for <paramref name="size"/>
    /// bytes. False when the region has no such address. Exposed for the unit tests.</summary>
    internal static bool PickAllocBase(long regionBase, long regionSize, int size,
        long nearAddr, long searchRange, int granule, out long candidate)
    {
        candidate = 0;
        long regionEnd = regionBase + regionSize;
        long rangeMin = nearAddr - searchRange;
        long rangeMax = nearAddr + searchRange;
        long alignedBase = (regionBase + granule - 1) / granule * granule;
        long start = Math.Max(alignedBase, rangeMin);
        if (start % granule != 0) start = (start / granule + 1) * granule;
        if (start + size > regionEnd) return false;
        if (start >= rangeMax) return false;
        candidate = start;
        return true;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint VirtualAlloc(nint lpAddress, nuint dwSize, uint flAllocationType, uint flProtect);

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
