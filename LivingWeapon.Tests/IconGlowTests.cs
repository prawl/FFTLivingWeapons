using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-295 cycle B: IconGlow's stateful half (IconGlow.cs + IconGlow.Apply.cs) driven entirely
/// through <see cref="FakeIconGlowStore"/> -- an in-memory stand-in for glow_icons/manifest.json,
/// the deployed FFTIVC tree, and modded.pac. No real asset ever needs to exist for this file to
/// pass (the glow_icons bake is a separate, parallel arc). Every test passes a SYNCHRONOUS
/// runBackground (<c>a =&gt; a()</c>) unless it is specifically testing the in-flight-task guard
/// (U8), so the background splice completes before Tick() returns and assertions can read the
/// fake pac buffer immediately.
/// </summary>
public class IconGlowTests
{
    private static byte[] RandBytes(int seed, int len)
    {
        var bytes = new byte[len];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static byte[] BuildPac(int totalLen, params (int Offset, byte[] Bytes)[] placements)
    {
        var pac = new byte[totalLen];
        new Random(999999).NextBytes(pac);   // filler, so an un-placed region can never accidentally match a needle
        foreach (var (offset, bytes) in placements)
            Array.Copy(bytes, 0, pac, offset, bytes.Length);
        return pac;
    }

    private static string Sha1Hex(byte[] bytes)
    {
        using var sha1 = SHA1.Create();
        return Convert.ToHexString(sha1.ComputeHash(bytes)).ToLowerInvariant();
    }

    private static IconGlowEntry MakeEntry(int id, string surface, byte[] baseBytes, Dictionary<int, byte[]> variants)
    {
        var variantFiles = new Dictionary<string, string>();
        var variantSha1s = new Dictionary<string, string>();
        foreach (var kv in variants)
        {
            variantFiles[kv.Key.ToString()] = $"ei_{id:000}_{surface}_t{kv.Key}.tex";
            variantSha1s[kv.Key.ToString()] = Sha1Hex(kv.Value);
        }
        return new IconGlowEntry
        {
            Id = id,
            Surface = surface,
            BaseRel = $"data/enhanced/ui/icon/ei_{id:000}_{surface}.tex",
            Length = baseBytes.Length,
            BaseSha1 = Sha1Hex(baseBytes),
            Variants = variantFiles,
            VariantSha1s = variantSha1s,
        };
    }

    /// <summary>In-memory stand-in for every file IconGlow ever touches. WriteAt mutates
    /// <see cref="Pac"/> in place (like the real file) so a test can read the buffer back
    /// afterward and also records every call in <see cref="Writes"/> for exact offset/length
    /// assertions.</summary>
    private sealed class FakeIconGlowStore : IIconGlowStore
    {
        public IconGlowManifest? Manifest;
        public byte[]? Pac;
        public bool FailWrite;
        public bool FailReadPac;
        public readonly Dictionary<(int Id, string Surface), byte[]> BaseTex = new();
        public readonly Dictionary<(int Id, string Surface, int Tier), byte[]> Variants = new();
        public readonly List<(long Offset, byte[] Bytes)> Writes = new();

        public IconGlowManifest? ReadManifest() => Manifest;
        public byte[]? ReadBaseTex(IconGlowEntry entry) => BaseTex.TryGetValue((entry.Id, entry.Surface), out var b) ? b : null;
        public byte[]? ReadVariantTex(IconGlowEntry entry, int tier) => Variants.TryGetValue((entry.Id, entry.Surface, tier), out var b) ? b : null;
        public byte[]? ReadPac() => FailReadPac ? null : Pac;

        public bool WriteAt(long offset, byte[] bytes)
        {
            Writes.Add((offset, bytes));
            if (FailWrite || Pac == null || offset < 0 || offset + bytes.Length > Pac.Length) return false;
            Array.Copy(bytes, 0, Pac, offset, bytes.Length);
            return true;
        }
    }

    // ---- U4 (THE LOAD-BEARING TEST): splice writes the variant's exact bytes at the exact
    // stored offset, for BOTH surfaces a weapon id carries. A wrong offset or a wrong length here
    // corrupts a 64MB file the game reads back on its next list open. ----

    [Fact]
    public void Splice_WritesVariantBytesAtOffset_ExactLength()
    {
        var store = new FakeIconGlowStore();
        const int id = 42;
        byte[] cardBase = RandBytes(101, 24), cardT2 = RandBytes(102, 24);
        byte[] smallBase = RandBytes(201, 12), smallT2 = RandBytes(202, 12);

        var cardEntry = MakeEntry(id, "card", cardBase, new() { [2] = cardT2 });
        var smallEntry = MakeEntry(id, "small", smallBase, new() { [2] = smallT2 });
        store.Manifest = new IconGlowManifest { SchemaVersion = 1, Icons = new() { cardEntry, smallEntry } };
        store.BaseTex[(id, "card")] = cardBase;
        store.BaseTex[(id, "small")] = smallBase;
        store.Variants[(id, "card", 2)] = cardT2;
        store.Variants[(id, "small", 2)] = smallT2;

        const int cardOffset = 5000, smallOffset = 9000;
        store.Pac = BuildPac(20000, (cardOffset, cardBase), (smallOffset, smallBase));

        var kills = new Dictionary<int, int> { [id] = 10 };   // prod tier 2
        var icon = new IconGlow("m", kills, new[] { id }, store, a => a());

        icon.Tick();

        Assert.Equal(cardT2, store.Pac.Skip(cardOffset).Take(cardBase.Length).ToArray());
        Assert.Equal(smallT2, store.Pac.Skip(smallOffset).Take(smallBase.Length).ToArray());
        Assert.Contains(store.Writes, w => w.Offset == cardOffset && w.Bytes.SequenceEqual(cardT2));
        Assert.Contains(store.Writes, w => w.Offset == smallOffset && w.Bytes.SequenceEqual(smallT2));
        // nothing outside the two exact windows moved
        Assert.Equal(cardBase.Length, store.Writes.Single(w => w.Offset == cardOffset).Bytes.Length);
        Assert.Equal(smallBase.Length, store.Writes.Single(w => w.Offset == smallOffset).Bytes.Length);
    }

    // ---- U5: tier 0 splices BASE bytes back in -- the restore path a PlaythroughReset drives ----

    [Fact]
    public void TierZero_SplicesBaseBytes()
    {
        var store = new FakeIconGlowStore();
        const int id = 7;
        byte[] baseBytes = RandBytes(301, 16), t1 = RandBytes(302, 16);
        var entry = MakeEntry(id, "card", baseBytes, new() { [1] = t1 });
        store.Manifest = new IconGlowManifest { SchemaVersion = 1, Icons = new() { entry } };
        store.BaseTex[(id, "card")] = baseBytes;
        store.Variants[(id, "card", 1)] = t1;
        const int offset = 40;
        store.Pac = BuildPac(2000, (offset, baseBytes));

        var kills = new Dictionary<int, int> { [id] = 5 };   // tier 1
        var icon = new IconGlow("m", kills, new[] { id }, store, a => a());
        icon.Tick();
        Assert.Equal(t1, store.Pac.Skip(offset).Take(baseBytes.Length).ToArray());

        kills[id] = 0;   // PlaythroughReset clears the shared tally in place
        icon.Tick();
        Assert.Equal(baseBytes, store.Pac.Skip(offset).Take(baseBytes.Length).ToArray());
    }

    // ---- U6: a missing/malformed manifest stands the whole subsystem down, once, never throws ----

    [Fact]
    public void MissingManifest_WarnsOnce_StandsDown_NeverThrows()
    {
        var store = new FakeIconGlowStore();   // Manifest left null: "missing"
        var icon = new IconGlow("m", new Dictionary<int, int>(), Array.Empty<int>(), store, a => a());

        using var cap = LogCapture.Start();
        var ex = Record.Exception(() => { icon.Tick(); icon.Tick(); icon.Tick(); });

        Assert.Null(ex);
        Assert.Equal(1, cap.File.Count(l => l.Contains("IconGlow standing down")));
    }

    [Fact]
    public void UnsupportedSchemaVersion_StandsDown()
    {
        var store = new FakeIconGlowStore { Manifest = new IconGlowManifest { SchemaVersion = 2, Icons = new() } };
        var icon = new IconGlow("m", new Dictionary<int, int>(), Array.Empty<int>(), store, a => a());

        using var cap = LogCapture.Start();
        icon.Tick();

        Assert.Equal(1, cap.File.Count(l => l.Contains("IconGlow standing down") && l.Contains("schemaVersion")));
    }

    // ---- U7: a failed write degrades that icon, warns exactly once, and keeps retrying ----

    [Fact]
    public void FailedWrite_RetriesOnNextTrigger_WarnsOncePerIcon()
    {
        var store = new FakeIconGlowStore { FailWrite = true };
        const int id = 55;
        byte[] baseBytes = RandBytes(401, 10), t1 = RandBytes(402, 10);
        var entry = MakeEntry(id, "card", baseBytes, new() { [1] = t1 });
        store.Manifest = new IconGlowManifest { SchemaVersion = 1, Icons = new() { entry } };
        store.BaseTex[(id, "card")] = baseBytes;
        store.Variants[(id, "card", 1)] = t1;
        store.Pac = BuildPac(500, (50, baseBytes));

        var kills = new Dictionary<int, int> { [id] = 5 };
        var icon = new IconGlow("m", kills, new[] { id }, store, a => a());

        using var cap = LogCapture.Start();
        icon.Tick();
        icon.Tick();   // write never succeeded -> the diff is still nonempty, so it retries

        Assert.Equal(2, store.Writes.Count(w => w.Offset == 50));
        Assert.Equal(1, cap.File.Count(l => l.Contains($"icon {id}") && l.Contains("failed to write")));
    }

    // ---- U8: while a splice is (simulated) still in flight, a later Tick starts no second one ----

    [Fact]
    public void Tick_InFlightTask_DoesNotStartASecond()
    {
        var store = new FakeIconGlowStore();
        const int id = 9;
        byte[] baseBytes = RandBytes(501, 10), t1 = RandBytes(502, 10);
        var entry = MakeEntry(id, "card", baseBytes, new() { [1] = t1 });
        store.Manifest = new IconGlowManifest { SchemaVersion = 1, Icons = new() { entry } };
        store.BaseTex[(id, "card")] = baseBytes;
        store.Variants[(id, "card", 1)] = t1;
        store.Pac = BuildPac(500, (50, baseBytes));

        var kills = new Dictionary<int, int> { [id] = 5 };
        int starts = 0;
        // Captures the apply action but never invokes it -- stands in for a splice that is still
        // running on the background task when the next tick arrives.
        var icon = new IconGlow("m", kills, new[] { id }, store, _ => starts++);

        icon.Tick();
        icon.Tick();
        icon.Tick();

        Assert.Equal(1, starts);
    }

    // ---- U9: a manifest id outside the known weapon set is a stale bake -- rejected at load ----

    [Fact]
    public void ManifestIdOutsideWeaponSet_RejectedAtLoad()
    {
        var store = new FakeIconGlowStore();
        const int knownId = 3, staleId = 999;
        byte[] knownBase = RandBytes(601, 10), knownT1 = RandBytes(602, 10);
        byte[] staleBase = RandBytes(603, 10), staleT1 = RandBytes(604, 10);
        var knownEntry = MakeEntry(knownId, "card", knownBase, new() { [1] = knownT1 });
        var staleEntry = MakeEntry(staleId, "card", staleBase, new() { [1] = staleT1 });
        store.Manifest = new IconGlowManifest { SchemaVersion = 1, Icons = new() { knownEntry, staleEntry } };
        store.BaseTex[(knownId, "card")] = knownBase;
        store.Variants[(knownId, "card", 1)] = knownT1;
        store.BaseTex[(staleId, "card")] = staleBase;
        store.Variants[(staleId, "card", 1)] = staleT1;
        store.Pac = BuildPac(1000, (50, knownBase), (300, staleBase));

        // weaponIds carries ONLY knownId -- staleId is the stale-manifest defense's target.
        var kills = new Dictionary<int, int> { [knownId] = 5, [staleId] = 5 };
        var icon = new IconGlow("m", kills, new[] { knownId }, store, a => a());

        icon.Tick();

        Assert.Equal(knownT1, store.Pac.Skip(50).Take(knownBase.Length).ToArray());
        Assert.Equal(staleBase, store.Pac.Skip(300).Take(staleBase.Length).ToArray());   // untouched
        Assert.Empty(store.Writes.Where(w => w.Offset == 300));
    }

    // ---- U10: a second tier change reuses the offset found on the FIRST index build. An
    // implementation that re-searches would find the base needle GONE (tier 1's bytes replaced
    // it) and wrongly stand the icon down instead of advancing it to tier 3. ----

    [Fact]
    public void SecondSplice_ReusesIndexOffset()
    {
        var store = new FakeIconGlowStore();
        const int id = 11;
        byte[] baseBytes = RandBytes(701, 10), t1 = RandBytes(702, 10), t3 = RandBytes(703, 10);
        var entry = MakeEntry(id, "card", baseBytes, new() { [1] = t1, [3] = t3 });
        store.Manifest = new IconGlowManifest { SchemaVersion = 1, Icons = new() { entry } };
        store.BaseTex[(id, "card")] = baseBytes;
        store.Variants[(id, "card", 1)] = t1;
        store.Variants[(id, "card", 3)] = t3;
        const int offset = 77;
        store.Pac = BuildPac(1000, (offset, baseBytes));

        var kills = new Dictionary<int, int> { [id] = 5 };   // tier 1
        var icon = new IconGlow("m", kills, new[] { id }, store, a => a());
        icon.Tick();
        Assert.Equal(t1, store.Pac.Skip(offset).Take(baseBytes.Length).ToArray());

        kills[id] = 15;   // tier 3
        icon.Tick();

        Assert.Equal(t3, store.Pac.Skip(offset).Take(baseBytes.Length).ToArray());
    }

    // ---- U11: a stale bake (deployed base tex no longer matches the manifest's baked hash)
    // degrades the icon to unmanaged rather than splicing an old-body rim over new art ----

    [Fact]
    public void BaseSha1Mismatch_DegradesToUnmanaged_WarnsOnce()
    {
        var store = new FakeIconGlowStore();
        const int id = 13;
        byte[] deployedBase = RandBytes(801, 10);   // what is actually on disk
        byte[] bakedAgainst = RandBytes(802, 10);   // what the manifest's baseSha1 was computed from
        byte[] t1 = RandBytes(803, 10);
        var entry = MakeEntry(id, "card", bakedAgainst, new() { [1] = t1 });
        entry.Length = deployedBase.Length;
        store.Manifest = new IconGlowManifest { SchemaVersion = 1, Icons = new() { entry } };
        store.BaseTex[(id, "card")] = deployedBase;
        store.Variants[(id, "card", 1)] = t1;
        const int offset = 15;
        store.Pac = BuildPac(500, (offset, deployedBase));

        var kills = new Dictionary<int, int> { [id] = 5 };
        var icon = new IconGlow("m", kills, new[] { id }, store, a => a());

        using var cap = LogCapture.Start();
        icon.Tick();
        Assert.Equal(deployedBase, store.Pac.Skip(offset).Take(deployedBase.Length).ToArray());   // untouched
        Assert.Equal(1, cap.File.Count(l => l.Contains("stale glow bake")));

        icon.Tick();   // stays unmanaged, no repeat warn
        Assert.Equal(1, cap.File.Count(l => l.Contains("stale glow bake")));
    }
}
