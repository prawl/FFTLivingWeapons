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
    /// consulted, mirroring the production store's own never-throw contract.
    ///
    /// LW-381 R2-2: <see cref="PatchPacBatchCalls"/> records every <c>PatchPacBatch</c>
    /// invocation (the whole item list passed that call) so a test can assert both how many times
    /// the store was actually invoked -- the batching fix's whole point -- and what each item
    /// carried. <see cref="PatchPacResult"/> is the single canned result every item in every call
    /// returns -- a per-surface toggle was not needed by anything in this file's test list (the
    /// routing tests key off the RESULT value, not which surface produced it).</summary>
    private sealed class FakeIconGlowStore : IIconGlowStore
    {
        public IconGlowManifest? Manifest;
        public readonly Dictionary<(int Id, string Surface), byte[]> Deployed = new();
        public readonly Dictionary<(int Id, string Surface), byte[]> Backup = new();
        public readonly Dictionary<(int Id, string Surface, int Tier), byte[]> Variants = new();
        public readonly HashSet<(int Id, string Surface)> FailDeployedWrite = new();
        public readonly HashSet<(int Id, string Surface)> FailBackupWrite = new();
        public IconGlowWriteError? FailError;
        public IconGlowWriteError? LastWriteError { get; private set; }
        public readonly List<(int Id, string Surface, byte[] Bytes)> DeployedWrites = new();
        public readonly List<(int Id, string Surface, byte[] Bytes)> BackupWrites = new();
        /// <summary>LW-381 R2-2: one entry per <c>PatchPacBatch</c> CALL (not per item), so a test
        /// can assert how many times the store was actually invoked -- the whole point of the
        /// batching fix is fewer, larger calls rather than one call per icon.</summary>
        public readonly List<IReadOnlyList<(IconGlowEntry Entry, byte[] OldBytes, byte[] NewBytes)>> PatchPacBatchCalls = new();
        public PacPatchResult PatchPacResult = PacPatchResult.Patched;
        /// <summary>LW-381: a per-(id,surface) override on top of the single canned
        /// <see cref="PatchPacResult"/> -- lets a test make ONE surface land Patched while a
        /// sibling in the SAME batch call lands something else (T10's "hider only sees the
        /// actually-patched surfaces" case).</summary>
        public readonly Dictionary<(int Id, string Surface), PacPatchResult> PatchPacResultOverride = new();

        public IconGlowManifest? ReadManifest() => Manifest;
        public byte[]? ReadDeployedTex(IconGlowEntry entry) => Deployed.TryGetValue((entry.Id, entry.Surface), out var b) ? b : null;
        public byte[]? ReadVariantTex(IconGlowEntry entry, int tier) => Variants.TryGetValue((entry.Id, entry.Surface, tier), out var b) ? b : null;
        public byte[]? ReadBaseBackup(IconGlowEntry entry) => Backup.TryGetValue((entry.Id, entry.Surface), out var b) ? b : null;

        public bool WriteDeployedTex(IconGlowEntry entry, byte[] bytes)
        {
            LastWriteError = null;
            DeployedWrites.Add((entry.Id, entry.Surface, bytes));
            if (FailDeployedWrite.Contains((entry.Id, entry.Surface))) { LastWriteError = FailError; return false; }
            Deployed[(entry.Id, entry.Surface)] = bytes;
            return true;
        }

        public bool WriteBaseBackup(IconGlowEntry entry, byte[] bytes)
        {
            LastWriteError = null;
            BackupWrites.Add((entry.Id, entry.Surface, bytes));
            if (FailBackupWrite.Contains((entry.Id, entry.Surface))) { LastWriteError = FailError; return false; }
            Backup[(entry.Id, entry.Surface)] = bytes;
            return true;
        }

        public IReadOnlyDictionary<(int Id, string Surface), PacPatchResult> PatchPacBatch(
            IReadOnlyList<(IconGlowEntry Entry, byte[] OldBytes, byte[] NewBytes)> items)
        {
            PatchPacBatchCalls.Add(items);
            var results = new Dictionary<(int, string), PacPatchResult>();
            foreach (var item in items)
            {
                var key = (item.Entry.Id, item.Entry.Surface);
                results[key] = PatchPacResultOverride.TryGetValue(key, out var r) ? r : PatchPacResult;
            }
            return results;
        }
    }

    /// <summary>LW-381: records every <see cref="Hide"/> call's full target list; returns a
    /// Hidden outcome for every target unless <see cref="OnHide"/> is set (T12's postpone-then-
    /// hide sequencing needs a call-count-driven result).</summary>
    private sealed class RecordingIconCacheHider : IIconCacheHider
    {
        public readonly List<IReadOnlyList<IconCacheTarget>> Calls = new();
        public Func<IReadOnlyList<IconCacheTarget>, IconCacheHideResult>? OnHide;

        public IconCacheHideResult Hide(IReadOnlyList<IconCacheTarget> targets)
        {
            Calls.Add(targets);
            if (OnHide != null) return OnHide(targets);
            var result = new IconCacheHideResult();
            foreach (var t in targets) result.Set(t.Id, t.Surface, new SurfaceOutcome(HideOutcome.Hidden, 0, 0, 1, 1));
            return result;
        }
    }

    /// <summary>LW-381 T11: a hider that always throws -- pins that a hide failure can never
    /// reach out of ApplyDiff and affect the loose-write result or the apply's own summary line.</summary>
    private sealed class ThrowingIconCacheHider : IIconCacheHider
    {
        public IconCacheHideResult Hide(IReadOnlyList<IconCacheTarget> targets) => throw new InvalidOperationException("hider boom");
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
        var icon = new IconGlow("m", kills, new[] { id }, store, new NoopIconCacheHider(), a => a());

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
        var icon = new IconGlow("m", kills, new[] { id }, store, new NoopIconCacheHider(), a => a());
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
        var icon = new IconGlow("m", new Dictionary<int, int>(), Array.Empty<int>(), store, new NoopIconCacheHider(), a => a());

        using var cap = LogCapture.Start();
        var ex = Record.Exception(() => { icon.Tick(); icon.Tick(); icon.Tick(); });

        Assert.Null(ex);
        Assert.Equal(1, cap.File.Count(l => l.Contains("IconGlow standing down")));
    }

    // The bake writes an inputsSha1 per entry for its own build gate. The real file reader must
    // keep loading a manifest that carries fields the runtime never declared.
    [Fact]
    public void FileStore_ReadManifest_LoadsEntriesCarryingUndeclaredFields()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lw_glow_manifest_" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(dir, "glow_icons"));
        System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "glow_icons", "manifest.json"),
            "{\"schemaVersion\":1,\"tierScales\":{\"1\":0.6},\"icons\":[{\"id\":33,\"surface\":\"card\","
            + "\"baseRel\":\"data/x/ei_033_uitx.tex\",\"length\":51296,\"baseSha1\":\"aa\",\"inputsSha1\":\"bb\","
            + "\"variants\":{\"1\":\"v1.tex\"},\"variantSha1s\":{\"1\":\"cc\"},\"someFutureField\":[1,2]}]}");
        try
        {
            var manifest = new FileIconGlowStore(dir).ReadManifest();

            Assert.NotNull(manifest);
            Assert.Equal(1, manifest!.SchemaVersion);
            var entry = Assert.Single(manifest.Icons);
            Assert.Equal(33, entry.Id);
            Assert.Equal("aa", entry.BaseSha1);
            Assert.Equal("v1.tex", entry.Variants["1"]);
        }
        finally { System.IO.Directory.Delete(dir, true); }
    }

    [Fact]
    public void UnsupportedSchemaVersion_StandsDown()
    {
        var store = new FakeIconGlowStore { Manifest = new IconGlowManifest { SchemaVersion = 2, Icons = new() } };
        var icon = new IconGlow("m", new Dictionary<int, int>(), Array.Empty<int>(), store, new NoopIconCacheHider(), a => a());

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
        var icon = new IconGlow("m", kills, new[] { id }, store, new NoopIconCacheHider(), a => a());

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
        var icon = new IconGlow("m", kills, new[] { id }, store, new NoopIconCacheHider(), _ => starts++);

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
        var icon = new IconGlow("m", kills, new[] { knownId }, store, new NoopIconCacheHider(), a => a());

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
        var icon = new IconGlow("m", kills, new[] { id }, store, new NoopIconCacheHider(), a => a());
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
        var icon = new IconGlow("m", kills, new[] { id }, store, new NoopIconCacheHider(), a => a());

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
        var icon = new IconGlow("m", kills, new[] { id }, store, new NoopIconCacheHider(), a => a());

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
        var icon = new IconGlow("m", kills, new[] { id }, store, new NoopIconCacheHider(), a => a());
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
        var icon = new IconGlow("m", kills, new[] { id }, store, new NoopIconCacheHider(), a => a());
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
        var icon = new IconGlow("m", kills, new[] { id }, store, new NoopIconCacheHider(), a => a());

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
        var icon = new IconGlow("m", kills, new[] { id }, store, new NoopIconCacheHider(), a => a());
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
        var icon = new IconGlow("m", kills, new[] { id }, store, new NoopIconCacheHider(), a => a());

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
        var icon = new IconGlow("m", kills, new[] { id }, store, new NoopIconCacheHider(), a => a());
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
        var icon = new IconGlow("m", kills, new[] { id }, store, new NoopIconCacheHider(), a => a());

        using var cap = LogCapture.Start();
        icon.Tick();

        Assert.Empty(store.DeployedWrites);   // nothing written for this id at all
        Assert.DoesNotContain((id, "card"), store.Backup.Keys);
        Assert.DoesNotContain((id, "small"), store.Backup.Keys);
        Assert.Equal(1, cap.File.Count(l => l.Contains("refusing to overwrite")));
    }

    private static readonly IconGlowWriteError SharingViolation = new("IOException", 32,
        "The process cannot access the file because it is being used by another process.");

    private static (FakeIconGlowStore Store, IconGlow Icon) FailingStore(int id, string[] surfaces, bool backup, IconGlowWriteError? error)
    {
        var store = new FakeIconGlowStore { FailError = error };
        var entries = new List<IconGlowEntry>();
        int seed = 1700 + id;
        foreach (var surface in surfaces)
        {
            byte[] baseBytes = RandBytes(seed++, 10), t1 = RandBytes(seed++, 10);
            entries.Add(MakeEntry(id, surface, baseBytes, new() { [1] = t1 }));
            store.Deployed[(id, surface)] = baseBytes;
            store.Variants[(id, surface, 1)] = t1;
            (backup ? store.FailBackupWrite : store.FailDeployedWrite).Add((id, surface));
        }
        if (!backup) foreach (var surface in surfaces) store.Backup[(id, surface)] = store.Deployed[(id, surface)];
        store.Manifest = new IconGlowManifest { SchemaVersion = 1, Icons = entries };
        var kills = new Dictionary<int, int> { [id] = 5 };
        return (store, new IconGlow("m", kills, new[] { id }, store, new NoopIconCacheHider(), a => a()));
    }

    [Fact]
    public void WriteFailure_WarnNamesTheExceptionAndWin32Code()
    {
        var (_, icon) = FailingStore(61, new[] { "card" }, false, SharingViolation);
        using var cap = LogCapture.Start();
        icon.Tick();

        var warns = cap.File.Where(l => l.Contains("failed to write icon")).ToList();
        Assert.Single(warns);
        Assert.Contains("failed to write icon 61 (card); will retry. (detail: IOException, Win32 error 32, The process cannot access the file", warns[0]);
    }

    [Fact]
    public void WriteFailure_WarnsOncePerFilePerSession()
    {
        var (_, icon) = FailingStore(62, new[] { "card", "small" }, false, SharingViolation);
        using var cap = LogCapture.Start();
        icon.Tick();
        icon.Tick();
        icon.Tick();

        var warns = cap.File.Where(l => l.Contains("failed to write icon 62")).ToList();
        Assert.Equal(2, warns.Count);
        Assert.Single(warns, l => l.Contains("(card)"));
        Assert.Single(warns, l => l.Contains("(small)"));
    }

    [Fact]
    public void SnapshotFailure_LineCarriesTheDetailTail()
    {
        var (_, icon) = FailingStore(63, new[] { "card" }, true, SharingViolation);
        using var cap = LogCapture.Start();
        icon.Tick();

        Assert.Equal(1, cap.File.Count(l => l.Contains("refusing to overwrite what cannot be restored. (detail: IOException, Win32 error 32,")));
    }

    [Fact]
    public void WriteFailure_WithoutAnErrorObjectHasNoDetailTail()
    {
        var (_, icon) = FailingStore(64, new[] { "card" }, false, null);
        using var cap = LogCapture.Start();
        icon.Tick();

        var warn = Assert.Single(cap.File.Where(l => l.Contains("failed to write icon 64")));
        Assert.DoesNotContain("(detail:", warn);
    }

    [Fact]
    public void WriteError_DetailCollapsesLineBreaks()
    {
        var err = new IconGlowWriteError("IOException", 5, "Access is denied.\r\nTry again.\n");
        Assert.Equal("IOException, Win32 error 5, Access is denied. Try again.", err.Detail);
    }

    [Fact]
    public void RealStore_LockedDestination_ReportsWin32CodeAndRecovers()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lw428_" + Guid.NewGuid().ToString("N"));
        try
        {
            var entry = MakeEntry(70, "card", RandBytes(1, 10), new());
            var store = new FileIconGlowStore(dir);
            var dest = System.IO.Path.Combine(dir, "FFTIVC", entry.BaseRel.Replace('/', System.IO.Path.DirectorySeparatorChar));
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dest)!);
            System.IO.File.WriteAllBytes(dest, RandBytes(2, 10));

            using (new System.IO.FileStream(dest, System.IO.FileMode.Open, System.IO.FileAccess.ReadWrite, System.IO.FileShare.None))
            {
                Assert.False(store.WriteDeployedTex(entry, RandBytes(3, 10)));
                Assert.NotNull(store.LastWriteError);
                Assert.Equal(32, store.LastWriteError!.Win32Code);
                Assert.Empty(System.IO.Directory.GetFiles(System.IO.Path.GetDirectoryName(dest)!, "*.tmp"));
            }

            Assert.True(store.WriteDeployedTex(entry, RandBytes(3, 10)));
            Assert.Null(store.LastWriteError);
        }
        finally { try { System.IO.Directory.Delete(dir, true); } catch { } }
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
        var icon = new IconGlow("m", kills, new[] { id }, store, new NoopIconCacheHider(), a => a());

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
        var icon = new IconGlow("m", kills, new[] { id }, store, new NoopIconCacheHider(), a => a());

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
        var icon = new IconGlow("m", kills, new[] { id }, store, new NoopIconCacheHider(), a => a());

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
        var icon = new IconGlow("m", kills, new[] { id }, store, new NoopIconCacheHider(), a => a());
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
        var icon = new IconGlow("m", kills, new[] { idA, idB }, store, new NoopIconCacheHider(), a => a());

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
        var icon = new IconGlow("m", kills, new[] { idA, idB }, store, new NoopIconCacheHider(), a => a());

        using var cap = LogCapture.Start();
        icon.Tick();
        icon.Tick();

        Assert.Empty(store.DeployedWrites);
        Assert.Empty(store.BackupWrites);
        Assert.Equal(1, cap.File.Count(l => l.Contains("share the base tex filename")
            && l.Contains(idA.ToString()) && l.Contains(idB.ToString())));
    }

    // ---- LW-381: modded.pac same-session mirror. Every case below drives IconGlow through the
    // fake store's PatchPacBatch hook -- no real pac file, no real modded.pac, anywhere in this
    // region. R2-2 fix round: the store is now flushed ONCE per ApplyDiff pass rather than once
    // per surface, so most assertions below key off PatchPacBatchCalls (call COUNT and each
    // call's item list) rather than a per-surface call tally. ----

    // ---- a tier change queues both surfaces and flushes them in ONE batch call, carrying the
    // previously deployed bytes as OldBytes and the new tier's bytes as NewBytes ----

    [Fact]
    public void TierChange_QueuesBothSurfaces_IntoOnePacBatchFlush_WithOldAndNewBytes()
    {
        var store = new FakeIconGlowStore();
        const int id = 61;
        byte[] cardBase = RandBytes(1201, 10), cardT1 = RandBytes(1202, 10);
        byte[] smallBase = RandBytes(1203, 6), smallT1 = RandBytes(1204, 6);
        var cardEntry = MakeEntry(id, "card", cardBase, new() { [1] = cardT1 });
        var smallEntry = MakeEntry(id, "small", smallBase, new() { [1] = smallT1 });
        store.Manifest = new IconGlowManifest { SchemaVersion = 1, Icons = new() { cardEntry, smallEntry } };
        store.Deployed[(id, "card")] = cardBase;
        store.Deployed[(id, "small")] = smallBase;
        store.Variants[(id, "card", 1)] = cardT1;
        store.Variants[(id, "small", 1)] = smallT1;

        var kills = new Dictionary<int, int> { [id] = 5 };   // tier 1
        var icon = new IconGlow("m", kills, new[] { id }, store, new NoopIconCacheHider(), a => a());
        icon.Tick();

        // ONE flush for the whole apply (R2-2), carrying both surfaces' items.
        Assert.Single(store.PatchPacBatchCalls);
        var items = store.PatchPacBatchCalls[0];
        Assert.Equal(2, items.Count);
        Assert.Contains(items, it => it.Entry.Id == id && it.Entry.Surface == "card"
            && it.OldBytes.SequenceEqual(cardBase) && it.NewBytes.SequenceEqual(cardT1));
        Assert.Contains(items, it => it.Entry.Id == id && it.Entry.Surface == "small"
            && it.OldBytes.SequenceEqual(smallBase) && it.NewBytes.SequenceEqual(smallT1));
    }

    // ---- the pac mirror is queued only AFTER WriteDeployedTex succeeded for that surface, and
    // not at all when the loose write failed -- so nothing is even flushed ----

    [Fact]
    public void PacBatch_ExcludesSurface_WhenDeployedWriteFailed()
    {
        var store = new FakeIconGlowStore();
        const int id = 62;
        byte[] baseBytes = RandBytes(1301, 10), t1 = RandBytes(1302, 10);
        var entry = MakeEntry(id, "card", baseBytes, new() { [1] = t1 });
        store.Manifest = new IconGlowManifest { SchemaVersion = 1, Icons = new() { entry } };
        store.Deployed[(id, "card")] = baseBytes;
        store.Variants[(id, "card", 1)] = t1;
        store.FailDeployedWrite.Add((id, "card"));   // the loose write itself fails

        var kills = new Dictionary<int, int> { [id] = 5 };
        var icon = new IconGlow("m", kills, new[] { id }, store, new NoopIconCacheHider(), a => a());
        icon.Tick();

        Assert.NotEmpty(store.DeployedWrites);       // the write was attempted...
        Assert.Empty(store.PatchPacBatchCalls);      // ...but never succeeded, so nothing to flush
    }

    // ---- PatchPacBatch returning Failed/Ambiguous for an item does NOT fail the apply, does NOT
    // mark the id unmanaged, and warns at most once per id per launch -- R2-8 fix: driving the
    // SAME id through two genuinely DIFFERENT tier changes across two ticks (rather than a second
    // tick that never reaches the pac mirror at all, which could not have failed the old
    // assertion either way) so the "only one warning" claim is actually put at risk. ----

    // internal enum can't be a public [Theory] parameter type (CS0051) -- resultName stands in.
    [Theory]
    [InlineData("Failed")]
    [InlineData("Ambiguous")]
    public void PacBatch_SearchOrWriteFailure_DoesNotFailApply_WarnsOncePerIcon_AcrossRepeatedFailures(string resultName)
    {
        var result = Enum.Parse<PacPatchResult>(resultName);
        var store = new FakeIconGlowStore { PatchPacResult = result };
        const int id = 63;
        byte[] baseBytes = RandBytes(1401, 10), t1 = RandBytes(1402, 10), t2 = RandBytes(1405, 10);
        var entry = MakeEntry(id, "card", baseBytes, new() { [1] = t1, [2] = t2 });
        store.Manifest = new IconGlowManifest { SchemaVersion = 1, Icons = new() { entry } };
        store.Deployed[(id, "card")] = baseBytes;
        store.Variants[(id, "card", 1)] = t1;
        store.Variants[(id, "card", 2)] = t2;

        var kills = new Dictionary<int, int> { [id] = 5 };   // tier 1
        var icon = new IconGlow("m", kills, new[] { id }, store, new NoopIconCacheHider(), a => a());

        using var cap = LogCapture.Start();
        icon.Tick();
        // The loose write itself succeeded and is the source of truth here: the pac failure
        // must be purely informational.
        Assert.Equal(t1, store.Deployed[(id, "card")]);
        Assert.DoesNotContain(cap.File, l => l.Contains($"leaves icon {id} plain"));

        kills[id] = 10;   // tier 2 -- a genuinely different apply, reaches the pac mirror again
        icon.Tick();
        Assert.Equal(t2, store.Deployed[(id, "card")]);

        // The pac mirror really did fire on BOTH ticks (proving the second tick was not a no-op
        // that trivially "didn't repeat" the warning), yet only ONE warning was ever emitted.
        Assert.Equal(2, store.PatchPacBatchCalls.Count);
        Assert.Equal(1, cap.File.Count(l => l.Contains($"icon {id}") && l.Contains("modded.pac")));
    }

    // ---- R2-4: NotFound is expected steady state (this session's own re-tier can run after the
    // launch merge already read the pac), never a warning -- Debug only. ----

    [Fact]
    public void PacBatch_NotFound_LogsDebugOnly_NeverWarns()
    {
        var store = new FakeIconGlowStore { PatchPacResult = PacPatchResult.NotFound };
        const int id = 66;
        byte[] baseBytes = RandBytes(1406, 10), t1 = RandBytes(1407, 10);
        var entry = MakeEntry(id, "card", baseBytes, new() { [1] = t1 });
        store.Manifest = new IconGlowManifest { SchemaVersion = 1, Icons = new() { entry } };
        store.Deployed[(id, "card")] = baseBytes;
        store.Variants[(id, "card", 1)] = t1;

        var kills = new Dictionary<int, int> { [id] = 5 };
        var icon = new IconGlow("m", kills, new[] { id }, store, new NoopIconCacheHider(), a => a());

        using var cap = LogCapture.Start();
        icon.Tick();

        Assert.Equal(t1, store.Deployed[(id, "card")]);
        Assert.DoesNotContain(cap.File, l => l.Contains("[WARN]") && l.Contains($"icon {id}") && l.Contains("modded.pac"));
        Assert.Contains(cap.File, l => l.Contains("[DEBUG]") && l.Contains($"icon {id}") && l.Contains("modded.pac"));
    }

    // ---- Unavailable warns at most once per launch across ALL icons, not once per icon -- and
    // both icons still share the one batch flush. ----

    [Fact]
    public void PacBatch_Unavailable_WarnsOncePerLaunch_AcrossAllIcons()
    {
        var store = new FakeIconGlowStore { PatchPacResult = PacPatchResult.Unavailable };
        const int idA = 64, idB = 65;
        byte[] baseA = RandBytes(1501, 10), t1A = RandBytes(1502, 10);
        byte[] baseB = RandBytes(1503, 10), t1B = RandBytes(1504, 10);
        var entryA = MakeEntry(idA, "card", baseA, new() { [1] = t1A });
        var entryB = MakeEntry(idB, "card", baseB, new() { [1] = t1B });
        store.Manifest = new IconGlowManifest { SchemaVersion = 1, Icons = new() { entryA, entryB } };
        store.Deployed[(idA, "card")] = baseA;
        store.Deployed[(idB, "card")] = baseB;
        store.Variants[(idA, "card", 1)] = t1A;
        store.Variants[(idB, "card", 1)] = t1B;

        var kills = new Dictionary<int, int> { [idA] = 5, [idB] = 5 };
        var icon = new IconGlow("m", kills, new[] { idA, idB }, store, new NoopIconCacheHider(), a => a());

        using var cap = LogCapture.Start();
        icon.Tick();

        Assert.Single(store.PatchPacBatchCalls);              // one flush, both icons batched together
        Assert.Equal(2, store.PatchPacBatchCalls[0].Count);   // both surfaces were in it
        Assert.Equal(1, cap.File.Count(l => l.Contains("cannot locate modded.pac")));
    }

    // ---- when the deployed bytes already equal the target bytes, the surface is never queued
    // for the pac mirror at all. The straightforward way this happens (Judge seeds _applied
    // straight from an already-correct deployed variant) never even reaches ApplyId -- see
    // Judge_SeedsAppliedFromDeployedVariant. This test instead pins the guard on the queueing
    // path itself, via the one route that calls WriteDeployedTex with bytes that were already
    // deployed: FIX 1's judgeAllIds heal writes EVERY surface of a divergent id to the healed
    // tier, even a surface that individually already held those exact bytes (card, below)
    // alongside one that did not (small). ----

    [Fact]
    public void PacBatch_NotQueued_ForSurfaceAlreadyAtTargetBytes_EvenWhenSiblingSurfaceChanges()
    {
        var store = new FakeIconGlowStore();
        const int id = 67;
        byte[] cardBase = RandBytes(1601, 10), cardT1 = RandBytes(1602, 10);
        byte[] smallBase = RandBytes(1603, 6), smallT1 = RandBytes(1604, 6);
        var cardEntry = MakeEntry(id, "card", cardBase, new() { [1] = cardT1 });
        var smallEntry = MakeEntry(id, "small", smallBase, new() { [1] = smallT1 });
        store.Manifest = new IconGlowManifest { SchemaVersion = 1, Icons = new() { cardEntry, smallEntry } };
        store.Deployed[(id, "card")] = cardT1;     // card: already at tier 1
        store.Deployed[(id, "small")] = smallBase; // small: still pristine -- DIVERGENT id
        store.Variants[(id, "card", 1)] = cardT1;
        store.Variants[(id, "small", 1)] = smallT1;

        var kills = new Dictionary<int, int> { [id] = 5 };   // desired tier 1
        var icon = new IconGlow("m", kills, new[] { id }, store, new NoopIconCacheHider(), a => a());
        icon.Tick();   // first-ever tick: judgeAllIds judges+heals this id in one pass (FIX 1)

        // Both surfaces get written (the heal writes every surface unconditionally)...
        Assert.Contains(store.DeployedWrites, w => w.Id == id && w.Surface == "card");
        Assert.Contains(store.DeployedWrites, w => w.Id == id && w.Surface == "small");
        // ...but only "small" actually changed bytes, so only "small" is in the (one) flush.
        Assert.Single(store.PatchPacBatchCalls);
        Assert.Single(store.PatchPacBatchCalls[0]);
        Assert.Equal("small", store.PatchPacBatchCalls[0][0].Entry.Surface);
    }

    // ---- R2-3: the pac warn set is independent of _warnedOnce. A cosmetic pac failure for icon
    // X must not permanently mute a LATER, genuinely different, loose-tex write failure warning
    // for the same id -- that is the failure that actually breaks the feature, and censoring it
    // behind a cosmetic same-session-only failure would be a defect in the evidence chain. ----

    [Fact]
    public void PacWarnSet_IndependentOfWarnedOnce_PacFailureDoesNotSuppressLaterLooseWriteFailureWarning()
    {
        var store = new FakeIconGlowStore { PatchPacResult = PacPatchResult.Failed };
        const int id = 68;
        byte[] baseBytes = RandBytes(1801, 10), t1 = RandBytes(1802, 10), t2 = RandBytes(1803, 10);
        var entry = MakeEntry(id, "card", baseBytes, new() { [1] = t1, [2] = t2 });
        store.Manifest = new IconGlowManifest { SchemaVersion = 1, Icons = new() { entry } };
        store.Deployed[(id, "card")] = baseBytes;
        store.Variants[(id, "card", 1)] = t1;
        store.Variants[(id, "card", 2)] = t2;

        var kills = new Dictionary<int, int> { [id] = 5 };   // tier 1
        var icon = new IconGlow("m", kills, new[] { id }, store, new NoopIconCacheHider(), a => a());

        using var cap = LogCapture.Start();
        icon.Tick();   // pac mirror fails (Failed) -- one warning, on the pac-dedicated set

        kills[id] = 10;   // tier 2 -- now the LOOSE write itself fails: a genuinely different bug
        store.FailDeployedWrite.Add((id, "card"));
        icon.Tick();

        Assert.Equal(1, cap.File.Count(l => l.Contains($"icon {id}") && l.Contains("modded.pac")));
        Assert.Equal(1, cap.File.Count(l => l.Contains($"icon {id}") && l.Contains("failed to write")));
    }

    // ---- LW-381 second half: IconGlow hands only the surfaces that actually landed in
    // modded.pac this pass to the cache hider (T10), a throwing hider never touches the apply's
    // own result (T11), and a Postponed surface is retried by the NEXT tick's dedicated
    // hide-only pass, stopping once it comes back Hidden (T12). ----

    [Fact]
    public void FlushPacBatch_HandsOnlyThisPassPatchedSurfaces_ToTheHider()
    {
        var store = new FakeIconGlowStore();
        const int id22 = 22, id51 = 51;
        byte[] cardBase = RandBytes(2001, 10), cardT1 = RandBytes(2002, 10);
        byte[] smallBase = RandBytes(2003, 6), smallT1 = RandBytes(2004, 6);
        byte[] otherBase = RandBytes(2005, 10), otherT1 = RandBytes(2006, 10);
        var cardEntry = MakeEntry(id22, "card", cardBase, new() { [1] = cardT1 });
        var smallEntry = MakeEntry(id22, "small", smallBase, new() { [1] = smallT1 });
        var otherEntry = MakeEntry(id51, "card", otherBase, new() { [1] = otherT1 });
        store.Manifest = new IconGlowManifest { SchemaVersion = 1, Icons = new() { cardEntry, smallEntry, otherEntry } };
        store.Deployed[(id22, "card")] = cardBase;
        store.Deployed[(id22, "small")] = smallBase;
        store.Deployed[(id51, "card")] = otherBase;
        store.Variants[(id22, "card", 1)] = cardT1;
        store.Variants[(id22, "small", 1)] = smallT1;
        store.Variants[(id51, "card", 1)] = otherT1;
        store.PatchPacResultOverride[(id51, "card")] = PacPatchResult.NotFound;   // id22 keeps the default Patched

        var kills = new Dictionary<int, int> { [id22] = 5, [id51] = 5 };   // both tier 1
        var hider = new RecordingIconCacheHider();
        var icon = new IconGlow("m", kills, new[] { id22, id51 }, store, hider, a => a(), hideDrawnIcons: true, healer: new NoopIconCacheHealer { Armed = true });

        icon.Tick();

        Assert.Single(hider.Calls);
        var targets = hider.Calls[0];
        Assert.Equal(2, targets.Count);
        Assert.Contains(targets, t => t.Id == id22 && t.Surface == "card" && t.Entry == cardEntry);
        Assert.Contains(targets, t => t.Id == id22 && t.Surface == "small" && t.Entry == smallEntry);
        Assert.DoesNotContain(targets, t => t.Id == id51);
    }

    [Fact]
    public void FlushPacBatch_NeverCallsHider_WhenNothingPatchedThisPass()
    {
        var store = new FakeIconGlowStore { PatchPacResult = PacPatchResult.NotFound };
        const int id = 70;
        byte[] baseBytes = RandBytes(2010, 10), t1 = RandBytes(2011, 10);
        var entry = MakeEntry(id, "card", baseBytes, new() { [1] = t1 });
        store.Manifest = new IconGlowManifest { SchemaVersion = 1, Icons = new() { entry } };
        store.Deployed[(id, "card")] = baseBytes;
        store.Variants[(id, "card", 1)] = t1;

        var kills = new Dictionary<int, int> { [id] = 5 };
        var hider = new RecordingIconCacheHider();
        var icon = new IconGlow("m", kills, new[] { id }, store, hider, a => a(), hideDrawnIcons: true, healer: new NoopIconCacheHealer { Armed = true });

        icon.Tick();

        Assert.Empty(hider.Calls);
    }

    [Fact]
    public void ThrowingHider_NeverFailsApply_SummaryLineStillLogged()
    {
        var store = new FakeIconGlowStore();
        const int id = 71;
        byte[] baseBytes = RandBytes(2020, 10), t1 = RandBytes(2021, 10);
        var entry = MakeEntry(id, "card", baseBytes, new() { [1] = t1 });
        store.Manifest = new IconGlowManifest { SchemaVersion = 1, Icons = new() { entry } };
        store.Deployed[(id, "card")] = baseBytes;
        store.Variants[(id, "card", 1)] = t1;

        var kills = new Dictionary<int, int> { [id] = 5 };   // tier 1
        var icon = new IconGlow("m", kills, new[] { id }, store, new ThrowingIconCacheHider(), a => a(), hideDrawnIcons: true, healer: new NoopIconCacheHealer { Armed = true });

        using var cap = LogCapture.Start();
        var ex = Record.Exception(() => icon.Tick());

        Assert.Null(ex);
        Assert.Equal(t1, store.Deployed[(id, "card")]);   // the loose write is unaffected by the throw
        Assert.Equal(1, cap.File.Count(l => l.Contains("IconGlow: judged") && l.Contains("wrote")));

        // _applied recorded the write despite the hider throwing: a second tick with no kill
        // change performs no further write.
        int writesAfterFirst = store.DeployedWrites.Count;
        icon.Tick();
        Assert.Equal(writesAfterFirst, store.DeployedWrites.Count);
    }

    [Fact]
    public void PendingHide_PostponedByFirstApply_RetriedByNextTick_StopsAfterHidden()
    {
        var store = new FakeIconGlowStore();
        const int id = 72;
        byte[] baseBytes = RandBytes(2030, 10), t1 = RandBytes(2031, 10);
        var entry = MakeEntry(id, "card", baseBytes, new() { [1] = t1 });
        store.Manifest = new IconGlowManifest { SchemaVersion = 1, Icons = new() { entry } };
        store.Deployed[(id, "card")] = baseBytes;
        store.Variants[(id, "card", 1)] = t1;

        var hider = new RecordingIconCacheHider();
        int callCount = 0;
        hider.OnHide = targets =>
        {
            callCount++;
            var outcome = callCount == 1
                ? new SurfaceOutcome(HideOutcome.Postponed, 0, 0, 0, 0)
                : new SurfaceOutcome(HideOutcome.Hidden, 0, 0, 1, 1);
            var result = new IconCacheHideResult();
            foreach (var t in targets) result.Set(t.Id, t.Surface, outcome);
            return result;
        };

        var kills = new Dictionary<int, int> { [id] = 5 };   // tier 1
        var captured = new List<Action>();
        var icon = new IconGlow("m", kills, new[] { id }, store, hider, a => captured.Add(a), hideDrawnIcons: true, healer: new NoopIconCacheHealer { Armed = true });

        icon.Tick();                     // schedules the normal apply
        Assert.Single(captured);
        captured[0]();                    // ApplyDiff -> FlushPacBatch -> hider call #1 -> Postponed
        captured.Clear();
        Assert.Single(hider.Calls);

        icon.Tick();                      // nothing else to apply, but a pending hide remains
        Assert.Single(captured);
        captured[0]();                     // RunPendingHidesOnly -> hider call #2 -> Hidden
        captured.Clear();
        Assert.Equal(2, hider.Calls.Count);
        Assert.Contains(hider.Calls[1], t => t.Id == id && t.Surface == "card");

        icon.Tick();                       // pending set is now empty -> nothing scheduled
        Assert.Empty(captured);
        Assert.Equal(2, hider.Calls.Count);   // no further hider calls
    }

    // ---- LW-423: the hide is on in production behind an armed healer, and these replays model the
    // disarmed case where it stays off. Fixture replays the 10:12:42 heap of
    // sitting 1: icon 37 small had 3 live parts entries and 1 texture record, the card had parts
    // entries and no texture record. ----

    private const int Lw423Id = 37;

    private static FakeHeap DrawnSmallIconHeap(out IconCacheHide hide)
    {
        var b = new IconCacheHideTests.MemBuilder(0x3_0000_0000);
        var needles = IconCacheHidePolicy.Needles(Lw423Id);
        long cardUri = b.WriteCString(needles[0].Text);
        long smallUri = b.WriteCString(needles[2].Text);
        long smallTex = b.WriteCString(needles[3].Text);
        b.WritePartsEntry(cardUri, 0x100001, 0x0101);
        for (uint i = 0; i < 3; i++) b.WritePartsEntry(smallUri, 0x380067 + i, 0x0101);
        for (int i = 0; i < 8; i++) b.WritePartsEntry(smallUri, 0, 0x0000);
        b.WriteTexRecord(smallTex, 12384, 0x11112222);
        for (int i = 0; i < 40; i++) b.WriteStrayPointer(smallTex);
        var heap = new FakeHeap((b.Base, b.ToArray()));
        hide = new IconCacheHide(heap, () => false);
        return heap;
    }

    private static FakeIconGlowStore Lw423Store(out byte[] cardBase, out byte[] smallBase)
    {
        var store = new FakeIconGlowStore();
        cardBase = RandBytes(4001, 51296);
        smallBase = RandBytes(4002, 12384);
        var c1 = RandBytes(4003, 51296); var c2 = RandBytes(4004, 51296);
        var s1 = RandBytes(4005, 12384); var s2 = RandBytes(4006, 12384);
        var cardEntry = MakeEntry(Lw423Id, "card", cardBase, new() { [1] = c1, [2] = c2 });
        var smallEntry = MakeEntry(Lw423Id, "small", smallBase, new() { [1] = s1, [2] = s2 });
        store.Manifest = new IconGlowManifest { SchemaVersion = 1, Icons = new() { cardEntry, smallEntry } };
        store.Deployed[(Lw423Id, "card")] = cardBase;
        store.Deployed[(Lw423Id, "small")] = smallBase;
        store.Variants[(Lw423Id, "card", 1)] = c1;
        store.Variants[(Lw423Id, "card", 2)] = c2;
        store.Variants[(Lw423Id, "small", 1)] = s1;
        store.Variants[(Lw423Id, "small", 2)] = s2;
        return store;
    }

    [Fact]
    public void TierChangeAfterTheSmallIconWasDrawn_LeavesTheGameCacheUntouched_ReplaysSitting1()
    {
        var heap = DrawnSmallIconHeap(out var hide);
        var store = Lw423Store(out _, out _);
        var kills = new Dictionary<int, int> { [Lw423Id] = 8 };   // tier 1
        var icon = new IconGlow("m", kills, new[] { Lw423Id }, store, hide, a => a());

        using var cap = LogCapture.Start(LogLevel.Debug);
        icon.Tick();

        Assert.Equal(store.Variants[(Lw423Id, "card", 1)], store.Deployed[(Lw423Id, "card")]);
        Assert.Equal(store.Variants[(Lw423Id, "small", 1)], store.Deployed[(Lw423Id, "small")]);
        Assert.Single(store.PatchPacBatchCalls);
        Assert.Equal(2, store.PatchPacBatchCalls[0].Count);
        Assert.Equal(0, heap.Writes);
        Assert.DoesNotContain(cap.File, l => l.Contains("IconCacheHide: icon 37"));
        Assert.Equal(1, cap.File.Count(l => l.EndsWith(IconGlow.KeptArtLine(2))));
    }

    [Fact]
    public void MidSessionTierRise_AfterTheSmallIconWasDrawn_LeavesTheGameCacheUntouched()
    {
        var heap = DrawnSmallIconHeap(out var hide);
        var store = Lw423Store(out _, out _);
        var kills = new Dictionary<int, int> { [Lw423Id] = 5 };   // tier 1
        var icon = new IconGlow("m", kills, new[] { Lw423Id }, store, hide, a => a());
        icon.Tick();

        kills[Lw423Id] = 10;   // tier 2
        using var cap = LogCapture.Start(LogLevel.Debug);
        icon.Tick();

        Assert.Equal(0, heap.Writes);
        Assert.Equal(store.Variants[(Lw423Id, "card", 2)], store.Deployed[(Lw423Id, "card")]);
        Assert.Equal(store.Variants[(Lw423Id, "small", 2)], store.Deployed[(Lw423Id, "small")]);
        Assert.Equal(2, store.PatchPacBatchCalls.Count);
        Assert.Equal(1, cap.File.Count(l => l.EndsWith(IconGlow.KeptArtLine(2))));
    }

    [Fact]
    public void PlaythroughResetDrop_WritesBaseAndLeavesTheGameCacheUntouched()
    {
        var heap = DrawnSmallIconHeap(out var hide);
        var store = Lw423Store(out var cardBase, out var smallBase);
        var kills = new Dictionary<int, int> { [Lw423Id] = 10 };
        var icon = new IconGlow("m", kills, new[] { Lw423Id }, store, hide, a => a());
        icon.Tick();

        kills.Clear();   // PlaythroughReset clears the tally in place
        using var cap = LogCapture.Start(LogLevel.Debug);
        icon.Tick();

        Assert.Equal(cardBase, store.Deployed[(Lw423Id, "card")]);
        Assert.Equal(smallBase, store.Deployed[(Lw423Id, "small")]);
        Assert.Equal(0, heap.Writes);
        Assert.Equal(1, cap.File.Count(l => l.EndsWith(IconGlow.KeptArtLine(2))));
    }

    // ---- LW-427: the hide runs only while the heal hook is armed, and the names are tracked first. ----

    private sealed class OrderRecordingHider : IIconCacheHider
    {
        private readonly NoopIconCacheHealer _healer;
        public readonly List<int> TrackedAtHide = new();
        public OrderRecordingHider(NoopIconCacheHealer healer) => _healer = healer;

        public IconCacheHideResult Hide(IReadOnlyList<IconCacheTarget> targets)
        {
            TrackedAtHide.Add(_healer.Tracked.Count);
            var result = new IconCacheHideResult();
            foreach (var t in targets) result.Set(t.Id, t.Surface, new SurfaceOutcome(HideOutcome.Postponed, 0, 0, 0, 0));
            return result;
        }
    }

    [Fact]
    public void ArmedHealer_HidesTheDrawnIcons_AndTracksBothPartsNamesFirst()
    {
        var heap = DrawnSmallIconHeap(out var hide);
        var store = Lw423Store(out _, out _);
        var kills = new Dictionary<int, int> { [Lw423Id] = 8 };
        var healer = new NoopIconCacheHealer { Armed = true };
        var icon = new IconGlow("m", kills, new[] { Lw423Id }, store, hide, a => a(), hideDrawnIcons: true, healer: healer);

        icon.Tick();

        Assert.True(heap.Writes > 0);
        var needles = IconCacheHidePolicy.Needles(Lw423Id);
        Assert.Contains(needles[0].Text, healer.Tracked);
        Assert.Contains(needles[2].Text, healer.Tracked);
        Assert.DoesNotContain(healer.Tracked, t => t.EndsWith(".tex"));
    }

    [Fact]
    public void DisarmedHealer_KeepsTheArt_AndNeverCallsTheHider()
    {
        var heap = DrawnSmallIconHeap(out var hide);
        var store = Lw423Store(out _, out _);
        var kills = new Dictionary<int, int> { [Lw423Id] = 8 };
        var healer = new NoopIconCacheHealer { Armed = false };
        var icon = new IconGlow("m", kills, new[] { Lw423Id }, store, hide, a => a(), hideDrawnIcons: true, healer: healer);

        using var cap = LogCapture.Start(LogLevel.Debug);
        icon.Tick();

        Assert.Equal(0, heap.Writes);
        Assert.Empty(healer.Tracked);
        Assert.Equal(1, cap.File.Count(l => l.EndsWith(IconGlow.KeptArtLine(2))));
    }

    [Fact]
    public void NoHealer_NeverHides_EvenWithHideDrawnIconsOn()
    {
        var hider = new RecordingIconCacheHider();
        var store = Lw423Store(out _, out _);
        var kills = new Dictionary<int, int> { [Lw423Id] = 8 };
        var icon = new IconGlow("m", kills, new[] { Lw423Id }, store, hider, a => a(), hideDrawnIcons: true);

        icon.Tick();

        Assert.Empty(hider.Calls);
    }

    [Fact]
    public void TrackRunsBeforeHide_OnTheFirstPass_AndOnThePendingRetry()
    {
        var healer = new NoopIconCacheHealer { Armed = true };
        var hider = new OrderRecordingHider(healer);
        var store = Lw423Store(out _, out _);
        var kills = new Dictionary<int, int> { [Lw423Id] = 8 };
        var captured = new List<Action>();
        var icon = new IconGlow("m", kills, new[] { Lw423Id }, store, hider, a => captured.Add(a), hideDrawnIcons: true, healer: healer);

        icon.Tick();
        captured[0](); captured.Clear();           // first pass hides, postponed
        Assert.Equal(new[] { 2 }, hider.TrackedAtHide);

        healer.Tracked.Clear();
        icon.Tick();
        captured[0](); captured.Clear();           // the pending retry
        Assert.Equal(2, hider.TrackedAtHide.Count);
        Assert.Equal(2, hider.TrackedAtHide[1]);   // tracked again before the second hide call
    }

    [Fact]
    public void PendingRetry_ForgetsTheHide_WhenTheHealerDisarms()
    {
        var healer = new NoopIconCacheHealer { Armed = true };
        var hider = new OrderRecordingHider(healer);
        var store = Lw423Store(out _, out _);
        var kills = new Dictionary<int, int> { [Lw423Id] = 8 };
        var captured = new List<Action>();
        var icon = new IconGlow("m", kills, new[] { Lw423Id }, store, hider, a => captured.Add(a), hideDrawnIcons: true, healer: healer);
        icon.Tick();
        captured[0](); captured.Clear();

        healer.Armed = false;
        icon.Tick();
        captured[0](); captured.Clear();
        icon.Tick();

        Assert.Single(hider.TrackedAtHide);
        Assert.Empty(captured);
    }

    [Fact]
    public void BusyBuilders_PostponeTheHide_UntilQuiet_WithNoTrackAndNoHiderCall()
    {
        var healer = new NoopIconCacheHealer { Armed = true, BuildersQuiet = false };
        var hider = new RecordingIconCacheHider();
        var store = Lw423Store(out _, out _);
        var kills = new Dictionary<int, int> { [Lw423Id] = 8 };
        var captured = new List<Action>();
        var icon = new IconGlow("m", kills, new[] { Lw423Id }, store, hider, a => captured.Add(a), hideDrawnIcons: true, healer: healer);
        using var cap = LogCapture.Start(LogLevel.Debug);

        icon.Tick();
        captured[0](); captured.Clear();
        icon.Tick();
        captured[0](); captured.Clear();           // a retry while still busy

        Assert.Empty(hider.Calls);
        Assert.Empty(healer.Tracked);
        Assert.Equal(1, cap.File.Count(l => l.Contains("hide waits, a screen is still loading")));

        healer.BuildersQuiet = true;
        icon.Tick();
        captured[0](); captured.Clear();

        Assert.Single(hider.Calls);
        Assert.NotEmpty(healer.Tracked);
    }

    [Fact]
    public void ParkedHide_WaitingOverTenSeconds_LogsOneStarvationLinePerLaunch()
    {
        var healer = new NoopIconCacheHealer { Armed = true, BuildersQuiet = false };
        var store = Lw423Store(out _, out _);
        var kills = new Dictionary<int, int> { [Lw423Id] = 8 };
        var captured = new List<Action>();
        long now = 1_000;
        var icon = new IconGlow("m", kills, new[] { Lw423Id }, store, new RecordingIconCacheHider(), a => captured.Add(a), hideDrawnIcons: true, healer: healer);
        icon.Clock = () => now;
        using var cap = LogCapture.Start(LogLevel.Debug);

        icon.Tick(); captured[0](); captured.Clear();
        now = 10_900;
        icon.Tick(); captured[0](); captured.Clear();
        Assert.Equal(0, cap.File.Count(l => l.Contains("have waited")));

        now = 12_000;
        icon.Tick(); captured[0](); captured.Clear();
        now = 30_000;
        icon.Tick(); captured[0](); captured.Clear();

        Assert.Equal(1, cap.File.Count(l => l.Contains("have waited 11 seconds for the menus to go quiet")));
    }

    [Fact]
    public void PendingRetry_ThatHides_LogsTheHiddenCount()
    {
        var healer = new NoopIconCacheHealer { Armed = true, BuildersQuiet = false };
        var store = Lw423Store(out _, out _);
        var kills = new Dictionary<int, int> { [Lw423Id] = 8 };
        var captured = new List<Action>();
        var icon = new IconGlow("m", kills, new[] { Lw423Id }, store, new RecordingIconCacheHider(), a => captured.Add(a), hideDrawnIcons: true, healer: healer);
        using var cap = LogCapture.Start(LogLevel.Debug);

        icon.Tick(); captured[0](); captured.Clear();
        healer.BuildersQuiet = true;
        icon.Tick(); captured[0](); captured.Clear();

        Assert.Equal(1, cap.File.Count(l => l.Contains("pending retry, hide: 2 surface(s) hidden.")));
    }
}
