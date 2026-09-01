using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-381 second half: <see cref="IconCacheHide"/> driven entirely through <see cref="FakeHeap"/>
/// -- no real game process anywhere. <see cref="MemBuilder"/> lays out a byte-exact fake heap
/// image (strings, parts entries, texture records) and hands back the absolute address of each
/// piece so assertions can check exactly which bytes moved. Field offsets/sizes mirror
/// IconCacheHide.Policy.cs's constants; a mismatch here would fail every real test, not just a
/// contrived one, so re-deriving them independently would buy nothing.
/// </summary>
public class IconCacheHideTests
{
    /// <summary>Appends a fake heap image and returns absolute addresses for each piece written.
    /// Every string is preceded by a NUL (the pass-1 start rule) unless explicitly noted.</summary>
    private sealed class MemBuilder
    {
        private readonly List<byte> _bytes = new();
        public long Base { get; }
        public MemBuilder(long baseAddr) { Base = baseAddr; _bytes.Add(0); }   // leading NUL: every real string gets one
        public long Addr => Base + _bytes.Count;

        private void AlignTo(int n) { while (Addr % n != 0) _bytes.Add(0xCC); }

        public long WriteCString(string s)
        {
            long addr = Addr;
            _bytes.AddRange(Encoding.ASCII.GetBytes(s));
            _bytes.Add(0);
            return addr;
        }

        /// <summary>A string with NO preceding NUL -- embedded inside an unrelated byte, for the
        /// pass-1 "must start after a NUL" rule (T3).</summary>
        public long WriteEmbeddedCString(byte precedingByte, string s)
        {
            _bytes.Add(precedingByte);
            long addr = Addr;
            _bytes.AddRange(Encoding.ASCII.GetBytes(s));
            _bytes.Add(0);
            return addr;
        }

        public long WritePartsEntry(long namePtr, uint id, ushort state)
        {
            AlignTo(8);
            long addr = Addr;
            var buf = new byte[IconCacheHidePolicy.PartsHeadSize];
            BitConverter.GetBytes(namePtr).CopyTo(buf, 0);
            BitConverter.GetBytes(id).CopyTo(buf, IconCacheHidePolicy.PartsIdOffset);
            BitConverter.GetBytes(state).CopyTo(buf, IconCacheHidePolicy.PartsStateOffset);
            _bytes.AddRange(buf);
            return addr;
        }

        public long WriteTexRecord(long namePtr, long size, uint hash)
        {
            AlignTo(8);
            long addr = Addr;
            var buf = new byte[IconCacheHidePolicy.TexHeadSize];
            BitConverter.GetBytes(namePtr).CopyTo(buf, 0);
            BitConverter.GetBytes(size).CopyTo(buf, IconCacheHidePolicy.TexSizeOffset);
            BitConverter.GetBytes(hash).CopyTo(buf, IconCacheHidePolicy.TexHashOffset);
            _bytes.AddRange(buf);
            return addr;
        }

        /// <summary>A bare pointer followed by PartsHeadSize-8 zero bytes -- a candidate whose
        /// surrounding "record" is garbage and must fail classification (T1's stray pointer).</summary>
        public long WriteStrayPointer(long target)
        {
            AlignTo(8);
            long addr = Addr;
            _bytes.AddRange(BitConverter.GetBytes(target));
            _bytes.AddRange(new byte[IconCacheHidePolicy.PartsHeadSize - 8]);
            return addr;
        }

        public void PadTo(long absoluteAddr)
        {
            while (Addr < absoluteAddr) _bytes.Add(0);
        }

        public byte[] ToArray() => _bytes.ToArray();
    }

    private static IconGlowEntry Entry(int id, string surface, int length) =>
        new() { Id = id, Surface = surface, Length = length };

    private static byte[] Head(FakeHeap heap, long addr, int len)
    {
        Assert.True(heap.TryReadBytes(addr, len, out var buf));
        return buf;
    }

    // ---- T1 (LOAD-BEARING): a full realistic fixture with real + decoy + stray candidates on
    // BOTH surfaces of one icon; only the real records get written, both surfaces end up Hidden. ----

    [Fact]
    public void Hide_WritesOnlyTheRealRecords_BothSurfacesHidden()
    {
        const int id = 22;
        var b = new MemBuilder(0x1_0000_0000);
        var needles = IconCacheHidePolicy.Needles(id);
        long cardUri = b.WriteCString(needles[0].Text);
        long cardTex = b.WriteCString(needles[1].Text);
        long smallUri = b.WriteCString(needles[2].Text);
        long smallTex = b.WriteCString(needles[3].Text);

        long cardPart1 = b.WritePartsEntry(cardUri, 0x380067, 0x0101);
        long cardPart2 = b.WritePartsEntry(cardUri, 0x2E0042, 0x0101);
        long cardPartDecoy = b.WritePartsEntry(cardUri, 0x123456, 0x0000);   // wrong state
        long smallPart1 = b.WritePartsEntry(smallUri, 0x380067, 0x0101);
        long smallPart2 = b.WritePartsEntry(smallUri, 0x2E0042, 0x0101);
        long strayPointer = b.WriteStrayPointer(smallUri);   // points at a real string, garbage around it

        long cardTexRecord = b.WriteTexRecord(cardTex, 51296, 0xAD4781AC);
        long smallTexRecord = b.WriteTexRecord(smallTex, 12384, 0x11112222);
        long cardTexDecoy = b.WriteTexRecord(cardTex, 51295, 0x33334444);   // wrong size (off by one)

        var heap = new FakeHeap((b.Base, b.ToArray()));
        var hider = new IconCacheHide(heap, () => false);
        var targets = new[]
        {
            new IconCacheTarget(id, "card", Entry(id, "card", 51296)),
            new IconCacheTarget(id, "small", Entry(id, "small", 12384)),
        };

        var result = hider.Hide(targets);

        Assert.Equal(HideOutcome.Hidden, result.Get(id, "card")!.Value.Outcome);
        Assert.Equal(HideOutcome.Hidden, result.Get(id, "small")!.Value.Outcome);

        // exactly 6 real records hidden -> 12 writes (name + id/hash per record), nothing else
        Assert.Equal(12, heap.Writes);

        foreach (var (addr, stringAddr) in new[] { (cardPart1, cardUri), (cardPart2, cardUri), (smallPart1, smallUri), (smallPart2, smallUri) })
        {
            var head = Head(heap, addr, IconCacheHidePolicy.PartsHeadSize);
            Assert.Equal(BitConverter.GetBytes(stringAddr + 1), head.Take(8));
            Assert.Equal(0u, BitConverter.ToUInt32(head, IconCacheHidePolicy.PartsIdOffset));
        }
        foreach (var (addr, stringAddr) in new[] { (cardTexRecord, cardTex), (smallTexRecord, smallTex) })
        {
            var head = Head(heap, addr, IconCacheHidePolicy.TexHeadSize);
            Assert.Equal(BitConverter.GetBytes(stringAddr + 1), head.Take(8));
            Assert.Equal(0u, BitConverter.ToUInt32(head, IconCacheHidePolicy.TexHashOffset));
        }

        // the decoys and the stray pointer are byte-for-byte untouched
        var original = b.ToArray();
        foreach (var addr in new[] { cardPartDecoy, cardTexDecoy, strayPointer })
        {
            int off = (int)(addr - b.Base);
            var untouched = Head(heap, addr, addr == strayPointer ? 8 : (addr == cardTexDecoy ? IconCacheHidePolicy.TexHeadSize : IconCacheHidePolicy.PartsHeadSize));
            var expected = original.Skip(off).Take(untouched.Length).ToArray();
            Assert.Equal(expected, untouched);
        }
    }

    // ---- T2 (COUPLING): a surface with clean parts candidates but NO texture record writes
    // nothing for that surface (TextureMissing); the sibling surface in the same call is fine. ----

    [Fact]
    public void Hide_Coupling_NoTextureRecord_LeavesPartsUntouched_SiblingSurfaceUnaffected()
    {
        const int id = 23;
        var b = new MemBuilder(0x2_0000_0000);
        var needles = IconCacheHidePolicy.Needles(id);
        long cardUri = b.WriteCString(needles[0].Text);
        long cardTex = b.WriteCString(needles[1].Text);
        long smallUri = b.WriteCString(needles[2].Text);
        // small's tex-path string is never even written -- nothing to point a texture record at.

        long cardPart = b.WritePartsEntry(cardUri, 0x380067, 0x0101);
        long cardTexRecord = b.WriteTexRecord(cardTex, 100, 0xAAAAAAAA);
        long smallPart1 = b.WritePartsEntry(smallUri, 0x380067, 0x0101);
        long smallPart2 = b.WritePartsEntry(smallUri, 0x2E0042, 0x0101);

        var heap = new FakeHeap((b.Base, b.ToArray()));
        var hider = new IconCacheHide(heap, () => false);
        var targets = new[]
        {
            new IconCacheTarget(id, "card", Entry(id, "card", 100)),
            new IconCacheTarget(id, "small", Entry(id, "small", 50)),
        };

        var result = hider.Hide(targets);

        Assert.Equal(HideOutcome.Hidden, result.Get(id, "card")!.Value.Outcome);
        var small = result.Get(id, "small")!.Value;
        Assert.Equal(HideOutcome.TextureMissing, small.Outcome);
        Assert.Equal(0, small.PartsHidden);
        Assert.Equal(0, small.TexHidden);

        // card's 1 parts entry + 1 texture record hid (4 writes); small's 2 parts entries untouched
        Assert.Equal(4, heap.Writes);
        var original = b.ToArray();
        foreach (var addr in new[] { smallPart1, smallPart2 })
        {
            int off = (int)(addr - b.Base);
            var untouched = Head(heap, addr, IconCacheHidePolicy.PartsHeadSize);
            Assert.Equal(original.Skip(off).Take(untouched.Length).ToArray(), untouched);
        }
    }

    // ---- T3: Needles/IsExpectedString pure behavior, plus the pass-1 "preceded by NUL" rule
    // exercised through the full sweep (a suffix-prefix match embedded mid-string never becomes
    // a candidate, even though its OWN bytes exactly equal a needle from that point forward). ----

    [Fact]
    public void Needles_PadsIdAndYieldsFourExactStrings()
    {
        var needles = IconCacheHidePolicy.Needles(7);
        Assert.Equal(4, needles.Count);
        Assert.Contains(needles, n => n.Text == "UITextureParts://ui/ffto/icon/equip_item/textureparts/ei_007_uitx" && n.Surface == "card" && n.Kind == NeedleKind.PartsUri);
        Assert.Contains(needles, n => n.Text == "ui/ffto/icon/equip_item/texture/ei_007_uitx.tex" && n.Surface == "card" && n.Kind == NeedleKind.TexPath);
        Assert.Contains(needles, n => n.Text == "UITextureParts://ui/ffto/icon/equip_item_s/textureparts/ei_s_007_uitx" && n.Surface == "small" && n.Kind == NeedleKind.PartsUri);
        Assert.Contains(needles, n => n.Text == "ui/ffto/icon/equip_item_s/texture/ei_s_007_uitx.tex" && n.Surface == "small" && n.Kind == NeedleKind.TexPath);
    }

    [Fact]
    public void IsExpectedString_RejectsALongerStringSharingTheNeedlesPrefix()
    {
        var targets = new[] { new IconCacheTarget(7, "card", Entry(7, "card", 10)) };
        var lookup = IconCacheHidePolicy.BuildNeedleLookup(targets);
        string real = IconCacheHidePolicy.Needles(7)[1].Text;   // the card tex path
        var longer = Encoding.ASCII.GetBytes(real + "EXTRA").Concat(new byte[] { 0 }).ToArray();

        Assert.Null(IconCacheHidePolicy.IsExpectedString(longer, lookup));
        // non-vacuity: the EXACT string still matches through the same lookup
        var exact = Encoding.ASCII.GetBytes(real).Concat(new byte[] { 0 }).ToArray();
        Assert.NotNull(IconCacheHidePolicy.IsExpectedString(exact, lookup));
    }

    [Fact]
    public void Hide_IgnoresAMidStringOccurrence_NoPrecedingNul()
    {
        const int id = 24;
        var b = new MemBuilder(0x3_0000_0000);
        var needles = IconCacheHidePolicy.Needles(id);
        // the card tex-path text appears, but embedded right after a '/' (no NUL before it) --
        // must never be treated as a real string start, even though the suffix from there on is
        // an exact needle match.
        b.WriteEmbeddedCString((byte)'/', needles[1].Text);

        var heap = new FakeHeap((b.Base, b.ToArray()));
        var hider = new IconCacheHide(heap, () => false);
        var result = hider.Hide(new[] { new IconCacheTarget(id, "card", Entry(id, "card", 10)) });

        Assert.Equal(HideOutcome.NothingResident, result.Get(id, "card")!.Value.Outcome);
        Assert.Equal(0, heap.Writes);
    }

    // ---- T4: a targeted read that changes between classification and the pre-write re-read
    // refuses the write instead of trusting stale classification. ----

    [Fact]
    public void Hide_RefusesWrite_WhenTargetedReadChangesBeforeRewrite()
    {
        const int id = 25;
        var b = new MemBuilder(0x4_0000_0000);
        var needles = IconCacheHidePolicy.Needles(id);
        long cardTex = b.WriteCString(needles[1].Text);
        long cardTexRecord = b.WriteTexRecord(cardTex, 100, 0xAAAAAAAA);

        var heap = new FakeHeap((b.Base, b.ToArray()));
        var original = Head(heap, cardTexRecord, IconCacheHidePolicy.TexHeadSize);
        var mutated = (byte[])original.Clone();
        BitConverter.GetBytes(999L).CopyTo(mutated, IconCacheHidePolicy.TexSizeOffset);   // no longer matches
        int call = 0;
        heap.ReadOverride = (addr, len) =>
        {
            if (addr != cardTexRecord || len != IconCacheHidePolicy.TexHeadSize) return null;
            call++;
            return call == 1 ? null : mutated;   // 1st (classification) read: normal; 2nd (re-verify): stale
        };

        var hider = new IconCacheHide(heap, () => false);
        var result = hider.Hide(new[] { new IconCacheTarget(id, "card", Entry(id, "card", 100)) });

        var card = result.Get(id, "card")!.Value;
        Assert.Equal(HideOutcome.Refused, card.Outcome);
        Assert.Equal(0, heap.Writes);
    }

    // ---- T5: genuinely nothing resident -- no candidates at all, no throw. ----

    [Fact]
    public void Hide_NothingResident_WhenNoCandidatesExistAtAll()
    {
        const int id = 26;
        var heap = new FakeHeap((0x5_0000_0000, new byte[4096]));
        var hider = new IconCacheHide(heap, () => false);

        var result = hider.Hide(new[]
        {
            new IconCacheTarget(id, "card", Entry(id, "card", 10)),
            new IconCacheTarget(id, "small", Entry(id, "small", 10)),
        });

        Assert.Equal(HideOutcome.NothingResident, result.Get(id, "card")!.Value.Outcome);
        Assert.Equal(HideOutcome.NothingResident, result.Get(id, "small")!.Value.Outcome);
        Assert.Equal(0, heap.Writes);
    }

    // ---- C8 (round 2, F9/A3-13): when pass 1 finds no string at all, pass 2 (the pointer sweep)
    // must be skipped entirely -- there is nothing for it to look up, so a second full Regions()
    // pass would just burn the cost for a launch that has not drawn this icon yet. ----

    [Fact]
    public void Hide_NothingResident_SkipsPointerSweep_OneRegionsCallTotal()
    {
        const int id = 34;
        var heap = new FakeHeap((0xB_0000_0000, new byte[4096]));
        var hider = new IconCacheHide(heap, () => false);

        var result = hider.Hide(new[]
        {
            new IconCacheTarget(id, "card", Entry(id, "card", 10)),
            new IconCacheTarget(id, "small", Entry(id, "small", 10)),
        });

        Assert.Equal(HideOutcome.NothingResident, result.Get(id, "card")!.Value.Outcome);
        Assert.Equal(HideOutcome.NothingResident, result.Get(id, "small")!.Value.Outcome);
        Assert.Equal(0, heap.Writes);
        Assert.Equal(1, heap.RegionsCalls);
    }

    // ---- T6: a string whose full text spans the 4MB chunk boundary (needs trail slack), with
    // its referencing record on the OTHER side of that boundary, is still found and hidden. ----

    [Fact]
    public void Hide_FindsRecordAcrossAChunkBoundary()
    {
        const int id = 27;
        const long chunkSize = 4 * 1024 * 1024;
        long baseAddr = 0x6_0000_0000;
        var b = new MemBuilder(baseAddr);
        var needles = IconCacheHidePolicy.Needles(id);
        string uri = needles[0].Text;   // card parts URI

        // A3-2 (round 2): land the string's FIRST character just 5 bytes before the boundary, so
        // even the NEEDLE PREFIX (30 bytes, "UITextureParts://ui/ffto/icon/") straddles the edge
        // -- not merely the string's longer tail. The old span search stopped exactly at the
        // boundary and could never assemble a prefix match that starts this close to it; only the
        // extended span (searching out to lookback+searchable+prefix.Length-1, covered by the
        // 4096-byte trail slack) finds it.
        b.PadTo(baseAddr + chunkSize - 5);
        long uriAddr = b.WriteCString(uri);
        Assert.True(uriAddr + uri.Length + 1 > baseAddr + chunkSize, "fixture bug: string must actually cross the boundary");

        // The referencing parts entry lands after the string -- past the boundary, in chunk 1's
        // own sweep range.
        long partAddr = b.WritePartsEntry(uriAddr, 0x380067, 0x0101);
        Assert.True(partAddr >= baseAddr + chunkSize, "fixture bug: the record must land in the NEXT chunk");

        // Pad the region out well past chunk 1's start so ChunkReader.ReadInRegion covers it.
        b.PadTo(baseAddr + chunkSize + 4096);

        var heap = new FakeHeap((baseAddr, b.ToArray()));
        var hider = new IconCacheHide(heap, () => false);
        var result = hider.Hide(new[] { new IconCacheTarget(id, "card", Entry(id, "card", 10)) });

        // No texture record exists in this fixture -- coupling means TextureMissing, but the
        // PartsCandidates count proves the record was actually FOUND across the boundary.
        var card = result.Get(id, "card")!.Value;
        Assert.Equal(HideOutcome.TextureMissing, card.Outcome);
        Assert.Equal(1, card.PartsCandidates);
    }

    // ---- T7: a batch of two ids costs exactly two Regions() calls total (one per pass), not
    // one per id. ----

    [Fact]
    public void Hide_BatchOfTwoIds_EnumeratesRegionsExactlyTwice()
    {
        const int idA = 28, idB = 29;
        var b = new MemBuilder(0x7_0000_0000);
        foreach (var id in new[] { idA, idB })
        {
            var needles = IconCacheHidePolicy.Needles(id);
            long uri = b.WriteCString(needles[0].Text);
            long tex = b.WriteCString(needles[1].Text);
            b.WritePartsEntry(uri, 0x380067, 0x0101);
            b.WriteTexRecord(tex, 10, 0xAAAAAAAA);
        }

        var heap = new FakeHeap((b.Base, b.ToArray()));
        var hider = new IconCacheHide(heap, () => false);
        hider.Hide(new[]
        {
            new IconCacheTarget(idA, "card", Entry(idA, "card", 10)),
            new IconCacheTarget(idB, "card", Entry(idB, "card", 10)),
        });

        Assert.Equal(2, heap.RegionsCalls);
    }

    // ---- T8: a write that fails IGameMemory.Writable (a TOCTOU protection change) refuses
    // instead of writing. ----

    [Fact]
    public void Hide_RefusesWrite_WhenRegionIsNotWritableAtWriteTime()
    {
        const int id = 30;
        var b = new MemBuilder(0x8_0000_0000);
        var needles = IconCacheHidePolicy.Needles(id);
        long tex = b.WriteCString(needles[1].Text);
        b.WriteTexRecord(tex, 10, 0xAAAAAAAA);

        var heap = new FakeHeap((b.Base, b.ToArray()));
        heap.WritableOverride = (_, _) => false;
        var hider = new IconCacheHide(heap, () => false);

        var result = hider.Hide(new[] { new IconCacheTarget(id, "card", Entry(id, "card", 10)) });

        Assert.Equal(HideOutcome.Refused, result.Get(id, "card")!.Value.Outcome);
        Assert.Equal(0, heap.Writes);
    }

    // ---- C7 (round 2, F7): a texture record that CLASSIFIES but whose write is refused (the
    // region stops being Writable at write time) must leave the surface's parts entries
    // completely untouched -- the coupling gate (texHidden > 0) has to hold for a write refusal,
    // not just for a missing/decoy record (T2 already covers that case). Non-vacuity: relaxing
    // the gate to `texHidden >= 0` -- the same break round 1's verify used -- makes the parts
    // entry get hidden anyway and this test fails; that break-and-restore is recorded in the
    // session's silent-repair ledger rather than left in the tree. ----

    [Fact]
    public void Hide_RefusedTextureWrite_LeavesPartsEntriesUntouched()
    {
        const int id = 33;
        var b = new MemBuilder(0xA_0000_0000);
        var needles = IconCacheHidePolicy.Needles(id);
        long cardUri = b.WriteCString(needles[0].Text);
        long cardTex = b.WriteCString(needles[1].Text);

        long cardPart = b.WritePartsEntry(cardUri, 0x380067, 0x0101);
        long cardTexRecord = b.WriteTexRecord(cardTex, 10, 0xAAAAAAAA);

        var heap = new FakeHeap((b.Base, b.ToArray()));
        // Classifies fine; only the TEXTURE record's own two write addresses refuse (the parts
        // entry stays writable) -- a blanket "nothing is writable" override would make a broken
        // coupling gate look correct too, since the parts write would refuse on its own merits.
        heap.WritableOverride = (addr, len) => addr != cardTexRecord && addr != cardTexRecord + IconCacheHidePolicy.TexHashOffset;
        var hider = new IconCacheHide(heap, () => false);

        var result = hider.Hide(new[] { new IconCacheTarget(id, "card", Entry(id, "card", 10)) });

        var card = result.Get(id, "card")!.Value;
        Assert.Equal(HideOutcome.Refused, card.Outcome);
        Assert.Equal(0, card.PartsHidden);
        Assert.Equal(0, card.TexHidden);
        Assert.Equal(0, heap.Writes);

        var original = b.ToArray();
        foreach (var addr in new[] { cardPart, cardTexRecord })
        {
            int off = (int)(addr - b.Base);
            int len = addr == cardPart ? IconCacheHidePolicy.PartsHeadSize : IconCacheHidePolicy.TexHeadSize;
            var untouched = Head(heap, addr, len);
            Assert.Equal(original.Skip(off).Take(len).ToArray(), untouched);
        }
    }

    // ---- C12 (round 3, A4-2/PD-2): a texture record hides fine but BOTH of its surface's parts
    // entries refuse (an address-selective WritableOverride hitting only the parts entries, the
    // texture record's own two write addresses stay writable) -- the outcome still counts as
    // Hidden (the coupling rule only needs texHidden > 0), but the summary line must NOT promise a
    // redraw: the parts entries still point at the old texture handle. Non-vacuity: reverting
    // SummaryLine's Hidden branch to the single always-redraw sentence makes this assertion fail
    // (recorded RED in the session's report before the two-branch fix went in). ----

    [Fact]
    public void Hide_TextureHiddenBothPartsRefused_HiddenWithoutPromisingRedraw()
    {
        const int id = 41;
        var b = new MemBuilder(0xD_0000_0000);
        var needles = IconCacheHidePolicy.Needles(id);
        long cardUri = b.WriteCString(needles[0].Text);
        long cardTex = b.WriteCString(needles[1].Text);

        long cardPart1 = b.WritePartsEntry(cardUri, 0x380067, 0x0101);
        long cardPart2 = b.WritePartsEntry(cardUri, 0x2E0042, 0x0101);
        long cardTexRecord = b.WriteTexRecord(cardTex, 10, 0xAAAAAAAA);

        var heap = new FakeHeap((b.Base, b.ToArray()));
        // Address-selective: ONLY the two parts entries' own write addresses refuse. The texture
        // record's write addresses (and everything else) stay writable, so it hides cleanly.
        var partsWriteAddrs = new HashSet<long>
        {
            cardPart1, cardPart1 + IconCacheHidePolicy.PartsIdOffset,
            cardPart2, cardPart2 + IconCacheHidePolicy.PartsIdOffset,
        };
        heap.WritableOverride = (addr, len) => !partsWriteAddrs.Contains(addr);
        var hider = new IconCacheHide(heap, () => false);

        var result = hider.Hide(new[] { new IconCacheTarget(id, "card", Entry(id, "card", 10)) });

        var card = result.Get(id, "card")!.Value;
        Assert.Equal(HideOutcome.Hidden, card.Outcome);
        Assert.Equal(0, card.PartsHidden);
        Assert.Equal(2, card.PartsCandidates);
        Assert.Equal(1, card.TexHidden);
        Assert.Equal(2, heap.Writes);   // the texture's own name+hash writes only -- zero parts writes

        string line = IconCacheHidePolicy.SummaryLine(id, "card", card);
        Assert.Contains("the texture is fresh but its menu records were not", line);
        Assert.DoesNotContain("the next menu open redraws it", line);
    }

    // ---- C14 (round 3, A4-6/N1): the pass-1 extended span (A3-2) can read up to PrefixMax - 1
    // bytes past a chunk's own searchable window (the span extension) plus CStringMax bytes (the
    // forward string read) into ChunkReader's own trail slack -- ChunkReader.cs zero-clears past a
    // short region-end read (ChunkReader.ZeroClearLength, which the policy constant
    // ChunkReaderZeroClearLength now reads directly, so this test pins the reader itself) so that
    // reach only ever lands on deterministic zero, never a stale byte from a longer previous read.
    // This is what makes a future shrink of ChunkReader's clear length fail HERE instead of
    // silently matching stale bytes. Non-vacuity: the assertion is a real inequality, not a tautology
    // -- shrinking ChunkReaderZeroClearLength below PrefixMax + CStringMax fails it (recorded RED
    // in the session's report by trying exactly that before restoring the real 512). ----

    [Fact]
    public void ChunkReaderZeroClearLength_CoversPrefixMaxPlusCStringMax()
    {
        Assert.True(IconCacheHidePolicy.ChunkReaderZeroClearLength >=
            IconCacheHidePolicy.PrefixMax + IconCacheHidePolicy.CStringMax);
    }

    // ---- T9: in-battle stops the sweep before it starts -- Postponed, no byte read, no write. ----

    [Fact]
    public void Hide_InBattleFromTheStart_Postpones_ReadsNothing_WritesNothing()
    {
        const int id = 31;
        var b = new MemBuilder(0x9_0000_0000);
        var needles = IconCacheHidePolicy.Needles(id);
        long tex = b.WriteCString(needles[1].Text);
        b.WriteTexRecord(tex, 10, 0xAAAAAAAA);

        var heap = new FakeHeap((b.Base, b.ToArray()));
        var hider = new IconCacheHide(heap, () => true);

        var result = hider.Hide(new[]
        {
            new IconCacheTarget(id, "card", Entry(id, "card", 10)),
            new IconCacheTarget(id, "small", Entry(id, "small", 10)),
        });

        Assert.Equal(HideOutcome.Postponed, result.Get(id, "card")!.Value.Outcome);
        Assert.Equal(HideOutcome.Postponed, result.Get(id, "small")!.Value.Outcome);
        Assert.Equal(0, heap.Writes);
        Assert.Equal(0, heap.ReadCalls);
    }
}
