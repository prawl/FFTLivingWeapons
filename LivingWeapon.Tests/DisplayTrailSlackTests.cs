using System;
using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// ★ TrailSlack boundary (plan v2, LANDMINE 1): the bidirectional attribution search
/// (CardScanner) can now find a weapon's flavor AFTER its "Kills: " hit, the NEW deployed
/// equip-meter layout (Kills line first, blank line, flavor below). When that hit sits near the
/// end of a chunk's own searchable window, the owner's flavor can live almost entirely PAST the
/// chunk boundary, in the region ChunkReader.TrailSlack covers. Real max living flavor is 152
/// chars / 304 UTF-16 bytes (Excalibur id35); this drives a flavor of that exact width so the
/// full forward reach (kills literal + meter slot + "\n\n" + flavor, all UTF-16) is ~344 bytes
/// past the chunk boundary: comfortably inside TrailSlack=4096, but past the OLD TrailSlack=64
/// (mutation: reverting TrailSlack to 64 drops this card's paint, every pass, since chunk
/// boundaries are deterministic per region, no self-heal).
/// </summary>
public class DisplayTrailSlackTests
{
    private const int WeaponId = 10;
    private const int LivingWeaponFlavorChars = 152; // Excalibur id35's real max living flavor

    [Fact]
    public void Forward_owner_flavor_past_a_non_final_chunk_boundary_is_discovered_and_painted()
    {
        string flavor = new string('x', LivingWeaponFlavorChars);
        var meta = new Dictionary<int, WeaponMeta>
        {
            { WeaponId, new WeaponMeta { Name = "Excalibur", Flavor = flavor, Wp = 40, Cat = "Sword", Formula = 1 } },
        };
        var kills = new Dictionary<int, int> { { WeaponId, 11 } }; // meter body "11/25 to +2" (11 chars, no padding)
        var clock = new TestClock();

        const int enc = 2; // UTF-16: the widest encoding, and the one the TrailSlack formula is sized against
        int chunkSize = DisplaySweep.ChunkSize;
        // Two chunks + generous slack so the forward-reaching flavor (and TrailSlack itself) both
        // fit comfortably inside the backing array.
        int regionSize = chunkSize * 2 + 8192;
        long regionBase = 0x90_0000_0000L;
        var regionData = new byte[regionSize];

        byte[] killsB = ByteScan.Enc("Kills: ", enc);
        // The UNPAINTED placeholder (kills=0's meter, NOT the target kills=11 value), so the
        // paint-round-trip assertion below is non-vacuous: it only passes if Display actually
        // discovered and repainted the slot, not because the fixture happened to pre-seed the
        // expected answer.
        byte[] slotB  = ByteScan.Enc(Signatures.KillsMeterSlot(0), enc);
        byte[] nnB    = ByteScan.Enc("\n\n", enc);
        byte[] flvB   = ByteScan.Enc(flavor, enc);

        // Place "Kills: " so its OWN literal ends exactly at the chunk-0/chunk-1 boundary:
        // the meter slot, the blank line, and the entire flavor all land PAST chunkSize, in the
        // region only TrailSlack (not the next chunk's own Lookback prefix) can reach on a
        // single-chunk read.
        int killsPos = chunkSize - killsB.Length;
        Array.Copy(killsB, 0, regionData, killsPos, killsB.Length);
        int slotPos = killsPos + killsB.Length;
        Array.Copy(slotB, 0, regionData, slotPos, slotB.Length);
        int nnPos = slotPos + slotB.Length;
        Array.Copy(nnB, 0, regionData, nnPos, nnB.Length);
        int flavorPos = nnPos + nnB.Length;
        Array.Copy(flvB, 0, regionData, flavorPos, flvB.Length);

        // Sanity-check the fixture's own premise: the forward reach from the "Kills: " hit past
        // the chunk boundary must land in the ~344-byte range the plan's own measurement cites:
        // past the OLD TrailSlack=64 (so that undersized value would drop this card's paint) but
        // nowhere near the CURRENT TrailSlack=4096. Independent of ChunkReader.TrailSlack itself,
        // so this precondition can't rot into a tautology if the constant ever moves again; the
        // REAL load-bearing assertion is the paint-round-trip below.
        int forwardReachPastChunkBoundary = (flavorPos + flvB.Length) - chunkSize;
        Assert.InRange(forwardReachPastChunkBoundary, 300, 400);

        long statBase = 0x91_0000_0000L;
        var statics = new byte[64];
        statics[0] = WeaponId; statics[1] = 0; // MirrorWeapon = WeaponId

        var heap = new FakeHeap((regionBase, regionData), (statBase, statics));
        var wrapped = new OffsetRemapMem(heap,
            mirrorWeaponAddr:  statBase,
            mirrorOffHandAddr: statBase + 2,
            wpScratchAddr:     statBase + 4);
        var display = new Display(meta, kills, wrapped, clock.Func);

        CardFixtures.DrainGeneration(display, clock, 600);

        heap.TryReadBytes(regionBase + slotPos, Signatures.KillsMeterSlotChars * enc, out var painted);
        Assert.Equal(ByteScan.Enc(Signatures.KillsMeterSlot(11), enc), painted);
    }
}
