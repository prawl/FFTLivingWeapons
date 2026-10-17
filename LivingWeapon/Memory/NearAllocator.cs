using System;
using System.Runtime.InteropServices;

namespace LivingWeapon;

/// <summary>
/// Executable memory within +-2 GB of a target address, for the accessor-hook stubs (a five-byte
/// E9 jmp rel32 only reaches that far) and the relocated extended catalog (signed 32-bit displacement from
/// the image base). Ported from the FFTHandsFree research rig (branch capbreak-equip, working on 1.5.2 2026-08-26/27).
/// The live adapter walks MEM_FREE regions with VirtualQueryEx (up from the target first, then below it)
/// and commits the first 64 KB-aligned candidate with VirtualAlloc(PAGE_EXECUTE_READWRITE). It never frees,
/// because a stub page may be mid-execution on a game thread (the rig's rule). Tests inject a fake.
/// </summary>
internal interface INearAllocator
{
    /// <summary>Allocates at least <paramref name="size"/> executable bytes within int32 reach of <paramref name="nearAddr"/>. Returns 0 when no region qualifies; never throws.</summary>
    long Alloc(int size, long nearAddr);
}

internal sealed class LiveNearAllocator : INearAllocator
{
    private readonly IRegionWalker _walker;

    /// <param name="walker">Address-space seam; null (production) walks this process via <see cref="LiveRegionWalker"/>.</param>
    public LiveNearAllocator(IRegionWalker? walker = null) => _walker = walker ?? new LiveRegionWalker();

    /// <summary>Just under 2 GB, so thunk -&gt; stub and stub -&gt; image both encode as rel32.</summary>
    internal const long SearchRange = 0x7FFF0000L;
    /// <summary>VirtualAlloc snaps a requested base down to 64 KB; candidates are rounded UP first.</summary>
    internal const int AllocGranule = 0x10000;

    /// <summary>scans up from <paramref name="nearAddr"/> first, then falls back to the window below it.
    /// A single bottom-up pass would take the lowest free page, which under ASLR lands 2 GB, 64 KB below the image,
    /// out of int32 reach of its rip-relative fields (everything below a 0x7FF6... image is free). Above the image the
    /// first free page sits right past its end. Callers still run their own reach check on the result.</summary>
    public long Alloc(int size, long nearAddr)
    {
        try
        {
            long up = Scan(size, nearAddr, nearAddr + SearchRange);
            if (up != 0) return up;
            long lo = nearAddr - SearchRange;
            return Scan(size, lo < 0 ? 0 : lo, nearAddr);
        }
        catch (Exception) { /* fall through: 0 = refused */ }
        return 0;
    }

    /// <summary>Walks [from, to) region by region and commits the first granule-aligned candidate with room for <paramref name="size"/> bytes; 0 when none qualifies.</summary>
    private long Scan(int size, long from, long to)
    {
        long scan = from;
        while (scan < to)
        {
            if (!_walker.Query(scan, out var region)) break;
            if (region.Free && region.Size >= size
                && PickInWindow(region.Base, region.Size, size, from, to, AllocGranule, out long candidate))
            {
                long got = _walker.Commit(candidate, size);
                if (got != 0) return got;
            }
            long next = region.Base + region.Size;
            if (next <= scan) break;   // malformed region. Refuse rather than spin
            scan = next;
        }
        return 0;
    }

    /// <summary>The lowest granule-aligned address in a free region that also lies in [nearAddr, searchRange,
    /// nearAddr + searchRange) with room for <paramref name="size"/> bytes. False if none. Exposed for tests.</summary>
    internal static bool PickAllocBase(long regionBase, long regionSize, int size,
        long nearAddr, long searchRange, int granule, out long candidate)
        => PickInWindow(regionBase, regionSize, size, nearAddr - searchRange, nearAddr + searchRange, granule, out candidate);

    /// <summary>Variant of <see cref="PickAllocBase"/> over an explicit window [rangeMin, rangeMax).</summary>
    internal static bool PickInWindow(long regionBase, long regionSize, int size,
        long rangeMin, long rangeMax, int granule, out long candidate)
    {
        candidate = 0;
        long regionEnd = regionBase + regionSize;
        long alignedBase = (regionBase + granule - 1) / granule * granule;
        long start = Math.Max(alignedBase, rangeMin);
        if (start % granule != 0) start = (start / granule + 1) * granule;
        if (start + size > regionEnd) return false;
        if (start >= rangeMax) return false;
        candidate = start;
        return true;
    }
}

/// <summary>One answer from the address-space walk, a run of pages starting at <see cref="Base"/> (page-aligned, as VirtualQueryEx reports it) and whether it is free.</summary>
internal readonly record struct MemRegion(long Base, long Size, bool Free);

/// <summary>Address-space seam under <see cref="LiveNearAllocator"/>, so scan order can be unit-tested
/// against modelled layouts (ASLR, preferred base) without touching the real process.</summary>
internal interface IRegionWalker
{
    /// <summary>The region containing <paramref name="address"/>; false ends the walk.</summary>
    bool Query(long address, out MemRegion region);

    /// <summary>Reserves and commits <paramref name="size"/> executable bytes at <paramref name="address"/>; returns the granted base, or 0 on refusal.</summary>
    long Commit(long address, int size);
}

/// <summary>Production walker over VirtualQueryEx and VirtualAlloc (MEM_RESERVE | MEM_COMMIT, PAGE_EXECUTE_READWRITE). Never frees (see INearAllocator).</summary>
internal sealed class LiveRegionWalker : IRegionWalker
{
    private const uint MEM_FREE = 0x10000;
    private const uint MEM_RESERVE = 0x2000;
    private const uint MEM_COMMIT = 0x1000;
    private const uint PAGE_EXECUTE_READWRITE = 0x40;

    public bool Query(long address, out MemRegion region)
    {
        region = default;
        if (VirtualQueryEx(GetCurrentProcess(), (nint)address, out var mbi, (uint)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>()) == 0)
            return false;
        region = new MemRegion((long)mbi.BaseAddress, (long)mbi.RegionSize, mbi.State == MEM_FREE);
        return true;
    }

    public long Commit(long address, int size) =>
        (long)VirtualAlloc((nint)address, (nuint)size, MEM_RESERVE | MEM_COMMIT, PAGE_EXECUTE_READWRITE);

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
