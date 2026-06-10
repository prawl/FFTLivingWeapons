using System.Runtime.InteropServices;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// Rod of Faith's "Rapture" signature. At +3, when the wielder's HP drops below RaptureHpPct
/// (30%) of max, Master Teleportation (movement id 243, Tuning.RaptureMoveId) is written and
/// HELD in the wielder's movement-ability field for RaptureTurns (3) of their turns, then the
/// PREVIOUS movement bytes are restored (the Maim save-once/hold/restore pattern + TurnTracker).
///
/// Pure jobs in Rapture.Policy.cs:
///   (1) IsActive: gates on raptureMove AND tier >= AtTier.
///   (2) IsBelow / ShouldArm: the integer hp-percent gate (ConditionMet's math); a dead
///       wielder (hp <= 0) NEVER arms (a corpse needs no teleport).
///   (3) IsExpired: window over after RaptureTurns completed wielder turns.
///   (4) CanRearm: hysteresis -- after a window, HP must recover to/above the threshold
///       before a new drop can arm again (else a low-HP wielder teleports forever).
///   (5) FieldFor / WriteField / ReadField: the 3-byte movement field image for the grant,
///       and the guarded save/hold/restore writes.
///   (6) RaptureState: the never-re-save invariant (saving while held would capture our own
///       teleport bytes and the restore would restore the grant).
/// </summary>
public class RaptureTests
{
    private static WeaponSignature RapSig(int atTier = 3) =>
        new() { AtTier = atTier, RaptureMove = true, DisplayLabel = "Rapture" };

    private sealed class FakeMemory : IGameMemory
    {
        private readonly System.Collections.Generic.Dictionary<long, byte> _bytes = new();
        public void Set8(long a, byte v) => _bytes[a] = v;
        public byte U8(long a) => _bytes.TryGetValue(a, out var v) ? v : (byte)0;
        public ushort U16(long a) => (ushort)(U8(a) | (U8(a + 1) << 8));
    }

    // ---- (1) IsActive ----

    [Fact]
    public void IsActive_false_when_no_signature()
        => Assert.False(Rapture.IsActive(null, tier: 3));

    [Fact]
    public void IsActive_false_when_not_a_rapture_weapon()
        => Assert.False(Rapture.IsActive(new WeaponSignature { AtTier = 3 }, tier: 3));

    [Fact]
    public void IsActive_false_below_tier()
        => Assert.False(Rapture.IsActive(RapSig(), tier: 2));

    [Fact]
    public void IsActive_true_at_and_above_tier()
    {
        Assert.True(Rapture.IsActive(RapSig(), tier: 3));
        Assert.True(Rapture.IsActive(RapSig(), tier: 4));
    }

    // ---- (2) IsBelow / ShouldArm: the 30% gate ----

    [Theory]
    [InlineData(29, 100, true)]
    [InlineData(30, 100, false)]   // exactly 30% is NOT below
    [InlineData(59, 200, true)]
    [InlineData(60, 200, false)]
    [InlineData(99, 100, false)]
    [InlineData(1, 100, true)]
    [InlineData(50, 0, false)]     // junk maxHp -> safe false
    public void IsBelow_gates_on_hp_percent(int hp, int maxHp, bool expected)
        => Assert.Equal(expected, Rapture.IsBelow(hp, maxHp, 0.30));

    [Fact]
    public void ShouldArm_never_arms_a_dead_wielder()
    {
        Assert.False(Rapture.ShouldArm(0, 100, 0.30));   // 0 < 30% but dead
        Assert.True(Rapture.ShouldArm(29, 100, 0.30));
    }

    // ---- (3) IsExpired: 3 completed wielder turns ----

    [Theory]
    [InlineData(0, 3, false)]
    [InlineData(2, 3, false)]
    [InlineData(3, 3, true)]
    [InlineData(4, 3, true)]
    public void IsExpired_after_the_turn_window(int turnsSinceArm, int window, bool expected)
        => Assert.Equal(expected, Rapture.IsExpired(turnsSinceArm, window));

    // ---- (4) CanRearm: hysteresis ----

    [Theory]
    [InlineData(false, true, false)]   // window spent, HP still low -> no instant re-arm
    [InlineData(false, false, true)]   // HP recovered to/above the threshold -> re-armable
    [InlineData(true, true, true)]     // already primed -> a drop arms
    [InlineData(true, false, true)]
    public void CanRearm_requires_recovery_between_windows(bool ready, bool below, bool expected)
        => Assert.Equal(expected, Rapture.CanRearm(ready, below));

    // ---- (5) FieldFor / WriteField / ReadField ----

    [Fact]
    public void FieldFor_master_teleportation_is_byte1_0x04()
    {
        var f = Rapture.FieldFor(243);
        Assert.NotNull(f);
        Assert.Equal(new byte[] { 0x00, 0x04, 0x00 }, f);
    }

    [Fact]
    public void FieldFor_rejects_an_id_outside_the_movement_field()
        => Assert.Null(Rapture.FieldFor(999));

    [Fact]
    public void WriteField_replaces_and_restore_brings_back_the_saved_movement()
    {
        var buf = new byte[256];
        buf[Offsets.AMovement] = 0x80;   // the player's own Move +1 (movement id 230)
        var h = GCHandle.Alloc(buf, GCHandleType.Pinned);
        try
        {
            long addr = h.AddrOfPinnedObject().ToInt64();
            byte[]? saved = Rapture.ReadField(addr);
            Assert.Equal(new byte[] { 0x80, 0x00, 0x00 }, saved);

            Rapture.WriteField(addr, Rapture.FieldFor(243)!);   // the grant REPLACES the field
            Assert.Equal(0x00, buf[Offsets.AMovement]);
            Assert.Equal(0x04, buf[Offsets.AMovement + 1]);

            Rapture.WriteField(addr, saved!);                   // restore the player's pick
            Assert.Equal(0x80, buf[Offsets.AMovement]);
            Assert.Equal(0x00, buf[Offsets.AMovement + 1]);
        }
        finally { h.Free(); }
    }

    // ---- (6) RaptureState: save-once / release ----

    [Fact]
    public void State_never_resaves_while_held()
    {
        var st = new RaptureState();
        Assert.False(st.Held);
        st.Arm(1000L, new byte[] { 0x80, 0, 0 }, baselineTurns: 2, fp: (30, 65, 70));
        Assert.True(st.Held);
        Assert.Equal(2, st.BaselineTurns);

        // A second arm while held must NOT overwrite the saved bytes (they hold the player's
        // movement; re-saving would capture our own teleport bytes).
        st.Arm(2000L, new byte[] { 0, 0x04, 0 }, baselineTurns: 5, fp: (1, 1, 1));
        Assert.Equal(new byte[] { 0x80, 0, 0 }, st.SavedField);
        Assert.Equal(2, st.BaselineTurns);
        Assert.Equal(1000L, st.Addr);
        Assert.Equal((30, 65, 70), st.Fp);   // the never-re-save invariant covers the fingerprint

        st.Release();
        Assert.False(st.Held);
        Assert.Null(st.SavedField);
    }

    [Fact]
    public void State_tracks_the_last_located_address_for_the_restore()
    {
        var st = new RaptureState();
        st.Arm(1000L, new byte[] { 0, 0, 0 }, baselineTurns: 0, fp: (30, 65, 70));
        st.Addr = 3000L;   // band entry relocated; restore must target the new copy
        Assert.Equal(3000L, st.Addr);
        Assert.True(st.Held);
    }

    // ---- (7) SameUnit: the held writes verify the armed wielder still owns the address ----
    // Band slots are FIXED addresses and units migrate between them (the Maim.Drive lesson):
    // without this check, a stale Addr would stamp the teleport image onto a stranger.

    [Fact]
    public void SameUnit_matches_only_the_armed_brave_and_faith()
    {
        var m = new FakeMemory();
        long e = 0x5000;
        m.Set8(e + Offsets.ALevel, 30); m.Set8(e + Offsets.ABrave, 65); m.Set8(e + Offsets.AFaith, 70);
        Assert.True(Rapture.SameUnit(m, e, (30, 65, 70)));
        Assert.True(Rapture.SameUnit(m, e, (31, 65, 70)));    // mid-window level-up must not break the hold
        Assert.False(Rapture.SameUnit(m, e, (30, 66, 70)));   // a stranger now occupies the slot
        Assert.False(Rapture.SameUnit(m, e, (30, 65, 71)));
        Assert.False(Rapture.SameUnit(m, 0x6000, (30, 65, 70)));   // unreadable/zeroed -> mismatch
    }
}
