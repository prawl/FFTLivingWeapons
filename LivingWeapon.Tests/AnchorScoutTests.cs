using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-82's LivingWeapon adapter over the portable AnchorScan core (AnchorScout.cs +
/// AnchorScout.Report.cs): the jobcommand-table and roster-base specs, the Tick budget, the
/// verdict-change-gated log lines, the once-then-change-gated summary, and the rescan timer.
///
/// FakeSparseMemory.TryReadBytes now serves any read fully CONTAINED in a registered TerrainBlocks
/// entry (widened by this same LW-82 commit), so each test stages one wide byte[] region per
/// spec's scan window and pokes individual fields into it at the real Offsets/Barrage byte
/// positions AnchorScout's own spec builders compute from: the same production addresses, just
/// staged in a fake.
/// </summary>
public class AnchorScoutTests
{
    private const long JobPin = Barrage.AbilityBase;
    private const long RosterPin = Offsets.RosterBase;
    private const long Half = 0x10000;      // 64KB test region (the plan's fixture-size guidance)
    private const int ChunkBytes = 0x2000;  // 8KB chunks

    private static AnchorScout NewScout(FakeSparseMemory mem) => new(mem, Half, ChunkBytes);

    private static void StageRegion(FakeSparseMemory mem, long pin) =>
        mem.TerrainBlocks[pin - Half] = new byte[2 * Half];

    private static void PutBytes(FakeSparseMemory mem, long pin, long addr, byte[] bytes) =>
        Array.Copy(bytes, 0, mem.TerrainBlocks[pin - Half], addr - (pin - Half), bytes.Length);

    private static void PutByte(FakeSparseMemory mem, long pin, long addr, byte value) =>
        PutBytes(mem, pin, addr, new[] { value });

    private static void PutU16(FakeSparseMemory mem, long pin, long addr, int value) =>
        PutBytes(mem, pin, addr, new byte[] { (byte)value, (byte)(value >> 8) });

    private static void StageJobCommandAt(FakeSparseMemory mem, long tableBase)
    {
        PutBytes(mem, JobPin, tableBase + 8L * Barrage.RecSize, LaunchGuard.Rec8Sig);
        PutBytes(mem, JobPin, tableBase + 9L * Barrage.RecSize, LaunchGuard.Rec9Sig);
    }

    private static void StageRosterSlot0(FakeSparseMemory mem, long rosterBase, int level, byte sprite, int brave, int faith)
    {
        PutByte(mem, RosterPin, rosterBase + Offsets.RSprite, sprite);
        PutByte(mem, RosterPin, rosterBase + Offsets.RLevel, (byte)level);
        PutByte(mem, RosterPin, rosterBase + Offsets.RBrave, (byte)brave);
        PutByte(mem, RosterPin, rosterBase + Offsets.RFaith, (byte)faith);
        PutU16(mem, RosterPin, rosterBase + Offsets.RNameId, 1);   // the signature itself: nameId==1 (Ramza)
    }

    private static void StageRosterSlot(FakeSparseMemory mem, long rosterBase, int slot, int level, int nameId, int brave, int faith)
    {
        long addr = rosterBase + (long)slot * Offsets.RosterStride;
        PutByte(mem, RosterPin, addr + Offsets.RLevel, (byte)level);
        PutByte(mem, RosterPin, addr + Offsets.RBrave, (byte)brave);
        PutByte(mem, RosterPin, addr + Offsets.RFaith, (byte)faith);
        PutU16(mem, RosterPin, addr + Offsets.RNameId, nameId);
    }

    private static void StageHealthyRoster(FakeSparseMemory mem, long rosterBase)
    {
        StageRosterSlot0(mem, rosterBase, level: 50, sprite: 0x02, brave: 70, faith: 65);
        StageRosterSlot(mem, rosterBase, slot: 1, level: 40, nameId: 42, brave: 50, faith: 55);   // populated, non-Ramza
    }

    private static (List<string> console, List<string> file) SwapLogger()
    {
        var console = new List<string>();
        var file = new List<string>();
        ModLogger.Instance = new FileConsoleLogger(console.Add, file.Add);
        return (console, file);
    }

    private static void TickMany(AnchorScout scout, int n) { for (int i = 0; i < n; i++) scout.Tick(); }

    // ---- 13. LOAD-BEARING: jobcommand content moved to pin+0x6000; zeros remain at the pin ----

    [Fact]
    public void JobCommand_found_elsewhere_reports_the_new_location_and_delta_on_the_trace_line()
    {
        var mem = new FakeSparseMemory();
        StageRegion(mem, JobPin);
        StageJobCommandAt(mem, JobPin + 0x6000);   // sabotage target: break base math or the boundary handling and this reds
        var prior = ModLogger.Instance;
        var (_, file) = SwapLogger();
        try
        {
            var scout = NewScout(mem);
            TickMany(scout, 30);   // region/chunkBytes exhausts well within this budget

            long expectedBase = JobPin + 0x6000;
            Assert.Contains(file, l => l.Contains($"found elsewhere at 0x{expectedBase:X}"));
            Assert.Contains(file, l => l.Contains("delta +0x6000"));
        }
        finally { ModLogger.Instance = prior; }
    }

    // ---- 14. healthy staging -> both anchors at-pin + one summary ----

    [Fact]
    public void Healthy_staging_produces_both_at_pin_lines_and_one_summary()
    {
        var mem = new FakeSparseMemory();
        StageRegion(mem, JobPin);
        StageJobCommandAt(mem, JobPin);
        StageRegion(mem, RosterPin);
        StageHealthyRoster(mem, RosterPin);
        var prior = ModLogger.Instance;
        var (_, file) = SwapLogger();
        try
        {
            var scout = NewScout(mem);
            TickMany(scout, 30);

            Assert.Contains(file, l => l.Contains("anchor jobcommand-table: found at pin"));
            Assert.Contains(file, l => l.Contains("anchor roster-base: found at pin"));
            Assert.Single(file, l => l.Contains("anchor scout:"));
            Assert.Contains(file, l => l.Contains("anchor scout: 2 at pin, 0 elsewhere, 0 ambiguous, 0 not found"));
        }
        finally { ModLogger.Instance = prior; }
    }

    // ---- 15. roster decoy staging (stride-riding misaligned candidate) -> rejected, FoundAtPin ----

    [Fact]
    public void Roster_decoy_at_a_misaligned_base_is_rejected_and_the_real_pin_still_wins()
    {
        var mem = new FakeSparseMemory();
        StageRegion(mem, RosterPin);
        StageHealthyRoster(mem, RosterPin);
        // A stride-riding decoy 5 bytes off the real base (misaligned: 5 % 8 != 0), shaped to pass
        // Confirm's own bounds if BaseAlignment did not reject it first: the measured real-world
        // shape (766 shape-only candidates narrow to the pin by alignment, not by Confirm alone).
        long decoyBase = RosterPin + 5;
        StageRosterSlot0(mem, decoyBase, level: 60, sprite: 0x01, brave: 80, faith: 75);
        StageRosterSlot(mem, decoyBase, slot: 1, level: 30, nameId: 99, brave: 40, faith: 45);
        var prior = ModLogger.Instance;
        var (_, file) = SwapLogger();
        try
        {
            var scout = NewScout(mem);
            TickMany(scout, 30);

            Assert.Contains(file, l => l.Contains("anchor roster-base: found at pin"));
            Assert.DoesNotContain(file, l => l.Contains("anchor roster-base: ambiguous"));
        }
        finally { ModLogger.Instance = prior; }
    }

    // ---- 16. log-once: repeat Ticks after conclusion add no further lines ----

    [Fact]
    public void Repeat_ticks_after_conclusion_add_no_further_lines()
    {
        var mem = new FakeSparseMemory();
        StageRegion(mem, JobPin);
        StageJobCommandAt(mem, JobPin);
        StageRegion(mem, RosterPin);
        StageHealthyRoster(mem, RosterPin);
        var prior = ModLogger.Instance;
        var (_, file) = SwapLogger();
        try
        {
            var scout = NewScout(mem);
            TickMany(scout, 30);   // fully concludes both specs and logs the summary
            int countAfterFirstRun = file.Count;

            TickMany(scout, 60);   // well short of RescanTicks (450): no rescan interferes

            Assert.Equal(countAfterFirstRun, file.Count);
        }
        finally { ModLogger.Instance = prior; }
    }

    // ---- 17. rescan upgrade: roster region empty -> NotFound + summary; populate -> one new
    // ---- anchor line + one re-emitted summary after RescanTicks ----

    [Fact]
    public void Rescan_upgrades_a_not_found_roster_after_a_save_populates_it()
    {
        var mem = new FakeSparseMemory();
        StageRegion(mem, JobPin);
        StageJobCommandAt(mem, JobPin);
        // Roster region deliberately left UNSTAGED: every read fails, so the scan concludes NotFound.
        var prior = ModLogger.Instance;
        var (_, file) = SwapLogger();
        try
        {
            var scout = NewScout(mem);
            TickMany(scout, 30);   // both specs conclude: jobcommand at pin, roster not found

            Assert.Contains(file, l => l.Contains("anchor roster-base: not found"));
            Assert.Contains(file, l => l.Contains("anchor scout: 1 at pin, 0 elsewhere, 0 ambiguous, 1 not found"));
            int countBeforeRescan = file.Count;

            // "a save loads": the roster region is now populated, healthy, at its pin.
            StageRegion(mem, RosterPin);
            StageHealthyRoster(mem, RosterPin);

            TickMany(scout, 450);   // RescanTicks: rearms every concluded non-pin spec

            var newLines = file.Skip(countBeforeRescan).ToList();
            Assert.Single(newLines, l => l.Contains("anchor roster-base: found at pin"));
            Assert.Single(newLines, l => l.Contains("anchor scout: 2 at pin, 0 elsewhere, 0 ambiguous, 0 not found"));
        }
        finally { ModLogger.Instance = prior; }
    }

    // ---- 18. sibling line exactly once, base - 0x110 ----

    [Fact]
    public void Roster_find_predicts_the_inventory_count_sibling_exactly_once()
    {
        var mem = new FakeSparseMemory();
        StageRegion(mem, RosterPin);
        StageHealthyRoster(mem, RosterPin);
        var prior = ModLogger.Instance;
        var (_, file) = SwapLogger();
        try
        {
            var scout = NewScout(mem);
            TickMany(scout, 30);

            long expectedInventoryBase = RosterPin - 0x110;
            Assert.Single(file, l => l.Contains("sibling prediction") && l.Contains($"0x{expectedInventoryBase:X}"));
        }
        finally { ModLogger.Instance = prior; }
    }

    // ---- 19. a full scout run performs ZERO writes (the non-vacuous half: real work happened) ----

    [Fact]
    public void A_full_scout_run_performs_zero_writes()
    {
        var mem = new FakeSparseMemory();
        StageRegion(mem, JobPin);
        StageJobCommandAt(mem, JobPin);
        StageRegion(mem, RosterPin);
        StageHealthyRoster(mem, RosterPin);
        var prior = ModLogger.Instance;
        var (_, file) = SwapLogger();
        bool priorWrites = Mem.WritesEnabled;
        try
        {
            var scout = NewScout(mem);
            TickMany(scout, 500);   // runs through a full rescan cycle too, not just the first conclusion

            // Non-vacuous: real scanning work actually happened (both anchors resolved and the
            // summary logged), so the zero-writes assertions below are not trivially true of a no-op.
            Assert.Contains(file, l => l.Contains("anchor jobcommand-table: found at pin"));
            Assert.Contains(file, l => l.Contains("anchor roster-base: found at pin"));
            Assert.Contains(file, l => l.Contains("anchor scout:"));

            Assert.Empty(mem.Written);
            Assert.Empty(mem.WrittenU16);
            Assert.Empty(mem.WrittenBytes);
            Assert.Equal(priorWrites, Mem.WritesEnabled);   // bonus: the scout never touches the global write gate
        }
        finally { ModLogger.Instance = prior; }
    }
}
