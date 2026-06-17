using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// Huntress's "Maim" signature. At +3, enemies struck by the +3 wielder lose their reaction
/// abilities for 3 of their turns, then the saved bits restore. Re-hit refreshes the window;
/// allies are never latched; a re-hit while held never overwrites the saved state (so the
/// restore bytes remain the original reaction bits, not the zeros we're holding).
///
/// Pure jobs in Maim.Policy.cs:
///   (1) IsActive: gates on crippleTurns > 0 AND tier >= AtTier.
///   (2) ShouldLatch: enemy-fingerprint filter for the victim (same pattern as Ricochet).
///   (3) IsTurn: per-target turn counting from CT (reuse CharmLock.IsTurn pattern).
///   (4) Never-re-save trap: once a victim is held, a second hit must NOT overwrite the saved
///       reaction bytes (those are the original non-zeroed state; overwriting with zeros means
///       we'd restore zeros, losing the reaction permanently).
///   (5) Refresh: re-hit while held resets the turn counter but keeps the same saved bytes.
///
/// Stateful runtime in Maim.cs: victim latch mirrors Ricochet's HP-diff detection during the
/// acted period; save/hold/restore mirrors CharmLock's Drive pattern; per-target turn counting
/// mirrors CharmLock's IsTurn. All reads/writes VirtualQuery-guarded.
/// </summary>
public class MaimTests
{
    // Pinned buffers are committed addresses in our own process, so the production adapter's
    // RPM/WPM reads work on them for real -- the guard path is exercised, not faked.
    private static readonly LiveMemory Live = new();

    private static WeaponSignature MaimSig(int crippleTurns = 3, int atTier = 3) =>
        new() { AtTier = atTier, CrippleTurns = crippleTurns, DisplayLabel = "Maim" };

    // ---- (1) IsActive ----

    [Fact]
    public void IsActive_false_when_no_signature()
        => Assert.False(Maim.IsActive(null, tier: 3));

    [Fact]
    public void IsActive_false_when_crippleTurns_zero()
        => Assert.False(Maim.IsActive(new WeaponSignature { CrippleTurns = 0, AtTier = 3 }, tier: 3));

    [Fact]
    public void IsActive_false_below_tier()
        => Assert.False(Maim.IsActive(MaimSig(), tier: 2));

    [Fact]
    public void IsActive_true_at_and_above_tier()
    {
        Assert.True(Maim.IsActive(MaimSig(atTier: 3), tier: 3));
        Assert.True(Maim.IsActive(MaimSig(atTier: 3), tier: 4));
    }

    // ---- (2) ShouldLatch: enemy filter ----

    [Fact]
    public void ShouldLatch_true_for_enemy()
        => Assert.True(Maim.ShouldLatch(isEnemy: true));

    [Fact]
    public void ShouldLatch_false_for_ally()
        => Assert.False(Maim.ShouldLatch(isEnemy: false));

    // ---- (3) IsTurn: per-target turn counting off CT ----
    // Reuses CharmLock.IsTurn logic â€” a turn = CT was near-full and has since reset notably lower.

    [Theory]
    [InlineData(100, 10, true)]
    [InlineData(95, 0, true)]
    [InlineData(90, 69, true)]
    [InlineData(90, 70, false)]   // not a big enough drop
    [InlineData(80, 5, false)]    // wasn't full when it dropped
    [InlineData(100, 100, false)] // still full
    [InlineData(0, 0, false)]
    public void IsTurn_detects_a_CT_reset_from_full(int last, int cur, bool expected)
        => Assert.Equal(expected, CtTurns.IsTurn(last, cur));

    // ---- (4) Never-re-save trap ----

    [Fact]
    public void Latch_never_overwrites_saved_reaction_while_held()
    {
        // Arrange: a pinned buffer holding a real reaction value.
        // Latch the victim, then immediately try to latch again with zeros held.
        using var unit = PinnedBuf.Of(256);
        unit.Bytes[Maim.ReactionBandOff]     = 0xAB;
        unit.Bytes[Maim.ReactionBandOff + 1] = 0xCD;
        unit.Bytes[Maim.ReactionBandOff + 2] = 0xEF;
        unit.Bytes[Maim.ReactionBandOff + 3] = 0x01;

        var fp = (mhp: 100, lvl: 20, br: 50, fa: 50);
        var state = new MaimState();

        // First latch: saves 0xAB_CD_EF_01.
        state.Latch(unit.Addr, fp, savedReaction: 0xABCDEF01u);
        uint firstSaved = state.SavedReaction(fp).GetValueOrDefault();

        // Simulate what a "re-latch while held" would do: try to latch with zeros.
        // ShouldResave must return false when the victim is already held.
        bool resave = Maim.ShouldResave(state.IsHeld(fp));
        Assert.False(resave);

        // The saved bytes must still be the original (not zeros).
        Assert.Equal(0xABCDEF01u, firstSaved);
    }

    // ---- (5) Refresh: re-hit while held resets turn counter, keeps saved bytes ----

    [Fact]
    public void Refresh_resets_turn_counter_but_keeps_saved_reaction()
    {
        var fp = (mhp: 100, lvl: 20, br: 50, fa: 50);
        var state = new MaimState();
        state.Latch(1000L, fp, savedReaction: 0xDEADBEEFu);

        // Advance 1 turn on the victim.
        state.CountTurn(fp);

        // Re-hit: refresh resets the turn counter.
        state.Refresh(fp);
        Assert.Equal(0, state.TurnCount(fp));

        // But the saved bytes are unchanged.
        Assert.Equal(0xDEADBEEFu, state.SavedReaction(fp).GetValueOrDefault());
    }

    // ---- Expiry after N turns ----

    [Fact]
    public void Expires_after_crippleTurns_victim_turns()
    {
        var fp = (mhp: 100, lvl: 20, br: 50, fa: 50);
        var state = new MaimState();
        state.Latch(1000L, fp, savedReaction: 0x00000001u);

        Assert.False(state.IsExpired(fp, crippleTurns: 3));
        state.CountTurn(fp);
        Assert.False(state.IsExpired(fp, crippleTurns: 3));
        state.CountTurn(fp);
        Assert.False(state.IsExpired(fp, crippleTurns: 3));
        state.CountTurn(fp);
        Assert.True(state.IsExpired(fp, crippleTurns: 3));
    }

    // ---- Guarded reaction write (in-process buffer stands in for the band entry) ----

    private static PinnedBuf MakeUnit(uint reaction)
    {
        var unit = PinnedBuf.Of(256);
        unit.Bytes[Maim.ReactionBandOff]     = (byte)(reaction & 0xFF);
        unit.Bytes[Maim.ReactionBandOff + 1] = (byte)((reaction >> 8) & 0xFF);
        unit.Bytes[Maim.ReactionBandOff + 2] = (byte)((reaction >> 16) & 0xFF);
        unit.Bytes[Maim.ReactionBandOff + 3] = (byte)(reaction >> 24);
        return unit;
    }

    private static uint ReadReaction(byte[] buf)
        => (uint)(buf[Maim.ReactionBandOff] | (buf[Maim.ReactionBandOff + 1] << 8)
           | (buf[Maim.ReactionBandOff + 2] << 16) | (buf[Maim.ReactionBandOff + 3] << 24));

    [Fact]
    public void HoldZero_writes_zeros_to_reaction_field()
    {
        using var unit = MakeUnit(0xDEADBEEFu);
        Maim.HoldZero(Live, unit.Addr);
        Assert.Equal(0u, ReadReaction(unit.Bytes));
    }

    [Fact]
    public void Restore_writes_back_the_saved_reaction_bytes()
    {
        using var unit = MakeUnit(0u);  // currently zeroed
        Maim.Restore(Live, unit.Addr, 0xABCD1234u);
        Assert.Equal(0xABCD1234u, ReadReaction(unit.Bytes));
    }

    [Fact]
    public void ReadReactionField_reads_4_bytes_little_endian()
    {
        using var unit = MakeUnit(0x12345678u);
        Assert.Equal(0x12345678u, Maim.ReadReactionField(Live, unit.Addr));
    }

    // ---- Main-hand-only activation gate (B1) ----
    // A Living Weapon earns kills in any hand, but commands its gift only from the main hand.

    [Fact]
    public void IsActingMainHand_true_when_mainHand_is_the_signature_weapon()
        => Assert.True(Signatures.IsActingMainHand(mainHand: 89, weaponId: 89));

    [Fact]
    public void IsActingMainHand_false_when_mainHand_is_a_different_weapon()
        => Assert.False(Signatures.IsActingMainHand(mainHand: 99, weaponId: 89));

    [Fact]
    public void IsActingMainHand_false_when_mainHand_is_zero_meaning_no_actor_resolved()
        => Assert.False(Signatures.IsActingMainHand(mainHand: 0, weaponId: 89));

    // ---- Integration: latch -> count the victim's OWN turns -> restore (the real Tick path) ----
    // Regression for the 1.5 live failure (maim never unlatched after 3+ enemy turns). Two coupled
    // bugs the live probe exposed: (a) the expiry counted turns off band +0x09, a DEAD byte that
    // stays flat 0 -- the live charge-time for enemies is band +0x25 (== Offsets.ACtSlam, what
    // CharmLock reads); (b) the count + expiry ran only inside the onField branch, but an enemy's CT
    // crosses 90->below-70 during ITS OWN turn, which is an ENEMY-turn frame (off-field). So a maimed
    // enemy must regain its reaction after CrippleTurns of its own turns counted across OFF-FIELD ticks.

    private const int HuntressId = 89;
    private const int LiveCtOff = 0x25;   // band-relative live charge-time (== Offsets.ACtSlam)

    private static (Maim maim, FakeSparseMemory mem, long victim) BuildMaimedVictim(
        (int mhp, int lvl, int br, int fa) fp, int bandSlot = 24, int crippleTurns = 3, int seedCt = 50)
    {
        var mem = new FakeSparseMemory();
        var meta = new Dictionary<int, WeaponMeta>
        {
            [HuntressId] = new WeaponMeta
            {
                Name = "Huntress", Wp = 8, Cat = "Bow", Formula = 2,
                Flavor = "A hunter's bow", Signature = MaimSig(crippleTurns: crippleTurns, atTier: 3)
            }
        };
        var kills = new Dictionary<int, int> { [HuntressId] = Tuning.ProdThresholds[2] };   // tier 3
        var tracker = new KillTracker(new Dictionary<int, int>(), mem, new HashSet<int>());
        tracker._lastPlayerMainHand = HuntressId;                 // the wielder is the last player to act
        mem.U8s[Offsets.Acted] = 1;                               // and it is acting this turn

        long victim = Band.Entry(bandSlot);
        SeatVictim(mem, victim, fp, hp: 100, reaction: 0x00080000u, ct: seedCt);   // 0x00080000 = Counter
        SeatEnemyFp(mem, fp);                                     // recognized as an enemy

        var maim = new Maim(meta, kills, tracker, mem: mem);
        return (maim, mem, victim);
    }

    private static void SeatVictim(FakeSparseMemory mem, long addr,
        (int mhp, int lvl, int br, int fa) fp, int hp, uint reaction, int ct)
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
        mem.ReadableAddrs.Add(addr + Maim.ReactionBandOff);       // reaction field (4 bytes @ +0x78)
        mem.WritableAddrs.Add(addr + Maim.ReactionBandOff);
        for (int i = 0; i < 4; i++) mem.U8s[addr + Maim.ReactionBandOff + i] = (byte)((reaction >> (8 * i)) & 0xFF);
        mem.U8s[addr + LiveCtOff] = (byte)ct;                     // live charge-time @ +0x25
    }

    private static void SeatEnemyFp(FakeSparseMemory mem, (int mhp, int lvl, int br, int fa) fp)
    {
        long slot = Offsets.ArrayReadBase;                        // static-array slot 0 (enemy side)
        mem.ReadableAddrs.Add(slot + Offsets.AMaxHp);
        mem.U16s[slot + Offsets.AMaxHp] = (ushort)fp.mhp;
        mem.U8s[slot + Offsets.ALevel] = (byte)fp.lvl;
        mem.U8s[slot + Offsets.ABrave] = (byte)fp.br;
        mem.U8s[slot + Offsets.AFaith] = (byte)fp.fa;
    }

    private static uint ReadReactionAt(FakeSparseMemory mem, long addr)
        => (uint)(mem.U8(addr + Maim.ReactionBandOff)
                | (mem.U8(addr + Maim.ReactionBandOff + 1) << 8)
                | (mem.U8(addr + Maim.ReactionBandOff + 2) << 16)
                | (mem.U8(addr + Maim.ReactionBandOff + 3) << 24));

    private static void TakeOneVictimTurn(Maim maim, FakeSparseMemory mem, long victim)
    {
        mem.U8s[victim + LiveCtOff] = 95; maim.Tick(onField: false);   // charged, about to act (enemy turn)
        mem.U8s[victim + LiveCtOff] = 10; maim.Tick(onField: false);   // acted -> CT reset = one turn taken
    }

    [Fact]
    public void Tick_suppresses_reaction_on_a_struck_enemy()
    {
        var fp = (mhp: 600, lvl: 50, br: 70, fa: 50);
        var (maim, mem, victim) = BuildMaimedVictim(fp);

        maim.Tick(onField: true);                       // baseline HP 100 (no damage yet)
        mem.U16s[victim + Offsets.AHp] = 80;            // the wielder's hit dealt 20
        maim.Tick(onField: true);

        Assert.Equal(0u, ReadReactionAt(mem, victim));  // reaction zeroed = suppressed
    }

    [Fact]
    public void Tick_restores_reaction_after_three_victim_turns_counted_off_field()
    {
        var fp = (mhp: 600, lvl: 50, br: 70, fa: 50);
        var (maim, mem, victim) = BuildMaimedVictim(fp, crippleTurns: 3);

        maim.Tick(onField: true);                       // baseline
        mem.U16s[victim + Offsets.AHp] = 80;            // wielder's hit -> latch + zero
        maim.Tick(onField: true);
        Assert.Equal(0u, ReadReactionAt(mem, victim));  // suppressed

        // The victim takes 3 of its OWN turns -- all on ENEMY-turn (off-field) frames.
        TakeOneVictimTurn(maim, mem, victim);
        TakeOneVictimTurn(maim, mem, victim);
        Assert.Equal(0u, ReadReactionAt(mem, victim));  // still suppressed at 2 turns

        TakeOneVictimTurn(maim, mem, victim);
        Assert.Equal(0x00080000u, ReadReactionAt(mem, victim));   // restored after the 3rd turn
    }

    [Fact]
    public void Tick_does_not_restore_early_before_crippleTurns()
    {
        var fp = (mhp: 600, lvl: 50, br: 70, fa: 50);
        var (maim, mem, victim) = BuildMaimedVictim(fp, crippleTurns: 3);

        maim.Tick(onField: true);
        mem.U16s[victim + Offsets.AHp] = 80;
        maim.Tick(onField: true);

        TakeOneVictimTurn(maim, mem, victim);           // only 1 of 3 turns
        Assert.Equal(0u, ReadReactionAt(mem, victim));  // must stay suppressed
    }

    // ---- Latch survives an off-field-animation hit ----
    // Regression: Observe ate the HP-drop delta during the off-field animation tick (active=false),
    // so the on-field acted tick saw no drop and never latched. The fix: Observe + latch run only
    // when onField is true -- so the delta survives until the acted window reopens.

    [Fact]
    public void Tick_latches_when_hit_lands_during_off_field_animation()
    {
        var fp = (600, 50, 70, 50);
        var (maim, mem, victim) = BuildMaimedVictim(fp);

        maim.Tick(onField: true);                       // baseline: HP 100 observed on-field
        mem.U16s[victim + Offsets.AHp] = 80;            // hit lands during attack animation ...
        maim.Tick(onField: false);                      // ... which is an off-field frame
        maim.Tick(onField: true);                       // acted window reopens; HP still 80 (no new hit)

        Assert.Equal(0u, ReadReactionAt(mem, victim));  // latch must fire -> reaction zeroed
    }

    // ---- Twin band entry must not thrash CT counting into premature expiry ----
    // Regression: the band-scan loop counted CT for EVERY slot whose fingerprint matched a held
    // victim. A frozen twin at a different slot with a different CT caused lastCt to thrash
    // (50 <-> 100) every tick, making IsTurn(100, 50) fire on nearly every tick -> 3 spurious
    // counts -> latch expired without the enemy ever taking a real turn.
    // The fix: CT is read only from HeldAddr (Drive), never from the band scan.

    [Fact]
    public void Tick_twin_band_entry_does_not_cause_premature_expiry()
    {
        var fp = (600, 50, 70, 50);
        // Slot 24 = the live hit target; slot 26 = a frozen twin with the same fingerprint.
        var (maim, mem, victim) = BuildMaimedVictim(fp, bandSlot: 24, crippleTurns: 3, seedCt: 50);

        // Latch: baseline tick then HP drop on the live slot.
        maim.Tick(onField: true);                       // baseline HP 100
        mem.U16s[victim + Offsets.AHp] = 80;
        maim.Tick(onField: true);                       // latch fires; HeldAddr = Band.Entry(24)
        Assert.Equal(0u, ReadReactionAt(mem, victim));  // suppressed

        // Seat a frozen twin at slot 26 with the SAME fingerprint and a high, frozen CT (100).
        // HP is not dropping here, so the twin never latches.
        long twin = Band.Entry(26);
        SeatVictim(mem, twin, fp, hp: 100, reaction: 0x00080000u, ct: 100);

        // Hold slot 24 CT steady at 50 (no turn threshold crossed); run 8 off-field ticks.
        // If the band loop reads both slots' CT the lastCt will thrash 50<->100 and fire
        // IsTurn(100, 50) on most ticks -> 3 spurious counts -> early restore.
        mem.U8s[victim + LiveCtOff] = 50;
        mem.U8s[twin   + LiveCtOff] = 100;
        for (int i = 0; i < 8; i++) maim.Tick(onField: false);

        Assert.Equal(0u, ReadReactionAt(mem, victim));  // still suppressed: twin must not thrash count
    }
}
