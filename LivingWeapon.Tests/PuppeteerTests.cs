using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// Galewind's "Puppeteer" signature (replaces Charm-Lock). At +3, an enemy struck by the +3 wielder
/// is dominated for its NEXT turn -- the player controls its move + full skillset -- then it reverts
/// to AI. One puppet at a time, a 3-turn (wielder) cooldown, and a target gate.
///
/// TARGET GATE: ⚠ ALLOW-EVERYONE (2026-06-18, user request "control everyone"). IsDominatable returns
/// true for every job id -- the job byte is not a reliable unit filter on the IC build (a live boss
/// read job 37, below the old human-band floor). The real filter is the enemy + fingerprint gates in
/// the Tick path (ShouldLatch(enemy) + maxHp/lvl/brave/faith sanity + enemy-roster match).
///
/// Pure gate in Puppeteer.Policy.cs: IsDominatable(jobId).
/// </summary>
public class PuppeteerTests
{
    // ---- the target gate: IsDominatable ----
    // ALLOW-EVERYONE (2026-06-18, user request "I want to control everyone"): the job gate is OFF and
    // returns true for EVERY id. It earned its retirement live -- a real 828-HP enemy (a boss) read job
    // 37 at combat +0x03, BELOW the old 74 floor, so the floor was refusing a genuine enemy. The job
    // byte is not a reliable unit filter on IC; the enemy + fingerprint gates in Puppeteer.cs are. The
    // band helpers (IsGenericHumanJob / IsMonsterJob) are kept intact so a gate can be reinstated.

    [Theory]
    [InlineData(0)]    // unreadable/empty job read -- still puppetable (the enemy gate upstream is the filter)
    [InlineData(1)]    // old UI-buffer-leak sentinel
    [InlineData(3)]    // PSX-legacy story id
    [InlineData(37)]   // the LIVE boss that retired the floor (828 HP enemy read job 37, below old 74)
    [InlineData(73)]   // one below the old human band
    [InlineData(74)]   // Squire
    [InlineData(99)]   // Goblin (monster)
    [InlineData(145)]  // 0x91 Construct 8
    [InlineData(160)]  // 0xA0 named story boss
    [InlineData(255)]  // 0xFF -- top of the job byte
    public void IsDominatable_allows_everyone(int jobId)
        => Assert.True(Puppeteer.IsDominatable(jobId));

    // ---- IsActive: configured (PuppeteerTurns > 0) AND the kill tier earned ----

    private static WeaponSignature PupSig(int turns = 1, int atTier = 3) =>
        new() { AtTier = atTier, PuppeteerTurns = turns, DisplayLabel = "Puppeteer" };

    [Fact]
    public void IsActive_false_when_no_signature()
        => Assert.False(Puppeteer.IsActive(null, tier: 3));

    [Fact]
    public void IsActive_false_when_puppeteerTurns_zero()
        => Assert.False(Puppeteer.IsActive(new WeaponSignature { PuppeteerTurns = 0, AtTier = 3 }, tier: 3));

    [Fact]
    public void IsActive_false_below_tier()
        => Assert.False(Puppeteer.IsActive(PupSig(), tier: 2));

    [Fact]
    public void IsActive_true_at_and_above_tier()
    {
        Assert.True(Puppeteer.IsActive(PupSig(atTier: 3), tier: 3));
        Assert.True(Puppeteer.IsActive(PupSig(atTier: 3), tier: 4));
    }

    // ---- ShouldLatch: only enemies are puppeted ----

    [Fact]
    public void ShouldLatch_true_for_enemy() => Assert.True(Puppeteer.ShouldLatch(isEnemy: true));

    [Fact]
    public void ShouldLatch_false_for_ally() => Assert.False(Puppeteer.ShouldLatch(isEnemy: false));

    // ---- OffCooldown: cooldownTurns of the WIELDER's own turns must elapse since the last puppet.
    // current < last is the battle-restart carryover guard (counter reset below the stored stamp). ----

    [Theory]
    [InlineData(5, 2, 3, true)]   // exactly 3 wielder turns elapsed == cooldown -> off
    [InlineData(4, 2, 3, false)]  // only 2 elapsed -> still on cooldown
    [InlineData(2, 2, 3, false)]  // same turn it was applied -> on cooldown
    [InlineData(6, 2, 3, true)]   // well past
    [InlineData(1, 5, 3, true)]   // current < last: battle-restart carryover -> off
    public void OffCooldown_requires_cooldown_wielder_turns(int current, int last, int cd, bool expected)
        => Assert.Equal(expected, Puppeteer.OffCooldown(current, last, cd));

    // ---- PuppeteerState: ONE puppet at a time + expiry after the WIELDER's next turn + cooldown ----

    [Fact]
    public void State_can_puppet_initially_then_blocks_while_active()
    {
        var st = new PuppeteerState();
        Assert.True(st.CanPuppet(currentWielderTurn: 0, cooldownTurns: 3));   // never puppeted -> allowed

        st.Puppet(addr: 1000L, fp: (600, 50, 70, 50), currentWielderTurn: 0,
                  wfp: null, wBaseline: 0, globalBaseline: 0);
        Assert.True(st.HasPuppet);
        Assert.False(st.CanPuppet(currentWielderTurn: 1, cooldownTurns: 3));  // one-at-a-time -> blocked while active
    }

    [Fact]
    public void State_expires_on_the_wielders_next_turn()
    {
        var st = new PuppeteerState();
        st.Puppet(1000L, (600, 50, 70, 50), currentWielderTurn: 0,
                  wfp: (60, 70, 50), wBaseline: 4, globalBaseline: 0);

        // Wielder hasn't taken another turn yet (wielderTurnsNow == wBaseline == 4): not expired.
        Assert.False(st.IsExpired(wielderTurnsNow: 4, globalTurnsNow: 0, puppetTurns: 1, wielderlessFallbackTurns: 12));
        // Wielder took one more turn (wielderTurnsNow == 5 = baseline+1 >= baseline+puppetTurns=5): expired.
        Assert.True(st.IsExpired(wielderTurnsNow: 5, globalTurnsNow: 0, puppetTurns: 1, wielderlessFallbackTurns: 12));
    }

    [Fact]
    public void State_multi_turn_needs_N_wielder_turns()
    {
        var st = new PuppeteerState();
        st.Puppet(1000L, (600, 50, 70, 50), currentWielderTurn: 0,
                  wfp: (60, 70, 50), wBaseline: 2, globalBaseline: 0);

        // puppetTurns: 2 -- need wBaseline+2 == 4 wielder turns elapsed.
        Assert.False(st.IsExpired(wielderTurnsNow: 3, globalTurnsNow: 0, puppetTurns: 2, wielderlessFallbackTurns: 12));
        Assert.True(st.IsExpired(wielderTurnsNow: 4, globalTurnsNow: 0, puppetTurns: 2, wielderlessFallbackTurns: 12));
    }

    [Fact]
    public void State_wielderless_falls_back_to_global_turns()
    {
        var st = new PuppeteerState();
        st.Puppet(1000L, (600, 50, 70, 50), currentWielderTurn: 0,
                  wfp: null, wBaseline: 0, globalBaseline: 0);

        Assert.False(st.IsExpired(wielderTurnsNow: 0, globalTurnsNow: 11, puppetTurns: 1, wielderlessFallbackTurns: 12));
        Assert.True(st.IsExpired(wielderTurnsNow: 0, globalTurnsNow: 12, puppetTurns: 1, wielderlessFallbackTurns: 12));
    }

    [Fact]
    public void State_cooldown_blocks_new_puppet_for_three_wielder_turns_after_release()
    {
        var st = new PuppeteerState();
        st.Puppet(1000L, (600, 50, 70, 50), currentWielderTurn: 4,
                  wfp: null, wBaseline: 0, globalBaseline: 0);
        st.Release();                   // reverted to AI; cooldown clock keeps running
        Assert.False(st.HasPuppet);

        Assert.False(st.CanPuppet(currentWielderTurn: 5, cooldownTurns: 3));  // 1 wielder turn later -> on cooldown
        Assert.False(st.CanPuppet(currentWielderTurn: 6, cooldownTurns: 3));  // 2 -> still on cooldown
        Assert.True(st.CanPuppet(currentWielderTurn: 7, cooldownTurns: 3));   // 3 -> off cooldown
    }

    [Fact]
    public void State_clear_resets_puppet_and_cooldown()
    {
        var st = new PuppeteerState();
        st.Puppet(1000L, (600, 50, 70, 50), currentWielderTurn: 4,
                  wfp: null, wBaseline: 0, globalBaseline: 0);
        st.Release();
        st.Clear();                     // battle exit
        Assert.False(st.HasPuppet);
        Assert.True(st.CanPuppet(currentWielderTurn: 5, cooldownTurns: 3));   // cooldown clock cleared -> allowed
    }

    // ================= runtime (Tick) integration tests =================
    // Mirror MaimTests: FakeSparseMemory seats the wielder gate + a struck enemy victim; assert the
    // agency bit (combat +0x05 / 0x08, band -0x17) is set on a dominatable enemy, held each tick, and
    // cleared after the WIELDER's next turn / on battle exit.

    private const int GalewindId = 9;

    // Wielder lives at a DISTINCT band slot and fingerprint from the puppet.
    // Puppet slot 24, wielder slot 25 (SlotsBack+5 to be player-side in any band layout --
    // but here we just need a different slot index from the victim slot 24).
    private const int WielderBandSlot = 25;
    private const int WielderLevel = 60;
    private const int WielderBrave = 80;
    private const int WielderFaith = 55;
    private const int WielderMaxHp = 350;
    private const int WielderHp = 350;

    /// <summary>Build the Puppeteer with:
    /// - victim at bandSlot (default 24) with given fp, job, agency, hp=100
    /// - wielder at WielderBandSlot (25) in the band AND in a roster slot so TryResolveActingPlayer succeeds
    /// - KillTracker constructed WITH GalewindId (9) in the tracked-weapons set
    /// - turn queue pointed at the WIELDER so LastActorFingerprint resolves to the wielder
    /// - acted=1 so the wielder is acting</summary>
    private static (Puppeteer pup, FakeSparseMemory mem, TurnTracker turns, KillTracker tracker, long victim)
        BuildPuppet((int mhp, int lvl, int br, int fa) fp, int job = 76, int bandSlot = 24, int puppetTurns = 1)
    {
        var mem = new FakeSparseMemory();
        var meta = new System.Collections.Generic.Dictionary<int, WeaponMeta>
        {
            [GalewindId] = new WeaponMeta
            {
                Name = "Galewind", Wp = 8, Cat = "Knife", Formula = 1, Flavor = "A windswept dagger",
                Signature = new WeaponSignature { AtTier = 3, PuppeteerTurns = puppetTurns, DisplayLabel = "Puppeteer" }
            }
        };
        var kills = new System.Collections.Generic.Dictionary<int, int> { [GalewindId] = Tuning.ProdThresholds[2] };  // tier 3

        // KillTracker WITH GalewindId (9) in the tracked set so the wielder resolves as a player actor.
        var tracker = new KillTracker(kills, mem, new System.Collections.Generic.HashSet<int> { GalewindId });

        // Seat the WIELDER in a roster slot so FingerprintPlayer succeeds.
        MemSeats.SeatRoster(mem, slot: 5, lvl: WielderLevel, br: WielderBrave, fa: WielderFaith, rh: GalewindId);

        // Seat the WIELDER in the band at WielderBandSlot with a REAL grid position (gx=3, gy=7).
        MemSeats.SeatBand(mem, WielderBandSlot,
                          weapon: GalewindId, lvl: WielderLevel, br: WielderBrave, fa: WielderFaith,
                          gx: 3, gy: 7, hp: WielderHp, maxHp: WielderMaxHp);

        // Set the turn queue to the WIELDER so TryResolveActingPlayer + TryResolveActingFingerprint
        // both return the wielder's fingerprint (lvl, br, fa).
        mem.U16s[Offsets.TurnQueue + Offsets.TqMaxHp] = (ushort)WielderMaxHp;
        mem.U16s[Offsets.TurnQueue + Offsets.TqHp]    = (ushort)WielderHp;
        mem.U16s[Offsets.TurnQueue + Offsets.TqLevel]  = (ushort)WielderLevel;
        mem.U8s[Offsets.Acted] = 1;  // the wielder is acting

        // Settle the KillTracker so it latches the wielder's fingerprint as LastActorFingerprint.
        for (int i = 0; i < 3; i++) tracker.Poll(true);

        var turns = new TurnTracker(mem);

        long victim = Band.Entry(bandSlot);
        SeatVictim(mem, victim, fp, hp: 100, job: job, agency: 0x50);   // 0x50 = enemy (0x08 clear)
        SeatEnemyFp(mem, fp);

        var pup = new Puppeteer(meta, kills, tracker, turns, mem: mem);
        return (pup, mem, turns, tracker, victim);
    }

    private static void SeatVictim(FakeSparseMemory mem, long addr,
        (int mhp, int lvl, int br, int fa) fp, int hp, int job, byte agency)
    {
        mem.ReadableAddrs.Add(addr + Offsets.AMaxHp);
        mem.U16s[addr + Offsets.AMaxHp] = (ushort)fp.mhp;
        mem.U8s[addr + Offsets.ALevel] = (byte)fp.lvl;
        mem.U8s[addr + Offsets.ABrave] = (byte)fp.br;
        mem.U8s[addr + Offsets.AFaith] = (byte)fp.fa;
        mem.ReadableAddrs.Add(addr + Offsets.AHp);
        mem.U16s[addr + Offsets.AHp] = (ushort)hp;
        mem.U8s[addr + Offsets.AGx] = 5;
        mem.U8s[addr + Offsets.AGy] = 5;
        mem.ReadableAddrs.Add(addr + Puppeteer.JobOff);
        mem.U8s[addr + Puppeteer.JobOff] = (byte)job;
        mem.ReadableAddrs.Add(addr + Puppeteer.AgencyOff);
        mem.WritableAddrs.Add(addr + Puppeteer.AgencyOff);
        mem.U8s[addr + Puppeteer.AgencyOff] = agency;
    }

    private static void SeatEnemyFp(FakeSparseMemory mem, (int mhp, int lvl, int br, int fa) fp)
    {
        long slot = Offsets.ArrayReadBase;                       // static-array slot 0 (enemy side)
        mem.ReadableAddrs.Add(slot + Offsets.AMaxHp);
        mem.U16s[slot + Offsets.AMaxHp] = (ushort)fp.mhp;
        mem.U8s[slot + Offsets.ALevel] = (byte)fp.lvl;
        mem.U8s[slot + Offsets.ABrave] = (byte)fp.br;
        mem.U8s[slot + Offsets.AFaith] = (byte)fp.fa;
    }

    private static bool AgencyBitSet(FakeSparseMemory mem, long victim)
        => (mem.U8(victim + Puppeteer.AgencyOff) & Puppeteer.AgencyBit) != 0;

    /// <summary>Advance a full turn for the unit with fingerprint (mhp, lvl, br, fa) by
    /// toggling the turn queue to that unit and pulsing acted 0->1 + turns.Poll().
    /// Per Phase-2 correction #5: toggles turn-queue ON, calls pup.Tick(), then pulses the
    /// acted edge WITH the unit still in the queue (so TryActiveFingerprint credits it),
    /// then hands off the queue, then calls pup.Tick() again -- exercising both the Drive path
    /// and the Expire wielder-clock check.</summary>
    private static void UnitTakesATurn(FakeSparseMemory mem, Puppeteer pup, TurnTracker turns,
        int mhp, int lvl, int br, int fa, int hp)
    {
        // Step 1: queue points at this unit (active).
        mem.U16s[Offsets.TurnQueue + Offsets.TqMaxHp] = (ushort)mhp;
        mem.U16s[Offsets.TurnQueue + Offsets.TqHp]    = (ushort)hp;
        mem.U16s[Offsets.TurnQueue + Offsets.TqLevel]  = (ushort)lvl;
        pup.Tick(onField: false);   // tick while it is the active unit

        // Step 2: acted 0->1 edge fires WHILE the queue still names this unit --
        // TryActiveFingerprint finds the band match and credits Turns(fp).
        mem.U8s[Offsets.Acted] = 0; turns.Poll();
        mem.U8s[Offsets.Acted] = 1; turns.Poll();   // rising edge: credits this unit's turn
        mem.U8s[Offsets.Acted] = 0;

        // Step 3: queue hands off to someone else (queue no longer matches this unit).
        mem.U16s[Offsets.TurnQueue + Offsets.TqMaxHp] = (ushort)(mhp + 1);   // different mhp = another unit

        pup.Tick(onField: false);   // Expire now sees Turns(fp) advanced; releases if wielder clock hit
    }

    // ---- LOAD-BEARING test ----
    // This test MUST fail under the old ObserveActive mechanism (the turn-queue hand-off fires
    // after the puppet moves, releasing control mid-turn) and pass under the new wielder-clock.

    [Fact]
    public void Tick_holds_control_through_the_puppets_full_turn_then_releases_on_the_wielders_next_turn()
    {
        // The victim's fp is DISTINCT from the wielder's fp (different mhp/lvl/br/fa).
        var puppetFp = (mhp: 600, lvl: 50, br: 70, fa: 50);
        var (pup, mem, turns, tracker, victim) = BuildPuppet(puppetFp, job: 76, bandSlot: 24, puppetTurns: 1);

        // --- Step 1: DOMINATE ---
        pup.Tick(onField: true);                        // baseline HP 100 (no damage yet)
        mem.U16s[victim + Offsets.AHp] = 80;            // wielder's hit dealt 20
        pup.Tick(onField: true);

        Assert.True(AgencyBitSet(mem, victim));          // agency bit SET -- player control granted
        Assert.NotNull(pup.PuppetFingerprint);           // puppet is latched

        // --- Step 2: PUPPET takes its FULL turn (queue -> puppet -> hand-off + acted edge) ---
        // This is the exact sequence the OLD ObserveActive saw; it would have released here.
        // Under the new mechanism, control MUST STILL BE HELD after this step.
        UnitTakesATurn(mem, pup, turns,
                       puppetFp.mhp, puppetFp.lvl, puppetFp.br, puppetFp.fa, hp: 80);

        // THE NON-VACUOUS ASSERTION: old code released here, new code must not.
        Assert.True(AgencyBitSet(mem, victim),
            "FAILED: agency bit was cleared after the puppet's turn -- the old mid-turn revert. " +
            "The fix did not take effect.");
        Assert.NotNull(pup.PuppetFingerprint);

        // --- Step 3: WIELDER takes its next turn (advances Turns(wielderFp) past baseline) ---
        // Settle the tracker so the wielder is latched as the acting unit again.
        mem.U8s[Offsets.Acted] = 1;
        for (int i = 0; i < 3; i++) tracker.Poll(true);   // re-latch wielder fp

        UnitTakesATurn(mem, pup, turns,
                       WielderMaxHp, WielderLevel, WielderBrave, WielderFaith, hp: WielderHp);

        // After the WIELDER's turn: the wielder-turn clock advances past baseline -> released.
        Assert.False(AgencyBitSet(mem, victim));         // agency bit CLEARED -- reverted to AI
        Assert.Null(pup.PuppetFingerprint);
    }

    [Fact]
    public void Tick_dominates_a_struck_dominatable_enemy()
    {
        var fp = (mhp: 600, lvl: 50, br: 70, fa: 50);
        var (pup, mem, _, _, victim) = BuildPuppet(fp, job: 76);   // Knight = dominatable

        pup.Tick(onField: true);                  // baseline HP 100 (no damage yet)
        mem.U16s[victim + Offsets.AHp] = 80;      // the wielder's hit dealt 20
        pup.Tick(onField: true);

        Assert.True(AgencyBitSet(mem, victim));                       // 0x08 set = human-controlled
        Assert.Equal(0x58, mem.U8(victim + Puppeteer.AgencyOff));     // 0x50 | 0x08 (other bits preserved)
    }

    [Fact]
    public void Tick_dominates_a_low_job_id_boss()
    {
        // Regression for the live find: an 828-max-HP enemy (a boss) read job 37 at combat +0x03, below
        // the old 74 floor, and was wrongly refused. Under allow-everyone, a low/story job id is
        // dominated. (BuildPuppet seats current HP at 100; the boss identity is the 828 MAX-HP fp.)
        var fp = (mhp: 828, lvl: 50, br: 70, fa: 50);
        var (pup, mem, _, _, victim) = BuildPuppet(fp, job: 37);

        pup.Tick(onField: true);                // baseline (current HP 100)
        mem.U16s[victim + Offsets.AHp] = 80;    // the wielder's hit dealt 20 -> latch
        pup.Tick(onField: true);

        Assert.True(AgencyBitSet(mem, victim));                       // 0x08 set = human-controlled
        Assert.Equal(0x58, mem.U8(victim + Puppeteer.AgencyOff));     // 0x50 | 0x08 (other bits preserved)
    }

    [Fact]
    public void Tick_dominates_an_important_character_under_allow_everyone()
    {
        var fp = (mhp: 600, lvl: 50, br: 70, fa: 50);
        var (pup, mem, _, _, victim) = BuildPuppet(fp, job: 160);   // 0xA0 named story boss -- formerly gated out, now puppetable

        pup.Tick(onField: true);
        mem.U16s[victim + Offsets.AHp] = 80;     // the wielder's hit dealt 20
        pup.Tick(onField: true);

        Assert.True(AgencyBitSet(mem, victim));                       // 0x08 set = human-controlled
        Assert.Equal(0x58, mem.U8(victim + Puppeteer.AgencyOff));     // 0x50 | 0x08 (other bits preserved)
    }

    [Fact]
    public void Tick_holds_the_agency_bit_each_tick()
    {
        var fp = (mhp: 600, lvl: 50, br: 70, fa: 50);
        var (pup, mem, _, _, victim) = BuildPuppet(fp);
        pup.Tick(onField: true);
        mem.U16s[victim + Offsets.AHp] = 80;
        pup.Tick(onField: true);
        Assert.True(AgencyBitSet(mem, victim));

        mem.U8s[victim + Puppeteer.AgencyOff] = 0x50;   // the engine re-derived it back to AI
        pup.Tick(onField: false);
        Assert.True(AgencyBitSet(mem, victim));         // the hold re-asserts the bit
    }

    [Fact]
    public void Tick_reverts_to_AI_after_the_puppet_completes_one_full_turn()
    {
        // Verify revert happens: dominate, then the WIELDER takes a turn -> release.
        var fp = (mhp: 600, lvl: 50, br: 70, fa: 50);
        var (pup, mem, turns, tracker, victim) = BuildPuppet(fp, puppetTurns: 1);
        pup.Tick(onField: true);
        mem.U16s[victim + Offsets.AHp] = 80;            // wielder's hit -> latch
        pup.Tick(onField: true);
        Assert.True(AgencyBitSet(mem, victim));

        // Re-latch wielder fp in the KillTracker and advance the wielder's turn clock.
        mem.U8s[Offsets.Acted] = 1;
        for (int i = 0; i < 3; i++) tracker.Poll(true);   // re-latch wielder fp

        UnitTakesATurn(mem, pup, turns,
                       WielderMaxHp, WielderLevel, WielderBrave, WielderFaith, hp: WielderHp);

        Assert.False(AgencyBitSet(mem, victim));        // reverted to AI
        Assert.Null(pup.PuppetFingerprint);
    }

    [Fact]
    public void Tick_does_not_revert_before_the_puppet_takes_a_turn()
    {
        var fp = (mhp: 600, lvl: 50, br: 70, fa: 50);
        var (pup, mem, _, _, victim) = BuildPuppet(fp, puppetTurns: 1);
        pup.Tick(onField: true);
        mem.U16s[victim + Offsets.AHp] = 80;
        pup.Tick(onField: true);
        Assert.True(AgencyBitSet(mem, victim));

        pup.Tick(onField: false);                       // no turn taken yet -> still controlled
        Assert.True(AgencyBitSet(mem, victim));
    }

    [Fact]
    public void ResetBattle_clears_the_agency_bit()
    {
        var fp = (mhp: 600, lvl: 50, br: 70, fa: 50);
        var (pup, mem, _, _, victim) = BuildPuppet(fp);
        pup.Tick(onField: true);
        mem.U16s[victim + Offsets.AHp] = 80;
        pup.Tick(onField: true);
        Assert.True(AgencyBitSet(mem, victim));

        pup.ResetBattle();
        Assert.False(AgencyBitSet(mem, victim));        // battle exit reverts the enemy
        Assert.Null(pup.PuppetFingerprint);
    }

    // ---- cooldown regression: re-puppet is blocked until the cooldown's battle-turns elapse ----
    // The bug this pins: the cooldown was keyed to a per-unit wielder count via the "last actor"
    // fingerprint, which flickered to the puppet after it acted -> the count ran backwards and the
    // carryover guard waved every re-puppet through. Keyed to GlobalTurns (monotonic), it holds.

    [Fact]
    public void Tick_cooldown_blocks_re_puppet_until_the_cooldown_turns_pass()
    {
        var fp = (mhp: 600, lvl: 50, br: 70, fa: 50);   // Tuning.PuppeteerCooldownTurns == 4 (3 turns blocked, fire on the 4th)
        var (pup, mem, turns, tracker, victim) = BuildPuppet(fp, puppetTurns: 1);

        pup.Tick(onField: true);                        // baseline (GlobalTurns 0)
        mem.U16s[victim + Offsets.AHp] = 80;
        pup.Tick(onField: true);                        // dominate (cooldown stamp = GlobalTurns 0)
        Assert.True(AgencyBitSet(mem, victim));

        // Advance the wielder's turn so the puppet expires.
        mem.U8s[Offsets.Acted] = 1;
        for (int i = 0; i < 3; i++) tracker.Poll(true);
        UnitTakesATurn(mem, pup, turns,
                       WielderMaxHp, WielderLevel, WielderBrave, WielderFaith, hp: WielderHp);
        Assert.False(AgencyBitSet(mem, victim));        // puppet expired

        // Re-hit the now-AI enemy 1 battle-turn later: still on cooldown (1 < 4) -> NOT re-dominated.
        mem.U8s[Offsets.Acted] = 1;
        mem.U16s[victim + Offsets.AHp] = 60;
        pup.Tick(onField: true);
        Assert.False(AgencyBitSet(mem, victim));        // cooldown blocks (GlobalTurns 1)

        // Two more battle-turns pass (GlobalTurns -> 3): STILL on cooldown (3 < 4) -- 3 turns blocked.
        mem.U8s[Offsets.Acted] = 0; turns.Poll(); mem.U8s[Offsets.Acted] = 1; turns.Poll();
        mem.U8s[Offsets.Acted] = 0; turns.Poll(); mem.U8s[Offsets.Acted] = 1; turns.Poll();
        mem.U8s[Offsets.Acted] = 1;
        mem.U16s[victim + Offsets.AHp] = 40;
        pup.Tick(onField: true);
        Assert.False(AgencyBitSet(mem, victim));        // 3 battle-turns elapsed -> STILL blocked (3 < 4)

        // The 4th battle-turn passes (GlobalTurns -> 4); off cooldown -> re-dominates on the 4th turn.
        mem.U8s[Offsets.Acted] = 0; turns.Poll(); mem.U8s[Offsets.Acted] = 1; turns.Poll();
        mem.U8s[Offsets.Acted] = 1;
        mem.U16s[victim + Offsets.AHp] = 20;
        pup.Tick(onField: true);
        Assert.True(AgencyBitSet(mem, victim));         // 4 battle-turns elapsed -> off cooldown
    }

    // ---- guarded write path on a real pinned buffer (the production RPM/WPM path) ----

    [Fact]
    public void SetAgency_sets_then_clears_only_bit_0x08_via_the_guarded_path()
    {
        var live = new LiveMemory();
        using var unit = PinnedBuf.Of(256);
        long band = unit.Addr + 64;                         // so band + AgencyOff (negative) stays in-buffer
        unit.Bytes[64 + Puppeteer.AgencyOff] = 0x50;        // enemy baseline (0x08 clear, other bits set)

        Puppeteer.SetAgency(live, band, on: true);
        Assert.Equal(0x58, unit.Bytes[64 + Puppeteer.AgencyOff]);   // 0x08 set, 0x50 preserved

        Puppeteer.SetAgency(live, band, on: false);
        Assert.Equal(0x50, unit.Bytes[64 + Puppeteer.AgencyOff]);   // 0x08 cleared, rest intact
    }
}
