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
        Assert.Contains("0x6A5E4A1B", records[0].payload);
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

    private static void StageSentinels(FakeSparseMemory mem, uint slot0, uint slot9, byte mode,
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

            StepSensingN(rig.Guard, 29);
            Assert.Equal(GuardState.Verifying, rig.Guard.SensingState);
            rig.Guard.StepSensing();   // exactly MismatchDebounce (30) mismatching steps

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

            StepSensingN(rig.Guard, 30);

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
    public void Sensing_lane_never_steps_before_the_main_guard_arms()
    {
        WithWritesRestored(() =>
        {
            var mem = HealthyMemory(level: 0);   // unpopulated roster: the main guard keeps retrying
            StageSentinels(mem, 1u, 1u, 7, 9, 5, 5);   // would be a Mismatch if the lane ever stepped
            var rig = NewSensingRig(mem);
            rig.Guard.Step();

            StepSensingN(rig.Guard, 35);

            Assert.Equal(GuardState.Verifying, rig.Guard.State);
            Assert.Equal(GuardState.Verifying, rig.Guard.SensingState);
            Assert.Empty(rig.Records);
            Assert.Empty(rig.Flushes);
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

            StepSensingN(rig.Guard, 30);

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

            StepSensingN(rig.Guard, 30);

            Assert.Equal(GuardState.StoodDown, rig.Guard.SensingState);
            Assert.Contains(cap.File, l => l.Contains("battle sentinels do not read the way this mod expects"));
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

            StepSensingN(rig.Guard, 30);

            Assert.Equal(GuardState.StoodDown, rig.Guard.SensingState);
            string line = Assert.Single(cap.File, l => l.Contains("The battle sentinels do not read the way this mod expects"));
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

    private static StallRig NewStallRig(FakeSparseMemory mem, bool forceUnreadable = false)
    {
        var rig = new StallRig();
        rig.Guard = new LaunchGuard(mem, forceMismatch: false,
            notice: (t, x) => rig.Notices.Add((t, x)),
            recorder: (t, p) => rig.Records.Add((t, p)),
            forceUnreadable: forceUnreadable);
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
                                     LaunchGuard.RosterWaitInfo, LaunchGuard.RecoveryInfo })
            Assert.DoesNotContain("standing down to protect your save", text);
    }
}
