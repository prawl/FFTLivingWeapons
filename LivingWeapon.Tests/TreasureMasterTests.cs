using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LivingWeapon;
using Newtonsoft.Json;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// Runtime state machine for the Treasure Master module.  All tests drive the typed
/// Tick(DateTime, bool) + ResetBattle() entry points through a FakeSparseMemory so
/// no live game process is needed.
///
/// House invariant matrix:
///   (1)  !inLive ticks issue ZERO writes (Written empty throughout).
///   (2)  Arms after stable map id + fingerprint + audit, then writes cur|0x80 to each
///        Resting addr.
///   (3)  Re-stamps after a simulated engine clear (reset byte to off -> next tick re-writes).
///   (4)  Pre-marked 0x81 byte (Held): never written at all.
///   (5)  OR-only structural assert: every value in Written has bit 0x80 set.
///   (6)  Fingerprint mismatch at arm: zero writes ever + once log.
///   (7)  Fingerprint drift mid-battle (mutate terrain bytes, advance past the revalidate
///        tick): writes CONTINUE -- identity is proven at ARM time, so a mid-battle drift is
///        informational only and never disarms (LIVE INCIDENT #4, Siedge Weald map 74).
///   (8)  Map-id flip mid-ARMED: no writes on that tick; full re-arm cycle against the
///        new map after the bad-tick threshold.
///   (9)  Foreign addrs at arm: foreign addrs are simply skipped; arms when >= minPlausible
///        ok addrs exist; foreign addrs are never written.
///   (10) Unwritable addr skipped while siblings still written.
///   (11) ResetBattle clears state, writes nothing, fresh battle re-arms.
///   (12) Stub map (no tiles): no writes, exactly zero tile-addr writes.
///   (13) Build-key mismatch: zero flag-address reads/writes ever.
///   (14) Foreign bytes while ARMED (e.g. camera pan): module stays ARMED, skips the
///        foreign addrs, holds the rest; resumes writing foreign addrs when they return
///        to Resting (camera-pan round-trip).
///
/// Plus one PinnedBuf fact through LiveMemory: hold a 6-byte tile against pinned
/// process memory, assert 0x80 lands at the target offset and neighbors are untouched.
/// </summary>
public class TreasureMasterTests
{
    // ── test-db helpers ──────────────────────────────────────────────────────────

    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "tm_" + Path.GetRandomFileName());
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>
    /// A tile address inside the module span but outside the UI arena, guaranteed
    /// to be unique per test invocation via a base + offset scheme.
    /// 0x140200000 is well inside 0x140000000..0x143000000 and outside the UI arena.
    /// </summary>
    private static long TileAddr(int slot = 0) => 0x140200000L + slot * 0x1000;

    // Addr layout written into treasure.json: each addr entry is [addrHex, offHex].
    private static string AddrJson(long addr, byte off = 0x00)
        => $@"[""{addr:x}"", ""{off:x02}""]";

    /// <summary>Build a treasure.json with one map, one tile, given addresses.</summary>
    private static TreasureDb BuildDb(
        string dir, int mapId = 74, string name = "Test Map",
        int tileX = 0, int tileY = 1,
        IEnumerable<(long addr, byte off)>? addrs = null,
        int? fpLen = null, string? fpHash = null,
        TreasureBuildKey? buildKey = null,
        bool stub = false,
        int? fpVer = null)
    {
        var addrList = addrs?.ToList() ?? new List<(long, byte)>
            { (TileAddr(0), 0x00), (TileAddr(1), 0x00) };

        string tilesJson = stub ? "[]" : $@"[{{
            ""x"": {tileX}, ""y"": {tileY},
            ""addrs"": [{string.Join(", ", addrList.Select(a => AddrJson(a.addr, a.off)))}]
        }}]";

        string fpVerStr   = fpVer  is {} ver ? $@"""fpVer"": {ver},"   : "";
        string fpLenStr   = fpLen  is {} l ? $@"""fpLen"": {l},"   : "";
        string fpHashStr  = fpHash is {} h ? $@"""fpHash"": ""{h}"","  : "";
        string bkStr      = buildKey is null ? "null" :
            $@"{{""timeDateStamp"": {buildKey.TimeDateStamp}, ""sizeOfImage"": {buildKey.SizeOfImage}}}";

        string json = $@"{{
            ""buildKey"": {bkStr},
            ""maps"": [{{
                ""mapId"": {mapId}, ""name"": ""{name}"", ""tileCount"": {(stub ? 2 : addrList.Count)},
                {fpVerStr}
                {fpLenStr}
                {fpHashStr}
                ""tiles"": {tilesJson}
            }}]
        }}";
        File.WriteAllText(Path.Combine(dir, "treasure.json"), json);
        return TreasureDb.Load(dir);
    }

    // ── terrain-fingerprint helpers ──────────────────────────────────────────────

    /// <summary>Compute the v1 FNV-1a64 hash of <paramref name="terrain"/> to seed fpHash.</summary>
    private static string TerrainFpHash(byte[] terrain)
        => TreasureMaster.Fnv1a64(terrain).ToString("x");

    /// <summary>Compute the v2 masked hash of <paramref name="terrain"/> to seed fpHash.</summary>
    private static string TerrainFpHashV2(byte[] terrain)
        => TreasureMaster.MaskedTerrainHash(terrain).ToString("x");

    // ── fake-memory builder ──────────────────────────────────────────────────────

    /// <summary>
    /// A FakeSparseMemory pre-seeded for a one-tile, two-addr test scenario:
    ///   - mapId at Offsets.LiveBattleMapId
    ///   - terrain bytes at Offsets.TerrainGrid (fpLen bytes)
    ///   - tile addrs Resting (0x00), marked Writable
    /// </summary>
    private static FakeSparseMemory BuildMem(
        byte mapId,
        byte[] terrain,
        IList<long> tileAddrs,
        byte initialByte = 0x00,
        bool addrsWritable = true)
    {
        var mem = new FakeSparseMemory();
        // map id
        mem.U8s[Offsets.LiveBattleMapId] = mapId;
        mem.ReadableAddrs.Add(Offsets.LiveBattleMapId);
        // terrain block (TryReadBytes path -- added as a region)
        mem.TerrainBlocks[Offsets.TerrainGrid] = terrain;
        // tile addrs
        foreach (long a in tileAddrs)
        {
            mem.U8s[a] = initialByte;
            mem.ReadableAddrs.Add(a);
            if (addrsWritable) mem.WritableAddrs.Add(a);
        }
        return mem;
    }

    // PE header read helper: seed the fake with what ArmAudit.ReadPeBuildKey reads.
    // ArmAudit reads U32 as 4 little-endian U8 reads.
    private static void SeedPeHeader(FakeSparseMemory mem, uint timeDateStamp, uint sizeOfImage)
    {
        // e_lfanew = U32 @ 0x140000000+0x3C
        long elfanewAddr = 0x140000000L + 0x3C;
        uint eLfanew = 0x100; // a plausible e_lfanew offset
        mem.SeedU32(elfanewAddr, eLfanew);
        // timeDateStamp = U32 @ base+e_lfanew+8
        long tsAddr = 0x140000000L + eLfanew + 8;
        mem.SeedU32(tsAddr, timeDateStamp);
        // sizeOfImage = U32 @ base+e_lfanew+0x50
        long szAddr = 0x140000000L + eLfanew + 0x50;
        mem.SeedU32(szAddr, sizeOfImage);
    }

    // ── tick helpers ─────────────────────────────────────────────────────────────

    private static void TickN(TreasureMaster tm, int n, bool inLive = true, DateTime? t = null)
    {
        var now = t ?? DateTime.Now;
        for (int i = 0; i < n; i++)
            tm.Tick(now + TimeSpan.FromMilliseconds(i * 33), inLive);
    }

    // Advance past the stability window (TreasureArmStableTicks) + a few extra for arming
    private static void StabilizeAndArm(TreasureMaster tm, int extra = 5)
        => TickN(tm, Tuning.TreasureArmStableTicks + extra);

    /// <summary>Create a TreasureMaster with alwaysOn=true so tests work in prod builds
    /// (TreasureAlwaysOn is false in prod; tests must override it).</summary>
    private static TreasureMaster Make(TreasureDb db, IGameMemory mem)
        => new(db, mem, alwaysOn: true);

    // ── (1) !inLive ticks issue ZERO writes ──────────────────────────────────────

    [Fact]
    public void InLiveFalse_zero_writes_throughout()
    {
        var dir = TempDir();
        var terrain = new byte[7];
        var db = BuildDb(dir, fpLen: terrain.Length, fpHash: TerrainFpHash(terrain));
        var addrs = new[] { TileAddr(0), TileAddr(1) };
        var mem = BuildMem(74, terrain, addrs);

        var tm = Make(db, mem);
        TickN(tm, Tuning.TreasureArmStableTicks + 20, inLive: false);

        Assert.Empty(mem.Written);
    }

    // ── (2) Arms and writes cur|0x80 to each Resting addr ────────────────────────

    [Fact]
    public void Armed_writes_OR_0x80_to_each_resting_addr()
    {
        var dir  = TempDir();
        var terrain = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };
        var addrs = new[] { TileAddr(0), TileAddr(1) };
        var db = BuildDb(dir, fpLen: terrain.Length, fpHash: TerrainFpHash(terrain),
            addrs: addrs.Select(a => (a, (byte)0x00)));
        var mem = BuildMem(74, terrain, addrs, initialByte: 0x00);

        var tm = Make(db, mem);
        StabilizeAndArm(tm);

        Assert.True(mem.Written.ContainsKey(addrs[0]));
        Assert.True(mem.Written.ContainsKey(addrs[1]));
        Assert.Equal(0x80, mem.Written[addrs[0]]);
        Assert.Equal(0x80, mem.Written[addrs[1]]);
    }

    // addr with low bit set (0x01) should write 0x81
    [Fact]
    public void Armed_writes_0x81_to_resting_addr_with_low_bit()
    {
        var dir  = TempDir();
        var terrain = new byte[] { 0xAA, 0xBB, 0x11, 0x22, 0x33, 0x44, 0x55 };
        var addr = TileAddr(2);
        var db = BuildDb(dir, fpLen: terrain.Length, fpHash: TerrainFpHash(terrain),
            addrs: new[] { (addr, (byte)0x00) });
        var mem = BuildMem(74, terrain, new[] { addr }, initialByte: 0x01);

        var tm = Make(db, mem);
        StabilizeAndArm(tm);

        Assert.Equal(0x81, mem.Written[addr]);
    }

    // ── (3) Re-stamps after a simulated engine clear ──────────────────────────────

    [Fact]
    public void Armed_restamps_after_engine_clears_mark()
    {
        var dir  = TempDir();
        var terrain = new byte[] { 0x10, 0x20, 0x30, 0x40, 0x50, 0x60, 0x70 };
        var addr = TileAddr(3);
        var db = BuildDb(dir, fpLen: terrain.Length, fpHash: TerrainFpHash(terrain),
            addrs: new[] { (addr, (byte)0x00) });
        var mem = BuildMem(74, terrain, new[] { addr });

        var tm = Make(db, mem);
        StabilizeAndArm(tm);
        Assert.True(mem.Written.ContainsKey(addr));

        // Simulate engine clearing the mark
        mem.U8s[addr] = 0x00;
        mem.Written.Clear();

        // Next tick should re-write
        tm.Tick(DateTime.Now, inLive: true);
        Assert.Equal(0x80, mem.Written[addr]);
    }

    // ── (4) Pre-marked 0x81 byte (Held): never written ───────────────────────────

    [Fact]
    public void Held_addr_never_written()
    {
        var dir  = TempDir();
        var terrain = new byte[] { 0xCA, 0xFE, 0xBA, 0xBE, 0xDE, 0xAD, 0x01 };
        var addr = TileAddr(4);
        var db = BuildDb(dir, fpLen: terrain.Length, fpHash: TerrainFpHash(terrain),
            addrs: new[] { (addr, (byte)0x00) });
        // 0x81 = Held (bit 0x80 + engine don't-care low bit)
        var mem = BuildMem(74, terrain, new[] { addr }, initialByte: 0x81);

        var tm = Make(db, mem);
        StabilizeAndArm(tm);
        // Advance many more ticks past revalidate period
        TickN(tm, Tuning.TreasureRevalidateEveryNTicks + 10);

        Assert.False(mem.Written.ContainsKey(addr));
    }

    // ── (5) OR-only structural assert ────────────────────────────────────────────

    [Fact]
    public void OR_only_structural_every_Written_value_has_0x80_set()
    {
        var dir  = TempDir();
        var terrain = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };
        var addrs = new[] { TileAddr(0), TileAddr(1) };
        var db = BuildDb(dir, fpLen: terrain.Length, fpHash: TerrainFpHash(terrain),
            addrs: addrs.Select(a => (a, (byte)0x00)));
        var mem = BuildMem(74, terrain, addrs);

        var tm = Make(db, mem);
        StabilizeAndArm(tm);
        TickN(tm, Tuning.TreasureRevalidateEveryNTicks + 20);

        Assert.NotEmpty(mem.Written);
        foreach (var kv in mem.Written)
            Assert.NotEqual(0, kv.Value & 0x80);
    }

    // ── (6) Fingerprint mismatch at arm: ARMS ANYWAY (advisory fingerprint) ────────
    // LIVE INCIDENT #5: terrain fingerprints are advisory only. Per-battle weather (rain)
    // perturbs the hashed terrain fields, so a captured hash won't match a differently-
    // weathered instance -- and there is no data to know which maps can weather. Arming is
    // gated by the per-tile resting-byte quorum (L3), not the fingerprint, so a mismatch
    // still arms; the mismatch is logged once per battle as telemetry.

    [Fact]
    public void Fingerprint_mismatch_at_arm_arms_anyway()
    {
        var dir  = TempDir();
        var realTerrain  = new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77 };
        var wrongTerrain = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x00 };
        var addr = TileAddr(5);
        // DB stores hash for realTerrain, but memory has wrongTerrain (e.g. it's raining).
        var db = BuildDb(dir, fpLen: realTerrain.Length, fpHash: TerrainFpHash(realTerrain),
            addrs: new[] { (addr, (byte)0x00) });
        var mem = BuildMem(74, wrongTerrain, new[] { addr });

        var tm = Make(db, mem);
        StabilizeAndArm(tm);

        // The resting tile addr meets quorum, so the module arms and holds despite the
        // fingerprint mismatch.
        Assert.True(mem.Written.ContainsKey(addr));
        Assert.Equal(0x80, mem.Written[addr]);
    }

    // ── (7) Fingerprint drift mid-battle: KEEP HOLDING (no disarm) ────────────────
    // LIVE INCIDENT #4 (Siedge Weald, map 74): a fingerprinted map's "static" terrain
    // fields drifted ~26 s into the same battle, and the old periodic-revalidation path
    // permanently disarmed -- killing the marks for the rest of the fight. The mid-battle
    // fingerprint check is now informational only: identity is proven at ARM time and the
    // per-tick map-id check guards chained battles, so a mid-battle drift must NOT stop
    // holding.

    [Fact]
    public void Fingerprint_drift_mid_battle_keeps_holding()
    {
        var dir  = TempDir();
        var terrain = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };
        var addr = TileAddr(6);
        var db = BuildDb(dir, fpLen: terrain.Length, fpHash: TerrainFpHash(terrain),
            addrs: new[] { (addr, (byte)0x00) });
        var mem = BuildMem(74, terrain, new[] { addr });

        var tm = Make(db, mem);
        StabilizeAndArm(tm);
        Assert.NotEmpty(mem.Written);

        // Drift the terrain bytes in memory (static fields no longer match the captured hash).
        for (int i = 0; i < terrain.Length; i++)
            terrain[i] ^= 0xFF;
        mem.TerrainBlocks[Offsets.TerrainGrid] = terrain;
        mem.Written.Clear();
        mem.U8s[addr] = 0x00;   // engine cleared the mark

        // Let the old revalidation path fully run its course (revalidate boundary + arm
        // cap): under the OLD contract the module would now be permanently BattleDisarmed.
        TickN(tm, Tuning.TreasureRevalidateEveryNTicks + Tuning.TreasureArmAttemptCap + 10);

        // Clear and prove the module is STILL holding (old behavior: zero writes here).
        mem.Written.Clear();
        mem.U8s[addr] = 0x00;
        TickN(tm, 5);

        Assert.True(mem.Written.ContainsKey(addr),
            "mid-battle fingerprint drift must keep holding, not disarm");
        Assert.Equal(0x80, mem.Written[addr]);
    }

    // ── (8) Map-id flip mid-ARMED ─────────────────────────────────────────────────

    [Fact]
    public void MapId_flip_mid_armed_suspends_writes_and_triggers_full_reset()
    {
        var dir  = TempDir();
        var terrain = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };
        var addr = TileAddr(7);
        var db = BuildDb(dir, fpLen: terrain.Length, fpHash: TerrainFpHash(terrain),
            addrs: new[] { (addr, (byte)0x00) });
        var mem = BuildMem(74, terrain, new[] { addr });

        var tm = Make(db, mem);
        StabilizeAndArm(tm);
        mem.Written.Clear();

        // Flip to an unknown map id (not in db)
        mem.U8s[Offsets.LiveBattleMapId] = 99;

        // Run bad-tick threshold ticks -- should NOT write the tile
        TickN(tm, Tuning.TreasureMapIdBadTicksToReset + 2);

        Assert.Empty(mem.Written);
    }

    // ── (9) Foreign addrs at arm: foreign addrs never written; arms when quorum met ──

    /// <summary>
    /// A foreign addr at arm time (e.g. tile off-screen when the battle starts) is simply
    /// never written -- the module arms once TreasureMinPlausibleAddrs ok addrs are visible.
    /// Key assertion: the foreign addr is never written; the resting siblings ARE written.
    /// </summary>
    [Fact]
    public void Foreign_addr_at_arm_is_skipped_and_resting_sibling_is_written()
    {
        var dir  = TempDir();
        var terrain = new byte[] { 0xAA, 0xBB, 0xCC, 0x11, 0x22, 0x33, 0x44 };
        var addrForeign = TileAddr(8);
        // Build a map with enough ok addrs to meet quorum (minPlausible=4 default, but we
        // use a multi-addr layout -- provide TreasureMinPlausibleAddrs ok addrs + 1 foreign).
        var okAddrs = Enumerable.Range(0, Tuning.TreasureMinPlausibleAddrs)
            .Select(i => TileAddr(40 + i))
            .ToArray();
        var allAddrs = okAddrs.Concat(new[] { addrForeign }).ToArray();
        var db = BuildDb(dir, fpLen: terrain.Length, fpHash: TerrainFpHash(terrain),
            addrs: allAddrs.Select(a => (a, (byte)0x00)));
        var mem = BuildMem(74, terrain, allAddrs, initialByte: 0x00);
        // Flip the foreign addr to 0x42 AFTER seeding (BuildMem seeds all as 0x00)
        mem.U8s[addrForeign] = 0x42;

        var tm = Make(db, mem);
        StabilizeAndArm(tm);

        // Foreign addr is never written
        Assert.False(mem.Written.ContainsKey(addrForeign));
        // At least one ok addr IS written
        Assert.True(okAddrs.Any(a => mem.Written.ContainsKey(a)));
    }

    /// <summary>
    /// Below quorum: all addrs foreign at arm time -> module stays ARMING, zero writes.
    /// It will eventually arm once tiles scroll back into view.
    /// </summary>
    [Fact]
    public void Foreign_all_addrs_at_arm_stays_arming_zero_writes()
    {
        var dir  = TempDir();
        var terrain = new byte[] { 0xAA, 0xBB, 0xCC, 0x11, 0x22, 0x33, 0x44 };
        var addr = TileAddr(8);
        var db = BuildDb(dir, fpLen: terrain.Length, fpHash: TerrainFpHash(terrain),
            addrs: new[] { (addr, (byte)0x00) });
        // 0x42 is Foreign (not in {0x00, 0x01, 0x80, 0x81})
        var mem = BuildMem(74, terrain, new[] { addr }, initialByte: 0x42);

        var tm = Make(db, mem);
        // Arm stable ticks + many extra -- still ARMING (quorum not met)
        TickN(tm, Tuning.TreasureArmStableTicks + Tuning.TreasureArmAttemptCap + 10);

        Assert.Empty(mem.Written);
    }

    // ── (10) Unwritable addr skipped while siblings still written ─────────────────

    [Fact]
    public void Unwritable_addr_skipped_siblings_still_written()
    {
        var dir  = TempDir();
        var terrain = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };
        var addrA = TileAddr(9);
        var addrB = TileAddr(10);
        var db = BuildDb(dir, fpLen: terrain.Length, fpHash: TerrainFpHash(terrain),
            addrs: new[] { (addrA, (byte)0x00), (addrB, (byte)0x00) });
        var mem = BuildMem(74, terrain, new[] { addrA, addrB });
        // Mark addrA as NOT writable
        mem.WritableAddrs.Remove(addrA);

        var tm = Make(db, mem);
        StabilizeAndArm(tm);

        Assert.False(mem.Written.ContainsKey(addrA));
        Assert.True(mem.Written.ContainsKey(addrB));
        Assert.Equal(0x80, mem.Written[addrB]);
    }

    // ── (11) ResetBattle clears state, writes nothing, fresh battle re-arms ───────

    [Fact]
    public void ResetBattle_clears_state_and_writes_nothing_on_first_tick()
    {
        var dir  = TempDir();
        var terrain = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };
        var addr = TileAddr(11);
        var db = BuildDb(dir, fpLen: terrain.Length, fpHash: TerrainFpHash(terrain),
            addrs: new[] { (addr, (byte)0x00) });
        var mem = BuildMem(74, terrain, new[] { addr });

        var tm = Make(db, mem);
        StabilizeAndArm(tm);
        mem.Written.Clear();

        tm.ResetBattle();

        // Immediately after ResetBattle, zero writes
        tm.Tick(DateTime.Now, inLive: true);
        Assert.Empty(mem.Written);
    }

    [Fact]
    public void ResetBattle_fresh_battle_after_reset_rearms_and_writes()
    {
        var dir  = TempDir();
        var terrain = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };
        var addr = TileAddr(15);  // unique slot to avoid cross-test contamination
        var db = BuildDb(dir, fpLen: terrain.Length, fpHash: TerrainFpHash(terrain),
            addrs: new[] { (addr, (byte)0x00) });
        var mem = BuildMem(74, terrain, new[] { addr });

        var tm = Make(db, mem);
        // First battle
        StabilizeAndArm(tm);
        Assert.NotEmpty(mem.Written);

        // Reset (simulates battle end + engine clearing marks)
        tm.ResetBattle();
        mem.Written.Clear();
        mem.U8s[addr] = 0x00;  // engine cleared the mark

        // Second battle: fresh stability + arm cycle
        TickN(tm, Tuning.TreasureArmStableTicks + 5);

        Assert.NotEmpty(mem.Written);
        Assert.Equal(0x80, mem.Written[addr]);
    }

    // ── (12) Stub map: no writes, zero tile-addr writes ───────────────────────────

    [Fact]
    public void Stub_map_no_writes()
    {
        var dir  = TempDir();
        // stub=true: no tiles, no fpHash
        var db = BuildDb(dir, stub: true);
        var addr = TileAddr(12);
        var mem  = new FakeSparseMemory();
        mem.U8s[Offsets.LiveBattleMapId] = 74;
        mem.ReadableAddrs.Add(Offsets.LiveBattleMapId);
        mem.U8s[addr] = 0x00;
        mem.ReadableAddrs.Add(addr);
        mem.WritableAddrs.Add(addr);

        var tm = Make(db, mem);
        TickN(tm, Tuning.TreasureArmStableTicks + 20);

        Assert.Empty(mem.Written);
    }

    // ── (13) Build-key mismatch: zero flag-address reads/writes ever ──────────────

    [Fact]
    public void BuildKey_mismatch_zero_writes_and_no_flag_addr_reads()
    {
        var dir  = TempDir();
        var terrain = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };
        var addr = TileAddr(13);
        // DB has buildKey {ts=0x1234, soi=0x5678}
        var db = BuildDb(dir,
            fpLen: terrain.Length, fpHash: TerrainFpHash(terrain),
            addrs: new[] { (addr, (byte)0x00) },
            buildKey: new TreasureBuildKey { TimeDateStamp = 0x1234, SizeOfImage = 0x5678 });

        var mem = BuildMem(74, terrain, new[] { addr });
        // Seed PE header with DIFFERENT values -> mismatch
        SeedPeHeader(mem, timeDateStamp: 0xAAAA, sizeOfImage: 0xBBBB);

        var tm = Make(db, mem);
        TickN(tm, Tuning.TreasureArmStableTicks + 20);

        Assert.Empty(mem.Written);
        // The flag addr should never have been touched
        Assert.False(mem.ReadCount.TryGetValue(addr, out _));
    }

    // ── FIX 1: L0 gate bypass -- PE header unreadable defers and blocks phase entry ──

    /// <summary>
    /// When the dataset has a BuildKey but the PE header bytes are NOT yet readable,
    /// CheckGlobalIdle resets _globalIdleChecked=false and returns. The tick must return
    /// immediately without entering the phase switch -- no arming, no writes, no flag reads.
    /// Once the header becomes readable with a matching key, the module proceeds normally.
    /// </summary>
    [Fact]
    public void L0_PE_header_unreadable_blocks_arming_and_writes()
    {
        var dir = TempDir();
        var terrain = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };
        var addr = TileAddr(20);
        var bk = new TreasureBuildKey { TimeDateStamp = 0xAB12, SizeOfImage = 0xCD34 };
        var db = BuildDb(dir, fpLen: terrain.Length, fpHash: TerrainFpHash(terrain),
            addrs: new[] { (addr, (byte)0x00) },
            buildKey: bk);

        var mem = BuildMem(74, terrain, new[] { addr });
        // PE header NOT seeded -- Readable returns false for all PE offsets.

        var tm = Make(db, mem);
        // Many ticks: header stays unreadable -- must never write, never arm.
        TickN(tm, Tuning.TreasureArmStableTicks + 20);

        Assert.Empty(mem.Written);
        Assert.False(mem.ReadCount.TryGetValue(addr, out _));
    }

    [Fact]
    public void L0_PE_header_becomes_readable_matching_key_module_then_arms()
    {
        var dir = TempDir();
        var terrain = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };
        var addr = TileAddr(21);
        var bk = new TreasureBuildKey { TimeDateStamp = 0xAB12, SizeOfImage = 0xCD34 };
        var db = BuildDb(dir, fpLen: terrain.Length, fpHash: TerrainFpHash(terrain),
            addrs: new[] { (addr, (byte)0x00) },
            buildKey: bk);

        var mem = BuildMem(74, terrain, new[] { addr });
        // PE header absent initially.
        var tm = Make(db, mem);
        TickN(tm, 5);
        Assert.Empty(mem.Written);

        // Now seed the matching key and run enough ticks to stabilize + arm.
        SeedPeHeader(mem, timeDateStamp: 0xAB12, sizeOfImage: 0xCD34);
        TickN(tm, Tuning.TreasureArmStableTicks + 10);

        Assert.True(mem.Written.ContainsKey(addr));
        Assert.Equal(0x80, mem.Written[addr]);
    }

    // ── (14) Foreign bytes while ARMED: stay armed, skip foreign, resume on return ──

    /// <summary>
    /// After arming cleanly, if tile addresses return Foreign bytes (camera pan, action camera),
    /// the module must stay ARMED, skip those foreign addrs on that tick, and continue
    /// writing the non-foreign addrs.  No disarm on any number of foreign bytes.
    /// </summary>
    [Fact]
    public void Armed_foreign_bytes_stays_armed_and_skips_foreign_addrs()
    {
        var dir = TempDir();
        var terrain = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };
        var addrs = new[] { TileAddr(30), TileAddr(31), TileAddr(32) };
        var db = BuildDb(dir, fpLen: terrain.Length, fpHash: TerrainFpHash(terrain),
            addrs: addrs.Select(a => (a, (byte)0x00)));
        var mem = BuildMem(74, terrain, addrs, initialByte: 0x00);

        var tm = Make(db, mem);
        StabilizeAndArm(tm);
        Assert.NotEmpty(mem.Written);

        // Mutate 2 of 3 addrs to Foreign (camera panned away), reset addrs[2] to Resting
        mem.U8s[addrs[0]] = 0x42;
        mem.U8s[addrs[1]] = 0x42;
        mem.U8s[addrs[2]] = 0x00;
        mem.Written.Clear();

        // One tick: foreign addrs skipped, non-foreign Resting addr IS written -- still ARMED
        tm.Tick(DateTime.Now, inLive: true);

        Assert.False(mem.Written.ContainsKey(addrs[0]));
        Assert.False(mem.Written.ContainsKey(addrs[1]));
        Assert.True(mem.Written.ContainsKey(addrs[2]));

        // Subsequent ticks: stays armed, continues writing addrs[2]
        mem.U8s[addrs[2]] = 0x00;
        mem.Written.Clear();
        TickN(tm, 3);
        Assert.True(mem.Written.ContainsKey(addrs[2]));
    }

    /// <summary>
    /// Camera-pan round-trip: bytes go Foreign (0x42) while off-screen -> skipped, stays ARMED;
    /// then return to Resting (0x00) when camera pans back -> written again.
    /// Proves no permanent state is set by the off-screen interval.
    /// </summary>
    [Fact]
    public void Armed_camera_pan_roundtrip_bytes_return_resting_are_written_again()
    {
        var dir = TempDir();
        var terrain = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };
        var addrs = new[] { TileAddr(33), TileAddr(34), TileAddr(35) };
        var db = BuildDb(dir, fpLen: terrain.Length, fpHash: TerrainFpHash(terrain),
            addrs: addrs.Select(a => (a, (byte)0x00)));
        var mem = BuildMem(74, terrain, addrs, initialByte: 0x00);

        var tm = Make(db, mem);
        StabilizeAndArm(tm);

        // Phase 1: all addrs go Foreign (off-screen) -- module stays armed, zero writes
        foreach (var a in addrs) mem.U8s[a] = 0x42;
        mem.Written.Clear();
        TickN(tm, 5);
        Assert.Empty(mem.Written);  // all foreign, all skipped; module still ARMED

        // Phase 2: camera pans back, bytes return to Resting (engine cleared 0x80 too)
        foreach (var a in addrs) mem.U8s[a] = 0x00;
        mem.Written.Clear();
        tm.Tick(DateTime.Now, inLive: true);

        // All addrs now Resting -> all written again
        foreach (var a in addrs)
            Assert.True(mem.Written.ContainsKey(a), $"addr {a:x} should be written after camera pan back");
    }

    // ── (15) Non-field-0 terrain mutation: hash unchanged -> stays armed ────────────
    // This is the structural fix for LIVE INCIDENT #2.
    // v2 MaskedTerrainHash ignores fields 1-6; mutating only those bytes must not
    // trigger any disarm or re-arm cycle -- the module stays ARMED with no gap in writes.

    /// <summary>
    /// Non-field-0 bytes mutate (field 1 and field 6, the incident pattern) while
    /// field-0 bytes hold still.  The v2 masked hash is unchanged -> module stays
    /// ARMED, writes continue without interruption.
    /// </summary>
    [Fact]
    public void Armed_non_field0_terrain_mutation_hash_unchanged_stays_armed()
    {
        var dir = TempDir();
        // 7-byte record: field 0 = 0x05; fields 1-6 = some initial values
        var terrain = new byte[]
        {
            0x05, 0xAA, 0x00, 0x00, 0x00, 0x00, 0x00,  // record 0
        };
        var addr = TileAddr(50);
        var db = BuildDb(dir, fpLen: terrain.Length, fpHash: TerrainFpHashV2(terrain),
            addrs: new[] { (addr, (byte)0x00) }, fpVer: 2);
        var mem = BuildMem(74, terrain, new[] { addr });

        var tm = Make(db, mem);
        StabilizeAndArm(tm);
        Assert.NotEmpty(mem.Written);

        // Mutate field 1 and field 6 (the incident pattern) -- field 0 unchanged
        terrain[1] = 0x11;  // field 1 changed
        terrain[6] = 0xFF;  // field 6 changed
        mem.TerrainBlocks[Offsets.TerrainGrid] = terrain;
        mem.Written.Clear();
        mem.U8s[addr] = 0x00;  // engine cleared mark

        // Advance past revalidate period: hash must still match -> stays ARMED, writes resume
        TickN(tm, Tuning.TreasureRevalidateEveryNTicks + 5);

        // Still writing -- module did NOT disarm or re-arm
        Assert.True(mem.Written.ContainsKey(addr),
            "non-field-0 mutation should not change masked hash; module should stay armed");
    }

    /// <summary>
    /// v2 map: the hashed field (field-0) drifts mid-battle.  Under the informational-only
    /// mid-battle check the module KEEPS HOLDING -- it does not suspend or disarm, even past
    /// the arm attempt cap (the old behavior re-proved, suspended, and eventually disarmed).
    /// </summary>
    [Fact]
    public void Armed_v2_field0_drift_mid_battle_keeps_holding()
    {
        var dir = TempDir();
        var terrain = new byte[]
        {
            0x05, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,  // record 0: field-0=0x05 (v2-hashed)
        };
        var addr = TileAddr(51);
        var db = BuildDb(dir, fpLen: terrain.Length, fpHash: TerrainFpHashV2(terrain),
            addrs: new[] { (addr, (byte)0x00) }, fpVer: 2);
        var mem = BuildMem(74, terrain, new[] { addr });

        var tm = Make(db, mem);
        StabilizeAndArm(tm);
        Assert.NotEmpty(mem.Written);

        // Drift field-0 (the v2-hashed field) -> hash no longer matches.
        terrain[0] = 0x09;
        mem.TerrainBlocks[Offsets.TerrainGrid] = terrain;

        // Let the old revalidation path fully run (boundary + arm cap -> old: BattleDisarmed).
        TickN(tm, Tuning.TreasureRevalidateEveryNTicks + Tuning.TreasureArmAttemptCap + 10);

        // Clear and prove it is STILL holding.
        mem.Written.Clear();
        mem.U8s[addr] = 0x00;
        TickN(tm, 5);

        Assert.True(mem.Written.ContainsKey(addr),
            "v2 field-0 drift must keep holding (informational check, no disarm)");
    }

    /// <summary>
    /// A mid-battle drift on the hashed field must not even briefly suspend holding: the
    /// ticks straight across the revalidate boundary still write.  The old behavior dropped
    /// to ARMING and published null while it "re-proved", which is what made the marks
    /// visibly vanish (LIVE INCIDENT #4).
    /// </summary>
    [Fact]
    public void Armed_drift_does_not_suspend_holding_across_revalidate_boundary()
    {
        var dir = TempDir();
        var terrain = new byte[]
        {
            0x07, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        };
        var addr = TileAddr(52);
        var db = BuildDb(dir, fpLen: terrain.Length, fpHash: TerrainFpHashV2(terrain),
            addrs: new[] { (addr, (byte)0x00) }, fpVer: 2);
        var mem = BuildMem(74, terrain, new[] { addr });

        var tm = Make(db, mem);
        StabilizeAndArm(tm);

        // Drift field-0, then tick just past the first revalidate boundary -- the exact
        // point where the old code dropped to ARMING and published null.
        terrain[0] = 0xFF;
        mem.TerrainBlocks[Offsets.TerrainGrid] = terrain;
        TickN(tm, Tuning.TreasureRevalidateEveryNTicks + 3);

        // Clear what happened during the crossing; the NEXT ticks must still hold (old code
        // would be re-proving in ARMING here and write nothing).
        mem.Written.Clear();
        mem.U8s[addr] = 0x00;
        TickN(tm, 3);

        Assert.True(mem.Written.ContainsKey(addr),
            "holding must not suspend across the mid-battle drift boundary");
    }

    // ── (17) v3 fingerprint: water-map regression ────────────────────────────────
    // LIVE INCIDENT #3 (Zeirchele Falls, map 83): fields {0,1,6} animate on water maps;
    // a v2 fingerprint (field-0 only) cycles with the animation and triggers spurious
    // disarm/re-arm.  A v3 fingerprint (fields {2,3,4,5}) is immune because those
    // fields are static geometry on all map types.

    /// <summary>Compute the v3 masked hash of terrain to seed fpHash for a v3 map.</summary>
    private static string TerrainFpHashV3(byte[] terrain)
        => TreasureMaster.MaskedTerrainHashV3(terrain).ToString("x");

    /// <summary>
    /// A v3 map whose field-0 (height) bytes mutate every tick (water animation) while
    /// fields {2,3,4,5} hold still MUST STAY ARMED -- this is the exact water-map regression.
    /// </summary>
    [Fact]
    public void V3_field0_animates_fields2345_static_stays_armed()
    {
        var dir = TempDir();
        // 7-byte record: field-0 will animate; fields 2-5 are static geometry.
        var terrain = new byte[]
        {
            0x10, 0x00, 0x05, 0x06, 0x07, 0x08, 0x00,  // record 0: field-0=0x10 (will change)
        };
        var addr = TileAddr(70);
        var db = BuildDb(dir, fpLen: terrain.Length, fpHash: TerrainFpHashV3(terrain),
            addrs: new[] { (addr, (byte)0x00) }, fpVer: 3);
        var mem = BuildMem(74, terrain, new[] { addr });

        var tm = Make(db, mem);
        StabilizeAndArm(tm);
        Assert.NotEmpty(mem.Written);

        // Animate field-0 repeatedly (water height cycle) -- fields 2-5 are untouched
        for (int cycle = 0; cycle < 5; cycle++)
        {
            terrain[0] = (byte)(0x10 + cycle);  // field-0 changes every frame
            mem.TerrainBlocks[Offsets.TerrainGrid] = terrain;
            mem.U8s[addr] = 0x00;   // engine cleared mark
            mem.Written.Clear();

            // Advance past a revalidate period -- v3 hash must still match
            TickN(tm, Tuning.TreasureRevalidateEveryNTicks + 5);

            Assert.True(mem.Written.ContainsKey(addr),
                $"cycle {cycle}: v3 should stay armed when only field-0 animates");
        }
    }

    /// <summary>
    /// A v3 map whose field-6 (flow) bytes animate stays armed (same root cause as field-0).
    /// </summary>
    [Fact]
    public void V3_field6_animates_fields2345_static_stays_armed()
    {
        var dir = TempDir();
        var terrain = new byte[]
        {
            0x10, 0x00, 0x05, 0x06, 0x07, 0x08, 0x01,  // record 0: field-6=0x01 (will animate)
        };
        var addr = TileAddr(71);
        var db = BuildDb(dir, fpLen: terrain.Length, fpHash: TerrainFpHashV3(terrain),
            addrs: new[] { (addr, (byte)0x00) }, fpVer: 3);
        var mem = BuildMem(74, terrain, new[] { addr });

        var tm = Make(db, mem);
        StabilizeAndArm(tm);
        Assert.NotEmpty(mem.Written);

        // Animate field-6 (flow) -- stays armed
        terrain[6] = 0xFF;
        mem.TerrainBlocks[Offsets.TerrainGrid] = terrain;
        mem.U8s[addr] = 0x00;
        mem.Written.Clear();
        TickN(tm, Tuning.TreasureRevalidateEveryNTicks + 5);

        Assert.True(mem.Written.ContainsKey(addr),
            "v3 should stay armed when only field-6 (flow) animates");
    }

    /// <summary>
    /// A v3 map whose field-3 (a hashed "static geometry" field) drifts mid-battle KEEPS
    /// HOLDING.  LIVE INCIDENT #4 proved these fields are not always battle-invariant
    /// (Siedge Weald, map 74: fields {2,3,4,5} changed ~26 s into the same battle); a
    /// mid-battle drift is no longer treated as a different map.  The arm-time fingerprint
    /// gate still proves identity before the first hold; only the mid-battle re-check is
    /// downgraded to informational.
    /// </summary>
    [Fact]
    public void V3_static_field_drift_mid_battle_keeps_holding()
    {
        var dir = TempDir();
        var terrain = new byte[]
        {
            0x10, 0x00, 0x05, 0x06, 0x07, 0x08, 0x00,  // record 0: field-3=0x06 (v3-hashed)
        };
        var addr = TileAddr(72);
        var db = BuildDb(dir, fpLen: terrain.Length, fpHash: TerrainFpHashV3(terrain),
            addrs: new[] { (addr, (byte)0x00) }, fpVer: 3);
        var mem = BuildMem(74, terrain, new[] { addr });

        var tm = Make(db, mem);
        StabilizeAndArm(tm);
        Assert.NotEmpty(mem.Written);

        // Drift field-3 (a v3-hashed field) -- the exact live-incident pattern.
        terrain[3] = 0xAA;
        mem.TerrainBlocks[Offsets.TerrainGrid] = terrain;

        // Let the old revalidation path fully run (boundary + arm cap -> old: BattleDisarmed).
        TickN(tm, Tuning.TreasureRevalidateEveryNTicks + Tuning.TreasureArmAttemptCap + 10);

        // Clear and prove it is STILL holding.
        mem.Written.Clear();
        mem.U8s[addr] = 0x00;
        TickN(tm, 5);

        Assert.True(mem.Written.ContainsKey(addr),
            "v3 static-field drift mid-battle must keep holding (LIVE INCIDENT #4)");
        Assert.Equal(0x80, mem.Written[addr]);
    }

    /// <summary>
    /// FingerprintMatches dispatches on fpVer: a v2 map uses v2 hash (MaskedTerrainHash),
    /// a v3 map uses v3 hash (MaskedTerrainHashV3).  A v3 map with a v2-computed fpHash
    /// does NOT match, and vice versa (the two hashes are different for the same raw bytes).
    /// </summary>
    [Fact]
    public void FingerprintMatches_dispatch_v2_vs_v3()
    {
        var dir = TempDir();
        // Same terrain; v2 and v3 hashes will differ for this buffer
        var terrain = new byte[]
        {
            0x10, 0xAA, 0x05, 0x06, 0x07, 0x08, 0xBB,
        };

        var addrV2 = TileAddr(73);
        var addrV3 = TileAddr(74);

        // v2 map: fpVer=2, fpHash from v2 formula
        var dbV2 = BuildDb(dir, mapId: 74, fpLen: terrain.Length,
            fpHash: TerrainFpHashV2(terrain),
            addrs: new[] { (addrV2, (byte)0x00) }, fpVer: 2);

        var memV2 = BuildMem(74, terrain, new[] { addrV2 });
        var tmV2 = Make(dbV2, memV2);
        StabilizeAndArm(tmV2);
        Assert.True(memV2.Written.ContainsKey(addrV2),
            "v2 map with v2 hash should arm");

        var dir3 = TempDir();
        // v3 map: fpVer=3, fpHash from v3 formula
        var dbV3 = BuildDb(dir3, mapId: 74, fpLen: terrain.Length,
            fpHash: TerrainFpHashV3(terrain),
            addrs: new[] { (addrV3, (byte)0x00) }, fpVer: 3);

        var memV3 = BuildMem(74, terrain, new[] { addrV3 });
        var tmV3 = Make(dbV3, memV3);
        StabilizeAndArm(tmV3);
        Assert.True(memV3.Written.ContainsKey(addrV3),
            "v3 map with v3 hash should arm");
    }

    /// <summary>
    /// A v3 map with a v2-computed fpHash (wrong version's hash) ARMS ANYWAY -- the
    /// fingerprint is advisory, so a hash mismatch (wrong version or weather drift) no longer
    /// blocks arming; the per-tile quorum is the gate.
    /// </summary>
    [Fact]
    public void FingerprintMatches_v3_map_with_v2_hash_arms_anyway()
    {
        var dir = TempDir();
        var terrain = new byte[]
        {
            0x10, 0xAA, 0x05, 0x06, 0x07, 0x08, 0xBB,
        };
        var addr = TileAddr(75);
        // fpVer=3 but fpHash is computed with v2 formula -> mismatch (advisory only now).
        var db = BuildDb(dir, fpLen: terrain.Length,
            fpHash: TerrainFpHashV2(terrain),   // wrong hash for v3
            addrs: new[] { (addr, (byte)0x00) }, fpVer: 3);
        var mem = BuildMem(74, terrain, new[] { addr });

        var tm = Make(db, mem);
        StabilizeAndArm(tm);

        Assert.True(mem.Written.ContainsKey(addr));
        Assert.Equal(0x80, mem.Written[addr]);
    }

    // ── PinnedBuf fact: 0x80 lands at target, neighbors untouched ─────────────────

    /// <summary>
    /// Drive a 6-byte pinned buffer through LiveMemory (RPM/WPM on our own process),
    /// asserting the mark bit lands at offset 0 and bytes 1-5 are untouched.
    /// Mirrors MemBitsTests and BarrageTests pattern.
    /// </summary>
    [Fact]
    public void PinnedBuf_hold_0x80_lands_and_neighbors_untouched()
    {
        using var pin = PinnedBuf.Of(6);
        pin.Bytes[0] = 0x00;   // target -- should become 0x80
        pin.Bytes[1] = 0x42;   // neighbor
        pin.Bytes[2] = 0x11;
        pin.Bytes[3] = 0xFE;
        pin.Bytes[4] = 0x00;
        pin.Bytes[5] = 0x55;

        var live = new LiveMemory();
        // Guard is satisfied: our own process memory is always Writable + Readable
        Assert.True(live.Writable(pin.Addr, 1));
        int cur = live.U8(pin.Addr);
        byte want = TreasureMaster.WantWrite((byte)cur);
        live.W8(pin.Addr, want);

        Assert.Equal(0x80, pin.Bytes[0]);
        Assert.Equal(0x42, pin.Bytes[1]);
        Assert.Equal(0x11, pin.Bytes[2]);
        Assert.Equal(0xFE, pin.Bytes[3]);
        Assert.Equal(0x00, pin.Bytes[4]);
        Assert.Equal(0x55, pin.Bytes[5]);
    }

    // ── (16) Hot-reload seam tests ────────────────────────────────────────────

    /// <summary>Stamp unchanged across TreasureStampCheckTicks ticks -> load never re-invoked.</summary>
    [Fact]
    public void Reload_stamp_unchanged_load_not_reinvoked()
    {
        var dir = TempDir();
        var terrain = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };
        var addr = TileAddr(60);
        var db = BuildDb(dir, fpLen: terrain.Length, fpHash: TerrainFpHash(terrain),
            addrs: new[] { (addr, (byte)0x00) });
        var mem = BuildMem(74, terrain, new[] { addr });

        int loadCount = 0;
        DateTime? stamp = DateTime.UtcNow;
        var tm = new TreasureMaster(
            load: () => { loadCount++; return db; },
            datasetStamp: () => stamp,
            mem: mem,
            alwaysOn: true);

        // Run well past the check interval; stamp never changes.
        TickN(tm, Tuning.TreasureStampCheckTicks * 3);

        // Initial load counts as 1 (eager at first tick).
        Assert.Equal(1, loadCount);
    }

    /// <summary>Stamp changes -> load re-invoked, state cleared, new map arms.</summary>
    [Fact]
    public void Reload_stamp_change_triggers_reload_and_state_cleared()
    {
        var dir = TempDir();
        var terrain = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };
        var addr = TileAddr(61);
        var db = BuildDb(dir, mapId: 74, fpLen: terrain.Length, fpHash: TerrainFpHash(terrain),
            addrs: new[] { (addr, (byte)0x00) });
        var mem = BuildMem(74, terrain, new[] { addr });

        int loadCount = 0;
        DateTime? stamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var tm = new TreasureMaster(
            load: () => { loadCount++; return db; },
            datasetStamp: () => stamp,
            mem: mem,
            alwaysOn: true);

        // Stabilize and arm on the initial dataset.
        StabilizeAndArm(tm);
        Assert.Equal(1, loadCount);
        Assert.NotEmpty(mem.Written);

        // Advance stamp -> triggers reload.
        stamp = new DateTime(2026, 1, 1, 0, 1, 0, DateTimeKind.Utc);
        // Clear written log so we can observe the re-arm writes separately.
        mem.Written.Clear();
        mem.U8s[addr] = 0x00;

        // Run enough ticks to cross the check interval AND re-stabilize + arm.
        TickN(tm, Tuning.TreasureStampCheckTicks + Tuning.TreasureArmStableTicks + 10);

        Assert.Equal(2, loadCount);
        // After reload + re-arm, writes resume.
        Assert.NotEmpty(mem.Written);
    }

    /// <summary>
    /// Empty dataset at boot (load returns empty db), then stamp changes to a populated
    /// dataset -> module un-idles and arms on the same map id.
    /// </summary>
    [Fact]
    public void Reload_empty_at_boot_then_populated_dataset_un_idles_and_arms()
    {
        var dir = TempDir();
        var terrain = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };
        var addr = TileAddr(62);

        // Two distinct db instances: empty, then populated.
        var emptyDb = TreasureDb.MakeEmpty();
        var populatedDb = BuildDb(dir, mapId: 74, fpLen: terrain.Length,
            fpHash: TerrainFpHash(terrain), addrs: new[] { (addr, (byte)0x00) });

        var mem = BuildMem(74, terrain, new[] { addr });

        int loadCount = 0;
        DateTime? stamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        bool returnPopulated = false;

        var tm = new TreasureMaster(
            load: () => { loadCount++; return returnPopulated ? populatedDb : emptyDb; },
            datasetStamp: () => stamp,
            mem: mem,
            alwaysOn: true);

        // Many ticks with empty dataset: module stays idle (globally), no writes.
        TickN(tm, Tuning.TreasureStampCheckTicks + 10);
        Assert.Empty(mem.Written);

        // Now switch to populated and bump stamp.
        returnPopulated = true;
        stamp = new DateTime(2026, 1, 1, 0, 1, 0, DateTimeKind.Utc);

        // Run enough ticks to detect stamp change, reload, un-idle, stabilize + arm.
        TickN(tm, Tuning.TreasureStampCheckTicks + Tuning.TreasureArmStableTicks + 10);

        Assert.True(loadCount >= 2, $"Expected at least 2 loads, got {loadCount}");
        Assert.NotEmpty(mem.Written);
    }

    /// <summary>
    /// Reload with a mismatched build key -> global disarm re-evaluated (module stays/re-idles).
    /// </summary>
    [Fact]
    public void Reload_mismatched_build_key_re_idles_module()
    {
        var dir = TempDir();
        var terrain = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };
        var addr = TileAddr(63);

        // Dataset whose build key does NOT match the PE header in mem.
        var mismatchDb = BuildDb(dir, mapId: 74, fpLen: terrain.Length,
            fpHash: TerrainFpHash(terrain), addrs: new[] { (addr, (byte)0x00) },
            buildKey: new TreasureBuildKey { TimeDateStamp = 0x1111, SizeOfImage = 0x2222 });

        var mem = BuildMem(74, terrain, new[] { addr });
        // Seed PE header with DIFFERENT values -> mismatch.
        SeedPeHeader(mem, timeDateStamp: 0xAAAA, sizeOfImage: 0xBBBB);

        int loadCount = 0;
        DateTime? stamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var tm = new TreasureMaster(
            load: () => { loadCount++; return mismatchDb; },
            datasetStamp: () => stamp,
            mem: mem,
            alwaysOn: true);

        // Run enough to detect stamp changes and reload multiple times; key always mismatches.
        TickN(tm, Tuning.TreasureStampCheckTicks + Tuning.TreasureArmStableTicks + 10);
        stamp = new DateTime(2026, 1, 1, 0, 1, 0, DateTimeKind.Utc);
        TickN(tm, Tuning.TreasureStampCheckTicks + Tuning.TreasureArmStableTicks + 10);

        // Key always mismatches -> zero flag-address writes ever.
        Assert.Empty(mem.Written);
        Assert.False(mem.ReadCount.TryGetValue(addr, out _),
            "flag address should never be read when build key mismatches");
    }

    // ── map-id-only mode (water/lava maps) ────────────────────────────────────
    // A map whose stored fpHash is null and fpVer is 0 is a map-id-only map.
    // The terrain fingerprint gate is entirely absent: it arms on map-id + address
    // quorum alone, and never disarms on terrain change.
    //
    // Invariant matrix:
    //   (MIO-1) Arms without fingerprint match, even when the live terrain hash is garbage.
    //   (MIO-2) Does NOT disarm on terrain change mid-battle (the water regression).
    //   (MIO-3) A map-id flip still disarms a map-id-only map (live wrong-map guard stays).
    //   (MIO-4) A fingerprinted map ARMS ANYWAY on a fingerprint mismatch (advisory only).
    //   (MIO-5) Bake: a map-id-only map (verified tiles, null fpHash) ships alongside a
    //            fingerprinted map; both appear in the output.

    /// <summary>Build a map-id-only db (fpVer=0, fpHash=null, no fpLen).</summary>
    private static TreasureDb BuildMapIdOnlyDb(
        string dir, int mapId = 83, string name = "Zeirchele Falls",
        int tileX = 2, int tileY = 3,
        IEnumerable<(long addr, byte off)>? addrs = null)
    {
        var addrList = addrs?.ToList() ?? new List<(long, byte)>
            { (TileAddr(100), 0x00), (TileAddr(101), 0x00), (TileAddr(102), 0x00) };
        string tilesJson = $@"[{{
            ""x"": {tileX}, ""y"": {tileY},
            ""addrs"": [{string.Join(", ", addrList.Select(a => AddrJson(a.addr, a.off)))}]
        }}]";
        string json = $@"{{
            ""buildKey"": null,
            ""maps"": [{{
                ""mapId"": {mapId}, ""name"": ""{name}"", ""tileCount"": 1,
                ""fpVer"": 0,
                ""tiles"": {tilesJson}
            }}]
        }}";
        File.WriteAllText(Path.Combine(dir, "treasure.json"), json);
        return TreasureDb.Load(dir);
    }

    /// <summary>
    /// (MIO-1) A map-id-only map arms WITHOUT a fingerprint match, even when the live
    /// terrain is garbage/changing (simulating Zeirchele Falls water animation).
    /// The module must reach ARMED and write its tile addresses.
    /// </summary>
    [Fact]
    public void MapIdOnly_arms_without_fingerprint_match_even_with_garbage_terrain()
    {
        var dir  = TempDir();
        var addrs = new[] { TileAddr(100), TileAddr(101), TileAddr(102) };
        var db = BuildMapIdOnlyDb(dir,
            addrs: addrs.Select(a => (a, (byte)0x00)));

        // Terrain is random garbage -- no valid fingerprint can ever match.
        var garbageTerrain = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x12, 0x34, 0x56 };
        var mem = new FakeSparseMemory();
        mem.U8s[Offsets.LiveBattleMapId] = 83;
        mem.ReadableAddrs.Add(Offsets.LiveBattleMapId);
        mem.TerrainBlocks[Offsets.TerrainGrid] = garbageTerrain;
        foreach (var a in addrs)
        {
            mem.U8s[a] = 0x00;
            mem.ReadableAddrs.Add(a);
            mem.WritableAddrs.Add(a);
        }

        var tm = Make(db, mem);
        StabilizeAndArm(tm);

        // Must have written to all three addresses despite garbage terrain.
        foreach (var a in addrs)
        {
            Assert.True(mem.Written.ContainsKey(a),
                $"map-id-only: addr {a:x} should be written even with garbage terrain");
            Assert.Equal(0x80, mem.Written[a]);
        }
    }

    /// <summary>
    /// (MIO-2) A map-id-only map that is already ARMED must NOT disarm on terrain change
    /// mid-battle.  This is the exact water regression: fields {0,1,6} animate every frame,
    /// but the map-id-only path has no terrain revalidation loop at all.
    /// </summary>
    [Fact]
    public void MapIdOnly_does_not_disarm_on_terrain_change_mid_battle()
    {
        var dir  = TempDir();
        var addrs = new[] { TileAddr(103), TileAddr(104), TileAddr(105) };
        var db = BuildMapIdOnlyDb(dir, mapId: 83, tileX: 2, tileY: 3,
            addrs: addrs.Select(a => (a, (byte)0x00)));

        var terrain = new byte[] { 0x10, 0x00, 0x05, 0x06, 0x07, 0x08, 0x01 };
        var mem = new FakeSparseMemory();
        mem.U8s[Offsets.LiveBattleMapId] = 83;
        mem.ReadableAddrs.Add(Offsets.LiveBattleMapId);
        mem.TerrainBlocks[Offsets.TerrainGrid] = terrain;
        foreach (var a in addrs)
        {
            mem.U8s[a] = 0x00;
            mem.ReadableAddrs.Add(a);
            mem.WritableAddrs.Add(a);
        }

        var tm = Make(db, mem);
        StabilizeAndArm(tm);
        Assert.NotEmpty(mem.Written);

        // Continuously mutate the terrain (water animation cycling all fields).
        for (int cycle = 0; cycle < 5; cycle++)
        {
            terrain[0] = (byte)(0x10 + cycle);
            terrain[1] = (byte)(0xAA + cycle);
            terrain[6] = (byte)(0xFF - cycle);
            mem.TerrainBlocks[Offsets.TerrainGrid] = terrain;
            // Engine clears the mark between cycles.
            foreach (var a in addrs) mem.U8s[a] = 0x00;
            mem.Written.Clear();

            TickN(tm, Tuning.TreasureRevalidateEveryNTicks + 5);

            foreach (var a in addrs)
                Assert.True(mem.Written.ContainsKey(a),
                    $"cycle {cycle}: map-id-only map should stay armed despite terrain animation");
        }
    }

    /// <summary>
    /// (MIO-3) Even though there is no terrain gate, the live wrong-map guard (map-id
    /// check every tick) still disarms a map-id-only map when the map id changes.
    /// </summary>
    [Fact]
    public void MapIdOnly_map_id_flip_still_disarms()
    {
        var dir  = TempDir();
        var addrs = new[] { TileAddr(106), TileAddr(107), TileAddr(108) };
        var db = BuildMapIdOnlyDb(dir, mapId: 83,
            addrs: addrs.Select(a => (a, (byte)0x00)));

        var terrain = new byte[] { 0x10, 0x00, 0x05, 0x06, 0x07, 0x08, 0x01 };
        var mem = new FakeSparseMemory();
        mem.U8s[Offsets.LiveBattleMapId] = 83;
        mem.ReadableAddrs.Add(Offsets.LiveBattleMapId);
        mem.TerrainBlocks[Offsets.TerrainGrid] = terrain;
        foreach (var a in addrs)
        {
            mem.U8s[a] = 0x00;
            mem.ReadableAddrs.Add(a);
            mem.WritableAddrs.Add(a);
        }

        var tm = Make(db, mem);
        StabilizeAndArm(tm);
        mem.Written.Clear();

        // Flip to an unknown map id.
        mem.U8s[Offsets.LiveBattleMapId] = 77;

        // Run through the bad-tick threshold -- must stop writing.
        TickN(tm, Tuning.TreasureMapIdBadTicksToReset + 2);

        Assert.Empty(mem.Written);
    }

    /// <summary>
    /// (MIO-4) A fingerprinted map (fpHash not null) ARMS ANYWAY on a fingerprint mismatch,
    /// exactly like a map-id-only map: the fingerprint is advisory, so the only gate is the
    /// per-tile resting quorum. A fingerprinted map and a map-id-only map now behave the same
    /// at arm time when their terrain doesn't match the captured hash (weather drift).
    /// </summary>
    [Fact]
    public void Fingerprinted_map_arms_despite_fingerprint_mismatch_like_mapidonly()
    {
        var dir  = TempDir();
        var realTerrain  = new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77 };
        var wrongTerrain = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x00 };
        var addr = TileAddr(109);
        // Explicitly a fingerprinted (non-map-id-only) map.
        var db = BuildDb(dir, mapId: 74, fpLen: realTerrain.Length,
            fpHash: TerrainFpHash(realTerrain),
            addrs: new[] { (addr, (byte)0x00) });

        // Memory has the wrong terrain -- fingerprint mismatch (e.g. it's raining).
        var mem = BuildMem(74, wrongTerrain, new[] { addr });

        var tm = Make(db, mem);
        StabilizeAndArm(tm);

        Assert.True(mem.Written.ContainsKey(addr));
        Assert.Equal(0x80, mem.Written[addr]);
    }

    // ── FastHold tests ────────────────────────────────────────────────────────────
    // No real threads are spawned (Start/StartFastHold never called).
    // All tests drive HoldOnce() directly -- the thread-safe property is argued by
    // construction: TileHolder is stateless and OR-only, so concurrent callers are safe.

    /// <summary>
    /// FastHold.HoldOnce with a published map writes 0x80 to the tile addresses via
    /// the underlying TileHolder (same fake-memory path as the normal tick).
    /// </summary>
    [Fact]
    public void FastHold_HoldOnce_with_published_map_writes_tile_addresses()
    {
        var dir = TempDir();
        var terrain = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };
        var addrs = new[] { TileAddr(200), TileAddr(201) };
        var db = BuildDb(dir, fpLen: terrain.Length, fpHash: TerrainFpHash(terrain),
            addrs: addrs.Select(a => (a, (byte)0x00)));
        var mem = BuildMem(74, terrain, addrs, initialByte: 0x00);

        var holder = new TileHolder(mem);
        var fh = new FastHold(holder, intervalMs: 8);

        // Null published: HoldOnce writes nothing.
        fh.HoldOnce();
        Assert.Empty(mem.Written);

        // Publish a map: HoldOnce writes 0x80 to each resting addr.
        var map = db.Maps[0];
        fh.Publish(map);
        fh.HoldOnce();

        Assert.True(mem.Written.ContainsKey(addrs[0]));
        Assert.True(mem.Written.ContainsKey(addrs[1]));
        Assert.Equal(0x80, mem.Written[addrs[0]]);
        Assert.Equal(0x80, mem.Written[addrs[1]]);
    }

    /// <summary>
    /// FastHold.HoldOnce with null published writes nothing even after a map was
    /// previously published (Publish(null) clears the held reference).
    /// </summary>
    [Fact]
    public void FastHold_HoldOnce_with_null_published_writes_nothing()
    {
        var dir = TempDir();
        var terrain = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };
        var addrs = new[] { TileAddr(202), TileAddr(203) };
        var db = BuildDb(dir, fpLen: terrain.Length, fpHash: TerrainFpHash(terrain),
            addrs: addrs.Select(a => (a, (byte)0x00)));
        var mem = BuildMem(74, terrain, addrs, initialByte: 0x00);

        var holder = new TileHolder(mem);
        var fh = new FastHold(holder, intervalMs: 8);

        // Publish then clear.
        fh.Publish(db.Maps[0]);
        fh.Publish(null);

        fh.HoldOnce();
        Assert.Empty(mem.Written);
    }

    /// <summary>
    /// Once ARMED, FastHold.HoldOnce writes the tile addresses (the map was published
    /// by TreasureMaster's Tick path on transition to Phase.Armed).
    /// </summary>
    [Fact]
    public void FastHold_armed_state_HoldOnce_writes_tile_addresses()
    {
        var dir = TempDir();
        var terrain = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };
        var addrs = new[] { TileAddr(210), TileAddr(211) };
        var db = BuildDb(dir, fpLen: terrain.Length, fpHash: TerrainFpHash(terrain),
            addrs: addrs.Select(a => (a, (byte)0x00)));
        var mem = BuildMem(74, terrain, addrs, initialByte: 0x00);

        var tm = Make(db, mem);
        StabilizeAndArm(tm);

        // Clear written log so only HoldOnce writes are counted.
        mem.Written.Clear();
        // Reset addrs so they look Resting again (engine cleared the mark).
        foreach (var a in addrs) mem.U8s[a] = 0x00;

        tm.FastHold.HoldOnce();

        Assert.True(mem.Written.ContainsKey(addrs[0]),
            "FastHold.HoldOnce should write addr[0] when phase is Armed");
        Assert.True(mem.Written.ContainsKey(addrs[1]),
            "FastHold.HoldOnce should write addr[1] when phase is Armed");
    }

    /// <summary>
    /// After ResetBattle(), FastHold.HoldOnce writes nothing (null was published
    /// by ResetBattle on the battle-exit edge).
    /// </summary>
    [Fact]
    public void FastHold_after_ResetBattle_HoldOnce_writes_nothing()
    {
        var dir = TempDir();
        var terrain = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };
        var addrs = new[] { TileAddr(212), TileAddr(213) };
        var db = BuildDb(dir, fpLen: terrain.Length, fpHash: TerrainFpHash(terrain),
            addrs: addrs.Select(a => (a, (byte)0x00)));
        var mem = BuildMem(74, terrain, addrs, initialByte: 0x00);

        var tm = Make(db, mem);
        StabilizeAndArm(tm);

        tm.ResetBattle();
        mem.Written.Clear();
        foreach (var a in addrs) mem.U8s[a] = 0x00;

        tm.FastHold.HoldOnce();

        Assert.Empty(mem.Written);
    }

    /// <summary>
    /// After a !inLive tick, FastHold.HoldOnce writes nothing (null was published
    /// by the !inLive early return in Tick).
    /// </summary>
    [Fact]
    public void FastHold_after_inLive_false_tick_HoldOnce_writes_nothing()
    {
        var dir = TempDir();
        var terrain = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };
        var addrs = new[] { TileAddr(214), TileAddr(215) };
        var db = BuildDb(dir, fpLen: terrain.Length, fpHash: TerrainFpHash(terrain),
            addrs: addrs.Select(a => (a, (byte)0x00)));
        var mem = BuildMem(74, terrain, addrs, initialByte: 0x00);

        var tm = Make(db, mem);
        // Arm the module in live battle.
        StabilizeAndArm(tm);

        // Now tick with inLive=false.
        tm.Tick(DateTime.Now, inLive: false);
        mem.Written.Clear();
        foreach (var a in addrs) mem.U8s[a] = 0x00;

        tm.FastHold.HoldOnce();

        Assert.Empty(mem.Written);
    }

    // ── (18) battleDisplayed gate: formation / enemy-turn coverage ────────────────
    // The Treasure Master module gates on a single bool from the Engine. Prior to this
    // fix the Engine passed InLiveBattle, which flickered false during enemy turns and
    // animations (battleMode==1 without slot0==0xFF). This kept resetting _stableTicks
    // and prevented arming mid-battle. The Engine now passes BattleDisplayed instead:
    // slot9==0xFFFFFFFF && battleMode!=0. Formation and enemy turns both satisfy that,
    // so the module receives a stable true throughout the battle.
    //
    // The module's Tick(DateTime, bool) interface is unchanged; these tests drive it
    // directly with the semantically correct gate value.

    /// <summary>
    /// Continuous gate=true (formation or any battle mode) arms in exactly
    /// TreasureArmStableTicks ticks -- no flicker resets the counter.
    /// </summary>
    [Fact]
    public void BattleDisplayed_continuous_true_arms_in_stable_ticks()
    {
        var dir = TempDir();
        var terrain = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };
        var addr = TileAddr(220);
        var db = BuildDb(dir, fpLen: terrain.Length, fpHash: TerrainFpHash(terrain),
            addrs: new[] { (addr, (byte)0x00) });
        var mem = BuildMem(74, terrain, new[] { addr });

        var tm = Make(db, mem);

        // Exactly TreasureArmStableTicks - 1 ticks: not yet armed.
        TickN(tm, Tuning.TreasureArmStableTicks - 1, inLive: true);
        Assert.Empty(mem.Written);

        // One more tick tips past the threshold -- now armed + write.
        TickN(tm, 6, inLive: true);
        Assert.NotEmpty(mem.Written);
        Assert.Equal(0x80, mem.Written[addr]);
    }

    /// <summary>
    /// Gate=false (world map) resets stability and publishes null -- no writes and
    /// FastHold stops holding.
    /// </summary>
    [Fact]
    public void BattleDisplayed_false_resets_stability_and_publishes_null()
    {
        var dir = TempDir();
        var terrain = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };
        var addr = TileAddr(221);
        var db = BuildDb(dir, fpLen: terrain.Length, fpHash: TerrainFpHash(terrain),
            addrs: new[] { (addr, (byte)0x00) });
        var mem = BuildMem(74, terrain, new[] { addr });

        var tm = Make(db, mem);

        // Arm the module.
        StabilizeAndArm(tm);
        Assert.NotEmpty(mem.Written);

        // Transition to world map (battleDisplayed=false).
        tm.Tick(DateTime.Now, inLive: false);

        // Clear and verify: no further writes; HoldOnce produces nothing (null published).
        mem.Written.Clear();
        foreach (var a in new[] { addr }) mem.U8s[a] = 0x00;

        tm.FastHold.HoldOnce();
        Assert.Empty(mem.Written);
    }

    /// <summary>
    /// After gate goes false (world map) then true again (new battle / formation),
    /// the module re-arms from scratch -- the stability counter was reset.
    /// </summary>
    [Fact]
    public void BattleDisplayed_false_then_true_rearms_from_scratch()
    {
        var dir = TempDir();
        var terrain = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };
        var addr = TileAddr(222);
        var db = BuildDb(dir, fpLen: terrain.Length, fpHash: TerrainFpHash(terrain),
            addrs: new[] { (addr, (byte)0x00) });
        var mem = BuildMem(74, terrain, new[] { addr });

        var tm = Make(db, mem);

        // First battle: arm.
        StabilizeAndArm(tm);
        Assert.NotEmpty(mem.Written);

        // World map: gate=false resets state.
        tm.Tick(DateTime.Now, inLive: false);
        tm.ResetBattle();
        mem.Written.Clear();
        mem.U8s[addr] = 0x00;

        // Not yet armed on the very next true tick (stability counter was zeroed).
        tm.Tick(DateTime.Now, inLive: true);
        Assert.Empty(mem.Written);

        // Full stability window passes -> arms and writes again.
        TickN(tm, Tuning.TreasureArmStableTicks + 5, inLive: true);
        Assert.NotEmpty(mem.Written);
        Assert.Equal(0x80, mem.Written[addr]);
    }

    /// <summary>
    /// Simulates the pre-fix flicker: the gate alternates false/true repeatedly
    /// (battleMode==1 without excuse each odd tick, like an enemy turn without slot0==0xFF).
    /// With the old InLiveBattle gate this reset _stableTicks on every odd tick.
    /// With the new battleDisplayed gate (always true while the map is displayed),
    /// the module receives continuous true and arms normally.
    /// This test drives Tick with continuous true -- asserting it arms in <= stableTicks + overhead.
    /// </summary>
    [Fact]
    public void BattleDisplayed_stable_gate_arms_despite_what_inLive_would_have_been()
    {
        var dir = TempDir();
        var terrain = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };
        var addr = TileAddr(223);
        var db = BuildDb(dir, fpLen: terrain.Length, fpHash: TerrainFpHash(terrain),
            addrs: new[] { (addr, (byte)0x00) });
        var mem = BuildMem(74, terrain, new[] { addr });

        var tm = Make(db, mem);

        // Drive with continuous gate=true (what battleDisplayed produces during a battle).
        // With inLive this would alternately be false on enemy turns, resetting _stableTicks.
        // With battleDisplayed the counter accumulates uninterrupted.
        TickN(tm, Tuning.TreasureArmStableTicks + 10, inLive: true);

        Assert.NotEmpty(mem.Written);
        Assert.Equal(0x80, mem.Written[addr]);
    }

    // ── RingGate.ScholarRingEquipped detector (BATTLE-ONLY) ──────────────────────
    // The gate opens iff a roster slot wears the Scholar's Ring (260) AND that unit is
    // DEPLOYED in the live battle band (matched by brave/faith + level drift). A benched
    // ring-bearer never opens the gate -- equipment on a unit not on the field has no effect.

    /// <summary>Seed a single roster slot's accessory field (u16 at RosterBase + slot*stride + RAccessory).</summary>
    private static void SeedAccessory(FakeSparseMemory mem, int slot, ushort itemId)
    {
        long rb   = Offsets.RosterBase + (long)slot * Offsets.RosterStride;
        long addr = rb + Offsets.RAccessory;
        mem.U16s[addr]       = itemId;
        mem.ReadableAddrs.Add(addr);
    }

    /// <summary>Seed a band entry (Band.Entry(bandSlot)) as a valid deployed unit with the given
    /// fingerprint so Band.IsValid passes and RingGate's band cross-check finds it. nameId seeds
    /// the frame's roster-nameId back-reference (Offsets.ANameId) that RingGate's tier-1/tier-2
    /// veto locate reads (D2/D7); default 0 matches the pre-nameId unseeded contract byte-for-byte.</summary>
    private static void SeedBandUnit(FakeSparseMemory mem, int bandSlot,
        byte level, byte brave, byte faith, ushort mhp = 500, byte gx = 5, byte gy = 5, int nameId = 0)
    {
        long a = Band.Entry(bandSlot);
        mem.U8s[a + Offsets.ALevel] = level;  mem.ReadableAddrs.Add(a + Offsets.ALevel);
        mem.U8s[a + Offsets.ABrave] = brave;  mem.ReadableAddrs.Add(a + Offsets.ABrave);
        mem.U8s[a + Offsets.AFaith] = faith;  mem.ReadableAddrs.Add(a + Offsets.AFaith);
        mem.U16s[a + Offsets.AMaxHp] = mhp;   mem.ReadableAddrs.Add(a + Offsets.AMaxHp);
        mem.U8s[a + Offsets.AGx] = gx;        mem.ReadableAddrs.Add(a + Offsets.AGx);
        mem.U8s[a + Offsets.AGy] = gy;        mem.ReadableAddrs.Add(a + Offsets.AGy);
        mem.U16s[a + Offsets.ANameId] = (ushort)nameId;   // unguarded fail-safe read (Iai/RingGate pattern, D8)
    }

    /// <summary>Seed a roster slot wearing the Scholar's Ring with a valid (level,brave,faith)
    /// fingerprint. When <paramref name="deployed"/>, also seed a matching band entry so the unit
    /// is "on the field"; benched leaves the band empty so RingGate sees no deployed bearer.
    /// nameId, when nonzero, seeds this slot's roster nameId (Offsets.RNameId) AND marks it
    /// Readable -- RingGate's caller-side capture (D7, guarded like GrowthEngine.Apply's); left at
    /// 0 (unseeded, unreadable) reproduces "roster nameId capture unavailable" for the tier-2-
    /// inert-veto parity tests. A deployed band entry carries the SAME nameId by default (the
    /// real, identity-verified match) -- tests that need a colliding foreign nameId seed the band
    /// entry separately.</summary>
    private static void SeedRingBearer(FakeSparseMemory mem, int rosterSlot, bool deployed,
        int bandSlot = 0, byte level = 50, byte brave = 70, byte faith = 70, int nameId = 0)
    {
        long rb = Offsets.RosterBase + (long)rosterSlot * Offsets.RosterStride;
        mem.U16s[rb + Offsets.RAccessory] = (ushort)Offsets.ScholarRingItemId;
        mem.ReadableAddrs.Add(rb + Offsets.RAccessory);
        mem.U8s[rb + Offsets.RLevel] = level;  mem.ReadableAddrs.Add(rb + Offsets.RLevel);
        mem.U8s[rb + Offsets.RBrave] = brave;  mem.ReadableAddrs.Add(rb + Offsets.RBrave);
        mem.U8s[rb + Offsets.RFaith] = faith;  mem.ReadableAddrs.Add(rb + Offsets.RFaith);
        if (nameId != 0)
        {
            mem.U16s[rb + Offsets.RNameId] = (ushort)nameId;
            mem.ReadableAddrs.Add(rb + Offsets.RNameId);
        }
        if (deployed) SeedBandUnit(mem, bandSlot, level, brave, faith, nameId: nameId);
    }

    [Fact]
    public void RingGate_RingDeployedInSlot0_ReturnsTrue()
    {
        var mem = new FakeSparseMemory();
        SeedRingBearer(mem, rosterSlot: 0, deployed: true);

        Assert.True(RingGate.ScholarRingEquipped(mem));
    }

    [Fact]
    public void RingGate_RingDeployedInHighSlot_ReturnsTrue()
    {
        var mem = new FakeSparseMemory();
        SeedRingBearer(mem, rosterSlot: Offsets.RosterSlots - 1, deployed: true);

        Assert.True(RingGate.ScholarRingEquipped(mem));
    }

    /// <summary>Battle-only core contract: a ring-bearer who is NOT in the live battle band
    /// (benched) does not open the gate -- equipment on a benched unit has no effect.</summary>
    [Fact]
    public void RingGate_RingBearerBenched_ReturnsFalse()
    {
        var mem = new FakeSparseMemory();
        SeedRingBearer(mem, rosterSlot: 0, deployed: false);   // ring in roster, no band entry

        Assert.False(RingGate.ScholarRingEquipped(mem));
    }

    /// <summary>A deployed bearer whose live band level drifted UP from a mid-battle level-up
    /// (within Band.MaxLevelDrift) still matches -- the roster keeps the pre-battle level.</summary>
    [Fact]
    public void RingGate_DeployedBearerLevelDrift_ReturnsTrue()
    {
        var mem = new FakeSparseMemory();
        long rb = Offsets.RosterBase;   // slot 0
        mem.U16s[rb + Offsets.RAccessory] = (ushort)Offsets.ScholarRingItemId; mem.ReadableAddrs.Add(rb + Offsets.RAccessory);
        mem.U8s[rb + Offsets.RLevel] = 50; mem.ReadableAddrs.Add(rb + Offsets.RLevel);
        mem.U8s[rb + Offsets.RBrave] = 70; mem.ReadableAddrs.Add(rb + Offsets.RBrave);
        mem.U8s[rb + Offsets.RFaith] = 70; mem.ReadableAddrs.Add(rb + Offsets.RFaith);
        SeedBandUnit(mem, bandSlot: 0, level: 55, brave: 70, faith: 70);   // +5 live drift

        Assert.True(RingGate.ScholarRingEquipped(mem));
    }

    /// <summary>ScholarRingInRoster is true whenever the ring is in the party, deployed or not --
    /// it gates the "no ring" nag so a benched/still-loading bearer isn't called "no ring".</summary>
    [Fact]
    public void RingGate_ScholarRingInRoster_TrueEvenIfBenched()
    {
        var mem = new FakeSparseMemory();
        SeedRingBearer(mem, rosterSlot: 2, deployed: false);   // ring in roster, not deployed

        Assert.True(RingGate.ScholarRingInRoster(mem));     // it IS in the party...
        Assert.False(RingGate.ScholarRingEquipped(mem));    // ...but not deployed -> gate stays closed
    }

    [Fact]
    public void RingGate_ScholarRingInRoster_FalseWhenNoRing()
    {
        var mem = new FakeSparseMemory();
        SeedAccessory(mem, 0, 218);   // some other accessory, no Scholar's Ring anywhere

        Assert.False(RingGate.ScholarRingInRoster(mem));
    }

    [Fact]
    public void RingGate_NoRingOnlyOtherAccessories_ReturnsFalse()
    {
        var mem = new FakeSparseMemory();
        // Accessories that are real items but not the ring.
        SeedAccessory(mem, 0, 218);
        SeedAccessory(mem, 1, 255);
        SeedAccessory(mem, 2, 224);
        SeedAccessory(mem, 3, 226);

        Assert.False(RingGate.ScholarRingEquipped(mem));
    }

    [Fact]
    public void RingGate_AllSlotsUnreadable_ReturnsFalse()
    {
        // FakeSparseMemory.Readable() returns false for all unseeded addrs.
        var mem = new FakeSparseMemory();
        Assert.False(RingGate.ScholarRingEquipped(mem));
    }

    [Fact]
    public void RingGate_EmptyAccessory255_ReturnsFalse()
    {
        var mem = new FakeSparseMemory();
        // 255 = "empty" accessory slot; must not be confused with the ring.
        SeedAccessory(mem, 0, 255);
        SeedAccessory(mem, 1, 0);

        Assert.False(RingGate.ScholarRingEquipped(mem));
    }

    // ── RingGate nameId two-tier-with-veto locate (Plan v2 D2/D7, TDD item 13) ───────────────
    // BandHasUnit gains the same two-tier-with-veto locate as Wielder.Locate/GrowthEngine.ReadHp:
    // a band entry sharing the ring-bearer's (level,brave,faith) but carrying a DIFFERENT, foreign
    // nonzero frame nameId is an fp-collider (an unrelated unit, enemy or ally) -- the old
    // fingerprint-only match falsely counted it as "the ring-bearer is deployed".

    /// <summary>[LOAD-BEARING, TDD item 13a] The ring-bearer's roster nameId (100) is known, but
    /// the only band entry sharing its (level,brave,faith) carries a DIFFERENT, foreign nonzero
    /// frame nameId (918) -- an fp-collider, not the ring-bearer's own entry. Tier 1 excludes it
    /// (918 != 100) and tier 2's veto excludes it too (foreign nonzero nameId), so the gate must
    /// NOT report the ring-bearer as deployed. Non-vacuity: before this fix BandHasUnit had no
    /// nameId awareness at all and matched on fp alone, so this assertion fails against the old
    /// implementation.</summary>
    [Fact]
    public void RingGate_ForeignNameIdCollider_KnownRosterNameId_VetoExcludes_ReturnsFalse()
    {
        var mem = new FakeSparseMemory();
        SeedRingBearer(mem, rosterSlot: 0, deployed: false, nameId: 100);   // roster nameId known; bearer itself never got a band entry
        SeedBandUnit(mem, bandSlot: 10, level: 50, brave: 70, faith: 70, nameId: 918);   // fp-colliding foreign unit

        Assert.False(RingGate.ScholarRingEquipped(mem),
            "a foreign-nameId fp-collider must not count as the ring-bearer's deployed match");
    }

    /// <summary>[TDD item 13b] Roster nameId capture is unavailable (RNameId never seeded/
    /// unreadable, the pre-nameId default) -- tier 2's veto is inert at rosterNameId &lt;= 0, so a
    /// band entry sharing the bearer's fp still counts as deployed regardless of its OWN frame
    /// nameId. This is the fallback-parity property: every pre-nameId RingGate test above (none
    /// of which seed RNameId) exercises this exact path unchanged.</summary>
    [Fact]
    public void RingGate_BandEntryCarriesForeignNameId_RosterNameIdUnavailable_StillDeploys()
    {
        var mem = new FakeSparseMemory();
        SeedRingBearer(mem, rosterSlot: 0, deployed: false);   // RNameId left unseeded -> capture unavailable
        SeedBandUnit(mem, bandSlot: 0, level: 50, brave: 70, faith: 70, nameId: 918);   // entry's own nameId irrelevant when the veto is inert

        Assert.True(RingGate.ScholarRingEquipped(mem),
            "roster nameId unavailable -> tier 2's veto is inert, an fp-matching entry still counts as deployed");
    }

    // ── TreasureMaster ring-gate integration ─────────────────────────────────────
    // alwaysOn=false + ring equipped  => arms + writes.
    // alwaysOn=false + no ring        => never arms, zero writes, logs idle once.
    // alwaysOn=true  + no ring        => arms (override path, ring not read).

    /// <summary>
    /// Build a standard TreasureMaster mem seeded for a known map, optionally with
    /// the Scholar's Ring in a roster slot.
    /// </summary>
    private static (TreasureDb db, FakeSparseMemory mem, long addr) BuildRingGateScenario(
        bool ringEquipped, int rosterSlot = 0, bool deployed = true)
    {
        var dir     = TempDir();
        var terrain = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };
        var addr    = TileAddr(300);
        var db      = BuildDb(dir, fpLen: terrain.Length, fpHash: TerrainFpHash(terrain),
                         addrs: new[] { (addr, (byte)0x00) });
        var mem     = BuildMem(74, terrain, new[] { addr });

        if (ringEquipped)
            SeedRingBearer(mem, rosterSlot, deployed);   // deployed=true also seeds the band entry
        // Not equipped: leave all roster accessory slots unseeded (Readable = false -> returns false).

        return (db, mem, addr);
    }

    [Fact]
    public void RingGate_AlwaysOff_RingEquipped_ArmsAndWrites()
    {
        var (db, mem, addr) = BuildRingGateScenario(ringEquipped: true);

        var tm = new TreasureMaster(db, mem, alwaysOn: false);
        StabilizeAndArm(tm);

        Assert.True(mem.Written.ContainsKey(addr),
            "alwaysOn=false + ring equipped should arm and write tile");
        Assert.Equal(0x80, mem.Written[addr]);
    }

    [Fact]
    public void RingGate_AlwaysOff_NoRing_NeverArms_ZeroWrites()
    {
        var (db, mem, addr) = BuildRingGateScenario(ringEquipped: false);

        var tm = new TreasureMaster(db, mem, alwaysOn: false);
        TickN(tm, Tuning.TreasureArmStableTicks + 20);

        Assert.Empty(mem.Written);
    }

    /// <summary>Battle-only at the integration level: the ring is equipped on a roster unit who
    /// is BENCHED (not in the live battle band), so the module never arms -- no marks.</summary>
    [Fact]
    public void RingGate_AlwaysOff_RingBenched_NeverArms()
    {
        var (db, mem, addr) = BuildRingGateScenario(ringEquipped: true, deployed: false);

        var tm = new TreasureMaster(db, mem, alwaysOn: false);
        TickN(tm, Tuning.TreasureArmStableTicks + 20);

        Assert.Empty(mem.Written);
    }

    /// <summary>The live battle band can finish populating a beat AFTER the map id stabilises
    /// (fresh battle load). A ring-bearer in the roster whose band entry appears LATE must still
    /// arm -- the gate re-checks while disarmed rather than caching the first "no". (Live
    /// 2026-06-12: a deployed mage wearing the ring got "module idle" + no marks all battle.)</summary>
    [Fact]
    public void RingGate_DeployedBearerAppearsLate_StillArms()
    {
        var dir     = TempDir();
        var terrain = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };
        var addr    = TileAddr(305);
        var db      = BuildDb(dir, fpLen: terrain.Length, fpHash: TerrainFpHash(terrain),
                         addrs: new[] { (addr, (byte)0x00) });
        var mem     = BuildMem(74, terrain, new[] { addr });

        // Ring is in the roster from the start, but the unit's band entry is NOT there yet.
        long rb = Offsets.RosterBase;
        mem.U16s[rb + Offsets.RAccessory] = (ushort)Offsets.ScholarRingItemId; mem.ReadableAddrs.Add(rb + Offsets.RAccessory);
        mem.U8s[rb + Offsets.RLevel] = 50; mem.ReadableAddrs.Add(rb + Offsets.RLevel);
        mem.U8s[rb + Offsets.RBrave] = 70; mem.ReadableAddrs.Add(rb + Offsets.RBrave);
        mem.U8s[rb + Offsets.RFaith] = 70; mem.ReadableAddrs.Add(rb + Offsets.RFaith);

        var tm = new TreasureMaster(db, mem, alwaysOn: false);
        // Past stability with NO band entry yet -> not armed (bearer looks benched for now).
        TickN(tm, Tuning.TreasureArmStableTicks + 5);
        Assert.Empty(mem.Written);

        // The band populates the bearer mid-disarm.
        SeedBandUnit(mem, bandSlot: 0, level: 50, brave: 70, faith: 70);
        TickN(tm, 5);

        // Re-check finds the now-deployed bearer -> arms and writes (cache-once would never arm).
        Assert.True(mem.Written.ContainsKey(addr),
            "a late-appearing deployed bearer must arm -- the gate must re-check, not cache the first 'no'");
    }

    [Fact]
    public void RingGate_AlwaysOff_NoRing_FastHold_PublishesNull()
    {
        var (db, mem, addr) = BuildRingGateScenario(ringEquipped: false);

        var tm = new TreasureMaster(db, mem, alwaysOn: false);
        TickN(tm, Tuning.TreasureArmStableTicks + 5);

        // HoldOnce on a null-published FastHold must write nothing.
        mem.Written.Clear();
        mem.U8s[addr] = 0x00;
        tm.FastHold.HoldOnce();

        Assert.Empty(mem.Written);
    }

    [Fact]
    public void RingGate_AlwaysOn_NoRing_ArmsAnyway()
    {
        var (db, mem, addr) = BuildRingGateScenario(ringEquipped: false);

        var tm = new TreasureMaster(db, mem, alwaysOn: true);
        StabilizeAndArm(tm);

        Assert.True(mem.Written.ContainsKey(addr),
            "alwaysOn=true must arm even with no ring");
    }

    // Per-battle ring gate (rolled back from the live-recheck design on 2026-06-12): the ring
    // is read ONCE when the module first arms and cached for the battle. Removing or adding the
    // ring mid-battle has no effect until the next battle (ResetBattle re-reads). This is the
    // simpler, robust contract -- the live mid-battle recheck had a re-equip-never-restores
    // failure on real hardware.

    /// <summary>
    /// Per-battle ring gate: once the check latches for a battle, the cached result persists
    /// even if the ring is removed from the roster mid-battle (the gate is not re-read while
    /// ARMED). Removing the ring mid-battle therefore does NOT drop the marks; the next battle
    /// re-reads (see RingGate_ResetBattle_RecheckOnNextBattle).
    /// </summary>
    [Fact]
    public void RingGate_RingCheckedOncePerBattle_CachedResultSurvivesRingRemoval()
    {
        var (db, mem, addr) = BuildRingGateScenario(ringEquipped: true, rosterSlot: 0);

        var tm = new TreasureMaster(db, mem, alwaysOn: false);

        // Arm with ring present.
        StabilizeAndArm(tm);
        Assert.True(mem.Written.ContainsKey(addr), "should have armed with ring present");

        // Remove the ring from the roster mid-battle (gate already latched for this battle),
        // then tick WELL past any plausible recheck cadence. The OLD live-recheck design would
        // re-read the roster here, see the ring gone, and disarm; per-battle must not.
        long accessorAddr = Offsets.RosterBase + (long)0 * Offsets.RosterStride + Offsets.RAccessory;
        mem.U16s[accessorAddr] = 0;   // ring gone from memory
        TickN(tm, Tuning.TreasureArmStableTicks * 2 + 30);

        mem.Written.Clear();
        mem.U8s[addr] = 0x00;         // engine cleared mark

        // Still ARMED and writing -- the ring gate is cached for the battle, never re-read.
        TickN(tm, 5);
        Assert.True(mem.Written.ContainsKey(addr),
            "ARMED ticks should keep writing -- the ring gate is cached per battle");
    }

    /// <summary>
    /// alwaysOn=true never reads the roster: the accessory address must never appear in
    /// ReadCount regardless of how many ticks elapse (the gate short-circuits on alwaysOn).
    /// </summary>
    [Fact]
    public void RingGate_AlwaysOn_NeverReadsRoster()
    {
        var (db, mem, addr) = BuildRingGateScenario(ringEquipped: false);

        var tm = new TreasureMaster(db, mem, alwaysOn: true);
        TickN(tm, Tuning.TreasureArmStableTicks * 3 + 30);

        // No roster accessory address should have been read at all.
        for (int slot = 0; slot < Offsets.RosterSlots; slot++)
        {
            long rb   = Offsets.RosterBase + (long)slot * Offsets.RosterStride;
            long racc = rb + Offsets.RAccessory;
            Assert.False(mem.ReadCount.ContainsKey(racc),
                $"alwaysOn=true must never read roster slot {slot} accessory");
        }
    }

    [Fact]
    public void RingGate_ResetBattle_RecheckOnNextBattle()
    {
        // Ring absent on battle 1 -> no arm.  ResetBattle.  Ring appears -> battle 2 arms.
        var dir     = TempDir();
        var terrain = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };
        var addr    = TileAddr(301);
        var db      = BuildDb(dir, fpLen: terrain.Length, fpHash: TerrainFpHash(terrain),
                         addrs: new[] { (addr, (byte)0x00) });
        var mem     = BuildMem(74, terrain, new[] { addr });

        // Battle 1: no ring.
        var tm = new TreasureMaster(db, mem, alwaysOn: false);
        TickN(tm, Tuning.TreasureArmStableTicks + 20);
        Assert.Empty(mem.Written);

        // Between battles: equip the ring on a unit who is deployed in battle 2.
        tm.ResetBattle();
        SeedRingBearer(mem, rosterSlot: 0, deployed: true);
        mem.U8s[addr] = 0x00;

        // Battle 2: deployed ring-bearer -> should arm and write.
        TickN(tm, Tuning.TreasureArmStableTicks + 10);
        Assert.True(mem.Written.ContainsKey(addr),
            "after ResetBattle ring appears -> should arm on next battle");
    }
}
