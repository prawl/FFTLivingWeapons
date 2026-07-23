using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-109/LW-110: HoldTimedStat's window-closed branch used to remove its tracking record
/// UNCONDITIONALLY, even when the revert write was skipped because the byte read neither the
/// boosted value nor the baked residue -- silently abandoning the hold for the rest of the
/// battle with no way back (asymmetric with the ordinary Hold path, which keeps its record and
/// retries). These pin the corrected three-way split (revert / already-natural / genuinely
/// unexpected) and, for LW-110, that the previously-silent capture/boost/revert writes now log.
/// Uses CavalierChargeTests' bare-engine construction (no ledger wiring needed: rosterNameId
/// defaults to 0, the NaturalLedger's fail-open bypass lane, so `baked` stays 0 throughout --
/// the LW-100 baked-residue interplay is GrowthEngineRestartTests' job, not this file's).
/// </summary>
public class TimedStatWindowCloseTests
{
    private static readonly WeaponSignature Sig = new()
    {
        AtTier = 3, StatBonus = 3, Stat = "Speed", ForTurns = 2
    };

    private static GrowthEngine MakeEngine(FakeSparseMemory mem)
        => new GrowthEngine(
               new Dictionary<int, WeaponMeta>(),
               new Dictionary<int, int>(),
               new TurnTracker(mem),
               mem);

    // ---- KEYSTONE (LW-109): an unexpected reading at window-close survives, then a later
    // boosted reading is the retry that finally reverts it ----

    [Fact]
    public void WindowClose_unexpected_reading_keeps_the_record_for_a_later_retry()
    {
        var mem = new FakeSparseMemory();
        long s = 0x1000_0000;
        mem.U8s[s + Offsets.CSpeed] = 8;
        mem.WritableAddrs.Add(s + Offsets.CSpeed);

        var engine = MakeEngine(mem);
        engine.HoldTimedStat(s, Sig, tier: 3, turns: 0);   // capture: 8 -> 11
        Assert.Equal((byte)11, mem.U8s[s + Offsets.CSpeed]);

        mem.U8s[s + Offsets.CSpeed] = 50;                   // something else wrote a value we don't expect
        engine.HoldTimedStat(s, Sig, tier: 3, turns: 5);   // window closed: neither boosted nor natural
        Assert.Equal((byte)50, mem.U8s[s + Offsets.CSpeed]);   // no revert attempted -- left alone

        mem.U8s[s + Offsets.CSpeed] = 11;                   // a later tick: back to the boosted reading
        engine.HoldTimedStat(s, Sig, tier: 3, turns: 6);   // the retry: record survived, so this reverts
        Assert.Equal((byte)8, mem.U8s[s + Offsets.CSpeed]);
    }

    // ---- the byte already reading natural at window-close is SUCCESS, not a retry candidate ----

    [Fact]
    public void WindowClose_reading_already_natural_removes_the_record()
    {
        var mem = new FakeSparseMemory();
        long s = 0x1000_0000;
        mem.U8s[s + Offsets.CSpeed] = 8;
        mem.WritableAddrs.Add(s + Offsets.CSpeed);

        var engine = MakeEngine(mem);
        engine.HoldTimedStat(s, Sig, tier: 3, turns: 0);   // capture: 8 -> 11

        mem.U8s[s + Offsets.CSpeed] = 8;                    // already natural by the time the window closes
        engine.HoldTimedStat(s, Sig, tier: 3, turns: 5);   // success: nothing to revert
        Assert.Equal((byte)8, mem.U8s[s + Offsets.CSpeed]);

        // Prove the record is really gone, not lingering as a retry candidate: a later
        // coincidentally-boosted-looking reading must NOT be reverted -- nothing owns this
        // address anymore, and re-reverting it here would be the double-revert the spec bans.
        mem.U8s[s + Offsets.CSpeed] = 11;
        engine.HoldTimedStat(s, Sig, tier: 3, turns: 6);
        Assert.Equal((byte)11, mem.U8s[s + Offsets.CSpeed]);
    }

    // ---- the ordinary revert path still removes the record (no regression) ----

    [Fact]
    public void WindowClose_ordinary_revert_still_removes_the_record()
    {
        var mem = new FakeSparseMemory();
        long s = 0x1000_0000;
        mem.U8s[s + Offsets.CSpeed] = 8;
        mem.WritableAddrs.Add(s + Offsets.CSpeed);

        var engine = MakeEngine(mem);
        engine.HoldTimedStat(s, Sig, tier: 3, turns: 0);   // capture: 8 -> 11

        engine.HoldTimedStat(s, Sig, tier: 3, turns: 5);   // window closed, cur == boosted: ordinary revert
        Assert.Equal((byte)8, mem.U8s[s + Offsets.CSpeed]);

        mem.U8s[s + Offsets.CSpeed] = 11;                   // a later coincidental boosted-looking reading
        engine.HoldTimedStat(s, Sig, tier: 3, turns: 6);   // must be left alone: no record, no double revert
        Assert.Equal((byte)11, mem.U8s[s + Offsets.CSpeed]);
    }

    // ---- LW-110: the previously-silent capture/boost/revert paths now log at Debug ----

    private static (List<string> console, List<string> file) InstallLogger()
    {
        var console = new List<string>();
        var file = new List<string>();
        ModLogger.Instance = new FileConsoleLogger(console.Add, file.Add) { LogLevel = LogLevel.Debug };
        return (console, file);
    }

    [Fact]
    public void Capture_and_boost_log_at_Debug()
    {
        var (_, file) = InstallLogger();
        try
        {
            var mem = new FakeSparseMemory();
            long s = 0x1000_0000;
            mem.U8s[s + Offsets.CSpeed] = 8;
            mem.WritableAddrs.Add(s + Offsets.CSpeed);

            var engine = MakeEngine(mem);
            engine.HoldTimedStat(s, Sig, tier: 3, turns: 0);   // capture + boost: 8 -> 11
        }
        finally { ModLogger.UseNullLogger(); }

        Assert.Contains(file, l => l.Contains("[growth]") && l.Contains("timed-stat: capture") && l.Contains("8"));
        Assert.Contains(file, l => l.Contains("[growth]") && l.Contains("timed-stat: boost") && l.Contains("11"));
    }

    [Fact]
    public void Revert_logs_at_Debug()
    {
        var (_, file) = InstallLogger();
        try
        {
            var mem = new FakeSparseMemory();
            long s = 0x1000_0000;
            mem.U8s[s + Offsets.CSpeed] = 8;
            mem.WritableAddrs.Add(s + Offsets.CSpeed);

            var engine = MakeEngine(mem);
            engine.HoldTimedStat(s, Sig, tier: 3, turns: 0);   // capture + boost
            engine.HoldTimedStat(s, Sig, tier: 3, turns: 5);   // window closed: ordinary revert
        }
        finally { ModLogger.UseNullLogger(); }

        Assert.Contains(file, l => l.Contains("[growth]") && l.Contains("timed-stat: revert") && l.Contains("8"));
    }
}
