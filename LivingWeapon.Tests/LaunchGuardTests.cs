using System;
using System.Collections.Generic;
using System.Reflection;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-50's LivingWeapon adapter over FingerprintGuard, split by LW-112 and unchained by LW-393 into
/// three independent state machines. The MAIN guard checks two data-only landmarks (PE build key,
/// Ramza's roster row shape); a mismatch there stands the roster door down (writes stay disabled all
/// session). The KIT-LANE guard owns the JobCommand rec8/rec9 ability-byte signature and the SENSING
/// lane owns the six battle sentinels. Both lanes step from the first tick, the kit lane judges only once a save has loaded, and neither waits for the
/// main guard to arm. A lane mismatch closes only its own door (the weapon-granted commands, or the
/// new game detector) while everything else keeps working. Post-arm semantics are unchanged for the
/// kit lane: an all-zero rec8/rec9 window is a Mismatch (blanked), not a boot window, because that
/// table is file baked static data. Only a genuine read failure still reads Unreadable. No guard
/// raises a box on a closure any more; the parts board does (PartsBoardTests), except the LW-392
/// stall box, which stays the main guard's own.
///
/// FakeSparseMemory.TryReadBytes serves any read fully contained in a registered TerrainBlocks
/// entry (LW-82 widened this from exact-address-only), so every PE and JobCommand read here is
/// staged there; U8/U16 roster fields go through the U8s/U16s dictionaries (unseeded reads default
/// to 0, i.e. an unpopulated roster slot).
/// </summary>
public class LaunchGuardTests
{
    private const long ModuleBase = 0x140000000L;
    private const long ELfanewOff = 0x3C;
    private const uint ELfanew = 0x100;   // an arbitrary plausible e_lfanew offset
    private const long TimeDateStampOff = 8;
    private const long SizeOfImageOff = 0x50;

    private static void SeedU32Bytes(FakeSparseMemory mem, long addr, uint value) =>
        mem.TerrainBlocks[addr] = new byte[]
        {
            (byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24),
        };

    private static void StagePeHealthy(FakeSparseMemory mem)
    {
        SeedU32Bytes(mem, ModuleBase + ELfanewOff, ELfanew);
        SeedU32Bytes(mem, ModuleBase + ELfanew + TimeDateStampOff, LaunchGuard.ExpectedTimeDateStamp);
        SeedU32Bytes(mem, ModuleBase + ELfanew + SizeOfImageOff, LaunchGuard.ExpectedSizeOfImage);
    }

    private static void StageJobCommandHealthy(FakeSparseMemory mem)
    {
        long rec8 = Barrage.AbilityBase + 8L * Barrage.RecSize;
        long rec9 = Barrage.AbilityBase + 9L * Barrage.RecSize;
        mem.TerrainBlocks[rec8] = new byte[] { 0x96, 0x97, 0x98, 0x99, 0x9A, 0x9B, 0x9C, 0x9D };
        mem.TerrainBlocks[rec9] = new byte[] { 0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6A, 0x6B };
    }

    private static void StageRamzaRow(FakeSparseMemory mem, int level, int nameId, byte sprite, int brave, int faith)
    {
        long rb = Offsets.RosterBase;
        mem.U8s[rb + Offsets.RLevel] = (byte)level;
        mem.U16s[rb + Offsets.RNameId] = (ushort)nameId;
        mem.U8s[rb + Offsets.RSprite] = sprite;
        mem.U8s[rb + Offsets.RBrave] = (byte)brave;
        mem.U8s[rb + Offsets.RFaith] = (byte)faith;
    }

    internal static FakeSparseMemory HealthyMemory(int level = 12, int nameId = 1, byte sprite = 0x02,
        int brave = 70, int faith = 65)
    {
        var mem = new FakeSparseMemory();
        StagePeHealthy(mem);
        StageJobCommandHealthy(mem);
        StageRamzaRow(mem, level, nameId, sprite, brave, faith);
        StageTwinFlags(mem, 0, 0);
        return mem;
    }

    internal static void StageTwinFlags(FakeSparseMemory mem, byte menu, byte browse)
    {
        mem.TerrainBlocks[Offsets.MenuOpenFlag] = new byte[] { menu };
        mem.TerrainBlocks[Offsets.PartyBrowseFlag] = new byte[] { browse };
    }

    private static void StepTwinN(LaunchGuard g, int n) { for (int i = 0; i < n; i++) g.StepTwin(); }

    // LW-387 (ported by LW-388): the Denuvo-free 1.5.2 build loads at a random (ASLR) base. These
    // stage the PE header, Ramza's roster row and the JobCommand bytes at addresses that FOLLOW the
    // live image base, so they must be called AFTER ImageBase.Set (or deliberately before it, in the
    // unresolved mirror).
    private const long RelocatedBase = 0x7FF6_1234_0000L;

    private static FakeSparseMemory RelocatedHealthyMemory()
    {
        var mem = new FakeSparseMemory();
        SeedU32Bytes(mem, ImageBase.Base + ELfanewOff, ELfanew);
        SeedU32Bytes(mem, ImageBase.Base + ELfanew + TimeDateStampOff, LaunchGuard.ExpectedTimeDateStamp);
        SeedU32Bytes(mem, ImageBase.Base + ELfanew + SizeOfImageOff, LaunchGuard.ExpectedSizeOfImage);
        long rec8 = Barrage.AbilityBase + 8L * Barrage.RecSize;
        long rec9 = Barrage.AbilityBase + 9L * Barrage.RecSize;
        mem.TerrainBlocks[rec8] = new byte[] { 0x96, 0x97, 0x98, 0x99, 0x9A, 0x9B, 0x9C, 0x9D };
        mem.TerrainBlocks[rec9] = new byte[] { 0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6A, 0x6B };
        StageRamzaRow(mem, 12, 1, 0x02, 70, 65);
        return mem;
    }

    [Fact]
    public void Guard_arms_when_the_image_is_relocated_and_the_base_is_resolved()
    {
        try
        {
            Assert.True(ImageBase.Set(RelocatedBase));
            var mem = RelocatedHealthyMemory();
            var guard = new LaunchGuard(mem, forceMismatch: false);

            guard.Step();

            Assert.Equal(GuardState.Armed, guard.State);
        }
        finally { ImageBase.ResetForTests(); }
    }

    [Fact]
    public void Guard_stays_unreadable_when_the_image_is_relocated_but_the_base_is_not_resolved()
    {
        // The exact player symptom before LW-387: the image sits at a random base, the mod still
        // reads the preferred one, every landmark read fails, the guard retries silently forever.
        try
        {
            ImageBase.Set(RelocatedBase);
            var mem = RelocatedHealthyMemory();   // staged at the relocated addresses
            ImageBase.ResetForTests();            // the mod never learned the live base
            var guard = new LaunchGuard(mem, forceMismatch: false);

            var notices = new List<(string title, string text)>();
            using var cap = LogCapture.Start();
            guard = new LaunchGuard(mem, forceMismatch: false, notice: (t, x) => notices.Add((t, x)));

            for (int i = 0; i < LaunchGuard.ImageStallSteps + 1; i++) guard.Step();

            Assert.Equal(GuardState.Verifying, guard.State);
            // LW-392: the same symptom now speaks, once in the log (WARN) and once in a box.
            Assert.Single(notices);
            Assert.Single(cap.File, l => l.Contains("[WARN]") && l.Contains("has not been able to read the game"));
        }
        finally { ImageBase.ResetForTests(); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(200)]
    public void RamzaRow_unpopulated_is_unreadable(int level)
    {
        var mem = HealthyMemory(level: level);
        var guard = new LaunchGuard(mem, forceMismatch: false);

        for (int i = 0; i < 50; i++) guard.Step();

        Assert.Equal(GuardState.Verifying, guard.State);
    }

    [Fact]
    public void RamzaRow_wrong_nameId_is_mismatch()
    {
        var mem = HealthyMemory(nameId: 5);
        var guard = new LaunchGuard(mem, forceMismatch: false);

        for (int i = 0; i < 40; i++) guard.Step();

        Assert.Equal(GuardState.StoodDown, guard.State);
    }

    [Fact]
    public void RamzaRow_monster_sprite_is_mismatch()
    {
        var mem = HealthyMemory(sprite: 0x82);
        var guard = new LaunchGuard(mem, forceMismatch: false);

        for (int i = 0; i < 40; i++) guard.Step();

        Assert.Equal(GuardState.StoodDown, guard.State);
    }

    [Fact]
    public void RamzaRow_valid_is_match()
    {
        var mem = HealthyMemory();
        var guard = new LaunchGuard(mem, forceMismatch: false);

        guard.Step();

        Assert.Equal(GuardState.Armed, guard.State);
    }

    [Fact]
    public void KitLane_read_failure_stays_verifying_silently()
    {
        // LW-112: the old JobCommand-vs-empty-roster boot-window test is vacuous now (the JobCommand
        // landmark moved off the main guard entirely, so an empty roster row no longer touches it at
        // all). Its replacement, at the lane level: a failed read (the window absent from the fake,
        // mirroring a transient RPM failure) must leave the lane Verifying forever, silently -- no
        // notice, no stand-down -- exactly like any other Unreadable landmark.
        bool prevWrites = Mem.WritesEnabled;
        try
        {
            Mem.WritesEnabled = false;
            var mem = HealthyMemory();
            long rec8 = Barrage.AbilityBase + 8L * Barrage.RecSize;
            mem.TerrainBlocks.Remove(rec8);   // absent from the fake: TryReadBytes fails outright
            var calls = new List<(string title, string text)>();
            var guard = new LaunchGuard(mem, forceMismatch: false, notice: (title, text) => calls.Add((title, text)));

            guard.Step();
            Assert.Equal(GuardState.Armed, guard.State);

            for (int i = 0; i < 35; i++) guard.StepKitLane();

            Assert.Equal(GuardState.Verifying, guard.KitLaneState);
            Assert.False(guard.KitLaneArmed);
            Assert.Empty(calls);
        }
        finally { Mem.WritesEnabled = prevWrites; }
    }

    [Fact]
    public void ExpectedPeKey_constants_are_pinned()
    {
        Assert.Equal(0x6A5E4A1Bu, LaunchGuard.ExpectedTimeDateStamp);
        Assert.Equal(0x03F9E000u, LaunchGuard.ExpectedSizeOfImage);
    }

    [Fact]
    public void ForceMismatch_arms_and_warns_unknown_build()
    {
        // LW-397: the drill perturbs the expected stamp; the stamp is a report now, never a switch.
        WithWritesRestored(() =>
        {
            using var cap = LogCapture.Start();
            var calls = new List<(string title, string text)>();
            var guard = new LaunchGuard(HealthyMemory(), forceMismatch: true, notice: (t, x) => calls.Add((t, x)));

            for (int i = 0; i < 40; i++) guard.Step();

            Assert.Equal(GuardState.Armed, guard.State);
            Assert.False(guard.BuildStampKnown);
            string line = Assert.Single(cap.File, l => l.Contains("new to the mod"));
            Assert.Contains("LW_FORCE_FINGERPRINT_MISMATCH", line);
            Assert.Empty(calls);
        });
    }

    [Fact]
    public void Armed_edge_enables_mem_writes()
    {
        bool prev = Mem.WritesEnabled;
        try
        {
            Mem.WritesEnabled = false;
            var mem = HealthyMemory();
            var guard = new LaunchGuard(mem, forceMismatch: false);

            guard.Step();

            Assert.Equal(GuardState.Armed, guard.State);
            Assert.True(Mem.WritesEnabled);
        }
        finally { Mem.WritesEnabled = prev; }
    }

    [Fact]
    public void StoodDown_never_enables_writes()
    {
        bool prev = Mem.WritesEnabled;
        try
        {
            Mem.WritesEnabled = false;
            var mem = HealthyMemory(nameId: 5);   // mismatch
            var guard = new LaunchGuard(mem, forceMismatch: false);

            for (int i = 0; i < 40; i++) guard.Step();

            Assert.Equal(GuardState.StoodDown, guard.State);
            Assert.False(Mem.WritesEnabled);
        }
        finally { Mem.WritesEnabled = prev; }
    }

    [Fact]
    public void StandDown_never_invokes_the_notice_itself()
    {
        // LW-393: the box moved to the parts board, which shows one per closure wave.
        var mem = HealthyMemory(nameId: 5);   // mismatch
        var calls = new List<(string title, string text)>();
        var guard = new LaunchGuard(mem, forceMismatch: false, notice: (title, text) => calls.Add((title, text)));
        for (int i = 0; i < 40; i++) guard.Step();
        Assert.Equal(GuardState.StoodDown, guard.State);

        for (int i = 0; i < 20; i++) guard.Step();

        Assert.Empty(calls);
    }

    [Fact]
    public void Roster_stand_down_error_line_names_the_parts_and_keeps_the_scanner_phrase()
    {
        using var cap = LogCapture.Start();
        var guard = new LaunchGuard(HealthyMemory(nameId: 5), forceMismatch: false);

        for (int i = 0; i < 40; i++) guard.Step();

        string line = Assert.Single(cap.File, l => l.Contains("[ERROR]"));
        Assert.Contains("standing down to protect your save", line);
        Assert.Contains("the parts that need it are standing down to protect your save", line);
        Assert.Contains("see the Parts report line for what still runs", line);
        Assert.DoesNotContain("LW_FORCE_", line);
    }

    [Fact]
    public void Armed_never_invokes_notice()
    {
        var mem = HealthyMemory();
        var calls = new List<(string title, string text)>();
        var guard = new LaunchGuard(mem, forceMismatch: false, notice: (title, text) => calls.Add((title, text)));

        guard.Step();
        Assert.Equal(GuardState.Armed, guard.State);

        for (int i = 0; i < 20; i++) guard.Step();

        Assert.Empty(calls);
    }

    // --- LW-53: the guard lifecycle records into the flight ring and requests its own dedicated
    // "standdown" flush, so a stand-down leaves a durable archive, not just the log line. ---

    [Fact]
    public void StandDown_records_guard_flight_entry_with_landmark_diag()
    {
        var mem = HealthyMemory(nameId: 5);   // mismatch: ramza-roster-row only
        var records = new List<(string type, string payload)>();
        var guard = new LaunchGuard(mem, forceMismatch: false, recorder: (t, p) => records.Add((t, p)));

        for (int i = 0; i < 40; i++) guard.Step();
        Assert.Equal(GuardState.StoodDown, guard.State);

        Assert.Single(records);
        Assert.Equal("guard", records[0].type);
        Assert.Contains("stand-down", records[0].payload);
        Assert.Contains("ramza-roster-row", records[0].payload);

        for (int i = 0; i < 20; i++) guard.Step();   // stand-down is terminal: no repeat record

        Assert.Single(records);
    }

    [Fact]
    public void Armed_edge_records_guard_flight_entry()
    {
        bool prev = Mem.WritesEnabled;
        try
        {
            Mem.WritesEnabled = false;
            var mem = HealthyMemory();
            var records = new List<(string type, string payload)>();
            var guard = new LaunchGuard(mem, forceMismatch: false, recorder: (t, p) => records.Add((t, p)));

            guard.Step();

            Assert.Equal(GuardState.Armed, guard.State);
            Assert.Single(records);
            Assert.Equal("guard", records[0].type);
            Assert.Contains("armed", records[0].payload);

            for (int i = 0; i < 20; i++) guard.Step();   // armed is terminal: no repeat record

            Assert.Single(records);
        }
        finally { Mem.WritesEnabled = prev; }
    }

    [Fact]
    public void StandDown_requests_a_standdown_flush_exactly_once()
    {
        var mem = HealthyMemory(nameId: 5);   // mismatch
        var triggers = new List<string>();
        var guard = new LaunchGuard(mem, forceMismatch: false, requestFlush: t => triggers.Add(t));

        for (int i = 0; i < 40; i++) guard.Step();
        Assert.Equal(GuardState.StoodDown, guard.State);

        for (int i = 0; i < 20; i++) guard.Step();   // stand-down is terminal: no repeat request

        Assert.Single(triggers);
        Assert.Equal("standdown", triggers[0]);
    }

    [Fact]
    public void Armed_edge_never_requests_a_flush()
    {
        var mem = HealthyMemory();
        var triggers = new List<string>();
        var guard = new LaunchGuard(mem, forceMismatch: false, requestFlush: t => triggers.Add(t));

        guard.Step();

        Assert.Equal(GuardState.Armed, guard.State);
        Assert.Empty(triggers);
    }

    [Fact]
    public void Notice_default_is_null_so_tests_never_raise_ui()
    {
        // Reflection on the private field (rather than behavior alone) pins the exact landmine: a
        // re-added "?? <the real OS notice>" coalesce in the ctor would raise a real Win32 message
        // box on every dotnet test run, and only the field itself proves the default is truly null
        // rather than a fallback delegate that happens not to have been called yet.
        var mem = HealthyMemory(nameId: 5);   // mismatch, so the guard below reaches StoodDown
        var guard = new LaunchGuard(mem, forceMismatch: false);

        var field = typeof(LaunchGuard).GetField("_notice", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        Assert.Null(field!.GetValue(guard));

        // The notice-less guard must still stand down cleanly: the "?.Invoke" null path must not throw.
        for (int i = 0; i < 40; i++) guard.Step();

        Assert.Equal(GuardState.StoodDown, guard.State);
    }

    // --- LW-83: mismatch readings carry observed-vs-expected detail, and a drill-forced
    // stand-down self-identifies by naming the LW_FORCE_FINGERPRINT_MISMATCH flag. ---

    [Fact]
    public void Unknown_stamp_line_and_warn_carry_observed_and_expected()
    {
        WithWritesRestored(() =>
        {
            using var cap = LogCapture.Start();
            var mem = HealthyMemory();
            SeedU32Bytes(mem, ModuleBase + ELfanew + TimeDateStampOff, 0x11111111);
            var records = new List<(string type, string payload)>();
            var guard = new LaunchGuard(mem, forceMismatch: false, recorder: (t, p) => records.Add((t, p)));

            for (int i = 0; i < 40; i++) guard.Step();

            Assert.Equal(GuardState.Armed, guard.State);
            // The probe runs before the edge; the edge records armed, then the unknown-build line.
            Assert.Equal(3, records.Count);
            Assert.Contains("build stamp unknown", records[0].payload);
            Assert.Contains("0x11111111", records[0].payload);
            Assert.Contains("0x6A5E4A1B", records[0].payload);
            Assert.Contains("armed (all landmarks match", records[1].payload);
            Assert.Contains("armed on an unknown build", records[2].payload);
            Assert.Contains("0x11111111", records[2].payload);
            string warn = Assert.Single(cap.File, l => l.Contains("new to the mod"));
            Assert.Contains("[WARN]", warn);
            Assert.Contains("0x11111111", warn);
            Assert.Contains("0x6A5E4A1B", warn);
            // No drill: the stamp line must not mention the flag.
            string stampLine = Assert.Single(cap.File, l => l.Contains("Game build stamp"));
            Assert.DoesNotContain("LW_FORCE_FINGERPRINT_MISMATCH", stampLine);
        });
    }

    [Fact]
    public void Drill_unknown_build_names_the_flag_in_record_and_log()
    {
        WithWritesRestored(() =>
        {
            using var cap = LogCapture.Start();
            var records = new List<(string type, string payload)>();
            var guard = new LaunchGuard(HealthyMemory(), forceMismatch: true, recorder: (t, p) => records.Add((t, p)));

            for (int i = 0; i < 40; i++) guard.Step();

            Assert.Equal(GuardState.Armed, guard.State);
            var unknownBuild = Assert.Single(records, r => r.payload.Contains("armed on an unknown build"));
            Assert.Contains("LW_FORCE_FINGERPRINT_MISMATCH", unknownBuild.payload);
            // Only the armed-edge record names the flag; the probe's stamp record does not.
            var stamp = Assert.Single(records, r => r.payload.Contains("build stamp unknown"));
            Assert.DoesNotContain("LW_FORCE_FINGERPRINT_MISMATCH", stamp.payload);
            Assert.Single(cap.File, l => l.Contains("[WARN]") && l.Contains("LW_FORCE_FINGERPRINT_MISMATCH"));
            // The numbers on both sides are identical under the drill, so the stamp line must say why.
            string stampLine = Assert.Single(cap.File, l => l.Contains("Game build stamp"));
            Assert.Contains("LW_FORCE_FINGERPRINT_MISMATCH", stampLine);
        });
    }

    [Fact]
    public void Real_standdown_does_not_name_the_flag()
    {
        var mem = HealthyMemory(nameId: 5);   // mismatch
        var records = new List<(string type, string payload)>();
        var guard = new LaunchGuard(mem, forceMismatch: false, recorder: (t, p) => records.Add((t, p)));

        for (int i = 0; i < 40; i++) guard.Step();

        Assert.Equal(GuardState.StoodDown, guard.State);
        Assert.Single(records);
        Assert.DoesNotContain("LW_FORCE_FINGERPRINT_MISMATCH", records[0].payload);
    }

    [Fact]
    public void RosterRow_mismatch_detail_names_observed_fields()
    {
        var mem = HealthyMemory(nameId: 5);
        var records = new List<(string type, string payload)>();
        var guard = new LaunchGuard(mem, forceMismatch: false, recorder: (t, p) => records.Add((t, p)));

        for (int i = 0; i < 40; i++) guard.Step();

        Assert.Equal(GuardState.StoodDown, guard.State);
        Assert.Single(records);
        Assert.Contains("nameId=5", records[0].payload);
    }

    [Fact]
    public void KitLane_mismatch_detail_carries_observed_bytes()
    {
        // LW-112: was JobCommand_mismatch_detail_carries_observed_bytes, asserting the OLD bug (a
        // JobCommand conflict standing down the whole mod). Rewritten: the same conflict now stands
        // down only the lane, and the detail lands in the LANE's own stand-down record.
        bool prevWrites = Mem.WritesEnabled;
        try
        {
            Mem.WritesEnabled = false;
            var mem = HealthyMemory();
            long rec8 = Barrage.AbilityBase + 8L * Barrage.RecSize;
            mem.TerrainBlocks[rec8] = new byte[] { 0xAA, 0xAA, 0xAA, 0xAA, 0xAA, 0xAA, 0xAA, 0xAA };
            var records = new List<(string type, string payload)>();
            var guard = new LaunchGuard(mem, forceMismatch: false, recorder: (t, p) => records.Add((t, p)));

            guard.Step();
            Assert.Equal(GuardState.Armed, guard.State);
            for (int i = 0; i < 30; i++) guard.StepKitLane();

            Assert.Equal(GuardState.StoodDown, guard.KitLaneState);
            var laneRecord = Assert.Single(records, r => r.payload.Contains("kit-lane"));
            Assert.Contains("rec8", laneRecord.payload);
            Assert.Contains("AA-AA", laneRecord.payload);
        }
        finally { Mem.WritesEnabled = prevWrites; }
    }

    [Fact]
    public void KitLane_both_recs_mismatch_composes_both_details()
    {
        // LW-112: was JobCommand_both_recs_mismatch_composes_both_details. Both signature windows
        // wrong at once (non-zero bytes: an all-zero window is its own distinct "blanked" case, see
        // KitLane_all_zero_window_is_a_blanked_row_not_a_boot_window), so the composed detail must
        // name BOTH recs, each with its own observed bytes -- now inside the LANE's stand-down.
        bool prevWrites = Mem.WritesEnabled;
        try
        {
            Mem.WritesEnabled = false;
            var mem = HealthyMemory();
            long rec8 = Barrage.AbilityBase + 8L * Barrage.RecSize;
            long rec9 = Barrage.AbilityBase + 9L * Barrage.RecSize;
            mem.TerrainBlocks[rec8] = new byte[] { 0xAA, 0xAA, 0xAA, 0xAA, 0xAA, 0xAA, 0xAA, 0xAA };
            mem.TerrainBlocks[rec9] = new byte[] { 0xBB, 0xBB, 0xBB, 0xBB, 0xBB, 0xBB, 0xBB, 0xBB };
            var records = new List<(string type, string payload)>();
            var guard = new LaunchGuard(mem, forceMismatch: false, recorder: (t, p) => records.Add((t, p)));

            guard.Step();
            Assert.Equal(GuardState.Armed, guard.State);
            for (int i = 0; i < 30; i++) guard.StepKitLane();

            Assert.Equal(GuardState.StoodDown, guard.KitLaneState);
            var laneRecord = Assert.Single(records, r => r.payload.Contains("kit-lane"));
            Assert.Contains("rec8", laneRecord.payload);
            Assert.Contains("rec9", laneRecord.payload);
            Assert.Contains("AA-AA", laneRecord.payload);
            Assert.Contains("BB-BB", laneRecord.payload);
        }
        finally { Mem.WritesEnabled = prevWrites; }
    }

    // --- LW-112: the JobCommand landmark split off the main guard into its own second
    // FingerprintGuard instance (the kit-lane guard), so a custom job/ability mod rewriting that
    // table only ever disables the three weapon-granted commands (Barrage, Shadow Blade, Provoke),
    // never the whole mod. The lane only ever steps once the main guard has Armed (StepKitLane's
    // gate, LaunchGuard.KitLane.cs) -- that ordering is what the tests below pin. ---

    [Fact]
    public void KitLane_conflict_arms_the_mod_and_stands_down_only_the_lane()
    {
        bool prevWrites = Mem.WritesEnabled;
        try
        {
            Mem.WritesEnabled = false;
            var mem = HealthyMemory();
            long rec8 = Barrage.AbilityBase + 8L * Barrage.RecSize;
            mem.TerrainBlocks[rec8] = new byte[] { 0xAA, 0xAA, 0xAA, 0xAA, 0xAA, 0xAA, 0xAA, 0xAA };   // foreign, non-zero
            var calls = new List<(string title, string text)>();
            var records = new List<(string type, string payload)>();
            var triggers = new List<string>();
            var console = new List<string>();
            var file = new List<string>();
            var prior = ModLogger.Instance;
            ModLogger.Instance = new FileConsoleLogger(console.Add, file.Add);
            try
            {
                var guard = new LaunchGuard(mem, forceMismatch: false,
                    notice: (title, text) => calls.Add((title, text)),
                    recorder: (t, p) => records.Add((t, p)),
                    requestFlush: t => triggers.Add(t));

                guard.Step();
                Assert.Equal(GuardState.Armed, guard.State);

                for (int i = 0; i < 30; i++) guard.StepKitLane();

                // Pins that AnchorScout's StoodDown condition (Engine.cs) can never see a lane verdict:
                // the main guard's own State stays Armed no matter what the lane does.
                Assert.Equal(GuardState.Armed, guard.State);
                Assert.True(guard.Armed);
                Assert.True(Mem.WritesEnabled);
                Assert.False(guard.KitLaneArmed);
                Assert.Equal(GuardState.StoodDown, guard.KitLaneState);

                Assert.Empty(calls);   // the parts board boxes now, never the guard

                Assert.Contains(records, r => r.type == "guard" && r.payload.Contains("kit-lane"));
                Assert.Single(triggers);
                Assert.Equal("standdown", triggers[0]);

                // Review follow-up: pin the lane stand-down log line itself, not just the notice/
                // record payloads above. WARN tier (never Error -- the mod stays armed), and it
                // names the "jobcommand-table" landmark (KitLaneStandDown's diag string folds in
                // the landmark name via FingerprintGuard.Step), so a log-scanning tool or a human
                // triaging livingweapon.log can find this exact event.
                Assert.Contains(file, line => line.Contains("[WARN]") && line.Contains("jobcommand-table"));
            }
            finally { ModLogger.Instance = prior; }
        }
        finally { Mem.WritesEnabled = prevWrites; }
    }

    [Fact]
    public void Unknown_stamp_and_lane_conflict_arm_the_mod_and_stand_down_only_the_lane()
    {
        // LW-397: an unknown stamp no longer stands the mod down, so the lane now steps and speaks
        // on its own; its copy carries the hedge that the game update itself may be the cause.
        WithWritesRestored(() =>
        {
            var mem = HealthyMemory();
            SeedU32Bytes(mem, ModuleBase + ELfanew + TimeDateStampOff, 0x11111111);
            long rec8 = Barrage.AbilityBase + 8L * Barrage.RecSize;
            mem.TerrainBlocks[rec8] = new byte[] { 0xAA, 0xAA, 0xAA, 0xAA, 0xAA, 0xAA, 0xAA, 0xAA };   // simultaneous lane conflict
            var calls = new List<(string title, string text)>();
            using var cap = LogCapture.Start();
            var guard = new LaunchGuard(mem, forceMismatch: false, notice: (title, text) => calls.Add((title, text)));

            for (int i = 0; i < 40; i++) { guard.Step(); guard.StepKitLane(); }

            Assert.Equal(GuardState.Armed, guard.State);
            Assert.Single(cap.File, l => l.Contains("[WARN]") && l.Contains("its own checks passed"));
            Assert.Equal(GuardState.StoodDown, guard.KitLaneState);

            Assert.Empty(calls);
            Assert.Equal(LaunchGuard.UnknownBuildHedge, guard.BuildHedge);
            string laneWarn = Assert.Single(cap.File, l => l.Contains("[WARN]") && l.Contains("jobcommand-table"));
            Assert.Contains(PartsCopy.Hedge, laneWarn);
            Assert.Contains(LaunchGuard.UnknownBuildHedge, laneWarn);
            Assert.True(laneWarn.IndexOf("0x11111111", System.StringComparison.Ordinal) > laneWarn.IndexOf("(detail:", System.StringComparison.Ordinal));
            Assert.DoesNotContain(cap.File, l => l.Contains("standing down to protect your save"));
        });
    }

    [Fact]
    public void Unknown_stamp_never_stands_down()
    {
        // THE LOAD-BEARING TEST (LW-397): both header fields wrong, healthy roster, far past the
        // 30-step mismatch debounce. The stamp must never switch the mod off.
        WithWritesRestored(() =>
        {
            using var cap = LogCapture.Start();
            var mem = HealthyMemory();
            SeedU32Bytes(mem, ModuleBase + ELfanew + TimeDateStampOff, 0x11111111);
            SeedU32Bytes(mem, ModuleBase + ELfanew + SizeOfImageOff, 0x22222222);
            var calls = new List<(string title, string text)>();
            var guard = new LaunchGuard(mem, forceMismatch: false, notice: (t, x) => calls.Add((t, x)));

            for (int i = 0; i < 200; i++) guard.Step();

            Assert.Equal(GuardState.Armed, guard.State);
            Assert.True(Mem.WritesEnabled);
            Assert.Empty(calls);
            Assert.Single(cap.File, l => l.Contains("new to the mod"));
            Assert.DoesNotContain(cap.File, l => l.Contains("standing down to protect your save"));
        });
    }

    [Fact]
    public void Known_stamp_logs_one_line_and_no_warn()
    {
        WithWritesRestored(() =>
        {
            using var cap = LogCapture.Start();
            var records = new List<(string type, string payload)>();
            var guard = new LaunchGuard(HealthyMemory(), forceMismatch: false, recorder: (t, p) => records.Add((t, p)));

            for (int i = 0; i < 40; i++) guard.Step();

            Assert.Equal(GuardState.Armed, guard.State);
            Assert.True(guard.BuildStampKnown);
            string line = Assert.Single(cap.File, l => l.Contains("Game build stamp"));
            Assert.Contains("0x6A5E4A1B", line);
            Assert.Contains("made for", line);
            Assert.DoesNotContain(cap.File, l => l.Contains("new to the mod"));
            Assert.Single(records);   // only the armed record: a known stamp records nothing
        });
    }

    [Fact]
    public void Stamp_line_logs_once_before_the_armed_edge()
    {
        WithWritesRestored(() =>
        {
            using var cap = LogCapture.Start();
            var mem = HealthyMemory(level: 0);   // roster slot empty: the guard stays Verifying
            SeedU32Bytes(mem, ModuleBase + ELfanew + TimeDateStampOff, 0x11111111);
            var guard = new LaunchGuard(mem, forceMismatch: false);

            for (int i = 0; i < 40; i++) guard.Step();

            Assert.Equal(GuardState.Verifying, guard.State);
            Assert.Single(cap.File, l => l.Contains("Game build stamp"));
            Assert.DoesNotContain(cap.File, l => l.Contains("new to the mod"));

            StageRamzaRow(mem, 12, 1, 0x02, 70, 65);
            guard.Step();

            Assert.Equal(GuardState.Armed, guard.State);
            Assert.Single(cap.File, l => l.Contains("Game build stamp"));
            Assert.Single(cap.File, l => l.Contains("[WARN]") && l.Contains("new to the mod"));
        });
    }

    [Fact]
    public void Lane_copy_carries_no_stamp_detail_on_a_known_build()
    {
        WithWritesRestored(() =>
        {
            using var cap = LogCapture.Start();
            var mem = HealthyMemory();
            long rec8 = Barrage.AbilityBase + 8L * Barrage.RecSize;
            mem.TerrainBlocks[rec8] = new byte[] { 0xAA, 0xAA, 0xAA, 0xAA, 0xAA, 0xAA, 0xAA, 0xAA };
            var calls = new List<(string title, string text)>();
            var guard = new LaunchGuard(mem, forceMismatch: false, notice: (t, x) => calls.Add((t, x)));

            for (int i = 0; i < 40; i++) { guard.Step(); guard.StepKitLane(); }

            Assert.Equal(GuardState.StoodDown, guard.KitLaneState);
            Assert.Empty(calls);
            Assert.Equal("", guard.BuildHedge);
            Assert.Equal("", guard.BuildDetail);
            string laneWarn = Assert.Single(cap.File, l => l.Contains("[WARN]") && l.Contains("jobcommand-table"));
            Assert.DoesNotContain("TimeDateStamp", laneWarn);
            Assert.DoesNotContain(LaunchGuard.UnknownBuildHedge, laneWarn);
        });
    }

    [Fact]
    public void Roster_mismatch_still_stands_the_whole_mod_down()
    {
        // Roster-row mismatch is a MAIN-guard landmark, unaffected by the LW-112 split: still a full
        // stand-down, JobCommand health irrelevant.
        var mem = HealthyMemory(nameId: 5);
        var guard = new LaunchGuard(mem, forceMismatch: false);

        for (int i = 0; i < 40; i++) guard.Step();

        Assert.Equal(GuardState.StoodDown, guard.State);
        Assert.False(guard.Armed);
    }

    [Fact]
    public void All_healthy_arms_both_guards()
    {
        bool prevWrites = Mem.WritesEnabled;
        try
        {
            Mem.WritesEnabled = false;
            var mem = HealthyMemory();
            var calls = new List<(string title, string text)>();
            var console = new List<string>();
            var file = new List<string>();
            var prior = ModLogger.Instance;
            ModLogger.Instance = new FileConsoleLogger(console.Add, file.Add);
            try
            {
                var guard = new LaunchGuard(mem, forceMismatch: false, notice: (title, text) => calls.Add((title, text)));

                guard.Step();
                Assert.Equal(GuardState.Armed, guard.State);

                guard.StepKitLane();

                Assert.Equal(GuardState.Armed, guard.KitLaneState);
                Assert.True(guard.KitLaneArmed);
                Assert.Empty(calls);
                Assert.Contains(file, line => line.Contains("weapon commands (Barrage, Shadow Blade and Provoke) have passed their check") && line.Contains("once the party data check passes too"));
            }
            finally { ModLogger.Instance = prior; }
        }
        finally { Mem.WritesEnabled = prevWrites; }
    }

    [Fact]
    public void KitLane_all_zero_window_is_a_blanked_row_not_a_boot_window()
    {
        // Pins the deliberate divergence from FingerprintGuard.ByteSignature's own all-zero rule
        // (LaunchGuard.KitLane.cs's DescribeKitLaneMismatch doc): post-arm, an all-zero window is a
        // blanked row, not a boot window, so it must stand the lane down (with the calm notice),
        // not leave it silently Verifying forever.
        bool prevWrites = Mem.WritesEnabled;
        try
        {
            Mem.WritesEnabled = false;
            var mem = HealthyMemory();
            long rec8 = Barrage.AbilityBase + 8L * Barrage.RecSize;
            mem.TerrainBlocks[rec8] = new byte[8];   // present, all-zero: a blanked row
            var calls = new List<(string title, string text)>();
            var records = new List<(string type, string payload)>();
            var guard = new LaunchGuard(mem, forceMismatch: false,
                notice: (title, text) => calls.Add((title, text)),
                recorder: (t, p) => records.Add((t, p)));

            guard.Step();
            Assert.Equal(GuardState.Armed, guard.State);

            for (int i = 0; i < 30; i++) guard.StepKitLane();

            Assert.Equal(GuardState.StoodDown, guard.KitLaneState);
            Assert.False(guard.KitLaneArmed);
            Assert.Empty(calls);
            Assert.Contains(records, r => r.type == "guard" && r.payload.Contains("kit-lane") && r.payload.Contains("all zero"));
        }
        finally { Mem.WritesEnabled = prevWrites; }
    }

    [Fact]
    public void KitLane_transient_mismatch_resets_the_streak()
    {
        bool prevWrites = Mem.WritesEnabled;
        try
        {
            Mem.WritesEnabled = false;
            var mem = HealthyMemory();
            long rec8 = Barrage.AbilityBase + 8L * Barrage.RecSize;
            var goodRec8 = (byte[])mem.TerrainBlocks[rec8].Clone();
            mem.TerrainBlocks[rec8] = new byte[] { 0xAA, 0xAA, 0xAA, 0xAA, 0xAA, 0xAA, 0xAA, 0xAA };

            var guard = new LaunchGuard(mem, forceMismatch: false);
            guard.Step();
            Assert.Equal(GuardState.Armed, guard.State);

            for (int i = 0; i < 20; i++) guard.StepKitLane();   // under the debounce threshold (30)
            Assert.Equal(GuardState.Verifying, guard.KitLaneState);

            mem.TerrainBlocks[rec8] = goodRec8;   // heals before the debounce trips
            guard.StepKitLane();

            Assert.Equal(GuardState.Armed, guard.KitLaneState);
            Assert.True(guard.KitLaneArmed);
        }
        finally { Mem.WritesEnabled = prevWrites; }
    }

    [Fact]
    public void Drill_forceMismatch_arms_and_the_lanes_step_normally()
    {
        WithWritesRestored(() =>
        {
            using var cap = LogCapture.Start();
            var calls = new List<(string title, string text)>();
            var guard = new LaunchGuard(HealthyMemory(), forceMismatch: true, notice: (t, x) => calls.Add((t, x)));

            for (int i = 0; i < 40; i++) { guard.Step(); guard.StepKitLane(); }

            Assert.Equal(GuardState.Armed, guard.State);
            Assert.Equal(GuardState.Armed, guard.KitLaneState);
            Assert.Empty(calls);
            Assert.Single(cap.File, l => l.Contains("[WARN]") && l.Contains("new to the mod"));
        });
    }

    [Fact]
    public void Lane_notice_never_contains_the_scanner_stand_down_phrase()
    {
        // tools/scan_logs.py:59 classifies the exact phrase "standing down to protect your save" as
        // a full stand-down. A lane stand-down must never speak it anywhere (notice or log line), or
        // a healthy session (weapon-granted commands aside) would misreport as dead.
        bool prevWrites = Mem.WritesEnabled;
        try
        {
            Mem.WritesEnabled = false;
            var mem = HealthyMemory();
            long rec8 = Barrage.AbilityBase + 8L * Barrage.RecSize;
            mem.TerrainBlocks[rec8] = new byte[] { 0xAA, 0xAA, 0xAA, 0xAA, 0xAA, 0xAA, 0xAA, 0xAA };
            var calls = new List<(string title, string text)>();
            var console = new List<string>();
            var file = new List<string>();
            var prior = ModLogger.Instance;
            ModLogger.Instance = new FileConsoleLogger(console.Add, file.Add);
            try
            {
                var guard = new LaunchGuard(mem, forceMismatch: false, notice: (title, text) => calls.Add((title, text)));

                guard.Step();
                for (int i = 0; i < 30; i++) guard.StepKitLane();

                Assert.Equal(GuardState.StoodDown, guard.KitLaneState);
                Assert.Empty(calls);
                var closed = PartsReportTests.AllOpen();
                closed[Door.KitLane] = guard.KitLaneDoor;
                Assert.DoesNotContain("standing down to protect your save", PartsReport.ComposeBox(closed, "", guard.BuildHedge, ""));
                Assert.DoesNotContain(file, line => line.Contains("standing down to protect your save"));
            }
            finally { ModLogger.Instance = prior; }
        }
        finally { Mem.WritesEnabled = prevWrites; }
    }

    [Fact]
    public void Armed_edge_log_line_pins_the_scanner_ARMED_MARK_phrase()
    {
        // tools/scan_logs.py's ARMED_MARK classifies the literal phrase "Living Weapons is armed"
        // as its evidence that writes were enabled this session. STANDDOWN_MARK already
        // has this dual-language pin (see the "standing down to protect your save" assertions
        // above, e.g. Lane_notice_never_contains_the_scanner_stand_down_phrase); this is
        // ARMED_MARK's twin (LW-148), so a wording edit to LaunchGuard.ArmedEdge's log line breaks
        // a test here instead of silently blinding the scanner to a healthy session.
        bool prevWrites = Mem.WritesEnabled;
        try
        {
            Mem.WritesEnabled = false;
            var mem = HealthyMemory();
            var file = new List<string>();
            var prior = ModLogger.Instance;
            ModLogger.Instance = new FileConsoleLogger(_ => { }, file.Add);
            try
            {
                var guard = new LaunchGuard(mem, forceMismatch: false);

                guard.Step();

                Assert.Equal(GuardState.Armed, guard.State);
                Assert.Contains(file, line => line.Contains("Living Weapons is armed"));
            }
            finally { ModLogger.Instance = prior; }
        }
        finally { Mem.WritesEnabled = prevWrites; }
    }

    // ---- LW-395: the sensing lane (LaunchGuard.Sensing.cs) -------------------------------------
    // The lane verifies the six battle sentinels once per launch, out of battle only. Staged through
    // TerrainBlocks because the probe reads them with TryReadBytes (a failed read is Unreadable).

    internal static void StageSentinels(FakeSparseMemory mem, uint slot0, uint slot9, byte mode,
        ushort eventId, byte pause, byte submenu)
    {
        SeedU32Bytes(mem, Offsets.Slot0, slot0);
        SeedU32Bytes(mem, Offsets.Slot9, slot9);
        mem.TerrainBlocks[Offsets.BattleMode] = new[] { mode };
        mem.TerrainBlocks[Offsets.EventId] = new[] { (byte)eventId, (byte)(eventId >> 8) };
        mem.TerrainBlocks[Offsets.PauseFlag] = new[] { pause };
        mem.TerrainBlocks[Offsets.SubmenuFlag] = new[] { submenu };
    }

    // A guard plus everything its recorder, flush and notice taps captured.
    private sealed class SensingRig
    {
        public readonly List<(string type, string payload)> Records = new();
        public readonly List<string> Flushes = new();
        public readonly List<(string title, string text)> Notices = new();
        public LaunchGuard Guard = null!;
    }

    private static SensingRig NewSensingRig(FakeSparseMemory mem, bool forceSensingMismatch = false)
    {
        var rig = new SensingRig();
        rig.Guard = new LaunchGuard(mem, forceMismatch: false,
            notice: (t, x) => rig.Notices.Add((t, x)),
            recorder: (t, p) => rig.Records.Add((t, p)),
            requestFlush: f => rig.Flushes.Add(f),
            forceSensingMismatch: forceSensingMismatch);
        return rig;
    }

    // Arming the main guard flips Mem.WritesEnabled; restore it so no test leaks the static.
    private static void WithWritesRestored(System.Action body)
    {
        bool prev = Mem.WritesEnabled;
        try { Mem.WritesEnabled = false; body(); }
        finally { Mem.WritesEnabled = prev; }
    }

    private static void StepSensingN(LaunchGuard g, int n) { for (int i = 0; i < n; i++) g.StepSensing(); }

    [Theory]
    [InlineData(0xFFFFFFFFu, 0xFFFFFFFFu, (byte)255, (ushort)0xFFFF, (byte)0, (byte)0)]   // idle: title screen through the pre-first-battle world
    [InlineData(0xFFFFFFFFu, 0xFFFFFFFFu, (byte)0, (ushort)2, (byte)0, (byte)1)]          // the Orbonne opener
    [InlineData(0x11u, 0xFFFFFFFFu, (byte)0, (ushort)0xFFFF, (byte)0, (byte)0)]           // post-battle small phase value
    [InlineData(0x10u, 0xFFFFFFFFu, (byte)0, (ushort)0xFFFF, (byte)0, (byte)0)]           // post-victory stuck marker
    public void Sensing_lane_arms_on_the_out_of_battle_tuples(uint slot0, uint slot9, byte mode, ushort ev, byte pause, byte sub)
    {
        WithWritesRestored(() =>
        {
            var mem = HealthyMemory();
            StageSentinels(mem, slot0, slot9, mode, ev, pause, sub);
            var rig = NewSensingRig(mem);

            rig.Guard.Step();
            Assert.Equal(GuardState.Armed, rig.Guard.State);
            Assert.False(rig.Guard.SensingArmed);
            rig.Guard.StepSensing();

            Assert.True(rig.Guard.SensingArmed);
            Assert.Equal(GuardState.Armed, rig.Guard.SensingState);
            Assert.Contains(rig.Records, r => r.type == "guard" && r.payload.StartsWith("sensing lane armed"));
            Assert.Empty(rig.Flushes);
        });
    }

    [Theory]
    [InlineData(0x10u, 0xFFFFFFFFu, (byte)3, (ushort)271, (byte)0, (byte)0)]    // a live battle
    [InlineData(0x10u, 0u, (byte)1, (ushort)0, (byte)0, (byte)0)]               // slot9 dropped mid-battle: still family (a)
    [InlineData(0xFFFFFFFFu, 0xFFFFFFFFu, (byte)2, (ushort)0xFFFF, (byte)0, (byte)0)]   // battle mode alone
    [InlineData(0x10u, 0xFFFFFFFFu, (byte)0, (ushort)271, (byte)0, (byte)0)]    // marker-only mid-battle dialogue frame (mode 0, real event id): InLiveBattle's excuse
    [InlineData(0x10u, 0xFFFFFFFFu, (byte)0, (ushort)0xFFFF, (byte)1, (byte)0)] // marker-only paused frame (mode 0, pause flag): InLiveBattle's excuse
    [InlineData(0x10u, 0u, (byte)0, (ushort)271, (byte)0, (byte)0)]             // dialogue frame with slot9 dropped: must wait, never reach family (b)
    public void Sensing_lane_waits_silently_while_a_battle_is_live(uint slot0, uint slot9, byte mode, ushort ev, byte pause, byte sub)
    {
        // A Continue can resume a mid-battle autosave, so the lane can come up with a battle live;
        // it must retry (never count toward the debounce, never stand down a healthy build).
        WithWritesRestored(() =>
        {
            var mem = HealthyMemory();
            StageSentinels(mem, slot0, slot9, mode, ev, pause, sub);
            var rig = NewSensingRig(mem);
            rig.Guard.Step();
            int before = rig.Records.Count;

            StepSensingN(rig.Guard, 35);

            Assert.Equal(GuardState.Verifying, rig.Guard.SensingState);
            Assert.False(rig.Guard.SensingArmed);
            Assert.Equal(before, rig.Records.Count);
            Assert.Empty(rig.Flushes);

            // and it still arms once the battle is over
            StageSentinels(mem, 0xFFFFFFFFu, 0xFFFFFFFFu, 0, 0xFFFF, 0, 0);
            rig.Guard.StepSensing();
            Assert.True(rig.Guard.SensingArmed);
        });
    }

    [Fact]
    public void Sensing_foreign_slot9_stands_the_lane_down_after_the_debounce_and_nothing_else()
    {
        WithWritesRestored(() =>
        {
            var mem = HealthyMemory();
            StageSentinels(mem, 0xFFFFFFFFu, 1u, 255, 0xFFFF, 0, 0);
            var rig = NewSensingRig(mem);
            rig.Guard.Step();

            StepSensingN(rig.Guard, LaunchGuard.SensingBootGraceSteps + 29);
            Assert.Equal(GuardState.Verifying, rig.Guard.SensingState);
            rig.Guard.StepSensing();   // the boot grace, then exactly MismatchDebounce (30) mismatching steps

            Assert.Equal(GuardState.StoodDown, rig.Guard.SensingState);
            Assert.False(rig.Guard.SensingArmed);
            var rec = Assert.Single(rig.Records, r => r.payload.Contains("sensing stand-down"));
            Assert.Equal("guard", rec.type);
            Assert.Contains("slot9=0x00000001", rec.payload);
            Assert.Equal("standdown", Assert.Single(rig.Flushes));
            Assert.Empty(rig.Notices);   // no OS box for this lane
            Assert.Equal(GuardState.Armed, rig.Guard.State);
            Assert.True(Mem.WritesEnabled);
        });
    }

    [Theory]
    [InlineData(0u, 0u, (byte)0, (ushort)0, (byte)0, (byte)0, true)]                              // all zero: the address moved
    [InlineData(0xFFFFFFFFu, 0xFFFFFFFFu, (byte)255, (ushort)0, (byte)0, (byte)0, true)]          // event 0 out of battle
    [InlineData(0xFFFFFFFFu, 0xFFFFFFFFu, (byte)255, (ushort)0x400, (byte)0, (byte)0, true)]      // event above the plausible ceiling
    [InlineData(0xFFFFFFFFu, 0xFFFFFFFFu, (byte)255, (ushort)401, (byte)0, (byte)0, false)]       // highest observed id: a Match
    [InlineData(0xFFFFFFFFu, 0xFFFFFFFFu, (byte)7, (ushort)0xFFFF, (byte)0, (byte)0, true)]       // mode outside both families
    [InlineData(0x100u, 0xFFFFFFFFu, (byte)0, (ushort)0xFFFF, (byte)0, (byte)0, true)]            // slot0 neither frozen nor small
    [InlineData(0xFFFFFFFFu, 0xFFFFFFFFu, (byte)0, (ushort)0xFFFF, (byte)2, (byte)0, true)]       // pause is not a flag
    [InlineData(0xFFFFFFFFu, 0xFFFFFFFFu, (byte)0, (ushort)0xFFFF, (byte)0, (byte)2, true)]       // submenu is not a flag
    public void Sensing_out_of_battle_rules_match_or_mismatch(uint slot0, uint slot9, byte mode, ushort ev, byte pause, byte sub, bool mismatch)
    {
        WithWritesRestored(() =>
        {
            var mem = HealthyMemory();
            StageSentinels(mem, slot0, slot9, mode, ev, pause, sub);
            var rig = NewSensingRig(mem);
            rig.Guard.Step();

            StepSensingN(rig.Guard, LaunchGuard.SensingBootGraceSteps + 30);

            if (mismatch)
            {
                Assert.Equal(GuardState.StoodDown, rig.Guard.SensingState);
                Assert.Contains(rig.Records, r => r.payload.Contains($"slot0=0x{slot0:X8}")
                    && r.payload.Contains($"slot9=0x{slot9:X8}") && r.payload.Contains($"event=0x{ev:X4}"));
            }
            else Assert.True(rig.Guard.SensingArmed);
        });
    }

    [Fact]
    public void Sensing_missing_sentinel_block_stays_verifying_silently()
    {
        WithWritesRestored(() =>
        {
            var mem = HealthyMemory();
            StageSentinels(mem, 0xFFFFFFFFu, 0xFFFFFFFFu, 255, 0xFFFF, 0, 0);
            mem.TerrainBlocks.Remove(Offsets.BattleMode);   // TryReadBytes fails outright
            var rig = NewSensingRig(mem);
            rig.Guard.Step();
            int before = rig.Records.Count;

            StepSensingN(rig.Guard, 35);

            Assert.Equal(GuardState.Verifying, rig.Guard.SensingState);
            Assert.Equal(before, rig.Records.Count);
            Assert.Empty(rig.Flushes);
        });
    }

    [Fact]
    public void Sensing_lane_arms_with_the_roster_row_empty()
    {
        // LW-393: the lane no longer waits for the roster door. The stamp latch is the only wait.
        WithWritesRestored(() =>
        {
            var mem = HealthyMemory(level: 0);   // unpopulated roster: the main guard keeps retrying
            StageSentinels(mem, 0xFFFFFFFFu, 0xFFFFFFFFu, 255, 0xFFFF, 0, 0);
            var rig = NewSensingRig(mem);
            rig.Guard.Step();   // latches the build stamp, cannot arm

            rig.Guard.StepSensing();

            Assert.Equal(GuardState.Verifying, rig.Guard.State);
            Assert.True(rig.Guard.SensingArmed);
            Assert.False(Mem.WritesEnabled);
        });
    }

    [Fact]
    public void Sensing_lane_waits_while_the_build_stamp_has_not_been_read()
    {
        WithWritesRestored(() =>
        {
            var mem = HealthyMemory();
            StageSentinels(mem, 0xFFFFFFFFu, 0xFFFFFFFFu, 255, 0xFFFF, 0, 0);
            var rig = NewSensingRig(mem);   // the main guard never steps, so the image was never read

            StepSensingN(rig.Guard, 400);

            Assert.Equal(GuardState.Verifying, rig.Guard.SensingState);
            Assert.Empty(rig.Records);
            Assert.Equal(DoorState.Waiting, rig.Guard.SensingDoor.State);

            rig.Guard.Step();
            rig.Guard.StepSensing();
            Assert.True(rig.Guard.SensingArmed);
        });
    }

    [Fact]
    public void Sensing_boot_grace_holds_a_mismatch_then_lets_it_count()
    {
        WithWritesRestored(() =>
        {
            using var cap = LogCapture.Start();
            var mem = HealthyMemory();
            StageSentinels(mem, 0xFFFFFFFFu, 1u, 255, 0xFFFF, 0, 0);   // foreign slot9
            var rig = NewSensingRig(mem);
            rig.Guard.Step();

            StepSensingN(rig.Guard, LaunchGuard.SensingBootGraceSteps + 29);   // 300 held, then 29 counted
            Assert.Equal(GuardState.Verifying, rig.Guard.SensingState);
            Assert.DoesNotContain(rig.Records, r => r.payload.Contains("sensing stand-down"));
            Assert.Single(cap.File, l => l.Contains("boot grace"));

            rig.Guard.StepSensing();

            Assert.Equal(GuardState.StoodDown, rig.Guard.SensingState);
        });
    }

    [Fact]
    public void Sensing_boot_grace_still_arms_a_matching_tuple_at_once()
    {
        WithWritesRestored(() =>
        {
            var mem = HealthyMemory();
            StageSentinels(mem, 0xFFFFFFFFu, 0xFFFFFFFFu, 255, 0xFFFF, 0, 0);
            var rig = NewSensingRig(mem);
            rig.Guard.Step();

            rig.Guard.StepSensing();

            Assert.True(rig.Guard.SensingArmed);
        });
    }

    [Fact]
    public void Sensing_stall_watcher_stays_silent_through_a_live_battle()
    {
        WithWritesRestored(() =>
        {
            using var cap = LogCapture.Start();
            var mem = HealthyMemory();
            StageSentinels(mem, 0x10u, 0xFFFFFFFFu, 3, 271, 0, 0);   // a live battle: Unreadable by design
            var rig = NewSensingRig(mem);
            rig.Guard.Step();

            StepSensingN(rig.Guard, LaunchGuard.DoorWaitSteps + 50);

            Assert.DoesNotContain(cap.File, l => l.Contains("has not been able to read its data"));
            Assert.Equal(GuardState.Verifying, rig.Guard.SensingState);
        });
    }

    [Fact]
    public void Sensing_stall_watcher_stays_silent_through_the_boot_grace()
    {
        WithWritesRestored(() =>
        {
            using var cap = LogCapture.Start();
            var mem = HealthyMemory();
            StageSentinels(mem, 0xFFFFFFFFu, 1u, 255, 0xFFFF, 0, 0);   // a mismatch held as unreadable
            var rig = NewSensingRig(mem);
            rig.Guard.Step();

            StepSensingN(rig.Guard, LaunchGuard.SensingBootGraceSteps);

            Assert.DoesNotContain(cap.File, l => l.Contains("has not been able to read its data"));
        });
    }

    [Fact]
    public void Sensing_genuinely_unreadable_block_logs_the_timed_line_once_and_never_boxes()
    {
        WithWritesRestored(() =>
        {
            using var cap = LogCapture.Start();
            var mem = HealthyMemory();
            StageSentinels(mem, 0xFFFFFFFFu, 0xFFFFFFFFu, 255, 0xFFFF, 0, 0);
            mem.TerrainBlocks.Remove(Offsets.BattleMode);
            var rig = NewSensingRig(mem);
            rig.Guard.Step();

            StepSensingN(rig.Guard, LaunchGuard.DoorWaitSteps - 1);
            Assert.DoesNotContain(cap.File, l => l.Contains("has not been able to read its data"));
            StepSensingN(rig.Guard, 1 + 200);

            string line = Assert.Single(cap.File, l => l.Contains("has not been able to read its data"));
            Assert.Contains("[INFO]", line);
            Assert.Contains("battle sentinel check", line);
            Assert.Contains("battle sensing", line);
            Assert.DoesNotContain("standing down to protect your save", line);
            Assert.Empty(rig.Notices);
        });
    }

    [Fact]
    public void KitLane_waits_for_a_loaded_save_and_then_judges()
    {
        WithWritesRestored(() =>
        {
            var mem = HealthyMemory(level: 0);
            mem.TerrainBlocks[Offsets.RosterBase + Offsets.RLevel] = new byte[] { 0 };   // mapped zero, the title screen
            var guard = new LaunchGuard(mem, forceMismatch: false);

            for (int i = 0; i < 200; i++) guard.StepKitLane();

            Assert.Equal(GuardState.Verifying, guard.KitLaneState);
            Assert.False(guard.KitLaneArmed);
            Assert.Equal(DoorState.Waiting, guard.KitLaneDoor.State);
            Assert.Equal(LaunchGuard.KitLaneWaitingReason, guard.KitLaneDoor.Reason);

            mem.U8s[Offsets.RosterBase + Offsets.RLevel] = 12;
            guard.StepKitLane();

            Assert.Equal(GuardState.Armed, guard.KitLaneState);
            Assert.True(guard.KitLaneArmed);
        });
    }

    [Fact]
    public void KitLane_title_wait_never_logs_the_stall_line()
    {
        WithWritesRestored(() =>
        {
            using var cap = LogCapture.Start();
            var mem = HealthyMemory(level: 0);
            mem.TerrainBlocks[Offsets.RosterBase + Offsets.RLevel] = new byte[] { 0 };
            var guard = new LaunchGuard(mem, forceMismatch: false);

            for (int i = 0; i < LaunchGuard.DoorWaitSteps + 300; i++) guard.StepKitLane();

            Assert.Equal(GuardState.Verifying, guard.KitLaneState);
            Assert.DoesNotContain(cap.File, l => l.Contains("has not been able to read its data"));
        });
    }

    [Fact]
    public void Title_first_roster_drill_keeps_the_kit_lane_independent()
    {
        WithWritesRestored(() =>
        {
            var mem = HealthyMemory(level: 0);
            mem.TerrainBlocks[Offsets.RosterBase + Offsets.RLevel] = new byte[] { 0 };
            var guard = new LaunchGuard(mem, forceMismatch: false, forceRosterMismatch: true);
            for (int i = 0; i < 40; i++) { guard.Step(); guard.StepKitLane(); }
            Assert.Equal(GuardState.Verifying, guard.KitLaneState);

            mem.U8s[Offsets.RosterBase + Offsets.RLevel] = 12;
            for (int i = 0; i < 40; i++) { guard.Step(); guard.StepKitLane(); }

            Assert.Equal(GuardState.StoodDown, guard.State);
            Assert.True(guard.KitLaneArmed);
        });
    }

    [Fact]
    public void KitLane_unreadable_for_the_door_window_logs_its_timed_line_once_and_stays_quiet_below_it()
    {
        WithWritesRestored(() =>
        {
            using var cap = LogCapture.Start();
            var mem = HealthyMemory();
            mem.TerrainBlocks.Remove(Barrage.AbilityBase + 8L * Barrage.RecSize);
            var calls = new List<(string title, string text)>();
            var guard = new LaunchGuard(mem, forceMismatch: false, notice: (t, x) => calls.Add((t, x)));

            for (int i = 0; i < LaunchGuard.DoorWaitSteps - 1; i++) guard.StepKitLane();
            Assert.DoesNotContain(cap.File, l => l.Contains("has not been able to read its data"));
            for (int i = 0; i < 300; i++) guard.StepKitLane();

            string line = Assert.Single(cap.File, l => l.Contains("has not been able to read its data"));
            Assert.Contains("job command check", line);
            Assert.Contains("weapon commands", line);
            Assert.DoesNotContain("standing down to protect your save", line);
            Assert.Empty(calls);
            Assert.Equal(DoorState.Waiting, guard.KitLaneDoor.State);
        });
    }

    // ---- LW-393: the roster drill and the door readings ----------------------------------------

    private const string RosterDrillFlag = "LW_FORCE_ROSTER_MISMATCH";

    [Fact]
    public void Roster_drill_stands_a_healthy_row_down_after_the_debounce_and_names_its_flag()
    {
        WithWritesRestored(() =>
        {
            using var cap = LogCapture.Start();
            var records = new List<(string type, string payload)>();
            var calls = new List<(string title, string text)>();
            var guard = new LaunchGuard(HealthyMemory(), forceMismatch: false, forceRosterMismatch: true,
                notice: (t, x) => calls.Add((t, x)), recorder: (t, p) => records.Add((t, p)));

            for (int i = 0; i < 29; i++) guard.Step();
            Assert.Equal(GuardState.Verifying, guard.State);
            guard.Step();

            Assert.Equal(GuardState.StoodDown, guard.State);
            Assert.False(Mem.WritesEnabled);
            var rec = Assert.Single(records, r => r.payload.Contains("stand-down"));
            Assert.Contains(RosterDrillFlag, rec.payload);
            string line = Assert.Single(cap.File, l => l.Contains("[ERROR]"));
            Assert.Contains(RosterDrillFlag, line);
            Assert.Contains("This stand down was forced by the LW_FORCE_ROSTER_MISMATCH drill flag (dev builds only).", line);
            Assert.Empty(calls);
            Assert.Contains(RosterDrillFlag, guard.RosterDoor.Reason);
        });
    }

    [Fact]
    public void Roster_drill_on_a_healthy_row_gives_the_bare_drill_detail()
    {
        WithWritesRestored(() =>
        {
            var records = new List<(string type, string payload)>();
            var guard = new LaunchGuard(HealthyMemory(), forceMismatch: false, forceRosterMismatch: true,
                recorder: (t, p) => records.Add((t, p)));

            for (int i = 0; i < 30; i++) guard.Step();

            var rec = Assert.Single(records, r => r.payload.Contains("stand-down"));
            Assert.Contains("(drill: " + RosterDrillFlag + ")", rec.payload);
            Assert.DoesNotContain("observed", rec.payload);
        });
    }

    [Fact]
    public void Roster_drill_on_a_wrong_row_keeps_the_observed_fields_and_adds_the_drill_tag()
    {
        WithWritesRestored(() =>
        {
            var records = new List<(string type, string payload)>();
            var guard = new LaunchGuard(HealthyMemory(nameId: 5), forceMismatch: false, forceRosterMismatch: true,
                recorder: (t, p) => records.Add((t, p)));

            for (int i = 0; i < 30; i++) guard.Step();

            var rec = Assert.Single(records, r => r.payload.Contains("stand-down"));
            Assert.Contains("observed nameId=5", rec.payload);
            Assert.Contains("; drill: " + RosterDrillFlag, rec.payload);
        });
    }

    [Fact]
    public void Roster_drill_waits_for_a_populated_row_like_a_real_mismatch_would()
    {
        WithWritesRestored(() =>
        {
            var guard = new LaunchGuard(HealthyMemory(level: 0), forceMismatch: false, forceRosterMismatch: true);

            for (int i = 0; i < 100; i++) guard.Step();

            Assert.Equal(GuardState.Verifying, guard.State);
        });
    }

    [Fact]
    public void Roster_drill_never_touches_the_other_lanes()
    {
        WithWritesRestored(() =>
        {
            var mem = HealthyMemory();
            StageSentinels(mem, 0xFFFFFFFFu, 0xFFFFFFFFu, 255, 0xFFFF, 0, 0);
            var guard = new LaunchGuard(mem, forceMismatch: false, forceRosterMismatch: true);
            for (int i = 0; i < 40; i++) { guard.Step(); guard.StepKitLane(); guard.StepSensing(); }

            Assert.Equal(GuardState.StoodDown, guard.State);
            Assert.True(guard.KitLaneArmed);
            Assert.True(guard.SensingArmed);
        });
    }

    [Fact]
    public void Real_roster_stand_down_names_no_drill_flag_anywhere()
    {
        WithWritesRestored(() =>
        {
            using var cap = LogCapture.Start();
            var records = new List<(string type, string payload)>();
            var guard = new LaunchGuard(HealthyMemory(nameId: 5), forceMismatch: false, recorder: (t, p) => records.Add((t, p)));

            for (int i = 0; i < 40; i++) guard.Step();

            Assert.Equal(GuardState.StoodDown, guard.State);
            Assert.DoesNotContain(cap.File, l => l.Contains("LW_FORCE_"));
            Assert.DoesNotContain(records, r => r.payload.Contains("LW_FORCE_"));
            Assert.DoesNotContain("LW_FORCE_", guard.RosterDoor.Reason);
        });
    }

    [Fact]
    public void Roster_door_reads_waiting_then_open_then_closed_with_stable_reasons()
    {
        WithWritesRestored(() =>
        {
            var mem = HealthyMemory(level: 0);
            mem.TerrainBlocks[Offsets.RosterBase + Offsets.RLevel] = new byte[] { 0 };   // mapped zero, the title screen
            var guard = new LaunchGuard(mem, forceMismatch: false);
            Assert.Equal(DoorState.Waiting, guard.RosterDoor.State);
            Assert.Equal("waiting for a save to load", guard.RosterDoor.Reason);

            guard.Step();
            Assert.Equal(DoorState.Waiting, guard.RosterDoor.State);
            Assert.Equal("waiting for a save to load", guard.RosterDoor.Reason);
            Assert.False(guard.RosterDoor.FailedCheck);

            StageRamzaRow(mem, 12, 1, 0x02, 70, 65);
            guard.Step();
            Assert.Equal(DoorState.Open, guard.RosterDoor.State);

            var bad = new LaunchGuard(HealthyMemory(nameId: 5), forceMismatch: false);
            for (int i = 0; i < 40; i++) bad.Step();
            Assert.Equal(DoorState.Closed, bad.RosterDoor.State);
            Assert.True(bad.RosterDoor.FailedCheck);
            Assert.Equal("the party data is not where this mod expects it", bad.RosterDoor.Reason);
        });
    }

    [Fact]
    public void Roster_door_reads_the_game_cannot_be_read_yet_when_the_page_is_unreadable()
    {
        WithWritesRestored(() =>
        {
            var guard = new LaunchGuard(HealthyMemory(level: 0), forceMismatch: false);   // level byte not mappable

            guard.Step();

            Assert.Equal(DoorState.Waiting, guard.RosterDoor.State);
            Assert.Equal("the game cannot be read yet", guard.RosterDoor.Reason);
        });
    }

    [Fact]
    public void Kit_lane_and_sensing_doors_read_per_state_and_only_the_kit_lane_closure_is_a_failed_check()
    {
        WithWritesRestored(() =>
        {
            var mem = HealthyMemory();
            StageSentinels(mem, 0xFFFFFFFFu, 1u, 255, 0xFFFF, 0, 0);   // foreign slot9
            long rec8 = Barrage.AbilityBase + 8L * Barrage.RecSize;
            mem.TerrainBlocks[rec8] = new byte[] { 0xAA, 0xAA, 0xAA, 0xAA, 0xAA, 0xAA, 0xAA, 0xAA };
            var guard = new LaunchGuard(mem, forceMismatch: false);

            Assert.Equal(DoorState.Waiting, guard.KitLaneDoor.State);
            Assert.Equal("the job command check has not run yet", guard.KitLaneDoor.Reason);
            Assert.Equal(DoorState.Waiting, guard.SensingDoor.State);
            Assert.Equal("the battle sentinel check has not run yet", guard.SensingDoor.Reason);

            guard.Step();
            for (int i = 0; i < 30; i++) guard.StepKitLane();
            StepSensingN(guard, LaunchGuard.SensingBootGraceSteps + 30);

            Assert.Equal(DoorState.Closed, guard.KitLaneDoor.State);
            Assert.True(guard.KitLaneDoor.FailedCheck);
            Assert.Equal("another mod rewrote the job command data", guard.KitLaneDoor.Reason);
            Assert.Equal(DoorState.Closed, guard.SensingDoor.State);
            Assert.False(guard.SensingDoor.FailedCheck);
            Assert.Equal("the battle sentinels read wrong, so only the new game detector is off", guard.SensingDoor.Reason);
        });
    }

    [Fact]
    public void Kit_lane_sensing_and_twin_doors_read_open_on_healthy_memory()
    {
        WithWritesRestored(() =>
        {
            var mem = HealthyMemory();
            StageSentinels(mem, 0xFFFFFFFFu, 0xFFFFFFFFu, 255, 0xFFFF, 0, 0);
            var guard = new LaunchGuard(mem, forceMismatch: false);

            guard.Step();
            guard.StepKitLane();
            guard.StepSensing();
            guard.StepTwin();

            Assert.Equal(DoorState.Open, guard.RosterDoor.State);
            Assert.Equal(DoorState.Open, guard.KitLaneDoor.State);
            Assert.Equal(DoorState.Open, guard.SensingDoor.State);
            Assert.Equal(DoorState.Open, guard.TwinPistolsDoor.State);
        });
    }

    [Fact]
    public void Sensing_transient_mismatch_heals()
    {
        WithWritesRestored(() =>
        {
            var mem = HealthyMemory();
            StageSentinels(mem, 0xFFFFFFFFu, 1u, 255, 0xFFFF, 0, 0);
            var rig = NewSensingRig(mem);
            rig.Guard.Step();

            StepSensingN(rig.Guard, 10);
            Assert.Equal(GuardState.Verifying, rig.Guard.SensingState);
            StageSentinels(mem, 0xFFFFFFFFu, 0xFFFFFFFFu, 255, 0xFFFF, 0, 0);
            rig.Guard.StepSensing();

            Assert.True(rig.Guard.SensingArmed);
        });
    }

    [Fact]
    public void Sensing_drill_forces_a_mismatch_on_healthy_memory_and_names_only_its_own_flag()
    {
        WithWritesRestored(() =>
        {
            using var cap = LogCapture.Start();
            var mem = HealthyMemory();
            StageSentinels(mem, 0xFFFFFFFFu, 0xFFFFFFFFu, 255, 0xFFFF, 0, 0);
            var rig = NewSensingRig(mem, forceSensingMismatch: true);
            rig.Guard.Step();

            StepSensingN(rig.Guard, LaunchGuard.SensingBootGraceSteps + 30);

            Assert.Equal(GuardState.StoodDown, rig.Guard.SensingState);
            Assert.Contains(rig.Records, r => r.payload.Contains("LW_FORCE_SENSING_MISMATCH"));
            Assert.DoesNotContain(rig.Records, r => r.payload.Contains("LW_FORCE_FINGERPRINT_MISMATCH"));
            Assert.Contains(cap.File, l => l.Contains("LW_FORCE_SENSING_MISMATCH"));
            Assert.DoesNotContain(cap.File, l => l.Contains("LW_FORCE_FINGERPRINT_MISMATCH"));
        });
    }

    [Fact]
    public void Sensing_real_stand_down_names_neither_drill_flag_and_never_speaks_the_scanner_phrase()
    {
        // tools/scan_logs.py STANDDOWN_MARK classifies "standing down to protect your save" as a
        // full stand-down; this lane leaves the mod running, so it must never say it.
        WithWritesRestored(() =>
        {
            using var cap = LogCapture.Start();
            var mem = HealthyMemory();
            StageSentinels(mem, 0xFFFFFFFFu, 1u, 255, 0xFFFF, 0, 0);
            var rig = NewSensingRig(mem);
            rig.Guard.Step();

            StepSensingN(rig.Guard, LaunchGuard.SensingBootGraceSteps + 30);

            Assert.Equal(GuardState.StoodDown, rig.Guard.SensingState);
            Assert.Contains(cap.File, l => l.Contains("battle status data does not read the way this mod expects"));
            Assert.DoesNotContain(cap.File, l => l.Contains("standing down to protect your save"));
            Assert.DoesNotContain(cap.File, l => l.Contains("LW_FORCE_"));
            Assert.DoesNotContain(rig.Records, r => r.payload.Contains("LW_FORCE_"));
        });
    }

    [Fact]
    public void Sensing_stand_down_line_is_warn_tier_not_error()
    {
        WithWritesRestored(() =>
        {
            using var cap = LogCapture.Start();
            var mem = HealthyMemory();
            StageSentinels(mem, 0xFFFFFFFFu, 1u, 255, 0xFFFF, 0, 0);
            var rig = NewSensingRig(mem);
            rig.Guard.Step();

            StepSensingN(rig.Guard, LaunchGuard.SensingBootGraceSteps + 30);

            Assert.Equal(GuardState.StoodDown, rig.Guard.SensingState);
            string line = Assert.Single(cap.File, l => l.Contains("battle status data does not read the way this mod expects"));
            Assert.Contains("[WARN]", line);
            Assert.DoesNotContain("[ERROR]", line);
        });
    }

    // --- LW-392: the mod says so when it cannot find the game ---

    private sealed class StallRig
    {
        public LaunchGuard Guard = null!;
        public readonly List<(string title, string text)> Notices = new();
        public readonly List<(string type, string payload)> Records = new();
    }

    private static StallRig NewStallRig(FakeSparseMemory mem, bool forceUnreadable = false, string? version = null)
    {
        var rig = new StallRig();
        rig.Guard = new LaunchGuard(mem, forceMismatch: false,
            notice: (t, x) => rig.Notices.Add((t, x)),
            recorder: (t, p) => rig.Records.Add((t, p)),
            forceUnreadable: forceUnreadable, version: version);
        return rig;
    }

    private static void StepN(LaunchGuard g, int n) { for (int i = 0; i < n; i++) g.Step(); }

    private const string BoxHead = "Living Weapons cannot find the game yet";

    [Fact]
    // NON-VACUITY LEVER (spec v2, verified 2026-10-16): in LandmarkStall.Watch change `streak == stallSteps`
    // to `streak == stallSteps + 100`; nothing fires inside this test's +50 margin and Assert.Single(rig.Notices)
    // fails with an empty collection. The repeat levers above cannot work alone: the once-only property is
    // deduped at two layers (the watcher fires once per streak AND _stallWarned), by design.
    public void Image_unreadable_for_the_stall_window_warns_once_and_boxes_once()
    {
        // LOAD-BEARING (LW-392). Two dedupe layers guard the box: the decorator fires once per
        // streak (LandmarkStall `streak == stallSteps`) and the adapter latches (_stallWarned).
        // Levers that turn this red: (a) change `==` to `>=` in LandmarkStall.Watch AND delete the
        // `if (_stallWarned) return;` line in OnImageStalled -> 51 notices, Assert.Single fails;
        // (b) swap the PE landmark's OnImageStalled handler for a no-op -> no notice, Assert.Single fails.
        WithWritesRestored(() =>
        {
            using var cap = LogCapture.Start();
            var rig = NewStallRig(new FakeSparseMemory());   // no PE blocks, roster level 0: nothing readable

            StepN(rig.Guard, LaunchGuard.ImageStallSteps + 50);

            var notice = Assert.Single(rig.Notices);
            Assert.StartsWith(BoxHead, notice.text);
            string line = Assert.Single(cap.File, l => l.Contains("has not been able to read the game"));
            Assert.Contains("[WARN]", line);
            Assert.Equal(GuardState.Verifying, rig.Guard.State);
            Assert.Single(rig.Records, r => r.type == "guard" && r.payload.Contains("stalled"));
        });
    }

    [Fact]
    public void Image_unreadable_below_the_window_stays_silent()
    {
        WithWritesRestored(() =>
        {
            using var cap = LogCapture.Start();
            var rig = NewStallRig(new FakeSparseMemory());

            StepN(rig.Guard, LaunchGuard.ImageStallSteps - 1);

            Assert.Empty(rig.Notices);
            Assert.DoesNotContain(cap.File, l => l.Contains("[WARN]"));
            Assert.Empty(rig.Records);
        });
    }

    [Fact]
    public void Title_screen_wait_logs_the_calm_line_once_and_never_boxes()
    {
        WithWritesRestored(() =>
        {
            using var cap = LogCapture.Start();
            var mem = HealthyMemory(level: 0);
            mem.TerrainBlocks[Offsets.RosterBase + Offsets.RLevel] = new byte[] { 0 };   // mapped, zero: the title screen
            var rig = NewStallRig(mem);

            StepN(rig.Guard, LaunchGuard.RosterWaitSteps + 50);

            Assert.Empty(rig.Notices);
            string line = Assert.Single(cap.File, l => l.Contains("waiting for a save to load"));
            Assert.Contains("[INFO]", line);
            Assert.DoesNotContain(cap.File, l => l.Contains("[WARN]"));
            Assert.Equal(GuardState.Verifying, rig.Guard.State);
        });
    }

    [Fact]
    public void Roster_page_unreadable_reads_as_cannot_find_the_game()
    {
        WithWritesRestored(() =>
        {
            using var cap = LogCapture.Start();
            var rig = NewStallRig(HealthyMemory(level: 0));   // PE fine; roster level byte is NOT mappable

            StepN(rig.Guard, LaunchGuard.RosterWaitSteps + 1);

            var notice = Assert.Single(rig.Notices);
            Assert.StartsWith(BoxHead, notice.text);
            Assert.Single(cap.File, l => l.Contains("has not been able to read the game"));
            Assert.DoesNotContain(cap.File, l => l.Contains("waiting for a save to load"));
        });
    }

    [Fact]
    public void Arming_after_a_stall_logs_the_recovery_line()
    {
        WithWritesRestored(() =>
        {
            using var cap = LogCapture.Start();
            var mem = new FakeSparseMemory();
            var rig = NewStallRig(mem);
            StepN(rig.Guard, LaunchGuard.ImageStallSteps + 1);
            Assert.Single(rig.Notices);

            StagePeHealthy(mem);
            StageRamzaRow(mem, 12, 1, 0x02, 70, 65);
            rig.Guard.Step();

            Assert.Equal(GuardState.Armed, rig.Guard.State);
            Assert.Single(cap.File, l => l.Contains("found the game after the earlier warning"));
            Assert.Contains(rig.Records, r => r.type == "guard" && r.payload.Contains("armed after the stall warning"));
        });
    }

    [Fact]
    public void Arming_without_a_stall_logs_no_recovery_line()
    {
        WithWritesRestored(() =>
        {
            using var cap = LogCapture.Start();
            var rig = NewStallRig(HealthyMemory());

            rig.Guard.Step();

            Assert.Equal(GuardState.Armed, rig.Guard.State);
            Assert.DoesNotContain(cap.File, l => l.Contains("found the game after the earlier warning"));
            Assert.DoesNotContain(rig.Records, r => r.payload.Contains("armed after the stall warning"));
        });
    }

    [Fact]
    public void Unreadable_drill_forces_the_warning_then_releases()
    {
        WithWritesRestored(() =>
        {
            using var cap = LogCapture.Start();
            var rig = NewStallRig(HealthyMemory(), forceUnreadable: true);

            StepN(rig.Guard, LaunchGuard.ImageStallSteps + 1);
            var notice = Assert.Single(rig.Notices);
            Assert.StartsWith(BoxHead, notice.text);
            Assert.Contains("LW_FORCE_GUARD_UNREADABLE", notice.text);
            Assert.Contains(cap.File, l => l.Contains("[WARN]") && l.Contains("LW_FORCE_GUARD_UNREADABLE"));
            Assert.Equal(GuardState.Verifying, rig.Guard.State);

            StepN(rig.Guard, LaunchGuard.DrillBlindSteps - (LaunchGuard.ImageStallSteps + 1));   // DrillBlindSteps total
            Assert.Equal(GuardState.Verifying, rig.Guard.State);
            rig.Guard.Step();

            Assert.Equal(GuardState.Armed, rig.Guard.State);
            Assert.Single(rig.Notices);
            Assert.Single(cap.File, l => l.Contains("found the game after the earlier warning"));
        });
    }

    [Fact]
    public void Real_stall_does_not_name_the_drill_flag()
    {
        WithWritesRestored(() =>
        {
            using var cap = LogCapture.Start();
            var rig = NewStallRig(new FakeSparseMemory());

            StepN(rig.Guard, LaunchGuard.ImageStallSteps + 1);

            Assert.DoesNotContain("LW_FORCE_", Assert.Single(rig.Notices).text);
            Assert.DoesNotContain(cap.File, l => l.Contains("LW_FORCE_"));
        });
    }

    [Fact]
    public void Stall_texts_never_speak_the_scanner_stand_down_phrase()
    {
        // tools/scan_logs.py STANDDOWN_MARK reads "standing down to protect your save" as a dead mod.
        foreach (var text in new[] { LaunchGuard.ImageStallWarn, LaunchGuard.ImageStallBox,
                                     LaunchGuard.RosterWaitInfo, LaunchGuard.RecoveryInfo,
                                     LaunchGuard.UnknownBuildWarn(0x11111111, 0x22222222), LaunchGuard.UnknownBuildHedge,
                                     LaunchGuard.DrillStampSentence,
                                     LaunchGuard.KnownStampLine(0x6A5E4A1B, 0x03F9E000),
                                     LaunchGuard.UnknownStampLine(0x11111111, 0x22222222) })
        {
            Assert.DoesNotContain("standing down to protect your save", text);
            Assert.DoesNotContain("switched itself off", text);
        }
    }

    // The twin weapon door (LW-400).

    private const string TwinDrillFlag = "LW_FORCE_TWIN_MISMATCH";
    private const string ScannerPhrase = "standing down to protect your save";

    private static FakeSparseMemory EmptySlotMemory()
    {
        var mem = HealthyMemory(level: 0);
        mem.TerrainBlocks[Offsets.RosterBase + Offsets.RLevel] = new byte[] { 0 };   // mapped zero, the title screen
        return mem;
    }

    [Fact]
    public void Twin_door_waits_while_the_roster_row_is_empty()
    {
        using var cap = LogCapture.Start();
        var guard = new LaunchGuard(EmptySlotMemory(), forceMismatch: false);

        StepTwinN(guard, 400);

        Assert.Equal(GuardState.Verifying, guard.TwinState);
        Assert.Equal(DoorState.Waiting, guard.TwinPistolsDoor.State);
        Assert.Equal("waiting for a save to load", guard.TwinPistolsDoor.Reason);
        Assert.DoesNotContain(cap.File, l => l.Contains("menu flag check"));
    }

    [Fact]
    public void Twin_door_arms_on_a_healthy_save_with_flags_zero_or_one()
    {
        using var cap = LogCapture.Start();
        var records = new List<(string type, string payload)>();
        var guard = new LaunchGuard(HealthyMemory(), forceMismatch: false, recorder: (t, p) => records.Add((t, p)));

        guard.StepTwin();
        guard.StepTwin();

        Assert.Equal(GuardState.Armed, guard.TwinState);
        Assert.Equal(DoorState.Open, guard.TwinPistolsDoor.State);
        Assert.Single(records, r => r.payload.Contains("twin lane armed"));
        Assert.Single(cap.File, l => l.Contains("The menu flags read as expected; the twin weapon grant is enabled."));

        var ones = HealthyMemory();
        StageTwinFlags(ones, 1, 1);
        var guard2 = new LaunchGuard(ones, forceMismatch: false);
        guard2.StepTwin();
        Assert.Equal(GuardState.Armed, guard2.TwinState);
    }

    // LW-439: the menu byte is byte 4 of a pointer in the game's per frame record list (1 under the
    // old fixed base, 246 under the ASLR base 0x7FF65FC60000), so the door accepts any value there
    // and only the browse byte, a real 0 or 1 flag, can fault.
    [Fact]
    public void Twin_door_arms_with_the_menu_byte_at_246()
    {
        var mem = HealthyMemory();
        StageTwinFlags(mem, 246, 0);
        var guard = new LaunchGuard(mem, forceMismatch: false);

        guard.StepTwin();

        Assert.Equal(GuardState.Armed, guard.TwinState);
    }

    [Fact]
    public void Twin_door_stands_down_on_an_implausible_browse_flag()
    {
        using var cap = LogCapture.Start();
        var records = new List<(string type, string payload)>();
        var flushes = new List<string>();
        var mem = HealthyMemory();
        StageTwinFlags(mem, 0, 7);
        var guard = new LaunchGuard(mem, forceMismatch: false, recorder: (t, p) => records.Add((t, p)),
            requestFlush: flushes.Add);

        StepTwinN(guard, 29);
        Assert.Equal(GuardState.Verifying, guard.TwinState);
        guard.StepTwin();

        Assert.Equal(GuardState.StoodDown, guard.TwinState);
        Assert.Equal(DoorState.Closed, guard.TwinPistolsDoor.State);
        Assert.True(guard.TwinPistolsDoor.FailedCheck);
        Assert.Equal(LaunchGuard.TwinClosedReason, guard.TwinPistolsDoor.Reason);
        string warn = Assert.Single(cap.File, l => l.Contains("[WARN]"));
        Assert.Contains("browse flag reads 7", warn);
        Assert.DoesNotContain(ScannerPhrase, warn);
        Assert.DoesNotContain("LW_FORCE_", warn);
        Assert.Single(records, r => r.payload.Contains("twin lane stand-down"));
        Assert.Equal(new[] { "standdown" }, flushes);
    }

    [Fact]
    public void Twin_door_stands_down_on_an_out_of_range_hand_word()
    {
        using var cap = LogCapture.Start();
        var mem = HealthyMemory();
        mem.U16s[Offsets.RosterBase + Offsets.ROffHand] = 0x1234;
        var guard = new LaunchGuard(mem, forceMismatch: false);

        StepTwinN(guard, 30);

        Assert.Equal(GuardState.StoodDown, guard.TwinState);
        string warn = Assert.Single(cap.File, l => l.Contains("[WARN]"));
        Assert.Contains("off hand reads 0x1234", warn);

        foreach (ushort ok in new ushort[] { 0, 255, 0x1FF, 0xFFFF })
        {
            var good = HealthyMemory();
            good.U16s[Offsets.RosterBase + Offsets.ROffHand] = ok;
            good.U16s[Offsets.RosterBase + Offsets.RSupport] = ok;
            var g = new LaunchGuard(good, forceMismatch: false);
            g.StepTwin();
            Assert.Equal(GuardState.Armed, g.TwinState);
        }
    }

    [Fact]
    public void Twin_flags_unreadable_keeps_waiting_and_speaks_once()
    {
        using var cap = LogCapture.Start();
        var mem = HealthyMemory();
        mem.TerrainBlocks.Remove(Offsets.MenuOpenFlag);
        mem.TerrainBlocks.Remove(Offsets.PartyBrowseFlag);
        var guard = new LaunchGuard(mem, forceMismatch: false);

        StepTwinN(guard, LaunchGuard.DoorWaitSteps);
        Assert.Single(cap.File, l => l.Contains("menu flag check has not been able to read"));
        StepTwinN(guard, 3600 - LaunchGuard.DoorWaitSteps);

        string line = Assert.Single(cap.File, l => l.Contains("menu flag check has not been able to read"));
        Assert.Contains("twin weapons stays WAITING", line);
        Assert.DoesNotContain(ScannerPhrase, line);
        Assert.Equal(DoorState.Waiting, guard.TwinPistolsDoor.State);
        Assert.Equal("the menu flags cannot be read yet", guard.TwinPistolsDoor.Reason);

        using var cap2 = LogCapture.Start();
        var blind = new LaunchGuard(HealthyMemory(level: 0), forceMismatch: false);   // roster page unreadable
        StepTwinN(blind, 3600);
        Assert.DoesNotContain(cap2.File, l => l.Contains("menu flag check"));
    }

    [Fact]
    public void Twin_drill_fires_only_on_a_populated_row()
    {
        using var cap = LogCapture.Start();
        var mem = EmptySlotMemory();
        var guard = new LaunchGuard(mem, forceMismatch: false, forceTwinMismatch: true);

        StepTwinN(guard, 100);
        Assert.Equal(GuardState.Verifying, guard.TwinState);

        mem.U8s[Offsets.RosterBase + Offsets.RLevel] = 12;
        StepTwinN(guard, 29);
        Assert.Equal(GuardState.Verifying, guard.TwinState);
        guard.StepTwin();

        Assert.Equal(GuardState.StoodDown, guard.TwinState);
        Assert.Contains("drill: " + TwinDrillFlag, Assert.Single(cap.File, l => l.Contains("[WARN]")));
        Assert.Contains(TwinDrillFlag, guard.TwinPistolsDoor.Reason);

        using var cap2 = LogCapture.Start();
        var real = HealthyMemory();
        StageTwinFlags(real, 0, 7);
        var realGuard = new LaunchGuard(real, forceMismatch: false);
        StepTwinN(realGuard, 30);
        Assert.DoesNotContain(cap2.File, l => l.Contains("LW_FORCE_"));
        Assert.DoesNotContain("LW_FORCE_", realGuard.TwinPistolsDoor.Reason);
    }

    [Fact]
    public void Twin_door_reads_per_detail_with_stable_reasons()
    {
        var empty = new LaunchGuard(EmptySlotMemory(), forceMismatch: false);
        Assert.Equal("the menu flag check has not run yet", empty.TwinPistolsDoor.Reason);
        empty.StepTwin();
        Assert.Equal("waiting for a save to load", empty.TwinPistolsDoor.Reason);

        var blind = new LaunchGuard(HealthyMemory(level: 0), forceMismatch: false);
        blind.StepTwin();
        Assert.Equal("the game cannot be read yet", blind.TwinPistolsDoor.Reason);

        var noFlags = HealthyMemory();
        noFlags.TerrainBlocks.Remove(Offsets.MenuOpenFlag);
        var unreadable = new LaunchGuard(noFlags, forceMismatch: false);
        unreadable.StepTwin();
        Assert.Equal("the menu flags cannot be read yet", unreadable.TwinPistolsDoor.Reason);
        Assert.False(unreadable.TwinPistolsDoor.FailedCheck);

        var bad = HealthyMemory();
        StageTwinFlags(bad, 0, 9);
        var closed = new LaunchGuard(bad, forceMismatch: false);
        StepTwinN(closed, 30);
        Assert.True(closed.TwinPistolsDoor.FailedCheck);
        Assert.Equal(DoorState.Closed, closed.TwinPistolsDoor.State);
    }

    [Fact]
    public void Twin_texts_never_speak_the_scanner_stand_down_phrase()
    {
        var mem = HealthyMemory();
        StageTwinFlags(mem, 0, 7);
        using var cap = LogCapture.Start();
        StepTwinN(new LaunchGuard(mem, forceMismatch: true, forceTwinMismatch: true), 30);
        Assert.DoesNotContain(cap.File, l => l.Contains(ScannerPhrase));
    }

    private const string StallFooter = "Living Weapons 7.7.7, game build unknown";

    [Fact]
    public void The_stall_box_is_headline_first_paragraph_hedge_what_to_do_then_the_footer()
    {
        WithWritesRestored(() =>
        {
            using var cap = LogCapture.Start();
            var rig = NewStallRig(new FakeSparseMemory(), version: "7.7.7");

            StepN(rig.Guard, LaunchGuard.ImageStallSteps + 1);

            string box = Assert.Single(rig.Notices).text;
            Assert.Equal(LaunchGuard.ImageStallBox + "\n\n" + StallFooter, box);
            Assert.EndsWith(PartsCopy.WhatToDoBox + "\n\n" + StallFooter, box);
            Assert.Contains(PartsCopy.Hedge + "\n\n" + PartsCopy.WhatToDoBox, box);
            Assert.DoesNotContain("0x", box);
        });
    }

    [Fact]
    public void The_stall_box_puts_the_drill_sentence_before_the_footer_as_its_own_paragraph()
    {
        WithWritesRestored(() =>
        {
            using var cap = LogCapture.Start();
            var rig = NewStallRig(HealthyMemory(), forceUnreadable: true, version: "7.7.7");

            StepN(rig.Guard, LaunchGuard.ImageStallSteps + 1);

            string box = Assert.Single(rig.Notices).text;
            Assert.EndsWith("\n\nThis warning was forced by the LW_FORCE_GUARD_UNREADABLE drill flag (dev builds only).\n\n" + StallFooter, box);
        });
    }

    [Fact]
    public void The_stall_box_has_no_footer_paragraph_when_no_footer_is_wired()
    {
        WithWritesRestored(() =>
        {
            using var cap = LogCapture.Start();
            var rig = NewStallRig(new FakeSparseMemory());

            StepN(rig.Guard, LaunchGuard.ImageStallSteps + 1);

            Assert.Equal(LaunchGuard.ImageStallBox, Assert.Single(rig.Notices).text);
        });
    }

    [Fact]
    public void The_build_footer_stamp_is_unknown_until_the_header_is_read_then_the_zero_padded_stamp()
    {
        WithWritesRestored(() =>
        {
            using var cap = LogCapture.Start();
            var guard = new LaunchGuard(HealthyMemory(), forceMismatch: false, version: "7.7.7");
            Assert.Equal("unknown", guard.BuildFooterStamp);
            Assert.Equal("Living Weapons 7.7.7, game build unknown", guard.Footer);

            guard.Step();

            Assert.Equal($"0x{LaunchGuard.ExpectedTimeDateStamp:X8}", guard.BuildFooterStamp);
            Assert.Equal($"Living Weapons 7.7.7, game build 0x{LaunchGuard.ExpectedTimeDateStamp:X8}", guard.Footer);
            Assert.Equal("", new LaunchGuard(HealthyMemory(), forceMismatch: false).Footer);
        });
    }

    [Fact]
    public void The_build_footer_stamp_reports_the_real_stamp_on_an_unknown_build()
    {
        WithWritesRestored(() =>
        {
            using var cap = LogCapture.Start();
            var guard = new LaunchGuard(HealthyMemory(), forceMismatch: true);

            guard.Step();

            Assert.Equal($"0x{LaunchGuard.ExpectedTimeDateStamp:X8}", guard.BuildFooterStamp);
            Assert.Equal(false, guard.BuildStampKnown);
        });
    }

    [Fact]
    public void Latching_the_stamp_hands_the_build_to_the_flight_header()
    {
        WithWritesRestored(() =>
        {
            Flight.Reset();
            try
            {
                Flight.SetBuildInfo(version: "7.7.7");
                var guard = new LaunchGuard(HealthyMemory(), forceMismatch: false);
                guard.Step();
                Assert.Equal(("7.7.7", $"0x{LaunchGuard.ExpectedTimeDateStamp:X8}"), Flight.BuildInfo);
            }
            finally { Flight.Reset(); }
        });
    }
}
