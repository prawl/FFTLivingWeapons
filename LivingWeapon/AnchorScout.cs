using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// LW-82: the LivingWeapon adapter over the portable <see cref="AnchorScan"/> core. VERIFIER
/// SCOUT, NOT HEALER (owner-locked design decision): Engine.cs calls <see cref="Tick"/> ONLY while
/// LaunchGuard has stood down; this re-finds the registered anchors by pin-neighborhood scans,
/// logs a re-find inventory, and influences NOTHING (no writes, no arming, no GuardState changes).
/// Every consumer keeps trusting Offsets/Barrage's own pins; this is diagnostics only, turning a
/// game-patch event's Phase B (docs/PATCH_REANCHOR.md) from a multi-hour live probe session into
/// "read one log for the starting map".
///
/// v1 anchors (first LW-82 slice): jobcommand-table (Barrage.AbilityBase, content-anchored on the
/// rec8/rec9 ability-byte signature LaunchGuard's own landmark already uses; FILE-BAKED static
/// image data, so it needs no save loaded, tools/probes/anchorscan_feasibility_probe.py 2026-07-14)
/// and roster-base (Offsets.RosterBase, shape + stride + %8-alignment-anchored on Ramza's slot 0
/// plus any populated non-Ramza row in slots +1..+3; needs a save loaded to populate rows). A
/// roster find additionally predicts the inventory-count table as a sibling (roster - 0x110, the
/// delta held across pre-1.5/1.5/1.5.1: Offsets.cs:103,353 + docs/research/PORT_1.5_OFFSETS.md:
/// 57-58); see AnchorScout.Report.cs for that and every other log line's exact prose.
///
/// This file owns the specs, the Tick budget, and the rescan timer; AnchorScout.Report.cs owns
/// turning a concluded verdict into log lines (a real WHAT-vs-narration seam, the LaunchGuard/
/// Landmarks precedent).
/// </summary>
internal sealed partial class AnchorScout
{
    private const string JobCommandSpecName = "jobcommand-table";
    private const string RosterBaseSpecName = "roster-base";

    // Window margin: +/-0x400000 half-width vs the max observed 1.5 static delta (+0x6C3C,
    // Offsets.LiveBattleMapId) is about 151x (tools/probes/anchorscan_feasibility_probe.py,
    // LIVE_LEDGER 2026-07-14 rows): empirically supported for these regions, not theoretically
    // bounded. A miss reads NotFound, which is an honest report, never a silent false success.
    private const long DefaultRegionHalfWidth = 0x400000;
    // 2 chunks/tick x 64KB = 128KB scanned per 33ms Engine tick (Engine.PollMs): an 8MB default
    // window scans out in roughly 64 ticks (about 2 seconds), fast without hogging the tick the
    // guard's own Step still runs on every StoodDown tick.
    private const int DefaultChunkBytes = 0x10000;
    private const int ChunksPerTick = 2;
    // ~450 ticks x 33ms is about 15s: covers a save loading after the guard already stood down
    // (the roster region reads all-zero pre-save, a legitimate NotFound) and any other pre-save
    // garbage verdict, without restarting the mod. Any concluded non-pin verdict re-arms.
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

    /// <summary>Called only while LaunchGuard is StoodDown (Engine.cs's pre-arm branch). Steps
    /// every still-scanning spec a small budget of chunks, logs any spec whose CONCLUDED verdict
    /// changed since it was last reported, then (once every spec has concluded) logs the re-find
    /// summary once, re-emitting it whenever the concluded combination later changes (a rescan
    /// upgrade). Every ~RescanTicks ticks, every spec concluded anywhere OTHER than its pin re-arms
    /// (Reset), so a save loading after stand-down gets a fresh chance without restarting the mod.
    /// A spec still Scanning is left alone by the rescan (nothing to re-arm).</summary>
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

    // Roster row layout, cited from Offsets.cs (RLevel/RSprite/RBrave/RFaith/RNameId/RosterStride):
    // one whole-row read per slot (0x232 bytes covers up to and including RNameId's 2 bytes),
    // never a scalar U8/U16 (a live RPM call per field would cost roughly 5x a single bulk read).
    private const int SlotRowSize = 0x232;
    private const int PopulatedSlotFrom = 1;
    private const int PopulatedSlotTo = 3;
    private const int RamzaNameId = 1;
    private const byte MonsterSpriteFloor = 0x80;   // mirrors LaunchGuard.Landmarks' own constant; not shared (independent adapters, one byte each)
    private const int MaxBraveFaith = 100;
    private const int MaxPlausibleNameId = 1024;

    private static AnchorSpec BuildRosterBaseSpec(IGameMemory mem, long regionHalfWidth)
    {
        long pin = Offsets.RosterBase;
        byte[] nameIdIsOne = { 0x01, 0x00 };   // little-endian u16 nameId==1 (Ramza), at slot 0's RNameId
        bool Confirm(long candidateBase) => ConfirmRosterRow(mem, candidateBase);
        // Calibration (live 2026-07-14, tools/probes/anchorscan_feasibility_probe.py + the two
        // LIVE_LEDGER AnchorScan rows): 11,869 raw {01,00} hits in the 8MB window; the shape rules
        // alone leave 766 candidates; stride structure leaves 2; the %8 alignment demand leaves
        // exactly the pin (both known bases are 16-aligned, pre-1.5's 0x1411A18D0 and
        // 1.5/1.5.1's 0x1411A7D10, and the one stride-riding decoy reads %8 == 6). Alignment is
        // checked by the core BEFORE Confirm, so ~7/8 of raw hits are rejected without a read.
        return new AnchorSpec(RosterBaseSpecName, nameIdIsOne, Offsets.RNameId, pin,
            pin - regionHalfWidth, pin + regionHalfWidth, baseAlignment: 8, confirm: Confirm);
    }

    /// <summary>Slot 0's nameId is already guaranteed 1 by the very signature that found this
    /// candidate; this confirms the REST of slot 0's row is a plausible unit (level/sprite/brave/
    /// faith), then demands at least one of slots +1..+3 is populated by a non-Ramza unit (the
    /// relaxed "any of three" rule: solo-roster or early-prologue saves where slot +1 alone might
    /// be empty are unverified, recalibrated live 2026-07-14, still exactly the pin). A slot that
    /// fails to read at all fails the whole confirm (fail-closed).</summary>
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
