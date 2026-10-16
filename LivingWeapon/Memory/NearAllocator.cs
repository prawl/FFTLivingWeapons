using System;
using System.Runtime.InteropServices;

namespace LivingWeapon;

/// <summary>
/// LW-346: executable memory within +-2 GB of a target address, for the accessor-hook stubs (a
/// five-byte E9 jmp rel32 can only reach that far) and the relocated extended catalog (the
/// accessor's <c>lea rax,[rax*4 + disp32]</c> is a signed 32-bit displacement from the image
/// base). Ported from the FFTHandsFree research rig's INearAllocator/LiveNearAllocator
/// (branch capbreak-equip, owner-observed working on 1.5.2 2026-08-26/27).
///
/// The live adapter walks MEM_FREE regions with VirtualQueryEx (upward from the target first, then
/// the window below it, LW-388) and commits the first 64 KB-aligned candidate with
/// VirtualAlloc(PAGE_EXECUTE_READWRITE). It never frees: a
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
    private readonly IRegionWalker _walker;

    /// <param name="walker">The address-space seam; null (production) walks this process with
    /// VirtualQueryEx/VirtualAlloc (<see cref="LiveRegionWalker"/>).</param>
    public LiveNearAllocator(IRegionWalker? walker = null) => _walker = walker ?? new LiveRegionWalker();

    /// <summary>Just under 2 GB, so thunk -&gt; stub and stub -&gt; image both encode as rel32.</summary>
    internal const long SearchRange = 0x7FFF0000L;
    /// <summary>VirtualAlloc snaps a requested base DOWN to 64 KB; candidates are rounded UP first.</summary>
    internal const int AllocGranule = 0x10000;

    /// <summary>LW-388 (R1): scans UP from <paramref name="nearAddr"/> first, [nearAddr, nearAddr +
    /// SearchRange), then falls back to [nearAddr - SearchRange, nearAddr). The old single
    /// bottom-up pass committed the lowest free page of the whole window: harmless at the
    /// preferred base (the low address space was busy, so the page landed a few hundred MB below
    /// the image), fatal under ASLR (everything below a 0x7FF6... image is free, so the page
    /// landed 2 GB - 64 KB below it, out of int32 reach of the image's own rip-relative fields).
    /// Above the image the first free page sits right past its end, close to every field. Every
    /// caller still runs its own reach check on the result (the refusal net).</summary>
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

    /// <summary>Walks [from, to) region by region and commits the first granule-aligned candidate
    /// with room for <paramref name="size"/> bytes; 0 when none qualifies.</summary>
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
            if (next <= scan) break;   // malformed region: refuse rather than spin
            scan = next;
        }
        return 0;
    }

    /// <summary>Pure: the lowest granule-aligned address inside a free region that also lies in
    /// [nearAddr - searchRange, nearAddr + searchRange) with room for <paramref name="size"/>
    /// bytes. False when the region has no such address. Exposed for the unit tests.</summary>
    internal static bool PickAllocBase(long regionBase, long regionSize, int size,
        long nearAddr, long searchRange, int granule, out long candidate)
        => PickInWindow(regionBase, regionSize, size, nearAddr - searchRange, nearAddr + searchRange, granule, out candidate);

    /// <summary>Pure: <see cref="PickAllocBase"/> over an explicit window [rangeMin, rangeMax).</summary>
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

/// <summary>One answer from the address-space walk: a run of pages starting at <see cref="Base"/>
/// (the queried address, page-aligned, the way VirtualQueryEx reports it) and whether it is free.</summary>
internal readonly record struct MemRegion(long Base, long Size, bool Free);

/// <summary>LW-388 A14: the address-space seam under <see cref="LiveNearAllocator"/>, so the scan
/// order can be unit-tested against modelled layouts (ASLR, preferred base) without touching the
/// real process.</summary>
internal interface IRegionWalker
{
    /// <summary>The region containing <paramref name="address"/>; false ends the walk.</summary>
    bool Query(long address, out MemRegion region);

    /// <summary>Reserve and commit <paramref name="size"/> executable bytes at exactly
    /// <paramref name="address"/>; the granted base, or 0 on refusal.</summary>
    long Commit(long address, int size);
}

/// <summary>The production walker: VirtualQueryEx over this process and VirtualAlloc
/// (MEM_RESERVE | MEM_COMMIT, PAGE_EXECUTE_READWRITE). Never frees (see INearAllocator).</summary>
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
