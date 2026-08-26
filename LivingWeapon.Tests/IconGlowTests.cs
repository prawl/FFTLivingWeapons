using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-336: IconGlow's stateful half (IconGlow.cs + IconGlow.Apply.cs) driven entirely through
/// <see cref="FakeIconGlowStore"/> -- an in-memory stand-in for glow_icons/manifest.json, the
/// deployed FFTIVC tree, and the base-backup snapshot store. No real asset ever needs to exist
/// for this file to pass (the glow_icons bake is a separate, parallel arc). Every test passes a
/// SYNCHRONOUS runBackground (<c>a =&gt; a()</c>) unless it is specifically testing the
/// in-flight-task guard, so the background apply completes before Tick() returns and assertions
/// can read the fake deployed-tex dictionary immediately.
///
/// modded.pac is gone from this whole file: owner-witnessed 2026-08-26 killed that display path
/// (icon textures cache at first draw; mid-session pac writes never show), so the runtime now
/// writes the DEPLOYED loose tex directly -- the file the launch merge re-reads next boot.
/// </summary>
public class IconGlowTests
{
    private static byte[] RandBytes(int seed, int len)
    {
        var bytes = new byte[len];
        new Random(seed).NextBytes(bytes);
        return bytes;
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

    /// <summary>In-memory stand-in for every file IconGlow ever touches: the deployed FFTIVC
    /// tree (<see cref="Deployed"/>), the base-backup snapshot store (<see cref="Backup"/>), and
    /// the baked glow_icons variant files (<see cref="Variants"/>). Per-(id,surface) fail
    /// toggles let a test force one surface's write to fail while its sibling still succeeds
    /// (the partial-failure retry contract). Every write call is logged in
    /// <see cref="DeployedWrites"/>/<see cref="BackupWrites"/> BEFORE the fail toggle is
    /// consulted, mirroring the production store's own never-throw contract.</summary>
    private sealed class FakeIconGlowStore : IIconGlowStore
    {
        public IconGlowManifest? Manifest;
        public readonly Dictionary<(int Id, string Surface), byte[]> Deployed = new();
        public readonly Dictionary<(int Id, string Surface), byte[]> Backup = new();
        public readonly Dictionary<(int Id, string Surface, int Tier), byte[]> Variants = new();
        public readonly HashSet<(int Id, string Surface)> FailDeployedWrite = new();
        public readonly HashSet<(int Id, string Surface)> FailBackupWrite = new();
        public readonly List<(int Id, string Surface, byte[] Bytes)> DeployedWrites = new();
        public readonly List<(int Id, string Surface, byte[] Bytes)> BackupWrites = new();

        public IconGlowManifest? ReadManifest() => Manifest;
        public byte[]? ReadDeployedTex(IconGlowEntry entry) => Deployed.TryGetValue((entry.Id, entry.Surface), out var b) ? b : null;
        public byte[]? ReadVariantTex(IconGlowEntry entry, int tier) => Variants.TryGetValue((entry.Id, entry.Surface, tier), out var b) ? b : null;
        public byte[]? ReadBaseBackup(IconGlowEntry entry) => Backup.TryGetValue((entry.Id, entry.Surface), out var b) ? b : null;

        public bool WriteDeployedTex(IconGlowEntry entry, byte[] bytes)
        {
            DeployedWrites.Add((entry.Id, entry.Surface, bytes));
            if (FailDeployedWrite.Contains((entry.Id, entry.Surface))) return false;
            Deployed[(entry.Id, entry.Surface)] = bytes;
            return true;
        }

        public bool WriteBaseBackup(IconGlowEntry entry, byte[] bytes)
        {
            BackupWrites.Add((entry.Id, entry.Surface, bytes));
            if (FailBackupWrite.Contains((entry.Id, entry.Surface))) return false;
            Backup[(entry.Id, entry.Surface)] = bytes;
            return true;
        }
    }

    // ---- ApplyId writes the variant's exact bytes to the deployed tex for BOTH surfaces a
    // weapon id carries, from a pristine starting point (also exercises the first-touch
    // snapshot). ----

    [Fact]
    public void ApplyId_WritesVariantBytes_ForBothSurfaces()
    {
        var store = new FakeIconGlowStore();
        const int id = 42;
        byte[] cardBase = RandBytes(101, 24), cardT2 = RandBytes(102, 24);
        byte[] smallBase = RandBytes(201, 12), smallT2 = RandBytes(202, 12);

        var cardEntry = MakeEntry(id, "card", cardBase, new() { [2] = cardT2 });
        var smallEntry = MakeEntry(id, "small", smallBase, new() { [2] = smallT2 });
        store.Manifest = new IconGlowManifest { SchemaVersion = 1, Icons = new() { cardEntry, smallEntry } };
        store.Deployed[(id, "card")] = cardBase;
        store.Deployed[(id, "small")] = smallBase;
        store.Variants[(id, "card", 2)] = cardT2;
        store.Variants[(id, "small", 2)] = smallT2;

        var kills = new Dictionary<int, int> { [id] = 10 };   // prod tier 2
        var icon = new IconGlow("m", kills, new[] { id }, store, a => a());

        icon.Tick();

        Assert.Equal(cardT2, store.Deployed[(id, "card")]);
        Assert.Equal(smallT2, store.Deployed[(id, "small")]);
        Assert.Contains(store.DeployedWrites, w => w.Id == id && w.Surface == "card" && w.Bytes.SequenceEqual(cardT2));
        Assert.Contains(store.DeployedWrites, w => w.Id == id && w.Surface == "small" && w.Bytes.SequenceEqual(smallT2));
        // the pristine base got snapshotted before the overwrite
        Assert.Equal(cardBase, store.Backup[(id, "card")]);
        Assert.Equal(smallBase, store.Backup[(id, "small")]);
    }

    // ---- tier 0 restores base bytes via the earlier snapshot -- the restore path a
    // PlaythroughReset drives ----

    [Fact]
    public void TierZero_ReturnsToBase_AfterEarlierTierApply()
    {
        var store = new FakeIconGlowStore();
        const int id = 7;
        byte[] baseBytes = RandBytes(301, 16), t1 = RandBytes(302, 16);
        var entry = MakeEntry(id, "card", baseBytes, new() { [1] = t1 });
        store.Manifest = new IconGlowManifest { SchemaVersion = 1, Icons = new() { entry } };
        store.Deployed[(id, "card")] = baseBytes;   // pristine at first touch
        store.Variants[(id, "card", 1)] = t1;

        var kills = new Dictionary<int, int> { [id] = 5 };   // tier 1
        var icon = new IconGlow("m", kills, new[] { id }, store, a => a());
        icon.Tick();
        Assert.Equal(t1, store.Deployed[(id, "card")]);
        Assert.Equal(baseBytes, store.Backup[(id, "card")]);   // snapshotted on the way up

        kills[id] = 0;   // PlaythroughReset clears the shared tally in place
        icon.Tick();
        Assert.Equal(baseBytes, store.Deployed[(id, "card")]);
    }

    // ---- a missing/malformed manifest stands the whole subsystem down, once, never throws ----

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

    // ---- a failed deployed-tex write degrades that icon, warns exactly once, and keeps
    // retrying ----

    [Fact]
    public void FailedWrite_RetriesOnNextTrigger_WarnsOncePerIcon()
    {
        var store = new FakeIconGlowStore();
        const int id = 55;
        byte[] baseBytes = RandBytes(401, 10), t1 = RandBytes(402, 10);
        var entry = MakeEntry(id, "card", baseBytes, new() { [1] = t1 });
        store.Manifest = new IconGlowManifest { SchemaVersion = 1, Icons = new() { entry } };
        store.Deployed[(id, "card")] = baseBytes;
        store.Variants[(id, "card", 1)] = t1;
        store.FailDeployedWrite.Add((id, "card"));

        var kills = new Dictionary<int, int> { [id] = 5 };
        var icon = new IconGlow("m", kills, new[] { id }, store, a => a());

        using var cap = LogCapture.Start();
        icon.Tick();
        icon.Tick();   // write never succeeded -> the diff is still nonempty, so it retries

        Assert.Equal(2, store.DeployedWrites.Count(w => w.Id == id && w.Surface == "card"));
        Assert.Equal(1, cap.File.Count(l => l.Contains($"icon {id}") && l.Contains("failed to write")));
    }

    // ---- while a splice is (simulated) still in flight, a later Tick starts no second one ----

    [Fact]
    public void Tick_InFlightTask_DoesNotStartASecond()
    {
        var store = new FakeIconGlowStore();
        const int id = 9;
        byte[] baseBytes = RandBytes(501, 10), t1 = RandBytes(502, 10);
        var entry = MakeEntry(id, "card", baseBytes, new() { [1] = t1 });
        store.Manifest = new IconGlowManifest { SchemaVersion = 1, Icons = new() { entry } };
        store.Deployed[(id, "card")] = baseBytes;
        store.Variants[(id, "card", 1)] = t1;

        var kills = new Dictionary<int, int> { [id] = 5 };
        int starts = 0;
        // Captures the apply action but never invokes it -- stands in for an apply that is
        // still running on the background task when the next tick arrives.
        var icon = new IconGlow("m", kills, new[] { id }, store, _ => starts++);

        icon.Tick();
        icon.Tick();
        icon.Tick();

        Assert.Equal(1, starts);
    }

    // ---- a manifest id outside the known weapon set is a stale bake -- rejected at load ----

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
        store.Deployed[(knownId, "card")] = knownBase;
        store.Variants[(knownId, "card", 1)] = knownT1;
        store.Deployed[(staleId, "card")] = staleBase;
        store.Variants[(staleId, "card", 1)] = staleT1;

        // weaponIds carries ONLY knownId -- staleId is the stale-manifest defense's target.
        var kills = new Dictionary<int, int> { [knownId] = 5, [staleId] = 5 };
        var icon = new IconGlow("m", kills, new[] { knownId }, store, a => a());

        icon.Tick();

        Assert.Equal(knownT1, store.Deployed[(knownId, "card")]);
        Assert.Equal(staleBase, store.Deployed[(staleId, "card")]);   // untouched
        Assert.Empty(store.DeployedWrites.Where(w => w.Id == staleId));
    }

    // ---- a second tier change on a later tick correctly advances to the new variant ----

    [Fact]
    public void SecondTierChange_AdvancesToNewVariant()
    {
        var store = new FakeIconGlowStore();
        const int id = 11;
        byte[] baseBytes = RandBytes(701, 10), t1 = RandBytes(702, 10), t3 = RandBytes(703, 10);
        var entry = MakeEntry(id, "card", baseBytes, new() { [1] = t1, [3] = t3 });
        store.Manifest = new IconGlowManifest { SchemaVersion = 1, Icons = new() { entry } };
        store.Deployed[(id, "card")] = baseBytes;
        store.Variants[(id, "card", 1)] = t1;
        store.Variants[(id, "card", 3)] = t3;

        var kills = new Dictionary<int, int> { [id] = 5 };   // tier 1
        var icon = new IconGlow("m", kills, new[] { id }, store, a => a());
        icon.Tick();
        Assert.Equal(t1, store.Deployed[(id, "card")]);

        kills[id] = 15;   // tier 3
        icon.Tick();

        Assert.Equal(t3, store.Deployed[(id, "card")]);
    }

    // ---- a deployed tex that matches neither the manifest's baked base nor any variant hash
    // (a stale bake, or someone else's art) degrades the icon to unmanaged rather than splicing
    // over it ----

    [Fact]
    public void DeployedTexMatchesNoKnownHash_DegradesToUnmanaged_WarnsOnce()
    {
        var store = new FakeIconGlowStore();
        const int id = 13;
        byte[] deployedBase = RandBytes(801, 10);   // what is actually on disk
        byte[] bakedAgainst = RandBytes(802, 10);   // what the manifest's baseSha1 was computed from
        byte[] t1 = RandBytes(803, 10);
        var entry = MakeEntry(id, "card", bakedAgainst, new() { [1] = t1 });
        entry.Length = deployedBase.Length;
        store.Manifest = new IconGlowManifest { SchemaVersion = 1, Icons = new() { entry } };
        store.Deployed[(id, "card")] = deployedBase;
        store.Variants[(id, "card", 1)] = t1;

        var kills = new Dictionary<int, int> { [id] = 5 };
        var icon = new IconGlow("m", kills, new[] { id }, store, a => a());

        using var cap = LogCapture.Start();
        icon.Tick();
        Assert.Equal(deployedBase, store.Deployed[(id, "card")]);   // untouched
        Assert.Equal(1, cap.File.Count(l => l.Contains("foreign art")));

        icon.Tick();   // stays unmanaged, no repeat warn
        Assert.Equal(1, cap.File.Count(l => l.Contains("foreign art")));
    }

    // ---- LOAD-BEARING: when the deployed tex already holds the exact variant bytes for the
    // desired tier (a prior session, or a pre-tiered install), the judge seeds _applied from
    // that fact and NO write happens -- pins the fix for the LW-334 interim script having
    // pre-tiered files that the old pac-splice path would otherwise have stood down as
    // unmanaged (never having a plain base to index against). ----

    [Fact]
    public void Judge_SeedsAppliedFromDeployedVariant()
    {
        var store = new FakeIconGlowStore();
        const int id = 21;
        byte[] cardBase = RandBytes(901, 14), cardT3 = RandBytes(902, 14);
        byte[] smallBase = RandBytes(903, 8), smallT3 = RandBytes(904, 8);
        var cardEntry = MakeEntry(id, "card", cardBase, new() { [3] = cardT3 });
        var smallEntry = MakeEntry(id, "small", smallBase, new() { [3] = smallT3 });
        store.Manifest = new IconGlowManifest { SchemaVersion = 1, Icons = new() { cardEntry, smallEntry } };
        store.Deployed[(id, "card")] = cardT3;     // already at tier 3 on both surfaces
        store.Deployed[(id, "small")] = smallT3;

        var kills = new Dictionary<int, int> { [id] = 15 };   // tier 3
        var icon = new IconGlow("m", kills, new[] { id }, store, a => a());

        using var cap = LogCapture.Start();
        icon.Tick();

        Assert.Empty(store.DeployedWrites);
        Assert.Empty(store.BackupWrites);   // neither surface was pristine, nothing to snapshot
        Assert.DoesNotContain(cap.File, l => l.Contains("leaves icon 21 plain"));

        icon.Tick();   // desired already matches the seeded applied tier -> no further work
        Assert.Empty(store.DeployedWrites);
    }

    [Fact]
    public void Judge_PristineSnapshotsThenApplies()
    {
        var store = new FakeIconGlowStore();
        const int id = 22;
        byte[] baseBytes = RandBytes(1001, 10), t2 = RandBytes(1002, 10);
        var entry = MakeEntry(id, "card", baseBytes, new() { [2] = t2 });
        store.Manifest = new IconGlowManifest { SchemaVersion = 1, Icons = new() { entry } };
        store.Deployed[(id, "card")] = baseBytes;   // pristine
        store.Variants[(id, "card", 2)] = t2;

        var kills = new Dictionary<int, int> { [id] = 10 };   // tier 2
        var icon = new IconGlow("m", kills, new[] { id }, store, a => a());
        icon.Tick();

        Assert.Equal(baseBytes, store.Backup[(id, "card")]);
        Assert.Equal(t2, store.Deployed[(id, "card")]);

        int writesAfterFirst = store.DeployedWrites.Count;
        icon.Tick();   // applied already recorded 2 -> no further writes
        Assert.Equal(writesAfterFirst, store.DeployedWrites.Count);
    }

    [Fact]
    public void Tier0_RestoresFromBackup()
    {
        var store = new FakeIconGlowStore();
        const int id = 23;
        byte[] baseBytes = RandBytes(1101, 10), t2 = RandBytes(1102, 10);
        var entry = MakeEntry(id, "card", baseBytes, new() { [2] = t2 });
        store.Manifest = new IconGlowManifest { SchemaVersion = 1, Icons = new() { entry } };
        store.Deployed[(id, "card")] = t2;       // already holds tier 2
        store.Backup[(id, "card")] = baseBytes;  // a backup already exists from an earlier session

        // Starts at the tier that matches what is already deployed, then drops to 0 -- a more
        // targeted variant of this restore than JudgeAll_RestoresStaleRimWhenDesiredZero above,
        // which pins the FIX 1 case (kills 0 from the very first tick) directly.
        var kills = new Dictionary<int, int> { [id] = 10 };   // tier 2, matches the deployed t2
        var icon = new IconGlow("m", kills, new[] { id }, store, a => a());
        icon.Tick();
        Assert.Equal(t2, store.Deployed[(id, "card")]);   // judge seeded 2 == desired 2, no write

        kills[id] = 0;
        icon.Tick();

        Assert.Equal(baseBytes, store.Deployed[(id, "card")]);
        Assert.Empty(store.BackupWrites);   // the surface was never pristine at judge time -- no new snapshot
    }

    [Fact]
    public void Judge_ForeignArtUnmanagedWarnsOnce()
    {
        var store = new FakeIconGlowStore();
        const int id = 24;
        byte[] baseBytes = RandBytes(1201, 10), t1 = RandBytes(1202, 10);
        byte[] foreign = RandBytes(1203, 10);   // unrelated to base or any baked variant
        var entry = MakeEntry(id, "card", baseBytes, new() { [1] = t1 });
        store.Manifest = new IconGlowManifest { SchemaVersion = 1, Icons = new() { entry } };
        store.Deployed[(id, "card")] = foreign;

        var kills = new Dictionary<int, int> { [id] = 5 };
        var icon = new IconGlow("m", kills, new[] { id }, store, a => a());

        using var cap = LogCapture.Start();
        icon.Tick();
        icon.Tick();

        Assert.Equal(1, cap.File.Count(l => l.Contains("foreign art")));
        Assert.Empty(store.DeployedWrites);
        Assert.Empty(store.BackupWrites);
        Assert.Equal(foreign, store.Deployed[(id, "card")]);
    }

    [Fact]
    public void Tier0_WithoutBackup_Unmanaged()
    {
        var store = new FakeIconGlowStore();
        const int id = 25;
        byte[] baseBytes = RandBytes(1301, 10), t2 = RandBytes(1302, 10);
        var entry = MakeEntry(id, "card", baseBytes, new() { [2] = t2 });
        store.Manifest = new IconGlowManifest { SchemaVersion = 1, Icons = new() { entry } };
        store.Deployed[(id, "card")] = t2;   // holds tier 2, no backup ever taken

        // Same shape as Tier0_RestoresFromBackup: reach tier 0 by dropping FROM the tier that
        // matches what is already deployed.
        var kills = new Dictionary<int, int> { [id] = 10 };   // tier 2, matches the deployed t2
        var icon = new IconGlow("m", kills, new[] { id }, store, a => a());
        icon.Tick();
        Assert.Equal(t2, store.Deployed[(id, "card")]);   // judge seeded 2 == desired 2, no write

        kills[id] = 0;
        using var cap = LogCapture.Start();
        icon.Tick();

        Assert.Equal(t2, store.Deployed[(id, "card")]);   // unchanged, nothing to restore from
        Assert.Empty(store.DeployedWrites);
        Assert.Equal(1, cap.File.Count(l => l.Contains("no pristine base copy is available")));
    }

    [Fact]
    public void PartialWriteFailure_StaysStaleAndRetries()
    {
        var store = new FakeIconGlowStore();
        const int id = 26;
        byte[] cardBase = RandBytes(1401, 10), cardT1 = RandBytes(1402, 10);
        byte[] smallBase = RandBytes(1403, 6), smallT1 = RandBytes(1404, 6);
        var cardEntry = MakeEntry(id, "card", cardBase, new() { [1] = cardT1 });
        var smallEntry = MakeEntry(id, "small", smallBase, new() { [1] = smallT1 });
        store.Manifest = new IconGlowManifest { SchemaVersion = 1, Icons = new() { cardEntry, smallEntry } };
        store.Deployed[(id, "card")] = cardBase;
        store.Deployed[(id, "small")] = smallBase;
        store.Variants[(id, "card", 1)] = cardT1;
        store.Variants[(id, "small", 1)] = smallT1;
        store.FailDeployedWrite.Add((id, "card"));   // card fails, small succeeds

        var kills = new Dictionary<int, int> { [id] = 5 };   // tier 1
        var icon = new IconGlow("m", kills, new[] { id }, store, a => a());

        using (var cap = LogCapture.Start())
        {
            icon.Tick();
            Assert.Equal(cardBase, store.Deployed[(id, "card")]);    // failed write -- unchanged
            Assert.Equal(smallT1, store.Deployed[(id, "small")]);    // succeeded
            Assert.Equal(1, cap.File.Count(l => l.Contains($"icon {id}") && l.Contains("failed to write")));
        }

        // applied did not record the tier -- the next diff still sees this id as changed and
        // retries BOTH surfaces (harmlessly rewriting the one that already succeeded)
        store.FailDeployedWrite.Remove((id, "card"));
        icon.Tick();
        Assert.Equal(cardT1, store.Deployed[(id, "card")]);
        Assert.Equal(smallT1, store.Deployed[(id, "small")]);

        int writesAfterHeal = store.DeployedWrites.Count;
        icon.Tick();   // now recorded -> no further work
        Assert.Equal(writesAfterHeal, store.DeployedWrites.Count);
    }

    [Fact]
    public void Judge_DivergentSurfaces_SeedsMinimum()
    {
        var store = new FakeIconGlowStore();
        const int id = 27;
        byte[] cardBase = RandBytes(1501, 10), cardT2 = RandBytes(1502, 10);
        byte[] smallBase = RandBytes(1503, 6), smallT2 = RandBytes(1504, 6);
        var cardEntry = MakeEntry(id, "card", cardBase, new() { [2] = cardT2 });
        var smallEntry = MakeEntry(id, "small", smallBase, new() { [2] = smallT2 });
        store.Manifest = new IconGlowManifest { SchemaVersion = 1, Icons = new() { cardEntry, smallEntry } };
        store.Deployed[(id, "card")] = cardT2;     // already at tier 2
        store.Deployed[(id, "small")] = smallBase; // still pristine
        store.Variants[(id, "card", 2)] = cardT2;
        store.Variants[(id, "small", 2)] = smallT2;

        var kills = new Dictionary<int, int> { [id] = 10 };   // tier 2
        var icon = new IconGlow("m", kills, new[] { id }, store, a => a());
        icon.Tick();

        Assert.Equal(smallBase, store.Backup[(id, "small")]);   // the pristine surface got snapshotted
        // both surfaces end up healed to the desired tier -- including the one that was
        // already there, proving the seed was the MINIMUM (0), not "already fine, skip it"
        Assert.Contains(store.DeployedWrites, w => w.Id == id && w.Surface == "card" && w.Bytes.SequenceEqual(cardT2));
        Assert.Equal(smallT2, store.Deployed[(id, "small")]);
    }

    [Fact]
    public void SnapshotFailure_RefusesOverwrite()
    {
        var store = new FakeIconGlowStore();
        const int id = 28;
        byte[] cardBase = RandBytes(1601, 10), cardT1 = RandBytes(1602, 10);
        byte[] smallBase = RandBytes(1603, 6), smallT1 = RandBytes(1604, 6);
        var cardEntry = MakeEntry(id, "card", cardBase, new() { [1] = cardT1 });
        var smallEntry = MakeEntry(id, "small", smallBase, new() { [1] = smallT1 });
        store.Manifest = new IconGlowManifest { SchemaVersion = 1, Icons = new() { cardEntry, smallEntry } };
        store.Deployed[(id, "card")] = cardBase;     // pristine
        store.Deployed[(id, "small")] = smallBase;   // also pristine
        store.FailBackupWrite.Add((id, "card"));     // the snapshot for "card" cannot be written

        var kills = new Dictionary<int, int> { [id] = 5 };
        var icon = new IconGlow("m", kills, new[] { id }, store, a => a());

        using var cap = LogCapture.Start();
        icon.Tick();

        Assert.Empty(store.DeployedWrites);   // nothing written for this id at all
        Assert.DoesNotContain((id, "card"), store.Backup.Keys);
        Assert.DoesNotContain((id, "small"), store.Backup.Keys);
        Assert.Equal(1, cap.File.Count(l => l.Contains("refusing to overwrite")));
    }

    // ---- LW-336 adversarial-verify fix round: five findings, six new tests. See each fix's
    // own remarks in IconGlow.cs / IconGlow.Apply.cs for the mechanism these pin. ----

    // ---- FIX 1 (the ship-blocker), LOAD-BEARING: a tiered tex left on disk from an earlier
    // session, with kills already back at 0 before this launch's very first tick, must not stay
    // rimmed forever. Diff() alone can never see it -- an id absent from _applied counts as
    // tier 0 == desired 0 == "no change" -- so only a once-per-launch judge of EVERY manageable
    // id can discover the truth and correct it. Against the pre-fix code this never resolves no
    // matter how many ticks run (changed.Count stays 0 forever), confirming RED. ----

    [Fact]
    public void JudgeAll_RestoresStaleRimWhenDesiredZero()
    {
        var store = new FakeIconGlowStore();
        const int id = 31;
        byte[] cardBase = RandBytes(1701, 10), cardT2 = RandBytes(1702, 10);
        byte[] smallBase = RandBytes(1703, 6), smallT2 = RandBytes(1704, 6);
        var cardEntry = MakeEntry(id, "card", cardBase, new() { [2] = cardT2 });
        var smallEntry = MakeEntry(id, "small", smallBase, new() { [2] = smallT2 });
        store.Manifest = new IconGlowManifest { SchemaVersion = 1, Icons = new() { cardEntry, smallEntry } };
        store.Deployed[(id, "card")] = cardT2;     // stale tier-2 tex left over from an earlier session
        store.Deployed[(id, "small")] = smallT2;
        store.Backup[(id, "card")] = cardBase;     // that earlier session already snapshotted the base
        store.Backup[(id, "small")] = smallBase;

        var kills = new Dictionary<int, int> { [id] = 0 };   // desired tier 0 from the very first tick
        var icon = new IconGlow("m", kills, new[] { id }, store, a => a());

        icon.Tick();
        icon.Tick();

        Assert.Equal(cardBase, store.Deployed[(id, "card")]);
        Assert.Equal(smallBase, store.Deployed[(id, "small")]);
    }

    // ---- FIX 1's narrower cousin: JudgeId's seeded MINIMUM already equals the desired tier
    // (0), but one surface (card) is still stuck at tier 2. A future Diff can never notice this
    // -- applied == desired the moment it is seeded -- so the heal must happen right at judge
    // time, in the same pass that discovers the divergence. ----

    [Fact]
    public void DivergentSurfaces_HealEvenWhenMinimumEqualsDesired()
    {
        var store = new FakeIconGlowStore();
        const int id = 32;
        byte[] cardBase = RandBytes(1711, 10), cardT2 = RandBytes(1712, 10);
        byte[] smallBase = RandBytes(1713, 6);
        var cardEntry = MakeEntry(id, "card", cardBase, new() { [2] = cardT2 });
        var smallEntry = MakeEntry(id, "small", smallBase, new());
        store.Manifest = new IconGlowManifest { SchemaVersion = 1, Icons = new() { cardEntry, smallEntry } };
        store.Deployed[(id, "card")] = cardT2;     // stuck at tier 2
        store.Deployed[(id, "small")] = smallBase; // already pristine (tier 0)
        store.Backup[(id, "card")] = cardBase;     // an earlier session's snapshot, needed to heal card
        store.Backup[(id, "small")] = smallBase;

        var kills = new Dictionary<int, int> { [id] = 0 };   // desired tier 0 == the seeded minimum
        var icon = new IconGlow("m", kills, new[] { id }, store, a => a());

        icon.Tick();

        Assert.Equal(cardBase, store.Deployed[(id, "card")]);     // healed
        Assert.Equal(smallBase, store.Deployed[(id, "small")]);   // already correct, unchanged
    }

    // ---- FIX 2a: a Publish-zip update can lay REBAKED bases while an old base_backup/
    // survives. The only moment that is healable is judge time -- when a surface is found
    // PRISTINE and its existing backup does not match, re-snapshot it right there. ----

    [Fact]
    public void StaleBackup_ResnapshottedOnPristineJudge()
    {
        var store = new FakeIconGlowStore();
        const int id = 33;
        byte[] baseBytes = RandBytes(1721, 10), oldBackup = RandBytes(1722, 10);
        var entry = MakeEntry(id, "card", baseBytes, new());
        store.Manifest = new IconGlowManifest { SchemaVersion = 1, Icons = new() { entry } };
        store.Deployed[(id, "card")] = baseBytes;   // pristine against the CURRENT bake
        store.Backup[(id, "card")] = oldBackup;     // stale: from a bake this base no longer matches

        var kills = new Dictionary<int, int> { [id] = 0 };
        var icon = new IconGlow("m", kills, new[] { id }, store, a => a());

        icon.Tick();

        Assert.Equal(baseBytes, store.Backup[(id, "card")]);
    }

    // ---- FIX 2b: if staleness ever slips past judge time (or a backup goes bad afterward),
    // ApplyId must never write it -- a wrong-version restore would look correct at a glance and
    // rot the deployed art silently. ----

    [Fact]
    public void StaleBackup_RefusedAtTierZeroRestore()
    {
        var store = new FakeIconGlowStore();
        const int id = 34;
        byte[] baseBytes = RandBytes(1731, 10), t2 = RandBytes(1732, 10), staleBackup = RandBytes(1733, 10);
        var entry = MakeEntry(id, "card", baseBytes, new() { [2] = t2 });
        store.Manifest = new IconGlowManifest { SchemaVersion = 1, Icons = new() { entry } };
        store.Deployed[(id, "card")] = t2;
        store.Backup[(id, "card")] = staleBackup;   // does not match entry.BaseSha1

        // reach tier 0 by dropping FROM the tier that matches what is already deployed, so the
        // judge seeds truthfully first (mirrors Tier0_RestoresFromBackup's own pattern above).
        var kills = new Dictionary<int, int> { [id] = 10 };   // tier 2, matches the deployed t2
        var icon = new IconGlow("m", kills, new[] { id }, store, a => a());
        icon.Tick();
        Assert.Equal(t2, store.Deployed[(id, "card")]);   // judge seeded 2 == desired 2, no write yet

        kills[id] = 0;
        using var cap = LogCapture.Start();
        icon.Tick();

        Assert.Equal(t2, store.Deployed[(id, "card")]);   // untouched -- the stale backup was refused
        Assert.Empty(store.DeployedWrites.Where(w => w.Id == id));
        Assert.Equal(1, cap.File.Count(l => l.Contains("older bake")));
    }

    // ---- FIX 3: a successful judge/apply pass must leave exactly one observable trace, so a
    // live pass can see the subsystem work without per-icon spam. ----

    [Fact]
    public void ApplyBatch_EmitsOneSummaryLine()
    {
        var store = new FakeIconGlowStore();
        const int idA = 35, idB = 36;
        byte[] baseA = RandBytes(1741, 10), t1A = RandBytes(1742, 10);
        byte[] baseB = RandBytes(1743, 10), t2B = RandBytes(1744, 10);
        var entryA = MakeEntry(idA, "card", baseA, new() { [1] = t1A });
        var entryB = MakeEntry(idB, "card", baseB, new() { [2] = t2B });
        store.Manifest = new IconGlowManifest { SchemaVersion = 1, Icons = new() { entryA, entryB } };
        store.Deployed[(idA, "card")] = baseA;
        store.Deployed[(idB, "card")] = baseB;
        store.Variants[(idA, "card", 1)] = t1A;
        store.Variants[(idB, "card", 2)] = t2B;

        var kills = new Dictionary<int, int> { [idA] = 5, [idB] = 10 };   // tier 1, tier 2
        var icon = new IconGlow("m", kills, new[] { idA, idB }, store, a => a());

        using var cap = LogCapture.Start();
        icon.Tick();

        Assert.Equal(t1A, store.Deployed[(idA, "card")]);
        Assert.Equal(t2B, store.Deployed[(idB, "card")]);
        Assert.Equal(1, cap.File.Count(l => l.Contains("IconGlow: judged") && l.Contains("wrote")));
    }

    // ---- FIX 5's tripwire: the backup store flattens to just the base tex FILENAME
    // (Path.GetFileName), so two different ids whose baked BaseRel resolves to the same
    // filename would cross-wire each other's snapshots. Guard it at manifest load, before
    // either id is ever trusted. Zero collisions exist in the real 242-entry manifest; this
    // guards a future bake rename. ----

    [Fact]
    public void ManifestBasenameCollision_Unmanaged()
    {
        var store = new FakeIconGlowStore();
        const int idA = 37, idB = 38;
        byte[] baseA = RandBytes(1751, 10), baseB = RandBytes(1752, 10);
        var entryA = MakeEntry(idA, "card", baseA, new());
        var entryB = MakeEntry(idB, "card", baseB, new());
        entryB.BaseRel = entryA.BaseRel;   // force a basename collision across two different ids
        store.Manifest = new IconGlowManifest { SchemaVersion = 1, Icons = new() { entryA, entryB } };
        store.Deployed[(idA, "card")] = baseA;
        store.Deployed[(idB, "card")] = baseB;

        var kills = new Dictionary<int, int> { [idA] = 5, [idB] = 5 };
        var icon = new IconGlow("m", kills, new[] { idA, idB }, store, a => a());

        using var cap = LogCapture.Start();
        icon.Tick();
        icon.Tick();

        Assert.Empty(store.DeployedWrites);
        Assert.Empty(store.BackupWrites);
        Assert.Equal(1, cap.File.Count(l => l.Contains("share the base tex filename")
            && l.Contains(idA.ToString()) && l.Contains(idB.ToString())));
    }
}
