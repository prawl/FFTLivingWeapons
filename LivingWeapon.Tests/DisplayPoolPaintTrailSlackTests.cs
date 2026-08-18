using System;
using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-37's pool scan reuses ChunkReader (Lookback/TrailSlack) exactly like the whole-heap
/// sweep, so a boundary card (one whose "Kills: " literal ends right at a chunk boundary,
/// with its owner flavor landing almost entirely in TrailSlack) must still be discovered and
/// painted through the pool path. Direct pool-path twin of DisplayTrailSlackTests.cs (same
/// fixture shape and the same real-world worst case, Excalibur id35's 152-char flavor), driven
/// with poolPaint:true instead of the default sweep.
/// </summary>
public class DisplayPoolPaintTrailSlackTests
{
    private const int WeaponId = 10;
    private const int LivingWeaponFlavorChars = 152;

    [Fact]
    public void PoolPaint_forward_owner_flavor_past_a_non_final_chunk_boundary_is_discovered_and_painted()
    {
        string flavor = new string('x', LivingWeaponFlavorChars);
        var meta = new Dictionary<int, WeaponMeta>
        {
            { WeaponId, new WeaponMeta { Name = "Excalibur", Flavor = flavor, Wp = 40, Cat = "Sword", Formula = 1 } },
        };
        var kills = new Dictionary<int, int> { { WeaponId, 11 } };   // meter body "11/15 to +3"
        var clock = new TestClock();

        const int enc = 2;   // UTF-16: the widest encoding, and the one TrailSlack is sized against
        int chunkSize = DisplaySweep.ChunkSize;
        int regionSize = chunkSize * 2 + 8192;   // two chunks + slack, well inside the backing array
        long regionBase = 0x92_0000_0000L;
        var regionData = new byte[regionSize];

        byte[] nameB  = ByteScan.Enc("Excalibur", enc);
        byte[] killsB = ByteScan.Enc("Kills: ", enc);
        byte[] slotB  = ByteScan.Enc(Signatures.KillsMeterSlot(0), enc);   // unpainted placeholder
        byte[] nnB    = ByteScan.Enc("\n\n", enc);
        byte[] flvB   = ByteScan.Enc(flavor, enc);

        // "Kills: " ends exactly at the chunk-0/chunk-1 boundary: the meter slot, blank line,
        // and the entire flavor all land past chunkSize, in TrailSlack territory. The owner NAME
        // sits just ahead of the Kills line (chunk 0, within PoolLocatorPolicy.NameWindow) so the
        // buffer carries the baked-pool signature the discriminator now requires.
        int killsPos = chunkSize - killsB.Length;
        int namePos = killsPos - nnB.Length - nameB.Length;
        Array.Copy(nameB, 0, regionData, namePos, nameB.Length);
        Array.Copy(nnB, 0, regionData, namePos + nameB.Length, nnB.Length);
        Array.Copy(killsB, 0, regionData, killsPos, killsB.Length);
        int slotPos = killsPos + killsB.Length;
        Array.Copy(slotB, 0, regionData, slotPos, slotB.Length);
        int nnPos = slotPos + slotB.Length;
        Array.Copy(nnB, 0, regionData, nnPos, nnB.Length);
        int flavorPos = nnPos + nnB.Length;
        Array.Copy(flvB, 0, regionData, flavorPos, flvB.Length);

        int forwardReachPastChunkBoundary = (flavorPos + flvB.Length) - chunkSize;
        Assert.InRange(forwardReachPastChunkBoundary, 300, 400);   // fixture sanity, not the real assertion

        long statBase = 0x93_0000_0000L;
        var statics = new byte[64];
        statics[0] = WeaponId; statics[1] = 0;

        var heap = new FakeHeap((regionBase, regionData, true), (statBase, statics, true));
        var display = CardFixtures.MakeDisplay(meta, kills, heap, statBase, clock, poolPaint: true);

        // LW-261: the region here (two chunks + slack, > PoolLocator.LocateBudgetBytes) now takes
        // more than one Tick's worth of the resumable scan's own budget to locate. Drive the
        // locate to completion directly (StepPoolLocate, the same method Engine's own "pool-
        // locate" lane calls independently every tick) BEFORE the first Tick(), rather than
        // interleaving Tick() calls while the scan is still in flight -- interleaving would let
        // Tick's own whole-heap sweep fallback (which runs whenever the pool path hasn't
        // published yet, out-of-battle budget 16MB, easily enough for this single ~8MB region)
        // race the budgeted pool locate and win, which is the correct real-world fallback
        // behavior but not what this fixture means to exercise: whether the pool path itself can
        // discover a boundary-straddling card once it HAS located the region.
        for (int i = 0; i < 10; i++) { display.StepPoolLocate(); clock.Ms += 33; }

        clock.Ms += DisplaySweep.HotRescanMs + 1;
        display.Tick(false);

        // The sweep never ran (Tick(budget, OnChunk) was never invoked): the paint below can
        // only have come from PoolLocator's own chunked region walk.
        Assert.False(display._sweep.IsComplete);

        heap.TryReadBytes(regionBase + slotPos, Signatures.KillsMeterSlotChars * enc, out var painted);
        Assert.Equal(ByteScan.Enc(Signatures.KillsMeterSlot(11), enc), painted);
    }
}
