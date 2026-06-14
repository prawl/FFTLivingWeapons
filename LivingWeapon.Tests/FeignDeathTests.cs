using System;
using LivingWeapon;
using Xunit;
using static LivingWeapon.FeignDeath;

namespace LivingWeapon.Tests;

/// <summary>
/// Wrathblade's "Feign Death" signature (+3). A lethal hit becomes a played-dead corpse that acts for
/// ~2 turns (ignored: Invisible held), then the finishing blow + the engine's Reraise stand it up at
/// ~10% HP, KO state cleared. ONCE per battle.
///
/// Pure jobs in FeignDeath.Policy.cs, tested directly here:
///   (1) IsActive: feignDeath flag AND tier >= AtTier (mirrors Rapture.IsActive).
///   (2) IsReviveEdge: the 0 -> positive transition that ends the once-per-battle window.
///   (3) Step: the whole played-dead state machine -- the dead-bit / revive-edge interplay that the
///       throwaway probe got wrong twice is pinned here so it can't regress (set the dead bit at the
///       finishing blow so Reraise sees a death; do NOT re-stamp it through the revive; clear it in
///       Recover so the stand-up leaves no hearts).
///   (4) SetReraise/SetInvisible/SetDead + HoldAlive/ForceKill: the guarded bit/HP writes -- exercised
///       for real against a PinnedBuf (a committed address; the production RPM/WPM guard path runs).
///   (5) ActivatesOnMainHandOnly: the main-hand-only activation contract.
/// The full Tick orchestration (roster -> band locate -> apply) is the live-verified integration
/// (proven 2026-06-14, the possum probe this runtime is ported from).
/// </summary>
public class FeignDeathTests
{
    // Pinned buffers are committed addresses in our own process, so LiveMemory's RPM/WPM operate on
    // them for real -- the guard path is exercised, not faked.
    private static readonly LiveMemory Live = new();

    private static WeaponSignature FeignSig(int atTier = 3) =>
        new() { AtTier = atTier, FeignDeath = true, DisplayLabel = "Feign Death" };

    // ---- (1) IsActive ----

    [Fact]
    public void IsActive_false_when_no_signature()
        => Assert.False(FeignDeath.IsActive(null, tier: 3));

    [Fact]
    public void IsActive_false_when_not_a_feign_weapon()
        => Assert.False(FeignDeath.IsActive(new WeaponSignature { AtTier = 3 }, tier: 3));

    [Fact]
    public void IsActive_false_below_tier()
        => Assert.False(FeignDeath.IsActive(FeignSig(), tier: 2));

    [Fact]
    public void IsActive_true_at_and_above_tier()
    {
        Assert.True(FeignDeath.IsActive(FeignSig(), tier: 3));
        Assert.True(FeignDeath.IsActive(FeignSig(), tier: 4));
    }

    // ---- (2) IsReviveEdge: the corpse stood back up ----

    [Theory]
    [InlineData(true, 45, true)]    // was dead, now alive -> the revive edge (Reraise fired)
    [InlineData(true, 1, true)]
    [InlineData(false, 45, false)]  // never died -> not a feign revive
    [InlineData(true, 0, false)]    // still a corpse -> hold, don't release yet
    [InlineData(false, 0, false)]
    public void IsReviveEdge_only_on_dead_to_alive(bool wasDead, int hp, bool expected)
        => Assert.Equal(expected, FeignDeath.IsReviveEdge(wasDead, hp));

    // ---- (3) Step: the played-dead state machine ----

    [Fact]
    public void Step_watching_stays_while_the_wielder_is_alive()
    {
        var a = Step(Phase.Watching, hp: 450, dead: false, sawAlive: true,
                     possumDone: false, recoverElapsed: false, finishKilled: false, finishWasDead: false);
        Assert.Equal(Phase.Watching, a.Next);
        Assert.Null(a.Dead);          // no writes while merely watching
        Assert.Null(a.Invisible);
        Assert.Equal(HpAction.None, a.Hp);
    }

    [Fact]
    public void Step_watching_does_not_arm_before_the_wielder_was_seen_alive()
    {
        // A unit located already-dead (sawAlive==false) must NOT feign -- it was a corpse, not a kill.
        var a = Step(Phase.Watching, hp: 0, dead: true, sawAlive: false,
                     possumDone: false, recoverElapsed: false, finishKilled: false, finishWasDead: false);
        Assert.Equal(Phase.Watching, a.Next);
    }

    [Fact]
    public void Step_watching_enters_possum_on_a_lethal_hit_by_hp()
    {
        var a = Step(Phase.Watching, hp: 0, dead: false, sawAlive: true,
                     possumDone: false, recoverElapsed: false, finishKilled: false, finishWasDead: false);
        Assert.Equal(Phase.Possum, a.Next);
    }

    [Fact]
    public void Step_watching_enters_possum_on_the_dead_bit_even_if_hp_unread()
    {
        // A scripted status-death can set the bit before the HP write the poll would catch.
        var a = Step(Phase.Watching, hp: 200, dead: true, sawAlive: true,
                     possumDone: false, recoverElapsed: false, finishKilled: false, finishWasDead: false);
        Assert.Equal(Phase.Possum, a.Next);
    }

    [Fact]
    public void Step_possum_holds_prone_alive_and_invisible()
    {
        var a = Step(Phase.Possum, hp: 1, dead: false, sawAlive: true,
                     possumDone: false, recoverElapsed: false, finishKilled: false, finishWasDead: false);
        Assert.Equal(Phase.Possum, a.Next);
        Assert.Equal(false, a.Dead);            // dead bit cleared -> prone but able to act
        Assert.Equal(true, a.Invisible);        // re-stamped each tick (breaks on action)
        Assert.Equal(HpAction.HoldAlive, a.Hp); // hold HP at 1
        Assert.Null(a.Reraise);                 // Reraise NOT shown during play (stealth)
    }

    [Fact]
    public void Step_possum_advances_to_finish_when_the_window_is_done()
    {
        var a = Step(Phase.Possum, hp: 1, dead: false, sawAlive: true,
                     possumDone: true, recoverElapsed: false, finishKilled: false, finishWasDead: false);
        Assert.Equal(Phase.Finish, a.Next);
    }

    [Fact]
    public void Step_finish_deals_the_blow_with_the_dead_bit_set()
    {
        // THE regression: the engine does not flag dead on a memory HP write, so the finishing blow
        // must set the dead bit or Reraise has no death to undo (the "no stand-up" probe run).
        var a = Step(Phase.Finish, hp: 1, dead: false, sawAlive: true,
                     possumDone: false, recoverElapsed: false, finishKilled: false, finishWasDead: false);
        Assert.Equal(Phase.Finish, a.Next);
        Assert.Equal(HpAction.ForceKill, a.Hp);
        Assert.Equal(true, a.Dead);      // <-- without this, nothing revives
        Assert.Equal(true, a.Reraise);   // held through the death
        Assert.Equal(false, a.Invisible);
        Assert.True(a.MarkKilled);
    }

    [Fact]
    public void Step_finish_keeps_playing_dead_until_the_wielder_is_up_next_then_strikes()
    {
        // Killing the wielder early leaves it dead-and-scheduled for a long climb (engine crash). So
        // until it is "up next" (upNext=false) keep it prone + alive + ignored; strike only at up-next.
        var wait = Step(Phase.Finish, hp: 1, dead: false, sawAlive: true,
                        possumDone: false, recoverElapsed: false, finishKilled: false, finishWasDead: false,
                        otherAllyAlive: true, upNext: false);
        Assert.NotEqual(HpAction.ForceKill, wait.Hp);   // do NOT strike yet
        Assert.Equal(HpAction.HoldAlive, wait.Hp);      // keep it alive (prone)
        Assert.Equal(false, wait.Dead);                 // not flagged dead while waiting
        Assert.Equal(true, wait.Invisible);             // still ignored
        Assert.False(wait.MarkKilled);

        var strike = Step(Phase.Finish, hp: 1, dead: false, sawAlive: true,
                          possumDone: false, recoverElapsed: false, finishKilled: false, finishWasDead: false,
                          otherAllyAlive: true, upNext: true);
        Assert.Equal(HpAction.ForceKill, strike.Hp);    // up next -> strike (brief dead state)
        Assert.Equal(true, strike.Dead);
        Assert.True(strike.MarkKilled);
    }

    [Fact]
    public void Step_finish_holds_dead_while_the_corpse_waits_for_reraise()
    {
        var a = Step(Phase.Finish, hp: 0, dead: true, sawAlive: true,
                     possumDone: false, recoverElapsed: false, finishKilled: true, finishWasDead: false);
        Assert.Equal(Phase.Finish, a.Next);
        Assert.Null(a.Dead);             // do NOT re-stamp dead -- it stays set on its own; re-setting
                                         // it at the corpse's turn fought the engine's auto-Reraise
        Assert.Equal(true, a.Reraise);   // Reraise IS held (the death-commit would clear it)
        Assert.True(a.MarkWasDead);
    }

    [Fact]
    public void Step_finish_hands_off_to_recover_on_the_revive_edge_without_restamping_dead()
    {
        // THE other regression: re-stamping the dead bit on the revive left the hearts / skipped turn.
        var a = Step(Phase.Finish, hp: 45, dead: false, sawAlive: true,
                     possumDone: false, recoverElapsed: false, finishKilled: true, finishWasDead: true);
        Assert.Equal(Phase.Recover, a.Next);
        Assert.Null(a.Dead);   // <-- do NOT re-set dead here; Recover clears it
    }

    [Fact]
    public void Step_finish_force_kills_only_when_another_ally_is_alive()
    {
        var a = Step(Phase.Finish, hp: 1, dead: false, sawAlive: true,
                     possumDone: false, recoverElapsed: false, finishKilled: false, finishWasDead: false,
                     otherAllyAlive: true);
        Assert.Equal(HpAction.ForceKill, a.Hp);
        Assert.Equal(true, a.Dead);
        Assert.False(a.Spent);
    }

    [Fact]
    public void Step_finish_degrades_to_survival_when_last_unit_standing()
    {
        // Force-killing the last living party unit would be a party wipe (game over) before Reraise
        // could fire. Degrade: keep the wielder alive at 1 HP, drop the ignored state, end the feign.
        var a = Step(Phase.Finish, hp: 1, dead: false, sawAlive: true,
                     possumDone: false, recoverElapsed: false, finishKilled: false, finishWasDead: false,
                     otherAllyAlive: false);
        Assert.NotEqual(HpAction.ForceKill, a.Hp);   // <-- never manufacture a wipe
        Assert.Equal(HpAction.HoldAlive, a.Hp);      // survive at 1 HP
        Assert.Equal(false, a.Invisible);            // drop the ignored state
        Assert.Null(a.Dead);                         // never flagged dead
        Assert.Null(a.Reraise);                      // no death coming -> no Reraise
        Assert.True(a.Spent);                        // feign over (graceful)
    }

    [Fact]
    public void Step_recover_clears_the_ko_state_and_holds_alive()
    {
        var a = Step(Phase.Recover, hp: 45, dead: true, sawAlive: true,
                     possumDone: false, recoverElapsed: false, finishKilled: true, finishWasDead: true);
        Assert.Equal(Phase.Recover, a.Next);
        Assert.Equal(false, a.Dead);            // hold the KO bit cleared -> no hearts, turn not skipped
        Assert.Equal(false, a.Reraise);         // spent, dropped
        Assert.Equal(HpAction.HoldAlive, a.Hp); // never slip back to 0
        Assert.False(a.Spent);
    }

    [Fact]
    public void Step_recover_marks_spent_when_the_window_elapses()
    {
        var a = Step(Phase.Recover, hp: 45, dead: false, sawAlive: true,
                     possumDone: false, recoverElapsed: true, finishKilled: true, finishWasDead: true);
        Assert.True(a.Spent);
    }

    // ---- ShouldRearm: restart / full-heal re-arms the once-per-battle feign ----

    [Theory]
    [InlineData(true, 450, 450, false, true)]   // spent + back at full HP -> re-arm (restart)
    [InlineData(true, 449, 450, false, false)]  // spent but not full -> stay spent
    [InlineData(true, 45, 450, false, false)]   // spent, freshly revived at 10% -> stay spent
    [InlineData(false, 450, 450, false, false)] // not spent -> nothing to re-arm
    [InlineData(true, 450, 450, true, false)]   // full HP but flagged dead -> not a clean re-arm
    [InlineData(true, 0, 0, false, false)]      // garbage / unread maxHp -> never re-arm
    public void ShouldRearm_only_on_a_spent_wielder_back_at_full_hp(bool spent, int hp, int maxHp, bool dead, bool expected)
        => Assert.Equal(expected, FeignDeath.ShouldRearm(spent, hp, maxHp, dead));

    // ---- TurnEnded: the active-struct turn-edge that counts the wielder's possum turns ----

    [Theory]
    [InlineData(true, false, true)]     // was active, now not -> the wielder's turn just ended
    [InlineData(true, true, false)]     // still active -> mid-turn, not ended
    [InlineData(false, true, false)]    // just became active -> turn starting, not ended
    [InlineData(false, false, false)]   // not active either tick -> no edge
    public void TurnEnded_only_on_active_to_inactive(bool wasActive, bool nowActive, bool expected)
        => Assert.Equal(expected, FeignDeath.TurnEnded(wasActive, nowActive));

    // ---- Elapsed: the wall-clock window ----

    [Fact]
    public void Elapsed_is_true_only_at_or_past_the_window()
    {
        var t0 = new DateTime(2026, 6, 14, 12, 0, 0, DateTimeKind.Utc);
        Assert.False(FeignDeath.Elapsed(t0, t0.AddSeconds(17.9), 18.0));
        Assert.True(FeignDeath.Elapsed(t0, t0.AddSeconds(18.0), 18.0));
        Assert.True(FeignDeath.Elapsed(t0, t0.AddSeconds(40), 18.0));
    }

    // ---- (4) the guarded bit / HP writes against a real PinnedBuf ----

    [Fact]
    public void Status_bits_are_at_the_documented_band_offsets()
    {
        Assert.Equal(0x47, Offsets.AReraise);
        Assert.Equal(0x20, Offsets.AReraiseBit);
        Assert.Equal(0x47, Offsets.AInvisible);   // shares the byte with Reraise
        Assert.Equal(0x10, Offsets.AInvisibleBit);
        Assert.Equal(0x45, Offsets.ADeadStatus);
        Assert.Equal(0x20, Offsets.ADeadBit);
    }

    [Fact]
    public void SetReraise_holds_then_drops_the_bit_preserving_other_status_bits()
    {
        using var e = PinnedBuf.Of(256);
        e.Bytes[Offsets.AReraise] = 0x01;   // some OTHER status bit already set in this byte
        Assert.False(FeignDeath.HasReraise(Live, e.Addr));

        FeignDeath.SetReraise(Live, e.Addr, on: true);    // hold Reraise
        Assert.True(FeignDeath.HasReraise(Live, e.Addr));
        Assert.Equal(0x21, e.Bytes[Offsets.AReraise]);     // 0x20 reraise | 0x01 preserved

        FeignDeath.SetReraise(Live, e.Addr, on: false);   // drop it (spent)
        Assert.False(FeignDeath.HasReraise(Live, e.Addr));
        Assert.Equal(0x01, e.Bytes[Offsets.AReraise]);     // the other bit is untouched
    }

    [Fact]
    public void SetReraise_re_applies_after_the_death_clears_the_bit()
    {
        // The mechanism that makes "held == permanent": the death-commit clears +0x47; the next hold
        // re-stamps it, so the engine still sees Reraise when the corpse's CT reaches 100.
        using var e = PinnedBuf.Of(256);
        FeignDeath.SetReraise(Live, e.Addr, on: true);
        Assert.True(FeignDeath.HasReraise(Live, e.Addr));

        e.Bytes[Offsets.AReraise] = 0x00;   // the engine's death-commit wipes the status byte
        Assert.False(FeignDeath.HasReraise(Live, e.Addr));

        FeignDeath.SetReraise(Live, e.Addr, on: true);   // the hold re-applies it
        Assert.True(FeignDeath.HasReraise(Live, e.Addr));
    }

    [Fact]
    public void SetInvisible_and_SetReraise_share_the_byte_without_clobbering_each_other()
    {
        using var e = PinnedBuf.Of(256);
        FeignDeath.SetInvisible(Live, e.Addr, on: true);
        FeignDeath.SetReraise(Live, e.Addr, on: true);
        Assert.True(FeignDeath.HasInvisible(Live, e.Addr));
        Assert.True(FeignDeath.HasReraise(Live, e.Addr));
        Assert.Equal(0x30, e.Bytes[Offsets.AInvisible]);   // 0x10 invisible | 0x20 reraise

        FeignDeath.SetInvisible(Live, e.Addr, on: false);  // drop Invisible (finishing blow)
        Assert.False(FeignDeath.HasInvisible(Live, e.Addr));
        Assert.True(FeignDeath.HasReraise(Live, e.Addr));  // Reraise survives
        Assert.Equal(0x20, e.Bytes[Offsets.AInvisible]);
    }

    [Fact]
    public void SetDead_sets_then_clears_the_dead_bit()
    {
        using var e = PinnedBuf.Of(256);
        FeignDeath.SetDead(Live, e.Addr, on: true);
        Assert.Equal(Offsets.ADeadBit, (byte)(e.Bytes[Offsets.ADeadStatus] & Offsets.ADeadBit));
        FeignDeath.SetDead(Live, e.Addr, on: false);
        Assert.Equal(0, e.Bytes[Offsets.ADeadStatus] & Offsets.ADeadBit);
    }

    [Fact]
    public void HoldAlive_lifts_zero_hp_to_one_and_leaves_positive_hp_alone()
    {
        using var e = PinnedBuf.Of(256);
        // HP == 0 -> lifted to 1.
        e.Bytes[Offsets.AHp] = 0; e.Bytes[Offsets.AHp + 1] = 0;
        FeignDeath.HoldAlive(Live, e.Addr);
        Assert.Equal(1, Live.U16(e.Addr + Offsets.AHp));

        // HP == 450 -> untouched (don't stomp a heal).
        e.Bytes[Offsets.AHp] = 0xC2; e.Bytes[Offsets.AHp + 1] = 0x01;   // 450
        FeignDeath.HoldAlive(Live, e.Addr);
        Assert.Equal(450, Live.U16(e.Addr + Offsets.AHp));
    }

    [Fact]
    public void ForceKill_drives_hp_to_zero()
    {
        using var e = PinnedBuf.Of(256);
        e.Bytes[Offsets.AHp] = 0xC2; e.Bytes[Offsets.AHp + 1] = 0x01;   // 450
        FeignDeath.ForceKill(Live, e.Addr);
        Assert.Equal(0, Live.U16(e.Addr + Offsets.AHp));
    }

    // ---- (5) main-hand-only activation contract ----

    [Fact]
    public void ActivatesOnMainHandOnly_is_documented_in_policy()
        => Assert.True(FeignDeath.ActivatesOnMainHandOnly);
}
