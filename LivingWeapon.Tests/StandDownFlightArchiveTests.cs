using System.Collections.Generic;
using System.IO;
using System.Linq;
using LivingWeapon;
using Newtonsoft.Json.Linq;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-53 end-to-end: the one seam no existing test exercises. Every LaunchGuardTests test above
/// captures the recorder/requestFlush delegates directly (fake, isolated); every FlightRecorderTests
/// test drives the FlightRecorder instance directly. This file wires a REAL Flight facade
/// (Flight.Init) and a REAL FileConsoleLogger (fake sinks, mirroring CounterAttributionTests'
/// ModLogger.Instance swap-and-restore) through a REAL LaunchGuard construction, proving a
/// fingerprint-guard stand-down actually leaves a durable flight_*_standdown.jsonl archive.
/// </summary>
public class StandDownFlightArchiveTests
{
    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "lw_standdown_" + Path.GetRandomFileName());
        Directory.CreateDirectory(d);
        return d;
    }

    // Minimal PE + JobCommand + Ramza-row staging, mirroring LaunchGuardTests.HealthyMemory but
    // with a wrong nameId so the guard mismatches on ramza-roster-row alone (40 Steps stands it
    // down; MismatchDebounce is 30, LaunchGuard.cs).
    private static void SeedU32Bytes(FakeSparseMemory mem, long addr, uint value) =>
        mem.TerrainBlocks[addr] = new byte[]
        {
            (byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24),
        };

    private static FakeSparseMemory MismatchingMemory()
    {
        const long moduleBase = 0x140000000L;
        const long eLfanewOff = 0x3C;
        const uint eLfanew = 0x100;
        const long timeDateStampOff = 8;
        const long sizeOfImageOff = 0x50;

        var mem = new FakeSparseMemory();
        SeedU32Bytes(mem, moduleBase + eLfanewOff, eLfanew);
        SeedU32Bytes(mem, moduleBase + eLfanew + timeDateStampOff, LaunchGuard.ExpectedTimeDateStamp);
        SeedU32Bytes(mem, moduleBase + eLfanew + sizeOfImageOff, LaunchGuard.ExpectedSizeOfImage);

        long rec8 = Barrage.AbilityBase + 8L * Barrage.RecSize;
        long rec9 = Barrage.AbilityBase + 9L * Barrage.RecSize;
        mem.TerrainBlocks[rec8] = new byte[] { 0x96, 0x97, 0x98, 0x99, 0x9A, 0x9B, 0x9C, 0x9D };
        mem.TerrainBlocks[rec9] = new byte[] { 0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6A, 0x6B };

        long rb = Offsets.RosterBase;
        mem.U8s[rb + Offsets.RLevel] = 12;
        mem.U16s[rb + Offsets.RNameId] = 5;   // wrong: mismatches ramza-roster-row only
        mem.U8s[rb + Offsets.RSprite] = 0x02;
        mem.U8s[rb + Offsets.RBrave] = 70;
        mem.U8s[rb + Offsets.RFaith] = 65;
        return mem;
    }

    private static bool ArchiveHasGuardStandDownRecord(string path)
    {
        var lines = File.ReadAllLines(path);
        return lines.Skip(1)   // line 0 is the header object
            .Select(JObject.Parse)
            .Any(o => (string)o["e"]! == "guard" && ((string)o["d"]!).Contains("stand-down"));
    }

    [Fact]
    public void Standdown_archive_lands_on_disk()
    {
        var dir = TempDir();
        Flight.Init(dir);
        var console = new List<string>();
        var file = new List<string>();
        var prior = ModLogger.Instance;
        ModLogger.Instance = new FileConsoleLogger(console.Add, file.Add);
        try
        {
            var guard = new LaunchGuard(MismatchingMemory(), forceMismatch: false,
                recorder: Flight.Record, requestFlush: Flight.RequestFlush);

            for (int i = 0; i < 40; i++) guard.Step();
            Assert.Equal(GuardState.StoodDown, guard.State);

            Flight.DrainPending();

            var flightDir = Path.Combine(dir, "flight");
            var files = Directory.GetFiles(flightDir, "flight_*_standdown.jsonl");
            Assert.Single(files);
            Assert.True(ArchiveHasGuardStandDownRecord(files[0]));
        }
        finally { Flight.Reset(); ModLogger.Instance = prior; }
    }

    [Fact]
    public void Standdown_archive_survives_a_burnt_error_latch()
    {
        var dir = TempDir();
        Flight.Init(dir);
        var console = new List<string>();
        var file = new List<string>();
        var prior = ModLogger.Instance;
        ModLogger.Instance = new FileConsoleLogger(console.Add, file.Add);
        try
        {
            // Burn the error FlushOnce latch on an EARLIER, unrelated error before the guard ever
            // records anything: an empty-ring drain writes no file, but the latch is now spent for
            // the rest of the session (mirrors Mod.cs hooks failure / Engine.cs tick-loop catch).
            ModLogger.Error(LogVerb.Engine, "unrelated pre-stand-down error");
            Flight.DrainPending();
            var flightDir = Path.Combine(dir, "flight");
            bool anyFileYet = Directory.Exists(flightDir) && Directory.GetFiles(flightDir, "flight_*.jsonl").Length > 0;
            Assert.False(anyFileYet);

            var guard = new LaunchGuard(MismatchingMemory(), forceMismatch: false,
                recorder: Flight.Record, requestFlush: Flight.RequestFlush);

            for (int i = 0; i < 40; i++) guard.Step();
            Assert.Equal(GuardState.StoodDown, guard.State);

            Flight.DrainPending();

            var files = Directory.GetFiles(flightDir, "flight_*_standdown.jsonl");
            Assert.Single(files);
            Assert.True(ArchiveHasGuardStandDownRecord(files[0]));
        }
        finally { Flight.Reset(); ModLogger.Instance = prior; }
    }
}
