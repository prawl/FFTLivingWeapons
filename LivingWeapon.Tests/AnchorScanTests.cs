using System;
using System.Collections.Generic;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-82's portable scan core (AnchorScan.cs): a plain delegate over an in-memory byte buffer, no
/// game types involved. Every fixture places a 4-byte signature {0xAA,0xBB,0xCC,0xDD} at chosen
/// offsets inside a flat buffer addressed from 0; SignatureOffset is nonzero in most fixtures so
/// the hit-to-base arithmetic (base = hit start - SignatureOffset) is actually exercised, not
/// accidentally satisfied by offset 0.
/// </summary>
public class AnchorScanTests
{
    private static readonly byte[] Sig = { 0xAA, 0xBB, 0xCC, 0xDD };
    private const int SigOffset = 4;   // base sits 4 bytes before the signature hit

    /// <summary>A flat buffer addressed from 0. Reads outside [0, mem.Length) fail (return false),
    /// same fail-safe contract as Mem/LiveMemory. <paramref name="unreadable"/> optionally names
    /// chunk-start addresses whose read must fail even though the bytes exist, so tests can force
    /// the unreadable-chunk path deliberately. <paramref name="reads"/>, if provided, records every
    /// (addr, len) actually requested so a test can assert no read strayed past a region.</summary>
    private static AnchorTryRead FromBuffer(byte[] mem, HashSet<long>? unreadable = null,
        List<(long addr, int len)>? reads = null)
    {
        return (long addr, int len, out byte[] buf) =>
        {
            reads?.Add((addr, len));
            buf = new byte[len];
            if (unreadable != null && unreadable.Contains(addr)) return false;
            if (addr < 0 || addr + len > mem.Length) return false;
            Array.Copy(mem, addr, buf, 0, len);
            return true;
        };
    }

    private static void PlaceSignature(byte[] mem, int hitStart)
    {
        for (int i = 0; i < Sig.Length; i++) mem[hitStart + i] = Sig[i];
    }

    private static AnchorScan RunToCompletion(AnchorScan scan)
    {
        while (scan.Step()) { }
        return scan;
    }

    // 1. found-at-pin
    [Fact]
    public void Found_at_pin_when_the_only_hit_matches_the_pinned_address()
    {
        var mem = new byte[64];
        PlaceSignature(mem, hitStart: 20);   // base = 20 - 4 = 16
        var spec = new AnchorSpec("anchor", Sig, SigOffset, pinnedAddress: 16, regionLo: 0, regionHi: 64);
        var scan = new AnchorScan(spec, FromBuffer(mem), chunkBytes: 16);

        RunToCompletion(scan);

        Assert.Equal(AnchorVerdict.FoundAtPin, scan.Verdict);
        Assert.Equal(new long[] { 16 }, scan.Bases);
    }

    // 2. found-elsewhere
    [Fact]
    public void Found_elsewhere_when_the_only_hit_does_not_match_the_pinned_address()
    {
        var mem = new byte[64];
        PlaceSignature(mem, hitStart: 20);   // base = 16
        var spec = new AnchorSpec("anchor", Sig, SigOffset, pinnedAddress: 999, regionLo: 0, regionHi: 64);
        var scan = new AnchorScan(spec, FromBuffer(mem), chunkBytes: 16);

        RunToCompletion(scan);

        Assert.Equal(AnchorVerdict.FoundElsewhere, scan.Verdict);
        Assert.Equal(new long[] { 16 }, scan.Bases);
    }

    // 3. ambiguous (both bases reported)
    [Fact]
    public void Ambiguous_when_more_than_one_base_survives_and_both_are_reported()
    {
        var mem = new byte[128];
        PlaceSignature(mem, hitStart: 20);    // base = 16
        PlaceSignature(mem, hitStart: 100);   // base = 96
        var spec = new AnchorSpec("anchor", Sig, SigOffset, pinnedAddress: 16, regionLo: 0, regionHi: 128);
        var scan = new AnchorScan(spec, FromBuffer(mem), chunkBytes: 16);

        RunToCompletion(scan);

        Assert.Equal(AnchorVerdict.Ambiguous, scan.Verdict);
        Assert.Contains(16L, scan.Bases);
        Assert.Contains(96L, scan.Bases);
        Assert.Equal(2, scan.Bases.Count);
    }

    // 4. not-found
    [Fact]
    public void Not_found_when_the_signature_never_appears()
    {
        var mem = new byte[64];   // all zero: no signature anywhere
        var spec = new AnchorSpec("anchor", Sig, SigOffset, pinnedAddress: 16, regionLo: 0, regionHi: 64);
        var scan = new AnchorScan(spec, FromBuffer(mem), chunkBytes: 16);

        RunToCompletion(scan);

        Assert.Equal(AnchorVerdict.NotFound, scan.Verdict);
        Assert.Empty(scan.Bases);
    }

    // 5. signature straddling a chunk boundary is found
    [Fact]
    public void Signature_straddling_a_chunk_boundary_is_found()
    {
        var mem = new byte[64];
        PlaceSignature(mem, hitStart: 14);   // straddles the cursor=0 chunk's chunkBytes=16 boundary
        var spec = new AnchorSpec("anchor", Sig, SigOffset, pinnedAddress: 10, regionLo: 0, regionHi: 64);
        var scan = new AnchorScan(spec, FromBuffer(mem), chunkBytes: 16);

        RunToCompletion(scan);

        Assert.Equal(AnchorVerdict.FoundAtPin, scan.Verdict);   // base = 14 - 4 = 10 == pin
        Assert.Equal(new long[] { 10 }, scan.Bases);
    }

    // 6. a hit whose match extends into the overlap tail is reported exactly once
    [Fact]
    public void A_hit_extending_into_the_overlap_tail_is_reported_exactly_once()
    {
        var mem = new byte[64];
        PlaceSignature(mem, hitStart: 15);   // bytes 15..18: starts in chunk 0's window, tail extends past 16
        var spec = new AnchorSpec("anchor", Sig, SigOffset, pinnedAddress: 11, regionLo: 0, regionHi: 64);
        var scan = new AnchorScan(spec, FromBuffer(mem), chunkBytes: 16);

        RunToCompletion(scan);

        Assert.Single(scan.Bases);
        Assert.Equal(11L, scan.Bases[0]);
    }

    // 7. unreadable chunks skipped and counted, scan continues
    [Fact]
    public void Unreadable_chunks_are_skipped_and_counted_and_the_scan_continues()
    {
        var mem = new byte[64];
        PlaceSignature(mem, hitStart: 36);   // base = 32, lands in the chunk starting at cursor 32
        var unreadable = new HashSet<long> { 16 };   // the chunk starting at cursor 16 fails to read
        var spec = new AnchorSpec("anchor", Sig, SigOffset, pinnedAddress: 32, regionLo: 0, regionHi: 64);
        var scan = new AnchorScan(spec, FromBuffer(mem, unreadable), chunkBytes: 16);

        RunToCompletion(scan);

        Assert.Equal(1, scan.UnreadableChunks);
        Assert.Equal(AnchorVerdict.FoundAtPin, scan.Verdict);
        Assert.Equal(new long[] { 32 }, scan.Bases);
    }

    // 8. Confirm rejection drops a hit
    [Fact]
    public void Confirm_rejection_drops_an_otherwise_matching_hit()
    {
        var mem = new byte[64];
        PlaceSignature(mem, hitStart: 20);   // base = 16
        var spec = new AnchorSpec("anchor", Sig, SigOffset, pinnedAddress: 16, regionLo: 0, regionHi: 64,
            confirm: _ => false);
        var scan = new AnchorScan(spec, FromBuffer(mem), chunkBytes: 16);

        RunToCompletion(scan);

        Assert.Equal(AnchorVerdict.NotFound, scan.Verdict);
        Assert.Empty(scan.Bases);
    }

    // 9. BaseAlignment rejects a misaligned base BEFORE Confirm runs
    [Fact]
    public void BaseAlignment_rejects_a_misaligned_base_before_confirm_ever_runs()
    {
        var mem = new byte[64];
        PlaceSignature(mem, hitStart: 21);   // base = 21 - 4 = 17: not a multiple of 8
        int confirmCalls = 0;
        var spec = new AnchorSpec("anchor", Sig, SigOffset, pinnedAddress: 17, regionLo: 0, regionHi: 64,
            baseAlignment: 8, confirm: _ => { confirmCalls++; return true; });
        var scan = new AnchorScan(spec, FromBuffer(mem), chunkBytes: 16);

        RunToCompletion(scan);

        Assert.Equal(0, confirmCalls);
        Assert.Equal(AnchorVerdict.NotFound, scan.Verdict);
    }

    // 10. verdict stays Scanning until region exhausted, Step reports completion
    [Fact]
    public void Verdict_stays_scanning_until_the_region_is_exhausted()
    {
        var mem = new byte[64];   // no signature: region needs multiple chunks to exhaust
        var spec = new AnchorSpec("anchor", Sig, SigOffset, pinnedAddress: 16, regionLo: 0, regionHi: 64);
        var scan = new AnchorScan(spec, FromBuffer(mem), chunkBytes: 16);

        Assert.Equal(AnchorVerdict.Scanning, scan.Verdict);
        bool more = scan.Step();
        Assert.True(more);
        Assert.Equal(AnchorVerdict.Scanning, scan.Verdict);

        while (more) more = scan.Step();

        Assert.NotEqual(AnchorVerdict.Scanning, scan.Verdict);
        Assert.Equal(AnchorVerdict.NotFound, scan.Verdict);
    }

    // 11. Reset re-arms
    [Fact]
    public void Reset_rearms_the_scan_for_an_identical_rerun()
    {
        var mem = new byte[64];
        PlaceSignature(mem, hitStart: 20);   // base = 16
        var spec = new AnchorSpec("anchor", Sig, SigOffset, pinnedAddress: 16, regionLo: 0, regionHi: 64);
        var scan = new AnchorScan(spec, FromBuffer(mem), chunkBytes: 16);
        RunToCompletion(scan);
        Assert.Equal(AnchorVerdict.FoundAtPin, scan.Verdict);

        scan.Reset();

        Assert.Equal(AnchorVerdict.Scanning, scan.Verdict);
        Assert.Empty(scan.Bases);
        Assert.Equal(0, scan.UnreadableChunks);

        RunToCompletion(scan);
        Assert.Equal(AnchorVerdict.FoundAtPin, scan.Verdict);
        Assert.Equal(new long[] { 16 }, scan.Bases);
    }

    // 12. tail chunk clamps at RegionHi (a signature placed past RegionHi is NOT found; no read past region)
    [Fact]
    public void Tail_chunk_clamps_at_RegionHi_and_never_reads_past_the_region()
    {
        var mem = new byte[64];
        // Placed so the signature only fully exists if a read were allowed to cross RegionHi=32:
        // hitStart 30 means bytes 30..33, but the region ends at 32.
        PlaceSignature(mem, hitStart: 30);
        var reads = new List<(long addr, int len)>();
        var spec = new AnchorSpec("anchor", Sig, SigOffset, pinnedAddress: 26, regionLo: 0, regionHi: 32);
        var scan = new AnchorScan(spec, FromBuffer(mem, reads: reads), chunkBytes: 16);

        RunToCompletion(scan);

        Assert.Equal(AnchorVerdict.NotFound, scan.Verdict);
        Assert.Empty(scan.Bases);
        foreach (var (addr, len) in reads)
            Assert.True(addr + len <= 32, $"read [{addr}, {addr + len}) crossed RegionHi=32");
    }
}
