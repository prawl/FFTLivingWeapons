using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// Module-level Provoke coverage: the two table byte-writes (idempotent, per-byte guarded, never
/// touching the decoy mirror), the eligibility trio (tier gate + main-hand-only), and the release
/// path (table bytes restored, our slot only). The JobCommand injection mechanism itself (find/
/// inject/release/hold) is the proven Barrage/ShadowBlade code, already covered by BarrageTests
/// and ShadowBladeTests -- these tests exercise Provoke's own wiring of it, plus the table half
/// that has no Barrage/ShadowBlade precedent. See BarrageShadowBladeCollisionTests for the
/// three-way record-7 collision (Barrage + Shadow Blade + Provoke sharing one JobCommand record).
/// </summary>
public class ProvokeTests
{
    private const int DefenderId = Provoke.DefenderId;   // 33
    private const int ProvokeAbilityId = ProvokePolicy.ProvokeAbilityId;   // 189
    private const int KnightJob = 76;
    private const int KnightRecord = 7;

    private static Dictionary<int, WeaponMeta> Meta() => new()
    {
        [DefenderId] = new WeaponMeta { Signature = new WeaponSignature { AtTier = 3, GrantCommandAbilityId = ProvokeAbilityId } },
    };

    private static Dictionary<int, int> MaxKills() => new() { [DefenderId] = 999 };
    private static Dictionary<int, int> NoKills() => new() { [DefenderId] = 0 };

    /// <summary>Same shape as Meta() but with an attacker-controlled/typo'd granted ability id, for
    /// the bounds-guard tests below.</summary>
    private static Dictionary<int, WeaponMeta> MetaWithAbilityId(int abilityId) => new()
    {
        [DefenderId] = new WeaponMeta { Signature = new WeaponSignature { AtTier = 3, GrantCommandAbilityId = abilityId } },
    };

    private static void SeatWielder(FakeSparseMemory m, int rosterSlot, int rhand, int job, int offHand = 0)
    {
        long rb = Offsets.RosterBase + (long)rosterSlot * Offsets.RosterStride;
        m.U16s[rb + Offsets.RNameId] = 1;
        m.ReadableAddrs.Add(rb + Offsets.RNameId);
        m.U8s[rb + Offsets.RLevel] = 30;
        m.U16s[rb + Offsets.RRHand] = (ushort)rhand;
        m.U16s[rb + Offsets.ROffHand] = (ushort)offHand;
        m.U8s[rb + Barrage.RJobId] = (byte)job;
        m.U8s[rb + Barrage.RSecondary] = 0;
    }

    /// <summary>Mark both table addresses (the live action byte + the six authored inflict-row
    /// bytes) readable and writable in the fake -- the recipe every table-write test needs.</summary>
    private static void StageTable(FakeSparseMemory m, int abilityId = ProvokeAbilityId)
    {
        long actionAddr = ProvokePolicy.ActionInflictAddr(abilityId);
        long inflictAddr = ProvokePolicy.InflictRowAddr(ProvokePolicy.ProvokeInflictRow);
        m.ReadableAddrs.Add(actionAddr);
        m.WritableAddrs.Add(actionAddr);
        for (int i = 0; i < ProvokePolicy.InflictStride; i++)
        {
            m.ReadableAddrs.Add(inflictAddr + i);
            m.WritableAddrs.Add(inflictAddr + i);
        }
    }

    // --- Test 1: THE LOAD-BEARING TEST ---

    [Fact]
    public void Provoke_repoints_ability_189_inflict_byte_in_the_LIVE_table()
    {
        var m = new FakeSparseMemory();
        SeatWielder(m, rosterSlot: 0, rhand: DefenderId, job: KnightJob);
        StageTable(m);

        // Stage the DECOY mirror readable AND writable too -- without this, a decoy-mis-pinned
        // implementation would fail the fake's exact-address Writable check, write nothing
        // anywhere, and pass the "no write in the decoy range" assertion in both the correct and
        // the broken world. See ProvokePolicyTests for why only a test can catch a mis-pin here.
        long decoyByte = ProvokePolicy.DecoyActionTable + ProvokeAbilityId * ProvokePolicy.ActionStride + ProvokePolicy.ActionInflictOffset;
        m.ReadableAddrs.Add(decoyByte);
        m.WritableAddrs.Add(decoyByte);

        var provoke = new Provoke(Meta(), MaxKills(), m);
        provoke.Tick();

        long liveByte = ProvokePolicy.ActionInflictAddr(ProvokeAbilityId);
        Assert.True(m.Written.TryGetValue(liveByte, out byte gotByte),
            $"expected a write to the live ability-189 inflict byte at 0x{liveByte:X}; none happened");
        Assert.Equal((byte)ProvokePolicy.ProvokeInflictRow, gotByte);

        long decoyLow = ProvokePolicy.DecoyActionTable;
        long decoyHigh = Offsets.LiveActionTable;   // half-open: the two tables are exactly contiguous
        foreach (var addr in m.Written.Keys)
            Assert.False(addr >= decoyLow && addr < decoyHigh, $"wrote into the decoy range at 0x{addr:X}");
    }

    // --- Test 3: mode-first authored row ---

    [Fact]
    public void Authored_inflict_row_29_is_written_mode_first()
    {
        var m = new FakeSparseMemory();
        SeatWielder(m, rosterSlot: 0, rhand: DefenderId, job: KnightJob);
        StageTable(m);

        var provoke = new Provoke(Meta(), MaxKills(), m);
        provoke.Tick();

        long inflictAddr = ProvokePolicy.InflictRowAddr(ProvokePolicy.ProvokeInflictRow);
        var got = new byte[ProvokePolicy.InflictStride];
        for (int i = 0; i < got.Length; i++) got[i] = m.U8(inflictAddr + i);
        Assert.Equal(new byte[] { 0x80, 0x80, 0x00, 0x00, 0x00, 0x00 }, got);
    }

    // --- Test 4: idempotence ---

    [Fact]
    public void A_tick_that_finds_every_byte_already_correct_writes_nothing()
    {
        var m = new FakeSparseMemory();
        SeatWielder(m, rosterSlot: 0, rhand: DefenderId, job: KnightJob);
        StageTable(m);

        long actionAddr = ProvokePolicy.ActionInflictAddr(ProvokeAbilityId);
        long inflictAddr = ProvokePolicy.InflictRowAddr(ProvokePolicy.ProvokeInflictRow);
        m.U8s[actionAddr] = (byte)ProvokePolicy.ProvokeInflictRow;
        for (int i = 0; i < ProvokePolicy.InflictStride; i++)
            m.U8s[inflictAddr + i] = ProvokePolicy.DesiredInflictRow[i];

        var provoke = new Provoke(Meta(), MaxKills(), m);
        provoke.Tick();

        Assert.Empty(m.Written);
    }

    // --- Test 5: refusal ---

    [Fact]
    public void A_refused_write_writes_nothing_and_logs()
    {
        var m = new FakeSparseMemory();
        SeatWielder(m, rosterSlot: 0, rhand: DefenderId, job: KnightJob);

        long actionAddr = ProvokePolicy.ActionInflictAddr(ProvokeAbilityId);
        long inflictAddr = ProvokePolicy.InflictRowAddr(ProvokePolicy.ProvokeInflictRow);
        m.ReadableAddrs.Add(actionAddr);
        for (int i = 0; i < ProvokePolicy.InflictStride; i++) m.ReadableAddrs.Add(inflictAddr + i);
        // Deliberately NOT marked writable.

        var file = new List<string>();
        ModLogger.Instance = new FileConsoleLogger(_ => { }, file.Add) { LogLevel = LogLevel.Debug };
        try
        {
            var provoke = new Provoke(Meta(), MaxKills(), m);
            provoke.Tick();
        }
        finally { ModLogger.UseNullLogger(); }

        Assert.Empty(m.Written);
        Assert.Contains(file, l => l.Contains("refused"));
    }

    // --- Test 6: eligibility trio ---

    [Fact]
    public void Tier_below_3_does_not_grant()
    {
        var m = new FakeSparseMemory();
        SeatWielder(m, rosterSlot: 0, rhand: DefenderId, job: KnightJob);
        StageTable(m);

        var provoke = new Provoke(Meta(), NoKills(), m);
        provoke.Tick();

        Assert.Empty(m.Written);
    }

    [Fact]
    public void Defender_in_the_off_hand_does_not_grant()
    {
        var m = new FakeSparseMemory();
        SeatWielder(m, rosterSlot: 0, rhand: 0, job: KnightJob, offHand: DefenderId);
        StageTable(m);

        var provoke = new Provoke(Meta(), MaxKills(), m);
        provoke.Tick();

        Assert.Empty(m.Written);
    }

    [Fact]
    public void Main_hand_at_tier_3_grants()
    {
        var m = new FakeSparseMemory();
        SeatWielder(m, rosterSlot: 0, rhand: DefenderId, job: KnightJob);
        StageTable(m);

        var provoke = new Provoke(Meta(), MaxKills(), m);
        provoke.Tick();

        long actionAddr = ProvokePolicy.ActionInflictAddr(ProvokeAbilityId);
        Assert.Equal((byte)ProvokePolicy.ProvokeInflictRow, m.Written[actionAddr]);
    }

    // --- Test 7: release restores the table and only our slot ---

    [Fact]
    public void Release_restores_the_table_bytes_and_only_our_slot()
    {
        var m = new FakeSparseMemory();
        long flagAddr = Barrage.AbilityBase + (long)KnightRecord * Barrage.RecSize - Barrage.FlagPrefixSize;
        long abBase = Barrage.AbilityBase + (long)KnightRecord * Barrage.RecSize;
        var buf = new byte[Barrage.RecSize];
        m.TerrainBlocks[flagAddr] = buf;
        for (int i = 0; i < Barrage.FlagPrefixSize; i++) { m.ReadableAddrs.Add(flagAddr + i); m.WritableAddrs.Add(flagAddr + i); }
        for (int i = 0; i < Barrage.AbilityCount; i++) { m.ReadableAddrs.Add(abBase + i); m.WritableAddrs.Add(abBase + i); }

        // Barrage and Shadow Blade already occupy slots 1 and 2 (0-indexed 0 and 1) -- seeded
        // directly, not via ticking those modules, since this test is about Provoke's own release
        // discipline, not the collision recipe (see BarrageShadowBladeCollisionTests for that).
        m.U8s[abBase + 0] = 102;   // Barrage (358 & 0xFF)
        m.U8s[abBase + 1] = 165;   // Shadow Blade
        buf[Barrage.FlagPrefixSize + 0] = 102;
        buf[Barrage.FlagPrefixSize + 1] = 165;

        SeatWielder(m, rosterSlot: 0, rhand: DefenderId, job: KnightJob);
        StageTable(m);

        void Sync() { for (int i = 0; i < buf.Length; i++) buf[i] = m.U8(flagAddr + i); }

        var provoke = new Provoke(Meta(), MaxKills(), m);
        Sync(); provoke.Tick(); Sync();

        long actionAddr = ProvokePolicy.ActionInflictAddr(ProvokeAbilityId);
        long inflictAddr = ProvokePolicy.InflictRowAddr(ProvokePolicy.ProvokeInflictRow);
        Assert.Equal((byte)ProvokePolicy.ProvokeInflictRow, m.U8(actionAddr));
        Assert.Equal((byte)189, buf[Barrage.FlagPrefixSize + 2]);   // Provoke landed on the third slot
        Assert.Equal((byte)102, buf[Barrage.FlagPrefixSize + 0]);   // Barrage untouched
        Assert.Equal((byte)165, buf[Barrage.FlagPrefixSize + 1]);   // Shadow Blade untouched

        // The wielder loses the Defender entirely -- both lifecycles release.
        m.U16s[Offsets.RosterBase + Offsets.RRHand] = 0xFFFF;
        Sync(); provoke.Tick(); Sync();

        Assert.Equal((byte)0, buf[Barrage.FlagPrefixSize + 2]);     // our slot released
        Assert.Equal((byte)102, buf[Barrage.FlagPrefixSize + 0]);   // Barrage's slot untouched
        Assert.Equal((byte)165, buf[Barrage.FlagPrefixSize + 1]);   // Shadow Blade's slot untouched
        Assert.Equal((byte)0, m.U8(actionAddr));                    // action byte restored to its captured original (0, unseeded)
        for (int i = 0; i < ProvokePolicy.InflictStride; i++)
            Assert.Equal((byte)0, m.U8(inflictAddr + i));           // inflict row restored to its captured original
    }

    // --- Test 8: the two lifecycles must not share a release path ---

    /// <summary>Pins the single most important correction in the design: the TABLE lifecycle
    /// (keyed only on "a tier-3 main-hand Defender wielder exists at all") and the SLOT lifecycle
    /// (keyed on JobCommand grant resolution) must never be fused. If they were, a wielder losing
    /// grant eligibility -- a job change, here -- would repoint ability 189 back to its vanilla
    /// effect even though the Defender is still equipped, reopening the window a queued cast could
    /// resolve against the wrong table row. This test changes ONLY the wielder's job (never
    /// unequips the Defender) and asserts the slot releases while the table stays exactly as
    /// authored.</summary>
    [Fact]
    public void Losing_grant_eligibility_releases_the_slot_but_leaves_the_table_repoint_intact()
    {
        var m = new FakeSparseMemory();
        long flagAddr = Barrage.AbilityBase + (long)KnightRecord * Barrage.RecSize - Barrage.FlagPrefixSize;
        long abBase = Barrage.AbilityBase + (long)KnightRecord * Barrage.RecSize;
        var buf = new byte[Barrage.RecSize];
        m.TerrainBlocks[flagAddr] = buf;
        for (int i = 0; i < Barrage.FlagPrefixSize; i++) { m.ReadableAddrs.Add(flagAddr + i); m.WritableAddrs.Add(flagAddr + i); }
        for (int i = 0; i < Barrage.AbilityCount; i++) { m.ReadableAddrs.Add(abBase + i); m.WritableAddrs.Add(abBase + i); }

        SeatWielder(m, rosterSlot: 0, rhand: DefenderId, job: KnightJob);
        StageTable(m);

        void Sync() { for (int i = 0; i < buf.Length; i++) buf[i] = m.U8(flagAddr + i); }

        var provoke = new Provoke(Meta(), MaxKills(), m);
        Sync(); provoke.Tick(); Sync();

        long actionAddr = ProvokePolicy.ActionInflictAddr(ProvokeAbilityId);
        long inflictAddr = ProvokePolicy.InflictRowAddr(ProvokePolicy.ProvokeInflictRow);

        // Both lifecycles fire on the same tick: the slot is injected AND the table is repointed.
        Assert.Equal((byte)189, buf[Barrage.FlagPrefixSize + 0]);
        Assert.Equal((byte)ProvokePolicy.ProvokeInflictRow, m.U8(actionAddr));

        // The wielder's JOB changes to White Mage (79), off the Squire/Knight whitelist (see
        // ProvokePolicyTests.TryResolveGrantDelegatesToTheSharedShadowBladeWhitelist) -- the SLOT
        // lifecycle's grant resolution fails and releases. The Defender stays equipped in the
        // main hand throughout, so the TABLE lifecycle (keyed only on wielder existence, never
        // job -- see Provoke.Table.cs's class doc) must not move at all.
        m.U8s[Offsets.RosterBase + Barrage.RJobId] = 79;
        Sync(); provoke.Tick(); Sync();

        Assert.Equal((byte)0, buf[Barrage.FlagPrefixSize + 0]);   // slot lifecycle: our slot released
        Assert.Equal((byte)0x1D, m.U8(actionAddr));                // table lifecycle untouched: action byte still 0x1D (29)
        var gotRow = new byte[ProvokePolicy.InflictStride];
        for (int i = 0; i < gotRow.Length; i++) gotRow[i] = m.U8(inflictAddr + i);
        Assert.Equal(new byte[] { 0x80, 0x80, 0x00, 0x00, 0x00, 0x00 }, gotRow);   // authored row untouched
    }

    // --- Test 9: capture-failure bail ---

    /// <summary>Pins CaptureOriginal/WriteTable's "all seven bytes or nothing" invariant. One of
    /// the six inflict-row bytes is left OUT of ReadableAddrs (everything else, including the
    /// action byte, is readable and writable). CaptureOriginal's per-byte readability loop hits
    /// the unreadable one and returns without ever setting _tableCaptured -- WriteTable's
    /// "if (!_tableCaptured) return;" must then refuse to write ANY of the other six bytes, even
    /// though they are individually readable and writable, because a partial capture would leave
    /// RestoreTable with no original to put back for the byte it never got to read.</summary>
    [Fact]
    public void An_unreadable_table_byte_blocks_every_other_table_write_this_tick()
    {
        var m = new FakeSparseMemory();
        SeatWielder(m, rosterSlot: 0, rhand: DefenderId, job: KnightJob);

        long actionAddr = ProvokePolicy.ActionInflictAddr(ProvokeAbilityId);
        long inflictAddr = ProvokePolicy.InflictRowAddr(ProvokePolicy.ProvokeInflictRow);
        m.ReadableAddrs.Add(actionAddr);
        m.WritableAddrs.Add(actionAddr);
        for (int i = 0; i < ProvokePolicy.InflictStride; i++)
        {
            if (i == 3) continue;   // the one byte left unreadable -- capture must fail because of it
            m.ReadableAddrs.Add(inflictAddr + i);
            m.WritableAddrs.Add(inflictAddr + i);
        }
        // Deliberately NOT staging the JobCommand record (flagAddr): Tick() bails out of the slot
        // half before touching memory, so every write this test observes is the table half's alone.

        var provoke = new Provoke(Meta(), MaxKills(), m);
        provoke.Tick();

        Assert.Empty(m.Written);
    }

    // --- Test 10: write order, data before pointer ---

    /// <summary>Pins the ordering fix: the six inflict-row bytes must land before the action byte
    /// that repoints ability 189 at them, so the ability can never resolve against a half-authored
    /// row. All seven bytes are staged readable/writable but seeded to WRONG values so every one
    /// of them actually needs a write, then WriteOrder (see FakeSparseMemory) is checked to confirm
    /// each inflict-row write happened strictly before the action-byte write.</summary>
    [Fact]
    public void WriteTable_writes_every_inflict_row_byte_before_the_action_byte()
    {
        var m = new FakeSparseMemory();
        SeatWielder(m, rosterSlot: 0, rhand: DefenderId, job: KnightJob);
        StageTable(m);

        long actionAddr = ProvokePolicy.ActionInflictAddr(ProvokeAbilityId);
        long inflictAddr = ProvokePolicy.InflictRowAddr(ProvokePolicy.ProvokeInflictRow);
        m.U8s[actionAddr] = 0;   // wrong: wants ProvokeInflictRow (29)
        for (int i = 0; i < ProvokePolicy.InflictStride; i++)
            m.U8s[inflictAddr + i] = 0xFF;   // wrong for every index (desired is 0x80,0x80,0,0,0,0)

        var provoke = new Provoke(Meta(), MaxKills(), m);
        provoke.Tick();

        int actionWriteIdx = m.WriteOrder.IndexOf(actionAddr);
        Assert.True(actionWriteIdx >= 0, "expected a write to the action byte");
        for (int i = 0; i < ProvokePolicy.InflictStride; i++)
        {
            int inflictWriteIdx = m.WriteOrder.IndexOf(inflictAddr + i);
            Assert.True(inflictWriteIdx >= 0, $"expected a write to inflict-row byte {i}");
            Assert.True(inflictWriteIdx < actionWriteIdx,
                $"inflict-row byte {i} (write #{inflictWriteIdx}) must precede the action byte (write #{actionWriteIdx})");
        }
    }

    // --- Test 11: bounds guard refuses a typo'd/out-of-range ability id ---

    /// <summary>Pins ProvokePolicy.IsValidAbilityId's guard in WriteTable. meta.Signature carries
    /// 1890 (a plausible typo for 189, and past ActionRows = 368) as the granted ability id. The
    /// (bogus) computed action-byte address for 1890 is deliberately staged readable AND writable
    /// -- the guard must refuse BEFORE that address is ever touched, not rely on it being
    /// unreachable in the fake. Zero table writes must happen anywhere.</summary>
    [Fact]
    public void An_out_of_range_granted_ability_id_writes_nothing_even_when_its_bogus_address_is_writable()
    {
        var m = new FakeSparseMemory();
        SeatWielder(m, rosterSlot: 0, rhand: DefenderId, job: KnightJob);
        const int badAbilityId = 1890;   // 1890 * 20 + 15 lands hundreds of rows past ActionRows (368)
        StageTable(m, abilityId: badAbilityId);

        var provoke = new Provoke(MetaWithAbilityId(badAbilityId), MaxKills(), m);
        provoke.Tick();

        Assert.Empty(m.Written);
    }

    // --- Hardening 1: a refused restore keeps the capture alive for a later retry ---

    /// <summary>RestoreTable must not forget the captured originals just because one byte's
    /// write-back was refused this tick -- otherwise the next tick's top-of-function
    /// "if (!_tableCaptured) return;" bails before ever retrying, and the refused byte (still
    /// showing our repoint) is stuck that way for the rest of the session. Revokes ONLY the action
    /// byte's write permission during the restore tick (the six inflict bytes restore cleanly),
    /// confirms the action byte is still stuck at its repointed value afterward, then restores
    /// write permission and confirms a later tick finishes the job instead of being a permanent
    /// no-op.</summary>
    [Fact]
    public void A_refused_restore_write_does_not_strand_the_repoint_forever()
    {
        var m = new FakeSparseMemory();
        SeatWielder(m, rosterSlot: 0, rhand: DefenderId, job: KnightJob);
        StageTable(m);

        var provoke = new Provoke(Meta(), MaxKills(), m);
        provoke.Tick();   // grant: captures the (unseeded, zero) originals and repoints the table

        long actionAddr = ProvokePolicy.ActionInflictAddr(ProvokeAbilityId);
        long inflictAddr = ProvokePolicy.InflictRowAddr(ProvokePolicy.ProvokeInflictRow);
        Assert.Equal((byte)ProvokePolicy.ProvokeInflictRow, m.U8(actionAddr));

        // Wielder loses the Defender -- RestoreTable runs. Revoke only the action byte's write
        // permission; the inflict row restores cleanly, the action byte's write-back is refused.
        m.U16s[Offsets.RosterBase + Offsets.RRHand] = 0xFFFF;
        m.WritableAddrs.Remove(actionAddr);
        provoke.Tick();

        Assert.Equal((byte)ProvokePolicy.ProvokeInflictRow, m.U8(actionAddr));   // refused: still repointed
        for (int i = 0; i < ProvokePolicy.InflictStride; i++)
            Assert.Equal((byte)0, m.U8(inflictAddr + i));                        // this half restored fine

        // Whatever blocked the write stops blocking. If the refusal above had cleared the capture,
        // this tick would see it already false and bail out of RestoreTable's own top guard without
        // even trying -- the action byte would stay 0x1D forever. It must not: the capture survives
        // a refusal, so this tick retries and finishes restoring the true original.
        m.WritableAddrs.Add(actionAddr);
        provoke.Tick();

        Assert.Equal((byte)0, m.U8(actionAddr));
    }

    // --- Hardening 2: the bounds refusal logs once, not every tick ---

    [Fact]
    public void Out_of_range_ability_id_logs_the_refusal_once_then_stays_silent()
    {
        var m = new FakeSparseMemory();
        SeatWielder(m, rosterSlot: 0, rhand: DefenderId, job: KnightJob);
        const int badAbilityId = 1890;

        var file = new List<string>();
        ModLogger.Instance = new FileConsoleLogger(_ => { }, file.Add) { LogLevel = LogLevel.Debug };
        try
        {
            var provoke = new Provoke(MetaWithAbilityId(badAbilityId), MaxKills(), m);
            provoke.Tick();
            provoke.Tick();
            provoke.Tick();
        }
        finally { ModLogger.UseNullLogger(); }

        Assert.Equal(1, file.Count(l => l.Contains("out of range")));
    }
}
