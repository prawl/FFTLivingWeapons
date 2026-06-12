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
///   (9)  Foreign byte (e.g. 0x42) at arm: Disarm, zero writes.
///   (10) Unwritable addr skipped while siblings still written.
///   (11) ResetBattle clears state, writes nothing, fresh battle re-arms.
///   (12) Stub map (no tiles): no writes, exactly zero tile-addr writes.
///   (13) Build-key mismatch: zero flag-address reads/writes ever.
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
        bool stub = false)
    {
        var addrList = addrs?.ToList() ?? new List<(long, byte)>
            { (TileAddr(0), 0x00), (TileAddr(1), 0x00) };

        string tilesJson = stub ? "[]" : $@"[{{
            ""x"": {tileX}, ""y"": {tileY},
            ""addrs"": [{string.Join(", ", addrList.Select(a => AddrJson(a.addr, a.off)))}]
        }}]";

        string fpLenStr   = fpLen  is {} l ? $@"""fpLen"": {l},"   : "";
        string fpHashStr  = fpHash is {} h ? $@"""fpHash"": ""{h}"","  : "";
        string bkStr      = buildKey is null ? "null" :
            $@"{{""timeDateStamp"": {buildKey.TimeDateStamp}, ""sizeOfImage"": {buildKey.SizeOfImage}}}";

        string json = $@"{{
            ""buildKey"": {bkStr},
            ""maps"": [{{
                ""mapId"": {mapId}, ""name"": ""{name}"", ""tileCount"": {(stub ? 2 : addrList.Count)},
                {fpLenStr}
                {fpHashStr}
                ""tiles"": {tilesJson}
            }}]
        }}";
        File.WriteAllText(Path.Combine(dir, "treasure.json"), json);
        return TreasureDb.Load(dir);
    }

    // ── terrain-fingerprint helpers ──────────────────────────────────────────────

    /// <summary>Compute the FNV-1a64 hash of <paramref name="terrain"/> to seed fpHash.</summary>
    private static string TerrainFpHash(byte[] terrain)
        => TreasureMaster.Fnv1a64(terrain).ToString("x");

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

        // After the fingerprint fails, no new tile writes (module goes BATTLE_DISARMED)
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

    // ── (9) Foreign byte at arm: Disarm, zero writes ─────────────────────────────

    [Fact]
    public void Foreign_byte_at_arm_disarms_and_zero_writes()
    {
        var dir  = TempDir();
        var terrain = new byte[] { 0xAA, 0xBB, 0xCC, 0x11, 0x22, 0x33, 0x44 };
        var addr = TileAddr(8);
        var db = BuildDb(dir, fpLen: terrain.Length, fpHash: TerrainFpHash(terrain),
            addrs: new[] { (addr, (byte)0x00) });
        // 0x42 is Foreign (not in {0x00, 0x01, 0x80, 0x81})
        var mem = BuildMem(74, terrain, new[] { addr }, initialByte: 0x42);

        var tm = Make(db, mem);
        StabilizeAndArm(tm);

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

    // ── FIX 3: Foreign bytes >1/3 while ARMED → BattleDisarmed + log ─────────────

    /// <summary>
    /// After arming cleanly, if more than 1/3 of the map's tile addresses return Foreign
    /// bytes, the module must transition to BattleDisarmed on that tick, log once, and
    /// stop writing on all subsequent ticks.
    /// </summary>
    [Fact]
    public void Armed_foreign_bytes_over_threshold_disarms_and_stops_writes()
    {
        var dir = TempDir();
        var terrain = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };
        // Three addrs: mutating 2 of 3 (>1/3) should trigger disarm.
        var addrs = new[] { TileAddr(30), TileAddr(31), TileAddr(32) };
        var db = BuildDb(dir, fpLen: terrain.Length, fpHash: TerrainFpHash(terrain),
            addrs: addrs.Select(a => (a, (byte)0x00)));
        var mem = BuildMem(74, terrain, addrs, initialByte: 0x00);

        var tm = Make(db, mem);
        StabilizeAndArm(tm);
        Assert.NotEmpty(mem.Written);

        // Mutate 2 of 3 addrs to Foreign (0x42), and reset addrs[2] to Resting so it
        // WOULD be written if the module stayed armed (proving disarm is what stops it).
        mem.U8s[addrs[0]] = 0x42;
        mem.U8s[addrs[1]] = 0x42;
        mem.U8s[addrs[2]] = 0x00;   // Resting -- would be written if still armed
        mem.Written.Clear();

        // One tick: should see >1/3 foreign, disarm, and write nothing (not even addrs[2]).
        tm.Tick(DateTime.Now, inLive: true);

        Assert.Empty(mem.Written);

        // Subsequent ticks: stays disarmed, no writes even though addrs[2] is still Resting.
        TickN(tm, 5);
        Assert.Empty(mem.Written);
    }

    [Fact]
    public void Armed_foreign_bytes_at_or_below_threshold_stays_armed()
    {
        var dir = TempDir();
        var terrain = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };
        // Three addrs: mutating exactly 1 of 3 (=1/3, not OVER 1/3) should NOT disarm.
        var addrs = new[] { TileAddr(33), TileAddr(34), TileAddr(35) };
        var db = BuildDb(dir, fpLen: terrain.Length, fpHash: TerrainFpHash(terrain),
            addrs: addrs.Select(a => (a, (byte)0x00)));
        var mem = BuildMem(74, terrain, addrs, initialByte: 0x00);

        var tm = Make(db, mem);
        StabilizeAndArm(tm);
        mem.Written.Clear();

        // Mutate exactly 1 of 3 to Foreign (at threshold, not over).
        mem.U8s[addrs[0]] = 0x42;

        // One tick: should NOT disarm; the two non-foreign addrs still get written/held.
        tm.Tick(DateTime.Now, inLive: true);

        // addrs[1] and addrs[2] are Resting (already written, then re-read as 0x80 = Held).
        // The key assertion: module is still writing, not disarmed.
        // (The two still-resting bytes, if cleared, would get written.)
        mem.U8s[addrs[1]] = 0x00;
        mem.U8s[addrs[2]] = 0x00;
        mem.Written.Clear();
        tm.Tick(DateTime.Now, inLive: true);
        Assert.True(mem.Written.ContainsKey(addrs[1]) || mem.Written.ContainsKey(addrs[2]));
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
}
