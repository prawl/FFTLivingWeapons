using System;
using System.Collections.Generic;
using System.Linq;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// Closes the mutation-proven test gaps identified by adversarial review (C1-C9).
/// Each test is named after its gap and is written to fail under the described mutation.
/// </summary>
public class DisplayMutationGapTests
{
    // ─── Shared fixture addresses ─────────────────────────────────────────────

    private const long StaticsBase = 0x80_0000_0000L;
    private const long SourceBase  = 0x81_0000_0000L;

    /// <summary>Build a 3-weapon meta dict (ids 10, 11, 12) with unique names, flavors,
    /// and nonzero kill counts so no assertion is vacuous against the baked "0   "
    /// placeholder (fixes the zero-count-assertions-vacuous gap).</summary>
    private static Dictionary<int, WeaponMeta> BuildMeta() => new()
    {
        { 10, new WeaponMeta { Name = "BladeA", Flavor = "Forged under winter stars", Wp = 12, Cat = "Sword", Formula = 1 } },
        { 11, new WeaponMeta { Name = "BladeB", Flavor = "Tempered in salt and fury", Wp = 14, Cat = "Sword", Formula = 1 } },
        { 12, new WeaponMeta { Name = "BladeC", Flavor = "Old iron knows no mercy",  Wp = 10, Cat = "Sword", Formula = 1 } },
    };

    private static string ReadSlot(FakeHeap heap, long baseAddr, int pos, int len)
    {
        heap.TryReadBytes(baseAddr + pos, len, out var buf);
        return System.Text.Encoding.ASCII.GetString(buf);
    }

    // ─── C1: empty target set does not gate kills painting ────────────────────

    /// <summary>C1: with BOTH mirror slots empty (0 and 0xFFFF, both outside valid id range),
    /// no suffix target is built -- but kills counters for all cards must still be painted.
    /// Mutation: "if (targets.Count == 0) return;" would leave them blank.</summary>
    [Fact]
    public void C1_empty_target_set_kills_slots_still_painted()
    {
        var meta  = BuildMeta();
        // Distinct nonzero counts so asserted values differ from the baked "0   ".
        var kills = new Dictionary<int, int> { { 10, 7 }, { 11, 13 }, { 12, 5 } };
        var clock = new TestClock();

        var src = new byte[512];
        var cA = CardFixtures.WriteCard(src, 0,   "BladeA", "Forged under winter stars");
        var cB = CardFixtures.WriteCard(src, 150, "BladeB", "Tempered in salt and fury");
        var cC = CardFixtures.WriteCard(src, 300, "BladeC", "Old iron knows no mercy");

        // Both mirror slots empty: MirrorWeapon=0, MirrorOffHand=0xFFFF
        var statics = new byte[64];
        statics[0] = 0;    statics[1] = 0;       // MirrorWeapon = 0 (invalid)
        statics[2] = 0xFF; statics[3] = 0xFF;     // MirrorOffHand = 0xFFFF (invalid)
        statics[4] = 10;

        var heap = new FakeHeap((SourceBase, src), (StaticsBase, statics));
        var display = CardFixtures.MakeDisplay(meta, kills, heap, StaticsBase, clock);

        CardFixtures.DrainGeneration(display, clock, 600);

        // Kills slots must be painted with actual counts, not the initial unpainted placeholder.
        Assert.Equal(Signatures.KillsMeterSlot(7),  ReadSlot(heap, SourceBase, cA.killsSlotPos, Signatures.KillsMeterSlotChars));
        Assert.Equal(Signatures.KillsMeterSlot(13), ReadSlot(heap, SourceBase, cB.killsSlotPos, Signatures.KillsMeterSlotChars));
        Assert.Equal(Signatures.KillsMeterSlot(5),  ReadSlot(heap, SourceBase, cC.killsSlotPos, Signatures.KillsMeterSlotChars));
    }

    // ─── C2: budget wiring is honored (long.MaxValue mutation) ───────────────

    /// <summary>C2: a card placed in the last chunk of a (BudgetOutOfBattle * 3)-sized region
    /// must NOT be painted after a single Tick (budget caps progress), but must be painted
    /// after enough Ticks to exhaust the entire region.
    /// Mutation: budget = long.MaxValue lets one Tick walk the entire multi-gigabyte region.</summary>
    [Fact]
    public void C2_budget_caps_progress_card_in_last_chunk_not_painted_in_one_tick()
    {
        var meta  = BuildMeta();
        var kills = new Dictionary<int, int> { { 10, 7 }, { 11, 0 }, { 12, 0 } };
        var clock = new TestClock();

        // Budget = 16 MB out-of-battle; make a region 3x that so the last chunk is unreachable
        // in one tick.  Use ChunkSize (4 MB) per chunk so we have a predictable layout.
        const long Budget = 16L * 1024 * 1024;  // == BudgetOutOfBattle
        const int ChunkSize = DisplaySweep.ChunkSize;
        int regionChunks = (int)(Budget / ChunkSize) + 2; // > budget worth of chunks
        long regionSize = (long)regionChunks * ChunkSize;

        long bigBase = 0x90_0000_0000L;
        var bigData = new byte[regionSize];

        // Place a valid card for id 10 in the LAST chunk.
        long lastChunkOffset = (long)(regionChunks - 1) * ChunkSize;
        CardFixtures.WriteCard(bigData, (int)lastChunkOffset, "BladeA", "Forged under winter stars");
        // Also plant the "Kills: 0   " literal that WriteCard already writes (already there).

        var statics = new byte[64];
        statics[0] = 10; statics[1] = 0; // MirrorWeapon = 10
        statics[4] = 12;

        var heap = new FakeHeap((bigBase, bigData), (StaticsBase + 0x1_0000_0000L, statics));
        var wrapped = new OffsetRemapMem(heap,
            mirrorWeaponAddr:  StaticsBase + 0x1_0000_0000L,
            mirrorOffHandAddr: StaticsBase + 0x1_0000_0000L + 2,
            wpScratchAddr:     StaticsBase + 0x1_0000_0000L + 4);
        var display = new Display(meta, kills, wrapped, clock.Func);

        // ONE tick -- the card in the last chunk must NOT be painted yet.
        clock.Ms += DisplaySweep.HotRescanMs + 1;
        display.Tick(false);

        int meterWidth = Signatures.KillsMeterSlotChars;
        heap.TryReadBytes(bigBase + lastChunkOffset + (int)ByteScan.Ascii("BladeA").Length
                          + 2 /* suffix */ + 3 /* pad */
                          + (int)ByteScan.Ascii("Forged under winter stars").Length
                          + (int)ByteScan.Ascii("\n\nKills: ").Length,
                          meterWidth, out var afterOneTick);
        string afterOneTickStr = System.Text.Encoding.ASCII.GetString(afterOneTick);
        // One Tick must not have reached the last chunk (still the unpainted placeholder).
        Assert.Equal(Signatures.KillsMeterSlot(0), afterOneTickStr);

        // After enough Ticks the full region is exhausted and the card is painted.
        bool painted = false;
        for (int i = 0; i < regionChunks * 3 && !painted; i++)
        {
            clock.Ms += DisplaySweep.HotRescanMs + 1;
            display.Tick(false);

            int killsOff = (int)lastChunkOffset
                + (int)ByteScan.Ascii("BladeA").Length + 2 + 3
                + (int)ByteScan.Ascii("Forged under winter stars").Length
                + (int)ByteScan.Ascii("\n\nKills: ").Length;
            heap.TryReadBytes(bigBase + killsOff, meterWidth, out var slot);
            if (System.Text.Encoding.ASCII.GetString(slot) == Signatures.KillsMeterSlot(7))
                painted = true;
        }

        Assert.True(painted, "Card in last chunk must be painted once the budget-paced sweep reaches it");
    }

    // ─── C3: UTF-16LE end-to-end through Display.Tick ─────────────────────────

    /// <summary>C3: a card whose name, flavor, and "Kills: " are UTF-16LE encoded must have
    /// its digits painted with high bytes 0x00 (the UTF-16 digit representation).
    /// Mutation: dropping every Enc != 1 hit means UTF-16 cards are never discovered.</summary>
    [Fact]
    public void C3_utf16_card_painted_through_display_tick()
    {
        var meta  = BuildMeta();
        var kills = new Dictionary<int, int> { { 10, 9 }, { 11, 0 }, { 12, 0 } };
        var clock = new TestClock();

        // Build a UTF-16 card for weapon 10.
        var cardParts = new List<byte>();
        cardParts.AddRange(ByteScan.Utf16("BladeA"));
        cardParts.AddRange(ByteScan.Utf16("  "));       // suffix slot
        cardParts.AddRange(ByteScan.Utf16("   "));       // pad
        cardParts.AddRange(ByteScan.Utf16("Forged under winter stars"));
        cardParts.AddRange(ByteScan.Utf16("\n\nKills: "));
        int killsSlotByteOffset = cardParts.Count;
        cardParts.AddRange(ByteScan.Utf16(Signatures.KillsMeterSlot(0)));
        byte[] cardBytes = cardParts.ToArray();

        long utf16Base = 0x82_0000_0000L;
        var regionData = new byte[Math.Max(cardBytes.Length + 64, 512)];
        Array.Copy(cardBytes, 0, regionData, 0, cardBytes.Length);

        var statics = new byte[64];
        statics[0] = 10; statics[1] = 0;  // MirrorWeapon = 10
        statics[4] = 12;

        long staticsAddr = 0x83_0000_0000L;
        var heap = new FakeHeap((utf16Base, regionData), (staticsAddr, statics));
        var wrapped = new OffsetRemapMem(heap,
            mirrorWeaponAddr:  staticsAddr,
            mirrorOffHandAddr: staticsAddr + 2,
            wpScratchAddr:     staticsAddr + 4);
        var display = new Display(meta, kills, wrapped, clock.Func);

        CardFixtures.DrainGeneration(display, clock, 600);

        // Read the painted slot (22 bytes = Signatures.KillsMeterSlotChars UTF-16 chars).
        int byteWidth = Signatures.KillsMeterSlotChars * 2;
        bool ok = heap.TryReadBytes(utf16Base + killsSlotByteOffset, byteWidth, out var painted);
        Assert.True(ok, "Must be able to read the kills slot");

        // The UTF-16 encoding of the kills=9 meter body has high bytes 0x00 interleaved.
        byte[] expected = ByteScan.Utf16(Signatures.KillsMeterSlot(9));
        Assert.Equal(expected, painted);
    }

    // ─── C4: Invalidate restarts sweep from new address ───────────────────────

    /// <summary>C4: after a generation completes, remove the source region, add a replacement
    /// at a DIFFERENT address with fresh card text, call Invalidate(), and advance past
    /// GenerationMinGapMs.  The relocated card must get painted.
    /// Mutation: deleting _sweep.Invalidate() from Display.Invalidate leaves the sweep
    /// cursor pointing past the old region, so it never discovers the new one in that pass.</summary>
    [Fact]
    public void C4_invalidate_with_relocated_region_discovers_new_card()
    {
        var meta  = BuildMeta();
        var kills = new Dictionary<int, int> { { 10, 7 }, { 11, 0 }, { 12, 0 } };
        var clock = new TestClock();

        // Initial source: card for id 10 at SourceBase.
        var src = new byte[512];
        CardFixtures.WriteCard(src, 0, "BladeA", "Forged under winter stars");

        var statics = new byte[64];
        statics[0] = 10; statics[1] = 0;
        statics[4] = 12;

        var heap = new FakeHeap((SourceBase, src), (StaticsBase, statics));
        var display = CardFixtures.MakeDisplay(meta, kills, heap, StaticsBase, clock);

        // Complete the initial generation.
        CardFixtures.DrainGeneration(display, clock, 600);

        // Remove the original region; add a replacement at a completely different address.
        heap.RemoveRegion(SourceBase);
        long newBase = 0x84_0000_0000L;
        var newSrc = new byte[512];
        var newCard = CardFixtures.WriteCard(newSrc, 0, "BladeA", "Forged under winter stars");
        heap.AddRegion(newBase, newSrc, writable: true);

        // Call Display.Invalidate and advance past GenerationMinGapMs.
        display.Invalidate();
        clock.Ms += DisplaySweep.GenerationMinGapMs + 10;

        // Drive ticks: the new region must be found and the card painted.
        bool painted = false;
        for (int i = 0; i < 400 && !painted; i++)
        {
            clock.Ms += DisplaySweep.HotRescanMs + 1;
            display.Tick(false);

            heap.TryReadBytes(newBase + newCard.killsSlotPos, Signatures.KillsMeterSlotChars, out var slot);
            if (System.Text.Encoding.ASCII.GetString(slot) == Signatures.KillsMeterSlot(7))
                painted = true;
        }

        Assert.True(painted, "Relocated card must be discovered and painted after Invalidate + new region");
    }

    // ─── C5: rotation covers all non-target ids ───────────────────────────────

    /// <summary>C5: in a multi-card region with one target weapon, every non-target card id
    /// must eventually receive its tier suffix within a bounded number of Ticks.
    /// Verifies the rotation slice fully cycles (not just the first RotationSlice ids).</summary>
    [Fact]
    public void C5_rotation_covers_all_non_target_ids_eventually()
    {
        // Build 20 weapons; weapon 20 is the mirror target; 21-39 are non-targets.
        var meta = new Dictionary<int, WeaponMeta>();
        for (int id = 20; id < 40; id++)
            meta[id] = new WeaponMeta
            {
                Name    = $"Wep{id:D2}",
                Flavor  = $"Flavor for weapon number {id:D2} goes here",
                Wp      = 10,
                Cat     = "Sword",
                Formula = 1,
            };

        // Prod threshold[0] = 5; use 5 kills => tier 1, suffix "+".
        var kills = new Dictionary<int, int>();
        for (int id = 20; id < 40; id++) kills[id] = 5;

        const int CardStride = 128;
        var src = new byte[CardStride * 20 + 512];
        var cardMeta = new (int suffixPos, int killsSlotPos)[20];
        for (int i = 0; i < 20; i++)
        {
            int id = 20 + i;
            var (sp, _, ks) = CardFixtures.WriteCard(src, i * CardStride, $"Wep{id:D2}", $"Flavor for weapon number {id:D2} goes here");
            cardMeta[i] = (sp, ks);
        }

        long srcBase  = 0x85_0000_0000L;
        long statBase = 0x86_0000_0000L;
        var statics = new byte[64];
        statics[0] = 20; statics[1] = 0; // mirror = id 20

        var heap = new FakeHeap((srcBase, src), (statBase, statics));
        var wrapped = new OffsetRemapMem(heap,
            mirrorWeaponAddr:  statBase,
            mirrorOffHandAddr: statBase + 2,
            wpScratchAddr:     statBase + 4);
        var clock = new TestClock();
        var display = new Display(meta, kills, wrapped, clock.Func);

        // Run bounded Ticks.
        for (int i = 0; i < 1000; i++)
        {
            clock.Ms += DisplaySweep.HotRescanMs + 1;
            display.Tick(false);
        }

        // Every non-target id (21-39) must have its suffix slot painted to "+" (tier 1).
        byte[] plusAscii = ByteScan.Ascii("+");
        for (int i = 1; i < 20; i++)   // i=0 is the target (id 20), skip
        {
            heap.TryReadBytes(srcBase + cardMeta[i].suffixPos, 1, out var slot);
            Assert.Equal(plusAscii[0], slot[0]);
        }
    }

    // ─── C6: vacuous-assertion repairs ────────────────────────────────────────

    /// <summary>C6a: the Invalidate repaint test must assert ALL three kill slots and the suffix
    /// slot so a partial repaint (only cA) does not pass the test.
    /// Distinct nonzero counts on all weapons prevent "0   "-vacuous slot assertions.</summary>
    [Fact]
    public void C6a_invalidate_then_tick_repaints_all_three_slots()
    {
        var meta  = BuildMeta();
        // Distinct nonzero counts so none assert against the baked "0   ".
        var kills = new Dictionary<int, int> { { 10, 7 }, { 11, 13 }, { 12, 5 } };
        var clock = new TestClock();

        var src = new byte[512];
        var cA = CardFixtures.WriteCard(src, 0,   "BladeA", "Forged under winter stars");
        var cB = CardFixtures.WriteCard(src, 150, "BladeB", "Tempered in salt and fury");
        var cC = CardFixtures.WriteCard(src, 300, "BladeC", "Old iron knows no mercy");

        // Prod threshold[1] = 20; kills < 20 → tier 0; suffix = "  ".
        var statics = new byte[64];
        statics[0] = 10; statics[1] = 0;
        statics[4] = 12;

        var heap = new FakeHeap((SourceBase + 0x10_0000_0000L, src),
                                (StaticsBase + 0x10_0000_0000L, statics));
        var wrapped = new OffsetRemapMem(heap,
            mirrorWeaponAddr:  StaticsBase + 0x10_0000_0000L,
            mirrorOffHandAddr: StaticsBase + 0x10_0000_0000L + 2,
            wpScratchAddr:     StaticsBase + 0x10_0000_0000L + 4);
        var display = new Display(meta, kills, wrapped, clock.Func);
        CardFixtures.DrainGeneration(display, clock, 600);

        display.Invalidate();

        // Reset all slots to their initial unpainted placeholder and "  " values.
        long srcAddr = SourceBase + 0x10_0000_0000L;
        int meterWidth = Signatures.KillsMeterSlotChars;
        heap.WriteBytes(srcAddr + cA.killsSlotPos, ByteScan.Ascii(Signatures.KillsMeterSlot(0)));
        heap.WriteBytes(srcAddr + cB.killsSlotPos, ByteScan.Ascii(Signatures.KillsMeterSlot(0)));
        heap.WriteBytes(srcAddr + cC.killsSlotPos, ByteScan.Ascii(Signatures.KillsMeterSlot(0)));
        heap.WriteBytes(srcAddr + cA.suffixPos, ByteScan.Ascii("  "));
        heap.WriteBytes(srcAddr + cB.suffixPos, ByteScan.Ascii("  "));
        heap.WriteBytes(srcAddr + cC.suffixPos, ByteScan.Ascii("  "));

        clock.Ms += DisplaySweep.GenerationMinGapMs + 1;
        CardFixtures.DrainGeneration(display, clock, 600);

        // Assert ALL three kill slots repainted with their distinct nonzero counts.
        Assert.Equal(Signatures.KillsMeterSlot(7),  ReadSlot(heap, srcAddr, cA.killsSlotPos, meterWidth));
        Assert.Equal(Signatures.KillsMeterSlot(13), ReadSlot(heap, srcAddr, cB.killsSlotPos, meterWidth));
        Assert.Equal(Signatures.KillsMeterSlot(5),  ReadSlot(heap, srcAddr, cC.killsSlotPos, meterWidth));
        // Target (id 10, 7 kills < prod threshold[0]=5 -- wait, 7 >= 5 → tier 1 = "+").
        Assert.Equal("+ ", ReadSlot(heap, srcAddr, cA.suffixPos, 2));
    }

    /// <summary>C6b: tier-0 sub-threshold count: assert the kills slot shows the nonzero count
    /// AND the suffix slot stays "  " (distinguishes "not painted" from "painted correctly").</summary>
    [Fact]
    public void C6b_tier_zero_subthreshold_count_paints_count_and_blank_suffix()
    {
        var meta  = BuildMeta();
        // 3 kills < prod threshold[0]=5 → tier 0; kills slot must show "3   "; suffix "  ".
        var kills = new Dictionary<int, int> { { 10, 3 }, { 11, 0 }, { 12, 0 } };
        var clock = new TestClock();

        var src = new byte[512];
        var cA = CardFixtures.WriteCard(src, 0, "BladeA", "Forged under winter stars");

        var statics = new byte[64];
        statics[0] = 10; statics[1] = 0;
        statics[4] = 12;

        long srcBase  = 0x87_0000_0000L;
        long statBase = 0x88_0000_0000L;
        var heap = new FakeHeap((srcBase, src), (statBase, statics));
        var wrapped = new OffsetRemapMem(heap,
            mirrorWeaponAddr:  statBase,
            mirrorOffHandAddr: statBase + 2,
            wpScratchAddr:     statBase + 4);
        var display = new Display(meta, kills, wrapped, clock.Func);
        CardFixtures.DrainGeneration(display, clock, 600);

        // Count slot must show the kills=3 meter body (nonzero, so assertion is non-vacuous).
        Assert.Equal(Signatures.KillsMeterSlot(3), ReadSlot(heap, srcBase, cA.killsSlotPos, Signatures.KillsMeterSlotChars));
        // Suffix slot must remain "  " (tier 0, not tier 1's "+" ).
        Assert.Equal("  ", ReadSlot(heap, srcBase, cA.suffixPos, 2));
    }

    /// <summary>C6c: the FindKills_lookback test must assert its precondition unconditionally
    /// before touching the conditional assertions -- guards the test itself against a fixture
    /// that silently skips the interesting branch.</summary>
    [Fact]
    public void C6c_findkills_lookback_precondition_and_hit_asserted_unconditionally()
    {
        // This is a pure CardScanner test (no Display), but the gap is in the conditional
        // assertion structure.  Build a flavor that's within FlavorWindow and assert
        // unconditionally.
        var meta = CardScannerFixtures.BuildMetaMap((1, "Sword", "Sharp"));
        var pats = new CardPatterns(meta);

        var parts = new List<byte>();
        var flavorB  = ByteScan.Enc("Sharp", 1);
        var fillerB  = ByteScan.Enc(" stuff\n\n", 1);
        var killsB   = ByteScan.Enc("Kills: ", 1);
        var slotB    = ByteScan.Enc(Signatures.KillsMeterSlot(0), 1);

        parts.AddRange(flavorB);
        parts.AddRange(fillerB);
        int lookback = parts.Count;
        parts.AddRange(killsB);
        parts.AddRange(slotB);

        byte[] buf = parts.ToArray();
        int searchable = buf.Length - lookback;

        // Precondition: distance from end-of-lookback to flavor-start must be within FlavorWindow.
        // The flavor starts at buf[0], lookback=flavorB.Length + fillerB.Length.
        int dist = lookback - flavorB.Length;   // gap between flavor-end and search-window start
        // Assert the precondition directly so it's never silently skipped.
        Assert.True(dist <= CardScanner.FlavorWindow,
            "Fixture precondition violated: flavor is too far from the search window");

        var hits = new List<CardScanner.KillsHit>();
        CardScanner.FindKills(buf, lookback, searchable, pats, hits);

        // With the precondition met, hits must be non-empty and correctly attributed.
        Assert.NotEmpty(hits);
        Assert.Equal(1, hits[0].Id);
    }

    // ─── C7: hot-chunk eviction when over MaxHotChunks ───────────────────────

    /// <summary>C7: mark MaxHotChunks+1 chunks hot; the oldest-marked entry must be evicted
    /// to keep the set at MaxHotChunks.  Re-marking a chunk refreshes its timestamp so it
    /// survives a subsequent overflow instead of the newly-added chunk.</summary>
    [Fact]
    public void C7_hot_chunk_cap_evicts_oldest_and_remarking_refreshes_position()
    {
        int cap = DisplaySweep.MaxHotChunks;
        long chunkStride = DisplaySweep.ChunkSize;

        // Build cap+1 separate one-chunk regions.
        var regions = new List<(long, byte[])>();
        for (int i = 0; i <= cap; i++)
            regions.Add((0x50_0000_0000L + i * chunkStride, new byte[DisplaySweep.ChunkSize]));

        var heap = new FakeHeap(regions.ToArray());
        long now = 0;
        var sw = new DisplaySweep(heap, () => now);

        // Drain the background generation first so hot-phase calls are clean.
        now += DisplaySweep.GenerationRestMs + 1;
        sw.Tick(long.MaxValue, (_, _, _, _) => { });

        // Mark the first cap chunks hot in ascending time order.
        // Advance the clock by 1ms between marks so timestamps are distinct.
        var bases = regions.Select(r => r.Item1).ToList();
        long oldestBase = bases[0];
        for (int i = 0; i < cap; i++)
        {
            now += 1;
            sw.MarkHot(bases[i]);
        }
        // Snapshot the current hot-set count (should be cap).
        // We test the OBSERVABLE behavior: after marking cap+1, a hot pass must offer
        // exactly cap chunks (the oldest got evicted).

        // Mark the (cap+1)-th chunk: this must evict oldestBase (lowest timestamp).
        now += 1;
        sw.MarkHot(bases[cap]);

        // Collect all chunks offered by the next hot pass.
        var offered = new HashSet<long>();
        now += DisplaySweep.HotRescanMs + 1;
        sw.Tick(long.MaxValue, (buf, lb, s, bufBase) => offered.Add(bufBase + lb));

        // The oldest chunk must NOT be offered (it was evicted).
        Assert.DoesNotContain(oldestBase, offered);
        // The newest chunk MUST be offered (it was just added).
        Assert.Contains(bases[cap], offered);

        // Part B: re-mark the originally-oldest chunk before adding another new one.
        // The re-mark should refresh its timestamp so it survives the next overflow.
        now += 1;
        sw.MarkHot(oldestBase);   // refresh -- now it's the newest

        // Add yet another fresh chunk (cap+2, not in heap regions, but we can re-use one
        // of the existing ones with a fake address already in the heap).
        // Instead, add a new region and mark it:
        long extraBase = 0x50_0000_0000L + (cap + 1) * chunkStride;
        heap.AddRegion(extraBase, new byte[DisplaySweep.ChunkSize], writable: true);
        now += 1;
        sw.MarkHot(extraBase);   // this should evict the second-oldest, not oldestBase

        offered.Clear();
        now += DisplaySweep.HotRescanMs + 1;
        sw.Tick(long.MaxValue, (buf, lb, s, bufBase) => offered.Add(bufBase + lb));

        // After the re-mark, oldestBase must survive (it was refreshed to newest before the eviction).
        Assert.Contains(oldestBase, offered);
    }

    // ─── C8: mid-generation invalidate pacing ────────────────────────────────

    /// <summary>C8: on a multi-chunk region with small budgets, call Invalidate() mid-generation.
    /// (a) The in-progress pass continues to completion.
    /// (b) The next pass starts after GenerationMinGapMs from last start, not GenerationRestMs.
    /// (c) Generation increments exactly once for the post-invalidate pass.</summary>
    [Fact]
    public void C8_mid_generation_invalidate_respects_pacing()
    {
        // Three-chunk region so the background walk takes multiple Ticks.
        long regionBase = 0x60_0000_0000L;
        int regionSize = DisplaySweep.ChunkSize * 3;
        var heap = DisplaySweepFixtures.OneRegion(regionBase, regionSize);
        long now = 0;
        var sw = new DisplaySweep(heap, () => now);

        long genBefore = sw.Generation;
        int bgOffers = 0;

        // (a) Consume first chunk only (budget = one chunk) so the generation is in progress.
        sw.Tick(DisplaySweep.ChunkSize, (_, _, _, _) => bgOffers++);
        Assert.False(sw.IsComplete, "Generation must still be in progress after one chunk");

        // Call Invalidate() mid-generation.
        sw.Invalidate();

        // Advance only to mid-gap (less than GenerationMinGapMs from now).
        now += DisplaySweep.GenerationMinGapMs / 2;
        bgOffers = 0;
        sw.Tick(DisplaySweep.ChunkSize, (_, _, _, _) => bgOffers++);

        // (a) The in-progress pass must continue -- more background chunks offered.
        // (Invalidate set the pending flag but does NOT abort the running walk.)
        // Either the walk is still going (more chunks) OR it completed this tick.
        // Either way, the generation counter should NOT have reset yet (pending is honored
        // only when _genComplete, which requires the full region to be walked first).
        // We verify: Generation did not jump beyond genBefore + 1 prematurely.
        Assert.True(sw.Generation <= genBefore + 1,
            "Generation must not jump twice before the first pass completes");

        // Finish the current pass by draining with unlimited budget.
        int safetyLimit = 100;
        while (!sw.IsComplete && safetyLimit-- > 0)
        {
            now += 1;
            sw.Tick(long.MaxValue, (_, _, _, _) => { });
        }
        Assert.True(sw.IsComplete, "Pass must complete when given unlimited budget");
        long genAfterFirstPass = sw.Generation;

        // (b) The invalidate-pending flag should let a new generation start after
        // GenerationMinGapMs from START (measured from _genStartTime), not GenerationRestMs.
        // Advance to just past GenerationMinGapMs from the original start (t=0).
        now = DisplaySweep.GenerationMinGapMs + 1;
        bgOffers = 0;
        sw.Tick(DisplaySweep.ChunkSize, (_, _, _, _) => bgOffers++);
        Assert.True(bgOffers > 0,
            "Post-invalidate pass must start after GenerationMinGapMs, not GenerationRestMs");

        // (c) Generation incremented exactly once for the new pass.
        Assert.Equal(genAfterFirstPass + 1, sw.Generation);
    }

    // ─── C9: cross-chunk-boundary card through the real sweep+scanner+sites ──

    /// <summary>C9: a card whose flavor ends just before a chunk boundary and whose "Kills: "
    /// begins just after it must be discovered via the lookback-prefix path and the painted
    /// count must land at the correct absolute address.
    /// Drives the real Display.Tick (sweep + scanner + sites composition).</summary>
    [Fact]
    public void C9_cross_chunk_boundary_card_painted_at_correct_address()
    {
        var meta  = BuildMeta();
        var kills = new Dictionary<int, int> { { 10, 11 }, { 11, 0 }, { 12, 0 } };
        var clock = new TestClock();

        // Build a two-chunk region where the card straddles the chunk boundary.
        // ChunkSize = 4 MB.  We want flavor to end just before offset ChunkSize,
        // and "Kills: " to start just after it.
        // Strategy: fill chunk 0 with the name+suffix+pad+flavor right up to the boundary,
        // then put "\n\nKills: 0   " at the start of chunk 1.
        int chunkSize = DisplaySweep.ChunkSize;
        int regionSize = chunkSize * 2 + 512; // room for both chunks + slack
        long regionBase = 0x70_0000_0000L;
        var regionData = new byte[regionSize];

        string name   = "BladeA";
        string flavor = "Forged under winter stars";

        byte[] nameB   = ByteScan.Ascii(name);
        byte[] sufB    = ByteScan.Ascii("  ");
        byte[] padB    = ByteScan.Ascii("   ");
        byte[] flvB    = ByteScan.Ascii(flavor);
        byte[] nnB     = ByteScan.Ascii("\n\nKills: ");
        byte[] kB      = ByteScan.Ascii(Signatures.KillsMeterSlot(0));

        // Place flavor so it ends exactly at (chunkSize - 1), i.e. last byte of chunk 0.
        // flvB.Length bytes ending at chunkSize-1 means starting at chunkSize - flvB.Length.
        int flavorStart = chunkSize - flvB.Length;
        // The name+suffix+pad must precede it.
        int blockLen = nameB.Length + sufB.Length + padB.Length;
        int nameStart = flavorStart - blockLen;
        Assert.True(nameStart >= 0, "Fixture: not enough room in chunk 0 for name+suffix+pad+flavor");

        Array.Copy(nameB, 0, regionData, nameStart,  nameB.Length);
        int suffixOff = nameStart + nameB.Length;
        Array.Copy(sufB,  0, regionData, suffixOff,   sufB.Length);
        Array.Copy(padB,  0, regionData, suffixOff + sufB.Length, padB.Length);
        Array.Copy(flvB,  0, regionData, flavorStart, flvB.Length);
        // flavor ends at index chunkSize-1 (inclusive).

        // "Kills: " starts at the very beginning of chunk 1.
        int killsStart = chunkSize;
        Array.Copy(nnB, 0, regionData, killsStart, nnB.Length);
        int killsSlotOff = killsStart + nnB.Length;
        Array.Copy(kB, 0, regionData, killsSlotOff, kB.Length);

        long statBase = 0x71_0000_0000L;
        var statics = new byte[64];
        statics[0] = 10; statics[1] = 0; // MirrorWeapon = 10
        statics[4] = 12;

        var heap = new FakeHeap((regionBase, regionData), (statBase, statics));
        var wrapped = new OffsetRemapMem(heap,
            mirrorWeaponAddr:  statBase,
            mirrorOffHandAddr: statBase + 2,
            wpScratchAddr:     statBase + 4);
        var display = new Display(meta, kills, wrapped, clock.Func);

        // Drive Display.Tick for enough Ticks to cover both chunks.
        CardFixtures.DrainGeneration(display, clock, 600);

        // The kills slot lives at regionBase + killsSlotOff in the heap.
        heap.TryReadBytes(regionBase + killsSlotOff, Signatures.KillsMeterSlotChars, out var painted);
        string got = System.Text.Encoding.ASCII.GetString(painted);
        Assert.Equal(Signatures.KillsMeterSlot(11), got);
    }
}
