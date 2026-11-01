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
        public readonly HashSet<long> Refuse = new();
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
            if (Refuse.Contains(address)) return 0;
            bool ok = _regions.Any(r => r.Free && address >= r.Start && address + size <= r.End);
            if (ok) Commits.Add(address);
            return ok ? address : 0;
        }
    }

    private const long UserTop = 0x7FFF_FFFF_0000L;   // top of the x64 user address space (64 KB granular)
    private const long AslrBase = 0x7FF6_1234_0000L;  // a plausible ASLR image base (LaunchGuard/ImageBase suites)
    private const long NoDrmSizeOfImage = 0x03F9E000L;   // LaunchGuard.ExpectedSizeOfImage, Denuvo-free 1.5.2
    private const long DenuvoSizeOfImage = 0x18D78000L;  // the Denuvo-wrapped 1.5.2 SizeOfImage (PORT_1.5.2_OFFSETS.md)

    private const long PreferredBase = 0x140000000L;
    private const long TopListFieldRva = 0x396190L;   // highest rip-relative list field, see the header

    [Fact]
    public void Preferred_base_with_nothing_free_above_picks_the_page_right_under_the_image()
    {
        var walker = new FakeWalker(
            (0x10000L, PreferredBase, true),
            (PreferredBase, PreferredBase + NoDrmSizeOfImage, false),
            (PreferredBase + NoDrmSizeOfImage, UserTop, false));

        long got = new LiveNearAllocator(walker).Alloc(0x1000, PreferredBase);

        Assert.Equal(PreferredBase - 0x10000, got);
        Assert.InRange(got - (PreferredBase + TopListFieldRva), int.MinValue, int.MaxValue);
    }

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
        // Busy for the whole window above; one free gap 256 MB below the image. The top granule of it wins.
        long gap = AslrBase - 0x10000000L;
        var walker = new FakeWalker(
            (0x10000L, gap, false),
            (gap, gap + 0x100000L, true),
            (gap + 0x100000L, UserTop, false));

        long got = new LiveNearAllocator(walker).Alloc(0x1000, AslrBase);

        Assert.Equal(gap + 0xF0000, got);
    }

    private static FakeWalker TwoGapsBelow() => new(
        (0x10000L, PreferredBase - 0x40000000L, false),
        (PreferredBase - 0x40000000L, PreferredBase - 0x40000000L + 0x100000L, true),
        (PreferredBase - 0x40000000L + 0x100000L, PreferredBase - 0x10000000L, false),
        (PreferredBase - 0x10000000L, PreferredBase - 0x10000000L + 0x100000L, true),
        (PreferredBase - 0x10000000L + 0x100000L, UserTop, false));

    [Fact]
    public void Fallback_below_takes_the_highest_free_gap_not_the_lowest()
    {
        long got = new LiveNearAllocator(TwoGapsBelow()).Alloc(0x1000, PreferredBase);

        Assert.Equal(PreferredBase - 0x10000000L + 0xF0000, got);
    }

    [Fact]
    public void Fallback_below_moves_down_when_the_top_commit_is_refused()
    {
        var walker = TwoGapsBelow();
        walker.Refuse.Add(PreferredBase - 0x10000000L + 0xF0000);

        long got = new LiveNearAllocator(walker).Alloc(0x1000, PreferredBase);

        Assert.Equal(PreferredBase - 0x40000000L + 0xF0000, got);
        Assert.Equal(new[] { got }, walker.Commits);
    }

    [Theory]
    [InlineData(0x150000000L, 0x20000L, 4096, 0xC0010000L, 0x140000000L, false, 0L)]
    [InlineData(0x13FF00000L, 0x200000L, 4096, 0xC0010000L, 0x140000000L, true, 0x13FFF0000L)]
    [InlineData(0x100000000L, 0x100000L, 4096, 0xC0010000L, 0x140000000L, true, 0x1000F0000L)]
    [InlineData(0xC0000000L, 0x10000000L, 4096, 0xC0010000L, 0x140000000L, true, 0xCFFF0000L)]
    [InlineData(0x100001000L, 0xF000L, 4096, 0xC0010000L, 0x140000000L, false, 0L)]
    public void PickTopInWindow_takes_the_highest_aligned_start_inside_the_window(
        long regionBase, long regionSize, int size, long rangeMin, long rangeMax, bool expectedOk, long expectedCandidate)
    {
        bool ok = LiveNearAllocator.PickTopInWindow(regionBase, regionSize, size, rangeMin, rangeMax,
            LiveNearAllocator.AllocGranule, out long candidate);

        Assert.Equal(expectedOk, ok);
        Assert.Equal(expectedCandidate, candidate);
    }

    [Fact]
    public void Refuses_with_zero_when_nothing_in_the_window_is_free()
    {
        var walker = new FakeWalker((0x10000L, UserTop, false));
        Assert.Equal(0, new LiveNearAllocator(walker).Alloc(0x1000, AslrBase));
        Assert.Empty(walker.Commits);
    }

    [Fact]
    public void Fallback_logs_one_near_page_line_only_when_nothing_above_commits()
    {
        // leg one, free page found right under the image
        using (var cap = LogCapture.Start(level: LogLevel.Debug))
        {
            new LiveNearAllocator(new FakeWalker(
                (0x10000L, PreferredBase, true),
                (PreferredBase, PreferredBase + NoDrmSizeOfImage, false),
                (PreferredBase + NoDrmSizeOfImage, UserTop, false))).Alloc(0x1000, PreferredBase);

            var line = Assert.Single(cap.File, l => l.Contains("near page:"));
            Assert.Contains("took the closest free page below the image at 0x13FFF0000", line);
        }

        // leg two, nothing free anywhere
        using (var cap = LogCapture.Start(level: LogLevel.Debug))
        {
            new LiveNearAllocator(new FakeWalker((0x10000L, UserTop, false))).Alloc(0x1000, AslrBase);

            var line = Assert.Single(cap.File, l => l.Contains("near page:"));
            Assert.Contains("nothing below the image either", line);
        }

        // leg three, the up scan succeeds so the fallback stays silent
        using (var cap = LogCapture.Start(level: LogLevel.Debug))
        {
            new LiveNearAllocator(new FakeWalker(
                (0x10000L, AslrBase, true),
                (AslrBase, AslrBase + NoDrmSizeOfImage, false),
                (AslrBase + NoDrmSizeOfImage, UserTop, true))).Alloc(0x1000, AslrBase);

            Assert.DoesNotContain(cap.File, l => l.Contains("near page:"));
        }
    }
}
