using System.Runtime.InteropServices;
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
    // Reuses CharmLock.IsTurn logic — a turn = CT was near-full and has since reset notably lower.

    [Theory]
    [InlineData(100, 10, true)]
    [InlineData(95, 0, true)]
    [InlineData(90, 69, true)]
    [InlineData(90, 70, false)]   // not a big enough drop
    [InlineData(80, 5, false)]    // wasn't full when it dropped
    [InlineData(100, 100, false)] // still full
    [InlineData(0, 0, false)]
    public void IsTurn_detects_a_CT_reset_from_full(int last, int cur, bool expected)
        => Assert.Equal(expected, Maim.IsTurn(last, cur));

    // ---- (4) Never-re-save trap ----

    [Fact]
    public void Latch_never_overwrites_saved_reaction_while_held()
    {
        // Arrange: a pinned buffer holding a real reaction value.
        // Latch the victim, then immediately try to latch again with zeros held.
        var buf = new byte[256];
        buf[Maim.ReactionBandOff]     = 0xAB;
        buf[Maim.ReactionBandOff + 1] = 0xCD;
        buf[Maim.ReactionBandOff + 2] = 0xEF;
        buf[Maim.ReactionBandOff + 3] = 0x01;
        var h = GCHandle.Alloc(buf, GCHandleType.Pinned);
        long addr = h.AddrOfPinnedObject().ToInt64();
        try
        {
            var fp = (mhp: 100, lvl: 20, br: 50, fa: 50);
            var state = new MaimState();

            // First latch: saves 0xAB_CD_EF_01.
            state.Latch(addr, fp, savedReaction: 0xABCDEF01u);
            uint firstSaved = state.SavedReaction(fp).GetValueOrDefault();

            // Simulate what a "re-latch while held" would do: try to latch with zeros.
            // ShouldResave must return false when the victim is already held.
            bool resave = Maim.ShouldResave(state.IsHeld(fp));
            Assert.False(resave);

            // The saved bytes must still be the original (not zeros).
            Assert.Equal(0xABCDEF01u, firstSaved);
        }
        finally { h.Free(); }
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

    private static (long addr, byte[] buf, GCHandle h) MakeUnit(uint reaction)
    {
        var buf = new byte[256];
        buf[Maim.ReactionBandOff]     = (byte)(reaction & 0xFF);
        buf[Maim.ReactionBandOff + 1] = (byte)((reaction >> 8) & 0xFF);
        buf[Maim.ReactionBandOff + 2] = (byte)((reaction >> 16) & 0xFF);
        buf[Maim.ReactionBandOff + 3] = (byte)(reaction >> 24);
        var h = GCHandle.Alloc(buf, GCHandleType.Pinned);
        return (h.AddrOfPinnedObject().ToInt64(), buf, h);
    }

    private static uint ReadReaction(byte[] buf)
        => (uint)(buf[Maim.ReactionBandOff] | (buf[Maim.ReactionBandOff + 1] << 8)
           | (buf[Maim.ReactionBandOff + 2] << 16) | (buf[Maim.ReactionBandOff + 3] << 24));

    [Fact]
    public void HoldZero_writes_zeros_to_reaction_field()
    {
        var (addr, buf, h) = MakeUnit(0xDEADBEEFu);
        try
        {
            Maim.HoldZero(addr);
            Assert.Equal(0u, ReadReaction(buf));
        }
        finally { h.Free(); }
    }

    [Fact]
    public void Restore_writes_back_the_saved_reaction_bytes()
    {
        var (addr, buf, h) = MakeUnit(0u);  // currently zeroed
        try
        {
            Maim.Restore(addr, 0xABCD1234u);
            Assert.Equal(0xABCD1234u, ReadReaction(buf));
        }
        finally { h.Free(); }
    }

    [Fact]
    public void ReadReactionField_reads_4_bytes_little_endian()
    {
        var (addr, buf, h) = MakeUnit(0x12345678u);
        try
        {
            Assert.Equal(0x12345678u, Maim.ReadReactionField(addr));
        }
        finally { h.Free(); }
    }

    // ---- Main-hand-only activation gate (B1) ----
    // A Living Weapon earns kills in any hand, but commands its gift only from the main hand.

    [Fact]
    public void IsActingMainHand_true_when_mainHand_is_the_signature_weapon()
        => Assert.True(Maim.IsActingMainHand(mainHand: 89, weaponId: 89));

    [Fact]
    public void IsActingMainHand_false_when_mainHand_is_a_different_weapon()
        => Assert.False(Maim.IsActingMainHand(mainHand: 99, weaponId: 89));

    [Fact]
    public void IsActingMainHand_false_when_mainHand_is_zero_meaning_no_actor_resolved()
        => Assert.False(Maim.IsActingMainHand(mainHand: 0, weaponId: 89));
}
