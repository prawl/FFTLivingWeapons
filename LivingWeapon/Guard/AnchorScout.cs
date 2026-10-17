using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// LivingWeapon adapter over the portable <see cref="AnchorScan"/> core. A diagnostic
/// scout, not a healer. Engine.cs calls <see cref="Tick"/> only while LaunchGuard has stood down;
/// it re-finds the registered anchors by pin-neighborhood scan and logs an inventory, with no
/// writes, no arming and no GuardState changes. That gives a game-patch Phase B
/// (docs/PATCH_REANCHOR.md) a starting map in one log.
///
/// Anchors so far are jobcommand-table (Barrage.AbilityBase, anchored on the rec8/rec9
/// ability-byte signature LaunchGuard uses; file-baked static data, so no save is needed;
/// tools/probes/anchorscan_feasibility_probe.py 2026-07-14) and roster-base (Offsets.RosterBase,
/// anchored on Ramza's slot 0 shape, stride and %8 alignment plus a populated non-Ramza row in
/// slots +1..+3; needs a save loaded). A roster find also predicts the inventory-count table at
/// roster - 0x110 (Offsets.cs:103,353 + docs/research/PORT_1.5_OFFSETS.md:57-58). Log prose is
/// in AnchorScout.Report.cs, split from the specs and timers here.
/// </summary>
internal sealed partial class AnchorScout
{
    private const string JobCommandSpecName = "jobcommand-table";
    private const string RosterBaseSpecName = "roster-base";

    // Half-width is ~151x the max observed 1.5 static delta (+0x6C3C, Offsets.LiveBattleMapId;
    // tools/probes/anchorscan_feasibility_probe.py, LIVE_LEDGER 2026-07-14). Empirical; a miss reads NotFound.
    private const long DefaultRegionHalfWidth = 0x400000;
    // 2 chunks x 64KB per 33ms tick (Engine.PollMs); the 8MB window takes ~64 ticks.
    private const int DefaultChunkBytes = 0x10000;
    private const int ChunksPerTick = 2;
    // ~450 ticks x 33ms is about 15s, long enough for a save to load after stand-down.
    private const int RescanTicks = 450;

    private readonly AnchorSpec[] _specs;
    private readonly AnchorScan[] _scans;
    private readonly AnchorVerdict[] _lastLogged;
    private AnchorVerdict[]? _lastSummary;
    private int _ticksSinceRescan;

    public AnchorScout(IGameMemory mem) : this(mem, DefaultRegionHalfWidth, DefaultChunkBytes) { }

    /// <summary>Test seam: an injectable region half-width and chunk size so a fixture can stage a
    /// small window (e.g. a 64KB region, 8KB chunks) instead of the real multi-megabyte scan.</summary>
    public AnchorScout(IGameMemory mem, long regionHalfWidth, int chunkBytes)
    {
        _specs = new[]
        {
            BuildJobCommandSpec(mem, regionHalfWidth),
            BuildRosterBaseSpec(mem, regionHalfWidth),
        };
        AnchorTryRead tryRead = (long a, int n, out byte[] b) => mem.TryReadBytes(a, n, out b);
        _scans = new AnchorScan[_specs.Length];
        for (int i = 0; i < _specs.Length; i++)
            _scans[i] = new AnchorScan(_specs[i], tryRead, chunkBytes);
        _lastLogged = new AnchorVerdict[_specs.Length];
        for (int i = 0; i < _lastLogged.Length; i++) _lastLogged[i] = AnchorVerdict.Scanning;
    }

    /// <summary>Called only while LaunchGuard is StoodDown (Engine.cs's pre-arm branch). Steps each
    /// scanning spec a few chunks and logs a spec's concluded verdict when it changes. Once all
    /// have concluded it logs the re-find summary, again if the combination later changes. Every
    /// ~RescanTicks ticks, specs concluded anywhere but the pin are reset so a save loading after
    /// stand-down gets a fresh scan.</summary>
    public void Tick()
    {
        for (int i = 0; i < _scans.Length; i++)
        {
            var scan = _scans[i];
            for (int c = 0; c < ChunksPerTick && scan.Verdict == AnchorVerdict.Scanning; c++)
                scan.Step();

            if (scan.Verdict != AnchorVerdict.Scanning && scan.Verdict != _lastLogged[i])
            {
                LogVerdict(_specs[i], scan);
                _lastLogged[i] = scan.Verdict;
            }
        }

        MaybeLogSummary();
        MaybeRescan();
    }

    private void MaybeRescan()
    {
        if (++_ticksSinceRescan < RescanTicks) return;
        _ticksSinceRescan = 0;
        foreach (var scan in _scans)
            if (scan.Verdict != AnchorVerdict.Scanning && scan.Verdict != AnchorVerdict.FoundAtPin)
                scan.Reset();
    }

    // --- spec construction (game knowledge lives here; AnchorScan itself stays agnostic) ---

    private static AnchorSpec BuildJobCommandSpec(IGameMemory mem, long regionHalfWidth)
    {
        long pin = Barrage.AbilityBase;
        bool Confirm(long candidateBase)
        {
            long rec9Addr = candidateBase + 9L * Barrage.RecSize;
            return mem.TryReadBytes(rec9Addr, LaunchGuard.Rec9Sig.Length, out var buf)
                && BytesEqual(buf, LaunchGuard.Rec9Sig);
        }
        return new AnchorSpec(JobCommandSpecName, LaunchGuard.Rec8Sig, 8 * Barrage.RecSize, pin,
            pin - regionHalfWidth, pin + regionHalfWidth, baseAlignment: 1, confirm: Confirm);
    }

    // Row layout from Offsets.cs. One 0x232-byte read per slot beats per-field reads (~5x).
    private const int SlotRowSize = 0x232;
    private const int PopulatedSlotFrom = 1;
    private const int PopulatedSlotTo = 3;
    private const int RamzaNameId = 1;
    private const byte MonsterSpriteFloor = 0x80;   // same value as LaunchGuard.Landmarks, kept separate
    private const int MaxBraveFaith = 100;
    private const int MaxPlausibleNameId = 1024;

    private static AnchorSpec BuildRosterBaseSpec(IGameMemory mem, long regionHalfWidth)
    {
        long pin = Offsets.RosterBase;
        byte[] nameIdIsOne = { 0x01, 0x00 };   // little-endian u16 nameId==1 (Ramza), at slot 0's RNameId
        bool Confirm(long candidateBase) => ConfirmRosterRow(mem, candidateBase);
        // Calibration 2026-07-14 (tools/probes/anchorscan_feasibility_probe.py, two LIVE_LEDGER
        // AnchorScan rows). The 8MB window has 11,869 raw {01,00} hits; shape rules leave 766,
        // stride structure 2, and the %8 alignment leaves only the pin. Both known bases are
        // 16-aligned (pre-1.5's 0x1411A18D0, 1.5/1.5.1's 0x1411A7D10) and the one stride-riding
        // decoy reads %8 == 6. The core checks alignment before Confirm, rejecting ~7/8 of hits
        // without a read.
        return new AnchorSpec(RosterBaseSpecName, nameIdIsOne, Offsets.RNameId, pin,
            pin - regionHalfWidth, pin + regionHalfWidth, baseAlignment: 8, confirm: Confirm);
    }

    /// <summary>Slot 0's nameId is 1 by the signature that found this candidate; this checks the rest
    /// of slot 0 is a plausible unit, then needs at least one of slots +1..+3 populated by a non-Ramza
    /// unit (recalibrated 2026-07-14; solo or early-prologue saves are unverified). Any slot that
    /// fails to read fails the confirm.</summary>
    private static bool ConfirmRosterRow(IGameMemory mem, long candidateBase)
    {
        if (!mem.TryReadBytes(candidateBase, SlotRowSize, out var slot0)) return false;
        int level0 = slot0[Offsets.RLevel];
        byte sprite0 = slot0[Offsets.RSprite];
        int brave0 = slot0[Offsets.RBrave];
        int faith0 = slot0[Offsets.RFaith];
        if (level0 < 1 || level0 > 99) return false;
        if (sprite0 >= MonsterSpriteFloor) return false;
        if (brave0 < 1 || brave0 > MaxBraveFaith) return false;
        if (faith0 < 1 || faith0 > MaxBraveFaith) return false;

        bool anyPopulatedNonRamza = false;
        for (int k = PopulatedSlotFrom; k <= PopulatedSlotTo; k++)
        {
            long slotAddr = candidateBase + (long)k * Offsets.RosterStride;
            if (!mem.TryReadBytes(slotAddr, SlotRowSize, out var row)) return false;
            int level = row[Offsets.RLevel];
            int brave = row[Offsets.RBrave];
            int faith = row[Offsets.RFaith];
            int nameId = row[Offsets.RNameId] | (row[Offsets.RNameId + 1] << 8);
            if (level > 99 || brave > MaxBraveFaith || faith > MaxBraveFaith || nameId >= MaxPlausibleNameId)
                return false;
            if (level >= 1 && level <= 99 && nameId != RamzaNameId) anyPopulatedNonRamza = true;
        }
        return anyPopulatedNonRamza;
    }

    private static bool BytesEqual(IReadOnlyList<byte> a, IReadOnlyList<byte> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++) if (a[i] != b[i]) return false;
        return true;
    }
}
