using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-90: the NaturalLedger's pure semantics. The load-bearing shapes come straight from the
/// adversarial plan review: Engine.ResetBattleState fires on BOTH battle edges, so a clean
/// restart resets TWICE and the rolling two-slot history must survive any number of
/// empty-current resets; a record-nothing battle (double restart during formation) must
/// preserve the previous attempt's targets; and a correction must report the residue value so
/// the call site can keep it as an ownership token.
/// </summary>
public class NaturalLedgerTests
{
    private const int NameId = 298;
    private const int Lvl = 30;

    // ---- the canonical restart: record, double reset (exit + enter), corrected capture ----

    [Fact]
    public void Restart_with_double_reset_corrects_to_the_accepted_natural()
    {
        var ledger = new NaturalLedger();
        Assert.Equal(8, ledger.FilterCapture(NameId, StatLane.Speed, 8, Lvl, out _));   // battle 1: clean capture
        ledger.RecordWrite(NameId, StatLane.Speed, 11);                            // battle 1: hold writes 11

        ledger.OnBattleReset();   // exit edge
        ledger.OnBattleReset();   // enter edge (the REAL sequence; must not wipe the history)

        int natural = ledger.FilterCapture(NameId, StatLane.Speed, 11, Lvl, out int baked);
        Assert.Equal(8, natural);
        Assert.Equal(11, baked);
    }

    [Fact]
    public void Restart_with_single_reset_also_corrects()
    {
        // The enter-only restart (no exit edge, the Larceny precedent) resets exactly once.
        var ledger = new NaturalLedger();
        ledger.FilterCapture(NameId, StatLane.Speed, 8, Lvl, out _);
        ledger.RecordWrite(NameId, StatLane.Speed, 11);
        ledger.OnBattleReset();

        Assert.Equal(8, ledger.FilterCapture(NameId, StatLane.Speed, 11, Lvl, out int baked));
        Assert.Equal(11, baked);
    }

    [Fact]
    public void Zero_reset_recapture_matches_the_current_attempt()
    {
        // A mid-battle struct relocation forces a re-capture with NO reset in between: the
        // byte reads the target the mod itself wrote this battle. Current-set matching covers it.
        var ledger = new NaturalLedger();
        ledger.FilterCapture(NameId, StatLane.Speed, 8, Lvl, out _);
        ledger.RecordWrite(NameId, StatLane.Speed, 11);

        Assert.Equal(8, ledger.FilterCapture(NameId, StatLane.Speed, 11, Lvl, out int baked));
        Assert.Equal(11, baked);
    }

    [Fact]
    public void Chained_double_restart_preserves_prev_through_a_record_nothing_battle()
    {
        // Battle 1 records 11. Restart; battle 2's capture corrects but the player restarts
        // again during formation BEFORE any hold records. Battle 3 must still correct: the
        // empty-current resets between battles 2 and 3 must not destroy battle 1's targets.
        var ledger = new NaturalLedger();
        ledger.FilterCapture(NameId, StatLane.Speed, 8, Lvl, out _);
        ledger.RecordWrite(NameId, StatLane.Speed, 11);

        ledger.OnBattleReset(); ledger.OnBattleReset();                       // restart 1
        Assert.Equal(8, ledger.FilterCapture(NameId, StatLane.Speed, 11, Lvl, out _));   // battle 2 capture, records NOTHING

        ledger.OnBattleReset(); ledger.OnBattleReset();                       // restart 2 (current empty both times)
        Assert.Equal(8, ledger.FilterCapture(NameId, StatLane.Speed, 11, Lvl, out int baked));   // battle 3
        Assert.Equal(11, baked);
    }

    [Fact]
    public void Chained_restart_where_battle_2_re_records_keeps_correcting()
    {
        // Battle 2's corrected hold re-records its target (per evaluation); restart again;
        // battle 3 corrects against battle 2's records.
        var ledger = new NaturalLedger();
        ledger.FilterCapture(NameId, StatLane.Speed, 8, Lvl, out _);
        ledger.RecordWrite(NameId, StatLane.Speed, 11);
        ledger.OnBattleReset(); ledger.OnBattleReset();

        Assert.Equal(8, ledger.FilterCapture(NameId, StatLane.Speed, 11, Lvl, out _));
        ledger.RecordWrite(NameId, StatLane.Speed, 11);   // battle 2's re-hold records again

        ledger.OnBattleReset(); ledger.OnBattleReset();
        Assert.Equal(8, ledger.FilterCapture(NameId, StatLane.Speed, 11, Lvl, out _));   // battle 3
    }

    // ---- accept semantics ----

    [Fact]
    public void Unmatched_first_sight_is_accepted_and_refreshes_the_natural()
    {
        // A legit between-battles change (the Sprint Shoes class): 9 was never a written
        // target, so it is accepted, and future corrections correct to 9, not the stale 8.
        var ledger = new NaturalLedger();
        ledger.FilterCapture(NameId, StatLane.Speed, 8, Lvl, out _);
        ledger.RecordWrite(NameId, StatLane.Speed, 11);
        ledger.OnBattleReset(); ledger.OnBattleReset();

        Assert.Equal(9, ledger.FilterCapture(NameId, StatLane.Speed, 9, Lvl, out int baked));   // accepted
        Assert.Equal(0, baked);

        ledger.RecordWrite(NameId, StatLane.Speed, 12);
        ledger.OnBattleReset();
        Assert.Equal(9, ledger.FilterCapture(NameId, StatLane.Speed, 12, Lvl, out _));   // corrects to the REFRESHED natural
    }

    [Fact]
    public void First_sight_equal_to_the_accepted_natural_is_never_a_correction()
    {
        // The guard acceptedNatural != firstSight: a unit whose true natural happens to equal
        // a recorded target AND the accepted natural just passes through unchanged.
        var ledger = new NaturalLedger();
        ledger.FilterCapture(NameId, StatLane.Speed, 11, Lvl, out _);
        ledger.RecordWrite(NameId, StatLane.Speed, 11);   // target == natural (degenerate hold)
        ledger.OnBattleReset();

        Assert.Equal(11, ledger.FilterCapture(NameId, StatLane.Speed, 11, Lvl, out int baked));
        Assert.Equal(0, baked);
    }

    [Fact]
    public void Correction_does_not_refresh_the_accepted_natural()
    {
        // After a correction, the accepted natural stays the ORIGINAL value; a third capture
        // of the same residue still corrects to it.
        var ledger = new NaturalLedger();
        ledger.FilterCapture(NameId, StatLane.Speed, 8, Lvl, out _);
        ledger.RecordWrite(NameId, StatLane.Speed, 11);
        ledger.OnBattleReset();

        ledger.FilterCapture(NameId, StatLane.Speed, 11, Lvl, out _);
        Assert.Equal(8, ledger.FilterCapture(NameId, StatLane.Speed, 11, Lvl, out _));
    }

    // ---- bypass + bounds ----

    [Fact]
    public void NameId_zero_or_negative_bypasses_entirely()
    {
        var ledger = new NaturalLedger();
        ledger.FilterCapture(0, StatLane.Speed, 8, Lvl, out _);
        ledger.RecordWrite(0, StatLane.Speed, 11);
        ledger.OnBattleReset();
        Assert.Equal(11, ledger.FilterCapture(0, StatLane.Speed, 11, Lvl, out int baked));
        Assert.Equal(0, baked);

        ledger.RecordWrite(-1, StatLane.Speed, 11);
        ledger.OnBattleReset();
        Assert.Equal(11, ledger.FilterCapture(-1, StatLane.Speed, 11, Lvl, out _));
    }

    [Fact]
    public void Lanes_and_identities_are_independent()
    {
        var ledger = new NaturalLedger();
        ledger.FilterCapture(NameId, StatLane.Speed, 8, Lvl, out _);
        ledger.RecordWrite(NameId, StatLane.Speed, 11);
        ledger.OnBattleReset();

        // Same value on a DIFFERENT lane: no correction.
        Assert.Equal(11, ledger.FilterCapture(NameId, StatLane.Pa, 11, Lvl, out int bakedLane));
        Assert.Equal(0, bakedLane);
        // Same value on a DIFFERENT unit: no correction.
        Assert.Equal(11, ledger.FilterCapture(NameId + 1, StatLane.Speed, 11, Lvl, out int bakedUnit));
        Assert.Equal(0, bakedUnit);
    }

    [Fact]
    public void Out_of_byte_range_targets_are_not_recorded()
    {
        var ledger = new NaturalLedger();
        ledger.FilterCapture(NameId, StatLane.Speed, 8, Lvl, out _);
        ledger.RecordWrite(NameId, StatLane.Speed, 0);
        ledger.RecordWrite(NameId, StatLane.Speed, 256);
        ledger.OnBattleReset();
        // Neither value matches later (0 and 256 can't be a byte's first sight anyway; the
        // pin is that RecordWrite rejected them rather than storing garbage).
        Assert.Equal(56, ledger.FilterCapture(NameId, StatLane.Speed, 56, Lvl, out _));
    }

    [Fact]
    public void Ring_cap_drops_the_oldest_target()
    {
        var ledger = new NaturalLedger();
        ledger.FilterCapture(NameId, StatLane.Speed, 8, Lvl, out _);
        for (int t = 0; t <= NaturalLedger.TargetCap; t++)          // TargetCap + 1 distinct values
            ledger.RecordWrite(NameId, StatLane.Speed, 100 + t);    // 100 .. 100+cap
        ledger.OnBattleReset();

        // The newest survives (corrects); the oldest (100) was evicted (accepted as fresh).
        // Order matters: the newest is checked FIRST, because an accepted capture of the
        // evicted value legitimately refreshes the accepted natural (the designed semantics).
        Assert.Equal(8, ledger.FilterCapture(NameId, StatLane.Speed, 100 + NaturalLedger.TargetCap, Lvl, out int bakedNew));
        Assert.Equal(100 + NaturalLedger.TargetCap, bakedNew);
        Assert.Equal(100, ledger.FilterCapture(NameId, StatLane.Speed, 100, Lvl, out int bakedOld));
        Assert.Equal(0, bakedOld);
    }

    [Fact]
    public void Duplicate_targets_dedup_within_an_attempt()
    {
        var ledger = new NaturalLedger();
        ledger.FilterCapture(NameId, StatLane.Speed, 8, Lvl, out _);
        for (int i = 0; i < 100; i++) ledger.RecordWrite(NameId, StatLane.Speed, 11);   // per-tick spam
        ledger.RecordWrite(NameId, StatLane.Speed, 12);
        ledger.OnBattleReset();
        // Both values survived the spam (dedup, not eviction-by-repetition).
        Assert.Equal(8, ledger.FilterCapture(NameId, StatLane.Speed, 11, Lvl, out _));
        Assert.Equal(8, ledger.FilterCapture(NameId, StatLane.Speed, 12, Lvl, out _));
    }

    // ---- the level key (the implementation review's major: the +1 collision loop) ----

    [Fact]
    public void Level_up_to_a_recorded_target_is_accepted_not_corrected()
    {
        // A +1 boost target (10-15% factors on single-digit stats) collides with the unit's
        // ordinary +1 level-up gain: natural 10 records target 11, the unit levels and its
        // TRUE natural becomes 11. Without the level key this corrected 11 -> 10 every battle
        // forever (the corrected hold re-records 11 each time), eating the earned point. A
        // restart never commits a level, so level inequality means REAL change: accept.
        var ledger = new NaturalLedger();
        ledger.FilterCapture(NameId, StatLane.Speed, 10, Lvl, out _);
        ledger.RecordWrite(NameId, StatLane.Speed, 11);
        ledger.OnBattleReset(); ledger.OnBattleReset();

        int natural = ledger.FilterCapture(NameId, StatLane.Speed, 11, Lvl + 1, out int baked);
        Assert.Equal(11, natural);
        Assert.Equal(0, baked);

        // And the refreshed entry corrects future restarts at the NEW level.
        ledger.RecordWrite(NameId, StatLane.Speed, 12);
        ledger.OnBattleReset();
        Assert.Equal(11, ledger.FilterCapture(NameId, StatLane.Speed, 12, Lvl + 1, out _));
    }

    [Fact]
    public void Restart_at_the_same_level_still_corrects()
    {
        // The inverse pin: the level key must not weaken the restart correction (a restart
        // never moves the roster level; the LIVE_LEDGER mid-battle level-drift row).
        var ledger = new NaturalLedger();
        ledger.FilterCapture(NameId, StatLane.Speed, 10, Lvl, out _);
        ledger.RecordWrite(NameId, StatLane.Speed, 11);
        ledger.OnBattleReset(); ledger.OnBattleReset();

        Assert.Equal(10, ledger.FilterCapture(NameId, StatLane.Speed, 11, Lvl, out int baked));
        Assert.Equal(11, baked);
    }

    // ---- clear ----

    [Fact]
    public void Clear_forgets_everything()
    {
        var ledger = new NaturalLedger();
        ledger.FilterCapture(NameId, StatLane.Speed, 8, Lvl, out _);
        ledger.RecordWrite(NameId, StatLane.Speed, 11);
        ledger.OnBattleReset();
        ledger.Clear();

        Assert.Equal(11, ledger.FilterCapture(NameId, StatLane.Speed, 11, Lvl, out int baked));
        Assert.Equal(0, baked);
    }
}
