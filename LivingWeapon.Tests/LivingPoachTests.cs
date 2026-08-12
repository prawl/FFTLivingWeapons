using System;
using System.Collections.Generic;
using System.IO;
using LivingWeapon;
using Xunit;
using static LivingWeapon.Tests.KillTrackerFixtures;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-167 Living Poach (docs plan v2, stages 1-2). Mirrors GunSlingerTests' staged structure:
///   Stage-1  LivingPoachPolicy pure decisions (Decide / StripIconMarkup)
///   Stage-1b PoachMap loader (the real committed poach.json + missing/corrupt-file disarm)
///   Stage-1c LW-169 job-id remap pinned regression (owner-observed live 2026-08-12): the old
///            species = victim job byte - 95 arithmetic is gone; poach.json now keys straight off
///            each monster job's own id (tools/extract_poach_map.py decodes it off the Job sheet).
///   Stage-2  LivingPoach executor via FakeSparseMemory (guarded W8 store write + toast + the
///            per-corpse dedupe latch) and DeedFanout/KillTracker's widened deed seam.
///   Stage-2b The killer's real Poach-bit read (combat support bitfield).
///   Stage-4  The real basic-Attack discriminator (LivingPoach.ReadWasBasicAttack) and the ARMED
///            end-to-end wiring (Engine.cs no longer passes a constant false) -- see LivingPoach.cs's
///            ctor doc. Most stage-2 executor tests above still inject wasBasicAttack directly
///            (MakePoach's bool param) to stay isolated from live-memory fixtures; that is
///            deliberate, not stale coverage -- the discriminator itself is proven separately below.
/// Reliquary/KillTracker's PRE-EXISTING suites (ReliquaryTests.cs, KillTrackerDeedTests.cs,
/// KillTrackerBattleCountersTests.cs) are the regression proof that this feature's seam-widening is
/// byte-identical for every other consumer; they run unmodified.
/// </summary>
public class LivingPoachTests : IDisposable
{
    private readonly List<TempDirs> _tempDirs = new();
    public void Dispose() { foreach (var t in _tempDirs) t.Dispose(); }
    private string TempDir() { var t = TempDirs.Create("lw_poach_"); _tempDirs.Add(t); return t.Dir; }

    /// <summary>Walk up from the test bin dir to the repo root, mirroring MetaSchemaTests'
    /// RepoMetaPath -- returns the LivingWeapon/ dir (poach.json's home), not the file itself.</summary>
    private static string RepoLivingWeaponDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "LivingWeapon", "poach.json");
            if (File.Exists(candidate)) return Path.Combine(dir.FullName, "LivingWeapon");
            dir = dir.Parent;
        }
        throw new FileNotFoundException("LivingWeapon/poach.json not found above the test bin dir");
    }

    // ── Stage-1: LivingPoachPolicy pure decisions ─────────────────────────────

    // KEYSTONE (the double-fire guard #1): a vanilla-formula weapon must NEVER fire, even with
    // every other gate green -- vanilla's own Poach support already procs through that weapon.
    [Fact]
    public void Decide_KEYSTONE_vanillaFormula_neverFires_evenWithEveryOtherGateGreen()
    {
        var v = LivingPoachPolicy.Decide(weaponIsDormantFormula: false, killerHasPoach: true,
            wasBasicAttack: true, victimJob: 94, jobMapped: true, roll: 0);
        Assert.Equal(PoachVerdict.None, v);
    }

    // Double-fire guard #2: vanilla poaches an ABILITY kill itself (owner-observed live
    // 2026-08-12), so an ability kill must never ALSO fire the Living Poach roll.
    [Fact]
    public void Decide_wasBasicAttack_false_None()
    {
        var v = LivingPoachPolicy.Decide(true, killerHasPoach: true, wasBasicAttack: false,
            victimJob: 94, jobMapped: true, roll: 0);
        Assert.Equal(PoachVerdict.None, v);
    }

    [Fact]
    public void Decide_killerHasPoach_false_None()
    {
        var v = LivingPoachPolicy.Decide(true, killerHasPoach: false, wasBasicAttack: true,
            victimJob: 94, jobMapped: true, roll: 0);
        Assert.Equal(PoachVerdict.None, v);
    }

    // LW-169: jobMapped is the whole monster-eligibility gate now -- no range check behind it.
    // A human job (or a monster job poach.json never resolved) reads jobMapped: false here.
    [Fact]
    public void Decide_unmapped_job_None()
    {
        var v = LivingPoachPolicy.Decide(true, true, true, victimJob: 93, jobMapped: false, roll: 0);
        Assert.Equal(PoachVerdict.None, v);
    }

    // internal enum can't be a public [Theory] parameter type (CS0051) -- expectCommon stands in.
    [Theory]
    [InlineData(0, true)]
    [InlineData(224, true)]
    [InlineData(225, false)]
    [InlineData(255, false)]
    public void Decide_roll_boundaries(int roll, bool expectCommon)
    {
        var v = LivingPoachPolicy.Decide(true, true, true, victimJob: 94, jobMapped: true, roll: roll);
        Assert.Equal(expectCommon ? PoachVerdict.Common : PoachVerdict.Rare, v);
    }

    [Theory]
    [InlineData("Chocobo Carcass<Icon=103>", "Chocobo Carcass")]
    [InlineData("Chocobo Carcass", "Chocobo Carcass")]
    public void StripIconMarkup_removes_markup_leaves_plain_names_alone(string raw, string expected)
        => Assert.Equal(expected, LivingPoachPolicy.StripIconMarkup(raw));

    // ── Stage-1b: PoachMap loader ──────────────────────────────────────────────

    [Fact]
    public void PoachMap_loads_the_real_committed_poach_json()
    {
        var map = new PoachMap(RepoLivingWeaponDir());
        Assert.True(map.IsLoaded);

        Assert.True(map.TryGetJob(94, out var chocobo));   // Chocobo
        Assert.Equal(1, chocobo.CommonKey);
        Assert.Equal("Chocobo Carcass", chocobo.CommonName);

        Assert.True(map.TryGetJob(99, out var gobbledygook));   // Gobbledygook
        Assert.Equal(11, gobbledygook.CommonKey);
    }

    [Fact]
    public void PoachMap_missing_file_disarms_without_throwing()
    {
        PoachMap? map = null;
        var ex = Record.Exception(() => map = new PoachMap(TempDir()));
        Assert.Null(ex);
        Assert.False(map!.IsLoaded);
        Assert.False(map.TryGetJob(94, out _));
    }

    [Fact]
    public void PoachMap_corrupt_json_disarms_without_throwing()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "poach.json"), "{ not valid json");
        PoachMap? map = null;
        var ex = Record.Exception(() => map = new PoachMap(dir));
        Assert.Null(ex);
        Assert.False(map!.IsLoaded);
    }

    [Fact]
    public void PoachMap_missing_file_logs_exactly_one_warning()
    {
        using var cap = LogCapture.Start(file: false);
        _ = new PoachMap(TempDir());
        Assert.Single(cap.Console, l => l.Contains("Living Poach is disarmed"));
    }

    // Every carcass key in the committed file must fall inside the store's u8[96] -- the write
    // computes PoachStoreBase + key - 1, so a key outside 1..96 would write below or past the
    // array, and a duplicate key would let two different carcasses collide on one byte. Pins the
    // real committed poach.json so a future regeneration (tools/extract_poach_map.py) can never
    // silently produce either.
    [Fact]
    public void PoachMap_every_carcass_key_is_in_1_96_and_unique()
    {
        var map = new PoachMap(RepoLivingWeaponDir());
        Assert.True(map.IsLoaded);

        var keys = new List<int>();
        for (int jobId = 1; jobId <= 300; jobId++)
        {
            if (!map.TryGetJob(jobId, out var carcass)) continue;
            keys.Add(carcass.CommonKey);
            keys.Add(carcass.RareKey);
        }
        Assert.NotEmpty(keys);

        var seen = new HashSet<int>();
        foreach (int key in keys)
        {
            Assert.InRange(key, 1, 96);
            Assert.True(seen.Add(key), $"duplicate carcass key {key}");
        }
    }

    // ── Stage-1c: LW-169 job-id remap pinned regression (owner-observed live 2026-08-12) ──────
    // A Black Chocobo (job 95) carried the victim's job byte straight through and was refused
    // under the old "species = job - 95" arithmetic (job 95 landed one below JobInMonsterRange's
    // own 96..144 band). The game's Job sheet carries each monster job's PoachItem common/rare
    // Keys directly; human jobs (Mime, job 93) carry none. Map-membership BY JOB ID is the whole
    // gate now -- these pin the exact falsifying case plus its nearest neighbors against both the
    // map lookup and the full executor.

    [Fact]
    public void PoachMap_job95_blackChocobo_is_mapped_to_keys_3_and_4()
    {
        var map = new PoachMap(RepoLivingWeaponDir());
        Assert.True(map.TryGetJob(95, out var carcass));
        Assert.Equal(3, carcass.CommonKey);
        Assert.Equal(4, carcass.RareKey);
    }

    [Fact]
    public void PoachMap_job94_chocobo_is_mapped_to_keys_1_and_2()
    {
        var map = new PoachMap(RepoLivingWeaponDir());
        Assert.True(map.TryGetJob(94, out var carcass));
        Assert.Equal(1, carcass.CommonKey);
        Assert.Equal(2, carcass.RareKey);
    }

    [Fact]
    public void PoachMap_job93_mime_human_job_is_unmapped()
    {
        var map = new PoachMap(RepoLivingWeaponDir());
        Assert.False(map.TryGetJob(93, out _));
    }

    // THE FALSIFYING CASE: a Black Chocobo (job 95) killed by a basic Attack, dormant-formula
    // weapon, Poach-supported killer -- every gate green. Under the old arithmetic this never
    // wrote the store (confirmed RED against the pre-fix tree); it must decide Common/Rare now.
    [Fact]
    public void RecordPoachDeed_job95_blackChocobo_THE_FALSIFYING_CASE_now_writes_the_store()
    {
        var mem = new FakeSparseMemory();
        var map = new PoachMap(RepoLivingWeaponDir());
        var meta = DormantMeta();
        MakeStoreSlot(mem, 3, current: 0);   // job 95's real committed common key
        var poach = MakePoach(mem, map, meta, roll: 0);   // roll 0 -> Common

        poach.RecordPoachDeed(PoachWeaponId, new VictimSnapshot(true, 42, 95, false), slot: 0,
            delayedOrCharged: false, viaFallback: false);

        long addr = Offsets.PoachStoreBase + (3 - 1);
        Assert.Equal((byte)1, mem.U8(addr));
    }

    // ── Stage-2: LivingPoach executor via FakeSparseMemory ─────────────────────

    private const int PoachWeaponId = 999;   // an arbitrary id -- self-built meta, no items.json dependency
    private const int TestJobId = 94;        // an arbitrary fabricated map entry -- the fixture json below keys on it
    private const int TestCommonKey = 1;
    private const int TestRareKey = 2;
    private const int TestVictimJob = TestJobId;

    private static Dictionary<int, WeaponMeta> DormantMeta(int formula = 45) => new()
    {
        [PoachWeaponId] = new WeaponMeta { Name = "Test Blade", Wp = 10, Cat = "Sword", Formula = formula }
    };

    private string MakePoachJson(int jobId, int commonKey, string commonName, int rareKey, string rareName)
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "poach.json"),
            "{\"jobs\":{\"" + jobId + "\":{\"common\":{\"key\":" + commonKey + ",\"name\":\"" + commonName + "\"},"
            + "\"rare\":{\"key\":" + rareKey + ",\"name\":\"" + rareName + "\"}}}}");
        return dir;
    }

    private static void MakeStoreSlot(FakeSparseMemory mem, int key, byte current = 0)
    {
        long addr = Offsets.PoachStoreBase + (key - 1);
        mem.ReadableAddrs.Add(addr);
        mem.WritableAddrs.Add(addr);
        mem.U8s[addr] = current;
    }

    private static LivingPoach MakePoach(FakeSparseMemory mem, PoachMap map, Dictionary<int, WeaponMeta> meta,
        bool killerHasPoach = true, bool wasBasicAttack = true, int roll = 0, BannerToast? toast = null)
    {
        toast ??= new BannerToast(meta, new Dictionary<int, int>(), enabled: true);
        // wasBasicAttack takes weaponId (Func<int, bool>, matching killerHasPoach's shape) since
        // LW-167 stage 4 armed it to a per-weapon discriminator (LivingPoach.ReadWasBasicAttack);
        // this helper still injects the boolean directly so every executor-only test below stays
        // isolated from live memory fixtures.
        return new LivingPoach(meta, map, mem, toast, _ => killerHasPoach, _ => wasBasicAttack, () => roll);
    }

    [Fact]
    public void RecordPoachDeed_eligible_increments_the_store_by_exactly_one()
    {
        var mem = new FakeSparseMemory();
        var map = new PoachMap(MakePoachJson(TestJobId, TestCommonKey, "Chocobo Carcass", TestRareKey, "Chocobo Carcass<Icon=103>"));
        var meta = DormantMeta();
        MakeStoreSlot(mem, TestCommonKey, current: 5);
        var poach = MakePoach(mem, map, meta, roll: 0);   // roll 0 -> Common

        poach.RecordPoachDeed(PoachWeaponId, new VictimSnapshot(true, 42, TestVictimJob, false), slot: 0,
            delayedOrCharged: false, viaFallback: false);

        long addr = Offsets.PoachStoreBase + (TestCommonKey - 1);
        Assert.True(mem.Written.ContainsKey(addr));
        Assert.Equal((byte)6, mem.U8(addr));
    }

    [Fact]
    public void RecordPoachDeed_store_at_cap_255_refuses_the_write()
    {
        var mem = new FakeSparseMemory();
        var map = new PoachMap(MakePoachJson(TestJobId, TestCommonKey, "Chocobo Carcass", TestRareKey, "x"));
        var meta = DormantMeta();
        MakeStoreSlot(mem, TestCommonKey, current: 255);
        var poach = MakePoach(mem, map, meta, roll: 0);

        poach.RecordPoachDeed(PoachWeaponId, new VictimSnapshot(true, 42, TestVictimJob, false), slot: 0, false, false);

        long addr = Offsets.PoachStoreBase + (TestCommonKey - 1);
        Assert.False(mem.Written.ContainsKey(addr));
        Assert.Equal((byte)255, mem.U8(addr));
    }

    [Fact]
    public void RecordPoachDeed_unwritable_address_no_write_no_throw()
    {
        var mem = new FakeSparseMemory();
        var map = new PoachMap(MakePoachJson(TestJobId, TestCommonKey, "Chocobo Carcass", TestRareKey, "x"));
        var meta = DormantMeta();
        long addr = Offsets.PoachStoreBase + (TestCommonKey - 1);
        mem.ReadableAddrs.Add(addr);
        mem.U8s[addr] = 3;   // readable, but NOT marked writable
        var poach = MakePoach(mem, map, meta, roll: 0);

        var ex = Record.Exception(() =>
            poach.RecordPoachDeed(PoachWeaponId, new VictimSnapshot(true, 42, TestVictimJob, false), slot: 0, false, false));

        Assert.Null(ex);
        Assert.False(mem.Written.ContainsKey(addr));
    }

    [Fact]
    public void RecordPoachDeed_dual_credit_same_corpse_writes_exactly_once()
    {
        var mem = new FakeSparseMemory();
        var map = new PoachMap(MakePoachJson(TestJobId, TestCommonKey, "Chocobo Carcass", TestRareKey, "x"));
        var meta = new Dictionary<int, WeaponMeta>
        {
            [PoachWeaponId] = new WeaponMeta { Name = "Blade A", Formula = 45 },
            [PoachWeaponId + 1] = new WeaponMeta { Name = "Blade B", Formula = 46 },
        };
        MakeStoreSlot(mem, TestCommonKey, current: 0);
        var poach = MakePoach(mem, map, meta, roll: 0);
        var victim = new VictimSnapshot(true, 42, TestVictimJob, false);

        // Dual-wield credit: KillTracker's CreditKill loop calls the sink once PER credited
        // weapon, same slot+nameId both times.
        poach.RecordPoachDeed(PoachWeaponId, victim, slot: 7, false, false);
        poach.RecordPoachDeed(PoachWeaponId + 1, victim, slot: 7, false, false);

        long addr = Offsets.PoachStoreBase + (TestCommonKey - 1);
        Assert.Equal((byte)1, mem.U8(addr));
        Assert.Equal(1, mem.WriteOrder.FindAll(a => a == addr).Count);
    }

    [Fact]
    public void RecordPoachDeed_battle_reset_clears_the_latch_same_corpse_can_poach_again()
    {
        var mem = new FakeSparseMemory();
        var map = new PoachMap(MakePoachJson(TestJobId, TestCommonKey, "Chocobo Carcass", TestRareKey, "x"));
        var meta = DormantMeta();
        MakeStoreSlot(mem, TestCommonKey, current: 0);
        var poach = MakePoach(mem, map, meta, roll: 0);
        var victim = new VictimSnapshot(true, 42, TestVictimJob, false);
        long addr = Offsets.PoachStoreBase + (TestCommonKey - 1);

        poach.RecordPoachDeed(PoachWeaponId, victim, slot: 0, false, false);
        poach.RecordPoachDeed(PoachWeaponId, victim, slot: 0, false, false);   // same battle: deduped
        Assert.Equal((byte)1, mem.U8(addr));

        poach.ResetBattle();
        poach.RecordPoachDeed(PoachWeaponId, victim, slot: 0, false, false);   // next battle: fresh latch

        Assert.Equal((byte)2, mem.U8(addr));
    }

    [Fact]
    public void RecordPoachDeed_enqueues_toast_once_with_key_5_and_stripped_name()
    {
        var mem = new FakeSparseMemory();
        var map = new PoachMap(MakePoachJson(TestJobId, TestCommonKey, "Chocobo Carcass", TestRareKey, "Chocobo Carcass<Icon=103>"));
        var meta = DormantMeta();
        MakeStoreSlot(mem, TestRareKey, current: 0);
        var toast = new BannerToast(meta, new Dictionary<int, int>(), enabled: true);
        var poach = new LivingPoach(meta, map, mem, toast, _ => true, _ => true, () => 255);   // roll 255 -> Rare

        poach.RecordPoachDeed(PoachWeaponId, new VictimSnapshot(true, 42, TestVictimJob, false), slot: 0, false, false);

        var queued = Assert.Single(toast._queue);
        Assert.Equal(PoachWeaponId, queued.weaponId);
        Assert.Equal(Tuning.PoachToastKey, queued.tier);
        Assert.Equal("Chocobo Carcass", queued.payload);   // markup stripped
    }

    [Fact]
    public void PoachToastKey_never_collides_with_tiers_bulwark_or_milestones()
    {
        Assert.Equal(5, Tuning.PoachToastKey);
        Assert.DoesNotContain(Tuning.PoachToastKey, new[] { 1, 2, 3, Tuning.BulwarkToastKey });
        Assert.True(Tuning.PoachToastKey > 0);   // never collides with a negated milestone key
    }

    [Fact]
    public void RecordPoachDeed_vanillaFormula_weapon_never_writes()
    {
        var mem = new FakeSparseMemory();
        var map = new PoachMap(MakePoachJson(TestJobId, TestCommonKey, "Chocobo Carcass", TestRareKey, "x"));
        var meta = DormantMeta(formula: 1);   // vanilla-readable formula -- NOT in Tuning.DormantPoachFormulas
        MakeStoreSlot(mem, TestCommonKey, current: 0);
        var poach = MakePoach(mem, map, meta, roll: 0);

        poach.RecordPoachDeed(PoachWeaponId, new VictimSnapshot(true, 42, TestVictimJob, false), slot: 0, false, false);

        Assert.Empty(mem.Written);
    }

    [Fact]
    public void RecordPoachDeed_wasBasicAttack_false_never_writes()
    {
        // Proves the EXECUTOR itself honors whatever wasBasicAttack(weaponId) reports (not merely
        // the pure policy) by injecting false directly, independent of the stage-4 discriminator's
        // own live-memory read (covered separately below by ReadWasBasicAttack's own unit tests
        // and the end-to-end armed-wiring test).
        var mem = new FakeSparseMemory();
        var map = new PoachMap(MakePoachJson(TestJobId, TestCommonKey, "Chocobo Carcass", TestRareKey, "x"));
        var meta = DormantMeta();
        MakeStoreSlot(mem, TestCommonKey, current: 0);
        var poach = MakePoach(mem, map, meta, wasBasicAttack: false, roll: 0);

        poach.RecordPoachDeed(PoachWeaponId, new VictimSnapshot(true, 42, TestVictimJob, false), slot: 0, false, false);

        Assert.Empty(mem.Written);
    }

    // ── Stage-3: corpse despawn integration (LW-167 stage 3, CorpseDespawn.cs) ────
    // Hand-seeded (not MemSeats: MemSeats never marks Readable, and CorpseDespawn's every read
    // is guard-gated) -- mirrors CorpseDespawnTests.cs's fixture shape exactly.

    private const int DespawnBandSlot = 10;
    private const long DespawnNodeAddr = 0x2000_3000;

    private static long DespawnFrame => Offsets.FrameReadBase + (long)DespawnBandSlot * Offsets.CombatStride;

    /// <summary>A live, freshly-dead, unconverted corpse at DespawnBandSlot with
    /// <paramref name="nameId"/>, plus a single render node whose combat back-pointer resolves to
    /// it -- every CorpseDespawn precondition green by default. Callers override individual
    /// fields/omit the node to exercise one refusal.</summary>
    private static void SeedDespawnableCorpse(FakeSparseMemory mem, ushort nameId, bool turnOpen = false)
    {
        long entry = Band.Entry(DespawnBandSlot);
        mem.U8s[entry + Offsets.ADeadStatus] = Offsets.ADeadBit;
        mem.MarkReadable(entry + Offsets.ADeadStatus, 1);
        mem.U16s[entry + Offsets.ANameId] = nameId;
        mem.MarkReadable(entry + Offsets.ANameId, 2);
        mem.U8s[entry + Offsets.ACorpseConvertMarker] = 0;
        mem.MarkReadable(entry + Offsets.ACorpseConvertMarker, 1);
        mem.U8s[entry + Offsets.ATurnFlag] = (byte)(turnOpen ? 1 : 0);
        mem.MarkReadable(entry + Offsets.ATurnFlag, 1);

        mem.SeedU64(Offsets.DespawnNodeListHead, (ulong)DespawnNodeAddr);
        mem.MarkReadable(Offsets.DespawnNodeListHead, 8);
        mem.SeedU64(DespawnNodeAddr, 0);   // next pointer: end of list
        mem.U8s[DespawnNodeAddr + Offsets.DespawnNodeIdOff] = 3;
        mem.SeedU64(DespawnNodeAddr + Offsets.DespawnNodeCombatOff, (ulong)DespawnFrame);
        mem.MarkReadable(DespawnNodeAddr, Offsets.DespawnNodeCombatOff + 8);
        mem.MarkWritable(DespawnNodeAddr + Offsets.DespawnNodeModeOff, 1);
        // node flag byte 0 -- BYTE-wide, matching the Proven row's own access to +0x12C. Already
        // covered readable by the node-prefix MarkReadable call above (0x12C < CombatOff+8).
        mem.U8s[DespawnNodeAddr + Offsets.DespawnNodeModeOff] = 0;

        // "not the current actor" -- any id other than the node's own 3.
        mem.U16s[Offsets.DespawnCurrentActorNodeId] = 0xFFFF;
        mem.U16s[Offsets.DespawnCurrentActorNodeId + 2] = 0xFFFF;
        mem.MarkReadable(Offsets.DespawnCurrentActorNodeId, 4);
    }

    [Fact]
    public void RecordPoachDeed_eligible_poach_also_despawns_the_corpse()
    {
        var mem = new FakeSparseMemory();
        var map = new PoachMap(MakePoachJson(TestJobId, TestCommonKey, "Chocobo Carcass", TestRareKey, "x"));
        var meta = DormantMeta();
        MakeStoreSlot(mem, TestCommonKey, current: 0);
        const ushort nameId = 42;
        SeedDespawnableCorpse(mem, nameId);
        var poach = MakePoach(mem, map, meta, roll: 0);

        poach.RecordPoachDeed(PoachWeaponId, new VictimSnapshot(true, nameId, TestVictimJob, false), slot: DespawnBandSlot, false, false);

        long storeAddr = Offsets.PoachStoreBase + (TestCommonKey - 1);
        Assert.Equal((byte)1, mem.U8(storeAddr));   // the poach itself still landed
        // Both the store write and the despawn write are W8 now (byte-wide), so they share
        // mem.Written -- assert the despawn write specifically rather than the dict's size.
        Assert.True(mem.Written.ContainsKey(DespawnNodeAddr + Offsets.DespawnNodeModeOff));
        Assert.Equal((byte)0x20, mem.Written[DespawnNodeAddr + Offsets.DespawnNodeModeOff]);
    }

    // LW-172: turnOpen refuses TRANSIENTLY (the corpse is still confirmed dead and ours -- only the
    // open-turn guard stands in the way), so this is no longer a permanent one-shot miss: the
    // immediate-call assertions below are UNCHANGED (querying the queue takes no memory action of
    // its own, so nothing here weakens), plus a new assertion proves the corpse is now retried
    // rather than abandoned -- strictly more coverage, not less.
    [Fact]
    public void RecordPoachDeed_despawn_refusal_does_not_roll_back_the_store_write_and_queues_for_retry()
    {
        var mem = new FakeSparseMemory();
        var map = new PoachMap(MakePoachJson(TestJobId, TestCommonKey, "Chocobo Carcass", TestRareKey, "x"));
        var meta = DormantMeta();
        MakeStoreSlot(mem, TestCommonKey, current: 0);
        const ushort nameId = 42;
        SeedDespawnableCorpse(mem, nameId, turnOpen: true);   // the despawn's own guard refuses -- TRANSIENT
        var poach = MakePoach(mem, map, meta, roll: 0);

        poach.RecordPoachDeed(PoachWeaponId, new VictimSnapshot(true, nameId, TestVictimJob, false), slot: DespawnBandSlot, false, false);

        long storeAddr = Offsets.PoachStoreBase + (TestCommonKey - 1);
        Assert.Equal((byte)1, mem.U8(storeAddr));   // the carcass still stands (its own W8 write)
        Assert.False(mem.Written.ContainsKey(DespawnNodeAddr + Offsets.DespawnNodeModeOff));   // but nothing despawned yet

        // The corpse is queued, not abandoned: still transiently refused, it stays pending across
        // a Tick rather than silently dropping (a Permanent refusal, proven separately below,
        // WOULD drop after one Tick).
        poach.Tick();
        Assert.False(mem.Written.ContainsKey(DespawnNodeAddr + Offsets.DespawnNodeModeOff));   // still pending, still not despawned
    }

    // ── Stage-2b: the killer's real Poach-bit read (combat support bitfield) ───

    [Fact]
    public void ReadKillerHasPoach_true_when_the_deployed_wielders_support_bit_is_set()
    {
        var mem = new FakeSparseMemory();
        const int weaponId = 71;
        MemSeats.SeatRoster(mem, 0, lvl: 30, br: 50, fa: 50, rh: weaponId);
        MemSeats.SeatBand(mem, 24, weapon: weaponId, lvl: 30, br: 50, fa: 50, gx: 5, gy: 5);
        long entry = Band.Entry(24);
        Signatures.SupportBit(Tuning.PoachSupportAbilityId, out int off, out byte mask);
        long addr = entry + Offsets.ASupport + off;
        mem.ReadableAddrs.Add(addr);
        mem.U8s[addr] = mask;

        Assert.True(LivingPoach.ReadKillerHasPoach(mem, weaponId));
    }

    [Fact]
    public void ReadKillerHasPoach_false_when_the_bit_is_clear()
    {
        var mem = new FakeSparseMemory();
        const int weaponId = 71;
        MemSeats.SeatRoster(mem, 0, lvl: 30, br: 50, fa: 50, rh: weaponId);
        MemSeats.SeatBand(mem, 24, weapon: weaponId, lvl: 30, br: 50, fa: 50, gx: 5, gy: 5);
        long entry = Band.Entry(24);
        Signatures.SupportBit(Tuning.PoachSupportAbilityId, out int off, out byte mask);
        long addr = entry + Offsets.ASupport + off;
        mem.ReadableAddrs.Add(addr);
        mem.U8s[addr] = 0;

        Assert.False(LivingPoach.ReadKillerHasPoach(mem, weaponId));
    }

    [Fact]
    public void ReadKillerHasPoach_false_when_the_wielder_cannot_be_located()
    {
        var mem = new FakeSparseMemory();   // nobody deployed at all
        Assert.False(LivingPoach.ReadKillerHasPoach(mem, weaponId: 71));
    }

    // ── Stage-2c: KillTracker's widened deed seam (CreditKill -> RecordPoachDeed) ──

    private const int WilhamSlot = Offsets.SlotsBack;   // band slot 20, player-side actor

    private sealed class CapturingDeedSink : IDeedSink
    {
        public readonly List<(int weaponId, VictimSnapshot victim)> Deeds = new();
        public readonly List<int> Misses = new();
        public readonly List<(int weaponId, VictimSnapshot victim, int slot, bool delayedOrCharged, bool viaFallback)> PoachCalls = new();
        public void RecordDeed(int weaponId, in VictimSnapshot victim) => Deeds.Add((weaponId, victim));
        public void DeedMiss(int slot) => Misses.Add(slot);
        public void RecordPoachDeed(int weaponId, in VictimSnapshot victim, int slot, bool delayedOrCharged, bool viaFallback)
            => PoachCalls.Add((weaponId, victim, slot, delayedOrCharged, viaFallback));
    }

    [Fact]
    public void CreditKill_reports_to_RecordPoachDeed_alongside_RecordDeed()
    {
        var kills = new Dictionary<int, int>();
        var m = new FakeSparseMemory();
        var sink = new CapturingDeedSink();
        var weapons = new HashSet<int> { 52 };
        SetRoster(m, slot: 3, level: 99, brave: 89, faith: 76, weapon: 52);
        SetUnit(m, WilhamSlot, hp: 352, maxHp: 352, level: 99, brave: 89, faith: 76);
        SetActive(m, hp: 352, maxHp: 352, level: 99);
        SetEnemy(m, slot: 0, hp: 300);
        BandFixtures.SeedVictimFields(m, slot: 0, nameId: 918, job: 99, undead: false);
        var t = new KillTracker(kills, m, weapons, recorder: null, deeds: sink);

        Settle(t);
        SetUnit(m, slot: 0, hp: 0);
        Settle(t, 3);

        // Whether THIS particular fixture resolves via the acted-latch or the turn-queue fallback
        // is KillTracker's own well-tested attribution call (KillTrackerBattleCountersTests.cs
        // etc.) -- not what this test is proving. What matters here is that CreditKill's widened
        // seam reports the SAME weaponId/slot RecordDeed got, and that the delayed-action flag
        // (no Jump/charge was ever armed in this fixture) reads false.
        var call = Assert.Single(sink.PoachCalls);
        Assert.Equal(52, call.weaponId);
        Assert.Equal(0, call.slot);
        Assert.False(call.delayedOrCharged);
    }

    [Fact]
    public void DeedFanout_forwards_RecordDeed_and_DeedMiss_to_inner_and_RecordPoachDeed_to_LivingPoach()
    {
        var mem = new FakeSparseMemory();
        var map = new PoachMap(MakePoachJson(TestJobId, TestCommonKey, "Chocobo Carcass", TestRareKey, "x"));
        var meta = DormantMeta();
        MakeStoreSlot(mem, TestCommonKey, current: 0);
        var poach = MakePoach(mem, map, meta, roll: 0);
        var inner = new CapturingDeedSink();
        var fan = new DeedFanout(inner, poach);
        var victim = new VictimSnapshot(true, 1, TestVictimJob, false);

        fan.RecordDeed(9, in victim);
        fan.DeedMiss(3);
        fan.RecordPoachDeed(PoachWeaponId, in victim, slot: 0, delayedOrCharged: false, viaFallback: false);

        Assert.Single(inner.Deeds);
        Assert.Equal(9, inner.Deeds[0].weaponId);
        Assert.Single(inner.Misses);
        Assert.Equal(3, inner.Misses[0]);
        Assert.Empty(inner.PoachCalls);   // RecordPoachDeed never reaches the inner (Reliquary-side) sink
        Assert.Equal((byte)1, mem.U8(Offsets.PoachStoreBase + (TestCommonKey - 1)));   // it reached LivingPoach instead
    }

    // ── Stage-4: the real basic-Attack discriminator + the ARMED end-to-end wiring ─────
    //
    // LivingPoach.ReadWasBasicAttack (LIVE_LEDGER "The basic-Attack discriminator (LW-167 stage
    // 4)" row, 2026-08-12): resolves the credited weapon's KILLER via the same lane
    // ReadKillerHasPoach uses, then reads that wielder's own AArec action record. kind==5
    // (performing) + abil==0 means a confirmed basic Attack; anything else fails closed to false.

    private const int DiscBandSlot = 24;

    private static long SeedDiscriminatorKiller(FakeSparseMemory mem, int weaponId)
    {
        MemSeats.SeatRoster(mem, 0, lvl: 30, br: 50, fa: 50, rh: weaponId);
        MemSeats.SeatBand(mem, DiscBandSlot, weapon: weaponId, lvl: 30, br: 50, fa: 50, gx: 5, gy: 5);
        return Band.Entry(DiscBandSlot);
    }

    [Fact]
    public void ReadWasBasicAttack_true_when_the_killers_record_is_kind5_performing_abil0()
    {
        var mem = new FakeSparseMemory();
        const int weaponId = 71;
        long entry = SeedDiscriminatorKiller(mem, weaponId);
        long rec = entry + Offsets.AArec;
        mem.MarkReadable(rec + Offsets.ArecKind, 1);
        mem.U8s[rec + Offsets.ArecKind] = Offsets.ArecKindPerforming;
        mem.MarkReadable(rec + Offsets.ArecAbil, 2);
        mem.U16s[rec + Offsets.ArecAbil] = 0;

        Assert.True(LivingPoach.ReadWasBasicAttack(mem, weaponId));
    }

    [Fact]
    public void ReadWasBasicAttack_false_when_the_record_names_an_ability()
    {
        var mem = new FakeSparseMemory();
        const int weaponId = 71;
        long entry = SeedDiscriminatorKiller(mem, weaponId);
        long rec = entry + Offsets.AArec;
        mem.MarkReadable(rec + Offsets.ArecKind, 1);
        mem.U8s[rec + Offsets.ArecKind] = Offsets.ArecKindPerforming;
        mem.MarkReadable(rec + Offsets.ArecAbil, 2);
        mem.U16s[rec + Offsets.ArecAbil] = 141;   // Rend Weapon -- the owner-observed live counter-example

        Assert.False(LivingPoach.ReadWasBasicAttack(mem, weaponId));
    }

    [Fact]
    public void ReadWasBasicAttack_false_when_kind_is_receiving_not_performing()
    {
        var mem = new FakeSparseMemory();
        const int weaponId = 71;
        long entry = SeedDiscriminatorKiller(mem, weaponId);
        long rec = entry + Offsets.AArec;
        mem.MarkReadable(rec + Offsets.ArecKind, 1);
        mem.U8s[rec + Offsets.ArecKind] = 6;   // receiving: a struck unit's stale stamp, not the killer's own
        mem.MarkReadable(rec + Offsets.ArecAbil, 2);
        mem.U16s[rec + Offsets.ArecAbil] = 0;

        Assert.False(LivingPoach.ReadWasBasicAttack(mem, weaponId));
    }

    [Fact]
    public void ReadWasBasicAttack_false_when_the_action_record_is_unreadable()
    {
        var mem = new FakeSparseMemory();
        const int weaponId = 71;
        SeedDiscriminatorKiller(mem, weaponId);   // AArec bytes deliberately never marked Readable

        Assert.False(LivingPoach.ReadWasBasicAttack(mem, weaponId));
    }

    [Fact]
    public void ReadWasBasicAttack_false_when_the_killer_cannot_be_located()
    {
        var mem = new FakeSparseMemory();   // nobody deployed at all
        Assert.False(LivingPoach.ReadWasBasicAttack(mem, weaponId: 71));
    }

    /// <summary>Wires the SAME delegate shape Engine.cs uses (both LivingPoach static readers over
    /// live memory, roll pinned to Common so the store address under test is deterministic) --
    /// the ARMED production wiring, not an injected stand-in.</summary>
    private static LivingPoach MakeArmedPoach(FakeSparseMemory mem, PoachMap map, Dictionary<int, WeaponMeta> meta)
    {
        var toast = new BannerToast(meta, new Dictionary<int, int>(), enabled: true);
        return new LivingPoach(meta, map, mem, toast,
            killerHasPoach: id => LivingPoach.ReadKillerHasPoach(mem, id),
            wasBasicAttack: id => LivingPoach.ReadWasBasicAttack(mem, id),
            roll: () => 0);
    }

    private static void SeedArmedKiller(FakeSparseMemory mem, int weaponId, ushort abil)
    {
        long entry = SeedDiscriminatorKiller(mem, weaponId);
        Signatures.SupportBit(Tuning.PoachSupportAbilityId, out int off, out byte mask);
        mem.ReadableAddrs.Add(entry + Offsets.ASupport + off);
        mem.U8s[entry + Offsets.ASupport + off] = mask;   // killerHasPoach true
        long rec = entry + Offsets.AArec;
        mem.MarkReadable(rec + Offsets.ArecKind, 1);
        mem.U8s[rec + Offsets.ArecKind] = Offsets.ArecKindPerforming;
        mem.MarkReadable(rec + Offsets.ArecAbil, 2);
        mem.U16s[rec + Offsets.ArecAbil] = abil;
    }

    // KEYSTONE POSITIVE: the first reachable-armed happy path. Confirmed RED against the disarmed
    // wiring (wasBasicAttack forced to a constant false, mirroring the pre-stage-4 Engine.cs) before
    // switching MakeArmedPoach's wasBasicAttack delegate to the real ReadWasBasicAttack -- with the
    // constant false the store stayed at 0 (mem.Written empty); with the real discriminator wired
    // in, unchanged otherwise, it writes. This is what proves the discriminator (not some other
    // gate) is what flips the result.
    [Fact]
    public void RecordPoachDeed_ARMED_fully_eligible_credit_with_attack_stamp_writes_the_store()
    {
        var mem = new FakeSparseMemory();
        var map = new PoachMap(MakePoachJson(TestJobId, TestCommonKey, "Chocobo Carcass", TestRareKey, "x"));
        var meta = DormantMeta();
        MakeStoreSlot(mem, TestCommonKey, current: 0);
        SeedArmedKiller(mem, PoachWeaponId, abil: 0);   // confirmed basic Attack
        var poach = MakeArmedPoach(mem, map, meta);

        poach.RecordPoachDeed(PoachWeaponId, new VictimSnapshot(true, 42, TestVictimJob, false), slot: 0,
            delayedOrCharged: false, viaFallback: false);

        long storeAddr = Offsets.PoachStoreBase + (TestCommonKey - 1);
        Assert.Equal((byte)1, mem.U8(storeAddr));
    }

    [Fact]
    public void RecordPoachDeed_ARMED_ability_stamp_killer_record_never_writes()
    {
        var mem = new FakeSparseMemory();
        var map = new PoachMap(MakePoachJson(TestJobId, TestCommonKey, "Chocobo Carcass", TestRareKey, "x"));
        var meta = DormantMeta();
        MakeStoreSlot(mem, TestCommonKey, current: 0);
        SeedArmedKiller(mem, PoachWeaponId, abil: 141);   // Rend Weapon: a real ability, not the basic Attack
        var poach = MakeArmedPoach(mem, map, meta);

        poach.RecordPoachDeed(PoachWeaponId, new VictimSnapshot(true, 42, TestVictimJob, false), slot: 0,
            delayedOrCharged: false, viaFallback: false);

        Assert.Empty(mem.Written);
    }

    // Kill-shape refusals (deliverable 2): override the discriminator outright. killerHasPoach and
    // wasBasicAttack both inject true (MakePoach's defaults) so a failure here can only be the
    // viaFallback/delayedOrCharged guard, never a starved upstream gate.

    [Fact]
    public void RecordPoachDeed_viaFallback_true_never_writes_even_with_every_other_gate_green()
    {
        var mem = new FakeSparseMemory();
        var map = new PoachMap(MakePoachJson(TestJobId, TestCommonKey, "Chocobo Carcass", TestRareKey, "x"));
        var meta = DormantMeta();
        MakeStoreSlot(mem, TestCommonKey, current: 0);
        var poach = MakePoach(mem, map, meta, roll: 0);

        poach.RecordPoachDeed(PoachWeaponId, new VictimSnapshot(true, 42, TestVictimJob, false), slot: 0,
            delayedOrCharged: false, viaFallback: true);

        Assert.Empty(mem.Written);
    }

    [Fact]
    public void RecordPoachDeed_delayedOrCharged_true_never_writes_even_with_every_other_gate_green()
    {
        var mem = new FakeSparseMemory();
        var map = new PoachMap(MakePoachJson(TestJobId, TestCommonKey, "Chocobo Carcass", TestRareKey, "x"));
        var meta = DormantMeta();
        MakeStoreSlot(mem, TestCommonKey, current: 0);
        var poach = MakePoach(mem, map, meta, roll: 0);

        poach.RecordPoachDeed(PoachWeaponId, new VictimSnapshot(true, 42, TestVictimJob, false), slot: 0,
            delayedOrCharged: true, viaFallback: false);

        Assert.Empty(mem.Written);
    }

    // ── Stage-5: LW-172 the despawn retry lifecycle (LivingPoach.Despawn.cs) ───────────────────
    // Owner design decision (live pass, 2026-08-12): a vanilla-poached corpse yields NEITHER
    // crystal nor chest, so a TRANSIENT despawn refusal must retry instead of standing as a
    // one-shot miss (a corpse left standing could crystallize on its own turn, handing out BOTH
    // the carcass and a crystal). These fixtures reuse Stage-3's SeedDespawnableCorpse (band slot
    // DespawnBandSlot, node DespawnNodeAddr, node id 3) and drive the TRANSIENT case via the
    // current-actor guard (SetCurrentActorNodeId) since it is trivial to both trigger and clear.

    /// <summary>Overwrite the current-actor node-id global SeedDespawnableCorpse already marked
    /// Readable. Setting it to the corpse's own node id (3) makes CorpseDespawn's current-actor
    /// guard refuse TRANSIENTLY (Band slot {DespawnBandSlot}'s node is the acting unit); any other
    /// value clears the guard.</summary>
    private static void SetCurrentActorNodeId(FakeSparseMemory mem, byte nodeId)
    {
        mem.U16s[Offsets.DespawnCurrentActorNodeId] = nodeId;
        mem.U16s[Offsets.DespawnCurrentActorNodeId + 2] = 0;
    }

    private static long DespawnCrystalAddr => Band.Entry(DespawnBandSlot) + Offsets.ACrystalHearts;
    private static long DespawnModeAddr => DespawnNodeAddr + Offsets.DespawnNodeModeOff;

    [Fact]
    public void Tick_transient_refusal_stays_pending_and_despawns_once_the_guard_clears_writing_exactly_once()
    {
        var mem = new FakeSparseMemory();
        var map = new PoachMap(MakePoachJson(TestJobId, TestCommonKey, "Chocobo Carcass", TestRareKey, "x"));
        var meta = DormantMeta();
        MakeStoreSlot(mem, TestCommonKey, current: 0);
        const ushort nameId = 42;
        SeedDespawnableCorpse(mem, nameId);
        SetCurrentActorNodeId(mem, 3);   // matches the corpse's own node -- TRANSIENT (never remove the acting unit)
        var poach = MakePoach(mem, map, meta, roll: 0);

        poach.RecordPoachDeed(PoachWeaponId, new VictimSnapshot(true, nameId, TestVictimJob, false), slot: DespawnBandSlot, false, false);
        Assert.False(mem.Written.ContainsKey(DespawnModeAddr));

        poach.Tick();
        poach.Tick();
        Assert.False(mem.Written.ContainsKey(DespawnModeAddr));   // still pending

        SetCurrentActorNodeId(mem, 0xFF);   // the guard clears -- no longer the current actor
        poach.Tick();

        Assert.True(mem.Written.ContainsKey(DespawnModeAddr));
        Assert.Equal((byte)0x20, mem.Written[DespawnModeAddr]);
        Assert.Equal(1, mem.WriteOrder.FindAll(a => a == DespawnModeAddr).Count);   // the write landed exactly once overall

        // Success drops the corpse from the pending set: a further Tick touches nothing more.
        poach.Tick();
        Assert.Equal(1, mem.WriteOrder.FindAll(a => a == DespawnModeAddr).Count);
    }

    [Fact]
    public void Tick_pins_the_crystal_counter_to_3_each_pending_tick_and_stops_after_success()
    {
        var mem = new FakeSparseMemory();
        var map = new PoachMap(MakePoachJson(TestJobId, TestCommonKey, "Chocobo Carcass", TestRareKey, "x"));
        var meta = DormantMeta();
        MakeStoreSlot(mem, TestCommonKey, current: 0);
        const ushort nameId = 42;
        SeedDespawnableCorpse(mem, nameId);
        SetCurrentActorNodeId(mem, 3);
        mem.MarkWritable(DespawnCrystalAddr, 1);
        var poach = MakePoach(mem, map, meta, roll: 0);

        poach.RecordPoachDeed(PoachWeaponId, new VictimSnapshot(true, nameId, TestVictimJob, false), slot: DespawnBandSlot, false, false);

        poach.Tick();
        Assert.Equal((byte)3, mem.U8(DespawnCrystalAddr));
        poach.Tick();
        Assert.Equal(2, mem.WriteOrder.FindAll(a => a == DespawnCrystalAddr).Count);

        SetCurrentActorNodeId(mem, 0xFF);   // clears the guard -- this Tick despawns instead of pinning
        poach.Tick();
        Assert.Equal(2, mem.WriteOrder.FindAll(a => a == DespawnCrystalAddr).Count);   // no pin on the success tick

        poach.Tick();   // dropped from pending -- nothing more happens
        Assert.Equal(2, mem.WriteOrder.FindAll(a => a == DespawnCrystalAddr).Count);
    }

    [Fact]
    public void Tick_pin_write_is_guarded_unwritable_counter_no_op_no_throw()
    {
        var mem = new FakeSparseMemory();
        var map = new PoachMap(MakePoachJson(TestJobId, TestCommonKey, "Chocobo Carcass", TestRareKey, "x"));
        var meta = DormantMeta();
        MakeStoreSlot(mem, TestCommonKey, current: 0);
        const ushort nameId = 42;
        SeedDespawnableCorpse(mem, nameId);
        SetCurrentActorNodeId(mem, 3);
        // DespawnCrystalAddr deliberately left NOT writable.
        var poach = MakePoach(mem, map, meta, roll: 0);
        poach.RecordPoachDeed(PoachWeaponId, new VictimSnapshot(true, nameId, TestVictimJob, false), slot: DespawnBandSlot, false, false);

        var ex = Record.Exception(() => poach.Tick());

        Assert.Null(ex);
        Assert.False(mem.Written.ContainsKey(DespawnCrystalAddr));
    }

    [Theory]
    [InlineData(false, true)]    // no longer Dead -- alive again
    [InlineData(true, false)]    // nameId mismatch -- slot reused by a different unit
    public void Tick_permanent_staleness_drops_pending_corpse_with_no_write_and_no_pin(bool stillDead, bool nameIdMatches)
    {
        var mem = new FakeSparseMemory();
        var map = new PoachMap(MakePoachJson(TestJobId, TestCommonKey, "Chocobo Carcass", TestRareKey, "x"));
        var meta = DormantMeta();
        MakeStoreSlot(mem, TestCommonKey, current: 0);
        const ushort nameId = 42;
        SeedDespawnableCorpse(mem, nameId);
        SetCurrentActorNodeId(mem, 3);   // queue it via a transient refusal first
        mem.MarkWritable(DespawnCrystalAddr, 1);
        var poach = MakePoach(mem, map, meta, roll: 0);
        poach.RecordPoachDeed(PoachWeaponId, new VictimSnapshot(true, nameId, TestVictimJob, false), slot: DespawnBandSlot, false, false);

        // The corpse turns permanently stale before the next tick.
        long entry = Band.Entry(DespawnBandSlot);
        if (!stillDead) mem.U8s[entry + Offsets.ADeadStatus] = 0;
        if (!nameIdMatches) mem.U16s[entry + Offsets.ANameId] = (ushort)(nameId + 1);

        poach.Tick();

        Assert.False(mem.Written.ContainsKey(DespawnCrystalAddr));   // no pin
        Assert.False(mem.Written.ContainsKey(DespawnModeAddr));      // no despawn write

        // Dropped, not merely stalled: clearing the current-actor guard afterward proves nothing
        // is retried anymore (a still-pending corpse WOULD despawn here, per the test above).
        SetCurrentActorNodeId(mem, 0xFF);
        poach.Tick();
        Assert.False(mem.Written.ContainsKey(DespawnModeAddr));
    }

    [Fact]
    public void Tick_permanent_chestBit_drops_pending_corpse_with_no_write_and_no_pin()
    {
        var mem = new FakeSparseMemory();
        var map = new PoachMap(MakePoachJson(TestJobId, TestCommonKey, "Chocobo Carcass", TestRareKey, "x"));
        var meta = DormantMeta();
        MakeStoreSlot(mem, TestCommonKey, current: 0);
        const ushort nameId = 42;
        SeedDespawnableCorpse(mem, nameId);
        SetCurrentActorNodeId(mem, 3);
        mem.MarkWritable(DespawnCrystalAddr, 1);
        var poach = MakePoach(mem, map, meta, roll: 0);
        poach.RecordPoachDeed(PoachWeaponId, new VictimSnapshot(true, nameId, TestVictimJob, false), slot: DespawnBandSlot, false, false);

        long entry = Band.Entry(DespawnBandSlot);
        mem.U8s[entry + Offsets.ACorpseConvertMarker] = Offsets.ACorpseChestBitMask;   // engine's own chest conversion

        poach.Tick();

        Assert.False(mem.Written.ContainsKey(DespawnCrystalAddr));
        Assert.False(mem.Written.ContainsKey(DespawnModeAddr));
    }

    [Fact]
    public void Tick_watchdog_drops_after_the_tick_cap_with_exactly_one_warn_and_no_further_pins()
    {
        var mem = new FakeSparseMemory();
        var map = new PoachMap(MakePoachJson(TestJobId, TestCommonKey, "Chocobo Carcass", TestRareKey, "x"));
        var meta = DormantMeta();
        MakeStoreSlot(mem, TestCommonKey, current: 0);
        const ushort nameId = 42;
        SeedDespawnableCorpse(mem, nameId);
        SetCurrentActorNodeId(mem, 3);   // never clears -- the corpse can never resolve
        mem.MarkWritable(DespawnCrystalAddr, 1);
        var poach = MakePoach(mem, map, meta, roll: 0);
        poach.RecordPoachDeed(PoachWeaponId, new VictimSnapshot(true, nameId, TestVictimJob, false), slot: DespawnBandSlot, false, false);

        using var cap = LogCapture.Start(file: false);
        for (int i = 0; i < LivingPoach.PendingTickCap; i++) poach.Tick();

        Assert.Single(cap.Console, l => l.Contains("gave up"));
        int pinsDuringRetry = mem.WriteOrder.FindAll(a => a == DespawnCrystalAddr).Count;
        Assert.Equal(LivingPoach.PendingTickCap - 1, pinsDuringRetry);   // pinned every tick except the final (drop) tick
        Assert.False(mem.Written.ContainsKey(DespawnModeAddr));

        // Dropped, not merely paused: further ticks add neither pins nor warnings.
        poach.Tick();
        Assert.Equal(pinsDuringRetry, mem.WriteOrder.FindAll(a => a == DespawnCrystalAddr).Count);
        Assert.Single(cap.Console, l => l.Contains("gave up"));
    }

    [Fact]
    public void ResetBattle_clears_the_pending_despawn_queue_no_cross_battle_writes()
    {
        var mem = new FakeSparseMemory();
        var map = new PoachMap(MakePoachJson(TestJobId, TestCommonKey, "Chocobo Carcass", TestRareKey, "x"));
        var meta = DormantMeta();
        MakeStoreSlot(mem, TestCommonKey, current: 0);
        const ushort nameId = 42;
        SeedDespawnableCorpse(mem, nameId);
        SetCurrentActorNodeId(mem, 3);
        mem.MarkWritable(DespawnCrystalAddr, 1);
        var poach = MakePoach(mem, map, meta, roll: 0);
        poach.RecordPoachDeed(PoachWeaponId, new VictimSnapshot(true, nameId, TestVictimJob, false), slot: DespawnBandSlot, false, false);
        poach.Tick();
        int pinsBeforeReset = mem.WriteOrder.FindAll(a => a == DespawnCrystalAddr).Count;
        Assert.Equal(1, pinsBeforeReset);

        poach.ResetBattle();

        // The guard clearing after the reset would have let a still-pending corpse despawn; it
        // must not, because ResetBattle dropped it from the queue.
        SetCurrentActorNodeId(mem, 0xFF);
        poach.Tick();

        Assert.Equal(pinsBeforeReset, mem.WriteOrder.FindAll(a => a == DespawnCrystalAddr).Count);   // no further pin
        Assert.False(mem.Written.ContainsKey(DespawnModeAddr));   // no cross-battle despawn write either
    }

    [Fact]
    public void Tick_never_rewrites_the_store_or_requeues_the_toast_for_a_pending_corpse()
    {
        var mem = new FakeSparseMemory();
        var map = new PoachMap(MakePoachJson(TestJobId, TestCommonKey, "Chocobo Carcass", TestRareKey, "Chocobo Carcass<Icon=103>"));
        var meta = DormantMeta();
        MakeStoreSlot(mem, TestCommonKey, current: 0);
        const ushort nameId = 42;
        SeedDespawnableCorpse(mem, nameId);
        SetCurrentActorNodeId(mem, 3);
        mem.MarkWritable(DespawnCrystalAddr, 1);
        var toast = new BannerToast(meta, new Dictionary<int, int>(), enabled: true);
        var poach = new LivingPoach(meta, map, mem, toast, _ => true, _ => true, () => 0);
        long storeAddr = Offsets.PoachStoreBase + (TestCommonKey - 1);

        poach.RecordPoachDeed(PoachWeaponId, new VictimSnapshot(true, nameId, TestVictimJob, false), slot: DespawnBandSlot, false, false);
        Assert.Equal((byte)1, mem.U8(storeAddr));
        Assert.Single(toast._queue);

        poach.Tick();
        poach.Tick();
        poach.Tick();

        Assert.Equal((byte)1, mem.U8(storeAddr));   // the credit-moment write is never repeated
        Assert.Single(toast._queue);                // the toast is never requeued
    }

    [Fact]
    public void RetryLifecycle_write_set_is_exactly_the_store_write_plus_the_pin_bytes_plus_the_mode_flag_byte()
    {
        var mem = new FakeSparseMemory();
        var map = new PoachMap(MakePoachJson(TestJobId, TestCommonKey, "Chocobo Carcass", TestRareKey, "x"));
        var meta = DormantMeta();
        MakeStoreSlot(mem, TestCommonKey, current: 0);
        const ushort nameId = 42;
        SeedDespawnableCorpse(mem, nameId);
        SetCurrentActorNodeId(mem, 3);
        mem.MarkWritable(DespawnCrystalAddr, 1);
        var poach = MakePoach(mem, map, meta, roll: 0);
        long storeAddr = Offsets.PoachStoreBase + (TestCommonKey - 1);

        poach.RecordPoachDeed(PoachWeaponId, new VictimSnapshot(true, nameId, TestVictimJob, false), slot: DespawnBandSlot, false, false);
        poach.Tick();
        poach.Tick();
        SetCurrentActorNodeId(mem, 0xFF);
        poach.Tick();   // succeeds

        var expected = new HashSet<long> { storeAddr, DespawnCrystalAddr, DespawnModeAddr };
        var actual = new HashSet<long>(mem.Written.Keys);
        Assert.Equal(expected, actual);
    }
}
