using System.Collections.Generic;
using System.Reflection;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-50's LivingWeapon adapter over FingerprintGuard: the three data-only landmarks (PE build
/// key, JobCommand rec8/rec9 signature, Ramza roster row shape), the boot-window safety rule (the
/// JobCommand landmark stays Unreadable, never Mismatch, until a save populates Ramza's roster
/// row), the always-compiled forceMismatch knob, the Mem.WritesEnabled arming edge, and the
/// deferred hook-arm handshake.
///
/// FakeSparseMemory.TryReadBytes only serves exact-address TerrainBlocks entries, so every PE and
/// JobCommand read here is staged there; U8/U16 roster fields go through the U8s/U16s
/// dictionaries (unseeded reads default to 0, i.e. an unpopulated roster slot).
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
    public void JobCommand_unreadable_until_ramza_row_populated()
    {
        // Signature windows staged CORRECT, but the roster row is empty: the guard must stay
        // Verifying forever, never Mismatch (the boot-window blocker's test).
        var mem = HealthyMemory(level: 0);
        var guard = new LaunchGuard(mem, forceMismatch: false);

        for (int i = 0; i < 50; i++) guard.Step();

        Assert.Equal(GuardState.Verifying, guard.State);
    }

    [Fact]
    public void ExpectedPeKey_constants_are_pinned()
    {
        Assert.Equal(0x6A0F86A9u, LaunchGuard.ExpectedTimeDateStamp);
        Assert.Equal(0x190EB000u, LaunchGuard.ExpectedSizeOfImage);
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
}
