using System;
using System.Collections.Generic;
using System.IO;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-150 S5: the first test that actually constructs an <see cref="Engine"/>. Two ctor seams
/// made this possible without either crashing `dotnet test` or corrupting a real save:
///   - `mem`: null (production) builds a real LiveMemory; a test injects a FakeSparseMemory that
///     is now shared by EVERY subsystem the ctor wires -- including LaunchGuard -- because
///     LW-150 S5 collapsed the ctor's local `live` variable down to an alias of `_live`.
///   - `notice`: null (production) falls back to StandDownNotice.Show (the real OS message box);
///     a test passes a captured no-op so a healthy or mismatched guard never pops a Win32 dialog
///     mid test-run.
///
/// HealthyMemory below is copied verbatim from LaunchGuardTests' own seeding recipe (PE build key
/// + JobCommand rec8/rec9 signature + Ramza's roster row) -- it arms the real guard in one Step
/// there, so reusing it here proves the SAME memory that arms the guard is also the memory
/// Tick()'s seven sentinel reads route through post-arm: the injection seam reaches the whole
/// subsystem tree, not just the guard.
/// </summary>
public class EngineTests
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

    /// <summary>LW-150 S5 hazard (c): SaveLocation.ResolveSaveDir walks up two directories from
    /// modDir (Directory.GetParent(modDir).Parent) to find the Reloaded root, then creates
    /// &lt;root&gt;/User/Mods/&lt;ModId&gt; for real on disk. A modDir fewer than 3 levels below
    /// the TempDirs root would resolve that root OUTSIDE the sandbox TempDirs cleans up (e.g. the
    /// OS temp root's own parent), leaking a real directory. Nesting modDir 3 levels deep keeps
    /// the resolved root (and everything Engine writes) inside the disposed TempDirs.Dir.</summary>
    private static string NestedModDir(TempDirs temp)
    {
        string modDir = Path.Combine(temp.Dir, "Reloaded-II", "Mods", "prawl.fft.livingweapons");
        Directory.CreateDirectory(modDir);
        return modDir;
    }

    [Fact]
    public void Tick_routes_the_seven_battle_sentinels_through_the_injected_memory()
    {
        // (b) FIRST, before any other setup: nothing here must ever touch the real console/file
        // sinks or (via a logged stand-down) risk confusion with a real run's logs.
        ModLogger.UseNullLogger();
        try
        {
            using var temp = TempDirs.Create("lw_engine_");
            string modDir = NestedModDir(temp);
            var mem = HealthyMemory();
            var notices = new List<(string title, string text)>();

            // (a) notice is a captured no-op: a mismatch here must NEVER raise a real OS message
            // box during `dotnet test`. mem is the fake: the guard arms over it AND Tick()'s
            // sentinel reads route through it, because Engine's ctor now aliases `live` to `_live`.
            var engine = new Engine(modDir, mem: mem, notice: (t, x) => notices.Add((t, x)));

            // Tick 1: guard is Verifying -> Step() arms it (HealthyMemory matches every landmark,
            // mirroring LaunchGuardTests.RamzaRow_valid_is_match's single-Step arm) but Tick()
            // still returns early THIS call (Engine.cs's guard block always returns once it ran
            // Step this tick, win or lose). From tick 2 on, Armed is already true, so Tick() falls
            // through the guard block into the seven sentinel reads. healthy-but-unstaged
            // battle/pause/submenu/event addresses all read 0 -> InLiveBattle is false -> Tick()
            // takes the out-of-battle branch every time (never mutates a save, no battle to be in).
            for (int i = 0; i < 5; i++) engine.Tick();

            Assert.Empty(notices);   // healthy memory arms cleanly; the notice must never fire

            // Non-vacuity: pre-refactor, Tick() read static Mem directly, so this fake's ReadCount
            // would show NONE of these addresses touched at all. Post-refactor it shows all seven
            // sentinel reads routed through the injected fake (slot0/slot9 are U32 reads -- the
            // default IGameMemory composition of two U16 reads -- observable at their base address
            // via FakeSparseMemory.U16's ReadCount tracking, see FakeSparseMemory.cs).
            Assert.True(mem.ReadCount.ContainsKey(Offsets.Slot0), "slot0 (U32) was never read through the fake");
            Assert.True(mem.ReadCount.ContainsKey(Offsets.Slot9), "slot9 (U32) was never read through the fake");
            Assert.True(mem.ReadCount.ContainsKey(Offsets.BattleMode), "battleMode (U8) was never read through the fake");
            Assert.True(mem.ReadCount.ContainsKey(Offsets.EventId), "eventId (U16) was never read through the fake");
            Assert.True(mem.ReadCount.ContainsKey(Offsets.PauseFlag), "PauseFlag (U8) was never read through the fake");
            Assert.True(mem.ReadCount.ContainsKey(Offsets.SubmenuFlag), "SubmenuFlag (U8) was never read through the fake");

            // THE RULING (locked): PauseFlag is read TWICE per tick that reaches it -- once at the
            // top (line ~359) and once more, deliberately fresher, feeding StatusCardOpen (line
            // ~425) -- never collapsed into one read. Pin the 2x ratio against BattleMode (read
            // exactly once per the same tick) rather than a hand-counted literal, so this survives
            // an unrelated change to how many ticks armed vs. reached the sentinel reads.
            Assert.Equal(mem.ReadCount[Offsets.BattleMode] * 2, mem.ReadCount[Offsets.PauseFlag]);
        }
        finally { ModLogger.UseNullLogger(); }
    }

    // --- LW-150 S5 item 3: the two load-bearing orderings at the ctor's _signatures/
    // _fieldSignatures assignment are comment-enforced today ("Both orders are load-bearing and
    // preserved verbatim from the hand-wired era..."). Pin the EXACT type sequences read straight
    // off Engine.cs so an accidental reorder breaks a test, not just a comment nobody re-reads. ---

    [Fact]
    public void SignatureResetOrder_matches_the_hand_wired_battle_exit_sequence()
    {
        ModLogger.UseNullLogger();
        try
        {
            using var temp = TempDirs.Create("lw_engine_order_");
            var engine = new Engine(NestedModDir(temp), mem: HealthyMemory(), notice: (_, __) => { });

            Assert.Equal(new[]
            {
                typeof(ExtraTurn), typeof(EagleEye), typeof(Ricochet), typeof(Maim),
                typeof(Kobu), typeof(Iai), typeof(Mushin), typeof(Larceny), typeof(Puppeteer), typeof(Plague),
                typeof(Barrage), typeof(ShadowBlade), typeof(Provoke), typeof(ProvokeHold),
                typeof(Renewal), typeof(Rapture), typeof(SpiritualFont), typeof(FeignDeath),
                typeof(Benediction), typeof(Sanctuary), typeof(Choir), typeof(Bulwark), typeof(TreasureMaster),
            }, engine.SignatureResetOrder);
        }
        finally { ModLogger.UseNullLogger(); }
    }

    [Fact]
    public void FieldTickOrder_matches_the_hand_wired_in_battle_sequence_and_excludes_the_pre_gate_four()
    {
        ModLogger.UseNullLogger();
        try
        {
            using var temp = TempDirs.Create("lw_engine_order_");
            var engine = new Engine(NestedModDir(temp), mem: HealthyMemory(), notice: (_, __) => { });

            var expected = new[]
            {
                typeof(ExtraTurn), typeof(EagleEye), typeof(Ricochet), typeof(Maim), typeof(Kobu), typeof(Iai),
                typeof(Mushin), typeof(Larceny), typeof(Puppeteer), typeof(Plague), typeof(ProvokeHold),
                typeof(Renewal), typeof(Rapture), typeof(SpiritualFont),
                typeof(FeignDeath), typeof(Benediction), typeof(Sanctuary), typeof(Choir), typeof(Bulwark),
            };
            Assert.Equal(expected, engine.FieldTickOrder);

            // The four that tick pre-gate instead (see the ctor's comment above _signatures): named
            // explicitly so a reader never has to diff the two arrays above by eye to find them.
            var preGate = new[] { typeof(Barrage), typeof(ShadowBlade), typeof(Provoke), typeof(TreasureMaster) };
            foreach (var t in preGate)
                Assert.DoesNotContain(t, engine.FieldTickOrder);
        }
        finally { ModLogger.UseNullLogger(); }
    }

    // --- LW-184: Engine.Tick's fan-out is now a declarative TickPhase[] table (TickPhase.cs).
    // These two pin the table itself against the hand-wired sequence it replaced, so a reorder or
    // a dropped/miswired row breaks a test, not just a comment. ---

    [Fact]
    public void Phases_match_the_hand_wired_tick_order()
    {
        ModLogger.UseNullLogger();
        try
        {
            using var temp = TempDirs.Create("lw_engine_phases_");
            var engine = new Engine(NestedModDir(temp), mem: HealthyMemory(), notice: (_, __) => { });

            var expected = new List<(string Name, Func<TickPhaseState, bool> Gate, int EveryNTicks, bool FiresOnFirstPass)>
            {
                ("kit-barrage", TickGates.KitLane, 1, false),
                ("kit-shadowblade", TickGates.KitLane, 1, false),
                ("kit-provoke", TickGates.KitLane, 1, false),
                ("treasure", TickGates.Always, 1, false),
                ("gunslinger", TickGates.Always, 30, false),
                ("scholar-ring", TickGates.OutOfBattle, 30, false),
                ("display-out", TickGates.OutOfBattle, 1, false),
                ("kill-poll", TickGates.InBattle, 1, false),
                ("turn-poll", TickGates.InBattle, 1, false),
                ("field-signatures", TickGates.InBattle, 1, false),
                ("living-poach", TickGates.InBattle, 1, false),
                ("growth", TickGates.InBattle, 3, true),
                ("toast", TickGates.InBattle, 1, false),
                ("attack-card", TickGates.InBattle, 1, false),
            };
#if LWDEV
            expected.AddRange(new (string, Func<TickPhaseState, bool>, int, bool)[]
            {
                ("turn-owner-spike", TickGates.InBattle, 1, false),
                ("status-spike", TickGates.InBattle, 1, false),
                ("body-double-spike", TickGates.InBattle, 1, false),
                ("provoke-spike", TickGates.InBattle, 1, false),
                ("numeral-spike", TickGates.InBattle, 1, false),
            });
#endif
            expected.Add(("save-on-change", TickGates.InBattle, 1, false));
            expected.Add(("paint", TickGates.InBattle, 1, false));

            Assert.Equal(expected.Count, engine.Phases.Count);
            for (int i = 0; i < expected.Count; i++)
            {
                var row = engine.Phases[i];
                Assert.Equal(expected[i].Name, row.Name);
                Assert.Same(expected[i].Gate, row.Gate);
                Assert.Equal(expected[i].EveryNTicks, row.EveryNTicks);
                Assert.Equal(expected[i].FiresOnFirstPass, row.FiresOnFirstPass);
            }
        }
        finally { ModLogger.UseNullLogger(); }
    }

    [Fact]
    public void Phase_after_annotations_only_name_earlier_rows()
    {
        ModLogger.UseNullLogger();
        try
        {
            using var temp = TempDirs.Create("lw_engine_phases_after_");
            var engine = new Engine(NestedModDir(temp), mem: HealthyMemory(), notice: (_, __) => { });

            // Machine-checks every row's "after" ordering-reason annotations: each cited name must
            // belong to a row that already ran (strictly earlier in the table), so the comments
            // documenting a data-flow dependency (e.g. toast/save-on-change reading Changed from
            // kill-poll) can never silently drift out of sync with the actual row order.
            var seen = new HashSet<string>();
            foreach (var phase in engine.Phases)
            {
                foreach (var reason in phase.After)
                    Assert.True(seen.Contains(reason),
                        $"'{phase.Name}' cites '{reason}' in After, but that row has not run yet.");
                seen.Add(phase.Name);
            }
        }
        finally { ModLogger.UseNullLogger(); }
    }

    // LW-184 follow-up: the two tests above pin the phase TABLE's shape against the hand-wired
    // order it replaced, but neither one ever calls Tick() -- a row wired to the wrong lambda or
    // the wrong gate (e.g. a copy-paste that left kill-poll gated on OutOfBattle) would sail
    // through both untouched. This one drives the real Tick() through both regimes and checks
    // which rows actually ran, by memory address, not by table introspection.
    [Fact]
    public void InBattle_regime_runs_the_battle_fanout_and_out_of_battle_does_not()
    {
        ModLogger.UseNullLogger();
        try
        {
            // Two separate engines/memories -- one driven into the live battlefield, one left
            // out of battle -- sidesteps BattleState's debounced EXIT (a real battle end needs 4
            // real seconds of accumulated out-of-live time and Tick() has no injectable clock),
            // so each regime is reached cleanly from its own fresh start instead of round-tripping
            // one engine through an edge Tick() cannot fast-forward in a unit test.
            using var tempIn = TempDirs.Create("lw_engine_regime_in_");
            var memIn = HealthyMemory();
            var engineIn = new Engine(NestedModDir(tempIn), mem: memIn, notice: (_, __) => { });
            engineIn.Tick();   // arms the guard (mirrors the sentinel test); phases don't run this tick.
            // Stage the in-battle regime: battleMode 3 alone satisfies both BattleState.EnterSignal
            // (instant enter) and BattleState.InLiveBattle; slot9 stuck at its sentinel matches the
            // documented in-battle pair (Offsets.Slot9's own doc comment).
            SeedU32Bytes(memIn, Offsets.Slot9, 0xFFFFFFFF);
            memIn.U8s[Offsets.BattleMode] = 3;
            engineIn.Tick();   // Tick A: enters battle this same tick, then runs the in-battle fan-out.

            using var tempOut = TempDirs.Create("lw_engine_regime_out_");
            var memOut = HealthyMemory();
            var engineOut = new Engine(NestedModDir(tempOut), mem: memOut, notice: (_, __) => { });
            engineOut.Tick();   // arms the guard
            engineOut.Tick();   // Tick B: healthy-but-unstaged sentinels all read 0 -> stays out of battle.

            // Signature: Offsets.ActorPtr (the engine's own acting-unit pointer, resolved via
            // Band.ActorEntry) is touched ONLY by in-battle-gated rows -- kill-poll's
            // ActorRegister.Update chief among them -- never by the Always/OutOfBattle rows
            // (kit-lane trio, treasure, gunslinger, scholar-ring, display-out), none of which ever
            // reference it. A region-level presence check, not an exact count, so this survives an
            // unrelated reshuffle of exactly which in-battle row reads it first.
            Assert.True(memIn.ReadCount.ContainsKey(Offsets.ActorPtr),
                "in-battle regime: Offsets.ActorPtr was never read -- the in-battle fan-out did not run");
            Assert.False(memOut.ReadCount.ContainsKey(Offsets.ActorPtr),
                "out-of-battle regime: Offsets.ActorPtr was read -- an in-battle-gated row ran while NowIn was false");

            // The prologue's own sentinel reads fire every tick regardless of regime (both engines
            // are past their arm tick here) -- the out-of-battle regime is not just "nothing ran",
            // it is specifically "the battle-fanout rows didn't run".
            Assert.True(memOut.ReadCount.ContainsKey(Offsets.Slot0));
            Assert.True(memOut.ReadCount.ContainsKey(Offsets.Slot9));
            Assert.True(memOut.ReadCount.ContainsKey(Offsets.BattleMode));
            Assert.True(memOut.ReadCount.ContainsKey(Offsets.PauseFlag));
            Assert.True(memOut.ReadCount.ContainsKey(Offsets.EventId));
        }
        finally { ModLogger.UseNullLogger(); }
    }

    [Fact]
    public void ResolveNotice_falls_back_to_StandDownNotice_Show_and_passes_through_a_non_null_override()
    {
        // A verifier proved dropping the `?? StandDownNotice.Show` fallback at the ctor's
        // LaunchGuard wiring site leaves the whole suite green while production would silently
        // lose the stand-down message box: nothing else here constructs an Engine with `notice`
        // left null AND then observes which delegate LaunchGuard got. Pin the null-coalesce
        // directly instead of trusting the wiring site to keep doing it.
        Assert.Equal((Action<string, string>)StandDownNotice.Show, Engine.ResolveNotice(null));

        Action<string, string> passthrough = (_, __) => { };
        Assert.Same(passthrough, Engine.ResolveNotice(passthrough));
    }

    [Fact]
    public void Mem_defaults_to_null_so_a_bare_construction_still_compiles_and_arms_over_LiveMemory()
    {
        // LW-150 S5's byte-identical-production guarantee: a caller that never passes `mem` (every
        // production call site, Mod.cs included) must still compile and behave exactly as before
        // the ctor param existed. This test cannot observe a real LiveMemory arming for real (there
        // is no game process attached under `dotnet test`), but it pins that the overload resolves
        // and the ctor does not throw when `mem` and `notice` are both omitted.
        ModLogger.UseNullLogger();
        try
        {
            using var temp = TempDirs.Create("lw_engine_defaults_");
            string modDir = NestedModDir(temp);

            var ex = Record.Exception(() => new Engine(modDir));

            Assert.Null(ex);
        }
        finally { ModLogger.UseNullLogger(); }
    }
}
