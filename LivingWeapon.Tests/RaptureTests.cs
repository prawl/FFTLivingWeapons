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
///   (3)/(4) retired: the turn-cap and its hysteresis are gone -- the window releases when
///       the wielder RECOVERS to/above the threshold (HasRecovered, RaptureWindowTests).
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

    // ---- (3)/(4) retired with the turn-cap: the window now releases on RECOVERY (HasRecovered,
    // RaptureWindowTests) -- the live-verified release -- not on a turn count. ----

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

    // ---- (5b) ReadBackSet: the once-per-window live-test signal for the held bit ----
    // RaptureMoveId 243 (Master Teleportation) is CUT content per FOLDABLE_ABILITIES, so the
    // engine honoring its movement bit is unverified -- the arm-time read-back (SET/MISS in
    // the log) settles it in one live battle, mirroring Spiritual Font's verdict convention.

    [Fact]
    public void ReadBackSet_reports_whether_the_held_bit_survived()
    {
        var m = new FakeMemory();
        long e = 0x5000;
        Assert.False(Rapture.ReadBackSet(m, e, 243));    // engine zeroed the cut ability's bit -> MISS
        m.Set8(e + Offsets.AMovement + 1, 0x04);         // byte1 0x04 = Master Teleportation
        Assert.True(Rapture.ReadBackSet(m, e, 243));     // SET
        Assert.False(Rapture.ReadBackSet(m, e, 999));    // out-of-field id can never read SET
    }

    // ---- (6) RaptureState: save-once / release ----

    [Fact]
    public void State_never_resaves_while_held()
    {
        var st = new RaptureState();
        Assert.False(st.Held);
        st.Arm(1000L, new byte[] { 0x80, 0, 0 }, baselineTurns: 2, fp: (30, 65, 70),
               grant: new byte[] { 0, 0x04, 0 });
        Assert.True(st.Held);
        Assert.Equal(2, st.BaselineTurns);

        // A second arm while held must NOT overwrite the saved bytes (they hold the player's
        // movement; re-saving would capture our own teleport bytes).
        st.Arm(2000L, new byte[] { 0, 0x04, 0 }, baselineTurns: 5, fp: (1, 1, 1),
               grant: new byte[] { 0xFF, 0xFF, 0xFF });
        Assert.Equal(new byte[] { 0x80, 0, 0 }, st.SavedField);
        Assert.Equal(2, st.BaselineTurns);
        Assert.Equal(1000L, st.Addr);
        Assert.Equal((30, 65, 70), st.Fp);   // the never-re-save invariant covers the fingerprint
        Assert.Equal(new byte[] { 0, 0x04, 0 }, st.GrantField);   // ...and the grant image

        st.Release();
        Assert.False(st.Held);
        Assert.Null(st.SavedField);
    }

    [Fact]
    public void State_tracks_the_last_located_address_for_the_restore()
    {
        var st = new RaptureState();
        st.Arm(1000L, new byte[] { 0, 0, 0 }, baselineTurns: 0, fp: (30, 65, 70),
               grant: new byte[] { 0, 0x04, 0 });
        st.Addr = 3000L;   // band entry relocated; restore must target the new copy
        Assert.Equal(3000L, st.Addr);
        Assert.True(st.Held);
    }

    [Fact]
    public void State_holds_its_own_copy_of_the_grant_image()
    {
        var st = new RaptureState();
        var grant = new byte[] { 0x01, 0x84, 0x00 };
        st.Arm(1000L, new byte[] { 0x80, 0, 0 }, baselineTurns: 0, fp: (30, 65, 70), grant: grant);
        grant[0] = 0xFF;   // a mutating caller buffer must not bend the held image
        Assert.Equal(new byte[] { 0x01, 0x84, 0x00 }, st.GrantField);
        st.Release();
        Assert.Null(st.GrantField);
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
