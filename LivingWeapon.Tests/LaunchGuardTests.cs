using System.Collections.Generic;
using System.Reflection;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-50's LivingWeapon adapter over FingerprintGuard, split by LW-112 into two independent state
/// machines. The MAIN guard checks two data-only landmarks (PE build key, Ramza's roster row
/// shape); a mismatch there stands the WHOLE mod down (writes stay disabled all session). The
/// KIT-LANE guard owns the third landmark, the JobCommand rec8/rec9 ability-byte signature: it only
/// ever steps once the main guard has already armed, and a lane mismatch disables ONLY the three
/// weapon-granted commands (Barrage, Shadow Blade, Provoke) while the mod itself stays armed and
/// everything else keeps working. Post-arm, an all-zero rec8/rec9 window is a Mismatch ("blanked"),
/// not a boot-window escape: the lane never probes before a save has populated the roster (that
/// gate lives on the main guard's own roster landmark), so by the time it runs an all-zero read can
/// only mean another mod blanked the row, never an unbuilt table. Only a genuine read failure
/// (module not mapped yet, a short read) still reads Unreadable. The always-compiled forceMismatch
/// knob, the Mem.WritesEnabled arming edge, and the deferred hook-arm handshake are all main-guard
/// concerns, unaffected by the split.
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

    private static FakeSparseMemory HealthyMemory(int level = 12, int nameId = 1, byte sprite = 0x02,
        int brave = 70, int faith = 65)
    {
        var mem = new FakeSparseMemory();
        StagePeHealthy(mem);
        StageJobCommandHealthy(mem);
        StageRamzaRow(mem, level, nameId, sprite, brave, faith);
        return mem;
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
        Assert.Equal(0x6A5EA53Cu, LaunchGuard.ExpectedTimeDateStamp);
        Assert.Equal(0x18D78000u, LaunchGuard.ExpectedSizeOfImage);
    }

    [Fact]
    public void ForceMismatch_stands_down_despite_matching_memory()
    {
        var mem = HealthyMemory();
        var guard = new LaunchGuard(mem, forceMismatch: true);

        for (int i = 0; i < 40; i++) guard.Step();

        Assert.Equal(GuardState.StoodDown, guard.State);
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
    public void HookArm_offered_before_arm_fires_on_armed_edge_exactly_once()
    {
        var mem = HealthyMemory();
        var guard = new LaunchGuard(mem, forceMismatch: false);
        int armCount = 0;
        guard.OfferHookArm(() => armCount++);
        Assert.Equal(0, armCount);

        guard.Step();

        Assert.Equal(GuardState.Armed, guard.State);
        Assert.Equal(1, armCount);

        guard.Step();
        Assert.Equal(1, armCount);
    }

    [Fact]
    public void HookArm_offered_after_arm_fires_immediately()
    {
        var mem = HealthyMemory();
        var guard = new LaunchGuard(mem, forceMismatch: false);
        guard.Step();
        Assert.Equal(GuardState.Armed, guard.State);

        int armCount = 0;
        guard.OfferHookArm(() => armCount++);

        Assert.Equal(1, armCount);
    }

    [Fact]
    public void HookArm_never_fires_when_stood_down()
    {
        var mem = HealthyMemory(nameId: 5);   // mismatch
        var guard = new LaunchGuard(mem, forceMismatch: false);
        for (int i = 0; i < 40; i++) guard.Step();
        Assert.Equal(GuardState.StoodDown, guard.State);

        int armCount = 0;
        guard.OfferHookArm(() => armCount++);

        Assert.Equal(0, armCount);
    }

    [Fact]
    public void StandDown_invokes_notice_exactly_once()
    {
        var mem = HealthyMemory(nameId: 5);   // mismatch
        var calls = new List<(string title, string text)>();
        var guard = new LaunchGuard(mem, forceMismatch: false, notice: (title, text) => calls.Add((title, text)));

        for (int i = 0; i < 40; i++) guard.Step();
        Assert.Equal(GuardState.StoodDown, guard.State);

        for (int i = 0; i < 20; i++) guard.Step();   // stand-down is terminal: no repeat notice

        Assert.Single(calls);
        Assert.Equal("FFT Living Weapons", calls[0].title);
        Assert.Contains("livingweapon.log", calls[0].text);
        Assert.Contains("ptyrawl@gmail.com", calls[0].text);
        // Owner-authored copy, 2026-07-14: pin the headline and the attach-your-logs ask.
        Assert.Contains("Living Weapons has switched itself off", calls[0].text);
        Assert.Contains("attach your logs", calls[0].text);
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
    public void StandDown_record_carries_observed_and_expected_pe_key()
    {
        var mem = HealthyMemory();
        SeedU32Bytes(mem, ModuleBase + ELfanew + TimeDateStampOff, 0x11111111);
        var records = new List<(string type, string payload)>();
        var guard = new LaunchGuard(mem, forceMismatch: false, recorder: (t, p) => records.Add((t, p)));

        for (int i = 0; i < 40; i++) guard.Step();

        Assert.Equal(GuardState.StoodDown, guard.State);
        Assert.Single(records);
        Assert.Contains("0x6A5EA53C", records[0].payload);
        Assert.Contains("0x11111111", records[0].payload);
    }

    [Fact]
    public void Drill_standdown_names_the_flag_in_record_and_log()
    {
        var mem = HealthyMemory();
        var records = new List<(string type, string payload)>();
        var console = new List<string>();
        var file = new List<string>();
        var prior = ModLogger.Instance;
        ModLogger.Instance = new FileConsoleLogger(console.Add, file.Add);
        try
        {
            var guard = new LaunchGuard(mem, forceMismatch: true, recorder: (t, p) => records.Add((t, p)));

            for (int i = 0; i < 40; i++) guard.Step();

            Assert.Equal(GuardState.StoodDown, guard.State);
            Assert.Single(records);
            Assert.Contains("LW_FORCE_FINGERPRINT_MISMATCH", records[0].payload);
            Assert.Contains(file, line => line.Contains("LW_FORCE_FINGERPRINT_MISMATCH"));
        }
        finally { ModLogger.Instance = prior; }
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

                Assert.Single(calls);
                Assert.Contains("Living Weapons found another mod editing the same game data", calls[0].text);
                Assert.DoesNotContain("switched itself off", calls[0].text);
                Assert.DoesNotContain("standing down to protect your save", calls[0].text);

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
    public void PeConflict_and_laneConflict_together_stand_down_whole_mod_lane_never_steps()
    {
        // Sharpened ordering test: a simultaneous PE mismatch AND a JobCommand conflict must still
        // resolve as ONE full stand-down, never as a lane-only narration -- the lane can only ever
        // speak once the main guard has already armed, and here it never does.
        var mem = HealthyMemory();
        SeedU32Bytes(mem, ModuleBase + ELfanew + TimeDateStampOff, 0x11111111);   // PE mismatch
        long rec8 = Barrage.AbilityBase + 8L * Barrage.RecSize;
        mem.TerrainBlocks[rec8] = new byte[] { 0xAA, 0xAA, 0xAA, 0xAA, 0xAA, 0xAA, 0xAA, 0xAA };   // simultaneous lane conflict
        var calls = new List<(string title, string text)>();
        var console = new List<string>();
        var file = new List<string>();
        var prior = ModLogger.Instance;
        ModLogger.Instance = new FileConsoleLogger(console.Add, file.Add);
        try
        {
            var guard = new LaunchGuard(mem, forceMismatch: false, notice: (title, text) => calls.Add((title, text)));

            for (int i = 0; i < 40; i++) { guard.Step(); guard.StepKitLane(); }

            Assert.Equal(GuardState.StoodDown, guard.State);
            Assert.Equal(GuardState.Verifying, guard.KitLaneState);   // the lane NEVER stepped: main never armed

            Assert.Single(calls);
            Assert.Contains("Living Weapons has switched itself off", calls[0].text);   // the game-update copy

            Assert.Contains(file, line => line.Contains("standing down to protect your save"));
            Assert.DoesNotContain(file, line => line.Contains("another installed mod"));
        }
        finally { ModLogger.Instance = prior; }
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
                Assert.Contains(file, line => line.Contains("weapon-granted commands") && line.Contains("enabled"));
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
            Assert.Single(calls);
            Assert.DoesNotContain("standing down to protect your save", calls[0].text);
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
    public void Drill_forceMismatch_still_full_stand_down_and_lane_never_speaks()
    {
        var mem = HealthyMemory();   // all memory healthy; the drill knob perturbs the EXPECTED value, not memory
        var calls = new List<(string title, string text)>();
        var console = new List<string>();
        var file = new List<string>();
        var prior = ModLogger.Instance;
        ModLogger.Instance = new FileConsoleLogger(console.Add, file.Add);
        try
        {
            var guard = new LaunchGuard(mem, forceMismatch: true, notice: (title, text) => calls.Add((title, text)));

            for (int i = 0; i < 40; i++) { guard.Step(); guard.StepKitLane(); }

            Assert.Equal(GuardState.StoodDown, guard.State);
            Assert.Contains(file, line => line.Contains("LW_FORCE_FINGERPRINT_MISMATCH"));
            Assert.Equal(GuardState.Verifying, guard.KitLaneState);   // lane never speaks: main never armed
            Assert.Single(calls);
        }
        finally { ModLogger.Instance = prior; }
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
                Assert.Single(calls);
                Assert.DoesNotContain("standing down to protect your save", calls[0].text);
                Assert.DoesNotContain(file, line => line.Contains("standing down to protect your save"));
            }
            finally { ModLogger.Instance = prior; }
        }
        finally { Mem.WritesEnabled = prevWrites; }
    }
}
