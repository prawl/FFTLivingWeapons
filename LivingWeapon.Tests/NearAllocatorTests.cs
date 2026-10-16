using System;
using System.Collections.Generic;
using System.Linq;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-388 R1/A14: which end of its +-2 GB window <see cref="LiveNearAllocator"/> picks. The old
/// scan walked up from nearAddr - 2 GB and committed the first free region. At the preferred base
/// the low 3 GB were busy, so the page landed a few hundred MB below the image; under ASLR the
/// space below a 0x7FF6... image is empty, so the page landed 2 GB - 64 KB below it, out of int32
/// reach of every rip-relative list field (RVAs up to 0x396190), and the extended inventory refused
/// to arm. The scan now walks UP from the image first and falls back to the window below. These
/// model the address space through the injectable <see cref="IRegionWalker"/>.
/// </summary>
public class NearAllocatorTests
{
    /// <summary>A fake address space: sorted, contiguous [Start, End) regions, each free or busy.
    /// Query answers like VirtualQueryEx (the page-aligned queried address as the base, the rest of
    /// its run as the size); Commit grants any page-aligned request inside a free region.</summary>
    private sealed class FakeWalker : IRegionWalker
    {
        private readonly List<(long Start, long End, bool Free)> _regions;
        public readonly List<long> Commits = new();
        public FakeWalker(params (long Start, long End, bool Free)[] regions) => _regions = regions.ToList();

        public bool Query(long address, out MemRegion region)
        {
            region = default;
            foreach (var r in _regions)
            {
                if (address < r.Start || address >= r.End) continue;
                long page = address & ~0xFFFL;
                region = new MemRegion(page, r.End - page, r.Free);
                return true;
            }
            return false;
        }

        public long Commit(long address, int size)
        {
            bool ok = _regions.Any(r => r.Free && address >= r.Start && address + size <= r.End);
            if (ok) Commits.Add(address);
            return ok ? address : 0;
        }
    }

    private const long UserTop = 0x7FFF_FFFF_0000L;   // top of the x64 user address space (64 KB granular)
    private const long AslrBase = 0x7FF6_1234_0000L;  // a plausible ASLR image base (LaunchGuard/ImageBase suites)
    private const long NoDrmSizeOfImage = 0x03F9E000L;   // LaunchGuard.ExpectedSizeOfImage, Denuvo-free 1.5.2
    private const long DenuvoSizeOfImage = 0x18D78000L;  // the Denuvo-wrapped 1.5.2 SizeOfImage (PORT_1.5.2_OFFSETS.md)

    [Fact]
    public void Aslr_layout_picks_the_first_free_page_above_the_image_within_reach_of_its_fields()
    {
        // (a) everything below the image free, the image itself busy, free again right above it.
        var walker = new FakeWalker(
            (0x10000L, AslrBase, true),
            (AslrBase, AslrBase + NoDrmSizeOfImage, false),
            (AslrBase + NoDrmSizeOfImage, UserTop, true));

        long got = new LiveNearAllocator(walker).Alloc(0x1000, AslrBase);

        Assert.True(got >= AslrBase + NoDrmSizeOfImage, $"picked 0x{got:X}, below the image end");
        // within int32 reach of a rip field near the image's code (RVA 0x400000)
        Assert.InRange(got - (AslrBase + 0x400000), int.MinValue, int.MaxValue);
        Assert.Equal(0, got % LiveNearAllocator.AllocGranule);
    }

    [Fact]
    public void Preferred_layout_picks_inside_reach_above_the_image()
    {
        // (b) the preferred base: the low address space busy (the old 3 GB of DLLs and heaps), the
        // Denuvo-sized image at 0x140000000, free above.
        var walker = new FakeWalker(
            (0x10000L, 0x140000000L, false),
            (0x140000000L, 0x140000000L + DenuvoSizeOfImage, false),
            (0x140000000L + DenuvoSizeOfImage, UserTop, true));

        long got = new LiveNearAllocator(walker).Alloc(0x1000, 0x140000000L);

        Assert.NotEqual(0, got);
        Assert.InRange(got - 0x140000000L, int.MinValue, int.MaxValue);
        Assert.True(got >= 0x140000000L + DenuvoSizeOfImage);
    }

    [Fact]
    public void Falls_back_below_the_image_when_nothing_above_is_free()
    {
        // Busy for the whole window above; one free gap 256 MB below the image.
        long gap = AslrBase - 0x10000000L;
        var walker = new FakeWalker(
            (0x10000L, gap, false),
            (gap, gap + 0x100000L, true),
            (gap + 0x100000L, UserTop, false));

        long got = new LiveNearAllocator(walker).Alloc(0x1000, AslrBase);

        Assert.Equal(gap, got);
    }

    [Fact]
    public void Refuses_with_zero_when_nothing_in_the_window_is_free()
    {
        var walker = new FakeWalker((0x10000L, UserTop, false));
        Assert.Equal(0, new LiveNearAllocator(walker).Alloc(0x1000, AslrBase));
        Assert.Empty(walker.Commits);
    }
}
