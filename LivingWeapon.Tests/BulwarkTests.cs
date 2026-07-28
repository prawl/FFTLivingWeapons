using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// Module-level coverage for the Sunderer's "Bulwark" signature (docs/BULWARK_AC.md) against
/// FakeSparseMemory band seats + a real BannerToast: the full-wait plant trigger, the single
/// behind-tile terrain-grid write, the vacancy/deferred-raise machinery, every release path, and
/// the ResetBattle restore-then-clear contract. Pure-decision coverage lives in
/// BulwarkPolicyTests.cs.
/// </summary>
public class BulwarkTests
{
    private const int SundererId = Bulwark.SundererId;   // 50

    private static long GridAddr(int idx) => Offsets.PathTerrainGrid + (long)idx * Offsets.PathTerrainStride + Offsets.PathTerrainVetoField;
    private static int Idx(int x, int y, int w, int layerBit = 0) => x + y * w + layerBit * 0x100;

    private static void SetFlags(FakeSparseMemory mem, long entry, int turnFlag, int moved, int acted)
    {
        mem.ReadableAddrs.Add(entry + Offsets.ATurnFlag);
        mem.ReadableAddrs.Add(entry + Offsets.AMoved);
        mem.ReadableAddrs.Add(entry + Offsets.AActed);
        mem.U8s[entry + Offsets.ATurnFlag] = (byte)turnFlag;
        mem.U8s[entry + Offsets.AMoved] = (byte)moved;
        mem.U8s[entry + Offsets.AActed] = (byte)acted;
    }

    /// <summary>Stage a terrain-grid tile as readable + writable with a seeded original f6 byte.
    /// Default 0x20 is tree-like (a plausible slope/terrain byte) but unvetoed (bit 0x02 clear).</summary>
    private static void StageTerrain(FakeSparseMemory mem, int idx, byte origF6 = 0x20)
    {
        long addr = GridAddr(idx);
        mem.ReadableAddrs.Add(addr);
        mem.WritableAddrs.Add(addr);
        mem.U8s[addr] = origF6;
    }

    private static Dictionary<int, WeaponMeta> Meta(bool bulwark = true, int atTier = 3)
        => new()
        {
            [SundererId] = new WeaponMeta
            {
                Name = "Sunderer", Wp = 13, Cat = "KnightSword", Formula = 46,
                Flavor = "spiteful edge",
                Signature = bulwark
                    ? new WeaponSignature { AtTier = atTier, Bulwark = true, DisplayLabel = "Bulwark" }
                    : new WeaponSignature { AtTier = atTier, Bulwark = false, DisplayLabel = "" }
            }
        };

    private static (FakeSparseMemory mem, Dictionary<int, int> kills, BannerToast toast, Bulwark bulwark, long wielderEntry)
        Build(int kills = -1, int wx = 5, int wy = 5, int mapW = 11, int mapH = 12, int layerBit = 0, int facing = 0,
              Dictionary<int, WeaponMeta>? meta = null)
    {
        var mem = new FakeSparseMemory();
        meta ??= Meta();
        var killDict = new Dictionary<int, int> { [SundererId] = kills >= 0 ? kills : Tuning.ProdThresholds[2] };
        var fp = (lvl: 30, br: 50, fa: 50);
        const int wielderSlot = 24;
        long wielderEntry = Band.Entry(wielderSlot);

        MemSeats.SeatRoster(mem, 0, lvl: fp.lvl, br: fp.br, fa: fp.fa, rh: SundererId);
        MemSeats.SeatBand(mem, wielderSlot, weapon: SundererId, lvl: fp.lvl, br: fp.br, fa: fp.fa, gx: wx, gy: wy, hp: 200, maxHp: 300);

        mem.U8s[Offsets.MapDimsWH] = (byte)mapW;
        mem.U8s[Offsets.MapDimsWH + 1] = (byte)mapH;
        // ALayerBit packs BOTH the terrain layer (bit 0x80) and the facing (low 2 bits) in one byte.
        mem.U8s[wielderEntry + Offsets.ALayerBit] = (byte)((layerBit << 7) | facing);

        var toast = new BannerToast(meta, killDict, enabled: true);
        var bulwark = new Bulwark(meta, killDict, toast, mem);
        return (mem, killDict, toast, bulwark, wielderEntry);
    }

    /// <summary>Full-wait sequence: prime closed, open, close (a genuine full wait -- or a
    /// move/act variant per the caller's flags).</summary>
    private static void RunFullWaitCycle(Bulwark bulwark, FakeSparseMemory mem, long entry, int moved = 0, int acted = 0)
    {
        SetFlags(mem, entry, 0, 0, 0);
        bulwark.Tick(true);   // prime
        SetFlags(mem, entry, 1, 0, 0);   // turn opens
        bulwark.Tick(true);
        SetFlags(mem, entry, 0, moved, acted);   // turn closes
        bulwark.Tick(true);
    }

    // ================= Plant on a genuine full wait =================

    /// <summary>THE LOAD-BEARING TEST. All four orthogonal neighbors of (5,5) are staged; a full
    /// wait must veto ONLY the one tile behind the wielder for the given facing, leaving the other
    /// three untouched -- proving the design change from "four-tile ring" to "single behind tile"
    /// end to end, not just in BulwarkPolicy's pure mapping.</summary>
    [Theory]
    [InlineData(0, 5, 6)]   // facing South: behind = (5,6)
    [InlineData(1, 6, 5)]   // facing West: behind = (6,5)
    [InlineData(2, 5, 4)]   // facing North: behind = (5,4)
    [InlineData(3, 4, 5)]   // facing East: behind = (4,5)
    public void FullWait_Plants_ExactlyTheBehindTile_PerFacing(int facing, int behindX, int behindY)
    {
        var (mem, kills, toast, bulwark, entry) = Build(wx: 5, wy: 5, mapW: 11, mapH: 12, facing: facing);
        int w = 11;
        var allFour = new (int x, int y)[] { (4, 5), (6, 5), (5, 4), (5, 6) };
        var idxByTile = new Dictionary<(int, int), int>();
        foreach (var t in allFour)
        {
            int idx = Idx(t.x, t.y, w);
            idxByTile[t] = idx;
            StageTerrain(mem, idx, 0x20);
        }

        RunFullWaitCycle(bulwark, mem, entry);

        int behindIdx = idxByTile[(behindX, behindY)];
        // Hardcoded expected byte (0x20 | 0x02 = 0x22) on purpose, NOT BulwarkPolicy.VetoedF6(0x20):
        // asserting against the same function under test would let a broken VetoedF6 pass this test.
        Assert.Equal((byte)0x22, mem.U8(GridAddr(behindIdx)));
        foreach (var t in allFour)
        {
            if (t == (behindX, behindY)) continue;
            Assert.Equal((byte)0x20, mem.U8(GridAddr(idxByTile[t])));
        }

        Assert.Single(toast._queue);
        Assert.Equal(SundererId, toast._queue[0].weaponId);
        Assert.Equal(Tuning.BulwarkToastKey, toast._queue[0].tier);
    }

    [Fact]
    public void MoveOnlyTurn_DoesNotPlant()
    {
        var (mem, kills, toast, bulwark, entry) = Build(wx: 5, wy: 5);
        int idx = Idx(5, 6, 11);   // behind tile, facing South (default)
        StageTerrain(mem, idx, 0x20);

        RunFullWaitCycle(bulwark, mem, entry, moved: 1, acted: 0);

        Assert.Equal((byte)0x20, mem.U8(GridAddr(idx)));   // untouched
        Assert.Empty(toast._queue);
    }

    [Fact]
    public void ActedTurn_DoesNotPlant()
    {
        var (mem, kills, toast, bulwark, entry) = Build(wx: 5, wy: 5);
        int idx = Idx(5, 6, 11);
        StageTerrain(mem, idx, 0x20);

        RunFullWaitCycle(bulwark, mem, entry, moved: 0, acted: 1);

        Assert.Equal((byte)0x20, mem.U8(GridAddr(idx)));
        Assert.Empty(toast._queue);
    }

    [Fact]
    public void BelowTier_NeverPlants()
    {
        var (mem, kills, toast, bulwark, entry) = Build(kills: Tuning.ProdThresholds[1], wx: 5, wy: 5);   // tier 2 < AtTier 3
        int idx = Idx(5, 6, 11);
        StageTerrain(mem, idx, 0x20);

        RunFullWaitCycle(bulwark, mem, entry);

        Assert.Equal((byte)0x20, mem.U8(GridAddr(idx)));
        Assert.Empty(toast._queue);
    }

    [Fact]
    public void MissingSignatureFlag_NeverPlants_NoCrash()
    {
        var (mem, kills, toast, bulwark, entry) = Build(wx: 5, wy: 5, meta: Meta(bulwark: false));
        int idx = Idx(5, 6, 11);
        StageTerrain(mem, idx, 0x20);

        RunFullWaitCycle(bulwark, mem, entry);

        Assert.Equal((byte)0x20, mem.U8(GridAddr(idx)));
        Assert.Empty(toast._queue);
    }

    [Fact]
    public void TwoDeployedSunderers_ResolverRefuses_NoPlant()
    {
        var mem = new FakeSparseMemory();
        var meta = Meta();
        var kills = new Dictionary<int, int> { [SundererId] = Tuning.ProdThresholds[2] };

        var fpA = (lvl: 30, br: 50, fa: 50);
        long entryA = Band.Entry(24);
        MemSeats.SeatRoster(mem, 0, lvl: fpA.lvl, br: fpA.br, fa: fpA.fa, rh: SundererId);
        MemSeats.SeatBand(mem, 24, weapon: SundererId, lvl: fpA.lvl, br: fpA.br, fa: fpA.fa, gx: 5, gy: 5, hp: 200, maxHp: 300);

        var fpB = (lvl: 40, br: 60, fa: 70);
        MemSeats.SeatRoster(mem, 1, lvl: fpB.lvl, br: fpB.br, fa: fpB.fa, rh: SundererId);
        MemSeats.SeatBand(mem, 21, weapon: SundererId, lvl: fpB.lvl, br: fpB.br, fa: fpB.fa, gx: 8, gy: 8, hp: 150, maxHp: 260);

        mem.U8s[Offsets.MapDimsWH] = 11;
        mem.U8s[Offsets.MapDimsWH + 1] = 12;

        int idx = Idx(5, 6, 11);   // behind tile for entryA, facing South (default, unseeded byte)
        StageTerrain(mem, idx, 0x20);

        var toast = new BannerToast(meta, kills, enabled: true);
        var bulwark = new Bulwark(meta, kills, toast, mem);

        RunFullWaitCycle(bulwark, mem, entryA);

        Assert.Equal((byte)0x20, mem.U8(GridAddr(idx)));
        Assert.Empty(toast._queue);
    }

    // ================= Dims sanity gates (AC A3) =================

    [Theory]
    [InlineData(0, 12, 5, 5)]     // W=0
    [InlineData(31, 12, 5, 5)]    // W>30
    [InlineData(20, 20, 5, 5)]    // W*H>256
    [InlineData(11, 12, 11, 5)]   // gx>=W
    public void InsaneMapDims_RefusesThePlant(int mapW, int mapH, int wx, int wy)
    {
        var (mem, kills, toast, bulwark, entry) = Build(wx: wx, wy: wy, mapW: mapW, mapH: mapH);
        // Stage every plausible in-range neighbor tile so a bug that ignores the gate would visibly write.
        for (int idx = 0; idx < 300; idx++) StageTerrain(mem, idx, 0x20);

        RunFullWaitCycle(bulwark, mem, entry);

        Assert.Empty(toast._queue);
        Assert.Empty(mem.Written);   // the gate refuses before any grid write is attempted
    }

    // ================= Vacancy (AC criterion 5, now scoped to the one behind tile) =================

    [Fact]
    public void OccupiedBehindTile_NoWriteAtPlant_ButPlantedAndToasted()
    {
        var (mem, kills, toast, bulwark, entry) = Build(wx: 5, wy: 5, mapW: 11, mapH: 12);
        int idx = Idx(5, 6, 11);   // behind tile, facing South (default)
        StageTerrain(mem, idx, 0x20);
        // An occupant sits on the behind tile (5,6).
        MemSeats.SeatBand(mem, 10, weapon: 0, lvl: 20, br: 30, fa: 30, gx: 5, gy: 6, hp: 100, maxHp: 100);

        RunFullWaitCycle(bulwark, mem, entry);

        Assert.Equal((byte)0x20, mem.U8(GridAddr(idx)));   // never written while occupied
        Assert.Single(toast._queue);   // the stance still exists and announces itself

        // Occupant leaves.
        mem.U8s[Band.Entry(10) + Offsets.ALevel] = 0;   // no longer a plausible seat

        SetFlags(mem, entry, 0, 0, 0);   // hold ticks continue while the turn flag stays CLOSED (no edge)
        bulwark.Tick(true);              // vacant tick 1: streak = 1, not yet raised
        Assert.Equal((byte)0x20, mem.U8(GridAddr(idx)));

        // The byte drifts before the raise (proves orig is captured AT RAISE TIME, not deferral time).
        mem.U8s[GridAddr(idx)] = 0x0A;

        bulwark.Tick(true);              // vacant tick 2: raised now, orig captured as 0x0A
        Assert.Equal(BulwarkPolicy.VetoedF6(0x0A), mem.U8(GridAddr(idx)));
    }

    [Fact]
    public void OccupiedBehindTile_OccupantNeverLeaves_NeverWritten()
    {
        var (mem, kills, toast, bulwark, entry) = Build(wx: 5, wy: 5, mapW: 11, mapH: 12);
        int idx = Idx(5, 6, 11);
        StageTerrain(mem, idx, 0x20);
        MemSeats.SeatBand(mem, 10, weapon: 0, lvl: 20, br: 30, fa: 30, gx: 5, gy: 6, hp: 100, maxHp: 100);

        RunFullWaitCycle(bulwark, mem, entry);
        SetFlags(mem, entry, 0, 0, 0);
        bulwark.Tick(true);
        bulwark.Tick(true);
        bulwark.Tick(true);

        Assert.Equal((byte)0x20, mem.U8(GridAddr(idx)));   // still occupied throughout: never written
    }

    [Fact]
    public void UnwritableVacantBehindTile_Deferred_RaisedOnceWritable()
    {
        var (mem, kills, toast, bulwark, entry) = Build(wx: 5, wy: 5, mapW: 11, mapH: 12);
        int idx = Idx(5, 6, 11);
        long addr = GridAddr(idx);
        mem.ReadableAddrs.Add(addr);
        // Deliberately no WritableAddrs entry: readable but not writable this tick, despite being
        // genuinely vacant (nobody stands there).
        mem.U8s[addr] = 0x20;

        RunFullWaitCycle(bulwark, mem, entry);
        Assert.Equal((byte)0x20, mem.U8(addr));   // vacant but unwritable: deferred, not written
        Assert.Single(toast._queue);              // the stance exists regardless

        SetFlags(mem, entry, 0, 0, 0);   // hold ticks continue while the turn flag stays CLOSED
        bulwark.Tick(true);              // still unwritable: the deferred watch keeps waiting
        Assert.Equal((byte)0x20, mem.U8(addr));

        mem.WritableAddrs.Add(addr);     // the transient miss clears

        bulwark.Tick(true);              // now writable: raised, orig captured at raise time
        Assert.Equal(BulwarkPolicy.VetoedF6(0x20), mem.U8(addr));
    }

    // ================= Release paths =================

    [Theory]
    [InlineData((byte)0x20)]
    [InlineData((byte)0x00)]
    public void Release_OnRisingEdge_RestoresExactOriginal(byte orig)
    {
        var (mem, kills, toast, bulwark, entry) = Build(wx: 5, wy: 5, mapW: 11, mapH: 12);
        int idx = Idx(5, 6, 11);
        StageTerrain(mem, idx, orig);

        RunFullWaitCycle(bulwark, mem, entry);
        Assert.Equal(BulwarkPolicy.VetoedF6(orig), mem.U8(GridAddr(idx)));

        SetFlags(mem, entry, 1, 0, 0);   // RISE: next turn opens -> release
        bulwark.Tick(true);

        Assert.Equal(orig, mem.U8(GridAddr(idx)));

        // A subsequent re-assert tick must touch nothing further (book is empty).
        int writesBefore = mem.WriteOrder.Count;
        bulwark.Tick(true);
        Assert.Equal(writesBefore, mem.WriteOrder.Count);
    }

    // ---- LW-145 fix 5: Release only restores a byte it can prove it still owns ----

    [Fact]
    public void Release_ByteStillOurs_RestoresExactOriginal()
    {
        // The ownership check's happy path, pinned explicitly alongside the mismatch case below:
        // the byte still reads as VetoedF6(saved) at release time, so it restores exactly as
        // before the fix.
        var (mem, kills, toast, bulwark, entry) = Build(wx: 5, wy: 5, mapW: 11, mapH: 12);
        int idx = Idx(5, 6, 11);
        StageTerrain(mem, idx, 0x20);

        RunFullWaitCycle(bulwark, mem, entry);
        Assert.Equal(BulwarkPolicy.VetoedF6(0x20), mem.U8(GridAddr(idx)));   // still ours, untouched since plant

        SetFlags(mem, entry, 1, 0, 0);   // RISE: next turn opens -> release
        bulwark.Tick(true);

        Assert.Equal((byte)0x20, mem.U8(GridAddr(idx)));
    }

    [Fact]
    public void Release_OwnershipMismatch_SkipsTheOverwrite_AndLogsOnce()
    {
        // Every other restore surface in the repo ownership-verifies before writing a saved
        // original back; Release didn't. Simulate another system rewriting the tile's obstacle
        // byte in the one-tick gap between MaintainPlant's last re-assert and the release edge
        // (Release sets _planted=false before Bulwark.Tick's own MaintainPlant call this same
        // tick, so nothing re-asserts the vetoed form first -- this IS the realistic race).
        var (mem, kills, toast, bulwark, entry) = Build(wx: 5, wy: 5, mapW: 11, mapH: 12);
        int idx = Idx(5, 6, 11);
        StageTerrain(mem, idx, 0x20);

        RunFullWaitCycle(bulwark, mem, entry);
        Assert.Equal(BulwarkPolicy.VetoedF6(0x20), mem.U8(GridAddr(idx)));

        // Another system's write lands: no longer OUR vetoed form of the saved original (0x22).
        mem.U8s[GridAddr(idx)] = 0x99;

        var console = new List<string>();
        var file = new List<string>();
        var prior = ModLogger.Instance;
        ModLogger.Instance = new FileConsoleLogger(console.Add, file.Add);
        try
        {
            SetFlags(mem, entry, 1, 0, 0);   // RISE: next turn opens -> release
            bulwark.Tick(true);
        }
        finally { ModLogger.Instance = prior; }

        Assert.Equal((byte)0x99, mem.U8(GridAddr(idx)));   // NOT overwritten with the stale saved original
        Assert.Contains(file, l => l.Contains("leaves that tile alone"));
    }

    [Fact]
    public void WielderDeath_Hp0_MidHold_Restores()
    {
        var (mem, kills, toast, bulwark, entry) = Build(wx: 5, wy: 5, mapW: 11, mapH: 12);
        int idx = Idx(5, 6, 11);
        StageTerrain(mem, idx, 0x20);
        RunFullWaitCycle(bulwark, mem, entry);
        Assert.Equal(BulwarkPolicy.VetoedF6(0x20), mem.U8(GridAddr(idx)));

        mem.U16s[entry + Offsets.AHp] = 0;
        SetFlags(mem, entry, 1, 0, 0);
        bulwark.Tick(true);

        Assert.Equal((byte)0x20, mem.U8(GridAddr(idx)));
    }

    [Fact]
    public void WielderDeath_DeadBit_MidHold_Restores()
    {
        var (mem, kills, toast, bulwark, entry) = Build(wx: 5, wy: 5, mapW: 11, mapH: 12);
        int idx = Idx(5, 6, 11);
        StageTerrain(mem, idx, 0x20);
        RunFullWaitCycle(bulwark, mem, entry);
        Assert.Equal(BulwarkPolicy.VetoedF6(0x20), mem.U8(GridAddr(idx)));

        mem.U8s[entry + Offsets.ADeadStatus] = Offsets.ADeadBit;
        SetFlags(mem, entry, 1, 0, 0);
        bulwark.Tick(true);

        Assert.Equal((byte)0x20, mem.U8(GridAddr(idx)));
    }

    [Fact]
    public void Unresolvable_ForBulwarkUnresolvedTicks_Restores()
    {
        var mem = new FakeSparseMemory();
        var meta = Meta();
        var kills = new Dictionary<int, int> { [SundererId] = Tuning.ProdThresholds[2] };
        var fp = (lvl: 30, br: 50, fa: 50);
        long entry = Band.Entry(24);
        MemSeats.SeatRoster(mem, 0, lvl: fp.lvl, br: fp.br, fa: fp.fa, rh: SundererId);
        MemSeats.SeatBand(mem, 24, weapon: SundererId, lvl: fp.lvl, br: fp.br, fa: fp.fa, gx: 5, gy: 5, hp: 200, maxHp: 300);
        mem.U8s[Offsets.MapDimsWH] = 11;
        mem.U8s[Offsets.MapDimsWH + 1] = 12;
        int idx = Idx(5, 6, 11);
        StageTerrain(mem, idx, 0x20);

        var toast = new BannerToast(meta, kills, enabled: true);
        var bulwark = new Bulwark(meta, kills, toast, mem);
        RunFullWaitCycle(bulwark, mem, entry);
        Assert.Equal(BulwarkPolicy.VetoedF6(0x20), mem.U8(GridAddr(idx)));

        // The wielder vanishes from the band (benched / roster wipe): the roster row now points
        // nowhere resolvable -- simulate by clearing the roster's right-hand id.
        mem.U16s[Offsets.RosterBase + 0 * Offsets.RosterStride + Offsets.RRHand] = 0xFFFF;

        for (int i = 0; i < Tuning.BulwarkUnresolvedTicks; i++) bulwark.Tick(true);

        Assert.Equal((byte)0x20, mem.U8(GridAddr(idx)));
    }

    // ================= ResetBattle: restore-then-clear (A1, inverted 2026-07-28) =================

    /// <summary>REPLACES the old ResetBattle_ClearsStateAndPerformsZeroWrites test: the grid does
    /// NOT rebuild per battle (contradicted live 2026-07-28), so ResetBattle must now ACTIVELY
    /// RESTORE a mid-hold tile before clearing state, and a second ResetBattle call (the routine
    /// enter-edge no-op that follows every exit-edge reset) must perform zero further writes.</summary>
    [Fact]
    public void ResetBattle_MidHold_RestoresHeldByteThenClears()
    {
        var (mem, kills, toast, bulwark, entry) = Build(wx: 5, wy: 5, mapW: 11, mapH: 12);
        int idx = Idx(5, 6, 11);
        StageTerrain(mem, idx, 0x20);
        RunFullWaitCycle(bulwark, mem, entry);
        Assert.Equal(BulwarkPolicy.VetoedF6(0x20), mem.U8(GridAddr(idx)));

        bulwark.ResetBattle();

        Assert.Equal((byte)0x20, mem.U8(GridAddr(idx)));   // restored to the exact original

        int writesBefore = mem.WriteOrder.Count;
        bulwark.ResetBattle();   // the routine enter-edge call: book already empty
        Assert.Equal(writesBefore, mem.WriteOrder.Count);   // zero further writes

        // And the module is fully alive afterward (state genuinely cleared, not just restored):
        // a fresh full wait plants again cleanly.
        mem.U8s[GridAddr(idx)] = 0x09;   // simulate a differing terrain byte on the next go
        mem.WritableAddrs.Add(GridAddr(idx));
        RunFullWaitCycle(bulwark, mem, entry);
        Assert.Equal(BulwarkPolicy.VetoedF6(0x09), mem.U8(GridAddr(idx)));
    }

    [Fact]
    public void ResetBattle_EmptyBook_PerformsZeroWrites()
    {
        var (mem, kills, toast, bulwark, entry) = Build(wx: 5, wy: 5, mapW: 11, mapH: 12);

        bulwark.ResetBattle();

        Assert.Empty(mem.Written);
        Assert.Empty(mem.WriteOrder);
    }

    // ================= Re-assert (per-tick hold) =================

    [Fact]
    public void ExternallyRevertedHeldByte_RewrittenNextTick()
    {
        var (mem, kills, toast, bulwark, entry) = Build(wx: 5, wy: 5, mapW: 11, mapH: 12);
        int idx = Idx(5, 6, 11);
        StageTerrain(mem, idx, 0x20);
        RunFullWaitCycle(bulwark, mem, entry);
        Assert.Equal(BulwarkPolicy.VetoedF6(0x20), mem.U8(GridAddr(idx)));

        mem.U8s[GridAddr(idx)] = 0x20;   // something else reverted it back to the original

        SetFlags(mem, entry, 0, 0, 0);   // any live tick while still held (flag stays CLOSED: no edge)
        bulwark.Tick(true);

        Assert.Equal(BulwarkPolicy.VetoedF6(0x20), mem.U8(GridAddr(idx)));
    }

    // ================= Layer + facing share one byte =================

    /// <summary>Raw ALayerBit byte 0x82 = layer 1 (bit 0x80) | facing 2 North (low 2 bits): the
    /// write must land on the North-behind tile (5,4) on LAYER 1 (idx includes +0x100). North
    /// (facing 2) looks toward +y (live pass 2026-07-28 06:15), so its behind tile is y-1.</summary>
    [Fact]
    public void LayerBitAndFacingShareTheByte()
    {
        var (mem, kills, toast, bulwark, entry) = Build(wx: 5, wy: 5, mapW: 11, mapH: 12, layerBit: 1, facing: 2);
        Assert.Equal((byte)0x82, mem.U8(entry + Offsets.ALayerBit));

        int idx = Idx(5, 4, 11, layerBit: 1);   // behind = (5,4) on layer 1
        StageTerrain(mem, idx, 0x20);

        RunFullWaitCycle(bulwark, mem, entry);

        Assert.Equal(BulwarkPolicy.VetoedF6(0x20), mem.U8(GridAddr(idx)));
    }
}
