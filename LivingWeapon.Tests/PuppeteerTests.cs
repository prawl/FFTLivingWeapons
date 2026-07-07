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

    // ---- PuppeteerState: ONE puppet at a time + the GlobalTurns release CAP + cooldown ----
    // The NORMAL release -- after the puppet's own turn -- is detected live in Puppeteer.Hold.cs and
    // is exercised by the Tick integration tests below; this state only owns the cap + cooldown.

    [Fact]
    public void State_can_puppet_initially_then_blocks_while_active()
    {
        var st = new PuppeteerState();
        Assert.True(st.CanPuppet(currentWielderTurn: 0, cooldownTurns: 3));   // never puppeted -> allowed

        st.Puppet(addr: 1000L, fp: (600, 50, 70, 50), currentWielderTurn: 0, globalBaseline: 0);
        Assert.True(st.HasPuppet);
        Assert.False(st.CanPuppet(currentWielderTurn: 1, cooldownTurns: 3));  // one-at-a-time -> blocked while active
    }

    // ---- SAFETY CAP (LW-5): the puppet is bounded to at most capTurns GlobalTurns after dominate --
    // the backstop should the live "own turn" queue signal never fire. Never a hold to battle exit. ----

    [Fact]
    public void State_caps_release_at_the_global_turn_backstop()
    {
        var st = new PuppeteerState();
        st.Puppet(1000L, (600, 50, 70, 50), currentWielderTurn: 0, globalBaseline: 0);

        Assert.False(st.IsCapped(globalTurnsNow: 11, capTurns: 12));
        Assert.True(st.IsCapped(globalTurnsNow: 12, capTurns: 12));
    }

    [Fact]
    public void State_cooldown_blocks_new_puppet_for_three_wielder_turns_after_release()
    {
        var st = new PuppeteerState();
        st.Puppet(1000L, (600, 50, 70, 50), currentWielderTurn: 4, globalBaseline: 0);
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
        st.Puppet(1000L, (600, 50, 70, 50), currentWielderTurn: 4, globalBaseline: 0);
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
        BuildPuppet((int mhp, int lvl, int br, int fa) fp, int job = 76, int bandSlot = 24, int puppetTurns = 1,
                    System.Collections.Generic.List<(string type, string payload)>? rec = null)
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

        var pup = new Puppeteer(meta, kills, tracker, turns, mem: mem,
                                recorder: rec is null ? null : (t, p) => rec.Add((t, p)));
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

    /// <summary>Point Offsets.ActorPtr at <paramref name="bandEntry"/>'s combat frame (the inverse
    /// of Band.ActorEntry: frame = bandEntry - BandEntry). Mirrors IaiTests.PointActorAt.</summary>
    private static void PointActorAt(FakeSparseMemory mem, long bandEntry) =>
        mem.SeedU64(Offsets.ActorPtr, (ulong)(bandEntry - Offsets.BandEntry));

    /// <summary>Drive one full turn for the unit with fingerprint (mhp, lvl, br, fa): the turn queue
    /// names it across an acted rising..falling edge, ticking the Puppeteer through BOTH edges so its
    /// own-turn detector (Puppeteer.Hold.cs) sees the queue naming the puppet during the action.
    /// QueueNamesPuppet is true iff (mhp,lvl) match the puppet's fp, so calling this with the PUPPET's
    /// fp completes an own-turn (release), and with any OTHER unit's fp does not. Also pulses
    /// TurnTracker (GlobalTurns/credit) exactly as a real turn would.</summary>
    private static void UnitTakesATurn(FakeSparseMemory mem, Puppeteer pup, TurnTracker turns,
        int mhp, int lvl, int br, int fa, int hp)
    {
        mem.U16s[Offsets.TurnQueue + Offsets.TqMaxHp] = (ushort)mhp;
        mem.U16s[Offsets.TurnQueue + Offsets.TqHp]    = (ushort)hp;
        mem.U16s[Offsets.TurnQueue + Offsets.TqLevel]  = (ushort)lvl;

        mem.U8s[Offsets.Acted] = 0; pup.Tick(onField: false);               // pre-turn: acted low, queue named
        mem.U8s[Offsets.Acted] = 1; turns.Poll(); pup.Tick(onField: false);  // acted RISING: credit + own-turn opens (puppet only)
        mem.U8s[Offsets.Acted] = 0; turns.Poll(); pup.Tick(onField: false);  // acted FALLING: own-turn completes (puppet only)

        // Queue hands off to someone else.
        mem.U16s[Offsets.TurnQueue + Offsets.TqMaxHp] = (ushort)(mhp + 1);
        pup.Tick(onField: false);
    }

    // ---- LOAD-BEARING test (LW-5, the 2026-07-07 fix) ----
    // Encodes BOTH failure modes the fix removes: (1) control must HOLD through a NON-puppet unit's
    // turn (the premature drop the old wielder-clock caused when the puppet was not the next actor),
    // and (2) control must RELEASE after the PUPPET's OWN turn (detected by the turn queue naming it).

    [Fact]
    public void Tick_holds_until_the_puppet_takes_its_own_turn_then_releases()
    {
        // The victim's fp is DISTINCT from the wielder's fp (different mhp/lvl/br/fa).
        var puppetFp = (mhp: 600, lvl: 50, br: 70, fa: 50);
        var (pup, mem, turns, _, victim) = BuildPuppet(puppetFp, job: 76, bandSlot: 24, puppetTurns: 1);

        // --- DOMINATE ---
        pup.Tick(onField: true);                        // baseline HP 100 (no damage yet)
        mem.U16s[victim + Offsets.AHp] = 80;            // the wielder's hit dealt 20
        pup.Tick(onField: true);
        Assert.True(AgencyBitSet(mem, victim));
        Assert.NotNull(pup.PuppetFingerprint);

        // --- A NON-puppet unit (the wielder) takes a turn: the queue never names the puppet, so
        // control MUST HOLD. This is the exact premature-drop the old wielder-clock got wrong. ---
        UnitTakesATurn(mem, pup, turns, WielderMaxHp, WielderLevel, WielderBrave, WielderFaith, hp: WielderHp);
        Assert.True(AgencyBitSet(mem, victim),
            "FAILED: control dropped on a NON-puppet unit's turn -- the premature release the fix removes.");
        Assert.NotNull(pup.PuppetFingerprint);

        // --- The PUPPET takes its OWN turn (queue names it across an acted edge) -> release. ---
        UnitTakesATurn(mem, pup, turns, puppetFp.mhp, puppetFp.lvl, puppetFp.br, puppetFp.fa, hp: 80);
        Assert.False(AgencyBitSet(mem, victim), "the puppet took its own turn -- control must revert to AI");
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
        // Verify revert happens: dominate, then the PUPPET takes its own turn -> release.
        var fp = (mhp: 600, lvl: 50, br: 70, fa: 50);
        var (pup, mem, turns, _, victim) = BuildPuppet(fp, puppetTurns: 1);
        pup.Tick(onField: true);
        mem.U16s[victim + Offsets.AHp] = 80;            // the wielder's hit -> latch
        pup.Tick(onField: true);
        Assert.True(AgencyBitSet(mem, victim));

        UnitTakesATurn(mem, pup, turns, fp.mhp, fp.lvl, fp.br, fp.fa, hp: 80);   // the puppet's own turn

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
        var (pup, mem, turns, _, victim) = BuildPuppet(fp, puppetTurns: 1);

        pup.Tick(onField: true);                        // baseline (GlobalTurns 0)
        mem.U16s[victim + Offsets.AHp] = 80;
        pup.Tick(onField: true);                        // dominate (cooldown stamp = GlobalTurns 0)
        Assert.True(AgencyBitSet(mem, victim));

        // The puppet takes its OWN turn -> expires (GlobalTurns -> 1; the cooldown stamp was 0).
        UnitTakesATurn(mem, pup, turns, fp.mhp, fp.lvl, fp.br, fp.fa, hp: 80);
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

    // ---- the actor-pointer arming path (D1): OR'd with the latch path, strictly widening the gate ----
    // (puppeteer arming/detection rebuild, 2026-07-03: the latch-only gate never fired live -- a stale/
    // other LastPlayerMainHand + Acted==0 is the exact diagnosed scenario. The engine's own ActorPtr
    // naming the wielder's own band seat is an independent, additional way to open the gate.)

    [Fact]
    public void Tick_dominates_via_the_actor_pointer_with_a_stale_latch()
    {
        // ---- LOAD-BEARING: the exact live-failure scenario. Must fail on unfixed code (latch-only
        // gate cannot open here) and pass once the pointer path is wired in. ----
        var fp = (mhp: 600, lvl: 50, br: 70, fa: 50);
        var (pup, mem, _, tracker, victim) = BuildPuppet(fp, job: 76, bandSlot: 24, puppetTurns: 1);

        // Stale latch: LastPlayerMainHand names some OTHER weapon (here: none), Acted == 0 -- the
        // latch path is fully closed, exactly as diagnosed live.
        tracker._lastPlayerMainHand = 0;
        mem.U8s[Offsets.Acted] = 0;

        // The engine's own actor pointer names the WIELDER's own frame -- the independent signal.
        long wielderEntry = Band.Entry(WielderBandSlot);
        PointActorAt(mem, wielderEntry);

        pup.Tick(onField: true);                 // baseline HP 100 (no damage yet)
        mem.U16s[victim + Offsets.AHp] = 80;     // the wielder's hit dealt 20
        pup.Tick(onField: true);

        Assert.True(AgencyBitSet(mem, victim),
            "FAILED: the pointer path did not open the gate -- with the latch fully closed " +
            "(stale LastPlayerMainHand, Acted==0) the old latch-only mechanism cannot detect this " +
            "live scenario even though the engine's own actor pointer names the wielder's own seat.");
        Assert.NotNull(pup.PuppetFingerprint);
    }

    [Fact]
    public void Tick_does_not_dominate_when_pointer_names_a_non_wielder_seat()
    {
        var fp = (mhp: 600, lvl: 50, br: 70, fa: 50);
        var (pup, mem, _, tracker, victim) = BuildPuppet(fp, job: 76, bandSlot: 24, puppetTurns: 1);

        tracker._lastPlayerMainHand = 0;
        mem.U8s[Offsets.Acted] = 0;

        // The pointer names the VICTIM's own frame -- not the wielder's. Neither path is open.
        PointActorAt(mem, victim);

        pup.Tick(onField: true);
        mem.U16s[victim + Offsets.AHp] = 80;
        pup.Tick(onField: true);

        Assert.False(AgencyBitSet(mem, victim));
        Assert.Null(pup.PuppetFingerprint);
    }

    // ---- non-lossy detection: rearm-unwritable is the one detectably-transient block Puppeteer has ----

    [Fact]
    public void Tick_retries_dominate_when_the_agency_byte_is_transiently_unwritable()
    {
        var fp = (mhp: 600, lvl: 50, br: 70, fa: 50);
        var (pup, mem, _, _, victim) = BuildPuppet(fp, job: 76, bandSlot: 24, puppetTurns: 1);

        pup.Tick(onField: true);   // baseline HP 100

        mem.WritableAddrs.Remove(victim + Puppeteer.AgencyOff);
        mem.U16s[victim + Offsets.AHp] = 80;
        pup.Tick(onField: true);
        Assert.False(AgencyBitSet(mem, victim),
            "an unwritable agency byte must not dominate, but must rearm the drop for retry");
        Assert.Null(pup.PuppetFingerprint);

        mem.WritableAddrs.Add(victim + Puppeteer.AgencyOff);
        pup.Tick(onField: true);   // no further HP change -- the rearmed drop re-detects
        Assert.True(AgencyBitSet(mem, victim));
        Assert.NotNull(pup.PuppetFingerprint);
    }

    // ---- bounded retry: an inactive tick consumes the drop -- reopening the gate must not resurrect it ----

    [Fact]
    public void Tick_bounded_retry_does_not_resurrect_a_drop_consumed_while_inactive()
    {
        var fp = (mhp: 600, lvl: 50, br: 70, fa: 50);
        var (pup, mem, _, tracker, victim) = BuildPuppet(fp, job: 76, bandSlot: 24, puppetTurns: 1);

        // Close BOTH paths for the drop tick.
        tracker._lastPlayerMainHand = 0;
        mem.U8s[Offsets.Acted] = 0;

        pup.Tick(onField: true);                    // baseline HP 100
        mem.U16s[victim + Offsets.AHp] = 80;        // HP drop while INACTIVE
        pup.Tick(onField: true);                    // consumed, verdict "inactive" -- no rearm
        Assert.False(AgencyBitSet(mem, victim));

        // Reopen the gate (latch path) with NO further HP change.
        tracker._lastPlayerMainHand = GalewindId;
        mem.U8s[Offsets.Acted] = 1;
        pup.Tick(onField: true);
        Assert.False(AgencyBitSet(mem, victim),
            "an inactive tick must consume the drop -- reopening the gate must not resurrect it");
        Assert.Null(pup.PuppetFingerprint);
    }

    // ---- cache resilience: the additive fingerprint cache survives a one-tick Readable() flap ----

    [Fact]
    public void Tick_dominates_through_a_transient_static_array_read_flap()
    {
        var fp = (mhp: 600, lvl: 50, br: 70, fa: 50);
        var (pup, mem, _, _, victim) = BuildPuppet(fp, job: 76, bandSlot: 24, puppetTurns: 1);

        pup.Tick(onField: true);   // tick 1: captures the fp cache (static array readable) + baselines HP

        mem.ReadableAddrs.Remove(Offsets.ArrayReadBase + Offsets.AMaxHp);   // one-tick flap
        mem.U16s[victim + Offsets.AHp] = 80;
        pup.Tick(onField: true);

        Assert.True(AgencyBitSet(mem, victim),
            "the cached enemy fingerprint set must survive a Readable flap on the drop tick");
    }

    // ---- ResetBattle clears the new cache field (precedent: KobuTests.ResetBattle_clears_the_cached_enemy_fingerprints) ----

    [Fact]
    public void ResetBattle_clears_the_cached_enemy_fingerprints()
    {
        var fp = (mhp: 600, lvl: 50, br: 70, fa: 50);
        var (pup, mem, _, _, victim) = BuildPuppet(fp, job: 76, bandSlot: 24, puppetTurns: 1);

        pup.Tick(onField: true);   // captures the fp cache + baselines HP
        pup.ResetBattle();

        // Nothing left to recapture -- the static-array slot goes unreadable for the new battle.
        mem.ReadableAddrs.Remove(Offsets.ArrayReadBase + Offsets.AMaxHp);

        pup.Tick(onField: true);   // fresh baseline (HP state also cleared by ResetBattle)
        mem.U16s[victim + Offsets.AHp] = 80;
        pup.Tick(onField: true);   // drop detected, but empty cache -> not-enemy, consumed

        Assert.False(AgencyBitSet(mem, victim),
            "ResetBattle must clear the cached enemy fingerprints -- an empty cache treats the drop as not-enemy");
    }

    // ---- victim-dead consume (D3): a kill-strike must not dominate a corpse, and must not burn the cooldown ----

    [Fact]
    public void Tick_victim_dead_consumes_without_dominating_or_burning_cooldown()
    {
        var fp = (mhp: 600, lvl: 50, br: 70, fa: 50);
        var (pup, mem, _, _, victim) = BuildPuppet(fp, job: 76, bandSlot: 24, puppetTurns: 1);

        pup.Tick(onField: true);                  // baseline HP 100
        mem.U16s[victim + Offsets.AHp] = 0;      // the wielder's hit was a kill strike
        pup.Tick(onField: true);
        Assert.False(AgencyBitSet(mem, victim), "a kill-strike must not dominate a corpse");
        Assert.Null(pup.PuppetFingerprint);

        // No wielder turn has elapsed (GlobalTurns unchanged) -- a fresh non-lethal hit on the SAME
        // enemy must still dominate: if the victim-dead verdict had wrongly stamped the cooldown,
        // this second dominate would be blocked (0 elapsed wielder-turns < PuppeteerCooldownTurns).
        mem.U16s[victim + Offsets.AMaxHp] = (ushort)fp.mhp;   // "revived" to full HP
        mem.U16s[victim + Offsets.AHp] = (ushort)fp.mhp;
        pup.Tick(onField: true);                              // re-baseline HP at the new value
        mem.U16s[victim + Offsets.AHp] = 80;                  // a real, non-lethal hit
        pup.Tick(onField: true);

        Assert.True(AgencyBitSet(mem, victim), "victim-dead must consume WITHOUT burning the cooldown");
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

    // ---- D1 REVISED (2026-07-04): identity match survives the revolving band MIRROR seat ----
    // Live log evidence proved plain address equality false-negatives: the mirror seat means one
    // unit legitimately exists at multiple band addresses, so Wielder.ResolveDeployedMainHand and
    // Band.ActorEntry can each return a DIFFERENT copy of the SAME wielder and never compare equal
    // by address. The frame nameId back-reference (Offsets.ANameId) is the mirror-safe identity
    // (Puppeteer.Policy.PointerNamesWielder).

    [Fact]
    public void Tick_dominates_when_the_pointer_names_a_mirror_copy_of_the_wielder()
    {
        // ---- LOAD-BEARING: the exact live-falsified scenario. Must fail under plain address-
        // equality pointerMatch and pass once identity (ANameId) drives the comparison. ----
        var mem = new FakeSparseMemory();
        var meta = new System.Collections.Generic.Dictionary<int, WeaponMeta>
        {
            [GalewindId] = new WeaponMeta
            {
                Name = "Galewind", Wp = 8, Cat = "Knife", Formula = 1, Flavor = "A windswept dagger",
                Signature = new WeaponSignature { AtTier = 3, PuppeteerTurns = 1, DisplayLabel = "Puppeteer" }
            }
        };
        var kills = new System.Collections.Generic.Dictionary<int, int> { [GalewindId] = Tuning.ProdThresholds[2] };
        var tracker = new KillTracker(kills, mem, new System.Collections.Generic.HashSet<int> { GalewindId });

        const int nameId = 42;
        const int wLvl = 60, wBr = 80, wFa = 55, wMhp = 350, wHp = 350;
        const int FirstCopySlot = 10, SecondCopySlot = 15;   // FirstCopySlot < SecondCopySlot: tier-1's
                                                              // homogeneous tie-break returns the FIRST.

        MemSeats.SeatRoster(mem, slot: 5, lvl: wLvl, br: wBr, fa: wFa, rh: GalewindId, nameId: nameId);

        // Two band copies of the SAME wielder (real position, matching fp, matching frame nameId) --
        // the revolving mirror seat's proven shape (MemSeats.SeatFrameNameId, Iai's precedent).
        MemSeats.SeatBand(mem, FirstCopySlot, weapon: GalewindId, lvl: wLvl, br: wBr, fa: wFa,
                          gx: 3, gy: 7, hp: wHp, maxHp: wMhp);
        MemSeats.SeatFrameNameId(mem, FirstCopySlot, nameId);
        MemSeats.SeatBand(mem, SecondCopySlot, weapon: GalewindId, lvl: wLvl, br: wBr, fa: wFa,
                          gx: 9, gy: 2, hp: wHp, maxHp: wMhp);
        MemSeats.SeatFrameNameId(mem, SecondCopySlot, nameId);

        // Latch fully CLOSED: no LastPlayerMainHand match, Acted 0 -- only the pointer path can open.
        tracker._lastPlayerMainHand = 0;
        mem.U8s[Offsets.Acted] = 0;

        // The engine's ActorPtr names the SECOND copy -- Wielder.ResolveDeployedMainHand's tier-1
        // tie-break resolves the FIRST copy's address instead (see FirstCopySlot/SecondCopySlot
        // above), so plain address equality can never match here.
        PointActorAt(mem, Band.Entry(SecondCopySlot));

        var turns = new TurnTracker(mem);
        long victim = Band.Entry(24);
        var enemyFp = (mhp: 600, lvl: 50, br: 70, fa: 50);
        SeatVictim(mem, victim, enemyFp, hp: 100, job: 76, agency: 0x50);
        SeatEnemyFp(mem, enemyFp);

        var pup = new Puppeteer(meta, kills, tracker, turns, mem: mem);

        pup.Tick(onField: true);                 // baseline HP 100 (no damage yet)
        mem.U16s[victim + Offsets.AHp] = 80;     // the wielder's hit dealt 20
        pup.Tick(onField: true);

        Assert.True(AgencyBitSet(mem, victim),
            "FAILED: the pointer named a MIRROR copy of the wielder (same identity, different band " +
            "address) -- address-equality pointerMatch cannot see this; nameId identity must.");
        Assert.NotNull(pup.PuppetFingerprint);
    }

    [Fact]
    public void Tick_does_not_dominate_when_the_pointer_names_a_foreign_actor()
    {
        var mem = new FakeSparseMemory();
        var meta = new System.Collections.Generic.Dictionary<int, WeaponMeta>
        {
            [GalewindId] = new WeaponMeta
            {
                Name = "Galewind", Wp = 8, Cat = "Knife", Formula = 1, Flavor = "A windswept dagger",
                Signature = new WeaponSignature { AtTier = 3, PuppeteerTurns = 1, DisplayLabel = "Puppeteer" }
            }
        };
        var kills = new System.Collections.Generic.Dictionary<int, int> { [GalewindId] = Tuning.ProdThresholds[2] };
        var tracker = new KillTracker(kills, mem, new System.Collections.Generic.HashSet<int> { GalewindId });

        const int nameId = 42, foreignNameId = 7;
        const int wLvl = 60, wBr = 80, wFa = 55, wMhp = 350, wHp = 350;
        const int WielderSlot = 25, ForeignSlot = 30;

        MemSeats.SeatRoster(mem, slot: 5, lvl: wLvl, br: wBr, fa: wFa, rh: GalewindId, nameId: nameId);
        MemSeats.SeatBand(mem, WielderSlot, weapon: GalewindId, lvl: wLvl, br: wBr, fa: wFa,
                          gx: 3, gy: 7, hp: wHp, maxHp: wMhp);
        MemSeats.SeatFrameNameId(mem, WielderSlot, nameId);

        // A foreign actor frame -- a different unit entirely -- whose OWN nameId reads 7, not 42.
        MemSeats.SeatBand(mem, ForeignSlot, weapon: 3, lvl: 40, br: 45, fa: 45, gx: 6, gy: 6, hp: 200, maxHp: 200);
        MemSeats.SeatFrameNameId(mem, ForeignSlot, foreignNameId);

        tracker._lastPlayerMainHand = 0;
        mem.U8s[Offsets.Acted] = 0;
        PointActorAt(mem, Band.Entry(ForeignSlot));

        var turns = new TurnTracker(mem);
        long victim = Band.Entry(24);
        var enemyFp = (mhp: 600, lvl: 50, br: 70, fa: 50);
        SeatVictim(mem, victim, enemyFp, hp: 100, job: 76, agency: 0x50);
        SeatEnemyFp(mem, enemyFp);

        var pup = new Puppeteer(meta, kills, tracker, turns, mem: mem);

        pup.Tick(onField: true);
        mem.U16s[victim + Offsets.AHp] = 80;
        pup.Tick(onField: true);

        Assert.False(AgencyBitSet(mem, victim),
            "the pointer names a DIFFERENT unit (foreign nameId 7 vs the wielder's resolved 42) -- " +
            "must not dominate");
        Assert.Null(pup.PuppetFingerprint);
    }

    // ---- flight taps: the dominate + release brackets are the kept diagnostic. (The per-tick recon
    // trace that cracked the release signal was retired once the fix landed -- LW-5, 2026-07-07.) ----

    [Fact]
    public void Flight_taps_dominate_then_release_own_turn()
    {
        var rec = new System.Collections.Generic.List<(string type, string payload)>();
        var fp = (mhp: 600, lvl: 50, br: 70, fa: 50);
        var (pup, mem, turns, _, victim) = BuildPuppet(fp, job: 76, bandSlot: 24, puppetTurns: 1, rec: rec);

        pup.Tick(onField: true);                 // baseline HP 100
        mem.U16s[victim + Offsets.AHp] = 80;     // the wielder's hit dealt 20 -> dominate
        pup.Tick(onField: true);
        Assert.Contains(rec, r => r.type == "pup" && r.payload.StartsWith("dominate"));

        UnitTakesATurn(mem, pup, turns, fp.mhp, fp.lvl, fp.br, fp.fa, hp: 80);   // the puppet's own turn
        Assert.False(AgencyBitSet(mem, victim));
        Assert.Contains(rec, r => r.type == "pup" && r.payload.StartsWith("release reason=own-turn"));
    }
}
