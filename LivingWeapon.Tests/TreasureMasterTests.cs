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
///   (7)  Fingerprint corrupted mid-battle (mutate terrain bytes, advance past the
///        revalidate tick): writes stop after the bad fingerprint is seen.
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

    // ── (6) Fingerprint mismatch at arm: zero writes ──────────────────────────────

    [Fact]
    public void Fingerprint_mismatch_at_arm_zero_writes()
    {
        var dir  = TempDir();
        var realTerrain  = new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77 };
        var wrongTerrain = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x00 };
        var addr = TileAddr(5);
        // DB stores hash for realTerrain, but memory has wrongTerrain
        var db = BuildDb(dir, fpLen: realTerrain.Length, fpHash: TerrainFpHash(realTerrain),
            addrs: new[] { (addr, (byte)0x00) });
        var mem = BuildMem(74, wrongTerrain, new[] { addr });

        var tm = Make(db, mem);
        // Arm cap ticks -- none should write
        TickN(tm, Tuning.TreasureArmStableTicks + Tuning.TreasureArmAttemptCap + 10);

        Assert.Empty(mem.Written);
    }

    // ── (7) Fingerprint corrupted mid-battle ─────────────────────────────────────

    [Fact]
    public void Fingerprint_corrupted_mid_battle_stops_writes()
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

        // Corrupt the terrain bytes in memory
        for (int i = 0; i < terrain.Length; i++)
            terrain[i] ^= 0xFF;
        mem.TerrainBlocks[Offsets.TerrainGrid] = terrain;
        mem.Written.Clear();

        // Advance past revalidate period so the fingerprint check fires
        TickN(tm, Tuning.TreasureRevalidateEveryNTicks + 5);

        // After the fingerprint fails, no new tile writes (module goes back to ARMING)
        Assert.Empty(mem.Written);
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
    /// Field-0 byte mutates mid-battle (tile height actually changed -- shouldn't happen
    /// in practice but must be handled gracefully).  The masked hash changes -> module
    /// transitions back to ARMING, suspends writes.  Once the field-0 byte reverts to the
    /// original value the hash matches again and the module re-arms, resuming writes.
    /// </summary>
    [Fact]
    public void Armed_field0_terrain_mutation_re_proves_then_rearms_on_revert()
    {
        var dir = TempDir();
        var terrain = new byte[]
        {
            0x05, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,  // record 0: field-0=0x05
        };
        var addr = TileAddr(51);
        var db = BuildDb(dir, fpLen: terrain.Length, fpHash: TerrainFpHashV2(terrain),
            addrs: new[] { (addr, (byte)0x00) }, fpVer: 2);
        var mem = BuildMem(74, terrain, new[] { addr });

        var tm = Make(db, mem);
        StabilizeAndArm(tm);
        Assert.NotEmpty(mem.Written);

        // Mutate field-0 -> hash changes -> fingerprint mismatch on revalidation
        terrain[0] = 0x09;
        mem.TerrainBlocks[Offsets.TerrainGrid] = terrain;
        mem.Written.Clear();

        // Advance past revalidate period to trigger the fingerprint check
        TickN(tm, Tuning.TreasureRevalidateEveryNTicks + 5);

        // While re-proving, zero tile writes (module is ARMING again)
        Assert.Empty(mem.Written);

        // Revert field-0 back to original -> hash matches again -> re-arms
        terrain[0] = 0x05;
        mem.TerrainBlocks[Offsets.TerrainGrid] = terrain;
        mem.U8s[addr] = 0x00;

        // Enough ticks to re-arm: fingerprint matches + addr quorum
        TickN(tm, Tuning.TreasureArmStableTicks + 10);

        Assert.True(mem.Written.ContainsKey(addr),
            "after field-0 reverts, fingerprint matches again -> should re-arm and write");
    }

    /// <summary>
    /// Re-proving after a fingerprint flap must log "flap" message once per battle,
    /// NOT "fingerprint changed mid-battle -- disarmed" (the old behavior that triggered
    /// the live incident complaint).
    /// </summary>
    [Fact]
    public void Armed_field0_mutation_transitions_to_Arming_not_BattleDisarmed()
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

        // Corrupt field-0 -> revalidation fires -> must go to ARMING (not BattleDisarmed)
        terrain[0] = 0xFF;
        mem.TerrainBlocks[Offsets.TerrainGrid] = terrain;
        mem.Written.Clear();
        TickN(tm, Tuning.TreasureRevalidateEveryNTicks + 5);
        Assert.Empty(mem.Written);  // suspended during re-prove

        // Now restore AND advance past stability window
        terrain[0] = 0x07;
        mem.TerrainBlocks[Offsets.TerrainGrid] = terrain;
        mem.U8s[addr] = 0x00;
        TickN(tm, Tuning.TreasureArmStableTicks + 10);

        // If BattleDisarmed (old bug), nothing would EVER write again; re-arm proves ARMING
        Assert.True(mem.Written.ContainsKey(addr),
            "after re-arm from ARMING, should write again; BattleDisarmed would block this forever");
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
}
